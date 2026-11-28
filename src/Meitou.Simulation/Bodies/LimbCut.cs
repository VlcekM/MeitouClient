using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Dialogue;
using Meitou.Simulation.Dialogue;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// What a part cut off does outside the medical state, the same for every way one comes off: a blow or bolt, an execution, the rack, a bite and a
/// worsening wound. The original's sever (<c>FUN_14064edc0</c>, <b>Verified</b> by decompile) does it all itself, so each of its callers (the blow
/// <c>FUN_14064f300</c>, the wound tick <c>FUN_14064f5c0</c>, the rack <c>FUN_14029af90</c>, the bite <c>FUN_140650370</c>) gets it.
/// </summary>
internal static class LimbCut
{
    /// <summary>
    /// Applies one <see cref="MedicalEventKind.PartSevered"/> event of the character in <paramref name="slot"/>: the toughness XP
    /// (<see cref="XpService.ToughnessFromLimbLoss"/>, for a character with stats; none without <paramref name="xp"/>), the look loses the limb
    /// (its item drops when <paramref name="limbDrops"/>), and the lost-limb line (<see cref="Cry"/>). Serial phases only.
    /// </summary>
    public static void Apply(World world, int slot, CharacterCold cold, MedicalState medical, in MedicalEvent cut, XpService? xp, bool limbDrops)
    {
        var part = medical.Parts[cut.Part].Template;
        if (xp is not null && cold.Stats is { } stats && cold.Race is { } race) xp.ToughnessFromLimbLoss(stats, race);

        world.Characters.LimbsChanged.Enqueue((slot, part.LimbSlot, limbDrops));
        if (cut.Conscious) Cry(world, slot, part);
    }

    /// <summary>
    /// Event 70 LOST_LEG for a leg, 71 LOST_ARM for an arm, none for a part of a type outside the four (<see cref="DialogueEvents.LostLimb"/>). The
    /// original says it at the cut through the cut-in (<c>FUN_140685730</c>: a conversation of another event the character is in ends when it has
    /// a line for this one), before a leg's collapse knocks a standing character out. Here it is said on the next tick, as a cut-in that takes the
    /// character as conscious, as it was at the cut (<see cref="DialogueEvent.AwakeAtSource"/>).
    /// </summary>
    static void Cry(World world, int slot, BodyPartTemplate part)
    {
        if (DialogueEvents.LostLimb(part) is not { } cry) return;

        var who = world.Characters.IdOf(slot);
        world.Defer(new DialogueEvent(who, CharacterId.None, cry) { Tick = world.Tick + 1, CutIn = true, AwakeAtSource = true });
    }
}
