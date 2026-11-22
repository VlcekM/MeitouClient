using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Modding;

/// <summary>
/// What an <see cref="EffectAnchor"/> follows.
/// </summary>
public enum EffectAnchorKind
{
    /// <summary>
    /// A fixed world point.
    /// </summary>
    Point,
    /// <summary>
    /// A bone of a character, as drawn.
    /// </summary>
    Bone,
    /// <summary>
    /// The melee weapon a character holds drawn, as drawn.
    /// </summary>
    Weapon,
    /// <summary>
    /// A player-built or nest building (<c>WorldSnapshot.Buildings</c>, <c>NestBuildings</c>).
    /// </summary>
    Building,
}

/// <summary>
/// Where a code mod's display-only effect plays and how it is turned and scaled (docs/modding.md "Visual effects"): the particle system's origin
/// at the anchor's point, its axes turned to the anchor's frame and its emitters' places scaled by <see cref="Stretch"/>. Made with
/// <see cref="At"/>, <see cref="Bone"/>, <see cref="Weapon"/> or <see cref="Building"/>; two anchors made alike are equal (the key of
/// <see cref="ModContext.KeepEffect"/>).
/// </summary>
public readonly record struct EffectAnchor
{
    /// <summary>
    /// The chest bone, where a character's effect plays when the mod names no bone.
    /// </summary>
    public const string ChestBone = "Bip01 Spine2";

    /// <summary>
    /// What it follows.
    /// </summary>
    public EffectAnchorKind Kind { get; init; }

    /// <summary>
    /// A point anchor's world point; a bone's or building's offset in its frame.
    /// </summary>
    public Vector3 Offset { get; init; }

    /// <summary>
    /// The turn in the anchor's frame (null: none).
    /// </summary>
    public Quaternion? Turn { get; init; }

    /// <summary>
    /// The character of a bone or weapon anchor.
    /// </summary>
    public CharacterId Character { get; init; }

    /// <summary>
    /// A bone anchor's bone, a building anchor's id.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// The scale of the system's emitters (null: the anchor's own, 1 but for a weapon, whose blade sets it).
    /// </summary>
    public float? Stretch { get; init; }

    /// <summary>
    /// A fixed world point, turned by <paramref name="turn"/>.
    /// </summary>
    public static EffectAnchor At(Vector3 point, Quaternion? turn = null) =>
        new() { Kind = EffectAnchorKind.Point, Offset = point, Turn = turn };

    /// <summary>
    /// The character's <paramref name="bone"/> (e.g. <c>Bip01 Head</c>), position and turn, moved by <paramref name="offset"/> and turned by
    /// <paramref name="turn"/> in the bone's frame.
    /// </summary>
    public static EffectAnchor Bone(CharacterId on, string bone, Vector3 offset = default, Quaternion? turn = null) =>
        new() { Kind = EffectAnchorKind.Bone, Character = on, Name = bone, Offset = offset, Turn = turn };

    /// <summary>
    /// The melee weapon the character holds drawn: the origin at the weapon's hilt end, +Y along the blade, scaled so 8.5 units span the weapon.
    /// </summary>
    public static EffectAnchor Weapon(CharacterId on) => new() { Kind = EffectAnchorKind.Weapon, Character = on };

    /// <summary>
    /// A player-built or nest building by its instance id (<c>BuildingSnapshot.Id</c>), its origin and turn, moved by <paramref name="offset"/>
    /// and turned by <paramref name="turn"/> in its frame.
    /// </summary>
    public static EffectAnchor Building(string id, Vector3 offset = default, Quaternion? turn = null) =>
        new() { Kind = EffectAnchorKind.Building, Name = id, Offset = offset, Turn = turn };

    /// <summary>
    /// This anchor with its emitters scaled by <paramref name="stretch"/> (in (0, 100]).
    /// </summary>
    public EffectAnchor Scaled(float stretch) => this with { Stretch = stretch };
}
