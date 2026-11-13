using System;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
//  PLAYER MANAGER  (global access: PlayerManager.Instance)
//  Only decides WHICH nation is controlled by the player (Nation.IsPlayer).
//  Every other nation is AI (Nation.IsAI, set by AIManager).
// ─────────────────────────────────────────────────────────────────────────────
[DefaultExecutionOrder(-1050)]
public class PlayerManager : MonoBehaviour
{
    // ── singleton ──
    private static PlayerManager _instance;
    private static bool _quitting;
    public static bool HasInstance => _instance != null;

    public static PlayerManager Instance
    {
        get
        {
            if (_instance == null && !_quitting)
            {
#if UNITY_2022_2_OR_NEWER
                _instance = FindFirstObjectByType<PlayerManager>();
#else
                _instance = FindObjectOfType<PlayerManager>();
#endif
                if (_instance == null)
                    _instance = new GameObject("PlayerManager").AddComponent<PlayerManager>();
            }
            return _instance;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() { _instance = null; _quitting = false; }

    [Tooltip("Defaults to the MapManager found in the scene.")]
    [SerializeField] private MapManager map;
    [Tooltip("Nation id the player controls when the map is generated. -1 = random nation.")]
    [SerializeField] private int startNationId = -1;
    [Tooltip("ON: nobody is the player (every nation is AI, spectator mode).")]
    [SerializeField] private bool spectator = false;
    [Tooltip("Read-only mirror (Editor).")]
    [SerializeField] private string playerNationName;

    public Nation PlayerNation { get; private set; }
    public bool HasPlayer => PlayerNation != null;

    /// <summary>oldPlayerNation, newPlayerNation (either can be null)</summary>
    public event Action<Nation, Nation> OnPlayerChanged;

    public bool IsPlayer(Nation n) => n != null && n == PlayerNation;
    public bool IsAI(Nation n) => n != null && n != PlayerNation;

    private MapManager _subscribedMap;

    private void Awake()
    {
        if (_instance != null && _instance != this) { Destroy(this); return; }
        _instance = this;
    }

    private void OnApplicationQuit() => _quitting = true;

    private void OnEnable()
    {
        if (_instance != this) return;
        WorldManager w = WorldManager.Instance;
        w.OnNationsCleared += HandleCleared;
        w.OnNationRemoved += HandleRemoved;

        if (map == null)
        {
#if UNITY_2022_2_OR_NEWER
            map = FindFirstObjectByType<MapManager>();
#else
            map = FindObjectOfType<MapManager>();
#endif
        }
        if (map != null)
        {
            _subscribedMap = map;
            map.OnMapGenerated += PickStart;
            if (map.IsReady && PlayerNation == null) PickStart();
        }
    }

    private void OnDisable()
    {
        if (WorldManager.HasInstance)
        {
            WorldManager.Instance.OnNationsCleared -= HandleCleared;
            WorldManager.Instance.OnNationRemoved -= HandleRemoved;
        }
        if (_subscribedMap != null) _subscribedMap.OnMapGenerated -= PickStart;
        _subscribedMap = null;
        if (_instance == this && _quitting) _instance = null;
    }

    private void OnDestroy() { if (_instance == this) _instance = null; }

    private void PickStart()
    {
        if (spectator) { SetPlayer(null); return; }
        WorldManager w = WorldManager.Instance;
        Nation n = startNationId >= 0 ? w.GetNation(startNationId) : null;
        if (n == null || n.IsEmpty) n = w.GetRandomNation();
        SetPlayer(n);
    }

    /// <summary>Makes 'n' the player nation (null = no player). The old player nation becomes AI.</summary>
    public bool SetPlayer(Nation n)
    {
        if (n != null && !(WorldManager.Instance.TryGetNation(n.Id, out Nation reg) && reg == n)) return false;
        Nation old = PlayerNation;
        if (old == n) return true;

        if (old != null) { old.IsPlayer = false; old.IsAI = true; }
        PlayerNation = n;
        if (n != null) { n.IsPlayer = true; n.IsAI = false; }
        playerNationName = n != null ? n.Name : "";
        OnPlayerChanged?.Invoke(old, n);
        return true;
    }

    public void ClearPlayer() => SetPlayer(null);

    private void HandleCleared()
    {
        PlayerNation = null;
        playerNationName = "";
    }

    private void HandleRemoved(Nation n)
    {
        if (n == PlayerNation) { n.IsPlayer = false; PlayerNation = null; playerNationName = ""; OnPlayerChanged?.Invoke(n, null); }
    }

    [ContextMenu("Player: Pick Random Nation")]
    private void MenuRandom() => SetPlayer(WorldManager.Instance.GetRandomNation());

    [ContextMenu("Player: Set Nation (startNationId)")]
    private void MenuSet() => SetPlayer(WorldManager.Instance.GetNation(startNationId));

    [ContextMenu("Player: No Player (spectator)")]
    private void MenuNone() => SetPlayer(null);
}