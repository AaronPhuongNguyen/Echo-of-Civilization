using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

#region Enums & status types

public enum PlayerMode : byte { Normal, Invest, Military }
public enum SelectionKind : byte { None, Regiments, Province }

public struct WarLine
{
    public string Enemy;
    public bool Attacker;
    public float MyProgress, TheirProgress;
    public int Days;
}

/// <summary>
/// QUICK ACCESS to everything about the player's nation. Cached and refreshed every PlayerManager.statusInterval.
/// The UI only reads this object (PlayerManager.Instance.Status) or listens to PlayerEvents.OnStatusChanged.
/// </summary>
public sealed partial class PlayerStatus
{
    // nation
    public bool HasPlayer;
    public string NationName = "", CapitalName = "";
    public Color NationColor = Color.white;
    public int Provinces, Area, QueuedIncreases;
    public double Population, Economy, Treasury, MinTreasury, MaxTreasury, DailyIncome;
    public float AvgDevastation, AvgDefend, AvgInfrastructure;

    // military
    public bool HasMilitary;
    public double ManpowerCurrent, ManpowerMax, ManpowerPercent, Mobilized, RegimentMen, AffordableMen;
    public int Regiments, Columns, PreparationLevel;
    public float PreparationNow, AvgStrength;

    // war
    public bool AtWar;
    public readonly List<WarLine> Wars = new List<WarLine>(4);

    // input state
    public PlayerMode Mode;
    public bool InvestStatChosen, AdditiveToggle, PathDrawArmed;
    public StatType InvestStat;
    public int SelectedRegiments;
    public string SelectedProvinceName = "";

    // price of the NEXT increase of every stat (selected own province, otherwise the capital)
    public string PriceProvinceName = "";
    public readonly double[] NextPrice = new double[ProvinceUpgrades.StatCount];

    public void ResetNation()
    {
        HasPlayer = false; NationName = CapitalName = ""; Provinces = Area = QueuedIncreases = 0;
        Population = Economy = Treasury = MinTreasury = MaxTreasury = DailyIncome = 0.0;
        AvgDevastation = AvgDefend = AvgInfrastructure = 0f;
        HasMilitary = false; ManpowerCurrent = ManpowerMax = ManpowerPercent = Mobilized = RegimentMen = AffordableMen = 0.0;
        Regiments = Columns = PreparationLevel = 0; PreparationNow = AvgStrength = 0f;
        AtWar = false; Wars.Clear(); PriceProvinceName = "";
        Array.Clear(NextPrice, 0, NextPrice.Length);
    }
}

#endregion

#region PlayerEvents (intents + results)

/// <summary>
/// Buttons, keys and touch only RAISE the "intent" events. PlayerManager (or anything else) LISTENS and acts.
/// "Result" events tell the UI what happened.
/// </summary>
public static partial class PlayerEvents
{
    // ── intents ──
    public static event Action<PlayerMode> OnSetMode;
    public static event Action<StatType> OnSelectInvestStat;
    public static event Action<Province, StatType> OnInvest;
    public static event Action<Vector2> OnInvestAt;                    // screen position (click on a province)
    public static event Action<StatType> OnInvestAll;
    public static event Action<Vector2, bool> OnSelectAt;              // screen position, additive
    public static event Action<Rect, bool> OnSelectBox;                // screen rect, additive
    public static event Action OnSelectAllRegiments;
    public static event Action OnClearSelection;
    public static event Action<Vector2> OnSecondaryAt;                 // right click / long press
    public static event Action<Province> OnMoveSelected;
    public static event Action<List<Province>> OnMoveSelectedVia;
    public static event Action OnRecallSelected;
    public static event Action OnReturnToNation;
    public static event Action<int> OnSetPreparation;

    // ── results / state ──
    public static event Action<PlayerMode> OnModeChanged;
    public static event Action<bool, StatType> OnInvestStatChanged;    // chosen?, stat
    public static event Action OnSelectionChanged;
    public static event Action<Province, StatType, bool, string> OnInvestResult;
    public static event Action<StatType, int, double> OnInvestAllResult;   // stat, increases queued, money spent
    public static event Action<string, int, int> OnCommandResult;      // what, ok, failed
    public static event Action<Rect, bool> OnBoxSelectVisual;          // draw / hide the selection rectangle
    public static event Action<IReadOnlyList<Vector3>, bool> OnPathVisual;   // draw / hide the path being drawn
    public static event Action<PlayerStatus> OnStatusChanged;
    public static event Action<string> OnMessage;                      // short text for a toast

    public static void RaiseSetMode(PlayerMode m) => OnSetMode?.Invoke(m);
    public static void RaiseSelectInvestStat(StatType s) => OnSelectInvestStat?.Invoke(s);
    public static void RaiseInvest(Province p, StatType s) => OnInvest?.Invoke(p, s);
    public static void RaiseInvestAt(Vector2 screen) => OnInvestAt?.Invoke(screen);
    public static void RaiseInvestAll(StatType s) => OnInvestAll?.Invoke(s);
    public static void RaiseSelectAt(Vector2 screen, bool additive) => OnSelectAt?.Invoke(screen, additive);
    public static void RaiseSelectBox(Rect r, bool additive) => OnSelectBox?.Invoke(r, additive);
    public static void RaiseSelectAllRegiments() => OnSelectAllRegiments?.Invoke();
    public static void RaiseClearSelection() => OnClearSelection?.Invoke();
    public static void RaiseSecondaryAt(Vector2 screen) => OnSecondaryAt?.Invoke(screen);
    public static void RaiseMoveSelected(Province p) => OnMoveSelected?.Invoke(p);
    public static void RaiseMoveSelectedVia(List<Province> list) => OnMoveSelectedVia?.Invoke(list);
    public static void RaiseRecallSelected() => OnRecallSelected?.Invoke();
    public static void RaiseReturnToNation() => OnReturnToNation?.Invoke();
    public static void RaiseSetPreparation(int level) => OnSetPreparation?.Invoke(level);

    public static void RaiseModeChanged(PlayerMode m) => OnModeChanged?.Invoke(m);
    public static void RaiseInvestStatChanged(bool chosen, StatType s) => OnInvestStatChanged?.Invoke(chosen, s);
    public static void RaiseSelectionChanged() => OnSelectionChanged?.Invoke();
    public static void RaiseInvestResult(Province p, StatType s, bool ok, string err) => OnInvestResult?.Invoke(p, s, ok, err);
    public static void RaiseInvestAllResult(StatType s, int count, double spent) => OnInvestAllResult?.Invoke(s, count, spent);
    public static void RaiseCommandResult(string what, int ok, int failed) => OnCommandResult?.Invoke(what, ok, failed);
    public static void RaiseBoxSelectVisual(Rect r, bool active) => OnBoxSelectVisual?.Invoke(r, active);
    public static void RaisePathVisual(IReadOnlyList<Vector3> pts, bool active) => OnPathVisual?.Invoke(pts, active);
    public static void RaiseStatusChanged(PlayerStatus s) => OnStatusChanged?.Invoke(s);
    public static void RaiseMessage(string text) => OnMessage?.Invoke(text);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        OnSetMode = null; OnSelectInvestStat = null; OnInvest = null; OnInvestAt = null; OnInvestAll = null;
        OnSelectAt = null; OnSelectBox = null; OnSelectAllRegiments = null; OnClearSelection = null; OnSecondaryAt = null;
        OnMoveSelected = null; OnMoveSelectedVia = null; OnRecallSelected = null; OnReturnToNation = null; OnSetPreparation = null;
        OnModeChanged = null; OnInvestStatChanged = null; OnSelectionChanged = null; OnInvestResult = null; OnInvestAllResult = null;
        OnCommandResult = null; OnBoxSelectVisual = null; OnPathVisual = null; OnStatusChanged = null; OnMessage = null;
    }
}

#endregion

// ─────────────────────────────────────────────────────────────────────────────
//  PLAYER MANAGER  (PlayerManager.Instance)
//   - which nation the player controls (Nation.IsPlayer)
//   - THE input controller (keyboard / mouse / touch). It raises EventBus events for the camera / time
//     and PlayerEvents for everything the player does.
//   - modes (Normal / Invest / Military), selection (Regiment > Province > none), invest + military commands
//   - PlayerStatus: quick access for the UI
// ─────────────────────────────────────────────────────────────────────────────
[DefaultExecutionOrder(-1050)]
public partial class PlayerManager : TickBehaviour
{
    #region Singleton

    private static PlayerManager _instance;
    private static bool _quitting;
    public static bool HasInstance => _instance != null;

    public static PlayerManager Instance
    {
        get
        {
            if (_instance == null && !_quitting)
            {
#if UNITY_2022_2_OR_NEWER
                _instance = FindFirstObjectByType<PlayerManager>();
#else
                _instance = FindObjectOfType<PlayerManager>();
#endif
                if (_instance == null)
                    _instance = new GameObject("PlayerManager").AddComponent<PlayerManager>();
            }
            return _instance;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() { _instance = null; _quitting = false; }

    #endregion

    #region Inspector

    [Header("References")]
    [Tooltip("Defaults to the MapManager found in the scene.")]
    [SerializeField] private MapManager map;
    [Tooltip("Defaults to Camera.main.")]
    [SerializeField] private Camera cam;

    [Header("Player nation")]
    [Tooltip("Nation id the player controls when the map is generated. -1 = random nation.")]
    [SerializeField] private int startNationId = -1;
    [Tooltip("ON: nobody is the player (every nation is AI, spectator mode).")]
    [SerializeField] private bool spectator = false;
    [Tooltip("On: Only render the army of a Side who is in war with player (Ally will always render), if spectator mode, render the war side only")]
    public bool useFogOfWar = true;
    [Tooltip("Read-only mirror (Editor).")]
    [SerializeField] private string playerNationName;

    [Header("Keyboard: time / camera")]
    [SerializeField] private bool enableKeyboard = true;
    [SerializeField] private KeyCode pauseKey = KeyCode.Space;
    [SerializeField] private KeyCode speedUpKey = KeyCode.RightBracket;
    [SerializeField] private KeyCode speedDownKey = KeyCode.LeftBracket;
    [SerializeField] private KeyCode focusCapitalKey = KeyCode.F;
    [SerializeField] private KeyCode[] zoomInKeys = { KeyCode.Equals, KeyCode.Plus, KeyCode.KeypadPlus };
    [SerializeField] private KeyCode[] zoomOutKeys = { KeyCode.Minus, KeyCode.KeypadMinus };

    [Header("Keyboard: modes (hold CTRL)")]
    [SerializeField] private KeyCode investKey = KeyCode.I;
    [SerializeField] private KeyCode militaryKey = KeyCode.M;
    [SerializeField] private KeyCode returnKey = KeyCode.R;
    [SerializeField] private KeyCode selectAllKey = KeyCode.A;      // CTRL + A in Military mode
    [SerializeField] private KeyCode cancelKey = KeyCode.Escape;
    [Header("Keyboard: inside a mode")]
    [SerializeField] private KeyCode investAllKey = KeyCode.A;      // Invest mode, stat chosen
    [SerializeField] private KeyCode recallKey = KeyCode.H;         // Military mode
    [SerializeField] private KeyCode prepUpKey = KeyCode.Period;
    [SerializeField] private KeyCode prepDownKey = KeyCode.Comma;

    [Header("Invest")]
    [Tooltip("Number key 1..9 -> stat. Extend this array for more stats.")]
    [SerializeField]
    private StatType[] investSlots =
        { StatType.PopGrowth, StatType.Tax, StatType.Economy, StatType.Infrastructure, StatType.Defend };
    [Tooltip("Invest never spends the treasury below this value (0 = never into debt).")]
    [SerializeField] private double investKeepTreasury = 0.0;
    [Tooltip("ON: investing may use the debt allowance of the treasury (MinTreasury).")]
    [SerializeField] private bool allowDebt = false;
    [Tooltip("Safety cap of the 'invest in everything' loop (attempts).")]
    [SerializeField, Min(10)] private int investAllMaxSteps = 4000;

    [Header("Pointer")]
    [SerializeField] private bool simulateTouchWithMouse = true;
    [Tooltip("Hold a finger (or left mouse button) still this long to raise a long press.")]
    [SerializeField] private float longPressSeconds = 0.5f;
    [Tooltip("Movement (pixels) that still counts as 'not moving'.")]
    [SerializeField] private float longPressMoveTolerance = 15f;

    [Header("Selection / commands")]
    [Tooltip("ON: in Military mode a drag draws a selection box (camera: keys, wheel, pinch). OFF: drag always pans.")]
    [SerializeField] private bool militaryDragSelects = true;
    [Tooltip("Pick radius (screen pixels, multiplied by the card scale) around a regiment card.")]
    [SerializeField, Min(4f)] private float regimentPickPixels = 32f;
    [Tooltip("Drawn paths are thinned to at most this many waypoints.")]
    [SerializeField, Min(2)] private int maxPathWaypoints = 8;
    [Tooltip("A new path sample is taken every time the finger moved this many tiles.")]
    [SerializeField, Min(0.1f)] private float pathSampleTiles = 1.5f;
    [SerializeField] private bool logSelection = false;

    [Header("Status (quick access for the UI)")]
    [SerializeField, Min(0.05f)] private float statusInterval = 0.25f;

    protected override bool IgnoreGameSpeed => true;   // input keeps working while paused
    protected override int TickPriority => -100;       // before every subscriber that ticks

    #endregion

    #region State

    public Nation PlayerNation { get; private set; }
    public bool HasPlayer => PlayerNation != null;

    /// <summary>oldPlayerNation, newPlayerNation (either can be null)</summary>
    public event Action<Nation, Nation> OnPlayerChanged;

    public bool IsPlayer(Nation n) => n != null && n == PlayerNation;
    public bool IsAI(Nation n) => n != null && n != PlayerNation;

    public PlayerStatus Status { get; } = new PlayerStatus();

    private PlayerMode _mode = PlayerMode.Normal;
    private bool _hasInvestStat;
    private StatType _investStat;
    private bool _additiveToggle, _pathArmed;

    public PlayerMode Mode => _mode;
    public bool HasInvestStat => _hasInvestStat;
    public StatType InvestStat => _investStat;
    public bool AdditiveHeld => ShiftKey() || _additiveToggle;
    public bool PathDrawArmed => _pathArmed;

    private readonly List<Regiment> _selected = new List<Regiment>(16);
    public IReadOnlyList<Regiment> SelectedRegiments => _selected;
    public Province SelectedProvince { get; private set; }
    public SelectionKind Selection =>
        _selected.Count > 0 ? SelectionKind.Regiments : SelectedProvince != null ? SelectionKind.Province : SelectionKind.None;

    private MapManager _subscribedMap;
    private bool _armyHooked;
    private float _nextStatus;

    private WorldManager World => WorldManager.Instance;
    private ArmyManager Army => ArmyManager.Instance;
    private WarManager WM => WarManager.Instance;

    // pointer state
    private enum DragIntent : byte { Camera, Box, Path }
    private DragIntent _intent;
    private bool _dragging, _pressMoved, _longFired;
    private int _fingerId = -1;
    private Vector2 _lastPos, _pressStart, _lastAxis;
    private float _pressHeld, _lastZoomAxis;
    private bool _pinching;
    private float _lastPinchDist;

    private readonly List<Vector3> _pathPts = new List<Vector3>(64);
    private readonly List<Province> _wpBuf = new List<Province>(16);
    private readonly List<Vector3> _lineBuf = new List<Vector3>(16);

    private static readonly KeyCode[] AllKeys = (KeyCode[])Enum.GetValues(typeof(KeyCode));

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

        WorldManager w = WorldManager.Instance;
        w.OnNationsCleared += HandleCleared;
        w.OnNationRemoved += HandleRemoved;

        if (cam == null) cam = Camera.main;
        if (map == null)
        {
#if UNITY_2022_2_OR_NEWER
            map = FindFirstObjectByType<MapManager>();
#else
            map = FindObjectOfType<MapManager>();
#endif
        }
        if (map != null)
        {
            _subscribedMap = map;
            map.OnMapGenerated += PickStart;
            map.OnProvincesMerged += HandleMerged;
            if (map.IsReady && PlayerNation == null) PickStart();
        }
        SubscribeIntents();
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        if (_dragging) CancelPress();
        _pinching = false;

        UnsubscribeIntents();
        if (WorldManager.HasInstance)
        {
            WorldManager.Instance.OnNationsCleared -= HandleCleared;
            WorldManager.Instance.OnNationRemoved -= HandleRemoved;
        }
        if (_subscribedMap != null)
        {
            _subscribedMap.OnMapGenerated -= PickStart;
            _subscribedMap.OnProvincesMerged -= HandleMerged;
        }
        _subscribedMap = null;
        if (_armyHooked && ArmyManager.HasInstance) ArmyManager.Instance.OnRegimentRemoved -= HandleRegimentRemoved;
        _armyHooked = false;
        if (_instance == this && _quitting) _instance = null;
    }

    private void OnDestroy() { if (_instance == this) _instance = null; }

    private void SubscribeIntents()
    {
        PlayerEvents.OnSetMode += HandleSetMode;
        PlayerEvents.OnSelectInvestStat += HandleSelectInvestStat;
        PlayerEvents.OnInvest += HandleInvest;
        PlayerEvents.OnInvestAt += HandleInvestAt;
        PlayerEvents.OnInvestAll += HandleInvestAll;
        PlayerEvents.OnSelectAt += HandleSelectAt;
        PlayerEvents.OnSelectBox += HandleSelectBox;
        PlayerEvents.OnSelectAllRegiments += HandleSelectAllRegiments;
        PlayerEvents.OnClearSelection += HandleClearSelection;
        PlayerEvents.OnSecondaryAt += HandleSecondaryAt;
        PlayerEvents.OnMoveSelected += HandleMoveSelected;
        PlayerEvents.OnMoveSelectedVia += HandleMoveSelectedVia;
        PlayerEvents.OnRecallSelected += HandleRecall;
        PlayerEvents.OnReturnToNation += HandleReturnToNation;
        PlayerEvents.OnSetPreparation += HandleSetPreparation;
        SubscribeArmyIntents();
    }

    private void UnsubscribeIntents()
    {
        PlayerEvents.OnSetMode -= HandleSetMode;
        PlayerEvents.OnSelectInvestStat -= HandleSelectInvestStat;
        PlayerEvents.OnInvest -= HandleInvest;
        PlayerEvents.OnInvestAt -= HandleInvestAt;
        PlayerEvents.OnInvestAll -= HandleInvestAll;
        PlayerEvents.OnSelectAt -= HandleSelectAt;
        PlayerEvents.OnSelectBox -= HandleSelectBox;
        PlayerEvents.OnSelectAllRegiments -= HandleSelectAllRegiments;
        PlayerEvents.OnClearSelection -= HandleClearSelection;
        PlayerEvents.OnSecondaryAt -= HandleSecondaryAt;
        PlayerEvents.OnMoveSelected -= HandleMoveSelected;
        PlayerEvents.OnMoveSelectedVia -= HandleMoveSelectedVia;
        PlayerEvents.OnRecallSelected -= HandleRecall;
        PlayerEvents.OnReturnToNation -= HandleReturnToNation;
        PlayerEvents.OnSetPreparation -= HandleSetPreparation;
        UnsubscribeArmyIntents();
    }

    public override void Tick(float dt)
    {
        if (enableKeyboard) ReadKeyboard();
        ReadWheel();
        if (!ReadPinch()) ReadPointer(dt);
        TickArmyExtras(dt);
        TickDebug();

        if (!_armyHooked && ArmyManager.HasInstance)
        {
            _armyHooked = true;
            ArmyManager.Instance.OnRegimentRemoved += HandleRegimentRemoved;
        }
        if (Time.unscaledTime >= _nextStatus) RefreshStatus();
    }

    #endregion

    #region Player nation

    private void PickStart()
    {
        if (spectator) { SetPlayer(null); return; }
        WorldManager w = WorldManager.Instance;
        Nation n = startNationId >= 0 ? w.GetNation(startNationId) : null;
        if (n == null || n.IsEmpty) n = w.GetRandomNation();
        SetPlayer(n);
    }

    /// <summary>Fog of War: should the army cards of 'n' be drawn? Called by ArmyManager (cheap dictionary lookups only).</summary>
    public bool IsArmyVisible(Nation n)
    {
        if (n == null) return false;
        if (!useFogOfWar) return true;

        WarManager wm = WarManager.HasInstance ? WarManager.Instance : null;
        if (wm == null) return PlayerNation == null || n == PlayerNation;

        Nation p = PlayerNation;
        if (p == null) return wm.IsAtWar(n);            // spectator: only the war sides
        if (n == p) return true;
        if (wm.IsAtWar(p, n)) return true;              // enemy of the player

        if (RelationshipManager.HasInstance)            // ally / overlord / vassal
        {
            RelationshipManager rel = RelationshipManager.Instance;
            if (rel.IsAllied(p, n) || rel.IsVassalOf(n, p) || rel.IsVassalOf(p, n)) return true;
        }

        IReadOnlyList<War> wars = wm.GetWars(p);        // same side in a war
        for (int i = 0; i < wars.Count; i++)
            if (wars[i].SideOf(n.Id) == wars[i].SideOf(p.Id)) return true;
        return false;
    }

    /// <summary>Makes 'n' the player nation (null = no player). The old player nation becomes AI.</summary>
    public bool SetPlayer(Nation n)
    {
        if (n != null && !(WorldManager.Instance.TryGetNation(n.Id, out Nation reg) && reg == n)) return false;
        Nation old = PlayerNation;
        if (old == n) return true;

        if (old != null) { old.IsPlayer = false; old.IsAI = true; }
        PlayerNation = n;
        if (n != null) { n.IsPlayer = true; n.IsAI = false; }
        playerNationName = n != null ? n.Name : "";

        ClearSelectionSilently();
        SwitchMode(PlayerMode.Normal);
        _nextStatus = 0f;
        OnPlayerChanged?.Invoke(old, n);
        return true;
    }

    public void ClearPlayer() => SetPlayer(null);

    private void HandleCleared()
    {
        PlayerNation = null;
        playerNationName = "";
        ClearSelectionSilently();
        SwitchMode(PlayerMode.Normal);
        _nextStatus = 0f;
    }

    private void HandleRemoved(Nation n)
    {
        if (n == PlayerNation)
        {
            n.IsPlayer = false; PlayerNation = null; playerNationName = "";
            ClearSelectionSilently();
            SwitchMode(PlayerMode.Normal);
            OnPlayerChanged?.Invoke(n, null);
        }
    }

    private void HandleMerged(Province origin, IReadOnlyList<Province> absorbed)
    {
        if (SelectedProvince != null && SelectedProvince.IsMerged)
        {
            SelectedProvince = map.GetProvince(SelectedProvince.MergedInto);
            SelectionChanged();
        }
    }

    private void HandleRegimentRemoved(Regiment r, bool destroyed)
    {
        if (_selected.Remove(r)) SelectionChanged();
    }

    #endregion

    #region Public API (bind these to UI buttons: they ONLY raise events)

    // ── modes ──
    public void RequestMode(int mode) => PlayerEvents.RaiseSetMode((PlayerMode)Mathf.Clamp(mode, 0, 2));
    public void RequestNormalMode() => PlayerEvents.RaiseSetMode(PlayerMode.Normal);
    public void RequestInvestMode() => PlayerEvents.RaiseSetMode(PlayerMode.Invest);
    public void RequestMilitaryMode() => PlayerEvents.RaiseSetMode(PlayerMode.Military);
    public void RequestToggleInvestMode() => PlayerEvents.RaiseSetMode(_mode == PlayerMode.Invest ? PlayerMode.Normal : PlayerMode.Invest);
    public void RequestToggleMilitaryMode() => PlayerEvents.RaiseSetMode(_mode == PlayerMode.Military ? PlayerMode.Normal : PlayerMode.Military);

    // ── invest ──
    /// <summary>Stat by index (StatType value): 0 Tax, 1 Economy, 2 Defend, 3 PopGrowth, 4 Infrastructure.</summary>
    public void RequestInvestStat(int statIndex) => PlayerEvents.RaiseSelectInvestStat((StatType)Mathf.Clamp(statIndex, 0, ProvinceUpgrades.StatCount - 1));
    /// <summary>Slot = the number key minus one (0 = key "1").</summary>
    public void RequestInvestSlot(int slot)
    {
        if (slot < 0 || slot >= investSlots.Length) { PlayerEvents.RaiseSetMode(PlayerMode.Normal); return; }   // invalid -> exit mode
        PlayerEvents.RaiseSelectInvestStat(investSlots[slot]);
    }
    public void RequestInvestPopGrowth() => PlayerEvents.RaiseSelectInvestStat(StatType.PopGrowth);
    public void RequestInvestTax() => PlayerEvents.RaiseSelectInvestStat(StatType.Tax);
    public void RequestInvestEconomy() => PlayerEvents.RaiseSelectInvestStat(StatType.Economy);
    public void RequestInvestInfrastructure() => PlayerEvents.RaiseSelectInvestStat(StatType.Infrastructure);
    public void RequestInvestDefend() => PlayerEvents.RaiseSelectInvestStat(StatType.Defend);
    /// <summary>Invest the chosen stat in ALL provinces equally until the treasury is used up.</summary>
    public void RequestInvestAll() { if (_hasInvestStat) PlayerEvents.RaiseInvestAll(_investStat); else PlayerEvents.RaiseMessage("Choose a stat first."); }
    /// <summary>One increase in the SELECTED province using the chosen stat.</summary>
    public void RequestInvestSelected()
    {
        if (!_hasInvestStat) { PlayerEvents.RaiseMessage("Choose a stat first."); return; }
        if (SelectedProvince != null) PlayerEvents.RaiseInvest(SelectedProvince, _investStat);
    }
    public void RequestInvestProvince(Province p, StatType stat) => PlayerEvents.RaiseInvest(p, stat);

    // ── selection ──
    public void RequestSelectAll() => PlayerEvents.RaiseSelectAllRegiments();
    public void RequestClearSelection() => PlayerEvents.RaiseClearSelection();
    public void RequestToggleAdditive() { _additiveToggle = !_additiveToggle; _nextStatus = 0f; }
    public void RequestTogglePathDraw() { _pathArmed = !_pathArmed; _nextStatus = 0f; }

    // ── military ──
    public void RequestMoveSelected(Province p) => PlayerEvents.RaiseMoveSelected(p);
    public void RequestRecall() => PlayerEvents.RaiseRecallSelected();
    public void RequestSetPreparation(int level) => PlayerEvents.RaiseSetPreparation(level);
    public void RequestPrepUp() => PlayerEvents.RaiseSetPreparation(Status.PreparationLevel + 1);
    public void RequestPrepDown() => PlayerEvents.RaiseSetPreparation(Status.PreparationLevel - 1);

    // ── camera ──
    /// <summary>Camera returns to the capital of the player's nation (CTRL + R).</summary>
    public void RequestReturnToNation() => PlayerEvents.RaiseReturnToNation();

    #endregion

    #region Input: keyboard / wheel / pinch

    private static bool CtrlKey() =>
        Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ||
        Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);

    private static bool ShiftKey() => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

    private void ReadKeyboard()
    {
        if (Input.GetKeyDown(pauseKey)) EventBus.RaiseTogglePause();
        if (Input.GetKeyDown(speedUpKey)) EventBus.RaiseSpeedUp();
        if (Input.GetKeyDown(speedDownKey)) EventBus.RaiseSpeedDown();
        if (Input.GetKeyDown(focusCapitalKey) && !(DebugMode && CtrlKey()))
        {
            bool hasMouse = Input.mousePresent && !Application.isMobilePlatform;
            EventBus.RaiseFocusCapital(Input.mousePosition, hasMouse);
        }

        bool ctrl = CtrlKey();
        bool consumed = ctrl && ReadCtrlCombos();
        if (!consumed) ReadModeKeys();

        // keys that double as camera axes (A) must not pan while they are used as commands
        bool suppressAxis = ctrl || (_mode == PlayerMode.Invest && Input.GetKey(investAllKey));
        Vector2 axis = suppressAxis ? Vector2.zero : new Vector2(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical"));
        if (axis.sqrMagnitude > 1f) axis.Normalize();
        if (axis != Vector2.zero || _lastAxis != Vector2.zero) EventBus.RaiseMoveAxis(axis);
        _lastAxis = axis;

        float z = 0f;
        if (AnyKey(zoomInKeys)) z += 1f;
        if (AnyKey(zoomOutKeys)) z -= 1f;
        if (z != 0f || _lastZoomAxis != 0f) EventBus.RaiseZoomAxis(z);
        _lastZoomAxis = z;
    }

    /// <summary>CTRL + I / M / R / A / number. Returns true when a combo was used.</summary>
    private bool ReadCtrlCombos()
    {
        if (Input.GetKeyDown(investKey)) { RequestToggleInvestMode(); return true; }
        if (Input.GetKeyDown(militaryKey)) { RequestToggleMilitaryMode(); return true; }
        if (Input.GetKeyDown(returnKey)) { RequestReturnToNation(); return true; }
        if (_mode == PlayerMode.Military && Input.GetKeyDown(selectAllKey)) { RequestSelectAll(); return true; }
        int n = NumberKeyDown();
        if (_mode == PlayerMode.Military && ShiftKey())          // CTRL+SHIFT+1..9 = save control group
        { int g = NumberKeyDown(); if (g >= 0) { RequestSaveGroup(g); return true; } }
        if (n >= 0) { RequestInvestSlot(n); return true; }            // CTRL + number: straight into that invest mode
        return false;
    }

    private void ReadModeKeys()
    {
        if (Input.GetKeyDown(cancelKey))
        {
            if (_mode != PlayerMode.Normal) RequestNormalMode(); else RequestClearSelection();
            return;
        }

        if (_mode == PlayerMode.Invest)
        {
            int n = NumberKeyDown();
            if (n >= 0) { RequestInvestSlot(n); return; }
            if (_hasInvestStat) { if (Input.GetKeyDown(investAllKey)) RequestInvestAll(); }
            else if (AnyInvalidKeyDown()) RequestNormalMode();          // waiting for a number: anything else exits
        }
        else if (_mode == PlayerMode.Military)
        {
            if (Input.GetKeyDown(recallKey)) RequestRecall();
            if (Input.GetKeyDown(prepUpKey)) RequestPrepUp();
            if (Input.GetKeyDown(prepDownKey)) RequestPrepDown();
        }
        ReadArmyKeys();
    }

    /// <summary>0..8 for the number keys 1..9 (top row or keypad), -1 when none was pressed.</summary>
    private static int NumberKeyDown()
    {
        for (int i = 0; i < 9; i++)
            if (Input.GetKeyDown(KeyCode.Alpha1 + i) || Input.GetKeyDown(KeyCode.Keypad1 + i)) return i;
        return -1;
    }

    /// <summary>A real key (not modifier / mouse / joystick / time key) went down this frame.</summary>
    private bool AnyInvalidKeyDown()
    {
        if (!Input.anyKeyDown) return false;
        for (int i = 0; i < AllKeys.Length; i++)
        {
            KeyCode k = AllKeys[i];
            if (k == KeyCode.None || (int)k >= (int)KeyCode.Mouse0) continue;      // mouse + joystick
            if (k == KeyCode.LeftShift || k == KeyCode.RightShift || k == KeyCode.LeftControl || k == KeyCode.RightControl ||
                k == KeyCode.LeftAlt || k == KeyCode.RightAlt || k == KeyCode.LeftCommand || k == KeyCode.RightCommand ||
                k == KeyCode.CapsLock || k == pauseKey || k == speedUpKey || k == speedDownKey || k == focusCapitalKey) continue;
            if (Input.GetKeyDown(k)) return true;
        }
        return false;
    }

    private static bool AnyKey(KeyCode[] keys)
    {
        for (int i = 0; i < keys.Length; i++) if (Input.GetKey(keys[i])) return true;
        return false;
    }

    private void ReadWheel()
    {
        float wheel = Input.mouseScrollDelta.y;
        if (Mathf.Abs(wheel) > 0.001f) EventBus.RaiseZoomWheel(wheel);
    }

    /// <summary>2 fingers. Returns true while pinching so the pointer logic is skipped.</summary>
    private bool ReadPinch()
    {
        if (Input.touchCount >= 2)
        {
            float dist = Vector2.Distance(Input.GetTouch(0).position, Input.GetTouch(1).position);
            if (!_pinching)
            {
                _pinching = true;
                _lastPinchDist = dist;
                if (_dragging) CancelPress();            // a pinch cancels the drag
            }
            else
            {
                float diff = dist - _lastPinchDist;
                _lastPinchDist = dist;
                if (diff != 0f) EventBus.RaisePinch(diff);
            }
            return true;
        }
        _pinching = false;
        return false;
    }

    #endregion

    #region Input: pointer (1 finger / left mouse) + right click

    private void ReadPointer(float dt)
    {
        if (Input.mousePresent && !Application.isMobilePlatform && Input.GetMouseButtonDown(1) && !IsOverUI(-1))
            PlayerEvents.RaiseSecondaryAt(Input.mousePosition);                 // right click

        bool got = TryGetPointer(out Vector2 pos, out bool began, out bool ended);
        if (got)
        {
            if (began) BeginPress(pos);
            if (_dragging) MovePress(pos, dt);
            if (ended && _dragging) EndPress(pos);
        }
        else if (_dragging) CancelPress();                                      // pointer lost
    }

    private void BeginPress(Vector2 pos)
    {
        _dragging = true;
        _lastPos = pos; _pressStart = pos;
        _pressHeld = 0f; _pressMoved = false; _longFired = false;

        if (DebugBlocksDrag || _mode != PlayerMode.Military || !militaryDragSelects) _intent = DragIntent.Camera;
        else if ((ShiftKey() || _pathArmed) && OwnSelectedCount() > 0) _intent = DragIntent.Path;
        else _intent = DragIntent.Box;

        if (_intent == DragIntent.Camera) EventBus.RaiseDragBegin(pos);
        else if (_intent == DragIntent.Path) { _pathPts.Clear(); _pathPts.Add(ScreenToWorld(pos)); }
    }

    private void MovePress(Vector2 pos, float dt)
    {
        Vector2 delta = pos - _lastPos;
        _lastPos = pos;
        if (!_pressMoved && Vector2.Distance(pos, _pressStart) > longPressMoveTolerance) _pressMoved = true;

        switch (_intent)
        {
            case DragIntent.Camera:
                if (delta != Vector2.zero) EventBus.RaiseDragMove(delta);
                break;
            case DragIntent.Box:
                if (_pressMoved && !_longFired) PlayerEvents.RaiseBoxSelectVisual(MakeRect(_pressStart, pos), true);
                break;
            case DragIntent.Path:
                if (_pressMoved && !_longFired) SamplePath(pos);
                break;
        }
        UpdateLongPress(pos, dt);
    }

    private void UpdateLongPress(Vector2 pos, float dt)
    {
        if (_pressMoved || _longFired) return;
        _pressHeld += dt;
        if (_pressHeld < longPressSeconds) return;

        _longFired = true;                                                      // once per press
        if (_mode == PlayerMode.Military && OwnSelectedCount() > 0) PlayerEvents.RaiseSecondaryAt(pos);   // = right click on touch
        else
        {
            EventBus.RaiseLongPress(pos);
            EventBus.RaiseProvinceLongPressed(ScreenToWorld(pos), ProvinceAt(pos));
        }
    }

    private void EndPress(Vector2 pos)
    {
        bool tap = !_pressMoved && !_longFired && _pressHeld < longPressSeconds;
        DragIntent intent = _intent;
        bool moved = _pressMoved && !_longFired;
        bool additive = AdditiveHeld;
        Vector2 start = _pressStart;

        _dragging = false;
        _fingerId = -1;

        switch (intent)
        {
            case DragIntent.Camera:
                EventBus.RaiseDragEnd();
                break;
            case DragIntent.Box:
                if (moved) { PlayerEvents.RaiseBoxSelectVisual(default, false); PlayerEvents.RaiseSelectBox(MakeRect(start, pos), additive); }
                break;
            case DragIntent.Path:
                PlayerEvents.RaisePathVisual(_pathPts, false);
                if (moved) CommitPath();
                break;
        }

        if (tap)
        {
            EventBus.RaiseTap(pos);
            if (DebugTap(pos, additive)) { }                       // debug click mode consumed it
            else if (_mode == PlayerMode.Invest) PlayerEvents.RaiseInvestAt(pos);
            else PlayerEvents.RaiseSelectAt(pos, additive);
        }
    }

    private void CancelPress()
    {
        _dragging = false;
        _fingerId = -1;
        if (_intent == DragIntent.Camera) EventBus.RaiseDragEnd();
        else { PlayerEvents.RaiseBoxSelectVisual(default, false); PlayerEvents.RaisePathVisual(_pathPts, false); }
    }

    private bool TryGetPointer(out Vector2 pos, out bool began, out bool ended)
    {
        pos = default; began = false; ended = false;

        if (Input.touchCount > 0)
        {
            Touch? chosen = null;

            if (_dragging)
            {
                for (int i = 0; i < Input.touchCount; i++)
                {
                    Touch t = Input.GetTouch(i);
                    if (t.fingerId == _fingerId) { chosen = t; break; }
                }
            }
            else if (Input.touchCount == 1)
            {
                Touch t = Input.GetTouch(0);
                if (t.phase == TouchPhase.Began && !IsOverUI(t.fingerId))
                {
                    chosen = t;
                    _fingerId = t.fingerId;
                }
            }

            if (chosen == null) return false;

            Touch c = chosen.Value;
            pos = c.position;
            began = c.phase == TouchPhase.Began;
            ended = c.phase == TouchPhase.Ended || c.phase == TouchPhase.Canceled;
            return true;
        }

        if (simulateTouchWithMouse && !Application.isMobilePlatform)
        {
            if (Input.GetMouseButtonDown(0) && !IsOverUI(-1))
            {
                pos = Input.mousePosition; began = true; return true;
            }
            if (_dragging)
            {
                pos = Input.mousePosition;
                ended = Input.GetMouseButtonUp(0);
                return true;
            }
        }
        return false;
    }

    private static bool IsOverUI(int pointerId) =>
        EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(pointerId);

    private static Rect MakeRect(Vector2 a, Vector2 b) =>
        Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));

    private void SamplePath(Vector2 screen)
    {
        float tile = map != null ? map.settings.tileSize : 1f;
        float min = tile * pathSampleTiles;
        Vector3 w = ScreenToWorld(screen);
        if ((w - _pathPts[_pathPts.Count - 1]).sqrMagnitude < min * min) return;
        _pathPts.Add(w);
        PlayerEvents.RaisePathVisual(_pathPts, true);
    }

    #endregion

    #region Helpers (screen -> world / province / regiment)

    private Vector3 ScreenToWorld(Vector2 s)
    {
        if (cam == null) cam = Camera.main;
        if (cam == null) return Vector3.zero;
        Vector3 w = cam.ScreenToWorldPoint(new Vector3(s.x, s.y, -cam.transform.position.z));
        w.z = 0f;
        return w;
    }

    private Province ProvinceAt(Vector2 screen) =>
        cam != null && map != null && map.IsReady ? map.GetProvinceAt(ScreenToWorld(screen)) : null;

    private Vector3 PosOf(Province p) => map.TileToWorldCenter(Mathf.RoundToInt(p.Centroid.x), Mathf.RoundToInt(p.Centroid.y));

    private Military MilitaryOf() =>
        PlayerNation != null && ArmyManager.HasInstance ? ArmyManager.Instance.GetMilitary(PlayerNation) : null;

    private int OwnSelectedCount()
    {
        int c = 0;
        for (int i = 0; i < _selected.Count; i++)
            if (_selected[i].Military != null && _selected[i].Military.Nation == PlayerNation) c++;
        return c;
    }

    /// <summary>Nearest OWN regiment card under the pointer (screen distance).</summary>
    private Regiment PickRegiment(Vector2 screen)
    {
        Military m = MilitaryOf();
        if (m == null || cam == null || !Army.MarkersVisible) return null;

        float rad = regimentPickPixels * Mathf.Max(0.1f, Army.CardScale);
        float bestD = rad * rad;
        Regiment best = null;
        for (int i = 0; i < m.Regiments.Count; i++)
        {
            Regiment r = m.Regiments[i];
            if (r.Military == null || r.IsColumn) continue;
            Vector3 sp = cam.WorldToScreenPoint(r.WorldPosition);
            if (sp.z < 0f) continue;
            float d = ((Vector2)sp - screen).sqrMagnitude;
            if (d < bestD) { bestD = d; best = r; }
        }
        return best;
    }

    #endregion

    #region Mode handlers

    private void HandleSetMode(PlayerMode mode) => SwitchMode(mode);

    private void SwitchMode(PlayerMode mode)
    {
        bool changed = mode != _mode;
        bool statReset = mode != PlayerMode.Invest && _hasInvestStat;
        if (mode == PlayerMode.Invest && changed) { _hasInvestStat = false; statReset = true; }   // wait for a number
        _mode = mode;
        if (mode != PlayerMode.Invest) _hasInvestStat = false;
        if (!changed && !statReset) return;

        _nextStatus = 0f;
        if (changed) PlayerEvents.RaiseModeChanged(_mode);
        PlayerEvents.RaiseInvestStatChanged(_hasInvestStat, _investStat);
        if (changed)
            PlayerEvents.RaiseMessage(mode == PlayerMode.Invest ? "Invest mode: pick a stat (1-5)"
                                    : mode == PlayerMode.Military ? "Military mode" : "Normal mode");
    }

    private void HandleSelectInvestStat(StatType stat)
    {
        if (_mode != PlayerMode.Invest) { _mode = PlayerMode.Invest; PlayerEvents.RaiseModeChanged(_mode); }
        _investStat = stat;
        _hasInvestStat = true;
        _nextStatus = 0f;
        PlayerEvents.RaiseInvestStatChanged(true, stat);
        PlayerEvents.RaiseMessage("Invest: " + stat + "  (click a province, A = all)");
    }

    #endregion

    #region Invest handlers

    private void HandleInvestAt(Vector2 screen)
    {
        if (!_hasInvestStat) { PlayerEvents.RaiseMessage("Choose a stat first (1-5)."); return; }
        Province p = ProvinceAt(screen);
        if (p == null) return;
        if (p.Owner != PlayerNation) { PlayerEvents.RaiseMessage("Not your province."); return; }
        PlayerEvents.RaiseInvest(p, _investStat);
    }

    private void HandleInvest(Province p, StatType stat)
    {
        bool ok = TryInvest(p, stat, out string err);
        PlayerEvents.RaiseInvestResult(p, stat, ok, err);
        if (!ok && err != null) PlayerEvents.RaiseMessage(err);
        _nextStatus = 0f;
    }

    private bool TryInvest(Province p, StatType stat, out string error)
    {
        error = null;
        Nation n = PlayerNation;
        if (n == null) { error = "No player nation."; return false; }
        if (p == null || p.Owner != n) { error = "Not your province."; return false; }

        double price = World.GetIncreasePrice(p, stat);
        double floor = allowDebt ? n.MinTreasury : investKeepTreasury;
        if (n.Treasury - price < floor) { error = "Not enough treasury."; return false; }
        return World.TryQueueIncrease(p, stat, out error);
    }

    /// <summary>Round-robin over ALL provinces (start rotates every round) until nothing is affordable any more.</summary>
    private void HandleInvestAll(StatType stat)
    {
        Nation n = PlayerNation;
        if (n == null || n.IsEmpty) return;

        double before = n.Treasury;
        int count = 0, attempts = 0;
        IReadOnlyList<Province> provs = n.Provinces;
        for (int round = 0; attempts < investAllMaxSteps; round++)
        {
            int okRound = 0, c = provs.Count;
            for (int i = 0; i < c && attempts < investAllMaxSteps; i++)
            {
                attempts++;
                if (TryInvest(provs[(i + round) % c], stat, out _)) { okRound++; count++; }
            }
            if (okRound == 0) break;
        }

        PlayerEvents.RaiseInvestAllResult(stat, count, before - n.Treasury);
        PlayerEvents.RaiseMessage(count > 0 ? stat + ": " + count + " increases queued" : "Nothing affordable.");
        _nextStatus = 0f;
    }

    #endregion

    #region Selection handlers (Regiment > Province > none)

    private void HandleSelectAt(Vector2 screen, bool additive)
    {
        Regiment r = PickRegiment(screen);
        if (r != null)
        {
            if (!additive) ClearSelectionSilently();
            if (!_selected.Remove(r)) _selected.Add(r);               // additive click on a selected one removes it
        }
        else
        {
            Province p = ProvinceAt(screen);
            if (!additive) ClearSelectionSilently();
            if (p != null)
            {
                SelectedProvince = p;
                if (logSelection) LogProvince(p);
            }
        }
        SelectionChanged();
    }

    private void HandleSelectBox(Rect rect, bool additive)
    {
        if (!additive) ClearSelectionSilently();
        Military m = MilitaryOf();
        if (m != null && cam != null)
        {
            for (int i = 0; i < m.Regiments.Count; i++)
            {
                Regiment r = m.Regiments[i];
                if (r.Military == null || r.IsColumn || _selected.Contains(r)) continue;
                Vector3 sp = cam.WorldToScreenPoint(r.WorldPosition);
                if (sp.z >= 0f && rect.Contains(new Vector2(sp.x, sp.y))) _selected.Add(r);
            }
        }
        SelectionChanged();
    }

    private void HandleSelectAllRegiments()
    {
        ClearSelectionSilently();
        Military m = MilitaryOf();
        if (m != null)
            for (int i = 0; i < m.Regiments.Count; i++)
                if (m.Regiments[i].Military != null && !m.Regiments[i].IsColumn) _selected.Add(m.Regiments[i]);
        SelectionChanged();
        PlayerEvents.RaiseMessage(_selected.Count + " regiments selected");
    }

    private void HandleClearSelection()
    {
        if (Selection == SelectionKind.None) return;
        ClearSelectionSilently();
        SelectionChanged();
    }

    private void ClearSelectionSilently() { _selected.Clear(); SelectedProvince = null; }

    private void SelectionChanged()
    {
        _nextStatus = 0f;
        PlayerEvents.RaiseSelectionChanged();
    }

    private void PruneSelection()
    {
        bool changed = false;
        for (int i = _selected.Count - 1; i >= 0; i--)
            if (_selected[i] == null || _selected[i].Military == null) { _selected.RemoveAt(i); changed = true; }
        if (SelectedProvince != null && SelectedProvince.IsMerged)
        {
            SelectedProvince = map != null ? map.GetProvince(SelectedProvince.MergedInto) : null;
            changed = true;
        }
        if (changed) PlayerEvents.RaiseSelectionChanged();
    }

    private void LogProvince(Province p)
    {
        Nation n = p.Owner;
        Debug.Log("[Select] " + p.Name + " (#" + p.Id + ")" + (p.IsCapital ? " (CAPITAL)" : "") +
                  "  Owner: " + (n != null ? n.Name : "Neutral") + "  Tiles: " + p.TileCount +
                  "  Pop: " + p.Population.ToString("F0") + "  Income: " + p.Income.ToString("F1"));
    }

    #endregion

    #region Military command handlers

    /// <summary>Right click / long press on a province: all own selected regiments march there (enemy or own).</summary>
    private void HandleSecondaryAt(Vector2 screen)
    {
        if (DebugSecondary(screen)) return;
        if (_mode != PlayerMode.Military || OwnSelectedCount() == 0) return;
        Province p = ProvinceAt(screen);
        if (p != null) PlayerEvents.RaiseMoveSelected(p);
    }

    private void HandleMoveSelected(Province dest)
    {
        Nation n = PlayerNation;
        if (n == null || dest == null || !ArmyManager.HasInstance) return;

        int ok = 0, fail = 0, cnt = 0;
        Vector3 c = Vector3.zero;
        for (int i = 0; i < _selected.Count; i++)
        {
            Regiment r = _selected[i];
            if (r.Military == null || r.Military.Nation != n) continue;
            c += r.WorldPosition; cnt++;
            if (Army.MoveRegiment(r, dest)) { ok++; HoldForManual(r); } else fail++;     // BFS path is built automatically
        }
        if (cnt == 0) return;

        if (ok > 0 && TacticVisuals.Enabled)
        {
            Vector3 to = PosOf(dest);
            Color col = n.Color;
            TacticVisuals.Arrow(c / cnt, to, col, 6f);
            if (dest.Owner != n) TacticVisuals.Announce(to, n.Name + ": Attack " + dest.Name, col, 4f);
            else TacticVisuals.Circle(to, Army.TileSize * 3f, col, 4f, true);
        }
        PlayerEvents.RaiseCommandResult("Move to " + dest.Name, ok, fail);
        if (fail > 0 && ok == 0) PlayerEvents.RaiseMessage("No path to " + dest.Name);
        _nextStatus = 0f;
    }

    private void HandleMoveSelectedVia(List<Province> waypoints)
    {
        Nation n = PlayerNation;
        if (n == null || waypoints == null || waypoints.Count == 0 || !ArmyManager.HasInstance) return;

        int ok = 0, fail = 0, cnt = 0;
        Vector3 c = Vector3.zero;
        for (int i = 0; i < _selected.Count; i++)
        {
            Regiment r = _selected[i];
            if (r.Military == null || r.Military.Nation != n) continue;
            c += r.WorldPosition; cnt++;
            if (Army.MoveRegimentVia(r, waypoints)) { ok++; HoldForManual(r); } else fail++;
        }
        if (cnt == 0) return;

        if (ok > 0 && TacticVisuals.Enabled)
        {
            _lineBuf.Clear();
            _lineBuf.Add(c / cnt);
            for (int i = 0; i < waypoints.Count; i++) _lineBuf.Add(PosOf(waypoints[i]));
            TacticVisuals.Line(_lineBuf, n.Color, 8f, false, true);
        }
        PlayerEvents.RaiseCommandResult("Path (" + waypoints.Count + " waypoints)", ok, fail);
        _nextStatus = 0f;
    }

    /// <summary>Shift + drag finished: turn the drawn line into a clean list of provinces and command it.</summary>
    private void CommitPath()
    {
        BuildWaypoints(_pathPts, _wpBuf);
        if (_wpBuf.Count == 0) { PlayerEvents.RaiseMessage("Path is not valid."); return; }
        if (_wpBuf.Count == 1) PlayerEvents.RaiseMoveSelected(_wpBuf[0]);
        else PlayerEvents.RaiseMoveSelectedVia(_wpBuf);
    }

    /// <summary>
    /// Normalizes a freehand line: provinces under it in order, loops removed, provinces the nation cannot enter skipped,
    /// thinned to maxPathWaypoints. The result is a clean province chain instead of a wobbly line.
    /// </summary>
    private void BuildWaypoints(List<Vector3> pts, List<Province> result)
    {
        result.Clear();
        Nation n = PlayerNation;
        if (n == null || map == null || !ArmyManager.HasInstance) return;

        for (int i = 0; i < pts.Count; i++)
        {
            Province p = map.GetProvinceAt(pts[i]);
            if (p == null || p.IsMerged || !Army.CanEnter(n, p)) continue;
            int idx = result.IndexOf(p);
            if (idx >= 0) { result.RemoveRange(idx + 1, result.Count - idx - 1); continue; }     // loop -> cut it
            result.Add(p);
        }

        int max = Mathf.Max(2, maxPathWaypoints);
        if (result.Count > max)
        {
            var thin = new List<Province>(max);
            for (int i = 0; i < max; i++) thin.Add(result[Mathf.RoundToInt(i * (result.Count - 1f) / (max - 1f))]);
            result.Clear();
            result.AddRange(thin);
        }
    }

    private void HandleRecall()
    {
        Nation n = PlayerNation;
        if (n == null || n.Capital == null) return;
        HandleMoveSelected(n.Capital);
    }

    private void HandleSetPreparation(int level)
    {
        Military m = MilitaryOf();
        if (m == null) return;
        m.SetPreparationLevel(Mathf.Clamp(level, 1, 10));
        _nextStatus = 0f;
        PlayerEvents.RaiseMessage("Preparation level " + m.PreparationLevel);
    }

    #endregion

    #region Camera handler

    /// <summary>
    /// CameraController focuses the capital of the nation under the given screen point. We pass a tile of OUR capital,
    /// so no change to CameraController is needed.
    /// </summary>
    private void HandleReturnToNation()
    {
        Nation n = PlayerNation;
        Province cap = n != null ? n.Capital : null;
        if (cap == null || cap.Tiles == null || cap.Tiles.Length == 0 || map == null || cam == null)
        {
            PlayerEvents.RaiseMessage("No nation to return to.");
            return;
        }
        int t = cap.Tiles[0];
        Vector3 world = map.TileToWorldCenter(t % map.Width, t / map.Width);
        Vector3 sp = cam.WorldToScreenPoint(world);
        EventBus.RaiseFocusCapital(new Vector2(sp.x, sp.y), true);
    }

    #endregion

    #region Status (quick access for the UI)

    private void RefreshStatus()
    {
        _nextStatus = Time.unscaledTime + Mathf.Max(0.05f, statusInterval);
        PruneSelection();

        PlayerStatus s = Status;
        s.Mode = _mode;
        s.InvestStatChosen = _hasInvestStat;
        s.InvestStat = _investStat;
        s.AdditiveToggle = _additiveToggle;
        s.PathDrawArmed = _pathArmed;
        s.SelectedRegiments = _selected.Count;
        s.SelectedProvinceName = SelectedProvince != null ? SelectedProvince.Name : "";

        RefreshArmyStatus(s);

        Nation n = PlayerNation;
        if (n == null || n.IsEmpty)
        {
            s.ResetNation();
            PlayerEvents.RaiseStatusChanged(s);
            return;
        }

        // nation
        s.HasPlayer = true;
        s.NationName = n.Name;
        s.NationColor = n.Color;
        s.CapitalName = n.Capital != null ? n.Capital.Name : "";
        s.Provinces = n.ProvinceCount;
        s.Area = n.Area;
        s.Population = n.Population;
        s.Economy = n.Economy;
        s.Treasury = n.Treasury;
        s.MinTreasury = n.MinTreasury;
        s.MaxTreasury = n.MaxTreasury;
        s.DailyIncome = n.GDP;
        s.AvgDevastation = n.AverageDevastation;
        s.AvgDefend = n.AverageDefend;
        s.AvgInfrastructure = n.AverageInfrastructure;

        int queued = 0;
        for (int i = 0; i < n.Provinces.Count; i++)
            if (n.Provinces[i].HasUpgrades) queued += n.Provinces[i].Upgrades.Queue.Count;
        s.QueuedIncreases = queued;

        // next price of every stat (selected own province, otherwise the capital)
        Province pp = SelectedProvince != null && SelectedProvince.Owner == n ? SelectedProvince : n.Capital;
        s.PriceProvinceName = pp != null ? pp.Name : "";
        for (int k = 0; k < s.NextPrice.Length; k++)
            s.NextPrice[k] = pp != null ? World.GetIncreasePrice(pp, (StatType)k) : 0.0;

        // military
        Military m = MilitaryOf();
        s.HasMilitary = m != null;
        if (m != null)
        {
            s.ManpowerCurrent = m.CurrentManpower;
            s.ManpowerMax = m.MaxManpower;
            s.ManpowerPercent = m.ManpowerPercent;
            s.Mobilized = m.MobilizedCap;
            s.RegimentMen = m.TotalRegimentManpower();
            s.AffordableMen = m.AffordableMen;
            s.Regiments = m.Regiments.Count;
            s.Columns = m.Columns.Count;
            s.PreparationLevel = m.PreparationLevel;
            s.PreparationNow = m.CurrentPreparation;
            float str = 0f;
            for (int i = 0; i < m.Regiments.Count; i++) str += m.Regiments[i].StrengthPercent;
            s.AvgStrength = m.Regiments.Count > 0 ? str / m.Regiments.Count : 0f;
        }
        else
        {
            s.ManpowerCurrent = s.ManpowerMax = s.ManpowerPercent = s.Mobilized = s.RegimentMen = s.AffordableMen = 0.0;
            s.Regiments = s.Columns = s.PreparationLevel = 0;
            s.PreparationNow = s.AvgStrength = 0f;
        }

        // wars
        s.Wars.Clear();
        s.AtWar = false;
        if (WarManager.HasInstance)
        {
            IReadOnlyList<War> wars = WM.GetWars(n);
            for (int i = 0; i < wars.Count; i++)
            {
                War w = wars[i];
                if (w.Ended) continue;
                WarSide side = w.SideOf(n.Id);
                if (side == WarSide.None) continue;
                Nation enemy = World.GetNation(side == WarSide.Attacker ? w.DefenderId : w.AttackerId);
                s.Wars.Add(new WarLine
                {
                    Enemy = enemy != null ? enemy.Name : "?",
                    Attacker = side == WarSide.Attacker,
                    MyProgress = w.ProgressOf(side),
                    TheirProgress = w.ProgressOf(War.Opposite(side)),
                    Days = w.DaysActive
                });
            }
            s.AtWar = s.Wars.Count > 0;
        }

        PlayerEvents.RaiseStatusChanged(s);
    }

    #endregion

    #region Context menu

    [ContextMenu("Player: Pick Random Nation")]
    private void MenuRandom() => SetPlayer(WorldManager.Instance.GetRandomNation());

    [ContextMenu("Player: Set Nation (startNationId)")]
    private void MenuSet() => SetPlayer(WorldManager.Instance.GetNation(startNationId));

    [ContextMenu("Player: No Player (spectator)")]
    private void MenuNone() => SetPlayer(null);

    [ContextMenu("Player: Log Status")]
    private void MenuLogStatus()
    {
        RefreshStatus();
        PlayerStatus s = Status;
        Debug.Log($"[Player] {s.NationName} | treasury {s.Treasury:F0} (+{s.DailyIncome:F1}/day) | provinces {s.Provinces} | " +
                  $"pop {s.Population:N0} | regiments {s.Regiments} | prep {s.PreparationLevel} | wars {s.Wars.Count} | mode {s.Mode}");
    }

    #endregion
}