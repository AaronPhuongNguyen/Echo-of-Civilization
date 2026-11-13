using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
//  MILITARY  (one per nation, created and owned by ArmyManager)
//
//  Manpower model:
//   - MaxManpower     = Nation.Population * Manpower.Value
//   - CurrentManpower = men the nation has (reserve + men inside regiments).
//   - Preparation     = how much of MaxManpower is mobilized: level 1..10 = 10%..100%.
//   - MobilizedCap    = min(CurrentManpower, MaxManpower * CurrentPreparation)
//   - Regiment share  = automatic equal split, or a custom percent.
//   - Regiment max    = MobilizedCap * Share.
// ─────────────────────────────────────────────────────────────────────────────
public sealed class Military
{
    public readonly Nation Nation;

    /// <summary>Share of the population that can be drafted (Base 0.5 = 50%).</summary>
    public readonly GameValue Manpower = new GameValue(0.5f);

    public readonly List<Regiment> Regiments = new List<Regiment>();

    public string LastError { get; private set; }

    public double CurrentManpower { get; internal set; }
    public double MaxManpower => Nation.Population * Manpower.Value;

    // ── combat stats (GameValue: flat / percent / total modifiers work) ──
    public readonly GameValue AttackPerMan = new GameValue(5f);
    public readonly GameValue DefendPerMan = new GameValue(6.5f);
    public readonly GameValue HpPerMan = new GameValue(5f);
    /// <summary>Multiplier of all damage this nation deals.</summary>
    public readonly GameValue DamageMultiplier = new GameValue(1f);

    /// <summary>Reinforcement columns on their way to the front (their men count as already used).</summary>
    public readonly List<Regiment> Columns = new List<Regiment>();
    public double ManpowerPercent { get { double m = MaxManpower; return m > 0.0 ? CurrentManpower / m * 100.0 : 0.0; } }

    public int PreparationLevel { get; private set; }
    public float CurrentPreparation { get; internal set; }
    public float TargetPreparation => PreparationLevel * 10f;

    public double MobilizedCap => Math.Min(CurrentManpower, MaxManpower * CurrentPreparation / 100.0);

    public int RegimentCount => Regiments.Count;

    internal Military(Nation nation, int preparationLevel)
    {
        Nation = nation;
        PreparationLevel = Mathf.Clamp(preparationLevel, 1, 10);
        CurrentPreparation = TargetPreparation;
    }

    public double TotalRegimentManpower()
    {
        double sum = 0.0;
        for (int i = 0; i < Regiments.Count; i++) sum += Regiments[i].Manpower;
        for (int i = 0; i < Columns.Count; i++) sum += Columns[i].Manpower;
        return sum;
    }

    // ── preparation ──
    public void SetPreparationLevel(int level)
    {
        level = Mathf.Clamp(level, 1, 10);
        if (level == PreparationLevel) return;
        PreparationLevel = level;
        if (ArmyManager.HasInstance) ArmyManager.Instance.MarkPreparationDirty();
    }

    // ── regiments ──
    public Regiment AddRegiment(Province at = null)
    {
        LastError = null;
        if (!ArmyManager.HasInstance) { LastError = "No ArmyManager."; return null; }
        ArmyManager army = ArmyManager.Instance;

        Regiment r = army.SpawnRegiment(this, at);
        if (r == null) { LastError = "Could not create a regiment (no province)."; return null; }

        Regiments.Add(r);
        army.RefreshStacks(this);
        RecalculateShares();
        ClampRegiments();

        double free = Math.Max(0.0, CurrentManpower - TotalRegimentManpower());
        r.Manpower = Math.Min(r.Allocation, free);
        r.RefreshText();

        army.RaiseCreated(r);
        return r;
    }

    internal void AddRegiments(int count)
    {
        LastError = null;
        if (count <= 0 || !ArmyManager.HasInstance) return;
        ArmyManager army = ArmyManager.Instance;

        int first = Regiments.Count;
        for (int i = 0; i < count; i++)
        {
            Regiment r = army.SpawnRegiment(this, null);
            if (r == null) break;
            Regiments.Add(r);                              // added at once so the next one lands in another province
        }

        army.RefreshStacks(this);
        RecalculateShares();
        ClampRegiments();

        double free = Math.Max(0.0, CurrentManpower - TotalRegimentManpower());
        for (int i = first; i < Regiments.Count; i++)
        {
            Regiment r = Regiments[i];
            double give = Math.Min(r.Allocation, free);
            r.Manpower = give;
            free -= give;
            r.RefreshText();
            army.RaiseCreated(r);
        }
    }

    public bool RemoveRegiment(Regiment r)
    {
        LastError = null;
        if (r == null || !Regiments.Remove(r)) { LastError = "Regiment is not part of this military."; return false; }
        ArmyManager.Instance.RaiseRemoved(r, false);
        ArmyManager.Instance.ReleaseRegiment(r);
        ArmyManager.Instance.RefreshStacks(this);
        RecalculateShares();
        return true;
    }

    internal void DestroyRegiment(Regiment r)
    {
        if (!Regiments.Remove(r)) return;
        CurrentManpower = Math.Max(0.0, CurrentManpower - r.Manpower);
        ArmyManager.Instance.RaiseRemoved(r, true);
        ArmyManager.Instance.ReleaseRegiment(r);
        ArmyManager.Instance.RefreshStacks(this);
        RecalculateShares();
    }

    public bool RemoveLastRegiment() => Regiments.Count > 0 && RemoveRegiment(Regiments[Regiments.Count - 1]);

    /// <summary>Creates a reinforcement column with 'men' men (taken from the free manpower) at a province.</summary>
    internal Regiment AddColumn(Province at, double men)
    {
        if (!ArmyManager.HasInstance || at == null || men <= 0.0) return null;
        Regiment c = ArmyManager.Instance.SpawnRegiment(this, at);
        if (c == null) return null;
        c.IsColumn = true;
        c.Share = 0f;
        c.Manpower = men;
        Columns.Add(c);
        return c;
    }

    /// <summary>Removes a column; its men go back to the reserve (they stay in CurrentManpower).</summary>
    internal void RemoveColumn(Regiment c)
    {
        if (!Columns.Remove(c)) return;
        ArmyManager.Instance.ReleaseRegiment(c);
    }

    /// <summary>A column was wiped out: its men are lost.</summary>
    public void DestroyColumn(Regiment c)
    {
        if (!Columns.Remove(c)) return;
        CurrentManpower = Math.Max(0.0, CurrentManpower - c.Manpower);
        ArmyManager.Instance.ReleaseRegiment(c);
    }

    public bool SetRegimentShare(Regiment r, float percent)
    {
        LastError = null;
        if (r == null || !Regiments.Contains(r)) { LastError = "Regiment is not part of this military."; return false; }

        if (percent < 0f) r.CustomShare = -1f;
        else
        {
            float f = Mathf.Clamp(percent, 0f, 100f) / 100f;
            float others = 0f;
            for (int i = 0; i < Regiments.Count; i++)
                if (Regiments[i] != r && Regiments[i].CustomShare >= 0f) others += Regiments[i].CustomShare;
            if (others + f > 1.0001f)
            {
                LastError = "Custom shares would exceed 100% (others already use " + (others * 100f).ToString("F1") + "%).";
                return false;
            }
            r.CustomShare = f;
        }

        RecalculateShares();
        ClampRegiments();
        return true;
    }

    public void ResetShares()
    {
        for (int i = 0; i < Regiments.Count; i++) Regiments[i].CustomShare = -1f;
        RecalculateShares();
        ClampRegiments();
    }

    internal void RecalculateShares()
    {
        float custom = 0f;
        int auto = 0;
        for (int i = 0; i < Regiments.Count; i++)
        {
            if (Regiments[i].CustomShare >= 0f) custom += Regiments[i].CustomShare;
            else auto++;
        }
        float autoShare = auto > 0 ? Mathf.Max(0f, 1f - custom) / auto : 0f;
        for (int i = 0; i < Regiments.Count; i++)
            Regiments[i].Share = Regiments[i].CustomShare >= 0f ? Regiments[i].CustomShare : autoShare;
    }

    internal void ClampRegiments()
    {
        for (int i = 0; i < Regiments.Count; i++)
        {
            Regiment r = Regiments[i];
            double alloc = r.Allocation;
            if (r.Manpower > alloc) { r.Manpower = alloc; r.RefreshText(); }
        }
    }

    internal void DailyUpdate(ArmyManager army)
    {
        double max = MaxManpower;
        double days = Math.Max(0.01, army.manpowerRecoveryYears) * DateManager.DaysPerMonth * DateManager.MonthsPerYear;
        CurrentManpower = Math.Min(max, CurrentManpower + max / days);

        ClampRegiments();

        for (int i = 0; i < Regiments.Count; i++)
        {
            Regiment r = Regiments[i];
            if (r.State == RegimentState.Reinforcing && r.Manpower >= r.Allocation * 0.999) r.State = RegimentState.Idle;
        }
    }
}

public static class MilitaryExtensions
{
    public static Military GetMilitary(this Nation nation) =>
        ArmyManager.HasInstance ? ArmyManager.Instance.GetMilitary(nation) : null;
}

[Serializable]
public class MilitaryInfo
{
    public string nation;
    public double maxManpower;
    public double currentManpower;
    public float manpowerPercent;
    public float preparationNow;
    public int preparationLevel;
    public double mobilized;
    public int regiments;
    public string shares;
}

// ─────────────────────────────────────────────────────────────────────────────
//  ARMY MANAGER
//   LOGIC : every Regiment lives at a WORLD position on the real map (province centroid, moves when retreating).
//   VISUAL: each LateUpdate the world position is converted to a screen position and a pooled RegimentView
//           card (Screen Space canvas) is placed there. Only on-screen regiments own a card.
// ─────────────────────────────────────────────────────────────────────────────
[DefaultExecutionOrder(-700)]
public class ArmyManager : TickBehaviour
{
    // ── singleton ──
    private static ArmyManager _instance;
    private static bool _quitting;

    public static bool HasInstance => _instance != null;

    public static ArmyManager Instance
    {
        get
        {
            if (_instance == null && !_quitting)
            {
#if UNITY_2022_2_OR_NEWER
                _instance = FindFirstObjectByType<ArmyManager>();
#else
                _instance = FindObjectOfType<ArmyManager>();
#endif
                if (_instance == null)
                    _instance = new GameObject("ArmyManager").AddComponent<ArmyManager>();
            }
            return _instance;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() { _instance = null; _quitting = false; }

    // ── references ──
    [Header("References")]
    [Tooltip("Defaults to the MapManager found in the scene.")]
    [SerializeField] private MapManager map;

    [Header("Regiment UI (generated at runtime)")]
    [Tooltip("A SCREEN SPACE canvas (Overlay or Camera). Cards are placed here from the regiments' world positions.")]
    [SerializeField] private Canvas screenCanvas;
    [Tooltip("Background rectangle sprite. Empty = a plain solid rectangle.")]
    [SerializeField] private Sprite backgroundSprite;
    [Tooltip("Marker image (shown on the left).")]
    [SerializeField] private Sprite markerSprite;
    [Tooltip("Font for the manpower text. Empty = the TMP default font.")]
    [SerializeField] private TMP_FontAsset fontAsset;
    [Tooltip("Card size in canvas units: width = size * 16/9, height = size * 9/16.")]
    [Min(1)] public int regimentSize = 60;
    [Range(0f, 1f)] public float backgroundAlpha = 0.85f;

    // ── manpower ──
    [Header("Manpower")]
    [Min(0.1f)] public float manpowerRecoveryYears = 12f;
    [Range(1, 10)] public int defaultPreparationLevel = 1;
    [Min(0.01f)] public float prepStepInterval = 0.5f;
    [Min(0.01f)] public float prepStepPercent = 1f;

    // ── regiments ──
    [Header("Regiments")]
    [Tooltip("Regiments = MaxManpower / this (clamped). At full mobilization a regiment holds about this many men.")]
    [Min(1f)] public float manpowerPerRegiment = 10000f;
    [Min(1)] public int minRegiments = 1;
    [Min(1)] public int maxRegiments = 30;

    [Range(0f, 100f)] public float retreatManpowerPercent = 10f;
    [Min(0f)] public float MinManpowerToRetreat = 5000f;
    [Range(0f, 1f)] public float reinforcePerDay = 0.02f;
    [Tooltip("Retreat speed in tiles per game-second (real map movement).")]
    [Min(0f)] public float retreatSpeedTiles = 8f;

    // ── visual ──
    [Header("Visual")]
    [Tooltip("Regiments of the same nation in the same province are stacked downwards (screen), this many card heights apart.")]
    [Min(0f)] public float stackSpacing = 1.1f;
    [SerializeField] private Camera viewCamera;
    [Tooltip("The manpower text is shown only while the screen is at most this many tiles wide. 0 = always.")]
    [Min(0f)] public float textMaxVisibleTiles = 250f;
    [Tooltip("Cards are shown only while the screen is at most this many tiles wide. 0 = always.")]
    [Min(0f)] public float markerMaxVisibleTiles = 700f;

    // ── context menu helpers ──
    [Header("Context menu helpers (right-click the component title)")]
    [Tooltip("Nation id. -1 = random nation.")]
    [SerializeField] private int debugNation = -1;
    [Tooltip("Regiment index. -1 = random regiment.")]
    [SerializeField] private int debugRegiment = -1;
    [SerializeField] private float debugAmount = 20f;

    [Header("Sea movement")]
    public bool allowSeaMovement = true;
    [Tooltip("Speed multiplier while the army is over water.")]
    [Range(0.05f, 1f)] public float seaSpeedFactor = 0.5f;
    [Tooltip("Max sea crossing between two provinces, in tiles (centroid to centroid).")]
    [Min(1f)] public float seaRangeTiles = 60f;

    [Header("Inspector view (read-only mirror, Editor only)")]
    [SerializeField] private bool showMilitaryInInspector = true;
    [SerializeField] private List<MilitaryInfo> militaryView = new List<MilitaryInfo>();
    private float _nextInspectorRefresh;

    // ── debug ──
    [Header("Debug")]
    [Tooltip("ON: every nation that is at war shows a big number on the map in its own color = its total manpower (reserve + men in regiments / at the front).")]
    public bool debugShowWarManpower = false;
    [Tooltip("How much of the nation's bounding box the number may fill.")]
    [Range(0.2f, 1f)] public float debugLabelFill = 0.6f;

    private sealed class DebugLabel
    {
        public TextMeshPro Text;
        public string Last;
        public int ProvinceCount = -1;
        public float BoundsAt;
        public Vector3 Center;
        public Vector2 Size;
    }
    private readonly Dictionary<int, DebugLabel> _dbgLabels = new Dictionary<int, DebugLabel>();
    private Transform _dbgRoot;
    private float _dbgNext;

    // ── data ──
    private readonly Dictionary<int, Military> _militaries = new Dictionary<int, Military>();
    private readonly List<Military> _list = new List<Military>();
    private readonly List<Regiment> _moving = new List<Regiment>();
    private int[] _bfsPrev, _bfsStamp;
    private int _stamp;
    private readonly Queue<int> _bfsQ = new Queue<int>();
    private readonly List<Province> _pathBuf = new List<Province>();
    public IReadOnlyList<Regiment> MovingRegiments => _moving;

    private RectTransform _root;
    private RegimentView _template;
    private MapManager _subscribedMap;
    private float _prepAcc;
    private bool _prepDirty;
    private bool _warnedCanvas;

    // camera state (visual sync only when something changed)
    private bool _lodDirty = true;
    private Vector3 _lodPos;
    private Quaternion _lodRot;
    private float _lodZoom, _lodAspect;
    private int _lodW, _lodH;

    public IReadOnlyList<Military> Militaries => _list;
    public float TileSize => map != null ? map.settings.tileSize : 1f;

    public Sprite BackgroundSprite => backgroundSprite;
    public Sprite MarkerSprite => markerSprite;
    public TMP_FontAsset FontAsset => fontAsset;
    public float BackgroundAlpha => backgroundAlpha;
    public Vector2 RegimentRect => new Vector2(regimentSize * (16f / 9f), regimentSize * (9f / 16f));

    public event Action<Regiment> OnRegimentCreated;
    public event Action<Regiment, bool> OnRegimentRemoved;
    public event Action<Regiment, Province> OnRegimentRetreat;
    public event Action<Regiment> OnRegimentArrived;

    protected override int TickPriority => -700;


    public int DesiredRegiments(Military m) =>
        Mathf.Clamp(Mathf.RoundToInt((float)(m.MaxManpower / Math.Max(1f, manpowerPerRegiment))),
                    Mathf.Max(1, minRegiments), Mathf.Max(minRegiments, maxRegiments));

    // ─────────────────────────────────────────────────────────────────────
    //  LIFECYCLE
    // ─────────────────────────────────────────────────────────────────────
    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Debug.LogWarning("Duplicate ArmyManager removed.");
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

        WorldManager.Instance.OnNationsCleared += ClearAll;
        WorldManager.Instance.OnNationRemoved += HandleNationRemoved;
        DateManager.Instance.OnDayPassed += HandleDay;
        map.OnProvincesMerged += HandleProvincesMerged;

        if (map != null)
        {
            _subscribedMap = map;
            map.OnMapGenerated += BuildAll;
            if (map.IsReady) BuildAll();
        }
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        if (WorldManager.HasInstance)
        {
            WorldManager.Instance.OnNationsCleared -= ClearAll;
            WorldManager.Instance.OnNationRemoved -= HandleNationRemoved;
            if (_subscribedMap != null) _subscribedMap.OnProvincesMerged -= HandleProvincesMerged;
        }
        if (DateManager.HasInstance) DateManager.Instance.OnDayPassed -= HandleDay;
        if (_subscribedMap != null) _subscribedMap.OnMapGenerated -= BuildAll;
        _subscribedMap = null;
        if (_instance == this && _quitting) _instance = null;
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
        if (_template != null) Destroy(_template.gameObject);
    }

    private void BuildAll()
    {
        ClearAll();
        IReadOnlyList<Nation> nations = WorldManager.Instance.Nations;
        for (int i = 0; i < nations.Count; i++)
            if (!nations[i].IsEmpty) EnsureMilitary(nations[i]);
    }

    private void ClearAll()
    {
        for (int i = 0; i < _list.Count; i++)
        {
            for (int j = 0; j < _list[i].Columns.Count; j++) ReleaseRegiment(_list[i].Columns[j]);
            _list[i].Columns.Clear();
        }
        _list.Clear();
        _militaries.Clear();
        _moving.Clear();
        _prepDirty = false;
    }

    private void HandleNationRemoved(Nation n)
    {
        if (n == null || !_militaries.TryGetValue(n.Id, out Military m)) return;
        for (int j = 0; j < m.Columns.Count; j++) ReleaseRegiment(m.Columns[j]);
        m.Columns.Clear();
        m.Regiments.Clear();
        _militaries.Remove(n.Id);
        _list.Remove(m);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  PUBLIC API
    // ─────────────────────────────────────────────────────────────────────
    public Military GetMilitary(Nation n) =>
        n != null && _militaries.TryGetValue(n.Id, out Military m) ? m : null;

    public Military EnsureMilitary(Nation n)
    {
        if (n == null) return null;
        if (_militaries.TryGetValue(n.Id, out Military existing)) return existing;

        var m = new Military(n, defaultPreparationLevel);
        m.CurrentManpower = m.MaxManpower;
        _militaries.Add(n.Id, m);
        _list.Add(m);

        m.AddRegiments(DesiredRegiments(m));
        return m;
    }

    private void HandleProvincesMerged(Province origin, IReadOnlyList<Province> absorbed)
    {
        for (int i = 0; i < _list.Count; i++)
        {
            Military m = _list[i];
            for (int pass = 0; pass < 2; pass++)
            {
                List<Regiment> regs = pass == 0 ? m.Regiments : m.Columns;
                for (int j = 0; j < regs.Count; j++)
                    for (int a = 0; a < absorbed.Count; a++) regs[j].RemapProvince(absorbed[a], origin);
            }
            RefreshStacks(m);
        }
    }

    internal void MarkPreparationDirty() => _prepDirty = true;
    internal void RaiseCreated(Regiment r)
    {
        _lodDirty = true;
        OnRegimentCreated?.Invoke(r);
    }
    internal void RaiseRemoved(Regiment r, bool destroyed) => OnRegimentRemoved?.Invoke(r, destroyed);

    /// <summary>Removes a regiment from the world: stops its movement and gives its card back to the pool.</summary>
    internal void ReleaseRegiment(Regiment r)
    {
        if (r == null) return;
        _moving.Remove(r);
        ReleaseView(r);
        r.Clear();
        _lodDirty = true;
    }

    internal void TrackMoving(Regiment r)
    {
        if (r != null && !_moving.Contains(r)) _moving.Add(r);
    }

    /// <summary>Orders a regiment to march to a province along the province graph (shortest path).</summary>
    public bool MoveRegiment(Regiment r, Province dest) => MoveRegiment(r, dest, null);

    /// <summary>'avoid' = province ids the route may not cross (the destination itself is always allowed).</summary>
    public bool MoveRegiment(Regiment r, Province dest, HashSet<int> avoid)
    {
        if (r == null || dest == null || r.Military == null) return false;
        if (r.State == RegimentState.Retreating || r.State == RegimentState.Fighting) return false;

        Province from = r.CurrentProvince ?? r.Province;
        Nation mover = r.Military.Nation;
        Nation restrictTo = CanEnter(mover, from) ? mover : null;

        if (!FindPath(from, dest, _pathBuf, restrictTo, avoid)) return false;
        if (_pathBuf.Count == 0) _pathBuf.Add(dest);

        r.BeginMove(_pathBuf, ProvinceWorldPos(_pathBuf[0]));
        TrackMoving(r);
        if (!r.IsColumn) RefreshStacks(r.Military);
        return true;
    }

    /// <summary>Own land, unowned land, or land of a nation that is in one of our wars (enemy OR ally of the war).</summary>
    internal bool CanEnter(Nation mover, Province p)
    {
        if (mover == null || p == null) return true;
        Nation owner = p.Owner;
        if (owner == null || owner == mover) return true;
        if (!WarManager.HasInstance) return true;

        IReadOnlyList<War> wars = WarManager.Instance.GetWars(mover);
        for (int i = 0; i < wars.Count; i++)
            if (wars[i].SideOf(owner.Id) != WarSide.None) return true;
        return false;
    }

    /// <summary>BFS over the province borders. 'result' = provinces to visit (without the start). Empty = same province.</summary>
    internal bool FindPath(Province from, Province to, List<Province> result, Nation restrictTo = null, HashSet<int> avoid = null)
    {
        result.Clear();
        if (map == null || from == null || to == null) return false;
        if (from == to) return true;
        if (restrictTo != null && !CanEnter(restrictTo, to)) return false;


        IReadOnlyList<Province> all = map.Provinces;
        int count = all.Count;
        if (_bfsPrev == null || _bfsPrev.Length != count) { _bfsPrev = new int[count]; _bfsStamp = new int[count]; _stamp = 0; }
        _stamp++;

        _bfsQ.Clear();
        _bfsQ.Enqueue(from.Id);
        _bfsStamp[from.Id] = _stamp;
        _bfsPrev[from.Id] = -1;

        bool found = false;
        while (_bfsQ.Count > 0)
        {
            int c = _bfsQ.Dequeue();
            if (c == to.Id) { found = true; break; }
            int[] nb = all[c].Neighbors;
            for (int i = 0; i < nb.Length; i++)
            {
                int m = nb[i];
                if (_bfsStamp[m] == _stamp) continue;
                if (restrictTo != null && !CanEnter(restrictTo, all[m])) continue;   // neutral nation: blocked
                if (avoid != null && m != to.Id && avoid.Contains(m)) continue;
                _bfsStamp[m] = _stamp;
                _bfsPrev[m] = c;
                _bfsQ.Enqueue(m);
            }
        }
        if (!found)
        {
            if (!allowSeaMovement || !SeaSearch(from, to, restrictTo, avoid)) return false;
        }

        for (int c = to.Id; c != from.Id; c = _bfsPrev[c]) result.Add(all[c]);
        result.Reverse();
        return true;
    }

    /// <summary>Fallback BFS: land neighbours plus any province within seaRangeTiles (a sea crossing).</summary>
    private bool SeaSearch(Province from, Province to, Nation restrictTo, HashSet<int> avoid)
    {
        IReadOnlyList<Province> all = map.Provinces;
        _stamp++;
        _bfsQ.Clear();
        _bfsQ.Enqueue(from.Id);
        _bfsStamp[from.Id] = _stamp;
        _bfsPrev[from.Id] = -1;

        float r2 = seaRangeTiles * seaRangeTiles;
        while (_bfsQ.Count > 0)
        {
            int c = _bfsQ.Dequeue();
            if (c == to.Id) return true;
            Province pc = all[c];

            for (int m = 0; m < all.Count; m++)
            {
                if (_bfsStamp[m] == _stamp) continue;
                Province q = all[m];
                if (q.IsMerged || q.Tiles == null || q.Tiles.Length == 0) continue;
                if ((q.Centroid - pc.Centroid).sqrMagnitude > r2) continue;
                if (restrictTo != null && !CanEnter(restrictTo, q)) continue;
                if (avoid != null && m != to.Id && avoid.Contains(m)) continue;
                _bfsStamp[m] = _stamp;
                _bfsPrev[m] = c;
                _bfsQ.Enqueue(m);
            }
        }
        return false;
    }

    /// <summary>World position -> position in the UI root. False when off screen / no canvas.</summary>
    internal bool WorldToUi(Vector3 world, out Vector2 local)
    {
        local = default;
        Camera cam = viewCamera ? viewCamera : Camera.main;
        RectTransform root = GetRoot();
        if (cam == null || root == null) return false;

        Vector3 sp = cam.WorldToScreenPoint(world);
        float mx = Screen.width * 0.1f, my = Screen.height * 0.1f;
        if (sp.z <= 0f || sp.x < -mx || sp.x > Screen.width + mx || sp.y < -my || sp.y > Screen.height + my) return false;

        Camera canvasCam = screenCanvas.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : screenCanvas.rootCanvas.worldCamera;
        return RectTransformUtility.ScreenPointToLocalPointInRectangle(root, new Vector2(sp.x, sp.y), canvasCam, out local);
    }

    internal bool CardsVisibleAtZoom()
    {
        Camera cam = viewCamera ? viewCamera : Camera.main;
        if (cam == null || map == null) return false;
        return markerMaxVisibleTiles <= 0f || VisibleTiles(cam) <= markerMaxVisibleTiles;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  TICK / DAY  (game logic, follows game speed)
    // ─────────────────────────────────────────────────────────────────────
    public override void Tick(float deltaTime)
    {
        if (_prepDirty)
        {
            _prepAcc += deltaTime;
            float interval = Mathf.Max(0.01f, prepStepInterval);
            int guard = 0;
            while (_prepAcc >= interval && _prepDirty && guard++ < 16)
            {
                _prepAcc -= interval;
                StepPreparation();
            }
            if (guard >= 16) _prepAcc = 0f;
        }
        else _prepAcc = 0f;

        MoveRegiments(deltaTime);

#if UNITY_EDITOR
        if (showMilitaryInInspector && Time.unscaledTime >= _nextInspectorRefresh)
        {
            _nextInspectorRefresh = Time.unscaledTime + 1f;
            RefreshView();
        }
#endif
    }

    private void StepPreparation()
    {
        bool pending = false;
        for (int i = 0; i < _list.Count; i++)
        {
            Military m = _list[i];
            float target = m.TargetPreparation;
            float cur = m.CurrentPreparation;
            if (Mathf.Approximately(cur, target)) continue;

            cur = cur < target ? Mathf.Min(target, cur + prepStepPercent) : Mathf.Max(target, cur - prepStepPercent);
            m.CurrentPreparation = cur;
            m.ClampRegiments();
            if (!Mathf.Approximately(cur, target)) pending = true;
        }
        _prepDirty = pending;
    }

    private void HandleDay()
    {
        for (int i = 0; i < _list.Count; i++) _list[i].DailyUpdate(this);
    }

    [Header("Movement")]
    [Tooltip("March speed in tiles per game-second.")]
    [Min(0f)] public float moveSpeedTiles = 4f;
    [Tooltip("Reinforcement column speed in tiles per game-second.")]
    [Min(0f)] public float reinforceSpeedTiles = 6f;

    /// <summary>Real movement on the map (world units): marching armies, retreating armies and reinforcement columns.</summary>
    private void MoveRegiments(float deltaTime)
    {
        if (_moving.Count == 0) return;

        for (int i = _moving.Count - 1; i >= 0; i--)
        {
            if (i >= _moving.Count) continue;                 // handlers may shrink the list
            Regiment r = _moving[i];
            if (r.Military == null || (r.State != RegimentState.Moving && r.State != RegimentState.Retreating))
            { _moving.RemoveAt(i); continue; }

            float speed = r.State == RegimentState.Retreating ? retreatSpeedTiles
                        : r.IsColumn ? reinforceSpeedTiles : moveSpeedTiles;

            float step = speed * TileSize * deltaTime;
            if (allowSeaMovement && map != null && map.TryWorldToTile(r.WorldPosition, out int tx, out int ty) && !map.IsLand(tx, ty))
                step *= seaSpeedFactor;                      // over water: slower
            bool arrived = r.StepMove(step, this);
            Province cp = map != null ? map.GetProvinceAt(r.WorldPosition) : null;   // the province under the army
            if (cp != null) r.CurrentProvince = cp;

            if (arrived)
            {
                _moving.RemoveAt(i);
                r.CurrentProvince = r.Province;
                if (!r.IsColumn && r.Military != null) RefreshStacks(r.Military);
                OnRegimentArrived?.Invoke(r);
            }
        }
        _lodDirty = true;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  VISUAL SYNC: WORLD -> SCREEN  (LateUpdate: follows the camera even while the game is paused)
    // ─────────────────────────────────────────────────────────────────────
    private void LateUpdate()
    {
        if (_list.Count == 0 || map == null) return;
        Camera cam = viewCamera ? viewCamera : Camera.main;
        if (cam == null) return;

        Transform ct = cam.transform;
        float zoom = cam.orthographic ? cam.orthographicSize : cam.fieldOfView;
        bool moved = ct.position != _lodPos || ct.rotation != _lodRot || zoom != _lodZoom ||
                     cam.aspect != _lodAspect || Screen.width != _lodW || Screen.height != _lodH;
        if (!moved && !_lodDirty) return;

        _lodPos = ct.position; _lodRot = ct.rotation; _lodZoom = zoom; _lodAspect = cam.aspect;
        _lodW = Screen.width; _lodH = Screen.height;
        _lodDirty = false;

        RectTransform root = GetRoot();
        if (root == null) return;
        Camera canvasCam = screenCanvas.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay
            ? null : screenCanvas.rootCanvas.worldCamera;

        float visibleTiles = VisibleTiles(cam);
        bool showMarkers = markerMaxVisibleTiles <= 0f || visibleTiles <= markerMaxVisibleTiles;
        bool showText = showMarkers && (textMaxVisibleTiles <= 0f || visibleTiles <= textMaxVisibleTiles);

        float marginX = Screen.width * 0.1f, marginY = Screen.height * 0.1f;
        float stackStep = RegimentRect.y * stackSpacing;

        for (int i = 0; i < _list.Count; i++)
        {
            Military mil = _list[i];
            for (int pass = 0; pass < 2; pass++)
            {
                List<Regiment> regs = pass == 0 ? mil.Regiments : mil.Columns;
                for (int j = 0; j < regs.Count; j++)
                {
                    Regiment r = regs[j];
                    if (r.State == RegimentState.Fighting) { if (r.View != null) ReleaseView(r); continue; }

                    Vector3 sp = cam.WorldToScreenPoint(r.WorldPosition);
                    bool inside = sp.z > 0f &&
                                  sp.x >= -marginX && sp.x <= Screen.width + marginX &&
                                  sp.y >= -marginY && sp.y <= Screen.height + marginY;

                    if (!showMarkers || !inside)
                    {
                        if (r.View != null) ReleaseView(r);
                        continue;
                    }

                    if (r.View == null && !AcquireView(r)) continue;

                    RectTransformUtility.ScreenPointToLocalPointInRectangle(root, new Vector2(sp.x, sp.y), canvasCam, out Vector2 local);
                    bool travelling = r.IsColumn || r.State == RegimentState.Retreating || r.State == RegimentState.Moving;
                    if (!travelling) local.y -= r.StackIndex * stackStep;

                    r.View.SetLocalPosition(local);
                    r.View.SetTextVisible(showText);
                }
            }
        }
    }

    /// <summary>Screen width measured in map tiles (used for the zoom LOD).</summary>
    private float VisibleTiles(Camera cam)
    {
        float width;
        if (cam.orthographic) width = 2f * cam.orthographicSize * cam.aspect;
        else
        {
            var plane = new Plane(map.transform.forward, map.transform.position);
            Ray a = cam.ViewportPointToRay(new Vector3(0f, 0.5f, 0f));
            Ray b = cam.ViewportPointToRay(new Vector3(1f, 0.5f, 0f));
            if (!plane.Raycast(a, out float da) || !plane.Raycast(b, out float db)) return float.MaxValue;
            width = Vector3.Distance(a.GetPoint(da), b.GetPoint(db));
        }
        return width / Mathf.Max(0.0001f, TileSize);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  VIEW POOLING
    // ─────────────────────────────────────────────────────────────────────
    private bool AcquireView(Regiment r)
    {
        Transform root = GetRoot();
        if (root == null) return false;

        RegimentView v = PoolManager.Instance.Spawn(GetTemplate(), Vector3.zero, Quaternion.identity, root);
        if (v == null) return false;
        v.transform.localRotation = Quaternion.identity;
        v.transform.localScale = Vector3.one;
        r.View = v;
        v.Bind(r, this);
        return true;
    }

    private void ReleaseView(Regiment r)
    {
        RegimentView v = r.View;
        if (v == null) return;
        r.View = null;
        if (PoolManager.HasInstance) PoolManager.Instance.Despawn(v.gameObject);
        else Destroy(v.gameObject);
    }

    /// <summary>"Regiments" object stretched over the screen canvas (created once).</summary>
    internal RectTransform GetRoot()
    {
        if (_root != null) return _root;
        if (screenCanvas == null)
        {
            if (!_warnedCanvas) { _warnedCanvas = true; Debug.LogWarning("[Army] Assign the Screen Canvas on the ArmyManager (cards are not shown without it)."); }
            return null;
        }

        var go = new GameObject("Regiments", typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(screenCanvas.transform, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        rt.localScale = Vector3.one;
        _root = rt;
        return _root;
    }

    private RegimentView GetTemplate()
    {
        if (_template != null) return _template;
        var go = new GameObject("RegimentView (template)", typeof(RectTransform));
        go.SetActive(false);
        go.transform.SetParent(transform, false);
        _template = go.AddComponent<RegimentView>();
        return _template;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  SPAWN / PLACEMENT  (pure logic, no UI)
    // ─────────────────────────────────────────────────────────────────────
    internal Regiment SpawnRegiment(Military m, Province at)
    {
        if (at == null) at = PickDeployProvince(m);
        if (at == null) return null;
        return new Regiment(m, at, ProvinceWorldPos(at));
    }

    internal Province PickDeployProvince(Military m)
    {
        Nation n = m.Nation;
        if (n.Provinces.Count == 0) return n.Capital;
        if (n.Capital != null && !IsOccupied(m, n.Capital)) return n.Capital;

        Province p = null;
        for (int t = 0; t < 8; t++)
        {
            p = n.Provinces[RNG.GetInt(0, n.Provinces.Count)];
            if (!IsOccupied(m, p)) return p;
        }
        return p;
    }

    private static bool IsOccupied(Military m, Province p)
    {
        for (int i = 0; i < m.Regiments.Count; i++)
            if (m.Regiments[i].LocationProvince == p) return true;
        return false;
    }

    /// <summary>Real map position of a province (world units).</summary>
    internal Vector3 ProvinceWorldPos(Province p)
    {
        if (map == null || p == null) return Vector3.zero;
        return map.TileToWorldCenter(Mathf.RoundToInt(p.Centroid.x), Mathf.RoundToInt(p.Centroid.y));
    }

    /// <summary>Gives every regiment its stack index: k-th regiment (list order) of the same nation standing in the same province.</summary>
    internal void RefreshStacks(Military m)
    {
        List<Regiment> regs = m.Regiments;
        for (int i = 0; i < regs.Count; i++)
        {
            Province loc = regs[i].LocationProvince;
            int k = 0;
            for (int j = 0; j < i; j++)
                if (regs[j].LocationProvince == loc) k++;
            regs[i].StackIndex = k;
        }
        _lodDirty = true;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  RETREAT
    // ─────────────────────────────────────────────────────────────────────
    internal void CheckRetreat(Regiment r)
    {
        Military m = r.Military;
        if (m == null) return;

        if (r.IsColumn) { if (r.Manpower <= 0.5) m.DestroyColumn(r); return; }

        if (r.Manpower <= 0.5) { m.DestroyRegiment(r); return; }
        if (r.State == RegimentState.Retreating) return;
        if (r.Manpower > r.Allocation * retreatManpowerPercent / 100.0) return;

        if (r.Manpower < MinManpowerToRetreat) { m.DestroyRegiment(r); return; }

        Province p = CombatManager.HasInstance
            ? CombatManager.Instance.FindSafeProvince(m.Nation, r.WorldPosition, r.Province)
            : null;

        if (p != null || TryPickRetreatProvince(r, out p))
        {
            r.BeginRetreat(p, ProvinceWorldPos(p));
            if (!_moving.Contains(r)) _moving.Add(r);
            RefreshStacks(m);
            OnRegimentRetreat?.Invoke(r, p);
        }
        else m.DestroyRegiment(r);
    }

    private bool TryPickRetreatProvince(Regiment r, out Province result)
    {
        result = null;
        Nation own = r.Military.Nation;
        float best = float.MaxValue;
        for (int i = 0; i < own.Provinces.Count; i++)
        {
            Province p = own.Provinces[i];
            if (p == r.Province) continue;
            float d = (ProvinceWorldPos(p) - r.WorldPosition).sqrMagnitude;
            if (d < best) { best = d; result = p; }
        }
        return result != null;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  CONTEXT MENU   (-1 = random nation / random regiment / random province)
    // ─────────────────────────────────────────────────────────────────────
    [ContextMenu("Army: Add Regiment (random province of the nation)")]
    private void MenuAdd() => RunMenu("Add Regiment", (m, r) => m.AddRegiment(RandomProvinceOf(m.Nation)) != null, false);

    [ContextMenu("Army: Remove Regiment (debugRegiment, or random)")]
    private void MenuRemove() => RunMenu("Remove Regiment", (m, r) => m.RemoveRegiment(r), true);

    [ContextMenu("Army: Set Preparation Level (debugAmount 1-10)")]
    private void MenuPrep() => RunMenu("Preparation level " + Mathf.RoundToInt(debugAmount),
        (m, r) => { m.SetPreparationLevel(Mathf.RoundToInt(debugAmount)); return true; }, false);

    [ContextMenu("Army: Set Regiment Share % (debugAmount, negative = auto)")]
    private void MenuShare() => RunMenu("Share " + debugAmount + "%", (m, r) => r != null && m.SetRegimentShare(r, debugAmount), true);

    [ContextMenu("Army: Reset Shares (equal split)")]
    private void MenuReset() => RunMenu("Reset shares", (m, r) => { m.ResetShares(); return true; }, false);

    [ContextMenu("Army: Regiment Takes Casualties (debugAmount men)")]
    private void MenuCasualties() => RunMenu("Casualties " + debugAmount,
        (m, r) => { if (r == null) return false; r.TakeCasualties(debugAmount); return true; }, true);

    [ContextMenu("Army: Log Military")]
    private void MenuLog() => RunMenu("Log", (m, r) => true, false);

    private static Province RandomProvinceOf(Nation n)
    {
        if (n == null || n.Provinces.Count == 0) return null;   // null = the normal deploy rule
        return n.Provinces[RNG.GetInt(0, n.Provinces.Count)];
    }

    private void RunMenu(string label, Func<Military, Regiment, bool> action, bool needsRegiment)
    {
        WorldManager w = WorldManager.Instance;
        Nation n = debugNation >= 0 ? w.GetNation(debugNation) : w.GetRandomNation();
        Military m = GetMilitary(n);
        if (m == null) { Debug.LogWarning("[Army] No military (generate the map first, or check debugNation)."); return; }

        Regiment r = null;
        if (m.Regiments.Count > 0)
        {
            if (debugRegiment >= 0 && debugRegiment < m.Regiments.Count) r = m.Regiments[debugRegiment];
            else if (debugRegiment < 0 && needsRegiment) r = m.Regiments[RNG.GetInt(0, m.Regiments.Count)];
        }
        if (needsRegiment && r == null) { Debug.LogWarning("[Army] Regiment not found."); return; }

        bool ok = action(m, r);
        Debug.Log($"[Army] {label}: {n.Name} (#{n.Id}) -> " + (ok ? "OK" : "FAILED: " + m.LastError) + " | " + Describe(m));
        RefreshView();
    }

    private static string Describe(Military m)
    {
        return $"manpower {m.CurrentManpower:N0}/{m.MaxManpower:N0} ({m.ManpowerPercent:F1}%), " +
               $"prep {m.CurrentPreparation:F0}% -> {m.TargetPreparation:F0}% (level {m.PreparationLevel}), " +
               $"mobilized {m.MobilizedCap:N0}, regiments {m.RegimentCount}, shares [{SharesText(m)}]";
    }

    private static string SharesText(Military m)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < m.Regiments.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            Regiment r = m.Regiments[i];
            sb.Append((r.Share * 100f).ToString("F1")).Append(r.IsAutoShare ? "%" : "%*");
        }
        return sb.ToString();
    }

    private void RefreshView()
    {
#if UNITY_EDITOR
        militaryView.Clear();
        if (!showMilitaryInInspector) return;
        for (int i = 0; i < _list.Count; i++)
        {
            Military m = _list[i];
            militaryView.Add(new MilitaryInfo
            {
                nation = m.Nation.Name,
                maxManpower = m.MaxManpower,
                currentManpower = m.CurrentManpower,
                manpowerPercent = (float)m.ManpowerPercent,
                preparationNow = m.CurrentPreparation,
                preparationLevel = m.PreparationLevel,
                mobilized = m.MobilizedCap,
                regiments = m.RegimentCount,
                shares = SharesText(m)
            });
        }
#endif
    }
}