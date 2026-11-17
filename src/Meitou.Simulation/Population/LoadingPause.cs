using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Simulation.Population;

/// <summary>
/// The original's loading pause (docs/game/game-loop.md "Loading pause", <c>FUN_140a0f820</c>, <b>Verified</b>), a Faithful switch: while a load is
/// under way and the game is not paused otherwise, a corner of the square round the camera focus (<see cref="ZoneActivation.LoadingPauseReach"/>)
/// off the grid or in a zone not loaded holds the game paused without touching its speed, until every requested zone is loaded.
/// </summary>
public static class LoadingPause
{
    /// <summary>
    /// One frame of the pause: whether the world is held after it, given whether it was (<paramref name="held"/>) and whether it is paused otherwise
    /// (<paramref name="paused"/>). A world not yet held is held only when it is not paused, a load is under way and a corner round
    /// <paramref name="focus"/> is not loaded. A world that runs no tick (held, or paused with a load under way) takes the frame's zone step
    /// (<see cref="PopulationSystem.StepZones"/>), as the original's zone manager runs under any pause; a held one is let go once nothing is left to
    /// load.
    /// </summary>
    public static bool Step(World world, PopulationSystem population, Vector3 focus, bool held, bool paused)
    {
        if (world.LoadedZones is not { } loaded || !population.LoadPending(world, focus)) return false;

        if (!held && (paused || !CornerUnloaded(loaded, focus)))
        {
            if (paused) population.StepZones(world, focus);
            return false;
        }

        population.StepZones(world, focus);
        return population.LoadPending(world, focus);
    }

    /// <summary>
    /// Whether one of the four corners of the square round <paramref name="focus"/> is off the zone grid or in a zone not in <paramref name="loaded"/>.
    /// </summary>
    static bool CornerUnloaded(IReadOnlyCollection<ZoneCoordinate> loaded, Vector3 focus)
    {
        const float reach = ZoneActivation.LoadingPauseReach;
        for (int i = 0; i < 4; i++)
        {
            var zone = WorldLayout.ZoneOf(focus.X - reach + (i & 1) * 2 * reach, focus.Z - reach + (i >> 1) * 2 * reach);
            if (!zone.IsInsideGrid || !loaded.Contains(zone)) return true;
        }

        return false;
    }
}
