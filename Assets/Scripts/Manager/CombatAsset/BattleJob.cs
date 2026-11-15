using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

#region Helpers

internal static class JobUtil
{
    public static NativeArray<T> Alloc<T>(int n) where T : struct =>
        new NativeArray<T>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

    public static void Free<T>(ref NativeArray<T> a) where T : struct
    {
        if (a.IsCreated) a.Dispose();
    }
}

internal static class CombatGrid
{
    public static int Hash(int x, int y, int mask)
    {
        unchecked { return (int)(((uint)x * 73856093u ^ (uint)y * 19349663u) & (uint)mask); }
    }
}

#endregion

#region Projection (world -> screen)

public sealed class ProjectionBatch : IDisposable
{
    private NativeArray<float3> _pos;
    private NativeArray<float2> _screen;
    private NativeArray<byte> _visible;
    private int _cap;

    public void Reserve(int n)
    {
        if (n <= _cap) return;
        Dispose();
        _cap = Mathf.Max(128, Mathf.NextPowerOfTwo(n));
        _pos = JobUtil.Alloc<float3>(_cap);
        _screen = JobUtil.Alloc<float2>(_cap);
        _visible = JobUtil.Alloc<byte>(_cap);
    }

    public void Set(int i, Vector3 world) => _pos[i] = world;

    public void Run(Camera cam, int count, int screenW, int screenH)
    {
        if (count <= 0) return;
        Matrix4x4 m = cam.projectionMatrix * cam.worldToCameraMatrix;
        var job = new ProjectJob
        {
            pos = _pos,
            screenPos = _screen,
            visible = _visible,
            viewProj = new float4x4(m.GetColumn(0), m.GetColumn(1), m.GetColumn(2), m.GetColumn(3)),
            screen = new float2(screenW, screenH)
        };
        job.Schedule(count, 64).Complete();
    }

    public bool IsVisible(int i) => _visible[i] != 0;
    public Vector2 Screen(int i) => (Vector2)(float2)_screen[i];

    public void Dispose()
    {
        JobUtil.Free(ref _pos);
        JobUtil.Free(ref _screen);
        JobUtil.Free(ref _visible);
        _cap = 0;
    }

    [BurstCompile]
    private struct ProjectJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> pos;
        [WriteOnly] public NativeArray<float2> screenPos;
        [WriteOnly] public NativeArray<byte> visible;
        public float4x4 viewProj;
        public float2 screen;

        public void Execute(int i)
        {
            float4 c = math.mul(viewProj, new float4(pos[i], 1f));
            if (c.w <= 1e-5f) { visible[i] = 0; screenPos[i] = float2.zero; return; }

            float2 ndc = c.xy / c.w;
            float2 sp = (ndc * 0.5f + 0.5f) * screen;
            screenPos[i] = sp;

            float2 margin = screen * 0.1f;
            bool inside = sp.x >= -margin.x && sp.x <= screen.x + margin.x &&
                          sp.y >= -margin.y && sp.y <= screen.y + margin.y;
            visible[i] = (byte)(inside ? 1 : 0);
        }
    }
}

#endregion

#region Combat

/// <summary>
/// One combat step for all regiments: spatial grid (counting sort) + parallel damage job.
///   outer radius : two hostile regiments inside it (min of both radii) hurt each other.
///   inner radius : contact flag (the battle logic in CombatManager pushes / stops armies).
///   dice         : stateless pair roll, the only modifier.
///   output       : hp lost, flags, index of the nearest hostile unit (used to cluster battles).
/// </summary>
public sealed class CombatBatch : IDisposable
{
    public const byte FlagInRange = 1, FlagInContact = 2;

    private NativeArray<float3> _pos;
    private NativeArray<float> _men, _atk, _def, _dmg, _outer, _inner, _outHp;
    private NativeArray<int> _nation, _uid, _outNear, _cx, _cy, _cellStart, _cellItems, _cellFill;
    private NativeArray<byte> _hostile, _outFlags;
    private int _cap, _nationCap;
    private JobHandle _handle;

    public bool Pending { get; private set; }

    public void Reserve(int n, int nationCap)
    {
        Complete();
        nationCap = Mathf.Max(1, nationCap);
        if (n > _cap)
        {
            FreeUnits();
            _cap = Mathf.Max(256, Mathf.NextPowerOfTwo(n));
            int table = Mathf.NextPowerOfTwo(Mathf.Max(64, _cap * 2));
            _pos = JobUtil.Alloc<float3>(_cap);
            _men = JobUtil.Alloc<float>(_cap); _atk = JobUtil.Alloc<float>(_cap); _def = JobUtil.Alloc<float>(_cap);
            _dmg = JobUtil.Alloc<float>(_cap); _outer = JobUtil.Alloc<float>(_cap); _inner = JobUtil.Alloc<float>(_cap);
            _outHp = JobUtil.Alloc<float>(_cap);
            _nation = JobUtil.Alloc<int>(_cap); _uid = JobUtil.Alloc<int>(_cap); _outNear = JobUtil.Alloc<int>(_cap);
            _cx = JobUtil.Alloc<int>(_cap); _cy = JobUtil.Alloc<int>(_cap); _cellItems = JobUtil.Alloc<int>(_cap);
            _cellStart = JobUtil.Alloc<int>(table + 2); _cellFill = JobUtil.Alloc<int>(table + 1);
            _outFlags = JobUtil.Alloc<byte>(_cap);
        }
        if (nationCap != _nationCap || !_hostile.IsCreated)
        {
            JobUtil.Free(ref _hostile);
            _nationCap = nationCap;
            _hostile = JobUtil.Alloc<byte>(nationCap * nationCap);
        }
    }

    public void ClearHostility()
    {
        for (int i = 0; i < _hostile.Length; i++) _hostile[i] = 0;
    }

    public void SetHostile(int a, int b)
    {
        if (a < 0 || b < 0 || a >= _nationCap || b >= _nationCap) return;
        _hostile[a * _nationCap + b] = 1;
        _hostile[b * _nationCap + a] = 1;
    }

    public void Set(int i, Vector3 pos, float men, float atk, float def, float dmgMul, float outer, float inner, int nation, int uid)
    {
        _pos[i] = pos; _men[i] = men; _atk[i] = atk; _def[i] = def; _dmg[i] = dmgMul;
        _outer[i] = outer; _inner[i] = inner; _nation[i] = nation; _uid[i] = uid;
    }

    /// <param name="cell">Grid cell size: must be >= the largest outer radius.</param>
    public void Schedule(int count, float cell, int day, float dt, float damageScale, float defRatio, bool useDice, float diceBonus,
                     int rollDays, int buffDays)
    {
        if (count <= 0 || Pending) return;
        int table = Mathf.NextPowerOfTwo(Mathf.Max(64, count * 2));
        int mask = table - 1;
        float invCell = 1f / Mathf.Max(0.0001f, cell);

        var grid = new GridJob
        {
            pos = _pos,
            nation = _nation,
            cx = _cx,
            cy = _cy,
            cellStart = _cellStart,
            cellItems = _cellItems,
            cellFill = _cellFill,
            count = count,
            mask = mask,
            invCell = invCell
        };
        JobHandle gh = grid.Schedule();

        var job = new CombatJob
        {
            pos = _pos,
            men = _men,
            atk = _atk,
            def = _def,
            dmg = _dmg,
            outer = _outer,
            inner = _inner,
            nation = _nation,
            uid = _uid,
            hostile = _hostile,
            cx = _cx,
            cy = _cy,
            cellStart = _cellStart,
            cellItems = _cellItems,
            outHp = _outHp,
            outFlags = _outFlags,
            outNear = _outNear,
            count = count,
            mask = mask,
            invCell = invCell,
            nationCap = _nationCap,
            day = day,
            dt = dt,
            damageScale = damageScale,
            defRatio = Mathf.Max(0.0001f, defRatio),
            useDice = useDice ? 1 : 0,
            diceBonus = diceBonus,
            rollDays = Mathf.Max(1, rollDays),
            buffDays = Mathf.Max(0, buffDays)
        };
        _handle = job.Schedule(count, 16, gh);
        Pending = true;
        JobHandle.ScheduleBatchedJobs();
    }

    public void Complete()
    {
        if (!Pending) return;
        _handle.Complete();
        Pending = false;
    }

    public float Hp(int i) => _outHp[i];
    public byte Flags(int i) => _outFlags[i];
    public int Near(int i) => _outNear[i];

    private void FreeUnits()
    {
        JobUtil.Free(ref _pos);
        JobUtil.Free(ref _men); JobUtil.Free(ref _atk); JobUtil.Free(ref _def);
        JobUtil.Free(ref _dmg); JobUtil.Free(ref _outer); JobUtil.Free(ref _inner);
        JobUtil.Free(ref _outHp);
        JobUtil.Free(ref _nation); JobUtil.Free(ref _uid); JobUtil.Free(ref _outNear);
        JobUtil.Free(ref _cx); JobUtil.Free(ref _cy); JobUtil.Free(ref _cellItems);
        JobUtil.Free(ref _cellStart); JobUtil.Free(ref _cellFill);
        JobUtil.Free(ref _outFlags);
        _cap = 0;
    }

    public void Dispose()
    {
        Complete();
        FreeUnits();
        JobUtil.Free(ref _hostile);
        _nationCap = 0;
    }

    [BurstCompile]
    private struct GridJob : IJob
    {
        [ReadOnly] public NativeArray<float3> pos;
        [ReadOnly] public NativeArray<int> nation;
        public NativeArray<int> cx, cy, cellStart, cellItems, cellFill;
        public int count, mask;
        public float invCell;

        public void Execute()
        {
            for (int k = 0; k <= mask + 1; k++) cellStart[k] = 0;
            for (int i = 0; i < count; i++)
            {
                if (nation[i] < 0) { cx[i] = int.MinValue; cy[i] = 0; continue; }
                int x = (int)math.floor(pos[i].x * invCell), y = (int)math.floor(pos[i].y * invCell);
                cx[i] = x; cy[i] = y;
                cellStart[CombatGrid.Hash(x, y, mask) + 1]++;
            }
            for (int k = 0; k <= mask; k++) cellStart[k + 1] += cellStart[k];
            for (int k = 0; k <= mask; k++) cellFill[k] = cellStart[k];
            for (int i = 0; i < count; i++)
            {
                if (cx[i] == int.MinValue) continue;
                int h = CombatGrid.Hash(cx[i], cy[i], mask);
                cellItems[cellFill[h]++] = i;
            }
        }
    }

    [BurstCompile]
    private struct CombatJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> pos;
        [ReadOnly] public NativeArray<float> men, atk, def, dmg, outer, inner;
        [ReadOnly] public NativeArray<int> nation, uid, cx, cy, cellStart, cellItems;
        [ReadOnly] public NativeArray<byte> hostile;
        [WriteOnly] public NativeArray<float> outHp;
        [WriteOnly] public NativeArray<byte> outFlags;
        [WriteOnly] public NativeArray<int> outNear;

        public int count, mask, nationCap, day, useDice, rollDays, buffDays;
        public float invCell, dt, damageScale, diceBonus, defRatio;

        private static uint Hash(uint a, uint b)
        {
            uint h = a * 0x9E3779B1u ^ (b + 0x7F4A7C15u) * 0x85EBCA6Bu;
            h ^= h >> 15; h *= 0x2C1B3C6Du;
            h ^= h >> 12; h *= 0x297A2D39u;
            h ^= h >> 15;
            return h;
        }

        public void Execute(int i)
        {
            int ni = nation[i];
            if (ni < 0) { outHp[i] = 0f; outFlags[i] = 0; outNear[i] = -1; return; }

            float3 pi = pos[i];
            float menI = men[i], defI = def[i];
            uint uidI = (uint)uid[i];
            int row = ni * nationCap;
            int cxi = cx[i], cyi = cy[i];

            float hp = 0f;
            int flags = 0, near = -1;
            float best = float.MaxValue;

            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    int tx = cxi + dx, ty = cyi + dy;
                    int hcell = CombatGrid.Hash(tx, ty, mask);
                    int end = cellStart[hcell + 1];
                    for (int k = cellStart[hcell]; k < end; k++)
                    {
                        int j = cellItems[k];
                        if (j == i || cx[j] != tx || cy[j] != ty) continue;
                        int nj = nation[j];
                        if (hostile[row + nj] == 0) continue;

                        float3 d = pi - pos[j];
                        float d2 = math.lengthsq(d);
                        float rOut = math.min(outer[i], outer[j]);
                        if (d2 > rOut * rOut) continue;

                        flags |= 1;
                        if (d2 < best) { best = d2; near = j; }
                        float rIn = math.min(inner[i], inner[j]);
                        if (d2 <= rIn * rIn) flags |= 2;

                        float mj = 1f;
                        if (useDice != 0)
                        {
                            uint uidJ = (uint)uid[j];
                            uint lo = math.min(uidI, uidJ), hi = math.max(uidI, uidJ);
                            uint h = Hash(lo, hi);
                            int shifted = day + (int)(h % (uint)rollDays);
                            int epoch = shifted / rollDays;
                            int phase = shifted - epoch * rollDays;
                            if (phase < buffDays)
                            {
                                bool loBuffed = (Hash(h, (uint)epoch) & 1u) == 0u;
                                bool jIsLo = uidJ < uidI;
                                if (jIsLo == loBuffed) mj += diceBonus;
                            }
                        }

                        float atkJ = men[j] * atk[j] * mj;
                        float defIt = menI * defI;
                        float eff = atkJ * defRatio / (defIt + defRatio);
                        hp += eff * damageScale * dmg[j] * dt;
                    }
                }

            outHp[i] = hp;
            outFlags[i] = (byte)flags;
            outNear[i] = near;
        }
    }
}

#endregion