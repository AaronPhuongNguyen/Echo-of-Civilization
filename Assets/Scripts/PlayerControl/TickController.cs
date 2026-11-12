using System;
using UnityEngine;

/// <summary>
/// Pause / Resume and Speed Up / Down on top of TickManager.
/// Contains NO input code: it subscribes to EventBus.OnTogglePause / OnSpeedUp / OnSpeedDown
/// (raised by GameController hotkeys or by UI buttons). The public methods can also be wired to UI onClick.
/// </summary>
public class TimeController : MonoBehaviour
{
    [Header("Speed steps (ascending, limited by TickManager max speed)")]
    [SerializeField] private float[] speedSteps = { 0.5f, 1f, 2f, 3f, 5f };

    [Header("State (read-only mirror, Editor only)")]
    [SerializeField] private string state;

    private void OnValidate()
    {
        if (speedSteps == null || speedSteps.Length == 0) speedSteps = new[] { 1f };
        for (int i = 0; i < speedSteps.Length; i++) speedSteps[i] = Mathf.Max(0.05f, speedSteps[i]);
        Array.Sort(speedSteps);
    }

    private void OnEnable()
    {
        EventBus.OnTogglePause += TogglePause;
        EventBus.OnSpeedUp += SpeedUp;
        EventBus.OnSpeedDown += SpeedDown;
        TickManager.OnPausedChanged += OnPausedChanged;
        TickManager.OnGameSpeedChanged += OnSpeedChanged;
        RefreshState();
    }

    private void OnDisable()
    {
        EventBus.OnTogglePause -= TogglePause;
        EventBus.OnSpeedUp -= SpeedUp;
        EventBus.OnSpeedDown -= SpeedDown;
        TickManager.OnPausedChanged -= OnPausedChanged;
        TickManager.OnGameSpeedChanged -= OnSpeedChanged;
    }

    [ContextMenu("Pause")] public void Pause() => TickManager.Pause();
    [ContextMenu("Resume")] public void Resume() => TickManager.Resume();
    [ContextMenu("Toggle Pause")] public void TogglePause() => TickManager.TogglePause();

    [ContextMenu("Speed Up")]
    public void SpeedUp()
    {
        float cur = TickManager.GameSpeed;
        for (int i = 0; i < speedSteps.Length; i++)
        {
            if (speedSteps[i] > cur + 0.001f)
            {
                TickManager.GameSpeed = Mathf.Min(speedSteps[i], TickManager.MaxGameSpeed);
                return;
            }
        }
    }

    [ContextMenu("Speed Down")]
    public void SpeedDown()
    {
        float cur = TickManager.GameSpeed;
        for (int i = speedSteps.Length - 1; i >= 0; i--)
        {
            if (speedSteps[i] < cur - 0.001f)
            {
                TickManager.GameSpeed = speedSteps[i];
                return;
            }
        }
    }

    [ContextMenu("Reset Speed (x1)")] public void ResetSpeed() => TickManager.GameSpeed = 1f;
    public void SetSpeed(float speed) => TickManager.GameSpeed = speed;

    [ContextMenu("Log State")]
    private void LogState()
    {
        string date = DateManager.HasInstance ? DateManager.Instance.ToString() : "-";
        Debug.Log("Paused: " + TickManager.Paused + " | Speed: x" + TickManager.GameSpeed +
                  " | Date: " + date + " | GameTime: " + TickManager.GameTime.ToString("F1") + "s");
    }

    private void OnPausedChanged(bool paused) => RefreshState();
    private void OnSpeedChanged(float speed) => RefreshState();

    private void RefreshState()
    {
#if UNITY_EDITOR
        state = (TickManager.Paused ? "PAUSED  " : "Running ") + "x" + TickManager.GameSpeed;
#endif
    }
}