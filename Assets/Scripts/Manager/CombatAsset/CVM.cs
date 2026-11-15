using System;
using System.Collections.Generic;
using UnityEngine;

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
    [Min(100f)] public float width = 300f;
    [Min(20f)] public float height = 44f;
    public float offsetY = 0f;
    [Range(0.3f, 1f)] public float minScale = 0.7f;
    [Range(1f, 2f)] public float maxScale = 1.2f;


    [Header("Behaviour")]
    [Min(0f)] public float endGrace = 0.75f;
    [Min(1)] public int maxViews = 64;
    [Min(0.01f)] public float rebuildInterval = 0.05f;

    #region Data

    private sealed class Raw { public List<Regiment> units; public Vector3 world; public Vector2 local; public double men; public int parent, group; public bool valid; }
    private sealed class Grp { public readonly List<Regiment> units = new List<Regiment>(16); public Vector3 wsum; public double men; }

    private readonly List<Raw> _raws = new List<Raw>();
    private readonly List<Grp> _groups = new List<Grp>();
    // ---- Data (replace Raw / Grp / _raws / _groups) ----
    private readonly List<CombatView> _views = new List<CombatView>();
    private readonly List<CombatView> _voted = new List<CombatView>();
    private readonly Dictionary<int, CombatView> _uidView = new Dictionary<int, CombatView>();

    private List<List<Regiment>> _src;
    private int _srcCount, _stamp;
    private bool _dirty;
    private float _nextRebuild;
    private ArmyManager _army;
    private RectTransform _root;
    private CombatView _template;

    #endregion

    #region API (called by CombatManager)

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

        if (_dirty || Time.unscaledTime >= _nextRebuild)
        {
            _dirty = false;
            _nextRebuild = Time.unscaledTime + rebuildInterval;
            Rebuild();
        }

        // every frame: smooth in WORLD space, then project with the CURRENT camera -> no trailing while zooming
        float dt = Time.unscaledDeltaTime;
        float s = Mathf.Clamp(_army.CardScale, minScale, maxScale);
        for (int i = 0; i < _views.Count; i++)
        {
            CombatView v = _views[i];
            if (!v.Shown) continue;
            v.Tick(dt);
            if (!_army.TryWorldToCanvas(v.World, out Vector2 local)) continue;
            v.SetScale(s);
            v.SetCanvasPos(local + new Vector2(0f, offsetY * s));
        }
    }

    private static bool Hostile(Nation a, Nation b) =>
        a != null && b != null && a != b && WarManager.HasInstance && WarManager.Instance.IsAtWar(a, b);

    private void Rebuild()
    {
        RectTransform root = GetRoot();
        if (root == null) return;
        _stamp++;
        float now = Time.unscaledTime;

        bool show = _army.MarkersVisible;
        PlayerManager pm = PlayerManager.HasInstance ? PlayerManager.Instance : null;
        int rc = show && _src != null ? Mathf.Min(_srcCount, maxViews) : 0;

        // one battle = one view
        for (int i = 0; i < rc; i++)
        {
            List<Regiment> units = _src[i];

            double men = 0.0;
            Vector3 c = Vector3.zero;
            for (int k = 0; k < units.Count; k++)
            {
                Regiment u = units[k];
                if (u.Military == null || u.Manpower <= 0.5) continue;
                c += u.WorldPosition * (float)u.Manpower;
                men += u.Manpower;
            }
            if (men <= 0.0) continue;
            Vector3 world = c / (float)men;

            // reuse the view that already shows most of these regiments
            CombatView best = null; int bestV = 0;
            for (int k = 0; k < units.Count; k++)
            {
                Regiment u = units[k];
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
            best.SetVisible(Fill(best, units, world, pm));
        }

        // views nobody claimed: grace, then release
        for (int i = _views.Count - 1; i >= 0; i--)
        {
            CombatView v = _views[i];
            if (v.Claim == _stamp) continue;
            if (!show || now - v.LastSeen > endGrace)
            {
                ReleaseView(v);
                _views.RemoveAt(i);
            }
        }
    }

    private bool Fill(CombatView v, List<Regiment> units, Vector3 world, PlayerManager pm)
    {
        Nation anchorN = null, bigN = null;
        double bm = -1.0;
        for (int k = 0; k < units.Count; k++)
        {
            Regiment u = units[k];
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
        for (int k = 0; k < units.Count; k++)
        {
            Regiment u = units[k];
            if (u.Military == null) continue;
            Nation n = u.Military.Nation;
            if (n != anchorN && Hostile(anchorN, n)) { b += u.Manpower; if (u.Manpower > mb) { mb = u.Manpower; nb = n; } }
            else { a += u.Manpower; if (u.Manpower > ma) { ma = u.Manpower; na = n; } }
        }
        if (na == null || nb == null) return false;
        if (pm != null && !pm.IsArmyVisible(na) && !pm.IsArmyVisible(nb)) return false;

        v.Apply(na, nb, (float)a, (float)b, world);
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