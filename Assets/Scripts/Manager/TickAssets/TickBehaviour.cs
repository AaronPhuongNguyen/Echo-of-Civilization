using UnityEngine;

/// <summary>Optional base class: registers itself in OnEnable and unregisters in OnDisable.</summary>
public abstract class TickBehaviour : MonoBehaviour, ITick
{
    protected virtual int TickPriority => 0;               // lower = earlier
    protected virtual bool IgnoreGameSpeed => false;       // true = keeps ticking while paused (UI, camera)

    protected virtual void OnEnable() => TickManager.AddTick(this, TickPriority, IgnoreGameSpeed);
    protected virtual void OnDisable() => TickManager.RemoveTick(this);

    public abstract void Tick(float deltaTime);
}