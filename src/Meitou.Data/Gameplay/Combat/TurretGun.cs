using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay.Combat;

/// <summary>
/// A turret part that turns to the target (a BUILDING_PART with <c>rotation function</c> ROTATION_TARGET): <paramref name="Parent"/> is the index of
/// the part it hangs from (-1: the building), <paramref name="Id"/> is the BUILDING_PART record (the renderer finds the part's mesh by it),
/// <paramref name="Yaw"/> says whether it turns about its Y axis (<c>rotation axis</c> Y: the hinge) or pitches about X (the gun),
/// <paramref name="Offset"/> is its place in the parent's frame (<c>offset X/Y/Z</c>, before the building's scale).
/// </summary>
public readonly record struct TurretPart(int Parent, bool Yaw, Vector3 Offset, string Id = "");

/// <summary>
/// A turret building's gun as the original builds it (docs/game/combat.md "Turrets"): the <c>gun turret</c> GUN_DATA (<c>TurretBuilding</c>
/// constructor <c>FUN_14043cdb0</c>), the building's damage (<see cref="Damage"/>), its turning parts and whether it draws power.
/// </summary>
public sealed record TurretGun
{
    /// <summary>
    /// The BUILDING the gun belongs to.
    /// </summary>
    public required string BuildingId { get; init; }

    /// <summary>
    /// The building's <c>gun turret</c> GUN_DATA.
    /// </summary>
    public required GunData Gun { get; init; }

    /// <summary>
    /// The building's "Default damage" (<c>Building</c> slot 120 <c>FUN_140297b30</c>, the turret tooltip <c>FUN_14030d620</c>, <b>Verified</b>
    /// against the loader <c>FUN_14029e460</c>): BUILDING <c>output rate</c> x the functionality's <c>production mult</c>, / <c>max operators</c>
    /// when above 1; the research multiplier of a player building is applied by the caller.
    /// </summary>
    public float Damage { get; init; }

    /// <summary>
    /// The turning parts in the exe's part order (depth first).
    /// </summary>
    public IReadOnlyList<TurretPart> Parts { get; init; } = [];

    /// <summary>
    /// The BUILDING's <c>scale</c> (1 when unset).
    /// </summary>
    public float Scale { get; init; } = 1;

    /// <summary>
    /// A negative <c>power output</c>: the turret draws power and its efficiency is its grant (docs/game/buildings-production.md "Power").
    /// </summary>
    public bool IsConsumer { get; init; }

    /// <summary>
    /// The gun of a BUILDING with a <c>gun turret</c> GUN_DATA; null for a building without one (the original throws "has no gun data").
    /// </summary>
    public static TurretGun? From(GameDatabase db, GameRecord building)
    {
        if (building.GetReferences("gun turret") is not [var link, ..]) return null;
        if (db.Find(link.TargetStringId) is not { Type: FcsRecordType.GunData } gun) return null;

        var parts = new List<TurretPart>();
        TurretRig.Collect(db, building, -1, Vector3.Zero, parts, 0);
        float scale = building.GetFloat("scale", 1);
        return new TurretGun
        {
            BuildingId = building.StringId,
            Gun = GunData.From(gun),
            Damage = Output(db, building),
            Parts = parts,
            Scale = scale > 0 ? scale : 1,
            IsConsumer = building.GetInt("power output") < 0,
        };
    }

    /// <summary>
    /// A building's output value (<c>+0x1a4</c>, loader <c>FUN_14029e460</c>, <b>Verified</b>): <c>output rate</c> x the functionality's
    /// <c>production mult</c>, divided by <c>max operators</c> (the building's, else the functionality's when the building's is negative) when that
    /// is above 1. A turret's damage, a wall's turret bonus in percent.
    /// </summary>
    public static float Output(GameDatabase db, GameRecord building)
    {
        var function = building.GetReferences("functionality") is [var f, ..] ? db.Find(f.TargetStringId) : null;
        float value = building.GetFloat("output rate", 1) * (function?.GetFloat("production mult", 1) ?? 1);
        int operators = building.GetInt("max operators", -1);
        if (operators < 0) operators = function?.GetInt("max operators", 0) ?? 0;

        return operators > 1 ? value / operators : value;
    }
}

/// <summary>
/// The turning of a turret's parts (<c>TurretBuilding::vfunc_28</c> <c>140434b70</c>, <c>RotatingEnt::vfunc_2</c>, the error helpers
/// <c>FUN_1409b2fc0</c> / <c>FUN_1409b3090</c>; docs/audio.md "Turrets", docs/game/combat.md "Turrets"). Shared by the simulation's turrets and the
/// turret sounds.
/// </summary>
public static class TurretRig
{
    /// <summary>
    /// Collects the parts that turn to the target (<c>rotation function</c> 5), depth first like the exe's part list: each hangs from the nearest
    /// such ancestor, with the offsets of the parts between carried along. A part whose <c>rotation axis</c> is Y yaws; any other pitches.
    /// </summary>
    public static void Collect(GameDatabase db, GameRecord holder, int parent, Vector3 carried, List<TurretPart> parts, int depth)
    {
        if (depth > 8) return;

        foreach (var reference in holder.GetReferences("parts"))
        {
            if (db.Find(reference.TargetStringId) is not { Type: FcsRecordType.BuildingPart } part) continue;

            var offset = carried + (part.GetBool("is for position marker") ? Vector3.Zero : OffsetOf(part));
            if (part.GetInt("rotation function") == 5)
            {
                parts.Add(new TurretPart(parent, part.GetInt("rotation axis", 2) == 1, offset, part.StringId));
                Collect(db, part, parts.Count - 1, Vector3.Zero, parts, depth + 1);
            }
            else Collect(db, part, parent, offset, parts, depth + 1);
        }
    }

    /// <summary>
    /// A BUILDING_PART's <c>offset X</c>, <c>offset Y</c>, <c>offset Z</c> (0 for a missing one).
    /// </summary>
    static Vector3 OffsetOf(GameRecord part) =>
        new(part.GetFloat("offset X"), part.GetFloat("offset Y"), part.GetFloat("offset Z"));

    /// <summary>
    /// Turns every part toward <paramref name="aim"/> by at most <paramref name="step"/> radians: each, in order, takes the aim point into its own
    /// frame (its parent's orientation, then its own turn so far); a yaw part's error is <c>atan2(x, z)</c>, a pitch part's <c>atan2(-y, √(x² +
    /// z²))</c>. Returns the largest error in degrees before turning (the exe's <c>+0x448</c>), -99 without parts.
    /// </summary>
    public static float Turn(IReadOnlyList<TurretPart> parts, float[] angles, Vector3 position, float yaw, float scale, Vector3 aim, float step)
    {
        float worst = -99;
        for (int k = 0; k < parts.Count; k++)
        {
            var (at, orientation) = Derived(parts, angles, position, yaw, scale, k);
            var local = Vector3.Transform(aim - at, Quaternion.Inverse(orientation));
            float error = parts[k].Yaw ? MathF.Atan2(local.X, local.Z) : MathF.Atan2(-local.Y, MathF.Sqrt(local.X * local.X + local.Z * local.Z));
            worst = MathF.Max(worst, MathF.Abs(error) * (180 / MathF.PI));
            angles[k] += Math.Clamp(error, -step, step);
        }

        return worst;
    }

    /// <summary>
    /// The world place and orientation of part <paramref name="k"/>'s node: the building's, then each part down the chain (its offset in the parent's
    /// frame, scaled, and its own turn about its axis).
    /// </summary>
    public static (Vector3 Position, Quaternion Orientation) Derived(
        IReadOnlyList<TurretPart> parts, float[] angles, Vector3 position, float yaw, float scale, int k)
    {
        var part = parts[k];
        var (at, orientation) = part.Parent < 0
            ? (position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw))
            : Derived(parts, angles, position, yaw, scale, part.Parent);
        var turn = Quaternion.CreateFromAxisAngle(part.Yaw ? Vector3.UnitY : Vector3.UnitX, angles[k]);
        return (at + Vector3.Transform(part.Offset * scale, orientation), orientation * turn);
    }

    /// <summary>
    /// The muzzle and the barrel's direction (gun slots 9 and 12, <c>FUN_140434fc0</c>, <c>FUN_140434f40</c>): the barrel node's place + its
    /// orientation x (0, <c>barrel pos Y</c>, <c>barrel pos Z</c>) and its local +Z. The barrel node is the last turning part (<b>engine choice</b>:
    /// the exe takes the node of its gun entity, <c>+0x4a0</c>); without parts the building's own.
    /// </summary>
    public static (Vector3 Muzzle, Vector3 Forward) Barrel(TurretGun turret, float[] angles, Vector3 position, float yaw)
    {
        var (at, orientation) = turret.Parts.Count > 0
            ? Derived(turret.Parts, angles, position, yaw, turret.Scale, turret.Parts.Count - 1)
            : (position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));
        var muzzle = at + Vector3.Transform(new Vector3(0, turret.Gun.BarrelY, turret.Gun.BarrelZ) * turret.Scale, orientation);
        return (muzzle, Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, orientation)));
    }
}

/// <summary>
/// The turret guns of a database by BUILDING id, built once each (thread-safe).
/// </summary>
public sealed class TurretCatalog(GameDatabase db)
{
    /// <summary>
    /// Built guns by BUILDING id; null for a building that is no turret. Its own lock.
    /// </summary>
    readonly Dictionary<string, TurretGun?> _guns = new(StringComparer.Ordinal);

    /// <summary>
    /// Output values by BUILDING id. Its own lock.
    /// </summary>
    readonly Dictionary<string, float> _outputs = new(StringComparer.Ordinal);

    /// <summary>
    /// The gun of BUILDING <paramref name="buildingId"/>; null when it is no turret.
    /// </summary>
    public TurretGun? Get(string buildingId)
    {
        lock (_guns)
        {
            if (!_guns.TryGetValue(buildingId, out var gun))
                _guns[buildingId] = gun = db.Find(buildingId) is { Type: FcsRecordType.Building } r ? TurretGun.From(db, r) : null;

            return gun;
        }
    }

    /// <summary>
    /// A building's output value (<see cref="TurretGun.Output"/>): the turret bonus in percent of the wall a turret is mounted on; 0 for an
    /// unknown record.
    /// </summary>
    public float OutputOf(string buildingId)
    {
        lock (_outputs)
        {
            if (!_outputs.TryGetValue(buildingId, out float value))
                _outputs[buildingId] = value = db.Find(buildingId) is { Type: FcsRecordType.Building } r ? TurretGun.Output(db, r) : 0;

            return value;
        }
    }
}
