using System;
using System.Collections.Generic;
using UnityEngine;

#region Data

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
    public readonly Nation NationA, NationB;          // cached: removed nations delete their relations
    /// <summary>-100 .. +100. The same value for both directions.</summary>
    public float Value { get; internal set; }
    public RelationFlags Flags { get; internal set; }
    /// <summary>+1 = improving every tick (up to improveMaxValue), -1 = damaging every tick, 0 = stable.</summary>
    public int Trend { get; internal set; }
    /// <summary>Nation id of the overlord when Flags has Vassal, otherwise -1.</summary>
    public int OverlordId { get; internal set; } = -1;
    internal bool Trending;      // registered in the manager's trending list

    internal Relation(Nation a, Nation b)
    {
        bool swap = a.Id > b.Id;
        NationA = swap ? b : a; NationB = swap ? a : b;
        A = NationA.Id; B = NationB.Id;
    }
}

[Serializable]
public class RelationSettings
{
    [Tooltip("At or below this value the nations are Enemies.")]
    [Range(-100f, 0f)] public float enemyThreshold = -50f;
    [Tooltip("At or above this value the nations are Friends.")]
    [Range(0f, 100f)] public float friendThreshold = 50f;
    [Tooltip("Improve (the continuous trend) can never raise a relationship above this. Other ways (alliance, Set / Add) can still reach 100.")]
    [Range(0f, 100f)] public float improveMaxValue = 50f;
    [Tooltip("Relationship points added / removed per tick while Improve / Damage is running.")]
    public float changePerTick = 0.25f;
    [Tooltip("Game-seconds per relationship tick (follows game speed and pause). 0 = every frame.")]
    [Min(0f)] public float tickInterval = 1f;
}

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

#endregion

// ─────────────────────────────────────────────────────────────────────────────
//  RELATIONSHIP MANAGER  (RelationshipManager.Instance)
//  - One symmetric Relation per pair (A -> B == B -> A), created lazily. Value -100 .. +100.
//  - Agreements: NonAggression, DefensivePact, Alliance, Vassal.
//  - Improve / Damage = a trend per relationship tick. Improve stops at settings.improveMaxValue (50).
//  - Becoming an Enemy cancels NonAggression / DefensivePact / Alliance (Vassal stays).
//  - Relations of a removed nation are deleted through a per-nation index (no full scan).
// ─────────────────────────────────────────────────────────────────────────────
[DefaultExecutionOrder(-950)]
public class RelationshipManager : TickBehaviour
{
    public const float MinValue = -100f;
    public const float MaxValue = 100f;
    private const RelationFlags AgreementMask = RelationFlags.NonAggression | RelationFlags.DefensivePact | RelationFlags.Alliance;

    #region Singleton

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

    #endregion

    #region Inspector, data & events

    public RelationSettings settings = new RelationSettings();

    [Header("Context menu helpers (right-click the component title)")]
    [SerializeField] private int debugNationA = -1;
    [SerializeField] private int debugNationB = -1;
    [SerializeField] private float debugAmount = 10f;

    [Header("Diagnostics (Editor only, throttled)")]
    [SerializeField] private bool showRelationsInInspector = true;
    [Min(0.25f)] public float diagnosticsInterval = 1f;
    [SerializeField, Min(1)] private int inspectorMaxRows = 100;
    [SerializeField] private List<RelationInfo> relationsView = new List<RelationInfo>();
    private float _nextInspectorRefresh;

    private readonly Dictionary<long, Relation> _relations = new Dictionary<long, Relation>();
    private readonly Dictionary<int, List<Relation>> _byNation = new Dictionary<int, List<Relation>>();   // nation id -> its relations
    private readonly Dictionary<int, int> _overlordOf = new Dictionary<int, int>();                       // vassal id -> overlord id
    private readonly List<Relation> _trending = new List<Relation>();
    private readonly List<Relation> _relBuf = new List<Relation>();
    private readonly List<int> _intBuf = new List<int>();
    private float _acc;

    public int RelationCount => _relations.Count;
    public string LastError { get; private set; }

    /// <summary>nationA, nationB, oldValue, newValue</summary>
    public event Action<Nation, Nation, float, float> OnRelationChanged;
    public event Action<Nation, Nation, RelationStatus> OnStatusChanged;
    /// <summary>Raised when alliance / pact / vassal flags change. Gives the NEW flags.</summary>
    public event Action<Nation, Nation, RelationFlags> OnAgreementChanged;

    protected override int TickPriority => -800;

    private WorldManager World => WorldManager.Instance;

    #endregion

    #region Lifecycle

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

    private void OnDestroy() { if (_instance == this) _instance = null; }

    /// <summary>Only the relations of the removed nation are touched (per-nation index).</summary>
    private void HandleNationRemoved(Nation n)
    {
        if (n == null) return;
        int id = n.Id;

        if (_byNation.TryGetValue(id, out List<Relation> list))
        {
            _relBuf.Clear();
            _relBuf.AddRange(list);
            for (int i = 0; i < _relBuf.Count; i++)
            {
                Relation r = _relBuf[i];
                r.Trend = 0;                                   // the trending list drops it on the next tick
                _relations.Remove(Key(r.A, r.B));
                int other = r.A == id ? r.B : r.A;
                if (_byNation.TryGetValue(other, out List<Relation> ol))
                {
                    ol.Remove(r);
                    if (ol.Count == 0) _byNation.Remove(other);
                }
            }
            _byNation.Remove(id);
        }

        _overlordOf.Remove(id);
        _intBuf.Clear();
        foreach (var kv in _overlordOf) if (kv.Value == id) _intBuf.Add(kv.Key);
        for (int i = 0; i < _intBuf.Count; i++) _overlordOf.Remove(_intBuf[i]);
    }

    /// <summary>Deletes every relationship (also called when the map is regenerated).</summary>
    public void ClearAll()
    {
        for (int i = 0; i < _trending.Count; i++) { _trending[i].Trend = 0; _trending[i].Trending = false; }
        _trending.Clear();
        _relations.Clear();
        _byNation.Clear();
        _overlordOf.Clear();
        _acc = 0f;
    }

    #endregion

    #region Tick

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
                while (_acc >= interval && guard < 8 && _trending.Count > 0) { _acc -= interval; ApplyTrendTick(); guard++; }
                if (guard >= 8) _acc = 0f;     // too far behind: drop the backlog
            }
        }
        else _acc = 0f;

#if UNITY_EDITOR
        if (showRelationsInInspector && Time.unscaledTime >= _nextInspectorRefresh)
        {
            _nextInspectorRefresh = Time.unscaledTime + Mathf.Max(0.25f, diagnosticsInterval);
            RefreshView();
        }
#endif
    }

    private void ApplyTrendTick()
    {
        float step = settings.changePerTick;
        float cap = Mathf.Clamp(settings.improveMaxValue, MinValue, MaxValue);

        // backwards + swap-remove: safe while event handlers start / stop trends
        for (int i = _trending.Count - 1; i >= 0; i--)
        {
            if (i >= _trending.Count) continue;
            Relation r = _trending[i];
            if (r.Trend == 0) { RemoveTrending(i, r); continue; }

            if (r.Trend > 0)
            {
                if (r.Value >= cap) { r.Trend = 0; RemoveTrending(i, r); continue; }     // improve never passes the cap
                SetValueInternal(r, Mathf.Min(cap, r.Value + step));
                if (r.Value >= cap) { r.Trend = 0; RemoveTrending(i, r); }
            }
            else
            {
                SetValueInternal(r, r.Value - step);
                if (r.Value <= MinValue) { r.Trend = 0; RemoveTrending(i, r); }
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

    #endregion

    #region Queries

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

    /// <summary>True when Improve can still raise this relationship (value below improveMaxValue).</summary>
    public bool CanImprove(Nation a, Nation b) => GetValue(a, b) < settings.improveMaxValue;

    /// <summary>Fills 'result' with every nation that has a stored relation with n.</summary>
    public void GetRelated(Nation n, List<Relation> result)
    {
        result.Clear();
        if (n != null && _byNation.TryGetValue(n.Id, out List<Relation> l)) result.AddRange(l);
    }

    #endregion

    #region Value (instant, can reach -100 .. +100)

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

    #endregion

    #region Improve / Damage (continuous)

    /// <summary>Raises the value every tick, but only up to improveMaxValue (50). Fails when already at / above it.</summary>
    public bool ImproveRelationship(Nation a, Nation b)
    {
        if (!Check(a, b)) return false;
        if (GetValue(a, b) >= settings.improveMaxValue)
            return Fail("Relationship cannot be improved above " + settings.improveMaxValue.ToString("F0") + ".");
        return SetTrend(a, b, +1);
    }

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

    #endregion

    #region Agreements

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
        SetValueInternal(r, Mathf.Min(r.Value, settings.enemyThreshold));   // the Enemy rule already cancels agreements
        if ((r.Flags & AgreementMask) != 0)
        {
            r.Flags &= ~AgreementMask;
            RaiseAgreement(r);
        }
        return true;
    }

    public bool MakeDefensivePact(Nation a, Nation b) => MakePact(a, b, RelationFlags.DefensivePact, "Defensive pact already exists.");
    public bool BreakDefensivePact(Nation a, Nation b) => ClearFlag(a, b, RelationFlags.DefensivePact, "No defensive pact.");
    public bool MakeNonAggressionPact(Nation a, Nation b) => MakePact(a, b, RelationFlags.NonAggression, "Non-aggression pact already exists.");
    public bool BreakNonAggressionPact(Nation a, Nation b) => ClearFlag(a, b, RelationFlags.NonAggression, "No non-aggression pact.");

    private bool MakePact(Nation a, Nation b, RelationFlags flag, string exists)
    {
        if (!Check(a, b)) return false;
        Relation r = GetOrCreate(a, b);
        if ((r.Flags & RelationFlags.Vassal) != 0) return Fail("An overlord and its vassal cannot sign pacts.");
        if ((r.Flags & RelationFlags.Alliance) != 0) return Fail("They are allies (an alliance already includes this).");
        if ((r.Flags & flag) != 0) return Fail(exists);
        if (StatusOf(r.Value) == RelationStatus.Enemy) return Fail("Enemies cannot sign a pact.");

        r.Flags |= flag;
        RaiseAgreement(r);
        return true;
    }

    /// <summary>Makes 'vassal' a vassal of 'overlord'. Cancels alliance / pacts, lifts the value to at least 0 (never Enemy).</summary>
    public bool Vassalize(Nation overlord, Nation vassal)
    {
        if (!Check(overlord, vassal)) return false;
        if (_overlordOf.TryGetValue(vassal.Id, out int current))
            return Fail(current == overlord.Id ? "Already its vassal." : "The vassal already has another overlord.");

        int walk = overlord.Id;                                   // no cycles
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

    #endregion

    #region Internal

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
        if (_relations.TryGetValue(key, out Relation r)) return r;

        r = new Relation(a, b);
        _relations.Add(key, r);
        Index(a.Id, r);
        Index(b.Id, r);
        return r;
    }

    private void Index(int nationId, Relation r)
    {
        if (!_byNation.TryGetValue(nationId, out List<Relation> l)) _byNation[nationId] = l = new List<Relation>(4);
        l.Add(r);
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

    private bool Fail(string message) { LastError = message; return false; }

    private void SetValueInternal(Relation r, float value)
    {
        value = Mathf.Clamp(value, MinValue, MaxValue);
        float old = r.Value;
        if (Mathf.Approximately(old, value)) return;

        RelationStatus oldStatus = StatusOf(old);
        r.Value = value;
        RelationStatus newStatus = StatusOf(value);

        OnRelationChanged?.Invoke(r.NationA, r.NationB, old, value);
        if (newStatus == oldStatus) return;

        if (newStatus == RelationStatus.Enemy && (r.Flags & AgreementMask) != 0)
        {
            r.Flags &= ~AgreementMask;           // enemies cannot stay allied / keep pacts
            OnAgreementChanged?.Invoke(r.NationA, r.NationB, r.Flags);
        }
        OnStatusChanged?.Invoke(r.NationA, r.NationB, newStatus);
    }

    private void RaiseAgreement(Relation r) => OnAgreementChanged?.Invoke(r.NationA, r.NationB, r.Flags);

    #endregion

    #region Context menu (-1 = random nation)

    [ContextMenu("Rel: Improve Relationship (start, up to improveMaxValue)")]
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
        if (w == null || w.NationCount < 2) { Debug.LogWarning("[Relations] Need at least 2 nations (generate the map first)."); return false; }

        a = debugNationA >= 0 ? w.GetNation(debugNationA) : w.GetRandomNation();
        if (a == null) { Debug.LogWarning("[Relations] Nation A not found."); return false; }

        if (debugNationB >= 0) b = w.GetNation(debugNationB);
        else for (int t = 0; t < 32 && (b == null || b == a); t++) b = w.GetRandomNation();

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

    #endregion

    #region Diagnostics (Editor mirror, throttled by diagnosticsInterval)

    private void RefreshView()
    {
#if UNITY_EDITOR
        relationsView.Clear();
        if (!showRelationsInInspector) return;

        WorldManager w = World;
        foreach (Relation r in _relations.Values)
        {
            if (relationsView.Count >= inspectorMaxRows) break;
            Nation o = r.OverlordId >= 0 ? w.GetNation(r.OverlordId) : null;
            relationsView.Add(new RelationInfo
            {
                pair = r.NationA.Name + " <-> " + r.NationB.Name,
                value = r.Value,
                status = StatusOf(r.Value),
                flags = r.Flags,
                trend = r.Trend,
                overlord = o != null ? o.Name : ""
            });
        }
#endif
    }

    #endregion
}