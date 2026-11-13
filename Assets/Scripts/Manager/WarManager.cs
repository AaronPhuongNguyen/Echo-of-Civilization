using System;
using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
//  DATA
// ─────────────────────────────────────────────────────────────────────────────

public enum WarSide : byte { None = 0, Attacker = 1, Defender = 2 }
public enum WarResult : byte { Ongoing = 0, AttackerVictory = 1, DefenderVictory = 2, WhitePeace = 3 }

/// <summary>One war. Two "main" nations (the declarer and the target) plus any number of allies on each side.</summary>
public sealed class War
{
    public readonly int Id;
    public readonly int AttackerId, DefenderId;
    public readonly string Name;
    public readonly long StartDay;

    public readonly List<int> Attackers = new List<int>(4);   // nation ids (first = main attacker)
    public readonly List<int> Defenders = new List<int>(4);   // nation ids (first = main defender)
    /// <summary>province id -> nation id of the occupier. Ownership only changes when the war ends in a victory.</summary>
    public readonly Dictionary<int, int> Occupied = new Dictionary<int, int>();

    // ── war progress: every side has its own 0..100 % ──
    //   progress = milestones (capital / army destroyed / all provinces) + accumulated monthly bonus
    /// <summary>Attacker side progress 0..100 (%).</summary>
    public float AttackerProgress { get; internal set; }
    /// <summary>Defender side progress 0..100 (%).</summary>
    public float DefenderProgress { get; internal set; }
    /// <summary>Permanent part of the progress (monthly "more provinces" bonus, debug additions).</summary>
    public float AttackerBonus { get; internal set; }
    public float DefenderBonus { get; internal set; }
    /// <summary>The main nation had at least one regiment when the war started ("army destroyed" can only happen then).</summary>
    public bool AttackerHadArmy { get; internal set; }
    public bool DefenderHadArmy { get; internal set; }

    /// <summary>Attacker progress minus defender progress (-100 .. +100). Kept for old code that read the single score.</summary>
    public float Score => AttackerProgress - DefenderProgress;

    internal bool AttackerDecisionRaised, DefenderDecisionRaised;

    public float ProgressOf(WarSide side) =>
        side == WarSide.Attacker ? AttackerProgress : side == WarSide.Defender ? DefenderProgress : 0f;
    public int DaysActive { get; internal set; }
    public bool Ended { get; internal set; }
    public WarResult Result { get; internal set; }

    internal War(int id, int attacker, int defender, string name, long startDay)
    {
        Id = id; AttackerId = attacker; DefenderId = defender; Name = name; StartDay = startDay;
        Attackers.Add(attacker);
        Defenders.Add(defender);
    }

    public WarSide SideOf(int nationId)
    {
        if (Attackers.Contains(nationId)) return WarSide.Attacker;
        if (Defenders.Contains(nationId)) return WarSide.Defender;
        return WarSide.None;
    }

    public bool IsMain(int nationId) => nationId == AttackerId || nationId == DefenderId;
    public static WarSide Opposite(WarSide s) => s == WarSide.Attacker ? WarSide.Defender : s == WarSide.Defender ? WarSide.Attacker : WarSide.None;
}

[Serializable]
public class WarSettings
{
    [Header("Decision")]
    [Tooltip("At this progress a side may decide how the war ends (peace deal). The war never ends by itself.")]
    [Range(0f, 100f)] public float decisionProgress = 85f;
    [Tooltip("A nation may declare war only when the relationship value is BELOW this (Force ignores it).")]
    public float declareRelationThreshold = -10f;
    [Tooltip("Declaring war sets the relationship to Enemy (also cancels alliance / pacts).")]
    public bool makeEnemyOnDeclare = true;
    [Tooltip("Allies of the attacker / defender join automatically.")]
    public bool callAllies = true;
    [Tooltip("Defensive-pact partners of the DEFENDER join automatically.")]
    public bool callDefensivePacts = true;
    [Tooltip("Vassals follow their overlord into the war.")]
    public bool vassalsFollowOverlord = true;

    [Header("War progress (each side 0..100 %, the war never ends by itself)")]
    [Tooltip("Progress while the side holds the capital of the enemy main nation (lost again when it is liberated).")]
    [Range(0f, 100f)] public float capitalOccupiedPercent = 20f;
    [Tooltip("Progress while the army of the enemy main nation is destroyed (no regiments left).")]
    [Range(0f, 100f)] public float armyDestroyedPercent = 30f;
    [Tooltip("Progress while ALL provinces of the enemy main nation are occupied by this side.")]
    [Range(0f, 100f)] public float allProvincesPercent = 50f;
    [Tooltip("Every month the side with MORE occupied provinces gains this much (permanent).")]
    [Range(0f, 100f)] public float monthlyLeadPercent = 5f;

    [Header("Occupation spread")]
    [Tooltip("When a province becomes occupied, this many NEIGHBOURING enemy provinces (random, free of enemy fighters) are occupied with it. 0 = All of surrounding provinces.")]
    [Min(0)] public int spreadProvinces = 0;

    [Header("After the war")]
    [Tooltip("Days nobody on the two sides may declare war on each other again.")]
    public int truceDays = 360;
    [Tooltip("Relationship value set between the two main nations after peace (only if it is lower).")]
    public float relationAfterPeace = -5f;

    [Header("Army integration")]
    [Tooltip("Every day, a regiment that is standing in an enemy province occupies it (and liberates its own side's provinces).")]
    public bool autoOccupyByRegiments = true;
}

[Serializable]
public class WarInfo
{
    public int id;
    public string name;
    public string attackers;
    public string defenders;
    public float score;
    public float attackerProgress;
    public float defenderProgress;
    public int days;
    public int occupiedProvinces;
}
public enum WarOutcome { TakeOccupied, WhitePeace, Vassalize }
// ─────────────────────────────────────────────────────────────────────────────
//  WAR MANAGER  (global access: WarManager.Instance)
//
//  - Declare war (needs relation < threshold, no pact / vassal link, no truce)  /  Force declare (ignores them)
//  - Allies / defensive pacts / vassals join automatically (settings)
//  - Join / Leave / Capitulate / White peace / Victory (force end)
//  - Occupy / Liberate provinces -> war score -> automatic capitulation
//  - Occupying a province also occupies neighbouring enemy province(s) (settings.spreadProvinces)
//  - Victory transfers the provinces the winners occupy (through MapManager.SetProvinceOwner)
//  - Truce after peace, events, Inspector view, context menus (all can be forced)
// ─────────────────────────────────────────────────────────────────────────────
[DefaultExecutionOrder(-750)]
public class WarManager : TickBehaviour
{
    // ── singleton ──
    private static WarManager _instance;
    private static bool _quitting;

    public static bool HasInstance => _instance != null;

    public static WarManager Instance
    {
        get
        {
            if (_instance == null && !_quitting)
            {
#if UNITY_2022_2_OR_NEWER
                _instance = FindFirstObjectByType<WarManager>();
#else
                _instance = FindObjectOfType<WarManager>();
#endif
                if (_instance == null)
                    _instance = new GameObject("WarManager").AddComponent<WarManager>();
            }
            return _instance;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() { _instance = null; _quitting = false; }

    // ── settings ──
    public WarSettings settings = new WarSettings();
    [Tooltip("Defaults to the MapManager found in the scene (needed to transfer provinces).")]
    [SerializeField] private MapManager map;

    [Header("Context menu helpers (right-click the component title)")]
    [Tooltip("Nation id used as A (attacker / first nation). -1 = random nation.")]
    [SerializeField] private int debugNationA = -1;
    [Tooltip("Nation id used as B (defender / second nation). -1 = random nation.")]
    [SerializeField] private int debugNationB = -1;
    [Tooltip("Nation id used as C (the nation that joins a war). -1 = random nation.")]
    [SerializeField] private int debugNationC = -1;
    [Tooltip("Province id for Occupy / Liberate. -1 = random province (of the target for Occupy, of the occupied ones for Liberate).")]
    [SerializeField] private int debugProvince = -1;
    [Tooltip("Used by 'Add War Score' (positive = attacker side).")]
    [SerializeField] private float debugAmount = 10f;

    [Header("Inspector view (read-only mirror, Editor only)")]
    [SerializeField] private bool showWarsInInspector = true;
    [SerializeField] private List<WarInfo> warsView = new List<WarInfo>();
    private float _nextInspectorRefresh;

    // ── data ──
    private readonly List<War> _wars = new List<War>();
    private readonly Dictionary<int, List<War>> _byNation = new Dictionary<int, List<War>>();
    private readonly Dictionary<long, long> _truces = new Dictionary<long, long>();   // pair key -> last truce day
    private readonly List<War> _tmp = new List<War>();
    private readonly List<Province> _spreadBuf = new List<Province>();
    private int _nextWarId = 1;
    private MapManager _map;

    public IReadOnlyList<War> Wars => _wars;
    public int WarCount => _wars.Count;
    public string LastError { get; private set; }

    // ── events ──
    public event Action<War> OnWarDeclared;
    public event Action<War, WarResult> OnWarEnded;
    /// <summary>war, nation, side it joined</summary>
    public event Action<War, Nation, WarSide> OnNationJoined;
    public event Action<War, Nation> OnNationLeft;
    /// <summary>war, province, occupier (null = liberated)</summary>
    public event Action<War, Province, Nation> OnProvinceOccupationChanged;
    /// <summary>
    /// Raised AFTER OnProvinceOccupationChanged for a province that was occupied only because it
    /// neighbours a province that just got occupied (no army walked into it). war, province, occupier.
    /// </summary>
    public event Action<War, Province, Nation> OnProvinceOccupationSpread;
    /// <summary>Raised when the progress of any side changed (use War.AttackerProgress / DefenderProgress).</summary>
    public event Action<War> OnWarProgressChanged;
    /// <summary>Raised once when a side reaches decisionProgress (again if it drops below and re-reaches it).</summary>
    public event Action<War, WarSide> OnWarDecisionAvailable;

    protected override int TickPriority => -750;

    private WorldManager World => WorldManager.Instance;
    private RelationshipManager Rel => RelationshipManager.Instance;

    // ─────────────────────────────────────────────────────────────────────
    //  LIFECYCLE
    // ─────────────────────────────────────────────────────────────────────
    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Debug.LogWarning("Duplicate WarManager removed.");
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
        _map = map;

        World.OnNationRemoved += HandleNationRemoved;
        World.OnNationsCleared += ClearAll;
        DateManager.Instance.OnDayPassed += HandleDay;
        DateManager.Instance.OnMonthPassed += HandleMonth;
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        if (WorldManager.HasInstance)
        {
            WorldManager.Instance.OnNationRemoved -= HandleNationRemoved;
            WorldManager.Instance.OnNationsCleared -= ClearAll;
        }
        if (DateManager.HasInstance)
        {
            DateManager.Instance.OnDayPassed -= HandleDay;
            DateManager.Instance.OnMonthPassed -= HandleMonth;
        }
        if (_instance == this && _quitting) _instance = null;
    }

    private void OnDestroy() { if (_instance == this) _instance = null; }

    public override void Tick(float deltaTime)
    {
#if UNITY_EDITOR
        if (showWarsInInspector && Time.unscaledTime >= _nextInspectorRefresh)
        {
            _nextInspectorRefresh = Time.unscaledTime + 1f;
            RefreshView();
        }
#endif
    }

    /// <summary>Forgets every war and truce (also called when the map is regenerated). No events, no transfers.</summary>
    public void ClearAll()
    {
        _wars.Clear();
        _byNation.Clear();
        _truces.Clear();
        _nextWarId = 1;
    }

    private void HandleNationRemoved(Nation n)
    {
        if (n == null || !_byNation.TryGetValue(n.Id, out List<War> list)) return;
        _tmp.Clear();
        _tmp.AddRange(list);
        for (int i = 0; i < _tmp.Count; i++)
        {
            War w = _tmp[i];
            if (w.Ended) continue;
            if (w.IsMain(n.Id)) EndWar(w, WarResult.WhitePeace);
            else LeaveWar(w, n);
        }
        _byNation.Remove(n.Id);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  DAILY
    // ─────────────────────────────────────────────────────────────────────
    private void HandleDay()
    {
        if (_wars.Count == 0) return;
        _tmp.Clear();
        _tmp.AddRange(_wars);

        for (int i = 0; i < _tmp.Count; i++)
        {
            War w = _tmp[i];
            if (w.Ended) continue;
            w.DaysActive++;

            Nation a = World.GetNation(w.AttackerId), d = World.GetNation(w.DefenderId);
            if (a == null || a.IsEmpty) { EndWar(w, WarResult.DefenderVictory); continue; }   // main nation was wiped out
            if (d == null || d.IsEmpty) { EndWar(w, WarResult.AttackerVictory); continue; }

            if (settings.autoOccupyByRegiments) AutoOccupy(w);
            if (!w.Ended) RecalculateProgress(w);        // also catches "army destroyed"
        }
    }

    /// <summary>Every month the side that holds MORE provinces gets monthlyLeadPercent (permanent). Equal = nobody.</summary>
    private void HandleMonth()
    {
        if (_wars.Count == 0) return;
        _tmp.Clear();
        _tmp.AddRange(_wars);

        for (int i = 0; i < _tmp.Count; i++)
        {
            War w = _tmp[i];
            if (w.Ended) continue;

            int a = CountOccupied(w, WarSide.Attacker), d = CountOccupied(w, WarSide.Defender);
            float gain = Mathf.Max(0f, settings.monthlyLeadPercent);
            if (a > d) w.AttackerBonus = Mathf.Clamp(w.AttackerBonus + gain, 0f, 100f);
            else if (d > a) w.DefenderBonus = Mathf.Clamp(w.DefenderBonus + gain, 0f, 100f);
            RecalculateProgress(w);
        }
    }

    /// <summary>A regiment (not retreating) standing in a province: enemy-owned -> occupy it, own side's occupied province -> liberate it.</summary>
    private void AutoOccupy(War w)
    {
        if (!ArmyManager.HasInstance) return;
        ArmyManager army = ArmyManager.Instance;

        for (int side = 0; side < 2 && !w.Ended; side++)
        {
            List<int> members = side == 0 ? w.Attackers : w.Defenders;
            for (int m = 0; m < members.Count && !w.Ended; m++)
            {
                Nation n = World.GetNation(members[m]);
                Military mil = n != null ? army.GetMilitary(n) : null;
                if (mil == null) continue;

                for (int r = 0; r < mil.Regiments.Count && !w.Ended; r++)
                {
                    Regiment reg = mil.Regiments[r];
                    if (reg.State == RegimentState.Retreating || reg.Manpower <= 0.5) continue;
                    Province p = reg.LocationProvince;
                    if (p == null || p.Owner == null) continue;

                    WarSide ownerSide = w.SideOf(p.Owner.Id);
                    WarSide mySide = w.SideOf(n.Id);
                    if (ownerSide == WarSide.None) continue;
                    if (CombatManager.HasInstance && !CombatManager.Instance.CanTakeProvince(w, n, p)) continue;   // defenders must be cleared first

                    if (ownerSide != mySide) Occupy(w, n, p);                                   // enemy province
                    else if (w.Occupied.ContainsKey(p.Id)) Liberate(w, p);                      // our province, occupied -> take back
                }
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  QUERIES
    // ─────────────────────────────────────────────────────────────────────
    public bool IsAtWar(Nation n) => n != null && _byNation.TryGetValue(n.Id, out List<War> l) && l.Count > 0;

    /// <summary>True when a and b are on OPPOSITE sides of at least one war.</summary>
    public bool IsAtWar(Nation a, Nation b) => GetWar(a, b) != null;

    public War GetWar(Nation a, Nation b)
    {
        if (a == null || b == null || a == b || !_byNation.TryGetValue(a.Id, out List<War> l)) return null;
        for (int i = 0; i < l.Count; i++)
        {
            WarSide sa = l[i].SideOf(a.Id), sb = l[i].SideOf(b.Id);
            if (sa != WarSide.None && sb != WarSide.None && sa != sb) return l[i];
        }
        return null;
    }

    public IReadOnlyList<War> GetWars(Nation n) =>
        n != null && _byNation.TryGetValue(n.Id, out List<War> l) ? l : (IReadOnlyList<War>)Array.Empty<War>();

    public bool HasTruce(Nation a, Nation b) => TruceDaysLeft(a, b) > 0;

    public long TruceDaysLeft(Nation a, Nation b)
    {
        if (a == null || b == null || !_truces.TryGetValue(Key(a.Id, b.Id), out long end)) return 0;
        long left = end - DateManager.Instance.TotalDays;
        return left > 0 ? left : 0;
    }

    /// <summary>Fills 'result' with every nation that is on the opposite side of n in any war.</summary>
    public void GetEnemies(Nation n, List<Nation> result)
    {
        result.Clear();
        if (n == null || !_byNation.TryGetValue(n.Id, out List<War> l)) return;
        for (int i = 0; i < l.Count; i++)
        {
            WarSide mine = l[i].SideOf(n.Id);
            List<int> enemies = mine == WarSide.Attacker ? l[i].Defenders : l[i].Attackers;
            for (int j = 0; j < enemies.Count; j++)
            {
                Nation e = World.GetNation(enemies[j]);
                if (e != null && !result.Contains(e)) result.Add(e);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  DECLARE WAR
    //   normal : relation < declareRelationThreshold, no truce, no alliance / pact / vassal link
    //   force  : ignores all of that (cancels the agreements instead)
    // ─────────────────────────────────────────────────────────────────────
    public War DeclareWar(Nation attacker, Nation defender) => DeclareWar(attacker, defender, false);

    public War ForceDeclareWar(Nation attacker, Nation defender) => DeclareWar(attacker, defender, true);

    public War DeclareWar(Nation attacker, Nation defender, bool force)
    {
        if (!Check(attacker, defender)) return null;
        if (attacker.IsEmpty || defender.IsEmpty) { Fail("A nation without provinces cannot fight."); return null; }
        if (IsAtWar(attacker, defender)) { Fail("They are already at war."); return null; }

        RelationshipManager rel = Rel;
        if (!force)
        {
            if (HasTruce(attacker, defender)) { Fail("Truce: " + TruceDaysLeft(attacker, defender) + " days left."); return null; }

            float value = rel.GetValue(attacker, defender);
            if (!(value < settings.declareRelationThreshold))
            {
                Fail("Relationship " + value.ToString("F1") + " is not below " + settings.declareRelationThreshold + ".");
                return null;
            }

            RelationFlags f = rel.GetFlags(attacker, defender);
            if ((f & (RelationFlags.Alliance | RelationFlags.DefensivePact | RelationFlags.NonAggression | RelationFlags.Vassal)) != 0)
            {
                Fail("Break the alliance / pact / vassal link first (or use Force).");
                return null;
            }
        }
        else
        {
            // a vassal link is not removed by MakeEnemy, so free it explicitly
            if (rel.IsVassalOf(defender, attacker)) rel.ReleaseVassal(attacker, defender);
            else if (rel.IsVassalOf(attacker, defender)) rel.ReleaseVassal(defender, attacker);
        }

        if (settings.makeEnemyOnDeclare) rel.MakeEnemy(attacker, defender);   // enemies cannot keep pacts / alliances

        var war = new War(_nextWarId++, attacker.Id, defender.Id,
            attacker.Name + " vs " + defender.Name + " War", DateManager.Instance.TotalDays);
        _wars.Add(war);
        Register(war, attacker.Id);
        Register(war, defender.Id);
        _truces.Remove(Key(attacker.Id, defender.Id));

        war.AttackerHadArmy = HasArmy(attacker);
        war.DefenderHadArmy = HasArmy(defender);

        OnWarDeclared?.Invoke(war);
        CallAllies(war, attacker, WarSide.Attacker);
        CallAllies(war, defender, WarSide.Defender);
        return war;
    }

    /// <summary>Allies / (defender only) defensive-pact partners / vassals of 'leader' join the leader's side.</summary>
    private void CallAllies(War war, Nation leader, WarSide side)
    {
        if (!settings.callAllies && !settings.vassalsFollowOverlord) return;
        RelationshipManager rel = Rel;
        IReadOnlyList<Nation> all = World.Nations;

        for (int i = 0; i < all.Count; i++)
        {
            Nation o = all[i];
            if (o == leader || o.IsEmpty || war.SideOf(o.Id) != WarSide.None) continue;

            bool join = false;
            if (settings.callAllies && rel.IsAllied(leader, o)) join = true;
            else if (settings.callAllies && settings.callDefensivePacts && side == WarSide.Defender && rel.HasDefensivePact(leader, o)) join = true;
            else if (settings.vassalsFollowOverlord && (rel.IsVassalOf(o, leader))) join = true;

            if (join) AddMember(war, o, side);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  JOIN / LEAVE / CAPITULATE
    // ─────────────────────────────────────────────────────────────────────
    public bool JoinWar(War war, Nation nation, WarSide side)
    {
        LastError = null;
        if (war == null || war.Ended) return Fail("War is not running.");
        if (nation == null || World.GetNation(nation.Id) != nation) return Fail("Nation is not registered.");
        if (nation.IsEmpty) return Fail("A nation without provinces cannot fight.");
        if (side != WarSide.Attacker && side != WarSide.Defender) return Fail("Choose Attacker or Defender.");
        if (war.SideOf(nation.Id) != WarSide.None) return Fail("Already in this war.");
        AddMember(war, nation, side);
        return true;
    }

    /// <summary>Convenience: 'nation' joins the side that 'friend' fights on.</summary>
    public bool JoinWarAlongside(Nation nation, Nation friend)
    {
        LastError = null;
        if (friend == null || !_byNation.TryGetValue(friend.Id, out List<War> l) || l.Count == 0) return Fail("That nation is not at war.");
        War w = l[0];
        return JoinWar(w, nation, w.SideOf(friend.Id));
    }

    private void AddMember(War war, Nation n, WarSide side)
    {
        (side == WarSide.Attacker ? war.Attackers : war.Defenders).Add(n.Id);
        Register(war, n.Id);
        OnNationJoined?.Invoke(war, n, side);
    }

    /// <summary>A NON-main member leaves the war. Its occupations stay with the war.</summary>
    public bool LeaveWar(War war, Nation nation)
    {
        LastError = null;
        if (war == null || war.Ended) return Fail("War is not running.");
        if (nation == null || war.SideOf(nation.Id) == WarSide.None) return Fail("Nation is not in this war.");
        if (war.IsMain(nation.Id)) return Fail("A main belligerent cannot leave: use Capitulate / peace.");

        war.Attackers.Remove(nation.Id);
        war.Defenders.Remove(nation.Id);
        Unregister(war, nation.Id);

        // whatever it occupied is released
        List<int> free = null;
        foreach (var kv in war.Occupied) if (kv.Value == nation.Id) (free ?? (free = new List<int>())).Add(kv.Key);
        if (free != null)
            for (int i = 0; i < free.Count; i++)
            {
                war.Occupied.Remove(free[i]);
                Province fp = _map != null ? _map.GetProvince(free[i]) : null;
                if (fp != null) OnProvinceOccupationChanged?.Invoke(war, fp, null);
            }

        RecalculateProgress(war);
        OnNationLeft?.Invoke(war, nation);
        return true;
    }

    /// <summary>The nation gives up. A main belligerent loses the war (the other side wins), an ally simply leaves.</summary>
    public bool Capitulate(War war, Nation loser)
    {
        LastError = null;
        if (war == null || war.Ended) return Fail("War is not running.");
        WarSide s = loser != null ? war.SideOf(loser.Id) : WarSide.None;
        if (s == WarSide.None) return Fail("Nation is not in this war.");
        if (!war.IsMain(loser.Id)) return LeaveWar(war, loser);
        return EndWar(war, s == WarSide.Attacker ? WarResult.DefenderVictory : WarResult.AttackerVictory);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  PEACE / END
    // ─────────────────────────────────────────────────────────────────────
    public bool WhitePeace(War war) => EndWar(war, WarResult.WhitePeace);
    public bool AttackerWins(War war) => EndWar(war, WarResult.AttackerVictory);
    public bool DefenderWins(War war) => EndWar(war, WarResult.DefenderVictory);

    /// <summary>White peace between two nations: ends the war they fight each other in.</summary>
    public bool MakePeace(Nation a, Nation b)
    {
        LastError = null;
        War w = GetWar(a, b);
        if (w == null) return Fail("They are not at war.");
        return EndWar(w, WarResult.WhitePeace);
    }

    /// <summary>Ends every running war (white peace).</summary>
    public void EndAllWars()
    {
        _tmp.Clear();
        _tmp.AddRange(_wars);
        for (int i = 0; i < _tmp.Count; i++) EndWar(_tmp[i], WarResult.WhitePeace);
    }

    public bool CanDecide(War war, WarSide side) =>
    war != null && !war.Ended && side != WarSide.None && war.ProgressOf(side) >= settings.decisionProgress;

    /// <summary>The side that reached decisionProgress picks what happens. Player UI and AI both call this.</summary>
    public bool Decide(War war, WarSide winner, WarOutcome outcome)
    {
        LastError = null;
        if (!CanDecide(war, winner)) return Fail("This side cannot decide yet.");

        Nation winMain = World.GetNation(winner == WarSide.Attacker ? war.AttackerId : war.DefenderId);
        Nation loseMain = World.GetNation(winner == WarSide.Attacker ? war.DefenderId : war.AttackerId);

        switch (outcome)
        {
            case WarOutcome.TakeOccupied:
                return EndWar(war, winner == WarSide.Attacker ? WarResult.AttackerVictory : WarResult.DefenderVictory);
            case WarOutcome.WhitePeace:
                return EndWar(war, WarResult.WhitePeace);
            case WarOutcome.Vassalize:
                if (winMain == null || loseMain == null) return Fail("Nation missing.");
                if (!EndWar(war, WarResult.WhitePeace)) return false;     // no land changes, the loser becomes a vassal
                return Rel.Vassalize(winMain, loseMain);
        }
        return false;
    }

    private void CheckDecision(War war, float a, float d)
    {
        float t = settings.decisionProgress;
        if (a >= t) { if (!war.AttackerDecisionRaised) { war.AttackerDecisionRaised = true; OnWarDecisionAvailable?.Invoke(war, WarSide.Attacker); } }
        else war.AttackerDecisionRaised = false;
        if (d >= t) { if (!war.DefenderDecisionRaised) { war.DefenderDecisionRaised = true; OnWarDecisionAvailable?.Invoke(war, WarSide.Defender); } }
        else war.DefenderDecisionRaised = false;
    }

    /// <summary>
    /// Ends the war. Victory: the provinces occupied by the winning side change owner (to their occupier).
    /// Always: truce between the two sides, relationship of the main nations lifted to 'relationAfterPeace'.
    /// OnWarEnded is raised LAST: the war is already unregistered, so nations that are not at war any more are
    /// no longer hostile (CombatManager uses this to end battles and send the armies back to the border).
    /// </summary>
    public bool EndWar(War war, WarResult result)
    {
        LastError = null;
        if (war == null || war.Ended) return Fail("War is not running.");
        if (result == WarResult.Ongoing) return Fail("Choose a result.");

        war.Ended = true;
        war.Result = result;

        if (result == WarResult.AttackerVictory) ApplyOccupations(war, WarSide.Attacker);
        else if (result == WarResult.DefenderVictory) ApplyOccupations(war, WarSide.Defender);
        if (war.Occupied.Count > 0)
        {
            var ids = new List<int>(war.Occupied.Keys);
            war.Occupied.Clear();
            for (int i = 0; i < ids.Count; i++)
            {
                Province op = _map != null ? _map.GetProvince(ids[i]) : null;
                if (op != null) OnProvinceOccupationChanged?.Invoke(war, op, null);
            }
        }

        _wars.Remove(war);
        for (int i = 0; i < war.Attackers.Count; i++) Unregister(war, war.Attackers[i]);
        for (int i = 0; i < war.Defenders.Count; i++) Unregister(war, war.Defenders[i]);

        // truce between everybody who fought on opposite sides
        long end = DateManager.Instance.TotalDays + Mathf.Max(0, settings.truceDays);
        if (settings.truceDays > 0)
            for (int i = 0; i < war.Attackers.Count; i++)
                for (int j = 0; j < war.Defenders.Count; j++)
                    _truces[Key(war.Attackers[i], war.Defenders[j])] = end;

        Nation a = World.GetNation(war.AttackerId), d = World.GetNation(war.DefenderId);
        if (a != null && d != null && RelationshipManager.HasInstance)
        {
            RelationshipManager rel = RelationshipManager.Instance;
            rel.StopRelationshipChange(a, d);
            if (rel.GetValue(a, d) < settings.relationAfterPeace) rel.SetValue(a, d, settings.relationAfterPeace);
        }

        OnWarEnded?.Invoke(war, result);
        return true;
    }

    private void ApplyOccupations(War war, WarSide winner)
    {
        if (war.Occupied.Count == 0) return;
        WarSide loser = War.Opposite(winner);

        var list = new List<KeyValuePair<int, int>>(war.Occupied);
        for (int i = 0; i < list.Count; i++)
        {
            int pid = list[i].Key, occupier = list[i].Value;
            Province p = _map != null ? _map.GetProvince(pid) : null;
            if (p == null || p.Owner == null) continue;
            if (war.SideOf(occupier) != winner || war.SideOf(p.Owner.Id) != loser) continue;

            Nation target = World.GetNation(occupier);
            if (target == null) continue;

            Nation old = p.Owner;
            if (_map != null && _map.IsReady) _map.SetProvinceOwner(pid, occupier);
            else World.AssignProvince(p, target);
            OnProvinceOccupationChanged?.Invoke(war, p, null);   // occupation is over: it became an annexation
            if (old.IsEmpty && ArmyManager.HasInstance) { /* ArmyManager cleans up through WorldManager events when removed */ }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  OCCUPATION & WAR SCORE
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 'occupier' (must be in the war) occupies a province owned by the OTHER side.
    /// A newly occupied province also occupies up to settings.spreadProvinces neighbouring enemy provinces.
    /// </summary>
    public bool Occupy(War war, Nation occupier, Province province)
    {
        LastError = null;
        if (war == null || war.Ended) return Fail("War is not running.");
        if (occupier == null || province == null) return Fail("Nation / province is null.");
        WarSide mine = war.SideOf(occupier.Id);
        if (mine == WarSide.None) return Fail("The occupier is not in this war.");
        if (province.Owner == null) return Fail("Province has no owner.");
        WarSide ownerSide = war.SideOf(province.Owner.Id);
        if (ownerSide == WarSide.None) return Fail("The province owner is not in this war.");
        if (ownerSide == mine) return Fail("That province belongs to the occupier's own side.");

        bool wasOccupied = war.Occupied.TryGetValue(province.Id, out int prev);
        if (wasOccupied && prev == occupier.Id) return Fail("Already occupied by this nation.");

        war.Occupied[province.Id] = occupier.Id;

        OnProvinceOccupationChanged?.Invoke(war, province, occupier);

        if (!wasOccupied) SpreadOccupation(war, occupier, province, mine);
        RecalculateProgress(war);                         // capital / all provinces give progress directly
        return true;
    }

    /// <summary>Occupies up to settings.spreadProvinces random neighbours of 'origin' that belong to the enemy side.</summary>
    private void SpreadOccupation(War war, Nation occupier, Province origin, WarSide mine)
    {
        int count = settings.spreadProvinces;
        if (count < 0 || _map == null || origin.Neighbors == null) return;   // -1 = off

        _spreadBuf.Clear();
        int[] nb = origin.Neighbors;
        for (int i = 0; i < nb.Length; i++)
        {
            Province q = _map.GetProvince(nb[i]);
            if (q == null || q.Owner == null) continue;
            WarSide os = war.SideOf(q.Owner.Id);
            if (os == WarSide.None || os == mine) continue;                         // only enemy land
            if (war.Occupied.ContainsKey(q.Id)) continue;                           // already occupied
            if (CombatManager.HasInstance && !CombatManager.Instance.CanTakeProvince(war, occupier, q)) continue;   // enemy fighters / battle there
            _spreadBuf.Add(q);
        }

        int take = count == 0 ? _spreadBuf.Count : count;                           // 0 = all neighbours
        for (int n = 0; n < take && _spreadBuf.Count > 0 && !war.Ended; n++)
        {
            int idx = RNG.GetInt(0, _spreadBuf.Count);
            Province pick = _spreadBuf[idx];
            _spreadBuf.RemoveAt(idx);

            war.Occupied[pick.Id] = occupier.Id;
            OnProvinceOccupationChanged?.Invoke(war, pick, occupier);
            OnProvinceOccupationSpread?.Invoke(war, pick, occupier);
        }
    }

    /// <summary>Removes the occupation of a province and takes back its war score.</summary>
    public bool Liberate(War war, Province province)
    {
        LastError = null;
        if (war == null || war.Ended) return Fail("War is not running.");
        if (province == null || !war.Occupied.TryGetValue(province.Id, out int occ)) return Fail("Province is not occupied in this war.");

        war.Occupied.Remove(province.Id);

        OnProvinceOccupationChanged?.Invoke(war, province, null);
        RecalculateProgress(war);                         // a liberated capital takes its progress back
        return true;
    }

    /// <summary>Adds permanent progress (percent): positive = attacker side, negative = defender side. Never ends the war.</summary>
    public bool AddWarScore(War war, float amount)
    {
        LastError = null;
        if (war == null || war.Ended) return Fail("War is not running.");
        if (amount >= 0f) war.AttackerBonus = Mathf.Clamp(war.AttackerBonus + amount, 0f, 100f);
        else war.DefenderBonus = Mathf.Clamp(war.DefenderBonus - amount, 0f, 100f);
        RecalculateProgress(war);
        return true;
    }

    public int CountOccupied(War war, WarSide bySide)
    {
        int c = 0;
        foreach (var kv in war.Occupied) if (war.SideOf(kv.Value) == bySide) c++;
        return c;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  WAR PROGRESS (0..100 % per side). At 100 % NOTHING happens (AI / player decide later).
    //   capital of the enemy main nation occupied   : +capitalOccupiedPercent   (20)
    //   enemy main nation's army destroyed          : +armyDestroyedPercent     (30)
    //   ALL provinces of the enemy main nation held : +allProvincesPercent      (50)
    //   side with more occupied provinces           : +monthlyLeadPercent every month (5, permanent)
    //  The first three are live: they disappear again when the situation is reversed (capital liberated ...).
    // ─────────────────────────────────────────────────────────────────────
    private void RecalculateProgress(War war)
    {
        if (war == null || war.Ended) return;

        float a = Mathf.Clamp(Milestones(war, WarSide.Attacker) + war.AttackerBonus, 0f, 100f);
        float d = Mathf.Clamp(Milestones(war, WarSide.Defender) + war.DefenderBonus, 0f, 100f);
        if (Mathf.Approximately(a, war.AttackerProgress) && Mathf.Approximately(d, war.DefenderProgress)) return;

        war.AttackerProgress = a;
        war.DefenderProgress = d;
        CheckDecision(war, a, d);
        OnWarProgressChanged?.Invoke(war);
    }

    /// <summary>Progress that 'side' earns from the situation against the MAIN nation of the other side.</summary>
    private float Milestones(War war, WarSide side)
    {
        int enemyId = side == WarSide.Attacker ? war.DefenderId : war.AttackerId;
        Nation enemy = World.GetNation(enemyId);
        if (enemy == null || enemy.IsEmpty) return 0f;

        float sum = 0f;

        Province cap = enemy.Capital;
        if (cap != null && war.Occupied.TryGetValue(cap.Id, out int occ) && war.SideOf(occ) == side)
            sum += settings.capitalOccupiedPercent;

        bool enemyHadArmy = side == WarSide.Attacker ? war.DefenderHadArmy : war.AttackerHadArmy;
        if (enemyHadArmy && IsArmyDestroyed(enemy)) sum += settings.armyDestroyedPercent;

        if (AllProvincesOccupied(war, enemy, side)) sum += settings.allProvincesPercent;
        return sum;
    }

    private static bool HasArmy(Nation n)
    {
        if (n == null || !ArmyManager.HasInstance) return false;
        Military m = ArmyManager.Instance.GetMilitary(n);
        return m != null && m.Regiments.Count > 0;
    }

    private static bool IsArmyDestroyed(Nation n)
    {
        if (!ArmyManager.HasInstance) return false;
        Military m = ArmyManager.Instance.GetMilitary(n);
        return m == null || (m.Regiments.Count == 0 && m.Columns.Count == 0);
    }

    private static bool AllProvincesOccupied(War war, Nation enemy, WarSide side)
    {
        IReadOnlyList<Province> list = enemy.Provinces;
        if (list.Count == 0) return false;
        for (int i = 0; i < list.Count; i++)
            if (!war.Occupied.TryGetValue(list[i].Id, out int occ) || war.SideOf(occ) != side) return false;
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  INTERNAL
    // ─────────────────────────────────────────────────────────────────────
    private static long Key(int a, int b) =>
        a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    private void Register(War w, int nationId)
    {
        if (!_byNation.TryGetValue(nationId, out List<War> l)) _byNation[nationId] = l = new List<War>(2);
        if (!l.Contains(w)) l.Add(w);
    }

    private void Unregister(War w, int nationId)
    {
        if (_byNation.TryGetValue(nationId, out List<War> l))
        {
            l.Remove(w);
            if (l.Count == 0) _byNation.Remove(nationId);
        }
    }

    private bool Check(Nation a, Nation b)
    {
        LastError = null;
        if (a == null || b == null) return Fail("Nation is null.");
        if (a == b) return Fail("A nation cannot fight itself.");
        WorldManager w = World;
        if (w == null || w.GetNation(a.Id) != a || w.GetNation(b.Id) != b) return Fail("Nation is not registered in the WorldManager.");
        return true;
    }

    private bool Fail(string message)
    {
        LastError = message;
        return false;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  CONTEXT MENU  (Inspector, play mode, after the map is generated)
    //   A = attacker, B = defender, C = third nation. -1 = RANDOM. Results go to the Console.
    //   "Force" items ignore every condition.
    // ─────────────────────────────────────────────────────────────────────
    [ContextMenu("War: Declare War (A -> B, needs relation < threshold)")]
    private void MenuDeclare() => RunMenu("Declare War", (a, b) => DeclareWar(a, b, false) != null);

    [ContextMenu("War: FORCE Declare War (A -> B)")]
    private void MenuForceDeclare() => RunMenu("FORCE Declare War", (a, b) => DeclareWar(a, b, true) != null);

    [ContextMenu("War: C Joins A's Side")]
    private void MenuJoinA() => RunWar("C joins A", (a, b, w) => JoinWar(w, PickC(a, b), w.SideOf(a.Id)));

    [ContextMenu("War: C Joins B's Side")]
    private void MenuJoinB() => RunWar("C joins B", (a, b, w) => JoinWar(w, PickC(a, b), w.SideOf(b.Id)));

    [ContextMenu("War: C Leaves War")]
    private void MenuLeave() => RunWar("C leaves", (a, b, w) => LeaveWar(w, PickC(a, b)));

    [ContextMenu("War: A Occupies a Province of B (debugProvince, or random)")]
    private void MenuOccupy() => RunWar("A occupies", (a, b, w) => Occupy(w, a, PickProvince(b)));

    [ContextMenu("War: Liberate a Province (debugProvince, or random occupied)")]
    private void MenuLiberate() => RunWar("Liberate", (a, b, w) =>
    {
        Province p = debugProvince >= 0 && _map != null ? _map.GetProvince(debugProvince) : null;
        if (p == null && w.Occupied.Count > 0)
        {
            var ids = new List<int>(w.Occupied.Keys);
            p = _map != null ? _map.GetProvince(ids[RNG.GetInt(0, ids.Count)]) : null;
        }
        return Liberate(w, p);
    });

    [ContextMenu("War: Add War Score (debugAmount, + = A's side)")]
    private void MenuScore() => RunWar("Score " + debugAmount, (a, b, w) => AddWarScore(w, w.SideOf(a.Id) == WarSide.Attacker ? debugAmount : -debugAmount));

    [ContextMenu("War: A Capitulates")]
    private void MenuCapitulateA() => RunWar("A capitulates", (a, b, w) => Capitulate(w, a));

    [ContextMenu("War: B Capitulates")]
    private void MenuCapitulateB() => RunWar("B capitulates", (a, b, w) => Capitulate(w, b));

    [ContextMenu("War: White Peace (A <-> B)")]
    private void MenuWhitePeace() => RunMenu("White Peace", MakePeace);

    [ContextMenu("War: FORCE Attacker Victory")]
    private void MenuAttackerWins() => RunWar("Attacker victory", (a, b, w) => AttackerWins(w));

    [ContextMenu("War: FORCE Defender Victory")]
    private void MenuDefenderWins() => RunWar("Defender victory", (a, b, w) => DefenderWins(w));

    [ContextMenu("War: End All Wars (white peace)")]
    private void MenuEndAll() { EndAllWars(); RefreshView(); Debug.Log("[War] All wars ended."); }

    [ContextMenu("War: Clear Truce (A <-> B)")]
    private void MenuClearTruce() => RunMenu("Clear truce", (a, b) => _truces.Remove(Key(a.Id, b.Id)) || true);

    [ContextMenu("War: Log War")]
    private void MenuLog() => RunMenu("Log", (a, b) => true);

    [ContextMenu("War: Clear All Wars (no transfers)")]
    private void MenuClear() { ClearAll(); RefreshView(); Debug.Log("[War] Cleared."); }

    private void RunMenu(string label, Func<Nation, Nation, bool> action)
    {
        if (!PickPair(out Nation a, out Nation b)) return;
        bool ok = action(a, b);
        Debug.Log($"[War] {label}: {a.Name} (#{a.Id}) vs {b.Name} (#{b.Id}) -> " + (ok ? "OK" : "FAILED: " + LastError) + " | " + Describe(a, b));
        RefreshView();
    }

    private void RunWar(string label, Func<Nation, Nation, War, bool> action)
    {
        if (!PickPair(out Nation a, out Nation b)) return;
        War w = GetWar(a, b);
        if (w == null) { Debug.LogWarning($"[War] {a.Name} and {b.Name} are not at war (declare it first)."); return; }
        bool ok = action(a, b, w);
        Debug.Log($"[War] {label}: {w.Name} -> " + (ok ? "OK" : "FAILED: " + LastError) + " | " + Describe(a, b));
        RefreshView();
    }

    private bool PickPair(out Nation a, out Nation b)
    {
        a = b = null;
        WorldManager w = World;
        if (w == null || w.NationCount < 2)
        {
            Debug.LogWarning("[War] Need at least 2 nations (generate the map first).");
            return false;
        }

        a = debugNationA >= 0 ? w.GetNation(debugNationA) : w.GetRandomNation();
        if (a == null) { Debug.LogWarning("[War] Nation A not found."); return false; }

        if (debugNationB >= 0) b = w.GetNation(debugNationB);
        else
            for (int tries = 0; tries < 32 && (b == null || b == a); tries++) b = w.GetRandomNation();

        if (b == null || b == a) { Debug.LogWarning("[War] Nation B not found (or equals A)."); return false; }
        return true;
    }

    private Nation PickC(Nation a, Nation b)
    {
        WorldManager w = World;
        if (debugNationC >= 0) return w.GetNation(debugNationC);
        Nation c = null;
        for (int tries = 0; tries < 32 && (c == null || c == a || c == b); tries++) c = w.GetRandomNation();
        return c;
    }

    /// <summary>debugProvince, or (-1) a RANDOM province of 'owner'.</summary>
    private Province PickProvince(Nation owner)
    {
        if (_map == null) return null;
        if (debugProvince >= 0) return _map.GetProvince(debugProvince);
        if (owner == null || owner.Provinces.Count == 0) return null;
        return owner.Provinces[RNG.GetInt(0, owner.Provinces.Count)];
    }

    private string Describe(Nation a, Nation b)
    {
        War w = GetWar(a, b);
        string rel = RelationshipManager.HasInstance ? Rel.GetValue(a, b).ToString("F1") : "?";
        if (w == null) return $"no war, relation {rel}, truce {TruceDaysLeft(a, b)}d";
        return $"war #{w.Id} progress A {w.AttackerProgress:F0}% / D {w.DefenderProgress:F0}%, days {w.DaysActive}, attackers {SideText(w.Attackers)}, defenders {SideText(w.Defenders)}, " +
               $"occupied {w.Occupied.Count}, relation {rel}";
    }

    private string SideText(List<int> ids)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < ids.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            Nation n = World.GetNation(ids[i]);
            sb.Append(n != null ? n.Name : "#" + ids[i]);
        }
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  INSPECTOR VIEW
    // ─────────────────────────────────────────────────────────────────────
    private void RefreshView()
    {
#if UNITY_EDITOR
        warsView.Clear();
        if (!showWarsInInspector) return;
        for (int i = 0; i < _wars.Count; i++)
        {
            War w = _wars[i];
            warsView.Add(new WarInfo
            {
                id = w.Id,
                name = w.Name,
                attackers = SideText(w.Attackers),
                defenders = SideText(w.Defenders),
                score = w.Score,
                attackerProgress = w.AttackerProgress,
                defenderProgress = w.DefenderProgress,
                days = w.DaysActive,
                occupiedProvinces = w.Occupied.Count
            });
        }
#endif
    }
}