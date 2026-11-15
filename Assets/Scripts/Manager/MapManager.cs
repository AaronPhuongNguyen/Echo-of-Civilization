using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

#region Data types

/// <summary>Add Dirt, Sand, Mountain... here later. Colors are resolved in MapManager.TileColor().</summary>
public enum TileType : byte { Water = 0, Land = 1 }

[Serializable]
public class MergeSettings
{
    [Min(2)] public int maxProvincesPerMerge = 8;
    [Tooltip("Treasury price per ABSORBED province (0 = free).")]
    [Min(0f)] public float costPerAbsorbedProvince = 0f;
    [Tooltip("ON: Defend is summed (total defence kept). OFF: tile-weighted average.")]
    public bool sumDefend = true;
    [Tooltip("Refuse to merge provinces that are occupied in a war or have a battle.")]
    public bool blockWhileOccupied = true;
}

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

    [Header("Sea blocks (water tiles grouped into big regions, like provinces)")]
    [Tooltip("Average tiles per sea block. Bigger = fewer blocks = cheaper sea logic.")]
    public int seaTilesPerBlock = 6000;
    [Tooltip("Connected water bodies smaller than this many tiles are flagged as lakes.")]
    public int lakeMaxTiles = 20000;
    [Tooltip("Alpha of the faint line between two sea blocks in detail chunks (0 = hidden).")]
    [Range(0f, 1f)] public float seaBorderAlpha = 0.12f;

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
    public Color waterColor = new Color(0.12f, 0.32f, 0.58f);
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
    [Tooltip("Max chunk builds / refreshes per frame (shared by camera moves and ownership changes).")]
    public int chunkBuildsPerFrame = 2;
    public int maxPooledChunks = 6;
    public bool generateOnBackgroundThread = true;
    [Tooltip("Height-map rows are computed on several CPU cores.")]
    public bool parallelGeneration = true;

    public MapSettings Clone() => (MapSettings)MemberwiseClone();
}

/// <summary>
/// A BLOCK of water tiles (an ocean piece, a sea or a lake). Works like a province but at sea:
/// run your naval / sea logic on these (a few hundred blocks) instead of on millions of water tiles.
/// </summary>
public class SeaRegion
{
    public int Id;
    public string Name = "";
    public int[] Tiles = Array.Empty<int>();                 // tile indices (y * width + x)
    public int TileCount => Tiles.Length;
    public Vector2 Centroid;                                 // tile coordinates
    public Vector2Int Min, Max;                              // tile bounding box (inclusive)
    public int[] Neighbors = Array.Empty<int>();             // ids of sea blocks sharing a water border
    public int[] LandNeighbors = Array.Empty<int>();         // ids of provinces touching this block (its coast)
    public bool IsLake;                                      // part of a small, enclosed water body
    public bool IsCoastal => LandNeighbors.Length > 0;
}

#endregion

// ─────────────────────────────────────────────────────────────────────────────
//  MAP MANAGER
//
//  Memory/CPU design for 500x500 .. 2048x2048 on phones:
//   - generation (including the overview pixels) runs on a background thread, flat arrays only
//   - provinces AND sea blocks store exact int[] tile lists; height uses a ushort histogram (no sort)
//   - rendering = 1 low-res OVERVIEW texture + DETAIL CHUNKS only for what the camera sees
//   - every visual change is batched: ONE overview upload and a budgeted number of chunk refreshes per frame
//   - nations are stored in WorldManager (registered on the main thread after generation)
// ─────────────────────────────────────────────────────────────────────────────

public partial class MapManager : MonoBehaviour
{
    #region Inspector & events

    public MapSettings settings = new MapSettings();
    [SerializeField] private bool generateOnStart = true;
    [Tooltip("Only used to decide which detail chunks are visible. Defaults to Camera.main.")]
    [SerializeField] private Camera viewCamera;

    [Header("Province merging")]
    public MergeSettings merge = new MergeSettings();

    [Header("Capital marker")]
    [Tooltip("Prefab with a SpriteRenderer (white star). Tinted with the inverse nation color.")]
    [SerializeField] private GameObject capitalMarkerPrefab;
    [Tooltip("Marker size = capital province size (sqrt of tiles) * this.")]
    [SerializeField, Range(0.1f, 1.5f)] private float markerProvinceFraction = 0.6f;
    [Tooltip("Minimum marker size in tiles, so it stays visible in tiny provinces.")]
    [SerializeField] private float markerMinTiles = 3f;
    [SerializeField] private int markerSortingOrder = 10;

    public event Action OnMapGenerated;
    public event Action<Province, int, int> OnProvinceOwnerChanged;   // province, oldNationId, newNationId
    /// <summary>origin (the leader: its name is kept by default), absorbed provinces.</summary>
    public event Action<Province, IReadOnlyList<Province>> OnProvincesMerged;

    public string LastMergeError { get; private set; }
    public bool IsGenerating { get; private set; }
    public bool IsReady { get; private set; }

    #endregion

    #region Data

    private MapSettings _s = new MapSettings();               // snapshot used during/after generation
    private int _w, _h;
    private TileType[] _tiles;
    private int[] _tileProvince;                              // province id per tile, -1 = water
    private int[] _tileSea;                                   // sea block id per tile, -1 = land
    private int[] _landIdx;                                   // indices of all land tiles
    private int[] _ownerKey;                                  // nation id per province (fast copy of OwnerNationId)
    private int[][] _coastSeas;                               // sea block ids touching each province
    private readonly List<Province> _provinces = new List<Province>();
    private readonly List<SeaRegion> _seas = new List<SeaRegion>();
    private WorldManager _world;                              // owns the nations
    private Unity.Mathematics.Random _rng;                    // local generator (thread-safe, seeded from RNG.Seed)
    private bool _destroyed;

    // generation results handed to the main thread
    private int[] _pendingOwner;                              // region index per province
    private Color[] _pendingColors;                           // color per region
    private int[] _pendingCapital;                            // capital province id per region
    private string[] _pendingNames;                           // name per region

    public int Width => _w;
    public int Height => _h;
    public IReadOnlyList<Province> Provinces => _provinces;
    public IReadOnlyList<SeaRegion> Seas => _seas;
    public IReadOnlyList<Nation> Nations => _world != null ? _world.Nations : null;

    /// <summary>Offset from this transform to the map's bottom-left corner (local space). Pivot = map center.</summary>
    private Vector3 PivotOffset => _s.pivotAtCenter
        ? new Vector3(-_w * _s.tileSize * 0.5f, -_h * _s.tileSize * 0.5f, 0f)
        : Vector3.zero;

    #endregion

    #region Unity

    private void Start() { if (generateOnStart) Generate(); }

    private void LateUpdate()
    {
        if (!IsReady) return;
        int budget = Mathf.Max(1, _s.chunkBuildsPerFrame);   // one shared budget per frame
        FlushVisuals(ref budget);
        FlushMarkers();
        UpdateChunkVisibility(ref budget);
    }

    private void OnDestroy() { _destroyed = true; Clear(); }

    private void OnValidate()
    {
        settings.width = Mathf.Clamp(settings.width, 16, 4096);
        settings.height = Mathf.Clamp(settings.height, 16, 4096);
        settings.tileSize = Mathf.Max(0.01f, settings.tileSize);
        settings.minProvinces = Mathf.Max(1, settings.minProvinces);
        settings.provinceMaxDivisor = Mathf.Max(1, settings.provinceMaxDivisor);
        settings.seaTilesPerBlock = Mathf.Max(256, settings.seaTilesPerBlock);
        settings.lakeMaxTiles = Mathf.Max(0, settings.lakeMaxTiles);
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

    #region Public API: generate / clear / ownership

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
            InitRenderParams();

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
        _seas.Clear();
        _tiles = null;
        _tileProvince = null;
        _tileSea = null;
        _landIdx = null;
        _ownerKey = null;
        _coastSeas = null;
        _pendingOwner = null;
        _pendingColors = null;
        _pendingCapital = null;
        _pendingNames = null;
        _ovDirty = false;
        _anyChunkDirty = false;
        _chunkDirty = null;
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
        _ownerKey[provinceId] = nationId;
        UpdateProvinceColor(p);
        MarkProvinceDirty(p);                 // repainted once, next frame
        MarkMarkerDirty(old);
        MarkMarkerDirty(nationId);

        OnProvinceOwnerChanged?.Invoke(p, old, nationId);
        return true;
    }

    #endregion

    #region Public API: queries

    public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < _w && y < _h;
    public TileType GetTileType(int x, int y) => _tiles[y * _w + x];
    public bool IsLand(int x, int y) => InBounds(x, y) && _tiles[y * _w + x] == TileType.Land;
    public bool IsWater(int x, int y) => InBounds(x, y) && _tiles[y * _w + x] == TileType.Water;

    public int GetProvinceId(int x, int y) => InBounds(x, y) ? _tileProvince[y * _w + x] : -1;
    public Province GetProvince(int id) => id >= 0 && id < _provinces.Count ? _provinces[id] : null;
    public Nation GetNation(int id) => _world != null ? _world.GetNation(id) : null;

    public int GetNationId(int x, int y)
    {
        int p = GetProvinceId(x, y);
        return p < 0 ? -1 : _provinces[p].OwnerNationId;
    }

    /// <summary>Sea block id of a water tile, -1 for land / out of bounds.</summary>
    public int GetSeaId(int x, int y) => InBounds(x, y) ? _tileSea[y * _w + x] : -1;
    public SeaRegion GetSea(int id) => id >= 0 && id < _seas.Count ? _seas[id] : null;

    /// <summary>Sea blocks touching this province (empty = landlocked).</summary>
    public IReadOnlyList<int> GetCoastalSeas(Province p) =>
        p != null && _coastSeas != null && p.Id >= 0 && p.Id < _coastSeas.Length ? _coastSeas[p.Id] : Array.Empty<int>();

    public bool IsCoastal(Province p) => GetCoastalSeas(p).Count > 0;

    public bool TryWorldToTile(Vector3 world, out int x, out int y)
    {
        Vector3 local = transform.InverseTransformPoint(world) - PivotOffset;
        x = Mathf.FloorToInt(local.x / _s.tileSize);
        y = Mathf.FloorToInt(local.y / _s.tileSize);
        return InBounds(x, y);
    }

    public Vector3 TileToWorldCenter(int x, int y) =>
        transform.TransformPoint(new Vector3((x + 0.5f) * _s.tileSize, (y + 0.5f) * _s.tileSize, 0f) + PivotOffset);

    /// <summary>Local offset of the map's bottom-left corner (the overlay uses it).</summary>
    public Vector3 MapOrigin => PivotOffset;

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

    public SeaRegion GetSeaAt(Vector3 world) =>
        IsReady && TryWorldToTile(world, out int x, out int y) ? GetSea(GetSeaId(x, y)) : null;

    #endregion

    // ═════════════════════════════════════════════════════════════════════
    //  GENERATION  (everything in the next regions may run on a background thread)
    // ═════════════════════════════════════════════════════════════════════

    #region Generation: pipeline & RNG helpers

    private void GenerateData(uint seed)
    {
        _rng = new Unity.Mathematics.Random(HashSeed(seed));
        GenerateWorld();
        GenerateProvinces();
        GenerateSeas();
        GenerateAdjacency();
        GenerateNations();
        GenerateNames(seed);
        PrepareVisuals();
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
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static void Fill(int[] a, int v) { for (int i = 0; i < a.Length; i++) a[i] = v; }

    // one RNG call gives 16 "75% yes" decisions (2 bits each) -> far fewer RNG calls while growing regions
    private uint _bits;
    private int _bitsLeft;

    private bool Coin75()
    {
        if (_bitsLeft == 0) { _bits = _rng.NextUInt(); _bitsLeft = 16; }
        bool yes = (_bits & 3u) != 0u;
        _bits >>= 2;
        _bitsLeft--;
        return yes;
    }

    private void RunRows(int rows, Action<int> body)
    {
        if (_s.parallelGeneration) Parallel.For(0, rows, body);
        else for (int y = 0; y < rows; y++) body(y);
    }

    #endregion

    #region Generation: world (continents, land / water)

    // fractal simplex noise + edge falloff; exact land % via a 65536-bin histogram (no sorting)
    private void GenerateWorld()
    {
        int w = _w, h = _h, n = w * h;
        var hmap = new ushort[n];
        float minSide = Mathf.Min(w, h);

        // 1) continent seeds: spread out (best-candidate), each one a rotated, stretched blob
        int cc = _rng.NextInt(Mathf.Max(1, _s.minContinents), Mathf.Max(_s.minContinents, _s.maxContinents) + 1);
        var cx = new float[cc]; var cy = new float[cc];
        var a1 = new float[cc]; var a2 = new float[cc]; var b1 = new float[cc]; var b2 = new float[cc];
        var invR2 = new float[cc];
        for (int i = 0; i < cc; i++)
        {
            float bx = 0f, by = 0f, bestD = -1f;
            for (int t = 0; t < 16; t++)
            {
                float px = _rng.NextFloat(0.18f, 0.82f) * w;
                float py = _rng.NextFloat(0.18f, 0.82f) * h;
                float dmin = float.MaxValue;
                for (int j = 0; j < i; j++) dmin = Mathf.Min(dmin, (px - cx[j]) * (px - cx[j]) + (py - cy[j]) * (py - cy[j]));
                if (i == 0 || dmin > bestD) { bestD = dmin; bx = px; by = py; }
            }
            cx[i] = bx; cy[i] = by;
            float radius = _s.continentRadius * minSide * _rng.NextFloat(0.7f, 1.3f);
            float ang = _rng.NextFloat(0f, Mathf.PI);
            float cos = Mathf.Cos(ang), sin = Mathf.Sin(ang);
            float sx = _rng.NextFloat(0.75f, 1.4f), sy = _rng.NextFloat(0.75f, 1.4f);
            // pre-folded rotation + stretch: rx = dx*a1 + dy*a2, ry = dx*b1 + dy*b2
            a1[i] = cos / sx; a2[i] = sin / sx;
            b1[i] = -sin / sy; b2[i] = cos / sy;
            invR2[i] = 1f / (radius * radius);
        }

        float ox = _rng.NextFloat(0f, 1000f), oy = _rng.NextFloat(0f, 1000f);
        float wox = _rng.NextFloat(0f, 1000f), woy = _rng.NextFloat(0f, 1000f);
        float baseFreq = 1f / Mathf.Max(1f, _s.featureSize);
        float warpFreq = 1f / (minSide * 0.3f);
        float warpAmp = _s.coastWarp * minSide;
        float cw = Mathf.Clamp01(_s.continentWeight);
        float persistence = _s.persistence, lacunarity = _s.lacunarity, edgeWater = _s.edgeWater;
        int octaves = Mathf.Max(1, _s.octaves);

        // 2) domain warp is very low frequency: sample it on a coarse grid and interpolate (2 noise calls per 64 tiles)
        const int Step = 8;
        int gw = w / Step + 2, gh = h / Step + 2;
        var warpX = new float[gw * gh];
        var warpY = new float[gw * gh];
        for (int gy = 0; gy < gh; gy++)
            for (int gx = 0; gx < gw; gx++)
            {
                float sx = gx * Step * warpFreq, sy = gy * Step * warpFreq;
                warpX[gy * gw + gx] = Noise(wox + sx, woy + sy) * warpAmp;
                warpY[gy * gw + gx] = Noise(wox + 500f + sx, woy + 500f + sy) * warpAmp;
            }

        void Row(int y)
        {
            int gy = y / Step;
            float fy = (y - gy * Step) / (float)Step;
            int g0 = gy * gw, g1 = g0 + gw;
            float dy = (y + 0.5f) / h * 2f - 1f;

            for (int x = 0; x < w; x++)
            {
                int gx = x / Step;
                float fx = (x - gx * Step) / (float)Step;
                float wx = Lerp(Lerp(warpX[g0 + gx], warpX[g0 + gx + 1], fx), Lerp(warpX[g1 + gx], warpX[g1 + gx + 1], fx), fy);
                float wy = Lerp(Lerp(warpY[g0 + gx], warpY[g0 + gx + 1], fx), Lerp(warpY[g1 + gx], warpY[g1 + gx + 1], fx), fy);
                float px = x + wx, py = y + wy;

                // 3) continent mask: smallest normalized distance wins -> a single sqrt per tile
                float best = float.MaxValue;
                for (int c = 0; c < cc; c++)
                {
                    float ddx = px - cx[c], ddy = py - cy[c];
                    float rx = ddx * a1[c] + ddy * a2[c];
                    float ry = ddx * b1[c] + ddy * b2[c];
                    float d2 = (rx * rx + ry * ry) * invR2[c];
                    if (d2 < best) best = d2;
                }
                float mask = Mathf.Clamp01(1f - Mathf.Sqrt(best));
                mask = mask * mask * (3f - 2f * mask);

                // 4) fractal noise for coastline detail, bays and peninsulas
                float v = 0f, amp = 1f, total = 0f, f = baseFreq;
                for (int o = 0; o < octaves; o++)
                {
                    v += Noise(ox + x * f, oy + y * f) * amp;
                    total += amp;
                    amp *= persistence;
                    f *= lacunarity;
                }
                v = v / total * 0.5f + 0.5f;

                float dx = (x + 0.5f) / w * 2f - 1f;
                float d = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));      // 0 center .. 1 edge
                float height = mask * cw + v * (1f - cw) - edgeWater * d * d;

                hmap[y * w + x] = (ushort)Mathf.Clamp((int)((height + 1f) * 0.5f * 65535f), 0, 65535);
            }
        }
        RunRows(h, Row);

        // histogram (serial, trivial) -> exact land percentage
        var hist = new int[65536];
        for (int i = 0; i < n; i++) hist[hmap[i]]++;

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
        for (int i = 0; i < n; i++)
            if (hmap[i] >= threshold) _tiles[i] = TileType.Land;

        SmoothLand(_s.smoothingPasses);                               // round off jagged coasts
        FloodPass(TileType.Land, _s.minLandPatch, TileType.Water);    // remove tiny islands
        FloodPass(TileType.Water, _s.minWaterPatch, TileType.Land);   // fill tiny ponds (bigger ones stay as lakes/seas)

        int land = 0;
        for (int i = 0; i < n; i++) if (_tiles[i] == TileType.Land) land++;
        _landIdx = new int[land];
        int k = 0;
        for (int i = 0; i < n; i++) if (_tiles[i] == TileType.Land) _landIdx[k++] = i;
    }

    /// <summary>Majority filter (8 neighbours): >=5 land -> land, <=3 -> water. Done as two cheap 3-tap passes.</summary>
    private void SmoothLand(int passes)
    {
        if (passes <= 0) return;
        int w = _w, h = _h, n = w * h;
        var src = _tiles;
        var dst = new TileType[n];
        var rowSum = new byte[n];

        for (int pass = 0; pass < passes; pass++)
        {
            // horizontal: land count of (x-1, x, x+1)
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    int v = src[i] == TileType.Land ? 1 : 0;
                    if (x > 0 && src[i - 1] == TileType.Land) v++;
                    if (x < w - 1 && src[i + 1] == TileType.Land) v++;
                    rowSum[i] = (byte)v;
                }
            }
            // vertical: 3x3 total minus the tile itself = 8-neighbour count
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    int cnt = rowSum[i];
                    if (y > 0) cnt += rowSum[i - w];
                    if (y < h - 1) cnt += rowSum[i + w];
                    bool self = src[i] == TileType.Land;
                    if (self) cnt--;
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

    #endregion

    #region Generation: region partitioning (shared by provinces and sea blocks)

    /// <summary>
    /// Splits the tiles of 'type' (listed in idx) into ~target regions and writes the region id of every tile into 'map'
    /// (-1 elsewhere). Evenly scattered seeds + FIFO growth; disconnected landmasses / water bodies that got no seed
    /// are split into ~(size / average region size) regions. Returns the region count.
    /// </summary>
    private int PartitionTiles(int[] idx, int[] map, TileType type, int target)
    {
        int n = _w * _h, count = idx.Length;
        Fill(map, -1);

        var frontier = new Queue<int>(4096);
        int region = 0;
        foreach (int s in ScatterSeeds(idx, target))
        {
            map[s] = region++;
            frontier.Enqueue(s);
        }
        GrowTiles(map, type, frontier);

        float avg = (float)count / target;
        var comp = new List<int>();
        var stack = new List<int>();

        for (int li = 0; li < count; li++)
        {
            int s = idx[li];
            if (map[s] != -1) continue;

            comp.Clear(); stack.Clear();
            map[s] = -2; stack.Add(s);
            while (stack.Count > 0)
            {
                int c = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                comp.Add(c);
                int x = c % _w;
                if (x > 0) MarkTemp(map, type, c - 1, stack);
                if (x < _w - 1) MarkTemp(map, type, c + 1, stack);
                if (c >= _w) MarkTemp(map, type, c - _w, stack);
                if (c + _w < n) MarkTemp(map, type, c + _w, stack);
            }

            int kk = Mathf.Clamp(Mathf.RoundToInt(comp.Count / avg), 1, comp.Count);
            for (int tries = kk * 8, got = 0; got < kk && tries > 0; tries--)
            {
                int pick = comp[_rng.NextInt(0, comp.Count)];
                if (map[pick] != -2) continue;
                map[pick] = region++;
                frontier.Enqueue(pick);
                got++;
            }
            // tiles stay -2 until every component is processed so none is detected twice
        }
        for (int li = 0; li < count; li++) if (map[idx[li]] == -2) map[idx[li]] = -1;
        GrowTiles(map, type, frontier);
        return region;
    }

    private void MarkTemp(int[] map, TileType type, int t, List<int> stack)
    {
        if (map[t] == -1 && _tiles[t] == type) { map[t] = -2; stack.Add(t); }
    }

    /// <summary>Dart-throwing with a spatial grid: tiles at least ~0.6*sqrt(area/count) apart.</summary>
    private List<int> ScatterSeeds(int[] idx, int target)
    {
        int total = idx.Length;
        float r = 0.6f * Mathf.Sqrt((float)total / target);
        float r2 = r * r;
        float cell = Mathf.Max(1f, r / 1.4142f);
        int gw = (int)(_w / cell) + 1, gh = (int)(_h / cell) + 1;
        var grid = new int[gw * gh];                      // stores tile+1, 0 = empty
        var seeds = new List<int>(target);

        for (int attempts = target * 40; seeds.Count < target && attempts > 0; attempts--)
        {
            int t = idx[_rng.NextInt(0, total)];
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
        if (seeds.Count == 0) seeds.Add(idx[_rng.NextInt(0, total)]);
        return seeds;
    }

    /// <summary>
    /// Even-speed growth (FIFO queue): all regions expand at the same pace, giving compact, rounded shapes.
    /// Each free neighbour is claimed with 75% chance so the edges stay organic instead of diamond-shaped.
    /// </summary>
    private void GrowTiles(int[] map, TileType type, Queue<int> frontier)
    {
        int n = _w * _h;
        while (frontier.Count > 0)
        {
            int node = frontier.Dequeue();
            int id = map[node];
            int x = node % _w;
            bool left = false;
            if (x > 0) left |= TryClaim(map, type, node - 1, id, frontier);
            if (x < _w - 1) left |= TryClaim(map, type, node + 1, id, frontier);
            if (node >= _w) left |= TryClaim(map, type, node - _w, id, frontier);
            if (node + _w < n) left |= TryClaim(map, type, node + _w, id, frontier);
            if (left) frontier.Enqueue(node);       // still has free neighbours -> try again later
        }
    }

    // returns true if the neighbour is free but was not claimed this time
    private bool TryClaim(int[] map, TileType type, int m, int id, Queue<int> q)
    {
        if (map[m] != -1 || _tiles[m] != type) return false;
        if (Coin75()) { map[m] = id; q.Enqueue(m); return false; }
        return true;
    }

    #endregion

    #region Generation: provinces

    // count = random(minProvinces .. W*H/divisor)
    private void GenerateProvinces()
    {
        int n = _w * _h;
        int land = _landIdx.Length;
        _tileProvince = new int[n];
        if (land == 0) { Fill(_tileProvince, -1); return; }

        int maxP = Mathf.Max(_s.minProvinces, n / Mathf.Max(1, _s.provinceMaxDivisor));
        int target = _rng.NextInt(_s.minProvinces, maxP + 1);
        target = Mathf.Clamp(target, 1, Mathf.Max(1, land / Mathf.Max(1, _s.minTilesPerProvince)));

        int pc = PartitionTiles(_landIdx, _tileProvince, TileType.Land, target);

        // Province objects with exact-size tile arrays + random stats
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

    #endregion

    #region Generation: sea blocks

    // Water tiles are grouped into big blocks (one block ~ seaTilesPerBlock tiles). Every water body,
    // even a small lake, gets at least one block. Sea logic then works on a few hundred blocks, not millions of tiles.
    private void GenerateSeas()
    {
        int n = _w * _h;
        _tileSea = new int[n];
        int water = n - _landIdx.Length;
        if (water <= 0) { Fill(_tileSea, -1); return; }

        var idx = new int[water];
        int k = 0;
        for (int i = 0; i < n; i++) if (_tiles[i] == TileType.Water) idx[k++] = i;

        int target = Mathf.Max(1, water / Mathf.Max(256, _s.seaTilesPerBlock));
        int sc = PartitionTiles(idx, _tileSea, TileType.Water, target);

        var counts = new int[sc];
        for (int i = 0; i < water; i++) counts[_tileSea[idx[i]]]++;

        var sumX = new double[sc];
        var sumY = new double[sc];
        var fill = new int[sc];
        for (int i = 0; i < sc; i++)
            _seas.Add(new SeaRegion
            {
                Id = i,
                Tiles = new int[counts[i]],
                Min = new Vector2Int(int.MaxValue, int.MaxValue),
                Max = new Vector2Int(int.MinValue, int.MinValue)
            });

        for (int i = 0; i < water; i++)
        {
            int t = idx[i];
            int id = _tileSea[t];
            SeaRegion s = _seas[id];
            int x = t % _w, y = t / _w;
            s.Tiles[fill[id]++] = t;
            sumX[id] += x; sumY[id] += y;
            if (x < s.Min.x) s.Min.x = x;
            if (y < s.Min.y) s.Min.y = y;
            if (x > s.Max.x) s.Max.x = x;
            if (y > s.Max.y) s.Max.y = y;
        }
        for (int i = 0; i < sc; i++)
            _seas[i].Centroid = new Vector2((float)(sumX[i] / counts[i]), (float)(sumY[i] / counts[i]));
    }

    #endregion

    #region Generation: adjacency (province-province, sea-sea, province-sea in ONE pass)

    private sealed class AdjacencyBuilder
    {
        private readonly HashSet<long> _seen = new HashSet<long>();
        private long _last = -1;
        public readonly List<int>[] ProvProv, SeaSea, ProvSea, SeaProv;

        public AdjacencyBuilder(int provinces, int seas)
        {
            ProvProv = NewLists(provinces); SeaSea = NewLists(seas);
            ProvSea = NewLists(provinces); SeaProv = NewLists(seas);
        }

        private static List<int>[] NewLists(int n)
        {
            var l = new List<int>[n];
            for (int i = 0; i < n; i++) l[i] = new List<int>(6);
            return l;
        }

        private bool IsNew(int kind, int a, int b)
        {
            long key = ((long)kind << 60) | ((long)a << 30) | (long)b;
            if (key == _last) return false;
            _last = key;
            return _seen.Add(key);
        }

        /// <summary>Two touching tiles: (province a, sea a) and (province b, sea b); -1 = none.</summary>
        public void Edge(int pa, int sa, int pb, int sb)
        {
            if (pa == pb && sa == sb) return;
            if (pa >= 0 && pb >= 0)
            {
                int lo = pa < pb ? pa : pb, hi = pa < pb ? pb : pa;
                if (!IsNew(0, lo, hi)) return;
                ProvProv[pa].Add(pb); ProvProv[pb].Add(pa);
            }
            else if (sa >= 0 && sb >= 0)
            {
                int lo = sa < sb ? sa : sb, hi = sa < sb ? sb : sa;
                if (!IsNew(1, lo, hi)) return;
                SeaSea[sa].Add(sb); SeaSea[sb].Add(sa);
            }
            else if (pa >= 0 && sb >= 0) Coast(pa, sb);
            else if (sa >= 0 && pb >= 0) Coast(pb, sa);
        }

        private void Coast(int p, int s)
        {
            if (!IsNew(2, p, s)) return;
            ProvSea[p].Add(s);
            SeaProv[s].Add(p);
        }
    }

    private void GenerateAdjacency()
    {
        int w = _w, h = _h;
        int pc = _provinces.Count, sc = _seas.Count;
        var b = new AdjacencyBuilder(pc, sc);

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                int pa = _tileProvince[i], sa = _tileSea[i];
                if (x < w - 1) b.Edge(pa, sa, _tileProvince[i + 1], _tileSea[i + 1]);
                if (y < h - 1) b.Edge(pa, sa, _tileProvince[i + w], _tileSea[i + w]);
            }
        }

        _coastSeas = new int[pc][];
        for (int i = 0; i < pc; i++)
        {
            _provinces[i].Neighbors = b.ProvProv[i].ToArray();   // land borders (used by AI / gameplay / merging)
            _coastSeas[i] = b.ProvSea[i].ToArray();
        }
        for (int i = 0; i < sc; i++)
        {
            _seas[i].Neighbors = b.SeaSea[i].ToArray();
            _seas[i].LandNeighbors = b.SeaProv[i].ToArray();
        }

        // lakes = sea blocks whose connected water body is small
        var body = new int[sc];
        for (int i = 0; i < sc; i++) body[i] = -1;
        var stack = new List<int>();
        var members = new List<int>();
        for (int s = 0; s < sc; s++)
        {
            if (body[s] >= 0) continue;
            members.Clear(); stack.Clear();
            body[s] = s; stack.Add(s);
            int tiles = 0;
            while (stack.Count > 0)
            {
                int c = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                members.Add(c);
                tiles += _seas[c].TileCount;
                foreach (int m in _seas[c].Neighbors)
                    if (body[m] < 0) { body[m] = s; stack.Add(m); }
            }
            bool lake = tiles < _s.lakeMaxTiles;
            foreach (int m in members) _seas[m].IsLake = lake;
        }
    }

    #endregion

    #region Generation: nations

    // NATIONS (balanced)
    //  1. N nations are distributed over the landmasses in proportion to their land.
    //  2. Inside a landmass every nation gets a fair share +- nationSizeVariation  (no superpowers).
    //  3. Landmasses too small for a nation of their own become colonies of the nearest nation.
    //  The result (owner per province + colors) is stored in _pending* and registered in WorldManager on the main thread.
    private void GenerateNations()
    {
        int pc = _provinces.Count;
        if (pc == 0) return;
        int land = _landIdx.Length;

        var adj = new int[pc][];
        for (int i = 0; i < pc; i++) adj[i] = _provinces[i].Neighbors;

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

    #endregion

    #region Generation: names & colors

    private void GenerateNames(uint seed)
    {
        var rng = new Unity.Mathematics.Random(HashSeed(seed ^ 0x9E3779B9u));
        var usedP = new HashSet<string>();
        for (int i = 0; i < _provinces.Count; i++)
            _provinces[i].Name = NameManager.ProvinceName(ref rng, usedP);

        var usedS = new HashSet<string>();
        for (int i = 0; i < _seas.Count; i++)
        {
            string baseName = NameManager.ProvinceName(ref rng, usedS);
            _seas[i].Name = _seas[i].IsLake ? "Lake " + baseName : baseName + " Sea";
        }

        if (_pendingColors == null) return;
        var usedN = new HashSet<string>();
        _pendingNames = new string[_pendingColors.Length];
        for (int i = 0; i < _pendingNames.Length; i++)
            _pendingNames[i] = NameManager.NationName(ref rng, usedN);
    }

    // distinct nation colors: best-candidate sampling in a perceptual-ish color space
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

    #endregion

    #region Generation: register nations (main thread)

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

        for (int i = 0; i < _provinces.Count; i++) _ownerKey[i] = _provinces[i].OwnerNationId;

        _pendingOwner = null;
        _pendingColors = null;
        _pendingCapital = null;
        _pendingNames = null;
    }

    #endregion

    // ═════════════════════════════════════════════════════════════════════
    //  VISUALS
    //   Overview : one texture, ~1024px max, always visible, nation borders only. Pixels are built OFF the main thread.
    //   Chunks   : detail pieces (color 1px/tile + Alpha8 border texture) built only for what the camera sees.
    //   Changes are batched: one overview upload + a budgeted number of chunk refreshes per frame.
    // ═════════════════════════════════════════════════════════════════════

    #region Visuals: fields

    private class Chunk
    {
        public int cx, cy;
        public GameObject go;
        public Texture2D color, border;
        public Sprite colorSprite, borderSprite;
    }

    private int _cs, _ppt, _provPx, _nationPx;
    private byte _provAlpha, _nationAlpha, _seaAlpha;
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

    // batched dirty state
    private bool _ovDirty;
    private int _dMinX, _dMinY, _dMaxX, _dMaxY;
    private bool[] _chunkDirty;
    private bool _anyChunkDirty;

    #endregion

    #region Visuals: setup

    private void InitRenderParams()
    {
        _cs = Mathf.Clamp(_s.chunkSize, 32, 256);
        _ppt = Mathf.Clamp(_s.borderPixelsPerTile, 4, 16);
        _provPx = Mathf.Clamp(_s.provinceBorderPixels, 1, Mathf.Max(1, _ppt / 2));
        _nationPx = Mathf.Clamp(_s.nationBorderPixels, 1, Mathf.Max(1, _ppt / 2));
        _provAlpha = (byte)Mathf.RoundToInt(_s.provinceBorderAlpha * 255f);
        _nationAlpha = (byte)Mathf.RoundToInt(_s.nationBorderAlpha * 255f);
        _seaAlpha = (byte)Mathf.RoundToInt(_s.seaBorderAlpha * 255f);
        _borderRgb = _s.borderColor;
    }

    /// <summary>Background thread: province colors, owner keys and the whole overview pixel buffer.</summary>
    private void PrepareVisuals()
    {
        int pc = _provinces.Count;
        _ownerKey = new int[pc];
        for (int i = 0; i < pc; i++)
        {
            // nation ids are handed out 0..N-1 in region order by WorldManager.CreateNation
            int key = _pendingOwner != null ? _pendingOwner[i] : -1;
            _ownerKey[i] = key;
            Province p = _provinces[i];
            Color c = key >= 0 && _pendingColors != null ? _pendingColors[key] : _s.neutralColor;
            p.Color = new Color(c.r * p.Shade, c.g * p.Shade, c.b * p.Shade, 1f);
        }
        BuildOverviewPixels();
    }

    /// <summary>Main thread: creates the GameObjects / textures from the prepared data.</summary>
    private void BuildRendering()
    {
        CreateOverviewObjects();

        _ccx = CeilDiv(_w, _cs);
        _ccy = CeilDiv(_h, _cs);
        _chunks = new Chunk[_ccx * _ccy];
        _chunkDirty = new bool[_chunks.Length];
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

    #endregion

    #region Visuals: batched refresh

    /// <summary>Queues a repaint of everything around the province (overview rect + loaded chunks). Cheap: no texture work here.</summary>
    private void MarkProvinceDirty(Province p)
    {
        int minX = Mathf.Max(0, p.Min.x - 1), minY = Mathf.Max(0, p.Min.y - 1);
        int maxX = Mathf.Min(_w - 1, p.Max.x + 1), maxY = Mathf.Min(_h - 1, p.Max.y + 1);

        if (!_ovDirty) { _dMinX = minX; _dMinY = minY; _dMaxX = maxX; _dMaxY = maxY; _ovDirty = true; }
        else
        {
            if (minX < _dMinX) _dMinX = minX;
            if (minY < _dMinY) _dMinY = minY;
            if (maxX > _dMaxX) _dMaxX = maxX;
            if (maxY > _dMaxY) _dMaxY = maxY;
        }

        for (int cy = minY / _cs; cy <= maxY / _cs; cy++)
            for (int cx = minX / _cs; cx <= maxX / _cs; cx++)
            {
                int ci = cy * _ccx + cx;
                if (_chunks[ci] == null) continue;          // unloaded chunks are drawn from fresh data when built
                _chunkDirty[ci] = true;
                _anyChunkDirty = true;
            }
    }

    private void FlushVisuals(ref int budget)
    {
        if (_ovDirty)
        {
            _ovDirty = false;
            RepaintOverview(_dMinX, _dMinY, _dMaxX, _dMaxY);   // all changes of this frame -> ONE upload
        }

        if (!_anyChunkDirty) return;
        bool left = false;
        for (int i = 0; i < _chunkDirty.Length; i++)
        {
            if (!_chunkDirty[i]) continue;
            Chunk c = _chunks[i];
            if (c == null) { _chunkDirty[i] = false; continue; }
            if (budget <= 0) { left = true; continue; }
            budget--;
            FillChunk(c);                                      // clears the dirty flag
        }
        _anyChunkDirty = left;
    }

    #endregion

    #region Visuals: overview

    private void BuildOverviewPixels()
    {
        int maxSide = Mathf.Clamp(_s.overviewMaxSize, 128, 4096);
        _ovScale = Mathf.Max(1, CeilDiv(Mathf.Max(_w, _h), maxSide));
        _ovW = CeilDiv(_w, _ovScale);
        _ovH = CeilDiv(_h, _ovScale);
        _ovPx = new Color32[_ovW * _ovH];

        // pass 1: nation key per overview pixel (water = -2, neutral land = -1), one tile lookup per pixel
        var keys = new int[_ovW * _ovH];
        for (int y = 0; y < _ovH; y++)
            for (int x = 0; x < _ovW; x++)
                keys[y * _ovW + x] = OverviewKey(x, y);

        // pass 2: color + 1px nation border (only the side with the higher key is darkened; water counts as lowest)
        for (int y = 0; y < _ovH; y++)
        {
            for (int x = 0; x < _ovW; x++)
            {
                int i = y * _ovW + x;
                int key = keys[i];
                if (key == -2) { _ovPx[i] = _s.waterColor; continue; }

                Color32 col = _provinces[_tileProvince[OverviewTile(x, y)]].Color;
                int l = x > 0 ? keys[i - 1] : -2;
                int r = x < _ovW - 1 ? keys[i + 1] : -2;
                int d = y > 0 ? keys[i - _ovW] : -2;
                int u = y < _ovH - 1 ? keys[i + _ovW] : -2;
                if (l < key || r < key || d < key || u < key)
                    col = Color32.Lerp(col, _borderRgb, _s.nationBorderAlpha);
                _ovPx[i] = col;
            }
        }
    }

    private void CreateOverviewObjects()
    {
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
        return _ownerKey[_tileProvince[t]];
    }

    private void PaintOverviewPixel(int px, int py)
    {
        int t = OverviewTile(px, py);
        if (_tiles[t] == TileType.Water) { _ovPx[py * _ovW + px] = _s.waterColor; return; }

        int pid = _tileProvince[t];
        Color32 col = _provinces[pid].Color;
        int key = _ownerKey[pid];
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

    #endregion

    #region Visuals: detail chunks

    private void UpdateChunkVisibility(ref int budget)
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
        _chunks[cy * _ccx + cx] = c;
        FillChunk(c);
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
        _chunkDirty[c.cy * _ccx + c.cx] = false;

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
    //   same nation on both sides -> province border (thin)
    //   different nation / water  -> nation border   (bold)
    // Water tiles optionally paint a faint line where two sea blocks meet.
    private void DrawChunkBorders(int x0, int y0)
    {
        int stride = _cs * _ppt;
        int xMax = Mathf.Min(_cs, _w - x0), yMax = Mathf.Min(_cs, _h - y0);
        bool seaLines = _seaAlpha > 0;

        for (int ly = 0; ly < yMax; ly++)
        {
            int gy = y0 + ly;
            for (int lx = 0; lx < xMax; lx++)
            {
                int gx = x0 + lx;
                int pa = _tileProvince[gy * _w + gx];
                int px = lx * _ppt, py = ly * _ppt;

                if (pa < 0)
                {
                    if (seaLines) DrawSeaBorders(gx, gy, px, py, stride);
                    continue;
                }

                int na = _ownerKey[pa];
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

        bool nationBorder = pb < 0 || _ownerKey[pb] != na;
        alpha = nationBorder ? _nationAlpha : _provAlpha;
        thickness = nationBorder ? _nationPx : _provPx;
        return true;
    }

    private void DrawSeaBorders(int gx, int gy, int px, int py, int stride)
    {
        int i = gy * _w + gx;
        int sa = _tileSea[i];
        if (sa < 0) return;
        if (gx > 0 && SeaDiffers(sa, i - 1)) Paint(px, py, 1, _ppt, _seaAlpha, stride);
        if (gx < _w - 1 && SeaDiffers(sa, i + 1)) Paint(px + _ppt - 1, py, 1, _ppt, _seaAlpha, stride);
        if (gy > 0 && SeaDiffers(sa, i - _w)) Paint(px, py, _ppt, 1, _seaAlpha, stride);
        if (gy < _h - 1 && SeaDiffers(sa, i + _w)) Paint(px, py + _ppt - 1, _ppt, 1, _seaAlpha, stride);
    }

    private bool SeaDiffers(int sa, int j)
    {
        int sb = _tileSea[j];
        return sb >= 0 && sb != sa;
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

    private void ReleaseChunk(Chunk c)
    {
        int ci = c.cy * _ccx + c.cx;
        _chunks[ci] = null;
        _chunkDirty[ci] = false;
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

    #endregion

    #region Capital markers

    private sealed class Marker
    {
        public GameObject go;
        public SpriteRenderer sr;     // cached: no GetComponent per refresh
    }

    private Transform _markerRoot;
    private readonly Dictionary<int, Marker> _markers = new Dictionary<int, Marker>();
    private readonly HashSet<int> _dirtyMarkers = new HashSet<int>();
    private bool _allMarkersDirty;

    private void BuildCapitalMarkers()
    {
        DestroyMarkers();
        if (!capitalMarkerPrefab || _world == null) return;

        var root = new GameObject("Capital Markers");
        root.transform.SetParent(transform, false);
        root.transform.localPosition = PivotOffset;
        _markerRoot = root.transform;

        RefreshAllMarkers();
    }

    /// <summary>Re-checks every capital marker (applied at the end of the frame).</summary>
    public void RefreshCapitalMarkers() => _allMarkersDirty = true;

    private void MarkMarkerDirty(int nationId)
    {
        if (nationId >= 0 && capitalMarkerPrefab) _dirtyMarkers.Add(nationId);
    }

    private void FlushMarkers()
    {
        if (_markerRoot == null) return;
        if (_allMarkersDirty)
        {
            _allMarkersDirty = false;
            _dirtyMarkers.Clear();
            RefreshAllMarkers();
            return;
        }
        if (_dirtyMarkers.Count == 0) return;
        foreach (int id in _dirtyMarkers) RefreshMarker(id, _world.GetNation(id));
        _dirtyMarkers.Clear();
    }

    private void RefreshAllMarkers()
    {
        if (_markerRoot == null || !capitalMarkerPrefab) return;

        List<int> stale = null;
        foreach (var kv in _markers)
            if (_world.GetNation(kv.Key) == null)
            {
                if (stale == null) stale = new List<int>();
                stale.Add(kv.Key);
            }
        if (stale != null)
            for (int i = 0; i < stale.Count; i++) RefreshMarker(stale[i], null);

        foreach (Nation n in _world.Nations) RefreshMarker(n.Id, n);
    }

    private void RefreshMarker(int nationId, Nation n)
    {
        var pool = PoolManager.Instance;
        _markers.TryGetValue(nationId, out Marker m);
        Province cap = n != null ? n.Capital : null;

        if (cap == null)
        {
            if (m != null && m.go != null) pool.Despawn(m.go);
            _markers.Remove(nationId);
            return;
        }

        if (m == null || m.go == null || !m.go.activeSelf)     // missing or collected by the pool
        {
            GameObject go = pool.Spawn(capitalMarkerPrefab, _markerRoot);
            m = new Marker { go = go, sr = go.GetComponentInChildren<SpriteRenderer>() };
            _markers[nationId] = m;
        }

        m.go.transform.localPosition = new Vector3(
            (cap.Centroid.x + 0.5f) * _s.tileSize, (cap.Centroid.y + 0.5f) * _s.tileSize, 0f);

        if (!m.sr) return;
        m.sr.color = InverseColor(n.Color);
        m.sr.sortingOrder = markerSortingOrder;

        // default scale = the prefab's own scale. Pooled objects may still carry an old scale, so always set it.
        Vector3 defScale = capitalMarkerPrefab.transform.localScale;
        float spriteSize = m.sr.sprite ? Mathf.Max(0.0001f, m.sr.sprite.bounds.size.x) : 1f;
        float defSize = spriteSize * Mathf.Max(0.0001f, defScale.x);            // marker size at default scale (world units)

        float fit = Mathf.Sqrt(cap.TileCount) * markerProvinceFraction * _s.tileSize;   // what the province can hold
        float floor = markerMinTiles * _s.tileSize;                                      // never smaller than this
        float size = Mathf.Min(defSize, Mathf.Max(fit, floor));                          // big province -> defSize, tiny -> shrink

        float k = size / defSize;
        m.go.transform.localScale = new Vector3(defScale.x * k, defScale.y * k, defScale.z);
    }

    private void DestroyMarkers()
    {
        if (PoolManager.HasInstance)
            foreach (var kv in _markers) if (kv.Value.go != null) PoolManager.Instance.Despawn(kv.Value.go);
        _markers.Clear();
        _dirtyMarkers.Clear();
        _allMarkersDirty = false;
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

    #endregion

    #region Merge provinces

    public bool CanMerge(Province origin, IList<Province> group, out string error) =>
        Validate(origin, group, new List<Province>(), out error);

    public float GetMergeCost(IList<Province> group, Province origin)
    {
        int absorbed = 0;
        if (group != null) foreach (var p in group) if (p != null && p != origin) absorbed++;
        return absorbed * merge.costPerAbsorbedProvince;
    }

    public Province MergeProvinces(int originId, IList<int> ids, string newName = null)
    {
        var list = new List<Province>();
        if (ids != null) foreach (int id in ids) list.Add(GetProvince(id));
        return MergeProvinces(GetProvince(originId), list, newName);
    }

    /// <summary>
    /// Merges 'group' (neighbouring provinces of ONE nation) into 'origin'. The origin's id and name survive.
    /// newName null / empty = keep the origin's name. Returns the merged province, or null (see LastMergeError).
    /// </summary>
    public Province MergeProvinces(Province origin, IList<Province> group, string newName = null)
    {
        var all = new List<Province>();
        if (!Validate(origin, group, all, out string err)) { LastMergeError = err; return null; }

        Nation owner = origin.Owner;
        float cost = (all.Count - 1) * merge.costPerAbsorbedProvince;
        if (cost > 0f && !owner.TrySpend(cost)) { LastMergeError = "Not enough treasury."; return null; }
        LastMergeError = null;

        bool hadCapital = owner.Capital != null && all.Contains(owner.Capital);

        // ── combine everything BEFORE touching the origin ──
        int totalTiles = 0;
        double sx = 0, sy = 0, pop = 0, taxPop = 0, eco = 0, defSum = 0, defTile = 0, pg = 0, infra = 0, prod = 0, dev = 0;
        foreach (Province p in all)
        {
            int c = p.TileCount;
            totalTiles += c;
            sx += p.Centroid.x * c; sy += p.Centroid.y * c;
            pop += p.Population;
            taxPop += p.Tax.Base * p.Population;
            eco += p.Economy.Base;
            defSum += p.Defend.Base; defTile += p.Defend.Base * c;
            pg += p.PopGrowth.Base;
            infra += p.Infrastructure.Base * c;
            prod += p.ProductionIncome.Base * c;
            dev += p.Devastation * p.Population;
        }

        // ── tiles, bounds, centroid ──
        var tiles = new int[totalTiles];
        int k = 0;
        var group2 = new HashSet<int>();
        foreach (Province p in all)
        {
            group2.Add(p.Id);
            Array.Copy(p.Tiles, 0, tiles, k, p.Tiles.Length);
            k += p.Tiles.Length;
            if (p != origin)
            {
                for (int i = 0; i < p.Tiles.Length; i++) _tileProvince[p.Tiles[i]] = origin.Id;
                if (p.Min.x < origin.Min.x) origin.Min.x = p.Min.x;
                if (p.Min.y < origin.Min.y) origin.Min.y = p.Min.y;
                if (p.Max.x > origin.Max.x) origin.Max.x = p.Max.x;
                if (p.Max.y > origin.Max.y) origin.Max.y = p.Max.y;
            }
        }
        origin.Tiles = tiles;
        origin.Centroid = new Vector2((float)(sx / totalTiles), (float)(sy / totalTiles));

        // ── stats ──
        origin.Population = (float)pop;
        origin.Tax.Base = pop > 0 ? (float)(taxPop / pop) : origin.Tax.Base;
        origin.Economy.Base = (float)eco;
        origin.Defend.Base = merge.sumDefend ? (float)defSum : (float)(defTile / totalTiles);
        origin.PopGrowth.Base = (float)pg;
        origin.Infrastructure.Base = (float)(infra / totalTiles);
        origin.ProductionIncome.Base = (float)(prod / totalTiles);
        origin.Devastation = pop > 0 ? (float)(dev / pop) : 0f;

        // ── land neighbours: union of the group's outside neighbours; outsiders now point to the origin ──
        var outside = new HashSet<int>();
        foreach (Province p in all)
            foreach (int id in p.Neighbors) if (!group2.Contains(id)) outside.Add(id);
        var nb = new int[outside.Count];
        outside.CopyTo(nb);
        origin.Neighbors = nb;
        foreach (int id in outside)
            _provinces[id].Neighbors = RemapIds(_provinces[id].Neighbors, group2, origin.Id);

        // ── sea blocks touching the group now touch the origin ──
        var coast = new HashSet<int>();
        foreach (Province p in all) foreach (int s in _coastSeas[p.Id]) coast.Add(s);
        foreach (int s in coast)
            _seas[s].LandNeighbors = RemapIds(_seas[s].LandNeighbors, group2, origin.Id);
        var coastArr = new int[coast.Count];
        coast.CopyTo(coastArr);
        _coastSeas[origin.Id] = coastArr;

        // ── retire the absorbed provinces ──
        var absorbed = new List<Province>(all.Count - 1);
        foreach (Province p in all)
        {
            if (p == origin) continue;
            absorbed.Add(p);
            _world.DetachProvince(p);
            p.IsMerged = true;
            p.MergedInto = origin.Id;
            p.Tiles = Array.Empty<int>();
            p.Neighbors = Array.Empty<int>();
            _coastSeas[p.Id] = Array.Empty<int>();
            _ownerKey[p.Id] = -1;
        }

        if (!string.IsNullOrWhiteSpace(newName)) origin.Name = newName.Trim();
        if (hadCapital) _world.SetCapital(owner, origin);

        // ── visuals (batched: repainted once at the end of the frame) ──
        UpdateProvinceColor(origin);
        MarkProvinceDirty(origin);            // the borders between the merged provinces disappear
        MarkMarkerDirty(owner.Id);

        OnProvincesMerged?.Invoke(origin, absorbed);
        return origin;
    }

    /// <summary>Replaces every id of 'group' in 'ids' by one 'replacement' (no duplicates).</summary>
    private static int[] RemapIds(int[] ids, HashSet<int> group, int replacement)
    {
        var l = new List<int>(ids.Length);
        bool added = false;
        foreach (int m in ids)
        {
            if (group.Contains(m)) { if (!added) { l.Add(replacement); added = true; } }
            else l.Add(m);
        }
        return l.ToArray();
    }

    private bool Validate(Province origin, IList<Province> group, List<Province> all, out string error)
    {
        error = null;
        all.Clear();
        if (!IsReady) { error = "Map is not ready."; return false; }
        if (origin == null || origin.IsMerged || origin.Owner == null) { error = "Bad origin province."; return false; }

        all.Add(origin);
        if (group != null)
            for (int i = 0; i < group.Count; i++)
            {
                Province p = group[i];
                if (p == null || p.IsMerged) { error = "Bad province in the group."; return false; }
                if (!all.Contains(p)) all.Add(p);
            }

        if (all.Count < 2) { error = "Need at least two provinces."; return false; }
        if (all.Count > merge.maxProvincesPerMerge) { error = "Too many provinces (max " + merge.maxProvincesPerMerge + ")."; return false; }

        Nation owner = origin.Owner;
        foreach (Province p in all)
            if (p.Owner != owner) { error = "All provinces must belong to the same nation."; return false; }

        // connected group (BFS from the origin, inside the group only)
        var ids = new HashSet<int>();
        foreach (Province p in all) ids.Add(p.Id);
        var seen = new HashSet<int> { origin.Id };
        var q = new Queue<int>();
        q.Enqueue(origin.Id);
        while (q.Count > 0)
        {
            int c = q.Dequeue();
            foreach (int n in _provinces[c].Neighbors)
                if (ids.Contains(n) && seen.Add(n)) q.Enqueue(n);
        }
        if (seen.Count != all.Count) { error = "The provinces must touch each other."; return false; }

        if (merge.blockWhileOccupied)
        {
            foreach (Province p in all)
            {
                if (CombatManager.HasInstance && CombatManager.Instance.IsContested(p)) { error = p.Name + " has a battle."; return false; }
                if (WarManager.HasInstance)
                {
                    var wars = WarManager.Instance.Wars;
                    for (int w = 0; w < wars.Count; w++)
                        if (wars[w].Occupied.ContainsKey(p.Id)) { error = p.Name + " is occupied."; return false; }
                }
            }
        }

        float cost = (all.Count - 1) * merge.costPerAbsorbedProvince;
        if (cost > 0f && owner.Treasury - cost < owner.MinTreasury) { error = "Not enough treasury."; return false; }
        return true;
    }

    /// <summary>Grows a group around 'origin' (same owner, touching, smallest neighbours first). result[0] = origin.</summary>
    public bool FindMergeGroup(Province origin, int count, List<Province> result)
    {
        result.Clear();
        if (origin == null || origin.IsMerged || origin.Owner == null) return false;
        count = Mathf.Clamp(count, 2, Mathf.Max(2, merge.maxProvincesPerMerge));
        result.Add(origin);
        while (result.Count < count)
        {
            Province best = null;
            for (int i = 0; i < result.Count; i++)
                foreach (int id in result[i].Neighbors)
                {
                    Province q = GetProvince(id);
                    if (q == null || q.IsMerged || q.Owner != origin.Owner || result.Contains(q)) continue;
                    if (best == null || q.TileCount < best.TileCount) best = q;
                }
            if (best == null) break;
            result.Add(best);
        }
        return result.Count >= 2;
    }

    /// <summary>A fresh, unused province name (for "merge into a new name").</summary>
    public string GenerateProvinceName()
    {
        var used = new HashSet<string>();
        foreach (Province p in _provinces) if (!p.IsMerged) used.Add(p.Name);
        var rng = new Unity.Mathematics.Random(HashSeed((uint)RNG.GetInt(1, int.MaxValue)));
        return NameManager.ProvinceName(ref rng, used);
    }

    #endregion

    #region Debug

    [ContextMenu("Merge: random province + 2 neighbours (origin name)")]
    private void MenuMerge()
    {
        if (!IsReady || _provinces.Count == 0) return;
        var list = new List<Province>();
        for (int t = 0; t < 32; t++)
        {
            Province p = _provinces[RNG.GetInt(0, _provinces.Count)];
            if (!FindMergeGroup(p, 3, list)) continue;
            Province r = MergeProvinces(p, list);
            Debug.Log(r != null ? "[Map] Merged into " + r.Name + " (" + r.TileCount + " tiles)." : "[Map] Merge failed: " + LastMergeError);
            return;
        }
    }

    [ContextMenu("Speed Up")]
    public void SpeedUp() => EventBus.RaiseSpeedUp();
    [ContextMenu("Speed Down")]
    public void SpeedDown() => EventBus.RaiseSpeedDown();

    #endregion
}