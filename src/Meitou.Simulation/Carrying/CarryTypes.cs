using Meitou.Simulation.Aggro;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Carrying;

/// <summary>
/// Orders one of the carriers (the selection when the list is empty) to pick <see cref="Target"/> up: the nearest one that can carry walks to it and
/// lifts it onto its left shoulder (the original's <c>LIFT_PERSON_PLAYER_ORDER</c>, context menu verb "Pick Up"). The target must be knocked out or
/// dead, of a <c>carriable</c> race and not carried already.
/// </summary>
public sealed record PickUpOrder(IReadOnlyList<CharacterId> Carriers, CharacterId Target) : OrderCommand
{
    /// <summary>
    /// The verb was Kidnap (246, <c>Task_Kidnapping</c>): a sleeping target may be taken too, and the lift rolls <see
    /// cref="CarrySystem.KidnapChance"/> first.
    /// </summary>
    public bool Kidnap
    {
        get;
        init;
    }
}

/// <summary>
/// Orders the carriers (the selection when the list is empty) that carry someone to set it down where they stand (the original's
/// <c>PUT_DOWN_OBJECT</c>, "Put Down").
/// </summary>
public sealed record PutDownOrder(IReadOnlyList<CharacterId> Carriers) : OrderCommand;

/// <summary>
/// Orders one of the looters (the selection when the list is empty) to loot <see cref="Target"/>, a knocked-out or dead character: the nearest walks
/// to it and takes everything from its inventory and backpack that fits its own (a take-all; worn gear stays, the loot window takes it). Looting
/// someone of another faction in sight of its members is a theft.
/// </summary>
public sealed record LootOrder(IReadOnlyList<CharacterId> Looters, CharacterId Target) : OrderCommand;

/// <summary>
/// As <see cref="LootOrder"/>, but in reach the looter opens <see cref="Target"/>'s inventory in a window (<see cref="PlayerState.OpenLoot"/>)
/// instead of taking all: items are then taken one at a time (<see cref="TakeLootItem"/>). Closed with <see cref="Items.CloseContainer"/>, or when
/// the looter walks off, goes down, or the body comes round or is lifted.
/// </summary>
public sealed record OpenLootOrder(IReadOnlyList<CharacterId> Looters, CharacterId Target) : OrderCommand;

/// <summary>
/// A right click on an item of the open loot (<see cref="Item"/>: worn gear and the bag's contents included, <see cref="Items.Inventory.All"/>
/// order): <see cref="Count"/> units (0: until one fails) move into the looter's (<see cref="Items.InventoryCommands.MoveInto"/>: its open bag first
/// with <see cref="BagFirst"/>, then its slots, then its grid). A theft as a loot is (<see cref="Crime.CrimeSystem.IsLootTheft"/>); a locked-on item
/// stays. Dragging goes through the held item (<see cref="Items.LiftItem"/>).
/// </summary>
public sealed record TakeLootItem(Items.InventoryItemToken Item) : SimCommand
{
    public int Count
    {
        get;
        init;
    }
    public bool BagFirst
    {
        get;
        init;
    }
}

/// <summary>
/// A right click on the looter's (<see cref="Character"/>, the one who opened it) <see cref="Item"/>: units into the open loot, as <see
/// cref="TakeLootItem"/> the other way.
/// </summary>
public sealed record PutLootItem(CharacterId Character, Items.InventoryItemToken Item) : SimCommand
{
    public int Count
    {
        get;
        init;
    }
    public bool BagFirst
    {
        get;
        init;
    }
}

/// <summary>
/// Orders one of the attackers (the selection when the list is empty) to knock <see cref="Target"/> out from behind (the original's order 228, verb
/// "Stealth KO", <c>Task_StealthKO</c>): the nearest sneaking one that can act creeps up behind it and swings; the roll (<see
/// cref="CarrySystem.StealthKoChance"/>) decides between a knock-out and a fight.
/// </summary>
public sealed record StealthKoOrder(IReadOnlyList<CharacterId> Attackers, CharacterId Target) : OrderCommand;

/// <summary>
/// A stealth knock-out swing that was rolled: the target was knocked out (<see cref="Success"/>) or turned on the attacker; <see cref="NoXp"/>: a
/// miss that gave no XP.
/// </summary>
public readonly record struct StealthKoEntry(long Tick, CharacterId Attacker, CharacterId Target, float Chance, bool Success, bool NoXp = false);

/// <summary>
/// The Rescue button for the characters (the selection when the list is empty), the original's order 148 <c>FIND_AND_RESCUE_IF_THERES_BEDS</c> (NUM8;
/// docs/game/ai-tasks.md "Perma-jobs"): with <see cref="Job"/> (Shift) it is added to their job lists (with its companion 106), where an idle
/// character picks up the nearest downed member of its faction by itself; else each looks for one once.
/// </summary>
public sealed record RescueJobCommand(IReadOnlyList<CharacterId> Characters, bool Job = true) : OrderCommand;

/// <summary>
/// The numbers of <see cref="CarrySystem"/>: the clip fractions are the original's (<b>Observed</b>), the rest engine choices
/// (docs/game/carry-loot.md, docs/simulation.md "Carrying").
/// </summary>
public sealed class CarrySettings
{
    /// <summary>
    /// The carrier lifts or loots from this far (X/Z units); the walk there ends here (the first aid reach).
    /// </summary>
    public float Reach { get; init; } = 14;

    /// <summary>
    /// Ticks between the NPCs' looks for a downed character, a body to loot or a weapon (the retaliation and first aid rhythm); a player character's
    /// rescue and foraging jobs are the decision step's.
    /// </summary>
    public int AutoEveryTicks { get; init; } = 15;

    /// <summary>
    /// The daylight sight R before the race's <c>vision range mult</c>: how far a rescuer looks for a downed character (the original searches its
    /// sensed-character list, whose size is <b>Unknown</b>) and how far a witness sees a looter. The leader rescue has no range (FUN_140598540,
    /// Verified).
    /// </summary>
    public float Sight { get; init; } = AggroSystem.OutdoorSight;

    /// <summary>
    /// A body that is put down is placed this far beside its carrier (engine choice).
    /// </summary>
    public float PutDownSide { get; init; } = 3;

    /// <summary>
    /// The fraction of the lift clip at which the body is attached (<b>Observed</b>, <c>Task_LiftSomething</c> update: progress above 0.4).
    /// </summary>
    public const float AttachAt = 0.4f;

    /// <summary>
    /// The fraction at which the lift is finished (<b>Observed</b>: 0.95).
    /// </summary>
    public const float DoneAt = 0.95f;

    /// <summary>
    /// Putting down plays the clip backwards and lets go below this fraction (<b>Observed</b>, <c>Task_PutDownObject</c>: 0.2).
    /// </summary>
    public const float ReleaseBelow = 0.2f;
}

/// <summary>
/// The start flags of an NPC's <see cref="CharacterTask.Loot"/> (<c>Task_Loot_AI</c> +0x80..+0x84, written once by the start <c>FUN_140355ad0</c> and
/// only read by the update <c>FUN_14034d930</c>, <b>Verified</b>): fixed when the task begins, not re-evaluated while it runs.
/// </summary>
[Flags]
public enum LootStart : byte
{
    None = 0,
    /// <summary>
    /// +0x80: the looter has no weapon and the victim has one.
    /// </summary>
    WantsWeapon = 1,
    /// <summary>
    /// +0x81: the victim is not on the player's side (or dead); never set for a police looter.
    /// </summary>
    NotPlayerSide = 2,
    /// <summary>
    /// +0x82: a law-enforcement looter confiscates.
    /// </summary>
    Police = 4,
    /// <summary>
    /// +0x83: the looter is a Bandit or malnourished and takes only food; never set for a police looter.
    /// </summary>
    FoodOnly = 8,
    /// <summary>
    /// +0x84: the victim is an animal.
    /// </summary>
    Animal = 16,
    /// <summary>
    /// Meitou: the loot window of this looter is open; the looter stays in the task until it closes (Task_Loot_Order's update, <c>FUN_140354ef0</c>).
    /// </summary>
    WindowHeld = 32,
}

/// <summary>
/// What one loot took: the items moved into the looter's inventory, the ones that did not fit and stay, and whether a member of the victim's faction
/// saw it.
/// </summary>
public readonly record struct LootEntry(long Tick, CharacterId Looter, CharacterId Victim, int Taken, int Left, bool Witnessed);

/// <summary>
/// A carried body's placement (its node: position and rotation) in its carrier's frame: relative to the carrier's position, before its facing. The
/// body keeps its carrier's position and facing; the host draws it at <see cref="World"/> and the ragdoll's pinned bones follow it there.
/// </summary>
public readonly record struct CarryAttach(System.Numerics.Vector3 Offset, System.Numerics.Quaternion Rotation)
{
    /// <summary>
    /// The body's node in the world for a carrier at <paramref name="position"/> facing <paramref name="yaw"/>.
    /// </summary>
    public (System.Numerics.Vector3 Position, System.Numerics.Quaternion Rotation) World(System.Numerics.Vector3 position, float yaw)
    {
        var turn = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, yaw);
        return (position + System.Numerics.Vector3.Transform(Offset, turn), System.Numerics.Quaternion.Normalize(turn * Rotation));
    }
}

/// <summary>
/// Where a carried body hangs (docs/game/carry-loot.md "Carry and release", <b>Observed</b> <c>CharacterAnimal::vfunc_77</c>, <c>FUN_1405b5980</c>):
/// its node sits on the carrier's <see cref="CarrySystem.AttachBone"/>, moved by an offset in the bone's frame that grows with the body's
/// <c>Height</c>, and turned by the bone's rotation then <see cref="Twist"/>.
/// </summary>
public static class CarryPose
{
    // The offsets at height 0, 0.5 and 1 (units, the bone's frame).
    static readonly System.Numerics.Vector3 Short = new(-1.4f, 0, 1), Middle = new(-1.9f, -1, 2.6f), Tall = new(-3, -0.4f, 5);

    /// <summary>
    /// 90 degrees about x, then about 14 degrees about z (the original's two fixed quaternions).
    /// </summary>
    public static readonly System.Numerics.Quaternion Twist =
        new System.Numerics.Quaternion(0.70710677f, 0, 0, 0.70710677f) * new System.Numerics.Quaternion(0, 0, 0.121869296f, 0.99254614f);

    /// <summary>
    /// Without the carrier's skeleton: on its left shoulder, upright (engine stand-in).
    /// </summary>
    public static readonly CarryAttach Fallback = new(new(2, 14, 0), System.Numerics.Quaternion.Identity);

    /// <summary>
    /// The body's height as 0..1: (<c>Height</c> slider - 80) / 40, 0.5 without a body file (the original's <c>characterHeight_0to1</c>, a 30-bone
    /// skeleton's).
    /// </summary>
    public static float Height01(Data.Characters.CharacterAppearance? look) =>
        look?.Body?.Floats.TryGetValue("Height", out var h) == true ? (h - 80) * 0.025f : 0.5f;

    /// <summary>
    /// The offset from the bone for a body of <paramref name="height01"/>: a lerp short to middle below 0.5, middle to tall above.
    /// </summary>
    public static System.Numerics.Vector3 Offset(float height01)
    {
        float t = height01 * 2;
        return t < 1 ? System.Numerics.Vector3.Lerp(Short, Middle, t) : System.Numerics.Vector3.Lerp(Middle, Tall, t - 1);
    }

    /// <summary>
    /// The placement for the carrier's attach bone at <paramref name="bone"/> / <paramref name="rotation"/> in its frame (model space, lowered by its
    /// ground offset).
    /// </summary>
    public static CarryAttach Attach(System.Numerics.Vector3 bone, System.Numerics.Quaternion rotation, float height01) =>
        new(bone + System.Numerics.Vector3.Transform(Offset(height01), rotation), System.Numerics.Quaternion.Normalize(rotation * Twist));
}