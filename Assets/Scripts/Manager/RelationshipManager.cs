using System;
using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
//  DATA
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Derived from the relationship value: Enemy at or below the enemy threshold, Friend at or above the friend threshold.</summary>
public enum RelationStatus : byte { Enemy = 0, Neutral = 1, Friend = 2 }

[Flags]
public enum RelationFlags : byte
{
    None = 0,
    NonAggression = 1,
    DefensivePact = 2,
    Alliance = 4,
    Vassal = 8        // directional, see Relation.OverlordId
}

/// <summary>One symmetric relationship between two nations. A is always the lower nation id.</summary>
public sealed class Relation
{
    public readonly int A, B;
    /// <summary>-100 .. +100. The same value for both directions.</summary>
    public float Value { get; internal set; }
    public RelationFlags Flags { get; internal set; }
    /// <summary>+1 = improving every tick, -1 = damaging every tick, 0 = stable.</summary>
    public int Trend { get; internal set; }
    /// <summary>Nation id of the overlord when Flags has Vassal, otherwise -1.</summary>
    public int OverlordId { get; internal set; } = -1;
    internal bool Trending;      // registered in the manager's trending list

    internal Relation(int a, int b) { A = a; B = b; }
}

[Serializable]
public class RelationSettings
{
    [Tooltip("At or below this value the nations are Enemies.")]
    [Range(-100f, 0f)] public float enemyThreshold = -50f;
    [Tooltip("At or above this value the nations are Friends.")]
    [Range(0f, 100f)] public float friendThreshold = 50f;
    [Tooltip("Relationship points added / removed per tick while Improve / Damage is running.")]
    public float changePerTick = 0.25f;
    [Tooltip("Game-seconds per relationship tick (follows game speed and pause). 0 = every frame.")]
    [Min(0f)] public float tickInterval = 1f;
}

/// <summary>Read-only snapshot shown in the Inspector (Editor only).</summary>
[Serializable]
public class RelationInfo
{
    public string pair;
    public float value;
    public RelationStatus status;
    public RelationFlags flags;
    public int trend;
    public string overlord;
}

// ─────────────────────────────────────────────────────────────────────────────
//  RELATIONSHIP MANAGER  (global access: RelationshipManager.Instance)
//
//  - Relations are symmetric: A -> B is always the same as B -> A (one object per pair, created lazily).
//  - Value range -100 .. +100.
//  - Agreements: NonAggression, DefensivePact, Alliance, Vassal.
//  - Improve / Damage = a trend that changes the value by 'changePerTick' every relationship tick.
//  - Becoming an Enemy automatically cancels NonAggression / DefensivePact / Alliance (Vassal stays).
//  - Relations of a removed nation are deleted; ClearNations() clears everything.
// ─────────────────────────────────────────────────────────────────────────────

[DefaultExecutionOrder(-950)]
public class RelationshipManager : TickBehaviour
{
    public const float MinValue = -100f;
    public const float MaxValue = 100f;
    private const RelationFlags AgreementMask = RelationFlags.NonAggression | RelationFlags.DefensivePact | RelationFlags.Alliance;

    // ── singleton ──
    private static RelationshipManager _instance;
    private static bool _quitting;

    public static bool HasInstance => _instance != null;

    public static RelationshipManager Instance
    {
        get
        {
            if (_instance == null && !_quitting)
            {
#if UNITY_2022_2_OR_NEWER
                _instance = FindFirstObjectByType<RelationshipManager>();
#else
                _instance = FindObjectOfType<RelationshipManager>();
#endif
                if (_instance == null)
                    _instance = new GameObject("RelationshipManager").AddComponent<RelationshipManager>();
            }
            return _instance;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() { _instance = null; _quitting = false; }

    // ── settings ──
    public RelationSettings settings = new RelationSettings();

    [Header("Context menu helpers (right-click the component title)")]
    [Tooltip("Nation id used as A. -1 = random nation.  (Vassalize: A = overlord)")]
    [SerializeField] private int debugNationA = -1;
    [Tooltip("Nation id used as B. -1 = random nation.  (Vassalize: B = vassal)")]
    [SerializeField] private int debugNationB = -1;
    [Tooltip("Used by 'Add Value' (delta) and 'Set Value' (absolute).")]
    [SerializeField] private float debugAmount = 10f;

    [Header("Inspector view (read-only mirror, Editor only)")]
    [SerializeField] private bool showRelationsInInspector = true;
    [SerializeField, Min(1)] private int inspectorMaxRows = 100;
    [SerializeField] private List<RelationInfo> relationsView = new List<RelationInfo>();
    private float _nextInspectorRefresh;

    // ── data ──
    private readonly Dictionary<long, Relation> _relations = new Dictionary<long, Relation>();
    private readonly Dictionary<int, int> _overlordOf = new Dictionary<int, int>();   // vassal id -> overlord id
    private readonly List<Relation> _trending = new List<Relation>();
    private readonly List<long> _tmpKeys = new List<long>();
    private float _acc;

    public int RelationCount => _relations.Count;
    /// <summary>Why the last Make* / Set* call returned false.</summary>
    public string LastError { get; private set; }

    // ── events ──
    /// <summary>nationA, nationB, oldValue, newValue</summary>
    public event Action<Nation, Nation, float, float> OnRelationChanged;
    public event Action<Nation, Nation, RelationStatus> OnStatusChanged;
    /// <summary>Raised when alliance / pact / vassal flags change. Gives the NEW flags.</summary>
    public event Action<Nation, Nation, RelationFlags> OnAgreementChanged;

    protected override int TickPriority => -800;

    private WorldManager World => WorldManager.Instance;

    // ─────────────────────────────────────────────────────────────────────
    //  LIFECYCLE
    // ─────────────────────────────────────────────────────────────────────
    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Debug.LogWarning("Duplicate RelationshipManager removed.");
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
        WorldManager w = World;
        if (w == null) return;
        w.OnNationRemoved += HandleNationRemoved;
        w.OnNationsCleared += ClearAll;
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        if (WorldManager.HasInstance)
        {
            WorldManager.Instance.OnNationRemoved -= HandleNationRemoved;
            WorldManager.Instance.OnNationsCleared -= ClearAll;
        }
        if (_instance == this && _quitting) _instance = null;
    }

    private void HandleNationRemoved(Nation n)
    {
        if (n == null) return;
        int id = n.Id;

        _tmpKeys.Clear();
        foreach (var kv in _relations)
        {
            Relation r = kv.Value;
            if (r.A != id && r.B != id) continue;
            r.Trend = 0;                       // the trending list drops it on the next tick
            _tmpKeys.Add(kv.Key);
        }
        for (int i = 0; i < _tmpKeys.Count; i++) _relations.Remove(_tmpKeys[i]);

        _overlordOf.Remove(id);
        List<int> stale = null;
        foreach (var kv in _overlordOf)
            if (kv.Value == id) (stale ?? (stale = new List<int>())).Add(kv.Key);
        if (stale != null) for (int i = 0; i < stale.Count; i++) _overlordOf.Remove(stale[i]);
    }

    /// <summary>Deletes every relationship (also called automatically when the map is regenerated).</summary>
    public void ClearAll()
    {
        for (int i = 0; i < _trending.Count; i++) { _trending[i].Trend = 0; _trending[i].Trending = false; }
        _trending.Clear();
        _relations.Clear();
        _overlordOf.Clear();
        _acc = 0f;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  TICK
    // ─────────────────────────────────────────────────────────────────────
    public override void Tick(float deltaTime)
    {
        if (_trending.Count > 0)
        {
            float interval = settings.tickInterval;
            if (interval <= 0f) { _acc = 0f; ApplyTrendTick(); }
            else
            {
                _acc += deltaTime;
                int guard = 0;
                while (_acc >= interval && guard < 8) { _acc -= interval; ApplyTrendTick(); guard++; }
                if (guard >= 8) _acc = 0f;     // too far behind: drop the backlog
            }
        }
        else _acc = 0f;

#if UNITY_EDITOR
        if (showRelationsInInspector && Time.unscaledTime >= _nextInspectorRefresh)
        {
            _nextInspectorRefresh = Time.unscaledTime + 1f;
            RefreshView();
        }
#endif
    }

    private void ApplyTrendTick()
    {
        // backwards + swap-remove: safe while event handlers start / stop trends
        for (int i = _trending.Count - 1; i >= 0; i--)
        {
            Relation r = _trending[i];
            if (r.Trend == 0)
            {
                RemoveTrending(i, r);
                continue;
            }

            SetValueInternal(r, r.Value + r.Trend * settings.changePerTick);

            if ((r.Trend > 0 && r.Value >= MaxValue) || (r.Trend < 0 && r.Value <= MinValue))
            {
                r.Trend = 0;
                RemoveTrending(i, r);
            }
        }
    }

    private void RemoveTrending(int index, Relation r)
    {
        r.Trending = false;
        int last = _trending.Count - 1;
        _trending[index] = _trending[last];
        _trending.RemoveAt(last);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  QUERIES
    // ─────────────────────────────────────────────────────────────────────
    public RelationStatus StatusOf(float value)
    {
        if (value <= settings.enemyThreshold) return RelationStatus.Enemy;
        if (value >= settings.friendThreshold) return RelationStatus.Friend;
        return RelationStatus.Neutral;
    }

    public float GetValue(Nation a, Nation b) => TryGet(a, b, out Relation r) ? r.Value : 0f;
    public RelationStatus GetStatus(Nation a, Nation b) => StatusOf(GetValue(a, b));
    public RelationFlags GetFlags(Nation a, Nation b) => TryGet(a, b, out Relation r) ? r.Flags : RelationFlags.None;
    public Relation GetRelation(Nation a, Nation b) => TryGet(a, b, out Relation r) ? r : null;

    public bool IsEnemy(Nation a, Nation b) => GetStatus(a, b) == RelationStatus.Enemy;
    public bool IsFriend(Nation a, Nation b) => GetStatus(a, b) == RelationStatus.Friend;
    public bool IsAllied(Nation a, Nation b) => (GetFlags(a, b) & RelationFlags.Alliance) != 0;
    public bool HasDefensivePact(Nation a, Nation b) => (GetFlags(a, b) & RelationFlags.DefensivePact) != 0;
    public bool HasNonAggression(Nation a, Nation b) => (GetFlags(a, b) & RelationFlags.NonAggression) != 0;

    public bool IsVassalOf(Nation vassal, Nation overlord) =>
        vassal != null && overlord != null && _overlordOf.TryGetValue(vassal.Id, out int o) && o == overlord.Id;

    public Nation GetOverlord(Nation vassal) =>
        vassal != null && _overlordOf.TryGetValue(vassal.Id, out int o) ? World.GetNation(o) : null;

    // ─────────────────────────────────────────────────────────────────────
    //  VALUE  (instant)
    // ─────────────────────────────────────────────────────────────────────
    public bool SetValue(Nation a, Nation b, float value)
    {
        if (!Check(a, b)) return false;
        SetValueInternal(GetOrCreate(a, b), value);
        return true;
    }

    public bool AddValue(Nation a, Nation b, float delta)
    {
        if (!Check(a, b)) return false;
        Relation r = GetOrCreate(a, b);
        SetValueInternal(r, r.Value + delta);
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  IMPROVE / DAMAGE  (continuous, changePerTick every relationship tick)
    // ─────────────────────────────────────────────────────────────────────
    public bool ImproveRelationship(Nation a, Nation b) => SetTrend(a, b, +1);
    public bool DamageRelationship(Nation a, Nation b) => SetTrend(a, b, -1);
    public bool StopRelationshipChange(Nation a, Nation b) => SetTrend(a, b, 0);

    private bool SetTrend(Nation a, Nation b, int trend)
    {
        if (!Check(a, b)) return false;
        if (trend == 0 && !TryGet(a, b, out _)) return true;     // nothing to stop

        Relation r = GetOrCreate(a, b);
        r.Trend = trend;
        if (trend != 0 && !r.Trending) { r.Trending = true; _trending.Add(r); }
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  AGREEMENTS
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Alliance: raises the value to at least the friend threshold, replaces pacts, stops any Improve/Damage.</summary>
    public bool MakeAlly(Nation a, Nation b)
    {
        if (!Check(a, b)) return false;
        Relation r = GetOrCreate(a, b);
        if ((r.Flags & RelationFlags.Vassal) != 0) return Fail("An overlord and its vassal cannot be allies.");
        if ((r.Flags & RelationFlags.Alliance) != 0) return Fail("Already allied.");

        r.Trend = 0;
        SetValueInternal(r, Mathf.Max(r.Value, settings.friendThreshold));
        r.Flags = (r.Flags & ~(RelationFlags.DefensivePact | RelationFlags.NonAggression)) | RelationFlags.Alliance;
        RaiseAgreement(r);
        return true;
    }

    public bool BreakAlliance(Nation a, Nation b) => ClearFlag(a, b, RelationFlags.Alliance, "No alliance.");

    /// <summary>Enemy: drops the value to at most the enemy threshold, cancels alliance / pacts, stops any Improve/Damage.</summary>
    public bool MakeEnemy(Nation a, Nation b)
    {
        if (!Check(a, b)) return false;
        Relation r = GetOrCreate(a, b);

        r.Trend = 0;
        SetValueInternal(r, Mathf.Min(r.Value, settings.enemyThreshold));   // also cancels agreements via the Enemy rule
        if ((r.Flags & AgreementMask) != 0)
        {
            r.Flags &= ~AgreementMask;
            RaiseAgreement(r);
        }
        return true;
    }

    public bool MakeDefensivePact(Nation a, Nation b)
    {
        if (!Check(a, b)) return false;
        Relation r = GetOrCreate(a, b);
        if ((r.Flags & RelationFlags.Vassal) != 0) return Fail("An overlord and its vassal cannot sign pacts.");
        if ((r.Flags & RelationFlags.Alliance) != 0) return Fail("They are allies (an alliance already includes this).");
        if ((r.Flags & RelationFlags.DefensivePact) != 0) return Fail("Defensive pact already exists.");
        if (StatusOf(r.Value) == RelationStatus.Enemy) return Fail("Enemies cannot sign a pact.");

        r.Flags |= RelationFlags.DefensivePact;
        RaiseAgreement(r);
        return true;
    }

    public bool BreakDefensivePact(Nation a, Nation b) => ClearFlag(a, b, RelationFlags.DefensivePact, "No defensive pact.");

    public bool MakeNonAggressionPact(Nation a, Nation b)
    {
        if (!Check(a, b)) return false;
        Relation r = GetOrCreate(a, b);
        if ((r.Flags & RelationFlags.Vassal) != 0) return Fail("An overlord and its vassal cannot sign pacts.");
        if ((r.Flags & RelationFlags.Alliance) != 0) return Fail("They are allies (an alliance already includes this).");
        if ((r.Flags & RelationFlags.NonAggression) != 0) return Fail("Non-aggression pact already exists.");
        if (StatusOf(r.Value) == RelationStatus.Enemy) return Fail("Enemies cannot sign a pact.");

        r.Flags |= RelationFlags.NonAggression;
        RaiseAgreement(r);
        return true;
    }

    public bool BreakNonAggressionPact(Nation a, Nation b) => ClearFlag(a, b, RelationFlags.NonAggression, "No non-aggression pact.");

    /// <summary>Makes 'vassal' a vassal of 'overlord'. Cancels alliance / pacts, lifts the value to at least 0 (never Enemy).</summary>
    public bool Vassalize(Nation overlord, Nation vassal)
    {
        if (!Check(overlord, vassal)) return false;
        if (_overlordOf.TryGetValue(vassal.Id, out int current))
            return Fail(current == overlord.Id ? "Already its vassal." : "The vassal already has another overlord.");

        // no cycles: walking up from the overlord must never reach the vassal
        int walk = overlord.Id;
        while (_overlordOf.TryGetValue(walk, out int up))
        {
            if (up == vassal.Id) return Fail("The overlord is itself a (sub-)vassal of that nation.");
            walk = up;
        }

        Relation r = GetOrCreate(overlord, vassal);
        SetValueInternal(r, Mathf.Max(r.Value, 0f));
        r.Flags = (r.Flags & ~AgreementMask) | RelationFlags.Vassal;
        r.OverlordId = overlord.Id;
        _overlordOf[vassal.Id] = overlord.Id;
        RaiseAgreement(r);
        return true;
    }

    public bool ReleaseVassal(Nation overlord, Nation vassal)
    {
        if (!Check(overlord, vassal)) return false;
        if (!IsVassalOf(vassal, overlord)) return Fail("That nation is not a vassal of the overlord.");

        Relation r = GetOrCreate(overlord, vassal);
        r.Flags &= ~RelationFlags.Vassal;
        r.OverlordId = -1;
        _overlordOf.Remove(vassal.Id);
        RaiseAgreement(r);
        return true;
    }

    private bool ClearFlag(Nation a, Nation b, RelationFlags flag, string missing)
    {
        if (!Check(a, b)) return false;
        if (!TryGet(a, b, out Relation r) || (r.Flags & flag) == 0) return Fail(missing);
        r.Flags &= ~flag;
        RaiseAgreement(r);
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  INTERNAL
    // ─────────────────────────────────────────────────────────────────────
    private static long Key(int a, int b) =>
        a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    private bool TryGet(Nation a, Nation b, out Relation r)
    {
        r = null;
        if (a == null || b == null || a == b) return false;
        return _relations.TryGetValue(Key(a.Id, b.Id), out r);
    }

    private Relation GetOrCreate(Nation a, Nation b)
    {
        long key = Key(a.Id, b.Id);
        if (!_relations.TryGetValue(key, out Relation r))
        {
            r = a.Id < b.Id ? new Relation(a.Id, b.Id) : new Relation(b.Id, a.Id);
            _relations.Add(key, r);
        }
        return r;
    }

    private bool Check(Nation a, Nation b)
    {
        LastError = null;
        if (a == null || b == null) return Fail("Nation is null.");
        if (a == b) return Fail("A nation cannot have a relationship with itself.");
        WorldManager w = World;
        if (w == null || w.GetNation(a.Id) != a || w.GetNation(b.Id) != b) return Fail("Nation is not registered in the WorldManager.");
        return true;
    }

    private bool Fail(string message)
    {
        LastError = message;
        return false;
    }

    private void SetValueInternal(Relation r, float value)
    {
        value = Mathf.Clamp(value, MinValue, MaxValue);
        float old = r.Value;
        if (Mathf.Approximately(old, value)) return;

        RelationStatus oldStatus = StatusOf(old);
        r.Value = value;
        RelationStatus newStatus = StatusOf(value);

        Nation na = World.GetNation(r.A), nb = World.GetNation(r.B);
        bool both = na != null && nb != null;
        if (both) OnRelationChanged?.Invoke(na, nb, old, value);

        if (newStatus == oldStatus) return;

        if (newStatus == RelationStatus.Enemy && (r.Flags & AgreementMask) != 0)
        {
            r.Flags &= ~AgreementMask;           // enemies cannot stay allied / keep pacts
            if (both) OnAgreementChanged?.Invoke(na, nb, r.Flags);
        }
        if (both) OnStatusChanged?.Invoke(na, nb, newStatus);
    }

    private void RaiseAgreement(Relation r)
    {
        Nation na = World.GetNation(r.A), nb = World.GetNation(r.B);
        if (na != null && nb != null) OnAgreementChanged?.Invoke(na, nb, r.Flags);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  CONTEXT MENU  (Inspector, play mode, after the map is generated)
    //   Uses debugNationA / debugNationB (-1 = random). Results are logged to the Console.
    // ─────────────────────────────────────────────────────────────────────
    [ContextMenu("Rel: Improve Relationship (start)")]
    private void MenuImprove() => RunMenu("Improve", ImproveRelationship);

    [ContextMenu("Rel: Damage Relationship (start)")]
    private void MenuDamage() => RunMenu("Damage", DamageRelationship);

    [ContextMenu("Rel: Stop Relationship Change")]
    private void MenuStop() => RunMenu("Stop change", StopRelationshipChange);

    [ContextMenu("Rel: Add Value (debugAmount)")]
    private void MenuAdd() => RunMenu("Add " + debugAmount, (a, b) => AddValue(a, b, debugAmount));

    [ContextMenu("Rel: Set Value (debugAmount)")]
    private void MenuSet() => RunMenu("Set " + debugAmount, (a, b) => SetValue(a, b, debugAmount));

    [ContextMenu("Rel: Make Ally")]
    private void MenuAlly() => RunMenu("Make Ally", MakeAlly);

    [ContextMenu("Rel: Break Alliance")]
    private void MenuBreakAlly() => RunMenu("Break Alliance", BreakAlliance);

    [ContextMenu("Rel: Make Enemy")]
    private void MenuEnemy() => RunMenu("Make Enemy", MakeEnemy);

    [ContextMenu("Rel: Defensive Pact")]
    private void MenuDefPact() => RunMenu("Defensive Pact", MakeDefensivePact);

    [ContextMenu("Rel: Break Defensive Pact")]
    private void MenuBreakDefPact() => RunMenu("Break Defensive Pact", BreakDefensivePact);

    [ContextMenu("Rel: Non-Aggression Pact")]
    private void MenuNonAgg() => RunMenu("Non-Aggression Pact", MakeNonAggressionPact);

    [ContextMenu("Rel: Break Non-Aggression Pact")]
    private void MenuBreakNonAgg() => RunMenu("Break Non-Aggression Pact", BreakNonAggressionPact);

    [ContextMenu("Rel: Vassalize (A = overlord, B = vassal)")]
    private void MenuVassalize() => RunMenu("Vassalize", Vassalize);

    [ContextMenu("Rel: Release Vassal (A = overlord, B = vassal)")]
    private void MenuRelease() => RunMenu("Release Vassal", ReleaseVassal);

    [ContextMenu("Rel: Log Relation")]
    private void MenuLog() => RunMenu("Log", (a, b) => true);

    [ContextMenu("Rel: Clear All Relations")]
    private void MenuClear() { ClearAll(); RefreshView(); Debug.Log("[Relations] Cleared."); }

    private void RunMenu(string label, Func<Nation, Nation, bool> action)
    {
        if (!PickPair(out Nation a, out Nation b)) return;
        bool ok = action(a, b);
        Debug.Log($"[Relations] {label}: {a.Name} (#{a.Id}) <-> {b.Name} (#{b.Id}) -> " +
                  (ok ? "OK" : "FAILED: " + LastError) + " | " + Describe(a, b));
        RefreshView();
    }

    private bool PickPair(out Nation a, out Nation b)
    {
        a = b = null;
        WorldManager w = World;
        if (w == null || w.NationCount < 2)
        {
            Debug.LogWarning("[Relations] Need at least 2 nations (generate the map first).");
            return false;
        }

        a = debugNationA >= 0 ? w.GetNation(debugNationA) : w.GetRandomNation();
        if (a == null) { Debug.LogWarning("[Relations] Nation A not found."); return false; }

        if (debugNationB >= 0) b = w.GetNation(debugNationB);
        else
            for (int tries = 0; tries < 32 && (b == null || b == a); tries++) b = w.GetRandomNation();

        if (b == null || b == a) { Debug.LogWarning("[Relations] Nation B not found (or equals A)."); return false; }
        return true;
    }

    private string Describe(Nation a, Nation b)
    {
        TryGet(a, b, out Relation r);
        float v = r != null ? r.Value : 0f;
        string s = $"value {v:F2} ({StatusOf(v)}), flags {(r != null ? r.Flags : RelationFlags.None)}, trend {(r != null ? r.Trend : 0)}";
        if (r != null && r.OverlordId >= 0)
        {
            Nation o = World.GetNation(r.OverlordId);
            s += ", overlord " + (o != null ? o.Name : "#" + r.OverlordId);
        }
        return s;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  INSPECTOR VIEW
    // ─────────────────────────────────────────────────────────────────────
    private void RefreshView()
    {
#if UNITY_EDITOR
        relationsView.Clear();
        if (!showRelationsInInspector) return;

        WorldManager w = World;
        foreach (Relation r in _relations.Values)
        {
            if (relationsView.Count >= inspectorMaxRows) break;
            Nation na = w.GetNation(r.A), nb = w.GetNation(r.B);
            Nation o = r.OverlordId >= 0 ? w.GetNation(r.OverlordId) : null;
            relationsView.Add(new RelationInfo
            {
                pair = (na != null ? na.Name : "#" + r.A) + " <-> " + (nb != null ? nb.Name : "#" + r.B),
                value = r.Value,
                status = StatusOf(r.Value),
                flags = r.Flags,
                trend = r.Trend,
                overlord = o != null ? o.Name : ""
            });
        }
#endif
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }
}