using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Simulation;

public sealed partial class PopulationSystem
{
    /// <summary>The AI package an imprisoned member's own squad runs (<c>FUN_14079c1c0</c>, <b>Verified</b>): "captured NPC".</summary>
    const string CapturedPackage = "CAPTURED_NPC";

    /// <summary>The AI package that squad runs when the member is a slave (slave state 1): "Slave only squad".</summary>
    const string SlaveOnlyPackage = "5344-gamedata.base";

    /// <summary>
    /// The split at unload (<c>FUN_14079c1c0</c>, <b>Verified</b>; a platoon with 2 or more members): each imprisoned member goes into a squad and platoon of its own, same faction
    /// and template, at its place, loaded (it then keeps or loses its zone by its own test), with the captured NPC package (the slave-only one for a slave); persistent when the
    /// old platoon is or the member is unique. A persistent platoon left with just that member keeps it (reused). The original's skip of a chained slave whose owner exists and its
    /// carried clause (partly <b>Unknown</b>) are not modelled. Serial.
    /// </summary>
    void SplitPrisoners(World world, Platoon p, Squad squad)
    {
        var table = world.Characters;
        foreach (var id in squad.Members.ToArray())
        {
            if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { IsImprisoned: true } cold) continue;
            if (squad.Members.Count == 1 && PersistentPlatoon(p)) break;
            var at = table.Previous[slot].Position;
            var np = new Platoon
            {
                Id = world.Platoons.NextId(), Origin = p.Origin, TemplateId = p.TemplateId, Faction = p.Faction, Key = p.Key ^ (ulong)(squad.Members.Count + 1) * 0x9E3779B97F4A7C15UL,
                Size = 1, Position = new Vector2(at.X, at.Z), State = PlatoonState.Loaded, DwellLeft = 5,
                Persistent = PersistentPlatoon(p) || Meitou.Simulation.UniqueCharacters.IsUnique(data.Db.Find(cold.RecordId)),
            };
            string package = cold.SlaveState == 1 ? SlaveOnlyPackage : CapturedPackage;
            var made = new Squad
            {
                Id = world.Squads.NextId(), TemplateId = squad.TemplateId, Name = squad.Name, Faction = squad.Faction, Town = squad.Town, PlatoonId = np.Id,
                Position = at, HomeCentre = new Vector2(at.X, at.Z), HomeRadius = squad.HomeRadius, SpawnSize = 1,
                SavedAi = PackageNamed(package) is { } found ? new Saving.AiSave(null, 0, found) : null,
            };
            squad.Members.Remove(id);
            if (squad.Leader == id) squad.Leader = SquadRegistry.Successor(squad.Members, table);
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
    }

    bool PersistentPlatoon(Platoon p) => !p.PersistentCleared && (p.Persistent || p.Saved?.Source.Record.Bools.GetValueOrDefault("persistent") == true);

    /// <summary>The id of the AI_PACKAGE the original looks up by name (<c>FUN_1406bfe60</c>); null when the data has none.</summary>
    string? PackageNamed(string name) =>
        data.Db.Find(name) is { Type: FcsRecordType.AI_PACKAGE } r ? r.StringId : data.Db.OfType(FcsRecordType.AI_PACKAGE).FirstOrDefault(x => x.Name == name)?.StringId;
}
