using System;
using UnityEngine;

/// <summary>
/// In-game calendar. 1 day = 1 game-second (follows TickManager game speed + pause).
/// 30 days = 1 month, 12 months = 1 year. Default start date: 1/1/5000.
/// Read the date with Day / Month / Year. Subscribe to OnDayPassed / OnMonthPassed / OnYearPassed for daily logic.
/// </summary>
[DefaultExecutionOrder(-1100)]
public class DateManager : TickBehaviour
{
    public const int DaysPerMonth = 30;
    public const int MonthsPerYear = 12;
    /// <summary>Economy is paid out over this many days (GDP = Economy / this). Not the same as DaysPerMonth * MonthsPerYear on purpose.</summary>
    public const int EconomyDaysPerYear = 365;

    private const int MaxDaysPerFrame = 31;      // protection: never process more than ~1 month in one frame

    [Header("Start date")]
    [SerializeField, Min(1)] private int startDay = 1;
    [SerializeField, Min(1)] private int startMonth = 1;
    [SerializeField] private int startYear = 5000;

    [Header("Speed")]
    [Tooltip("Game-seconds per in-game day (1 = one day per second at game speed x1).")]
    [SerializeField, Min(0.01f)] private float secondsPerDay = 1f;

    [Header("Current date (read-only mirror, Editor only)")]
    [SerializeField] private string currentDate;

    // ── singleton ──
    private static DateManager _instance;
    private static bool _quitting;

    public static bool HasInstance => _instance != null;

    public static DateManager Instance
    {
        get
        {
            if (_instance == null && !_quitting)
            {
#if UNITY_2022_2_OR_NEWER
                _instance = FindFirstObjectByType<DateManager>();
#else
                _instance = FindObjectOfType<DateManager>();
#endif
                if (_instance == null)
                    _instance = new GameObject("DateManager").AddComponent<DateManager>();
            }
            return _instance;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() { _instance = null; _quitting = false; }

    // ── date ──
    public int Day { get; private set; } = 1;       // 1..30
    public int Month { get; private set; } = 1;     // 1..12
    public int Year { get; private set; } = 5000;

    /// <summary>Absolute day number (handy for comparing dates / measuring durations).</summary>
    public long TotalDays => ToTotalDays(Day, Month, Year);

    public event Action OnDayPassed;
    public event Action OnMonthPassed;
    public event Action OnYearPassed;

    private float _accumulator;

    protected override int TickPriority => -1100;

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Debug.LogWarning("Duplicate DateManager removed.");
            Destroy(this);
            return;
        }
        _instance = this;
        SetDate(startDay, startMonth, startYear);
    }

    private void OnApplicationQuit() => _quitting = true;

    protected override void OnDisable()
    {
        base.OnDisable();
        if (_instance == this && _quitting) _instance = null;
    }

    public override void Tick(float deltaTime)
    {
        // deltaTime is already scaled by game speed and is 0 / skipped while paused
        _accumulator += deltaTime;

        float spd = Mathf.Max(0.01f, secondsPerDay);
        int guard = 0;
        while (_accumulator >= spd && guard++ < MaxDaysPerFrame)
        {
            _accumulator -= spd;
            AdvanceDay();
        }
        if (_accumulator > spd) _accumulator = spd;      // dropped backlog (very high speed + lag)
    }

    public void SetDate(int day, int month, int year)
    {
        Day = Mathf.Clamp(day, 1, DaysPerMonth);
        Month = Mathf.Clamp(month, 1, MonthsPerYear);
        Year = year;
        _accumulator = 0f;
        UpdateView();
    }

    public static long ToTotalDays(int day, int month, int year) =>
        ((long)year * MonthsPerYear + (month - 1)) * DaysPerMonth + (day - 1);

    public override string ToString() => Day + "/" + Month + "/" + Year;

    private void AdvanceDay()
    {
        bool newMonth = false, newYear = false;

        if (++Day > DaysPerMonth)
        {
            Day = 1;
            newMonth = true;
            if (++Month > MonthsPerYear)
            {
                Month = 1;
                Year++;
                newYear = true;
            }
        }

        UpdateView();

        // one failing listener must not stop the calendar or the other listeners
        Raise(OnDayPassed);
        if (newMonth) Raise(OnMonthPassed);
        if (newYear) Raise(OnYearPassed);
    }

    private static void Raise(Action a)
    {
        if (a == null) return;
        foreach (Action d in a.GetInvocationList())
        {
            try { d(); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }

    private void UpdateView()
    {
#if UNITY_EDITOR
        currentDate = ToString();
#endif
    }
}