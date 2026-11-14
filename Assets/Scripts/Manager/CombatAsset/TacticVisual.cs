using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// World-space tactic overlays (pooled, capped, sized from the camera view):
/// Arrow, CurvedArrow, Pincer, Ring, Circle (+pulse), Cross, Diamond, Bracket, Line, Label, Announce.
/// </summary>
public class TacticVisuals : MonoBehaviour
{
    private static TacticVisuals _i;
    private static bool _quit;
    private static Material _mat;
    private static readonly Stack<TacticFade> _linePool = new Stack<TacticFade>();
    private static readonly Stack<TacticFade> _textPool = new Stack<TacticFade>();
    private static int _active;

    public static bool Enabled = true;
    /// <summary>Hard cap of visuals alive at once (extra ones are dropped, never queued).</summary>
    public static int MaxActive = 48;

    private static TacticVisuals Instance
    {
        get
        {
            if (_i == null && !_quit) _i = new GameObject("TacticVisuals").AddComponent<TacticVisuals>();
            return _i;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() { _i = null; _quit = false; _mat = null; _linePool.Clear(); _textPool.Clear(); _active = 0; }
    private void OnApplicationQuit() => _quit = true;

    private static float ViewHeight(Vector3 at)
    {
        Camera c = Camera.main;
        if (c == null) return 100f;
        if (c.orthographic) return c.orthographicSize * 2f;
        float d = Mathf.Abs(c.transform.InverseTransformPoint(at).z);
        return 2f * d * Mathf.Tan(c.fieldOfView * 0.5f * Mathf.Deg2Rad);
    }

    // ── pool ──
    private static TacticFade Take(bool text)
    {
        Stack<TacticFade> pool = text ? _textPool : _linePool;
        TacticFade f = null;
        while (pool.Count > 0) { f = pool.Pop(); if (f != null) break; f = null; }
        if (f == null)
        {
            var go = new GameObject(text ? "Tactic Label" : "Tactic Line");
            go.transform.SetParent(_i.transform, false);
            f = go.AddComponent<TacticFade>();
            f.IsText = text;
            if (text)
            {
                var t = go.AddComponent<TextMeshPro>();
                t.alignment = TextAlignmentOptions.Center;
                t.fontStyle = FontStyles.Bold;
                t.overflowMode = TextOverflowModes.Overflow;
                t.outlineWidth = 0.25f;
                t.outlineColor = new Color32(0, 0, 0, 255);
                t.autoSizeTextContainer = true;
                t.sortingOrder = 21;
                f.Tx = t;
            }
            else
            {
                var lr = go.AddComponent<LineRenderer>();
                if (_mat == null) _mat = new Material(Shader.Find("Sprites/Default"));
                lr.sharedMaterial = _mat;
                lr.numCapVertices = 4;
                lr.numCornerVertices = 3;
                lr.sortingOrder = 20;
                f.Lr = lr;
            }
        }
        f.gameObject.SetActive(true);
        _active++;
        return f;
    }

    internal static void Release(TacticFade f)
    {
        f.gameObject.SetActive(false);
        (f.IsText ? _textPool : _linePool).Push(f);
        _active--;
    }

    private static bool Ready() => Enabled && !_quit && Instance != null && _active < MaxActive;

    private static void Emit(IList<Vector3> pts, Color col, float life, bool loop, bool world, Vector3 origin,
                             float width, float grow)
    {
        if (pts == null || pts.Count < 2 || !Ready()) return;
        TacticFade f = Take(false);
        LineRenderer lr = f.Lr;
        lr.useWorldSpace = world;
        lr.loop = loop;
        lr.positionCount = pts.Count;
        for (int i = 0; i < pts.Count; i++) lr.SetPosition(i, pts[i]);
        lr.widthMultiplier = ViewHeight(origin) * 0.01f * width;
        f.transform.position = world ? Vector3.zero : origin;
        f.transform.localScale = Vector3.one;
        f.Begin(col, life, 0f, grow);
    }

    private static void AddHead(List<Vector3> list, Vector3 prev, Vector3 to, float vh)
    {
        Vector3 dir = (to - prev).normalized;
        if (dir.sqrMagnitude < 0.0001f) return;
        Vector3 perp = new Vector3(-dir.y, dir.x, 0f);
        float h = vh * 0.03f;
        list.Add(to - dir * h + perp * h * 0.5f);
        list.Add(to);
        list.Add(to - dir * h - perp * h * 0.5f);
    }

    // ── shapes ──
    public static void Arrow(Vector3 from, Vector3 to, Color col, float life = 8f, float width = 1f)
    {
        var l = new List<Vector3>(5) { from, to };
        AddHead(l, from, to, ViewHeight(from));
        Emit(l, col, life, false, true, from, width, 0f);
    }

    /// <summary>Arrow along a bezier curve. bend > 0 curves left, < 0 right (relative to the length).</summary>
    public static void CurvedArrow(Vector3 from, Vector3 to, Color col, float life = 8f, float bend = 0.3f, float width = 1f)
    {
        Vector3 d = to - from;
        Vector3 ctrl = (from + to) * 0.5f + new Vector3(-d.y, d.x, 0f) * bend;
        var l = new List<Vector3>(20);
        const int N = 14;
        for (int i = 0; i <= N; i++)
        {
            float t = i / (float)N, u = 1f - t;
            l.Add(u * u * from + 2f * u * t * ctrl + t * t * to);
        }
        AddHead(l, l[N - 1], to, ViewHeight(from));
        Emit(l, col, life, false, true, from, width, 0f);
    }

    /// <summary>Two curved arrows from opposite sides that meet at the target.</summary>
    public static void Pincer(Vector3 a, Vector3 b, Vector3 target, Color col, float life = 8f)
    {
        CurvedArrow(a, target, col, life, 0.3f, 1.2f);
        CurvedArrow(b, target, col, life, -0.3f, 1.2f);
    }

    public static void Ring(List<Vector3> pts, Color col, float life = 10f)
    {
        if (pts == null || pts.Count < 3) return;
        Vector3 c = Vector3.zero;
        for (int i = 0; i < pts.Count; i++) c += pts[i];
        c /= pts.Count;
        var sorted = new List<Vector3>(pts);
        sorted.Sort((a, b) => Mathf.Atan2(a.y - c.y, a.x - c.x).CompareTo(Mathf.Atan2(b.y - c.y, b.x - c.x)));
        Emit(sorted, col, life, true, true, c, 1f, 0f);
    }

    /// <summary>Circle around a point (radius in world units). pulse = it grows while it fades.</summary>
    public static void Circle(Vector3 center, float radius, Color col, float life = 6f, bool pulse = false)
    {
        const int N = 36;
        var l = new List<Vector3>(N);
        for (int i = 0; i < N; i++)
        {
            float a = i * Mathf.PI * 2f / N;
            l.Add(new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * radius);
        }
        Emit(l, col, life, true, false, center, pulse ? 0.8f : 1f, pulse ? 1.2f : 0f);
    }

    public static void Cross(Vector3 c, float size, Color col, float life = 6f)
    {
        var l = new List<Vector3>(5)
        {
            new Vector3(-size, -size), new Vector3(size, size), c - c, new Vector3(-size, size), new Vector3(size, -size)
        };
        Emit(l, col, life, false, false, c, 1.1f, 0f);
    }

    public static void Diamond(Vector3 c, float size, Color col, float life = 6f)
    {
        var l = new List<Vector3>(4)
        { new Vector3(0, size), new Vector3(size, 0), new Vector3(0, -size), new Vector3(-size, 0) };
        Emit(l, col, life, true, false, c, 1f, 0f);
    }

    /// <summary>A line with short ticks at both ends (a defended front).</summary>
    public static void Bracket(Vector3 a, Vector3 b, Color col, float life = 8f)
    {
        Vector3 d = (b - a).normalized;
        Vector3 p = new Vector3(-d.y, d.x, 0f) * ViewHeight(a) * 0.02f;
        var l = new List<Vector3>(4) { a + p, a, b, b + p };
        Emit(l, col, life, false, true, a, 1.2f, 0f);
    }

    public static void Line(IList<Vector3> pts, Color col, float life, bool loop, bool arrowHead)
    {
        if (pts == null || pts.Count < 2) return;
        var l = new List<Vector3>(pts);
        if (arrowHead) AddHead(l, l[l.Count - 2], l[l.Count - 1], ViewHeight(l[0]));
        Emit(l, col, life, loop, true, l[0], 1f, 0f);
    }

    public static void Label(Vector3 pos, string text, Color col, float life = 3f)
    {
        if (!Ready()) return;
        TacticFade f = Take(true);
        float vh = ViewHeight(pos);
        f.transform.position = pos;
        f.transform.localScale = Vector3.one * vh * 0.01f;
        f.Tx.text = text;
        f.Begin(col, life, vh * 0.03f, 0f);
    }

    /// <summary>Tactic name label + pulse ring (the AI uses it to show which tactic it picked).</summary>
    public static void Announce(Vector3 pos, string text, Color col, float life = 4f)
    {
        Label(pos, text, col, life);
        Circle(pos, ViewHeight(pos) * 0.04f, col, life, true);
    }
}

internal sealed class TacticFade : MonoBehaviour
{
    public bool IsText;
    public LineRenderer Lr;
    public TextMeshPro Tx;
    private Color _c;
    private float _life, _t, _rise, _grow;
    private Vector3 _scale;
    private bool _on;

    public void Begin(Color c, float life, float rise, float grow)
    {
        _c = c; _life = Mathf.Max(0.1f, life); _t = 0f; _rise = rise; _grow = grow;
        _scale = transform.localScale; _on = true;
        Apply(1f);
    }

    private void Apply(float a)
    {
        var col = new Color(_c.r, _c.g, _c.b, a);
        if (Lr) { Lr.startColor = col; Lr.endColor = col; }
        if (Tx) Tx.color = col;
    }

    private void Update()
    {
        if (!_on) return;
        _t += Time.unscaledDeltaTime;
        float k = _t / _life;
        if (k >= 1f) { _on = false; TacticVisuals.Release(this); return; }
        Apply(1f - k * k);
        if (_rise > 0f) transform.position += Vector3.up * (_rise * Time.unscaledDeltaTime);
        if (_grow > 0f) transform.localScale = _scale * (1f + _grow * k);
    }
}