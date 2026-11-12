using UnityEngine;

/// <summary>
/// Subscribes to EventBus.OnLongPress (and optionally OnTap), converts the screen position into a province
/// and raises EventBus.OnProvinceLongPressed so UI / gameplay can react.
/// </summary>
public class ProvinceSelector : MonoBehaviour
{
    [SerializeField] private Camera cam;
    [Tooltip("Leave empty to find the MapManager in the scene automatically.")]
    [SerializeField] private MapManager map;
    [SerializeField] private bool selectOnTap = false;
    [SerializeField] private bool logSelection = true;

    private void OnEnable()
    {
        if (cam == null) cam = Camera.main;
        if (map == null)
        {
#if UNITY_2022_2_OR_NEWER
            map = FindFirstObjectByType<MapManager>();
#else
            map = FindObjectOfType<MapManager>();
#endif
        }
        EventBus.OnLongPress += Select;
        if (selectOnTap) EventBus.OnTap += Select;
    }

    private void OnDisable()
    {
        EventBus.OnLongPress -= Select;
        EventBus.OnTap -= Select;
    }

    private void Select(Vector2 screenPos)
    {
        if (cam == null) return;

        Vector3 w = cam.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, -cam.transform.position.z));
        w.z = 0f;

        Province p = (map != null && map.IsReady) ? map.GetProvinceAt(w) : null;
        EventBus.RaiseProvinceLongPressed(w, p);

        if (logSelection) Log(w, p);
    }

    private void Log(Vector3 w, Province p)
    {
        if (map == null || !map.IsReady) { Debug.Log("[Select] map is not ready"); return; }
        if (!map.TryWorldToTile(w, out int x, out int y)) { Debug.Log("[Select] " + w + " is outside the map"); return; }
        if (p == null) { Debug.Log("[Select] tile (" + x + "," + y + ") = " + map.GetTileType(x, y) + " (no province)"); return; }

        Nation n = p.Owner;
        Debug.Log(
            "[Select] tile (" + x + "," + y + ")  " + p.Name + " (#" + p.Id + ")" + (p.IsCapital ? " (CAPITAL)" : "") + "\n" +
            "Owner: " + (n != null ? n.Name + " (id " + n.Id + ", provinces " + n.ProvinceCount + ", treasury " + n.Treasury.ToString("F0") + ")" : "Neutral") + "\n" +
            "Tiles: " + p.TileCount + "  Neighbors: " + p.Neighbors.Length + "\n" +
            "Population: " + p.Population.ToString("F0") + "  PopGrowth: " + p.PopGrowth.Value.ToString("F3") + "\n" +
            "Tax: " + p.Tax.Value.ToString("F2") + "  Economy: " + p.Economy.Value.ToString("F2") + "  Defend: " + p.Defend.Value.ToString("F2") + "\n" +
            "Infrastructure: " + p.Infrastructure.Value.ToString("F1") + "  Efficiency: " + p.ProductionEfficiency.ToString("F2") +
            "  Devastation: " + p.Devastation.ToString("F2") + "\n" +
            "Income: " + p.Income.ToString("F1") + "  Queued increases: " + (p.HasUpgrades ? p.Upgrades.Queue.Count.ToString() : "0"));
    }
}