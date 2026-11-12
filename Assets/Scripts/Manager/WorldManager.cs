using System;
using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
//  DATA
// ─────────────────────────────────────────────────────────────────────────────

public enum StatType : byte { Tax = 0, Economy = 1, Defend = 2, PopGrowth = 3, Infrastructure = 4 }

public struct UpgradeJob
{
    public StatType Stat;
    public double Price;     // what was paid (refunded on cancel)
    public UpgradeJob(StatType s, double p) { Stat = s; Price = p; }
}

/// <summary>Per-province upgrade state. Created lazily (most provinces never upgrade anything).</summary>
public class ProvinceUpgrades
{
    public const int StatCount = 5;
    public readonly List<UpgradeJob> Queue = new List<UpgradeJob>(4);
    public int DaysLeft;
    public readonly int[] Done = new int[StatCount];
    public readonly int[] Queued = new int[StatCount];
    /// <summary>Independent price of each stat in THIS province (Base = basePrice).</summary>
    public readonly GameValue[] Price = new GameValue[StatCount];
    internal bool IsActive;

    public ProvinceUpgrades(float basePrice)
    {
        for (int i = 0; i < StatCount; i++) Price[i] = new GameValue(basePrice);
    }
}

public class Province : IDirtyable
{
    // ── identity / geometry ──
    public int Id;
    public string Name = "";
    public int OwnerNationId = -1;               // nation's independent Id, -1 = neutral
    public Nation Owner { get; internal set; }   // set by WorldManager.AssignProvince
    public int[] Tiles;                          // CLUSTER of tile indices (y * width + x)
    public int TileCount => Tiles.Length;
    public Vector2 Centroid;                     // tile coordinates
    public int[] Neighbors = Array.Empty<int>();   // ids of provinces sharing a land border
    public Vector2Int Min, Max;                  // tile bounding box (inclusive)
    public float Shade = 1f;                     // small brightness variation so provinces look different
    public Color32 Color;                        // cached display color

    // ── stats (GameValue: (Base + Flat + Base * Percent) * Total) ──
    public readonly GameValue Tax = new GameValue();
    public readonly GameValue Economy = new GameValue();
    public readonly GameValue Defend = new GameValue();
    public readonly GameValue PopGrowth = new GameValue();
    public readonly GameValue Infrastructure = new GameValue();
    /// <summary>"Production Income": multiplier of the whole income (Base 1 = 100%).</summary>
    public readonly GameValue ProductionIncome = new GameValue(1f);

    private float _devastation, _population;
    public float Population { get => _population; set { _population = Mathf.Max(0f, value); MarkDirty(); } }
    /// <summary>0..1. Reduces the province income (1 = income is zero).</summary>
    public float Devastation { get => _devastation; set { _devastation = Mathf.Clamp01(value); MarkDirty(); } }

    public Province()
    {
        Tax.Bind(this); Economy.Bind(this); Defend.Bind(this);
        PopGrowth.Bind(this); Infrastructure.Bind(this); ProductionIncome.Bind(this);
    }

    public GameValue GetStat(StatType s)
    {
        switch (s)
        {
            case StatType.Tax: return Tax;
            case StatType.Economy: return Economy;
            case StatType.Defend: return Defend;
            case StatType.PopGrowth: return PopGrowth;
            default: return Infrastructure;
        }
    }

    /// <summary>Production Efficiency = 1 (base 100%) + Infrastructure * 0.03.</summary>
    public float ProductionEfficiency => 1f + Infrastructure.Value * 0.03f;

    /// <summary>Income = ProductionEfficiency * (Tax * Population + Economy) * ProductionIncome * (1 - Devastation).</summary>
    public float Income =>
        ProductionEfficiency * (Tax.Value * _population + Economy.Value) * ProductionIncome.Value * (1f - _devastation);

    public bool IsCapital => Owner != null && Owner.Capital == this;

    public void MarkDirty() => Owner?.MarkDirty();

    // ── upgrades ──
    private ProvinceUpgrades _upgrades;
    public ProvinceUpgrades Upgrades =>
    _upgrades ?? (_upgrades = new ProvinceUpgrades(WorldManager.Instance.upgrades.basePrice));
    public bool HasUpgrades => _upgrades != null && _upgrades.Queue.Count > 0;

    /// <summary>Queues one increase (+1 base by default). Paid by the owner's treasury. See WorldManager.upgrades.</summary>
    public bool Increase(StatType stat) => WorldManager.Instance.TryQueueIncrease(this, stat);
    public bool IncreaseTax() => Increase(StatType.Tax);
    public bool IncreaseEconomy() => Increase(StatType.Economy);
    public bool IncreaseDefend() => Increase(StatType.Defend);
    public bool IncreasePopGrowth() => Increase(StatType.PopGrowth);
    public bool IncreaseInfrastructure() => Increase(StatType.Infrastructure);
}

public class Nation : IDirtyable
{
    /// <summary>Unique, stable ID. Independent of the list position.</summary>
    public int Id { get; }
    public string Name;
    public Color Color;
    public readonly List<Province> Provinces = new List<Province>();

    /// <summary>Multiplier of the daily income (Base 1 = 100%).</summary>
    public readonly GameValue GdpMultiplier = new GameValue(1f);
    /// <summary>Multiplier of every increase price this nation pays (Base 1 = 100%).</summary>
    public readonly GameValue UpgradeCostMultiplier = new GameValue(1f);

    // cached aggregates (recomputed lazily when a province changes)
    private bool _dirty = true;
    private int _area;
    private double _economy, _population;
    private float _avgDevastation, _avgDefend, _avgInfrastructure;

    internal Nation(int id, string name, Color color)
    {
        Id = id; Name = name; Color = color;
        GdpMultiplier.Bind(this);
        UpgradeCostMultiplier.Bind(this);
    }

    /// <summary>Capital province (starts with double population and defend). If lost, the most populated remaining province takes over.</summary>
    public Province Capital { get; internal set; }

    // ── treasury ──
    public double Treasury { get; private set; }
    public double MaxTreasury => Economy;
    public double MinTreasury => -Economy;
    /// <summary>Daily income: Economy spread over one year, times GdpMultiplier.</summary>
    public double GDP => Economy * GdpMultiplier.Value / DateManager.EconomyDaysPerYear;

    public double AddMoney(double amount)
    {
        double e = Economy;
        Treasury = Math.Min(e, Math.Max(-e, Treasury + amount));
        return Treasury;
    }

    public bool TrySpend(double amount)
    {
        if (amount < 0 || Treasury - amount < MinTreasury) return false;
        Treasury -= amount;
        return true;
    }

    internal void CollectDailyIncome() => AddMoney(GDP);

    public int ProvinceCount => Provinces.Count;
    public bool IsEmpty => Provinces.Count == 0;
    public bool IsExtinct => Provinces.Count == 0;

    public int Area { get { Refresh(); return _area; } }
    public double Economy { get { Refresh(); return _economy; } }
    public double Population { get { Refresh(); return _population; } }
    public float AverageDevastation { get { Refresh(); return _avgDevastation; } }
    public float AverageDefend { get { Refresh(); return _avgDefend; } }
    public float AverageInfrastructure { get { Refresh(); return _avgInfrastructure; } }

    public void MarkDirty() => _dirty = true;
    internal void RefreshIfDirty() => Refresh();

    private void Refresh()
    {
        if (!_dirty) return;
        _dirty = false;

        int area = 0;
        double econ = 0, pop = 0, dev = 0, def = 0, infra = 0;
        for (int i = 0; i < Provinces.Count; i++)
        {
            Province p = Provinces[i];
            area += p.TileCount;
            econ += p.Income;
            pop += p.Population;
            dev += p.Devastation;
            def += p.Defend.Value;
            infra += p.Infrastructure.Value;
        }

        int n = Provinces.Count;
        _area = area;
        _economy = econ;
        _population = pop;
        _avgDevastation = n > 0 ? (float)(dev / n) : 0f;
        _avgDefend = n > 0 ? (float)(def / n) : 0f;
        _avgInfrastructure = n > 0 ? (float)(infra / n) : 0f;

        Treasury = Math.Min(_economy, Math.Max(-_economy, Treasury));
    }
}

public enum NationStat { Area, Provinces, Population, Economy }

/// <summary>Read-only snapshot of a nation, only used to show the list in the Inspector.</summary>
[Serializable]
public class NationInfo
{
    public string name;
    public int id;
    public Color color;
    public int provinces;
    public int area;
    public double population;
    public double economy;
    public double gdp;
    public double treasury;
    public double minTreasury;
    public double maxTreasury;
    public int capitalProvinceId;
}

/// <summary>Rules of the "Increase" system (all editable in the WorldManager Inspector).</summary>
[Serializable]
public class UpgradeSettings
{
    [Tooltip("Starting price of every stat in every province.")]
    public float basePrice = 50f;
    [Tooltip("Global inflation: price += inflation * the province's stat value. Tax 0.5 * 5 = +2.5.")]
    public float inflation = 5f;
    // (inflationPerIncrease removed)
    [Tooltip("Days one increase takes. Queued increases run one after another.")]
    public int daysPerIncrease = 90;
    [Tooltip("Max increases waiting in one province's queue (including the running one).")]
    public int maxQueue = 100;
    [Tooltip("How much Base a completed increase adds (Economy 1.1 -> 2.1).")]
    public float amountPerIncrease = 1f;
}

// ─────────────────────────────────────────────────────────────────────────────
//  WORLD MANAGER  (global access: WorldManager.Instance)
// ─────────────────────────────────────────────────────────────────────────────

[DefaultExecutionOrder(-1000)]
public class WorldManager : TickBehaviour
{
    // ── singleton ──
    private static WorldManager _instance;
    private static bool _quitting;

    public static bool HasInstance => _instance != null;

    public static WorldManager Instance
    {
        get
        {
            if (_instance == null && !_quitting)
            {
#if UNITY_2022_2_OR_NEWER
                _instance = FindFirstObjectByType<WorldManager>();
#else
                _instance = FindObjectOfType<WorldManager>();
#endif
                if (_instance == null)
                    _instance = new GameObject("WorldManager").AddComponent<WorldManager>();
            }
            return _instance;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() { _instance = null; _quitting = false; }

    // ── nations ──
    private readonly List<Nation> _nations = new List<Nation>();
    private readonly Dictionary<int, Nation> _byId = new Dictionary<int, Nation>();
    private int _nextNationId;

    [Header("Increase system (Tax / Economy / Defend / ...)")]
    public UpgradeSettings upgrades = new UpgradeSettings();
    private readonly List<Province> _active = new List<Province>();   // provinces with a running increase

    [Header("Inspector view (read-only mirror, Editor only)")]
    [SerializeField] private bool showNationsInInspector = true;
    [SerializeField, Min(0.1f)] private float inspectorRefreshSeconds = 1f;
    [SerializeField] private List<NationInfo> nationsView = new List<NationInfo>();
    private float _nextInspectorRefresh;

    public IReadOnlyList<Nation> Nations => _nations;
    public int NationCount => _nations.Count;

    public event Action<Nation> OnNationCreated;
    public event Action<Nation> OnNationRemoved;
    public event Action OnNationsCleared;
    public event Action<Province, StatType> OnIncreaseQueued;
    public event Action<Province, StatType> OnIncreaseCompleted;

    protected override int TickPriority => -1000;

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Debug.LogWarning("Duplicate WorldManager removed.");
            Destroy(this);
            return;
        }
        _instance = this;
    }

    private void OnApplicationQuit() => _quitting = true;

    protected override void OnEnable()
    {
        base.OnEnable();
        DateManager.Instance.OnDayPassed += HandleDayPassed;
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        if (DateManager.HasInstance) DateManager.Instance.OnDayPassed -= HandleDayPassed;
        if (_instance == this && _quitting) _instance = null;
    }

    private void HandleDayPassed()
    {
        for (int i = 0; i < _nations.Count; i++)
        {
            Nation n = _nations[i];
            if (!n.IsEmpty) n.CollectDailyIncome();
        }
        ProcessIncreases();
    }

    public override void Tick(float deltaTime)
    {
#if UNITY_EDITOR
        if (showNationsInInspector && Time.unscaledTime >= _nextInspectorRefresh)
        {
            _nextInspectorRefresh = Time.unscaledTime + inspectorRefreshSeconds;
            RefreshInspectorView();
        }
#endif
    }

#if UNITY_EDITOR
    private void RefreshInspectorView()
    {
        while (nationsView.Count > _nations.Count) nationsView.RemoveAt(nationsView.Count - 1);
        while (nationsView.Count < _nations.Count) nationsView.Add(new NationInfo());

        for (int i = 0; i < _nations.Count; i++)
        {
            Nation n = _nations[i];
            NationInfo v = nationsView[i];
            v.name = n.Name;
            v.id = n.Id;
            v.color = n.Color;
            v.provinces = n.ProvinceCount;
            v.area = n.Area;
            v.population = n.Population;
            v.economy = n.Economy;
            v.gdp = n.GDP;
            v.treasury = n.Treasury;
            v.minTreasury = n.MinTreasury;
            v.maxTreasury = n.MaxTreasury;
            v.capitalProvinceId = n.Capital != null ? n.Capital.Id : -1;
        }
    }
#endif

    // ─────────────────────────────────────────────────────────────────────
    //  INCREASE SYSTEM
    //   price = (basePrice + times * basePrice * inflation) * nation.UpgradeCostMultiplier
    //   times = increases of that stat already done + already queued in that province
    //   Paid when queued; refunded when cancelled. One increase at a time per province, daysPerIncrease each.
    // ─────────────────────────────────────────────────────────────────────

    public double GetIncreasePrice(Province p, StatType stat)
    {
        ProvinceUpgrades u = p.Upgrades;
        int s = (int)stat;
        float projected = p.GetStat(stat).Value + u.Queued[s] * upgrades.amountPerIncrease;
        double price = u.Price[s].Value + upgrades.inflation * projected;
        if (p.Owner != null) price *= p.Owner.UpgradeCostMultiplier.Value;
        return price;
    }

    public bool TryQueueIncrease(Province p, StatType stat, out string error)
    {
        error = null;
        if (p == null) { error = "No province."; return false; }
        Nation n = p.Owner;
        if (n == null) { error = "Province has no owner."; return false; }

        ProvinceUpgrades u = p.Upgrades;
        if (u.Queue.Count >= Mathf.Max(1, upgrades.maxQueue)) { error = "Queue is full."; return false; }

        double price = GetIncreasePrice(p, stat);
        if (!n.TrySpend(price)) { error = "Not enough treasury."; return false; }

        u.Queue.Add(new UpgradeJob(stat, price));
        u.Queued[(int)stat]++;
        if (u.Queue.Count == 1) u.DaysLeft = Mathf.Max(1, upgrades.daysPerIncrease);
        if (!u.IsActive) { u.IsActive = true; _active.Add(p); }

        OnIncreaseQueued?.Invoke(p, stat);
        return true;
    }

    public bool TryQueueIncrease(Province p, StatType stat) => TryQueueIncrease(p, stat, out _);

    public bool IncreaseTax(Province p) => TryQueueIncrease(p, StatType.Tax);
    public bool IncreaseEconomy(Province p) => TryQueueIncrease(p, StatType.Economy);
    public bool IncreaseDefend(Province p) => TryQueueIncrease(p, StatType.Defend);
    public bool IncreasePopGrowth(Province p) => TryQueueIncrease(p, StatType.PopGrowth);
    public bool IncreaseInfrastructure(Province p) => TryQueueIncrease(p, StatType.Infrastructure);

    /// <summary>Cancels the LAST queued increase and refunds its price (keeps later prices consistent).</summary>
    public bool CancelLastIncrease(Province p)
    {
        if (p == null || !p.HasUpgrades) return false;
        ProvinceUpgrades u = p.Upgrades;
        int last = u.Queue.Count - 1;
        UpgradeJob job = u.Queue[last];
        u.Queue.RemoveAt(last);
        u.Queued[(int)job.Stat]--;
        if (last == 0) { u.DaysLeft = 0; RemoveActive(p); }
        if (p.Owner != null) p.Owner.AddMoney(job.Price);
        return true;
    }

    private void CancelAllIncreases(Province p, bool refund)
    {
        ProvinceUpgrades u = p.Upgrades;
        double sum = 0;
        for (int i = 0; i < u.Queue.Count; i++) sum += u.Queue[i].Price;
        u.Queue.Clear();
        Array.Clear(u.Queued, 0, u.Queued.Length);
        u.DaysLeft = 0;
        RemoveActive(p);
        if (refund && p.Owner != null && sum > 0) p.Owner.AddMoney(sum);
    }

    private void RemoveActive(Province p)
    {
        ProvinceUpgrades u = p.Upgrades;
        if (!u.IsActive) return;
        u.IsActive = false;
        _active.Remove(p);
    }

    private void ProcessIncreases()
    {
        // backwards + swap-remove: safe and O(1) per finished province
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            Province p = _active[i];
            ProvinceUpgrades u = p.Upgrades;

            if (u.Queue.Count == 0 || p.Owner == null)
            {
                u.IsActive = false;
                _active[i] = _active[_active.Count - 1];
                _active.RemoveAt(_active.Count - 1);
                continue;
            }

            if (--u.DaysLeft > 0) continue;

            UpgradeJob job = u.Queue[0];
            u.Queue.RemoveAt(0);
            u.Queued[(int)job.Stat]--;
            u.Done[(int)job.Stat]++;
            p.GetStat(job.Stat).AddBase(upgrades.amountPerIncrease);   // marks the nation dirty
            OnIncreaseCompleted?.Invoke(p, job.Stat);

            if (u.Queue.Count > 0) u.DaysLeft = Mathf.Max(1, upgrades.daysPerIncrease);
            else
            {
                u.IsActive = false;
                _active[i] = _active[_active.Count - 1];
                _active.RemoveAt(_active.Count - 1);
            }
        }
    }

    // ── create / remove ──
    public Nation CreateNation(string name, Color color)
    {
        var nation = new Nation(_nextNationId++, name, color);
        _nations.Add(nation);
        _byId.Add(nation.Id, nation);
        OnNationCreated?.Invoke(nation);
        return nation;
    }

    public bool RemoveNation(int id)
    {
        if (!_byId.TryGetValue(id, out Nation n) || n.Provinces.Count > 0) return false;
        _byId.Remove(id);
        _nations.Remove(n);
        OnNationRemoved?.Invoke(n);
        return true;
    }

    public void ClearNations()
    {
        foreach (Nation n in _nations)
        {
            foreach (Province p in n.Provinces)
            {
                if (p.HasUpgrades) CancelAllIncreases(p, false);
                p.Owner = null; p.OwnerNationId = -1;
            }
            n.Provinces.Clear();
            n.Capital = null;
        }
        _active.Clear();
        _nations.Clear();
        _byId.Clear();
        _nextNationId = 0;
        OnNationsCleared?.Invoke();
    }

    // ── ownership ──
    public bool AssignProvince(Province province, Nation nation)
    {
        if (province == null) return false;
        if (nation != null && !(_byId.TryGetValue(nation.Id, out Nation reg) && reg == nation)) return false;
        if (province.Owner == nation) return true;

        if (province.HasUpgrades) CancelAllIncreases(province, true);   // refund the previous owner

        Nation old = province.Owner;
        if (old != null)
        {
            old.Provinces.Remove(province);
            old.MarkDirty();
            if (old.Capital == province) old.Capital = MostPopulated(old);
        }

        province.Owner = nation;
        province.OwnerNationId = nation != null ? nation.Id : -1;

        if (nation != null)
        {
            nation.Provinces.Add(province);
            nation.MarkDirty();
            if (nation.Capital == null) nation.Capital = province;
        }
        return true;
    }

    public bool SetCapital(Nation nation, Province province)
    {
        if (nation == null || province == null || province.Owner != nation) return false;
        nation.Capital = province;
        return true;
    }

    private static Province MostPopulated(Nation n)
    {
        Province best = null;
        for (int i = 0; i < n.Provinces.Count; i++)
            if (best == null || n.Provinces[i].Population > best.Population) best = n.Provinces[i];
        return best;
    }

    // ── queries ──
    public Nation GetNation(int id) => _byId.TryGetValue(id, out Nation n) ? n : null;
    public bool TryGetNation(int id, out Nation nation) => _byId.TryGetValue(id, out nation);

    public Nation GetSmallestNation(NationStat by = NationStat.Area, bool includeEmpty = false) => Extreme(by, includeEmpty, true);
    public Nation GetLargestNation(NationStat by = NationStat.Area, bool includeEmpty = false) => Extreme(by, includeEmpty, false);

    public Nation GetRandomNation(bool includeEmpty = false)
    {
        if (_nations.Count == 0) return null;
        if (includeEmpty) return _nations[UnityEngine.Random.Range(0, _nations.Count)];

        int count = 0;
        for (int i = 0; i < _nations.Count; i++) if (!_nations[i].IsEmpty) count++;
        if (count == 0) return null;

        int pick = UnityEngine.Random.Range(0, count);
        for (int i = 0; i < _nations.Count; i++)
        {
            if (_nations[i].IsEmpty) continue;
            if (pick-- == 0) return _nations[i];
        }
        return null;
    }

    public void RecalculateAll()
    {
        for (int i = 0; i < _nations.Count; i++) { _nations[i].MarkDirty(); _nations[i].RefreshIfDirty(); }
    }

    private Nation Extreme(NationStat by, bool includeEmpty, bool smallest)
    {
        Nation best = null;
        double bestValue = 0;
        for (int i = 0; i < _nations.Count; i++)
        {
            Nation n = _nations[i];
            if (!includeEmpty && n.IsEmpty) continue;
            double v = StatOf(n, by);
            if (best == null || (smallest ? v < bestValue : v > bestValue)) { best = n; bestValue = v; }
        }
        return best;
    }

    private static double StatOf(Nation n, NationStat stat)
    {
        switch (stat)
        {
            case NationStat.Provinces: return n.ProvinceCount;
            case NationStat.Population: return n.Population;
            case NationStat.Economy: return n.Economy;
            default: return n.Area;
        }
    }
}