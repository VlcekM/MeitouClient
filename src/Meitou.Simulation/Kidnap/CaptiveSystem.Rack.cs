using Meitou.Data.Fcs;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.Kidnap;

/// <summary>
/// Torture racks: docs/game/locks.md "Torture racks".
/// </summary>
public sealed partial class CaptiveSystem
{
    /// <summary>
    /// The armour timer's rate (the double <c>0x1416b1630</c>, <b>Verified</b>): a body-armour piece comes off once its cut + blunt resistance is
    /// below the seconds on the rack x this.
    /// </summary>
    public const float ArmourStripRate = 0.04f;

    /// <summary>
    /// Every torture rack once a tick (<c>TortureBuilding</c> slot 28 <c>FUN_14029af90</c>, <b>Verified</b>). With nobody in it the armour timer and
    /// the busy flag clear. Its user (the cage's occupant) cries event 11 SCREAMING_TORTURE every frame (the dialogue's own speaking gate decides
    /// when it is heard). One that wears a piece in its <c>armour</c> section loses the first of them once the seconds on the rack x
    /// <see cref="ArmourStripRate"/> pass its cut + blunt resistance (destroyed, the timer restarts), and nothing else is done to it. Otherwise
    /// <see cref="MedicalState.StretchLimbs"/> runs. When the last frame stretched and this one has nothing left to stretch, the rack makes its
    /// output (<c>vfunc_184</c> with the progress it showed); else the machine's output progress shows how far the body is stretched.
    /// </summary>
    void StepRacks(World world)
    {
        foreach (var rack in world.Fixtures.All)
            if (rack.Rack) StepRack(world, rack, world.TickSeconds);
    }

    /// <summary>
    /// One tick of a torture rack: with its occupant on it, the armour comes off by its timer and the machine runs; empty, the rack and
    /// machine reset.
    /// </summary>
    void StepRack(World world, Fixture rack, float dt)
    {
        var table = world.Characters;
        var machine = world.Machines.Find(rack.Id);
        if (!table.TryResolveNext(rack.Occupant, out int c) || table.Cold(c) is not { Medical: not null, Race: not null } cc || cc.InWhat != rack.Id)
        {
            (rack.RackTimer, rack.RackBusy) = (0, false);
            if (machine is not null) machine.Status = 0;
            return;
        }

        world.Defer(new Dialogue.DialogueEvent(rack.Occupant, CharacterId.None, ScreamingTorture) { Tick = world.Tick + 1 });
        bool busy;
        if (StripArmour(world, rack, c, cc, dt))
        {
            busy = true;
            if (machine?.Output is { } held) held.Progress = 0;
            if (machine is not null) machine.Status = 0;
        }
        else
        {
            var events = new List<MedicalEvent>();
            var stretch = cc.Medical!.StretchLimbs(dt, ContextOf(world, c, cc), data.BodyOptions.Dismemberment != Dismemberment.Never, events);
            ApplyBodyEvents(world, rack.Occupant, c, cc.Medical, events);
            busy = stretch.Busy;
            if (rack.RackBusy && !busy) Finish(machine);
            else
            {
                if (machine?.Output is { } held) held.Progress = stretch.Progress;
                if (machine is not null) machine.Status = 1;
            }
        }

        rack.RackBusy = busy;
    }

    /// <summary>
    /// The rack's end (<c>vfunc_184</c>): the output takes one item when its progress has reached 0.999, then restarts; status 0.
    /// </summary>
    static void Finish(Production.Machine? machine)
    {
        if (machine is null) return;
        machine.Status = 0;
        if (machine.Output is not { } output) return;
        if (output.Progress >= 0.999f && !output.Full) output.Count++;
        output.Progress = 0;
    }

    /// <summary>
    /// The armour step: true while the user wears anything in its <c>armour</c> section. The timer grows by the frame; the first piece is destroyed
    /// once cut + blunt resistance is below timer x <see cref="ArmourStripRate"/>.
    /// </summary>
    bool StripArmour(World world, Fixture rack, int c, CharacterCold cc, float dt)
    {
        if (cc.Inventory is not { } inventory
                || inventory.Items.FirstOrDefault(i => i.Type == FcsRecordType.Armour && i.Section == "armour") is not { } piece)
            return false;

        rack.RackTimer += dt;
        if (data.Combat.Armour(piece.Record) is { } armour)
        {
            var p = ArmourPiece.Create(armour, ArmourPiece.QualityOf(armour, piece.Level), ArmourPiece.Graded(armour, piece.Level));
            if (p.CutDefence + p.BluntDefence >= rack.RackTimer * ArmourStripRate) return true;
        }

        inventory.Items.Remove(piece);
        _assembly.RefreshEquipment(cc);
        rack.RackTimer = 0;
        return true;
    }
}