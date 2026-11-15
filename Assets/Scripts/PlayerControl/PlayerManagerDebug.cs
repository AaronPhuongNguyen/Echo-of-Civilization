using System;
using System.Collections.Generic;
using UnityEngine;

// -----------------------------------------------------------------------------
//  PlayerManager, part 3: SECRET DEBUG MODE
//
//   ON  : press Delete 10 times within 2 seconds
//   OFF : Escape + Delete (hold one, press the other)
//
//   While ON (DebugMode == true) EVERY player action in all three PlayerManager files is written to the Console as
//   "[Player DEBUG] AREA: what happened" (keys, taps, selection, invest, move, merge army, reinforce, time, modes, ...).
//   Plus the cheats below (they also show a toast):
//    CTRL+L        Declare War mode: click a province = the player's nation declares (forced) war on its owner.
//                  SHIFT+click first = pick ANOTHER attacker, then click the defender. Turns itself off after the war starts.
//    CTRL+P        End ALL wars (white peace).
//    CTRL+TAB      Summon Army mode: click (or right click) a province = a standard army appears there.
//                  Thousands of men per regiment, costs nothing. Stays ON until CTRL+TAB / ESC.
//    CTRL+F        Formation mode: SHIFT+click = select a nation (first one = leader), ENTER = confirm. ESC = cancel.
//    CTRL+M        Merge Provinces mode: SHIFT+click = select provinces (first one = origin, keeps its name / id),
//                  ENTER = merge. Only ONE province selected + ENTER = auto group with its nearest same-owner neighbours.
// -----------------------------------------------------------------------------

public enum DebugSubMode : byte { None, DeclareWar, Formation, SummonArmy, MergeProvinces }

public static partial class PlayerEvents
{
    public static event Action<bool> OnDebugModeChanged;
    public static void RaiseDebugModeChanged(bool on) => OnDebugModeChanged?.Invoke(on);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetDebugStatics() { OnDebugModeChanged = null; }
}

public sealed partial class PlayerStatus
{
    public bool DebugMode;
    public string DebugSub = "";
}

public partial class PlayerManager
{
    #region Inspector

    [Header("Debug mode (secret). ON: Delete x10 within the window. OFF: Escape + Delete")]
    [Tooltip("Master switch. OFF = the cheat can never be activated (set it false for release builds).")]
    [SerializeField] private bool allowDebugMode = true;
    [SerializeField] private KeyCode debugToggleKey = KeyCode.Delete;
    [SerializeField, Min(2)] private int debugPressesNeeded = 10;
    [SerializeField, Min(0.5f)] private float debugPressWindow = 2f;
    [Tooltip("Every debug line is also written to the Console (the action log of DebugMode needs this ON).")]
    [SerializeField] private bool debugLogToConsole = true;

    [Header("Debug keys (CTRL + key)  -  note: CTRL+P is the Play shortcut of the Unity Editor, CTRL+TAB switches Editor tabs: rebind them there if needed")]
    [SerializeField] private KeyCode debugWarKey = KeyCode.L;
    [SerializeField] private KeyCode debugEndWarsKey = KeyCode.P;
    [SerializeField] private KeyCode debugFormKey = KeyCode.F;
    [Tooltip("CTRL + this key = Summon Army mode on / off.")]
    [SerializeField] private KeyCode debugSummonKey = KeyCode.Tab;
    [Tooltip("CTRL + this key = Merge Provinces mode on / off.")]
    [SerializeField] private KeyCode debugMergeKey = KeyCode.M;

    [Header("Debug: summoned army")]
    [SerializeField, Min(1)] private int debugArmyRegiments = 5;
    [Tooltip("Men in EACH summoned regiment (the nation's manpower capacity is raised when it is too small, so the regiments are not clamped back).")]
    [SerializeField, Min(100)] private int debugMenPerRegiment = 5000;

    [Header("Debug: merge")]
    [Tooltip("ENTER with a single selected province merges it with this many provinces in total (same owner, touching).")]
    [SerializeField, Range(2, 8)] private int debugMergeAutoCount = 3;

    #endregion

    #region State

    private bool _debug;
    private DebugSubMode _dbgSub;
    private Nation _dbgAttacker;
    private MapManager _dbgMap;
    private readonly Queue<float> _delPresses = new Queue<float>(16);
    private readonly List<Nation> _dbgForm = new List<Nation>(8);
    private readonly List<Province> _dbgMerge = new List<Province>(8);
    private readonly List<Regiment> _dbgNew = new List<Regiment>(16);

    public bool DebugMode => _debug;
    public DebugSubMode DebugSub => _dbgSub;
    /// <summary>True while a debug click mode needs the pointer (camera drag only, no box / path drawing).</summary>
    public bool DebugBlocksDrag => _debug && _dbgSub != DebugSubMode.None;

    /// <summary>Text for an on-screen hint (SelectionVisuals shows it).</summary>
    public string DebugHint
    {
        get
        {
            if (!_debug) return "";
            switch (_dbgSub)
            {
                case DebugSubMode.DeclareWar:
                    return _dbgAttacker == null
                        ? "DEBUG  DECLARE WAR: click a province  (SHIFT first = choose the attacker)"
                        : "DEBUG  DECLARE WAR: attacker " + _dbgAttacker.Name + "  -  click the defender";
                case DebugSubMode.Formation:
                    return "DEBUG  FORMATION: SHIFT+Click select (" + _dbgForm.Count + "),  ENTER confirm,  ESC cancel";
                case DebugSubMode.SummonArmy:
                    return "DEBUG  SUMMON ARMY: click a province  (" + debugArmyRegiments + " x " + debugMenPerRegiment.ToString("N0") + " men),  CTRL+TAB / ESC = exit";
                case DebugSubMode.MergeProvinces:
                    return "DEBUG  MERGE: SHIFT+Click select (" + _dbgMerge.Count + "),  ENTER merge (1 selected = auto group),  ESC cancel";
                default:
                    return "DEBUG MODE";
            }
        }
    }

    #endregion

    #region Toggle (Delete x10 / Escape + Delete)

    private void TickDebug()
    {
        if (!allowDebugMode) { if (_debug) SetDebug(false); return; }

        bool del = Input.GetKeyDown(debugToggleKey);
        if (!_debug)
        {
            if (!del) return;
            float now = Time.unscaledTime;
            _delPresses.Enqueue(now);
            while (_delPresses.Count > 0 && now - _delPresses.Peek() > debugPressWindow) _delPresses.Dequeue();
            if (_delPresses.Count >= debugPressesNeeded) { _delPresses.Clear(); SetDebug(true); }
            return;
        }

        bool escDown = Input.GetKeyDown(KeyCode.Escape);
        if ((del && Input.GetKey(KeyCode.Escape)) || (escDown && Input.GetKey(debugToggleKey))) { SetDebug(false); return; }
        if (escDown && _dbgSub != DebugSubMode.None) SetSub(DebugSubMode.None, "cancelled");
        if (_dbgSub != DebugSubMode.None && _mode != PlayerMode.Normal) SetSub(DebugSubMode.None, "mode changed");

        if (CtrlKey())
        {
            if (Input.GetKeyDown(debugWarKey)) ToggleSub(DebugSubMode.DeclareWar);
            else if (Input.GetKeyDown(debugEndWarsKey)) DebugEndAllWars();
            else if (Input.GetKeyDown(debugFormKey)) ToggleSub(DebugSubMode.Formation);
            else if (Input.GetKeyDown(debugSummonKey)) ToggleSub(DebugSubMode.SummonArmy);
            else if (Input.GetKeyDown(debugMergeKey)) ToggleSub(DebugSubMode.MergeProvinces);
        }

        bool enter = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
        if (enter)
        {
            if (_dbgSub == DebugSubMode.Formation) DebugConfirmFormation();
            else if (_dbgSub == DebugSubMode.MergeProvinces) DebugConfirmMerge();
            else DebugAct("KEY: ENTER (nothing to confirm, no Formation / Merge mode active)");
        }
    }

    private void SetDebug(bool on)
    {
        if (on == _debug) return;
        _debug = on;
        if (!on) SetSub(DebugSubMode.None, null);
        Status.DebugMode = on;
        _nextStatus = 0f;
        PlayerEvents.RaiseDebugModeChanged(on);

        if (on)
            DebugLog("MODE ON  |  every player action is logged to the Console  |  CTRL+L declare war  |  CTRL+P end all wars  |  CTRL+TAB summon army mode (click a province)  |  " +
                     "CTRL+F formation (SHIFT+click select, ENTER confirm)  |  CTRL+M merge provinces (SHIFT+click select, ENTER confirm)  |  ESC + DELETE = off");
        else DebugLog("MODE OFF");
    }

    private void ToggleSub(DebugSubMode s) => SetSub(_dbgSub == s ? DebugSubMode.None : s, "toggled");

    private void SetSub(DebugSubMode s, string why)
    {
        if (s == _dbgSub) return;
        DebugSubMode old = _dbgSub;
        _dbgSub = s;
        _dbgAttacker = null;
        _dbgForm.Clear();
        _dbgMerge.Clear();
        Status.DebugSub = s == DebugSubMode.None ? "" : s.ToString();
        _nextStatus = 0f;

        if (s == DebugSubMode.None)
        {
            if (why != null) DebugLog(old + " mode off (" + why + ")");
            return;
        }

        SwitchMode(PlayerMode.Normal);                       // clean input: no box / path drawing while a debug click mode runs
        ClearSelectionSilently();
        SelectionChanged();
        switch (s)
        {
            case DebugSubMode.DeclareWar:
                DebugLog("DECLARE WAR mode: click a province of the nation to attack (SHIFT + click first = choose another attacker). CTRL+L / ESC = cancel.");
                break;
            case DebugSubMode.Formation:
                DebugLog("FORMATION mode: SHIFT + click = select nations (the FIRST one is the leader), ENTER = confirm, ESC = cancel.");
                break;
            case DebugSubMode.SummonArmy:
                DebugLog("SUMMON ARMY mode: click (or right click) one of your provinces = " + debugArmyRegiments + " regiments x " +
                         debugMenPerRegiment.ToString("N0") + " men. CTRL+TAB / ESC = exit.");
                break;
            case DebugSubMode.MergeProvinces:
                DebugLog("MERGE mode: SHIFT + click = select provinces of ONE nation (the FIRST one is the origin and keeps its name), " +
                         "ENTER = merge (a single selected province = auto group of " + debugMergeAutoCount + "), ESC = cancel.");
                break;
        }
    }

    #endregion

    #region Pointer hooks (called from PlayerManager.cs, see DEBUG_PATCHES.md)

    /// <summary>Tap while a debug click mode is active. Returns true when the tap was consumed.</summary>
    private bool DebugTap(Vector2 screen, bool additive)
    {
        if (!_debug || _dbgSub == DebugSubMode.None) return false;

        Province p = ProvinceAt(screen);

        if (_dbgSub == DebugSubMode.SummonArmy)
        {
            if (p == null) DebugLog("Click on a province to summon the army.");
            else DebugSummonArmy(p);
            return true;
        }
        if (_dbgSub == DebugSubMode.MergeProvinces)
        {
            if (p == null || p.IsMerged) DebugLog("Click a province.");
            else DebugMergeClick(p, additive);
            return true;
        }

        if (p == null || p.Owner == null) { DebugLog("Click a province that has an owner."); return true; }

        if (_dbgSub == DebugSubMode.DeclareWar) DebugWarClick(p.Owner, additive);
        else DebugFormClick(p.Owner, additive);
        return true;
    }

    /// <summary>Right click while Summon Army mode is active summons an army too. Returns true when consumed.</summary>
    private bool DebugSecondary(Vector2 screen)
    {
        if (!_debug || _dbgSub != DebugSubMode.SummonArmy) return false;
        Province p = ProvinceAt(screen);
        if (p == null) { DebugLog("Right click on a province to summon the army."); return true; }
        DebugSummonArmy(p);
        return true;
    }

    #endregion

    #region Declare war

    private void DebugWarClick(Nation target, bool shift)
    {
        if (shift && _dbgAttacker == null)
        {
            _dbgAttacker = target;
            DebugMark(target, "ATTACKER", new Color(1f, 0.45f, 0.2f));
            DebugLog("Attacker = " + target.Name + ". Now click the defender.");
            return;
        }

        Nation attacker = _dbgAttacker ?? PlayerNation;
        if (attacker == null) { DebugWarn("No player nation: SHIFT + click to choose an attacker first."); return; }
        if (attacker == target) { DebugLog("Pick a different nation than " + attacker.Name + "."); return; }
        if (!WarManager.HasInstance) { DebugWarn("No WarManager in the scene."); return; }

        War w = WM.ForceDeclareWar(attacker, target);
        if (w == null) { DebugWarn("Declare war failed: " + WM.LastError); return; }

        DebugMark(target, "WAR", new Color(1f, 0.2f, 0.2f));
        DebugLog("WAR DECLARED (forced): " + attacker.Name + " -> " + target.Name +
                 "  |  attackers " + w.Attackers.Count + ", defenders " + w.Defenders.Count + " (allies / vassals joined)");
        SetSub(DebugSubMode.None, "war started");
    }

    private void DebugEndAllWars()
    {
        if (!WarManager.HasInstance) { DebugWarn("No WarManager in the scene."); return; }
        int count = WM.WarCount;
        if (count == 0) { DebugLog("No wars are running."); return; }
        WM.EndAllWars();
        DebugLog("ALL WARS ENDED (white peace): " + count + " war(s).");
    }

    #endregion

    #region Summon army

    /// <summary>
    /// Standard army, no treasury charged. A regiment can only hold Allocation men (MobilizedCap * Share), and the reserve
    /// is already inside the existing regiments, so the manpower capacity is raised first (only when too small) and each
    /// new regiment is then filled with debugMenPerRegiment men.
    /// </summary>
    private void DebugSummonArmy(Province at)
    {
        Nation n = PlayerNation;
        Military m = MilitaryOf();
        if (n == null || m == null) { DebugWarn("No player nation / military."); return; }
        if (!ArmyManager.HasInstance) { DebugWarn("No ArmyManager in the scene."); return; }
        if (!Army.CanEnter(n, at)) { DebugWarn("Cannot summon in " + at.Name + ": " + n.Name + " may not enter it (not at war with its owner)."); return; }

        ArmyManager am = ArmyManager.Instance;
        double men = Math.Min(Math.Max(100, debugMenPerRegiment), am.RegimentCapMen);

        // capacity: auto shares are 1 / regimentCount, so MobilizedCap must reach (men per regiment) x (regiment count)
        double per = men;
        for (int i = 0; i < m.Regiments.Count; i++) per = Math.Max(per, m.Regiments[i].Manpower);
        double need = Math.Min(per, am.RegimentCapMen) * (m.Regiments.Count + debugArmyRegiments);

        m.SetPreparationLevel(10);
        m.CurrentPreparation = 100f;                         // no slow preparation ramp
        bool raised = false;
        for (int i = 0; i < 6 && m.MaxManpower < need; i++)
        {
            double cur = m.MaxManpower;
            float k = cur > 1e-6 ? (float)Math.Min(1000.0, need / cur * 1.02) : 2f;
            m.Manpower.Base = Mathf.Max(0.01f, m.Manpower.Base) * k;
            raised = true;
        }
        if (m.MaxManpower < need) DebugWarn("Manpower capacity is still below " + need.ToString("N0") + " (nation has no population?). Regiments may be smaller.");
        m.CurrentManpower = m.MaxManpower;                   // refill the reserve

        _dbgNew.Clear();
        for (int i = 0; i < debugArmyRegiments; i++)
        {
            Regiment r = m.AddRegiment(at, false);           // pay = false: unlimited money
            if (r == null) { DebugWarn("Regiment " + (i + 1) + " failed: " + m.LastError); break; }
            _dbgNew.Add(r);
        }
        if (_dbgNew.Count == 0) return;

        // fill every new regiment (AddRegiment only gives it what was free in the reserve)
        for (int i = 0; i < _dbgNew.Count; i++)
        {
            Regiment r = _dbgNew[i];
            if (r.Military == null) continue;
            r.Manpower = Math.Min(men, r.Allocation);
            r.RefreshText();
        }
        double inRegiments = m.TotalRegimentManpower();
        if (m.CurrentManpower < inRegiments) m.CurrentManpower = Math.Min(m.MaxManpower, inRegiments);

        ClearSelectionSilently();
        double total = 0.0;
        for (int i = 0; i < _dbgNew.Count; i++)
        {
            Regiment r = _dbgNew[i];
            if (r.Military == null) continue;               // clamped away by the share recalculation
            _selected.Add(r);
            total += r.Manpower;
        }
        SelectionChanged();
        _nextStatus = 0f;

        DebugLog("ARMY SUMMONED in " + at.Name + ": " + _selected.Count + " regiments, " + total.ToString("N0") + " men " +
                 "(" + (_selected.Count > 0 ? (total / _selected.Count).ToString("N0") : "0") + " each), treasury untouched, " +
                 "preparation 100 %." + (raised ? " Manpower capacity raised to " + m.MaxManpower.ToString("N0") + "." : "") +
                 " Total regiments now " + m.Regiments.Count + ".");
    }

    #endregion

    #region Formation

    private void DebugFormClick(Nation n, bool shift)
    {
        if (!shift) { DebugLog("Hold SHIFT and click to select nations. ENTER confirms."); return; }

        int idx = _dbgForm.IndexOf(n);
        if (idx >= 0)
        {
            _dbgForm.RemoveAt(idx);
            DebugLog("Deselected " + n.Name + ".  " + FormSummary());
            return;
        }

        _dbgForm.Add(n);
        DebugMark(n, _dbgForm.Count == 1 ? "LEADER" : "MEMBER", _dbgForm.Count == 1 ? new Color(1f, 0.85f, 0.2f) : new Color(0.4f, 0.8f, 1f));
        DebugLog("Selected " + n.Name + (_dbgForm.Count == 1 ? " as LEADER" : " as member") + ".  " + FormSummary());
    }

    private string FormSummary()
    {
        if (_dbgForm.Count == 0) return "Nothing selected.";
        var sb = new System.Text.StringBuilder("Leader: ").Append(_dbgForm[0].Name);
        if (_dbgForm.Count > 1)
        {
            sb.Append(" | Members: ");
            for (int i = 1; i < _dbgForm.Count; i++) sb.Append(i > 1 ? ", " : "").Append(_dbgForm[i].Name);
        }
        return sb.ToString();
    }

    private void DebugConfirmFormation()
    {
        if (_dbgForm.Count < 2) { DebugWarn("Select at least 2 nations (the first one is the leader)."); return; }
        Nation leader = _dbgForm[0];
        List<Nation> members = _dbgForm.GetRange(1, _dbgForm.Count - 1);
        if (members.Contains(PlayerNation)) { DebugWarn("Your own nation cannot be absorbed: select it FIRST (as leader) or leave it out."); return; }

        string summary = FormSummary();
        if (!NationFormationManager.Instance.Form(leader, members))
        {
            DebugWarn("Formation failed: " + NationFormationManager.Instance.LastError);
            return;
        }
        DebugLog("FORMATION CONFIRMED: " + summary + "  (big transfers finish over a few frames)");
        SetSub(DebugSubMode.None, "formation confirmed");
    }

    #endregion

    #region Merge provinces

    private MapManager DbgMap()
    {
        if (_dbgMap == null)
        {
#if UNITY_2022_2_OR_NEWER
            _dbgMap = FindFirstObjectByType<MapManager>();
#else
            _dbgMap = FindObjectOfType<MapManager>();
#endif
        }
        return _dbgMap;
    }

    private void DebugMergeClick(Province p, bool shift)
    {
        if (!shift) { DebugLog("Hold SHIFT and click to select provinces. The FIRST one is the origin. ENTER merges."); return; }

        int idx = _dbgMerge.IndexOf(p);
        if (idx >= 0)
        {
            _dbgMerge.RemoveAt(idx);
            DebugLog("Deselected " + p.Name + ".  " + MergeSummary());
            return;
        }

        if (p.Owner == null) { DebugWarn(p.Name + " has no owner."); return; }
        if (_dbgMerge.Count > 0 && p.Owner != _dbgMerge[0].Owner)
        {
            DebugWarn(p.Name + " belongs to " + p.Owner.Name + ": all provinces must belong to " + _dbgMerge[0].Owner.Name + " (the origin's nation).");
            return;
        }
        if (_dbgMerge.Count >= DbgMap().merge.maxProvincesPerMerge)
        {
            DebugWarn("At most " + DbgMap().merge.maxProvincesPerMerge + " provinces per merge.");
            return;
        }

        _dbgMerge.Add(p);
        DebugMarkProvince(p, _dbgMerge.Count == 1 ? "ORIGIN" : "MERGE", _dbgMerge.Count == 1 ? new Color(1f, 0.85f, 0.2f) : new Color(0.4f, 0.8f, 1f));
        DebugLog("Selected " + p.Name + (_dbgMerge.Count == 1 ? " as ORIGIN" : "") + ".  " + MergeSummary());
    }

    private string MergeSummary()
    {
        if (_dbgMerge.Count == 0) return "Nothing selected.";
        var sb = new System.Text.StringBuilder("Origin: ").Append(_dbgMerge[0].Name);
        if (_dbgMerge.Count > 1)
        {
            sb.Append(" | Absorbed: ");
            for (int i = 1; i < _dbgMerge.Count; i++) sb.Append(i > 1 ? ", " : "").Append(_dbgMerge[i].Name);
        }
        return sb.ToString();
    }

    private void DebugConfirmMerge()
    {
        MapManager map = DbgMap();
        if (map == null || !map.IsReady) { DebugWarn("No MapManager / map not ready."); return; }
        if (_dbgMerge.Count == 0) { DebugWarn("SHIFT + click at least one province first (the origin)."); return; }

        Province origin = _dbgMerge[0];
        var group = new List<Province>(_dbgMerge);
        if (group.Count == 1 && !map.FindMergeGroup(origin, debugMergeAutoCount, group))
        {
            DebugWarn(origin.Name + " has no touching province of the same nation to merge with.");
            return;
        }

        string summary = MergeSummary();
        if (_dbgMerge.Count == 1) summary = "Origin: " + origin.Name + " (auto group of " + group.Count + ")";

        if (!map.CanMerge(origin, group, out string err)) { DebugWarn("Merge refused: " + err); return; }
        Province merged = map.MergeProvinces(origin, group);
        if (merged == null) { DebugWarn("Merge failed: " + map.LastMergeError); return; }

        DebugLog("PROVINCES MERGED: " + summary + "  ->  " + merged.Name + " (" + merged.TileCount + " tiles).");
        SetSub(DebugSubMode.None, "merge done");
    }

    #endregion

    #region Helpers (logging)

    /// <summary>Cheat feedback: Console AND toast (always, the cheat modes are only reachable in debug anyway).</summary>
    private void DebugLog(string message)
    {
        if (debugLogToConsole) Debug.Log("[Player DEBUG] " + message);
        PlayerEvents.RaiseMessage("[DEBUG] " + message);
    }

    private void DebugWarn(string message)
    {
        if (debugLogToConsole) Debug.LogWarning("[Player DEBUG] " + message);
        PlayerEvents.RaiseMessage("[DEBUG] " + message);
    }

    /// <summary>
    /// Action log: ONE Console line for every player action, only while DebugMode is ON (no toast).
    /// Call sites guard with "if (DebugMode)" so no string is built while debug is off.
    /// </summary>
    private void DebugAct(string message)
    {
        if (DebugMode && debugLogToConsole) Debug.Log("[Player DEBUG] " + message);
    }

    private static string ProvinceName(Province p) => p != null ? p.Name + " (#" + p.Id + ")" : "nothing (water / outside)";

    /// <summary>Short regiment description for the log: uid, men, strength, state, province.</summary>
    private static string DbgReg(Regiment r)
    {
        if (r == null) return "null";
        Province p = r.CurrentProvince ?? r.LocationProvince;
        return "#" + r.Uid + " " + r.Manpower.ToString("N0") + " men (" + (r.StrengthPercent * 100f).ToString("F0") + " %, " + r.State + ") @ " + (p != null ? p.Name : "?");
    }

    private double DbgSelectedMen()
    {
        double men = 0.0;
        for (int i = 0; i < _selected.Count; i++) if (_selected[i].Military != null) men += _selected[i].Manpower;
        return men;
    }

    private static string DbgProvinces(IList<Province> list)
    {
        if (list == null || list.Count == 0) return "[]";
        var sb = new System.Text.StringBuilder("[");
        for (int i = 0; i < list.Count; i++) sb.Append(i > 0 ? " > " : "").Append(list[i] != null ? list[i].Name : "?");
        return sb.Append(']').ToString();
    }

    /// <summary>Ring + label on the capital of a nation (only when tactic visuals are enabled).</summary>
    private void DebugMark(Nation n, string label, Color color)
    {
        if (n == null || n.Capital == null || !TacticVisuals.Enabled) return;
        Vector3 pos = PosOf(n.Capital);
        TacticVisuals.Circle(pos, Army.TileSize * 4f, color, 8f);
        TacticVisuals.Label(pos, label, color, 8f);
    }

    /// <summary>Ring + label on a province (only when tactic visuals are enabled).</summary>
    private void DebugMarkProvince(Province p, string label, Color color)
    {
        if (p == null || !TacticVisuals.Enabled) return;
        Vector3 pos = PosOf(p);
        TacticVisuals.Circle(pos, Army.TileSize * 3f, color, 15f);
        TacticVisuals.Label(pos, label, color, 15f);
    }

    #endregion
}