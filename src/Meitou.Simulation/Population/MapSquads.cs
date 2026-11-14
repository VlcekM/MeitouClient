using System.Numerics;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Population;

/// <summary>
/// A squad of another faction the world map shows (docs/game/ui-screens.md "Map", Characters): <paramref name="Key"/> is unique among the shown
/// ones, <paramref name="Position"/> its leader's place (X, Z), <paramref name="Faction"/> a position in the factions (-1: none);
/// <paramref name="Ally"/>: it works for the player (a contract on a player character).
/// </summary>
public readonly record struct MapSquadSnapshot(int Key, Vector2 Position, int Faction, bool Ally);

/// <summary>
/// The squads the world map tracks besides the player's (the original's MapScreen set, filled by <c>CampaignInstance</c> slot 1 and
/// <c>AIPackage</c> slot 15): every platoon of a running campaign and every squad under a contract whose mission target is a player character.
/// Listed again once a second, as the original moves its markers.
/// </summary>
public sealed class MapSquads
{
    /// <summary>
    /// The list as last made.
    /// </summary>
    IReadOnlyList<MapSquadSnapshot> _shown = [];

    /// <summary>
    /// The tick <see cref="_shown"/> is made again at.
    /// </summary>
    long _due;

    /// <summary>
    /// The tracked squads, made again when a second of ticks has passed.
    /// </summary>
    public IReadOnlyList<MapSquadSnapshot> Published(World world)
    {
        if (world.Tick < _due) return _shown;

        _due = world.Tick + Math.Max(1, (long)(1 / world.TickSeconds));
        var list = new List<MapSquadSnapshot>();
        var listed = new HashSet<int>();
        AddCampaigns(world, list, listed);
        AddContracts(world, list, listed);

        return _shown = list.Count == 0 ? [] : list;
    }

    /// <summary>
    /// The platoons of a campaign, loaded or not, until the campaign is removed.
    /// </summary>
    static void AddCampaigns(World world, List<MapSquadSnapshot> list, HashSet<int> listed)
    {
        foreach (var p in world.Platoons.All)
        {
            if (p.Campaign is null || p.Dead) continue;

            var squad = p.State == PlatoonState.Loaded ? world.Squads.Find(p.SquadId) : null;
            if (squad is not null) listed.Add(squad.Id);

            var at = squad is not null && LeaderPlace(world, squad) is { } leader ? leader : p.Position;
            list.Add(new(p.Id, at, p.Faction, false));
        }
    }

    /// <summary>
    /// The squads under a contract on a player character (hired guards and the like): allies.
    /// </summary>
    static void AddContracts(World world, List<MapSquadSnapshot> list, HashSet<int> listed)
    {
        foreach (var squad in world.Squads.All)
        {
            if (squad.Ai is not { Contract: not null } board || !IsPlayer(world, board.MissionTarget)) continue;
            if (listed.Contains(squad.Id) || LeaderPlace(world, squad) is not { } at) continue;

            // Contract keys sit below the platoon ids.
            list.Add(new(-1 - squad.Id, at, squad.Faction, true));
        }
    }

    /// <summary>
    /// Whether <paramref name="id"/> is a living character of the player's.
    /// </summary>
    static bool IsPlayer(World world, CharacterId id) =>
        world.Characters.IsAlive(id) && world.Characters.Cold(id.Slot) is { IsPlayer: true };

    /// <summary>
    /// Where the squad's leader stands, else its first living member; null when none lives.
    /// </summary>
    static Vector2? LeaderPlace(World world, Squad squad)
    {
        var table = world.Characters;
        if (table.IsAlive(squad.Leader)) return Flat(table.Previous[squad.Leader.Slot].Position);

        foreach (var member in squad.Members)
            if (table.IsAlive(member)) return Flat(table.Previous[member.Slot].Position);

        return null;
    }

    /// <summary>
    /// A world point's X and Z.
    /// </summary>
    static Vector2 Flat(Vector3 p) => new(p.X, p.Z);
}
