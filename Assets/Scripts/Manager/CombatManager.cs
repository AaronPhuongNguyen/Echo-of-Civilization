using System;
using System.Collections.Generic;
using UnityEngine;

#region Settings & diagnostics data

[Serializable]
public class CombatSettings
{
    [Header("Combat step (game-seconds, follows game speed / pause)")]
    [Tooltip("How often the Burst combat job is scheduled. Damage is scaled by the elapsed time.")]
    [Min(0.02f)] public float combatInterval = 0.1f;

    [Header("Damage")]
    [Tooltip("hp = ATK^2 / (ATK + DEF) * damageScale * damageMultiplier * seconds. Raise = faster battles.")]
    [Min(0.00001f)] public float damageScale = 0.005f;

    [Header("Dice (only buff in the game)")]
    public bool useDice = true;
    [Tooltip("The side that wins the roll gets ATK and DEF * (1 + this).")]
    public GameValue diceBonus = new GameValue(0.5f);
    [Min(1)] public int diceRollDays = 7;
    [Min(0)] public int diceBuffDays = 6;

    [Header("Push (stronger side pushes the weaker one back)")]
    [Tooltip("Strength share (0.5..1) where pushing starts.")]
    [Range(0.5f, 1f)] public float pushStartShare = 0.5f;
    [Tooltip("Strength share where the push reaches full march speed.")]
    [Range(0.5f, 1f)] public float pushFullShare = 0.9f;
    [Tooltip("The pusher is the hostile regiment (not itself pushed) next to a pushed one. It keeps marching and shoves the loser ahead of it.")]
    public bool pusherAdvances = true;

    [Header("Occupation (must fight / siege first)")]
    [Tooltip("Progress needed (days) with no enemy fighter left in the province.")]
    [Min(0.1f)] public float occupyDays = 3f;
    [Tooltip("Progress per day = 1 / (1 + Defend / this). Higher = defence matters less.")]
    [Min(0.01f)] public float defendDivisor = 1f;

    [Header("Occupy on the march")]
    [Tooltip("A marching regiment that ENTERS an enemy province with no enemy fighter inside occupies it at once (and liberates its own land that was occupied).")]
    public bool occupyOnPath = true;
    [Tooltip("Provinces whose Defend is above this are NOT taken in passing: they need a real siege.")]
    [Min(0f)] public float pathOccupyDefendLimit = 2f;

    [Header("Reinforcement")]
    public bool reinforcements = true;
    [Min(0.1f)] public float reinforceInterval = 3f;
    [Range(1f, 100f)] public float reinforceBelowPercent = 99f;
    [Range(1f, 100f)] public float columnPercent = 15f;
    [Min(1f)] public float minColumnManpower = 50f;
    [Min(1)] public int maxColumnsPerRegiment = 3;
    [Min(0f)] public float mergeRadiusTiles = 3f;
    [Min(1)] public int maxNewColumnsPerScan = 12;

    [Header("Occupation columns")]
    public bool occupationColumns = true;
    [Min(1)] public int occupierIntervalDays = 7;
    [Range(0.1f, 50f)] public float occupierPercent = 5f;
    [Min(1f)] public float occupierMinManpower = 100f;
    [Min(1)] public int maxOccupiersPerNation = 3;
    [Min(1)] public int occupierMaxHops = 4;

    [Header("After the war")]
    public bool returnToBorderAfterWar = true;
}

[Serializable]
public class CombatDiagnostics
{
    public int units, inRange, inContact, sieges;
}

[Serializable]
public class SiegeInfo
{
    public string province, occupier;
    public float progressDays;
}

#endregion

// ─────────────────────────────────────────────────────────────────────────────
//  COMBAT MANAGER  (CombatManager.Instance)
//  Fight   : hostile regiments inside each other's OUTER radius trade damage (Burst job);
//            inside the INNER radius they collide: marching stops, the stronger side pushes the weaker back.
//  Push    : the stronger regiment (the PUSHER) keeps advancing slowly along its path and shoves the weaker one.
//  Dice    : stateless, in the job (7 day roll, 6 day buff).
//  Occupy  : idle regiment, no enemy in range -> daily siege progress divided by the province Defend.
//            A marching regiment ALSO occupies every enemy province it walks into (no enemy fighter inside).
//  Reinforce: columns spawn in a safe province, walk to the front and merge.
// ─────────────────────────────────────────────────────────────────────────────
[DefaultExecutionOrder(-650)]
public class CombatManager : TickBehaviour
{
    #region Singleton

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

    #endregion

    #region Inspector & data

    public CombatSettings settings = new CombatSettings();
    [SerializeField] private MapManager map;

    [Header("Occupation overlay")]
    [Range(0f, 1f)] public float trailAlpha = 0.6f;
    [Min(128)] public int overlayMaxSize = 1024;
    public int overlaySortingOrder = 3;

    [Header("Context menu helpers")]
    [SerializeField] private int debugNationA = -1;
    [SerializeField] private int debugNationB = -1;
    [SerializeField] private int debugRegiment = -1;
    [SerializeField] private int debugProvince = -1;

    [Header("Diagnostics (Editor only, throttled)")]
    [SerializeField] private bool showDiagnostics = true;
    [Min(0.25f)] public float diagnosticsInterval = 1f;
    [SerializeField] private CombatDiagnostics diagnostics = new CombatDiagnostics();
    [SerializeField] private List<SiegeInfo> siegesView = new List<SiegeInfo>();
    private float _nextDiagnostics;

    private sealed class Siege
    {
        public int Nation;
        public War War;
        public bool Liberate;
        public float Progress;
    }

    private readonly CombatBatch _batch = new CombatBatch();
    private readonly List<Regiment> _units = new List<Regiment>(256);       // batch order
    private readonly Dictionary<int, List<Regiment>> _index = new Dictionary<int, List<Regiment>>();
    private readonly Stack<List<Regiment>> _listPool = new Stack<List<Regiment>>();
    private readonly Dictionary<int, int> _occupier = new Dictionary<int, int>();
    private readonly Dictionary<int, Siege> _sieges = new Dictionary<int, Siege>();
    private readonly HashSet<int> _siegeSeen = new HashSet<int>();
    private readonly List<int> _intBuf = new List<int>();
    private readonly Dictionary<int, long> _nextOccupier = new Dictionary<int, long>();
    private readonly Dictionary<Regiment, int> _hops = new Dictionary<Regiment, int>();
    private readonly List<Regiment> _tmpCols = new List<Regiment>();
    private readonly List<War> _tmpWars = new List<War>();
    private readonly List<War> _enterWars = new List<War>();
    private readonly List<War> _dayWars = new List<War>();
    private readonly List<Regiment> _staleBuf = new List<Regiment>();
    private readonly List<int> _pushedBuf = new List<int>(32);
    private readonly HashSet<int> _seenTargets = new HashSet<int>();

    private float _acc, _pendingDt, _reinforceAcc;
    private int _reinforceCursor;

    protected override int TickPriority => -650;

    public string LastError { get; private set; }

    #endregion

    #region Lifecycle

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

        ArmyManager.Instance.OnRegimentRetreat += HandleRetreat;
        ArmyManager.Instance.OnRegimentArrived += HandleArrived;
        ArmyManager.Instance.OnRegimentEnteredProvince += HandleEntered;
        WarManager wm = WarManager.Instance;
        wm.OnProvinceOccupationChanged += HandleOccupation;
        wm.OnProvinceOccupationSpread += HandleSpread;
        wm.OnWarEnded += HandleWarEnded;
        WorldManager.Instance.OnNationsCleared += ClearAll;
        DateManager.Instance.OnDayPassed += HandleDay;

        if (map != null)
        {
            map.OnMapGenerated += BuildOverlay;
            if (map.IsReady) BuildOverlay();
        }
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        _batch.Complete();
        if (ArmyManager.HasInstance)
        {
            ArmyManager.Instance.OnRegimentRetreat -= HandleRetreat;
            ArmyManager.Instance.OnRegimentArrived -= HandleArrived;
            ArmyManager.Instance.OnRegimentEnteredProvince -= HandleEntered;
        }
        if (WarManager.HasInstance)
        {
            WarManager.Instance.OnProvinceOccupationChanged -= HandleOccupation;
            WarManager.Instance.OnProvinceOccupationSpread -= HandleSpread;
            WarManager.Instance.OnWarEnded -= HandleWarEnded;
        }
        if (WorldManager.HasInstance) WorldManager.Instance.OnNationsCleared -= ClearAll;
        if (DateManager.HasInstance) DateManager.Instance.OnDayPassed -= HandleDay;
        if (map != null) map.OnMapGenerated -= BuildOverlay;
        if (_instance == this && _quitting) _instance = null;
    }

    private void OnDestroy()
    {
        _batch.Dispose();
        DestroyOverlay();
        if (_instance == this) _instance = null;
    }

    /// <summary>Forgets everything (map regenerated / nations cleared).</summary>
    public void ClearAll()
    {
        _batch.Complete();
        _units.Clear();
        _index.Clear();
        _occupier.Clear();
        _sieges.Clear();
        _nextOccupier.Clear();
        _hops.Clear();
        _pushedBuf.Clear();
        DestroyOverlay();
    }

    #endregion

    #region Tick (schedule) / LateUpdate (collect)

    public override void Tick(float deltaTime)
    {
        if (!ArmyManager.HasInstance || !WarManager.HasInstance) return;

        _acc += deltaTime;
        if (!_batch.Pending && _acc >= Mathf.Max(0.02f, settings.combatInterval)) ScheduleCombat();

        if (settings.reinforcements)
        {
            _reinforceAcc += deltaTime;
            if (_reinforceAcc >= settings.reinforceInterval) { _reinforceAcc = 0f; ReinforceAll(); }
        }

#if UNITY_EDITOR
        if (showDiagnostics && Time.unscaledTime >= _nextDiagnostics)
        {
            _nextDiagnostics = Time.unscaledTime + Mathf.Max(0.25f, diagnosticsInterval);
            RefreshDiagnostics();
        }
#endif
    }

    private void LateUpdate()
    {
        if (_dirty && _tex != null)
        {
            _dirty = false;
            _tex.SetPixelData(_px, 0);
            _tex.Apply(false);
        }
        if (_batch.Pending) ApplyCombat();
    }

    private void ScheduleCombat()
    {
        ArmyManager army = ArmyManager.Instance;
        WarManager wm = WarManager.Instance;
        IReadOnlyList<Military> mils = army.Militaries;

        float dt = Mathf.Min(_acc, 1f);
        _acc = 0f;

        // upper bound + nation capacity (ids are stable but not contiguous after removals)
        int upper = 0;
        for (int i = 0; i < mils.Count; i++) upper += mils[i].Regiments.Count + mils[i].Columns.Count;
        int nationCap = 1;
        IReadOnlyList<Nation> nations = WorldManager.Instance.Nations;
        for (int i = 0; i < nations.Count; i++) if (nations[i].Id + 1 > nationCap) nationCap = nations[i].Id + 1;

        _batch.Reserve(upper, nationCap);

        // hostility matrix (wars are few)
        _batch.ClearHostility();
        IReadOnlyList<War> wars = wm.Wars;
        for (int w = 0; w < wars.Count; w++)
        {
            War war = wars[w];
            if (war.Ended) continue;
            for (int a = 0; a < war.Attackers.Count; a++)
                for (int d = 0; d < war.Defenders.Count; d++)
                    _batch.SetHostile(war.Attackers[a], war.Defenders[d]);
        }

        // units + province index
        foreach (var kv in _index) { kv.Value.Clear(); _listPool.Push(kv.Value); }
        _index.Clear();
        _units.Clear();

        float tile = army.TileSize;
        for (int i = 0; i < mils.Count; i++)
        {
            Military m = mils[i];
            int nid = m.Nation.Id;
            float atk = m.AttackPerMan.Value, def = m.DefendPerMan.Value, dmg = m.DamageMultiplier.Value;
            float outer = m.OuterRadius.Value * tile;
            float inner = Mathf.Min(m.InnerRadius.Value * tile, outer);

            for (int pass = 0; pass < 2; pass++)
            {
                List<Regiment> regs = pass == 0 ? m.Regiments : m.Columns;
                for (int j = 0; j < regs.Count; j++)
                {
                    Regiment r = regs[j];
                    if (r.Military == null || r.State == RegimentState.Retreating || r.Manpower <= 0.5) continue;

                    _batch.Set(_units.Count, r.WorldPosition, (float)r.Manpower, atk, def, dmg, outer, inner, nid, r.Uid);
                    _units.Add(r);

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

        if (_units.Count == 0) return;
        _pendingDt = dt;
        _batch.Schedule(_units.Count, (int)DateManager.Instance.TotalDays, dt, settings.damageScale,
            settings.useDice, settings.diceBonus.Value, settings.diceRollDays, settings.diceBuffDays,
            army.moveSpeedTiles * tile, settings.pushStartShare, Mathf.Max(settings.pushStartShare + 0.01f, settings.pushFullShare));
    }

    private void ApplyCombat()
    {
        _batch.Complete();
        ArmyManager army = ArmyManager.Instance;
        float dt = _pendingDt;
        _pushedBuf.Clear();

        // pass 1: flags + visible push (a regiment that is pushed is the loser of its collision)
        for (int i = 0; i < _units.Count; i++)
        {
            Regiment r = _units[i];
            if (r.Military == null) continue;

            byte f = _batch.Flags(i);
            r.InRange = (f & CombatBatch.FlagInRange) != 0;
            r.InContact = (f & CombatBatch.FlagInContact) != 0;

            Vector3 push = _batch.Push(i);
            if (push.sqrMagnitude > 1e-8f)
            {
                army.Nudge(r, push * dt);
                if (r.InContact && _pushedBuf.Count < 64) _pushedBuf.Add(i);
            }
        }

        if (settings.pusherAdvances) MarkPushers();

        // pass 2: damage (may retreat / destroy a regiment, so it runs after every unit got its flags)
        for (int i = 0; i < _units.Count; i++)
        {
            Regiment r = _units[i];
            if (r.Military == null) continue;

            float hp = _batch.Hp(i);
            if (hp <= 0f) continue;
            r.LostHp += hp;
            double per = Math.Max(1.0, r.Military.HpPerMan.Value);
            if (r.LostHp < per) continue;
            double men = Math.Floor(r.LostHp / per);
            r.LostHp -= men * per;
            r.TakeCasualties(men);                                          // may retreat / destroy it
        }
    }

    /// <summary>
    /// For every pushed regiment the nearest hostile regiment that is NOT pushed itself is its pusher: it may keep
    /// marching (ArmyManager.MoveRegiments) and so follows the shoved enemy instead of standing still.
    /// </summary>
    private void MarkPushers()
    {
        if (_pushedBuf.Count == 0) return;
        float tile = ArmyManager.Instance.TileSize;
        double until = TickManager.GameTime + settings.combatInterval * 2.5 + 0.05;

        for (int k = 0; k < _pushedBuf.Count; k++)
        {
            Regiment loser = _units[_pushedBuf[k]];
            if (loser.Military == null) continue;
            Nation ln = loser.Military.Nation;

            Regiment best = null;
            float bestD = float.MaxValue;
            for (int j = 0; j < _units.Count; j++)
            {
                Regiment u = _units[j];
                if (u == loser || u.Military == null || u.State == RegimentState.Retreating) continue;

                float reach = Mathf.Max(loser.Military.InnerRadius.Value, u.Military.InnerRadius.Value) * tile * 1.25f;
                float d = (u.WorldPosition - loser.WorldPosition).sqrMagnitude;
                if (d > reach * reach || d >= bestD) continue;
                if (_batch.Push(j).sqrMagnitude > 1e-8f) continue;          // also pushed = not the winner
                if (!Hostile(u.Military.Nation, ln)) continue;

                bestD = d;
                best = u;
            }
            if (best != null) best.PushingUntil = until;
        }
    }

    #endregion

    #region Queries

    private bool Hostile(Nation a, Nation b) =>
        a != null && b != null && a != b && WarManager.Instance.IsAtWar(a, b);

    /// <summary>True while a regiment of a war enemy is in range of another one inside this province.</summary>
    public bool IsContested(Province p)
    {
        if (p == null || !_index.TryGetValue(p.Id, out List<Regiment> L)) return false;
        for (int i = 0; i < L.Count; i++) if (L[i].InRange) return true;
        return false;
    }

    public bool IsInCombat(Regiment r) => r != null && (r.InRange || r.InContact);

    /// <summary>Occupation progress of the province in days (0 = no siege).</summary>
    public float GetSiegeProgress(Province p) =>
        p != null && _sieges.TryGetValue(p.Id, out Siege s) ? s.Progress : 0f;

    /// <summary>Nearest province of 'n' without enemy fighters or an enemy occupation.</summary>
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
        if (_occupier.TryGetValue(p.Id, out int occ) && occ != n.Id) return false;
        if (_index.TryGetValue(p.Id, out List<Regiment> L))
            for (int i = 0; i < L.Count; i++)
            {
                Regiment u = L[i];
                if (u.Military != null && Hostile(u.Military.Nation, n)) return false;
            }
        return true;
    }

    /// <summary>False while an enemy fighter (of that war) still stands in the province.</summary>
    public bool CanTakeProvince(War w, Nation me, Province p)
    {
        if (w == null || me == null || p == null) return false;
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

    #endregion

    #region Occupation on the march

    /// <summary>
    /// A marching regiment walked into a new province. Enemy province without enemy fighters -> occupied at once
    /// (this also spreads to free neighbours, see WarManager.Occupy). Own land that the enemy occupies -> liberated.
    /// </summary>
    private void HandleEntered(Regiment r, Province p)
    {
        if (!settings.occupyOnPath || r == null || p == null || p.Owner == null || p.IsMerged) return;
        Military mil = r.Military;
        if (mil == null || r.State == RegimentState.Retreating || r.InContact) return;
        if (r.Manpower <= 0.5) return;
        if (r.IsColumn && r.Role != ColumnRole.Occupy) return;
        if (!WarManager.HasInstance) return;

        WarManager wm = WarManager.Instance;
        Nation me = mil.Nation;
        IReadOnlyList<War> wars = wm.GetWars(me);
        if (wars.Count == 0) return;

        _enterWars.Clear();
        for (int i = 0; i < wars.Count; i++) _enterWars.Add(wars[i]);

        for (int i = 0; i < _enterWars.Count; i++)
        {
            War war = _enterWars[i];
            if (war.Ended) continue;
            WarSide mine = war.SideOf(me.Id), os = war.SideOf(p.Owner.Id);
            if (mine == WarSide.None || os == WarSide.None) continue;

            if (os != mine)
            {
                // enemy land: take it in passing unless it is a fortress
                if (war.Occupied.TryGetValue(p.Id, out int cur) && war.SideOf(cur) == mine) continue;
                if (p.Defend.Value > settings.pathOccupyDefendLimit) continue;
                if (!CanTakeProvince(war, me, p)) continue;

                _sieges.Remove(p.Id);
                wm.Occupy(war, me, p);
                return;
            }

            // friendly land held by the enemy: free it
            if (war.Occupied.TryGetValue(p.Id, out int occ) && war.SideOf(occ) != mine)
            {
                if (!CanTakeProvince(war, me, p)) continue;
                _sieges.Remove(p.Id);
                wm.Liberate(war, p);
                return;
            }
        }
    }

    #endregion

    #region Reinforcement

    private void ReinforceAll()
    {
        IReadOnlyList<Military> mils = ArmyManager.Instance.Militaries;
        int count = mils.Count;
        if (count == 0) return;
        int budget = settings.maxNewColumnsPerScan;
        int start = _reinforceCursor % count;
        for (int k = 0; k < count && budget > 0; k++)
        {
            int idx = (start + k) % count;
            _reinforceCursor = idx + 1;
            Military m = mils[idx];
            for (int i = 0; i < m.Regiments.Count && budget > 0; i++)
            {
                if (AIManager.HasInstance && !AIManager.Instance.AllowReinforce(m.Regiments[i])) continue;
                if (TryReinforce(m.Regiments[i], settings.maxColumnsPerRegiment)) budget--;
            }
        }
    }

    private bool TryReinforce(Regiment u, int maxCols)
    {
        if (u.IsColumn || u.Military == null) return false;
        if (u.State == RegimentState.Retreating || u.State == RegimentState.Moving) return false;

        Military m = u.Military;
        double alloc = u.Allocation;
        if (alloc <= 0.0 || u.Manpower >= alloc * settings.reinforceBelowPercent / 100.0) return false;

        double incoming = 0.0;
        int cols = 0;
        for (int c = 0; c < m.Columns.Count; c++)
        {
            Regiment col = m.Columns[c];
            if (col.Role == ColumnRole.Merge && col.MergeTarget == u) { incoming += col.Manpower; cols++; }
        }
        if (cols >= maxCols) return false;

        ArmyManager army = ArmyManager.Instance;
        double need = alloc - u.Manpower - incoming;
        double free = m.CurrentManpower - m.TotalRegimentManpower();
        double men = Math.Min(Math.Min(need, free), Math.Min(alloc * settings.columnPercent / 100.0, army.ColumnCapMen));
        bool lastTopUp = men >= need - 0.5;
        if (men < 1.0 || (men < settings.minColumnManpower && !lastTopUp)) return false;

        Province exclude = u.InRange ? u.Province : null;
        Province from = FindSafeProvince(m.Nation, u.WorldPosition, exclude);
        if (from == null) return false;

        Regiment column = m.AddColumn(from, men);
        if (column == null) return false;
        column.Role = ColumnRole.Merge;
        column.MergeTarget = u;
        Province dest = u.LocationProvince ?? u.Province;
        if (!army.MoveRegiment(column, dest)) { m.RemoveColumn(column); return false; }
        return true;
    }
    /// <summary>Used by the AI (Reserve Surge): bypasses the 'below percent' check, allows more columns.</summary>
    public bool ForceReinforce(Regiment u, int maxCols) => TryReinforce(u, maxCols);

    /// <summary>A main regiment retreats: its columns follow it.</summary>
    private void HandleRetreat(Regiment r, Province target)
    {
        Military m = r.Military;
        if (m == null || r.IsColumn) return;

        ArmyManager army = ArmyManager.Instance;
        _tmpCols.Clear();
        for (int i = 0; i < m.Columns.Count; i++)
            if (m.Columns[i].Role == ColumnRole.Merge && m.Columns[i].MergeTarget == r) _tmpCols.Add(m.Columns[i]);

        for (int i = 0; i < _tmpCols.Count; i++)
            if (target == null || !army.MoveRegiment(_tmpCols[i], target)) m.RemoveColumn(_tmpCols[i]);
    }

    private void HandleArrived(Regiment r)
    {
        if (r.Military == null || !r.IsColumn) return;
        if (r.Role == ColumnRole.Hold || r.Role == ColumnRole.Occupy) return;

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
            double add = Math.Min(r.Manpower, room);
            t.Manpower += add;
            r.Manpower -= add;                     // the rest is refunded by RemoveColumn
            t.RefreshText();
            m.RemoveColumn(r);
        }
        else if (!army.MoveRegiment(r, t.LocationProvince)) m.RemoveColumn(r);
    }

    #endregion

    #region Occupation (siege) + occupation columns

    private void HandleDay()
    {
        if (_hops.Count > 0)
        {
            _staleBuf.Clear();
            foreach (var kv in _hops) if (kv.Key.Military == null) _staleBuf.Add(kv.Key);
            for (int i = 0; i < _staleBuf.Count; i++) _hops.Remove(_staleBuf[i]);
        }
        if (!ArmyManager.HasInstance || !WarManager.HasInstance) return;

        SiegeDay();
        if (!settings.occupationColumns) return;
        SpawnOccupiersDay();
        AdvanceOccupiersDay();
    }

    /// <summary>Daily: idle regiments with no enemy in range push the occupation progress of the province they stand in.</summary>
    private void SiegeDay()
    {
        WarManager wm = WarManager.Instance;
        IReadOnlyList<Military> mils = ArmyManager.Instance.Militaries;
        _siegeSeen.Clear();

        for (int i = 0; i < mils.Count; i++)
        {
            Military m = mils[i];
            Nation me = m.Nation;
            IReadOnlyList<War> wars = wm.GetWars(me);
            if (wars.Count == 0) continue;

            for (int pass = 0; pass < 2; pass++)
            {
                List<Regiment> regs = pass == 0 ? m.Regiments : m.Columns;
                for (int j = 0; j < regs.Count; j++)
                {
                    Regiment r = regs[j];
                    if (r.Military == null || r.Manpower <= 0.5) continue;
                    if (r.IsColumn && r.Role != ColumnRole.Occupy) continue;
                    if (r.State == RegimentState.Moving || r.State == RegimentState.Retreating) continue;
                    if (r.InRange || r.InContact) continue;                     // fight first

                    Province p = r.CurrentProvince ?? r.Province;
                    if (p == null || p.Owner == null || _siegeSeen.Contains(p.Id)) continue;

                    _tmpWars.Clear();
                    for (int w = 0; w < wars.Count; w++) _tmpWars.Add(wars[w]);
                    for (int w = 0; w < _tmpWars.Count; w++)
                    {
                        War war = _tmpWars[w];
                        if (war.Ended) continue;
                        WarSide mine = war.SideOf(me.Id), os = war.SideOf(p.Owner.Id);
                        if (mine == WarSide.None || os == WarSide.None) continue;

                        bool liberate;
                        if (os != mine) { if (war.Occupied.TryGetValue(p.Id, out int cur) && war.SideOf(cur) == mine) continue; liberate = false; }
                        else if (war.Occupied.TryGetValue(p.Id, out int occ) && war.SideOf(occ) != mine) liberate = true;
                        else continue;

                        if (!CanTakeProvince(war, me, p)) continue;

                        _siegeSeen.Add(p.Id);
                        if (!_sieges.TryGetValue(p.Id, out Siege s) || s.Nation != me.Id || s.War != war || s.Liberate != liberate)
                        {
                            s = new Siege { Nation = me.Id, War = war, Liberate = liberate };
                            _sieges[p.Id] = s;
                        }

                        // progress per day = 1 / (1 + Defend / divisor): strong defence = slower siege
                        s.Progress += 1f / (1f + Mathf.Max(0f, p.Defend.Value) / Mathf.Max(0.01f, settings.defendDivisor));
                        wm.MarkActivity(war);
                        if (s.Progress >= settings.occupyDays)
                        {
                            _sieges.Remove(p.Id);
                            if (liberate) wm.Liberate(war, p); else wm.Occupy(war, me, p);
                        }
                        break;
                    }
                }
            }
        }

        // sieges nobody pushed today are lost
        _intBuf.Clear();
        foreach (var kv in _sieges) if (!_siegeSeen.Contains(kv.Key)) _intBuf.Add(kv.Key);
        for (int i = 0; i < _intBuf.Count; i++) _sieges.Remove(_intBuf[i]);
    }

    private void SpawnOccupiersDay()
    {
        WarManager wm = WarManager.Instance;
        if (wm.Wars.Count == 0) return;

        long today = DateManager.Instance.TotalDays;
        _dayWars.Clear();
        _dayWars.AddRange(wm.Wars);
        for (int i = 0; i < _dayWars.Count; i++)
        {
            War w = _dayWars[i];
            if (w.Ended) continue;
            for (int side = 0; side < 2; side++)
            {
                List<int> members = side == 0 ? w.Attackers : w.Defenders;
                for (int k = 0; k < members.Count; k++)
                {
                    Nation n = WorldManager.Instance.GetNation(members[k]);
                    if (n == null || n.IsEmpty) continue;
                    if (_nextOccupier.TryGetValue(n.Id, out long due) && today < due) continue;
                    _nextOccupier[n.Id] = today + (TrySpawnOccupier(w, n) ? settings.occupierIntervalDays : 1);
                }
            }
        }
    }

    private bool TrySpawnOccupier(War w, Nation n)
    {
        ArmyManager army = ArmyManager.Instance;
        Military m = army.GetMilitary(n);
        if (m == null) return false;

        int active = 0;
        for (int i = 0; i < m.Columns.Count; i++) if (m.Columns[i].Role == ColumnRole.Occupy) active++;
        if (active >= settings.maxOccupiersPerNation) return false;

        double men = Math.Min(army.ColumnCapMen, Math.Max(settings.occupierMinManpower, army.manpowerPerRegiment * settings.occupierPercent / 100.0));
        if (m.CurrentManpower - m.TotalRegimentManpower() < men) return false;

        Vector3 refPos = n.Capital != null ? army.ProvinceWorldPos(n.Capital) : Vector3.zero;
        Province target = PickOccupyTarget(w, n, m, refPos, out _);
        if (target == null) return false;

        Province from = FindSafeProvince(n, army.ProvinceWorldPos(target), null);
        if (from == null) return false;

        Regiment c = m.AddColumn(from, men);
        if (c == null) return false;
        c.Role = ColumnRole.Occupy;
        if (!army.MoveRegiment(c, target)) { m.RemoveColumn(c); return false; }
        return true;
    }

    /// <summary>Idle occupation columns stay while their siege runs, then walk on or go home.</summary>
    private void AdvanceOccupiersDay()
    {
        IReadOnlyList<Military> mils = ArmyManager.Instance.Militaries;
        for (int i = 0; i < mils.Count; i++)
        {
            Military m = mils[i];
            for (int j = m.Columns.Count - 1; j >= 0; j--)
            {
                Regiment c = m.Columns[j];
                if (c.Role != ColumnRole.Occupy || c.State != RegimentState.Idle || c.InRange || c.InContact) continue;
                Province here = c.CurrentProvince ?? c.Province;
                if (here != null && _sieges.TryGetValue(here.Id, out Siege s) && s.Nation == m.Nation.Id) continue;
                AdvanceOccupier(m, c);
            }
        }
    }

    private void AdvanceOccupier(Military m, Regiment c)
    {
        Nation n = m.Nation;
        if (!WarManager.Instance.IsAtWar(n)) { _hops.Remove(c); m.RemoveColumn(c); return; }

        IReadOnlyList<War> wars = WarManager.Instance.GetWars(n);
        _hops.TryGetValue(c, out int hops);
        Province next = null;
        float bestD = float.MaxValue;
        if (hops < settings.occupierMaxHops)
            for (int i = 0; i < wars.Count; i++)
            {
                Province t = PickOccupyTarget(wars[i], n, m, c.WorldPosition, out float d);
                if (t != null && d < bestD) { bestD = d; next = t; }
            }

        if (next != null && ArmyManager.Instance.MoveRegiment(c, next)) { _hops[c] = hops + 1; return; }
        _hops.Remove(c);
        m.RemoveColumn(c);
    }

    private Province PickOccupyTarget(War w, Nation n, Military m, Vector3 refPos, out float dist)
    {
        dist = float.MaxValue;
        WarSide mine = w.SideOf(n.Id);
        if (map == null || mine == WarSide.None) return null;

        Province best = null;
        _seenTargets.Clear();
        for (int i = 0; i < n.Provinces.Count; i++) ScanOrigin(n.Provinces[i], w, mine, n, m, refPos, ref best, ref dist);
        foreach (var kv in w.Occupied)
        {
            if (w.SideOf(kv.Value) != mine) continue;
            Province held = map.GetProvince(kv.Key);
            if (held != null) ScanOrigin(held, w, mine, n, m, refPos, ref best, ref dist);
        }
        return best;
    }

    private void ScanOrigin(Province origin, War w, WarSide mine, Nation n, Military m, Vector3 refPos, ref Province best, ref float bestD)
    {
        int[] nb = origin.Neighbors;
        for (int i = 0; i < nb.Length; i++)
        {
            Province q = map.GetProvince(nb[i]);
            if (q == null || q.Owner == null || !_seenTargets.Add(q.Id)) continue;
            if (w.SideOf(q.Owner.Id) != War.Opposite(mine)) continue;
            if (w.Occupied.ContainsKey(q.Id)) continue;
            if (IsTargeted(m, q)) continue;
            float d = (ArmyManager.Instance.ProvinceWorldPos(q) - refPos).sqrMagnitude;
            if (d < bestD) { bestD = d; best = q; }
        }
    }

    private static bool IsTargeted(Military m, Province q)
    {
        for (int i = 0; i < m.Columns.Count; i++)
        {
            Regiment c = m.Columns[i];
            if (c.Role == ColumnRole.Occupy && c.HasPath && c.Destination == q) return true;
        }
        return false;
    }

    private void HandleOccupation(War w, Province p, Nation occupier)
    {
        if (p == null) return;
        if (_occupier.TryGetValue(p.Id, out int old) && (occupier == null || old != occupier.Id)) ClearProvincePixels(p);

        if (occupier != null) { _occupier[p.Id] = occupier.Id; PaintProvince(p, occupier); }
        else _occupier.Remove(p.Id);
    }

    private void HandleSpread(War w, Province p, Nation occupier) => PaintProvince(p, occupier);

    #endregion

    #region War ended

    private void HandleWarEnded(War w, WarResult result)
    {
        if (w == null || !ArmyManager.HasInstance) return;

        _intBuf.Clear();
        foreach (var kv in _sieges) if (kv.Value.War == w) _intBuf.Add(kv.Key);
        for (int i = 0; i < _intBuf.Count; i++) _sieges.Remove(_intBuf[i]);

        DropOccupiers(w.Attackers);
        DropOccupiers(w.Defenders);

        // fighting stops by itself: the hostility matrix is rebuilt on the next combat step
        if (!settings.returnToBorderAfterWar) return;
        ReturnToBorder(w, w.Attackers);
        ReturnToBorder(w, w.Defenders);
    }

    private void ReturnToBorder(War w, List<int> members)
    {
        ArmyManager army = ArmyManager.Instance;
        for (int m = 0; m < members.Count; m++)
        {
            Nation n = WorldManager.Instance.GetNation(members[m]);
            Military mil = n != null ? army.GetMilitary(n) : null;
            if (mil == null) continue;

            for (int i = 0; i < mil.Regiments.Count; i++)
            {
                Regiment r = mil.Regiments[i];
                if (r.State == RegimentState.Retreating) continue;

                Province here = r.CurrentProvince ?? r.Province;
                bool inEnemyLand = MustLeave(w, n, here);
                bool heading = r.State == RegimentState.Moving && MustLeave(w, n, r.Destination);
                if (!inEnemyLand && !heading) continue;

                Province home = FindSafeProvince(n, r.WorldPosition, null);
                if (home != null) army.MoveRegiment(r, home);
            }
        }
    }

    private void DropOccupiers(List<int> members)
    {
        ArmyManager army = ArmyManager.Instance;
        for (int k = 0; k < members.Count; k++)
        {
            Nation n = WorldManager.Instance.GetNation(members[k]);
            Military m = n != null ? army.GetMilitary(n) : null;
            if (m == null || WarManager.Instance.IsAtWar(n)) continue;
            for (int j = m.Columns.Count - 1; j >= 0; j--)
                if (m.Columns[j].Role == ColumnRole.Occupy) { _hops.Remove(m.Columns[j]); m.RemoveColumn(m.Columns[j]); }
        }
    }

    private bool MustLeave(War w, Nation me, Province p)
    {
        if (p == null || p.Owner == null || p.Owner == me) return false;
        WarSide mine = w.SideOf(me.Id), os = w.SideOf(p.Owner.Id);
        if (mine == WarSide.None || os == WarSide.None || os == mine) return false;
        return !Hostile(me, p.Owner);
    }

    #endregion

    #region Occupation overlay (painted per province)

    private Texture2D _tex;
    private Sprite _sprite;
    private GameObject _ovGo;
    private Color32[] _px;
    private int _ovW, _ovH, _ovScale;
    private bool _dirty;

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

    private void PaintProvince(Province p, Nation n)
    {
        if (_px == null || map == null || p == null || n == null || p.Tiles == null) return;
        Color32 col = n.Color;
        col.a = (byte)Mathf.RoundToInt(Mathf.Clamp01(trailAlpha) * 255f);
        int w = map.Width;
        for (int i = 0; i < p.Tiles.Length; i++)
        {
            int t = p.Tiles[i];
            _px[((t / w) / _ovScale) * _ovW + ((t % w) / _ovScale)] = col;
        }
        _dirty = true;
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

    #endregion

    #region Context menu & diagnostics

    [ContextMenu("Combat: Send Regiment (A) to Province (debugProvince / random province of B)")]
    private void MenuSend() => RunSend(false);

    [ContextMenu("Combat: Send ALL regiments of A to Province (debugProvince / random province of B)")]
    private void MenuSendAll() => RunSend(true);

    [ContextMenu("Combat: Log")]
    private void MenuLog()
    {
        int r = 0, c = 0;
        for (int i = 0; i < _units.Count; i++) { if (_units[i].InRange) r++; if (_units[i].InContact) c++; }
        Debug.Log($"[Combat] units {_units.Count}, in range {r}, in contact {c}, sieges {_sieges.Count}");
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

        Province dest = null;
        if (debugProvince >= 0 && map != null) dest = map.GetProvince(debugProvince);
        else if (b.Provinces.Count > 0) dest = b.Provinces[RNG.GetInt(0, b.Provinces.Count)];
        if (dest == null) { Debug.LogWarning("[Combat] Target province not found."); return; }

        int only = -1;
        if (!all)
        {
            if (debugRegiment >= 0) only = debugRegiment;
            else
            {
                var free = new List<int>();
                for (int i = 0; i < m.Regiments.Count; i++)
                    if (m.Regiments[i].State != RegimentState.Retreating) free.Add(i);
                if (free.Count == 0) { Debug.LogWarning("[Combat] Nation A has no free regiment."); return; }
                only = free[RNG.GetInt(0, free.Count)];
            }
        }

        ArmyManager army = ArmyManager.Instance;
        int sent = 0;
        for (int i = 0; i < m.Regiments.Count; i++)
        {
            if (!all && i != only) continue;
            Regiment r = m.Regiments[i];
            if (r.State == RegimentState.Retreating) continue;
            if (army.MoveRegiment(r, dest)) sent++;
        }
        Debug.Log($"[Combat] {a.Name} -> {dest.Name} ({b.Name}): sent {sent} regiment(s). At war: {WarManager.Instance.IsAtWar(a, b)}");
    }

    private void RefreshDiagnostics()
    {
#if UNITY_EDITOR
        int r = 0, c = 0;
        for (int i = 0; i < _units.Count; i++) { if (_units[i].InRange) r++; if (_units[i].InContact) c++; }
        diagnostics.units = _units.Count;
        diagnostics.inRange = r;
        diagnostics.inContact = c;
        diagnostics.sieges = _sieges.Count;

        siegesView.Clear();
        if (map == null) return;
        foreach (var kv in _sieges)
        {
            Province p = map.GetProvince(kv.Key);
            Nation n = WorldManager.Instance.GetNation(kv.Value.Nation);
            siegesView.Add(new SiegeInfo { province = p != null ? p.Name : "?", occupier = n != null ? n.Name : "?", progressDays = kv.Value.Progress });
        }
#endif
    }

    #endregion
}