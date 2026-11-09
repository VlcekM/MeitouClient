using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Saving;

/// <summary>
/// A character's slave state in its GAMESTATE_CHARACTER record: the original's <c>slavestate</c> int
/// (0 free, 1 slave, 2 escaped, 3 ex-slave; <c>FUN_140626580</c> writes it with <c>personality</c>, docs/formats/save.md),
/// the original's <c>ssct</c> float (<see cref="CharacterCold.SlaveSince"/>, written always), the original's <c>slaver</c> handle
/// (the owner character) and Meitou's <c>meitou slave owner</c> (the owner FACTION's string id), and the original's <c>shaved</c> bool
/// (<see cref="CharacterCold.Shaved"/>, written always). The owner key is written only while set and removed otherwise;
/// the <c>slaver</c> keys are left as they were read until the slave is freed or handed to another master,
/// then written as the null handle.
/// </summary>
public static class SlaveSave
{
    /// <summary>
    /// The GAMESTATE_CHARACTER keys of the slave state (see the class remarks).
    /// </summary>
    public const string StateKey = "slavestate", SinceKey = "ssct", OwnerKey = "meitou slave owner", SlaverKey = "slaver", ShavedKey = "shaved";

    /// <summary>
    /// Reads the slave state, its start, the owner faction and character and the shaved flag (nothing when there is no record).
    /// </summary>
    public static void Read(FcsRecord? state, CharacterCold cold, PopulationData data)
    {
        if (state is null) return;

        // The original keeps any int; Meitou models 0 to 3, so anything out of range loads as free.
        int saved = state.Ints.GetValueOrDefault(StateKey);
        cold.SlaveState = saved is >= 1 and <= 3 ? (byte)saved : (byte)0;
        cold.SlaveSince = state.Floats.GetValueOrDefault(SinceKey);
        cold.SlaveOwner = cold.SlaveState != 0 && state.Strings.GetValueOrDefault(OwnerKey) is { Length: > 0 } owner
            ? data.FactionIndex(owner)
            : -1;
        cold.Shaved = state.Bools.GetValueOrDefault(ShavedKey);

        // The owner character, found once it is in the world; a handle of another class (a squad) is not mapped.
        if (cold.SlaveState == 0 || cold.Save is not { } link) return;
        if (Hand.Read(state, SlaverKey) is { RecordType: FcsRecordType.Character } slaver) link.Slaver = slaver;
    }

    /// <summary>
    /// Writes the slave state, its start and the shaved flag; the owner faction only while set, the <c>slaver</c> handle as null once stale.
    /// </summary>
    public static void Write(FcsRecord state, CharacterCold cold, PopulationData data)
    {
        state.Ints[StateKey] = cold.SlaveState;
        state.Floats[SinceKey] = (float)cold.SlaveSince;
        state.Bools[ShavedKey] = cold.Shaved;
        if (cold.SlaveState != 0 && (uint)cold.SlaveOwner < (uint)data.Factions.Count)
            state.Strings[OwnerKey] = data.Factions[cold.SlaveOwner].Id;
        else
            state.Strings.Remove(OwnerKey);

        if (cold.Save is { SlaverStale: true }) Hand.Null.Write(state, SlaverKey);
    }
}