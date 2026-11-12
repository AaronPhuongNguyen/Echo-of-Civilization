using System.Collections.Generic;
using UnityEngine;

/// <summary>Optional: implement on a component of a pooled prefab to get spawn / despawn callbacks.</summary>
public interface IPoolable
{
    void OnSpawn();
    void OnDespawn();
}

/// <summary>
/// Global object pool (PoolManager.Instance).
///  - Spawn(prefab, ...) / Despawn(instance)
///  - Auto check: every 'checkInterval' seconds (real time, works while the game is paused) a slice of all
///    tracked instances is inspected. An instance that is INACTIVE but not in the pool (someone called
///    SetActive(false) instead of Despawn) is collected back into the pool automatically.
///    An instance that is ACTIVE but still marked as pooled (someone re-enabled it) is released from the pool.
///  - Destroyed instances are forgotten.
/// </summary>
[DefaultExecutionOrder(-900)]
public class PoolManager : TickBehaviour
{
    // ?? singleton ??
    private static PoolManager _instance;
    private static bool _quitting;

    public static bool HasInstance => _instance != null;

    public static PoolManager Instance
    {
        get
        {
            if (_instance == null && !_quitting)
            {
#if UNITY_2022_2_OR_NEWER
                _instance = FindFirstObjectByType<PoolManager>();
#else
                _instance = FindObjectOfType<PoolManager>();
#endif
                if (_instance == null)
                    _instance = new GameObject("PoolManager").AddComponent<PoolManager>();
            }
            return _instance;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() { _instance = null; _quitting = false; }

    // ?? settings ??
    [Tooltip("Seconds (real time) between auto-check passes.")]
    [SerializeField, Min(0.05f)] private float checkInterval = 0.5f;
    [Tooltip("Max instances inspected per pass (a rolling window, so huge pools stay cheap).")]
    [SerializeField, Min(16)] private int checksPerPass = 512;

    protected override int TickPriority => -900;
    protected override bool IgnoreGameSpeed => true;   // TickManager may start paused; the pool must still work

    // ?? data ??
    private sealed class Entry
    {
        public GameObject go;
        public GameObject prefab;
        public bool pooled;
        public IPoolable[] poolables;
    }

    private readonly Dictionary<GameObject, Stack<Entry>> _free = new Dictionary<GameObject, Stack<Entry>>();
    private readonly Dictionary<GameObject, Entry> _all = new Dictionary<GameObject, Entry>();   // instance -> entry
    private readonly List<Entry> _list = new List<Entry>();                                      // for the rolling check
    private Transform _root;
    private float _timer;
    private int _cursor;

    public int TrackedCount => _list.Count;

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Debug.LogWarning("Duplicate PoolManager removed.");
            Destroy(this);
            return;
        }
        _instance = this;
        EnsureRoot();
    }

    private void OnApplicationQuit() => _quitting = true;

    protected override void OnDisable()
    {
        base.OnDisable();
        if (_instance == this && _quitting) _instance = null;
    }

    private void EnsureRoot()
    {
        if (_root != null) return;
        var go = new GameObject("Pool (inactive objects)");
        go.transform.SetParent(transform, false);
        _root = go.transform;
    }

    // ?????????????????????????????????????????????????????????????????????
    //  API
    // ?????????????????????????????????????????????????????????????????????
    public GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation, Transform parent = null)
    {
        if (prefab == null) return null;
        EnsureRoot();

        Entry e = PopFree(prefab);
        if (e == null) e = CreateEntry(prefab);

        Transform t = e.go.transform;
        t.SetParent(parent, false);
        t.SetPositionAndRotation(position, rotation);
        e.pooled = false;
        e.go.SetActive(true);

        for (int i = 0; i < e.poolables.Length; i++) e.poolables[i]?.OnSpawn();
        return e.go;
    }

    /// <summary>Spawns with the prefab's own local position/rotation under 'parent' (position is then set by you).</summary>
    public GameObject Spawn(GameObject prefab, Transform parent = null)
    {
        GameObject go = Spawn(prefab, Vector3.zero, Quaternion.identity, parent);
        if (go != null && parent != null)
        {
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
        }
        return go;
    }

    public T Spawn<T>(T prefab, Vector3 position, Quaternion rotation, Transform parent = null) where T : Component
    {
        if (prefab == null) return null;
        GameObject go = Spawn(prefab.gameObject, position, rotation, parent);
        return go != null ? go.GetComponent<T>() : null;
    }

    /// <summary>Returns the instance to the pool. Objects that were not created by the pool are destroyed.</summary>
    public bool Despawn(GameObject instance)
    {
        if (instance == null) return false;
        if (!_all.TryGetValue(instance, out Entry e))
        {
            Destroy(instance);
            return false;
        }
        if (e.pooled) return true;
        Collect(e, true);
        return true;
    }

    public void Despawn(Component c) { if (c != null) Despawn(c.gameObject); }

    /// <summary>Creates 'count' inactive instances in advance.</summary>
    public void Prewarm(GameObject prefab, int count)
    {
        if (prefab == null) return;
        EnsureRoot();
        for (int i = 0; i < count; i++)
        {
            Entry e = CreateEntry(prefab);
            Collect(e, false);
        }
    }

    /// <summary>Destroys every pooled (inactive) instance. Active instances are untouched.</summary>
    public void ClearPool()
    {
        for (int i = _list.Count - 1; i >= 0; i--)
        {
            Entry e = _list[i];
            if (e.pooled) { RemoveEntry(i); if (e.go) Destroy(e.go); }
        }
        _free.Clear();
    }

    // ?????????????????????????????????????????????????????????????????????
    //  INTERNAL
    // ?????????????????????????????????????????????????????????????????????
    private Entry CreateEntry(GameObject prefab)
    {
        GameObject go = Instantiate(prefab, _root);
        go.SetActive(false);
        var e = new Entry
        {
            go = go,
            prefab = prefab,
            pooled = false,
            poolables = go.GetComponentsInChildren<IPoolable>(true)
        };
        _all[go] = e;
        _list.Add(e);
        return e;
    }

    private Entry PopFree(GameObject prefab)
    {
        if (!_free.TryGetValue(prefab, out Stack<Entry> stack)) return null;
        while (stack.Count > 0)
        {
            Entry e = stack.Pop();
            if (e.go == null || !e.pooled) continue;   // destroyed, or released by the auto check
            return e;
        }
        return null;
    }

    /// <summary>Puts an entry into the pool (inactive, under the pool root).</summary>
    private void Collect(Entry e, bool callback)
    {
        if (e.go == null) return;
        if (callback) for (int i = 0; i < e.poolables.Length; i++) e.poolables[i]?.OnDespawn();

        e.pooled = true;
        e.go.SetActive(false);
        e.go.transform.SetParent(_root, false);

        if (!_free.TryGetValue(e.prefab, out Stack<Entry> stack))
            _free[e.prefab] = stack = new Stack<Entry>();
        stack.Push(e);
    }

    private void RemoveEntry(int index)
    {
        Entry e = _list[index];
        if (e.go != null) _all.Remove(e.go);
        int last = _list.Count - 1;
        _list[index] = _list[last];
        _list.RemoveAt(last);
    }

    // ?????????????????????????????????????????????????????????????????????
    //  AUTO CHECK
    // ?????????????????????????????????????????????????????????????????????
    public override void Tick(float deltaTime)
    {
        _timer += deltaTime;     // IgnoreGameSpeed = true -> real time
        if (_timer < checkInterval) return;
        _timer = 0f;
        RunCheck();
    }

    private void RunCheck()
    {
        int count = _list.Count;
        if (count == 0) return;

        int n = Mathf.Min(count, checksPerPass);
        for (int k = 0; k < n; k++)
        {
            if (_cursor >= _list.Count) _cursor = 0;
            Entry e = _list[_cursor];

            if (e.go == null)                       // destroyed from outside
            {
                int last = _list.Count - 1;
                _list[_cursor] = _list[last];
                _list.RemoveAt(last);
                continue;                           // same cursor, new element
            }

            if (!e.pooled && !e.go.activeSelf)      // disabled by someone -> collect it
                Collect(e, true);
            else if (e.pooled && e.go.activeSelf)   // re-enabled by someone -> release it from the pool
                e.pooled = false;

            _cursor++;
        }
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }
}