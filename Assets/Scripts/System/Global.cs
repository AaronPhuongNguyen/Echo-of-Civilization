using System;
using UnityEngine;

//global class for all
public static class RNG
{
    private static Unity.Mathematics.Random _state;
    private static uint _seed;

    static RNG() => SetSeed((uint)System.DateTime.Now.Ticks);

    public static void SetSeed(uint seed)
    {
        _seed = seed == 0 ? 1u : seed;
        _state = new Unity.Mathematics.Random(_seed);
    }

    public static void Reset() => _state = new Unity.Mathematics.Random(_seed);
    public static uint Seed => _seed;

    public static int GetInt(int min, int max) => _state.NextInt(min, max);
    public static float GetFloat(float min, float max) => _state.NextFloat(min, max);
    public static float GetPercent() => GetInt(0, 4097) / 4096f; // [0, 1);
    public static Vector2 GetVector2(float min, float max) => _state.NextFloat2(min, max);
    public static Vector2 GetUniformVector2(float min, float max)
    {
        float v = GetFloat(min, max);
        return new Vector2(v, v);
    }
    public static Vector2 GetInsideCircle(float radius)
    {
        float angle = GetFloat(0, Mathf.PI * 2f);
        float dist = GetFloat(0, radius);
        return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * dist;
    }
}

/// <summary>Implemented by anything that caches data derived from GameValues (Province, Nation).</summary>
public interface IDirtyable { void MarkDirty(); }

/// <summary>
/// Value = (Base + Flat + Base * Percent) * Total
///   Base    : the raw value (random at generation, +1 per completed "Increase").
///   Flat    : flat bonus added to the value.
///   Percent : bonus as a fraction of Base (0.25 = +25 % of Base).
///   Total   : final multiplier of everything (1 = unchanged).
/// Serializable, so every field can be edited in the Inspector.
/// </summary>
[Serializable]
public class GameValue
{
    public float Base;
    public float Flat;
    public float Percent;
    public float Total = 1f;

    [NonSerialized] private IDirtyable _owner;   // no delegates -> cheap for thousands of provinces

    public GameValue() { }
    public GameValue(float baseValue) { Base = baseValue; }

    public float Value => (Base + Flat + Base * Percent) * Total;

    public static implicit operator float(GameValue v) => v.Value;

    internal void Bind(IDirtyable owner) => _owner = owner;

    /// <summary>Sets base + modifiers without notifying (used while generating).</summary>
    public void Init(float baseValue, StatModifier mod)
    {
        Base = baseValue;
        Flat = mod != null ? mod.Flat : 0f;
        Percent = mod != null ? mod.Percent : 0f;
        Total = mod != null ? mod.Total : 1f;
    }

    public void SetBase(float v) { Base = v; Notify(); }
    public void AddBase(float v) { Base += v; Notify(); }
    public void AddFlat(float v) { Flat += v; Notify(); }
    public void AddPercent(float v) { Percent += v; Notify(); }
    public void MultiplyTotal(float v) { Total *= v; Notify(); }
    public void ResetModifiers() { Flat = 0f; Percent = 0f; Total = 1f; Notify(); }

    public void CopyFrom(GameValue o)
    {
        Base = o.Base; Flat = o.Flat; Percent = o.Percent; Total = o.Total;
        Notify();
    }

    private void Notify() => _owner?.MarkDirty();

    public override string ToString() => Value.ToString("F2");
}

/// <summary>Default modifiers applied to every province stat at map generation (editable in MapSettings).</summary>
[Serializable]
public class StatModifier
{
    public float Flat = 0f;
    public float Percent = 0f;
    public float Total = 1f;
}