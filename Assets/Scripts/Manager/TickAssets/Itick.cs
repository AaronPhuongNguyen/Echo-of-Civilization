
/// <summary>Called every frame. deltaTime is already multiplied by the game speed (0 while paused).</summary>
public interface ITick
{
    void Tick(float deltaTime);
}

public interface ISimTick
{
    void SimTick(float step);
}