using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Battle card:  [Logo][Side A manpower] | [Logo][Side B manpower]   on a dark background.
/// Logos are tinted with the nation color of their side. Pooled by PoolManager, placed by CombatManager.
/// </summary>
public class BattleView : MonoBehaviour, IPoolable
{
    private Image _bg, _divider, _logoA, _logoB;
    private TextMeshProUGUI _textA, _textB;
    private bool _built;

    public void OnSpawn() { }
    public void OnDespawn() { }

    internal void Bind(Battle b, CombatManager cm, ArmyManager army)
    {
        Build(cm, army);

        _bg.sprite = cm.BackgroundSprite;
        _bg.type = cm.BackgroundSprite != null && cm.BackgroundSprite.border.sqrMagnitude > 0f ? Image.Type.Sliced : Image.Type.Simple;

        _logoA.sprite = cm.AttackLogo; _logoA.color = b.ColorA;
        _logoB.sprite = cm.DefendLogo; _logoB.color = b.ColorB;
        SetNumbers(b.ManpowerA, b.ManpowerB);
    }

    public void SetLocalPosition(Vector2 local) => ((RectTransform)transform).anchoredPosition = local;

    public void SetNumbers(double a, double b)
    {
        if (_textA == null) return;
        string sa = Regiment.FormatManpower(a), sb = Regiment.FormatManpower(b);
        if (_textA.text != sa) _textA.text = sa;
        if (_textB.text != sb) _textB.text = sb;
    }

    private void Build(CombatManager cm, ArmyManager army)
    {
        if (_built) return;
        _built = true;

        Vector2 rect = army.RegimentRect * cm.battleCardScale;
        float h = rect.y;
        float w = rect.x * 2f + h * 0.2f;
        float pad = h * 0.12f;
        float m = h - 2f * pad;

        var root = (RectTransform)transform;
        root.anchorMin = root.anchorMax = root.pivot = new Vector2(0.5f, 0.5f);
        root.sizeDelta = new Vector2(w, h);
        root.localScale = Vector3.one;

        _bg = NewImage("Background", root);
        _bg.color = new Color(0.08f, 0.08f, 0.08f, 0.88f);
        Stretch(_bg.rectTransform);

        _divider = NewImage("Divider", root);
        _divider.color = new Color(1f, 1f, 1f, 0.85f);
        RectTransform dv = _divider.rectTransform;
        dv.anchorMin = dv.anchorMax = new Vector2(0.5f, 0.5f);
        dv.pivot = new Vector2(0.5f, 0.5f);
        dv.sizeDelta = new Vector2(Mathf.Max(2f, h * 0.04f), h * 0.8f);
        dv.anchoredPosition = Vector2.zero;

        // left half: side A      right half: side B (same layout)
        _logoA = NewImage("LogoA", root);
        Place(_logoA.rectTransform, 0f, pad, m);
        _logoA.preserveAspect = true;
        _textA = NewText("ManpowerA", root, 0f, 0.5f, pad * 2f + m, pad * 0.5f, pad, h);

        _logoB = NewImage("LogoB", root);
        Place(_logoB.rectTransform, 0.5f, pad, m);
        _logoB.preserveAspect = true;
        _textB = NewText("ManpowerB", root, 0.5f, 1f, pad * 2f + m, pad, pad, h);

        if (army.FontAsset != null) { _textA.font = army.FontAsset; _textB.font = army.FontAsset; }
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }

    private static void Place(RectTransform rt, float anchorX, float pad, float size)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(anchorX, 0.5f);
        rt.pivot = new Vector2(0f, 0.5f);
        rt.anchoredPosition = new Vector2(pad, 0f);
        rt.sizeDelta = new Vector2(size, size);
    }

    private static TextMeshProUGUI NewText(string name, RectTransform parent, float minX, float maxX,
                                           float left, float right, float vert, float h)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<TextMeshProUGUI>();
        t.raycastTarget = false;
        t.alignment = TextAlignmentOptions.Center;
        t.overflowMode = TextOverflowModes.Overflow;
        t.fontStyle = FontStyles.Bold;
        t.fontSize = h * 0.45f;
        t.color = Color.white;
        RectTransform rt = t.rectTransform;
        rt.anchorMin = new Vector2(minX, 0f); rt.anchorMax = new Vector2(maxX, 1f);
        rt.offsetMin = new Vector2(left, vert); rt.offsetMax = new Vector2(-right, -vert);
        return t;
    }

    private static Image NewImage(string name, RectTransform parent)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.transform.SetParent(parent, false);
        Image img = go.GetComponent<Image>();
        img.raycastTarget = false;
        return img;
    }
}