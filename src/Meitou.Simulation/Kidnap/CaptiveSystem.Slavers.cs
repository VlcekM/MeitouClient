using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Factions;

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
    /// The <c>slaves</c> list of each squad template looked at for a lost owner, by its string id.
    /// </summary>
    readonly Dictionary<string, IReadOnlyList<RecordLink>> _slavesOf = [];

    /// <summary>
    /// A loaded slave whose save names its owner character (<see cref="Saving.SavedCharacterLink.Slaver"/>) takes it once that character is in the
    /// world: the owner's faction when it has none (Meitou's own key wins), and the owner's squad as the squad it obeys
    /// (<see cref="CharacterCold.SlaveMaster"/>). One waits while its owner is not loaded; a free one drops the handle. A slave whose only link is
    /// its Obedient Slave job's saved owner (<see cref="ObedientSlave.PendingOwnerJob"/>: the original's job copies the owner handle, so a save
    /// Meitou wrote names the master there) takes that character the same way, so goal 41's finder and the slave's senses read the master as the
    /// original's owner handle gives it. Nothing is looked at again until a character has been added since the last pass.
    /// </summary>
    void RestoreSlavers(World world)
    {
        var table = world.Characters;
        if (table.Spawns == _slaversSeenSpawns) return;
        _slaversSeenSpawns = table.Spawns;
        Dictionary<(int C, int I), int>? bySave = null;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { } cold) continue;

            if (cold.Save is { Slaver.IsNull: false } link) RestoreSlaver(table, i, cold, link, ref bySave);
            if (ObedientSlave.PendingOwnerJob(cold) is >= 0 and var k) RestoreJobOwner(table, i, cold, k, ref bySave);
            if (cold.Save is { SlaverLost: true } lost) RestoreLostSlaver(world, i, cold, lost);
        }
    }

    /// <summary>
    /// The save's <c>slaver</c> handle: a free slave drops it; once the owner is in the world it is adopted and the handle is spent.
    /// </summary>
    static void RestoreSlaver(CharacterTable table, int i, CharacterCold cold, Saving.SavedCharacterLink link,
        ref Dictionary<(int C, int I), int>? bySave)
    {
        if (cold.SlaveState == 0)
        {
            link.DropSlaver();
            return;
        }

        bySave ??= SavedSlots(table);
        if (!bySave.TryGetValue((link.Slaver.C, link.Slaver.I), out int owner) || owner == i) return;

        link.Slaver = Data.Save.Hand.Null;
        Adopt(cold, table.Cold(owner)!);
    }

    /// <summary>
    /// The Obedient Slave job's saved owner handle: once that character is in the world the job names it and the slave adopts it.
    /// </summary>
    static void RestoreJobOwner(CharacterTable table, int i, CharacterCold cold, int k, ref Dictionary<(int C, int I), int>? bySave)
    {
        var job = cold.Jobs[k];
        bySave ??= SavedSlots(table);
        if (!bySave.TryGetValue((job.Saved.C, job.Saved.I), out int owner) || owner == i) return;

        cold.Jobs[k] = job with { Character = table.IdOf(owner) };
        Adopt(cold, table.Cold(owner)!);
    }

    /// <summary>
    /// A slave whose Meitou save lost its owner (<see cref="Saving.SavedCharacterLink.SlaverLost"/>) takes as its master the nearest squad in the
    /// world of its owner faction whose template lists the slave's squad template under <c>slaves</c>: the squad the factory made it for
    /// (<see cref="Population.PopulationSystem"/>'s slaves). Engine choice, for saves written before the owner was; one no longer a slave stops looking.
    /// </summary>
    void RestoreLostSlaver(World world, int i, CharacterCold cold, Saving.SavedCharacterLink link)
    {
        if (cold.SlaveState != 1 || cold.SlaveMaster >= 0)
        {
            link.SlaverLost = false;
            return;
        }

        if (world.Squads.Find(cold.SquadId) is not { } own || MasterFor(world, i, cold.SlaveOwner, own.TemplateId) is not { } master) return;

        cold.SlaveMaster = master.Id;
        link.SlaverLost = false;
    }

    /// <summary>
    /// The squad of <paramref name="faction"/> with a living leader nearest to slot <paramref name="i"/> whose template's <c>slaves</c> name
    /// <paramref name="slaveTemplate"/>; null when there is none.
    /// </summary>
    Squad? MasterFor(World world, int i, int faction, string slaveTemplate)
    {
        var table = world.Characters;
        var at = table.Next[i].Position;
        Squad? best = null;
        float bestDistance = float.MaxValue;
        foreach (var squad in world.Squads.All)
        {
            if (squad.Faction != faction || !table.TryResolveNext(squad.Leader, out int leader)) continue;
            if (!SlavesOf(squad.TemplateId).Any(l => l.Id == slaveTemplate)) continue;

            float distance = Vector3.DistanceSquared(at, table.Next[leader].Position);
            if (distance >= bestDistance) continue;
            best = squad;
            bestDistance = distance;
        }

        return best;
    }

    /// <summary>
    /// A squad template's <c>slaves</c> list (empty for a record that is not a squad template).
    /// </summary>
    IReadOnlyList<RecordLink> SlavesOf(string templateId)
    {
        if (_slavesOf.TryGetValue(templateId, out var known)) return known;

        var slaves = data.Db.Find(templateId) is { } r && SquadFactory.IsTemplate(r) ? SquadTemplate.From(r).Slaves : [];
        _slavesOf[templateId] = slaves;
        return slaves;
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