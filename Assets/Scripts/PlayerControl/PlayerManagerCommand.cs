using System;
using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
//  PlayerManager, part 2: army commands.
//   Merge army | Call reinforcement | Auto battle | Halt | Quick selects | Control groups
//   Every command has: a keybinding (Military mode), a Request... method for UI buttons and a PlayerEvents intent.
//   Requires `partial` on PlayerManager, PlayerStatus and PlayerEvents (see PATCHES.md).
// ─────────────────────────────────────────────────────────────────────────────

#region Events (partial of PlayerEvents)

public static partial class PlayerEvents
{
    // intents
    public static event Action OnMergeArmy, OnCallReinforcement, OnToggleAutoBattle, OnHalt,
                               OnSelectIdle, OnSelectWounded, OnSelectInProvince;
    public static event Action<bool> OnSetAutoBattle;
    public static event Action<int> OnSaveGroup, OnRecallGroup;

    // results
    public static event Action<bool> OnAutoBattleChanged;

    public static void RaiseMergeArmy() => OnMergeArmy?.Invoke();
    public static void RaiseCallReinforcement() => OnCallReinforcement?.Invoke();
    public static void RaiseToggleAutoBattle() => OnToggleAutoBattle?.Invoke();
    public static void RaiseSetAutoBattle(bool on) => OnSetAutoBattle?.Invoke(on);
    public static void RaiseHalt() => OnHalt?.Invoke();
    public static void RaiseSelectIdle() => OnSelectIdle?.Invoke();
    public static void RaiseSelectWounded() => OnSelectWounded?.Invoke();
    public static void RaiseSelectInProvince() => OnSelectInProvince?.Invoke();
    public static void RaiseSaveGroup(int slot) => OnSaveGroup?.Invoke(slot);
    public static void RaiseRecallGroup(int slot) => OnRecallGroup?.Invoke(slot);
    public static void RaiseAutoBattleChanged(bool on) => OnAutoBattleChanged?.Invoke(on);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetArmyStatics()
    {
        OnMergeArmy = null; OnCallReinforcement = null; OnToggleAutoBattle = null; OnHalt = null;
        OnSelectIdle = null; OnSelectWounded = null; OnSelectInProvince = null;
        OnSetAutoBattle = null; OnSaveGroup = null; OnRecallGroup = null; OnAutoBattleChanged = null;
    }
}

public sealed partial class PlayerStatus
{
    public bool AutoBattle;
    public int IdleRegiments, WoundedRegiments, MergeableGroups, ReinforceColumns;
}

#endregion

public partial class PlayerManager
{
    #region Inspector

    [Header("Army keys (Military mode; B works in every mode except Invest)")]
    [SerializeField] private KeyCode mergeKey = KeyCode.G;
    [SerializeField] private KeyCode reinforceKey = KeyCode.T;
    [SerializeField] private KeyCode autoBattleKey = KeyCode.B;
    [SerializeField] private KeyCode haltKey = KeyCode.X;
    [SerializeField] private KeyCode selectIdleKey = KeyCode.Q;
    [SerializeField] private KeyCode selectWoundedKey = KeyCode.E;
    [SerializeField] private KeyCode selectInProvinceKey = KeyCode.V;

    [Header("Army commands")]
    [Tooltip("Columns sent to every regiment by 'Call reinforcement'.")]
    [SerializeField, Min(1)] private int reinforceColumns = 3;
    [Tooltip("Max regiments that get columns per click (weakest first).")]
    [SerializeField, Min(1)] private int reinforceMaxRegiments = 12;
    [Tooltip("'Select wounded' picks regiments below this strength.")]
    [SerializeField, Range(0.1f, 1f)] private float woundedBelow = 0.8f;
    [Tooltip("Reinforcement columns of the player walk to their regiment on their own (the AI does this for itself).")]
    [SerializeField] private bool routePlayerColumns = true;
    [SerializeField, Min(0.1f)] private float columnRouteInterval = 0.5f;
    [Tooltip("Creates the SelectionVisuals component (box, rings, path preview) when none exists.")]
    [SerializeField] private bool createSelectionVisuals = true;

    #endregion

    #region State

    private readonly List<Regiment> _tmp = new List<Regiment>(32);
    private readonly List<Regiment>[] _groups = new List<Regiment>[9];
    private readonly Dictionary<int, int> _stackProbe = new Dictionary<int, int>();
    private readonly HashSet<int> _provSet = new HashSet<int>();
    private float _nextRoute;
    private bool _visualsChecked;
    private bool _lastAuto;

    /// <summary>True while the AI commands the player's army (during war). Lives in AIManager, so it resets on its own.</summary>
    public bool AutoBattle => PlayerNation != null && AIManager.HasInstance && AIManager.Instance.IsAutoBattle(PlayerNation);

    #endregion

    #region Wiring (called from SubscribeIntents / UnsubscribeIntents / Tick / RefreshStatus)

    private void SubscribeArmyIntents()
    {
        PlayerEvents.OnMergeArmy += HandleMergeArmy;
        PlayerEvents.OnCallReinforcement += HandleCallReinforcement;
        PlayerEvents.OnToggleAutoBattle += HandleToggleAutoBattle;
        PlayerEvents.OnSetAutoBattle += SetAutoBattle;
        PlayerEvents.OnHalt += HandleHalt;
        PlayerEvents.OnSelectIdle += HandleSelectIdle;
        PlayerEvents.OnSelectWounded += HandleSelectWounded;
        PlayerEvents.OnSelectInProvince += HandleSelectInProvince;
        PlayerEvents.OnSaveGroup += HandleSaveGroup;
        PlayerEvents.OnRecallGroup += HandleRecallGroup;
    }

    private void UnsubscribeArmyIntents()
    {
        PlayerEvents.OnMergeArmy -= HandleMergeArmy;
        PlayerEvents.OnCallReinforcement -= HandleCallReinforcement;
        PlayerEvents.OnToggleAutoBattle -= HandleToggleAutoBattle;
        PlayerEvents.OnSetAutoBattle -= SetAutoBattle;
        PlayerEvents.OnHalt -= HandleHalt;
        PlayerEvents.OnSelectIdle -= HandleSelectIdle;
        PlayerEvents.OnSelectWounded -= HandleSelectWounded;
        PlayerEvents.OnSelectInProvince -= HandleSelectInProvince;
        PlayerEvents.OnSaveGroup -= HandleSaveGroup;
        PlayerEvents.OnRecallGroup -= HandleRecallGroup;
    }

    private void TickArmyExtras(float dt)
    {
        if (!_visualsChecked) { _visualsChecked = true; EnsureSelectionVisuals(); }

        bool auto = AutoBattle;                                  // AIManager can switch it off (player changed, nations cleared)
        if (auto != _lastAuto) { _lastAuto = auto; _nextStatus = 0f; PlayerEvents.RaiseAutoBattleChanged(auto); }

        RoutePlayerColumns();
    }

    private void EnsureSelectionVisuals()
    {
        if (!createSelectionVisuals) return;
#if UNITY_2022_2_OR_NEWER
        if (FindFirstObjectByType<SelectionVisuals>() != null) return;
#else
        if (FindObjectOfType<SelectionVisuals>() != null) return;
#endif
        new GameObject("SelectionVisuals").AddComponent<SelectionVisuals>();
    }

    private void RefreshArmyStatus(PlayerStatus s)
    {
        s.AutoBattle = AutoBattle;
        s.IdleRegiments = s.WoundedRegiments = s.MergeableGroups = s.ReinforceColumns = 0;
        Military m = MilitaryOf();
        if (m == null) return;

        _stackProbe.Clear();
        for (int i = 0; i < m.Regiments.Count; i++)
        {
            Regiment r = m.Regiments[i];
            if (r.Military == null || r.IsColumn) continue;
            if (r.State == RegimentState.Idle) s.IdleRegiments++;
            if (r.State != RegimentState.Retreating && r.StrengthPercent < woundedBelow) s.WoundedRegiments++;
            if (CanMerge(r))
            {
                Province p = r.CurrentProvince ?? r.LocationProvince;
                _stackProbe.TryGetValue(p.Id, out int c);
                _stackProbe[p.Id] = c + 1;
            }
        }
        foreach (var kv in _stackProbe) if (kv.Value >= 2) s.MergeableGroups++;
        for (int i = 0; i < m.Columns.Count; i++) if (m.Columns[i].Role == ColumnRole.Merge) s.ReinforceColumns++;
    }

    #endregion

    #region Keys

    private void ReadArmyKeys()
    {
        if (_mode == PlayerMode.Invest) return;
        if (Input.GetKeyDown(autoBattleKey)) RequestToggleAutoBattle();
        if (_mode != PlayerMode.Military) return;

        if (Input.GetKeyDown(mergeKey)) RequestMergeArmy();
        if (Input.GetKeyDown(reinforceKey)) RequestCallReinforcement();
        if (Input.GetKeyDown(haltKey)) RequestHalt();
        if (Input.GetKeyDown(selectIdleKey)) RequestSelectIdle();
        if (Input.GetKeyDown(selectWoundedKey)) RequestSelectWounded();
        if (Input.GetKeyDown(selectInProvinceKey)) RequestSelectInProvince();

        int n = NumberKeyDown();                                   // 1..9 recalls a control group (CTRL+SHIFT+1..9 saves it)
        if (n >= 0) RequestRecallGroup(n);
    }

    #endregion

    #region Public API (bind these to UI buttons: they ONLY raise events)

    public void RequestMergeArmy() => PlayerEvents.RaiseMergeArmy();
    public void RequestCallReinforcement() => PlayerEvents.RaiseCallReinforcement();
    public void RequestToggleAutoBattle() => PlayerEvents.RaiseToggleAutoBattle();
    public void RequestAutoBattle(bool on) => PlayerEvents.RaiseSetAutoBattle(on);
    public void RequestHalt() => PlayerEvents.RaiseHalt();
    public void RequestSelectIdle() => PlayerEvents.RaiseSelectIdle();
    public void RequestSelectWounded() => PlayerEvents.RaiseSelectWounded();
    public void RequestSelectInProvince() => PlayerEvents.RaiseSelectInProvince();
    /// <summary>Slot 0..8 = key "1".."9".</summary>
    public void RequestSaveGroup(int slot) => PlayerEvents.RaiseSaveGroup(slot);
    public void RequestRecallGroup(int slot) => PlayerEvents.RaiseRecallGroup(slot);
    /// <summary>Full mobilization / demobilization buttons.</summary>
    public void RequestMobilizeAll() => PlayerEvents.RaiseSetPreparation(10);
    public void RequestDemobilize() => PlayerEvents.RaiseSetPreparation(1);
    /// <summary>One click "get the army ready": merge stacks, then call reinforcement.</summary>
    public void RequestReadyArmy() { PlayerEvents.RaiseMergeArmy(); PlayerEvents.RaiseCallReinforcement(); }

    #endregion

    #region Helpers

    /// <summary>Own regiments that are selected; when nothing is selected: ALL own regiments.</summary>
    private void CollectTargets(List<Regiment> into)
    {
        into.Clear();
        Nation n = PlayerNation;
        if (n == null) return;
        for (int i = 0; i < _selected.Count; i++)
        {
            Regiment r = _selected[i];
            if (r.Military != null && r.Military.Nation == n && !r.IsColumn) into.Add(r);
        }
        if (into.Count > 0) return;
        Military m = MilitaryOf();
        if (m == null) return;
        for (int i = 0; i < m.Regiments.Count; i++)
            if (m.Regiments[i].Military != null) into.Add(m.Regiments[i]);
    }

    private static bool CanMerge(Regiment r) =>
        r.Military != null && !r.IsColumn && !r.InContact && !r.InRange &&
        (r.State == RegimentState.Idle || r.State == RegimentState.Reinforcing) &&
        (r.CurrentProvince ?? r.LocationProvince) != null;

    /// <summary>Called after a successful manual order: with Auto Battle on, the AI leaves these regiments alone until they arrive.</summary>
    private void HoldForManual(Regiment r)
    {
        if (AutoBattle) AIManager.Instance.HoldManual(r);
    }

    /// <summary>Own regiment cards inside a screen rectangle (used by the selection box and its live preview).</summary>
    public int GetRegimentsInRect(Rect rect, List<Regiment> into)
    {
        into.Clear();
        Military m = MilitaryOf();
        Camera c = cam != null ? cam : Camera.main;
        if (m == null || c == null) return 0;
        for (int i = 0; i < m.Regiments.Count; i++)
        {
            Regiment r = m.Regiments[i];
            if (r.Military == null || r.IsColumn) continue;
            Vector3 sp = c.WorldToScreenPoint(r.WorldPosition);
            if (sp.z >= 0f && rect.Contains(new Vector2(sp.x, sp.y))) into.Add(r);
        }
        return into.Count;
    }

    #endregion

    #region Merge army

    /// <summary>
    /// Regiments of the same province (idle, not fighting) become ONE regiment: the strongest one stays and takes the men
    /// of the others (up to its new allocation; leftover men go back to the reserve). Works on the selection, or on the
    /// whole army when nothing is selected.
    /// </summary>
    private void HandleMergeArmy()
    {
        Military m = MilitaryOf();
        if (m == null) return;
        CollectTargets(_tmp);

        var byProvince = new Dictionary<Province, List<Regiment>>();
        for (int i = 0; i < _tmp.Count; i++)
        {
            Regiment r = _tmp[i];
            if (!CanMerge(r)) continue;
            Province p = r.CurrentProvince ?? r.LocationProvince;
            if (!byProvince.TryGetValue(p, out List<Regiment> list)) byProvince[p] = list = new List<Regiment>(4);
            list.Add(r);
        }

        int removed = 0, groups = 0;
        double menBefore = 0.0, menKept = 0.0;
        var keepers = new List<Regiment>();
        foreach (var kv in byProvince)
        {
            List<Regiment> g = kv.Value;
            if (g.Count < 2) continue;
            g.Sort((a, b) => b.Manpower.CompareTo(a.Manpower));
            Regiment keep = g[0];
            double total = 0.0;
            for (int i = 0; i < g.Count; i++) total += g[i].Manpower;

            for (int i = 1; i < g.Count; i++) if (m.RemoveRegiment(g[i])) removed++;
            keep.Manpower = Math.Min(total, keep.Allocation);        // Allocation grows: fewer regiments share the same pool
            keep.RefreshText();
            menBefore += total;
            menKept += keep.Manpower;
            keepers.Add(keep);
            groups++;
        }

        for (int i = 0; i < keepers.Count; i++) if (!_selected.Contains(keepers[i])) _selected.Add(keepers[i]);
        if (groups > 0) SelectionChanged();

        PlayerEvents.RaiseCommandResult("Merge army", removed, 0);
        PlayerEvents.RaiseMessage(groups > 0
            ? "Merged " + (removed + groups) + " regiments into " + groups + " (" + menKept.ToString("N0") + " men" +
              (menBefore - menKept > 0.5 ? ", " + (menBefore - menKept).ToString("N0") + " back to reserve" : "") + ")"
            : "No regiments to merge here.");
        _nextStatus = 0f;
    }

    #endregion

    #region Call reinforcement

    /// <summary>Weakest regiments first: CombatManager spawns merge columns (men are paid from the treasury).</summary>
    private void HandleCallReinforcement()
    {
        if (!CombatManager.HasInstance) return;
        CollectTargets(_tmp);
        _tmp.Sort((a, b) => a.StrengthPercent.CompareTo(b.StrengthPercent));

        int ok = 0, fail = 0, left = reinforceMaxRegiments;
        CombatManager cm = CombatManager.Instance;
        for (int i = 0; i < _tmp.Count && left > 0; i++)
        {
            Regiment r = _tmp[i];
            if (r.State == RegimentState.Retreating) continue; //player can call whatever
            left--;
            if (cm.ForceReinforce(r, reinforceColumns)) ok++; else fail++;
        }

        PlayerEvents.RaiseCommandResult("Reinforce", ok, fail);
        if (ok == 0) PlayerEvents.RaiseMessage(fail > 0 ? "Cannot reinforce (no free men or treasury)." : "Everything is at full strength.");
        else PlayerEvents.RaiseMessage("Reinforcements sent to " + ok + " regiments");
        _nextStatus = 0f;
    }

    private bool InOrHeadingTo(Regiment r, Province p) =>
        (r.State != RegimentState.Moving && r.CurrentProvince == p) || (r.HasPath && r.Destination == p);

    /// <summary>Player columns chase the CURRENT position of their regiment and merge when close (same logic the AI uses for itself).</summary>
    private void RoutePlayerColumns()
    {
        if (!routePlayerColumns || Time.unscaledTime < _nextRoute) return;
        _nextRoute = Time.unscaledTime + columnRouteInterval;

        Military m = MilitaryOf();
        if (m == null || m.Columns.Count == 0 || !CombatManager.HasInstance) return;
        if (AutoBattle && WarManager.HasInstance && WM.IsAtWar(PlayerNation)) return;     // AIManager routes them during an auto war

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
            if (dest != null && !InOrHeadingTo(c, dest)) Army.MoveRegiment(c, dest);
        }
    }

    private static Regiment BestMergeTarget(Military m, Regiment c)
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

    #endregion

    #region Auto battle

    private void HandleToggleAutoBattle() => SetAutoBattle(!AutoBattle);

    /// <summary>
    /// ON: during a war the AI moves every regiment you are NOT commanding. Give an order (click / drawn path) and that
    /// regiment is yours until it has finished the path, then the AI takes it back. Peace: nothing happens.
    /// </summary>
    public void SetAutoBattle(bool on)
    {
        if (PlayerNation == null) { PlayerEvents.RaiseMessage("No player nation."); return; }
        AIManager.Instance.SetAutoBattle(PlayerNation, on);
        _lastAuto = AutoBattle;
        _nextStatus = 0f;
        PlayerEvents.RaiseAutoBattleChanged(_lastAuto);
        PlayerEvents.RaiseMessage(_lastAuto ? "Auto battle ON (AI commands idle regiments in war)" : "Auto battle OFF");
    }

    #endregion

    #region Halt

    /// <summary>Selected regiments stop where they are (they walk to the centre of their current province and wait there).</summary>
    private void HandleHalt()
    {
        int ok = 0, fail = 0;
        for (int i = 0; i < _selected.Count; i++)
        {
            Regiment r = _selected[i];
            if (r.Military == null || r.Military.Nation != PlayerNation || r.IsColumn) continue;
            Province p = r.CurrentProvince ?? r.LocationProvince;
            if (p != null && Army.MoveRegiment(r, p)) { ok++; HoldForManual(r); } else fail++;
        }
        if (ok + fail == 0) return;
        PlayerEvents.RaiseCommandResult("Halt", ok, fail);
        _nextStatus = 0f;
    }

    #endregion

    #region Quick selects + control groups

    private void SelectWhere(Func<Regiment, bool> filter, string label)
    {
        Military m = MilitaryOf();
        if (m == null) return;

        _tmp.Clear();
        for (int i = 0; i < m.Regiments.Count; i++)
        {
            Regiment r = m.Regiments[i];
            if (r.Military != null && !r.IsColumn && filter(r)) _tmp.Add(r);
        }
        if (_tmp.Count == 0) { PlayerEvents.RaiseMessage("No " + label); return; }

        if (!AdditiveHeld) ClearSelectionSilently();
        for (int i = 0; i < _tmp.Count; i++) if (!_selected.Contains(_tmp[i])) _selected.Add(_tmp[i]);
        SelectionChanged();
        PlayerEvents.RaiseMessage(_tmp.Count + " " + label);
    }

    private void HandleSelectIdle() =>
        SelectWhere(r => r.State == RegimentState.Idle, "idle regiments selected");

    private void HandleSelectWounded() =>
        SelectWhere(r => r.State != RegimentState.Retreating && r.StrengthPercent < woundedBelow, "wounded regiments selected");

    /// <summary>All own regiments standing in the province(s) of the selected regiments (or the selected province).</summary>
    private void HandleSelectInProvince()
    {
        _provSet.Clear();
        for (int i = 0; i < _selected.Count; i++)
        {
            Province p = _selected[i].CurrentProvince ?? _selected[i].LocationProvince;
            if (p != null) _provSet.Add(p.Id);
        }
        if (_provSet.Count == 0 && SelectedProvince != null) _provSet.Add(SelectedProvince.Id);
        if (_provSet.Count == 0) { PlayerEvents.RaiseMessage("Select a regiment or province first."); return; }

        SelectWhere(r =>
        {
            Province p = r.CurrentProvince ?? r.LocationProvince;
            return p != null && _provSet.Contains(p.Id);
        }, "regiments selected in province");
    }

    private void HandleSaveGroup(int slot)
    {
        if (slot < 0 || slot >= _groups.Length) return;
        var list = new List<Regiment>(_selected.Count);
        for (int i = 0; i < _selected.Count; i++)
            if (_selected[i].Military != null && _selected[i].Military.Nation == PlayerNation && !_selected[i].IsColumn) list.Add(_selected[i]);
        if (list.Count == 0) { PlayerEvents.RaiseMessage("Select regiments first."); return; }
        _groups[slot] = list;
        PlayerEvents.RaiseMessage("Group " + (slot + 1) + " saved (" + list.Count + ")");
    }

    private void HandleRecallGroup(int slot)
    {
        if (slot < 0 || slot >= _groups.Length) return;
        List<Regiment> g = _groups[slot];
        if (g == null) { PlayerEvents.RaiseMessage("Group " + (slot + 1) + " is empty."); return; }

        g.RemoveAll(r => r == null || r.Military == null || r.Military.Nation != PlayerNation);
        if (g.Count == 0) { _groups[slot] = null; PlayerEvents.RaiseMessage("Group " + (slot + 1) + " is gone."); return; }

        if (!AdditiveHeld) ClearSelectionSilently();
        for (int i = 0; i < g.Count; i++) if (!_selected.Contains(g[i])) _selected.Add(g[i]);
        SelectionChanged();
        PlayerEvents.RaiseMessage("Group " + (slot + 1) + " (" + g.Count + ")");
    }

    #endregion

    #region Context menu

    [ContextMenu("Player: Log Army Keys")]
    private void MenuLogArmyKeys()
    {
        Debug.Log("[Player] Military mode (CTRL+M): " +
                  mergeKey + " merge | " + reinforceKey + " reinforce | " + haltKey + " halt | " +
                  selectIdleKey + " select idle | " + selectWoundedKey + " select wounded | " + selectInProvinceKey + " select in province | " +
                  "1-9 recall group, CTRL+SHIFT+1-9 save group | " + autoBattleKey + " auto battle (any mode) | " +
                  "CTRL+A select all | H recall to capital | , . preparation | SHIFT/armed drag = path");
    }

    #endregion
}