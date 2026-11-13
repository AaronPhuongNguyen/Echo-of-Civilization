using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Runtime UGUI debug panel. Add this component to ANY GameObject (no canvas / UI assignment needed).
/// It builds its own Canvas + EventSystem and creates one tab per manager (War, Relationship,
/// NationFormation, Map). Every [ContextMenu] method of those managers becomes a button, and every
/// private "debug*" int/float field becomes an input box (nation A/B/C, province, amount, parts...).
/// Everything works through reflection, so new context menus appear automatically.
/// </summary>
public class DebugMenuPanel : MonoBehaviour
{
    [SerializeField] private bool startOpen = false;
    [SerializeField] private int fontSize = 28;
    [SerializeField] private int buttonHeight = 70;

    private static readonly Type[] ManagerTypes =
    {
        typeof(WarManager), typeof(RelationshipManager), typeof(NationFormationManager), typeof(MapManager)
    };
    private static readonly string[] TabNames = { "War", "Relation", "Formation", "Map" };

    private GameObject _panel;
    private RectTransform _content;
    private Text _log;
    private Font _font;
    private readonly List<Image> _tabImages = new List<Image>();
    private int _current = -1;

    private void Start()
    {
        _font = LoadFont();
        EnsureEventSystem();
        BuildUI();
        _panel.SetActive(startOpen);
        if (startOpen) SelectTab(0);
    }

    // ─────────────────────────────────────────────── UI BUILD
    private static Font LoadFont()
    {
#if UNITY_2022_2_OR_NEWER
        return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
#else
        return Resources.GetBuiltinResource<Font>("Arial.ttf");
#endif
    }

    private static void EnsureEventSystem()
    {
#if UNITY_2022_2_OR_NEWER
        if (FindFirstObjectByType<EventSystem>() != null) return;
#else
        if (FindObjectOfType<EventSystem>() != null) return;
#endif
        var go = new GameObject("EventSystem", typeof(EventSystem));
        Type inputSys = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
        if (inputSys != null) go.AddComponent(inputSys);
        else go.AddComponent<StandaloneInputModule>();
    }

    private void BuildUI()
    {
        var canvasGo = new GameObject("DebugMenuCanvas");
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 9999;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080, 1920);
        scaler.matchWidthOrHeight = 0.5f;
        canvasGo.AddComponent<GraphicRaycaster>();

        // toggle button (top-left)
        var toggle = MakeButton(canvasGo.transform, "DBG", new Color(0.1f, 0.1f, 0.1f, 0.85f), () =>
        {
            _panel.SetActive(!_panel.activeSelf);
            if (_panel.activeSelf) SelectTab(_current < 0 ? 0 : _current);
        });
        var trt = (RectTransform)toggle.transform;
        trt.anchorMin = trt.anchorMax = trt.pivot = new Vector2(0, 1);
        trt.anchoredPosition = new Vector2(10, -10);
        trt.sizeDelta = new Vector2(140, 80);

        // main panel
        _panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
        _panel.transform.SetParent(canvasGo.transform, false);
        _panel.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.88f);
        var prt = (RectTransform)_panel.transform;
        prt.anchorMin = new Vector2(0, 0); prt.anchorMax = new Vector2(1, 1);
        prt.offsetMin = new Vector2(10, 10); prt.offsetMax = new Vector2(-10, -100);

        // tabs
        var tabs = new GameObject("Tabs", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        tabs.transform.SetParent(_panel.transform, false);
        var tabsRt = (RectTransform)tabs.transform;
        tabsRt.anchorMin = new Vector2(0, 1); tabsRt.anchorMax = new Vector2(1, 1);
        tabsRt.pivot = new Vector2(0.5f, 1);
        tabsRt.sizeDelta = new Vector2(0, buttonHeight);
        var hl = tabs.GetComponent<HorizontalLayoutGroup>();
        hl.spacing = 4; hl.childForceExpandWidth = true; hl.childForceExpandHeight = true;
        for (int i = 0; i < ManagerTypes.Length; i++)
        {
            int idx = i;
            var b = MakeButton(tabs.transform, TabNames[i], new Color(0.25f, 0.25f, 0.25f), () => SelectTab(idx));
            _tabImages.Add(b.GetComponent<Image>());
        }

        // log line
        var logGo = new GameObject("Log", typeof(RectTransform), typeof(Text));
        logGo.transform.SetParent(_panel.transform, false);
        _log = logGo.GetComponent<Text>();
        _log.font = _font; _log.fontSize = fontSize - 6; _log.color = Color.yellow;
        _log.horizontalOverflow = HorizontalWrapMode.Wrap;
        var lrt = (RectTransform)logGo.transform;
        lrt.anchorMin = new Vector2(0, 0); lrt.anchorMax = new Vector2(1, 0);
        lrt.pivot = new Vector2(0.5f, 0);
        lrt.sizeDelta = new Vector2(-20, 90);
        lrt.anchoredPosition = new Vector2(0, 5);
        _log.text = "Ready.";

        // scroll area
        var scrollGo = new GameObject("Scroll", typeof(RectTransform), typeof(ScrollRect), typeof(RectMask2D), typeof(Image));
        scrollGo.transform.SetParent(_panel.transform, false);
        scrollGo.GetComponent<Image>().color = new Color(1, 1, 1, 0.03f);
        var srt = (RectTransform)scrollGo.transform;
        srt.anchorMin = Vector2.zero; srt.anchorMax = Vector2.one;
        srt.offsetMin = new Vector2(5, 100); srt.offsetMax = new Vector2(-5, -(buttonHeight + 6));

        var contentGo = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        contentGo.transform.SetParent(scrollGo.transform, false);
        _content = (RectTransform)contentGo.transform;
        _content.anchorMin = new Vector2(0, 1); _content.anchorMax = new Vector2(1, 1);
        _content.pivot = new Vector2(0.5f, 1);
        _content.anchoredPosition = Vector2.zero;
        var vl = contentGo.GetComponent<VerticalLayoutGroup>();
        vl.spacing = 6; vl.padding = new RectOffset(6, 6, 6, 6);
        vl.childControlHeight = true; vl.childControlWidth = true;
        vl.childForceExpandHeight = false; vl.childForceExpandWidth = true;
        contentGo.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var sr = scrollGo.GetComponent<ScrollRect>();
        sr.content = _content; sr.horizontal = false; sr.vertical = true;
        sr.movementType = ScrollRect.MovementType.Clamped;
        sr.scrollSensitivity = 40f;
    }

    private GameObject MakeButton(Transform parent, string label, Color color, Action onClick)
    {
        var go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = color;
        go.GetComponent<LayoutElement>().minHeight = buttonHeight;
        go.GetComponent<Button>().onClick.AddListener(() => onClick());

        var tgo = new GameObject("Text", typeof(RectTransform), typeof(Text));
        tgo.transform.SetParent(go.transform, false);
        var t = tgo.GetComponent<Text>();
        t.font = _font; t.fontSize = fontSize; t.color = Color.white;
        t.alignment = TextAnchor.MiddleCenter; t.text = label;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        var rt = (RectTransform)tgo.transform;
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(6, 2); rt.offsetMax = new Vector2(-6, -2);
        return go;
    }

    // ─────────────────────────────────────────────── TABS
    private void SelectTab(int index)
    {
        _current = index;
        for (int i = 0; i < _tabImages.Count; i++)
            _tabImages[i].color = i == index ? new Color(0.2f, 0.5f, 0.9f) : new Color(0.25f, 0.25f, 0.25f);

        for (int i = _content.childCount - 1; i >= 0; i--) Destroy(_content.GetChild(i).gameObject);

        Type type = ManagerTypes[index];
        UnityEngine.Object target = FindManager(type);
        if (target == null)
        {
            AddLabel("No " + type.Name + " in the scene (generate the map / start the game first).");
            return;
        }

        const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        // debug* fields -> input boxes
        foreach (FieldInfo f in type.GetFields(F))
        {
            if (!f.Name.StartsWith("debug", StringComparison.Ordinal)) continue;
            if (f.FieldType != typeof(int) && f.FieldType != typeof(float)) continue;
            AddField(target, f);
        }

        // [ContextMenu] methods -> buttons
        foreach (MethodInfo m in type.GetMethods(F))
        {
            var attr = m.GetCustomAttribute<ContextMenu>();
            if (attr == null || m.GetParameters().Length != 0) continue;
            MethodInfo method = m;
            UnityEngine.Object tgt = target;
            string label = attr.menuItem;
            var b = MakeButton(_content, label, new Color(0.18f, 0.35f, 0.2f), () => Invoke(tgt, method, label));
            b.GetComponent<LayoutElement>().minHeight = buttonHeight;
        }
    }

    private static UnityEngine.Object FindManager(Type t)
    {
#if UNITY_2022_2_OR_NEWER
        return FindFirstObjectByType(t);
#else
        return FindObjectOfType(t);
#endif
    }

    private void Invoke(UnityEngine.Object target, MethodInfo m, string label)
    {
        if (target == null) { _log.text = "Target was destroyed."; return; }
        try
        {
            m.Invoke(target, null);
            _log.text = "Ran: " + label + "  (details in Console)";
        }
        catch (Exception e)
        {
            Exception inner = e.InnerException ?? e;
            _log.text = "ERROR: " + inner.Message;
            Debug.LogException(inner);
        }
    }

    private void AddLabel(string text)
    {
        var go = new GameObject("Label", typeof(RectTransform), typeof(Text), typeof(LayoutElement));
        go.transform.SetParent(_content, false);
        var t = go.GetComponent<Text>();
        t.font = _font; t.fontSize = fontSize; t.color = Color.white; t.text = text;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        go.GetComponent<LayoutElement>().minHeight = buttonHeight;
    }

    private void AddField(UnityEngine.Object target, FieldInfo field)
    {
        var row = new GameObject(field.Name, typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        row.transform.SetParent(_content, false);
        row.GetComponent<LayoutElement>().minHeight = buttonHeight;
        var hl = row.GetComponent<HorizontalLayoutGroup>();
        hl.spacing = 6; hl.childForceExpandWidth = true; hl.childForceExpandHeight = true;
        hl.childControlWidth = true; hl.childControlHeight = true;

        var lgo = new GameObject("Name", typeof(RectTransform), typeof(Text), typeof(LayoutElement));
        lgo.transform.SetParent(row.transform, false);
        var lt = lgo.GetComponent<Text>();
        lt.font = _font; lt.fontSize = fontSize - 4; lt.color = Color.white;
        lt.alignment = TextAnchor.MiddleLeft; lt.text = field.Name;
        lgo.GetComponent<LayoutElement>().flexibleWidth = 1;

        var igo = new GameObject("Input", typeof(RectTransform), typeof(Image), typeof(InputField), typeof(LayoutElement));
        igo.transform.SetParent(row.transform, false);
        igo.GetComponent<Image>().color = new Color(1, 1, 1, 0.15f);
        igo.GetComponent<LayoutElement>().flexibleWidth = 1;

        var tgo = new GameObject("Text", typeof(RectTransform), typeof(Text));
        tgo.transform.SetParent(igo.transform, false);
        var t = tgo.GetComponent<Text>();
        t.font = _font; t.fontSize = fontSize; t.color = Color.white;
        t.alignment = TextAnchor.MiddleCenter; t.supportRichText = false;
        var trt = (RectTransform)tgo.transform;
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(6, 2); trt.offsetMax = new Vector2(-6, -2);

        var input = igo.GetComponent<InputField>();
        input.textComponent = t;
        bool isInt = field.FieldType == typeof(int);
        input.contentType = isInt ? InputField.ContentType.IntegerNumber : InputField.ContentType.DecimalNumber;
        object cur = field.GetValue(target);
        input.text = Convert.ToString(cur, System.Globalization.CultureInfo.InvariantCulture);

        input.onEndEdit.AddListener(s =>
        {
            if (target == null) return;
            try
            {
                if (isInt && int.TryParse(s, out int iv)) field.SetValue(target, iv);
                else if (!isInt && float.TryParse(s, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out float fv)) field.SetValue(target, fv);
            }
            catch (Exception e) { _log.text = "ERROR: " + e.Message; }
        });
    }
}