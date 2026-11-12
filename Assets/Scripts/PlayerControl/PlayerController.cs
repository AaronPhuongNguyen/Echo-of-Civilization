using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// THE input controller (replaces PlayerController input + TimeController hotkeys).
/// It reads keyboard / mouse / touch and ONLY raises EventBus events.
/// It never moves the camera, never touches TickManager: subscribers (CameraController,
/// TimeController, ProvinceSelector, ...) decide what happens.
///
/// Works on desktop (keyboard + mouse) and phones (touch drag, pinch, long press).
/// </summary>
public class GameController : TickBehaviour
{
    [Header("Keyboard")]
    [SerializeField] private bool enableKeyboard = true;
    [SerializeField] private KeyCode pauseKey = KeyCode.Space;
    [SerializeField] private KeyCode speedUpKey = KeyCode.RightBracket;
    [SerializeField] private KeyCode speedDownKey = KeyCode.LeftBracket;
    [SerializeField] private KeyCode focusCapitalKey = KeyCode.F;
    [SerializeField] private KeyCode[] zoomInKeys = { KeyCode.Equals, KeyCode.Plus, KeyCode.KeypadPlus };
    [SerializeField] private KeyCode[] zoomOutKeys = { KeyCode.Minus, KeyCode.KeypadMinus };

    [Header("Pointer")]
    [SerializeField] private bool simulateTouchWithMouse = true;   // desktop / Editor
    [Tooltip("Hold a finger (or left mouse button) still this long to raise OnLongPress.")]
    [SerializeField] private float longPressSeconds = 0.5f;
    [Tooltip("Movement (pixels) that still counts as 'not moving'.")]
    [SerializeField] private float longPressMoveTolerance = 15f;

    protected override bool IgnoreGameSpeed => true;   // input keeps working while paused
    protected override int TickPriority => -100;       // before every subscriber that ticks

    private Vector2 _lastAxis;
    private float _lastZoomAxis;

    private bool _dragging;
    private int _fingerId = -1;
    private Vector2 _lastPos, _pressStart;
    private float _pressHeld;
    private bool _pressMoved, _longFired;

    private bool _pinching;
    private float _lastPinchDist;

    protected override void OnDisable()
    {
        base.OnDisable();
        if (_dragging) EndDrag();
        _pinching = false;
    }

    public override void Tick(float dt)
    {
        if (enableKeyboard) ReadKeyboard();
        ReadWheel();
        if (!ReadPinch()) ReadPointer(dt);
    }

    // ── keyboard ──
    private void ReadKeyboard()
    {
        if (Input.GetKeyDown(pauseKey)) EventBus.RaiseTogglePause();
        if (Input.GetKeyDown(speedUpKey)) EventBus.RaiseSpeedUp();
        if (Input.GetKeyDown(speedDownKey)) EventBus.RaiseSpeedDown();
        if (Input.GetKeyDown(focusCapitalKey))
        {
            bool hasMouse = Input.mousePresent && !Application.isMobilePlatform;
            EventBus.RaiseFocusCapital(Input.mousePosition, hasMouse);
        }

        Vector2 axis = new Vector2(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical"));
        if (axis.sqrMagnitude > 1f) axis.Normalize();
        if (axis != Vector2.zero || _lastAxis != Vector2.zero) EventBus.RaiseMoveAxis(axis);
        _lastAxis = axis;

        float z = 0f;
        if (AnyKey(zoomInKeys)) z += 1f;
        if (AnyKey(zoomOutKeys)) z -= 1f;
        if (z != 0f || _lastZoomAxis != 0f) EventBus.RaiseZoomAxis(z);
        _lastZoomAxis = z;
    }

    private static bool AnyKey(KeyCode[] keys)
    {
        for (int i = 0; i < keys.Length; i++) if (Input.GetKey(keys[i])) return true;
        return false;
    }

    // ── wheel ──
    private void ReadWheel()
    {
        float wheel = Input.mouseScrollDelta.y;
        if (Mathf.Abs(wheel) > 0.001f) EventBus.RaiseZoomWheel(wheel);
    }

    // ── pinch (2 fingers). Returns true while pinching so the pointer logic is skipped. ──
    private bool ReadPinch()
    {
        if (Input.touchCount >= 2)
        {
            float dist = Vector2.Distance(Input.GetTouch(0).position, Input.GetTouch(1).position);
            if (!_pinching)
            {
                _pinching = true;
                _lastPinchDist = dist;
                if (_dragging) EndDrag();            // a pinch cancels the drag
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

    // ── pointer (1 finger / left mouse button) ──
    private void ReadPointer(float dt)
    {
        bool got = TryGetPointer(out Vector2 pos, out bool began, out bool ended);

        if (got)
        {
            if (began)
            {
                _dragging = true;
                _lastPos = pos;
                _pressStart = pos;
                _pressHeld = 0f;
                _pressMoved = false;
                _longFired = false;
                EventBus.RaiseDragBegin(pos);
            }

            if (_dragging)
            {
                Vector2 delta = pos - _lastPos;
                _lastPos = pos;
                if (delta != Vector2.zero) EventBus.RaiseDragMove(delta);
                UpdateLongPress(pos, dt);
            }

            if (ended && _dragging)
            {
                bool tap = !_pressMoved && !_longFired && _pressHeld < longPressSeconds;
                EndDrag();
                if (tap) EventBus.RaiseTap(pos);
            }
        }
        else if (_dragging)
        {
            EndDrag();   // pointer lost
        }
    }

    private void UpdateLongPress(Vector2 pos, float dt)
    {
        if (_pressMoved || _longFired) return;

        if (Vector2.Distance(pos, _pressStart) > longPressMoveTolerance) { _pressMoved = true; return; }

        _pressHeld += dt;
        if (_pressHeld >= longPressSeconds)
        {
            _longFired = true;                 // once per press
            EventBus.RaiseLongPress(pos);
        }
    }

    private void EndDrag()
    {
        _dragging = false;
        _fingerId = -1;
        EventBus.RaiseDragEnd();
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
}