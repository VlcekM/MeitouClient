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
    /// <summary>
    /// A bolt in flight or stuck in the ground (<c>WorldSnapshot.Projectiles</c>), as drawn.
    /// </summary>
    Projectile,
}

/// <summary>
/// Where a code mod's display-only effect plays and how it is turned and scaled (docs/modding.md "Visual effects"): the particle system's origin
/// at the anchor's point, its axes turned to the anchor's frame and its emitters' places scaled by <see cref="Stretch"/>. Made with
/// <see cref="At"/>, <see cref="Bone"/>, <see cref="Weapon"/>, <see cref="Building"/> or <see cref="Projectile"/>; two anchors made alike but for
/// their scale (<see cref="Scaled"/>, <see cref="Sized"/>, <see cref="Pulsing"/>) are the same key of <see cref="ModContext.KeepEffect"/> and
/// <see cref="ModContext.KeepTrail"/>, so a renewal changes the scale in place.
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
    /// A point anchor's world point; a bone's, weapon's, building's or bolt's offset in its frame.
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
    /// A projectile anchor's bolt (<c>ProjectileSnapshot.Id</c>).
    /// </summary>
    public int ProjectileId { get; init; }

    /// <summary>
    /// A factor on the stretch, the anchor's own or <see cref="Stretch"/> (null: 1).
    /// </summary>
    public float? Size { get; init; }

    /// <summary>
    /// How far the size swings either way while pulsing, a share of it in [0, 1) (0: no pulse).
    /// </summary>
    public float PulseAmount { get; init; }

    /// <summary>
    /// The seconds of one pulse (with <see cref="PulseAmount"/>).
    /// </summary>
    public float PulseSeconds { get; init; }

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
    /// A point on the melee weapon the character holds drawn: <see cref="Weapon(CharacterId)"/> moved by <paramref name="offset"/> and turned by
    /// <paramref name="turn"/> in the weapon's frame, the offset in its units (8.5 along +Y is the tip, whatever the weapon's length).
    /// </summary>
    public static EffectAnchor Weapon(CharacterId on, Vector3 offset, Quaternion? turn = null) =>
        new() { Kind = EffectAnchorKind.Weapon, Character = on, Offset = offset, Turn = turn };

    /// <summary>
    /// A player-built or nest building by its instance id (<c>BuildingSnapshot.Id</c>), its origin and turn, moved by <paramref name="offset"/>
    /// and turned by <paramref name="turn"/> in its frame.
    /// </summary>
    public static EffectAnchor Building(string id, Vector3 offset = default, Quaternion? turn = null) =>
        new() { Kind = EffectAnchorKind.Building, Name = id, Offset = offset, Turn = turn };

    /// <summary>
    /// A bolt by its id (<c>ProjectileSnapshot.Id</c>) as drawn, in flight or stuck in the ground: the origin at its nose, +Y along its flight,
    /// moved by <paramref name="offset"/> and turned by <paramref name="turn"/> in that frame.
    /// </summary>
    public static EffectAnchor Projectile(int id, Vector3 offset = default, Quaternion? turn = null) =>
        new() { Kind = EffectAnchorKind.Projectile, ProjectileId = id, Offset = offset, Turn = turn };

    /// <summary>
    /// This anchor with its emitters scaled by <paramref name="stretch"/> (in (0, 100]) in place of its own stretch.
    /// </summary>
    public EffectAnchor Scaled(float stretch) => this with { Stretch = stretch };

    /// <summary>
    /// This anchor with its stretch (its own, a weapon's blade length, or <see cref="Scaled"/>'s) times <paramref name="factor"/> (in (0, 100]).
    /// </summary>
    public EffectAnchor Sized(float factor) => this with { Size = factor };

    /// <summary>
    /// This anchor with its size swinging by <paramref name="amount"/> (a share in [0, 1)) either way, once every <paramref name="seconds"/> (in
    /// [0.05, 60]), from the start of the effect or trail.
    /// </summary>
    public EffectAnchor Pulsing(float amount, float seconds) => this with { PulseAmount = amount, PulseSeconds = seconds };

    /// <summary>
    /// The anchor without its scale: what a kept effect or trail is keyed by.
    /// </summary>
    internal EffectAnchor Key => this with { Stretch = null, Size = null, PulseAmount = 0, PulseSeconds = 0 };

    /// <summary>
    /// The size factor at <paramref name="seconds"/> since the start: <see cref="Size"/> times the pulse.
    /// </summary>
    internal float SizeAt(double seconds)
    {
        float size = Size ?? 1;
        if (PulseAmount <= 0 || PulseSeconds <= 0) return size;

        return size * (1 + PulseAmount * (float)Math.Sin(2 * Math.PI * seconds / PulseSeconds));
    }
}
