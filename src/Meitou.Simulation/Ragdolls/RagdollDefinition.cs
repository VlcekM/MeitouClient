using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Data.Physics;

namespace Meitou.Simulation.Ragdolls;

/// <summary>A rigid transform: rotate, then move. <see cref="Then"/> composes as "this in the frame of <paramref name="outer"/>".</summary>
public readonly record struct RigidTransform(Vector3 Position, Quaternion Rotation)
{
    public static readonly RigidTransform Identity = new(Vector3.Zero, Quaternion.Identity);

    /// <summary>This transform expressed in <paramref name="outer"/>'s parent frame (outer ∘ this).</summary>
    public RigidTransform Then(RigidTransform outer) =>
        new(Vector3.Transform(Position, outer.Rotation) + outer.Position, Quaternion.Normalize(outer.Rotation * Rotation));

    public RigidTransform Inverse()
    {
        var inverse = Quaternion.Inverse(Rotation);
        return new(Vector3.Transform(-Position, inverse), inverse);
    }

    public Vector3 Apply(Vector3 point) => Vector3.Transform(point, Rotation) + Position;
}

/// <summary>
/// One body of a ragdoll: the bone it moves, its shape (sizes already scaled), and where the bone sits in the body's frame. The body's frame is its shape's centre and
/// axes (a capsule along local Y, as in the file and in Bepu).
/// </summary>
public sealed class RagdollBody
{
    public required int Bone { get; init; }
    public required string Name { get; init; }
    public required PhysicsShapeType Type { get; init; }
    /// <summary>Box: full extents. Sphere: X is the radius. Capsule: X the radius, Y the length between the cap centres.</summary>
    public Vector3 Size { get; init; }
    /// <summary>Convex hull points in the body's frame (<see cref="PhysicsShapeType.Mesh"/>).</summary>
    public Vector3[]? Hull { get; init; }
    public float Mass { get; init; } = 1;
    /// <summary>The body in model space in the binding pose (scaled).</summary>
    public RigidTransform Bind { get; init; }
    /// <summary>
    /// The bone in the body's frame, bone = <c>BoneInBody.Then(body)</c>: at the actor's origin (the file's actor position) with the skeleton's binding rotation, as the game
    /// pins each actor to its bone (docs/game/ragdolls.md "Bodies: kinematic or simulated"). The actor is not always at the skeleton's bone: whale2.phs is about 1.3 x the
    /// Big-Bones skeleton, its Bip01 actor 24 from the bone.
    /// </summary>
    public RigidTransform BoneInBody { get; init; }
    /// <summary>The actor's origin in model space in the binding pose (scaled).</summary>
    public Vector3 Actor { get; init; }
}

/// <summary>
/// A joint between two bodies (indices into <see cref="RagdollDefinition.Bodies"/>), with its anchor and twist basis in each body's frame. The twist basis has the
/// joint's axis as Z and its normal as X, the same world basis seen from both bodies in the binding pose, so an angle of 0 is the binding pose (as PhysX makes it
/// when the joint is built from the file). Angles in radians.
/// </summary>
public sealed class RagdollJoint
{
    public required PhysicsJointType Type { get; init; }
    public required int A { get; init; }
    public required int B { get; init; }
    public Vector3 OffsetA { get; init; }
    public Vector3 OffsetB { get; init; }
    public Quaternion BasisA { get; init; }
    public Quaternion BasisB { get; init; }
    public bool Limits { get; init; }
    public float Low { get; init; }
    public float High { get; init; }
    public float Swing { get; init; }
    /// <summary>A revolute joint's spring (the file's spring 1, flag set by the game's joint builder <c>@ 1401a81b0</c>): stiffness (torque per radian), damper (torque per rad/s) and the rest angle in radians; stiffness 0 when the joint has none.</summary>
    public float SpringStiffness { get; init; }
    public float SpringDamper { get; init; }
    public float SpringTarget { get; init; }
    /// <summary>The axis in A's and B's frames (the hinge or twist axis).</summary>
    public Vector3 AxisA => Vector3.Transform(Vector3.UnitZ, BasisA);
    public Vector3 AxisB => Vector3.Transform(Vector3.UnitZ, BasisB);
}

/// <summary>
/// A ragdoll file (RACE <c>male ragdoll</c> / <c>female ragdoll</c>, docs/formats/phs.md) bound to a skeleton at a scale: one body per actor whose name is a bone
/// of the skeleton (the game's map from actor name to bone), the joints between them. Immutable; shared by every character with the same file, skeleton and scale.
/// </summary>
public sealed class RagdollDefinition
{
    public required RagdollBody[] Bodies { get; init; }
    public required RagdollJoint[] Joints { get; init; }
    /// <summary>By bone handle: the body moving it, -1 none.</summary>
    public required int[] BodyOfBone { get; init; }
    /// <summary>The body the character's position follows while the whole body is a ragdoll: <c>Bip01</c>'s, else the first (docs/game/ragdolls.md "Per frame").</summary>
    public int Root { get; init; }

    /// <summary>Where the character stands while its whole body is a ragdoll in <paramref name="pose"/>: the root actor's origin, which is its bone's (<see cref="RagdollBody.BoneInBody"/>).</summary>
    public Vector3 RootAt(ReadOnlySpan<RigidTransform> pose) => pose[Root].Apply(Bodies[Root].BoneInBody.Position);
    /// <summary>By body: the bodies joined to it (bit per body index, the first 64), which it does not collide with (no file joint enables collision).</summary>
    public required ulong[] Joined { get; init; }

    /// <summary>
    /// Binds <paramref name="file"/> to <paramref name="rig"/> at <paramref name="scale"/> (the race's or the animal's size; positions and sizes are multiplied by it as the game
    /// does). <paramref name="hull"/> reads a cooked convex mesh file (<see cref="PhysicsShape.CookedFile"/>) as points; null when it cannot. Null when no actor names a bone.
    /// </summary>
    public static RagdollDefinition? Build(PhysicsFile file, SkeletonRig rig, float scale, Func<string, Vector3[]?> hull)
    {
        var bodies = new List<RagdollBody>();
        var bodyOfActor = new int[file.Actors.Count];
        var bodyOfBone = new int[rig.BoneCount];
        Array.Fill(bodyOfBone, -1);
        for (int a = 0; a < file.Actors.Count; a++)
        {
            bodyOfActor[a] = -1;
            var actor = file.Actors[a];
            if (actor.Static || actor.Shapes.Count == 0) continue;
            if ((rig.Handle(actor.Name) ?? (actor.Bone is { } b ? rig.Handle(b) : null)) is not int bone || bodyOfBone[bone] >= 0) continue;
            // One shape per actor in every shipped ragdoll; a further one would need a compound (not built: the first stands for the actor).
            var shape = actor.Shapes[0];
            var bind = new RigidTransform(shape.Position * scale, shape.Rotation);
            var boneBind = new RigidTransform(actor.Position * scale, Quaternion.Normalize(Quaternion.Inverse(rig.BindRotationInverse[bone])));
            Vector3[]? points = null;
            if (shape.Type == PhysicsShapeType.Mesh)
            {
                // The cooked hull is in the shape's frame (its local pose places it on the actor). Bepu centres a hull on its centroid, so the body's frame moves there.
                if (hull(shape.CookedFile) is not { Length: >= 4 } raw) continue;
                var scaled = raw.Select(p => p * scale).ToArray();
                var pool = new BepuUtilities.Memory.BufferPool();
                try
                {
                    var made = new BepuPhysics.Collidables.ConvexHull(scaled, pool, out var centre);
                    made.Dispose(pool);
                    points = [.. scaled.Select(p => p - centre)];
                    bind = new RigidTransform(bind.Apply(centre), bind.Rotation);
                }
                finally { pool.Clear(); }
            }
            var size = shape.Type switch
            {
                PhysicsShapeType.Sphere => new Vector3(shape.Size.X / 2 * scale, 0, 0),
                PhysicsShapeType.Capsule => new Vector3(shape.Size.X / 2 * scale, shape.Size.Y * scale, 0),
                _ => shape.Size * scale,
            };
            bodyOfActor[a] = bodies.Count;
            bodyOfBone[bone] = bodies.Count;
            bodies.Add(new RagdollBody
            {
                Bone = bone,
                Name = actor.Name,
                Type = shape.Type,
                Size = size,
                Hull = points,
                Mass = actor.Mass > 0 ? actor.Mass : 1,
                Bind = bind,
                BoneInBody = boneBind.Then(bind.Inverse()),
                Actor = boneBind.Position,
            });
        }
        if (bodies.Count == 0) return null;

        var joints = new List<RagdollJoint>();
        var joined = new ulong[bodies.Count];
        foreach (var j in file.Joints)
        {
            int a = j.Actor0 >= 0 && j.Actor0 < bodyOfActor.Length ? bodyOfActor[j.Actor0] : -1;
            int b = j.Actor1 >= 0 && j.Actor1 < bodyOfActor.Length ? bodyOfActor[j.Actor1] : -1;
            // A joint to the world (an index of -1 or a static actor) would nail the ragdoll to a point of model space; none of the ragdolls has one.
            if (a < 0 || b < 0 || a == b || j.Type is not (PhysicsJointType.Revolute or PhysicsJointType.Spherical or PhysicsJointType.Fixed)) continue;
            var axis = j.Axis.LengthSquared() > 1e-8f ? Vector3.Normalize(j.Axis) : Vector3.UnitX;
            var normal = j.Normal - Vector3.Dot(j.Normal, axis) * axis;
            normal = normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : Perpendicular(axis);
            var basis = PhysicsAttachmentFile.FromColumns(normal, Vector3.Cross(axis, normal), axis);
            var (A, B) = (bodies[a].Bind, bodies[b].Bind);
            var anchor = j.Anchor * scale;
            float deg = MathF.PI / 180;
            joints.Add(new RagdollJoint
            {
                Type = j.Type,
                A = a,
                B = b,
                OffsetA = A.Inverse().Apply(anchor),
                OffsetB = B.Inverse().Apply(anchor),
                BasisA = Quaternion.Normalize(Quaternion.Inverse(A.Rotation) * basis),
                BasisB = Quaternion.Normalize(Quaternion.Inverse(B.Rotation) * basis),
                SpringStiffness = j.Type == PhysicsJointType.Revolute && j.Spring1 is { Spring: > 0 } ? j.Spring1.Value.Spring : 0,
                SpringDamper = j.Spring1?.Damper ?? 0,
                SpringTarget = (j.Spring1?.Target ?? 0) * deg,
                Limits = j.LimitsOn,
                Low = Math.Min(j.Low, j.High) * deg,
                High = Math.Max(j.Low, j.High) * deg,
                Swing = Math.Clamp(j.Swing, 1, 179) * deg,
            });
            if (a < 64) joined[b] |= 1UL << a;
            if (b < 64) joined[a] |= 1UL << b;
        }
        int root = rig.Handle("Bip01") is int r && bodyOfBone[r] >= 0 ? bodyOfBone[r] : 0;
        return new RagdollDefinition { Bodies = [.. bodies], Joints = [.. joints], BodyOfBone = bodyOfBone, Root = root, Joined = joined };
    }

    static Vector3 Perpendicular(Vector3 v) => Vector3.Normalize(Vector3.Cross(v, MathF.Abs(v.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
}