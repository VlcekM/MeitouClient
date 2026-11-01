using System.Numerics;

namespace Meitou.Simulation;

public sealed partial class World
{
    /// <summary>Prepares nearby population at a frame boundary and publishes it without running AI, movement, combat or game time.</summary>
    public bool PreparePopulation(Vector3 cameraFocus)
    {
        if (Characters.Phase != TablePhase.Outside) throw new InvalidOperationException("population loading requires a tick boundary");
        var population = systems.OfType<PopulationSystem>().FirstOrDefault();
        if (population is null) return true;
        bool ready;
        Characters.BeginTick();
        Characters.Phase = TablePhase.SlowWorld;
        try { ready = population.PrepareLoading(this, cameraFocus); }
        finally
        {
            Characters.Phase = TablePhase.Outside;
            Characters.EndTick();
        }
        if (Settings.PublishSnapshots)
        {
            Publish();
            KeepPrevious(Snapshot, shownBuffer);
        }
        return ready;
    }
}
