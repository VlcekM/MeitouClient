using Meitou.Simulation.Player;

namespace Meitou.Simulation.FirstAid;

/// <summary>
/// Orders the medics (the selection when the list is empty) to treat <see cref="Patient"/> with a first aid kit: walk to it, then bandage it until
/// nothing is left to bandage or the kit is used up. A patient among the medics treats itself. The player's first aid order (the original's context
/// menu verb "First Aid", <c>FIRST_AID_ORDER</c>); ignored for a medic that is knocked out or fighting.
/// </summary>
public sealed record TreatOrder(IReadOnlyList<CharacterId> Medics, CharacterId Patient) : OrderCommand;

/// <summary>
/// Orders the medics (the selection when the list is empty) to splint <see cref="Patient"/> with a splint kit (the original's verb "Splint",
/// <c>SPLINT_ORDER</c>): walk to it, then splint part after part until none is left or the kit is used up. A patient among the medics splints itself;
/// a medic without a splint kit in its own inventory or backpack refuses.
/// </summary>
public sealed record SplintOrder(IReadOnlyList<CharacterId> Medics, CharacterId Patient) : OrderCommand;

/// <summary>
/// Orders the characters (the selection when the list is empty) to sit down and rest. Any other order ends it.
/// </summary>
public sealed record RestOrder(IReadOnlyList<CharacterId> Characters) : OrderCommand;

/// <summary>
/// The Medic button for the characters (the selection when the list is empty), the original's order 58 <c>JOB_MEDIC</c>, plus 57
/// <c>JOB_REPAIR_ROBOT</c> when the active squad has a robot (docs/game/ui-input.md, "Perma-jobs" in ai-tasks.md): with <see cref="Job"/> (Shift)
/// they are added to their job lists, where an idle character with a kit treats hurt squad mates by itself; else each looks for a patient once.
/// </summary>
public sealed record MedicJobCommand(IReadOnlyList<CharacterId> Characters, bool Job = true) : OrderCommand;

/// <summary>
/// The numbers of <see cref="FirstAidSystem"/>; all engine choices (the original's first aid task is only partly decoded,
/// docs/game/character-stats.md "First aid").
/// </summary>
public sealed class FirstAidSettings
{
    /// <summary>
    /// The medic treats from this far (X/Z units) and a path to the patient ends here; above the movement system's arrival radius (10), so a medic
    /// that has walked up is in reach.
    /// </summary>
    public float Reach { get; init; } = 14;
    /// <summary>
    /// The most the medic and the treatment spot (the patient, or a caged one's cage use node) may differ in height and still be in reach: the same
    /// floor, as <see cref="Dialogue.DialogueSystem.TalkHeight"/> for talking. Without it a medic under an upstairs patient treated through the floor
    /// (<b>Observed</b>: the original's medics climb to a caged patient's floor, docs/game/character-stats.md "First aid").
    /// </summary>
    public float ReachHeight { get; init; } = 35;
    /// <summary>
    /// A moving medic or patient (units per second) is not still: the original needs the patient to stand still (<c>targetStandingStill</c>), a
    /// knocked-out one always does.
    /// </summary>
    public float StillSpeed { get; init; } = 1;
    /// <summary>
    /// A kit in a squad mate's inventory is used when this close (the same reach as sharing food).
    /// </summary>
    public float KitReach { get; init; } = 60;
    /// <summary>
    /// Ticks between looks for a patient by NPCs that treat by themselves (the retaliation system's rhythm); a player character's medic jobs are the
    /// decision step's.
    /// </summary>
    public int AutoEveryTicks { get; init; } = 15;
    /// <summary>
    /// Characters that treat by themselves look for a patient this far (X/Z units) from them, in their own squad.
    /// </summary>
    public float AutoReach { get; init; } = 250;
    /// <summary>
    /// NPCs with a kit treat hurt squad mates without an order (a stand-in for the original's medic job and AI goals until stage 9). The player's
    /// characters do only with the medic job on.
    /// </summary>
    public bool AutoNpcs { get; init; } = true;
}