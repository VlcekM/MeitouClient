using System.Numerics;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Saving;

/// <summary>
/// "Reset squad positions" (<c>Faction::resetSquadPositions</c>, <c>FUN_14036b370</c>; docs/formats/save.md "Reset squad positions"): the player's
/// squads of a save moved to one point before the world is made from it, squad i of the list at the point + (i, 0, i). The import uses the fixed
/// <see cref="SaveImport.ResetPosition"/>; a load the saved camera's pivot (<see cref="LoadPoint"/>).
/// </summary>
public static class SquadReset
{
    /// <summary>
    /// The squads a load's reset moves: the player's loaded ones but the squad of the dead, in the save's order.
    /// </summary>
    public static List<SavePlatoon> PlayerSquads(SaveGame game)
    {
        if (game.PlayerFaction is not { } player) return [];

        return [.. game.Platoons.Where(p => p.FactionId == player.Id && p.IsLoaded && !IsDeadSquad(p))];
    }

    /// <summary>
    /// Where a load's reset puts the squads (<c>loadGame</c>, <c>FUN_140373f00</c>): the camera's pivot once the save's camera is read, so the saved
    /// <c>pos</c>, or the loader's fallback when it is out of bounds (docs/formats/camera.md "Saved camera"). A save without a view (an older Meitou
    /// one, whose camera goes to the leader) gives the first squad's first member. Null when there is no such point.
    /// </summary>
    public static Vector3? LoadPoint(SaveCamera camera, IReadOnlyList<SavePlatoon> squads)
    {
        if (camera.HasView && KenshiCamera.SavedPivotInBounds(camera.Position)) return camera.Position;

        if (camera.HasView) return new Vector3(KenshiCamera.SavedPivotFallback.X, 0, KenshiCamera.SavedPivotFallback.Y);

        return squads.SelectMany(p => p.Characters).Select(c => (Vector3?)c.Position).FirstOrDefault();
    }

    /// <summary>
    /// Moves each of <paramref name="squads"/> to <paramref name="at"/> + (i, 0, i), i its place in the list, with its members.
    /// </summary>
    public static void Move(IReadOnlyList<SavePlatoon> squads, Vector3 at)
    {
        for (int i = 0; i < squads.Count; i++) Move(squads[i], at + new Vector3(i, 0, i));
    }

    /// <summary>
    /// Moves one squad to <paramref name="at"/>. Engine choice: the original sets the squad's pending position; here each member also stands 2 units
    /// east of the last.
    /// </summary>
    public static void Move(SavePlatoon platoon, Vector3 at)
    {
        platoon.Position = at;
        for (int m = 0; m < platoon.Characters.Count; m++) platoon.Characters[m].Position = at + new Vector3(2 * m, 0, 0);
    }

    /// <summary>
    /// Stands <paramref name="squads"/> (and their members) on the ground under them: the original's point has no height.
    /// </summary>
    public static void PlaceOnGround(IEnumerable<SavePlatoon> squads, Func<float, float, float> groundAt)
    {
        foreach (var platoon in squads)
        {
            platoon.Position = platoon.Position with { Y = groundAt(platoon.Position.X, platoon.Position.Z) };
            foreach (var c in platoon.Characters) c.Position = c.Position with { Y = groundAt(c.Position.X, c.Position.Z) };
        }
    }

    /// <summary>
    /// Whether a platoon is the faction's squad of the dead (<c>__DEAD_SQUAD__</c>), by its string id or its record's name.
    /// </summary>
    static bool IsDeadSquad(SavePlatoon platoon) =>
        platoon.Name == SquadManagement.DeadSquadName || platoon.Record.Name == SquadManagement.DeadSquadName;
}
