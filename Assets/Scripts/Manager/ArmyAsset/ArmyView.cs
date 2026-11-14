using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The card of one regiment on a SCREEN SPACE canvas: a square in the nation color with ONE number inside.
/// Size follows the zoom (ArmyManager.CardScale), so it is never "sometimes big, sometimes small".
/// Pooled by PoolManager. No logic of its own.
/// </summary>
public class RegimentView : MonoBehaviour, IPoolable
{
    #region State

    public Image Background { get; private set; }
    public TextMeshProUGUI ValueText { get; private set; }
    public Regiment Regiment { get; private set; }

    private const int MaxCachedValue = 200;
    private static string[] s_numbers;

    private int _shownValue = -1;
    private bool _textVisible = true;
    private float _appliedScale = -1f;

    private int _appliedSize = -1;
    private Sprite _appliedBg;
    private TMP_FontAsset _appliedFont;

    #endregion

    #region Pool & binding

    public void OnSpawn() { }

    public void OnDespawn()
    {
        Regiment = null;
        _shownValue = -1;
        _appliedScale = -1f;
    }

    internal void Bind(Regiment regiment, ArmyManager army)
    {
        Regiment = regiment;
        EnsureVisual(army);

        Color nation = regiment.Military.Nation.Color;
        Background.color = new Color(nation.r, nation.g, nation.b, army.BackgroundAlpha);
        ValueText.color = Color.white;

        _appliedScale = -1f;
        SetScale(army.CardScale, army.columnScale);

        _shownValue = -1;
        _textVisible = true;
        ValueText.gameObject.SetActive(true);
        SetManpower(regiment.Manpower);
    }

    #endregion

    #region Updates

    public void SetLocalPosition(Vector2 local) => ((RectTransform)transform).anchoredPosition = local;

    /// <summary>Zoom scale (columns are slightly smaller). Applied only when it changes.</summary>
    internal void SetScale(float scale, float columnScale)
    {
        float s = scale * (Regiment != null && Regiment.IsColumn ? columnScale : 1f);
        if (Mathf.Abs(s - _appliedScale) < 0.001f) return;
        _appliedScale = s;
        transform.localScale = new Vector3(s, s, 1f);
    }

    public void SetTextVisible(bool visible)
    {
        if (visible == _textVisible) return;
        _textVisible = visible;
        if (visible && Regiment != null) SetManpower(Regiment.Manpower);
        ValueText.gameObject.SetActive(visible);
    }

    public void SetManpower(double manpower)
    {
        if (ValueText == null || !_textVisible || !ArmyManager.HasInstance) return;
        int value = ArmyManager.Instance.ToValue(manpower);
        if (value == _shownValue) return;
        _shownValue = value;
        ValueText.text = Number(value);
    }

    private static string Number(int v)
    {
        if (s_numbers == null)
        {
            s_numbers = new string[MaxCachedValue + 1];
            for (int i = 0; i <= MaxCachedValue; i++) s_numbers[i] = i.ToString();
        }
        return v >= 0 && v <= MaxCachedValue ? s_numbers[v] : v.ToString();
    }

    #endregion

    #region Auto-generated UI

    private void EnsureVisual(ArmyManager army)
    {
        var root = (RectTransform)transform;

        if (Background == null)
        {
            var bgGo = new GameObject("Square", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            bgGo.transform.SetParent(root, false);
            Background = bgGo.GetComponent<Image>();
            Background.raycastTarget = false;

            var textGo = new GameObject("Value", typeof(RectTransform));
            textGo.transform.SetParent(root, false);
            ValueText = textGo.AddComponent<TextMeshProUGUI>();
            ValueText.raycastTarget = false;
            ValueText.alignment = TextAlignmentOptions.Center;
            ValueText.overflowMode = TextOverflowModes.Overflow;
            ValueText.enableWordWrapping = false;
            ValueText.fontStyle = FontStyles.Bold;
            _appliedSize = -1;
        }

        if (_appliedSize == army.regimentSize && _appliedBg == army.BackgroundSprite && _appliedFont == army.FontAsset) return;
        _appliedSize = army.regimentSize;
        _appliedBg = army.BackgroundSprite;
        _appliedFont = army.FontAsset;

        Vector2 rect = army.RegimentRect;

        root.anchorMin = root.anchorMax = root.pivot = new Vector2(0.5f, 0.5f);
        root.sizeDelta = rect;

        RectTransform bg = Background.rectTransform;
        bg.anchorMin = Vector2.zero; bg.anchorMax = Vector2.one;
        bg.offsetMin = Vector2.zero; bg.offsetMax = Vector2.zero;
        Background.sprite = army.BackgroundSprite;
        Background.type = army.BackgroundSprite != null && army.BackgroundSprite.border.sqrMagnitude > 0f
            ? Image.Type.Sliced : Image.Type.Simple;

        RectTransform tx = ValueText.rectTransform;
        tx.anchorMin = Vector2.zero; tx.anchorMax = Vector2.one;
        tx.offsetMin = Vector2.zero; tx.offsetMax = Vector2.zero;
        ValueText.fontSize = rect.y * 0.55f;
        if (army.FontAsset != null) ValueText.font = army.FontAsset;
    }

    #endregion
}