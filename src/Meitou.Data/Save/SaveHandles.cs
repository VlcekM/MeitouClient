using Meitou.Data.Fcs;

namespace Meitou.Data.Save;

/// <summary>
/// Resolves <see cref="Hand"/>s against a save: platoon handles by the platoon's container, character handles by (container, slot) within the platoon,
/// town and nest handles by the town's container, building handles by (zone container, slot) with the slot taken from the buildings' own handles.
/// </summary>
public sealed class SaveHandles
{
    /// <summary>
    /// The platoons by their handle's container.
    /// </summary>
    readonly Dictionary<int, SavePlatoon> _platoons = [];

    /// <summary>
    /// The characters by their handle's (container, slot).
    /// </summary>
    readonly Dictionary<(int C, int I), SaveCharacter> _characters = [];

    /// <summary>
    /// The towns and nests by their handle's container.
    /// </summary>
    readonly Dictionary<int, SaveTown> _towns = [];

    /// <summary>
    /// The zones' building states by their own handle's (container, slot).
    /// </summary>
    readonly Dictionary<(int C, int I), FcsRecord> _buildings = [];

    /// <summary>
    /// Indexes the platoons, characters, towns and building states of <paramref name="game"/>; null handles are left out.
    /// </summary>
    internal SaveHandles(SaveGame game)
    {
        foreach (var p in game.Platoons)
        {
            if (!p.Handle.IsNull) _platoons[p.Handle.C] = p;
            foreach (var c in p.Characters)
                if (!c.Handle.IsNull) _characters[(c.Handle.C, c.Handle.I)] = c;
        }
        foreach (var t in game.Towns)
            if (!t.Handle.IsNull) _towns[t.Handle.C] = t;
        foreach (var z in game.Zones.Values)
            foreach (var s in z.BuildingStates)
                if (Hand.Read(s, "handle") is { IsNull: false } h) _buildings[(h.C, h.I)] = s;
    }

    /// <summary>
    /// The platoon a PLATOON handle names, or null.
    /// </summary>
    public SavePlatoon? Platoon(Hand h) => h.Type == (int)FcsRecordType.Platoon ? _platoons.GetValueOrDefault(h.C) : null;

    /// <summary>
    /// The character a CHARACTER handle names, or null.
    /// </summary>
    public SaveCharacter? Character(Hand h) => h.Type == (int)FcsRecordType.Character ? _characters.GetValueOrDefault((h.C, h.I)) : null;

    /// <summary>
    /// The town a TOWN or NEST (85) handle names, or null.
    /// </summary>
    public SaveTown? Town(Hand h) => h.Type is (int)FcsRecordType.Town or 85 ? _towns.GetValueOrDefault(h.C) : null;

    /// <summary>
    /// The building state a BUILDING handle names, or null.
    /// </summary>
    public FcsRecord? Building(Hand h) => h.Type == (int)FcsRecordType.Building ? _buildings.GetValueOrDefault((h.C, h.I)) : null;

    /// <summary>
    /// The object a non-null handle names, or null when this save does not hold it.
    /// </summary>
    public object? Resolve(Hand h) =>
        h.IsNull ? null : h.Type switch
        {
            (int)FcsRecordType.Platoon => Platoon(h),
            (int)FcsRecordType.Character => Character(h),
            (int)FcsRecordType.Town or 85 => Town(h),
            (int)FcsRecordType.Building => Building(h),
            _ => null,
        };
}