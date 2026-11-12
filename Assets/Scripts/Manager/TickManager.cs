using System;
using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
//  INTERFACES
// ─────────────────────────────────────────────────────────────────────────────


/// <summary>Called at a FIXED rate (TickManager.SimTickRate per game-second). Use for RTS logic: income, AI, combat, movement steps.</summary>




// ─────────────────────────────────────────────────────────────────────────────
//  TICK MANAGER
//
//  - One Update() drives everything (no per-object Update overhead).
//  - Static API: works from anywhere, auto-creates its own GameObject (or use one from the scene).
//  - Safe: add/remove during a tick is fine, destroyed objects are skipped and unregistered,
//    one exception does not stop the other tickers.
//  - Game speed + pause, fixed-rate simulation ticks, and timers (Schedule) that follow game speed.
//  - Uses Time.unscaledDeltaTime and its own speed, so don't mix it with Time.timeScale.
// ─────────────────────────────────────────────────────────────────────────────

[DefaultExecutionOrder(-100)]
public class TickManager : MonoBehaviour
{
    #region Inspector (applied once when a TickManager placed in the scene wakes up)
    [Header("Time")]
    [SerializeField] private float startGameSpeed = 1f;
    [SerializeField] private bool startPaused=true;
    [SerializeField] private float maxGameSpeed = 5f;
    [Tooltip("Frame time is clamped to this (phone lag spikes / resume from background).")]
    [SerializeField] private float maxFrameTime = 0.1f;

    [Header("Simulation (fixed rate)")]
    [SerializeField] private int simTickRate = 10;                 // ticks per game-second
    [SerializeField] private int maxSimStepsPerFrame = 5;          // protection against the "spiral of death"
    #endregion

    // ── static state (so the manager survives reloads / duplicates) ──
    private static TickManager _runner;
    private static bool _auto, _creating, _quitting;

    private static readonly Registry<ITick> Ticks = new Registry<ITick>();
    private static readonly Registry<ISimTick> SimTicks = new Registry<ISimTick>();
    private static readonly Action<ITick, float> CallTick = (t, dt) => t.Tick(dt);
    private static readonly Action<ISimTick, float> CallSim = (t, dt) => t.SimTick(dt);

    private static bool _paused;
    private static float _gameSpeed = 1f, _maxGameSpeed = 8f, _maxFrameTime = 0.1f;
    private static int _simRate = 10, _maxSimSteps = 5;
    private static float _simAccumulator;

    #region Public state
    public static event Action<bool> OnPausedChanged;
    public static event Action<float> OnGameSpeedChanged;

    public static bool Paused
    {
        get => _paused;
        set { if (_paused == value) return; _paused = value; OnPausedChanged?.Invoke(value); }
    }

    /// <summary>1 = normal, 2 = double speed, 0.5 = slow. Clamped to [0, MaxGameSpeed].</summary>
    public static float GameSpeed
    {
        get => _gameSpeed;
        set
        {
            value = Mathf.Clamp(value, 0f, _maxGameSpeed);
            if (Mathf.Approximately(_gameSpeed, value)) return;
            _gameSpeed = value;
            OnGameSpeedChanged?.Invoke(value);
        }
    }

    public static float MaxGameSpeed { get => _maxGameSpeed; set => _maxGameSpeed = Mathf.Max(0.1f, value); }
    public static int SimTickRate { get => _simRate; set => _simRate = Mathf.Clamp(value, 1, 120); }

    /// <summary>Seconds of game time that have passed (affected by speed and pause).</summary>
    public static double GameTime { get; private set; }
    /// <summary>Total fixed simulation ticks executed. Use it as a deterministic clock.</summary>
    public static long SimTickCount { get; private set; }
    public static int TickerCount => Ticks.Count;
    public static int SimTickerCount => SimTicks.Count;
    public static int TimerCount => _timerMap.Count;
    #endregion

    #region Access
    /// <param name="priority">Lower runs first (e.g. -10 input, 0 gameplay, 10 UI). Same priority = order of registration.</param>
    /// <param name="ignoreGameSpeed">True = receives real delta time and keeps running while paused.</param>
    public static void AddTick(ITick t, int priority = 0, bool ignoreGameSpeed = false)
    {
        if (!EnsureRunner()) return;
        Ticks.Add(t, priority, ignoreGameSpeed);
    }

    public static void RemoveTick(ITick t) => Ticks.Remove(t);
    public static bool HasTick(ITick t) => Ticks.Contains(t);

    public static void AddSimTick(ISimTick t, int priority = 0)
    {
        if (!EnsureRunner()) return;
        SimTicks.Add(t, priority, false);
    }

    public static void RemoveSimTick(ISimTick t) => SimTicks.Remove(t);

    public static void Pause() => Paused = true;
    public static void Resume() => Paused = false;
    public static void TogglePause() => Paused = !Paused;
    public static void SetGameSpeed(float speed) => GameSpeed = speed;
    #endregion

    #region Timers
    private sealed class Timer
    {
        public int id;
        public float remaining, interval;
        public bool repeat, unscaled, active = true;
        public Action callback;
    }

    private static readonly List<Timer> _timers = new List<Timer>();
    private static readonly List<Timer> _timersPending = new List<Timer>();
    private static readonly Dictionary<int, Timer> _timerMap = new Dictionary<int, Timer>();
    private static int _nextTimerId = 1;

    /// <summary>Runs callback once after 'seconds' of GAME time. Returns an id for Cancel(). 0 = failed.</summary>
    public static int Delay(float seconds, Action callback, bool realTime = false) =>
        AddTimer(seconds, seconds, false, realTime, callback);

    /// <summary>Runs callback every 'interval' game-seconds (e.g. a day, an income tick).</summary>
    public static int Every(float interval, Action callback, bool realTime = false) =>
        AddTimer(interval, interval, true, realTime, callback);

    public static bool Cancel(int timerId)
    {
        if (!_timerMap.TryGetValue(timerId, out var t)) return false;
        t.active = false;
        _timerMap.Remove(timerId);
        return true;
    }

    private static int AddTimer(float first, float interval, bool repeat, bool unscaled, Action cb)
    {
        if (cb == null || !EnsureRunner()) return 0;
        var t = new Timer
        {
            id = _nextTimerId++,
            remaining = Mathf.Max(0f, first),
            interval = Mathf.Max(0.0001f, interval),
            repeat = repeat,
            unscaled = unscaled,
            callback = cb
        };
        _timerMap[t.id] = t;
        _timersPending.Add(t);
        return t.id;
    }
    #endregion

    #region Update
    private void Update() => CallAllTick(Time.unscaledDeltaTime);

    /// <summary>One full step with the given REAL delta time. Update() calls this; you can also call it manually (tests, replays).</summary>
    public void CallAllTick(float realDt)
    {
        realDt = Mathf.Min(realDt, _maxFrameTime);
        float dt = _paused ? 0f : realDt * _gameSpeed;

        if (!_paused)
        {
            GameTime += dt;

            // 1) fixed-rate simulation
            _simAccumulator += dt;
            float step = 1f / _simRate;
            int steps = 0;
            while (_simAccumulator >= step)
            {
                if (steps >= _maxSimSteps) { _simAccumulator = 0f; break; }   // too far behind: drop the backlog
                SimTicks.Run(CallSim, step, step, false);
                _simAccumulator -= step;
                SimTickCount++;
                steps++;
            }
        }

        // 2) per-frame ticks (scaled ones are skipped while paused, ignoreGameSpeed ones still run)
        Ticks.Run(CallTick, dt, realDt, _paused);

        // 3) timers
        RunTimers(dt, realDt, _paused);
    }

    private static void RunTimers(float dt, float realDt, bool paused)
    {
        if (_timersPending.Count > 0)
        {
            _timers.AddRange(_timersPending);
            _timersPending.Clear();
        }

        bool removed = false;
        for (int i = 0; i < _timers.Count; i++)
        {
            Timer t = _timers[i];
            if (!t.active) { removed = true; continue; }
            if (paused && !t.unscaled) continue;

            t.remaining -= t.unscaled ? realDt : dt;
            for (int guard = 0; t.active && t.remaining <= 0f; guard++)
            {
                try { t.callback(); }
                catch (Exception e) { Debug.LogException(e); }

                if (!t.repeat)
                {
                    t.active = false;
                    _timerMap.Remove(t.id);
                    removed = true;
                    break;
                }
                t.remaining += t.interval;
                if (guard >= 15) { t.remaining = t.interval; break; }     // never fire more than 16x per frame
            }
        }
        if (removed) _timers.RemoveAll(IsInactive);
    }

    private static bool IsInactive(Timer t) => !t.active;
    #endregion

    #region Lifecycle
    private void Awake()
    {
        if (_runner != null && _runner != this)
        {
            if (_auto)   // a scene-placed manager replaces the auto-created one (all state is static)
            {
                Destroy(_runner.gameObject);
                _runner = null;
            }
            else { Destroy(gameObject); return; }
        }

        _runner = this;
        _auto = _creating;
        if (transform.parent != null) transform.SetParent(null);
        DontDestroyOnLoad(gameObject);

        if (!_auto)   // apply inspector settings
        {
            _maxGameSpeed = Mathf.Max(0.1f, maxGameSpeed);
            _maxFrameTime = Mathf.Max(0.01f, maxFrameTime);
            _simRate = Mathf.Clamp(simTickRate, 1, 120);
            _maxSimSteps = Mathf.Max(1, maxSimStepsPerFrame);
            GameSpeed = startGameSpeed;
            Paused = startPaused;
        }
    }

    private void OnDestroy() { if (_runner == this) _runner = null; }
    private void OnApplicationQuit() { _quitting = true; }

    private static bool EnsureRunner()
    {
        if (_runner != null) return true;
        if (_quitting || !Application.isPlaying) return false;

        _creating = true;
        var go = new GameObject("[TickManager]");
        go.AddComponent<TickManager>();      // Awake registers it as _runner
        _creating = false;
        return _runner != null;
    }

    // Needed when "Enter Play Mode Options" has domain reload disabled.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _runner = null; _auto = _creating = _quitting = false;
        Ticks.Clear(); SimTicks.Clear();
        _timers.Clear(); _timersPending.Clear(); _timerMap.Clear(); _nextTimerId = 1;
        _paused = false; _gameSpeed = 1f; _maxGameSpeed = 8f; _maxFrameTime = 0.1f;
        _simRate = 10; _maxSimSteps = 5; _simAccumulator = 0f;
        GameTime = 0; SimTickCount = 0;
        OnPausedChanged = null; OnGameSpeedChanged = null;
    }
    #endregion

    // ─────────────────────────────────────────────────────────────────────
    //  Registry: ordered list with deferred add/remove (safe while iterating)
    // ─────────────────────────────────────────────────────────────────────
    private sealed class Registry<T> where T : class
    {
        private sealed class Entry
        {
            public T target;
            public int priority, order;
            public bool ignoreSpeed, unity, active = true;
        }

        private readonly List<Entry> _list = new List<Entry>();
        private readonly List<Entry> _pending = new List<Entry>();
        private readonly Dictionary<T, Entry> _map = new Dictionary<T, Entry>();
        private bool _dirtySort, _dirtyRemove;
        private int _order;

        public int Count => _map.Count;
        public bool Contains(T t) => t != null && _map.ContainsKey(t);

        public bool Add(T t, int priority, bool ignoreSpeed)
        {
            if (t == null || _map.ContainsKey(t)) return false;          // no duplicates
            var e = new Entry
            {
                target = t,
                priority = priority,
                ignoreSpeed = ignoreSpeed,
                order = _order++,
                unity = (object)t is UnityEngine.Object
            };
            _map[t] = e;
            _pending.Add(e);                                              // starts ticking on the next step
            return true;
        }

        public bool Remove(T t)
        {
            if (t == null || !_map.TryGetValue(t, out Entry e)) return false;
            e.active = false;                                             // stops immediately, even mid-iteration
            _map.Remove(t);
            _dirtyRemove = true;
            return true;
        }

        public void Clear()
        {
            _list.Clear(); _pending.Clear(); _map.Clear();
            _dirtySort = _dirtyRemove = false; _order = 0;
        }

        public void Run(Action<T, float> call, float scaledDt, float realDt, bool paused)
        {
            Flush();
            for (int i = 0; i < _list.Count; i++)
            {
                Entry e = _list[i];
                if (!e.active) continue;

                if (e.unity && (object)e.target is UnityEngine.Object uo && uo == null)   // destroyed MonoBehaviour
                {
                    e.active = false;
                    _map.Remove(e.target);
                    _dirtyRemove = true;
                    continue;
                }

                if (!e.ignoreSpeed && paused) continue;
                try { call(e.target, e.ignoreSpeed ? realDt : scaledDt); }
                catch (Exception ex) { Debug.LogException(ex, (object)e.target as UnityEngine.Object); }
            }
        }

        private void Flush()
        {
            if (_dirtyRemove)
            {
                _list.RemoveAll(IsInactive);
                _dirtyRemove = false;
            }
            if (_pending.Count > 0)
            {
                for (int i = 0; i < _pending.Count; i++)
                    if (_pending[i].active) _list.Add(_pending[i]);
                _pending.Clear();
                _dirtySort = true;
            }
            if (_dirtySort)
            {
                _list.Sort(Compare);          // priority, then registration order -> deterministic
                _dirtySort = false;
            }
        }

        private static bool IsInactive(Entry e) => !e.active;
        private static int Compare(Entry a, Entry b)
        {
            int c = a.priority.CompareTo(b.priority);
            return c != 0 ? c : a.order.CompareTo(b.order);
        }
    }
}