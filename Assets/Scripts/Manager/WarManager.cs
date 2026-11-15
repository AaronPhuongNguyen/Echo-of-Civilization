using System;
using System.Collections.Generic;
using UnityEngine;

#region Data types

public enum WarSide : byte { None = 0, Attacker = 1, Defender = 2 }
public enum WarResult : byte { Ongoing = 0, AttackerVictory = 1, DefenderVictory = 2, WhitePeace = 3 }
public enum WarOutcome { TakeOccupied, WhitePeace, Vassalize }

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
    /// <summary>Provinces demanded in the peace deal: province id -> nation id that receives it. Empty = use Occupied.</summary>
    public readonly Dictionary<int, int> Claims = new Dictionary<int, int>();

    /// <summary>Attacker / defender progress 0..100 (%): milestones + permanent bonus.</summary>
    public float AttackerProgress { get; internal set; }
    public float DefenderProgress { get; internal set; }
    public float AttackerBonus { get; internal set; }
    public float DefenderBonus { get; internal set; }
    /// <summary>The main nation had a regiment when the war started ("army destroyed" can only happen then).</summary>
    public bool AttackerHadArmy { get; internal set; }
    public bool DefenderHadArmy { get; internal set; }

    /// <summary>Attacker progress minus defender progress (-100 .. +100).</summary>
    public float Score => AttackerProgress - DefenderProgress;

    internal bool AttackerDecisionRaised, DefenderDecisionRaised;

    public long LastActivityDay { get; internal set; }
    public int DaysActive { get; internal set; }
    public bool Ended { get; internal set; }
    public WarResult Result { get; internal set; }

    internal War(int id, int attacker, int defender, string name, long startDay)
    {
        Id = id; AttackerId = attacker; DefenderId = defender; Name = name; StartDay = startDay;
        Attackers.Add(attacker);
        Defenders.Add(defender);
        LastActivityDay = startDay;
    }

    public float ProgressOf(WarSide side) =>
        side == WarSide.Attacker ? AttackerProgress : side == WarSide.Defender ? DefenderProgress : 0f;

    public WarSide SideOf(int nationId)
    {
        if (Attackers.Contains(nationId)) return WarSide.Attacker;
        if (Defenders.Contains(nationId)) return WarSide.Defender;
        return WarSide.None;
    }

    public bool IsMain(int nationId) => nationId == AttackerId || nationId == DefenderId;

    public static WarSide Opposite(WarSide s) =>
        s == WarSide.Attacker ? WarSide.Defender : s == WarSide.Defender ? WarSide.Attacker : WarSide.None;
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
    public bool callAllies = true;
    [Tooltip("Defensive-pact partners of the DEFENDER join automatically.")]
    public bool callDefensivePacts = true;
    public bool vassalsFollowOverlord = true;

    [Header("War progress (each side 0..100 %, the war never ends by itself)")]
    [Range(0f, 100f)] public float capitalOccupiedPercent = 20f;
    [Range(0f, 100f)] public float armyDestroyedPercent = 30f;
    [Range(0f, 100f)] public float allProvincesPercent = 50f;
    [Tooltip("Every month the side with MORE occupied provinces gains this much (permanent).")]
    [Range(0f, 100f)] public float monthlyLeadPercent = 5f;

    [Header("Peace demands")]
    [Min(0.1f)] public float provinceScoreCost = 8f;
    [Min(0f)] public float capitalScoreCost = 20f;
    [Range(0f, 1f)] public float occupiedCostFactor = 0.5f;

    [Header("Occupation spread")]
    [Tooltip("A newly occupied province also occupies this many neighbouring enemy provinces (free of enemy fighters). 0 = ALL neighbours.")]
    [Min(0)] public int spreadProvinces = 0;

    [Header("After the war")]
    public int truceDays = 360;
    public float relationAfterPeace = -5f;

    [Header("Idle war")]
    [Tooltip("A war with no occupation change / siege for this many YEARS ends in white peace. 0 = off.")]
    [Min(0f)] public float idleYearsWhitePeace = 5f;
}

[Serializable]
public class WarInfo
{
    public int id;
    public string name, attackers, defenders;
    public float score, attackerProgress, defenderProgress;
    public int days, occupiedProvinces;
}

#endregion

// ─────────────────────────────────────────────────────────────────────────────
//  WAR MANAGER  (WarManager.Instance)
//   Declare / join / leave / capitulate / peace, allies + pacts + vassals, truce.
//   Occupation is DONE BY CombatManager's siege (Occupy / Liberate are called from there).
//   Occupying also spreads to neighbouring enemy provinces. Victory transfers the occupied / claimed provinces.
//   OnWarEnded is raised LAST (war already unregistered) so CombatManager can send the armies home.
// ─────────────────────────────────────────────────────────────────────────────
[DefaultExecutionOrder(-750)]
public class WarManager : TickBehaviour
{
    #region Singleton

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

    #endregion

    #region Inspector, data & events

    public WarSettings settings = new WarSettings();
    [Tooltip("Defaults to the MapManager found in the scene (needed to transfer provinces).")]
    [SerializeField] private MapManager map;

    [Header("Context menu helpers (right-click the component title)")]
    [SerializeField] private int debugNationA = -1;
    [SerializeField] private int debugNationB = -1;
    [SerializeField] private int debugNationC = -1;
    [SerializeField] private int debugProvince = -1;
    [SerializeField] private float debugAmount = 10f;

    [Header("Diagnostics (Editor only, throttled)")]
    [SerializeField] private bool showWarsInInspector = true;
    [Min(0.25f)] public float diagnosticsInterval = 1f;
    [SerializeField] private List<WarInfo> warsView = new List<WarInfo>();
    private float _nextInspectorRefresh;

    private readonly List<War> _wars = new List<War>();
    private readonly Dictionary<int, List<War>> _byNation = new Dictionary<int, List<War>>();
    private readonly Dictionary<long, long> _truces = new Dictionary<long, long>();
    private readonly List<War> _tmp = new List<War>();             // daily / month / nation removal loops
    private readonly List<War> _tmpSurrender = new List<War>();
    private readonly List<Province> _spreadBuf = new List<Province>();
    private readonly List<Nation> _owners = new List<Nation>();
    private readonly List<Nation> _victims = new List<Nation>();
    private readonly List<int> _idBuf = new List<int>();
    private readonly List<KeyValuePair<int, int>> _pairBuf = new List<KeyValuePair<int, int>>();
    private readonly HashSet<int> _seen = new HashSet<int>();

    private int _nextWarId = 1;
    private MapManager _map;

    public IReadOnlyList<War> Wars => _wars;
    public int WarCount => _wars.Count;
    public string LastError { get; private set; }

    public event Action<War> OnWarDeclared;
    public event Action<War, WarResult> OnWarEnded;
    public event Action<War, Nation, WarSide> OnNationJoined;
    public event Action<War, Nation> OnNationLeft;
    /// <summary>war, province, occupier (null = liberated)</summary>
    public event Action<War, Province, Nation> OnProvinceOccupationChanged;
    /// <summary>After OnProvinceOccupationChanged for a province occupied only through spread.</summary>
    public event Action<War, Province, Nation> OnProvinceOccupationSpread;
    public event Action<War> OnWarProgressChanged;
    public event Action<War, WarSide> OnWarDecisionAvailable;

    protected override int TickPriority => -750;

    private WorldManager World => WorldManager.Instance;
    private RelationshipManager Rel => RelationshipManager.Instance;

    #endregion

    #region Lifecycle

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
            _nextInspectorRefresh = Time.unscaledTime + Mathf.Max(0.25f, diagnosticsInterval);
            RefreshView();
        }
#endif
    }

    /// <summary>Forgets every war and truce (map regenerated). No events, no transfers.</summary>
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
        var copy = new List<War>(list);                         // rare event: a private copy is fine
        for (int i = 0; i < copy.Count; i++)
        {
            War w = copy[i];
            if (w.Ended) continue;
            if (w.IsMain(n.Id)) EndWar(w, WarResult.WhitePeace);
            else LeaveWar(w, n);
        }
        _byNation.Remove(n.Id);
    }

    #endregion

    #region Daily / monthly

    private void HandleDay()
    {
        if (_wars.Count == 0) return;
        _tmp.Clear();
        _tmp.AddRange(_wars);

        long today = DateManager.Instance.TotalDays;
        long idleLimit = settings.idleYearsWhitePeace > 0f
            ? (long)(settings.idleYearsWhitePeace * DateManager.DaysPerMonth * DateManager.MonthsPerYear) : 0L;

        for (int i = 0; i < _tmp.Count; i++)
        {
            War w = _tmp[i];
            if (w.Ended) continue;
            w.DaysActive++;

            Nation a = World.GetNation(w.AttackerId), d = World.GetNation(w.DefenderId);
            if (a == null || a.IsEmpty) { EndWar(w, WarResult.DefenderVictory); continue; }   // main nation wiped out
            if (d == null || d.IsEmpty) { EndWar(w, WarResult.AttackerVictory); continue; }

            if (idleLimit > 0L && today - w.LastActivityDay >= idleLimit) { EndWar(w, WarResult.WhitePeace); continue; }

            RecalculateProgress(w);                              // also catches "army destroyed"
        }
    }

    /// <summary>The side with MORE occupied provinces gains monthlyLeadPercent (permanent). Equal = nobody.</summary>
    private void HandleMonth()
    {
        if (_wars.Count == 0) return;
        _tmp.Clear();
        _tmp.AddRange(_wars);

        float gain = Mathf.Max(0f, settings.monthlyLeadPercent);
        for (int i = 0; i < _tmp.Count; i++)
        {
            War w = _tmp[i];
            if (w.Ended) continue;

            CountOccupiedBoth(w, out int a, out int d);
            if (a > d) w.AttackerBonus = Mathf.Clamp(w.AttackerBonus + gain, 0f, 100f);
            else if (d > a) w.DefenderBonus = Mathf.Clamp(w.DefenderBonus + gain, 0f, 100f);
            RecalculateProgress(w);
        }
    }

    /// <summary>Keeps the idle-war timer alive (call it from sieges / fights).</summary>
    public void MarkActivity(War w)
    {
        if (w != null && !w.Ended) w.LastActivityDay = DateManager.Instance.TotalDays;
    }

    public void MarkActivity(Nation a, Nation b) => MarkActivity(GetWar(a, b));

    #endregion

    #region Queries

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

    /// <summary>Fills 'result' with every nation on the opposite side of n in any war.</summary>
    public void GetEnemies(Nation n, List<Nation> result)
    {
        result.Clear();
        if (n == null || !_byNation.TryGetValue(n.Id, out List<War> l)) return;
        for (int i = 0; i < l.Count; i++)
        {
            List<int> enemies = l[i].SideOf(n.Id) == WarSide.Attacker ? l[i].Defenders : l[i].Attackers;
            for (int j = 0; j < enemies.Count; j++)
            {
                Nation e = World.GetNation(enemies[j]);
                if (e != null && !result.Contains(e)) result.Add(e);
            }
        }
    }

    public int CountOccupied(War war, WarSide bySide)
    {
        int c = 0;
        foreach (var kv in war.Occupied) if (war.SideOf(kv.Value) == bySide) c++;
        return c;
    }

    private void CountOccupiedBoth(War war, out int attacker, out int defender)
    {
        attacker = defender = 0;
        foreach (var kv in war.Occupied)
        {
            WarSide s = war.SideOf(kv.Value);
            if (s == WarSide.Attacker) attacker++; else if (s == WarSide.Defender) defender++;
        }
    }

    #endregion

    #region Declare war

    public War DeclareWar(Nation attacker, Nation defender) => DeclareWar(attacker, defender, false);
    public War ForceDeclareWar(Nation attacker, Nation defender) => DeclareWar(attacker, defender, true);

    /// <summary>normal: relation below threshold, no truce, no alliance / pact / vassal link. force: ignores all of that.</summary>
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
            if (rel.IsVassalOf(defender, attacker)) rel.ReleaseVassal(attacker, defender);
            else if (rel.IsVassalOf(attacker, defender)) rel.ReleaseVassal(defender, attacker);
        }

        if (settings.makeEnemyOnDeclare) rel.MakeEnemy(attacker, defender);

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

    /// <summary>Allies / (defender only) defensive-pact partners / vassals of 'leader' join its side.</summary>
    private void CallAllies(War war, Nation leader, WarSide side)
    {
        if (!settings.callAllies && !settings.vassalsFollowOverlord) return;
        RelationshipManager rel = Rel;
        IReadOnlyList<Nation> all = World.Nations;
        Nation other = World.GetNation(side == WarSide.Attacker ? war.DefenderId : war.AttackerId);

        for (int i = 0; i < all.Count; i++)
        {
            Nation o = all[i];
            if (o == leader || o.IsEmpty || war.SideOf(o.Id) != WarSide.None) continue;
            if (other != null && rel.IsAllied(other, o) && rel.IsAllied(leader, o)) continue;   // allied to both: stays out

            bool join = false;
            if (settings.callAllies && rel.IsAllied(leader, o)) join = true;
            else if (settings.callAllies && settings.callDefensivePacts && side == WarSide.Defender && rel.HasDefensivePact(leader, o)) join = true;
            else if (settings.vassalsFollowOverlord && rel.IsVassalOf(o, leader)) join = true;

            if (join) AddMember(war, o, side);
        }
    }

    #endregion

    #region Join / leave / capitulate

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

    /// <summary>'nation' joins the side that 'friend' fights on.</summary>
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

    /// <summary>A NON-main member leaves the war. What it occupied is released.</summary>
    public bool LeaveWar(War war, Nation nation)
    {
        LastError = null;
        if (war == null || war.Ended) return Fail("War is not running.");
        if (nation == null || war.SideOf(nation.Id) == WarSide.None) return Fail("Nation is not in this war.");
        if (war.IsMain(nation.Id)) return Fail("A main belligerent cannot leave: use Capitulate / peace.");

        war.Attackers.Remove(nation.Id);
        war.Defenders.Remove(nation.Id);
        Unregister(war, nation.Id);

        _idBuf.Clear();
        foreach (var kv in war.Occupied) if (kv.Value == nation.Id) _idBuf.Add(kv.Key);
        for (int i = 0; i < _idBuf.Count; i++)
        {
            war.Occupied.Remove(_idBuf[i]);
            Province fp = _map != null ? _map.GetProvince(_idBuf[i]) : null;
            if (fp != null) OnProvinceOccupationChanged?.Invoke(war, fp, null);
        }

        RecalculateProgress(war);
        OnNationLeft?.Invoke(war, nation);
        return true;
    }

    /// <summary>A main belligerent loses the war (the other side wins), an ally simply leaves.</summary>
    public bool Capitulate(War war, Nation loser)
    {
        LastError = null;
        if (war == null || war.Ended) return Fail("War is not running.");
        WarSide s = loser != null ? war.SideOf(loser.Id) : WarSide.None;
        if (s == WarSide.None) return Fail("Nation is not in this war.");
        if (!war.IsMain(loser.Id)) return LeaveWar(war, loser);
        return EndWar(war, s == WarSide.Attacker ? WarResult.DefenderVictory : WarResult.AttackerVictory);
    }

    #endregion

    #region Peace / end

    public bool WhitePeace(War war) => EndWar(war, WarResult.WhitePeace);
    public bool AttackerWins(War war) => EndWar(war, WarResult.AttackerVictory);
    public bool DefenderWins(War war) => EndWar(war, WarResult.DefenderVictory);

    public bool MakePeace(Nation a, Nation b)
    {
        LastError = null;
        War w = GetWar(a, b);
        if (w == null) return Fail("They are not at war.");
        return EndWar(w, WarResult.WhitePeace);
    }

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
                if (!EndWar(war, WarResult.WhitePeace)) return false;
                return Rel.Vassalize(winMain, loseMain);
        }
        return false;
    }

    public float GetDemandBudget(War war, WarSide winner) => war != null ? war.ProgressOf(winner) : 0f;

    /// <summary>War score price of one province. Average province of its owner = provinceScoreCost.</summary>
    public float GetProvinceCost(War war, WarSide winner, Province p)
    {
        Nation o = p != null ? p.Owner : null;
        if (o == null || o.ProvinceCount == 0) return float.MaxValue;

        int c = o.ProvinceCount;
        double avgT = (double)o.Area / c;
        double avgP = o.Population / c + 1.0;
        double avgI = o.Economy / c + 0.0001;
        double value = 0.4 * p.TileCount / Math.Max(1.0, avgT)
                     + 0.4 * p.Population / avgP
                     + 0.2 * p.Income / avgI;

        float cost = settings.provinceScoreCost * (float)value;
        if (p.IsCapital) cost += settings.capitalScoreCost;
        if (war.Occupied.TryGetValue(p.Id, out int occ) && war.SideOf(occ) == winner) cost *= settings.occupiedCostFactor;
        return Mathf.Max(0.5f, cost);
    }

    /// <summary>The winner takes the listed provinces (occupied or not) if its progress pays for them.</summary>
    public bool Decide(War war, WarSide winner, IList<int> provinceIds)
    {
        LastError = null;
        if (!CanDecide(war, winner)) return Fail("This side cannot decide yet.");
        if (provinceIds == null || provinceIds.Count == 0) return Fail("No provinces demanded.");
        if (_map == null) return Fail("No map.");

        WarSide loser = War.Opposite(winner);
        Nation winMain = World.GetNation(winner == WarSide.Attacker ? war.AttackerId : war.DefenderId);
        if (winMain == null) return Fail("Nation missing.");

        float budget = GetDemandBudget(war, winner), total = 0f;
        _seen.Clear();
        for (int i = 0; i < provinceIds.Count; i++)
        {
            Province p = _map.GetProvince(provinceIds[i]);
            if (p == null || p.Owner == null || !_seen.Add(p.Id)) return Fail("Bad province in demand.");
            if (war.SideOf(p.Owner.Id) != loser) return Fail("Province does not belong to the losing side.");
            total += GetProvinceCost(war, winner, p);
        }
        if (total > budget + 0.01f) return Fail("Demand costs " + total.ToString("F1") + " but only " + budget.ToString("F1") + " war score.");

        war.Claims.Clear();
        foreach (int id in _seen)
        {
            int receiver = war.Occupied.TryGetValue(id, out int occ) && war.SideOf(occ) == winner ? occ : winMain.Id;
            war.Claims[id] = receiver;
        }
        return EndWar(war, winner == WarSide.Attacker ? WarResult.AttackerVictory : WarResult.DefenderVictory);
    }

    /// <summary>Victory transfers the occupied / claimed provinces. Always: truce + relation. OnWarEnded is raised LAST.</summary>
    public bool EndWar(War war, WarResult result)
    {
        LastError = null;
        if (war == null || war.Ended) return Fail("War is not running.");
        if (result == WarResult.Ongoing) return Fail("Choose a result.");

        war.Ended = true;
        war.Result = result;

        Dictionary<int, int> transfer = war.Claims.Count > 0 ? war.Claims : war.Occupied;
        if (result == WarResult.AttackerVictory) ApplyTransfers(war, WarSide.Attacker, transfer);
        else if (result == WarResult.DefenderVictory) ApplyTransfers(war, WarSide.Defender, transfer);

        if (war.Occupied.Count > 0)
        {
            _idBuf.Clear();
            foreach (var kv in war.Occupied) _idBuf.Add(kv.Key);
            war.Occupied.Clear();
            for (int i = 0; i < _idBuf.Count; i++)
            {
                Province op = _map != null ? _map.GetProvince(_idBuf[i]) : null;
                if (op != null) OnProvinceOccupationChanged?.Invoke(war, op, null);
            }
        }

        _wars.Remove(war);
        for (int i = 0; i < war.Attackers.Count; i++) Unregister(war, war.Attackers[i]);
        for (int i = 0; i < war.Defenders.Count; i++) Unregister(war, war.Defenders[i]);

        if (settings.truceDays > 0)
        {
            long end = DateManager.Instance.TotalDays + settings.truceDays;
            for (int i = 0; i < war.Attackers.Count; i++)
                for (int j = 0; j < war.Defenders.Count; j++)
                    _truces[Key(war.Attackers[i], war.Defenders[j])] = end;
        }

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

    private void ApplyTransfers(War war, WarSide winner, Dictionary<int, int> source)
    {
        if (source.Count == 0) return;
        WarSide loser = War.Opposite(winner);

        _pairBuf.Clear();
        foreach (var kv in source) _pairBuf.Add(kv);            // SetProvinceOwner may touch 'source' indirectly

        for (int i = 0; i < _pairBuf.Count; i++)
        {
            int pid = _pairBuf[i].Key, receiver = _pairBuf[i].Value;
            Province p = _map != null ? _map.GetProvince(pid) : null;
            if (p == null || p.Owner == null) continue;
            if (war.SideOf(receiver) != winner || war.SideOf(p.Owner.Id) != loser) continue;

            Nation target = World.GetNation(receiver);
            if (target == null) continue;

            if (_map != null && _map.IsReady) _map.SetProvinceOwner(pid, receiver);
            else World.AssignProvince(p, target);
            OnProvinceOccupationChanged?.Invoke(war, p, null);   // occupation became an annexation
        }
    }

    #endregion

    #region Occupation (called by CombatManager's siege) & surrender

    /// <summary>'occupier' (in the war) occupies a province owned by the OTHER side. May spread to neighbouring enemy provinces.</summary>
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
        if (wasOccupied && war.SideOf(prev) == mine) return Fail("Already occupied by this side.");

        war.Occupied[province.Id] = occupier.Id;
        war.LastActivityDay = DateManager.Instance.TotalDays;
        OnProvinceOccupationChanged?.Invoke(war, province, occupier);

        _owners.Clear();
        _owners.Add(province.Owner);
        if (!wasOccupied) SpreadOccupation(war, occupier, province, mine);
        RecalculateProgress(war);

        // surrender can end wars (and re-enter this class): work on a private snapshot of the victims
        _victims.Clear();
        for (int i = 0; i < _owners.Count; i++) if (!_victims.Contains(_owners[i])) _victims.Add(_owners[i]);
        Nation[] victims = _victims.ToArray();
        for (int i = 0; i < victims.Length; i++) CheckSurrender(victims[i]);
        return true;
    }

    private void SpreadOccupation(War war, Nation occupier, Province origin, WarSide mine)
    {
        if (_map == null || origin.Neighbors == null) return;

        _spreadBuf.Clear();
        int[] nb = origin.Neighbors;
        for (int i = 0; i < nb.Length; i++)
        {
            Province q = _map.GetProvince(nb[i]);
            if (q == null || q.Owner == null) continue;
            WarSide os = war.SideOf(q.Owner.Id);
            if (os == WarSide.None || os == mine) continue;                       // only enemy land
            if (war.Occupied.ContainsKey(q.Id)) continue;
            if (CombatManager.HasInstance && !CombatManager.Instance.CanTakeProvince(war, occupier, q)) continue;   // enemy fighters there
            _spreadBuf.Add(q);
        }

        int take = settings.spreadProvinces == 0 ? _spreadBuf.Count : settings.spreadProvinces;
        for (int n = 0; n < take && _spreadBuf.Count > 0 && !war.Ended; n++)
        {
            int idx = RNG.GetInt(0, _spreadBuf.Count);
            Province pick = _spreadBuf[idx];
            _spreadBuf[idx] = _spreadBuf[_spreadBuf.Count - 1];                   // swap-remove
            _spreadBuf.RemoveAt(_spreadBuf.Count - 1);

            war.Occupied[pick.Id] = occupier.Id;
            _owners.Add(pick.Owner);
            OnProvinceOccupationChanged?.Invoke(war, pick, occupier);
            OnProvinceOccupationSpread?.Invoke(war, pick, occupier);
        }
    }

    /// <summary>Removes the occupation of a province and takes back its war score. Spreads to neighbours like Occupy.</summary>
    public bool Liberate(War war, Province province)
    {
        LastError = null;
        if (war == null || war.Ended) return Fail("War is not running.");
        if (province == null || !war.Occupied.Remove(province.Id)) return Fail("Province is not occupied in this war.");

        war.LastActivityDay = DateManager.Instance.TotalDays;
        OnProvinceOccupationChanged?.Invoke(war, province, null);

        SpreadLiberation(war, province);
        RecalculateProgress(war);                                // a liberated capital takes its progress back
        return true;
    }

    /// <summary>Mirror of SpreadOccupation: neighbouring provinces of the same side that are occupied by the enemy are freed too.</summary>
    private void SpreadLiberation(War war, Province origin)
    {
        if (_map == null || origin.Neighbors == null || origin.Owner == null) return;

        Nation liberator = origin.Owner;                          // owner of the freed province = the liberating side
        WarSide mine = war.SideOf(liberator.Id);
        if (mine == WarSide.None) return;

        _spreadBuf.Clear();
        int[] nb = origin.Neighbors;
        for (int i = 0; i < nb.Length; i++)
        {
            Province q = _map.GetProvince(nb[i]);
            if (q == null || q.Owner == null) continue;
            if (war.SideOf(q.Owner.Id) != mine) continue;                         // only land of the liberating side
            if (!war.Occupied.TryGetValue(q.Id, out int occ)) continue;           // must be occupied
            if (war.SideOf(occ) == mine) continue;                                // ... by the enemy
            if (CombatManager.HasInstance && !CombatManager.Instance.CanTakeProvince(war, liberator, q)) continue;   // enemy fighters there
            _spreadBuf.Add(q);
        }

        int take = settings.spreadProvinces == 0 ? _spreadBuf.Count : settings.spreadProvinces;
        for (int n = 0; n < take && _spreadBuf.Count > 0 && !war.Ended; n++)
        {
            int idx = RNG.GetInt(0, _spreadBuf.Count);
            Province pick = _spreadBuf[idx];
            _spreadBuf[idx] = _spreadBuf[_spreadBuf.Count - 1];                   // swap-remove
            _spreadBuf.RemoveAt(_spreadBuf.Count - 1);

            war.Occupied.Remove(pick.Id);
            OnProvinceOccupationChanged?.Invoke(war, pick, null);
            OnProvinceOccupationSpread?.Invoke(war, pick, null);
        }
    }

    /// <summary>True when EVERY province of n is occupied by an enemy in some war n fights.</summary>
    private bool FullyOccupied(Nation n)
    {
        if (n == null || n.IsEmpty) return false;
        IReadOnlyList<War> wars = GetWars(n);
        if (wars.Count == 0) return false;

        int total = 0;                                           // cheap reject: not enough occupied entries at all
        for (int w = 0; w < wars.Count; w++) total += wars[w].Occupied.Count;
        if (total < n.Provinces.Count) return false;

        for (int i = 0; i < n.Provinces.Count; i++)
        {
            int pid = n.Provinces[i].Id;
            bool occupied = false;
            for (int w = 0; w < wars.Count && !occupied; w++)
            {
                if (!wars[w].Occupied.TryGetValue(pid, out int occ)) continue;
                WarSide so = wars[w].SideOf(occ), sn = wars[w].SideOf(n.Id);
                occupied = so != WarSide.None && sn != WarSide.None && so != sn;
            }
            if (!occupied) return false;
        }
        return true;
    }

    /// <summary>Everything occupied -> the nation surrenders in ALL its wars (main = loses, ally = leaves).</summary>
    private void CheckSurrender(Nation n)
    {
        if (!FullyOccupied(n)) return;
        _tmpSurrender.Clear();
        _tmpSurrender.AddRange(GetWars(n));
        for (int i = 0; i < _tmpSurrender.Count; i++)
            if (!_tmpSurrender[i].Ended) Capitulate(_tmpSurrender[i], n);
    }

    /// <summary>Adds permanent progress (percent): positive = attacker side, negative = defender side.</summary>
    public bool AddWarScore(War war, float amount)
    {
        LastError = null;
        if (war == null || war.Ended) return Fail("War is not running.");
        if (amount >= 0f) war.AttackerBonus = Mathf.Clamp(war.AttackerBonus + amount, 0f, 100f);
        else war.DefenderBonus = Mathf.Clamp(war.DefenderBonus - amount, 0f, 100f);
        RecalculateProgress(war);
        return true;
    }

    #endregion

    #region War progress (0..100 % per side, nothing happens at 100)

    //  capital of the enemy main nation occupied : +capitalOccupiedPercent
    //  enemy main nation's army destroyed        : +armyDestroyedPercent
    //  ALL provinces of the enemy main nation    : +allProvincesPercent
    //  more occupied provinces                   : +monthlyLeadPercent every month (permanent)
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

    private void CheckDecision(War war, float a, float d)
    {
        float t = settings.decisionProgress;
        if (a >= t) { if (!war.AttackerDecisionRaised) { war.AttackerDecisionRaised = true; OnWarDecisionAvailable?.Invoke(war, WarSide.Attacker); } }
        else war.AttackerDecisionRaised = false;
        if (d >= t) { if (!war.DefenderDecisionRaised) { war.DefenderDecisionRaised = true; OnWarDecisionAvailable?.Invoke(war, WarSide.Defender); } }
        else war.DefenderDecisionRaised = false;
    }

    private float Milestones(War war, WarSide side)
    {
        Nation enemy = World.GetNation(side == WarSide.Attacker ? war.DefenderId : war.AttackerId);
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
        if (list.Count == 0 || war.Occupied.Count < list.Count) return false;     // cheap reject
        for (int i = 0; i < list.Count; i++)
            if (!war.Occupied.TryGetValue(list[i].Id, out int occ) || war.SideOf(occ) != side) return false;
        return true;
    }

    #endregion

    #region Internal helpers

    private static long Key(int a, int b) =>
        a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    private void Register(War w, int nationId)
    {
        if (!_byNation.TryGetValue(nationId, out List<War> l)) _byNation[nationId] = l = new List<War>(2);
        if (!l.Contains(w)) l.Add(w);
    }

    private void Unregister(War w, int nationId)
    {
        if (!_byNation.TryGetValue(nationId, out List<War> l)) return;
        l.Remove(w);
        if (l.Count == 0) _byNation.Remove(nationId);
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

    private bool Fail(string message) { LastError = message; return false; }

    #endregion

    #region Context menu (-1 = random nation / province; "FORCE" ignores every condition)

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
    private void MenuClearTruce() => RunMenu("Clear truce", (a, b) => { _truces.Remove(Key(a.Id, b.Id)); return true; });

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
        if (w == null || w.NationCount < 2) { Debug.LogWarning("[War] Need at least 2 nations (generate the map first)."); return false; }

        a = debugNationA >= 0 ? w.GetNation(debugNationA) : w.GetRandomNation();
        if (a == null) { Debug.LogWarning("[War] Nation A not found."); return false; }

        if (debugNationB >= 0) b = w.GetNation(debugNationB);
        else for (int t = 0; t < 32 && (b == null || b == a); t++) b = w.GetRandomNation();

        if (b == null || b == a) { Debug.LogWarning("[War] Nation B not found (or equals A)."); return false; }
        return true;
    }

    private Nation PickC(Nation a, Nation b)
    {
        WorldManager w = World;
        if (debugNationC >= 0) return w.GetNation(debugNationC);
        Nation c = null;
        for (int t = 0; t < 32 && (c == null || c == a || c == b); t++) c = w.GetRandomNation();
        return c;
    }

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
        return $"war #{w.Id} progress A {w.AttackerProgress:F0}% / D {w.DefenderProgress:F0}%, days {w.DaysActive}, " +
               $"attackers {SideText(w.Attackers)}, defenders {SideText(w.Defenders)}, occupied {w.Occupied.Count}, relation {rel}";
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

    #endregion

    #region Diagnostics (Editor mirror, throttled by diagnosticsInterval)

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

    #endregion
}