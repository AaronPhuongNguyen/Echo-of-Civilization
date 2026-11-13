using System;
using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
//  DATA
// ─────────────────────────────────────────────────────────────────────────────

[Serializable]
public class CombatSettings
{
    [Header("Timing (game-seconds, follows game speed / pause)")]
    [Tooltip("How often meetings between enemy armies are detected.")]
    [Min(0.05f)] public float detectInterval = 0.2f;
    [Tooltip("One combat tick = one round of damage.")]
    [Min(0.05f)] public float tickInterval = 1f;

    [Header("Damage  (ATK / DEF / HP per man are GameValues on each Military)")]
    [Tooltip("Both sides take this extra damage every tick when Attacker ATK < Defender DEF.")]
    public GameValue stalemateDamage = new GameValue(1f);
    [Tooltip("0.5 = +50% damage (attacker when ATK >= DEF of defender, defender when its ATK > attacker DEF).")]
    public GameValue bonusDamage = new GameValue(0.5f);
    [Tooltip("Every this many HP lost = 1 manpower lost.")]
    [Min(1f)] public float hpLostPerManpower = 5f;

    [Header("Reinforcement")]
    public bool reinforcements = true;
    [Tooltip("Game-seconds between two reinforcement checks of a battle.")]
    [Min(0.1f)] public float reinforceInterval = 3f;
    [Tooltip("A regiment asks for help while it holds less than this percent of its allocation.")]
    [Range(1f, 100f)] public float reinforceBelowPercent = 99f;
    [Tooltip("Size of one column = this percent of the regiment's allocation.")]
    [Range(1f, 100f)] public float columnPercent = 15f;
    [Tooltip("A column is only sent when it would have at least this many men (and the nation has them).")]
    [Min(1f)] public float minColumnManpower = 50f;
    [Min(1)] public int maxColumnsPerRegiment = 3;
    [Tooltip("When the main regiment retreats, this part of a column stays to hold the enemy, the rest follows the retreating regiment.")]
    [Range(0f, 1f)] public float holdSplit = 0.25f;
    [Tooltip("A column merges when it is this close (tiles) to its regiment (or in the same province).")]
    [Min(0f)] public float mergeRadiusTiles = 3f;
}

[Serializable]
public class BattleInfo
{
    public int id;
    public string province;
    public string sideA;
    public string sideB;
    public double manpowerA, manpowerB;
    public double hpA, hpB;
    public int ticks;
}

/// <summary>One battle in one province. Side A = the one that attacked (arrived), side B = the defender.</summary>
public sealed class Battle
{
    public readonly int Id;
    public readonly Province Province;
    public readonly Vector3 WorldPos;
    public readonly Nation LeadA, LeadB;
    public readonly Color ColorA, ColorB;
    public readonly List<Regiment> SideA = new List<Regiment>(4);
    public readonly List<Regiment> SideB = new List<Regiment>(4);
    public int Ticks { get; internal set; }
    public bool Ended { get; internal set; }
    internal float ReinforceTimer;
    internal BattleView View;

    internal Battle(int id, Province p, Vector3 pos, Nation a, Nation b)
    {
        Id = id; Province = p; WorldPos = pos; LeadA = a; LeadB = b;
        ColorA = a.Color; ColorB = b.Color;
    }

    public double ManpowerA => Sum(SideA, false);
    public double ManpowerB => Sum(SideB, false);
    public double HpA => Sum(SideA, true);
    public double HpB => Sum(SideB, true);

    private static double Sum(List<Regiment> l, bool hp)
    {
        double s = 0.0;
        for (int i = 0; i < l.Count; i++) s += hp ? l[i].TotalHp : l[i].Manpower;
        return s;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  COMBAT MANAGER  (global access: CombatManager.Instance)
//
//  Meeting  : enemy (at war) regiments standing in the same province -> Battle (+ BattleView card).
//  Damage   : per tick, from the totals of each side (see RunBattle).
//  Occupy   : a regiment occupies the province it stands in once no enemy fighter is left there (WarManager.Occupy).
//  Trail    : while a regiment walks inside provinces it occupies, an overlay (nation color, alpha 0.6) is painted
//             on the tiles it walked through.
//  Reinforce: columns spawn at a safe province, walk to the front and merge into their regiment.
// ─────────────────────────────────────────────────────────────────────────────
[DefaultExecutionOrder(-650)]
public class CombatManager : TickBehaviour
{
    // ── singleton ──
    private static CombatManager _instance;
    private static bool _quitting;
    public static bool HasInstance => _instance != null;

    public static CombatManager Instance
    {
        get
        {
            if (_instance == null && !_quitting)
            {
#if UNITY_2022_2_OR_NEWER
                _instance = FindFirstObjectByType<CombatManager>();
#else
                _instance = FindObjectOfType<CombatManager>();
#endif
                if (_instance == null)
                    _instance = new GameObject("CombatManager").AddComponent<CombatManager>();
            }
            return _instance;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() { _instance = null; _quitting = false; }

    // ── settings ──
    public CombatSettings settings = new CombatSettings();
    [SerializeField] private MapManager map;

    [Header("Battle card (screen space, uses the ArmyManager canvas)")]
    [Tooltip("Logo of side A (the attacker). Tinted with the side's nation color.")]
    [SerializeField] private Sprite attackLogo;
    [Tooltip("Logo of side B (the defender). Tinted with the side's nation color.")]
    [SerializeField] private Sprite defendLogo;
    [Tooltip("Optional background sprite of the battle card.")]
    [SerializeField] private Sprite backgroundSprite;
    [Range(0.5f, 3f)] public float battleCardScale = 1.1f;

    [Header("Virtual border (occupation trail)")]
    [Tooltip("Radius in tiles of the painted trail around a walking army.")]
    [Min(1)] public int trailRadiusTiles = 3;
    [Range(0f, 1f)] public float trailAlpha = 0.6f;
    [Tooltip("Overlay texture max size (a 2048 map uses 2 tiles per pixel).")]
    [Min(128)] public int overlayMaxSize = 1024;
    public int overlaySortingOrder = 3;

    [Header("Context menu helpers (right-click the component title)")]
    [SerializeField] private int debugNationA = -1;
    [SerializeField] private int debugNationB = -1;
    [Tooltip("Regiment index of nation A. -1 = first free regiment.")]
    [SerializeField] private int debugRegiment = -1;
    [Tooltip("Target province id. -1 = capital of nation B.")]
    [SerializeField] private int debugProvince = -1;

    [Header("Inspector view (read-only mirror, Editor only)")]
    [SerializeField] private bool showBattlesInInspector = true;
    [SerializeField] private List<BattleInfo> battlesView = new List<BattleInfo>();
    private float _nextInspectorRefresh;

    // ── data ──
    private readonly List<Battle> _battles = new List<Battle>();
    private readonly Dictionary<int, Battle> _battleAt = new Dictionary<int, Battle>();
    private readonly Dictionary<int, List<Regiment>> _index = new Dictionary<int, List<Regiment>>();
    private readonly Stack<List<Regiment>> _listPool = new Stack<List<Regiment>>();
    private readonly Dictionary<int, int> _occupier = new Dictionary<int, int>();     // province id -> occupier nation id
    private readonly List<Battle> _tmpBattles = new List<Battle>();
    private readonly List<Regiment> _tmpUnits = new List<Regiment>();
    private readonly List<Regiment> _tmpCols = new List<Regiment>();
    private readonly List<War> _tmpWars = new List<War>();
    private BattleView _template;
    private int _nextId = 1;
    private float _detectAcc, _tickAcc;

    // overlay
    private Texture2D _tex;
    private Sprite _sprite;
    private GameObject _ovGo;
    private Color32[] _px;
    private int _ovW, _ovH, _ovScale;
    private bool _dirty;

    public IReadOnlyList<Battle> Battles => _battles;
    public Sprite AttackLogo => attackLogo;
    public Sprite DefendLogo => defendLogo;
    public Sprite BackgroundSprite => backgroundSprite;
    public string LastError { get; private set; }

    public event Action<Battle> OnBattleStarted;
    /// <summary>battle, winner (Attacker = side A, Defender = side B, None = draw / peace)</summary>
    public event Action<Battle, WarSide> OnBattleEnded;

    protected override int TickPriority => -650;

    // ─────────────────────────────────────────────────────────────────────
    //  LIFECYCLE
    // ─────────────────────────────────────────────────────────────────────
    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Debug.LogWarning("Duplicate CombatManager removed.");
            Destroy(this);
            return;
        }
        _instance = this;
    }

    private void OnApplicationQuit() => _quitting = true;

    protected override void OnEnable()
    {
        base.OnEnable();
        if (_instance != this) return;

        if (map == null)
        {
#if UNITY_2022_2_OR_NEWER
            map = FindFirstObjectByType<MapManager>();
#else
            map = FindObjectOfType<MapManager>();
#endif
        }

        ArmyManager army = ArmyManager.Instance;
        army.OnRegimentRetreat += HandleRetreat;
        army.OnRegimentArrived += HandleArrived;
        WarManager.Instance.OnProvinceOccupationChanged += HandleOccupation;
        WorldManager.Instance.OnNationsCleared += ClearAll;

        if (map != null)
        {
            map.OnMapGenerated += BuildOverlay;
            if (map.IsReady) BuildOverlay();
        }
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        if (ArmyManager.HasInstance)
        {
            ArmyManager.Instance.OnRegimentRetreat -= HandleRetreat;
            ArmyManager.Instance.OnRegimentArrived -= HandleArrived;
        }
        if (WarManager.HasInstance) WarManager.Instance.OnProvinceOccupationChanged -= HandleOccupation;
        if (WorldManager.HasInstance) WorldManager.Instance.OnNationsCleared -= ClearAll;
        if (map != null) map.OnMapGenerated -= BuildOverlay;
        if (_instance == this && _quitting) _instance = null;
    }

    private void OnDestroy()
    {
        DestroyOverlay();
        if (_template != null) Destroy(_template.gameObject);
        if (_instance == this) _instance = null;
    }

    /// <summary>Ends every battle without winner and forgets every occupation trail (map regenerated).</summary>
    public void ClearAll()
    {
        _tmpBattles.Clear();
        _tmpBattles.AddRange(_battles);
        for (int i = 0; i < _tmpBattles.Count; i++) EndBattle(_tmpBattles[i], WarSide.None);
        _battles.Clear();
        _battleAt.Clear();
        _index.Clear();
        _occupier.Clear();
        DestroyOverlay();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  TICK
    // ─────────────────────────────────────────────────────────────────────
    public override void Tick(float deltaTime)
    {
        if (!ArmyManager.HasInstance || !WarManager.HasInstance) return;

        _detectAcc += deltaTime;
        if (_detectAcc >= Mathf.Max(0.05f, settings.detectInterval))
        {
            _detectAcc = 0f;
            Detect();
        }

        if (_battles.Count > 0)
        {
            _tickAcc += deltaTime;
            if (_tickAcc >= Mathf.Max(0.05f, settings.tickInterval))
            {
                _tickAcc = 0f;
                RunBattles();
            }
        }

        PaintTrails();

#if UNITY_EDITOR
        if (showBattlesInInspector && Time.unscaledTime >= _nextInspectorRefresh)
        {
            _nextInspectorRefresh = Time.unscaledTime + 1f;
            RefreshView();
        }
#endif
    }

    // ─────────────────────────────────────────────────────────────────────
    //  DETECTION: who stands where, who meets whom
    // ─────────────────────────────────────────────────────────────────────
    private static bool IsFighter(Regiment r) =>
        r.Military != null && r.State != RegimentState.Retreating && r.Manpower > 0.5 &&
        (!r.IsColumn || (r.Role == ColumnRole.Hold && r.State != RegimentState.Moving));

    private void RebuildIndex()
    {
        foreach (var kv in _index) { kv.Value.Clear(); _listPool.Push(kv.Value); }
        _index.Clear();

        IReadOnlyList<Military> mils = ArmyManager.Instance.Militaries;
        for (int i = 0; i < mils.Count; i++)
        {
            Military m = mils[i];
            for (int pass = 0; pass < 2; pass++)
            {
                List<Regiment> regs = pass == 0 ? m.Regiments : m.Columns;
                for (int j = 0; j < regs.Count; j++)
                {
                    Regiment r = regs[j];
                    if (!IsFighter(r)) continue;
                    Province p = r.CurrentProvince;
                    if (p == null) continue;
                    if (!_index.TryGetValue(p.Id, out List<Regiment> l))
                    {
                        l = _listPool.Count > 0 ? _listPool.Pop() : new List<Regiment>(4);
                        _index.Add(p.Id, l);
                    }
                    l.Add(r);
                }
            }
        }
    }

    private bool Hostile(Nation a, Nation b) =>
        a != null && b != null && a != b && WarManager.Instance.IsAtWar(a, b);

    private void Detect()
    {
        RebuildIndex();

        // 1) start battles / let newcomers join
        foreach (var kv in _index)
        {
            List<Regiment> L = kv.Value;
            _battleAt.TryGetValue(kv.Key, out Battle b);
            if (L.Count < 2 && b == null) continue;

            if (b == null)
            {
                Regiment atk = null, def = null;
                for (int i = 0; i < L.Count && atk == null; i++)
                    for (int j = i + 1; j < L.Count; j++)
                    {
                        if (!Hostile(L[i].Military.Nation, L[j].Military.Nation)) continue;
                        if (L[j].State == RegimentState.Moving && L[i].State != RegimentState.Moving) { atk = L[j]; def = L[i]; }
                        else { atk = L[i]; def = L[j]; }
                        break;
                    }
                if (atk != null) b = StartBattle(atk.CurrentProvince, atk, def);
            }

            if (b == null) continue;
            for (int i = 0; i < L.Count; i++)
                if (L[i].Battle == null && L[i].State != RegimentState.Fighting) TryJoin(b, L[i]);
        }

        // 2) occupation when a regiment entered a new province
        foreach (var kv in _index)
        {
            List<Regiment> L = kv.Value;
            for (int i = 0; i < L.Count; i++)
            {
                Regiment r = L[i];
                if (r.IsColumn || r.Military == null || r.State == RegimentState.Fighting) continue;
                if (r.CheckedProvince == r.CurrentProvince) continue;
                r.CheckedProvince = r.CurrentProvince;
                TryOccupy(r);
            }
        }

        // 3) rear-guard columns that found no battle go home (their men return to the reserve)
        IReadOnlyList<Military> mils = ArmyManager.Instance.Militaries;
        for (int i = 0; i < mils.Count; i++)
        {
            List<Regiment> cols = mils[i].Columns;
            for (int j = cols.Count - 1; j >= 0; j--)
            {
                Regiment c = cols[j];
                if (c.Role == ColumnRole.Hold && c.State == RegimentState.Idle && c.Battle == null)
                    mils[i].RemoveColumn(c);
            }
        }
    }

    private Battle StartBattle(Province p, Regiment atk, Regiment def)
    {
        var b = new Battle(_nextId++, p, ArmyManager.Instance.ProvinceWorldPos(p),
            atk.Military.Nation, def.Military.Nation);
        b.SideA.Add(atk); b.SideB.Add(def);
        atk.EnterFight(b); def.EnterFight(b);
        _battles.Add(b);
        _battleAt[p.Id] = b;
        OnBattleStarted?.Invoke(b);
        return b;
    }

    private void TryJoin(Battle b, Regiment u)
    {
        Nation n = u.Military.Nation;
        bool a = n == b.LeadA || (Hostile(n, b.LeadB) && !Hostile(n, b.LeadA));
        bool d = n == b.LeadB || (Hostile(n, b.LeadA) && !Hostile(n, b.LeadB));
        if (a == d) return;                               // neutral (or confusing): stays out
        (a ? b.SideA : b.SideB).Add(u);
        u.EnterFight(b);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  BATTLE ROUND
    //   Totals: ATK = sum(men * ATK/man), DEF = sum(men * DEF/man), HP = sum(men * HP/man)
    //   dmg to B = max(0, ATK_A - DEF_B)   dmg to A = max(0, ATK_B - DEF_A)
    //   ATK_A <  DEF_B : both sides take +stalemateDamage
    //   ATK_A >= DEF_B : dmg to B * (1 + bonus)
    //   ATK_B >  DEF_A : dmg to A * (1 + bonus)
    //   Every hpLostPerManpower HP lost = 1 manpower lost.
    // ─────────────────────────────────────────────────────────────────────
    private void RunBattles()
    {
        _tmpBattles.Clear();
        _tmpBattles.AddRange(_battles);
        for (int i = 0; i < _tmpBattles.Count; i++)
            if (!_tmpBattles[i].Ended) RunBattle(_tmpBattles[i]);
    }

    private void RunBattle(Battle b)
    {
        Prune(b.SideA); Prune(b.SideB);
        if (b.SideA.Count == 0 || b.SideB.Count == 0 || !Hostile(b.LeadA, b.LeadB))
        {
            EndBattle(b, Winner(b));
            return;
        }

        b.Ticks++;
        Totals(b.SideA, out double atkA, out double defA, out double mulA);
        Totals(b.SideB, out double atkB, out double defB, out double mulB);

        double stale = settings.stalemateDamage.Value;
        double bonus = 1.0 + settings.bonusDamage.Value;

        double toB = Math.Max(0.0, atkA - defB);
        double toA = Math.Max(0.0, atkB - defA);
        if (atkA < defB) { toB += stale; toA += stale; }
        else toB *= bonus;
        if (atkB > defA) toA *= bonus;

        toB *= mulA;
        toA *= mulB;

        ApplyDamage(b.SideB, toB);
        ApplyDamage(b.SideA, toA);

        Prune(b.SideA); Prune(b.SideB);
        if (b.SideA.Count == 0 || b.SideB.Count == 0)
        {
            EndBattle(b, Winner(b));
            return;
        }

        if (settings.reinforcements)
        {
            b.ReinforceTimer += settings.tickInterval;
            if (b.ReinforceTimer >= settings.reinforceInterval)
            {
                b.ReinforceTimer = 0f;
                ReinforceSide(b, b.SideA);
                ReinforceSide(b, b.SideB);
            }
        }

        if (b.View != null) b.View.SetNumbers(b.ManpowerA, b.ManpowerB);
    }

    private static WarSide Winner(Battle b)
    {
        if (b.SideA.Count > 0 && b.SideB.Count == 0) return WarSide.Attacker;
        if (b.SideB.Count > 0 && b.SideA.Count == 0) return WarSide.Defender;
        return WarSide.None;
    }

    private static void Prune(List<Regiment> side)
    {
        for (int i = side.Count - 1; i >= 0; i--)
        {
            Regiment u = side[i];
            if (u.Military == null || u.State != RegimentState.Fighting || u.Manpower <= 0.5) side.RemoveAt(i);
        }
    }

    private static void Totals(List<Regiment> side, out double atk, out double def, out double mul)
    {
        atk = def = 0.0;
        double men = 0.0, m = 0.0;
        for (int i = 0; i < side.Count; i++)
        {
            Regiment u = side[i];
            Military mil = u.Military;
            double man = u.Manpower;
            atk += man * mil.AttackPerMan.Value;
            def += man * mil.DefendPerMan.Value;
            m += man * mil.DamageMultiplier.Value;
            men += man;
        }
        mul = men > 0.0 ? m / men : 1.0;
    }

    private void ApplyDamage(List<Regiment> side, double hp)
    {
        if (hp <= 0.0 || side.Count == 0) return;
        _tmpUnits.Clear();
        _tmpUnits.AddRange(side);

        double total = 0.0;
        for (int i = 0; i < _tmpUnits.Count; i++) total += _tmpUnits[i].Manpower;
        if (total <= 0.0) return;

        double per = Math.Max(1.0, settings.hpLostPerManpower);
        for (int i = 0; i < _tmpUnits.Count; i++)
        {
            Regiment u = _tmpUnits[i];
            if (u.Military == null) continue;
            u.LostHp += hp * (u.Manpower / total);
            if (u.LostHp < per) continue;
            double men = Math.Floor(u.LostHp / per);
            u.LostHp -= men * per;
            u.TakeCasualties(men);              // may retreat / destroy it
        }
    }

    private void EndBattle(Battle b, WarSide winner)
    {
        if (b == null || b.Ended) return;
        b.Ended = true;
        _battles.Remove(b);
        _battleAt.Remove(b.Province.Id);
        ReleaseView(b);

        ArmyManager army = ArmyManager.Instance;
        for (int pass = 0; pass < 2; pass++)
        {
            List<Regiment> side = pass == 0 ? b.SideA : b.SideB;
            for (int i = 0; i < side.Count; i++)
            {
                Regiment u = side[i];
                if (u.Military == null || u.State != RegimentState.Fighting) { u.Battle = null; continue; }
                u.LeaveFight();
                if (u.State == RegimentState.Moving) army.TrackMoving(u);   // continue its march
            }
        }

        if (winner != WarSide.None)
        {
            List<Regiment> win = winner == WarSide.Attacker ? b.SideA : b.SideB;
            for (int i = 0; i < win.Count; i++) TryOccupy(win[i]);
        }

        OnBattleEnded?.Invoke(b, winner);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  REINFORCEMENT
    // ─────────────────────────────────────────────────────────────────────
    private void ReinforceSide(Battle b, List<Regiment> side)
    {
        ArmyManager army = ArmyManager.Instance;
        _tmpUnits.Clear();
        _tmpUnits.AddRange(side);

        for (int i = 0; i < _tmpUnits.Count; i++)
        {
            Regiment u = _tmpUnits[i];
            if (u.IsColumn || u.Military == null || u.State != RegimentState.Fighting) continue;

            Military m = u.Military;
            double alloc = u.Allocation;
            if (alloc <= 0.0 || u.Manpower >= alloc * settings.reinforceBelowPercent / 100.0) continue;

            double incoming = 0.0;
            int cols = 0;
            for (int c = 0; c < m.Columns.Count; c++)
            {
                Regiment col = m.Columns[c];
                if (col.Role == ColumnRole.Merge && col.MergeTarget == u) { incoming += col.Manpower; cols++; }
            }
            if (cols >= settings.maxColumnsPerRegiment) continue;

            double need = alloc - u.Manpower - incoming;
            double free = m.CurrentManpower - m.TotalRegimentManpower();
            double men = Math.Min(Math.Min(need, free), alloc * settings.columnPercent / 100.0);
            if (men < settings.minColumnManpower) continue;              // not enough manpower: no reinforcement

            Province from = FindSafeProvince(m.Nation, b.WorldPos, b.Province);
            if (from == null) continue;

            Regiment column = m.AddColumn(from, men);
            if (column == null) continue;
            column.Role = ColumnRole.Merge;
            column.MergeTarget = u;
            if (!army.MoveRegiment(column, b.Province)) m.RemoveColumn(column);
        }
    }

    /// <summary>A main regiment starts retreating: its columns split (holdSplit hold the enemy, the rest follow it).</summary>
    private void HandleRetreat(Regiment r, Province target)
    {
        Military m = r.Military;
        if (m == null || r.IsColumn) return;

        ArmyManager army = ArmyManager.Instance;
        Province site = r.Province;                                   // still the battle province
        _tmpCols.Clear();
        for (int i = 0; i < m.Columns.Count; i++)
            if (m.Columns[i].Role == ColumnRole.Merge && m.Columns[i].MergeTarget == r) _tmpCols.Add(m.Columns[i]);

        for (int i = 0; i < _tmpCols.Count; i++)
        {
            Regiment c = _tmpCols[i];
            double hold = c.Manpower * settings.holdSplit;
            if (hold >= 1.0 && c.Manpower - hold >= 1.0 && site != null)
            {
                Regiment h = m.AddColumn(c.CurrentProvince ?? c.Province, hold);
                if (h != null)
                {
                    h.Teleport(c.WorldPosition);
                    h.CurrentProvince = c.CurrentProvince;
                    h.Role = ColumnRole.Hold;
                    if (army.MoveRegiment(h, site)) c.Manpower -= hold;
                    else m.RemoveColumn(h);
                }
            }
            if (target == null || !army.MoveRegiment(c, target)) m.RemoveColumn(c);   // the rest chases the retreating regiment
        }
    }

    private void HandleArrived(Regiment r)
    {
        if (r.Military == null || !r.IsColumn) return;
        if (r.Role == ColumnRole.Hold) return;                        // waits (Idle): joins the battle on the next detect

        Military m = r.Military;
        Regiment t = r.MergeTarget;
        if (t == null || t.Military != m) { m.RemoveColumn(r); return; }

        ArmyManager army = ArmyManager.Instance;
        float radius = settings.mergeRadiusTiles * army.TileSize;
        bool near = (t.CurrentProvince != null && t.CurrentProvince == r.CurrentProvince) ||
                    (t.WorldPosition - r.WorldPosition).sqrMagnitude <= radius * radius;

        if (near)
        {
            double room = Math.Max(0.0, t.Allocation - t.Manpower);
            t.Manpower += Math.Min(r.Manpower, room);                 // the rest stays in the reserve
            t.RefreshText();
            m.RemoveColumn(r);
        }
        else if (!army.MoveRegiment(r, t.LocationProvince)) m.RemoveColumn(r);   // chase it
    }

    // ─────────────────────────────────────────────────────────────────────
    //  SAFE PROVINCES / OCCUPATION RULES
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Nearest province of 'n' without a battle, enemy fighters or an enemy occupation.</summary>
    public Province FindSafeProvince(Nation n, Vector3 from, Province exclude = null)
    {
        if (n == null || !ArmyManager.HasInstance) return null;
        ArmyManager army = ArmyManager.Instance;
        Province best = null;
        float bestD = float.MaxValue;
        for (int i = 0; i < n.Provinces.Count; i++)
        {
            Province p = n.Provinces[i];
            if (p == exclude || !IsSafe(p, n)) continue;
            float d = (army.ProvinceWorldPos(p) - from).sqrMagnitude;
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }

    private bool IsSafe(Province p, Nation n)
    {
        if (_battleAt.ContainsKey(p.Id)) return false;
        if (_occupier.TryGetValue(p.Id, out int occ) && occ != n.Id) return false;
        if (_index.TryGetValue(p.Id, out List<Regiment> L))
            for (int i = 0; i < L.Count; i++)
            {
                Regiment u = L[i];
                if (u.Military != null && u.State != RegimentState.Retreating && Hostile(u.Military.Nation, n)) return false;
            }
        return true;
    }

    /// <summary>False while a battle runs in the province or an enemy fighter (of that war) still stands there.</summary>
    public bool CanTakeProvince(War w, Nation me, Province p)
    {
        if (w == null || me == null || p == null) return false;
        if (_battleAt.ContainsKey(p.Id)) return false;
        WarSide mine = w.SideOf(me.Id);
        if (_index.TryGetValue(p.Id, out List<Regiment> L))
            for (int i = 0; i < L.Count; i++)
            {
                Regiment u = L[i];
                if (u.Military == null || u.State == RegimentState.Retreating || u.Manpower <= 0.5) continue;
                if (u.CurrentProvince != p) continue;
                WarSide s = w.SideOf(u.Military.Nation.Id);
                if (s != WarSide.None && s != mine) return false;
            }
        return true;
    }

    /// <summary>The regiment occupies the province it stands in (enemy land) or liberates it (own side's land held by the enemy).</summary>
    internal bool TryOccupy(Regiment r)
    {
        if (r == null || r.Military == null || r.IsColumn) return false;
        if (r.State == RegimentState.Retreating || r.State == RegimentState.Fighting) return false;
        Province p = r.CurrentProvince ?? r.Province;
        if (p == null || p.Owner == null) return false;

        WarManager wm = WarManager.Instance;
        Nation me = r.Military.Nation;
        _tmpWars.Clear();
        IReadOnlyList<War> wars = wm.GetWars(me);
        for (int i = 0; i < wars.Count; i++) _tmpWars.Add(wars[i]);

        for (int i = 0; i < _tmpWars.Count; i++)
        {
            War w = _tmpWars[i];
            if (w.Ended) continue;
            WarSide mine = w.SideOf(me.Id), ownerSide = w.SideOf(p.Owner.Id);
            if (mine == WarSide.None || ownerSide == WarSide.None) continue;
            if (!CanTakeProvince(w, me, p)) continue;

            if (ownerSide != mine)
            {
                if (w.Occupied.TryGetValue(p.Id, out int cur) && w.SideOf(cur) == mine) continue;
                if (wm.Occupy(w, me, p)) { PaintDisc(r.WorldPosition, me); return true; }
            }
            else if (w.Occupied.TryGetValue(p.Id, out int occ) && w.SideOf(occ) != mine)
            {
                if (wm.Liberate(w, p)) return true;
            }
        }
        return false;
    }

    private void HandleOccupation(War w, Province p, Nation occupier)
    {
        if (p == null) return;
        if (_occupier.TryGetValue(p.Id, out int old) && (occupier == null || old != occupier.Id)) ClearProvincePixels(p);
        if (occupier != null) _occupier[p.Id] = occupier.Id;
        else _occupier.Remove(p.Id);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  VIRTUAL BORDER (overlay painted along the walked path)
    // ─────────────────────────────────────────────────────────────────────
    private void BuildOverlay()
    {
        DestroyOverlay();
        if (map == null || !map.IsReady) return;

        int w = map.Width, h = map.Height;
        float ts = map.settings.tileSize;
        int max = Mathf.Max(128, overlayMaxSize);
        _ovScale = Mathf.Max(1, (Mathf.Max(w, h) + max - 1) / max);
        _ovW = (w + _ovScale - 1) / _ovScale;
        _ovH = (h + _ovScale - 1) / _ovScale;
        _px = new Color32[_ovW * _ovH];

        _tex = new Texture2D(_ovW, _ovH, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        _tex.SetPixelData(_px, 0);
        _tex.Apply(false);
        _sprite = Sprite.Create(_tex, new Rect(0, 0, _ovW, _ovH), Vector2.zero, 1f / (_ovScale * ts), 0, SpriteMeshType.FullRect);

        _ovGo = new GameObject("Occupation Overlay");
        _ovGo.transform.SetParent(map.transform, false);
        _ovGo.transform.localPosition = map.MapOrigin;
        var sr = _ovGo.AddComponent<SpriteRenderer>();
        sr.sprite = _sprite;
        sr.sortingOrder = overlaySortingOrder;
    }

    private void DestroyOverlay()
    {
        if (_sprite) Destroy(_sprite);
        if (_tex) Destroy(_tex);
        if (_ovGo) Destroy(_ovGo);
        _sprite = null; _tex = null; _ovGo = null; _px = null; _dirty = false;
    }

    private void PaintTrails()
    {
        if (_px == null || map == null) return;
        IReadOnlyList<Regiment> moving = ArmyManager.Instance.MovingRegiments;
        for (int i = 0; i < moving.Count; i++)
        {
            Regiment r = moving[i];
            if (r.State != RegimentState.Moving || r.IsColumn || r.Military == null) continue;
            if (!map.TryWorldToTile(r.WorldPosition, out int x, out int y)) continue;

            var cur = new Vector2Int(x, y);
            Nation n = r.Military.Nation;
            if (r.TrailTile.x < 0) { PaintDisc(cur, n); r.TrailTile = cur; continue; }
            if (r.TrailTile == cur) continue;

            Vector2Int prev = r.TrailTile;
            int steps = Mathf.Clamp(Mathf.Max(Mathf.Abs(cur.x - prev.x), Mathf.Abs(cur.y - prev.y)), 1, 64);
            for (int s = 1; s <= steps; s++)
            {
                Vector2 q = Vector2.Lerp(prev, cur, s / (float)steps);
                PaintDisc(new Vector2Int(Mathf.RoundToInt(q.x), Mathf.RoundToInt(q.y)), n);
            }
            r.TrailTile = cur;
        }
    }

    private void PaintDisc(Vector3 world, Nation n)
    {
        if (_px == null || map == null || !map.TryWorldToTile(world, out int x, out int y)) return;
        PaintDisc(new Vector2Int(x, y), n);
    }

    /// <summary>Paints only tiles of provinces that 'n' currently occupies.</summary>
    private void PaintDisc(Vector2Int c, Nation n)
    {
        if (_px == null || n == null) return;
        Color32 col = n.Color;
        col.a = (byte)Mathf.RoundToInt(Mathf.Clamp01(trailAlpha) * 255f);
        int R = Mathf.Max(1, trailRadiusTiles), R2 = R * R;

        for (int dy = -R; dy <= R; dy++)
            for (int dx = -R; dx <= R; dx++)
            {
                if (dx * dx + dy * dy > R2) continue;
                int x = c.x + dx, y = c.y + dy;
                if (!map.InBounds(x, y)) continue;
                int pid = map.GetProvinceId(x, y);
                if (pid < 0 || !_occupier.TryGetValue(pid, out int occ) || occ != n.Id) continue;
                _px[(y / _ovScale) * _ovW + (x / _ovScale)] = col;
                _dirty = true;
            }
    }

    private void ClearProvincePixels(Province p)
    {
        if (_px == null || map == null || p.Tiles == null) return;
        int w = map.Width;
        for (int i = 0; i < p.Tiles.Length; i++)
        {
            int t = p.Tiles[i];
            _px[((t / w) / _ovScale) * _ovW + ((t % w) / _ovScale)] = default;
        }
        _dirty = true;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  BATTLE CARDS: world position -> screen position (LateUpdate)
    // ─────────────────────────────────────────────────────────────────────
    private void LateUpdate()
    {
        if (_dirty && _tex != null)
        {
            _dirty = false;
            _tex.SetPixelData(_px, 0);
            _tex.Apply(false);
        }

        if (_battles.Count == 0 || !ArmyManager.HasInstance) return;
        ArmyManager army = ArmyManager.Instance;
        bool zoomOk = army.CardsVisibleAtZoom();

        for (int i = 0; i < _battles.Count; i++)
        {
            Battle b = _battles[i];
            Vector2 local = default;
            if (!zoomOk || !army.WorldToUi(b.WorldPos, out local)) { ReleaseView(b); continue; }
            if (b.View == null && !AcquireView(b, army)) continue;
            b.View.SetLocalPosition(local);
        }
    }

    private bool AcquireView(Battle b, ArmyManager army)
    {
        RectTransform root = army.GetRoot();
        if (root == null) return false;
        BattleView v = PoolManager.Instance.Spawn(GetTemplate(), Vector3.zero, Quaternion.identity, root);
        if (v == null) return false;
        v.transform.localRotation = Quaternion.identity;
        v.transform.localScale = Vector3.one;
        b.View = v;
        v.Bind(b, this, army);
        return true;
    }

    private void ReleaseView(Battle b)
    {
        BattleView v = b.View;
        if (v == null) return;
        b.View = null;
        if (PoolManager.HasInstance) PoolManager.Instance.Despawn(v.gameObject);
        else Destroy(v.gameObject);
    }

    private BattleView GetTemplate()
    {
        if (_template != null) return _template;
        var go = new GameObject("BattleView (template)", typeof(RectTransform));
        go.SetActive(false);
        go.transform.SetParent(transform, false);
        _template = go.AddComponent<BattleView>();
        return _template;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  QUERIES / FORCE
    // ─────────────────────────────────────────────────────────────────────
    public Battle GetBattle(Province p) => p != null && _battleAt.TryGetValue(p.Id, out Battle b) ? b : null;
    public bool IsInBattle(Regiment r) => r != null && r.Battle != null && !r.Battle.Ended;

    /// <summary>Forces the end of a battle (no winner: everybody stays where they are).</summary>
    public void EndAllBattles()
    {
        _tmpBattles.Clear();
        _tmpBattles.AddRange(_battles);
        for (int i = 0; i < _tmpBattles.Count; i++) EndBattle(_tmpBattles[i], WarSide.None);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  CONTEXT MENU  (declare the war first: War: FORCE Declare War)
    // ─────────────────────────────────────────────────────────────────────
    [ContextMenu("Combat: Send Regiment (A) to Province (debugProvince / capital of B)")]
    private void MenuSend() => RunSend(false);

    [ContextMenu("Combat: Send ALL regiments of A to Province (debugProvince / capital of B)")]
    private void MenuSendAll() => RunSend(true);

    [ContextMenu("Combat: End All Battles (no winner)")]
    private void MenuEndAll() { EndAllBattles(); RefreshView(); Debug.Log("[Combat] All battles ended."); }

    [ContextMenu("Combat: Log Battles")]
    private void MenuLog()
    {
        if (_battles.Count == 0) Debug.Log("[Combat] No battles.");
        for (int i = 0; i < _battles.Count; i++)
        {
            Battle b = _battles[i];
            Debug.Log($"[Combat] #{b.Id} {b.Province.Name}: {b.LeadA.Name} {b.ManpowerA:N0} vs {b.LeadB.Name} {b.ManpowerB:N0} (tick {b.Ticks})");
        }
    }

    [ContextMenu("Combat: Clear Occupation Overlay")]
    private void MenuClearOverlay()
    {
        _occupier.Clear();
        if (_px != null) { Array.Clear(_px, 0, _px.Length); _dirty = true; }
    }

    private void RunSend(bool all)
    {
        WorldManager w = WorldManager.Instance;
        Nation a = debugNationA >= 0 ? w.GetNation(debugNationA) : w.GetRandomNation();
        Nation b = debugNationB >= 0 ? w.GetNation(debugNationB) : null;
        if (b == null) for (int t = 0; t < 32 && (b == null || b == a); t++) b = w.GetRandomNation();
        if (a == null || b == null || a == b) { Debug.LogWarning("[Combat] Need two different nations."); return; }

        Military m = a.GetMilitary();
        if (m == null || m.Regiments.Count == 0) { Debug.LogWarning("[Combat] Nation A has no regiments."); return; }

        Province dest = debugProvince >= 0 && map != null ? map.GetProvince(debugProvince) : b.Capital;
        if (dest == null) { Debug.LogWarning("[Combat] Target province not found."); return; }

        ArmyManager army = ArmyManager.Instance;
        int sent = 0;
        for (int i = 0; i < m.Regiments.Count; i++)
        {
            if (!all && debugRegiment >= 0 && i != debugRegiment) continue;
            Regiment r = m.Regiments[i];
            if (r.State == RegimentState.Retreating || r.State == RegimentState.Fighting) continue;
            if (army.MoveRegiment(r, dest)) sent++;
            if (!all) break;
        }
        Debug.Log($"[Combat] {a.Name} -> {dest.Name} ({b.Name}): sent {sent} regiment(s). At war: {WarManager.Instance.IsAtWar(a, b)}");
    }

    private void RefreshView()
    {
#if UNITY_EDITOR
        battlesView.Clear();
        if (!showBattlesInInspector) return;
        for (int i = 0; i < _battles.Count; i++)
        {
            Battle b = _battles[i];
            battlesView.Add(new BattleInfo
            {
                id = b.Id,
                province = b.Province.Name,
                sideA = b.LeadA.Name,
                sideB = b.LeadB.Name,
                manpowerA = b.ManpowerA,
                manpowerB = b.ManpowerB,
                hpA = b.HpA,
                hpB = b.HpB,
                ticks = b.Ticks
            });
        }
#endif
    }
}