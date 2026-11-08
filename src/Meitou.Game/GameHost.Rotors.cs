using System.Diagnostics;
using System.Numerics;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Game.Rendering.Objects;

namespace Meitou.Game;

/// <summary>
/// The parts that turn by themselves (<see cref="WorldObjectRenderer.AdvanceRotors"/>, docs/game/buildings-production.md "Rotating parts"): each frame the object renderer is told the scheduled wind
/// and what each building's ROTATION_OUTPUT parts turn by, then moves them by the frame's game time (real time x speed, 0 while paused). Display only.
/// </summary>
sealed partial class GameHost
{
    readonly Dictionary<string, float> rotorOutputs = new(StringComparer.Ordinal);
    readonly Dictionary<string, float> levelRotorOutputs = new(StringComparer.Ordinal);
    readonly HashSet<string> unbuiltSites = new(StringComparer.Ordinal);
    readonly Stopwatch rotorClock = new();

    /// <summary>Once a frame, with the buildings.</summary>
    void UpdateRotors(Meitou.Simulation.WorldSnapshot snapshot)
    {
        if (gpu?.Objects is not { } objects) return;
        rotorOutputs.Clear();
        foreach (var m in snapshot.Machines)
            if (!m.IsContainer) rotorOutputs[m.Site] = m.Rotation;
        unbuiltSites.Clear();
        foreach (var site in snapshot.Buildings)
            if (!site.Complete) unbuiltSites.Add(site.Id);
        objects.RotorOutput ??= RotorOutputOf;
        objects.RotorUnbuilt ??= placed => unbuiltSites.Contains(placed.PlacementId);
        objects.RotorWind ??= (x, z) => gpu.Weather?.World is { } weather ? weather.WindAt(x, z) : (0, Vector2.Zero);
        float real = rotorClock.IsRunning ? (float)Math.Min(rotorClock.Elapsed.TotalSeconds, 0.25) : 0;
        rotorClock.Restart();
        var sim = session.Simulation;
        objects.AdvanceRotors(sim.IsPaused ? 0 : real * (float)sim.Speed);
    }

    /// <summary>
    /// A building's ROTATION_OUTPUT share: a player machine's (<see cref="Meitou.Simulation.Production.MachineSnapshot.Rotation"/>); a level building, which the simulation does not run, by its data as
    /// its sound emitter is (engine choice): a generator its output x 0.01, a battery and a machine that needs neither operators nor power its <c>output rate</c>, any other machine 0, a building
    /// without a function 1 (the plain building's).
    /// </summary>
    float RotorOutputOf(PlacedMesh placed)
    {
        if (rotorOutputs.TryGetValue(placed.PlacementId, out float share)) return share;
        string id = placed.Owner.StringId;
        if (levelRotorOutputs.TryGetValue(id, out share)) return share;
        var data = scene.Database is { } db ? MachineData.From(db, placed.Owner) : null;
        share = data is null ? 1
            : data.IsGenerator ? data.PowerOutput * 0.01f
            : data.IsBattery || (data.PowerOperators == 0 && data.PowerOutput == 0) ? data.OutputRate
            : 0;
        levelRotorOutputs[id] = share;
        return share;
    }
}
