using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
//  ARMY JOBS  (Burst-compiled, run on worker threads)
//   ProjectionBatch : world -> screen for every regiment card in one parallel job.
//   CombatBatch     : radius based combat (damage + dice + push) for every regiment in one parallel job.
//  Managers never touch Unity.Mathematics types: they use these two small wrappers.
// ─────────────────────────────────────────────────────────────────────────────

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

    /// <summary>Projects 'count' positions. Visible = inside the screen enlarged by 10%.</summary>
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
/// One combat step for all regiments at once.
///   outer radius : two hostile regiments inside it (min of both radii) hurt each other.
///   inner radius : they collide: marching stops and the stronger side pushes the weaker back.
///   dice         : stateless. Every pair re-rolls every 'rollDays' (own phase per pair); the winner side gets
///                  ATK and DEF * (1 + bonus) for 'buffDays'. No other bonus exists.
///   damage       : eff = ATK^2 / (ATK + DEF), hp = eff * damageScale * damageMultiplier * dt
///   push         : stronger share (0.5..1) maps to 0..march speed (pushStart..pushFull).
/// </summary>
public sealed class CombatBatch : IDisposable
{
    public const byte FlagInRange = 1, FlagInContact = 2;

    private NativeArray<float3> _pos, _outPush, _outTarget;
    private NativeArray<float> _men, _atk, _def, _dmg, _outer, _inner, _outHp;
    private NativeArray<int> _nation, _uid;
    private NativeArray<byte> _hostile, _outFlags;
    private int _cap, _nationCap;
    private JobHandle _handle;

    public bool Pending { get; private set; }
    public int NationCap => _nationCap;

    public void Reserve(int n, int nationCap)
    {
        Complete();
        nationCap = Mathf.Max(1, nationCap);
        if (n > _cap)
        {
            FreeUnits();
            _cap = Mathf.Max(256, Mathf.NextPowerOfTwo(n));
            _pos = JobUtil.Alloc<float3>(_cap); _outPush = JobUtil.Alloc<float3>(_cap); _outTarget = JobUtil.Alloc<float3>(_cap);
            _men = JobUtil.Alloc<float>(_cap); _atk = JobUtil.Alloc<float>(_cap); _def = JobUtil.Alloc<float>(_cap);
            _dmg = JobUtil.Alloc<float>(_cap); _outer = JobUtil.Alloc<float>(_cap); _inner = JobUtil.Alloc<float>(_cap);
            _outHp = JobUtil.Alloc<float>(_cap);
            _nation = JobUtil.Alloc<int>(_cap); _uid = JobUtil.Alloc<int>(_cap);
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

    /// <summary>nation = -1 excludes the regiment from combat.</summary>
    public void Set(int i, Vector3 pos, float men, float atk, float def, float dmgMul, float outer, float inner, int nation, int uid)
    {
        _pos[i] = pos; _men[i] = men; _atk[i] = atk; _def[i] = def; _dmg[i] = dmgMul;
        _outer[i] = outer; _inner[i] = inner; _nation[i] = nation; _uid[i] = uid;
    }

    public void Schedule(int count, int day, float dt, float damageScale, bool useDice, float diceBonus,
                         int rollDays, int buffDays, float march, float pushStart, float pushFull)
    {
        if (count <= 0 || Pending) return;
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
            outHp = _outHp,
            outFlags = _outFlags,
            outPush = _outPush,
            outTarget = _outTarget,
            count = count,
            nationCap = _nationCap,
            day = day,
            dt = dt,
            damageScale = damageScale,
            useDice = useDice ? 1 : 0,
            diceBonus = diceBonus,
            rollDays = Mathf.Max(1, rollDays),
            buffDays = Mathf.Max(0, buffDays),
            march = march,
            pushStart = pushStart,
            pushFull = pushFull
        };
        _handle = job.Schedule(count, 8);
        Pending = true;
        JobHandle.ScheduleBatchedJobs();      // start the workers now, the result is collected in LateUpdate
    }

    public void Complete()
    {
        if (!Pending) return;
        _handle.Complete();
        Pending = false;
    }

    public float Hp(int i) => _outHp[i];
    public byte Flags(int i) => _outFlags[i];
    public Vector3 Push(int i) => _outPush[i];
    public Vector3 Target(int i) => _outTarget[i];

    private void FreeUnits()
    {
        JobUtil.Free(ref _pos); JobUtil.Free(ref _outPush); JobUtil.Free(ref _outTarget);
        JobUtil.Free(ref _men); JobUtil.Free(ref _atk); JobUtil.Free(ref _def);
        JobUtil.Free(ref _dmg); JobUtil.Free(ref _outer); JobUtil.Free(ref _inner);
        JobUtil.Free(ref _outHp);
        JobUtil.Free(ref _nation); JobUtil.Free(ref _uid);
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
    private struct CombatJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> pos;
        [ReadOnly] public NativeArray<float> men, atk, def, dmg, outer, inner;
        [ReadOnly] public NativeArray<int> nation, uid;
        [ReadOnly] public NativeArray<byte> hostile;
        [WriteOnly] public NativeArray<float> outHp;
        [WriteOnly] public NativeArray<byte> outFlags;
        [WriteOnly] public NativeArray<float3> outPush, outTarget;

        public int count, nationCap, day, useDice, rollDays, buffDays;
        public float dt, damageScale, diceBonus, march, pushStart, pushFull;

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
            if (ni < 0)
            {
                outHp[i] = 0f; outFlags[i] = 0; outPush[i] = float3.zero; outTarget[i] = float3.zero;
                return;
            }

            float3 pi = pos[i];
            float menI = men[i], atkI = atk[i], defI = def[i];
            uint uidI = (uint)uid[i];
            int row = ni * nationCap;
            float invSpan = 1f / math.max(0.0001f, pushFull - pushStart);

            float hp = 0f;
            int flags = 0;
            float3 push = float3.zero;
            float3 target = pi;
            float best = float.MaxValue;

            for (int j = 0; j < count; j++)
            {
                if (j == i) continue;
                int nj = nation[j];
                if (nj < 0 || hostile[row + nj] == 0) continue;

                float3 d = pi - pos[j];
                float d2 = math.lengthsq(d);
                float rOut = math.min(outer[i], outer[j]);
                if (d2 > rOut * rOut) continue;

                flags |= 1;
                if (d2 < best) { best = d2; target = pos[j]; }

                // ── dice: the ONLY modifier. Stateless: derived from the pair ids and the date ──
                float mi = 1f, mj = 1f;
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
                        bool iIsLo = uidI < uidJ;
                        if (iIsLo == loBuffed) mi += diceBonus; else mj += diceBonus;
                    }
                }

                // ── damage from j to i ──
                float atkJ = men[j] * atk[j] * mj;
                float defIt = menI * defI * mi;
                float eff = atkJ > 0f ? atkJ * atkJ / (atkJ + defIt) : 0f;
                hp += eff * damageScale * dmg[j] * dt;

                // ── collision: stronger pushes the weaker back, at most the march speed ──
                float rIn = math.min(inner[i], inner[j]);
                if (d2 <= rIn * rIn)
                {
                    flags |= 2;
                    float si = menI * (atkI + defI) * mi;
                    float sj = men[j] * (atk[j] + def[j]) * mj;
                    float sum = si + sj;
                    float share = sum > 0f ? si / sum : 0.5f;
                    float strong = math.max(share, 1f - share);
                    float t = math.saturate((strong - pushStart) * invSpan);
                    float3 dir = math.normalizesafe(d, new float3(1f, 0f, 0f));
                    if (share < 0.5f) push += dir * (t * march);    // weaker: pushed away
                    else push -= dir * (t * march);                 // stronger: follows the enemy
                }
            }

            float len = math.length(push);
            if (len > march && len > 0f) push *= march / len;

            outHp[i] = hp;
            outFlags[i] = (byte)flags;
            outPush[i] = push;
            outTarget[i] = target;
        }
    }
}

#endregion