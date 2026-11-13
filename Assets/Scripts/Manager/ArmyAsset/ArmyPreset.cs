using System;
using System.Collections.Generic;
using UnityEngine;

public enum RegimentState : byte
{
    Idle = 0,          // standing in a province, refilling up to its allocation
    Retreating = 1,    // moving (real map) to a safe province, does not fight, does not refill
    Reinforcing = 2,   // arrived after a retreat, refilling
    Moving = 3,        // marching along a province path
    Fighting = 4       // inside a battle (no card: the battle card is shown instead)
}

/// <summary>Merge = reinforcement column walking to its regiment. Hold = rear guard that fights at the battle site.</summary>
public enum ColumnRole { None ,Merge, Hold, Occupy }   // keep your existing order/values, just add Occupy

/// <summary>
/// One regiment of a nation's Military. PURE LOGIC at a WORLD position of the real map (no GameObject).
/// Columns (IsColumn) are reinforcement groups owned by Military.Columns.
/// </summary>
public sealed class Regiment
{
    // ── state ──
    public Military Military { get; private set; }
    /// <summary>Last province reached (the old one while retreating).</summary>
    public Province Province { get; private set; }
    public Province TargetProvince { get; private set; }
    /// <summary>The province the regiment is physically in right now (updated while marching).</summary>
    public Province CurrentProvince { get; internal set; }
    public RegimentState State { get; internal set; }

    public Vector3 WorldPosition { get; private set; }
    private Vector3 _targetWorldPos;
    private List<Province> _path;
    private int _pathIndex;

    public int StackIndex { get; internal set; }
    public RegimentView View { get; internal set; }

    public double Manpower { get; internal set; }
    public float CustomShare { get; internal set; } = -1f;
    public float Share { get; internal set; }

    // ── combat ──
    public Battle Battle { get; internal set; }
    /// <summary>HP lost that did not yet cost a full manpower.</summary>
    public double LostHp { get; internal set; }
    public double TotalHp => Military != null ? Manpower * Military.HpPerMan.Value : 0.0;


    // ── reinforcement column ──
    public bool IsColumn { get; internal set; }
    public ColumnRole Role { get; internal set; }
    public Regiment MergeTarget { get; internal set; }

    // ── bookkeeping used by CombatManager ──
    internal Vector2Int TrailTile = new Vector2Int(-1, -1);
    internal Province CheckedProvince;

    public double Allocation => Military != null ? Military.MobilizedCap * Share : 0.0;
    public float StrengthPercent
    {
        get { double a = Allocation; return a > 0.0 ? (float)(Manpower / a) : 0f; }
    }
    public bool IsAutoShare => CustomShare < 0f;
    public Province LocationProvince => State == RegimentState.Retreating && TargetProvince != null ? TargetProvince : Province;
    public bool HasPath => _path != null && _pathIndex < _path.Count;
    public Province Destination => HasPath ? _path[_path.Count - 1] : null;

    internal Regiment(Military military, Province province, Vector3 worldPosition)
    {
        Military = military;
        Province = province;
        CurrentProvince = province;
        CheckedProvince = province;
        WorldPosition = worldPosition;
        State = RegimentState.Idle;
    }

    /// <summary>Set by the AI while the regiment holds a tactical position (siege ring). Auto-support ignores it.</summary>
    internal bool TacticLock;

    internal void CopyPath(List<Province> dst)
    {
        dst.Clear();
        if (_path == null) return;
        for (int i = _pathIndex; i < _path.Count; i++) dst.Add(_path[i]);
    }

    /// <summary>A province was merged away: every reference to it now points to the origin.</summary>
    internal void RemapProvince(Province from, Province to)
    {
        if (Province == from) Province = to;
        if (CurrentProvince == from) CurrentProvince = to;
        if (TargetProvince == from) TargetProvince = to;
        if (CheckedProvince == from) CheckedProvince = to;
        if (_path != null)
            for (int i = 0; i < _path.Count; i++) if (_path[i] == from) _path[i] = to;
    }
    internal void Clear()
    {
        TacticLock = false;
        Military = null;
        Province = null;
        TargetProvince = null;
        CurrentProvince = null;
        CheckedProvince = null;
        View = null;
        Battle = null;
        MergeTarget = null;
        Role = ColumnRole.None;
        IsColumn = false;
        LostHp = 0.0;
        State = RegimentState.Idle;
        Manpower = 0.0;
        if (_path != null) _path.Clear();
        _pathIndex = 0;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  GAMEPLAY API
    // ─────────────────────────────────────────────────────────────────────
    public void TakeCasualties(double men)
    {
        if (Military == null || men <= 0.0) return;
        double lost = Math.Min(men, Manpower);
        Manpower -= lost;
        Military.CurrentManpower = Math.Max(0.0, Military.CurrentManpower - lost);
        RefreshText();
        ArmyManager.Instance.CheckRetreat(this);
    }

    public bool Disband() => Military != null && Military.RemoveRegiment(this);
    public bool SetShare(float percent) => Military != null && Military.SetRegimentShare(this, percent);

    /// <summary>Orders a march (use ArmyManager.MoveRegiment).</summary>
    internal void BeginMove(List<Province> path, Vector3 firstWorldPos)
    {
        if (_path == null) _path = new List<Province>(path.Count); else _path.Clear();
        _path.AddRange(path);
        _pathIndex = 0;
        _targetWorldPos = firstWorldPos;
        TargetProvince = null;
        State = RegimentState.Moving;
        TrailTile = new Vector2Int(-1, -1);
    }

    internal void BeginRetreat(Province target, Vector3 targetWorldPosition)
    {
        if (_path == null) _path = new List<Province>(1); else _path.Clear();
        _path.Add(target);
        _pathIndex = 0;
        Battle = null;
        State = RegimentState.Retreating;
        TargetProvince = target;
        _targetWorldPos = targetWorldPosition;
    }

    internal void EnterFight(Battle b)
    {
        Battle = b;
        State = RegimentState.Fighting;
    }

    /// <summary>After a battle: continue the march if there is one left, otherwise stand still.</summary>
    internal void LeaveFight()
    {
        Battle = null;
        if (State != RegimentState.Fighting) return;
        State = HasPath ? RegimentState.Moving : RegimentState.Idle;
    }

    internal void Teleport(Vector3 world) => WorldPosition = world;

    /// <summary>Moves 'step' world units along the path. Returns true when the whole path is done.</summary>
    internal bool StepMove(float step, ArmyManager army)
    {
        if (!HasPath) { Finish(); return true; }

        while (step > 0f)
        {
            float dist = Vector3.Distance(WorldPosition, _targetWorldPos);
            if (dist > step)
            {
                WorldPosition = Vector3.MoveTowards(WorldPosition, _targetWorldPos, step);
                return false;
            }
            WorldPosition = _targetWorldPos;
            step -= dist;

            Province = _path[_pathIndex];
            _pathIndex++;
            if (_pathIndex < _path.Count) _targetWorldPos = army.ProvinceWorldPos(_path[_pathIndex]);
            else { Finish(); return true; }
        }
        return false;
    }

    private void Finish()
    {
        _path?.Clear();
        _pathIndex = 0;
        if (State == RegimentState.Retreating) { TargetProvince = null; State = RegimentState.Reinforcing; }
        else if (State == RegimentState.Moving) State = RegimentState.Idle;
    }

    internal void RefreshText()
    {
        if (View != null) View.SetManpower(Manpower);
    }

    public static string FormatManpower(double v)
    {
        if (v >= 1000000.0) return (v / 1000000.0).ToString("0.#") + "M";
        if (v >= 1000.0) return (v / 1000.0).ToString("0.#") + "K";
        return ((int)v).ToString();
    }
}