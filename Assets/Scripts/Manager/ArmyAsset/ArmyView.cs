using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The card of one regiment on a SCREEN SPACE canvas:  [ Marker ][ Manpower ]  on a Background.
/// It has no logic of its own. ArmyManager binds it to a Regiment while that regiment is on screen
/// and places it every LateUpdate from the regiment's world position. Pooled by PoolManager.
/// </summary>
public class RegimentView : MonoBehaviour, IPoolable
{
    public Image Background { get; private set; }
    public Image ArmyMarker { get; private set; }
    public TextMeshProUGUI ManpowerText { get; private set; }
    public Regiment Regiment { get; private set; }

    private long _shownKey = -1;
    private bool _textVisible = true;

    // what the layout was last built for (re-applied only when the settings change)
    private int _appliedSize = -1;
    private Sprite _appliedBg, _appliedMarker;
    private TMP_FontAsset _appliedFont;

    public void OnSpawn() { }

    public void OnDespawn()
    {
        Regiment = null;
        _shownKey = -1;
    }

    /// <summary>Called by ArmyManager right after the card is taken from the pool.</summary>
    internal void Bind(Regiment regiment, ArmyManager army)
    {
        Regiment = regiment;
        EnsureVisual(army);

        Color nation = regiment.Military.Nation.Color;
        Color inv = InverseColor(nation);
        Background.color = new Color(nation.r, nation.g, nation.b, army.BackgroundAlpha);
        ArmyMarker.color = inv;
        ManpowerText.color = inv;

        _shownKey = -1;
        _textVisible = true;
        ManpowerText.gameObject.SetActive(true);
        SetManpower(regiment.Manpower);
    }

    public void SetLocalPosition(Vector2 local)
    {
        ((RectTransform)transform).anchoredPosition = local;
    }

    public void SetTextVisible(bool visible)
    {
        if (visible == _textVisible) return;
        _textVisible = visible;
        if (visible && Regiment != null) SetManpower(Regiment.Manpower);
        ManpowerText.gameObject.SetActive(visible);
    }

    /// <summary>Rewrites the text only when the SHOWN number changes, and never while the text is hidden.</summary>
    public void SetManpower(double manpower)
    {
        if (ManpowerText == null || !_textVisible) return;
        long key = DisplayKey(manpower);
        if (key == _shownKey) return;
        _shownKey = key;
        ManpowerText.text = Regiment.FormatManpower(manpower);
    }

    // one key per displayed value: 0..999 exact, 1.2K steps of 100, 1.2M steps of 100,000
    private static long DisplayKey(double v)
    {
        if (v >= 1000000.0) return 3000000000L + (long)System.Math.Round(v / 100000.0);
        if (v >= 1000.0) return 2000000000L + (long)System.Math.Round(v / 100.0);
        return (long)System.Math.Round(v);
    }

    // ?????????????????????????????????????????????????????????????????????
    //  AUTO-GENERATED UI
    // ?????????????????????????????????????????????????????????????????????
    private void EnsureVisual(ArmyManager army)
    {
        var root = (RectTransform)transform;

        if (Background == null)
        {
            Background = NewImage("Background", root);
            ArmyMarker = NewImage("Marker", root);

            var textGo = new GameObject("Manpower", typeof(RectTransform));
            textGo.transform.SetParent(root, false);
            ManpowerText = textGo.AddComponent<TextMeshProUGUI>();
            ManpowerText.raycastTarget = false;
            ManpowerText.alignment = TextAlignmentOptions.Center;
            ManpowerText.overflowMode = TextOverflowModes.Overflow;
            ManpowerText.fontStyle = FontStyles.Bold;
            _appliedSize = -1;
        }

        if (_appliedSize == army.regimentSize && _appliedBg == army.BackgroundSprite &&
            _appliedMarker == army.MarkerSprite && _appliedFont == army.FontAsset) return;

        _appliedSize = army.regimentSize;
        _appliedBg = army.BackgroundSprite;
        _appliedMarker = army.MarkerSprite;
        _appliedFont = army.FontAsset;

        Vector2 rect = army.RegimentRect;
        float h = rect.y;
        float pad = h * 0.12f;
        float m = h - 2f * pad;

        // the card is positioned by anchoredPosition inside the full-screen "Regiments" root (center anchors)
        root.anchorMin = root.anchorMax = root.pivot = new Vector2(0.5f, 0.5f);
        root.sizeDelta = rect;
        root.localScale = Vector3.one;

        RectTransform bg = Background.rectTransform;
        bg.anchorMin = Vector2.zero; bg.anchorMax = Vector2.one;
        bg.offsetMin = Vector2.zero; bg.offsetMax = Vector2.zero;
        Background.sprite = army.BackgroundSprite;
        Background.type = army.BackgroundSprite != null && army.BackgroundSprite.border.sqrMagnitude > 0f
            ? Image.Type.Sliced : Image.Type.Simple;

        RectTransform mk = ArmyMarker.rectTransform;
        mk.anchorMin = mk.anchorMax = new Vector2(0f, 0.5f);
        mk.pivot = new Vector2(0f, 0.5f);
        mk.anchoredPosition = new Vector2(pad, 0f);
        mk.sizeDelta = new Vector2(m, m);
        ArmyMarker.sprite = army.MarkerSprite;
        ArmyMarker.preserveAspect = true;

        RectTransform tx = ManpowerText.rectTransform;
        tx.anchorMin = Vector2.zero; tx.anchorMax = Vector2.one;
        tx.offsetMin = new Vector2(pad * 2f + m, pad);
        tx.offsetMax = new Vector2(-pad, -pad);
        ManpowerText.fontSize = h * 0.5f;
        if (army.FontAsset != null) ManpowerText.font = army.FontAsset;
    }

    private static Image NewImage(string name, RectTransform parent)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.transform.SetParent(parent, false);
        Image img = go.GetComponent<Image>();
        img.raycastTarget = false;                 // the card must never block clicks on the map
        return img;
    }

    private static Color InverseColor(Color c)
    {
        var inv = new Color(1f - c.r, 1f - c.g, 1f - c.b, 1f);
        float dr = inv.r - c.r, dg = inv.g - c.g, db = inv.b - c.b;
        if (dr * dr + dg * dg + db * db < 0.15f)
        {
            float lum = c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;
            return lum > 0.5f ? Color.black : Color.white;
        }
        return inv;
    }
}