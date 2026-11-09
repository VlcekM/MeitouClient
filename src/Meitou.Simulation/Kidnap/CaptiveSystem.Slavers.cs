namespace Meitou.Simulation.Kidnap;

/// <summary>
/// The owners a save names for its slaves (the original's `slaver` handle, docs/formats/save.md "Slave state").
/// </summary>
public sealed partial class CaptiveSystem
{
    /// <summary>
    /// How often the saved slaves still waiting for their owner are looked at (engine choice, as the carry links).
    /// </summary>
    const int RestoreEveryTicks = 20;

    /// <summary>
    /// The table's spawn count at the last pass: an owner not in the world can only appear through a spawn, so the pass waits for one.
    /// </summary>
    long _slaversSeenSpawns = -1;

    /// <summary>
    /// A loaded slave whose save names its owner character (<see cref="Saving.SavedCharacterLink.Slaver"/>) takes it once that character is in the
    /// world: the owner's faction when it has none (Meitou's own key wins), and the owner's squad as the squad it obeys
    /// (<see cref="CharacterCold.SlaveMaster"/>). One waits while its owner is not loaded; a free one drops the handle. Nothing is looked at again
    /// until a character has been added since the last pass.
    /// </summary>
    void RestoreSlavers(World world)
    {
        var table = world.Characters;
        if (table.Spawns == _slaversSeenSpawns) return;
        _slaversSeenSpawns = table.Spawns;
        var next = table.Next;
        Dictionary<(int C, int I), int>? bySave = null;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!next[i].Alive || table.Cold(i) is not { Save: { Slaver.IsNull: false } link } cold) continue;
            if (cold.SlaveState == 0)
            {
                link.DropSlaver();
                continue;
            }

            bySave ??= SavedSlots(table);
            if (!bySave.TryGetValue((link.Slaver.C, link.Slaver.I), out int owner) || owner == i) continue;
            link.Slaver = Data.Save.Hand.Null;
            Adopt(cold, table.Cold(owner)!);
        }
    }

    /// <summary>
    /// The slot of every character in the world loaded from the save, by its handle's container and slot.
    /// </summary>
    static Dictionary<(int C, int I), int> SavedSlots(CharacterTable table)
    {
        var slots = new Dictionary<(int C, int I), int>();
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Next[s].Alive && table.Cold(s)?.Save is { } saved)
                slots[(saved.Source.Handle.C, saved.Source.Handle.I)] = s;
        }

        return slots;
    }

    /// <summary>
    /// Makes a saved slave's owner its owner: the owner's faction and squad where the slave has none.
    /// </summary>
    static void Adopt(CharacterCold slave, CharacterCold owner)
    {
        if (slave.SlaveOwner < 0) slave.SlaveOwner = owner.Faction;
        if (slave.SlaveMaster < 0 && owner.SquadId >= 0) slave.SlaveMaster = owner.SquadId;
    }
}