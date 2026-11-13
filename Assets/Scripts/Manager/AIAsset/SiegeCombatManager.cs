using System.Collections.Generic;
using UnityEngine;

public partial class AIManager
{
    private readonly HashSet<int> _threatSet = new HashSet<int>();
    private readonly HashSet<int> _avoid = new HashSet<int>();
    private readonly List<Province> _sgCands = new List<Province>();
    private readonly List<Province> _slots = new List<Province>();
    private readonly List<Province> _pathTmp = new List<Province>();
    private readonly List<Vector3> _pts = new List<Vector3>();

    private void RunTactics(AIState st, Military m, bool passive)
    {
        if (!settings.useTactics) return;
        TacticVisuals.Enabled = settings.showTacticVisuals;

        // units holding a siege position stay locked
        for (int i = st.SiegeUnits.Count - 1; i >= 0; i--)
        {
            Regiment r = st.SiegeUnits[i];
            if (r.Military == null || r.State == RegimentState.Retreating) { if (r.Military != null) r.TacticLock = false; st.SiegeUnits.RemoveAt(i); continue; }
            r.TacticLock = true;
            _used.Add(r);
        }

        BuildThreatSet();
        Breakout(st, m);
        PreventSiege(st, m);

        if (st.SiegeTarget != null) ContinueSiege(st);
        else if (!passive && _clock >= st.NextTactic)
        {
            st.NextTactic = _clock + settings.tacticInterval;
            if (RNG.GetFloat(0f, 1f) < settings.flankSiegePreference) StartSiege(st);
        }
    }

    private void Announce(AIState st, string text, Province at)
    {
        if (!settings.showTacticVisuals || at == null || _clock < st.NextAnnounce) return;
        st.NextAnnounce = _clock + 1.5f;
        TacticVisuals.Label(Army.ProvinceWorldPos(at), text, st.Nation.Color);
    }

    private void BuildThreatSet()
    {
        _threatSet.Clear();
        for (int e = 0; e < _enemies.Count; e++)
        {
            Military em = Army.GetMilitary(_enemies[e]);
            if (em == null) continue;
            for (int i = 0; i < em.Regiments.Count; i++)
            {
                Regiment r = em.Regiments[i];
                if (r.State == RegimentState.Retreating || r.Manpower <= 0.5) continue;
                if (r.CurrentProvince != null) _threatSet.Add(r.CurrentProvince.Id);
                if (r.HasPath && r.Destination != null) _threatSet.Add(r.Destination.Id);
            }
        }
    }

    /// <summary>Does our side hold this province (owned / occupied / standing army) with no enemy present?</summary>
    private bool Held(Nation n, War w, WarSide mine, Province q)
    {
        if (EnemyMen(n, q) > 0.0) return false;
        if (w.Occupied.TryGetValue(q.Id, out int occ)) return w.SideOf(occ) == mine;
        if (q.Owner != null && w.SideOf(q.Owner.Id) == mine) return true;
        List<Regiment> l = _regsAt[q.Id];
        if (l != null)
            for (int i = 0; i < l.Count; i++)
                if (l[i].State != RegimentState.Moving && w.SideOf(l[i].Military.Nation.Id) == mine) return true;
        return false;
    }

    // ───────────── BREAKOUT: besieged armies attack the weakest point of the ring ─────────────
    private void Breakout(AIState st, Military m)
    {
        CombatManager cm = CombatManager.Instance;
        if (cm.Sieges.Count == 0) return;
        Nation n = st.Nation;

        for (int i = 0; i < m.Regiments.Count; i++)
        {
            Regiment r = m.Regiments[i];
            if (!Movable(r) || _used.Contains(r)) continue;
            Siege s = cm.GetSiegeAgainst(r);
            if (s == null) continue;

            Province target = null;
            double best = double.MaxValue;
            for (int k = 0; k < s.Ring.Count; k++)
            {
                Province q = s.Ring[k];
                double v = EnemyMen(n, q) * (q.Owner == n ? 0.8 : 1.0);
                if (v < best) { best = v; target = q; }
            }
            if (target == null || !Order(r, target)) continue;
            if (st.BreakoutSiege != s.Id) { st.BreakoutSiege = s.Id; Announce(st, "BREAKOUT!", target); }
        }
    }

    // ───────────── ANTI-SIEGE: keep an escape route open ─────────────
    private void PreventSiege(AIState st, Military m)
    {
        Nation n = st.Nation;
        int done = 0;
        _seen.Clear();
        for (int i = 0; i < m.Regiments.Count && done < 3; i++)
        {
            Regiment r = m.Regiments[i];
            if (r.State == RegimentState.Retreating || r.CurrentProvince == null) continue;
            Province p = r.CurrentProvince;
            if (!_seen.Add(p.Id)) continue;

            int[] nb = p.Neighbors;
            int hostile = 0;
            _slots.Clear();
            for (int k = 0; k < nb.Length; k++)
            {
                Province q = map.GetProvince(nb[k]);
                if (q == null) continue;
                if (_threatSet.Contains(q.Id)) hostile++;
                else if (Army.CanEnter(n, q)) _slots.Add(q);
            }
            if (hostile < 2 || hostile < (nb.Length + 1) / 2 || _slots.Count == 0) continue;

            for (int j = 0; j < m.Regiments.Count; j++)                 // the army inside stays put
                if (m.Regiments[j].CurrentProvince == p) _used.Add(m.Regiments[j]);

            for (int g = 0; g < _slots.Count && g < 2; g++)
            {
                Regiment f = Nearest(_free, Army.ProvinceWorldPos(_slots[g]), _slots[g], settings.minDefendStrength);
                if (f != null && Order(f, _slots[g])) { done++; Announce(st, "ANTI-SIEGE", _slots[g]); }
            }
        }
    }

    // ───────────── FLANK + SIEGE ─────────────
    private void AddSiegeCands(Province origin, War w, WarSide mine)
    {
        int[] nb = origin.Neighbors;
        for (int i = 0; i < nb.Length; i++)
        {
            Province q = map.GetProvince(nb[i]);
            if (q == null || q.Owner == null || !_seen.Add(q.Id)) continue;
            if (w.SideOf(q.Owner.Id) != War.Opposite(mine)) continue;
            if (w.Occupied.TryGetValue(q.Id, out int occ) && w.SideOf(occ) == mine) continue;
            _sgCands.Add(q);
        }
    }

    private void StartSiege(AIState st)
    {
        Nation n = st.Nation;
        _atk.Clear();
        for (int i = 0; i < _free.Count; i++)
        {
            Regiment r = _free[i];
            if (!_used.Contains(r) && r.StrengthPercent >= settings.minAttackStrength) _atk.Add(r);
        }
        if (_atk.Count < settings.siegeMinRegiments) return;
        int maxSlots = Mathf.Min(_atk.Count, settings.siegeMaxSlots);

        IReadOnlyList<War> wars = WM.GetWars(n);
        Province bestE = null;
        float bestScore = 0f;
        for (int wi = 0; wi < wars.Count; wi++)
        {
            War w = wars[wi];
            if (w.Ended) continue;
            WarSide mine = w.SideOf(n.Id);
            if (mine == WarSide.None) continue;

            _sgCands.Clear(); _seen.Clear();
            for (int i = 0; i < n.Provinces.Count; i++) AddSiegeCands(n.Provinces[i], w, mine);
            foreach (var kv in w.Occupied)
            {
                if (w.SideOf(kv.Value) != mine) continue;
                Province held = map.GetProvince(kv.Key);
                if (held != null) AddSiegeCands(held, w, mine);
            }
            int first = _sgCands.Count;
            if (_atk.Count >= 4)                                         // a deeper flank needs more units
                for (int i = 0; i < first && _sgCands.Count < 32; i++) AddSiegeCands(_sgCands[i], w, mine);

            for (int c = 0; c < _sgCands.Count; c++)
            {
                Province E = _sgCands[c];
                int slots = 0, enemySlots = 0;
                bool ok = true;
                int[] nb = E.Neighbors;
                for (int k = 0; k < nb.Length; k++)
                {
                    Province q = map.GetProvince(nb[k]);
                    if (q == null || Held(n, w, mine, q)) continue;
                    if (q.Owner != null && w.SideOf(q.Owner.Id) == WarSide.None) { ok = false; break; }   // neutral land: cannot be entered
                    slots++;
                    if (EnemyMen(n, q) > 0.0) enemySlots++;
                }
                if (!ok || slots == 0 || slots > maxSlots || enemySlots > 1) continue;

                float value = 1f + (E.IsCapital ? 1.5f : 0f) + (EnemyMen(n, E) > 0.0 ? 1f : 0f);
                float score = value / (1f + slots) * RNG.GetFloat(0.85f, 1.15f);
                if (score > bestScore) { bestScore = score; bestE = E; }
            }
        }
        if (bestE == null) return;

        st.SiegeTarget = bestE;
        st.SiegeSince = _clock;
        st.SiegeFormed = false;
        st.SiegeFrontal = false;
        st.SiegeUnits.Clear();
        AssignSlots(st);
        if (st.SiegeUnits.Count == 0) { EndSiegePlan(st); return; }
        Announce(st, "FLANK + SIEGE", bestE);
    }

    private void AssignSlots(AIState st)
    {
        Nation n = st.Nation;
        Province E = st.SiegeTarget;
        War w = E.Owner != null ? WM.GetWar(n, E.Owner) : null;
        if (w == null) return;
        WarSide mine = w.SideOf(n.Id);

        _slots.Clear();
        int[] nb = E.Neighbors;
        for (int i = 0; i < nb.Length; i++)
        {
            Province q = map.GetProvince(nb[i]);
            if (q == null || Held(n, w, mine, q)) continue;
            bool covered = false;
            for (int u = 0; u < st.SiegeUnits.Count && !covered; u++) covered = InOrHeading(st.SiegeUnits[u], q);
            if (!covered) _slots.Add(q);
        }
        if (_slots.Count == 0) return;

        _atk.Clear();
        Vector3 cen = Vector3.zero;
        for (int i = 0; i < _free.Count; i++)
        {
            Regiment r = _free[i];
            if (_used.Contains(r) || r.StrengthPercent < settings.minAttackStrength) continue;
            _atk.Add(r); cen += r.WorldPosition;
        }
        if (_atk.Count == 0) return;
        cen /= _atk.Count;

        // the farthest slots first: those are the flank positions
        _slots.Sort((a, b) => (Army.ProvinceWorldPos(b) - cen).sqrMagnitude.CompareTo((Army.ProvinceWorldPos(a) - cen).sqrMagnitude));

        _avoid.Clear();
        _avoid.Add(E.Id);
        for (int s = 0; s < _slots.Count; s++)
        {
            Province slot = _slots[s];
            Regiment r = Nearest(_atk, Army.ProvinceWorldPos(slot), null, 0f);
            if (r == null) break;
            _atk.Remove(r);
            _used.Add(r);
            if (!Army.MoveRegiment(r, slot, _avoid) && !Army.MoveRegiment(r, slot)) continue;

            r.TacticLock = true;
            st.SiegeUnits.Add(r);

            _pts.Clear();
            _pts.Add(r.WorldPosition);
            r.CopyPath(_pathTmp);
            for (int p = 0; p < _pathTmp.Count; p++) _pts.Add(Army.ProvinceWorldPos(_pathTmp[p]));
            if (_pts.Count >= 2) TacticVisuals.Line(_pts, n.Color, 12f, false, true);
        }
    }

    private void ContinueSiege(AIState st)
    {
        Nation n = st.Nation;
        Province E = st.SiegeTarget;
        War w = E.Owner != null ? WM.GetWar(n, E.Owner) : null;
        WarSide mine = w != null ? w.SideOf(n.Id) : WarSide.None;
        Siege s = CombatManager.Instance.GetSiegeAt(E);

        if (s != null && s.Side == mine)                                // ring closed: hold it
        {
            if (!st.SiegeFormed) { st.SiegeFormed = true; Announce(st, "ENCIRCLED!", E); }
            return;
        }
        if (st.SiegeFormed) { EndSiegePlan(st); return; }               // the siege is over: free the units

        bool bad = E.IsMerged || w == null || _clock - st.SiegeSince > settings.siegeTimeout ||
                   (w.Occupied.TryGetValue(E.Id, out int occ) && w.SideOf(occ) == mine);
        if (bad) { EndSiegePlan(st); st.NextTactic = _clock + settings.tacticInterval * 2f; return; }

        AssignSlots(st);

        if (settings.siegeFrontalFix && !st.SiegeFrontal && st.SiegeUnits.Count > 0)
        {
            int arrived = 0;
            for (int i = 0; i < st.SiegeUnits.Count; i++) if (st.SiegeUnits[i].State != RegimentState.Moving) arrived++;
            if (arrived >= 0.7f * st.SiegeUnits.Count)
            {
                Regiment f = Nearest(_free, Army.ProvinceWorldPos(E), E, settings.minAttackStrength);
                if (f != null && Order(f, E)) { st.SiegeFrontal = true; Announce(st, "PINCER", E); }
            }
        }
    }

    private void EndSiegePlan(AIState st)
    {
        for (int i = 0; i < st.SiegeUnits.Count; i++) if (st.SiegeUnits[i].Military != null) st.SiegeUnits[i].TacticLock = false;
        st.SiegeUnits.Clear();
        st.SiegeTarget = null;
        st.SiegeFormed = false;
        st.SiegeFrontal = false;
    }
}