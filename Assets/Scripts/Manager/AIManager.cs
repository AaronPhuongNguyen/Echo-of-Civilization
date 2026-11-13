using System;
using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
//  SETTINGS
// ─────────────────────────────────────────────────────────────────────────────
[Serializable]
public class AISettings
{
    [Header("Timing")]
    [Min(1)] public int economyIntervalDays = 6;
    [Min(1)] public int diplomacyIntervalDays = 15;
    [Tooltip("Game-seconds between two military decisions of one nation.")]
    [Min(0.2f)] public float militaryInterval = 1.5f;
    [Tooltip("Max nations that think per tick (spreads the CPU cost).")]
    [Min(1)] public int nationsPerTick = 4;

    [Header("Personality (random per nation)")]
    public Vector2 aggressionRange = new Vector2(0.1f, 0.9f);
    public Vector2 friendlinessRange = new Vector2(0.2f, 0.9f);

    [Header("Economy")]
    [Tooltip("Fraction of MaxTreasury that is never spent (peace / war).")]
    [Range(0f, 1f)] public float peaceReserve = 0.15f;
    [Range(0f, 1f)] public float warReserve = 0.05f;
    [Min(1)] public int maxIncreasesPerRound = 3;
    [Min(1)] public int sampleProvinces = 8;
    [Tooltip("AI never queues more than this many increases in one province (keeps investment spread out).")]
    [Min(1)] public int maxQueuePerProvince = 2;
    [Tooltip("MUST rule: a province whose PopGrowth Base is below this gets a PopGrowth increase first.")]
    public float mustPopGrowthBelow = 0.5f;
    public float popGrowthWeight = 5f, taxWeight = 4f, economyWeight = 3f, infrastructureWeight = 2f, defendWeight = 1f;

    [Header("Military")]
    [Range(1, 10)] public int peacePrepLevel = 1;
    [Range(1, 10)] public int alertMaxPrepLevel = 7;
    [Range(0f, 1f)] public float alertThreshold = 0.25f;
    [Tooltip("Attack power / enemy defence power needed to call a target 'weak'.")]
    public float attackRatio = 1.25f;
    [Tooltip("Own force wanted at a threatened province = enemy men * this.")]
    public float defendRatio = 1.15f;
    [Range(0f, 1f)] public float minAttackStrength = 0.6f;
    [Range(0f, 1f)] public float minDefendStrength = 0.25f;
    [Tooltip("Attacker: max share of free regiments used to defend (unless the capital is threatened).")]
    [Range(0f, 1f)] public float attackerDefendShare = 0.35f;
    [Tooltip("Province Defend stat * this = defence power added to a target (units: men * DEF).")]
    public float provinceDefendWeight = 2000f;
    [Tooltip("All-in: fraction of the army that must reach the staging province before the strike.")]
    [Range(0.3f, 1f)] public float massReadyFraction = 0.75f;
    [Tooltip("All-in: game-seconds after which the strike goes even if not everybody arrived.")]
    [Min(5f)] public float massTimeout = 40f;
    [Tooltip("AI re-balances the regiment shares (more manpower to the front, less to idle rear regiments).")]
    public bool manageShares = true;
    [Min(2f)] public float shareInterval = 8f;

    [Header("Diplomacy / war")]
    [Tooltip("Coalition power needed over the target before the AI plans a war.")]
    public float warMinPowerRatio = 1.6f;
    [Tooltip("A neighbour this much stronger is appeased (relations improved, pacts proposed).")]
    public float fearRatio = 1.2f;
    [Range(0f, 1f)] public float warChancePerCheck = 0.3f;
    [Min(1)] public int maxWars = 1;
    public int minDaysBetweenWars = 360;
    public float improveUntil = 45f;
    [Tooltip("AI main belligerent ends the war with a victory at this progress.")]
    public float winProgress = 100f;
    [Tooltip("AI vs AI wars older than this (days) with nobody above 60% end in white peace.")]
    public int stalemateDays = 1080;
    public bool allowVassalize = false;
}

public enum AIMass : byte { None = 0, Massing = 1 }

// ─────────────────────────────────────────────────────────────────────────────
//  AI MANAGER  (global access: AIManager.Instance)
//
//  Controls every nation that is NOT the player (Nation.IsAI = true).
//   ECONOMY   : spreads Increase orders over provinces, weighted by priority + treasury + balance.
//   MILITARY  : threat assessment -> preparation level, border positioning, defence / liberation / offence,
//               weakest-point blitz, all-in massing, share re-balancing (reinforcement control).
//   DIPLOMACY : improves relations, pacts, alliances, plans and declares wars, ends decided wars.
// ─────────────────────────────────────────────────────────────────────────────
[DefaultExecutionOrder(-600)]
public class AIManager : TickBehaviour
{
    // ── singleton ──
    private static AIManager _instance;
    private static bool _quitting;
    public static bool HasInstance => _instance != null;

    public static AIManager Instance
    {
        get
        {
            if (_instance == null && !_quitting)
            {
#if UNITY_2022_2_OR_NEWER
                _instance = FindFirstObjectByType<AIManager>();
#else
                _instance = FindObjectOfType<AIManager>();
#endif
                if (_instance == null) _instance = new GameObject("AIManager").AddComponent<AIManager>();
            }
            return _instance;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() { _instance = null; _quitting = false; }

    public AISettings settings = new AISettings();
    [SerializeField] private MapManager map;

    protected override int TickPriority => -600;

    public void RefreshNations() => RefreshMarks();

    // ── per nation state ──
    private sealed class AIState
    {
        public Nation Nation;
        public float Aggression, Friendliness;
        public float NextThink, NextShare;
        public long NextEconomy, NextDiplomacy, LastWarDay = -100000;
        public float Threat;
        public Nation ThreatFrom;
        public Nation WarTarget;
        public AIMass Mass;
        public Province Staging, Strike;
        public float MassSince;
        public bool SharesCustom;
    }

    private struct Threat { public Province P; public double Men; public float Pri; }
    private struct Target { public Province P; public double Def; public float Ratio, Score, Dist; public Vector3 Pos; }

    private readonly Dictionary<int, AIState> _states = new Dictionary<int, AIState>();
    private readonly List<AIState> _list = new List<AIState>();
    private int _cursor;
    private float _clock;
    private MapManager _subscribedMap;

    // index: province id -> regiments standing there (non retreating)
    private List<Regiment>[] _regsAt;
    private readonly List<int> _touched = new List<int>();

    // temp buffers (no allocations per think)
    private readonly List<Regiment> _free = new List<Regiment>();
    private readonly List<Regiment> _atk = new List<Regiment>();
    private readonly HashSet<Regiment> _used = new HashSet<Regiment>();
    private readonly HashSet<Nation> _nbSet = new HashSet<Nation>();
    private readonly HashSet<Nation> _intruders = new HashSet<Nation>();
    private readonly List<Nation> _enemies = new List<Nation>();
    private readonly Dictionary<int, double> _threatMap = new Dictionary<int, double>();
    private readonly HashSet<int> _defendSet = new HashSet<int>();
    private readonly List<Threat> _threats = new List<Threat>();
    private readonly List<Target> _targets = new List<Target>();
    private readonly HashSet<int> _seen = new HashSet<int>();
    private readonly List<Province> _front = new List<Province>();
    private readonly List<Province> _lib = new List<Province>();
    private readonly List<int> _cnt = new List<int>();
    private readonly List<War> _tmpWars = new List<War>();
    private readonly List<float> _w = new List<float>();
    private readonly List<float> _delta = new List<float>();
    private readonly List<int> _order = new List<int>();
    private readonly float[] _sw = new float[5];
    private readonly float[] _sb = new float[5];

    private WarManager WM => WarManager.Instance;
    private ArmyManager Army => ArmyManager.Instance;
    private RelationshipManager Rel => RelationshipManager.Instance;
    private WorldManager World => WorldManager.Instance;

    // ─────────────────────────────────────────────────────────────────────
    //  LIFECYCLE
    // ─────────────────────────────────────────────────────────────────────
    private void Awake()
    {
        if (_instance != null && _instance != this) { Destroy(this); return; }
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
        World.OnNationsCleared += ClearAll;
        World.OnNationRemoved += HandleRemoved;
        DateManager.Instance.OnDayPassed += HandleDay;
        if (PlayerManager.HasInstance) PlayerManager.Instance.OnPlayerChanged += HandlePlayerChanged;

        if (map != null)
        {
            _subscribedMap = map;
            map.OnMapGenerated += Rebuild;
            if (map.IsReady) Rebuild();
        }
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        if (WorldManager.HasInstance)
        {
            WorldManager.Instance.OnNationsCleared -= ClearAll;
            WorldManager.Instance.OnNationRemoved -= HandleRemoved;
        }
        if (DateManager.HasInstance) DateManager.Instance.OnDayPassed -= HandleDay;
        if (PlayerManager.HasInstance) PlayerManager.Instance.OnPlayerChanged -= HandlePlayerChanged;
        if (_subscribedMap != null) _subscribedMap.OnMapGenerated -= Rebuild;
        _subscribedMap = null;
        if (_instance == this && _quitting) _instance = null;
    }

    private void OnDestroy() { if (_instance == this) _instance = null; }

    private void ClearAll()
    {
        _states.Clear(); _list.Clear(); _cursor = 0; _regsAt = null; _touched.Clear();
    }

    private void HandleRemoved(Nation n)
    {
        if (n == null || !_states.TryGetValue(n.Id, out AIState s)) return;
        _states.Remove(n.Id);
        _list.Remove(s);
    }

    private void HandlePlayerChanged(Nation oldN, Nation newN) => RefreshMarks();

    private void Rebuild()
    {
        ClearAll();
        RefreshMarks();
    }

    /// <summary>Every nation without IsPlayer gets IsAI + a state; the player nation loses its state.</summary>
    private void RefreshMarks()
    {
        IReadOnlyList<Nation> all = World.Nations;
        for (int i = 0; i < all.Count; i++)
        {
            Nation n = all[i];
            PlayerManager pm = PlayerManager.HasInstance ? PlayerManager.Instance : null;
            bool player = n.IsPlayer || (pm != null && pm.PlayerNation == n);
            if (player)
            {
                n.IsAI = false;
                if (_states.TryGetValue(n.Id, out AIState old)) { _states.Remove(n.Id); _list.Remove(old); }
                continue;
            }
            n.IsAI = true;
            if (n.IsEmpty || _states.ContainsKey(n.Id)) continue;

            var st = new AIState
            {
                Nation = n,
                Aggression = RNG.GetFloat(settings.aggressionRange.x, Mathf.Max(settings.aggressionRange.x, settings.aggressionRange.y)),
                Friendliness = RNG.GetFloat(settings.friendlinessRange.x, Mathf.Max(settings.friendlinessRange.x, settings.friendlinessRange.y)),
                NextThink = RNG.GetFloat(0f, settings.militaryInterval),
                NextEconomy = DateManager.Instance.TotalDays + RNG.GetInt(0, settings.economyIntervalDays + 1),
                NextDiplomacy = DateManager.Instance.TotalDays + RNG.GetInt(1, settings.diplomacyIntervalDays + 1)
            };
            _states.Add(n.Id, st);
            _list.Add(st);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  DAILY: economy, diplomacy, war end
    // ─────────────────────────────────────────────────────────────────────
    private void HandleDay()
    {
        if (_list.Count == 0 || !WarManager.HasInstance || !RelationshipManager.HasInstance) return;
        long today = DateManager.Instance.TotalDays;

        for (int i = 0; i < _list.Count; i++)
        {
            AIState st = _list[i];
            if (st.Nation.IsEmpty) continue;

            CheckWarEnd(st);
            if (today >= st.NextEconomy)
            {
                st.NextEconomy = today + settings.economyIntervalDays + RNG.GetInt(0, 3);
                RunEconomy(st);
            }
            if (today >= st.NextDiplomacy)
            {
                st.NextDiplomacy = today + settings.diplomacyIntervalDays + RNG.GetInt(0, 5);
                RunDiplomacy(st, today);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  ECONOMY   Pop growth > Tax > Economy > Infrastructure > Defend(other)
    //   - province: sampled, the least invested (+ important / capital) wins, so nobody is left behind
    //   - stat: priority weights x balance (stat under-invested in THIS province) x threat boost, affordable only
    //   - MUST rule: first PopGrowth increase of every province comes first
    // ─────────────────────────────────────────────────────────────────────
    private void RunEconomy(AIState st)
    {
        Nation n = st.Nation;
        bool war = WM.IsAtWar(n);
        double reserve = n.MaxTreasury * (war ? settings.warReserve : settings.peaceReserve);
        for (int i = 0; i < settings.maxIncreasesPerRound; i++)
        {
            double budget = n.Treasury - reserve;
            if (budget <= 0.0) break;
            if (!InvestOnce(st, budget, war)) break;
        }
    }

    private bool InvestOnce(AIState st, double budget, bool war)
    {
        Nation n = st.Nation;
        IReadOnlyList<Province> provs = n.Provinces;
        int count = provs.Count;
        if (count == 0) return false;

        double avgPop = n.Population / count + 1.0;
        Province best = null;
        float bestScore = float.MinValue;
        for (int s = 0; s < settings.sampleProvinces; s++)
        {
            Province p = provs[RNG.GetInt(0, count)];
            ProvinceUpgrades u = p.Upgrades;
            if (u.Queue.Count >= settings.maxQueuePerProvince) continue;
            int invested = 0;
            for (int k = 0; k < ProvinceUpgrades.StatCount; k++) invested += u.Done[k] + u.Queued[k];
            float score = -invested
                          + Mathf.Clamp((float)(p.Population / avgPop), 0f, 2.5f) * 0.5f
                          + (p.IsCapital ? 0.6f : 0f)
                          + RNG.GetFloat(0f, 0.8f);
            if (score > bestScore) { bestScore = score; best = p; }
        }
        if (best == null) return false;

        if (!PickStat(st, best, budget, war, out StatType stat)) return false;
        return World.TryQueueIncrease(best, stat);
    }

    private bool PickStat(AIState st, Province p, double budget, bool war, out StatType stat)
    {
        stat = StatType.Tax;
        ProvinceUpgrades u = p.Upgrades;
        WorldManager w = World;

        // order matches StatType: Tax, Economy, Defend, PopGrowth, Infrastructure
        _sb[0] = settings.taxWeight; _sb[1] = settings.economyWeight; _sb[2] = settings.defendWeight;
        _sb[3] = settings.popGrowthWeight; _sb[4] = settings.infrastructureWeight;
        float total = 0f;
        int inv = 0;
        for (int k = 0; k < 5; k++) { total += _sb[k]; inv += u.Done[k] + u.Queued[k]; }
        total = Mathf.Max(0.0001f, total);

        for (int k = 0; k < 5; k++)
        {
            float wt = _sb[k];
            if (k == 2 && (war || st.Threat > 0.4f)) wt *= 1f + 3f * Mathf.Max(war ? 1f : 0f, st.Threat);   // defend when threatened
            float deficit = _sb[k] / total * (inv + 1) - (u.Done[k] + u.Queued[k]);                         // under-invested stat of this province
            wt *= Mathf.Clamp(1f + deficit, 0.15f, 3f);
            if (w.GetIncreasePrice(p, (StatType)k) > budget) wt = 0f;
            _sw[k] = wt;
        }

        // MUST: first PopGrowth increase of the province
        if (p.PopGrowth.Base < settings.mustPopGrowthBelow && u.Done[3] + u.Queued[3] == 0 && _sw[3] > 0f)
        { stat = StatType.PopGrowth; return true; }

        float sum = 0f;
        for (int k = 0; k < 5; k++) sum += _sw[k];
        if (sum <= 0f) return false;
        float roll = RNG.GetFloat(0f, sum);
        for (int k = 0; k < 5; k++)
        {
            roll -= _sw[k];
            if (roll <= 0f && _sw[k] > 0f) { stat = (StatType)k; return true; }
        }
        for (int k = 4; k >= 0; k--) if (_sw[k] > 0f) { stat = (StatType)k; return true; }
        return false;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  MILITARY TICK
    // ─────────────────────────────────────────────────────────────────────
    public override void Tick(float deltaTime)
    {
        if (_list.Count == 0 || map == null || !map.IsReady || !ArmyManager.HasInstance || !WarManager.HasInstance
            || !RelationshipManager.HasInstance || !CombatManager.HasInstance) return;

        _clock += deltaTime;
        bool indexed = false;
        int done = 0, count = _list.Count;
        for (int k = 0; k < count && done < settings.nationsPerTick; k++)
        {
            if (_cursor >= _list.Count) _cursor = 0;
            AIState st = _list[_cursor++];
            if (st.NextThink > _clock || st.Nation.IsEmpty) continue;
            if (!indexed) { BuildIndex(); indexed = true; }
            st.NextThink = _clock + settings.militaryInterval * RNG.GetFloat(0.8f, 1.2f);
            ThinkMilitary(st);
            done++;
        }
    }

    private void BuildIndex()
    {
        int pc = map.Provinces.Count;
        if (_regsAt == null || _regsAt.Length != pc) { _regsAt = new List<Regiment>[pc]; _touched.Clear(); }
        for (int i = 0; i < _touched.Count; i++) _regsAt[_touched[i]].Clear();
        _touched.Clear();

        IReadOnlyList<Military> mils = Army.Militaries;
        for (int i = 0; i < mils.Count; i++)
            for (int pass = 0; pass < 2; pass++)
            {
                List<Regiment> regs = pass == 0 ? mils[i].Regiments : mils[i].Columns;
                for (int j = 0; j < regs.Count; j++)
                {
                    Regiment r = regs[j];
                    if (r.Military == null || r.State == RegimentState.Retreating || r.Manpower <= 0.5) continue;
                    Province p = r.CurrentProvince;
                    if (p == null) continue;
                    List<Regiment> l = _regsAt[p.Id];
                    if (l == null) l = _regsAt[p.Id] = new List<Regiment>(4);
                    if (l.Count == 0) _touched.Add(p.Id);
                    l.Add(r);
                }
            }
    }

    private void ThinkMilitary(AIState st)
    {
        Nation n = st.Nation;
        Military m = Army.GetMilitary(n);
        if (m == null || m.Regiments.Count == 0) return;

        bool war = WM.IsAtWar(n);
        UpdateThreat(st, m, war);
        int lvl = war ? 10 : Mathf.RoundToInt(Mathf.Lerp(settings.peacePrepLevel, settings.alertMaxPrepLevel, st.Threat));
        m.SetPreparationLevel(lvl);

        if (war) CommandWar(st, m);
        else
        {
            st.Mass = AIMass.None;
            if (st.SharesCustom) { m.ResetShares(); st.SharesCustom = false; }
            CommandPeace(st, m);
        }

        if (settings.manageShares && (war || st.Threat > 0.4f)) ManageShares(st, m, war);
    }

    // ── helpers ──
    private bool Hostile(Nation a, Nation b) => WM.IsAtWar(a, b);

    private static bool Movable(Regiment r) =>
        !r.IsColumn && r.Military != null &&
        (r.State == RegimentState.Idle || r.State == RegimentState.Reinforcing);   // never re-task a marching army

    private static bool InOrHeading(Regiment r, Province p) =>
        (r.State != RegimentState.Moving && r.CurrentProvince == p) || (r.HasPath && r.Destination == p);

    private bool Order(Regiment r, Province p)
    {
        _used.Add(r);
        if (InOrHeading(r, p)) return true;
        return Army.MoveRegiment(r, p);
    }

    private Regiment Nearest(List<Regiment> list, Vector3 pos, Province skipIfAt, float minStrength)
    {
        Regiment best = null;
        float bestD = float.MaxValue;
        for (int i = 0; i < list.Count; i++)
        {
            Regiment r = list[i];
            if (_used.Contains(r) || r.StrengthPercent < minStrength) continue;
            if (skipIfAt != null && InOrHeading(r, skipIfAt)) continue;
            float d = (r.WorldPosition - pos).sqrMagnitude;
            if (d < bestD) { bestD = d; best = r; }
        }
        return best;
    }

    private double EnemyMen(Nation me, Province p)
    {
        List<Regiment> l = _regsAt[p.Id];
        if (l == null) return 0.0;
        double s = 0.0;
        for (int i = 0; i < l.Count; i++) if (Hostile(me, l[i].Military.Nation)) s += l[i].Manpower;
        return s;
    }

    private double EnemyDefPower(Nation me, Province p)
    {
        List<Regiment> l = _regsAt[p.Id];
        if (l == null) return 0.0;
        double s = 0.0;
        for (int i = 0; i < l.Count; i++)
            if (Hostile(me, l[i].Military.Nation)) s += l[i].Manpower * l[i].Military.DefendPerMan.Value;
        return s;
    }

    private double EnemyMenNear(Nation me, Province p)
    {
        double s = EnemyMen(me, p);
        int[] nb = p.Neighbors;
        for (int i = 0; i < nb.Length; i++) s += EnemyMen(me, map.GetProvince(nb[i])) * 0.5;
        return s;
    }

    private void CollectNeighbors(Nation n, HashSet<Nation> set)
    {
        for (int i = 0; i < n.Provinces.Count; i++)
        {
            int[] nb = n.Provinces[i].Neighbors;
            for (int j = 0; j < nb.Length; j++)
            {
                Province q = map.GetProvince(nb[j]);
                if (q != null && q.Owner != null && q.Owner != n) set.Add(q.Owner);
            }
        }
    }

    private void GetBorderProvinces(Nation n, Nation facing, List<Province> result)
    {
        for (int i = 0; i < n.Provinces.Count; i++)
        {
            Province p = n.Provinces[i];
            int[] nb = p.Neighbors;
            for (int j = 0; j < nb.Length; j++)
            {
                Province q = map.GetProvince(nb[j]);
                if (q != null && q.Owner == facing) { result.Add(p); break; }
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  THREAT ("something is not right")
    //   hostile neighbours with a big army, neighbours that mobilise, foreign troops on our land,
    //   a neighbour that is planning a war against us.
    // ─────────────────────────────────────────────────────────────────────
    private void UpdateThreat(AIState st, Military m, bool war)
    {
        Nation n = st.Nation;
        float target = 0f;
        Nation worst = null;

        if (war)
        {
            target = 1f;
            WM.GetEnemies(n, _enemies);
            if (_enemies.Count > 0) worst = _enemies[0];
        }
        else
        {
            RelationshipManager rel = Rel;
            _intruders.Clear();
            for (int i = 0; i < n.Provinces.Count; i++)
            {
                List<Regiment> l = _regsAt[n.Provinces[i].Id];
                if (l == null) continue;
                for (int j = 0; j < l.Count; j++)
                {
                    Nation o = l[j].Military.Nation;
                    if (o != n && !rel.IsAllied(n, o) && rel.GetValue(n, o) < 20f) _intruders.Add(o);
                }
            }

            _nbSet.Clear();
            CollectNeighbors(n, _nbSet);
            double myDef = m.CurrentManpower * (m.DefendPerMan.Value + m.HpPerMan.Value * 0.2f) + 1.0;
            float worstV = 0f;
            foreach (Nation o in _nbSet)
            {
                if (o.IsEmpty) continue;
                RelationFlags f = rel.GetFlags(n, o);
                if ((f & (RelationFlags.Alliance | RelationFlags.Vassal | RelationFlags.DefensivePact | RelationFlags.NonAggression)) != 0) continue;
                Military om = Army.GetMilitary(o);
                if (om == null) continue;

                float val = rel.GetValue(n, o);
                float hostility = Mathf.InverseLerp(15f, -60f, val);
                float ratio = Mathf.Clamp01((float)(om.CurrentManpower * om.AttackPerMan.Value / myDef) * 0.5f);
                float v = hostility * (0.35f + 0.65f * ratio);
                if (om.TargetPreparation >= 50f) v += 0.2f * hostility;                       // neighbour is mobilising
                if (_states.TryGetValue(o.Id, out AIState os) && os.WarTarget == n) v += 0.4f; // it is preparing a war against us
                if (_intruders.Contains(o)) v += 0.5f;                                         // troops on our land
                v = Mathf.Clamp01(v);
                if (v > worstV) { worstV = v; worst = o; }
            }
            target = worstV;
        }

        st.Threat = target > st.Threat ? target : Mathf.Max(target, st.Threat - 0.05f);   // rises fast, decays slowly
        st.ThreatFrom = worst;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  PEACE: when worried, station the army on the border facing the threat (+ capital when serious)
    // ─────────────────────────────────────────────────────────────────────
    private void CommandPeace(AIState st, Military m)
    {
        Nation n = st.Nation;
        if (st.Threat < settings.alertThreshold || st.ThreatFrom == null) return;

        _front.Clear();
        GetBorderProvinces(n, st.ThreatFrom, _front);
        if (st.Threat > 0.6f && n.Capital != null && !_front.Contains(n.Capital)) _front.Add(n.Capital);
        if (_front.Count == 0) return;

        _free.Clear(); _used.Clear();
        for (int i = 0; i < m.Regiments.Count; i++)
            if (Movable(m.Regiments[i]) && m.Regiments[i].Manpower >= 1.0) _free.Add(m.Regiments[i]);
        if (_free.Count == 0) return;

        int per = Mathf.Max(1, _free.Count / _front.Count);
        _cnt.Clear();
        for (int f = 0; f < _front.Count; f++) _cnt.Add(0);
        for (int i = 0; i < _free.Count; i++)
            for (int f = 0; f < _front.Count; f++)
                if (InOrHeading(_free[i], _front[f])) { _used.Add(_free[i]); _cnt[f]++; break; }

        for (int f = 0; f < _front.Count; f++)
        {
            Vector3 pos = Army.ProvinceWorldPos(_front[f]);
            while (_cnt[f] < per)
            {
                Regiment r = Nearest(_free, pos, null, 0f);
                if (r == null) break;
                Order(r, _front[f]);
                _cnt[f]++;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  WAR
    //   1) regroup weak marching units
    //   2) defence: answer the biggest threats on own land (capital first)
    //   3) liberation: take back own side's occupied provinces
    //   4) offence: Defender = counter-attack only with a clear advantage, Attacker = weakest point / all-in
    //   5) Defender leftovers rally where the pressure is
    // ─────────────────────────────────────────────────────────────────────
    private void CommandWar(AIState st, Military m)
    {
        Nation n = st.Nation;
        WM.GetEnemies(n, _enemies);
        if (_enemies.Count == 0) return;

        bool defender = true;
        IReadOnlyList<War> wars = WM.GetWars(n);
        for (int i = 0; i < wars.Count; i++) if (wars[i].SideOf(n.Id) == WarSide.Attacker) defender = false;

        _free.Clear(); _used.Clear(); _defendSet.Clear();
        for (int i = 0; i < m.Regiments.Count; i++)
        {
            Regiment r = m.Regiments[i];
            if (Movable(r) && r.Manpower >= 1.0) _free.Add(r);
        }
        if (_free.Count == 0) return;

        AssignDefence(st, m, defender);
        AssignLiberation(st, m);
        Offense(st, m, defender);
        if (defender) Rally(st, m);
    }

    private void AddThreat(Nation me, Province p, double men)
    {
        if (p == null || p.Owner == null) return;
        if (p.Owner != me && !Rel.IsAllied(me, p.Owner)) return;
        _threatMap.TryGetValue(p.Id, out double cur);
        _threatMap[p.Id] = cur + men;
    }

    private void AssignDefence(AIState st, Military m, bool defender)
    {
        Nation n = st.Nation;
        _threatMap.Clear();
        for (int e = 0; e < _enemies.Count; e++)
        {
            Military em = Army.GetMilitary(_enemies[e]);
            if (em == null) continue;
            for (int i = 0; i < em.Regiments.Count; i++)
            {
                Regiment r = em.Regiments[i];
                if (r.State == RegimentState.Retreating || r.Manpower <= 0.5) continue;
                AddThreat(n, r.CurrentProvince, r.Manpower);
                if (r.HasPath && r.Destination != r.CurrentProvince) AddThreat(n, r.Destination, r.Manpower);
            }
        }

        _threats.Clear();
        foreach (var kv in _threatMap)
        {
            Province p = map.GetProvince(kv.Key);
            if (p == null) continue;
            _defendSet.Add(p.Id);
            _threats.Add(new Threat { P = p, Men = kv.Value, Pri = (float)kv.Value * (p.IsCapital ? 3f : 1f) * (defender ? 1.25f : 1f) });
        }
        _threats.Sort((a, b) => b.Pri.CompareTo(a.Pri));

        int maxUse = defender ? _free.Count : Mathf.Max(1, Mathf.CeilToInt(_free.Count * settings.attackerDefendShare));
        int usedCount = 0;
        for (int t = 0; t < _threats.Count; t++)
        {
            Province p = _threats[t].P;
            bool cap = p.IsCapital;
            double have = 0.0;
            for (int i = 0; i < m.Regiments.Count; i++)
            {
                Regiment r = m.Regiments[i];
                if (r.State == RegimentState.Retreating) continue;
                if (r.CurrentProvince == p || (r.HasPath && r.Destination == p)) have += r.Manpower;
            }
            double need = _threats[t].Men * settings.defendRatio - have;
            Vector3 pos = Army.ProvinceWorldPos(p);
            while (need > 0.0 && (usedCount < maxUse || cap))
            {
                Regiment r = Nearest(_free, pos, p, settings.minDefendStrength);
                if (r == null) break;
                if (Order(r, p)) { need -= r.Manpower; usedCount++; }
            }
        }
    }

    private void AssignLiberation(AIState st, Military m)
    {
        Nation n = st.Nation;
        _lib.Clear();
        IReadOnlyList<War> wars = WM.GetWars(n);
        for (int i = 0; i < wars.Count; i++)
        {
            War w = wars[i];
            WarSide mine = w.SideOf(n.Id);
            foreach (var kv in w.Occupied)
            {
                if (w.SideOf(kv.Value) == mine) continue;
                Province p = map.GetProvince(kv.Key);
                if (p == null || p.Owner == null || w.SideOf(p.Owner.Id) != mine) continue;
                _defendSet.Add(p.Id);
                if (p.IsCapital) _lib.Insert(0, p); else _lib.Add(p);
            }
        }

        int limit = Mathf.Max(1, _free.Count / 2);
        for (int i = 0; i < _lib.Count && limit > 0; i++)
        {
            Province p = _lib[i];
            if (EnemyMen(n, p) > 0.0 || CombatManager.Instance.GetBattle(p) != null) continue;   // defence handles it
            Regiment r = Nearest(_free, Army.ProvinceWorldPos(p), p, settings.minDefendStrength);
            if (r == null) break;
            if (Order(r, p)) limit--;
        }
    }

    private bool NeedsOrder(Nation n, Regiment r) =>
        !(r.CurrentProvince != null && _defendSet.Contains(r.CurrentProvince.Id));   // defenders hold their ground

    // ── offence ──
    private void Offense(AIState st, Military m, bool counter)
    {
        Nation n = st.Nation;
        float minStr = counter ? 0.7f : settings.minAttackStrength;
        float ratioNeed = settings.attackRatio * (counter ? 1.15f : 1f);

        _atk.Clear();
        double myAtk = 0.0;
        Vector3 centroid = Vector3.zero;
        for (int i = 0; i < _free.Count; i++)
        {
            Regiment r = _free[i];
            if (_used.Contains(r) || r.StrengthPercent < minStr || !NeedsOrder(n, r)) continue;
            _atk.Add(r);
            myAtk += r.Manpower * r.Military.AttackPerMan.Value;
            centroid += r.WorldPosition;
        }
        if (_atk.Count == 0) { st.Mass = AIMass.None; return; }
        centroid /= _atk.Count;

        BuildTargets(st, myAtk, centroid);
        if (_targets.Count == 0) return;

        Target best = _targets[0];
        if (best.Ratio >= ratioNeed)
        {
            st.Mass = AIMass.None;
            Blitz(best, ratioNeed);
            return;
        }
        if (counter) return;       // defenders never gamble: they wait for a real advantage
        AllIn(st);
    }

    private void BuildTargets(AIState st, double myAtk, Vector3 centroid)
    {
        Nation n = st.Nation;
        _targets.Clear(); _seen.Clear();
        IReadOnlyList<War> wars = WM.GetWars(n);
        float tile = Mathf.Max(0.0001f, Army.TileSize);

        for (int wi = 0; wi < wars.Count; wi++)
        {
            War w = wars[wi];
            if (w.Ended) continue;
            WarSide mine = w.SideOf(n.Id);
            if (mine == WarSide.None) continue;

            for (int i = 0; i < n.Provinces.Count; i++) ConsiderNeighbours(n, n.Provinces[i], w, mine, myAtk, centroid, tile);
            foreach (var kv in w.Occupied)
            {
                if (w.SideOf(kv.Value) != mine) continue;
                Province held = map.GetProvince(kv.Key);
                if (held != null) ConsiderNeighbours(n, held, w, mine, myAtk, centroid, tile);
            }

            Nation mainEnemy = World.GetNation(mine == WarSide.Attacker ? w.DefenderId : w.AttackerId);
            if (mainEnemy != null && mainEnemy.Capital != null) TryAddTarget(n, mainEnemy.Capital, w, mine, myAtk, centroid, tile);
        }
        _targets.Sort((a, b) => b.Score.CompareTo(a.Score));
    }

    private void ConsiderNeighbours(Nation n, Province p, War w, WarSide mine, double myAtk, Vector3 centroid, float tile)
    {
        int[] nb = p.Neighbors;
        for (int i = 0; i < nb.Length; i++)
        {
            Province q = map.GetProvince(nb[i]);
            if (q != null) TryAddTarget(n, q, w, mine, myAtk, centroid, tile);
        }
    }

    private void TryAddTarget(Nation n, Province q, War w, WarSide mine, double myAtk, Vector3 centroid, float tile)
    {
        if (!_seen.Add(q.Id) || q.Owner == null) return;
        if (w.SideOf(q.Owner.Id) != War.Opposite(mine)) return;
        if (w.Occupied.TryGetValue(q.Id, out int occ) && w.SideOf(occ) == mine) return;

        double def = EnemyDefPower(n, q) + q.Defend.Value * settings.provinceDefendWeight;
        float ratio = (float)(myAtk / (def + 1.0));
        Vector3 pos = Army.ProvinceWorldPos(q);
        float dist = Vector3.Distance(centroid, pos) / tile;
        float value = 1f + (q.IsCapital ? 1.5f : 0f);
        float score = Mathf.Min(ratio, 8f) * value / (1f + dist * 0.02f);
        _targets.Add(new Target { P = q, Def = def, Ratio = ratio, Score = score, Dist = dist, Pos = pos });
    }

    /// <summary>Weak points found: hit as many as the army can, each with just enough force, nearest units first.</summary>
    private void Blitz(Target best, float ratioNeed)
    {
        for (int t = 0; t < _targets.Count && _atk.Count > 0; t++)
        {
            Target tg = _targets[t];
            if (tg.Ratio < ratioNeed) continue;
            double need = tg.Def * ratioNeed, got = 0.0;
            while (_atk.Count > 0)
            {
                Regiment r = Nearest(_atk, tg.Pos, null, 0f);
                if (r == null) { _atk.Clear(); break; }
                _atk.Remove(r);
                if (!Order(r, tg.P)) continue;
                got += r.Manpower * r.Military.AttackPerMan.Value;
                if (got >= need) break;
            }
        }
        for (int i = 0; i < _atk.Count; i++) Order(_atk[i], best.P);   // everybody else follows the best target
        _atk.Clear();
    }

    /// <summary>
    /// No weak point: gather at a staging province, then hit the most effective defence point together.
    /// </summary>
    private void AllIn(AIState st)
    {
        Nation n = st.Nation;
        bool valid = st.Strike != null && st.Strike.Owner != null && Hostile(n, st.Strike.Owner);
        if (st.Mass == AIMass.None || !valid)
        {
            Target pick = _targets[0];
            float bestV = -1f;
            for (int i = 0; i < _targets.Count; i++)
            {
                float v = (float)_targets[i].Def / (1f + _targets[i].Dist * 0.01f);
                if (v > bestV) { bestV = v; pick = _targets[i]; }
            }
            st.Strike = pick.P;
            st.Staging = NearestControlled(n, pick.Pos);
            st.Mass = AIMass.Massing;
            st.MassSince = _clock;
            if (st.Staging == null) { st.Mass = AIMass.None; Blitz(_targets[0], 0f); return; }
        }

        int arrived = 0, pending = 0;
        double power = 0.0;
        Military mil = Army.GetMilitary(n);
        if (mil != null)
            for (int i = 0; i < mil.Regiments.Count; i++)
            {
                Regiment r = mil.Regiments[i];
                if (r.State == RegimentState.Moving && r.HasPath && r.Destination == st.Staging) pending++;
            }
        for (int i = 0; i < _atk.Count; i++)
        {
            Regiment r = _atk[i];
            if (r.State != RegimentState.Moving && r.CurrentProvince == st.Staging)
            {
                arrived++;
                power += r.Manpower * r.Military.AttackPerMan.Value;
                _used.Add(r);
            }
            else Order(r, st.Staging);
        }

        double strikeDef = EnemyDefPower(n, st.Strike) + st.Strike.Defend.Value * settings.provinceDefendWeight;
        bool ready = arrived >= settings.massReadyFraction * (_atk.Count + pending) &&
                     power >= strikeDef * settings.attackRatio * 0.8;
        if (ready || _clock - st.MassSince > settings.massTimeout)
        {
            for (int i = 0; i < _atk.Count; i++)
                if (_atk[i].State != RegimentState.Moving && _atk[i].CurrentProvince == st.Staging)
                    Order(_atk[i], st.Strike);                                      // only the arrived ones strike, together
            st.Mass = AIMass.None;
        }
    }

    private Province NearestControlled(Nation n, Vector3 pos)
    {
        Province best = null;
        float bestD = float.MaxValue;
        for (int i = 0; i < n.Provinces.Count; i++)
        {
            Province p = n.Provinces[i];
            if (EnemyMen(n, p) > 0.0) continue;
            float d = (Army.ProvinceWorldPos(p) - pos).sqrMagnitude;
            if (d < bestD) { bestD = d; best = p; }
        }
        IReadOnlyList<War> wars = WM.GetWars(n);
        for (int i = 0; i < wars.Count; i++)
        {
            War w = wars[i];
            WarSide mine = w.SideOf(n.Id);
            foreach (var kv in w.Occupied)
            {
                if (w.SideOf(kv.Value) != mine) continue;
                Province p = map.GetProvince(kv.Key);
                if (p == null || EnemyMen(n, p) > 0.0) continue;
                float d = (Army.ProvinceWorldPos(p) - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = p; }
            }
        }
        return best;
    }

    /// <summary>Defender: idle units without a job go where the enemy pressure is highest.</summary>
    private void Rally(AIState st, Military m)
    {
        Nation n = st.Nation;
        Province hot = null;
        double hotV = 0.0;
        for (int i = 0; i < n.Provinces.Count; i++)
        {
            Province p = n.Provinces[i];
            double v = EnemyMenNear(n, p) * (p.IsCapital ? 2.0 : 1.0);
            if (v > hotV) { hotV = v; hot = p; }
        }
        if (hot == null) return;

        for (int i = 0; i < _free.Count; i++)
        {
            Regiment r = _free[i];
            if (_used.Contains(r) || r.State == RegimentState.Moving || r.StrengthPercent < settings.minDefendStrength) continue;
            if (r.CurrentProvince != null && _defendSet.Contains(r.CurrentProvince.Id)) continue;
            Order(r, hot);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  REINFORCEMENT CONTROL: re-balance regiment shares
    //   Fighting units get the biggest share, front units a normal one, idle rear units a small one.
    //   Men that a rear regiment no longer "owns" go back to the reserve and reach the front as columns.
    // ─────────────────────────────────────────────────────────────────────
    private void ManageShares(AIState st, Military m, bool war)
    {
        if (_clock < st.NextShare) return;
        st.NextShare = _clock + settings.shareInterval;

        List<Regiment> regs = m.Regiments;
        int c = regs.Count;
        if (c < 3) return;

        _w.Clear(); _delta.Clear(); _order.Clear();
        float sum = 0f, mn = float.MaxValue, mx = 0f;
        for (int i = 0; i < c; i++)
        {
            float wt = ShareWeight(st.Nation, regs[i], war);
            _w.Add(wt); sum += wt;
            mn = Mathf.Min(mn, wt); mx = Mathf.Max(mx, wt);
        }
        if (mx - mn < 0.3f) return;

        for (int i = 0; i < c; i++)
        {
            _delta.Add(_w[i] / sum * 0.995f - regs[i].Share);
            _order.Add(i);
        }
        // insertion sort: biggest decrease first (no temporary over-allocation)
        for (int i = 1; i < c; i++)
        {
            int key = _order[i];
            int j = i - 1;
            while (j >= 0 && _delta[_order[j]] > _delta[key]) { _order[j + 1] = _order[j]; j--; }
            _order[j + 1] = key;
        }
        for (int i = 0; i < c; i++)
        {
            int idx = _order[i];
            m.SetRegimentShare(regs[idx], _w[idx] / sum * 99.5f);
        }
        st.SharesCustom = true;
    }

    private float ShareWeight(Nation n, Regiment r, bool war)
    {
        if (r.State == RegimentState.Fighting) return 2.2f;
        if (r.State == RegimentState.Moving) return 1.3f;
        Province p = r.CurrentProvince;
        if (p != null && EnemyMenNear(n, p) > 0.0) return 1.2f;
        return war ? 0.6f : 0.8f;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  DIPLOMACY
    //   - appease stronger neighbours (improve -> non-aggression -> defensive pact -> alliance)
    //   - friendly nations improve relations with the weak and ally against common enemies
    //   - aggressive nations pick the weakest coalition, damage relations, (break pacts), declare war
    //   - AI never signs agreements with the player: it can only target or ignore the player
    // ─────────────────────────────────────────────────────────────────────
    private double Power(Nation x)
    {
        Military mx = Army.GetMilitary(x);
        return mx == null ? 0.0 : mx.CurrentManpower * (mx.AttackPerMan.Value + mx.DefendPerMan.Value);
    }

    private double CoalitionPower(Nation x, bool asDefender)
    {
        double s = Power(x);
        RelationshipManager rel = Rel;
        IReadOnlyList<Nation> all = World.Nations;
        for (int i = 0; i < all.Count; i++)
        {
            Nation o = all[i];
            if (o == x || o.IsEmpty) continue;
            if (rel.IsAllied(x, o) || rel.IsVassalOf(o, x) || (asDefender && rel.HasDefensivePact(x, o))) s += Power(o);
        }
        return s;
    }

    private void RunDiplomacy(AIState st, long today)
    {
        Nation n = st.Nation;
        RelationshipManager rel = Rel;
        WarManager wm = WM;
        bool atWar = wm.IsAtWar(n);
        if (!atWar) NationFormationManager.Instance.AIThink(n, st.Aggression, today);
        double myPower = CoalitionPower(n, false);

        _nbSet.Clear();
        CollectNeighbors(n, _nbSet);

        Nation candidate = null;
        double candidatePower = double.MaxValue;

        foreach (Nation o in _nbSet)
        {
            if (o.IsEmpty) continue;
            float val = rel.GetValue(n, o);

            Relation r = rel.GetRelation(n, o);
            if (r != null)
            {
                if (r.Trend > 0 && val >= settings.improveUntil) rel.StopRelationshipChange(n, o);
                else if (r.Trend < 0 && st.WarTarget != o && val <= rel.settings.enemyThreshold) rel.StopRelationshipChange(n, o);
            }
            if (wm.IsAtWar(n, o)) continue;

            double op = CoalitionPower(o, true);
            RelationFlags f = rel.GetFlags(n, o);

            // friendly side
            if (o.IsAI && st.WarTarget != o)
            {
                bool appease = op > myPower * settings.fearRatio;
                bool likes = st.Friendliness > 0.5f && val > -20f;
                if (appease || likes)
                {
                    if (val < settings.improveUntil && (r == null || r.Trend <= 0)) rel.ImproveRelationship(n, o);
                    bool hasAny = (f & (RelationFlags.Alliance | RelationFlags.Vassal)) != 0;
                    if (!hasAny)
                    {
                        if (val >= rel.settings.friendThreshold) rel.MakeAlly(n, o);
                        else if (val >= 25f && (f & RelationFlags.DefensivePact) == 0) rel.MakeDefensivePact(n, o);
                        else if (val >= 0f && (f & (RelationFlags.NonAggression | RelationFlags.DefensivePact)) == 0) rel.MakeNonAggressionPact(n, o);
                    }
                }
                if (settings.allowVassalize && st.Aggression > 0.5f && myPower > op * 3.0 &&
                    val >= 0f && (f & RelationFlags.Vassal) == 0 && rel.GetOverlord(o) == null)
                    rel.Vassalize(n, o);
            }

            // aggressive side: weakest unprotected coalition
            bool protectedPair = (f & (RelationFlags.Alliance | RelationFlags.Vassal)) != 0 ||
                                 (st.Aggression < 0.7f && (f & (RelationFlags.DefensivePact | RelationFlags.NonAggression)) != 0);
            if (!protectedPair && !wm.HasTruce(n, o) && op * settings.warMinPowerRatio <= myPower && op < candidatePower)
            { candidate = o; candidatePower = op; }
        }

        // plan a war
        if (!atWar || wm.GetWars(n).Count < settings.maxWars)
        {
            bool ready = today - st.LastWarDay >= settings.minDaysBetweenWars &&
                         wm.GetWars(n).Count < settings.maxWars &&
                         Army.GetMilitary(n) != null && Army.GetMilitary(n).ManpowerPercent >= 50.0;
            if (st.WarTarget == null && ready && candidate != null &&
                RNG.GetFloat(0f, 1f) < st.Aggression * settings.warChancePerCheck)
                st.WarTarget = candidate;
        }

        // pursue the plan
        Nation t = st.WarTarget;
        if (t == null) return;
        if (t.IsEmpty || wm.IsAtWar(n, t) || wm.HasTruce(n, t) ||
            CoalitionPower(t, true) * settings.warMinPowerRatio * 0.8 > myPower)
        {
            rel.StopRelationshipChange(n, t);
            st.WarTarget = null;
            return;
        }

        RelationFlags tf = rel.GetFlags(n, t);
        if (st.Aggression >= 0.7f)
        {
            if ((tf & RelationFlags.NonAggression) != 0) rel.BreakNonAggressionPact(n, t);
            if ((tf & RelationFlags.DefensivePact) != 0) rel.BreakDefensivePact(n, t);
        }

        if (rel.GetValue(n, t) < wm.settings.declareRelationThreshold)
        {
            rel.StopRelationshipChange(n, t);
            if (wm.DeclareWar(n, t) != null) st.LastWarDay = today;
            st.WarTarget = null;
        }
        else rel.DamageRelationship(n, t);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  WAR END  (AI main belligerents only)
    // ─────────────────────────────────────────────────────────────────────
    private void CheckWarEnd(AIState st)
    {
        Nation n = st.Nation;
        IReadOnlyList<War> src = WM.GetWars(n);
        if (src.Count == 0) return;
        _tmpWars.Clear();
        for (int i = 0; i < src.Count; i++) _tmpWars.Add(src[i]);

        for (int i = 0; i < _tmpWars.Count; i++)
        {
            War w = _tmpWars[i];
            if (w.Ended || !w.IsMain(n.Id)) continue;
            WarSide side = w.SideOf(n.Id);
            if (w.ProgressOf(side) >= settings.winProgress)
            {
                if (side == WarSide.Attacker) WM.AttackerWins(w); else WM.DefenderWins(w);
                continue;
            }
            if (side == WarSide.Attacker && w.DaysActive >= settings.stalemateDays &&
                w.AttackerProgress < 60f && w.DefenderProgress < 60f)
            {
                Nation other = World.GetNation(w.DefenderId);
                if (other != null && other.IsAI) WM.WhitePeace(w);
            }
        }
    }

    [ContextMenu("AI: Log States")]
    private void MenuLog()
    {
        for (int i = 0; i < _list.Count; i++)
        {
            AIState s = _list[i];
            Debug.Log($"[AI] {s.Nation.Name} aggr {s.Aggression:F2} friend {s.Friendliness:F2} threat {s.Threat:F2} " +
                      $"from {(s.ThreatFrom != null ? s.ThreatFrom.Name : "-")} target {(s.WarTarget != null ? s.WarTarget.Name : "-")} mass {s.Mass}");
        }
    }
}