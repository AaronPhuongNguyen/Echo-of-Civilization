using System;
using System.Collections.Generic;
using UnityEngine;

// ?????????????????????????????????????????????????????????????????????????????
//  PlayerManager, part 3: SECRET DEBUG MODE
//
//   ON  : press Delete 10 times within 2 seconds
//   OFF : Escape + Delete (hold one, press the other)
//
//   While ON (every action is written to the Console as "[Player DEBUG] ..."):
//    CTRL+L        Declare War mode: click a province = the player's nation declares (forced) war on its owner.
//                  SHIFT+click first = pick ANOTHER attacker, then click the defender. Turns itself off after the war starts.
//    CTRL+P        End ALL wars (white peace).
//    Military mode + hold TAB + RIGHT CLICK on a province = summon a standard army there. Costs nothing.
//    CTRL+F        Formation mode: SHIFT+click = select a nation (first one = leader), ENTER = confirm. ESC = cancel.
// ?????????????????????????????????????????????????????????????????????????????

public enum DebugSubMode : byte { None, DeclareWar, Formation }

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
    [Tooltip("Every debug action is also written to the Console.")]
    [SerializeField] private bool debugLogToConsole = true;

    [Header("Debug keys (CTRL + key)  -  note: CTRL+P is the Play shortcut of the Unity Editor, rebind it there if needed")]
    [SerializeField] private KeyCode debugWarKey = KeyCode.L;
    [SerializeField] private KeyCode debugEndWarsKey = KeyCode.P;
    [SerializeField] private KeyCode debugFormKey = KeyCode.F;
    [Tooltip("Hold this key in Military mode and right click a province to summon an army.")]
    [SerializeField] private KeyCode debugSummonHoldKey = KeyCode.Tab;

    [Header("Debug: summoned army")]
    [SerializeField, Min(1)] private int debugArmyRegiments = 5;

    #endregion

    #region State

    private bool _debug;
    private DebugSubMode _dbgSub;
    private Nation _dbgAttacker;
    private readonly Queue<float> _delPresses = new Queue<float>(16);
    private readonly List<Nation> _dbgForm = new List<Nation>(8);
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
        }
        if (_dbgSub == DebugSubMode.Formation && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)))
            DebugConfirmFormation();
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
            DebugLog("MODE ON  |  CTRL+L declare war  |  CTRL+P end all wars  |  Military mode: hold TAB + right click = summon army  |  " +
                     "CTRL+F formation (SHIFT+click select, ENTER confirm)  |  ESC + DELETE = off");
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
        if (s == DebugSubMode.DeclareWar)
            DebugLog("DECLARE WAR mode: click a province of the nation to attack (SHIFT + click first = choose another attacker). CTRL+L / ESC = cancel.");
        else
            DebugLog("FORMATION mode: SHIFT + click = select nations (the FIRST one is the leader), ENTER = confirm, ESC = cancel.");
    }

    #endregion

    #region Pointer hooks (called from PlayerManager.cs, see DEBUG_PATCHES.md)

    /// <summary>Tap while a debug click mode is active. Returns true when the tap was consumed.</summary>
    private bool DebugTap(Vector2 screen, bool additive)
    {
        if (!_debug || _dbgSub == DebugSubMode.None) return false;

        Province p = ProvinceAt(screen);
        if (p == null || p.Owner == null) { DebugLog("Click a province that has an owner."); return true; }

        if (_dbgSub == DebugSubMode.DeclareWar) DebugWarClick(p.Owner, additive);
        else DebugFormClick(p.Owner, additive);
        return true;
    }

    /// <summary>Right click: TAB held in Military mode summons an army. Returns true when consumed.</summary>
    private bool DebugSecondary(Vector2 screen)
    {
        if (!_debug || _mode != PlayerMode.Military || !Input.GetKey(debugSummonHoldKey)) return false;
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

    /// <summary>Standard army, no treasury charged: preparation is set to 100 %, the reserve is refilled, regiments are created for free.</summary>
    private void DebugSummonArmy(Province at)
    {
        Nation n = PlayerNation;
        Military m = MilitaryOf();
        if (n == null || m == null) { DebugWarn("No player nation / military."); return; }
        if (!Army.CanEnter(n, at)) { DebugWarn("Cannot summon in " + at.Name + ": " + n.Name + " may not enter it (not at war with its owner)."); return; }

        m.SetPreparationLevel(10);
        m.CurrentPreparation = 100f;                         // no slow preparation ramp: the allocation is available now
        m.CurrentManpower = m.MaxManpower;                   // refill the reserve

        _dbgNew.Clear();
        for (int i = 0; i < debugArmyRegiments; i++)
        {
            Regiment r = m.AddRegiment(at, false);           // pay = false: unlimited money
            if (r == null) { DebugWarn("Regiment " + (i + 1) + " failed: " + m.LastError); break; }
            _dbgNew.Add(r);
        }
        if (_dbgNew.Count == 0) return;

        ClearSelectionSilently();
        double men = 0.0;
        for (int i = 0; i < _dbgNew.Count; i++)
        {
            Regiment r = _dbgNew[i];
            if (r.Military == null) continue;               // clamped away by the share recalculation
            _selected.Add(r);
            men += r.Manpower;
        }
        SelectionChanged();
        _nextStatus = 0f;

        DebugLog("ARMY SUMMONED in " + at.Name + ": " + _selected.Count + " regiments, " + men.ToString("N0") + " men " +
                 "(" + (_selected.Count > 0 ? (men / _selected.Count).ToString("N0") : "0") + " each), treasury untouched, " +
                 "preparation 100 %. Total regiments now " + m.Regiments.Count + ".");
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

    #region Helpers

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

    /// <summary>Ring + label on the capital of a nation (only when tactic visuals are enabled).</summary>
    private void DebugMark(Nation n, string label, Color color)
    {
        if (n == null || n.Capital == null || !TacticVisuals.Enabled) return;
        Vector3 pos = PosOf(n.Capital);
        TacticVisuals.Circle(pos, Army.TileSize * 4f, color, 8f);
        TacticVisuals.Label(pos, label, color, 8f);
    }

    #endregion
}