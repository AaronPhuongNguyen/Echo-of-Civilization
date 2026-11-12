using UnityEngine;

/// <summary>
/// 2D RTS camera: pan (keys / drag with inertia), zoom (keys / wheel / pinch), map bounds.
/// Contains NO input code: it only reacts to EventBus events raised by GameController.
/// Pan speed scales with zoom level (wider view = faster).
/// </summary>
public class CameraController : TickBehaviour
{
    [Header("References")]
    [SerializeField] private Camera cam;

    [Header("Keyboard Pan")]
    [SerializeField] private float moveSpeed = 6f;               // world units/sec at referenceZoom
    [SerializeField] private float keyboardSmoothing = 12f;
    [SerializeField] private float referenceZoom = 10f;
    [SerializeField] private float speedZoomExponent = 1f;

    [Header("Drag + Inertia")]
    [SerializeField] private float dragSensitivity = 1f;
    [SerializeField] private float inertiaDamping = 4f;
    [SerializeField] private float maxInertiaSpeed = 30f;

    [Header("Zoom (orthographic size)")]
    [SerializeField] private float minZoom = 10f;
    [SerializeField] private float maxZoom = 250f;
    [SerializeField] private float keyZoomSpeed = 1.2f;          // exponential rate / sec
    [SerializeField] private float wheelZoomSpeed = 0.1f;        // per wheel notch
    [SerializeField] private float pinchZoomSpeed = 0.004f;      // per pixel of pinch change
    [SerializeField] private float zoomSmoothing = 10f;

    [Header("Map Bounds")]
    [Tooltip("Leave empty to find the MapManager in the scene automatically.")]
    [SerializeField] private MapManager map;
    [Tooltip("The camera VIEW can never leave (map size x this). Also limits the max zoom-out.")]
    [SerializeField] private float boundsScale = 1.2f;

    private Rect _bounds;
    private bool _hasBounds;

    private float _targetZoom, _currentZoom;

    // state fed by events
    private Vector2 _moveAxis;
    private float _zoomAxis;
    private bool _dragging, _dragEnding;
    private Vector2 _dragPending;          // screen pixels not yet applied

    private Vector2 _keyVelocity, _inertiaVelocity, _dragVelocitySample;

    protected override bool IgnoreGameSpeed => true;   // camera keeps working while paused

    protected override void OnEnable()
    {
        base.OnEnable();

        if (cam == null) cam = Camera.main;
        if (cam != null)
        {
            cam.orthographic = true;
            _targetZoom = _currentZoom = Mathf.Clamp(cam.orthographicSize, minZoom, maxZoom);
            cam.orthographicSize = _currentZoom;
        }

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
            map.OnMapGenerated += RefreshBounds;
            RefreshBounds();
        }

        EventBus.OnMoveAxis += HandleMoveAxis;
        EventBus.OnZoomAxis += HandleZoomAxis;
        EventBus.OnZoomWheel += HandleZoomWheel;
        EventBus.OnPinch += HandlePinch;
        EventBus.OnDragBegin += HandleDragBegin;
        EventBus.OnDragMove += HandleDragMove;
        EventBus.OnDragEnd += HandleDragEnd;
        EventBus.OnFocusCapital += HandleFocusCapital;
    }

    protected override void OnDisable()
    {
        EventBus.OnMoveAxis -= HandleMoveAxis;
        EventBus.OnZoomAxis -= HandleZoomAxis;
        EventBus.OnZoomWheel -= HandleZoomWheel;
        EventBus.OnPinch -= HandlePinch;
        EventBus.OnDragBegin -= HandleDragBegin;
        EventBus.OnDragMove -= HandleDragMove;
        EventBus.OnDragEnd -= HandleDragEnd;
        EventBus.OnFocusCapital -= HandleFocusCapital;

        base.OnDisable();
        if (map != null) map.OnMapGenerated -= RefreshBounds;
        _dragging = _dragEnding = false;
        _moveAxis = Vector2.zero; _zoomAxis = 0f;
        _inertiaVelocity = Vector2.zero;
    }

    // ?? event handlers (only store data; the work happens in Tick) ??
    private void HandleMoveAxis(Vector2 a) => _moveAxis = a;
    private void HandleZoomAxis(float a) => _zoomAxis = a;
    private void HandleZoomWheel(float w) => _targetZoom *= Mathf.Exp(-w * wheelZoomSpeed);
    private void HandlePinch(float diff) => _targetZoom *= Mathf.Exp(-diff * pinchZoomSpeed);

    private void HandleDragBegin(Vector2 _)
    {
        _dragging = true;
        _dragEnding = false;
        _dragPending = Vector2.zero;
        _dragVelocitySample = Vector2.zero;
        _inertiaVelocity = Vector2.zero;       // touching stops inertia
    }

    private void HandleDragMove(Vector2 screenDelta) => _dragPending += screenDelta;
    private void HandleDragEnd() => _dragEnding = true;   // released after the last movement is applied

    // ?? tick ??
    public override void Tick(float dt)
    {
        if (cam == null) return;

        HandleZoom(dt);
        HandleKeyboardPan(dt);
        HandleDrag(dt);
        ClampToBounds();
    }

    private void HandleZoom(float dt)
    {
        if (_zoomAxis != 0f) _targetZoom *= Mathf.Exp(-_zoomAxis * keyZoomSpeed * dt);

        float maxZ = EffectiveMaxZoom();
        _targetZoom = Mathf.Clamp(_targetZoom, minZoom, maxZ);
        _currentZoom = Mathf.Lerp(_currentZoom, _targetZoom, 1f - Mathf.Exp(-zoomSmoothing * dt));
        _currentZoom = Mathf.Clamp(_currentZoom, minZoom, maxZ);
        cam.orthographicSize = _currentZoom;
    }

    private void HandleKeyboardPan(float dt)
    {
        Vector2 target = _moveAxis * (moveSpeed * ZoomSpeedFactor());
        _keyVelocity = Vector2.Lerp(_keyVelocity, target, 1f - Mathf.Exp(-keyboardSmoothing * dt));
        if (_keyVelocity.sqrMagnitude > 0.00001f) PanCamera(_keyVelocity * dt);
    }

    private void HandleDrag(float dt)
    {
        if (_dragging)
        {
            // dragging the map: the camera moves opposite to the finger
            Vector2 camDelta = -_dragPending * (WorldUnitsPerPixel() * dragSensitivity);
            _dragPending = Vector2.zero;
            PanCamera(camDelta);

            if (dt > 0f)
                _dragVelocitySample = Vector2.Lerp(_dragVelocitySample, camDelta / dt, 0.35f);

            if (_dragEnding)
            {
                _dragging = false;
                _dragEnding = false;
                float maxSpeed = maxInertiaSpeed * ZoomSpeedFactor();
                _inertiaVelocity = Vector2.ClampMagnitude(_dragVelocitySample, maxSpeed);
            }
        }
        else if (_inertiaVelocity.sqrMagnitude > 0.0004f)
        {
            PanCamera(_inertiaVelocity * dt);
            _inertiaVelocity = Vector2.Lerp(_inertiaVelocity, Vector2.zero, 1f - Mathf.Exp(-inertiaDamping * dt));
        }
        else
        {
            _inertiaVelocity = Vector2.zero;
        }
    }

    private void HandleFocusCapital(Vector2 screenPos, bool hasPointer)
    {
        if (cam == null || map == null || !map.IsReady || !WorldManager.HasInstance) return;

        Nation n = null;
        if (hasPointer)
        {
            // nation under the mouse (null on sea, neutral land or outside the map)
            Vector3 w = cam.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, -cam.transform.position.z));
            n = map.GetNationAt(w);
        }

        // no mouse (phone), sea, or no owner -> random nation
        if (n == null || n.Capital == null) n = WorldManager.Instance.GetRandomNation();
        if (n == null || n.Capital == null) return;

        Vector2 c = n.Capital.Centroid;
        Vector3 world = map.TileToWorldCenter(Mathf.RoundToInt(c.x), Mathf.RoundToInt(c.y));

        Vector3 p = cam.transform.position;
        p.x = world.x; p.y = world.y;
        cam.transform.position = p;          // instant; bounds are clamped in this frame's Tick

        _keyVelocity = Vector2.zero;
        _inertiaVelocity = Vector2.zero;
        _dragVelocitySample = Vector2.zero;
        _dragPending = Vector2.zero;
    }

    // ?? helpers ??
    private float ZoomSpeedFactor()
    {
        float ratio = Mathf.Max(0.01f, _currentZoom / Mathf.Max(0.01f, referenceZoom));
        return Mathf.Pow(ratio, speedZoomExponent);
    }

    private float WorldUnitsPerPixel() => _currentZoom * 2f / Screen.height;

    private void PanCamera(Vector2 delta)
    {
        Vector3 p = cam.transform.position;
        p.x += delta.x;
        p.y += delta.y;
        cam.transform.position = p;
    }

    private void RefreshBounds()
    {
        if (map == null || !map.IsReady) { _hasBounds = false; return; }

        Rect r = map.WorldRect;
        Vector2 size = r.size * Mathf.Max(0.1f, boundsScale);
        _bounds = new Rect(r.center - size * 0.5f, size);
        _hasBounds = true;
    }

    private float EffectiveMaxZoom()
    {
        float max = maxZoom;
        if (_hasBounds)
        {
            float fit = Mathf.Min(_bounds.height * 0.5f, _bounds.width * 0.5f / Mathf.Max(0.01f, cam.aspect));
            max = Mathf.Min(max, fit);
        }
        return Mathf.Max(minZoom, max);
    }

    private void ClampToBounds()
    {
        if (!_hasBounds) return;

        float halfH = _currentZoom;
        float halfW = _currentZoom * cam.aspect;

        Vector3 p = cam.transform.position;
        float nx = ClampAxis(p.x, _bounds.xMin + halfW, _bounds.xMax - halfW, _bounds.center.x);
        float ny = ClampAxis(p.y, _bounds.yMin + halfH, _bounds.yMax - halfH, _bounds.center.y);

        if (!Mathf.Approximately(nx, p.x)) { _keyVelocity.x = 0f; _inertiaVelocity.x = 0f; _dragVelocitySample.x = 0f; }
        if (!Mathf.Approximately(ny, p.y)) { _keyVelocity.y = 0f; _inertiaVelocity.y = 0f; _dragVelocitySample.y = 0f; }

        p.x = nx; p.y = ny;
        cam.transform.position = p;
    }

    private static float ClampAxis(float v, float min, float max, float center) =>
        min > max ? center : Mathf.Clamp(v, min, max);
}