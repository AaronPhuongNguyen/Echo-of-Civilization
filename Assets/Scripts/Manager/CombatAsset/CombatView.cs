using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// ─────────────────────────────────────────────────────────────────────────────
//  COMBAT VIEW   [Logo][A manpower] vs [Logo][B manpower]
//  - drawn on its own root ABOVE the army cards
//  - overlapping views (in SCREEN space) are merged into one, separated again when they stop overlapping
//  - the numbers roll down one value at a time (100 -> 99 -> 98 ...), they never jump
//  - the battle clusters (and the push / shove) come from CombatManager
// ─────────────────────────────────────────────────────────────────────────────

public class CombatView : MonoBehaviour, IPoolable
{
    private RectTransform _rt;
    private GameObject _body;
    private Image _logoA, _logoB;
    private TextMeshProUGUI _numA, _numB, _letA, _letB;
    private bool _built, _visible = true, _hasPos, _hasValues;
    private float _scale = -1f, _tA, _tB, _dA, _dB;
    private int _shownA = -1, _shownB = -1;
    private Vector2 _target, _current;
    private Nation _nA, _nB;

    internal int Claim, Vote, Anchor = -1;
    internal float LastSeen;

    private static string[] s_nums;

    public void OnSpawn() { }

    public void OnDespawn()
    {
        _hasPos = false; _hasValues = false; _nA = _nB = null;
        _shownA = _shownB = -1; Anchor = -1; Claim = 0; Vote = 0; _scale = -1f;
        if (_body != null) _body.SetActive(true);
        _visible = true;
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

        var bg = MakeChild("Bg", brt, Vector2.zero, new Vector2(width, height)).gameObject.AddComponent<Image>();
        bg.color = new Color(0.06f, 0.06f, 0.08f, 0.88f);
        bg.raycastTarget = false;
        var bgRt = bg.rectTransform; bgRt.anchorMin = Vector2.zero; bgRt.anchorMax = Vector2.one;
        bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;

        float h = height - 10f;
        _logoA = MakeLogo(brt, "LogoA", -106f, h, font, out _letA);
        _numA = MakeText(brt, "NumA", -52f, 66f, height, font, height * 0.52f);
        MakeText(brt, "Vs", 0f, 26f, height, font, height * 0.38f).text = "vs";
        _logoB = MakeLogo(brt, "LogoB", 36f, h, font, out _letB);
        _numB = MakeText(brt, "NumB", 90f, 66f, height, font, height * 0.52f);
    }

    private static RectTransform MakeChild(string name, Transform parent, Vector2 pos, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        return rt;
    }

    private static TextMeshProUGUI MakeText(Transform parent, string name, float x, float w, float h, TMP_FontAsset font, float size)
    {
        var t = MakeChild(name, parent, new Vector2(x, 0f), new Vector2(w, h)).gameObject.AddComponent<TextMeshProUGUI>();
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

    private static Image MakeLogo(Transform parent, string name, float x, float size, TMP_FontAsset font, out TextMeshProUGUI letter)
    {
        var rt = MakeChild(name, parent, new Vector2(x, 0f), new Vector2(34f, size));
        var img = rt.gameObject.AddComponent<Image>();
        img.raycastTarget = false;
        letter = MakeText(rt, "Letter", 0f, 34f, size, font, size * 0.6f);
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

    internal void Apply(Nation a, Nation b, float valA, float valB, Vector2 local, bool snap, float scale)
    {
        if (a != _nA) { _nA = a; SetLogo(_logoA, _letA, a); }
        if (b != _nB) { _nB = b; SetLogo(_logoB, _letB, b); }

        _tA = valA; _tB = valB;
        if (!_hasValues) { _dA = _tA; _dB = _tB; _hasValues = true; }
        else { if (_tA > _dA) _dA = _tA; if (_tB > _dB) _dB = _tB; }   // growth snaps, loss rolls

        if (Mathf.Abs(scale - _scale) > 0.001f) { _scale = scale; _rt.localScale = new Vector3(scale, scale, 1f); }

        _target = local;
        if (!_hasPos || snap) { _current = local; _rt.anchoredPosition = local; _hasPos = true; }
    }

    internal void SetVisible(bool v)
    {
        if (v == _visible || _body == null) return;
        _visible = v;
        _body.SetActive(v);
    }

    private static float Roll(float disp, float target, float dt)
    {
        if (disp <= target) return target;
        float rate = Mathf.Max((disp - target) * 2.5f, 4f);      // values per second, always at least 4
        return Mathf.Max(target, disp - rate * dt);
    }

    internal void Tick(float dt)
    {
        if (!_visible || !_built || !_hasValues) return;

        _dA = Roll(_dA, _tA, dt);
        _dB = Roll(_dB, _tB, dt);
        int a = Mathf.CeilToInt(_dA), b = Mathf.CeilToInt(_dB);
        if (a != _shownA) { _shownA = a; _numA.text = Num(a); }
        if (b != _shownB) { _shownB = b; _numB.text = Num(b); }

        if ((_current - _target).sqrMagnitude > 0.01f)
        {
            _current = Vector2.Lerp(_current, _target, 1f - Mathf.Exp(-18f * dt));
            _rt.anchoredPosition = _current;
        }
    }

    private static string Num(int v)
    {
        if (s_nums == null)
        {
            s_nums = new string[2001];
            for (int i = 0; i <= 2000; i++) s_nums[i] = i.ToString();
        }
        return v >= 0 && v <= 2000 ? s_nums[v] : v.ToString();
    }
}

[DefaultExecutionOrder(-500)]
public class CombatViewManager : MonoBehaviour
{
    #region Singleton

    private static CombatViewManager _instance;
    private static bool _quitting;
    public static bool HasInstance => _instance != null;

    public static CombatViewManager Instance
    {
        get
        {
            if (_instance == null && !_quitting)
            {
#if UNITY_2022_2_OR_NEWER
                _instance = FindFirstObjectByType<CombatViewManager>();
#else
                _instance = FindObjectOfType<CombatViewManager>();
#endif
                if (_instance == null) _instance = new GameObject("CombatViewManager").AddComponent<CombatViewManager>();
            }
            return _instance;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() { _instance = null; _quitting = false; LogoProvider = null; }

    private void OnApplicationQuit() => _quitting = true;
    private void OnDestroy() { if (_instance == this) _instance = null; if (_template != null) Destroy(_template.gameObject); }

    #endregion

    /// <summary>Plug your flags in here: return null to get the colored letter logo.</summary>
    public static Func<Nation, Sprite> LogoProvider;

    [Header("Layout (canvas units)")]
    [Min(100f)] public float width = 256f;
    [Min(20f)] public float height = 44f;
    [Tooltip("The view floats this far above the battle (scaled with zoom).")]
    public float offsetY = 46f;
    [Range(0.3f, 1f)] public float minScale = 0.7f;
    [Range(1f, 2f)] public float maxScale = 1.2f;

    [Header("Behaviour")]
    [Tooltip("A view stays this many real seconds after its battle ended.")]
    [Min(0f)] public float endGrace = 0.75f;
    [Min(1)] public int maxViews = 64;
    [Tooltip("Real seconds between two rebuilds (a camera move rebuilds at once).")]
    [Min(0.01f)] public float rebuildInterval = 0.05f;

    #region Data

    private sealed class Raw { public List<Regiment> units; public Vector2 local; public double men; public int parent, group; public bool valid; }
    private sealed class Grp { public readonly List<Regiment> units = new List<Regiment>(16); public Vector2 sum; public double men; }

    private readonly List<Raw> _raws = new List<Raw>();
    private readonly List<Grp> _groups = new List<Grp>();
    private readonly List<CombatView> _views = new List<CombatView>();
    private readonly List<CombatView> _voted = new List<CombatView>();
    private readonly Dictionary<int, CombatView> _uidView = new Dictionary<int, CombatView>();

    private List<List<Regiment>> _src;
    private int _srcCount, _stamp, _seenVersion = -1;
    private bool _dirty;
    private float _nextRebuild;
    private ArmyManager _army;
    private RectTransform _root;
    private CombatView _template;

    #endregion

    #region API (called by CombatManager)

    /// <summary>The current battle clusters (read-only, owned by CombatManager).</summary>
    internal void Submit(List<List<Regiment>> clusters, int count)
    {
        _src = clusters;
        _srcCount = count;
        _dirty = true;
    }

    public void Clear()
    {
        _srcCount = 0;
        for (int i = _views.Count - 1; i >= 0; i--) ReleaseView(_views[i]);
        _views.Clear();
        _uidView.Clear();
    }

    #endregion

    #region Frame

    private void LateUpdate()
    {
        if (_srcCount == 0 && _views.Count == 0) return;
        if (_army == null) _army = ArmyManager.HasInstance ? ArmyManager.Instance : null;
        if (_army == null) return;

        bool cam = _army.ViewVersion != _seenVersion;
        if (_dirty || cam || Time.unscaledTime >= _nextRebuild)
        {
            _seenVersion = _army.ViewVersion;
            _dirty = false;
            _nextRebuild = Time.unscaledTime + rebuildInterval;
            Rebuild(cam);
        }

        float dt = Time.unscaledDeltaTime;
        for (int i = 0; i < _views.Count; i++) _views[i].Tick(dt);
    }

    private static bool Hostile(Nation a, Nation b) =>
        a != null && b != null && a != b && WarManager.HasInstance && WarManager.Instance.IsAtWar(a, b);

    private int FindRaw(int x)
    {
        while (_raws[x].parent != x) { _raws[x].parent = _raws[_raws[x].parent].parent; x = _raws[x].parent; }
        return x;
    }

    private void Rebuild(bool cameraMoved)
    {
        RectTransform root = GetRoot();
        if (root == null) return;
        _stamp++;
        float now = Time.unscaledTime;

        bool show = _army.MarkersVisible;
        float s = Mathf.Clamp(_army.CardScale, minScale, maxScale);
        float w = width * s, h = height * s;
        PlayerManager pm = PlayerManager.HasInstance ? PlayerManager.Instance : null;

        // 1) raw battles -> screen position
        int rc = show && _src != null ? Mathf.Min(_srcCount, maxViews) : 0;
        while (_raws.Count < rc) _raws.Add(new Raw());
        for (int i = 0; i < rc; i++)
        {
            Raw r = _raws[i];
            r.units = _src[i]; r.parent = i; r.group = -1; r.men = 0.0; r.valid = false;
            Vector3 c = Vector3.zero;
            for (int k = 0; k < r.units.Count; k++)
            {
                Regiment u = r.units[k];
                if (u.Military == null || u.Manpower <= 0.5) continue;
                c += u.WorldPosition * (float)u.Manpower;
                r.men += u.Manpower;
            }
            if (r.men <= 0.0) continue;
            r.valid = _army.TryWorldToCanvas(c / (float)r.men, out r.local);
        }

        // 2) merge overlapping ones (screen space)
        for (int i = 0; i < rc; i++)
        {
            if (!_raws[i].valid) continue;
            for (int j = i + 1; j < rc; j++)
            {
                if (!_raws[j].valid) continue;
                Vector2 d = _raws[i].local - _raws[j].local;
                if (Mathf.Abs(d.x) < w && Mathf.Abs(d.y) < h)
                {
                    int a = FindRaw(i), b = FindRaw(j);
                    if (a != b) _raws[a].parent = b;
                }
            }
        }

        int gc = 0;
        for (int i = 0; i < rc; i++)
        {
            Raw r = _raws[i];
            if (!r.valid) continue;
            Raw root0 = _raws[FindRaw(i)];
            if (root0.group < 0)
            {
                root0.group = gc;
                if (gc >= _groups.Count) _groups.Add(new Grp());
                Grp ng = _groups[gc++];
                ng.units.Clear(); ng.sum = Vector2.zero; ng.men = 0.0;
            }
            Grp g = _groups[root0.group];
            g.units.AddRange(r.units);
            g.sum += r.local * (float)r.men;
            g.men += r.men;
        }

        // 3) group -> view (keep the view that already shows most of these regiments)
        for (int gi = 0; gi < gc; gi++)
        {
            Grp g = _groups[gi];
            CombatView best = null; int bestV = 0;
            for (int k = 0; k < g.units.Count; k++)
            {
                Regiment u = g.units[k];
                if (u.Military == null) continue;
                if (_uidView.TryGetValue(u.Uid, out CombatView v) && v != null && v.Claim != _stamp)
                {
                    if (v.Vote == 0) _voted.Add(v);
                    if (++v.Vote > bestV) { bestV = v.Vote; best = v; }
                }
            }
            for (int k = 0; k < _voted.Count; k++) _voted[k].Vote = 0;
            _voted.Clear();

            if (best == null) best = AcquireView(root);
            if (best == null) continue;
            best.Claim = _stamp;
            best.LastSeen = now;

            Vector2 local = g.sum / (float)g.men + new Vector2(0f, offsetY * s);
            bool ok = Fill(best, g, local, cameraMoved, s, pm);
            best.SetVisible(ok);
        }

        // 4) uid map + unclaimed views (grace, then release)
        _uidView.Clear();
        for (int gi = 0; gi < gc; gi++)
        {
            Grp g = _groups[gi];
            CombatView owner = null;
            for (int i = 0; i < _views.Count; i++) if (_views[i].Claim == _stamp && OwnsGroup(_views[i], g)) { owner = _views[i]; break; }
        }
        // the map is filled while claiming (see Fill) -> nothing else to do here

        for (int i = _views.Count - 1; i >= 0; i--)
        {
            CombatView v = _views[i];
            if (v.Claim == _stamp) continue;
            if (!show || now - v.LastSeen > endGrace) { ReleaseView(v); _views.RemoveAt(i); }
        }
    }

    private bool OwnsGroup(CombatView v, Grp g) => false;   // kept trivial: ownership is registered in Fill

    private bool Fill(CombatView v, Grp g, Vector2 local, bool snap, float scale, PlayerManager pm)
    {
        Nation anchorN = null, bigN = null;
        double bm = -1.0;
        for (int k = 0; k < g.units.Count; k++)
        {
            Regiment u = g.units[k];
            if (u.Military == null) continue;
            _uidView[u.Uid] = v;
            Nation n = u.Military.Nation;
            if (n.Id == v.Anchor) anchorN = n;
            if (u.Manpower > bm) { bm = u.Manpower; bigN = n; }
        }
        if (anchorN == null)
        {
            if (bigN == null) return false;
            anchorN = bigN; v.Anchor = bigN.Id;
        }

        double a = 0.0, b = 0.0, ma = -1.0, mb = -1.0;
        Nation na = null, nb = null;
        for (int k = 0; k < g.units.Count; k++)
        {
            Regiment u = g.units[k];
            if (u.Military == null) continue;
            Nation n = u.Military.Nation;
            if (n != anchorN && Hostile(anchorN, n)) { b += u.Manpower; if (u.Manpower > mb) { mb = u.Manpower; nb = n; } }
            else { a += u.Manpower; if (u.Manpower > ma) { ma = u.Manpower; na = n; } }
        }
        if (na == null || nb == null) return false;
        if (pm != null && !pm.IsArmyVisible(na) && !pm.IsArmyVisible(nb)) return false;

        float per = Mathf.Max(1, _army.menPerValue);
        v.Apply(na, nb, (float)(a / per), (float)(b / per), local, snap, scale);
        return true;
    }

    #endregion

    #region View pooling / root

    private CombatView AcquireView(RectTransform root)
    {
        CombatView v = PoolManager.Instance.Spawn(GetTemplate(), Vector3.zero, Quaternion.identity, root);
        if (v == null) return null;
        v.transform.localRotation = Quaternion.identity;
        v.transform.SetAsLastSibling();
        v.Build(_army.FontAsset, width, height);
        _views.Add(v);
        return v;
    }

    private void ReleaseView(CombatView v)
    {
        if (v == null) return;
        if (PoolManager.HasInstance) PoolManager.Instance.Despawn(v.gameObject);
        else Destroy(v.gameObject);
    }

    private CombatView GetTemplate()
    {
        if (_template != null) return _template;
        var go = new GameObject("CombatView (template)", typeof(RectTransform));
        go.SetActive(false);
        go.transform.SetParent(transform, false);
        _template = go.AddComponent<CombatView>();
        return _template;
    }

    /// <summary>Full-screen root created AFTER the army root, kept above it.</summary>
    private RectTransform GetRoot()
    {
        RectTransform armyRoot = _army != null ? _army.GetRoot() : null;
        if (armyRoot == null) return null;

        if (_root == null)
        {
            var go = new GameObject("CombatViews", typeof(RectTransform));
            _root = (RectTransform)go.transform;
            _root.SetParent(armyRoot.parent, false);
            _root.anchorMin = Vector2.zero; _root.anchorMax = Vector2.one;
            _root.pivot = new Vector2(0.5f, 0.5f);
            _root.offsetMin = _root.offsetMax = Vector2.zero;
            _root.localScale = Vector3.one;
            _root.SetAsLastSibling();
        }
        else if (_root.GetSiblingIndex() < armyRoot.GetSiblingIndex()) _root.SetAsLastSibling();
        return _root;
    }

    #endregion
}