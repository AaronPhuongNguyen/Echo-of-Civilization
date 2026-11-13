using System;
using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
//  DATA
// ─────────────────────────────────────────────────────────────────────────────
public enum MemberState : byte { Loyal = 0, Rebelling = 1 }

[Serializable]
public class FormationSettings
{
    [Header("Split (random)")]
    [Tooltip("A part needs at least this many provinces.")]
    [Min(1)] public int minPartProvinces = 2;
    [Tooltip("Relationship between the old nation and the new ones right after a split.")]
    public Vector2 splitRelation = new Vector2(0f, 30f);

    [Header("Form Nation (AI asks)")]
    public bool aiFormation = true;
    [Tooltip("Max nations (besides the leader) in one empire.")]
    [Min(1)] public int maxMembers = 4;
    [Tooltip("Leader coalition power must be at least this many times the target's power.")]
    public float requiredPowerRatio = 1.5f;
    [Tooltip("Relationship below this value: the AI does not even ask.")]
    [Range(-100f, 100f)] public float minRelationToAsk = 0f;
    [Tooltip("Chance of a request per AI diplomacy turn (x aggression factor).")]
    [Range(0f, 1f)] public float askChance = 0.35f;
    [Tooltip("Accept chance bonus when the two nations are allied.")]
    [Range(0f, 1f)] public float allyBonus = 0.15f;
    [Tooltip("Relationship lost when the request is refused.")]
    public float refusePenalty = 5f;
    [Tooltip("Days before the same pair can be asked again.")]
    public int askCooldownDays = 180;
    [Tooltip("The AI may ask the player nation (needs UI: OnFormationRequestToPlayer). Off = never.")]
    public bool aiMayAskPlayer = false;

    [Header("Empire stability (monthly)")]
    [Tooltip("Loyalty gain per month in peace.")]
    public float peaceDrift = 0.6f;
    public float warDrift = -1.5f;
    [Tooltip("Armies below this manpower % make members nervous.")]
    public float weakArmyPercent = 30f;
    public float weakArmyDrift = -1f;
    [Tooltip("Extra loss per month for every additional member (overextension).")]
    public float sizeDrift = -0.3f;
    [Tooltip("Penalty factor when the empire lost territory (held fraction < 0.8).")]
    public float lostLandFactor = 10f;
    public float noise = 0.4f;
    [Tooltip("A member whose relationship is BELOW this demands independence.")]
    public float demandBelow = 20f;
    [Tooltip("Demand success chance is (relationship+100)/200, halved while the empire is at war.")]
    [Range(0f, 1f)] public float warGrantFactor = 0.5f;
    [Tooltip("Relationship right after a refused demand.")]
    public float failedRelation = -20f;
    [Tooltip("Days after a refusal until the rebels declare their independence war.")]
    public int rebelWarDelayDays = 60;
    [Tooltip("Relationship after a peaceful release.")]
    public float releaseRelation = 25f;
}

public sealed class EmpireMember
{
    public int NationId;
    public string Name;
    public Color Color;
    public List<int> ProvinceIds = new List<int>();
    public int CapitalId = -1;
    /// <summary>-100..100: how the member feels about the empire.</summary>
    public float Relation;
    public MemberState State;
    public long RebelSince;
}

public sealed class Empire
{
    public int LeaderId;
    public readonly List<EmpireMember> Members = new List<EmpireMember>(4);
}

[Serializable]
public class EmpireInfo
{
    public string leader;
    public string members;
}

// ─────────────────────────────────────────────────────────────────────────────
//  NATION FORMATION MANAGER  (global access: NationFormationManager.Instance)
//
//  Split  : one nation -> N random, connected parts (original keeps its capital part).
//  Form   : leader absorbs other nations. Absorbed nations vanish, but remember their provinces.
//  Release: a member gets back the provinces it brought IF the empire still owns them and no enemy occupies them.
//  AI     : stronger AI asks a neighbour (success ~ relationship, 100 = always).
//           Members of an unstable empire demand independence (success ~ relationship).
//           Refused: relationship = -20, after a delay the rebels start an independence war.
// ─────────────────────────────────────────────────────────────────────────────
[DefaultExecutionOrder(-640)]
public class NationFormationManager : TickBehaviour
{
    // ── singleton ──
    private static NationFormationManager _instance;
    private static bool _quitting;
    public static bool HasInstance => _instance != null;

    public static NationFormationManager Instance
    {
        get
        {
            if (_instance == null && !_quitting)
            {
#if UNITY_2022_2_OR_NEWER
                _instance = FindFirstObjectByType<NationFormationManager>();
#else
                _instance = FindObjectOfType<NationFormationManager>();
#endif
                if (_instance == null) _instance = new GameObject("NationFormationManager").AddComponent<NationFormationManager>();
            }
            return _instance;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() { _instance = null; _quitting = false; }

    public FormationSettings settings = new FormationSettings();
    [SerializeField] private MapManager map;

    [Header("Context menu helpers")]
    [SerializeField] private int debugNationA = -1;   // leader / nation to split
    [SerializeField] private int debugNationB = -1;   // member
    [SerializeField, Min(2)] private int debugParts = 3;

    [Header("Inspector view (Editor only)")]
    [SerializeField] private List<EmpireInfo> empiresView = new List<EmpireInfo>();

    protected override int TickPriority => -640;

    // ── data ──
    private readonly Dictionary<int, Empire> _empires = new Dictionary<int, Empire>();        // leader id -> empire
    private readonly Dictionary<long, long> _cooldown = new Dictionary<long, long>();        // pair -> day when asking is allowed again
    private sealed class CivilWar { public Empire Empire; public EmpireMember Rec; public int RebelId; }
    private readonly Dictionary<int, CivilWar> _civil = new Dictionary<int, CivilWar>();      // war id -> data
    private readonly List<Empire> _empBuf = new List<Empire>();
    private readonly List<Province> _provBuf = new List<Province>();
    private readonly List<War> _warBuf = new List<War>();
    private readonly HashSet<Nation> _nbBuf = new HashSet<Nation>();

    public string LastError { get; private set; }
    public int EmpireCount => _empires.Count;

    // ── events ──
    public event Action<Nation, List<Nation>> OnNationFormed;            // leader, absorbed (already removed)
    public event Action<Nation, List<Nation>> OnNationSplit;             // original, new nations
    public event Action<Nation, Nation> OnIndependenceGranted;           // empire leader, new nation
    public event Action<Nation, string> OnIndependenceDenied;            // empire leader, member name
    public event Action<Nation, Nation> OnFormationRefused;              // proposer, target
    public event Action<Nation, Nation> OnFormationRequestToPlayer;      // proposer, player nation

    private WorldManager World => WorldManager.Instance;
    private WarManager WM => WarManager.Instance;
    private ArmyManager Army => ArmyManager.Instance;
    private RelationshipManager Rel => RelationshipManager.Instance;

    // ─────────────────────────────────────────────────────────────────────
    //  ADAPTER: the only places that touch WorldManager API I could not see.
    //  Adjust these 3 methods to your WorldManager if the names differ.
    // ─────────────────────────────────────────────────────────────────────
    private Nation CreateNation(string name, Color color) => World.CreateNation(name, color);

    private void RemoveNation(Nation n)
    {
        if (n == null || !n.IsEmpty) return;
        World.RemoveNation(n.Id);
        if (map != null) map.RefreshCapitalMarkers();
    }

    private void SetCapital(Nation n, Province p)
    {
        if (n == null || p == null) return;
        World.SetCapital(n, p);
        if (map != null) map.RefreshCapitalMarkers();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  LIFECYCLE
    // ─────────────────────────────────────────────────────────────────────
    private void Awake()
    {
        if (_instance != null && _instance != this) { Destroy(this); return; }
        _instance = this;
    }

    private void OnApplicationQuit() => _quitting = true;

    protected override void OnEnable()
    {
        base.OnEnable();
        if (_instance != this) return;
        if (map == null)
        {
#if UNITY_2022_2_OR_NEWER
            map = FindFirstObjectByType<MapManager>();
#else
            map = FindObjectOfType<MapManager>();
#endif
        }
        World.OnNationsCleared += ClearAll;
        World.OnNationRemoved += HandleNationRemoved;
        DateManager.Instance.OnDayPassed += HandleDay;
        DateManager.Instance.OnMonthPassed += HandleMonth;
        WM.OnWarEnded += HandleWarEnded;
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        if (WorldManager.HasInstance)
        {
            WorldManager.Instance.OnNationsCleared -= ClearAll;
            WorldManager.Instance.OnNationRemoved -= HandleNationRemoved;
        }
        if (DateManager.HasInstance)
        {
            DateManager.Instance.OnDayPassed -= HandleDay;
            DateManager.Instance.OnMonthPassed -= HandleMonth;
        }
        if (WarManager.HasInstance) WarManager.Instance.OnWarEnded -= HandleWarEnded;
        if (_instance == this && _quitting) _instance = null;
    }

    private void OnDestroy() { if (_instance == this) _instance = null; }

    public override void Tick(float deltaTime) { }

    public void ClearAll()
    {
        _empires.Clear(); _cooldown.Clear(); _civil.Clear();
    }

    private void HandleNationRemoved(Nation n)
    {
        if (n != null) _empires.Remove(n.Id);          // leader gone: its empire (and the land) is gone too
    }

    // ─────────────────────────────────────────────────────────────────────
    //  QUERIES
    // ─────────────────────────────────────────────────────────────────────
    public Empire GetEmpire(Nation leader) => leader != null && _empires.TryGetValue(leader.Id, out Empire e) ? e : null;

    private bool Fail(string m) { LastError = m; return false; }

    private double Power(Nation x)
    {
        Military m = Army.GetMilitary(x);
        return m == null ? 0.0 : m.CurrentManpower * (m.AttackPerMan.Value + m.DefendPerMan.Value);
    }

    private bool OccupiedByEnemy(Nation holder, Province p)
    {
        IReadOnlyList<War> wars = WM.GetWars(holder);
        for (int i = 0; i < wars.Count; i++)
        {
            War w = wars[i];
            if (!w.Occupied.TryGetValue(p.Id, out int occ)) continue;
            WarSide so = w.SideOf(occ), sh = w.SideOf(holder.Id);
            if (so != WarSide.None && so != sh) return true;
        }
        return false;
    }

    private void Transfer(Province p, Nation to)
    {
        if (map != null && map.IsReady) map.SetProvinceOwner(p.Id, to.Id);
        else World.AssignProvince(p, to);
    }

    private static long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    private void Refresh(Nation n)
    {
        if (n == null) return;
        Army.EnsureMilitary(n);
        if (AIManager.HasInstance) AIManager.Instance.RefreshNations();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  SPLIT: random connected parts
    // ─────────────────────────────────────────────────────────────────────
    public List<Nation> Split(Nation n, int parts)
    {
        LastError = null;
        var result = new List<Nation>();
        if (n == null || n.IsEmpty) { Fail("Nothing to split."); return result; }
        if (_empires.ContainsKey(n.Id)) { Fail("Dissolve the empire first."); return result; }
        if (WM.IsAtWar(n)) { Fail("End the war first."); return result; }

        var all = new List<Province>(n.Provinces);
        parts = Mathf.Min(parts, all.Count / Mathf.Max(1, settings.minPartProvinces));
        if (parts < 2) { Fail("Too few provinces."); return result; }

        var mine = new HashSet<int>();
        for (int i = 0; i < all.Count; i++) mine.Add(all[i].Id);

        // seeds: part 0 = capital (the original nation), others random and distinct
        var seeds = new List<Province>(parts);
        seeds.Add(n.Capital != null && mine.Contains(n.Capital.Id) ? n.Capital : all[RNG.GetInt(0, all.Count)]);
        for (int t = 0; t < 200 && seeds.Count < parts; t++)
        {
            Province c = all[RNG.GetInt(0, all.Count)];
            if (!seeds.Contains(c)) seeds.Add(c);
        }
        parts = seeds.Count;

        var part = new Dictionary<int, int>(all.Count);
        var front = new List<Province>[parts];
        var weight = new float[parts];
        for (int k = 0; k < parts; k++)
        {
            front[k] = new List<Province>(8);
            weight[k] = RNG.GetFloat(0.4f, 1.6f);                // random size
            part[seeds[k].Id] = k;
            Expand(seeds[k], k, front[k], part, mine);
        }

        int remaining = all.Count - parts;
        while (remaining > 0)
        {
            float sum = 0f;
            for (int k = 0; k < parts; k++) if (front[k].Count > 0) sum += weight[k];
            if (sum <= 0f) break;
            float roll = RNG.GetFloat(0f, sum);
            int pick = -1;
            for (int k = 0; k < parts; k++)
            {
                if (front[k].Count == 0) continue;
                pick = k; roll -= weight[k];
                if (roll <= 0f) break;
            }
            List<Province> f = front[pick];
            int idx = RNG.GetInt(0, f.Count);
            Province p = f[idx];
            f[idx] = f[f.Count - 1]; f.RemoveAt(f.Count - 1);
            if (part.ContainsKey(p.Id)) continue;
            part[p.Id] = pick; remaining--;
            Expand(p, pick, f, part, mine);
        }

        // islands / disconnected leftovers: nearest seed
        for (int i = 0; i < all.Count; i++)
        {
            if (part.ContainsKey(all[i].Id)) continue;
            Vector3 pos = Army.ProvinceWorldPos(all[i]);
            int best = 0; float bd = float.MaxValue;
            for (int k = 0; k < parts; k++)
            {
                float d = (Army.ProvinceWorldPos(seeds[k]) - pos).sqrMagnitude;
                if (d < bd) { bd = d; best = k; }
            }
            part[all[i].Id] = best;
        }

        // create the new nations (part 0 stays the original)
        string[] titles = { "Kingdom of ", "Duchy of ", "Republic of ", "Principality of " };
        var created = new Nation[parts];
        created[0] = n;
        for (int k = 1; k < parts; k++)
        {
            Nation nn = CreateNation(titles[RNG.GetInt(0, titles.Length)] + seeds[k].Name,
                Color.HSVToRGB(RNG.GetFloat(0f, 1f), RNG.GetFloat(0.55f, 0.8f), RNG.GetFloat(0.65f, 0.9f)));
            if (nn == null) { Fail("CreateNation failed."); break; }
            created[k] = nn;
        }
        for (int i = 0; i < all.Count; i++)
        {
            int k = part[all[i].Id];
            if (k == 0 || created[k] == null) continue;
            Transfer(all[i], created[k]);
        }
        for (int k = 1; k < parts; k++)
        {
            if (created[k] == null) continue;
            SetCapital(created[k], seeds[k]);
            Refresh(created[k]);
            result.Add(created[k]);
        }
        // relations: everybody neutral-ish
        for (int a = 0; a < parts; a++)
            for (int b = a + 1; b < parts; b++)
                if (created[a] != null && created[b] != null)
                    Rel.SetValue(created[a], created[b], RNG.GetFloat(settings.splitRelation.x, settings.splitRelation.y));

        OnNationSplit?.Invoke(n, result);
        return result;
    }

    private void Expand(Province p, int k, List<Province> front, Dictionary<int, int> part, HashSet<int> mine)
    {
        int[] nb = p.Neighbors;
        for (int i = 0; i < nb.Length; i++)
        {
            if (!mine.Contains(nb[i]) || part.ContainsKey(nb[i])) continue;
            Province q = map.GetProvince(nb[i]);
            if (q != null) front.Add(q);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  FORM NATION
    //   leader = the nation that demands (keeps its name). Members disappear but get their land back later.
    // ─────────────────────────────────────────────────────────────────────
    public bool Form(Nation leader, IList<Nation> members)
    {
        LastError = null;
        if (leader == null || leader.IsEmpty || members == null || members.Count == 0) return Fail("Nothing to form.");
        _empires.TryGetValue(leader.Id, out Empire e);
        if ((e != null ? e.Members.Count : 0) + members.Count > settings.maxMembers) return Fail("Empire would be too big.");

        for (int i = 0; i < members.Count; i++)
        {
            Nation m = members[i];
            if (m == null || m == leader || m.IsEmpty || World.GetNation(m.Id) != m) return Fail("Invalid member.");
            IReadOnlyList<War> wars = WM.GetWars(m);
            for (int w = 0; w < wars.Count; w++)
            {
                if (wars[w].IsMain(m.Id)) return Fail(m.Name + " leads a war.");
                if (wars[w].SideOf(m.Id) != wars[w].SideOf(leader.Id)) return Fail(m.Name + " fights a war the leader does not share.");
            }
        }

        if (e == null) { e = new Empire { LeaderId = leader.Id }; _empires[leader.Id] = e; }
        var absorbed = new List<Nation>(members.Count);

        for (int i = 0; i < members.Count; i++)
        {
            Nation m = members[i];
            var rec = new EmpireMember { NationId = m.Id, Name = m.Name, Color = m.Color, Relation = Rel.GetValue(m, leader), CapitalId = m.Capital != null ? m.Capital.Id : -1 };

            // a member that was itself an empire: its members come along, its own record keeps only its own land
            var subIds = new HashSet<int>();
            if (_empires.TryGetValue(m.Id, out Empire sub))
            {
                _empires.Remove(m.Id);
                for (int s = 0; s < sub.Members.Count; s++)
                {
                    for (int q = 0; q < sub.Members[s].ProvinceIds.Count; q++) subIds.Add(sub.Members[s].ProvinceIds[q]);
                    e.Members.Add(sub.Members[s]);
                }
            }

            _provBuf.Clear();
            _provBuf.AddRange(m.Provinces);
            for (int p = 0; p < _provBuf.Count; p++)
                if (!subIds.Contains(_provBuf[p].Id)) rec.ProvinceIds.Add(_provBuf[p].Id);

            _warBuf.Clear();
            for (int w = 0; w < WM.GetWars(m).Count; w++) _warBuf.Add(WM.GetWars(m)[w]);
            for (int w = 0; w < _warBuf.Count; w++) WM.LeaveWar(_warBuf[w], m);

            Military mil = Army.GetMilitary(m);
            while (mil != null && mil.RemoveLastRegiment()) { }

            for (int p = 0; p < _provBuf.Count; p++) Transfer(_provBuf[p], leader);
            e.Members.Add(rec);
            absorbed.Add(m);
        }
        for (int i = 0; i < absorbed.Count; i++) RemoveNation(absorbed[i]);

        OnNationFormed?.Invoke(leader, absorbed);
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  RELEASE: member gets its land back (only what the empire still owns and no enemy occupies)
    // ─────────────────────────────────────────────────────────────────────
    private Nation Restore(Empire e, EmpireMember m, Nation holder)
    {
        var take = new List<Province>();
        for (int i = 0; i < m.ProvinceIds.Count; i++)
        {
            Province p = map.GetProvince(m.ProvinceIds[i]);
            if (p == null || p.Owner != holder || p == holder.Capital || OccupiedByEnemy(holder, p)) continue;
            take.Add(p);
        }
        if (take.Count == 0) return null;

        Nation n = World.GetNation(m.NationId);
        if (n == null || !n.IsEmpty) n = CreateNation(m.Name, m.Color);
        if (n == null) return null;

        Province cap = take[0];
        for (int i = 0; i < take.Count; i++)
        {
            if (take[i].Id == m.CapitalId) cap = take[i];
            Transfer(take[i], n);
        }
        SetCapital(n, cap);
        m.NationId = n.Id;
        Refresh(n);
        return n;
    }

    private void DropMember(Empire e, EmpireMember m)
    {
        e.Members.Remove(m);
        if (e.Members.Count == 0) _empires.Remove(e.LeaderId);
    }

    private Nation Release(Empire e, EmpireMember m)
    {
        Nation leader = World.GetNation(e.LeaderId);
        if (leader == null) { DropMember(e, m); return null; }
        Nation n = Restore(e, m, leader);
        DropMember(e, m);
        if (n == null) return null;                                   // no land left to give back
        Rel.SetValue(n, leader, settings.releaseRelation);
        OnIndependenceGranted?.Invoke(leader, n);
        return n;
    }

    public bool Dissolve(Nation leader)
    {
        LastError = null;
        Empire e = GetEmpire(leader);
        if (e == null) return Fail("Not an empire.");
        var copy = new List<EmpireMember>(e.Members);
        for (int i = 0; i < copy.Count; i++) Release(e, copy[i]);
        _empires.Remove(leader.Id);
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  AI: ask a neighbour to join (called from AIManager.RunDiplomacy)
    // ─────────────────────────────────────────────────────────────────────
    public void AIThink(Nation n, float aggression, long today)
    {
        if (!settings.aiFormation || n == null || n.IsEmpty || WM.IsAtWar(n)) return;
        Empire own = GetEmpire(n);
        if (own != null && own.Members.Count >= settings.maxMembers) return;
        if (RNG.GetFloat(0f, 1f) > settings.askChance * (0.4f + aggression)) return;

        double mine = Power(n);
        _nbBuf.Clear();
        for (int i = 0; i < n.Provinces.Count; i++)
        {
            int[] nb = n.Provinces[i].Neighbors;
            for (int j = 0; j < nb.Length; j++)
            {
                Province q = map.GetProvince(nb[j]);
                if (q != null && q.Owner != null && q.Owner != n) _nbBuf.Add(q.Owner);
            }
        }

        Nation target = null;
        double tp = 0.0;
        foreach (Nation o in _nbBuf)
        {
            if (o.IsEmpty || WM.IsAtWar(o) || _empires.ContainsKey(o.Id)) continue;
            if (o.IsPlayer && !settings.aiMayAskPlayer) continue;
            if (_cooldown.TryGetValue(Key(n.Id, o.Id), out long until) && today < until) continue;
            if (Rel.GetValue(n, o) < settings.minRelationToAsk) continue;
            if (Rel.IsVassalOf(o, n) || Rel.IsVassalOf(n, o)) continue;
            double op = Power(o);
            if (op * settings.requiredPowerRatio > mine) continue;     // only clearly weaker neighbours
            if (target == null || op > tp) { target = o; tp = op; }    // the strongest of the weak = best prize
        }
        if (target == null) return;

        _cooldown[Key(n.Id, target.Id)] = today + settings.askCooldownDays;

        if (target.IsPlayer) { OnFormationRequestToPlayer?.Invoke(n, target); return; }

        float v = Rel.GetValue(n, target);
        float chance = Mathf.Clamp01(Mathf.InverseLerp(settings.minRelationToAsk, 100f, v));
        if (Rel.IsAllied(n, target)) chance = Mathf.Min(1f, chance + settings.allyBonus);
        if (v >= 100f) chance = 1f;                                    // 100% relationship: gratefully accepts

        if (RNG.GetFloat(0f, 1f) <= chance) Form(n, new[] { target });
        else
        {
            Rel.AddValue(n, target, -settings.refusePenalty);
            OnFormationRefused?.Invoke(n, target);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  STABILITY / INDEPENDENCE
    // ─────────────────────────────────────────────────────────────────────
    private void HandleMonth()
    {
        if (_empires.Count == 0) return;
        long today = DateManager.Instance.TotalDays;
        _empBuf.Clear();
        _empBuf.AddRange(_empires.Values);

        for (int i = 0; i < _empBuf.Count; i++)
        {
            Empire e = _empBuf[i];
            Nation L = World.GetNation(e.LeaderId);
            if (L == null) { _empires.Remove(e.LeaderId); continue; }

            bool war = WM.IsAtWar(L);
            Military mil = Army.GetMilitary(L);
            bool weak = mil != null && mil.ManpowerPercent < settings.weakArmyPercent;

            int total = 0, held = 0;
            for (int m = 0; m < e.Members.Count; m++)
                for (int p = 0; p < e.Members[m].ProvinceIds.Count; p++)
                {
                    total++;
                    Province pr = map.GetProvince(e.Members[m].ProvinceIds[p]);
                    if (pr != null && pr.Owner == L) held++;
                }
            float fraction = total > 0 ? held / (float)total : 1f;

            float drift = war ? settings.warDrift : settings.peaceDrift;
            if (weak) drift += settings.weakArmyDrift;
            if (L.Treasury < 0.0) drift -= 1f;
            if (fraction < 0.8f) drift -= (0.8f - fraction) * settings.lostLandFactor;
            drift += settings.sizeDrift * (e.Members.Count - 1);

            var copy = new List<EmpireMember>(e.Members);
            for (int m = 0; m < copy.Count; m++)
            {
                EmpireMember mem = copy[m];
                if (mem.State != MemberState.Loyal) continue;
                mem.Relation = Mathf.Clamp(mem.Relation + drift + RNG.GetFloat(-settings.noise, settings.noise), -100f, 100f);
                if (mem.Relation < settings.demandBelow) Demand(e, mem, L, war, today);
            }
        }
    }

    private void Demand(Empire e, EmpireMember m, Nation leader, bool war, long today)
    {
        float chance = Mathf.Clamp01((m.Relation + 100f) / 200f) * (war ? settings.warGrantFactor : 1f);
        if (RNG.GetFloat(0f, 1f) <= chance) { Release(e, m); return; }

        m.Relation = settings.failedRelation;
        m.State = MemberState.Rebelling;
        m.RebelSince = today;
        OnIndependenceDenied?.Invoke(leader, m.Name);
    }

    private void HandleDay()
    {
        if (_empires.Count == 0) return;
        long today = DateManager.Instance.TotalDays;
        _empBuf.Clear();
        _empBuf.AddRange(_empires.Values);
        for (int i = 0; i < _empBuf.Count; i++)
        {
            Empire e = _empBuf[i];
            for (int m = e.Members.Count - 1; m >= 0; m--)
            {
                EmpireMember mem = e.Members[m];
                if (mem.State == MemberState.Rebelling && today - mem.RebelSince >= settings.rebelWarDelayDays)
                    DeclareIndependenceWar(e, mem);
            }
        }
    }

    /// <summary>The rebels take their land (still owned by the empire) and fight for it. Public: a player may call it early.</summary>
    public bool DeclareIndependenceWar(Empire e, EmpireMember m)
    {
        LastError = null;
        Nation leader = e != null ? World.GetNation(e.LeaderId) : null;
        if (leader == null || m == null) return Fail("Invalid empire.");

        Nation rebel = Restore(e, m, leader);
        if (rebel == null) { DropMember(e, m); return Fail("Rebels have no land left."); }
        DropMember(e, m);

        War w = WM.ForceDeclareWar(rebel, leader);
        if (w == null)
        {
            // cannot fight: back under the empire
            Reannex(e, m, rebel, leader);
            return Fail(WM.LastError);
        }
        _civil[w.Id] = new CivilWar { Empire = e, Rec = m, RebelId = rebel.Id };
        return true;
    }

    private void HandleWarEnded(War w, WarResult result)
    {
        if (!_civil.TryGetValue(w.Id, out CivilWar c)) return;
        _civil.Remove(w.Id);

        Nation rebel = World.GetNation(c.RebelId);
        Nation leader = World.GetNation(c.Empire.LeaderId);
        if (rebel == null) return;
        if (leader == null) return;                                   // empire is gone: the rebels stay free

        if (result == WarResult.DefenderVictory) { Reannex(c.Empire, c.Rec, rebel, leader); return; }

        // rebels won (or white peace): they also get back the rest of their land that is still free
        if (result == WarResult.AttackerVictory)
            for (int i = 0; i < c.Rec.ProvinceIds.Count; i++)
            {
                Province p = map.GetProvince(c.Rec.ProvinceIds[i]);
                if (p != null && p.Owner == leader && p != leader.Capital && !OccupiedByEnemy(leader, p)) Transfer(p, rebel);
            }
        OnIndependenceGranted?.Invoke(leader, rebel);
    }

    /// <summary>The empire won: the rebel nation disappears and everything it holds goes back to the leader.</summary>
    private void Reannex(Empire e, EmpireMember m, Nation rebel, Nation leader)
    {
        _provBuf.Clear();
        _provBuf.AddRange(rebel.Provinces);
        Military mil = Army.GetMilitary(rebel);
        while (mil != null && mil.RemoveLastRegiment()) { }
        for (int i = 0; i < _provBuf.Count; i++) Transfer(_provBuf[i], leader);
        RemoveNation(rebel);

        m.State = MemberState.Loyal;
        m.Relation = 0f;
        if (!e.Members.Contains(m)) e.Members.Add(m);
        _empires[e.LeaderId] = e;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  CONTEXT MENU
    // ─────────────────────────────────────────────────────────────────────
    private Nation Pick(int id) => id >= 0 ? World.GetNation(id) : World.GetRandomNation();

    [ContextMenu("Nation: Split (A into debugParts)")]
    private void MenuSplit()
    {
        Nation a = Pick(debugNationA);
        List<Nation> r = Split(a, debugParts);
        Debug.Log($"[Formation] Split {(a != null ? a.Name : "?")}: {r.Count} new nation(s). {LastError}");
    }

    [ContextMenu("Nation: Form (A leader absorbs B)")]
    private void MenuForm()
    {
        Nation a = Pick(debugNationA), b = Pick(debugNationB);
        Debug.Log($"[Formation] Form: {(Form(a, new[] { b }) ? "OK" : "FAILED: " + LastError)}");
    }

    [ContextMenu("Nation: Dissolve Empire (A)")]
    private void MenuDissolve() => Debug.Log($"[Formation] Dissolve: {(Dissolve(Pick(debugNationA)) ? "OK" : "FAILED: " + LastError)}");

    [ContextMenu("Nation: Make All Members Unhappy (relation 0)")]
    private void MenuUnhappy()
    {
        foreach (Empire e in _empires.Values)
            for (int i = 0; i < e.Members.Count; i++) e.Members[i].Relation = 0f;
    }
}