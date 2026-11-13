using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>World-space tactic overlays: arrows, rings, floating labels. Sized from the camera view, so readable at any zoom.</summary>
public class TacticVisuals : MonoBehaviour
{
    private static TacticVisuals _i;
    private static bool _quit;
    private static Material _mat;
    public static bool Enabled = true;

    private static TacticVisuals Instance
    {
        get
        {
            if (_i == null && !_quit) _i = new GameObject("TacticVisuals").AddComponent<TacticVisuals>();
            return _i;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() { _i = null; _quit = false; _mat = null; }
    private void OnApplicationQuit() => _quit = true;

    private static float ViewHeight(Vector3 at)
    {
        Camera c = Camera.main;
        if (c == null) return 100f;
        if (c.orthographic) return c.orthographicSize * 2f;
        float d = Mathf.Abs(c.transform.InverseTransformPoint(at).z);
        return 2f * d * Mathf.Tan(c.fieldOfView * 0.5f * Mathf.Deg2Rad);
    }

    public static void Arrow(Vector3 from, Vector3 to, Color col, float life = 8f)
    {
        var pts = new List<Vector3> { from, to };
        Line(pts, col, life, false, true);
    }

    public static void Ring(List<Vector3> pts, Color col, float life = 10f)
    {
        if (pts == null || pts.Count < 3) return;
        Vector3 c = Vector3.zero;
        for (int i = 0; i < pts.Count; i++) c += pts[i];
        c /= pts.Count;
        var sorted = new List<Vector3>(pts);
        sorted.Sort((a, b) => Mathf.Atan2(a.y - c.y, a.x - c.x).CompareTo(Mathf.Atan2(b.y - c.y, b.x - c.x)));
        Line(sorted, col, life, true, false);
    }

    public static void Line(IList<Vector3> pts, Color col, float life, bool loop, bool arrowHead)
    {
        if (!Enabled || _quit || pts == null || pts.Count < 2) return;
        TacticVisuals host = Instance;
        if (host == null) return;

        float vh = ViewHeight(pts[0]);
        var list = new List<Vector3>(pts);
        if (arrowHead)
        {
            Vector3 to = list[list.Count - 1], prev = list[list.Count - 2];
            Vector3 dir = (to - prev).normalized;
            Vector3 perp = new Vector3(-dir.y, dir.x, 0f);
            float h = vh * 0.03f;
            list.Add(to - dir * h + perp * h * 0.5f);
            list.Add(to);
            list.Add(to - dir * h - perp * h * 0.5f);
        }

        var go = new GameObject("Tactic Line");
        go.transform.SetParent(host.transform, false);
        var lr = go.AddComponent<LineRenderer>();
        if (_mat == null) _mat = new Material(Shader.Find("Sprites/Default"));
        lr.sharedMaterial = _mat;
        lr.useWorldSpace = true;
        lr.loop = loop;
        lr.positionCount = list.Count;
        for (int i = 0; i < list.Count; i++) lr.SetPosition(i, list[i]);
        lr.widthMultiplier = vh * 0.005f;
        lr.numCapVertices = 4;
        lr.numCornerVertices = 3;
        lr.sortingOrder = 20;
        lr.startColor = lr.endColor = col;
        go.AddComponent<TacticFade>().Init(lr, null, col, life, 0f);
    }

    public static void Label(Vector3 pos, string text, Color col, float life = 3f)
    {
        if (!Enabled || _quit) return;
        TacticVisuals host = Instance;
        if (host == null) return;

        var go = new GameObject("Tactic Label");
        go.transform.SetParent(host.transform, false);
        go.transform.position = pos;
        var t = go.AddComponent<TextMeshPro>();
        t.text = text;
        t.alignment = TextAlignmentOptions.Center;
        t.fontStyle = FontStyles.Bold;
        t.overflowMode = TextOverflowModes.Overflow;
        t.color = col;
        t.outlineWidth = 0.25f;
        t.outlineColor = new Color32(0, 0, 0, 255);
        float vh = ViewHeight(pos);
        t.autoSizeTextContainer = true;
        t.sortingOrder = 21;
        go.AddComponent<TacticFade>().Init(null, t, col, life, vh * 0.03f);
    }
}

internal sealed class TacticFade : MonoBehaviour
{
    private LineRenderer _lr;
    private TextMeshPro _tx;
    private Color _c;
    private float _life, _t, _rise;

    public void Init(LineRenderer lr, TextMeshPro tx, Color c, float life, float rise)
    { _lr = lr; _tx = tx; _c = c; _life = Mathf.Max(0.1f, life); _rise = rise; }

    private void Update()
    {
        _t += Time.unscaledDeltaTime;
        float k = _t / _life;
        if (k >= 1f) { Destroy(gameObject); return; }
        var col = new Color(_c.r, _c.g, _c.b, 1f - k * k);
        if (_lr) { _lr.startColor = col; _lr.endColor = col; }
        if (_tx) { _tx.color = col; transform.position += Vector3.up * (_rise * Time.unscaledDeltaTime); }
    }
}