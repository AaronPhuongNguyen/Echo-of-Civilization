using UnityEngine;

/// <summary>
/// Inspector tool to view / edit one province's GameValues at runtime.
/// Long-press a province in Play mode (it auto-loads), edit Base / Flat / Percent / Total, then
/// right-click the component header -> "Apply". Also lets you queue / cancel increases for testing.
/// </summary>
public class ProvinceDebugger : MonoBehaviour
{
    [SerializeField] private MapManager map;
    [SerializeField] private int provinceId;
    [SerializeField] private StatType increaseStat = StatType.Economy;
    [SerializeField] private bool followLongPress = true;

    [Header("Loaded values (edit, then Apply)")]
    public string provinceName;
    public float population;
    [Range(0f, 1f)] public float devastation;
    public GameValue tax = new GameValue();
    public GameValue economy = new GameValue();
    public GameValue defend = new GameValue();
    public GameValue popGrowth = new GameValue();
    public GameValue infrastructure = new GameValue();
    public GameValue productionIncome = new GameValue(1f);

    [Header("Read-only info")]
    [SerializeField] private string info;

    private void OnEnable()
    {
        if (map == null)
        {
#if UNITY_2022_2_OR_NEWER
            map = FindFirstObjectByType<MapManager>();
#else
            map = FindObjectOfType<MapManager>();
#endif
        }
        EventBus.OnProvinceLongPressed += HandleSelected;
    }

    private void OnDisable() => EventBus.OnProvinceLongPressed -= HandleSelected;

    private void HandleSelected(Vector2 world, Province p)
    {
        if (!followLongPress || p == null) return;
        provinceId = p.Id;
        Load();
    }

    private Province Get() => map != null && map.IsReady ? map.GetProvince(provinceId) : null;

    [ContextMenu("Load")]
    public void Load()
    {
        Province p = Get();
        if (p == null) { info = "Province not found / map not ready"; return; }

        provinceName = p.Name;
        population = p.Population;
        devastation = p.Devastation;
        tax.CopyFrom(p.Tax); economy.CopyFrom(p.Economy); defend.CopyFrom(p.Defend);
        popGrowth.CopyFrom(p.PopGrowth); infrastructure.CopyFrom(p.Infrastructure);
        productionIncome.CopyFrom(p.ProductionIncome);
        RefreshInfo(p);
    }

    [ContextMenu("Apply")]
    public void Apply()
    {
        Province p = Get();
        if (p == null) return;

        p.Name = provinceName;
        p.Population = population;
        p.Devastation = devastation;
        p.Tax.CopyFrom(tax); p.Economy.CopyFrom(economy); p.Defend.CopyFrom(defend);
        p.PopGrowth.CopyFrom(popGrowth); p.Infrastructure.CopyFrom(infrastructure);
        p.ProductionIncome.CopyFrom(productionIncome);
        p.MarkDirty();
        RefreshInfo(p);
    }

    [ContextMenu("Queue Increase")]
    public void QueueIncrease()
    {
        Province p = Get();
        if (p == null) return;
        WorldManager.Instance.TryQueueIncrease(p, increaseStat, out string error);
        info = error ?? "Queued " + increaseStat;
        RefreshInfo(p, error);
    }

    [ContextMenu("Cancel Last Increase")]
    public void CancelLast()
    {
        Province p = Get();
        if (p == null) return;
        WorldManager.Instance.CancelLastIncrease(p);
        RefreshInfo(p);
    }

    private void RefreshInfo(Province p, string note = null)
    {
        WorldManager w = WorldManager.Instance;
        string q = p.HasUpgrades ? p.Upgrades.Queue.Count + " queued, " + p.Upgrades.DaysLeft + " days left on the running one" : "no queue";
        info = (note != null ? note + " | " : "") + "Income " + p.Income.ToString("F1") + " | " + q +
               " | next " + increaseStat + " price " + w.GetIncreasePrice(p, increaseStat).ToString("F1");
    }
}