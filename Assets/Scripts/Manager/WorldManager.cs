using System;
using System.Collections.Generic;
using UnityEngine;

#region Data types

public enum StatType : byte { Tax = 0, Economy = 1, Defend = 2, PopGrowth = 3, Infrastructure = 4 }

public enum NationStat { Area, Provinces, Population, Economy }

public struct UpgradeJob
{
    public StatType Stat;
    public double Price;     // what was paid (refunded on cancel)
    public UpgradeJob(StatType s, double p) { Stat = s; Price = p; }
}

/// <summary>What one province currently contributes to its nation's cached totals.</summary>
internal struct NationContribution
{
    public double Econ, Pop, Dev, Def, Infra;
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

    /// <summary>Index inside WorldManager's active list, -1 when not active (O(1) removal).</summary>
    internal int ActiveIndex = -1;
    internal bool IsActive => ActiveIndex >= 0;

    public ProvinceUpgrades(float basePrice)
    {
        for (int i = 0; i < StatCount; i++) Price[i] = new GameValue(basePrice);
    }
}

/// <summary>Rules of the "Increase" system (all editable in the WorldManager Inspector).</summary>
[Serializable]
public class UpgradeSettings
{
    [Tooltip("Starting price of every stat in every province.")]
    public float basePrice = 50f;
    [Tooltip("Global inflation: price += inflation * the province's stat value. Tax 0.5 * 5 = +2.5.")]
    public float inflation = 5f;
    [Tooltip("Days one increase takes. Queued increases run one after another.")]
    public int daysPerIncrease = 90;
    [Tooltip("Max increases waiting in one province's queue (including the running one).")]
    public int maxQueue = 100;
    [Tooltip("How much Base a completed increase adds (Economy 1.1 -> 2.1).")]
    public float amountPerIncrease = 1f;
}

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

#endregion

#region Province

public class Province : IDirtyable
{
    #region Identity / geometry

    public int Id;
    public string Name = "";
    public int OwnerNationId = -1;               // nation's independent Id, -1 = neutral
    public Nation Owner { get; internal set; }   // set by WorldManager.AssignProvince
    public int[] Tiles;                          // CLUSTER of tile indices (y * width + x)
    public int TileCount => Tiles.Length;
    public Vector2 Centroid;                     // tile coordinates
    public int[] Neighbors = Array.Empty<int>(); // ids of provinces sharing a land border
    public Vector2Int Min, Max;                  // tile bounding box (inclusive)
    public float Shade = 1f;                     // small brightness variation so provinces look different
    public Color32 Color;                        // cached display color

    /// <summary>True when this province was merged into another one (dead stub, ignore it).</summary>
    public bool IsMerged { get; internal set; }
    public int MergedInto = -1;

    public bool IsCapital => Owner != null && Owner.Capital == this;

    #endregion

    #region Stats

    // GameValue: (Base + Flat + Base * Percent) * Total
    public readonly GameValue Tax = new GameValue();
    public readonly GameValue Economy = new GameValue();
    public readonly GameValue Defend = new GameValue();
    public readonly GameValue PopGrowth = new GameValue();
    public readonly GameValue Infrastructure = new GameValue();
    /// <summary>"Production Income": multiplier of the whole income (Base 1 = 100%).</summary>
    public readonly GameValue ProductionIncome = new GameValue(1f);

    private float _devastation, _population;

    public float Population
    {
        get => _population;
        set { _population = Mathf.Max(0f, value); MarkDirty(); }
    }

    /// <summary>0..1. Reduces the province income (1 = income is zero).</summary>
    public float Devastation
    {
        get => _devastation;
        set { _devastation = Mathf.Clamp01(value); MarkDirty(); }
    }

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

    #endregion

    #region Income (cached)

    private bool _stale = true;   // cached income needs a recompute
    private float _income;

    // Bookkeeping used by Nation to update its totals incrementally.
    internal NationContribution Accounted;
    internal Nation DirtyIn;      // the nation whose dirty list currently holds this province

    /// <summary>Production Efficiency = 1 (base 100%) + Infrastructure * 0.03.</summary>
    public float ProductionEfficiency => 1f + Infrastructure.Value * 0.03f;

    /// <summary>Income = ProductionEfficiency * (Tax * Population + Economy) * ProductionIncome * (1 - Devastation). Cached until a stat changes.</summary>
    public float Income
    {
        get
        {
            if (_stale)
            {
                _income = ProductionEfficiency * (Tax.Value * _population + Economy.Value)
                          * ProductionIncome.Value * (1f - _devastation);
                _stale = false;
            }
            return _income;
        }
    }

    internal NationContribution Snapshot()
    {
        NationContribution c;
        c.Econ = Income;
        c.Pop = _population;
        c.Dev = _devastation;
        c.Def = Defend.Value;
        c.Infra = Infrastructure.Value;
        return c;
    }

    public void MarkDirty()
    {
        _stale = true;
        Owner?.NotifyProvinceDirty(this);
    }

    #endregion

    #region Upgrades

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

    #endregion
}

#endregion

#region Nation

public class Nation : IDirtyable
{
    #region Identity

    /// <summary>Unique, stable ID. Independent of the list position.</summary>
    public int Id { get; }
    public string Name;
    public Color Color;
    public readonly List<Province> Provinces = new List<Province>();

    /// <summary>Capital province (starts with double population and defend). If lost, the most populated remaining province takes over.</summary>
    public Province Capital { get; internal set; }

    public bool IsPlayer { get; internal set; }
    public bool IsAI { get; internal set; }

    public int ProvinceCount => Provinces.Count;
    public bool IsEmpty => Provinces.Count == 0;
    public bool IsExtinct => Provinces.Count == 0;

    /// <summary>Multiplier of the daily income (Base 1 = 100%).</summary>
    public readonly GameValue GdpMultiplier = new GameValue(1f);
    /// <summary>Multiplier of every increase price this nation pays (Base 1 = 100%).</summary>
    public readonly GameValue UpgradeCostMultiplier = new GameValue(1f);

    internal Nation(int id, string name, Color color)
    {
        Id = id; Name = name; Color = color;
        GdpMultiplier.Bind(this);
        UpgradeCostMultiplier.Bind(this);
    }

    #endregion

    #region Cached aggregates (incremental)

    // Totals are kept up to date by applying only the DELTA of provinces that changed,
    // so a refresh costs O(changed provinces) instead of O(all provinces).
    private bool _fullRebuild = true;
    private readonly List<Province> _dirtyList = new List<Province>(16);
    private int _area;
    private double _economy, _population, _devSum, _defSum, _infraSum;

    public int Area { get { Refresh(); return _area; } }
    public double Economy { get { Refresh(); return _economy; } }
    public double Population { get { Refresh(); return _population; } }
    public float AverageDevastation { get { Refresh(); return Provinces.Count > 0 ? (float)(_devSum / Provinces.Count) : 0f; } }
    public float AverageDefend { get { Refresh(); return Provinces.Count > 0 ? (float)(_defSum / Provinces.Count) : 0f; } }
    public float AverageInfrastructure { get { Refresh(); return Provinces.Count > 0 ? (float)(_infraSum / Provinces.Count) : 0f; } }

    /// <summary>Forces a full rebuild on the next read (used by nation-level modifiers and RecalculateAll).</summary>
    public void MarkDirty() => _fullRebuild = true;
    internal void RefreshIfDirty() => Refresh();

    internal void NotifyProvinceDirty(Province p)
    {
        if (_fullRebuild || p.DirtyIn == this) return;
        p.DirtyIn = this;
        _dirtyList.Add(p);
    }

    internal void Attach(Province p)
    {
        Provinces.Add(p);
        _area += p.TileCount;
        p.Accounted = default;
        p.DirtyIn = null;
        p.MarkDirty();   // Owner is already this nation -> queued for the next delta refresh
    }

    internal void Detach(Province p)
    {
        if (!Provinces.Remove(p)) return;
        _area -= p.TileCount;
        AddContribution(p.Accounted, -1.0);
        p.Accounted = default;
    }

    private void Refresh()
    {
        if (_fullRebuild) RebuildAll();
        else if (_dirtyList.Count > 0) ApplyDirty();
        else return;

        Treasury = Math.Min(_economy, Math.Max(-_economy, Treasury));
    }

    private void RebuildAll()
    {
        _fullRebuild = false;
        _area = 0;
        _economy = _population = _devSum = _defSum = _infraSum = 0.0;

        for (int i = 0; i < Provinces.Count; i++)
        {
            Province p = Provinces[i];
            if (p.DirtyIn == this) p.DirtyIn = null;
            p.Accounted = p.Snapshot();
            _area += p.TileCount;
            AddContribution(p.Accounted, 1.0);
        }
        _dirtyList.Clear();
    }

    private void ApplyDirty()
    {
        for (int i = 0; i < _dirtyList.Count; i++)
        {
            Province p = _dirtyList[i];
            if (p.DirtyIn == this) p.DirtyIn = null;
            if (p.Owner != this) continue;

            AddContribution(p.Accounted, -1.0);
            p.Accounted = p.Snapshot();
            AddContribution(p.Accounted, 1.0);
        }
        _dirtyList.Clear();
    }

    private void AddContribution(in NationContribution c, double sign)
    {
        _economy += c.Econ * sign;
        _population += c.Pop * sign;
        _devSum += c.Dev * sign;
        _defSum += c.Def * sign;
        _infraSum += c.Infra * sign;
    }

    #endregion

    #region Treasury

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

    internal void CollectDailyIncome(double invDaysPerYear)
    {
        double e = Economy;   // single refresh
        double t = Treasury + e * GdpMultiplier.Value * invDaysPerYear;
        Treasury = Math.Min(e, Math.Max(-e, t));
    }

    #endregion

    #region Population

    /// <summary>Every manpower that dies removes 1 population, spread over provinces by population.</summary>
    public void LoseMen(double men)
    {
        if (men <= 0.0 || Provinces.Count == 0) return;
        double total = Population;               // cached, no extra pass
        if (total <= 0.0) return;

        // pop - loss * (pop / total)  ==  pop * (1 - loss / total): one multiply per province
        float keep = (float)(1.0 - Math.Min(men, total) / total);
        for (int i = 0; i < Provinces.Count; i++)
            Provinces[i].Population *= keep;
    }

    #endregion
}

#endregion

#region World manager

[DefaultExecutionOrder(-1000)]
public class WorldManager : TickBehaviour
{
    #region Singleton

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

    #endregion

    #region Fields & events

    private readonly List<Nation> _nations = new List<Nation>();
    private readonly Dictionary<int, Nation> _byId = new Dictionary<int, Nation>();
    private int _nextNationId;

    [Header("Increase system (Tax / Economy / Defend / ...)")]
    public UpgradeSettings upgrades = new UpgradeSettings();
    private readonly List<Province> _active = new List<Province>();   // provinces with a running increase

    [Header("Population growth")]
    [Min(1)] public int popGrowthYears = 5;
    public float popGrowthBase = 5000f;
    [Min(1), Tooltip("Provinces processed per frame when yearly growth is applied (spreads the work over frames).")]
    public int growthProvincesPerFrame = 512;

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

    #endregion

    #region Unity lifecycle

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
        DateManager.Instance.OnYearPassed += HandleYearPassed;
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        if (DateManager.HasInstance)
        {
            DateManager.Instance.OnDayPassed -= HandleDayPassed;
            DateManager.Instance.OnYearPassed -= HandleYearPassed;
        }
        if (_instance == this && _quitting) _instance = null;
    }

    public override void Tick(float deltaTime)
    {
        if (_growActive) ProcessGrowthSlice(Mathf.Max(1, growthProvincesPerFrame));

#if UNITY_EDITOR
        if (showNationsInInspector && Time.unscaledTime >= _nextInspectorRefresh)
        {
            _nextInspectorRefresh = Time.unscaledTime + inspectorRefreshSeconds;
            RefreshInspectorView();
        }
#endif
    }

    #endregion

    #region Daily / yearly handlers

    private void HandleDayPassed()
    {
        double inv = 1.0 / DateManager.EconomyDaysPerYear;
        for (int i = 0; i < _nations.Count; i++)
        {
            Nation n = _nations[i];
            if (!n.IsEmpty) n.CollectDailyIncome(inv);
        }
        ProcessIncreases();
    }

    private int _yearCounter;

    private void HandleYearPassed()
    {
        if (++_yearCounter < Mathf.Max(1, popGrowthYears)) return;
        _yearCounter = 0;
        BeginPopulationGrowth();
    }

    #endregion

    #region Population growth (time-sliced)

    private bool _growActive;
    private int _growNation, _growProv;

    /// <summary>Starts the yearly growth; the work is spread over several frames (see growthProvincesPerFrame).</summary>
    private void BeginPopulationGrowth()
    {
        if (_growActive) ProcessGrowthSlice(int.MaxValue);   // finish the previous pass first
        _growNation = 0;
        _growProv = 0;
        _growActive = true;
    }

    /// <summary>Each province: Population += popGrowthBase * PopGrowth. Applied immediately for all provinces.</summary>
    public void GrowPopulation()
    {
        _growNation = 0;
        _growProv = 0;
        _growActive = true;
        ProcessGrowthSlice(int.MaxValue);
    }

    private void ProcessGrowthSlice(int budget)
    {
        while (budget > 0 && _growNation < _nations.Count)
        {
            List<Province> list = _nations[_growNation].Provinces;
            if (_growProv >= list.Count) { _growNation++; _growProv = 0; continue; }

            Province p = list[_growProv++];
            p.Population += popGrowthBase * p.PopGrowth.Value;
            budget--;
        }
        if (_growNation >= _nations.Count) _growActive = false;
    }

    #endregion

    #region Increase system

    // price = (basePrice + times * basePrice * inflation) * nation.UpgradeCostMultiplier
    // times = increases of that stat already done + already queued in that province
    // Paid when queued; refunded when cancelled. One increase at a time per province, daysPerIncrease each.

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
        AddActive(p, u);

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
        if (last == 0) { u.DaysLeft = 0; RemoveActive(u); }
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
        RemoveActive(u);
        if (refund && p.Owner != null && sum > 0) p.Owner.AddMoney(sum);
    }

    private void ProcessIncreases()
    {
        // backwards + swap-remove: safe and O(1) per finished province
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            if (i >= _active.Count) continue;   // a callback may have shrunk the list

            Province p = _active[i];
            ProvinceUpgrades u = p.Upgrades;

            if (u.Queue.Count == 0 || p.Owner == null) { SwapRemoveActive(i); continue; }
            if (--u.DaysLeft > 0) continue;

            UpgradeJob job = u.Queue[0];
            u.Queue.RemoveAt(0);
            u.Queued[(int)job.Stat]--;
            u.Done[(int)job.Stat]++;
            p.GetStat(job.Stat).AddBase(upgrades.amountPerIncrease);   // marks the province dirty
            OnIncreaseCompleted?.Invoke(p, job.Stat);

            if (u.Queue.Count > 0) u.DaysLeft = Mathf.Max(1, upgrades.daysPerIncrease);
            else if (u.ActiveIndex >= 0) SwapRemoveActive(u.ActiveIndex);
        }
    }

    #endregion

    #region Active list (O(1) add/remove)

    private void AddActive(Province p, ProvinceUpgrades u)
    {
        if (u.IsActive) return;
        u.ActiveIndex = _active.Count;
        _active.Add(p);
    }

    private void RemoveActive(ProvinceUpgrades u)
    {
        if (u.IsActive) SwapRemoveActive(u.ActiveIndex);
    }

    private void SwapRemoveActive(int index)
    {
        int last = _active.Count - 1;
        _active[index].Upgrades.ActiveIndex = -1;
        if (index != last)
        {
            Province moved = _active[last];
            _active[index] = moved;
            moved.Upgrades.ActiveIndex = index;
        }
        _active.RemoveAt(last);
    }

    #endregion

    #region Create / remove nations

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
        for (int i = 0; i < _nations.Count; i++)
        {
            Nation n = _nations[i];
            for (int j = 0; j < n.Provinces.Count; j++)
            {
                Province p = n.Provinces[j];
                if (p.HasUpgrades)
                {
                    // no per-province list removal needed: _active is cleared below
                    ProvinceUpgrades u = p.Upgrades;
                    u.Queue.Clear();
                    Array.Clear(u.Queued, 0, u.Queued.Length);
                    u.DaysLeft = 0;
                    u.ActiveIndex = -1;
                }
                p.Owner = null;
                p.OwnerNationId = -1;
                p.Accounted = default;
                p.DirtyIn = null;
            }
            n.Provinces.Clear();
            n.Capital = null;
            n.MarkDirty();
        }
        _active.Clear();
        _nations.Clear();
        _byId.Clear();
        _nextNationId = 0;
        _yearCounter = 0;
        _growActive = false;
        OnNationsCleared?.Invoke();
    }

    #endregion

    #region Ownership

    public bool AssignProvince(Province province, Nation nation)
    {
        if (province == null) return false;
        if (nation != null && !(_byId.TryGetValue(nation.Id, out Nation reg) && reg == nation)) return false;
        if (province.Owner == nation) return true;

        if (province.HasUpgrades) CancelAllIncreases(province, true);   // refund the previous owner

        Nation old = province.Owner;
        if (old != null)
        {
            old.Detach(province);
            if (old.Capital == province) old.Capital = MostPopulated(old);
        }

        province.Owner = nation;
        province.OwnerNationId = nation != null ? nation.Id : -1;

        if (nation != null)
        {
            nation.Attach(province);
            if (nation.Capital == null) nation.Capital = province;
        }
        return true;
    }

    /// <summary>Removes a province from its owner (queued increases refunded). Used by province merging.</summary>
    internal void DetachProvince(Province p)
    {
        if (p == null) return;
        if (p.HasUpgrades) CancelAllIncreases(p, true);
        p.Owner?.Detach(p);
        p.Owner = null;
        p.OwnerNationId = -1;
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

    #endregion

    #region Queries

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

    /// <summary>Full rebuild of every nation's cached totals (also clears any floating point drift).</summary>
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

    #endregion

    #region Inspector mirror (Editor only)

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

    #endregion
}

#endregion