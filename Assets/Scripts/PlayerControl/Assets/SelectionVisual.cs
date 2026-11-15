using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Visuals for the player's input (listens to PlayerEvents, no setup needed: PlayerManager creates it).
///  - selection box: translucent fill, crisp border, soft pulse, live count + preview rings of the regiments inside
///  - selected regiments: ring that follows the card (scales with the zoom) + a line to the destination
///  - drawn path preview (Shift / armed drag) with dots on the samples
///  - "AUTO BATTLE" badge while the AI commands the army
/// Pure uGUI on its own overlay canvas: no raycasts, pooled images, zero allocations per frame.
/// </summary>
public class SelectionVisuals : MonoBehaviour
{
    [Header("Box")]
    [SerializeField] private Color boxFill = new Color(0.25f, 0.65f, 1f, 0.14f);
    [SerializeField] private Color boxBorder = new Color(0.45f, 0.8f, 1f, 0.95f);
    [SerializeField, Min(1f)] private float borderPixels = 2f;
    [SerializeField] private bool pulse = true;
    [SerializeField] private bool showBoxCount = true;
    [SerializeField] private bool previewRegimentsInBox = true;

    [Header("Selected regiments")]
    [SerializeField] private Color ringColor = new Color(1f, 0.92f, 0.3f, 0.95f);
    [SerializeField] private Color previewRingColor = new Color(0.6f, 0.9f, 1f, 0.8f);
    [SerializeField] private Color destinationColor = new Color(1f, 0.92f, 0.3f, 0.55f);
    [SerializeField] private bool showDestinationLines = true;
    [SerializeField, Min(8f)] private float ringPixels = 44f;
    [SerializeField, Min(1)] private int maxRings = 128;
    [SerializeField, Min(1f)] private float lineWidth = 3f;

    [Header("Path preview")]
    [SerializeField] private Color pathColor = new Color(0.4f, 1f, 0.55f, 0.9f);
    [SerializeField, Min(1f)] private float pathWidth = 4f;

    [Header("Auto battle badge")]
    [SerializeField] private bool showAutoBadge = true;
    [SerializeField] private Color autoColor = new Color(0.4f, 1f, 0.5f, 1f);

    [SerializeField] private int sortingOrder = 40;

    // pools
    private Canvas _canvas;
    private RectTransform _root;
    private Sprite _ringSprite, _dotSprite;
    private readonly List<Image> _lines = new List<Image>(64);
    private readonly List<Image> _rings = new List<Image>(64);
    private readonly List<Image> _dots = new List<Image>(64);
    private int _lineUsed, _ringUsed, _dotUsed;

    // box
    private RectTransform _boxRoot;
    private Image _boxFillImg, _top, _bottom, _left, _right;
    private TextMeshProUGUI _countLabel, _autoLabel, _debugLabel;
    private bool _boxActive;
    private Rect _box;
    private readonly List<Regiment> _inBox = new List<Regiment>(32);

    // path
    private IReadOnlyList<Vector3> _path;

    private float Inv => 1f / Mathf.Max(0.0001f, _canvas.scaleFactor);

    #region Lifecycle

    private void Awake()
    {
        var go = new GameObject("SelectionVisuals Canvas", typeof(RectTransform));
        go.transform.SetParent(transform, false);
        _canvas = go.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = sortingOrder;
        _root = (RectTransform)go.transform;

        _ringSprite = MakeCircle(64, 0.82f);
        _dotSprite = MakeCircle(32, 0f);

        BuildBox();
        _countLabel = MakeLabel("Count", 22, TextAlignmentOptions.Center);
        _autoLabel = MakeLabel("AutoBadge", 26, TextAlignmentOptions.Center);
        _debugLabel = MakeLabel("DebugBadge", 20, TextAlignmentOptions.Center);
        _debugLabel.rectTransform.sizeDelta = new Vector2(1100f, 36f);
        _countLabel.gameObject.SetActive(false);
        _autoLabel.gameObject.SetActive(false);
        _debugLabel.gameObject.SetActive(false);
    }

    private void OnEnable()
    {
        PlayerEvents.OnBoxSelectVisual += HandleBox;
        PlayerEvents.OnPathVisual += HandlePath;
    }

    private void OnDisable()
    {
        PlayerEvents.OnBoxSelectVisual -= HandleBox;
        PlayerEvents.OnPathVisual -= HandlePath;
        _boxActive = false;
        _path = null;
    }

    private void OnDestroy()
    {
        if (_ringSprite != null) { Destroy(_ringSprite.texture); Destroy(_ringSprite); }
        if (_dotSprite != null) { Destroy(_dotSprite.texture); Destroy(_dotSprite); }
    }

    private void HandleBox(Rect r, bool active) { _boxActive = active; _box = r; }
    private void HandlePath(IReadOnlyList<Vector3> pts, bool active) { _path = active ? pts : null; }

    #endregion

    #region Frame

    private void LateUpdate()
    {
        _lineUsed = _ringUsed = _dotUsed = 0;

        PlayerManager pm = PlayerManager.HasInstance ? PlayerManager.Instance : null;
        Camera cam = Camera.main;
        bool ok = pm != null && cam != null;
        ArmyManager army = ArmyManager.HasInstance ? ArmyManager.Instance : null;
        bool markers = army != null && army.MarkersVisible;

        if (ok)
        {
            if (markers) DrawSelection(pm, cam, army);
            DrawBox(pm, cam);
            DrawPath(cam);
        }
        else _boxRoot.gameObject.SetActive(false);

        DrawAutoBadge(ok ? pm : null);
        DrawDebugBadge(ok ? pm : null);
        TrimPools();
    }

    private void DrawSelection(PlayerManager pm, Camera cam, ArmyManager army)
    {
        float size = ringPixels * Mathf.Max(0.3f, army.CardScale);
        IReadOnlyList<Regiment> sel = pm.SelectedRegiments;
        int n = Mathf.Min(sel.Count, maxRings);
        for (int i = 0; i < n; i++)
        {
            Regiment r = sel[i];
            if (r == null || r.Military == null) continue;
            Vector3 sp = cam.WorldToScreenPoint(r.WorldPosition);
            if (sp.z < 0f) continue;
            Ring(sp, size, ringColor);

            if (showDestinationLines && r.HasPath && r.Destination != null)
            {
                Vector3 dp = cam.WorldToScreenPoint(army.ProvinceWorldPos(r.Destination));
                if (dp.z < 0f) continue;
                Line(sp, dp, destinationColor, lineWidth);
                Dot(dp, lineWidth * 3f, destinationColor);
            }
        }
    }

    private void DrawBox(PlayerManager pm, Camera cam)
    {
        if (!_boxActive || _box.width < 2f || _box.height < 2f)
        {
            _boxRoot.gameObject.SetActive(false);
            _countLabel.gameObject.SetActive(false);
            return;
        }

        float a = pulse ? 0.88f + 0.12f * Mathf.Sin(Time.unscaledTime * 6f) : 1f;
        float s = Inv;
        _boxRoot.gameObject.SetActive(true);
        _boxRoot.anchoredPosition = _box.min * s;
        _boxRoot.sizeDelta = _box.size * s;

        _boxFillImg.color = boxFill;
        Color b = boxBorder; b.a *= a;
        _top.color = _bottom.color = _left.color = _right.color = b;
        float t = borderPixels;
        _top.rectTransform.sizeDelta = new Vector2(0f, t);
        _bottom.rectTransform.sizeDelta = new Vector2(0f, t);
        _left.rectTransform.sizeDelta = new Vector2(t, 0f);
        _right.rectTransform.sizeDelta = new Vector2(t, 0f);

        int count = pm.GetRegimentsInRect(_box, _inBox);
        if (previewRegimentsInBox)
        {
            float size = ringPixels * (ArmyManager.HasInstance ? Mathf.Max(0.3f, ArmyManager.Instance.CardScale) : 1f);
            for (int i = 0; i < _inBox.Count && _ringUsed < maxRings; i++)
            {
                Vector3 sp = cam.WorldToScreenPoint(_inBox[i].WorldPosition);
                if (sp.z >= 0f) Ring(sp, size, previewRingColor);
            }
        }

        if (showBoxCount)
        {
            _countLabel.gameObject.SetActive(true);
            _countLabel.text = count > 0 ? count.ToString() : "";
            _countLabel.color = boxBorder;
            var rt = _countLabel.rectTransform;
            rt.anchoredPosition = new Vector2(_box.xMax, _box.yMax) * s + new Vector2(-16f, -16f);
        }
        else _countLabel.gameObject.SetActive(false);
    }

    private void DrawPath(Camera cam)
    {
        if (_path == null || _path.Count == 0) return;
        Vector3 prev = default;
        bool has = false;
        for (int i = 0; i < _path.Count; i++)
        {
            Vector3 sp = cam.WorldToScreenPoint(_path[i]);
            if (sp.z < 0f) { has = false; continue; }
            if (has) Line(prev, sp, pathColor, pathWidth);
            if (i == _path.Count - 1) Dot(sp, pathWidth * 3.5f, pathColor);
            prev = sp; has = true;
        }
    }

    private void DrawAutoBadge(PlayerManager pm)
    {
        if (!showAutoBadge || pm == null || !pm.AutoBattle) { _autoLabel.gameObject.SetActive(false); return; }
        _autoLabel.gameObject.SetActive(true);
        _autoLabel.text = pm.Status.AtWar ? "AUTO BATTLE" : "AUTO BATTLE (waits for war)";
        Color c = autoColor;
        c.a = 0.7f + 0.3f * Mathf.Sin(Time.unscaledTime * 3f);
        _autoLabel.color = c;
        var rt = _autoLabel.rectTransform;
        rt.anchoredPosition = new Vector2(Screen.width * 0.5f, Screen.height - 40f) * Inv;
    }

    private void DrawDebugBadge(PlayerManager pm)
    {
        if (pm == null || !pm.DebugMode) { _debugLabel.gameObject.SetActive(false); return; }
        _debugLabel.gameObject.SetActive(true);
        _debugLabel.text = pm.DebugHint;
        _debugLabel.color = new Color(1f, 0.55f, 0.2f, 0.75f + 0.25f * Mathf.Sin(Time.unscaledTime * 4f));
        _debugLabel.rectTransform.anchoredPosition = new Vector2(Screen.width * 0.5f, Screen.height - 78f) * Inv;
    }

    #endregion

    #region Pools

    private void Line(Vector2 a, Vector2 b, Color c, float width)
    {
        Image img = Take(_lines, ref _lineUsed, null);
        float s = Inv;
        Vector2 d = b - a;
        var rt = img.rectTransform;
        rt.pivot = new Vector2(0f, 0.5f);
        rt.anchoredPosition = a * s;
        rt.sizeDelta = new Vector2(d.magnitude * s, width);
        rt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
        img.color = c;
    }

    private void Ring(Vector2 screen, float pixels, Color c) => Place(Take(_rings, ref _ringUsed, _ringSprite), screen, pixels, c);
    private void Dot(Vector2 screen, float pixels, Color c) => Place(Take(_dots, ref _dotUsed, _dotSprite), screen, pixels, c);

    private void Place(Image img, Vector2 screen, float pixels, Color c)
    {
        var rt = img.rectTransform;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = screen * Inv;
        rt.sizeDelta = new Vector2(pixels, pixels) * Inv;
        rt.localRotation = Quaternion.identity;
        img.color = c;
    }

    private Image Take(List<Image> pool, ref int used, Sprite sprite)
    {
        if (used >= pool.Count) pool.Add(NewImage("vis", _root, sprite));
        Image img = pool[used++];
        if (!img.gameObject.activeSelf) img.gameObject.SetActive(true);
        return img;
    }

    private void TrimPools()
    {
        Trim(_lines, _lineUsed); Trim(_rings, _ringUsed); Trim(_dots, _dotUsed);
        _autoLabel.transform.SetAsLastSibling();
        _debugLabel.transform.SetAsLastSibling();
        _countLabel.transform.SetAsLastSibling();
    }

    private static void Trim(List<Image> pool, int used)
    {
        for (int i = used; i < pool.Count; i++)
        {
            if (!pool[i].gameObject.activeSelf) break;          // everything behind is already hidden
            pool[i].gameObject.SetActive(false);
        }
    }

    #endregion

    #region Construction

    private Image NewImage(string name, Transform parent, Sprite sprite)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.sprite = sprite;
        img.raycastTarget = false;
        var rt = img.rectTransform;
        rt.anchorMin = rt.anchorMax = Vector2.zero;
        return img;
    }

    private void BuildBox()
    {
        var go = new GameObject("Box", typeof(RectTransform));
        go.transform.SetParent(_root, false);
        _boxRoot = (RectTransform)go.transform;
        _boxRoot.anchorMin = _boxRoot.anchorMax = Vector2.zero;
        _boxRoot.pivot = Vector2.zero;

        _boxFillImg = NewImage("Fill", _boxRoot, null);
        Stretch(_boxFillImg.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        _top = NewImage("Top", _boxRoot, null);
        Stretch(_top.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
        _bottom = NewImage("Bottom", _boxRoot, null);
        Stretch(_bottom.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f));
        _left = NewImage("Left", _boxRoot, null);
        Stretch(_left.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f));
        _right = NewImage("Right", _boxRoot, null);
        Stretch(_right.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f));
        _boxRoot.gameObject.SetActive(false);
    }

    private static void Stretch(RectTransform rt, Vector2 min, Vector2 max, Vector2 pivot)
    {
        rt.anchorMin = min; rt.anchorMax = max; rt.pivot = pivot;
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = Vector2.zero;
    }

    private TextMeshProUGUI MakeLabel(string name, float size, TextAlignmentOptions align)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(_root, false);
        var t = go.AddComponent<TextMeshProUGUI>();
        if (ArmyManager.HasInstance && ArmyManager.Instance.FontAsset != null) t.font = ArmyManager.Instance.FontAsset;
        t.fontSize = size;
        t.alignment = align;
        t.fontStyle = FontStyles.Bold;
        t.raycastTarget = false;
        t.enableWordWrapping = false;
        var rt = t.rectTransform;
        rt.anchorMin = rt.anchorMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(480f, 40f);
        return t;
    }

    /// <summary>White anti-aliased disc (inner = 0) or ring (inner = inner radius as a fraction of the outer one).</summary>
    private static Sprite MakeCircle(int size, float inner)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var px = new Color32[size * size];
        float c = (size - 1) * 0.5f, R = size * 0.5f - 1f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                float a = Mathf.Clamp01(R - d + 0.5f);
                if (inner > 0f) a *= Mathf.Clamp01(d - R * inner + 0.5f);
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
    }

    #endregion
}