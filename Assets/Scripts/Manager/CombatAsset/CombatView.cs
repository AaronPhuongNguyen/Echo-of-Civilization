using TMPro;
using UnityEngine;
using UnityEngine.UI;

// [Logo] [Side A thousands] vs [Side B thousands] [Logo]
public class CombatView : MonoBehaviour, IPoolable
{
    private RectTransform _rt;
    private GameObject _body;
    private Image _logoA, _logoB;
    private TextMeshProUGUI _numA, _numB, _letA, _letB;
    private bool _built, _hasValues, _hasPos;
    private float _tA, _tB, _dA, _dB, _scale = -1f;
    private int _shownA = -1, _shownB = -1;
    private Nation _nA, _nB;
    private Vector3 _worldTarget, _world;

    public bool Shown { get; private set; } = true;
    public Vector3 World => _world;
    internal int Claim, Vote, Anchor = -1;
    internal float LastSeen;

    private static string[] s_nums;

    public void OnSpawn() { }

    public void OnDespawn()
    {
        _hasPos = false; _hasValues = false; _nA = _nB = null;
        _shownA = _shownB = -1; Anchor = -1; Claim = 0; Vote = 0; _scale = -1f;
        if (_body != null) _body.SetActive(true);
        Shown = true;
    }

    internal void Build(TMP_FontAsset font, float width, float height)
    {
        if (_built) return;
        _built = true;
        _rt = (RectTransform)transform;
        _rt.anchorMin = _rt.anchorMax = _rt.pivot = new Vector2(0.5f, 0.5f);
        _rt.sizeDelta = new Vector2(width, height);

        _body = new GameObject("Body", typeof(RectTransform));
        var brt = (RectTransform)_body.transform;
        brt.SetParent(_rt, false);
        brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one;
        brt.offsetMin = brt.offsetMax = Vector2.zero;

        var bg = Child("Bg", brt, Vector2.zero, new Vector2(width, height)).gameObject.AddComponent<Image>();
        bg.color = new Color(0.06f, 0.06f, 0.08f, 0.88f);
        bg.raycastTarget = false;

        float half = width * 0.5f, h = height - 10f;
        float logoX = half - 24f, numX = 60f;
        _logoA = Logo(brt, "LogoA", -logoX, h, font, out _letA);
        _numA = Text(brt, "NumA", -numX, 80f, height, font, height * 0.52f);
        Text(brt, "Vs", 0f, 26f, height, font, height * 0.38f).text = "vs";
        _numB = Text(brt, "NumB", numX, 80f, height, font, height * 0.52f);
        _logoB = Logo(brt, "LogoB", logoX, h, font, out _letB);
        _numA.enableAutoSizing = _numB.enableAutoSizing = true;
        _numA.fontSizeMin = _numB.fontSizeMin = 8f;
        _numA.fontSizeMax = _numB.fontSizeMax = height * 0.52f;
    }

    private static RectTransform Child(string name, Transform parent, Vector2 pos, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        return rt;
    }

    private static TextMeshProUGUI Text(Transform parent, string name, float x, float w, float h, TMP_FontAsset font, float size)
    {
        var t = Child(name, parent, new Vector2(x, 0f), new Vector2(w, h)).gameObject.AddComponent<TextMeshProUGUI>();
        t.raycastTarget = false;
        t.alignment = TextAlignmentOptions.Center;
        t.overflowMode = TextOverflowModes.Overflow;
        t.enableWordWrapping = false;
        t.fontStyle = FontStyles.Bold;
        t.fontSize = size;
        t.color = Color.white;
        if (font != null) t.font = font;
        return t;
    }

    private static Image Logo(Transform parent, string name, float x, float size, TMP_FontAsset font, out TextMeshProUGUI letter)
    {
        var rt = Child(name, parent, new Vector2(x, 0f), new Vector2(34f, size));
        var img = rt.gameObject.AddComponent<Image>();
        img.raycastTarget = false;
        letter = Text(rt, "Letter", 0f, 34f, size, font, size * 0.6f);
        return img;
    }

    private static void SetLogo(Image img, TextMeshProUGUI letter, Nation n)
    {
        Sprite sp = CombatViewManager.LogoProvider != null ? CombatViewManager.LogoProvider(n) : null;
        img.sprite = sp;
        img.color = sp != null ? Color.white : n.Color;
        letter.gameObject.SetActive(sp == null);
        if (sp == null) letter.text = string.IsNullOrEmpty(n.Name) ? "?" : char.ToUpperInvariant(n.Name[0]).ToString();
    }

    /// <summary>Data only. Screen position is computed every frame by the manager.</summary>
    internal void Apply(Nation a, Nation b, float thousandsA, float thousandsB, Vector3 world)
    {
        if (a != _nA) { _nA = a; SetLogo(_logoA, _letA, a); }
        if (b != _nB) { _nB = b; SetLogo(_logoB, _letB, b); }

        _tA = thousandsA; _tB = thousandsB;
        if (!_hasValues) { _dA = _tA; _dB = _tB; _hasValues = true; }
        else { if (_tA > _dA) _dA = _tA; if (_tB > _dB) _dB = _tB; }   // growth snaps, loss rolls

        _worldTarget = world;
        if (!_hasPos) { _world = world; _hasPos = true; }
    }

    internal void SetVisible(bool v)
    {
        if (v == Shown || _body == null) return;
        Shown = v;
        _body.SetActive(v);
    }

    internal void SetScale(float s)
    {
        if (Mathf.Abs(s - _scale) < 0.001f) return;
        _scale = s;
        _rt.localScale = new Vector3(s, s, 1f);
    }

    internal void SetCanvasPos(Vector2 p) => _rt.anchoredPosition = p;

    private static float Roll(float disp, float target, float dt)
    {
        if (disp <= target) return target;
        float rate = Mathf.Max((disp - target) * 2.5f, 150f);   // men per second, at least 150
        return Mathf.Max(target, disp - rate * dt);
    }

    /// <summary>Rolls the numbers and smooths the WORLD position (never the screen position).</summary>
    internal void Tick(float dt)
    {
        if (!Shown || !_built || !_hasValues) return;

        _dA = Roll(_dA, _tA, dt);
        _dB = Roll(_dB, _tB, dt);
        int a = Mathf.CeilToInt(_dA), b = Mathf.CeilToInt(_dB);
        if (a != _shownA) { _shownA = a; _numA.text = a.ToString("N0"); }
        if (b != _shownB) { _shownB = b; _numB.text = b.ToString("N0"); }

        _world = Vector3.Lerp(_world, _worldTarget, 1f - Mathf.Exp(-10f * dt));
    }
}