using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

#region Data

public enum MemberState : byte { Loyal = 0, Rebelling = 1 }

[Serializable]
public class FormationSettings
{
    [Header("Split (random)")]
    [Tooltip("A part needs at least this many provinces.")]
    [Min(1)] public int minPartProvinces = 2;
    [Tooltip("Relationship between the old nation and the new ones right after a split.")]
    public Vector2 splitRelation = new Vector2(0f, 30f);

    [Header("Form Nation (AI asks)")]
    public bool aiFormation = true;
    [Tooltip("Max nations (besides the leader) in one empire.")]
    [Min(1)] public int maxMembers = 15;
    [Tooltip("Leader power must be at least this many times the target's power.")]
    public float requiredPowerRatio = 1.5f;
    [Tooltip("Relationship below this value: the AI does not even ask.")]
    [Range(-100f, 100f)] public float minRelationToAsk = 50f;
    [Tooltip("Chance of a request per AI diplomacy turn (x aggression factor).")]
    [Range(0f, 1f)] public float askChance = 0.25f;
    [Range(0f, 1f)] public float allyBonus = 0.1f;
    public float refusePenalty = 10f;
    [Tooltip("Days before the same pair can be asked again.")]
    public int askCooldownDays = 1825;
    [Tooltip("The AI may ask the player nation (needs UI: OnFormationRequestToPlayer).")]
    public bool aiMayAskPlayer = false;

    [Header("Empire stability (monthly)")]
    public float peaceDrift = 0.6f;
    public float warDrift = -1.5f;
    public float weakArmyPercent = 30f;
    public float weakArmyDrift = -1f;
    public float sizeDrift = -0.3f;
    [Tooltip("Penalty factor when the empire lost territory (held fraction < 0.8).")]
    public float lostLandFactor = 10f;
    public float noise = 0.4f;
    [Tooltip("A member whose relationship is BELOW this demands independence.")]
    public float demandBelow = 15f;
    [Tooltip("Demand success chance is (relationship+100)/200, multiplied by this while the empire is at war.")]
    [Range(0f, 1f)] public float warGrantFactor = 0.5f;
    public float failedRelation = -25f;
    public int rebelWarDelayDays = 180;
    public float releaseRelation = 25f;

    [Header("Performance (province transfers are spread over frames)")]
    [Tooltip("Operations with up to this many provinces run instantly, bigger ones are time-sliced.")]
    [Min(1)] public int syncTransferLimit = 40;
    [Tooltip("Real milliseconds per frame spent moving provinces.")]
    [Min(0.1f)] public float transferBudgetMs = 2f;
    [Tooltip("At least this many provinces are moved per frame, whatever the budget.")]
    [Min(1)] public int minTransfersPerFrame = 4;
}

public sealed class EmpireMember
{
    public int NationId;
    public string Name;
    public Color Color;
    public List<int> ProvinceIds = new List<int>();
    public int CapitalId = -1;
    /// <summary>-100..100: how the member feels about the empire.</summary>
    public float Relation;
    public MemberState State;
    public long RebelSince;
}

public sealed class Empire
{
    public int LeaderId;
    public readonly List<EmpireMember> Members = new List<EmpireMember>(4);
}

[Serializable] public class EmpireInfo { public string leader, members; }
[Serializable] public class FormationDiagnostics { public int empires, pendingJobs, pendingProvinces, lockedNations; }

#endregion

#region Burst: split partition

/// <summary>
/// Random multi-source growth over a flattened (CSR) province graph. Candidates (province, part) live in one list;
/// a random one is taken, accepted with probability weight / maxWeight (bigger weight = bigger part).
/// Provinces that no seed can reach stay -1 (the manager gives them to the nearest seed).
/// </summary>
[BurstCompile]
internal struct SplitPartitionJob : IJob
{
    [ReadOnly] public NativeArray<int> adjStart;
    [ReadOnly] public NativeArray<int> adj;
    [ReadOnly] public NativeArray<int> seeds;
    [ReadOnly] public NativeArray<float> weight;
    public NativeArray<int> part;          // pre-filled with -1
    public uint seed;

    public void Execute()
    {
        var rng = new Unity.Mathematics.Random(seed | 1u);
        float maxW = 0.0001f;
        for (int k = 0; k < weight.Length; k++) if (weight[k] > maxW) maxW = weight[k];

        var cand = new NativeList<Unity.Mathematics.int2>(256, Allocator.Temp);
        for (int k = 0; k < seeds.Length; k++) part[seeds[k]] = k;
        for (int k = 0; k < seeds.Length; k++) Push(cand, seeds[k], k);

        int guard = adj.Length * 32 + 4096;
        while (cand.Length > 0 && guard-- > 0)
        {
            int i = rng.NextInt(0, cand.Length);
            Unity.Mathematics.int2 c = cand[i];
            if (part[c.x] >= 0) { cand.RemoveAtSwapBack(i); continue; }
            if (rng.NextFloat() * maxW > weight[c.y]) continue;            // rejected: stays in the list
            cand.RemoveAtSwapBack(i);
            part[c.x] = c.y;
            Push(cand, c.x, c.y);
        }
        cand.Dispose();
    }

    private void Push(NativeList<Unity.Mathematics.int2> cand, int node, int k)
    {
        for (int e = adjStart[node]; e < adjStart[node + 1]; e++)
        {
            int nb = adj[e];
            if (part[nb] < 0) cand.Add(new Unity.Mathematics.int2(nb, k));
        }
    }
}

#endregion

// ─────────────────────────────────────────────────────────────────────────────
//  NATION FORMATION MANAGER  (NationFormationManager.Instance)
//  Split  : one nation -> N random connected parts (Burst partition, transfers time-sliced).
//  Form   : leader absorbs nations; they vanish when their provinces are moved, and remember their land.
//  Release: a member gets back the provinces it brought (if the empire still owns them, no enemy occupies them).
//  AI     : strong AI asks a weaker neighbour; unstable empires get independence demands -> rebels / civil war.
//  Province moves go through ONE queue with a per-frame time budget, so big operations never spike a frame.
// ─────────────────────────────────────────────────────────────────────────────
[DefaultExecutionOrder(-640)]
public class NationFormationManager : TickBehaviour
{
    #region Singleton

    private static NationFormationManager _instance;
    private static bool _quitting;
    public static bool HasInstance => _instance != null;

    public static NationFormationManager Instance
    {
        get
        {
            if (_instance == null && !_quitting)
            {
#if UNITY_2022_2_OR_NEWER
                _instance = FindFirstObjectByType<NationFormationManager>();
#else
                _instance = FindObjectOfType<NationFormationManager>();
#endif
                if (_instance == null) _instance = new GameObject("NationFormationManager").AddComponent<NationFormationManager>();
            }
            return _instance;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() { _instance = null; _quitting = false; }

    #endregion

    #region Inspector, data & events

    public FormationSettings settings = new FormationSettings();
    [SerializeField] private MapManager map;

    [Header("Context menu helpers")]
    [SerializeField] private int debugNationA = -1;   // leader / nation to split
    [SerializeField] private int debugNationB = -1;   // member
    [SerializeField, Min(2)] private int debugParts = 3;

    [Header("Diagnostics (Editor only, throttled)")]
    [SerializeField] private bool showDiagnostics = true;
    [Min(0.25f)] public float diagnosticsInterval = 1f;
    [SerializeField] private FormationDiagnostics diagnostics = new FormationDiagnostics();
    [SerializeField] private List<EmpireInfo> empiresView = new List<EmpireInfo>();
    private float _nextDiagnostics;

    protected override int TickPriority => -640;

    private readonly struct Move
    {
        public readonly Province P;
        public readonly Nation To;
        public Move(Province p, Nation to) { P = p; To = to; }
    }

    private sealed class TransferJob
    {
        public List<Move> Moves;
        public int Cursor;
        public List<int> Locks;
        public Action Done;
    }

    private sealed class CivilWar { public Empire Empire; public EmpireMember Rec; public int RebelId; }

    private readonly Dictionary<int, Empire> _empires = new Dictionary<int, Empire>();       // leader id -> empire
    private readonly Dictionary<long, long> _cooldown = new Dictionary<long, long>();        // pair -> day asking is allowed again
    private readonly Dictionary<int, CivilWar> _civil = new Dictionary<int, CivilWar>();     // war id -> data
    private readonly Queue<TransferJob> _jobs = new Queue<TransferJob>();
    private readonly HashSet<int> _busy = new HashSet<int>();                                // nations inside a running transfer
    private readonly List<Nation> _refresh = new List<Nation>();
    private readonly List<Empire> _monthBuf = new List<Empire>();
    private readonly List<Empire> _dayBuf = new List<Empire>();
    private readonly List<EmpireMember> _memBuf = new List<EmpireMember>();
    private readonly List<War> _warBuf = new List<War>();
    private readonly List<Province> _provBuf = new List<Province>();
    private readonly HashSet<Nation> _nbBuf = new HashSet<Nation>();
    private readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();
    private int[] _local;                                                                    // province id -> local index (-1)
    private bool _aiRefreshPending;

    public string LastError { get; private set; }
    public int EmpireCount => _empires.Count;

    public event Action<Nation, List<Nation>> OnNationFormed;            // leader, absorbed (already removed)
    public event Action<Nation, List<Nation>> OnNationSplit;             // original, new nations
    public event Action<Nation, Nation> OnIndependenceGranted;           // empire leader, new nation
    public event Action<Nation, string> OnIndependenceDenied;            // empire leader, member name
    public event Action<Nation, Nation> OnFormationRefused;              // proposer, target
    public event Action<Nation, Nation> OnFormationRequestToPlayer;      // proposer, player nation

    private WorldManager World => WorldManager.Instance;
    private WarManager WM => WarManager.Instance;
    private ArmyManager Army => ArmyManager.Instance;
    private RelationshipManager Rel => RelationshipManager.Instance;

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
        World.OnNationRemoved += HandleNationRemoved;
        DateManager.Instance.OnDayPassed += HandleDay;
        DateManager.Instance.OnMonthPassed += HandleMonth;
        WM.OnWarEnded += HandleWarEnded;
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        if (WorldManager.HasInstance)
        {
            WorldManager.Instance.OnNationsCleared -= ClearAll;
            WorldManager.Instance.OnNationRemoved -= HandleNationRemoved;
        }
        if (DateManager.HasInstance)
        {
            DateManager.Instance.OnDayPassed -= HandleDay;
            DateManager.Instance.OnMonthPassed -= HandleMonth;
        }
        if (WarManager.HasInstance) WarManager.Instance.OnWarEnded -= HandleWarEnded;
        if (_instance == this && _quitting) _instance = null;
    }

    private void OnDestroy() { if (_instance == this) _instance = null; }

    public override void Tick(float deltaTime) { }

    public void ClearAll()
    {
        _empires.Clear(); _cooldown.Clear(); _civil.Clear();
        _jobs.Clear(); _busy.Clear(); _refresh.Clear();
        _local = null; _aiRefreshPending = false;
    }

    private void HandleNationRemoved(Nation n)
    {
        if (n == null) return;
        _empires.Remove(n.Id);          // leader gone: its empire (and the land) is gone too
        _busy.Remove(n.Id);
    }

    /// <summary>Real-time (works while the game is paused): spends the per-frame budget on queued province moves.</summary>
    private void LateUpdate()
    {
        if (_instance != this) return;
        ProcessJobs();
        FlushRefresh();
#if UNITY_EDITOR
        if (showDiagnostics && Time.unscaledTime >= _nextDiagnostics)
        {
            _nextDiagnostics = Time.unscaledTime + Mathf.Max(0.25f, diagnosticsInterval);
            RefreshDiagnostics();
        }
#endif
    }

    #endregion

    #region Time-sliced transfer queue

    private static long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    private bool IsBusy(Nation n) => n != null && _busy.Contains(n.Id);

    private void Lock(List<int> locks, Nation n)
    {
        if (n != null && _busy.Add(n.Id)) locks.Add(n.Id);
    }

    private void Unlock(List<int> locks)
    {
        if (locks == null) return;
        for (int i = 0; i < locks.Count; i++) _busy.Remove(locks[i]);
    }

    private void Transfer(Province p, Nation to)
    {
        if (map != null && map.IsReady) map.SetProvinceOwner(p.Id, to.Id);
        else World.AssignProvince(p, to);
    }

    private void ApplyMove(Move m)
    {
        if (m.P == null || m.To == null || m.P.IsMerged || m.P.Owner == m.To) return;
        if (World.GetNation(m.To.Id) != m.To) return;                  // target vanished
        Transfer(m.P, m.To);
    }

    /// <summary>Small batches run now, big ones are queued. 'done' runs when every province has moved. Locks are released then.</summary>
    private void RunMoves(List<Move> moves, List<int> locks, Action done)
    {
        if (moves.Count <= settings.syncTransferLimit)
        {
            for (int i = 0; i < moves.Count; i++) ApplyMove(moves[i]);
            Unlock(locks);
            done?.Invoke();
            return;
        }
        _jobs.Enqueue(new TransferJob { Moves = moves, Locks = locks, Done = done });
    }

    private void ProcessJobs()
    {
        if (_jobs.Count == 0) return;
        long budget = (long)(Mathf.Max(0.1f, settings.transferBudgetMs) * System.Diagnostics.Stopwatch.Frequency / 1000.0);
        int minDo = Mathf.Max(1, settings.minTransfersPerFrame);
        _sw.Restart();

        while (_jobs.Count > 0)
        {
            TransferJob j = _jobs.Peek();
            int did = 0;
            while (j.Cursor < j.Moves.Count)
            {
                ApplyMove(j.Moves[j.Cursor++]);
                if (++did >= minDo && _sw.ElapsedTicks >= budget) return;     // continue next frame
            }
            _jobs.Dequeue();
            Unlock(j.Locks);
            j.Done?.Invoke();                                                  // may enqueue new jobs
            if (_sw.ElapsedTicks >= budget) return;
        }
    }

    /// <summary>Military creation + AI refresh are batched: one AIManager refresh per frame, not one per nation.</summary>
    private void QueueRefresh(Nation n)
    {
        if (n == null) return;
        if (!_refresh.Contains(n)) _refresh.Add(n);
        _aiRefreshPending = true;
    }

    private void FlushRefresh()
    {
        if (!_aiRefreshPending) return;
        for (int i = 0; i < _refresh.Count; i++)
        {
            Nation n = _refresh[i];
            if (n != null && !n.IsEmpty && World.GetNation(n.Id) == n) Army.EnsureMilitary(n);
        }
        _refresh.Clear();
        _aiRefreshPending = false;
        if (AIManager.HasInstance) AIManager.Instance.RefreshNations();
    }

    #endregion

    #region Adapter (WorldManager)

    private Nation CreateNation(string name, Color color) => World.CreateNation(name, color);

    private void RemoveNation(Nation n)
    {
        if (n == null || !n.IsEmpty) return;
        World.RemoveNation(n.Id);
        if (map != null) map.RefreshCapitalMarkers();
    }

    private void SetCapital(Nation n, Province p)
    {
        if (n == null || p == null) return;
        World.SetCapital(n, p);
        if (map != null) map.RefreshCapitalMarkers();
    }

    #endregion

    #region Queries

    public Empire GetEmpire(Nation leader) => leader != null && _empires.TryGetValue(leader.Id, out Empire e) ? e : null;

    /// <summary>True while province moves of this nation are still running.</summary>
    public bool IsTransferring(Nation n) => IsBusy(n);

    private bool Fail(string m) { LastError = m; return false; }

    private double Power(Nation x)
    {
        Military m = Army.GetMilitary(x);
        return m == null ? 0.0 : m.CurrentManpower * (m.AttackPerMan.Value + m.DefendPerMan.Value);
    }

    private bool OccupiedByEnemy(Nation holder, Province p)
    {
        IReadOnlyList<War> wars = WM.GetWars(holder);
        for (int i = 0; i < wars.Count; i++)
        {
            War w = wars[i];
            if (!w.Occupied.TryGetValue(p.Id, out int occ)) continue;
            WarSide so = w.SideOf(occ), sh = w.SideOf(holder.Id);
            if (so != WarSide.None && so != sh) return true;
        }
        return false;
    }

    private void RemoveAllRegiments(Nation n)
    {
        Military mil = Army.GetMilitary(n);
        while (mil != null && mil.RemoveLastRegiment()) { }
    }

    #endregion

    #region Split (Burst partition)

    public List<Nation> Split(Nation n, int parts)
    {
        LastError = null;
        var result = new List<Nation>();
        if (n == null || n.IsEmpty) { Fail("Nothing to split."); return result; }
        if (IsBusy(n)) { Fail("Nation is still being rebuilt."); return result; }
        if (_empires.ContainsKey(n.Id)) { Fail("Dissolve the empire first."); return result; }
        if (WM.IsAtWar(n)) { Fail("End the war first."); return result; }
        if (map == null || !map.IsReady) { Fail("Map is not ready."); return result; }

        int count = n.Provinces.Count;
        parts = Mathf.Min(parts, count / Mathf.Max(1, settings.minPartProvinces));
        if (parts < 2) { Fail("Too few provinces."); return result; }

        _provBuf.Clear();
        _provBuf.AddRange(n.Provinces);
        EnsureLocal(map.Provinces.Count);
        for (int i = 0; i < count; i++) _local[_provBuf[i].Id] = i;

        // seeds (local indices): part 0 = capital (stays the original nation)
        var seeds = new int[parts];
        int seedCount = 0;
        int capLocal = n.Capital != null ? _local[n.Capital.Id] : -1;
        seeds[seedCount++] = capLocal >= 0 ? capLocal : RNG.GetInt(0, count);
        for (int t = 0; t < 200 && seedCount < parts; t++)
        {
            int c = RNG.GetInt(0, count);
            bool dup = false;
            for (int j = 0; j < seedCount; j++) if (seeds[j] == c) { dup = true; break; }
            if (!dup) seeds[seedCount++] = c;
        }
        parts = seedCount;
        if (parts < 2) { ResetLocal(count); Fail("Could not pick seeds."); return result; }

        // flattened adjacency (CSR) + Burst job
        int total = 0;
        for (int i = 0; i < count; i++) total += _provBuf[i].Neighbors.Length;

        var adjStart = new NativeArray<int>(count + 1, Allocator.TempJob);
        var adj = new NativeArray<int>(Mathf.Max(1, total), Allocator.TempJob);
        var partArr = new NativeArray<int>(count, Allocator.TempJob);
        var seedArr = new NativeArray<int>(parts, Allocator.TempJob);
        var weightArr = new NativeArray<float>(parts, Allocator.TempJob);

        int w = 0;
        for (int i = 0; i < count; i++)
        {
            adjStart[i] = w;
            int[] nb = _provBuf[i].Neighbors;
            for (int j = 0; j < nb.Length; j++)
            {
                int l = _local[nb[j]];
                if (l >= 0) adj[w++] = l;
            }
            partArr[i] = -1;
        }
        adjStart[count] = w;
        for (int k = 0; k < parts; k++) { seedArr[k] = seeds[k]; weightArr[k] = RNG.GetFloat(0.4f, 1.6f); }

        new SplitPartitionJob
        {
            adjStart = adjStart,
            adj = adj,
            seeds = seedArr,
            weight = weightArr,
            part = partArr,
            seed = (uint)RNG.GetInt(1, int.MaxValue)
        }.Schedule().Complete();

        int[] partOf = partArr.ToArray();
        adjStart.Dispose(); adj.Dispose(); partArr.Dispose(); seedArr.Dispose(); weightArr.Dispose();
        ResetLocal(count);

        // islands / disconnected leftovers: nearest seed
        Vector3[] seedPos = new Vector3[parts];
        for (int k = 0; k < parts; k++) seedPos[k] = Army.ProvinceWorldPos(_provBuf[seeds[k]]);
        for (int i = 0; i < count; i++)
        {
            if (partOf[i] >= 0) continue;
            Vector3 pos = Army.ProvinceWorldPos(_provBuf[i]);
            int best = 0; float bd = float.MaxValue;
            for (int k = 0; k < parts; k++)
            {
                float d = (seedPos[k] - pos).sqrMagnitude;
                if (d < bd) { bd = d; best = k; }
            }
            partOf[i] = best;
        }

        // create the new nations (part 0 stays the original), then queue the moves
        string[] titles = { "Kingdom of ", "Duchy of ", "Republic of ", "Principality of " };
        var created = new Nation[parts];
        var capProv = new Province[parts];
        created[0] = n;
        var locks = new List<int>(parts);
        Lock(locks, n);
        for (int k = 1; k < parts; k++)
        {
            capProv[k] = _provBuf[seeds[k]];
            Nation nn = CreateNation(titles[RNG.GetInt(0, titles.Length)] + capProv[k].Name,
                Color.HSVToRGB(RNG.GetFloat(0f, 1f), RNG.GetFloat(0.55f, 0.8f), RNG.GetFloat(0.65f, 0.9f)));
            if (nn == null) { Fail("CreateNation failed."); break; }
            created[k] = nn;
            Lock(locks, nn);
            result.Add(nn);
        }

        var moves = new List<Move>(count);
        for (int i = 0; i < count; i++)
        {
            int k = partOf[i];
            if (k == 0 || created[k] == null) continue;
            moves.Add(new Move(_provBuf[i], created[k]));
        }

        RunMoves(moves, locks, () =>
        {
            for (int k = 1; k < parts; k++)
            {
                if (created[k] == null) continue;
                SetCapital(created[k], capProv[k]);
                QueueRefresh(created[k]);
            }
            for (int a = 0; a < parts; a++)
                for (int b = a + 1; b < parts; b++)
                    if (created[a] != null && created[b] != null)
                        Rel.SetValue(created[a], created[b], RNG.GetFloat(settings.splitRelation.x, settings.splitRelation.y));
            OnNationSplit?.Invoke(n, result);
        });
        return result;
    }

    private void EnsureLocal(int size)
    {
        if (_local != null && _local.Length >= size) return;
        _local = new int[size];
        for (int i = 0; i < size; i++) _local[i] = -1;
    }

    private void ResetLocal(int count)
    {
        for (int i = 0; i < count; i++) _local[_provBuf[i].Id] = -1;
    }

    #endregion

    #region Form nation

    /// <summary>
    /// Leader absorbs the members (it keeps its name). Members vanish once their provinces are moved, but get land back later.
    /// Big operations finish over several frames: OnNationFormed is raised then.
    /// </summary>
    public bool Form(Nation leader, IList<Nation> members)
    {
        LastError = null;
        if (leader == null || leader.IsEmpty || members == null || members.Count == 0) return Fail("Nothing to form.");
        if (IsBusy(leader)) return Fail("The leader is still being rebuilt.");
        _empires.TryGetValue(leader.Id, out Empire e);
        if ((e != null ? e.Members.Count : 0) + members.Count > settings.maxMembers) return Fail("Empire would be too big.");

        for (int i = 0; i < members.Count; i++)
        {
            Nation m = members[i];
            if (m == null || m == leader || m.IsEmpty || World.GetNation(m.Id) != m) return Fail("Invalid member.");
            if (IsBusy(m)) return Fail(m.Name + " is still being rebuilt.");
            IReadOnlyList<War> wars = WM.GetWars(m);
            for (int w = 0; w < wars.Count; w++)
            {
                if (wars[w].IsMain(m.Id)) return Fail(m.Name + " leads a war.");
                if (wars[w].SideOf(m.Id) != wars[w].SideOf(leader.Id)) return Fail(m.Name + " fights a war the leader does not share.");
            }
        }

        if (e == null) { e = new Empire { LeaderId = leader.Id }; _empires[leader.Id] = e; }

        var absorbed = new List<Nation>(members.Count);
        var moves = new List<Move>();
        var locks = new List<int>(members.Count + 1);
        Lock(locks, leader);

        for (int i = 0; i < members.Count; i++)
        {
            Nation m = members[i];
            var rec = new EmpireMember
            {
                NationId = m.Id,
                Name = m.Name,
                Color = m.Color,
                Relation = Rel.GetValue(m, leader),
                CapitalId = m.Capital != null ? m.Capital.Id : -1
            };

            // a member that was itself an empire: its members come along, its own record keeps only its own land
            HashSet<int> subIds = null;
            if (_empires.TryGetValue(m.Id, out Empire sub))
            {
                _empires.Remove(m.Id);
                subIds = new HashSet<int>();
                for (int s = 0; s < sub.Members.Count; s++)
                {
                    List<int> ids = sub.Members[s].ProvinceIds;
                    for (int q = 0; q < ids.Count; q++) subIds.Add(ids[q]);
                    e.Members.Add(sub.Members[s]);
                }
            }

            for (int p = 0; p < m.Provinces.Count; p++)
            {
                Province pr = m.Provinces[p];
                if (subIds == null || !subIds.Contains(pr.Id)) rec.ProvinceIds.Add(pr.Id);
                moves.Add(new Move(pr, leader));
            }

            _warBuf.Clear();
            IReadOnlyList<War> mw = WM.GetWars(m);
            for (int w = 0; w < mw.Count; w++) _warBuf.Add(mw[w]);
            for (int w = 0; w < _warBuf.Count; w++) WM.LeaveWar(_warBuf[w], m);

            RemoveAllRegiments(m);
            e.Members.Add(rec);
            absorbed.Add(m);
            Lock(locks, m);
        }

        RunMoves(moves, locks, () =>
        {
            for (int i = 0; i < absorbed.Count; i++) RemoveNation(absorbed[i]);
            OnNationFormed?.Invoke(leader, absorbed);
        });
        return true;
    }

    #endregion

    #region Release / restore

    /// <summary>Gives the member its land back from 'holder'. Returns false when there is nothing to give. 'done' gets the nation when finished.</summary>
    private bool Restore(Empire e, EmpireMember m, Nation holder, Action<Nation> done)
    {
        var take = new List<Province>();
        for (int i = 0; i < m.ProvinceIds.Count; i++)
        {
            Province p = map.GetProvince(m.ProvinceIds[i]);
            if (p == null || p.Owner != holder || p == holder.Capital || OccupiedByEnemy(holder, p)) continue;
            take.Add(p);
        }
        if (take.Count == 0) return false;

        Nation n = World.GetNation(m.NationId);
        if (n == null || !n.IsEmpty) n = CreateNation(m.Name, m.Color);
        if (n == null) return false;
        m.NationId = n.Id;

        var locks = new List<int>(1);
        Lock(locks, n);

        Province cap = take[0];
        var moves = new List<Move>(take.Count);
        for (int i = 0; i < take.Count; i++)
        {
            if (take[i].Id == m.CapitalId) cap = take[i];
            moves.Add(new Move(take[i], n));
        }

        RunMoves(moves, locks, () =>
        {
            SetCapital(n, cap);
            QueueRefresh(n);
            done?.Invoke(n);
        });
        return true;
    }

    private void DropMember(Empire e, EmpireMember m)
    {
        e.Members.Remove(m);
        if (e.Members.Count == 0) _empires.Remove(e.LeaderId);
    }

    private bool Release(Empire e, EmpireMember m)
    {
        Nation leader = World.GetNation(e.LeaderId);
        if (leader == null) { DropMember(e, m); return false; }
        bool ok = Restore(e, m, leader, n =>
        {
            Rel.SetValue(n, leader, settings.releaseRelation);
            OnIndependenceGranted?.Invoke(leader, n);
        });
        DropMember(e, m);
        return ok;
    }

    public bool Dissolve(Nation leader)
    {
        LastError = null;
        Empire e = GetEmpire(leader);
        if (e == null) return Fail("Not an empire.");
        var copy = new List<EmpireMember>(e.Members);
        for (int i = 0; i < copy.Count; i++) Release(e, copy[i]);
        _empires.Remove(leader.Id);
        return true;
    }

    #endregion

    #region AI: ask a neighbour to join

    /// <summary>Called from AIManager.RunDiplomacy.</summary>
    public void AIThink(Nation n, float aggression, long today)
    {
        if (!settings.aiFormation || n == null || n.IsEmpty || map == null || IsBusy(n) || WM.IsAtWar(n)) return;
        Empire own = GetEmpire(n);
        if (own != null && own.Members.Count >= settings.maxMembers) return;
        if (RNG.GetFloat(0f, 1f) > settings.askChance * (0.4f + aggression)) return;

        double mine = Power(n);
        _nbBuf.Clear();
        for (int i = 0; i < n.Provinces.Count; i++)
        {
            int[] nb = n.Provinces[i].Neighbors;
            for (int j = 0; j < nb.Length; j++)
            {
                Province q = map.GetProvince(nb[j]);
                if (q != null && q.Owner != null && q.Owner != n) _nbBuf.Add(q.Owner);
            }
        }

        Nation target = null;
        double tp = 0.0;
        foreach (Nation o in _nbBuf)
        {
            if (o.IsEmpty || IsBusy(o) || WM.IsAtWar(o) || _empires.ContainsKey(o.Id)) continue;
            if (o.IsPlayer && !settings.aiMayAskPlayer) continue;
            if (_cooldown.TryGetValue(Key(n.Id, o.Id), out long until) && today < until) continue;
            if (Rel.GetValue(n, o) < settings.minRelationToAsk) continue;
            if (Rel.IsVassalOf(o, n) || Rel.IsVassalOf(n, o)) continue;
            double op = Power(o);
            if (op * settings.requiredPowerRatio > mine) continue;     // only clearly weaker neighbours
            if (target == null || op > tp) { target = o; tp = op; }    // the strongest of the weak = best prize
        }
        if (target == null) return;

        _cooldown[Key(n.Id, target.Id)] = today + settings.askCooldownDays;

        if (target.IsPlayer) { OnFormationRequestToPlayer?.Invoke(n, target); return; }

        float v = Rel.GetValue(n, target);
        float chance = Mathf.Clamp01(Mathf.InverseLerp(settings.minRelationToAsk, 100f, v));
        if (Rel.IsAllied(n, target)) chance = Mathf.Min(1f, chance + settings.allyBonus);
        if (v >= 100f) chance = 1f;

        if (RNG.GetFloat(0f, 1f) <= chance) Form(n, new[] { target });
        else
        {
            Rel.AddValue(n, target, -settings.refusePenalty);
            OnFormationRefused?.Invoke(n, target);
        }
    }

    #endregion

    #region Stability / independence / civil war

    private void HandleMonth()
    {
        if (_empires.Count == 0 || map == null) return;
        long today = DateManager.Instance.TotalDays;
        _monthBuf.Clear();
        _monthBuf.AddRange(_empires.Values);

        for (int i = 0; i < _monthBuf.Count; i++)
        {
            Empire e = _monthBuf[i];
            Nation L = World.GetNation(e.LeaderId);
            if (L == null) { _empires.Remove(e.LeaderId); continue; }

            bool war = WM.IsAtWar(L);
            Military mil = Army.GetMilitary(L);
            bool weak = mil != null && mil.ManpowerPercent < settings.weakArmyPercent;

            int total = 0, held = 0;
            for (int m = 0; m < e.Members.Count; m++)
            {
                List<int> ids = e.Members[m].ProvinceIds;
                for (int p = 0; p < ids.Count; p++)
                {
                    total++;
                    Province pr = map.GetProvince(ids[p]);
                    if (pr != null && pr.Owner == L) held++;
                }
            }
            float fraction = total > 0 ? held / (float)total : 1f;

            float drift = war ? settings.warDrift : settings.peaceDrift;
            if (weak) drift += settings.weakArmyDrift;
            if (L.Treasury < 0.0) drift -= 1f;
            if (fraction < 0.8f) drift -= (0.8f - fraction) * settings.lostLandFactor;
            drift += settings.sizeDrift * (e.Members.Count - 1);

            _memBuf.Clear();
            _memBuf.AddRange(e.Members);                               // Demand() edits e.Members
            for (int m = 0; m < _memBuf.Count; m++)
            {
                EmpireMember mem = _memBuf[m];
                if (mem.State != MemberState.Loyal) continue;
                mem.Relation = Mathf.Clamp(mem.Relation + drift + RNG.GetFloat(-settings.noise, settings.noise), -100f, 100f);
                if (mem.Relation < settings.demandBelow) Demand(e, mem, L, war, today);
            }
        }
    }

    private void Demand(Empire e, EmpireMember m, Nation leader, bool war, long today)
    {
        float chance = Mathf.Clamp01((m.Relation + 100f) / 200f) * (war ? settings.warGrantFactor : 1f);
        if (RNG.GetFloat(0f, 1f) <= chance) { Release(e, m); return; }

        m.Relation = settings.failedRelation;
        m.State = MemberState.Rebelling;
        m.RebelSince = today;
        OnIndependenceDenied?.Invoke(leader, m.Name);
    }

    private void HandleDay()
    {
        if (_empires.Count == 0) return;
        long today = DateManager.Instance.TotalDays;
        _dayBuf.Clear();
        _dayBuf.AddRange(_empires.Values);
        for (int i = 0; i < _dayBuf.Count; i++)
        {
            Empire e = _dayBuf[i];
            for (int m = e.Members.Count - 1; m >= 0; m--)
            {
                if (m >= e.Members.Count) continue;
                EmpireMember mem = e.Members[m];
                if (mem.State == MemberState.Rebelling && today - mem.RebelSince >= settings.rebelWarDelayDays)
                    DeclareIndependenceWar(e, mem);
            }
        }
    }

    /// <summary>The rebels take their land (still owned by the empire) and fight for it. The war starts when the land has moved.</summary>
    public bool DeclareIndependenceWar(Empire e, EmpireMember m)
    {
        LastError = null;
        Nation leader = e != null ? World.GetNation(e.LeaderId) : null;
        if (leader == null || m == null) return Fail("Invalid empire.");

        if (!Restore(e, m, leader, rebel => StartCivilWar(e, m, rebel, leader)))
        {
            DropMember(e, m);
            return Fail("Rebels have no land left.");
        }
        DropMember(e, m);
        return true;
    }

    private void StartCivilWar(Empire e, EmpireMember m, Nation rebel, Nation leader)
    {
        if (World.GetNation(leader.Id) != leader) return;                  // empire is gone: the rebels stay free
        War w = WM.ForceDeclareWar(rebel, leader);
        if (w == null) { Reannex(e, m, rebel, leader); return; }           // cannot fight: back under the empire
        _civil[w.Id] = new CivilWar { Empire = e, Rec = m, RebelId = rebel.Id };
    }

    private void HandleWarEnded(War w, WarResult result)
    {
        if (!_civil.TryGetValue(w.Id, out CivilWar c)) return;
        _civil.Remove(w.Id);

        Nation rebel = World.GetNation(c.RebelId);
        Nation leader = World.GetNation(c.Empire.LeaderId);
        if (rebel == null || leader == null) return;                      // gone: the rebels stay free

        if (result == WarResult.DefenderVictory) { Reannex(c.Empire, c.Rec, rebel, leader); return; }

        // rebels won (or white peace): on victory they also get back the rest of their land that is still free
        if (result == WarResult.AttackerVictory)
        {
            var moves = new List<Move>();
            for (int i = 0; i < c.Rec.ProvinceIds.Count; i++)
            {
                Province p = map.GetProvince(c.Rec.ProvinceIds[i]);
                if (p != null && p.Owner == leader && p != leader.Capital && !OccupiedByEnemy(leader, p)) moves.Add(new Move(p, rebel));
            }
            if (moves.Count > 0) RunMoves(moves, null, null);
        }
        OnIndependenceGranted?.Invoke(leader, rebel);
    }

    /// <summary>The empire won: everything the rebels hold goes back to the leader, then the rebel nation disappears.</summary>
    private void Reannex(Empire e, EmpireMember m, Nation rebel, Nation leader)
    {
        RemoveAllRegiments(rebel);

        var moves = new List<Move>(rebel.Provinces.Count);
        for (int i = 0; i < rebel.Provinces.Count; i++) moves.Add(new Move(rebel.Provinces[i], leader));

        m.State = MemberState.Loyal;
        m.Relation = 0f;
        if (!e.Members.Contains(m)) e.Members.Add(m);
        _empires[e.LeaderId] = e;

        var locks = new List<int>(1);
        Lock(locks, rebel);
        RunMoves(moves, locks, () => RemoveNation(rebel));
    }

    #endregion

    #region Context menu & diagnostics

    private Nation Pick(int id) => id >= 0 ? World.GetNation(id) : World.GetRandomNation();

    [ContextMenu("Nation: Split (A into debugParts)")]
    private void MenuSplit()
    {
        Nation a = Pick(debugNationA);
        List<Nation> r = Split(a, debugParts);
        Debug.Log($"[Formation] Split {(a != null ? a.Name : "?")}: {r.Count} new nation(s). {LastError}");
    }

    [ContextMenu("Nation: Form (A leader absorbs B)")]
    private void MenuForm()
    {
        Nation a = Pick(debugNationA), b = Pick(debugNationB);
        Debug.Log($"[Formation] Form: {(Form(a, new[] { b }) ? "OK" : "FAILED: " + LastError)}");
    }

    [ContextMenu("Nation: Dissolve Empire (A)")]
    private void MenuDissolve() => Debug.Log($"[Formation] Dissolve: {(Dissolve(Pick(debugNationA)) ? "OK" : "FAILED: " + LastError)}");

    [ContextMenu("Nation: Make All Members Unhappy (relation 0)")]
    private void MenuUnhappy()
    {
        foreach (Empire e in _empires.Values)
            for (int i = 0; i < e.Members.Count; i++) e.Members[i].Relation = 0f;
    }

    [ContextMenu("Nation: Log Pending Transfers")]
    private void MenuLog()
    {
        int pending = 0;
        foreach (TransferJob j in _jobs) pending += j.Moves.Count - j.Cursor;
        Debug.Log($"[Formation] jobs {_jobs.Count}, provinces left {pending}, locked nations {_busy.Count}, empires {_empires.Count}");
    }

    private void RefreshDiagnostics()
    {
#if UNITY_EDITOR
        int pending = 0;
        foreach (TransferJob j in _jobs) pending += j.Moves.Count - j.Cursor;
        diagnostics.empires = _empires.Count;
        diagnostics.pendingJobs = _jobs.Count;
        diagnostics.pendingProvinces = pending;
        diagnostics.lockedNations = _busy.Count;

        empiresView.Clear();
        foreach (var kv in _empires)
        {
            Nation leader = World.GetNation(kv.Key);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < kv.Value.Members.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                EmpireMember m = kv.Value.Members[i];
                sb.Append(m.Name).Append(" (").Append(m.Relation.ToString("F0")).Append(m.State == MemberState.Rebelling ? ", rebels)" : ")");
            }
            empiresView.Add(new EmpireInfo { leader = leader != null ? leader.Name : "#" + kv.Key, members = sb.ToString() });
        }
#endif
    }

    #endregion
}