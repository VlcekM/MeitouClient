using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Population;

/// <summary>
/// The population's squad splits: an imprisoned or carried-off member's own squad and the platoons that must be kept.
/// </summary>
public sealed partial class PopulationSystem
{
    /// <summary>
    /// The AI package an imprisoned member's own squad runs (<c>FUN_14079c1c0</c>, <b>Verified</b>): "captured NPC".
    /// </summary>
    const string CapturedPackage = "CAPTURED_NPC";

    /// <summary>
    /// The AI package that squad runs when the member is a slave (slave state 1): "Slave only squad".
    /// </summary>
    const string SlaveOnlyPackage = "5344-gamedata.base";

    /// <summary>
    /// What a split-off squad takes from the one it leaves: the platoon's origin town, template, faction, key and persistence.
    /// </summary>
    readonly record struct SplitFrom(int Origin, string TemplateId, int Faction, ulong Key, bool Persistent);

    /// <summary>
    /// The split at unload of a platoon (<c>FUN_14079c1c0</c>, <b>Verified</b>; one with 2 or more members): see
    /// <see cref="SplitCaptives(World, Squad, SplitFrom)"/>, with the platoon's origin, template, faction, key and persistence.
    /// </summary>
    void SplitCaptives(World world, Platoon p, Squad squad) =>
        SplitCaptives(world, squad, new SplitFrom(p.Origin, p.TemplateId, p.Faction, p.Key, PersistentPlatoon(p)));

    /// <summary>
    /// The split at unload (<c>FUN_14079c1c0</c>, <b>Verified</b>; a squad with 2 or more members): each imprisoned member, and each one carried
    /// away (<see cref="CarriedAway"/>), goes into a squad and platoon of its own, same faction and template, at its place, loaded (it then keeps or
    /// loses its zone by its own test), with the captured NPC package (the slave-only one for a slave); persistent when the old platoon is or the
    /// member is unique. A persistent platoon left with just that member keeps it (reused). The original's skip of a chained slave whose owner exists
    /// is not modelled. Serial.
    /// </summary>
    void SplitCaptives(World world, Squad squad, SplitFrom from)
    {
        var table = world.Characters;
        foreach (var id in squad.Members.ToArray())
        {
            if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { } cold) continue;
            if (!cold.IsImprisoned && !CarriedAway(world, cold)) continue;
            if (squad.Members.Count == 1 && from.Persistent) break;

            SplitOff(world, squad, from, id, cold, table.Previous[slot].Position);
        }
    }

    /// <summary>
    /// The carried clause of the split (<c>FUN_14079c1c0</c>, <b>Verified</b>): a member carried by a character it does not regard as friendly (vfunc
    /// <c>0x3f0</c>; here the same faction or an allied one), or by one of the player's faction, whatever the relation. A squad mate carrying a
    /// wounded mate home is not split.
    /// </summary>
    bool CarriedAway(World world, CharacterCold c)
    {
        var table = world.Characters;
        if (c.CarriedBy.IsNone || !table.TryResolveNext(c.CarriedBy, out int slot) || table.Cold(slot) is not { } carrier) return false;

        int player = PlayerFactionOf(world);
        if (carrier.IsPlayer || player >= 0 && carrier.Faction == player) return true;

        var relations = _data.Relations;
        bool friend = carrier.Faction == c.Faction
            || relations.Contains(carrier.Faction) && relations.Contains(c.Faction) && relations.IsAlly(c.Faction, carrier.Faction);
        return !friend;
    }

    /// <summary>
    /// Moves member <paramref name="id"/> out of <paramref name="squad"/> into a new loaded squad and platoon at <paramref name="at"/> running the
    /// captured NPC package (the slave-only one for a slave).
    /// </summary>
    void SplitOff(World world, Squad squad, SplitFrom from, CharacterId id, CharacterCold cold, Vector3 at)
    {
        var np = new Platoon
        {
            Id = world.Platoons.NextId(),
            Origin = from.Origin,
            TemplateId = from.TemplateId,
            Faction = from.Faction,
            Key = from.Key ^ (ulong)(squad.Members.Count + 1) * 0x9E3779B97F4A7C15UL,
            Size = 1,
            Position = new Vector2(at.X, at.Z),
            State = PlatoonState.Loaded,
            DwellLeft = 5,
            Persistent = from.Persistent || UniqueCharacters.IsUnique(_data.Db.Find(cold.RecordId)),
        };
        string package = cold.SlaveState == 1 ? SlaveOnlyPackage : CapturedPackage;
        var made = new Squad
        {
            Id = world.Squads.NextId(),
            TemplateId = squad.TemplateId,
            Name = squad.Name,
            Faction = squad.Faction,
            Town = squad.Town,
            PlatoonId = np.Id,
            Position = at,
            HomeCentre = new Vector2(at.X, at.Z),
            HomeRadius = squad.HomeRadius,
            SpawnSize = 1,
            SavedAi = PackageNamed(package) is { } found ? new Saving.AiSave(null, 0, found) : null,
        };

        squad.Members.Remove(id);
        if (squad.Leader == id) squad.Leader = SquadRegistry.Successor(squad.Members, world.Characters);
        LeaveSavedPlatoon(world, cold);

        cold.SquadId = made.Id;
        cold.Role = (int)SquadRole.Leader;
        cold.Ai = null;
        made.Members.Add(id);
        made.Leader = id;
        np.SquadId = made.Id;
        np.Package = package;
        world.Squads.Add(made);
        world.Platoons.Add(np);
    }

    /// <summary>
    /// A member made from a save's platoon leaves that platoon's record with the split, as the original's split moves the character into the new
    /// platoon (<c>FUN_14079c1c0</c>): a save then writes it with its new platoon, where it is. Kept in the old record it came back only with its
    /// home squad, so a kidnapped town character was gone after a load (#622).
    /// </summary>
    static void LeaveSavedPlatoon(World world, CharacterCold cold)
    {
        if (cold.Save is not { } link || world.Saved?.MemberOf(link) is not { } member) return;

        member.Gone = true;
        member.Live = CharacterId.None;
    }

    /// <summary>
    /// Whether a platoon is persistent: set by the engine or its saved record, unless cleared since.
    /// </summary>
    bool PersistentPlatoon(Platoon p) => !p.PersistentCleared && (p.Persistent || p.Saved?.Source.Record.Bools.GetValueOrDefault("persistent") == true);

    /// <summary>
    /// The id of the AI_PACKAGE the original looks up by name (<c>FUN_1406bfe60</c>); null when the data has none.
    /// </summary>
    string? PackageNamed(string name) =>
        _data.Db.Find(name) is { Type: FcsRecordType.AiPackage } r
            ? r.StringId
            : _data.Db.OfType(FcsRecordType.AiPackage).FirstOrDefault(x => x.Name == name)?.StringId;
}
