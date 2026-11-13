using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class SiegeSettings
{
    [Header("Siege")]
    public bool enabled = true;
    [Min(0.2f)] public float checkInterval = 1.5f;
    [Tooltip("A surrounded group bigger than this is not a siege.")]
    [Min(1)] public int maxPocketProvinces = 12;
    [Tooltip("Regiments of the besieger that must stand on the ring.")]
    [Min(1)] public int minRingRegiments = 2;
    [Tooltip("Fraction of manpower a besieged regiment loses per game-second.")]
    [Min(0f)] public float attritionPerSecond = 0.0004f;

    [Header("Ally support")]
    public bool supportEnabled = true;
    [Tooltip("Base radius in tiles. Scaled by max(map width, height) / 1024 when scaleWithMapSize is on.")]
    [Min(10f)] public float supportRadiusTiles = 200f;
    public bool scaleWithMapSize = true;
    [Min(0.5f)] public float supportScanInterval = 3f;
    [Min(1)] public int maxSupportersPerBattle = 3;
    [Range(0f, 1f)] public float supportMinStrength = 0.4f;
    [Tooltip("Do not help a side whose HP is already this many times the enemy's.")]
    [Min(1f)] public float supportWinRatio = 1.4f;
    public bool supportOwnNation = true;
    [Tooltip("Player regiments only move on their own orders unless this is on.")]
    public bool supportPlayerArmies = false;
}

public sealed class Siege
{
    public int Id;
    public War War;
    public WarSide Side;                 // the BESIEGER
    public Nation Leader;
    public readonly HashSet<int> Pocket = new HashSet<int>();
    public readonly List<Province> Ring = new List<Province>();
    public float Since;
}

public partial class CombatManager
{
    [Header("Siege & ally support")]
    public SiegeSettings siege = new SiegeSettings();

    private readonly List<Siege> _sieges = new List<Siege>();
    private readonly HashSet<int> _sgVisited = new HashSet<int>();
    private readonly List<Province> _sgList = new List<Province>();
    private readonly List<War> _sgWars = new List<War>();
    private int _nextSiegeId = 1;
    private float _siegeAcc, _supportAcc;

    public IReadOnlyList<Siege> Sieges => _sieges;
    public event Action<Siege> OnSiegeStarted;
    public event Action<Siege> OnSiegeEnded;

    // ───────────── queries ─────────────
    public Siege GetSiegeAt(Province p)
    {
        if (p == null) return null;
        for (int i = 0; i < _sieges.Count; i++) if (_sieges[i].Pocket.Contains(p.Id)) return _sieges[i];
        return null;
    }

    /// <summary>The siege that traps this regiment (it stands inside a pocket and is on the other side).</summary>
    public Siege GetSiegeAgainst(Regiment r)
    {
        if (r == null || r.Military == null || r.CurrentProvince == null) return null;
        for (int i = 0; i < _sieges.Count; i++)
        {
            Siege s = _sieges[i];
            if (s.Pocket.Contains(r.CurrentProvince.Id) && s.War.SideOf(r.Military.Nation.Id) == War.Opposite(s.Side)) return s;
        }
        return null;
    }

    public bool IsBesieged(Regiment r) => GetSiegeAgainst(r) != null;

    /// <summary>The besieged side may not liberate its own pocket while the ring holds.</summary>
    internal bool SiegeBlocks(War w, Nation me, Province p)
    {
        for (int i = 0; i < _sieges.Count; i++)
        {
            Siege s = _sieges[i];
            if (s.War == w && s.Pocket.Contains(p.Id) && w.SideOf(me.Id) == War.Opposite(s.Side)) return true;
        }
        return false;
    }

    // ───────────── tick ─────────────
    private void SiegeTick(float dt)
    {
        if (!siege.enabled || !WarManager.HasInstance || map == null) return;
        if (_sieges.Count > 0 && siege.attritionPerSecond > 0f) Attrition(dt);

        _siegeAcc += dt;
        if (_siegeAcc < siege.checkInterval) return;
        _siegeAcc = 0f;

        for (int i = _sieges.Count - 1; i >= 0; i--)
            if (SiegeDone(_sieges[i])) EndSiege(_sieges[i]);

        _sgWars.Clear();
        _sgWars.AddRange(WarManager.Instance.Wars);
        for (int i = 0; i < _sgWars.Count; i++)
        {
            War w = _sgWars[i];
            if (w.Ended) continue;
            FindSieges(w, WarSide.Attacker);
            if (!w.Ended) FindSieges(w, WarSide.Defender);
        }
    }

    private bool SiegeDone(Siege s)
    {
        if (s.War.Ended) return true;
        for (int i = 0; i < s.Ring.Count; i++) if (!Controls(s.War, s.Side, s.Ring[i])) return true;   // ring broken
        foreach (int id in s.Pocket)
            if (s.War.Occupied.TryGetValue(id, out int occ) && s.War.SideOf(occ) == s.Side) return false;
        return true;                                                                                  // everything liberated
    }

    private void EndSiege(Siege s)
    {
        if (!_sieges.Remove(s)) return;
        OnSiegeEnded?.Invoke(s);
    }

    private void EndSiegesOf(War w)
    {
        for (int i = _sieges.Count - 1; i >= 0; i--) if (_sieges[i].War == w) EndSiege(_sieges[i]);
    }

    private void Attrition(float dt)
    {
        _tmpUnits.Clear();
        for (int i = 0; i < _sieges.Count; i++)
        {
            Siege s = _sieges[i];
            WarSide opp = War.Opposite(s.Side);
            foreach (int id in s.Pocket)
            {
                if (!_index.TryGetValue(id, out List<Regiment> L)) continue;
                for (int j = 0; j < L.Count; j++)
                {
                    Regiment u = L[j];
                    if (u.Military != null && s.War.SideOf(u.Military.Nation.Id) == opp && !_tmpUnits.Contains(u)) _tmpUnits.Add(u);
                }
            }
        }
        for (int i = 0; i < _tmpUnits.Count; i++)
            if (_tmpUnits[i].Military != null) _tmpUnits[i].TakeCasualties(_tmpUnits[i].Manpower * siege.attritionPerSecond * dt);
    }

    // ───────────── detection ─────────────
    /// <summary>Does side 's' hold this province? (owns or occupies it, or has a standing army, and no enemy fighter is in it)</summary>
    public bool Controls(War w, WarSide s, Province p)
    {
        WarSide opp = War.Opposite(s);
        bool mine = false;
        if (_index.TryGetValue(p.Id, out List<Regiment> L))
            for (int i = 0; i < L.Count; i++)
            {
                Regiment u = L[i];
                if (u.Military == null || u.CurrentProvince != p || u.Manpower <= 0.5) continue;
                WarSide us = w.SideOf(u.Military.Nation.Id);
                if (us == opp) return false;
                if (us == s && u.State != RegimentState.Moving) mine = true;
            }
        if (mine) return true;
        if (w.Occupied.TryGetValue(p.Id, out int occ)) return w.SideOf(occ) == s;
        return p.Owner != null && w.SideOf(p.Owner.Id) == s;
    }

    private bool PocketCandidate(War w, WarSide s, Province p)
    {
        if (p.Owner == null || w.SideOf(p.Owner.Id) != War.Opposite(s)) return false;
        if (w.Occupied.TryGetValue(p.Id, out int occ) && w.SideOf(occ) == s) return false;
        return !Controls(w, s, p);
    }

    private void FindSieges(War w, WarSide s)
    {
        List<int> enemyIds = s == WarSide.Attacker ? w.Defenders : w.Attackers;
        _sgVisited.Clear();
        for (int e = 0; e < enemyIds.Count && !w.Ended; e++)
        {
            Nation en = WorldManager.Instance.GetNation(enemyIds[e]);
            if (en == null) continue;
            for (int i = 0; i < en.Provinces.Count && !w.Ended; i++)
            {
                Province start = en.Provinces[i];
                if (_sgVisited.Contains(start.Id) || !PocketCandidate(w, s, start)) continue;

                _sgList.Clear();
                _sgList.Add(start);
                _sgVisited.Add(start.Id);
                for (int k = 0; k < _sgList.Count; k++)
                {
                    int[] nb = _sgList[k].Neighbors;
                    for (int j = 0; j < nb.Length; j++)
                    {
                        Province q = map.GetProvince(nb[j]);
                        if (q == null || _sgVisited.Contains(q.Id) || !PocketCandidate(w, s, q)) continue;
                        _sgVisited.Add(q.Id);
                        _sgList.Add(q);
                    }
                }
                if (_sgList.Count <= siege.maxPocketProvinces) TryStartSiege(w, s, _sgList);
            }
        }
    }

    private void TryStartSiege(War w, WarSide s, List<Province> pocket)
    {
        var sg = new Siege { Id = _nextSiegeId, War = w, Side = s, Since = Time.time };
        var seen = new HashSet<int>();
        for (int i = 0; i < pocket.Count; i++) sg.Pocket.Add(pocket[i].Id);

        for (int i = 0; i < pocket.Count; i++)
            foreach (int id in pocket[i].Neighbors)
            {
                if (sg.Pocket.Contains(id) || !seen.Add(id)) continue;
                Province q = map.GetProvince(id);
                if (q == null) continue;
                if (!Controls(w, s, q)) return;                 // a hole in the ring
                sg.Ring.Add(q);
            }
        if (sg.Ring.Count == 0) return;

        int regs = 0;
        for (int i = 0; i < sg.Ring.Count; i++)
        {
            if (!_index.TryGetValue(sg.Ring[i].Id, out List<Regiment> L)) continue;
            for (int j = 0; j < L.Count; j++)
            {
                Regiment u = L[j];
                if (u.Military == null || u.State == RegimentState.Moving || w.SideOf(u.Military.Nation.Id) != s) continue;
                regs++;
                if (sg.Leader == null) sg.Leader = u.Military.Nation;
            }
        }
        if (regs < siege.minRingRegiments || sg.Leader == null) return;

        _nextSiegeId++;
        _sieges.Add(sg);
        WarManager wm = WarManager.Instance;
        for (int i = 0; i < pocket.Count && !w.Ended; i++)
            if (wm.Occupy(w, sg.Leader, pocket[i])) PaintProvince(pocket[i], sg.Leader);

        // visuals
        ArmyManager army = ArmyManager.Instance;
        var pts = new List<Vector3>(sg.Ring.Count);
        for (int i = 0; i < sg.Ring.Count; i++) pts.Add(army.ProvinceWorldPos(sg.Ring[i]));
        TacticVisuals.Ring(pts, sg.Leader.Color, 12f);
        TacticVisuals.Label(army.ProvinceWorldPos(pocket[0]), "SIEGE!", sg.Leader.Color, 4f);

        OnSiegeStarted?.Invoke(sg);
    }

    // ───────────── ally support ─────────────
    private struct SupCand { public Regiment R; public float D; public bool SideA; }
    private readonly List<SupCand> _sup = new List<SupCand>();

    /// <summary>Support radius in tiles: base value, scaled up on bigger maps.</summary>
    public float SupportRadiusTiles
    {
        get
        {
            float r = siege.supportRadiusTiles;
            if (siege.scaleWithMapSize && map != null && map.IsReady) r *= Mathf.Max(1f, Mathf.Max(map.Width, map.Height) / 1024f);
            return r;
        }
    }

    private void SupportTick(float dt)
    {
        if (!siege.supportEnabled || _battles.Count == 0) return;
        _supportAcc += dt;
        if (_supportAcc < siege.supportScanInterval) return;
        _supportAcc = 0f;

        ArmyManager army = ArmyManager.Instance;
        float rt = SupportRadiusTiles * army.TileSize, r2 = rt * rt;
        IReadOnlyList<Military> mils = army.Militaries;

        for (int bi = 0; bi < _battles.Count; bi++)
        {
            Battle b = _battles[bi];
            if (b.Ended) continue;
            int heading = 0;
            _sup.Clear();

            for (int mi = 0; mi < mils.Count; mi++)
            {
                Military m = mils[mi];
                Nation n = m.Nation;
                bool a = n == b.LeadA || (Hostile(n, b.LeadB) && !Hostile(n, b.LeadA));
                bool d = n == b.LeadB || (Hostile(n, b.LeadA) && !Hostile(n, b.LeadB));
                if (a == d) continue;                                   // not a friend of either side

                bool allowed = (siege.supportOwnNation || n != (a ? b.LeadA : b.LeadB)) &&
                               (siege.supportPlayerArmies || !n.IsPlayer);
                for (int j = 0; j < m.Regiments.Count; j++)
                {
                    Regiment u = m.Regiments[j];
                    if (u.State == RegimentState.Moving && u.HasPath && u.Destination == b.Province) { heading++; continue; }
                    if (!allowed) continue;
                    if (u.State != RegimentState.Idle && u.State != RegimentState.Reinforcing) continue;
                    if (u.TacticLock || u.Battle != null || u.Manpower < 1.0 || u.StrengthPercent < siege.supportMinStrength) continue;
                    if (u.CurrentProvince == b.Province) continue;
                    float dd = (u.WorldPosition - b.WorldPos).sqrMagnitude;
                    if (dd > r2) continue;
                    _sup.Add(new SupCand { R = u, D = dd, SideA = a });
                }
            }
            if (_sup.Count == 0 || heading >= siege.maxSupportersPerBattle) continue;
            _sup.Sort((x, y) => x.D.CompareTo(y.D));

            bool labelled = false;
            for (int k = 0; k < _sup.Count && heading < siege.maxSupportersPerBattle; k++)
            {
                SupCand c = _sup[k];
                double friend = c.SideA ? b.HpA : b.HpB, foe = c.SideA ? b.HpB : b.HpA;
                if (friend > foe * siege.supportWinRatio) continue;     // they are winning already
                if (!army.MoveRegiment(c.R, b.Province)) continue;
                heading++;
                Color col = c.R.Military.Nation.Color;
                TacticVisuals.Arrow(c.R.WorldPosition, b.WorldPos, col, 8f);
                if (!labelled) { TacticVisuals.Label(c.R.WorldPosition, "SUPPORT", col); labelled = true; }
            }
        }
    }
}