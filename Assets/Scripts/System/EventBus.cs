using System;
using UnityEngine;

/// <summary>
/// Global event hub. Controllers ONLY raise events here; every system decides by itself
/// what to do by subscribing (OnEnable += / OnDisable -=).
/// </summary>
public static class EventBus
{
    // ?? language ??
    public static event Action OnLanguageChanged;
    public static void RaiseLanguageChanged() => OnLanguageChanged?.Invoke();

    // ?? input: movement / zoom ??
    /// <summary>Keyboard pan axis (-1..1 each), raised while non-zero and once more when it returns to zero.</summary>
    public static event Action<Vector2> OnMoveAxis;
    /// <summary>Keyboard zoom axis: +1 = zoom in, -1 = zoom out. Same raising rule as OnMoveAxis.</summary>
    public static event Action<float> OnZoomAxis;
    /// <summary>Mouse wheel notches (positive = scroll up = zoom in).</summary>
    public static event Action<float> OnZoomWheel;
    /// <summary>Pinch change in pixels (positive = fingers apart = zoom in).</summary>
    public static event Action<float> OnPinch;

    // ?? input: pointer (touch or mouse) ??
    public static event Action<Vector2> OnDragBegin;   // screen position
    public static event Action<Vector2> OnDragMove;    // screen-pixel delta since the last tick
    public static event Action OnDragEnd;
    public static event Action<Vector2> OnTap;         // short press without moving
    public static event Action<Vector2> OnLongPress;   // screen position, fired once per press

    // ?? input: time ??
    public static event Action OnTogglePause;
    public static event Action OnSpeedUp;
    public static event Action OnSpeedDown;

    // ?? world ??
    /// <summary>World position + the province under it (null on water / outside the map).</summary>
    public static event Action<Vector2, Province> OnProvinceLongPressed;
    /// <summary>Screen position of the pointer + whether a real pointer exists (false on phones).</summary>
    public static event Action<Vector2, bool> OnFocusCapital;


    public static void RaiseMoveAxis(Vector2 a) => OnMoveAxis?.Invoke(a);
    public static void RaiseZoomAxis(float a) => OnZoomAxis?.Invoke(a);
    public static void RaiseZoomWheel(float w) => OnZoomWheel?.Invoke(w);
    public static void RaisePinch(float d) => OnPinch?.Invoke(d);
    public static void RaiseDragBegin(Vector2 p) => OnDragBegin?.Invoke(p);
    public static void RaiseDragMove(Vector2 d) => OnDragMove?.Invoke(d);
    public static void RaiseDragEnd() => OnDragEnd?.Invoke();
    public static void RaiseTap(Vector2 p) => OnTap?.Invoke(p);
    public static void RaiseLongPress(Vector2 p) => OnLongPress?.Invoke(p);
    public static void RaiseTogglePause() => OnTogglePause?.Invoke();
    public static void RaiseSpeedUp() => OnSpeedUp?.Invoke();
    public static void RaiseSpeedDown() => OnSpeedDown?.Invoke();
    public static void RaiseProvinceLongPressed(Vector2 w, Province p) => OnProvinceLongPressed?.Invoke(w, p);
    public static void RaiseFocusCapital(Vector2 screenPos, bool hasPointer) => OnFocusCapital?.Invoke(screenPos, hasPointer);
    public static void RaiseFocusCapital() => OnFocusCapital?.Invoke(Vector2.zero, false);   // UI button / mobile

    // Needed when "Enter Play Mode Options" has domain reload disabled.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        OnLanguageChanged = null;
        OnMoveAxis = null; OnZoomAxis = null; OnZoomWheel = null; OnPinch = null;
        OnDragBegin = null; OnDragMove = null; OnDragEnd = null; OnTap = null; OnLongPress = null;
        OnTogglePause = null; OnSpeedUp = null; OnSpeedDown = null;
        OnProvinceLongPressed = null; OnFocusCapital = null;
    }
}