using System.Numerics;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Movement;

/// <summary>
/// Puts living characters somewhere else at once (engine API for code mods, not in the original): a squad sent to its own place at a new game's
/// start, a story's scene change, a fast travel. Serial phases and <see cref="IWorldReady"/>.
/// </summary>
public static class CharacterPlacement
{
    /// <summary>
    /// How far a walkable spot is looked for round a point that is not walkable: 20 tries in discs growing by this much (the new game's search for
    /// where its squad stands).
    /// </summary>
    public const float SearchStep = 50;

    /// <summary>
    /// The tries of <see cref="WalkableNear"/>.
    /// </summary>
    const int SearchTries = 20;

    /// <summary>
    /// Puts <paramref name="characters"/> at <paramref name="at"/>: the first on the walkable spot nearest it (<see cref="WalkableNear"/>), the
    /// others spread round that spot as a ground-click move order of that many characters spreads them (<see cref="Formation.Scatter"/>; a spread
    /// spot that is not walkable takes the first one's), each on the ground there (the floor nearest <paramref name="at"/>'s height, else the
    /// terrain). Each one stands still facing as it did, its fight, task, path and orders dropped (its move, follow and job queue, as the Stop
    /// order drops them), so it waits there for whoever leads it next; the characters it carries come along. Outside a tick both of the table's
    /// state buffers are written, so the host's camera, which a new game puts on the first squad's leader after <see cref="IWorldReady"/>, sees
    /// the new place. A character that is dead, is carried or is held in a bed, cage or on a pole is left where it is (release it first); the
    /// rest keep their order in the list. Returns how many were put, 0 for a point that is not finite, with no walkable spot near it, or during
    /// the parallel phase. Each call costs a walkability test per character; call it at an event, not per tick.
    /// </summary>
    public static int Place(World world, IReadOnlyList<CharacterId> characters, Vector3 at)
    {
        var table = world.Characters;
        if (table.Phase == TablePhase.Parallel || characters.Count == 0) return 0;
        if (!float.IsFinite(at.X) || !float.IsFinite(at.Y) || !float.IsFinite(at.Z)) return 0;

        var centre = new Vector2(at.X, at.Z);
        ulong key = Rng.Mix(BitConverter.SingleToUInt32Bits(at.X) | (ulong)BitConverter.SingleToUInt32Bits(at.Z) << 32);
        if (WalkableNear(world, centre, key) is not { } first) return 0;

        var spots = Formation.Scatter(characters.Count, first);
        int placed = 0;
        foreach (var id in characters)
        {
            if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { Medical.Dead: false } cold) continue;
            if (!cold.CarriedBy.IsNone || cold.InSomething != HeldState.None) continue;

            var spot = spots[placed++];
            if (!world.Walkability.IsWalkable(spot.X, spot.Y)) spot = first;
            Halt(world, slot, cold);
            var ground = new Vector3(spot.X, world.Walkability.GroundHeight(spot.X, spot.Y, at.Y), spot.Y);
            table.Place(slot, ground, table.Next[slot].Yaw);
            PlaceCarried(table, cold, ground);
        }

        return placed;
    }

    /// <summary>
    /// <paramref name="centre"/> when walkable, else a walkable point drawn in discs round it growing by <see cref="SearchStep"/> (20 tries keyed by
    /// the world's seed and <paramref name="key"/>), else null.
    /// </summary>
    public static Vector2? WalkableNear(World world, Vector2 centre, ulong key)
    {
        if (world.Walkability.IsWalkable(centre.X, centre.Y)) return centre;

        for (int attempt = 0; attempt < SearchTries; attempt++)
        {
            var p = Rng.PointInDisc(Rng.Hash(world.Seed, key, RngPurpose.Spawn, (ulong)attempt), centre, SearchStep * (1 + attempt));
            if (world.Walkability.IsWalkable(p.X, p.Y)) return p;
        }

        return null;
    }

    /// <summary>
    /// Drops what the character in <paramref name="slot"/> was doing: its fight, and for the player's characters what the Stop order drops
    /// (<see cref="PlayerOrders.Stop"/>), for anyone else its task.
    /// </summary>
    static void Halt(World world, int slot, CharacterCold cold)
    {
        world.SystemOf<CombatSystem>()?.Disengage(world.Characters, cold.Id);
        if (cold.IsPlayer) PlayerOrders.Stop(world, slot, cold);
        else TaskStart.End(world, slot, TaskEnd.Stopped);
    }

    /// <summary>
    /// Puts the body <paramref name="carrier"/> carries at its new place too, so outside a tick both buffers already show it there; the carry system
    /// sets it on the carrier's shoulder at the next step.
    /// </summary>
    static void PlaceCarried(CharacterTable table, CharacterCold carrier, Vector3 at)
    {
        if (carrier.Carrying.IsNone || !table.TryResolveNext(carrier.Carrying, out int body)) return;

        table.Place(body, at, table.Next[body].Yaw);
    }
}
