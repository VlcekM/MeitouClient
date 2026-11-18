using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Data.World;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The loaded save's building-list entries of the towns' layout objects. The original saves a house's generated furniture as entries of their own
/// (ids <c>&lt;n&gt;-zone&lt;X&gt;.&lt;Y&gt;-INGAME</c>, state <c>is inside building</c>, the house's <c>serialised interior</c> 1; docs/formats/save.md
/// "Zone files"); their ids are not Meitou's, so a layout object is matched to the entry of its record standing within <see cref="Reach"/> of it, as
/// an interior cage is (<see cref="Locks.FixtureRegistry.SavedSpots"/>). Serial phases only.
/// </summary>
public sealed class SavedPieces
{
    /// <summary>
    /// How near an entry must stand to the layout object (<b>Observed</b>: the saved furniture stands at distance 0 from Meitou's placement of the
    /// same layout, docs/formats/save.md "Locks, cages and sentences").
    /// </summary>
    public const float Reach = 1;

    /// <summary>
    /// The entries by record: position, building key, zone, instance and GAMESTATE_BUILDING (null: none listed).
    /// </summary>
    readonly Dictionary<string, List<SavedPiece>> _byRecord = new(StringComparer.Ordinal);

    /// <summary>
    /// The entry each layout object was matched to, by its id; null: none.
    /// </summary>
    readonly Dictionary<string, SavedPiece?> _matched = new(StringComparer.Ordinal);

    /// <summary>
    /// Indexes every entry of the save's building lists that names a record.
    /// </summary>
    public SavedPieces(SaveGame save)
    {
        foreach (var zone in save.Zones.Values)
        {
            if (zone.BuildingList is not { } list) continue;

            var states = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
            foreach (var r in zone.BuildingStates) states[r.StringId] = r;

            foreach (var i in list.Instances)
                if (i.Target.Length > 0) Add(zone, i, states);
        }
    }

    /// <summary>
    /// The entry of the layout object <paramref name="piece"/>: the first of its record within <see cref="Reach"/>; null for none.
    /// </summary>
    public SavedPiece? Of(TownLayoutBuilding piece) => Of(piece.Id, piece.RecordId, piece.Position);

    /// <summary>
    /// The entry of the layout object <paramref name="id"/> (record <paramref name="recordId"/>, standing at <paramref name="position"/>): the first of
    /// its record within <see cref="Reach"/>; null for none.
    /// </summary>
    public SavedPiece? Of(string id, string recordId, Vector3 position)
    {
        if (_matched.TryGetValue(id, out var known)) return known;

        SavedPiece? found = null;
        foreach (var entry in _byRecord.GetValueOrDefault(recordId) ?? [])
        {
            if (Vector3.DistanceSquared(entry.Instance.Position, position) > Reach * Reach) continue;

            found = entry;
            break;
        }

        _matched[id] = found;
        return found;
    }

    /// <summary>
    /// Indexes one entry with its building state (an inventory state can come first, docs/formats/save.md "Zone files").
    /// </summary>
    void Add(SaveZone zone, FcsInstance instance, Dictionary<string, FcsRecord> states)
    {
        var state = instance.States.Select(id => states.GetValueOrDefault(id)).FirstOrDefault(r => r is not null);
        if (!_byRecord.TryGetValue(instance.Target, out var list)) _byRecord[instance.Target] = list = [];
        list.Add(new SavedPiece(zone, instance, state));
    }
}

/// <summary>
/// A building-list entry a layout object was matched to (<see cref="SavedPieces"/>): its zone file, instance and GAMESTATE_BUILDING (null: none).
/// Its keys are read only from a zone that is not loaded fresh (<see cref="Fresh"/>), as the original's loader guard has it
/// (docs/game/buildings-production.md "NPC generator state").
/// </summary>
public sealed record SavedPiece(SaveZone Zone, FcsInstance Instance, FcsRecord? State)
{
    /// <summary>
    /// The zone loads fresh in spite of its file: its building list holds <c>imported</c> (<c>FUN_1409ffad0</c>, key presence).
    /// </summary>
    public bool Fresh => Zone.BuildingList?.Bools.ContainsKey("imported") == true;
}
