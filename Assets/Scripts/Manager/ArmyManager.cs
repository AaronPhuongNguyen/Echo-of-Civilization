using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

#region Military (one per nation)

// ─────────────────────────────────────────────────────────────────────────────
//  MILITARY  (one per nation, created and owned by ArmyManager)
//   - MaxManpower     = Nation.Population * Manpower.Value
//   - CurrentManpower = men the nation has (reserve + men inside regiments).
//   - Preparation     = how much of MaxManpower is mobilized: level 1..10 = 10%..100%.
//   - MobilizedCap    = min(CurrentManpower, MaxManpower * CurrentPreparation)
//   - Regiment max    = MobilizedCap * Share, never above 100 value (100 000 men).
//   - Column max      = 10 value (10 000 men).
// ─────────────────────────────────────────────────────────────────────────────
public sealed class Military
{
    public readonly Nation Nation;

    public readonly GameValue Manpower = new GameValue(0.5f);

    public readonly List<Regiment> Regiments = new List<Regiment>();
    public readonly List<Regiment> Columns = new List<Regiment>();

    public string LastError { get; private set; }

    public double CurrentManpower { get; internal set; }
    public double MaxManpower => Nation.Population * Manpower.Value;
    public double ManpowerPercent { get { double m = MaxManpower; return m > 0.0 ? CurrentManpower / m * 100.0 : 0.0; } }

    #region Combat stats

    public readonly GameValue AttackPerMan = new GameValue(5f);
    public readonly GameValue DefendPerMan = new GameValue(3f);
    public readonly GameValue HpPerMan = new GameValue(5f);
    public readonly GameValue DamageMultiplier = new GameValue(1f);

    /// <summary>Outer attack radius in TILES: enemies inside it (both ways) start trading damage.</summary>
    public readonly GameValue OuterRadius = new GameValue(0.2f);
    /// <summary>Inner attack radius in TILES: contact (armies stop marching, the battle may push them).</summary>
    public readonly GameValue InnerRadius = new GameValue(0.1f);

    #endregion

    #region Preparation

    public int PreparationLevel { get; private set; }
    public float CurrentPreparation { get; internal set; }
    public float TargetPreparation => PreparationLevel * 10f;

    public double MobilizedCap => Math.Min(CurrentManpower, MaxManpower * CurrentPreparation / 100.0);
    public double RegimentCapMen => ArmyManager.HasInstance ? ArmyManager.Instance.RegimentCapMen : double.MaxValue;
    public int RegimentCount => Regiments.Count;

    internal Military(Nation nation, int preparationLevel)
    {
        Nation = nation;
        PreparationLevel = Mathf.Clamp(preparationLevel, 1, 10);
        CurrentPreparation = TargetPreparation;
    }

    public void SetPreparationLevel(int level)
    {
        level = Mathf.Clamp(level, 1, 10);
        if (level == PreparationLevel) return;
        PreparationLevel = level;
        if (ArmyManager.HasInstance) ArmyManager.Instance.MarkPreparationDirty();
    }

    #endregion

    #region Cost
    public double AffordableMen =>
    !ArmyManager.HasInstance || !ArmyManager.Instance.chargeTreasury ? double.MaxValue
    : Math.Max(0.0, Nation.Treasury) * ArmyManager.Instance.menPerTreasury;

    private bool Pay(double men)
    {
        ArmyManager a = ArmyManager.Instance;
        return !a.chargeTreasury || Nation.TrySpend(men / a.menPerTreasury);
    }

    private void Refund(double men)
    {
        ArmyManager a = ArmyManager.Instance;
        if (a.chargeTreasury && men > 0.0) Nation.AddMoney(men / a.menPerTreasury);
    }
    #endregion

    public double TotalRegimentManpower()
    {
        double sum = 0.0;
        for (int i = 0; i < Regiments.Count; i++) sum += Regiments[i].Manpower;
        for (int i = 0; i < Columns.Count; i++) sum += Columns[i].Manpower;
        return sum;
    }

    #region Regiments

    public Regiment AddRegiment(Province at = null, bool pay = false)
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
        double men = Math.Min(r.Allocation, free);
        if (pay) men = Math.Min(men, AffordableMen);
        if (pay && (men < 1.0 || !Pay(men)))
        {
            Regiments.Remove(r);
            army.ReleaseRegiment(r);
            army.RefreshStacks(this);
            RecalculateShares();
            LastError = "Not enough treasury.";
            return null;
        }
        r.Manpower = men;
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
            Regiments.Add(r);
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

    #endregion

    #region Columns

    internal Regiment AddColumn(Province at, double men, bool pay = true)
    {
        if (!ArmyManager.HasInstance || at == null || men <= 0.0) return null;
        ArmyManager army = ArmyManager.Instance;
        men = Math.Min(men, army.ColumnCapMen);
        if (pay) { men = Math.Min(men, AffordableMen); if (men < 1.0 || !Pay(men)) return null; }
        Regiment c = army.SpawnRegiment(this, at);
        if (c == null) { if (pay) Refund(men); return null; }
        c.IsColumn = true;
        c.Share = 0f;
        c.Manpower = men;
        Columns.Add(c);
        return c;
    }

    internal void RemoveColumn(Regiment c)
    {
        if (!Columns.Remove(c)) return;
        Refund(c.Manpower);
        ArmyManager.Instance.ReleaseRegiment(c);
    }

    public void DestroyColumn(Regiment c)
    {
        if (!Columns.Remove(c)) return;
        CurrentManpower = Math.Max(0.0, CurrentManpower - c.Manpower);
        ArmyManager.Instance.ReleaseRegiment(c);
    }

    #endregion

    #region Shares

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

    #endregion

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

#endregion

// ─────────────────────────────────────────────────────────────────────────────
//  ARMY MANAGER
//   LOGIC : every Regiment lives at a WORLD position on the real map.
//   VISUAL: one Burst job projects all positions to the screen; pooled RegimentViews are placed there.
//           Card size follows the ZOOM (CardScale, clamped + quantized) so it is consistent.
//   BATTLE: fighting / push / Combat Views are handled by CombatManager + CombatViewManager.
// ─────────────────────────────────────────────────────────────────────────────
[DefaultExecutionOrder(-700)]
public class ArmyManager : TickBehaviour
{
    #region Singleton

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

    #endregion

    #region Inspector

    [Header("References")]
    [SerializeField] private MapManager map;

    [Header("Regiment card (generated at runtime)")]
    [SerializeField] private Canvas screenCanvas;
    [SerializeField] private Sprite backgroundSprite;
    [SerializeField] private TMP_FontAsset fontAsset;
    [Tooltip("Card size in canvas units at scale 1 (a square).")]
    [Min(1)] public int regimentSize = 45;
    [Range(0f, 1f)] public float backgroundAlpha = 0.85f;

    [Header("Value (what the number on the card means)")]
    [Min(1)] public int menPerValue = 1000;
    [Min(1)] public int maxRegimentValue = 100;
    [Min(1)] public int maxColumnValue = 10;

    [Header("Manpower")]
    [Min(0.1f)] public float manpowerRecoveryYears = 12f;
    [Range(1, 10)] public int defaultPreparationLevel = 1;
    [Min(0.01f)] public float prepStepInterval = 0.5f;
    [Min(0.01f)] public float prepStepPercent = 1f;

    [Header("Regiments")]
    [Min(1f)] public float manpowerPerRegiment = 1000f;
    [Min(1)] public int minRegiments = 1;
    [Min(1)] public int maxRegiments = 30;

    [Range(0f, 100f)] public float retreatManpowerPercent = 10f;
    [Min(0f)] public float MinManpowerToRetreat = 5000f;
    [Range(0f, 1f)] public float reinforcePerDay = 0.03f;
    [Min(0f)] public float retreatSpeedTiles = 8f;

    [Header("Deploy cost")]
    [Min(0.01f)] public float menPerTreasury = 10f;
    public bool chargeTreasury = true;

    [Header("Movement")]
    [Tooltip("March speed in tiles per game-second.")]
    [Min(0f)] public float moveSpeedTiles = 5f;
    [Min(0f)] public float reinforceSpeedTiles = 8f;

    [Header("Sea movement")]
    public bool allowSeaMovement = true;
    [Range(0.05f, 1f)] public float seaSpeedFactor = 0.5f;
    [Min(1f)] public float seaRangeTiles = 60f;
    [Min(100)] public int maxSeaNodes = 6000;
    [Min(1)] public int seaHops = 8;   // how many connected sea regions one crossing may span

    [Header("Visual")]
    [Tooltip("Visible map width (tiles) at which a card has scale 1. Zoom in = bigger cards, zoom out = smaller.")]
    [Min(1f)] public float referenceVisibleTiles = 120f;
    [Range(0.2f, 1f)] public float minCardScale = 0.6f;
    [Range(1f, 3f)] public float maxCardScale = 1.2f;
    [Tooltip("Reinforcement columns are drawn at this share of a regiment card.")]
    [Range(0.4f, 1f)] public float columnScale = 0.75f;
    [Tooltip("The number is hidden while the card scale is below this (too small to read).")]
    [Range(0f, 1f)] public float hideTextBelowScale = 0.6f;
    [Range(0f, 1f)] public float pileOffset = 0.22f;
    [Min(0)] public int pileMaxDepth = 4;
    [SerializeField] private Camera viewCamera;
    [Tooltip("Cards are shown only while the screen is at most this many tiles wide. 0 = always.")]
    [Min(0f)] public float markerMaxVisibleTiles = 700f;
    [Min(0.05f)] public float visualRefreshInterval = 0.5f;

    [Header("Context menu helpers (right-click the component title)")]
    [SerializeField] private int debugNation = -1;
    [SerializeField] private int debugRegiment = -1;
    [SerializeField] private float debugAmount = 20f;

    [Header("Diagnostics (Editor only, throttled)")]
    [SerializeField] private bool showMilitaryInInspector = true;
    [Min(0.25f)] public float diagnosticsInterval = 1f;
    [SerializeField] private List<MilitaryInfo> militaryView = new List<MilitaryInfo>();
    private float _nextInspectorRefresh;

    [Header("Debug (reserved)")]
    public bool debugShowWarManpower = false;
    [Range(0.2f, 1f)] public float debugLabelFill = 0.6f;

    #endregion

    #region Data & events

    private readonly Dictionary<int, Military> _militaries = new Dictionary<int, Military>();
    private readonly List<Military> _list = new List<Military>();
    private readonly List<Regiment> _moving = new List<Regiment>();

    private readonly List<Regiment> _enteredR = new List<Regiment>(16);
    private readonly List<Province> _enteredP = new List<Province>(16);

    private readonly Queue<int> _seaQ = new Queue<int>();
    private int[] _seaDepth;

    private RectTransform _root;
    private RegimentView _template;
    private MapManager _subscribedMap;
    private float _prepAcc;
    private bool _prepDirty;
    private bool _warnedCanvas;

    public IReadOnlyList<Military> Militaries => _list;
    public IReadOnlyList<Regiment> MovingRegiments => _moving;
    public float TileSize => map != null ? map.settings.tileSize : 1f;

    public Sprite BackgroundSprite => backgroundSprite;
    public TMP_FontAsset FontAsset => fontAsset;
    public float BackgroundAlpha => backgroundAlpha;
    public Vector2 RegimentRect => new Vector2(regimentSize, regimentSize);

    /// <summary>Current zoom scale of the cards (also used by the Combat Views).</summary>
    public float CardScale { get; private set; } = 1f;
    /// <summary>False while zoomed out too far (cards and Combat Views are hidden).</summary>
    public bool MarkersVisible { get; private set; } = true;
    /// <summary>Incremented whenever the camera / screen changed (Combat Views reposition on it).</summary>
    public int ViewVersion { get; private set; }

    public double RegimentCapMen => (double)menPerValue * maxRegimentValue;
    public double ColumnCapMen => (double)menPerValue * maxColumnValue;
    public int ToValue(double men) => men <= 0.0 ? 0 : Mathf.CeilToInt((float)(men / Math.Max(1, menPerValue)));

    public event Action<Regiment> OnRegimentCreated;
    public event Action<Regiment, bool> OnRegimentRemoved;
    public event Action<Regiment, Province> OnRegimentRetreat;
    public event Action<Regiment> OnRegimentArrived;
    public event Action<Regiment, Province> OnRegimentEnteredProvince;

    protected override int TickPriority => -700;

    public int DesiredRegiments(Military m) =>
        Mathf.Clamp(Mathf.RoundToInt((float)(m.MaxManpower / Math.Max(1f, manpowerPerRegiment))),
                    Mathf.Max(1, minRegiments), Mathf.Max(minRegiments, maxRegiments));

    #endregion

    #region Lifecycle

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

        if (map != null)
        {
            _subscribedMap = map;
            map.OnMapGenerated += BuildAll;
            map.OnProvincesMerged += HandleProvincesMerged;
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
        }
        if (DateManager.HasInstance) DateManager.Instance.OnDayPassed -= HandleDay;
        if (_subscribedMap != null)
        {
            _subscribedMap.OnMapGenerated -= BuildAll;
            _subscribedMap.OnProvincesMerged -= HandleProvincesMerged;
        }
        _subscribedMap = null;
        if (_instance == this && _quitting) _instance = null;
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
        if (_template != null) Destroy(_template.gameObject);
        _proj.Dispose();
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
        for (int i = 0; i < _moving.Count; i++) _moving[i].MovingIndex = -1;
        _list.Clear();
        _militaries.Clear();
        _moving.Clear();
        _enteredR.Clear();
        _enteredP.Clear();
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

    #endregion

    #region Public API

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
    internal void MarkVisualDirty() => _lodDirty = true;

    internal void RaiseCreated(Regiment r)
    {
        _lodDirty = true;
        OnRegimentCreated?.Invoke(r);
    }

    internal void RaiseRemoved(Regiment r, bool destroyed) => OnRegimentRemoved?.Invoke(r, destroyed);

    internal void ReleaseRegiment(Regiment r)
    {
        if (r == null) return;
        UntrackMoving(r);
        ReleaseView(r);
        r.Clear();
        _lodDirty = true;
    }

    /// <summary>Battle push: moves a regiment on the map and keeps its province bookkeeping right.</summary>
    internal void Nudge(Regiment r, Vector3 delta)
    {
        r.Nudge(delta);
        _lodDirty = true;
        if (map == null || !map.IsReady) return;

        Province p = map.GetProvinceAt(r.WorldPosition);
        if (p == null || p == r.CurrentProvince) return;
        r.CurrentProvince = p;
        if (r.State == RegimentState.Idle || r.State == RegimentState.Reinforcing)
        {
            r.SettleIn(p);
            if (!r.IsColumn && r.Military != null) RefreshStacks(r.Military);
        }
    }

    internal void RaiseArrived(Regiment r) => OnRegimentArrived?.Invoke(r);

    /// <summary>World position -> local position inside the army root (for Combat Views).</summary>
    internal bool TryWorldToCanvas(Vector3 world, out Vector2 local)
    {
        local = default;
        Camera cam = viewCamera ? viewCamera : Camera.main;
        RectTransform root = GetRoot();
        if (cam == null || root == null) return false;

        Vector3 sp = cam.WorldToScreenPoint(world);
        if (sp.z < 0f) return false;
        Canvas rc = screenCanvas.rootCanvas;
        if (rc.renderMode == RenderMode.ScreenSpaceOverlay)
        {
            float inv = 1f / Mathf.Max(0.0001f, rc.scaleFactor);
            local = new Vector2((sp.x - Screen.width * 0.5f) * inv, (sp.y - Screen.height * 0.5f) * inv);
            return true;
        }
        return RectTransformUtility.ScreenPointToLocalPointInRectangle(root, sp, rc.worldCamera, out local);
    }

    #endregion

    #region Moving list (O(1) add / remove)

    internal void TrackMoving(Regiment r)
    {
        if (r == null || r.MovingIndex >= 0) return;
        r.MovingIndex = _moving.Count;
        _moving.Add(r);
    }

    private void UntrackMoving(Regiment r)
    {
        if (r.MovingIndex >= 0) SwapRemoveMoving(r.MovingIndex);
    }

    private void SwapRemoveMoving(int i)
    {
        int last = _moving.Count - 1;
        _moving[i].MovingIndex = -1;
        if (i != last)
        {
            Regiment moved = _moving[last];
            _moving[i] = moved;
            moved.MovingIndex = i;
        }
        _moving.RemoveAt(last);
    }

    #endregion

    #region Path finding

    private int[] _bfsPrev, _bfsStamp, _seaStamp;
    private int _stamp;
    private readonly Queue<int> _bfsQ = new Queue<int>();
    private readonly List<Province> _pathBuf = new List<Province>();
    private readonly HashSet<int> _allowed = new HashSet<int>();

    public bool MoveRegiment(Regiment r, Province dest) => MoveRegiment(r, dest, null);

    public bool MoveRegiment(Regiment r, Province dest, HashSet<int> avoid)
    {
        if (r == null || dest == null || r.Military == null) return false;
        if (r.State == RegimentState.Retreating) return false;

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

    private void PrepareAllowed(Nation mover)
    {
        _allowed.Clear();
        _allowed.Add(mover.Id);
        if (!WarManager.HasInstance) return;
        IReadOnlyList<War> wars = WarManager.Instance.GetWars(mover);
        for (int i = 0; i < wars.Count; i++)
        {
            War w = wars[i];
            for (int k = 0; k < w.Attackers.Count; k++) _allowed.Add(w.Attackers[k]);
            for (int k = 0; k < w.Defenders.Count; k++) _allowed.Add(w.Defenders[k]);
        }
    }

    private bool Allowed(Province p) => p.Owner == null || _allowed.Contains(p.Owner.Id);

    internal bool FindPath(Province from, Province to, List<Province> result, Nation restrictTo = null, HashSet<int> avoid = null)
    {
        result.Clear();
        if (map == null || from == null || to == null) return false;
        if (from == to) return true;

        bool restrict = restrictTo != null;
        if (restrict)
        {
            PrepareAllowed(restrictTo);
            if (!Allowed(to)) return false;
        }

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
                if (restrict && !Allowed(all[m])) continue;
                if (avoid != null && m != to.Id && avoid.Contains(m)) continue;
                _bfsStamp[m] = _stamp;
                _bfsPrev[m] = c;
                _bfsQ.Enqueue(m);
            }
        }
        if (!found && (!allowSeaMovement || !SeaSearch(from, to, restrict, avoid))) return false;

        int guard = all.Count + 2;
        for (int c = to.Id; c != from.Id; c = _bfsPrev[c])
        {
            if (c < 0 || c >= all.Count || guard-- <= 0) { result.Clear(); return false; }
            result.Add(all[c]);
        }
        result.Reverse();
        return true;
    }

    private bool SeaSearch(Province from, Province to, bool restrict, HashSet<int> avoid)
    {
        IReadOnlyList<Province> all = map.Provinces;
        IReadOnlyList<SeaRegion> seas = map.Seas;
        if (_seaStamp == null || _seaStamp.Length != seas.Count) { _seaStamp = new int[seas.Count]; _seaDepth = new int[seas.Count]; }

        _stamp++;
        _bfsQ.Clear();
        _bfsQ.Enqueue(from.Id);
        _bfsStamp[from.Id] = _stamp;
        _bfsPrev[from.Id] = -1;

        int visited = 0;
        while (_bfsQ.Count > 0)
        {
            int c = _bfsQ.Dequeue();
            if (c == to.Id) return true;
            if (++visited > maxSeaNodes) return false;
            Province pc = all[c];

            int[] nb = pc.Neighbors;
            for (int i = 0; i < nb.Length; i++) TryVisit(all, nb[i], c, to, restrict, avoid);

            IReadOnlyList<int> coast = map.GetCoastalSeas(pc);
            for (int k = 0; k < coast.Count; k++) FloodSea(all, seas, coast[k], c, to, restrict, avoid);
        }
        return false;
    }

    /// <summary>Floods the connected sea regions (up to seaHops) and links every allowed coast province to 'from'.</summary>
    private void FloodSea(IReadOnlyList<Province> all, IReadOnlyList<SeaRegion> seas, int start, int from, Province to,
                          bool restrict, HashSet<int> avoid)
    {
        if (start < 0 || start >= seas.Count || _seaStamp[start] == _stamp) return;
        _seaQ.Clear();
        _seaQ.Enqueue(start);
        _seaStamp[start] = _stamp;
        _seaDepth[start] = 0;

        while (_seaQ.Count > 0)
        {
            int si = _seaQ.Dequeue();
            SeaRegion s = seas[si];

            int[] land = s.LandNeighbors;
            for (int i = 0; i < land.Length; i++) TryVisit(all, land[i], from, to, restrict, avoid);

            if (_seaDepth[si] >= seaHops) continue;
            int[] sn = s.Neighbors;
            for (int n = 0; n < sn.Length; n++)
            {
                int t = sn[n];
                if (t < 0 || t >= seas.Count || _seaStamp[t] == _stamp) continue;
                _seaStamp[t] = _stamp;
                _seaDepth[t] = _seaDepth[si] + 1;
                _seaQ.Enqueue(t);
            }
        }
    }

    private void TryVisit(IReadOnlyList<Province> all, int m, int from, Province to, bool restrict, HashSet<int> avoid)
    {
        if (m < 0 || m >= all.Count || _bfsStamp[m] == _stamp) return;
        Province q = all[m];
        if (q.IsMerged) return;
        if (restrict && !Allowed(q)) return;
        if (avoid != null && m != to.Id && avoid.Contains(m)) return;
        _bfsStamp[m] = _stamp;
        _bfsPrev[m] = from;
        _bfsQ.Enqueue(m);
    }

    #endregion

    #region Tick / day

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
            _nextInspectorRefresh = Time.unscaledTime + Mathf.Max(0.25f, diagnosticsInterval);
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

    /// <summary>
    /// Real movement on the map: marching armies, retreating armies and reinforcement columns.
    /// A regiment in CONTACT with an enemy stands still (its battle may push it, see CombatManager).
    /// </summary>
    private void MoveRegiments(float deltaTime)
    {
        if (_moving.Count == 0) return;
        float tile = TileSize;
        _enteredR.Clear();
        _enteredP.Clear();

        for (int i = _moving.Count - 1; i >= 0; i--)
        {
            if (i >= _moving.Count) continue;
            Regiment r = _moving[i];
            if (r.Military == null || (r.State != RegimentState.Moving && r.State != RegimentState.Retreating))
            { SwapRemoveMoving(i); continue; }

            bool retreating = r.State == RegimentState.Retreating;
            if (r.InContact && !retreating) continue;

            float speed = retreating ? retreatSpeedTiles : r.IsColumn ? reinforceSpeedTiles : moveSpeedTiles;
            float step = speed * tile * deltaTime;
            if (allowSeaMovement && map != null && map.TryWorldToTile(r.WorldPosition, out int tx, out int ty) && !map.IsLand(tx, ty))
                step *= seaSpeedFactor;

            Province before = r.CurrentProvince;
            bool arrived = r.StepMove(step, this);
            Province cp = map != null ? map.GetProvinceAt(r.WorldPosition) : null;
            if (cp != null)
            {
                r.CurrentProvince = cp;
                if (!retreating && cp != before) { _enteredR.Add(r); _enteredP.Add(cp); }
            }

            if (arrived)
            {
                SwapRemoveMoving(i);
                r.CurrentProvince = r.Province;
                if (!r.IsColumn && r.Military != null) RefreshStacks(r.Military);
                OnRegimentArrived?.Invoke(r);
            }
        }
        _lodDirty = true;

        if (_enteredR.Count > 0)
        {
            for (int i = 0; i < _enteredR.Count; i++)
            {
                Regiment r = _enteredR[i];
                if (r.Military == null) continue;
                OnRegimentEnteredProvince?.Invoke(r, _enteredP[i]);
            }
            _enteredR.Clear();
            _enteredP.Clear();
        }
    }

    #endregion

    #region Visual sync: world -> screen (one Burst job)

    private readonly ProjectionBatch _proj = new ProjectionBatch();
    private readonly List<Regiment> _vis = new List<Regiment>(256);

    private bool _lodDirty = true;
    private Vector3 _lodPos;
    private Quaternion _lodRot;
    private float _lodZoom, _lodAspect, _nextForce;
    private int _lodW, _lodH;

    private void LateUpdate()
    {
        if (_list.Count == 0 || map == null) return;
        Camera cam = viewCamera ? viewCamera : Camera.main;
        if (cam == null) return;

        Transform ct = cam.transform;
        float zoom = cam.orthographic ? cam.orthographicSize : cam.fieldOfView;
        bool moved = ct.position != _lodPos || ct.rotation != _lodRot || zoom != _lodZoom ||
                     cam.aspect != _lodAspect || Screen.width != _lodW || Screen.height != _lodH;
        bool periodic = Time.unscaledTime >= _nextForce;
        if (!moved && !_lodDirty && !periodic) return;

        _lodPos = ct.position; _lodRot = ct.rotation; _lodZoom = zoom; _lodAspect = cam.aspect;
        _lodW = Screen.width; _lodH = Screen.height;
        _lodDirty = false;
        if (periodic) _nextForce = Time.unscaledTime + Mathf.Max(0.05f, visualRefreshInterval);

        RectTransform root = GetRoot();
        if (root == null) return;
        Canvas rc = screenCanvas.rootCanvas;
        bool overlay = rc.renderMode == RenderMode.ScreenSpaceOverlay;
        Camera canvasCam = overlay ? null : rc.worldCamera;
        float invScale = 1f / Mathf.Max(0.0001f, rc.scaleFactor);

        // zoom -> one consistent card size
        float visibleTiles = VisibleTiles(cam);
        MarkersVisible = markerMaxVisibleTiles <= 0f || visibleTiles <= markerMaxVisibleTiles;
        float raw = visibleTiles >= float.MaxValue * 0.5f ? minCardScale : referenceVisibleTiles / Mathf.Max(0.01f, visibleTiles);
        float scale = Mathf.Round(Mathf.Clamp(raw, minCardScale, maxCardScale) * 40f) / 40f;
        if (moved || !Mathf.Approximately(scale, CardScale)) ViewVersion++;
        CardScale = scale;

        bool showMarkers = MarkersVisible;
        bool showText = showMarkers && scale >= hideTextBelowScale;
        float pileStep = RegimentRect.y * scale * pileOffset;
        int pileDepth = Mathf.Max(0, pileMaxDepth);

        int upper = 0;
        for (int i = 0; i < _list.Count; i++) upper += _list[i].Regiments.Count + _list[i].Columns.Count;
        _proj.Reserve(upper);
        _vis.Clear();

        PlayerManager pm = PlayerManager.HasInstance ? PlayerManager.Instance : null;
        for (int i = 0; i < _list.Count; i++)
        {
            Military mil = _list[i];
            bool visible = showMarkers && (pm == null || pm.IsArmyVisible(mil.Nation));
            for (int pass = 0; pass < 2; pass++)
            {
                List<Regiment> regs = pass == 0 ? mil.Regiments : mil.Columns;
                for (int j = 0; j < regs.Count; j++)
                {
                    Regiment r = regs[j];
                    if (!visible || r.InRange) { if (r.View != null) ReleaseView(r); continue; }   // in battle: only the Combat View is shown
                    _proj.Set(_vis.Count, r.WorldPosition);
                    _vis.Add(r);
                }
            }
        }

        _proj.Run(cam, _vis.Count, Screen.width, Screen.height);

        float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
        for (int i = 0; i < _vis.Count; i++)
        {
            Regiment r = _vis[i];
            if (!_proj.IsVisible(i)) { if (r.View != null) ReleaseView(r); continue; }
            if (r.View == null && !AcquireView(r)) continue;


            Vector2 sp = _proj.Screen(i);
            Vector2 local;
            if (overlay) local = new Vector2((sp.x - cx) * invScale, (sp.y - cy) * invScale);
            else RectTransformUtility.ScreenPointToLocalPointInRectangle(root, sp, canvasCam, out local);

            bool travelling = r.IsColumn || r.State == RegimentState.Retreating || r.State == RegimentState.Moving || r.InContact;

            bool behind = !travelling && r.StackIndex > 0;
            if (behind)
            {
                float off = pileStep * Mathf.Min(r.StackIndex, pileDepth);
                local.x += off;
                local.y -= off;
            }
            if (behind != r.ViewBehind)
            {
                r.ViewBehind = behind;
                if (behind) r.View.transform.SetAsFirstSibling(); else r.View.transform.SetAsLastSibling();
            }

            r.View.SetScale(scale, columnScale);
            r.View.SetLocalPosition(local);
            r.View.SetTextVisible(showText);
        }
    }

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

    #endregion

    #region View pooling

    private bool AcquireView(Regiment r)
    {
        Transform root = GetRoot();
        if (root == null) return false;

        RegimentView v = PoolManager.Instance.Spawn(GetTemplate(), Vector3.zero, Quaternion.identity, root);
        if (v == null) return false;
        v.transform.localRotation = Quaternion.identity;
        v.transform.SetAsLastSibling();
        r.ViewBehind = false;
        r.View = v;
        v.Bind(r, this);
        return true;
    }

    private void ReleaseView(Regiment r)
    {
        RegimentView v = r.View;
        if (v == null) return;
        r.View = null;
        r.ViewBehind = false;
        if (PoolManager.HasInstance) PoolManager.Instance.Despawn(v.gameObject);
        else Destroy(v.gameObject);
    }

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

    #endregion

    #region Spawn / placement (pure logic, no UI)

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

    internal Vector3 ProvinceWorldPos(Province p)
    {
        if (map == null || p == null) return Vector3.zero;
        return map.TileToWorldCenter(Mathf.RoundToInt(p.Centroid.x), Mathf.RoundToInt(p.Centroid.y));
    }

    private readonly Dictionary<int, int> _stackCount = new Dictionary<int, int>();

    internal void RefreshStacks(Military m)
    {
        _stackCount.Clear();
        List<Regiment> regs = m.Regiments;
        for (int i = 0; i < regs.Count; i++)
        {
            Province loc = regs[i].LocationProvince;
            int key = loc != null ? loc.Id : -1;
            _stackCount.TryGetValue(key, out int k);
            regs[i].StackIndex = k;
            _stackCount[key] = k + 1;
        }
        _lodDirty = true;
    }

    #endregion

    #region Retreat

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
            ? CombatManager.Instance.FindSafeProvince(m.Nation, r.WorldPosition, r.CurrentProvince ?? r.Province)
            : null;

        if (p != null || TryPickRetreatProvince(r, out p))
        {
            r.BeginRetreat(p, ProvinceWorldPos(p));
            TrackMoving(r);
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

    #endregion

    #region Context menu (-1 = random nation / random regiment / random province)

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
        if (n == null || n.Provinces.Count == 0) return null;
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

    #endregion

    #region Diagnostics

    private void RefreshView()
    {
#if UNITY_EDITOR
        if (!showMilitaryInInspector) { militaryView.Clear(); return; }
        while (militaryView.Count > _list.Count) militaryView.RemoveAt(militaryView.Count - 1);
        while (militaryView.Count < _list.Count) militaryView.Add(new MilitaryInfo());

        for (int i = 0; i < _list.Count; i++)
        {
            Military m = _list[i];
            MilitaryInfo v = militaryView[i];
            v.nation = m.Nation.Name;
            v.maxManpower = m.MaxManpower;
            v.currentManpower = m.CurrentManpower;
            v.manpowerPercent = (float)m.ManpowerPercent;
            v.preparationNow = m.CurrentPreparation;
            v.preparationLevel = m.PreparationLevel;
            v.mobilized = m.MobilizedCap;
            v.regiments = m.RegimentCount;
            v.shares = SharesText(m);
        }
#endif
    }

    #endregion
}