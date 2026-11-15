using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using float3 = Unity.Mathematics.float3;
using float4 = Unity.Mathematics.float4;
using math = Unity.Mathematics.math;

#region Settings & data

[Serializable]
public class AISettings
{
    [Header("Timing / performance")]
    [Min(1)] public int economyIntervalDays = 7;
    [Min(1)] public int diplomacyIntervalDays = 90;
    [Min(0.2f)] public float militaryInterval = 1.5f;
    [Min(1)] public int nationsPerTick = 6;
    [Tooltip("Real milliseconds per tick the AI may spend thinking (at least one nation always thinks).")]
    [Min(0.2f)] public float thinkBudgetMs = 3f;
    [Tooltip("Real seconds between two snapshots of all regiments (feeds the Burst queries).")]
    [Min(0.1f)] public float snapshotInterval = 0.3f;
    [Min(1)] public int counterAttackAfterDays = 14;

    [Header("Peace decisions")]
    [Range(0.1f, 1f)] public float demandSpend = 1f;
    [Min(1)] public int maxDemandProvinces = 50;
    [Tooltip("A peace demand province should touch at least this share of its neighbours with demanded / own land, otherwise it is pruned (no ugly spikes).")]
    [Range(0f, 0.6f)] public float minDemandTouch = 0.4f;
    [Tooltip("Losing AI main belligerent surrenders when the enemy is above this progress and it is far behind (chance per day).")]
    [Range(0f, 1f)] public float surrenderChance = 0.04f;

    [Header("Personality")]
    public Vector2 aggressionRange = new Vector2(0.1f, 0.9f);
    public Vector2 friendlinessRange = new Vector2(0.2f, 0.9f);

    [Header("Economy")]
    [Range(0f, 1f)] public float peaceReserve = 0.12f;
    [Tooltip("Treasury also pays for men (1 treasury = 10 men): keep enough free for war columns.")]
    [Range(0f, 1f)] public float warReserve = 0.15f;
    [Min(1)] public int maxIncreasesPerRound = 8;
    [Tooltip("Provinces inspected per investment (a rolling window, so every province is visited).")]
    [Min(1)] public int sampleProvinces = 8;
    [Min(1)] public int maxQueuePerProvince = 16;
    public float mustPopGrowthBelow = 0.5f;
    public float popGrowthWeight = 5f, taxWeight = 4f, economyWeight = 3f, infrastructureWeight = 2f, defendWeight = 1f;

    [Header("Army size")]
    [Tooltip("A new regiment is only added when every regiment is at least this full.")]
    [Range(0.5f, 1f)] public float newRegimentFullness = 0.95f;

    [Header("Military")]
    [Range(1, 10)] public int peacePrepLevel = 1;
    [Range(1, 10)] public int alertMaxPrepLevel = 7;
    [Range(0f, 1f)] public float alertThreshold = 0.25f;
    public float attackRatio = 1.3f;
    public float defendRatio = 1.2f;
    [Range(0f, 1f)] public float minAttackStrength = 0.6f;
    [Range(0f, 1f)] public float minDefendStrength = 0.25f;
    [Range(0f, 1f)] public float attackerDefendShare = 0.35f;
    public float provinceDefendWeight = 2000f;
    [Tooltip("Radius (tiles) of the Burst power query around a target: defenders that can intervene.")]
    [Min(2f)] public float queryRadiusTiles = 35f;
    public bool manageShares = true;
    [Min(2f)] public float shareInterval = 4f;

    [Header("War brain")]
    [Tooltip("Engage a threat only with this share of the force it needs, otherwise mass behind the front.")]
    [Range(0.2f, 1f)] public float commitRatio = 0.75f;
    [Tooltip("An attack force wider than this (tiles) first gathers (Spearhead staging) instead of trickling in.")]
    [Min(2f)] public float gatherTiles = 6f;
    [Range(0.5f, 1f)] public float warRefillBelow = 0.92f;
    [Range(0.1f, 0.6f)] public float warColumnPercent = 0.3f;
    [Min(1)] public int warColumnsPerRegiment = 4;
    [Min(1)] public int warColumnsPerThink = 8;
    [Range(0f, 1f)] public float elasticBelow = 0.3f;

    [Header("Reinforcement")]
    [Tooltip("Defender: share of the spare men used for liberation columns (the rest reinforces the front).")]
    [Range(0f, 1f)] public float liberationShare = 0.4f;
    [Min(1)] public int maxLiberators = 8;
    [Min(1f)] public float reinforceCheck = 2f;
    [Tooltip("Attacker: columns sent to every striking regiment before a strike (Reserve Surge).")]
    [Min(0)] public int surgeColumns = 3;

    [Header("Tactics")]
    public bool useTactics = true;
    public bool showTacticVisuals = true;
    [Tooltip("Min game-seconds between two tactic decisions of one nation.")]
    [Min(1f)] public float tacticInterval = 4f;
    [Tooltip("Same tactic can not be repeated by a nation within this many game-seconds.")]
    [Min(1f)] public float tacticCooldown = 20f;
    [Tooltip("Visual budget: tactic drawings per real second (all nations together).")]
    [Min(0.5f)] public float visualsPerSecond = 3f;
    [Min(2)] public int siegeMinRegiments = 2;
    [Min(2)] public int siegeMaxSlots = 12;
    [Min(10f)] public float opTimeout = 60f;
    [Range(0.3f, 1f)] public float massReadyFraction = 0.6f;

    [Header("Diplomacy / war")]
    public float warMinPowerRatio = 1.6f;
    public float fearRatio = 1.2f;
    [Range(0f, 1f)] public float warChancePerCheck = 0.3f;
    [Min(1)] public int maxWars = 1;
    public int minDaysBetweenWars = 720;
    public float improveUntil = 45f;
    public int stalemateDays = 1080;
    public bool allowVassalize = true;

    [Header("Province merging")]
    public bool mergeProvinces = true;
    [Tooltip("Every AI picks a random goal in this range and merges its provinces down to it (never below).")]
    public Vector2Int mergeGoalRange = new Vector2Int(45, 60);
    [Min(1)] public int mergeIntervalDays = 30;
    [Range(0.05f, 1f)] public float mergeSmallFactor = 0.6f;
    [Range(2, 8)] public int mergeGroupSize = 6;
    [Range(0f, 1f)] public float mergeNewNameChance = 0.3f;
    public bool mergeOnlyInPeace = true;
    [Min(1)] public int mergePerTick = 1;

    public const int TacticCount = 12;
}
public enum AITactic : byte { None, Blitz, Spearhead, Pincer, Feint, Encircle, Surge, HoldLine, Elastic, CounterPunch, Liberate, Cutoff }



[Serializable]
public class AIDiagnostics { public int nations, units, queries, operations, mergeQueue; public float thinkMs; }

#endregion

#region Burst: radius power query

/// <summary>For every query (nation, position, radius): hostile men / DEF power / ATK power and friendly men inside the radius.</summary>
[BurstCompile]
internal struct PowerQueryJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<float3> upos;
    [ReadOnly] public NativeArray<float> umen, uatk, udef;
    [ReadOnly] public NativeArray<int> unation;
    [ReadOnly] public NativeArray<byte> rel;                 // 1 = hostile, 2 = friendly
    [ReadOnly] public NativeArray<float3> qpos;
    [ReadOnly] public NativeArray<int> qnation;
    [ReadOnly] public NativeArray<float> qr2;
    [WriteOnly] public NativeArray<float4> result;           // x enemy men, y enemy DEF power, z enemy ATK power, w friendly men
    public int units, nationCap;

    public void Execute(int i)
    {
        float4 r = new float4(0f, 0f, 0f, 0f);
        int row = qnation[i] * nationCap;
        float3 p = qpos[i];
        float rr = qr2[i];
        for (int j = 0; j < units; j++)
        {
            int nj = unation[j];
            if (nj < 0 || nj >= nationCap) continue;
            byte k = rel[row + nj];
            if (k == 0 || math.lengthsq(upos[j] - p) > rr) continue;
            float men = umen[j];
            if (k == 1) { r.x += men; r.y += men * udef[j]; r.z += men * uatk[j]; }
            else r.w += men;
        }
        result[i] = r;
    }
}

internal sealed class AIQueryBatch : IDisposable
{
    private NativeArray<float3> _upos, _qpos;
    private NativeArray<float> _umen, _uatk, _udef, _qr2;
    private NativeArray<int> _unation, _qnation;
    private NativeArray<byte> _rel;
    private NativeArray<float4> _res;
    private int _ucap, _qcap, _ncap;

    public void ReserveUnits(int n)
    {
        if (n <= _ucap) return;
        FreeUnits();
        _ucap = Mathf.Max(256, Mathf.NextPowerOfTwo(n));
        _upos = JobUtil.Alloc<float3>(_ucap); _umen = JobUtil.Alloc<float>(_ucap);
        _uatk = JobUtil.Alloc<float>(_ucap); _udef = JobUtil.Alloc<float>(_ucap);
        _unation = JobUtil.Alloc<int>(_ucap);
    }

    public void ReserveQueries(int n)
    {
        if (n <= _qcap) return;
        JobUtil.Free(ref _qpos); JobUtil.Free(ref _qr2); JobUtil.Free(ref _qnation); JobUtil.Free(ref _res);
        _qcap = Mathf.Max(64, Mathf.NextPowerOfTwo(n));
        _qpos = JobUtil.Alloc<float3>(_qcap); _qr2 = JobUtil.Alloc<float>(_qcap);
        _qnation = JobUtil.Alloc<int>(_qcap); _res = JobUtil.Alloc<float4>(_qcap);
    }

    public void ReserveNations(int cap)
    {
        cap = Mathf.Max(1, cap);
        if (cap == _ncap && _rel.IsCreated) return;
        JobUtil.Free(ref _rel);
        _ncap = cap;
        _rel = JobUtil.Alloc<byte>(cap * cap);
    }

    public void ClearRel() { for (int i = 0; i < _rel.Length; i++) _rel[i] = 0; }

    public void SetRel(int a, int b, byte v)
    {
        if (a < 0 || b < 0 || a >= _ncap || b >= _ncap) return;
        int i = a * _ncap + b, j = b * _ncap + a;
        if (v == 2 && (_rel[i] == 1 || _rel[j] == 1)) return;       // hostile wins
        _rel[i] = v; _rel[j] = v;
    }

    public void SetUnit(int i, Vector3 pos, float men, float atk, float def, int nation)
    { _upos[i] = pos; _umen[i] = men; _uatk[i] = atk; _udef[i] = def; _unation[i] = nation; }

    public void SetQuery(int i, Vector3 pos, int nation, float radius)
    { _qpos[i] = pos; _qnation[i] = nation; _qr2[i] = radius * radius; }

    public void Run(int units, int queries)
    {
        if (queries <= 0) return;
        new PowerQueryJob
        {
            upos = _upos,
            umen = _umen,
            uatk = _uatk,
            udef = _udef,
            unation = _unation,
            rel = _rel,
            qpos = _qpos,
            qnation = _qnation,
            qr2 = _qr2,
            result = _res,
            units = units,
            nationCap = _ncap
        }.Schedule(queries, 8).Complete();
    }

    public Vector4 Result(int i) { float4 r = _res[i]; return new Vector4(r.x, r.y, r.z, r.w); }

    private void FreeUnits()
    {
        JobUtil.Free(ref _upos); JobUtil.Free(ref _umen); JobUtil.Free(ref _uatk);
        JobUtil.Free(ref _udef); JobUtil.Free(ref _unation);
        _ucap = 0;
    }

    public void Dispose()
    {
        FreeUnits();
        JobUtil.Free(ref _qpos); JobUtil.Free(ref _qr2); JobUtil.Free(ref _qnation); JobUtil.Free(ref _res);
        JobUtil.Free(ref _rel);
        _qcap = _ncap = 0;
    }
}

#endregion

// ─────────────────────────────────────────────────────────────────────────────
//  AI MANAGER  (AIManager.Instance)
//  Economy  : rolling window over all provinces, lowest invested / potential first.
//  Army     : grows only when every regiment is full; men cost treasury (ArmyManager.menPerTreasury).
//  Military : Burst radius queries, 10 tactics (staged operations), prediction of enemy plans, defender 50 / 50
//             reinforcement, attacker Reserve Surge.
//  Diplomacy: appeasement, alliances, war planning by border share, beautiful peace demands.
// ─────────────────────────────────────────────────────────────────────────────
[DefaultExecutionOrder(-600)]
public class AIManager : TickBehaviour
{
    #region Singleton

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

    #endregion

    #region Inspector, state & buffers

    public AISettings settings = new AISettings();
    [SerializeField] private MapManager map;

    [Header("Diagnostics (Editor only, throttled)")]
    [SerializeField] private bool showDiagnostics = true;
    [Min(0.25f)] public float diagnosticsInterval = 1f;
    [SerializeField] private AIDiagnostics diagnostics = new AIDiagnostics();
    private float _nextDiagnostics, _thinkMs;

    protected override int TickPriority => -600;

    public void RefreshNations() => RefreshMarks();

    private static readonly string[] TacticNames =
{
    "", "Blitz", "Spearhead Breakthrough", "Pincer Movement", "Feint & Strike", "Encirclement",
    "Reserve Surge", "Hold the Line", "Elastic Defence", "Counter-Punch", "Liberation Drive", "Supply Cut"
};

    private sealed class Op
    {
        public AITactic Kind;
        public Province Strike, Alt;
        public float Start;
        public int Phase;
        public readonly List<Regiment> Units = new List<Regiment>(8);
        public readonly List<int> Grp = new List<int>(8);          // Feint: 0 = decoy
        public readonly List<Province> Stage = new List<Province>(8);
    }

    private sealed class AIState
    {
        public Nation Nation;
        public float Aggression, Friendliness;
        public float NextThink, NextShare, NextTactic, NextReinforce, ElasticUntil;
        public long NextEconomy, NextDiplomacy, NextMerge, LastWarDay = -100000;
        public float Threat;
        public Nation ThreatFrom, WarTarget;
        public Province Staging;
        public bool SharesCustom, MergeQueued;
        public int InvestCursor, MergeGoal;
        public AITactic LastTactic;
        public readonly float[] Ready = new float[AISettings.TacticCount];
        public Op Op;
    }

    private struct Threat { public Province P; public double Men; public float Pri; }
    private struct Target { public Province P; public double Def; public float Ratio, Score, Dist; public Vector3 Pos; }

    private readonly Dictionary<int, AIState> _states = new Dictionary<int, AIState>();
    private readonly List<AIState> _list = new List<AIState>();
    private readonly Queue<AIState> _mergeQ = new Queue<AIState>();
    private readonly AIQueryBatch _q = new AIQueryBatch();
    private readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();
    private int _cursor, _unitCount, _lastQueries;
    private float _clock, _nextSnap, _nextHotClear, _visTokens = 3f;
    private MapManager _subscribedMap;

    private List<Regiment>[] _regsAt;
    private readonly List<int> _touched = new List<int>();
    private readonly HashSet<int> _hot = new HashSet<int>();      // provinces the AI is fighting for (reinforce priority)

    private readonly List<Regiment> _free = new List<Regiment>();
    private readonly List<Regiment> _atk = new List<Regiment>();
    private readonly HashSet<Regiment> _used = new HashSet<Regiment>();
    private readonly HashSet<Nation> _intruders = new HashSet<Nation>();
    private readonly Dictionary<Nation, int> _nbCount = new Dictionary<Nation, int>();
    private readonly List<Nation> _enemies = new List<Nation>();
    private readonly Dictionary<int, double> _threatMap = new Dictionary<int, double>();
    private readonly HashSet<int> _defendSet = new HashSet<int>();
    private readonly List<Threat> _threats = new List<Threat>();
    private readonly List<Target> _targets = new List<Target>();
    private readonly HashSet<int> _seen = new HashSet<int>();
    private readonly List<Province> _front = new List<Province>();
    private readonly List<Province> _libClear = new List<Province>();
    private readonly List<int> _cnt = new List<int>();
    private readonly List<War> _tmpWars = new List<War>();
    private readonly List<float> _w = new List<float>();
    private readonly List<float> _delta = new List<float>();
    private readonly List<int> _order = new List<int>();
    private readonly float[] _sw5 = new float[5];
    private readonly float[] _sb5 = new float[5];
    private readonly float[] _tw = new float[AISettings.TacticCount];
    private readonly List<Province> _cand = new List<Province>();
    private readonly List<int> _demand = new List<int>();
    private readonly List<Province> _mergeBuf = new List<Province>();
    private readonly Dictionary<int, int> _libScore = new Dictionary<int, int>();
    private readonly List<Regiment> _refill = new List<Regiment>();
    private int[] _touch;
    private bool[] _isCand, _inMine;
    private float[] _costArr;

    private WarManager WM => WarManager.Instance;
    private ArmyManager Army => ArmyManager.Instance;
    private RelationshipManager Rel => RelationshipManager.Instance;
    private WorldManager World => WorldManager.Instance;

    #endregion

    #region Lifecycle

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
            map.OnProvincesMerged += HandleMerged;
            if (map.IsReady) Rebuild();
        }
        if (ArmyManager.HasInstance) ArmyManager.Instance.OnRegimentRemoved += (r, d) => _nextSnap = 0f;
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
        if (_subscribedMap != null)
        {
            _subscribedMap.OnMapGenerated -= Rebuild;
            _subscribedMap.OnProvincesMerged -= HandleMerged;
        }
        _subscribedMap = null;
        if (_instance == this && _quitting) _instance = null;
    }

    private void OnDestroy()
    {
        _q.Dispose();
        if (_instance == this) _instance = null;
    }

    private void ClearAll()
    {
        _states.Clear(); _list.Clear(); _mergeQ.Clear(); _cursor = 0;
        _regsAt = null; _touched.Clear(); _hot.Clear(); _touch = null; _isCand = null; _inMine = null; _costArr = null;
    }

    private void HandleRemoved(Nation n)
    {
        if (n == null || !_states.TryGetValue(n.Id, out AIState s)) return;
        _states.Remove(n.Id);
        _list.Remove(s);
    }

    private void HandlePlayerChanged(Nation o, Nation n) => RefreshMarks();
    private void Rebuild() { ClearAll(); RefreshMarks(); }

    /// <summary>A merged province may be a staging / strike target of an operation: those operations are cancelled.</summary>
    private void HandleMerged(Province origin, IReadOnlyList<Province> absorbed)
    {
        _regsAt = null;      // province ids changed: force a rebuild
        _nextSnap = 0f;
        for (int i = 0; i < _list.Count; i++)
        {
            AIState s = _list[i];
            if ((s.Staging != null && s.Staging.IsMerged) || (s.Op != null && (s.Op.Strike.IsMerged || s.Op.Stage.Exists(p => p != null && p.IsMerged))))
            { s.Staging = null; s.Op = null; }
        }
    }

    private void RefreshMarks()
    {
        IReadOnlyList<Nation> all = World.Nations;
        PlayerManager pm = PlayerManager.HasInstance ? PlayerManager.Instance : null;
        long today = DateManager.Instance.TotalDays;
        for (int i = 0; i < all.Count; i++)
        {
            Nation n = all[i];
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
                NextEconomy = today + RNG.GetInt(0, settings.economyIntervalDays + 1),
                NextDiplomacy = today + RNG.GetInt(1, settings.diplomacyIntervalDays + 1),
                NextMerge = today + RNG.GetInt(1, settings.mergeIntervalDays + 1),
                MergeGoal = RNG.GetInt(settings.mergeGoalRange.x, Mathf.Max(settings.mergeGoalRange.x, settings.mergeGoalRange.y) + 1),
                InvestCursor = RNG.GetInt(0, 1000)
            };
            _states.Add(n.Id, st);
            _list.Add(st);
        }
    }

    #endregion

    #region Daily: economy, diplomacy, merge scheduling, war end

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
            if (settings.mergeProvinces && today >= st.NextMerge)
            {
                int excess = st.Nation.ProvinceCount - st.MergeGoal;
                st.NextMerge = today + (excess > 8 ? 2 : settings.mergeIntervalDays + RNG.GetInt(0, 5));
                if (excess > 0 && !st.MergeQueued) { st.MergeQueued = true; _mergeQ.Enqueue(st); }
            }
        }
    }

    #endregion

    #region Economy

    private void RunEconomy(AIState st)
    {
        Nation n = st.Nation;
        ManageRegimentCount(st);
        bool war = WM.IsAtWar(n);
        double reserve = n.MaxTreasury * (war ? settings.warReserve : settings.peaceReserve);
        for (int i = 0; i < settings.maxIncreasesPerRound; i++)
        {
            double budget = n.Treasury - reserve;
            if (budget <= 0.0 || !InvestOnce(st, budget, war)) break;
        }
    }

    /// <summary>Rolling window over the provinces: lowest (invested / potential) wins, so nobody is left behind.</summary>
    private bool InvestOnce(AIState st, double budget, bool war)
    {
        Nation n = st.Nation;
        IReadOnlyList<Province> provs = n.Provinces;
        int count = provs.Count;
        if (count == 0) return false;

        double avgPop = n.Population / count + 1.0, avgTiles = (double)n.Area / count + 1.0, avgInc = n.Economy / count + 0.0001;
        Province best = null;
        float bestScore = float.MaxValue;
        int sample = Mathf.Min(settings.sampleProvinces, count);
        for (int s = 0; s < sample; s++)
        {
            if (st.InvestCursor >= count) st.InvestCursor = 0;
            Province p = provs[st.InvestCursor++];
            ProvinceUpgrades u = p.Upgrades;
            if (u.Queue.Count >= settings.maxQueuePerProvince) continue;

            int invested = 0;
            for (int k = 0; k < ProvinceUpgrades.StatCount; k++) invested += u.Done[k] + u.Queued[k];
            float potential = 0.5f * (float)(p.Population / avgPop) + 0.3f * (float)(p.TileCount / avgTiles) + 0.2f * (float)(p.Income / avgInc);
            potential = Mathf.Clamp(potential, 0.3f, 3f) + (p.IsCapital ? 0.5f : 0f);
            float score = (invested + 0.5f) / potential;
            if (score < bestScore) { bestScore = score; best = p; }
        }
        if (best == null) return false;
        return PickStat(st, best, budget, war, out StatType stat) && World.TryQueueIncrease(best, stat);
    }

    private bool PickStat(AIState st, Province p, double budget, bool war, out StatType stat)
    {
        stat = StatType.Tax;
        ProvinceUpgrades u = p.Upgrades;
        WorldManager w = World;
        _sb5[0] = settings.taxWeight; _sb5[1] = settings.economyWeight; _sb5[2] = settings.defendWeight;
        _sb5[3] = settings.popGrowthWeight; _sb5[4] = settings.infrastructureWeight;
        float total = 0f;
        int inv = 0;
        for (int k = 0; k < 5; k++) { total += _sb5[k]; inv += u.Done[k] + u.Queued[k]; }
        total = Mathf.Max(0.0001f, total);

        for (int k = 0; k < 5; k++)
        {
            float wt = _sb5[k];
            if (k == 2 && (war || st.Threat > 0.4f)) wt *= 1f + 3f * Mathf.Max(war ? 1f : 0f, st.Threat);
            float deficit = _sb5[k] / total * (inv + 1) - (u.Done[k] + u.Queued[k]);
            wt *= Mathf.Clamp(1f + deficit, 0.15f, 3f);
            if (w.GetIncreasePrice(p, (StatType)k) > budget) wt = 0f;
            _sw5[k] = wt;
        }
        if (p.PopGrowth.Base < settings.mustPopGrowthBelow && u.Done[3] + u.Queued[3] == 0 && _sw5[3] > 0f)
        { stat = StatType.PopGrowth; return true; }

        float sum = 0f;
        for (int k = 0; k < 5; k++) sum += _sw5[k];
        if (sum <= 0f) return false;
        float roll = RNG.GetFloat(0f, sum);
        for (int k = 0; k < 5; k++)
        {
            roll -= _sw5[k];
            if (roll <= 0f && _sw5[k] > 0f) { stat = (StatType)k; return true; }
        }
        for (int k = 4; k >= 0; k--) if (_sw5[k] > 0f) { stat = (StatType)k; return true; }
        return false;
    }

    /// <summary>No regiment cap, but the main army must be FULL before a new regiment is added, and the treasury pays for the men.</summary>
    private void ManageRegimentCount(AIState st)
    {
        Military m = Army.GetMilitary(st.Nation);
        if (m == null) return;
        int want = Mathf.Max(Army.DesiredRegiments(m), Mathf.RoundToInt((float)(m.MaxManpower / Math.Max(1f, Army.manpowerPerRegiment))));
        int have = m.Regiments.Count;

        if (have < want)
        {
            for (int i = 0; i < have; i++)
            {
                Regiment r = m.Regiments[i];
                if (r.State == RegimentState.Retreating || r.StrengthPercent < settings.newRegimentFullness) return;   // refill first
            }
            double free = m.CurrentManpower - m.TotalRegimentManpower();
            double need = Army.manpowerPerRegiment * 0.25;
            if (free < need || m.AffordableMen < need) return;
            if (st.SharesCustom) { m.ResetShares(); st.SharesCustom = false; }
            m.AddRegiment(null, true);
        }
        else if (have > want + 1 && !WM.IsAtWar(st.Nation))
        {
            for (int i = m.Regiments.Count - 1; i >= 0; i--)
                if (m.Regiments[i].State == RegimentState.Idle) { m.RemoveRegiment(m.Regiments[i]); break; }
        }
    }

    #endregion

    #region Province merging (queued, one per tick)

    private bool RunMerge(AIState st)
    {
        Nation n = st.Nation;
        if (map == null || !map.IsReady || n.IsEmpty) return false;
        if (settings.mergeOnlyInPeace && WM.IsAtWar(n)) return false;

        int spare = n.ProvinceCount - st.MergeGoal;
        if (spare < 1) return false;

        float limit = (float)((double)n.Area / n.ProvinceCount) * settings.mergeSmallFactor;
        Province seed = null;
        for (int i = 0; i < n.Provinces.Count; i++)
        {
            Province p = n.Provinces[i];
            bool small = p.TileCount < limit;
            if (seed != null && p.TileCount >= seed.TileCount) continue;
            if (!small && spare < 4) continue;                       // big excess: any province may go
            if (!HasOwnNeighbour(p, n) || (CombatManager.HasInstance && CombatManager.Instance.IsContested(p))) continue;
            seed = p;
        }
        if (seed == null) return false;

        int size = Mathf.Min(settings.mergeGroupSize, spare + 1);
        if (!map.FindMergeGroup(seed, size, _mergeBuf)) return false;

        Province leader = _mergeBuf[0];
        for (int i = 1; i < _mergeBuf.Count; i++)
        {
            Province p = _mergeBuf[i];
            if (leader.IsCapital) break;
            if (p.IsCapital || p.Population > leader.Population) leader = p;
        }
        string name = RNG.GetFloat(0f, 1f) < settings.mergeNewNameChance ? map.GenerateProvinceName() : null;
        return map.MergeProvinces(leader, _mergeBuf, name) != null;
    }

    private bool HasOwnNeighbour(Province p, Nation n)
    {
        int[] nb = p.Neighbors;
        for (int i = 0; i < nb.Length; i++)
        {
            Province q = map.GetProvince(nb[i]);
            if (q != null && q.Owner == n) return true;
        }
        return false;
    }

    #endregion

    #region Tick: time-sliced thinking

    public override void Tick(float deltaTime)
    {
        if (_list.Count == 0 || map == null || !map.IsReady || !ArmyManager.HasInstance || !WarManager.HasInstance
            || !RelationshipManager.HasInstance || !CombatManager.HasInstance) return;

        _clock += deltaTime;
        _visTokens = Mathf.Min(4f, _visTokens + Time.unscaledDeltaTime * settings.visualsPerSecond);
        if (Time.unscaledTime >= _nextHotClear) { _hot.Clear(); _nextHotClear = Time.unscaledTime + settings.militaryInterval * 2f; }

        _sw.Restart();
        long budget = (long)(settings.thinkBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
        int done = 0, count = _list.Count;
        for (int k = 0; k < count && done < settings.nationsPerTick; k++)
        {
            if (_cursor >= _list.Count) _cursor = 0;
            AIState st = _list[_cursor++];
            if (st.NextThink > _clock || st.Nation.IsEmpty) continue;
            if (done > 0 && _sw.ElapsedTicks >= budget) break;       // continue next tick
            EnsureSnapshot();
            st.NextThink = _clock + settings.militaryInterval * RNG.GetFloat(0.8f, 1.2f);
            ThinkMilitary(st);
            done++;
        }

        int merges = 0;
        while (_mergeQ.Count > 0 && merges < settings.mergePerTick)
        {
            AIState st = _mergeQ.Dequeue();
            st.MergeQueued = false;
            if (_states.ContainsKey(st.Nation.Id) && RunMerge(st)) merges++;
        }

        _thinkMs = Mathf.Lerp(_thinkMs, (float)(_sw.ElapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency), 0.1f);
#if UNITY_EDITOR
        if (showDiagnostics && Time.unscaledTime >= _nextDiagnostics)
        {
            _nextDiagnostics = Time.unscaledTime + Mathf.Max(0.25f, diagnosticsInterval);
            int ops = 0;
            for (int i = 0; i < _list.Count; i++) if (_list[i].Op != null) ops++;
            diagnostics.nations = _list.Count; diagnostics.units = _unitCount; diagnostics.queries = _lastQueries;
            diagnostics.operations = ops; diagnostics.mergeQueue = _mergeQ.Count; diagnostics.thinkMs = _thinkMs;
        }
#endif
    }

    #endregion

    #region Snapshot (province index + Burst unit arrays)

    private void EnsureSnapshot()
    {
        if (_regsAt != null && Time.unscaledTime < _nextSnap) return;
        _nextSnap = Time.unscaledTime + settings.snapshotInterval;

        int pc = map.Provinces.Count;
        if (_regsAt == null || _regsAt.Length != pc) { _regsAt = new List<Regiment>[pc]; _touched.Clear(); }
        for (int i = 0; i < _touched.Count; i++) _regsAt[_touched[i]].Clear();
        _touched.Clear();

        IReadOnlyList<Military> mils = Army.Militaries;
        int upper = 0;
        for (int i = 0; i < mils.Count; i++) upper += mils[i].Regiments.Count + mils[i].Columns.Count;
        _q.ReserveUnits(upper);

        int nationCap = 1;
        IReadOnlyList<Nation> nations = World.Nations;
        for (int i = 0; i < nations.Count; i++) if (nations[i].Id + 1 > nationCap) nationCap = nations[i].Id + 1;
        _q.ReserveNations(nationCap);
        _q.ClearRel();
        for (int i = 0; i < nations.Count; i++) _q.SetRel(nations[i].Id, nations[i].Id, 2);
        IReadOnlyList<War> wars = WM.Wars;
        for (int w = 0; w < wars.Count; w++)
        {
            War war = wars[w];
            if (war.Ended) continue;
            for (int a = 0; a < war.Attackers.Count; a++)
            {
                for (int d = 0; d < war.Defenders.Count; d++) _q.SetRel(war.Attackers[a], war.Defenders[d], 1);
                for (int b = a + 1; b < war.Attackers.Count; b++) _q.SetRel(war.Attackers[a], war.Attackers[b], 2);
            }
            for (int a = 0; a < war.Defenders.Count; a++)
                for (int b = a + 1; b < war.Defenders.Count; b++) _q.SetRel(war.Defenders[a], war.Defenders[b], 2);
        }

        int k = 0;
        for (int i = 0; i < mils.Count; i++)
        {
            Military m = mils[i];
            int nid = m.Nation.Id;
            float atk = m.AttackPerMan.Value, def = m.DefendPerMan.Value;
            for (int pass = 0; pass < 2; pass++)
            {
                List<Regiment> regs = pass == 0 ? m.Regiments : m.Columns;
                for (int j = 0; j < regs.Count; j++)
                {
                    Regiment r = regs[j];
                    if (r.Military == null || r.State == RegimentState.Retreating || r.Manpower <= 0.5) continue;
                    _q.SetUnit(k++, r.WorldPosition, (float)r.Manpower, atk, def, nid);
                    Province p = r.CurrentProvince;
                    if (p == null) continue;
                    List<Regiment> l = _regsAt[p.Id];
                    if (l == null) l = _regsAt[p.Id] = new List<Regiment>(4);
                    if (l.Count == 0) _touched.Add(p.Id);
                    l.Add(r);
                }
            }
        }
        _unitCount = k;
    }

    #endregion

    #region Military think

    private void ThinkMilitary(AIState st)
    {
        Nation n = st.Nation;
        Military m = Army.GetMilitary(n);
        if (m == null || m.Regiments.Count == 0) return;

        bool war = WM.IsAtWar(n);
        UpdateThreat(st, m, war);
        m.SetPreparationLevel(war ? 10 : Mathf.RoundToInt(Mathf.Lerp(settings.peacePrepLevel, settings.alertMaxPrepLevel, st.Threat)));

        if (war) CommandWar(st, m);
        else
        {
            st.Op = null;
            if (st.SharesCustom) { m.ResetShares(); st.SharesCustom = false; }
            CommandPeace(st, m);
        }
        if (settings.manageShares && (war || st.Threat > 0.4f)) ManageShares(st, m, war);
    }

    private bool Hostile(Nation a, Nation b) => WM.IsAtWar(a, b);

    private static bool Movable(Regiment r) =>
        !r.IsColumn && r.Military != null && (r.State == RegimentState.Idle || r.State == RegimentState.Reinforcing);

    private static bool InOrHeading(Regiment r, Province p) =>
        (r.State != RegimentState.Moving && r.CurrentProvince == p) || (r.HasPath && r.Destination == p);

    private bool Order(Regiment r, Province p)
    {
        _used.Add(r);
        if (p != null) _hot.Add(p.Id);
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
        List<Regiment> l = p != null && _regsAt != null && p.Id < _regsAt.Length ? _regsAt[p.Id] : null;
        if (l == null) return 0.0;
        double s = 0.0;
        for (int i = 0; i < l.Count; i++)
        {
            Regiment r = l[i];
            if (r.Military == null) continue;                     // released since the snapshot
            if (Hostile(me, r.Military.Nation)) s += r.Manpower;
        }
        return s;
    }

    private double EnemyDefPower(Nation me, Province p)
    {
        List<Regiment> l = p != null && _regsAt != null && p.Id < _regsAt.Length ? _regsAt[p.Id] : null;
        if (l == null) return 0.0;
        double s = 0.0;
        for (int i = 0; i < l.Count; i++)
        {
            Regiment r = l[i];
            if (r.Military == null) continue;
            if (Hostile(me, r.Military.Nation)) s += r.Manpower * r.Military.DefendPerMan.Value;
        }
        return s;
    }

    private double EnemyMenNear(Nation me, Province p)
    {
        double s = EnemyMen(me, p);
        int[] nb = p.Neighbors;
        for (int i = 0; i < nb.Length; i++) s += EnemyMen(me, map.GetProvince(nb[i])) * 0.5;
        return s;
    }

    /// <summary>Neighbour nations and how many border pairs they share with n.</summary>
    private void CollectNeighbors(Nation n)
    {
        _nbCount.Clear();
        for (int i = 0; i < n.Provinces.Count; i++)
        {
            int[] nb = n.Provinces[i].Neighbors;
            for (int j = 0; j < nb.Length; j++)
            {
                Province q = map.GetProvince(nb[j]);
                if (q == null || q.Owner == null || q.Owner == n) continue;
                _nbCount.TryGetValue(q.Owner, out int c);
                _nbCount[q.Owner] = c + 1;
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

    #endregion

    #region Threat

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
                    if (l[j].Military == null) continue;
                    Nation o = l[j].Military.Nation;
                    if (o != n && !rel.IsAllied(n, o) && rel.GetValue(n, o) < 20f) _intruders.Add(o);
                }
            }

            CollectNeighbors(n);
            double myDef = m.CurrentManpower * (m.DefendPerMan.Value + m.HpPerMan.Value * 0.2f) + 1.0;
            float worstV = 0f;
            foreach (var kv in _nbCount)
            {
                Nation o = kv.Key;
                if (o.IsEmpty) continue;
                RelationFlags f = rel.GetFlags(n, o);
                if ((f & (RelationFlags.Alliance | RelationFlags.Vassal | RelationFlags.DefensivePact | RelationFlags.NonAggression)) != 0) continue;
                Military om = Army.GetMilitary(o);
                if (om == null) continue;

                float hostility = Mathf.InverseLerp(15f, -60f, rel.GetValue(n, o));
                float ratio = Mathf.Clamp01((float)(om.CurrentManpower * om.AttackPerMan.Value / myDef) * 0.5f);
                float v = hostility * (0.35f + 0.65f * ratio);
                if (om.TargetPreparation >= 50f) v += 0.2f * hostility;
                if (_states.TryGetValue(o.Id, out AIState os) && os.WarTarget == n) v += 0.4f;
                if (_intruders.Contains(o)) v += 0.5f;
                v = Mathf.Clamp01(v);
                if (v > worstV) { worstV = v; worst = o; }
            }
            target = worstV;
        }
        st.Threat = target > st.Threat ? target : Mathf.Max(target, st.Threat - 0.05f);
        st.ThreatFrom = worst;
    }

    #endregion

    #region Tactic visuals (budgeted, fog aware)

    private bool Draw(AIState st)
    {
        if (!settings.showTacticVisuals || !TacticVisuals.Enabled || _visTokens < 1f) return false;
        PlayerManager pm = PlayerManager.HasInstance ? PlayerManager.Instance : null;
        if (pm != null && !pm.IsArmyVisible(st.Nation)) return false;
        _visTokens -= 1f;
        return true;
    }

    private Vector3 PosOf(Province p) => Army.ProvinceWorldPos(p);
    private Vector3 CentroidOf(List<Regiment> l)
    {
        Vector3 c = Vector3.zero;
        for (int i = 0; i < l.Count; i++) c += l[i].WorldPosition;
        return l.Count > 0 ? c / l.Count : c;
    }

    private bool IsReady(AIState st, AITactic t) => _clock >= st.Ready[(int)t];

    private void Used(AIState st, AITactic t)
    {
        st.Ready[(int)t] = _clock + settings.tacticCooldown;
        st.NextTactic = _clock + settings.tacticInterval;
        st.LastTactic = t;
    }

    #endregion

    #region Peace

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

        int sent = 0;
        for (int f = 0; f < _front.Count; f++)
        {
            Vector3 pos = PosOf(_front[f]);
            while (_cnt[f] < per)
            {
                Regiment r = Nearest(_free, pos, null, 0f);
                if (r == null) break;
                Order(r, _front[f]);
                _cnt[f]++; sent++;
            }
        }
        if (sent > 0 && settings.useTactics && _clock >= st.NextTactic && IsReady(st, AITactic.HoldLine) && Draw(st))
        {
            DrawHold(st, _front);
            Used(st, AITactic.HoldLine);
        }
    }

    private void DrawHold(AIState st, List<Province> provs)
    {
        Color col = st.Nation.Color;
        float r = Army.TileSize * 5f;
        int n = Mathf.Min(2, provs.Count);
        for (int i = 0; i < n; i++) TacticVisuals.Circle(PosOf(provs[i]), r, col, 7f);
        if (provs.Count >= 2) TacticVisuals.Bracket(PosOf(provs[0]), PosOf(provs[provs.Count - 1]), col, 7f);
        TacticVisuals.Announce(PosOf(provs[0]), st.Nation.Name + ": " + TacticNames[(int)AITactic.HoldLine], col, 4f);
    }

    #endregion

    #region War

    private void CommandWar(AIState st, Military m)
    {
        Nation n = st.Nation;
        WM.GetEnemies(n, _enemies);
        if (_enemies.Count == 0) return;

        bool defender = true;
        IReadOnlyList<War> wars = WM.GetWars(n);
        for (int i = 0; i < wars.Count; i++) if (wars[i].SideOf(n.Id) == WarSide.Attacker) defender = false;

        _free.Clear(); _used.Clear(); _defendSet.Clear(); _libClear.Clear();
        if (st.Op != null)
        {
            UpdateOp(st);
            if (st.Op != null) for (int i = 0; i < st.Op.Units.Count; i++) _used.Add(st.Op.Units[i]);
        }

        BuildThreats(st);          // 1) where will the enemy hit
        CollectLiberation(st);     // 2) which of our provinces are occupied and clear

        CombatManager cm = CombatManager.Instance;
        for (int i = 0; i < m.Regiments.Count; i++)
        {
            Regiment r = m.Regiments[i];
            if (!Movable(r) || r.Manpower < 1.0) continue;
            _free.Add(r);
            if (Committed(r, cm)) _used.Add(r);          // fighting / sieging: never pulled away
        }

        bool passive = defender && !IdleAsDefender(n);
        int pending = 0;
        if (_free.Count > 0)
        {
            AssignDefence(st, m, passive);                       // defend territory first
            pending = SendLiberators(st, m, defender);           // then liberate
            if (pending == 0 || !defender) Offense(st, m, passive);   // counter attack only after that
            if (passive || (defender && pending > 0)) Rally(st, m);
        }
        LiberationColumns(st, m);
        WarReinforce(st, m);
        SurrenderCheck(st);
    }

    private bool Committed(Regiment r, CombatManager cm)
    {
        if (r.InContact) return true;
        Province p = r.CurrentProvince;
        if (p == null || r.State == RegimentState.Moving) return false;
        return cm.GetSiegeProgress(p) > 0f && !_threatMap.ContainsKey(p.Id);
    }

    private bool Covered(Military m, Province p)
    {
        for (int i = 0; i < m.Regiments.Count; i++)
        {
            Regiment r = m.Regiments[i];
            if (r.State != RegimentState.Retreating && InOrHeading(r, p)) return true;
        }
        for (int i = 0; i < m.Columns.Count; i++)
        {
            Regiment c = m.Columns[i];
            if (c.Role == ColumnRole.Occupy && c.HasPath && c.Destination == p) return true;
        }
        return false;
    }

    private static float Spread(List<Regiment> l, Vector3 c)
    {
        float mx = 0f;
        for (int i = 0; i < l.Count; i++) mx = Mathf.Max(mx, (l[i].WorldPosition - c).sqrMagnitude);
        return Mathf.Sqrt(mx);
    }

    private void WarReinforce(AIState st, Military m)
    {
        CombatManager cm = CombatManager.Instance;
        int budget = settings.warColumnsPerThink;

        _refill.Clear();
        for (int i = 0; i < m.Regiments.Count; i++)
        {
            Regiment r = m.Regiments[i];
            if (r.State != RegimentState.Retreating && r.StrengthPercent < settings.warRefillBelow) _refill.Add(r);
        }
        _refill.Sort((a, b) => RefillPriority(b).CompareTo(RefillPriority(a)));
        for (int i = 0; i < _refill.Count && budget > 0; i++)
            if (cm.ForceReinforce(_refill[i], settings.warColumnsPerRegiment, settings.warColumnPercent, true)) budget--;

        RouteColumns(m);
    }

    private static float RefillPriority(Regiment r) =>
        (r.InRange || r.InContact ? 2f : r.HasPath ? 1f : 0.5f) + (1f - r.StrengthPercent);

    /// <summary>Every merge column chases the CURRENT position of its regiment and merges the moment it is close.</summary>
    private void RouteColumns(Military m)
    {
        CombatManager cm = CombatManager.Instance;
        for (int i = m.Columns.Count - 1; i >= 0; i--)
        {
            Regiment c = m.Columns[i];
            if (c.Role != ColumnRole.Merge || c.Military == null) continue;
            if (cm.TryMergeColumn(c)) continue;

            Regiment t = c.MergeTarget;
            if (t == null || t.Military != m || t.Allocation - t.Manpower < 1.0)
            {
                t = BestMergeTarget(m, c);
                c.MergeTarget = t;
            }
            if (t == null) continue;
            Province dest = t.HasPath ? t.Destination : (t.LocationProvince ?? t.Province);
            if (dest != null && !InOrHeading(c, dest)) Army.MoveRegiment(c, dest);
        }
    }

    private Regiment BestMergeTarget(Military m, Regiment c)
    {
        Regiment best = null;
        float bs = float.MaxValue;
        for (int i = 0; i < m.Regiments.Count; i++)
        {
            Regiment r = m.Regiments[i];
            if (r.State == RegimentState.Retreating || r.Allocation - r.Manpower < 300.0) continue;
            float s = (r.WorldPosition - c.WorldPosition).magnitude * (r.InRange || r.InContact ? 0.5f : 1f);
            if (s < bs) { bs = s; best = r; }
        }
        return best;
    }

    private bool IdleAsDefender(Nation n)
    {
        long today = DateManager.Instance.TotalDays;
        IReadOnlyList<War> wars = WM.GetWars(n);
        for (int i = 0; i < wars.Count; i++)
            if (wars[i].SideOf(n.Id) == WarSide.Defender && today - wars[i].LastActivityDay >= settings.counterAttackAfterDays)
                return true;
        return false;
    }

    // ── threat map with PREDICTION: marching destinations + the enemy AI's active operation ──
    private void AddThreat(Nation me, Province p, double men)
    {
        if (p == null || p.Owner == null) return;
        if (p.Owner != me && !Rel.IsAllied(me, p.Owner)) return;
        _threatMap.TryGetValue(p.Id, out double cur);
        _threatMap[p.Id] = cur + men;
    }

    private void BuildThreats(AIState st)
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
            // the enemy AI plans an operation: its strike target is where the blow will land
            if (_states.TryGetValue(_enemies[e].Id, out AIState es) && es.Op != null)
            {
                double men = 0.0;
                for (int i = 0; i < es.Op.Units.Count; i++) men += es.Op.Units[i].Manpower;
                AddThreat(n, es.Op.Strike, men * 1.1);
            }
        }

        _threats.Clear();
        bool defender = !WM.IsAtWar(n) ? false : true;
        foreach (var kv in _threatMap)
        {
            Province p = map.GetProvince(kv.Key);
            if (p == null) continue;
            _defendSet.Add(p.Id);
            _threats.Add(new Threat { P = p, Men = kv.Value, Pri = (float)kv.Value * (p.IsCapital ? 3f : 1f) * (defender ? 1.25f : 1f) });
        }
        _threats.Sort((a, b) => b.Pri.CompareTo(a.Pri));
    }

    private void AssignDefence(AIState st, Military m, bool passive)
    {
        Nation n = st.Nation;
        if (_threats.Count == 0) return;

        double pool = 0.0;
        for (int i = 0; i < _free.Count; i++)
            if (!_used.Contains(_free[i]) && _free[i].StrengthPercent >= settings.minDefendStrength) pool += _free[i].Manpower;
        if (pool <= 0.0) return;

        Threat top = _threats[0];
        float ratio = (float)(pool / (top.Men * settings.defendRatio + 1.0));
        bool tactics = settings.useTactics && _clock >= st.NextTactic;

        if (tactics && ratio < settings.elasticBelow && !top.P.IsCapital && IsReady(st, AITactic.Elastic) && ElasticFallback(st, top)) return;
        if (tactics && st.Op == null && ratio >= 1.25f && _free.Count >= 2 && IsReady(st, AITactic.CounterPunch) && EnemyMen(n, top.P) > 0.0)
        {
            _atk.Clear();
            for (int i = 0; i < _free.Count; i++)
                if (!_used.Contains(_free[i]) && _free[i].StrengthPercent >= settings.minDefendStrength) _atk.Add(_free[i]);
            if (StartOp(st, AITactic.CounterPunch, top.P, null, _atk)) return;
        }

        int maxUse = passive ? _free.Count : Mathf.Max(1, Mathf.CeilToInt(_free.Count * settings.attackerDefendShare));
        int usedCount = 0, drawn = 0;
        _front.Clear();
        for (int t = 0; t < _threats.Count; t++)
        {
            Province p = _threats[t].P;
            bool cap = p.IsCapital;
            if (_clock < st.ElasticUntil && !cap) continue;

            double have = 0.0;
            for (int i = 0; i < m.Regiments.Count; i++)
            {
                Regiment r = m.Regiments[i];
                if (r.State == RegimentState.Retreating) continue;
                if (r.CurrentProvince == p || (r.HasPath && r.Destination == p)) have += r.Manpower;
            }
            double need = _threats[t].Men * settings.defendRatio - have;
            if (need <= 0.0) continue;

            // not enough force to win here: do not trickle in, gather behind the front
            if (!cap && pool < need * settings.commitRatio)
            {
                if (t == 0) MassBehind(st, p, maxUse, ref usedCount);
                continue;
            }

            Vector3 pos = PosOf(p);
            bool any = false;
            while (need > 0.0 && (usedCount < maxUse || cap))
            {
                Regiment r = Nearest(_free, pos, p, settings.minDefendStrength);
                if (r == null) break;
                if (Order(r, p)) { need -= r.Manpower; pool -= r.Manpower; usedCount++; any = true; }
            }
            if (any && drawn++ < 2) _front.Add(p);
        }
        if (_front.Count > 0 && tactics && IsReady(st, AITactic.HoldLine) && Draw(st))
        {
            DrawHold(st, _front);
            Used(st, AITactic.HoldLine);
        }
    }

    private void MassBehind(AIState st, Province threat, int maxUse, ref int usedCount)
    {
        Province rally = CombatManager.Instance.FindSafeProvince(st.Nation, PosOf(threat), threat);
        if (rally == null) return;
        for (int i = 0; i < _free.Count && usedCount < maxUse; i++)
        {
            Regiment r = _free[i];
            if (_used.Contains(r) || r.StrengthPercent < settings.minDefendStrength) continue;
            if (Order(r, rally)) usedCount++;
        }
    }

    /// <summary>Overwhelmed: pull everybody back to a safe province and keep them out of the fight for a while.</summary>
    private bool ElasticFallback(AIState st, Threat top)
    {
        Nation n = st.Nation;
        Vector3 from = PosOf(top.P);
        Province back = CombatManager.Instance.FindSafeProvince(n, from, top.P);
        if (back == null) return false;
        int moved = 0;
        for (int i = 0; i < _free.Count; i++)
            if (!_used.Contains(_free[i]) && Order(_free[i], back)) moved++;
        if (moved == 0) return false;

        st.ElasticUntil = _clock + 20f;
        Used(st, AITactic.Elastic);
        if (Draw(st))
        {
            Vector3 to = PosOf(back);
            TacticVisuals.CurvedArrow(from, to, n.Color, 8f, -0.3f);
            TacticVisuals.Announce(to, n.Name + ": " + TacticNames[(int)AITactic.Elastic], n.Color, 4f);
        }
        return true;
    }

    private void CollectLiberation(AIState st)
    {
        Nation n = st.Nation;
        CombatManager cm = CombatManager.Instance;
        _libClear.Clear(); _libScore.Clear();
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
                if (_libScore.ContainsKey(p.Id)) continue;
                if (EnemyMen(n, p) > 0.0 || cm.IsContested(p)) continue;      // defence handles it

                int score = p.IsCapital ? 100 : 0;
                int[] nb = p.Neighbors;
                for (int k = 0; k < nb.Length; k++)
                {
                    if (!w.Occupied.TryGetValue(nb[k], out int o) || w.SideOf(o) == mine) continue;
                    Province q = map.GetProvince(nb[k]);
                    if (q != null && q.Owner != null && w.SideOf(q.Owner.Id) == mine) score += 10;   // spread value
                }
                _libScore[p.Id] = score;
                _libClear.Add(p);
            }
        }
        _libClear.Sort((a, b) => _libScore[b.Id].CompareTo(_libScore[a.Id]));
    }

    /// <summary>Returns how many clear provinces still have nobody going there.</summary>
    private int SendLiberators(AIState st, Military m, bool defender)
    {
        if (_libClear.Count == 0) return 0;
        int limit = defender ? int.MaxValue : Mathf.Max(1, _free.Count / 2);
        int sent = 0, pending = 0;
        Province first = null;
        for (int i = 0; i < _libClear.Count; i++)
        {
            Province p = _libClear[i];
            if (Covered(m, p)) continue;
            Regiment r = limit > 0 ? Nearest(_free, PosOf(p), p, settings.minDefendStrength) : null;
            if (r != null && Order(r, p)) { limit--; sent++; if (first == null) first = p; }
            else pending++;
        }
        if (sent > 0 && settings.useTactics && _clock >= st.NextTactic && IsReady(st, AITactic.Liberate) && Draw(st))
        {
            Vector3 to = PosOf(first);
            TacticVisuals.Arrow(CentroidOf(_free), to, new Color(0.3f, 1f, 0.4f), 8f);
            TacticVisuals.Announce(to, st.Nation.Name + ": " + TacticNames[(int)AITactic.Liberate], new Color(0.3f, 1f, 0.4f), 4f);
            Used(st, AITactic.Liberate);
        }
        return pending;
    }

    private void LiberationColumns(AIState st, Military m) => DefenderReinforce(st, m);

    /// <summary>
    /// Defender reinforcement: half of the spare men (paid by the treasury) become Occupy columns that liberate
    /// provinces the main army has cleared; the other half is left for the normal front reinforcement
    /// (CombatManager asks AllowReinforce, which only lets front regiments refill during a war).
    /// </summary>
    private void DefenderReinforce(AIState st, Military m)
    {
        if (_clock < st.NextReinforce || _libClear.Count == 0) return;
        st.NextReinforce = _clock + settings.reinforceCheck;

        Nation n = st.Nation;
        double free = m.CurrentManpower - m.TotalRegimentManpower();
        double budget = Math.Min(m.AffordableMen, free) * settings.liberationShare;
        int active = 0;
        for (int i = 0; i < m.Columns.Count; i++) if (m.Columns[i].Role == ColumnRole.Occupy) active++;

        CombatManager cm = CombatManager.Instance;
        int spawned = 0;
        Province lastTarget = null;
        for (int i = 0; i < _libClear.Count && active + spawned < settings.maxLiberators; i++)
        {
            Province p = _libClear[i];
            if (Covered(m, p)) continue;
            double men = Math.Min(Army.ColumnCapMen, Math.Min(budget, 3000.0));
            if (men < 500.0) break;
            Province from = cm.FindSafeProvince(n, PosOf(p), null);
            if (from == null) continue;
            Regiment c = m.AddColumn(from, men);
            if (c == null) continue;
            c.Role = ColumnRole.Occupy;
            if (!Army.MoveRegiment(c, p)) { m.RemoveColumn(c); continue; }
            budget -= c.Manpower;
            spawned++;
            lastTarget = p;
        }
        if (spawned > 0 && Draw(st))
        {
            Color g = new Color(0.3f, 1f, 0.4f);
            TacticVisuals.Diamond(PosOf(lastTarget), Army.TileSize * 2f, g, 6f);
            TacticVisuals.Label(PosOf(lastTarget), "Liberation columns", g, 3f);
        }
    }

    private static bool IsTargeted(Military m, Province p)
    {
        for (int i = 0; i < m.Columns.Count; i++)
        {
            Regiment c = m.Columns[i];
            if (c.Role == ColumnRole.Occupy && c.HasPath && c.Destination == p) return true;
        }
        return false;
    }

    /// <summary>Called by CombatManager before a regiment is reinforced: during a war the AI keeps the men at the front.</summary>
    public bool AllowReinforce(Regiment r)
    {
        Military m = r.Military;
        if (m == null) return true;
        Nation n = m.Nation;
        if (!n.IsAI || !WM.IsAtWar(n)) return true;
        if (r.InRange || r.InContact || r.StrengthPercent < 0.5f) return true;
        Province p = r.CurrentProvince;
        if (p != null && _hot.Contains(p.Id)) return true;
        return _states.TryGetValue(n.Id, out AIState s) && s.Op != null && s.Op.Units.Contains(r);
    }

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

    #endregion

    #region Offence: Burst scoring + tactic choice

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
            if (_used.Contains(r) || r.StrengthPercent < minStr) continue;
            if (r.CurrentProvince != null && _defendSet.Contains(r.CurrentProvince.Id)) continue;      // defenders hold their ground
            _atk.Add(r);
            myAtk += r.Manpower * r.Military.AttackPerMan.Value;
            centroid += r.WorldPosition;
        }
        if (_atk.Count == 0 || st.Op != null) return;
        centroid /= _atk.Count;

        BuildTargets(st, myAtk, centroid);
        if (_targets.Count == 0) return;

        Target best = _targets[0];
        bool strong = best.Ratio >= ratioNeed;
        if (counter && !strong) return;                       // defenders never gamble
        if (strong && !counter && _atk.Count >= 2 && Spread(_atk, centroid) > settings.gatherTiles * Army.TileSize
    && StartOp(st, AITactic.Spearhead, best.P, null, _atk)) return;

        if (!settings.useTactics || _clock < st.NextTactic)
        {
            if (strong) Blitz(st, best, ratioNeed);
            return;
        }

        // weights: the tactic that fits the situation, penalised when recently used
        for (int i = 0; i < _tw.Length; i++) _tw[i] = 0f;
        _tw[(int)AITactic.Blitz] = strong ? 3f : 0f;
        _tw[(int)AITactic.Spearhead] = counter ? 0f : (strong ? 0.6f : 1.4f);
        _tw[(int)AITactic.Pincer] = _atk.Count >= 2 ? 1.6f * (0.7f + st.Aggression) : 0f;
        _tw[(int)AITactic.Feint] = _atk.Count >= 3 && _targets.Count >= 2 && !counter ? 1.0f : 0f;
        _tw[(int)AITactic.Encircle] = _atk.Count >= settings.siegeMinRegiments && !counter ? (best.P.IsCapital || !strong ? 1.4f : 0.5f) : 0f;
        _tw[(int)AITactic.Cutoff] = _atk.Count >= 3 && !counter ? (strong ? 0.5f : 1.8f) : 0f;

        float sum = 0f;
        for (int i = 1; i < _tw.Length; i++)
        {
            if (_tw[i] <= 0f) continue;
            if (!IsReady(st, (AITactic)i)) _tw[i] = 0f;
            else if ((AITactic)i == st.LastTactic) _tw[i] *= 0.3f;
            sum += _tw[i];
        }
        if (sum <= 0f) { if (strong) Blitz(st, best, ratioNeed); return; }

        float roll = RNG.GetFloat(0f, sum);
        AITactic pick = AITactic.Blitz;
        for (int i = 1; i < _tw.Length; i++)
        {
            if (_tw[i] <= 0f) continue;
            pick = (AITactic)i;
            roll -= _tw[i];
            if (roll <= 0f) break;
        }

        if (pick == AITactic.Blitz) { Blitz(st, best, ratioNeed); return; }
        Province alt = _targets.Count > 1 ? _targets[1].P : null;
        if (!StartOp(st, pick, best.P, alt, _atk) && strong) Blitz(st, best, ratioNeed);
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

            for (int i = 0; i < n.Provinces.Count; i++) ConsiderNeighbours(n.Provinces[i], w, mine, centroid, tile);
            foreach (var kv in w.Occupied)
            {
                if (w.SideOf(kv.Value) != mine) continue;
                Province held = map.GetProvince(kv.Key);
                if (held != null) ConsiderNeighbours(held, w, mine, centroid, tile);
            }
            Nation mainEnemy = World.GetNation(mine == WarSide.Attacker ? w.DefenderId : w.AttackerId);
            if (mainEnemy != null && mainEnemy.Capital != null) TryAddTarget(mainEnemy.Capital, w, mine, centroid, tile);
        }

        if (_targets.Count < 3)
        {
            for (int wi = 0; wi < wars.Count; wi++)
            {
                War w = wars[wi];
                if (w.Ended) continue;
                WarSide mine = w.SideOf(n.Id);
                if (mine == WarSide.None) continue;
                List<int> foes = mine == WarSide.Attacker ? w.Defenders : w.Attackers;
                int added = 0;
                for (int f = 0; f < foes.Count && added < 12; f++)
                {
                    Nation en = World.GetNation(foes[f]);
                    if (en == null) continue;
                    for (int i = 0; i < en.Provinces.Count && added < 12; i++)
                    {
                        Province q = en.Provinces[i];
                        if (_seen.Contains(q.Id)) continue;
                        TryAddTarget(q, w, mine, centroid, tile);
                        added++;
                    }
                }
            }
        }
        if (_targets.Count == 0) return;

        // ONE Burst call scores every candidate: enemy defence that can intervene inside the query radius
        int c = _targets.Count;
        float rad = settings.queryRadiusTiles * tile;
        _q.ReserveQueries(c);
        for (int i = 0; i < c; i++) _q.SetQuery(i, _targets[i].Pos, n.Id, rad);
        _q.Run(_unitCount, c);
        _lastQueries = c;

        for (int i = 0; i < c; i++)
        {
            Target t = _targets[i];
            Vector4 r = _q.Result(i);
            double local = EnemyDefPower(n, t.P);
            t.Def = r.y * 0.7 + local * 0.3 + t.P.Defend.Value * settings.provinceDefendWeight;
            t.Ratio = (float)(myAtk / (t.Def + 1.0));
            float value = 1f + (t.P.IsCapital ? 1.5f : 0f);
            t.Score = Mathf.Min(t.Ratio, 8f) * value / (1f + t.Dist * 0.02f);
            _targets[i] = t;
        }
        _targets.Sort((a, b) => b.Score.CompareTo(a.Score));
    }

    private void ConsiderNeighbours(Province p, War w, WarSide mine, Vector3 centroid, float tile)
    {
        int[] nb = p.Neighbors;
        for (int i = 0; i < nb.Length; i++)
        {
            Province q = map.GetProvince(nb[i]);
            if (q != null) TryAddTarget(q, w, mine, centroid, tile);
        }
    }

    private void TryAddTarget(Province q, War w, WarSide mine, Vector3 centroid, float tile)
    {
        if (!_seen.Add(q.Id) || q.Owner == null) return;
        if (w.SideOf(q.Owner.Id) != War.Opposite(mine)) return;
        if (w.Occupied.TryGetValue(q.Id, out int occ) && w.SideOf(occ) == mine) return;
        Vector3 pos = PosOf(q);
        _targets.Add(new Target { P = q, Pos = pos, Dist = Vector3.Distance(centroid, pos) / tile });
    }

    /// <summary>Weak points: hit as many as the army can, each with just enough force, nearest units first.</summary>
    private void Blitz(AIState st, Target best, float ratioNeed)
    {
        int arrows = 0;
        Color col = st.Nation.Color;
        bool draw = settings.useTactics && Draw(st);
        double remaining = 0.0;
        for (int i = 0; i < _atk.Count; i++) remaining += _atk[i].Manpower * _atk[i].Military.AttackPerMan.Value;

        for (int t = 0; t < _targets.Count && _atk.Count > 0; t++)
        {
            Target tg = _targets[t];
            double need = tg.Def * ratioNeed, got = 0.0;
            if (t > 0 && (tg.Ratio < ratioNeed || remaining < need)) continue;      // never split below what it needs
            while (_atk.Count > 0 && got < need)
            {
                Regiment r = Nearest(_atk, tg.Pos, null, 0f);
                if (r == null) { _atk.Clear(); break; }
                _atk.Remove(r);
                Vector3 from = r.WorldPosition;
                if (!Order(r, tg.P)) continue;
                if (draw && arrows++ < 3) TacticVisuals.Arrow(from, tg.Pos, col, 6f);
                double p = r.Manpower * r.Military.AttackPerMan.Value;
                got += p; remaining -= p;
            }
        }
        for (int i = 0; i < _atk.Count; i++) Order(_atk[i], best.P);
        _atk.Clear();
        if (draw) TacticVisuals.Announce(best.Pos, st.Nation.Name + ": " + TacticNames[(int)AITactic.Blitz], col, 4f);
        Used(st, AITactic.Blitz);
    }

    #endregion

    #region Operations (Spearhead / Pincer / Counter-Punch / Feint / Encircle)

    private bool StartOp(AIState st, AITactic kind, Province strike, Province alt, List<Regiment> units)
    {
        if (units.Count == 0 || strike == null) return false;
        Nation n = st.Nation;
        var op = new Op { Kind = kind, Strike = strike, Alt = alt, Start = _clock };
        for (int i = 0; i < units.Count; i++) { op.Units.Add(units[i]); op.Grp.Add(0); op.Stage.Add(null); }
        Vector3 sp = PosOf(strike);

        switch (kind)
        {
            case AITactic.Spearhead:
                {
                    Province stage = NearestControlled(n, sp);
                    if (stage == null) return false;
                    for (int i = 0; i < op.Stage.Count; i++) op.Stage[i] = stage;
                    st.Staging = stage;
                    break;
                }
            case AITactic.Pincer:
            case AITactic.CounterPunch:
                {
                    if (!PickFlanks(n, strike, out Province a, out Province b)) return false;
                    Vector3 pa = PosOf(a), pb = PosOf(b);
                    int na = 0, nb = 0;
                    for (int i = 0; i < op.Units.Count; i++)
                    {
                        bool toA = (op.Units[i].WorldPosition - pa).sqrMagnitude <= (op.Units[i].WorldPosition - pb).sqrMagnitude;
                        op.Grp[i] = toA ? 0 : 1; op.Stage[i] = toA ? a : b;
                        if (toA) na++; else nb++;
                    }
                    if (na == 0 || nb == 0)                              // both flanks need somebody
                    {
                        int idx = nb == 0 ? op.Units.Count - 1 : 0;
                        op.Grp[idx] = nb == 0 ? 1 : 0; op.Stage[idx] = nb == 0 ? b : a;
                    }
                    break;
                }
            case AITactic.Feint:
                {
                    if (alt == null || units.Count < 3) return false;
                    Province stage = NearestControlled(n, sp);
                    if (stage == null) return false;
                    int decoys = Mathf.Max(1, units.Count / 4);
                    for (int d = 0; d < decoys; d++)                     // the weakest regiments play the decoy
                    {
                        int weakest = -1; double wm = double.MaxValue;
                        for (int i = 0; i < op.Units.Count; i++)
                            if (op.Grp[i] != 0 || op.Units[i].Manpower >= wm) continue; else { wm = op.Units[i].Manpower; weakest = i; }
                        if (weakest >= 0) { op.Grp[weakest] = -1; }
                    }
                    for (int i = 0; i < op.Units.Count; i++)
                    {
                        bool decoy = op.Grp[i] == -1;
                        op.Grp[i] = decoy ? 0 : 1;
                        op.Stage[i] = decoy ? alt : stage;
                    }
                    break;
                }
            case AITactic.Encircle:
                {
                    _cand.Clear();
                    int[] nb = strike.Neighbors;
                    for (int i = 0; i < nb.Length && _cand.Count < settings.siegeMaxSlots; i++)
                    {
                        Province q = map.GetProvince(nb[i]);
                        if (q != null && EnemyMen(n, q) <= 0.0 && Army.CanEnter(n, q)) _cand.Add(q);
                    }
                    if (_cand.Count < 2) return false;
                    for (int i = 0; i < op.Units.Count; i++)
                    {
                        Province bestSlot = _cand[0];
                        float bd = float.MaxValue;
                        for (int s = 0; s < _cand.Count; s++)
                        {
                            float d = (op.Units[i].WorldPosition - PosOf(_cand[s])).sqrMagnitude + s * 0.01f + (i % _cand.Count == s ? -1f : 0f);
                            if (d < bd) { bd = d; bestSlot = _cand[s]; }
                        }
                        op.Stage[i] = bestSlot;
                    }
                    break;
                }
            case AITactic.Cutoff:
                {
                    Province stage = NearestControlled(n, sp);
                    if (stage == null) return false;
                    Province rear = PickRear(n, strike, PosOf(stage));
                    if (rear == null) return false;
                    int blockers = Mathf.Max(1, units.Count / 3);
                    for (int b = 0; b < blockers; b++)
                    {
                        int pick = -1; float bd = float.MaxValue;
                        for (int i = 0; i < op.Units.Count; i++)
                        {
                            if (op.Grp[i] == 1) continue;
                            float d = (op.Units[i].WorldPosition - PosOf(rear)).sqrMagnitude;
                            if (d < bd) { bd = d; pick = i; }
                        }
                        if (pick >= 0) op.Grp[pick] = 1;
                    }
                    for (int i = 0; i < op.Units.Count; i++) op.Stage[i] = op.Grp[i] == 1 ? rear : stage;
                    st.Staging = stage;
                    break;
                }
            default: return false;
        }

        for (int i = 0; i < op.Units.Count; i++) Order(op.Units[i], op.Stage[i]);
        st.Op = op;
        Used(st, kind);

        if (Draw(st)) DrawOpStart(st, op);
        return true;
    }

    private bool PickFlanks(Nation n, Province strike, out Province a, out Province b)
    {
        a = b = null;
        _cand.Clear();
        int[] nb = strike.Neighbors;
        for (int i = 0; i < nb.Length; i++)
        {
            Province q = map.GetProvince(nb[i]);
            if (q != null && EnemyMen(n, q) <= 0.0 && Army.CanEnter(n, q)) _cand.Add(q);
        }
        Vector2 c = strike.Centroid;
        float bestDot = 0.3f;                                         // flanks must be at least ~70 degrees apart
        for (int i = 0; i < _cand.Count; i++)
            for (int j = i + 1; j < _cand.Count; j++)
            {
                float dot = Vector2.Dot((_cand[i].Centroid - c).normalized, (_cand[j].Centroid - c).normalized);
                if (dot < bestDot) { bestDot = dot; a = _cand[i]; b = _cand[j]; }
            }
        return a != null;
    }

    private void DrawOpStart(AIState st, Op op)
    {
        Color col = st.Nation.Color;
        Vector3 from = CentroidOf(op.Units), to = PosOf(op.Strike);
        var drawn = new HashSet<Province>();
        for (int i = 0; i < op.Stage.Count; i++)
        {
            Province s = op.Stage[i];
            if (s == null || !drawn.Add(s) || drawn.Count > 3) continue;
            if (op.Kind == AITactic.Feint && op.Grp[i] == 0) TacticVisuals.Arrow(from, PosOf(s), new Color(col.r, col.g, col.b, 0.6f), 6f, 0.6f);
            else TacticVisuals.CurvedArrow(from, PosOf(s), col, 6f, drawn.Count % 2 == 0 ? 0.2f : -0.2f);
        }
        if (op.Kind == AITactic.Encircle)
        {
            var ring = new List<Vector3>();
            foreach (Province p in drawn) ring.Add(PosOf(p));
            TacticVisuals.Ring(ring, col, 10f);
        }
        TacticVisuals.Announce(to, st.Nation.Name + ": " + TacticNames[(int)op.Kind], col, 4f);
    }

    private void UpdateOp(AIState st)
    {
        Op op = st.Op;
        Nation n = st.Nation;
        for (int i = op.Units.Count - 1; i >= 0; i--)
        {
            Regiment r = op.Units[i];
            if (r.Military == null || r.State == RegimentState.Retreating || r.Manpower <= 0.5)
            { op.Units.RemoveAt(i); op.Grp.RemoveAt(i); op.Stage.RemoveAt(i); }
        }
        bool counterOk = op.Kind == AITactic.CounterPunch ? EnemyMen(n, op.Strike) > 0.0 : (op.Strike.Owner != null && Hostile(n, op.Strike.Owner));
        if (op.Units.Count == 0 || !counterOk) { st.Op = null; return; }

        int main = 0, arrived = 0;
        for (int i = 0; i < op.Units.Count; i++)
        {
            if (op.Kind == AITactic.Feint && op.Grp[i] == 0) continue;           // decoys do not count
            main++;
            Regiment r = op.Units[i];
            Province sp = op.Stage[i];
            if (r.State != RegimentState.Moving && r.CurrentProvince == sp) arrived++;
            else if (!InOrHeading(r, sp)) Army.MoveRegiment(r, sp);               // pushed away / lost its path
        }
        bool timeout = _clock - op.Start > settings.opTimeout;
        bool ready = main > 0 && arrived >= Mathf.Max(1, Mathf.CeilToInt(settings.massReadyFraction * main));

        if (op.Kind == AITactic.Encircle && op.Phase == 0 && arrived >= Mathf.Max(1, main / 2))
        {
            Regiment pin = null;                                                  // frontal fix: one regiment pins the target
            float bd = float.MaxValue;
            for (int i = 0; i < op.Units.Count; i++)
            {
                float d = (op.Units[i].WorldPosition - PosOf(op.Strike)).sqrMagnitude;
                if (d < bd) { bd = d; pin = op.Units[i]; }
            }
            if (pin != null) Army.MoveRegiment(pin, op.Strike);
            op.Phase = 1;
            return;
        }
        if ((op.Kind == AITactic.Encircle && op.Phase == 1 && !ready && !timeout)) return;
        if (ready || timeout) Launch(st, op);
    }

    private void Launch(AIState st, Op op)
    {
        Nation n = st.Nation;
        if (settings.surgeColumns > 0) SurgeUnits(st, op.Units);                  // fill the strike force first

        Color col = n.Color;
        Vector3 to = PosOf(op.Strike);
        var seen = new HashSet<Province>();
        Province a = null, b = null;
        for (int i = 0; i < op.Units.Count; i++)
        {
            Regiment r = op.Units[i];
            Province s = op.Stage[i];
            if (s != null && seen.Add(s)) { if (a == null) a = s; else if (b == null) b = s; }
            if (op.Kind == AITactic.Cutoff && op.Grp[i] == 1) continue;           // blockers hold the rear
            Province target = op.Kind == AITactic.Feint && op.Grp[i] == 0 && op.Alt != null ? op.Alt : op.Strike;
            if (r.State != RegimentState.Moving || op.Kind != AITactic.Encircle) Order(r, target);
        }

        if (Draw(st))
        {
            if ((op.Kind == AITactic.Pincer || op.Kind == AITactic.CounterPunch) && a != null && b != null)
                TacticVisuals.Pincer(PosOf(a), PosOf(b), to, col, 8f);
            else
                foreach (Province s in seen) TacticVisuals.Arrow(PosOf(s), to, col, 8f, 1.5f);
            TacticVisuals.Cross(to, Army.TileSize * 1.5f, col, 6f);
            TacticVisuals.Announce(to, TacticNames[(int)op.Kind] + "!", col, 3f);
        }
        st.Op = null;
        st.NextTactic = _clock + settings.tacticInterval;
    }

    /// <summary>Reserve Surge: under-strength striking regiments get several columns at once (paid by the treasury).</summary>
    private void SurgeUnits(AIState st, List<Regiment> units)
    {
        CombatManager cm = CombatManager.Instance;
        int budget = 8, spawned = 0;
        Vector3 pos = Vector3.zero;
        for (int i = 0; i < units.Count && budget > 0; i++)
        {
            Regiment r = units[i];
            if (r.StrengthPercent >= 0.97f || r.State == RegimentState.Moving) continue;
            if (cm.ForceReinforce(r, settings.surgeColumns)) { budget--; spawned++; pos = r.WorldPosition; }
        }
        if (spawned > 0) { st.Ready[(int)AITactic.Surge] = _clock + settings.tacticCooldown; if (Draw(st)) TacticVisuals.Announce(pos, TacticNames[(int)AITactic.Surge], st.Nation.Color, 3f); }
    }

    private Province NearestControlled(Nation n, Vector3 pos)
    {
        Province best = null;
        float bestD = float.MaxValue;
        for (int i = 0; i < n.Provinces.Count; i++)
        {
            Province p = n.Provinces[i];
            if (EnemyMen(n, p) > 0.0) continue;
            float d = (PosOf(p) - pos).sqrMagnitude;
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
                float d = (PosOf(p) - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = p; }
            }
        }
        return best;
    }

    #endregion

    #region Shares

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

        for (int i = 0; i < c; i++) { _delta.Add(_w[i] / sum * 0.995f - regs[i].Share); _order.Add(i); }
        for (int i = 1; i < c; i++)                                                   // insertion sort: biggest decrease first
        {
            int key = _order[i], j = i - 1;
            while (j >= 0 && _delta[_order[j]] > _delta[key]) { _order[j + 1] = _order[j]; j--; }
            _order[j + 1] = key;
        }
        for (int i = 0; i < c; i++) m.SetRegimentShare(regs[_order[i]], _w[_order[i]] / sum * 99.5f);
        st.SharesCustom = true;
    }

    private float ShareWeight(Nation n, Regiment r, bool war)
    {
        if (r.InContact || r.InRange) return 2.2f;
        if (r.State == RegimentState.Moving) return 1.3f;
        Province p = r.CurrentProvince;
        if (p != null && EnemyMenNear(n, p) > 0.0) return 1.2f;
        return war ? 0.6f : 0.8f;
    }

    #endregion

    #region Diplomacy

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

        CollectNeighbors(n);
        Nation candidate = null;
        float candidateScore = 0f;

        foreach (var kv in _nbCount)
        {
            Nation o = kv.Key;
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

            if (o.IsAI && st.WarTarget != o)
            {
                bool appease = op > myPower * settings.fearRatio;
                bool likes = st.Friendliness > 0.5f && val > -20f;
                if (appease || likes)
                {
                    if (val < settings.improveUntil && (r == null || r.Trend <= 0)) rel.ImproveRelationship(n, o);
                    if ((f & (RelationFlags.Alliance | RelationFlags.Vassal)) == 0)
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

            // war target: the weakest unprotected coalition that shares the LONGEST border (compact expansion)
            bool protectedPair = (f & (RelationFlags.Alliance | RelationFlags.Vassal)) != 0 ||
                                 (st.Aggression < 0.7f && (f & (RelationFlags.DefensivePact | RelationFlags.NonAggression)) != 0);
            if (protectedPair || wm.HasTruce(n, o) || op * settings.warMinPowerRatio > myPower) continue;
            float score = kv.Value / (1f + (float)(op / Math.Max(1.0, myPower)) * 3f);
            if (score > candidateScore) { candidate = o; candidateScore = score; }
        }

        if (!atWar || wm.GetWars(n).Count < settings.maxWars)
        {
            bool ready = today - st.LastWarDay >= settings.minDaysBetweenWars &&
                         wm.GetWars(n).Count < settings.maxWars &&
                         Army.GetMilitary(n) != null && Army.GetMilitary(n).ManpowerPercent >= 50.0;
            if (st.WarTarget == null && ready && candidate != null &&
                RNG.GetFloat(0f, 1f) < st.Aggression * settings.warChancePerCheck)
                st.WarTarget = candidate;
        }

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

    #endregion

    #region War end / peace decisions

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
            if (WM.CanDecide(w, side)) { AIDecide(st, w, side); continue; }
            if (side == WarSide.Attacker && w.DaysActive >= settings.stalemateDays &&
                w.AttackerProgress < 60f && w.DefenderProgress < 60f)
            {
                Nation other = World.GetNation(w.DefenderId);
                if (other != null && other.IsAI) WM.WhitePeace(w);
            }
        }
    }

    /// <summary>A hopeless main belligerent gives up (the other side wins) instead of bleeding for years.</summary>
    private void SurrenderCheck(AIState st)
    {
        Nation n = st.Nation;
        IReadOnlyList<War> wars = WM.GetWars(n);
        for (int i = wars.Count - 1; i >= 0; i--)
        {
            War w = wars[i];
            if (w.Ended || !w.IsMain(n.Id)) continue;
            WarSide side = w.SideOf(n.Id);
            float mine = w.ProgressOf(side), theirs = w.ProgressOf(War.Opposite(side));
            Nation other = World.GetNation(side == WarSide.Attacker ? w.DefenderId : w.AttackerId);
            if (other == null || !other.IsAI) continue;                           // never forces the player to win
            if (theirs >= 70f && mine + 25f < theirs && RNG.GetFloat(0f, 1f) < settings.surrenderChance * 0.1f)
            { WM.Capitulate(w, n); return; }
        }
    }

    private void AIDecide(AIState st, War w, WarSide side)
    {
        Nation loser = World.GetNation(side == WarSide.Attacker ? w.DefenderId : w.AttackerId);
        if (settings.allowVassalize && st.Aggression > 0.5f && loser != null && Rel.GetOverlord(loser) == null &&
            Power(st.Nation) > Power(loser) * 2.0 && RNG.GetFloat(0f, 1f) < 0.5f && WM.Decide(w, side, WarOutcome.Vassalize)) return;

        // a friendly, peaceful AI ends a war that barely paid off
        if (st.Friendliness > 0.75f && st.Aggression < 0.3f && w.DaysActive > 360 && RNG.GetFloat(0f, 1f) < 0.4f &&
            WM.Decide(w, side, WarOutcome.WhitePeace)) return;

        // aggressive AIs spend everything, friendly ones are content with a compact region
        float spend = settings.demandSpend * (st.Aggression > 0.6f ? 1f : Mathf.Lerp(1f, 0.55f, st.Friendliness * 0.8f));
        if (TryDemand(st, w, side, spend)) return;
        WM.Decide(w, side, WarOutcome.TakeOccupied);
    }

    /// <summary>A hostile neighbour of the target that lies farther from us than the target itself.</summary>
    private Province PickRear(Nation n, Province strike, Vector3 from)
    {
        Province best = null;
        float bd = (PosOf(strike) - from).sqrMagnitude;
        int[] nb = strike.Neighbors;
        for (int i = 0; i < nb.Length; i++)
        {
            Province q = map.GetProvince(nb[i]);
            if (q == null || q.Owner == null || !Hostile(n, q.Owner) || EnemyMen(n, q) > 0.0 || !Army.CanEnter(n, q)) continue;
            float d = (PosOf(q) - from).sqrMagnitude;
            if (d > bd) { bd = d; best = q; }
        }
        return best;
    }

    private void EnsureDemandArrays(int pc)
    {
        if (_touch != null && _touch.Length == pc) { Array.Clear(_touch, 0, pc); Array.Clear(_isCand, 0, pc); Array.Clear(_inMine, 0, pc); return; }
        _touch = new int[pc]; _isCand = new bool[pc]; _inMine = new bool[pc]; _costArr = new float[pc];
    }

    /// <summary>
    /// "Beautiful" demands: the next province is the one that shares most of its border with land we already hold or
    /// demanded (holes get filled first, regions grow as a blob), then thin spikes are pruned again.
    /// </summary>
    private bool TryDemand(AIState st, War w, WarSide side, float spend)
    {
        List<int> loserIds = side == WarSide.Attacker ? w.Defenders : w.Attackers;
        List<int> ourIds = side == WarSide.Attacker ? w.Attackers : w.Defenders;
        int pc = map.Provinces.Count;
        EnsureDemandArrays(pc);
        _cand.Clear(); _demand.Clear();

        for (int i = 0; i < loserIds.Count; i++)
        {
            Nation ln = World.GetNation(loserIds[i]);
            if (ln == null) continue;
            for (int j = 0; j < ln.Provinces.Count; j++)
            {
                Province p = ln.Provinces[j];
                _cand.Add(p);
                _isCand[p.Id] = true;
                _costArr[p.Id] = WM.GetProvinceCost(w, side, p);
            }
        }
        if (_cand.Count == 0) return false;
        for (int i = 0; i < ourIds.Count; i++)
        {
            Nation a = World.GetNation(ourIds[i]);
            if (a == null) continue;
            for (int j = 0; j < a.Provinces.Count; j++) _inMine[a.Provinces[j].Id] = true;
        }
        for (int i = 0; i < _cand.Count; i++)
        {
            int c = 0;
            int[] nb = _cand[i].Neighbors;
            for (int k = 0; k < nb.Length; k++) if (_inMine[nb[k]]) c++;
            _touch[_cand[i].Id] = c;
        }

        float total = WM.GetDemandBudget(w, side) * spend, budget = total;
        Nation winMain = World.GetNation(side == WarSide.Attacker ? w.AttackerId : w.DefenderId);

        while (_demand.Count < settings.maxDemandProvinces)
        {
            Province best = null;
            float bestScore = float.MinValue;
            for (int i = 0; i < _cand.Count; i++)
            {
                Province p = _cand[i];
                if (!_isCand[p.Id] || _costArr[p.Id] > budget) continue;
                bool occ = w.Occupied.TryGetValue(p.Id, out int oc) && w.SideOf(oc) == side;
                int t = _touch[p.Id];
                if (t == 0 && !occ) continue;
                int deg = Mathf.Max(1, p.Neighbors.Length);
                float ratio = t / (float)deg;
                float score = ratio * ratio * 2f + (occ ? 0.35f : 0f) + (p.IsCapital ? 0.4f : 0f)
                              + (ratio >= 0.99f ? 1f : 0f)                       // a hole inside our land
                              - _costArr[p.Id] / Mathf.Max(1f, total) * 0.6f;
                if (score > bestScore) { bestScore = score; best = p; }
            }

            if (best == null && _demand.Count == 0 && winMain != null && winMain.Capital != null)   // sea between us: the nearest one
            {
                float bd = float.MaxValue;
                for (int i = 0; i < _cand.Count; i++)
                {
                    Province p = _cand[i];
                    if (!_isCand[p.Id] || _costArr[p.Id] > budget) continue;
                    float d = (p.Centroid - winMain.Capital.Centroid).sqrMagnitude;
                    if (d < bd) { bd = d; best = p; }
                }
            }
            if (best == null) break;

            _demand.Add(best.Id);
            _isCand[best.Id] = false;
            _inMine[best.Id] = true;
            budget -= _costArr[best.Id];
            int[] nb = best.Neighbors;
            for (int k = 0; k < nb.Length; k++) if (_isCand[nb[k]]) _touch[nb[k]]++;
        }

        // prune spikes (newest first): a demanded province that barely touches the rest would look ugly
        for (int i = _demand.Count - 1; i >= 0 && _demand.Count > 1; i--)
        {
            Province p = map.GetProvince(_demand[i]);
            if (p == null) continue;
            if (w.Occupied.TryGetValue(p.Id, out int oc) && w.SideOf(oc) == side) continue;
            int t = 0, deg = Mathf.Max(1, p.Neighbors.Length);
            for (int k = 0; k < p.Neighbors.Length; k++) if (_inMine[p.Neighbors[k]]) t++;
            if (t / (float)deg < settings.minDemandTouch) { _inMine[p.Id] = false; _demand.RemoveAt(i); }
        }

        return _demand.Count > 0 && WM.Decide(w, side, _demand);
    }

    #endregion

    #region Context menu

    [ContextMenu("AI: Log States")]
    private void MenuLog()
    {
        for (int i = 0; i < _list.Count; i++)
        {
            AIState s = _list[i];
            Debug.Log($"[AI] {s.Nation.Name} aggr {s.Aggression:F2} friend {s.Friendliness:F2} threat {s.Threat:F2} " +
                      $"from {(s.ThreatFrom != null ? s.ThreatFrom.Name : "-")} target {(s.WarTarget != null ? s.WarTarget.Name : "-")} " +
                      $"op {(s.Op != null ? TacticNames[(int)s.Op.Kind] : "-")} last {s.LastTactic} provinces {s.Nation.ProvinceCount}/{s.MergeGoal}");
        }
    }

    #endregion
}