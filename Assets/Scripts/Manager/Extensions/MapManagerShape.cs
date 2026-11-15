using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class ShapeSettings
{
    [Tooltip("Shape quality (0..1) below this = 'ugly'. 1 = perfect blob, worms / spirals / holes are ~0.2.")]
    [Range(0.2f, 0.9f)] public float uglyBelow = 0.55f;
    [Tooltip("Relaxation steps: more = rounder, slightly slower.")]
    [Range(1, 8)] public int lloydIterations = 4;
    [Tooltip("0 = every province in the group becomes equally big, 1 = each keeps its current area.")]
    [Range(0f, 1f)] public float keepAreaShare = 0.6f;
    [Tooltip("1 = merged province + direct neighbours are reshaped, 2 = neighbours of neighbours too.")]
    [Range(1, 3)] public int depth = 1;
    [Min(2)] public int maxProvincesPerReshape = 12;
    [Tooltip("Safety cap: reshaping never touches more tiles than this in one call.")]
    [Min(1000)] public int maxTilesPerReshape = 80000;
    [Tooltip("The reshape is only applied when the group score improves by at least this much.")]
    [Range(0f, 0.3f)] public float minImprovement = 0.02f;
    [Tooltip("Repeat the reshape while it keeps improving (max passes).")]
    [Range(1, 3)] public int passes = 2;
    [Tooltip("Elongation (long axis / short axis) tolerated before the score is penalised.")]
    [Range(1f, 4f)] public float elongationTolerance = 2f;
}

public struct ShapeInfo
{
    public int Area, Perimeter;
    public float Compactness, Elongation, Quality;   // Quality 0..1 (1 = perfect)
}

public partial class MapManager
{
    [Header("Province shape (beautify)")]
    public ShapeSettings shape = new ShapeSettings();

    /// <summary>Provinces whose tiles / borders / stats changed. Systems that cache province ids per unit should refresh.</summary>
    public event Action<IReadOnlyList<Province>> OnProvincesReshaped;
    public string LastReshapeError { get; private set; }

    private int[] _scrLocal;                                    // tile -> local index while reshaping, otherwise -1
    private readonly List<int> _scrStack = new List<int>(1024);
    private readonly List<Province> _setBuf = new List<Province>(16);
    private readonly Dictionary<int, int> _linkBuf = new Dictionary<int, int>();

    #region Measuring

    private struct Acc { public int Area, Perim; public double Sx, Sy, Sxx, Syy, Sxy; }

    private static ShapeInfo Finish(in Acc a, float tol)
    {
        var s = new ShapeInfo { Area = a.Area, Perimeter = a.Perim, Compactness = 1f, Elongation = 1f, Quality = 1f };
        if (a.Area < 6 || a.Perim <= 0) return s;

        double inv = 1.0 / a.Area, mx = a.Sx * inv, my = a.Sy * inv;
        double vx = a.Sxx * inv - mx * mx + 1.0 / 12.0, vy = a.Syy * inv - my * my + 1.0 / 12.0, vxy = a.Sxy * inv - mx * my;
        double h = (vx + vy) * 0.5, d = Math.Sqrt(Math.Max(0.0, (vx - vy) * (vx - vy) * 0.25 + vxy * vxy));
        s.Elongation = (float)Math.Sqrt((h + d) / Math.Max(1e-3, h - d));
        // isoperimetric ratio on a 4-connected grid: a square = 1, a blob ~0.88, a worm / spiral / holey shape << 0.5
        s.Compactness = Mathf.Clamp01(4f * Mathf.Sqrt(a.Area) / a.Perim);
        s.Quality = s.Compactness / (1f + Mathf.Max(0f, s.Elongation - tol) * 0.4f);
        return s;
    }

    public ShapeInfo MeasureShape(Province p)
    {
        var a = new Acc();
        if (p != null && !p.IsMerged && _tileProvince != null)
        {
            int id = p.Id;
            int[] t = p.Tiles;
            for (int i = 0; i < t.Length; i++)
            {
                int ti = t[i], x = ti % _w, y = ti / _w;
                a.Area++; a.Sx += x; a.Sy += y; a.Sxx += (double)x * x; a.Syy += (double)y * y; a.Sxy += (double)x * y;
                if (x == 0 || _tileProvince[ti - 1] != id) a.Perim++;
                if (x == _w - 1 || _tileProvince[ti + 1] != id) a.Perim++;
                if (y == 0 || _tileProvince[ti - _w] != id) a.Perim++;
                if (y == _h - 1 || _tileProvince[ti + _w] != id) a.Perim++;
            }
        }
        return Finish(a, shape.elongationTolerance);
    }

    public float ShapeQuality(Province p) => MeasureShape(p).Quality;
    public bool IsUgly(Province p) => ShapeQuality(p) < shape.uglyBelow;

    /// <summary>Area weighted average quality of all provinces of a nation.</summary>
    public float NationShapeQuality(Nation n)
    {
        if (n == null || n.IsEmpty) return 1f;
        double sum = 0, area = 0;
        for (int i = 0; i < n.Provinces.Count; i++)
        {
            ShapeInfo s = MeasureShape(n.Provinces[i]);
            sum += s.Quality * s.Area; area += s.Area;
        }
        return area > 0 ? (float)(sum / area) : 1f;
    }

    /// <summary>The worst shaped province of the nation (below 'below'), or null.</summary>
    public Province FindUglyProvince(Nation n, float below = -1f)
    {
        if (n == null || !IsReady) return null;
        if (below < 0f) below = shape.uglyBelow;
        Province worst = null;
        float wq = below;
        for (int i = 0; i < n.Provinces.Count; i++)
        {
            Province p = n.Provinces[i];
            if (p.TileCount < 6 || IsLocked(p)) continue;
            float q = MeasureShape(p).Quality;
            if (q < wq) { wq = q; worst = p; }
        }
        return worst;
    }

    private bool IsLocked(Province p)
    {
        if (!merge.blockWhileOccupied) return false;
        if (CombatManager.HasInstance && CombatManager.Instance.IsContested(p)) return true;
        if (WarManager.HasInstance)
        {
            var wars = WarManager.Instance.Wars;
            for (int w = 0; w < wars.Count; w++) if (wars[w].Occupied.ContainsKey(p.Id)) return true;
        }
        return false;
    }

    private int Nb(int t, int d)
    {
        switch (d)
        {
            case 0: return t % _w > 0 ? t - 1 : -1;
            case 1: return t % _w < _w - 1 ? t + 1 : -1;
            case 2: return t >= _w ? t - _w : -1;
            default: return t + _w < _w * _h ? t + _w : -1;
        }
    }

    #endregion

    #region Compact merge group (replaces "smallest neighbour first")

    /// <summary>
    /// Grows a merge group that stays round: candidates close to the group centre, touching the group in several places
    /// or completely enclosed by it (holes) win. result[0] = origin.
    /// </summary>
    public bool FindCompactMergeGroup(Province origin, int count, List<Province> result)
    {
        result.Clear();
        if (!IsReady || origin == null || origin.IsMerged || origin.Owner == null) return false;
        count = Mathf.Clamp(count, 2, Mathf.Max(2, merge.maxProvincesPerMerge));
        result.Add(origin);
        double sx = origin.Centroid.x * origin.TileCount, sy = origin.Centroid.y * origin.TileCount;
        int tiles = origin.TileCount;

        while (result.Count < count)
        {
            _linkBuf.Clear();
            for (int i = 0; i < result.Count; i++)
            {
                int[] nb = result[i].Neighbors;
                for (int j = 0; j < nb.Length; j++)
                {
                    Province q = GetProvince(nb[j]);
                    if (q == null || q.IsMerged || q.Owner != origin.Owner || result.Contains(q)) continue;
                    _linkBuf.TryGetValue(q.Id, out int c);
                    _linkBuf[q.Id] = c + 1;
                }
            }
            if (_linkBuf.Count == 0) break;

            Vector2 gc = new Vector2((float)(sx / tiles), (float)(sy / tiles));
            float unit = Mathf.Max(1f, Mathf.Sqrt(tiles));
            Province best = null;
            float bestScore = float.MaxValue;
            foreach (var kv in _linkBuf)
            {
                Province q = _provinces[kv.Key];
                bool enclosed = true;                                    // every neighbour already inside the group
                for (int j = 0; j < q.Neighbors.Length && enclosed; j++)
                    if (!result.Contains(_provinces[q.Neighbors[j]])) enclosed = false;

                float score = (q.Centroid - gc).magnitude / unit         // stay round: close to the centre
                              - 0.5f * (kv.Value - 1)                    // touches the group more than once
                              - (enclosed ? 2f : 0f)                     // fill holes first
                              + 0.1f * Mathf.Sqrt(q.TileCount) / unit;   // slight preference for small ones
                if (score < bestScore) { bestScore = score; best = q; }
            }
            if (best == null) break;
            result.Add(best);
            sx += best.Centroid.x * best.TileCount; sy += best.Centroid.y * best.TileCount; tiles += best.TileCount;
        }
        return result.Count >= 2;
    }

    #endregion

    #region Beautify (second layer after merging)

    /// <summary>Reshapes 'origin' and its same-nation neighbours (see ShapeSettings.depth). True if anything improved.</summary>
    public bool Beautify(Province origin)
    {
        LastReshapeError = null;
        if (!IsReady || origin == null || origin.IsMerged || origin.Owner == null)
        { LastReshapeError = "Bad province."; return false; }

        var set = _setBuf;
        set.Clear();
        set.Add(origin);
        int from = 0;
        for (int d = 0; d < shape.depth; d++)
        {
            int to = set.Count;
            for (int i = from; i < to; i++)
            {
                int[] nb = set[i].Neighbors;
                for (int j = 0; j < nb.Length; j++)
                {
                    if (set.Count >= shape.maxProvincesPerReshape) break;
                    Province q = GetProvince(nb[j]);
                    if (q == null || q.IsMerged || q.Owner != origin.Owner || set.Contains(q)) continue;
                    set.Add(q);
                }
            }
            from = to;
        }

        bool any = false;
        for (int pass = 0; pass < shape.passes; pass++)
        {
            if (!ReshapeSet(set)) break;
            any = true;
        }
        return any;
    }

    /// <summary>Reshapes the 'steps' ugliest provinces of a nation. Returns how many reshapes were applied.</summary>
    public int BeautifyNation(Nation n, int steps = 3)
    {
        int done = 0;
        for (int i = 0; i < steps; i++)
        {
            Province ugly = FindUglyProvince(n);
            if (ugly == null || !Beautify(ugly)) break;
            done++;
        }
        return done;
    }

    private float ScoreAssignment(int[] tiles, int[] assign, int[] ids, Acc[] acc)
    {
        int k = ids.Length;
        Array.Clear(acc, 0, k);
        for (int l = 0; l < tiles.Length; l++)
        {
            int t = tiles[l], a = assign[l], x = t % _w, y = t / _w, id = ids[a];
            acc[a].Area++; acc[a].Sx += x; acc[a].Sy += y;
            acc[a].Sxx += (double)x * x; acc[a].Syy += (double)y * y; acc[a].Sxy += (double)x * y;
            for (int d = 0; d < 4; d++)
            {
                int nt = Nb(t, d);
                int other = nt < 0 ? -2 : (_scrLocal[nt] >= 0 ? ids[assign[_scrLocal[nt]]] : _tileProvince[nt]);
                if (other != id) acc[a].Perim++;
            }
        }
        double ws = 0, area = 0;
        float mn = 1f;
        for (int i = 0; i < k; i++)
        {
            if (acc[i].Area == 0) return -1f;
            ShapeInfo s = Finish(acc[i], shape.elongationTolerance);
            ws += s.Quality * s.Area; area += s.Area;
            if (s.Quality < mn) mn = s.Quality;
        }
        return 0.5f * (float)(ws / area) + 0.5f * mn;     // average AND the worst one count
    }

    private bool ReshapeSet(List<Province> set)
    {
        int k = set.Count;
        if (k < 2) { LastReshapeError = "Need at least two provinces."; return false; }
        Nation owner = set[0].Owner;
        int total = 0;
        for (int i = 0; i < k; i++)
        {
            Province p = set[i];
            if (p.IsMerged || p.Owner != owner) { LastReshapeError = "Provinces must belong to one nation."; return false; }
            if (IsLocked(p)) { LastReshapeError = p.Name + " is occupied / contested."; return false; }
            total += p.TileCount;
        }
        if (total > shape.maxTilesPerReshape) { LastReshapeError = "Too many tiles."; return false; }

        int n = _w * _h;
        if (_scrLocal == null || _scrLocal.Length != n) { _scrLocal = new int[n]; Fill(_scrLocal, -1); }

        var tiles = new int[total];
        var oldIdx = new int[total];
        var newIdx = new int[total];
        var ids = new int[k];
        int pos = 0;
        for (int i = 0; i < k; i++)
        {
            ids[i] = set[i].Id;
            int[] pt = set[i].Tiles;
            for (int j = 0; j < pt.Length; j++)
            {
                tiles[pos] = pt[j]; oldIdx[pos] = i; newIdx[pos] = i;
                _scrLocal[pt[j]] = pos++;
            }
        }

        try
        {
            // ── connected landmasses of the union (islands are never mixed) ──
            var comp = new int[total];
            Fill(comp, -1);
            var comps = new List<List<int>>();
            for (int s = 0; s < total; s++)
            {
                if (comp[s] >= 0) continue;
                var list = new List<int>();
                int ci = comps.Count;
                comp[s] = ci; _scrStack.Clear(); _scrStack.Add(s);
                while (_scrStack.Count > 0)
                {
                    int l = _scrStack[_scrStack.Count - 1];
                    _scrStack.RemoveAt(_scrStack.Count - 1);
                    list.Add(l);
                    for (int d = 0; d < 4; d++)
                    {
                        int nt = Nb(tiles[l], d);
                        if (nt < 0) continue;
                        int ln = _scrLocal[nt];
                        if (ln >= 0 && comp[ln] < 0) { comp[ln] = ci; _scrStack.Add(ln); }
                    }
                }
                comps.Add(list);
            }

            var lab = new int[total];
            var seen = new bool[k];
            for (int c = 0; c < comps.Count; c++)
            {
                Array.Clear(seen, 0, k);
                int distinct = 0;
                foreach (int l in comps[c]) if (!seen[oldIdx[l]]) { seen[oldIdx[l]] = true; distinct++; }
                if (distinct < 2) continue;
                LloydComponent(comps[c], tiles, oldIdx, newIdx, k);
                FixFragments(comps[c], tiles, newIdx, lab, k);
            }

            int moved = 0;
            for (int l = 0; l < total; l++) if (oldIdx[l] != newIdx[l]) moved++;
            if (moved == 0) { LastReshapeError = "Nothing to change."; return false; }

            // ── only apply when the result really looks better ──
            var acc = new Acc[k];
            float before = ScoreAssignment(tiles, oldIdx, ids, acc);
            float after = ScoreAssignment(tiles, newIdx, ids, acc);
            if (after < 0f || after < before + shape.minImprovement) { LastReshapeError = "No improvement."; return false; }

            ApplyReshape(set, tiles, oldIdx, newIdx, owner);
            return true;
        }
        finally
        {
            for (int i = 0; i < total; i++) _scrLocal[tiles[i]] = -1;
        }
    }

    /// <summary>Weighted Lloyd relaxation: Euclidean Voronoi cells, weights nudge every cell towards its target area.</summary>
    private void LloydComponent(List<int> locals, int[] tiles, int[] oldIdx, int[] newIdx, int k)
    {
        int m = locals.Count;
        var cnt = new int[k];
        var sx = new double[k]; var sy = new double[k];
        for (int i = 0; i < m; i++)
        {
            int l = locals[i], o = oldIdx[l], t = tiles[l];
            cnt[o]++; sx[o] += t % _w; sy[o] += t / _w;
        }
        var P = new List<int>();
        for (int i = 0; i < k; i++) if (cnt[i] > 0) P.Add(i);
        int pc = P.Count;
        float equal = m / (float)pc;

        var px = new float[k]; var py = new float[k]; var w = new float[k]; var target = new float[k];
        var seed = new int[k]; var bd = new float[k]; var cc = new int[k];
        foreach (int p in P)
        {
            px[p] = (float)(sx[p] / cnt[p]); py[p] = (float)(sy[p] / cnt[p]);
            target[p] = Mathf.Lerp(equal, cnt[p], shape.keepAreaShare);
        }

        int iters = Mathf.Max(1, shape.lloydIterations);
        for (int it = 0; it < iters; it++)
        {
            // seed tile = tile of its own current region closest to the centroid (guarantees a non-empty cell)
            int[] cur = it == 0 ? oldIdx : newIdx;
            foreach (int p in P) { bd[p] = float.MaxValue; seed[p] = -1; }
            for (int i = 0; i < m; i++)
            {
                int l = locals[i], p = cur[l], t = tiles[l];
                float dx = t % _w - px[p], dy = t / _w - py[p], d = dx * dx + dy * dy;
                if (d < bd[p]) { bd[p] = d; seed[p] = l; }
            }

            for (int i = 0; i < m; i++)
            {
                int l = locals[i], t = tiles[l];
                float x = t % _w, y = t / _w, best = float.MaxValue;
                int bp = P[0];
                for (int j = 0; j < pc; j++)
                {
                    int p = P[j];
                    float dx = x - px[p], dy = y - py[p], d = dx * dx + dy * dy - w[p];
                    if (d < best) { best = d; bp = p; }
                }
                newIdx[l] = bp;
            }
            foreach (int p in P) if (seed[p] >= 0) newIdx[seed[p]] = p;

            if (it == iters - 1) break;

            foreach (int p in P) { cc[p] = 0; sx[p] = 0; sy[p] = 0; }
            for (int i = 0; i < m; i++)
            {
                int l = locals[i], p = newIdx[l], t = tiles[l];
                cc[p]++; sx[p] += t % _w; sy[p] += t / _w;
            }
            foreach (int p in P)
            {
                if (cc[p] == 0) continue;
                px[p] = (float)(sx[p] / cc[p]); py[p] = (float)(sy[p] / cc[p]);
                w[p] = Mathf.Clamp(w[p] + 0.3f * (target[p] - cc[p]), -equal, equal);   // dArea ~ pi * dWeight
            }
        }
    }

    /// <summary>Every province keeps only its biggest connected piece; smaller pieces join the neighbour they touch most.</summary>
    private void FixFragments(List<int> locals, int[] tiles, int[] assign, int[] lab, int k)
    {
        var best = new int[k]; var bestSize = new int[k]; var votes = new int[k];
        var frags = new List<List<int>>();
        var owners = new List<int>();

        for (int pass = 0; pass < 4; pass++)
        {
            for (int i = 0; i < locals.Count; i++) lab[locals[i]] = -1;
            frags.Clear(); owners.Clear();
            Fill(best, -1); Array.Clear(bestSize, 0, k);

            for (int i = 0; i < locals.Count; i++)
            {
                int s = locals[i];
                if (lab[s] >= 0) continue;
                var list = new List<int>();
                int id = frags.Count, p = assign[s];
                frags.Add(list); owners.Add(p);
                lab[s] = id; _scrStack.Clear(); _scrStack.Add(s);
                while (_scrStack.Count > 0)
                {
                    int l = _scrStack[_scrStack.Count - 1];
                    _scrStack.RemoveAt(_scrStack.Count - 1);
                    list.Add(l);
                    for (int d = 0; d < 4; d++)
                    {
                        int nt = Nb(tiles[l], d);
                        if (nt < 0) continue;
                        int ln = _scrLocal[nt];
                        if (ln < 0 || lab[ln] >= 0 || assign[ln] != p) continue;
                        lab[ln] = id; _scrStack.Add(ln);
                    }
                }
            }
            for (int f = 0; f < frags.Count; f++)
            {
                int p = owners[f];
                if (frags[f].Count > bestSize[p]) { bestSize[p] = frags[f].Count; best[p] = f; }
            }

            bool changed = false;
            for (int f = 0; f < frags.Count; f++)
            {
                int p = owners[f];
                if (best[p] == f) continue;
                Array.Clear(votes, 0, k);
                foreach (int l in frags[f])
                    for (int d = 0; d < 4; d++)
                    {
                        int nt = Nb(tiles[l], d);
                        if (nt < 0) continue;
                        int ln = _scrLocal[nt];
                        if (ln >= 0 && assign[ln] != p) votes[assign[ln]]++;
                    }
                int q = -1, qv = 0;
                for (int i = 0; i < k; i++) if (votes[i] > qv) { qv = votes[i]; q = i; }
                if (q < 0) continue;
                foreach (int l in frags[f]) assign[l] = q;
                changed = true;
            }
            if (!changed) break;
        }
    }

    private void ApplyReshape(List<Province> set, int[] tiles, int[] oldIdx, int[] newIdx, Nation owner)
    {
        int k = set.Count, total = tiles.Length;
        bool sumDef = merge.sumDefend;

        // old per-tile densities (snapshot BEFORE anything is overwritten)
        var dPop = new double[k]; var dTaxPop = new double[k]; var dEco = new double[k]; var dDef = new double[k];
        var dPg = new double[k]; var dInfra = new double[k]; var dProd = new double[k]; var dDev = new double[k];
        var oldTax = new float[k];
        for (int i = 0; i < k; i++)
        {
            Province p = set[i];
            double c = Math.Max(1, p.TileCount);
            dPop[i] = p.Population / c; dTaxPop[i] = p.Tax.Base * p.Population / c; dEco[i] = p.Economy.Base / c;
            dDef[i] = sumDef ? p.Defend.Base / c : p.Defend.Base;
            dPg[i] = p.PopGrowth.Base / c; dInfra[i] = p.Infrastructure.Base; dProd[i] = p.ProductionIncome.Base;
            dDev[i] = p.Devastation * p.Population / c;
            oldTax[i] = p.Tax.Base;
        }

        var changed = new bool[k];
        var cnt = new int[k];
        var pop = new double[k]; var taxPop = new double[k]; var eco = new double[k]; var def = new double[k];
        var pg = new double[k]; var infra = new double[k]; var prod = new double[k]; var dev = new double[k];
        for (int l = 0; l < total; l++)
        {
            int o = oldIdx[l], nw = newIdx[l];
            cnt[nw]++;
            if (o != nw) { changed[o] = true; changed[nw] = true; _tileProvince[tiles[l]] = set[nw].Id; }
            pop[nw] += dPop[o]; taxPop[nw] += dTaxPop[o]; eco[nw] += dEco[o]; def[nw] += dDef[o];
            pg[nw] += dPg[o]; infra[nw] += dInfra[o]; prod[nw] += dProd[o]; dev[nw] += dDev[o];
        }

        var newTiles = new int[k][];
        var fill = new int[k];
        for (int i = 0; i < k; i++) newTiles[i] = new int[cnt[i]];
        for (int l = 0; l < total; l++) { int nw = newIdx[l]; newTiles[nw][fill[nw]++] = tiles[l]; }

        var changedList = new List<Province>(k);
        for (int i = 0; i < k; i++)
        {
            if (!changed[i]) continue;
            Province p = set[i];
            MarkProvinceDirty(p);                                    // old bounds (repainted once next frame)

            p.Tiles = newTiles[i];
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            double sx = 0, sy = 0;
            for (int j = 0; j < p.Tiles.Length; j++)
            {
                int x = p.Tiles[j] % _w, y = p.Tiles[j] / _w;
                sx += x; sy += y;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
            p.Min = new Vector2Int(minX, minY); p.Max = new Vector2Int(maxX, maxY);
            p.Centroid = new Vector2((float)(sx / p.Tiles.Length), (float)(sy / p.Tiles.Length));

            double c = p.Tiles.Length;
            p.Population = (float)pop[i];
            p.Tax.Base = pop[i] > 0 ? (float)(taxPop[i] / pop[i]) : oldTax[i];
            p.Economy.Base = (float)eco[i];
            p.Defend.Base = sumDef ? (float)def[i] : (float)(def[i] / c);
            p.PopGrowth.Base = (float)pg[i];
            p.Infrastructure.Base = (float)(infra[i] / c);
            p.ProductionIncome.Base = (float)(prod[i] / c);
            p.Devastation = pop[i] > 0 ? (float)(dev[i] / pop[i]) : 0f;

            MarkProvinceDirty(p);                                    // new bounds
            changedList.Add(p);
        }

        RebuildLinks(set);
        MarkMarkerDirty(owner.Id);
        OnProvincesReshaped?.Invoke(changedList);
    }

    /// <summary>Local rebuild of land neighbours, coast seas and sea->province links around the reshaped set.</summary>
    private void RebuildLinks(List<Province> set)
    {
        int k = set.Count;
        var setIds = new HashSet<int>();
        foreach (Province p in set) setIds.Add(p.Id);

        var outside = new HashSet<int>();
        var seas = new HashSet<int>();
        foreach (Province p in set)
        {
            foreach (int id in p.Neighbors) if (!setIds.Contains(id)) outside.Add(id);
            foreach (int s in _coastSeas[p.Id]) seas.Add(s);
        }

        var hs = new HashSet<int>();
        var cs = new HashSet<int>();
        foreach (Province p in set)
        {
            hs.Clear(); cs.Clear();
            int lastP = -3, lastS = -3;
            int[] t = p.Tiles;
            for (int i = 0; i < t.Length; i++)
                for (int d = 0; d < 4; d++)
                {
                    int nt = Nb(t[i], d);
                    if (nt < 0) continue;
                    int q = _tileProvince[nt];
                    if (q >= 0) { if (q != p.Id && q != lastP) { hs.Add(q); lastP = q; } }
                    else { int s = _tileSea[nt]; if (s >= 0 && s != lastS) { cs.Add(s); lastS = s; } }
                }
            p.Neighbors = ToArray(hs);
            _coastSeas[p.Id] = ToArray(cs);
        }

        foreach (Province p in set)
        {
            foreach (int id in p.Neighbors) if (!setIds.Contains(id)) outside.Add(id);
            foreach (int s in _coastSeas[p.Id]) seas.Add(s);
        }

        foreach (int id in outside)
        {
            Province x = _provinces[id];
            var l = new List<int>(x.Neighbors.Length + 2);
            foreach (int m in x.Neighbors) if (!setIds.Contains(m)) l.Add(m);
            foreach (Province p in set) if (Array.IndexOf(p.Neighbors, id) >= 0) l.Add(p.Id);
            x.Neighbors = l.ToArray();
        }
        foreach (int sid in seas)
        {
            SeaRegion s = _seas[sid];
            var l = new List<int>(s.LandNeighbors.Length + 2);
            foreach (int m in s.LandNeighbors) if (!setIds.Contains(m)) l.Add(m);
            foreach (Province p in set) if (Array.IndexOf(_coastSeas[p.Id], sid) >= 0) l.Add(p.Id);
            s.LandNeighbors = l.ToArray();
        }
    }

    private static int[] ToArray(HashSet<int> h) { var a = new int[h.Count]; h.CopyTo(a); return a; }

    #endregion

    #region Debug

    [ContextMenu("Shape: beautify a random ugly province")]
    private void MenuBeautify()
    {
        if (!IsReady) return;
        for (int t = 0; t < 64; t++)
        {
            Province p = _provinces[RNG.GetInt(0, _provinces.Count)];
            if (p.IsMerged || p.Owner == null || !IsUgly(p)) continue;
            float q0 = ShapeQuality(p);
            bool ok = Beautify(p);
            Debug.Log(ok ? $"[Map] {p.Name}: shape {q0:F2} -> {ShapeQuality(p):F2}" : "[Map] Beautify: " + LastReshapeError);
            return;
        }
        Debug.Log("[Map] No ugly province found.");
    }

    #endregion
}