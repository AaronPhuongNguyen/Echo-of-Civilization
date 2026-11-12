using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
//  DATA   (Province / Nation classes now live in WorldManager.cs)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Add Dirt, Sand, Mountain... here later. Colors are resolved in MapManager.TileColor().</summary>
public enum TileType : byte { Water = 0, Land = 1 }

[Serializable]
public class MapSettings
{
    [Header("Size (500 - 2048 recommended)")]
    public int width = 1024;
    public int height = 1024;
    public float tileSize = 1f;
    [Tooltip("ON: the MapManager's position is the CENTER of the map. OFF: it is the bottom-left corner.")]
    public bool pivotAtCenter = true;

    [Header("Seed")]
    public bool randomSeed = false;
    public uint seed = 12345;

    [Header("World (continents)")]
    [Range(0f, 1f)] public float landPercent = 0.42f;      // Giảm nhẹ diện tích đất để biển có không gian mở, thoáng đãng hơn
    public int minContinents = 4;                        // Ít tâm lục địa hơn để tránh các mảng đất bị băm nát chằng chịt
    public int maxContinents = 7;
    [Range(0.05f, 0.5f)] public float continentRadius = 0.24f;   // Tăng nhẹ bán kính để các mảng lục địa đủ lớn, có hình khối rõ ràng
    [Range(0f, 1f)] public float continentWeight = 0.75f;        // TĂNG MẠNH: Giúp định hình lục địa rõ nét, loại bỏ hiện tượng đứt gãy lộn xộn
    [Range(0f, 0.4f)] public float coastWarp = 0.12f;            // GIẢM MẠNH: Giúp đường bờ biển bớt ngoằn ngoèo, gập ghềnh kiểu "nhiễu hạt"
    public float featureSize = 140f;                     // TĂNG MẠNH: Tạo các thung lũng, vịnh lớn và mượt mà, triệt tiêu hoàn toàn lỗi lỗ hỗng
    [Range(1, 6)] public int octaves = 4;                // Giảm số tầng nhiễu để các đường nét biên giới biển mượt và thanh thoát hơn
    [Range(0.1f, 0.9f)] public float persistence = 0.45f;
    [Range(1.2f, 3f)] public float lacunarity = 2.0f;
    [Range(0f, 1f)] public float edgeWater = 0.4f;        // Mở rộng viền nước quanh bản đồ để biển bao bọc lục địa hoàn hảo
    [Range(0, 4)] public int smoothingPasses = 3;        // TĂNG: Làm mượt triệt để các góc cạnh răng cưa của ô vuông bản đồ
    public int minLandPatch = 200;                       // Loại bỏ các mảng đất nhỏ vụn vặt bám quanh bờ biển
    public int minWaterPatch = 800;                      // TĂNG CỰC KỲ MẠNH: Xóa sạch toàn bộ các "lỗ thủng phô mai" nhỏ trên đất liền, chỉ giữ lại các hồ lớn

    [Header("Provinces (random count: Min .. Width*Height/Divisor)")]
    public int minProvinces = 30;
    [Tooltip("Max provinces = Width * Height / this. Lower divisor = way more provinces.")]
    public int provinceMaxDivisor = 40;
    public int minTilesPerProvince = 5;

    [Header("Province stats (random range per province)")]
    public Vector2 taxRange = new Vector2(0.1f, 1.1f);
    public Vector2 economyRange = new Vector2(0.05f, 0.5f);
    public Vector2 defendRange = new Vector2(0.1f, 0.5f);
    public Vector2 popGrowthRange = new Vector2(0.01f, 0.25f);
    public Vector2Int populationRange = new Vector2Int(500, 2500);

    [Header("Province stat modifiers (applied at generation)")]
    public StatModifier taxMod = new StatModifier();
    public StatModifier economyMod = new StatModifier();
    public StatModifier defendMod = new StatModifier();
    public StatModifier popGrowthMod = new StatModifier();
    public StatModifier infrastructureMod = new StatModifier();
    public StatModifier productionMod = new StatModifier();

    [Header("Nations (random count: Min .. Max)")]
    public int minNations = 15;
    public int maxNations = 45;
    [Tooltip("0 = every nation gets exactly the same land share. 0.3 = each nation is within +-30% of its fair share.")]
    [Range(0f, 0.8f)] public float nationSizeVariation = 0.35f;
    [Tooltip("Landmasses smaller than (this x average nation size) get no nation of their own; they join the nearest nation as colonies.")]
    [Range(0f, 1f)] public float minIslandNationFactor = 0.4f;

    [Header("Rendering")]
    public Color waterColor = new Color(0.12f, 0.32f, 0.58f); // Màu xanh biển sâu thẳm, dịu mắt hơn
    public Color neutralColor = new Color(0.6f, 0.6f, 0.6f);
    public Color borderColor = Color.black;
    [Range(0f, 1f)] public float provinceBorderAlpha = 0.35f;
    [Range(0f, 1f)] public float nationBorderAlpha = 0.75f;
    [Range(4, 16)] public int borderPixelsPerTile = 8;
    [Range(1, 8)] public int provinceBorderPixels = 1;
    [Range(1, 8)] public int nationBorderPixels = 2;

    [Header("Performance (phone)")]
    public int chunkSize = 128;
    public int maxDetailChunks = 9;
    public int overviewMaxSize = 1024;
    public int chunkBuildsPerFrame = 2;
    public int maxPooledChunks = 6;
    public bool generateOnBackgroundThread = true;

    public MapSettings Clone() => (MapSettings)MemberwiseClone();
}

// ─────────────────────────────────────────────────────────────────────────────
//  MAP MANAGER
//
//  Memory/CPU design for 500x500 .. 2048x2048 on phones:
//   - generation runs on a background thread, uses flat arrays only (no per-tile objects)
//   - provinces store exact int[] tile lists; world height uses a ushort histogram (no sort)
//   - rendering = 1 low-res OVERVIEW texture (always on) + DETAIL CHUNKS that exist only
//     for the part the camera sees. Chunk textures are rebuilt from data, pooled and reused.
//   - nations are stored in WorldManager (registered on the main thread after generation)
// ─────────────────────────────────────────────────────────────────────────────

public class MapManager : MonoBehaviour
{
    public MapSettings settings = new MapSettings();
    [SerializeField] private bool generateOnStart = true;
    [Tooltip("Only used to decide which detail chunks are visible. Defaults to Camera.main.")]
    [SerializeField] private Camera viewCamera;

    public event Action OnMapGenerated;
    public event Action<Province, int, int> OnProvinceOwnerChanged;   // province, oldNationId, newNationId

    public bool IsGenerating { get; private set; }
    public bool IsReady { get; private set; }

    [Header("Capital marker")]
    [Tooltip("Prefab with a SpriteRenderer (white star). Tinted with the inverse nation color.")]
    [SerializeField] private GameObject capitalMarkerPrefab;
    [Tooltip("Marker size = capital province size (sqrt of tiles) * this.")]
    [SerializeField, Range(0.1f, 1.5f)] private float markerProvinceFraction = 0.6f;
    [Tooltip("Minimum marker size in tiles, so it stays visible in tiny provinces.")]
    [SerializeField] private float markerMinTiles = 3f;
    [SerializeField] private int markerSortingOrder = 10;

    private Transform _markerRoot;
    private readonly Dictionary<int, GameObject> _markers = new Dictionary<int, GameObject>();

    // ── data ──
    private MapSettings _s = new MapSettings();               // snapshot used during/after generation
    private int _w, _h;
    private TileType[] _tiles;
    private int[] _tileProvince;                              // province id per tile, -1 = water
    private int[] _landIdx;                                   // indices of all land tiles
    private readonly List<Province> _provinces = new List<Province>();
    private WorldManager _world;                              // owns the nations
    private int[] _pendingOwner;                              // generation result: region index per province
    private Color[] _pendingColors;                           // generation result: color per region
    private int[] _pendingCapital;
    private string[] _pendingNames;                             // generation result: capital province id per region
    private Unity.Mathematics.Random _rng;                    // local generator (thread-safe, seeded from RNG.Seed)
    private bool _destroyed;

    public int Width => _w;
    public int Height => _h;

    /// <summary>Offset from this transform to the map's bottom-left corner (local space). Pivot = map center.</summary>
    private Vector3 PivotOffset => _s.pivotAtCenter
        ? new Vector3(-_w * _s.tileSize * 0.5f, -_h * _s.tileSize * 0.5f, 0f)
        : Vector3.zero;
    public IReadOnlyList<Province> Provinces => _provinces;
    public IReadOnlyList<Nation> Nations => _world != null ? _world.Nations : null;

    // ── rendering ──
    private class Chunk
    {
        public int cx, cy;
        public GameObject go;
        public Texture2D color, border;
        public Sprite colorSprite, borderSprite;
    }

    private int _cs, _ppt, _provPx, _nationPx;
    private byte _provAlpha, _nationAlpha;
    private Color32 _borderRgb;

    private GameObject _ovGo;
    private Texture2D _ovTex;
    private Sprite _ovSprite;
    private Color32[] _ovPx;
    private int _ovScale, _ovW, _ovH;

    private Transform _chunkRoot;
    private Chunk[] _chunks;
    private int _ccx, _ccy;
    private readonly Stack<Chunk> _pool = new Stack<Chunk>();
    private Color32[] _chunkColorBuf;
    private byte[] _chunkBorderBuf;

    private Vector3 _lastCamPos;
    private Quaternion _lastCamRot;
    private float _lastZoom, _lastAspect;
    private bool _chunkWorkPending;

    #region Unity
    private void Start() { if (generateOnStart) Generate(); }
    private void LateUpdate() { if (IsReady) UpdateChunkVisibility(); }
    private void OnDestroy() { _destroyed = true; Clear(); }

    private void OnValidate()
    {
        settings.width = Mathf.Clamp(settings.width, 16, 4096);
        settings.height = Mathf.Clamp(settings.height, 16, 4096);
        settings.tileSize = Mathf.Max(0.01f, settings.tileSize);
        settings.minProvinces = Mathf.Max(1, settings.minProvinces);
        settings.provinceMaxDivisor = Mathf.Max(1, settings.provinceMaxDivisor);
        settings.minNations = Mathf.Max(1, settings.minNations);
        settings.maxNations = Mathf.Max(settings.minNations, settings.maxNations);
        settings.chunkSize = Mathf.Clamp(settings.chunkSize, 32, 256);
        settings.maxDetailChunks = Mathf.Max(1, settings.maxDetailChunks);
        settings.taxRange = FixRange(settings.taxRange);
        settings.economyRange = FixRange(settings.economyRange);
        settings.defendRange = FixRange(settings.defendRange);
        settings.popGrowthRange = FixRange(settings.popGrowthRange);
        settings.populationRange.x = Mathf.Max(0, settings.populationRange.x);
        settings.populationRange.y = Mathf.Max(settings.populationRange.x, settings.populationRange.y);
    }

    private static Vector2 FixRange(Vector2 r) => new Vector2(r.x, Mathf.Max(r.x, r.y));
    #endregion

    // ─────────────────────────────────────────────────────────────────────
    //  PUBLIC API
    // ─────────────────────────────────────────────────────────────────────
    [ContextMenu("Generate Map")]
    public void Generate() { _ = GenerateAsync(); }

    public void Generate(uint seed)
    {
        settings.randomSeed = false;
        settings.seed = seed;
        Generate();
    }

    public async Task GenerateAsync()
    {
        if (IsGenerating) return;
        IsGenerating = true;
        try
        {
            Clear();
            _s = settings.Clone();
            _w = _s.width;
            _h = _s.height;
            _world = WorldManager.Instance;       // main thread: finds / creates the WorldManager

            // Global RNG stays in sync for the rest of the game; map generation uses a local copy of it
            // so it can safely run on a background thread without touching the global state.
            RNG.SetSeed(_s.randomSeed ? (uint)DateTime.Now.Ticks : _s.seed);
            uint seed = RNG.Seed;
            settings.seed = seed;

            if (_s.generateOnBackgroundThread) await Task.Run(() => GenerateData(seed));
            else GenerateData(seed);

            if (_destroyed || this == null) return;
            RegisterNations();                    // main thread: hand the nations over to the WorldManager
            BuildRendering();
            BuildCapitalMarkers();
            IsReady = true;
            OnMapGenerated?.Invoke();
        }
        catch (Exception e) { Debug.LogException(e); }
        finally { IsGenerating = false; }
    }

    public void Clear()
    {
        DestroyMarkers();
        IsReady = false;
        _provinces.Clear();
        _tiles = null;
        _tileProvince = null;
        _landIdx = null;
        _pendingOwner = null;
        _pendingColors = null;
        _pendingCapital = null;
        _pendingNames = null;
        if (WorldManager.HasInstance) WorldManager.Instance.ClearNations();

        if (_chunks != null)
            foreach (var c in _chunks) if (c != null) DestroyChunk(c);
        _chunks = null;
        while (_pool.Count > 0) DestroyChunk(_pool.Pop());
        if (_chunkRoot) Destroy(_chunkRoot.gameObject);
        _chunkRoot = null;

        if (_ovSprite) Destroy(_ovSprite);
        if (_ovTex) Destroy(_ovTex);
        if (_ovGo) Destroy(_ovGo);
        _ovPx = null; _chunkColorBuf = null; _chunkBorderBuf = null;
    }

    /// <summary>Gameplay entry point: conquer / transfer a province. nationId -1 = neutral.</summary>
    public bool SetProvinceOwner(int provinceId, int nationId)
    {
        if (!IsReady || provinceId < 0 || provinceId >= _provinces.Count) return false;

        Nation target = null;
        if (nationId >= 0 && !_world.TryGetNation(nationId, out target)) return false;

        Province p = _provinces[provinceId];
        int old = p.OwnerNationId;
        if (old == nationId) return true;

        if (!_world.AssignProvince(p, target)) return false;
        UpdateProvinceColor(p);
        RefreshProvinceVisuals(p);
        RefreshCapitalMarkers();

        OnProvinceOwnerChanged?.Invoke(p, old, nationId);
        return true;
    }

    public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < _w && y < _h;
    public TileType GetTileType(int x, int y) => _tiles[y * _w + x];
    public bool IsLand(int x, int y) => InBounds(x, y) && _tiles[y * _w + x] == TileType.Land;
    public int GetProvinceId(int x, int y) => InBounds(x, y) ? _tileProvince[y * _w + x] : -1;
    public Province GetProvince(int id) => id >= 0 && id < _provinces.Count ? _provinces[id] : null;
    public Nation GetNation(int id) => _world != null ? _world.GetNation(id) : null;

    public int GetNationId(int x, int y)
    {
        int p = GetProvinceId(x, y);
        return p < 0 ? -1 : _provinces[p].OwnerNationId;
    }

    public bool TryWorldToTile(Vector3 world, out int x, out int y)
    {
        Vector3 local = transform.InverseTransformPoint(world) - PivotOffset;
        x = Mathf.FloorToInt(local.x / _s.tileSize);
        y = Mathf.FloorToInt(local.y / _s.tileSize);
        return InBounds(x, y);
    }

    public Vector3 TileToWorldCenter(int x, int y) =>
        transform.TransformPoint(new Vector3((x + 0.5f) * _s.tileSize, (y + 0.5f) * _s.tileSize, 0f) + PivotOffset);

    /// <summary>World-space rectangle covered by the map (only meaningful when IsReady).</summary>
    public Rect WorldRect
    {
        get
        {
            Vector3 a = transform.TransformPoint(PivotOffset);
            Vector3 b = transform.TransformPoint(PivotOffset + new Vector3(_w * _s.tileSize, _h * _s.tileSize, 0f));
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }
    }

    public Province GetProvinceAt(Vector3 world) =>
        IsReady && TryWorldToTile(world, out int x, out int y) ? GetProvince(GetProvinceId(x, y)) : null;

    public Nation GetNationAt(Vector3 world) => GetProvinceAt(world)?.Owner;

    // ─────────────────────────────────────────────────────────────────────
    //  GENERATION  (everything below until VISUALS may run on a background thread)
    // ─────────────────────────────────────────────────────────────────────
    private void GenerateData(uint seed)
    {
        _rng = new Unity.Mathematics.Random(HashSeed(seed));
        GenerateWorld();
        GenerateProvinces();
        GenerateNations();
        GenerateNames(seed);
    }

    private static uint HashSeed(uint s)
    {
        s = (s ^ 61u) ^ (s >> 16);
        s *= 9u;
        s ^= s >> 4;
        s *= 0x27d4eb2du;
        s ^= s >> 15;
        return s == 0 ? 1u : s;
    }

    private static float Noise(float x, float y) => Unity.Mathematics.noise.snoise(new Unity.Mathematics.float2(x, y));

    // ── WORLD: fractal simplex noise + edge falloff; exact land % via a 65536-bin histogram (no sorting) ──
    private void GenerateWorld()
    {
        int n = _w * _h;
        var hmap = new ushort[n];
        var hist = new int[65536];
        float minSide = Mathf.Min(_w, _h);

        // 1) continent seeds: spread out (best-candidate), each one a rotated, stretched blob
        int cc = _rng.NextInt(Mathf.Max(1, _s.minContinents), Mathf.Max(_s.minContinents, _s.maxContinents) + 1);
        var cx = new float[cc]; var cy = new float[cc]; var cr = new float[cc];
        var cCos = new float[cc]; var cSin = new float[cc]; var cSx = new float[cc]; var cSy = new float[cc];
        for (int i = 0; i < cc; i++)
        {
            float bx = 0f, by = 0f, bestD = -1f;
            for (int t = 0; t < 16; t++)
            {
                float px = _rng.NextFloat(0.18f, 0.82f) * _w;
                float py = _rng.NextFloat(0.18f, 0.82f) * _h;
                float dmin = float.MaxValue;
                for (int j = 0; j < i; j++) dmin = Mathf.Min(dmin, (px - cx[j]) * (px - cx[j]) + (py - cy[j]) * (py - cy[j]));
                if (i == 0 || dmin > bestD) { bestD = dmin; bx = px; by = py; }
            }
            cx[i] = bx; cy[i] = by;
            cr[i] = _s.continentRadius * minSide * _rng.NextFloat(0.7f, 1.3f);
            float ang = _rng.NextFloat(0f, Mathf.PI);
            cCos[i] = Mathf.Cos(ang); cSin[i] = Mathf.Sin(ang);
            cSx[i] = _rng.NextFloat(0.75f, 1.4f);
            cSy[i] = _rng.NextFloat(0.75f, 1.4f);
        }

        float ox = _rng.NextFloat(0f, 1000f), oy = _rng.NextFloat(0f, 1000f);
        float wox = _rng.NextFloat(0f, 1000f), woy = _rng.NextFloat(0f, 1000f);
        float baseFreq = 1f / Mathf.Max(1f, _s.featureSize);
        float warpFreq = 1f / (minSide * 0.3f);
        float warpAmp = _s.coastWarp * minSide;
        float cw = Mathf.Clamp01(_s.continentWeight);
        int octaves = Mathf.Max(1, _s.octaves);

        for (int y = 0; y < _h; y++)
        {
            float dy = (y + 0.5f) / _h * 2f - 1f;
            for (int x = 0; x < _w; x++)
            {
                // 2) domain warp: bends the continent shapes into natural, irregular outlines
                float px = x + Noise(wox + x * warpFreq, woy + y * warpFreq) * warpAmp;
                float py = y + Noise(wox + 500f + x * warpFreq, woy + 500f + y * warpFreq) * warpAmp;

                // 3) continent mask: 1 in the core, 0 outside the (warped, stretched) radius
                float mask = 0f;
                for (int c = 0; c < cc; c++)
                {
                    float ddx = px - cx[c], ddy = py - cy[c];
                    float rx = (ddx * cCos[c] + ddy * cSin[c]) / cSx[c];
                    float ry = (-ddx * cSin[c] + ddy * cCos[c]) / cSy[c];
                    float m = 1f - Mathf.Sqrt(rx * rx + ry * ry) / cr[c];
                    if (m > mask) mask = m;
                }
                mask = Mathf.Clamp01(mask);
                mask = mask * mask * (3f - 2f * mask);

                // 4) fractal noise for coastline detail, bays and peninsulas
                float v = 0f, amp = 1f, total = 0f, f = baseFreq;
                for (int o = 0; o < octaves; o++)
                {
                    v += Noise(ox + x * f, oy + y * f) * amp;
                    total += amp;
                    amp *= _s.persistence;
                    f *= _s.lacunarity;
                }
                v = v / total * 0.5f + 0.5f;

                float dx = (x + 0.5f) / _w * 2f - 1f;
                float d = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));      // 0 center .. 1 edge
                float h = mask * cw + v * (1f - cw) - _s.edgeWater * d * d;

                int q = Mathf.Clamp((int)((h + 1f) * 0.5f * 65535f), 0, 65535);
                hmap[y * _w + x] = (ushort)q;
                hist[q]++;
            }
        }

        int landTarget = Mathf.Clamp(Mathf.RoundToInt(_s.landPercent * n), 0, n);
        int threshold = 65536, acc = 0;
        if (landTarget > 0)
        {
            for (int q = 65535; q >= 0; q--)
            {
                acc += hist[q];
                if (acc >= landTarget) { threshold = q; break; }
            }
        }

        _tiles = new TileType[n];
        int land = 0;
        for (int i = 0; i < n; i++)
        {
            if (hmap[i] >= threshold) { _tiles[i] = TileType.Land; land++; }
        }

        SmoothLand(_s.smoothingPasses);                               // round off jagged coasts
        FloodPass(TileType.Land, _s.minLandPatch, TileType.Water);    // remove tiny islands
        FloodPass(TileType.Water, _s.minWaterPatch, TileType.Land);   // fill tiny ponds (bigger ones stay as lakes/seas)

        land = 0;
        for (int i = 0; i < n; i++) if (_tiles[i] == TileType.Land) land++;
        _landIdx = new int[land];
        int k = 0;
        for (int i = 0; i < n; i++) if (_tiles[i] == TileType.Land) _landIdx[k++] = i;
    }

    /// <summary>Majority filter (8 neighbours): >=5 land -> land, <=3 -> water. Smooths coasts, kills pixel noise.</summary>
    private void SmoothLand(int passes)
    {
        int n = _w * _h;
        var src = _tiles;
        var dst = new TileType[n];
        for (int pass = 0; pass < passes; pass++)
        {
            for (int y = 0; y < _h; y++)
            {
                for (int x = 0; x < _w; x++)
                {
                    int cnt = 0;
                    for (int oy = -1; oy <= 1; oy++)
                    {
                        int yy = y + oy;
                        if (yy < 0 || yy >= _h) continue;
                        for (int ox = -1; ox <= 1; ox++)
                        {
                            if (ox == 0 && oy == 0) continue;
                            int xx = x + ox;
                            if (xx >= 0 && xx < _w && src[yy * _w + xx] == TileType.Land) cnt++;
                        }
                    }
                    int i = y * _w + x;
                    dst[i] = cnt >= 5 ? TileType.Land : (cnt <= 3 ? TileType.Water : src[i]);
                }
            }
            var tmp = src; src = dst; dst = tmp;
        }
        _tiles = src;
    }

    /// <summary>Flood-fills every connected patch of 'type'; patches smaller than minSize become 'replacement'.</summary>
    private void FloodPass(TileType type, int minSize, TileType replacement)
    {
        if (minSize <= 1) return;
        int n = _w * _h;
        var visited = new bool[n];
        var stack = new List<int>(1024);
        var comp = new List<int>(minSize);

        for (int s = 0; s < n; s++)
        {
            if (visited[s] || _tiles[s] != type) continue;
            comp.Clear(); stack.Clear();
            stack.Add(s); visited[s] = true;
            int size = 0;

            while (stack.Count > 0)
            {
                int c = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                size++;
                if (comp.Count < minSize) comp.Add(c);      // only stored while it can still be "small"

                int x = c % _w;
                int m;
                if (x > 0) { m = c - 1; if (!visited[m] && _tiles[m] == type) { visited[m] = true; stack.Add(m); } }
                if (x < _w - 1) { m = c + 1; if (!visited[m] && _tiles[m] == type) { visited[m] = true; stack.Add(m); } }
                if (c >= _w) { m = c - _w; if (!visited[m] && _tiles[m] == type) { visited[m] = true; stack.Add(m); } }
                if (c + _w < n) { m = c + _w; if (!visited[m] && _tiles[m] == type) { visited[m] = true; stack.Add(m); } }
            }

            if (size < minSize)
                foreach (int c in comp) _tiles[c] = replacement;
        }
    }

    // ── PROVINCES: count = random(minProvinces .. W*H/divisor); evenly scattered seeds + random region growth ──
    private void GenerateProvinces()
    {
        int n = _w * _h;
        int land = _landIdx.Length;
        _tileProvince = new int[n];
        for (int i = 0; i < n; i++) _tileProvince[i] = -1;
        if (land == 0) return;

        int maxP = Mathf.Max(_s.minProvinces, n / Mathf.Max(1, _s.provinceMaxDivisor));
        int target = _rng.NextInt(_s.minProvinces, maxP + 1);
        target = Mathf.Clamp(target, 1, Mathf.Max(1, land / Mathf.Max(1, _s.minTilesPerProvince)));

        var frontier = new Queue<int>(4096);
        int region = 0;
        foreach (int s in ScatterSeeds(land, target))
        {
            _tileProvince[s] = region++;
            frontier.Enqueue(s);
        }
        GrowTiles(frontier);

        // Islands that got no seed: split each into ~(size / average province size) provinces.
        float avg = (float)land / target;
        var comp = new List<int>();
        var stack = new List<int>();
        void Mark(int t)
        {
            if (_tileProvince[t] == -1 && _tiles[t] == TileType.Land) { _tileProvince[t] = -2; stack.Add(t); }
        }

        for (int li = 0; li < land; li++)
        {
            int s = _landIdx[li];
            if (_tileProvince[s] != -1) continue;

            comp.Clear(); stack.Clear();
            _tileProvince[s] = -2; stack.Add(s);
            while (stack.Count > 0)
            {
                int c = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                comp.Add(c);
                int x = c % _w;
                if (x > 0) Mark(c - 1);
                if (x < _w - 1) Mark(c + 1);
                if (c >= _w) Mark(c - _w);
                if (c + _w < n) Mark(c + _w);
            }

            int kk = Mathf.Clamp(Mathf.RoundToInt(comp.Count / avg), 1, comp.Count);
            for (int tries = kk * 8, got = 0; got < kk && tries > 0; tries--)
            {
                int pick = comp[_rng.NextInt(0, comp.Count)];
                if (_tileProvince[pick] != -2) continue;
                _tileProvince[pick] = region++;
                frontier.Enqueue(pick);
                got++;
            }
            // tiles stay -2 until all islands are processed so they are not detected twice
        }
        for (int li = 0; li < land; li++) if (_tileProvince[_landIdx[li]] == -2) _tileProvince[_landIdx[li]] = -1;
        GrowTiles(frontier);

        // build Province objects with exact-size tile arrays + random stats
        int pc = region;
        var counts = new int[pc];
        for (int li = 0; li < land; li++) counts[_tileProvince[_landIdx[li]]]++;

        var sx = new double[pc];
        var sy = new double[pc];
        var fill = new int[pc];
        for (int i = 0; i < pc; i++)
        {
            float shade = _rng.NextFloat(0.88f, 1f);
            float tax = RandomIn(_s.taxRange);
            float eco = RandomIn(_s.economyRange);
            float def = RandomIn(_s.defendRange);
            float pg = RandomIn(_s.popGrowthRange);
            int pop = _rng.NextInt(_s.populationRange.x, Mathf.Max(_s.populationRange.x, _s.populationRange.y) + 1);

            var prov = new Province
            {
                Id = i,
                Tiles = new int[counts[i]],
                Shade = shade,
                Min = new Vector2Int(int.MaxValue, int.MaxValue),
                Max = new Vector2Int(int.MinValue, int.MinValue),
                Population = pop,
                Devastation = 0f
            };
            prov.Tax.Init(tax, _s.taxMod);
            prov.Economy.Init(eco, _s.economyMod);
            prov.Defend.Init(def, _s.defendMod);
            prov.PopGrowth.Init(pg, _s.popGrowthMod);
            prov.Infrastructure.Init(0f, _s.infrastructureMod);
            prov.ProductionIncome.Init(1f, _s.productionMod);
            _provinces.Add(prov);
        }
        for (int li = 0; li < land; li++)
        {
            int t = _landIdx[li];
            int id = _tileProvince[t];
            Province p = _provinces[id];
            int x = t % _w, y = t / _w;
            p.Tiles[fill[id]++] = t;
            sx[id] += x; sy[id] += y;
            if (x < p.Min.x) p.Min.x = x;
            if (y < p.Min.y) p.Min.y = y;
            if (x > p.Max.x) p.Max.x = x;
            if (y > p.Max.y) p.Max.y = y;
        }
        for (int i = 0; i < pc; i++)
            _provinces[i].Centroid = new Vector2((float)(sx[i] / counts[i]), (float)(sy[i] / counts[i]));
    }

    private float RandomIn(Vector2 range) =>
        range.y > range.x ? _rng.NextFloat(range.x, range.y) : range.x;

    /// <summary>Dart-throwing with a spatial grid: land tiles at least ~0.6*sqrt(area/count) apart.</summary>
    private List<int> ScatterSeeds(int land, int target)
    {
        float r = 0.6f * Mathf.Sqrt((float)land / target);
        float r2 = r * r;
        float cell = Mathf.Max(1f, r / 1.4142f);
        int gw = (int)(_w / cell) + 1, gh = (int)(_h / cell) + 1;
        var grid = new int[gw * gh];                      // stores tile+1, 0 = empty
        var seeds = new List<int>(target);

        for (int attempts = target * 40; seeds.Count < target && attempts > 0; attempts--)
        {
            int t = _landIdx[_rng.NextInt(0, land)];
            int x = t % _w, y = t / _w;
            int gx = (int)(x / cell), gy = (int)(y / cell);

            bool ok = true;
            for (int yy = Mathf.Max(0, gy - 2); yy <= Mathf.Min(gh - 1, gy + 2) && ok; yy++)
            {
                for (int xx = Mathf.Max(0, gx - 2); xx <= Mathf.Min(gw - 1, gx + 2); xx++)
                {
                    int s = grid[yy * gw + xx] - 1;
                    if (s < 0) continue;
                    int dx = s % _w - x, dy = s / _w - y;
                    if (dx * dx + dy * dy < r2) { ok = false; break; }
                }
            }
            if (!ok) continue;
            grid[gy * gw + gx] = t + 1;
            seeds.Add(t);
        }
        if (seeds.Count == 0) seeds.Add(_landIdx[_rng.NextInt(0, land)]);
        return seeds;
    }

    /// <summary>
    /// Even-speed growth (FIFO queue): all provinces expand at the same pace, giving compact, rounded shapes.
    /// Each free neighbour is claimed with 75% chance so the edges stay organic instead of diamond-shaped.
    /// </summary>
    private void GrowTiles(Queue<int> frontier)
    {
        int n = _w * _h;
        while (frontier.Count > 0)
        {
            int node = frontier.Dequeue();
            int id = _tileProvince[node];
            int x = node % _w;
            bool left = false;
            if (x > 0) left |= TryClaim(node - 1, id, frontier);
            if (x < _w - 1) left |= TryClaim(node + 1, id, frontier);
            if (node >= _w) left |= TryClaim(node - _w, id, frontier);
            if (node + _w < n) left |= TryClaim(node + _w, id, frontier);
            if (left) frontier.Enqueue(node);       // still has free neighbours -> try again later
        }
    }

    // returns true if the neighbour is free but was not claimed this time
    private bool TryClaim(int m, int id, Queue<int> q)
    {
        if (_tileProvince[m] != -1 || _tiles[m] != TileType.Land) return false;
        if (_rng.NextFloat(0f, 1f) < 0.75f) { _tileProvince[m] = id; q.Enqueue(m); return false; }
        return true;
    }

    // ── NATIONS (balanced) ──
    //  1. N nations are distributed over the landmasses in proportion to their land.
    //  2. Inside a landmass every nation gets a fair share +- nationSizeVariation  (no superpowers).
    //  3. Landmasses too small for a nation of their own become colonies of the nearest nation.
    //  The result (owner per province + colors) is stored in _pending* and registered in WorldManager on the main thread.
    private void GenerateNations()
    {
        int pc = _provinces.Count;
        if (pc == 0) return;
        int land = _landIdx.Length;

        // adjacency
        var adj = new List<int>[pc];
        for (int i = 0; i < pc; i++) adj[i] = new List<int>(6);
        int n = _w * _h;
        for (int li = 0; li < land; li++)
        {
            int i = _landIdx[li];
            int a = _tileProvince[i];
            if (i % _w < _w - 1) LinkProvinces(adj, a, _tileProvince[i + 1]);
            if (i + _w < n) LinkProvinces(adj, a, _tileProvince[i + _w]);
        }

        // keep the land borders on the provinces (used by AI / gameplay)
        for (int i = 0; i < pc; i++) _provinces[i].Neighbors = adj[i].ToArray();

        // connected components of the province graph (islands / continents)
        var compOf = new int[pc];
        for (int i = 0; i < pc; i++) compOf[i] = -1;
        var comps = new List<List<int>>();
        var compWeight = new List<int>();
        var stack = new List<int>();
        for (int s = 0; s < pc; s++)
        {
            if (compOf[s] >= 0) continue;
            var comp = new List<int>();
            int weight = 0;
            int ci = comps.Count;
            compOf[s] = ci; stack.Add(s);
            while (stack.Count > 0)
            {
                int c = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                comp.Add(c);
                weight += _provinces[c].TileCount;
                foreach (int m in adj[c])
                    if (compOf[m] < 0) { compOf[m] = ci; stack.Add(m); }
            }
            comps.Add(comp);
            compWeight.Add(weight);
        }

        // how many nations?
        int minN = Mathf.Max(1, _s.minNations);
        int maxN = Mathf.Max(minN, _s.maxNations);
        int target = Mathf.Clamp(_rng.NextInt(minN, maxN + 1), 1, Mathf.Max(1, pc / 2));
        float avgNation = (float)land / target;

        // which landmasses are big enough for their own nation(s)?
        float minIsland = avgNation * Mathf.Clamp01(_s.minIslandNationFactor);
        var isMajor = new bool[comps.Count];
        var major = new List<int>();
        int majorLand = 0;
        for (int ci = 0; ci < comps.Count; ci++)
        {
            if (compWeight[ci] < minIsland) continue;
            isMajor[ci] = true; major.Add(ci); majorLand += compWeight[ci];
        }
        if (major.Count == 0)                       // only tiny islands exist -> all of them count
        {
            for (int ci = 0; ci < comps.Count; ci++) { isMajor[ci] = true; major.Add(ci); }
            majorLand = land;
        }

        // seeds + fair quota per nation inside each landmass
        float variation = Mathf.Clamp(_s.nationSizeVariation, 0f, 0.8f);
        var seedLists = new List<List<int>>(major.Count);
        var quota = new List<float>();
        int regions = 0;
        foreach (int ci in major)
        {
            var comp = comps[ci];
            int k = Mathf.Clamp(Mathf.RoundToInt(target * (float)compWeight[ci] / majorLand), 1, comp.Count);
            var seeds = PickProvinceSeeds(comp, k);
            seedLists.Add(seeds);

            var f = new float[seeds.Count];
            float fs = 0f;
            for (int j = 0; j < f.Length; j++) { f[j] = 1f + _rng.NextFloat(-variation, variation); fs += f[j]; }
            for (int j = 0; j < f.Length; j++) quota.Add(compWeight[ci] * f[j] / fs);
            regions += seeds.Count;
        }
        float[] targets = quota.ToArray();

        var owner = new int[pc];
        for (int i = 0; i < pc; i++) owner[i] = -1;
        var frontiers = new List<int>[regions];
        var size = new float[regions];
        var active = new List<int>(regions);
        int r = 0;
        foreach (var seeds in seedLists)
        {
            foreach (int s in seeds)
            {
                owner[s] = r;
                frontiers[r] = new List<int> { s };
                size[r] = _provinces[s].TileCount;
                active.Add(r);
                r++;
            }
        }

        var free = new List<int>(8);
        while (active.Count > 0)
        {
            // pick a region proportional to its remaining quota; when all quotas are full, pick uniformly (overflow)
            float sum = 0f;
            for (int k = 0; k < active.Count; k++) sum += Mathf.Max(0f, targets[active[k]] - size[active[k]]);

            int ai;
            if (sum > 0.5f)
            {
                float roll = _rng.NextFloat(0f, sum);
                ai = active.Count - 1;
                for (int k = 0; k < active.Count; k++)
                {
                    roll -= Mathf.Max(0f, targets[active[k]] - size[active[k]]);
                    if (roll <= 0f) { ai = k; break; }
                }
            }
            else ai = _rng.NextInt(0, active.Count);

            int reg = active[ai];
            var f = frontiers[reg];
            int fi = _rng.NextInt(0, f.Count);
            int node = f[fi];

            free.Clear();
            foreach (int m in adj[node]) if (owner[m] < 0) free.Add(m);

            if (free.Count == 0)
            {
                f[fi] = f[f.Count - 1];
                f.RemoveAt(f.Count - 1);
                if (f.Count == 0)
                {
                    active[ai] = active[active.Count - 1];
                    active.RemoveAt(active.Count - 1);
                }
                continue;
            }

            int pick = free[free.Count == 1 ? 0 : _rng.NextInt(0, free.Count)];
            owner[pick] = reg;
            size[reg] += _provinces[pick].TileCount;
            f.Add(pick);
        }

        // capitals: the province closest to the nation's (area-weighted) center. Random init stats are DOUBLED.
        var capital = new int[regions];
        {
            var sx = new double[regions];
            var sy = new double[regions];
            var area = new double[regions];
            for (int i = 0; i < pc; i++)
            {
                int o = owner[i];
                if (o < 0) continue;
                double w = _provinces[i].TileCount;
                sx[o] += _provinces[i].Centroid.x * w;
                sy[o] += _provinces[i].Centroid.y * w;
                area[o] += w;
            }

            var bestCap = new float[regions];
            for (int i = 0; i < regions; i++) { bestCap[i] = float.MaxValue; capital[i] = -1; }
            for (int i = 0; i < pc; i++)
            {
                int o = owner[i];
                if (o < 0) continue;
                var center = new Vector2((float)(sx[o] / area[o]), (float)(sy[o] / area[o]));
                float d = (_provinces[i].Centroid - center).sqrMagnitude;
                if (d < bestCap[o]) { bestCap[o] = d; capital[o] = i; }
            }

            for (int i = 0; i < regions; i++)
            {
                Province cp = _provinces[capital[i]];
                cp.Population *= 2f;
                cp.Defend.Base *= 2f;
            }
        }

        // tiny landmasses: become colonies of the nearest nation
        for (int ci = 0; ci < comps.Count; ci++)
        {
            if (isMajor[ci]) continue;
            var comp = comps[ci];

            int rep = comp[0];
            foreach (int c in comp) if (_provinces[c].TileCount > _provinces[rep].TileCount) rep = c;

            Vector2 pos = _provinces[rep].Centroid;
            float bestD = float.MaxValue;
            int bestOwner = 0;
            for (int i = 0; i < pc; i++)
            {
                if (owner[i] < 0) continue;
                float d = (_provinces[i].Centroid - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; bestOwner = owner[i]; }
            }
            foreach (int c in comp) owner[c] = bestOwner;
        }

        _pendingOwner = owner;
        _pendingCapital = capital;
        _pendingColors = MakeNationColors(regions);
    }

    private void GenerateNames(uint seed)
    {
        var rng = new Unity.Mathematics.Random(HashSeed(seed ^ 0x9E3779B9u));
        var usedP = new HashSet<string>();
        for (int i = 0; i < _provinces.Count; i++)
            _provinces[i].Name = NameManager.ProvinceName(ref rng, usedP);

        if (_pendingColors == null) return;
        var usedN = new HashSet<string>();
        _pendingNames = new string[_pendingColors.Length];
        for (int i = 0; i < _pendingNames.Length; i++)
            _pendingNames[i] = NameManager.NationName(ref rng, usedN);
    }

    /// <summary>Main thread: creates the nations in the WorldManager and gives every province to its owner.</summary>
    private void RegisterNations()
    {
        if (_pendingOwner == null || _pendingColors == null) return;

        _world.ClearNations();
        var created = new Nation[_pendingColors.Length];
        for (int i = 0; i < created.Length; i++)
            created[i] = _world.CreateNation(
            _pendingNames != null ? _pendingNames[i] : "Nation " + (i + 1), _pendingColors[i]);

        for (int i = 0; i < _provinces.Count; i++)
            _world.AssignProvince(_provinces[i], created[_pendingOwner[i]]);

        for (int i = 0; i < created.Length; i++)
            _world.SetCapital(created[i], _provinces[_pendingCapital[i]]);

        _pendingOwner = null;
        _pendingColors = null;
        _pendingCapital = null;
        _pendingNames = null;
    }

    private static void LinkProvinces(List<int>[] adj, int a, int b)
    {
        if (b < 0 || b == a) return;
        var la = adj[a];
        if (la.Contains(b)) return;
        la.Add(b);
        adj[b].Add(a);
    }

    private List<int> PickProvinceSeeds(List<int> comp, int k)
    {
        var seeds = new List<int> { comp[_rng.NextInt(0, comp.Count)] };
        var used = new HashSet<int>(seeds);
        for (int attempts = k * 60; seeds.Count < k && attempts > 0; attempts--)
        {
            int best = -1;
            float bestD = -1f;
            for (int s = 0; s < 10; s++)
            {
                int c = comp[_rng.NextInt(0, comp.Count)];
                if (used.Contains(c)) continue;
                Vector2 pc = _provinces[c].Centroid;
                float dmin = float.MaxValue;
                foreach (int e in seeds) dmin = Mathf.Min(dmin, (pc - _provinces[e].Centroid).sqrMagnitude);
                if (dmin > bestD) { bestD = dmin; best = c; }
            }
            if (best >= 0) { seeds.Add(best); used.Add(best); }
        }
        return seeds;
    }

    // ── distinct nation colors: best-candidate sampling in a perceptual-ish color space ──
    private Color[] MakeNationColors(int count)
    {
        var result = new Color[count];
        var all = new List<Color>(count + 2) { _s.waterColor, _s.neutralColor };   // nations must not look like water / neutral
        for (int i = 0; i < count; i++)
        {
            Color best = default;
            float bestScore = -1f;
            for (int s = 0; s < 32; s++)
            {
                Color c = HsvToRgb(_rng.NextFloat(0f, 1f), _rng.NextFloat(0.4f, 1f), _rng.NextFloat(0.55f, 1f));
                float score = float.MaxValue;
                for (int j = 0; j < all.Count; j++) score = Mathf.Min(score, ColorDistance(c, all[j]));
                if (score > bestScore) { bestScore = score; best = c; }
            }
            result[i] = best;
            all.Add(best);
        }
        return result;
    }

    private static float ColorDistance(Color a, Color b)   // "redmean" weighted RGB distance
    {
        float rm = (a.r + b.r) * 0.5f;
        float dr = a.r - b.r, dg = a.g - b.g, db = a.b - b.b;
        return Mathf.Sqrt((2f + rm) * dr * dr + 4f * dg * dg + (3f - rm) * db * db);
    }

    private static Color HsvToRgb(float h, float s, float v)
    {
        h = (h % 1f) * 6f;
        int i = (int)h;
        float f = h - i;
        float p = v * (1f - s), q = v * (1f - s * f), t = v * (1f - s * (1f - f));
        switch (i % 6)
        {
            case 0: return new Color(v, t, p);
            case 1: return new Color(q, v, p);
            case 2: return new Color(p, v, t);
            case 3: return new Color(p, q, v);
            case 4: return new Color(t, p, v);
            default: return new Color(v, p, q);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  VISUALS  (main thread)
    //   Overview : one texture, ~1024px max, always visible, nation borders only.
    //   Chunks   : 128x128-tile detail pieces (color 1px/tile + Alpha8 border texture) built only for
    //              what the camera sees, with province (40%) and nation (80%) borders.
    // ─────────────────────────────────────────────────────────────────────
    private void BuildRendering()
    {
        _cs = Mathf.Clamp(_s.chunkSize, 32, 256);
        _ppt = Mathf.Clamp(_s.borderPixelsPerTile, 4, 16);
        _provPx = Mathf.Clamp(_s.provinceBorderPixels, 1, Mathf.Max(1, _ppt / 2));
        _nationPx = Mathf.Clamp(_s.nationBorderPixels, 1, Mathf.Max(1, _ppt / 2));
        _provAlpha = (byte)Mathf.RoundToInt(_s.provinceBorderAlpha * 255f);
        _nationAlpha = (byte)Mathf.RoundToInt(_s.nationBorderAlpha * 255f);
        _borderRgb = _s.borderColor;

        foreach (var p in _provinces) UpdateProvinceColor(p);

        BuildOverview();

        _ccx = CeilDiv(_w, _cs);
        _ccy = CeilDiv(_h, _cs);
        _chunks = new Chunk[_ccx * _ccy];
        _chunkColorBuf = new Color32[_cs * _cs];
        _chunkBorderBuf = new byte[_cs * _ppt * _cs * _ppt];
        var rootGo = new GameObject("Map Chunks");
        rootGo.transform.SetParent(transform, false);
        rootGo.transform.localPosition = PivotOffset;
        _chunkRoot = rootGo.transform;

        _chunkWorkPending = true;
        _lastZoom = -1f;
    }

    private static int CeilDiv(int a, int b) => (a + b - 1) / b;

    private void UpdateProvinceColor(Province p)
    {
        Color c = p.Owner != null ? p.Owner.Color : _s.neutralColor;
        p.Color = new Color(c.r * p.Shade, c.g * p.Shade, c.b * p.Shade, 1f);
    }

    private Color32 TileColor(int i)
    {
        if (_tiles[i] == TileType.Water) return _s.waterColor;
        int p = _tileProvince[i];
        return p >= 0 ? _provinces[p].Color : (Color32)_s.neutralColor;
    }
    private void BuildCapitalMarkers()
    {
        DestroyMarkers();
        if (!capitalMarkerPrefab || _world == null) return;

        var root = new GameObject("Capital Markers");
        root.transform.SetParent(transform, false);
        root.transform.localPosition = PivotOffset;
        _markerRoot = root.transform;

        RefreshCapitalMarkers();
    }

    private void RefreshCapitalMarkers()
    {
        if (_markerRoot == null || !capitalMarkerPrefab) return;
        var pool = PoolManager.Instance;

        foreach (Nation n in _world.Nations)
        {
            Province cap = n.Capital;
            _markers.TryGetValue(n.Id, out GameObject go);

            if (cap == null)
            {
                if (go != null) pool.Despawn(go);
                _markers.Remove(n.Id);
                continue;
            }

            if (go == null || !go.activeSelf)     // missing or collected by the pool
            {
                go = pool.Spawn(capitalMarkerPrefab, _markerRoot);
                _markers[n.Id] = go;
            }

            go.transform.localPosition = new Vector3(
                (cap.Centroid.x + 0.5f) * _s.tileSize, (cap.Centroid.y + 0.5f) * _s.tileSize, 0f);

            var sr = go.GetComponentInChildren<SpriteRenderer>();
            if (!sr) continue;
            sr.color = InverseColor(n.Color);
            sr.sortingOrder = markerSortingOrder;

            float tiles = Mathf.Max(markerMinTiles, Mathf.Sqrt(cap.TileCount) * markerProvinceFraction);
            float spriteSize = sr.sprite ? Mathf.Max(0.0001f, sr.sprite.bounds.size.x) : 1f;
            float s = tiles * _s.tileSize / spriteSize;
            go.transform.localScale = new Vector3(s, s, 1f);
        }
    }

    private void DestroyMarkers()
    {
        if (PoolManager.HasInstance)
            foreach (var kv in _markers) if (kv.Value != null) PoolManager.Instance.Despawn(kv.Value);
        _markers.Clear();
        if (_markerRoot) Destroy(_markerRoot.gameObject);
        _markerRoot = null;
    }
    private static Color InverseColor(Color c)
    {
        var inv = new Color(1f - c.r, 1f - c.g, 1f - c.b, 1f);
        float dr = inv.r - c.r, dg = inv.g - c.g, db = inv.b - c.b;
        if (dr * dr + dg * dg + db * db < 0.15f)               // too close (greys) -> black / white
        {
            float lum = c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;
            return lum > 0.5f ? Color.black : Color.white;
        }
        return inv;
    }

    // ── overview ──
    private void BuildOverview()
    {
        int maxSide = Mathf.Clamp(_s.overviewMaxSize, 128, 4096);
        _ovScale = Mathf.Max(1, CeilDiv(Mathf.Max(_w, _h), maxSide));
        _ovW = CeilDiv(_w, _ovScale);
        _ovH = CeilDiv(_h, _ovScale);
        _ovPx = new Color32[_ovW * _ovH];
        for (int y = 0; y < _ovH; y++)
            for (int x = 0; x < _ovW; x++) PaintOverviewPixel(x, y);

        _ovTex = new Texture2D(_ovW, _ovH, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        _ovTex.SetPixelData(_ovPx, 0);
        _ovTex.Apply(false);
        _ovSprite = Sprite.Create(_ovTex, new Rect(0, 0, _ovW, _ovH), Vector2.zero, 1f / (_ovScale * _s.tileSize), 0, SpriteMeshType.FullRect);

        _ovGo = new GameObject("Map Overview");
        _ovGo.transform.SetParent(transform, false);
        _ovGo.transform.localPosition = PivotOffset;
        var sr = _ovGo.AddComponent<SpriteRenderer>();
        sr.sprite = _ovSprite;
        sr.sortingOrder = 0;
    }

    private int OverviewTile(int px, int py) =>
        Mathf.Min(_h - 1, py * _ovScale + _ovScale / 2) * _w + Mathf.Min(_w - 1, px * _ovScale + _ovScale / 2);

    private int OverviewKey(int px, int py)      // water = -2, neutral land = -1, otherwise nation id
    {
        if (px < 0 || py < 0 || px >= _ovW || py >= _ovH) return -2;
        int t = OverviewTile(px, py);
        if (_tiles[t] == TileType.Water) return -2;
        return _provinces[_tileProvince[t]].OwnerNationId;
    }

    private void PaintOverviewPixel(int px, int py)
    {
        int t = OverviewTile(px, py);
        if (_tiles[t] == TileType.Water) { _ovPx[py * _ovW + px] = _s.waterColor; return; }

        Province p = _provinces[_tileProvince[t]];
        Color32 col = p.Color;
        int key = p.OwnerNationId;
        // 1px nation border: only the side with the higher key is darkened (water counts as lowest)
        if (OverviewKey(px - 1, py) < key || OverviewKey(px + 1, py) < key ||
            OverviewKey(px, py - 1) < key || OverviewKey(px, py + 1) < key)
            col = Color32.Lerp(col, _borderRgb, _s.nationBorderAlpha);
        _ovPx[py * _ovW + px] = col;
    }

    private void RepaintOverview(int minX, int minY, int maxX, int maxY)   // tile rect
    {
        int x0 = Mathf.Max(0, minX / _ovScale - 1), x1 = Mathf.Min(_ovW - 1, maxX / _ovScale + 1);
        int y0 = Mathf.Max(0, minY / _ovScale - 1), y1 = Mathf.Min(_ovH - 1, maxY / _ovScale + 1);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++) PaintOverviewPixel(x, y);
        _ovTex.SetPixelData(_ovPx, 0);
        _ovTex.Apply(false);
    }

    // ── chunks ──
    private void UpdateChunkVisibility()
    {
        Camera cam = viewCamera ? viewCamera : Camera.main;
        if (!cam) return;

        Transform ct = cam.transform;
        float zoom = cam.orthographic ? cam.orthographicSize : cam.fieldOfView;
        if (!_chunkWorkPending && ct.position == _lastCamPos && ct.rotation == _lastCamRot &&
            zoom == _lastZoom && cam.aspect == _lastAspect) return;
        _lastCamPos = ct.position; _lastCamRot = ct.rotation; _lastZoom = zoom; _lastAspect = cam.aspect;
        _chunkWorkPending = false;

        if (!TryGetVisibleChunks(cam, out int cx0, out int cy0, out int cx1, out int cy1) ||
            (cx1 - cx0 + 1) * (cy1 - cy0 + 1) > _s.maxDetailChunks)
        {
            ReleaseAllChunks();          // zoomed out: overview only
            return;
        }

        int n0x = Mathf.Max(0, cx0 - 1), n1x = Mathf.Min(_ccx - 1, cx1 + 1);
        int n0y = Mathf.Max(0, cy0 - 1), n1y = Mathf.Min(_ccy - 1, cy1 + 1);

        for (int i = 0; i < _chunks.Length; i++)
        {
            Chunk c = _chunks[i];
            if (c != null && (c.cx < n0x || c.cx > n1x || c.cy < n0y || c.cy > n1y)) ReleaseChunk(c);
        }

        int budget = Mathf.Max(1, _s.chunkBuildsPerFrame);
        for (int cy = cy0; cy <= cy1; cy++)                       // visible first
            for (int cx = cx0; cx <= cx1; cx++)
                TryBuildChunk(cx, cy, ref budget);

        for (int cy = n0y; cy <= n1y; cy++)                       // then a 1-chunk margin to avoid pop-in
            for (int cx = n0x; cx <= n1x; cx++)
                if (cx < cx0 || cx > cx1 || cy < cy0 || cy > cy1)
                    TryBuildChunk(cx, cy, ref budget);
    }

    private void TryBuildChunk(int cx, int cy, ref int budget)
    {
        if (_chunks[cy * _ccx + cx] != null) return;
        if (budget <= 0) { _chunkWorkPending = true; return; }
        budget--;

        Chunk c = _pool.Count > 0 ? _pool.Pop() : CreateChunk();
        c.cx = cx; c.cy = cy;
        c.go.transform.localPosition = new Vector3(cx * _cs * _s.tileSize, cy * _cs * _s.tileSize, 0f);
        c.go.SetActive(true);
        FillChunk(c);
        _chunks[cy * _ccx + cx] = c;
    }

    private bool TryGetVisibleChunks(Camera cam, out int cx0, out int cy0, out int cx1, out int cy1)
    {
        cx0 = cy0 = cx1 = cy1 = 0;
        var plane = new Plane(transform.forward, transform.position);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < 4; i++)
        {
            Ray ray = cam.ViewportPointToRay(new Vector3(i & 1, i >> 1, 0f));
            if (!plane.Raycast(ray, out float d)) return false;
            Vector3 l = transform.InverseTransformPoint(ray.GetPoint(d)) - PivotOffset;
            minX = Mathf.Min(minX, l.x); maxX = Mathf.Max(maxX, l.x);
            minY = Mathf.Min(minY, l.y); maxY = Mathf.Max(maxY, l.y);
        }
        float inv = 1f / _s.tileSize;
        int tx0 = Mathf.FloorToInt(minX * inv), tx1 = Mathf.FloorToInt(maxX * inv);
        int ty0 = Mathf.FloorToInt(minY * inv), ty1 = Mathf.FloorToInt(maxY * inv);
        if (tx1 < 0 || ty1 < 0 || tx0 >= _w || ty0 >= _h) return false;

        cx0 = Mathf.Max(0, tx0) / _cs; cx1 = Mathf.Min(_w - 1, tx1) / _cs;
        cy0 = Mathf.Max(0, ty0) / _cs; cy1 = Mathf.Min(_h - 1, ty1) / _cs;
        return true;
    }

    private Chunk CreateChunk()
    {
        float ts = _s.tileSize;
        var c = new Chunk { go = new GameObject("Chunk") };
        c.go.transform.SetParent(_chunkRoot, false);

        c.color = new Texture2D(_cs, _cs, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        c.border = new Texture2D(_cs * _ppt, _cs * _ppt, TextureFormat.Alpha8, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        c.colorSprite = Sprite.Create(c.color, new Rect(0, 0, _cs, _cs), Vector2.zero, 1f / ts, 0, SpriteMeshType.FullRect);
        c.borderSprite = Sprite.Create(c.border, new Rect(0, 0, _cs * _ppt, _cs * _ppt), Vector2.zero, _ppt / ts, 0, SpriteMeshType.FullRect);

        var colorGo = new GameObject("Color");
        colorGo.transform.SetParent(c.go.transform, false);
        var csr = colorGo.AddComponent<SpriteRenderer>();
        csr.sprite = c.colorSprite;
        csr.sortingOrder = 1;

        var borderGo = new GameObject("Border");
        borderGo.transform.SetParent(c.go.transform, false);
        var bsr = borderGo.AddComponent<SpriteRenderer>();
        bsr.sprite = c.borderSprite;
        Color bc = _s.borderColor; bc.a = 1f;
        bsr.color = bc;                       // Alpha8 texture = alpha only, tinted by the border color
        bsr.sortingOrder = 2;
        return c;
    }

    private void FillChunk(Chunk c)
    {
        int x0 = c.cx * _cs, y0 = c.cy * _cs;
        for (int ly = 0; ly < _cs; ly++)
        {
            int gy = y0 + ly;
            for (int lx = 0; lx < _cs; lx++)
            {
                int gx = x0 + lx;
                _chunkColorBuf[ly * _cs + lx] = (gx < _w && gy < _h) ? TileColor(gy * _w + gx) : default;
            }
        }
        c.color.SetPixelData(_chunkColorBuf, 0);
        c.color.Apply(false);

        Array.Clear(_chunkBorderBuf, 0, _chunkBorderBuf.Length);
        DrawChunkBorders(x0, y0);
        c.border.SetPixelData(_chunkBorderBuf, 0);
        c.border.Apply(false);
    }

    // Each land tile paints a strip INSIDE itself along every edge that touches a different province.
    //   same nation on both sides -> province border (thin, 40% alpha)
    //   different nation / water  -> nation border   (bold, 80% alpha)
    private void DrawChunkBorders(int x0, int y0)
    {
        int stride = _cs * _ppt;
        int xMax = Mathf.Min(_cs, _w - x0), yMax = Mathf.Min(_cs, _h - y0);
        for (int ly = 0; ly < yMax; ly++)
        {
            int gy = y0 + ly;
            for (int lx = 0; lx < xMax; lx++)
            {
                int gx = x0 + lx;
                int pa = _tileProvince[gy * _w + gx];
                if (pa < 0) continue;
                int na = _provinces[pa].OwnerNationId;
                int px = lx * _ppt, py = ly * _ppt;

                if (Edge(pa, na, gx - 1, gy, out byte a, out int t)) Paint(px, py, t, _ppt, a, stride);               // left
                if (Edge(pa, na, gx + 1, gy, out a, out t)) Paint(px + _ppt - t, py, t, _ppt, a, stride);             // right
                if (Edge(pa, na, gx, gy - 1, out a, out t)) Paint(px, py, _ppt, t, a, stride);                        // bottom
                if (Edge(pa, na, gx, gy + 1, out a, out t)) Paint(px, py + _ppt - t, _ppt, t, a, stride);             // top
            }
        }
    }

    private bool Edge(int pa, int na, int nx, int ny, out byte alpha, out int thickness)
    {
        alpha = 0; thickness = 0;
        int pb = InBounds(nx, ny) ? _tileProvince[ny * _w + nx] : -1;
        if (pb == pa) return false;

        bool nationBorder = pb < 0 || _provinces[pb].OwnerNationId != na;
        alpha = nationBorder ? _nationAlpha : _provAlpha;
        thickness = nationBorder ? _nationPx : _provPx;
        return true;
    }

    private void Paint(int px, int py, int w, int h, byte alpha, int stride)
    {
        for (int yy = 0; yy < h; yy++)
        {
            int row = (py + yy) * stride + px;
            for (int xx = 0; xx < w; xx++)
                if (_chunkBorderBuf[row + xx] < alpha) _chunkBorderBuf[row + xx] = alpha;
        }
    }

    private void RefreshProvinceVisuals(Province p)
    {
        int minX = Mathf.Max(0, p.Min.x - 1), minY = Mathf.Max(0, p.Min.y - 1);
        int maxX = Mathf.Min(_w - 1, p.Max.x + 1), maxY = Mathf.Min(_h - 1, p.Max.y + 1);

        RepaintOverview(minX, minY, maxX, maxY);

        for (int cy = minY / _cs; cy <= maxY / _cs; cy++)
            for (int cx = minX / _cs; cx <= maxX / _cs; cx++)
            {
                Chunk c = _chunks[cy * _ccx + cx];
                if (c != null) FillChunk(c);       // only loaded (visible) chunks cost anything
            }
    }

    private void ReleaseChunk(Chunk c)
    {
        _chunks[c.cy * _ccx + c.cx] = null;
        c.go.SetActive(false);
        if (_pool.Count < _s.maxPooledChunks) _pool.Push(c);
        else DestroyChunk(c);
    }

    private void ReleaseAllChunks()
    {
        if (_chunks == null) return;
        for (int i = 0; i < _chunks.Length; i++)
            if (_chunks[i] != null) ReleaseChunk(_chunks[i]);
    }

    private void DestroyChunk(Chunk c)
    {
        if (c.colorSprite) Destroy(c.colorSprite);
        if (c.borderSprite) Destroy(c.borderSprite);
        if (c.color) Destroy(c.color);
        if (c.border) Destroy(c.border);
        if (c.go) Destroy(c.go);
    }
}