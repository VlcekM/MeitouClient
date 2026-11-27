using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using Meitou.Simulation.Ragdolls;

namespace Meitou.Tests.Physics;

public class RagdollGroundTests
{
    sealed class Flat(float height) : IRagdollEnvironment
    {
        public float GroundHeight(float x, float z, float nearY) => height;
        public bool Buildings(Vector3 min, Vector3 max, List<Vector3> triangles) => true;
    }

    /// <summary>
    /// Flat ground with a building floor at <paramref name="floor"/> over the square from -50 to 50, its collision read or still being read.
    /// </summary>
    sealed class Floored(float height, float floor, bool read) : IRagdollEnvironment
    {
        public float GroundHeight(float x, float z, float nearY) => height;

        public bool Buildings(Vector3 min, Vector3 max, List<Vector3> triangles)
        {
            if (!read) return false;

            Vector3 a = new(-50, floor, -50), b = new(50, floor, -50), c = new(50, floor, 50), d = new(-50, floor, 50);
            triangles.AddRange([a, b, c, a, c, d]);

            return true;
        }
    }

    /// <summary>
    /// A saved frozen pose rests when a body lies on the terrain or a building floor (upstream #1574): a body hanging 50 units over both was frozen in
    /// the air and falls again on load, and with advanced ragdolls a falling one does not freeze. Collision still being read counts as resting; without the
    /// buildings asked, only the terrain counts.
    /// </summary>
    [Fact]
    public void A_saved_pose_rests_only_on_the_terrain_or_a_building_floor_under_it()
    {
        RigidTransform At(float y) => new(new Vector3(0, y, 0), Quaternion.Identity);
        RigidTransform[] lying = [At(103), At(102)], hanging = [At(152), At(151)], onFloor = [At(203), At(202)];

        Assert.True(RagdollSystem.Rests(new Flat(100), lying, null, buildings: false));
        Assert.False(RagdollSystem.Rests(new Flat(100), hanging, null, buildings: true));
        Assert.True(RagdollSystem.Rests(new Floored(100, 200, read: true), onFloor, null, buildings: true));
        Assert.False(RagdollSystem.Rests(new Floored(100, 200, read: true), onFloor, null, buildings: false));
        Assert.False(RagdollSystem.Rests(new Floored(100, 200, read: true), hanging, null, buildings: true));   // the floor is above it
        Assert.True(RagdollSystem.Rests(new Floored(100, 200, read: false), hanging, null, buildings: true));

        // A body's own reach counts: a capsule of radius 2 and length 6 reaches 5 from its centre.
        var capsule = new RagdollBody { Bone = 0, Name = "Bip01 Pelvis", Type = Meitou.Data.Physics.PhysicsShapeType.Capsule, Size = new Vector3(2, 6, 0) };

        Assert.Equal(5, RagdollSystem.Reach(capsule));
    }

    /// <summary>
    /// The ground is a one-sided mesh, so a body behind it has nothing to land on: a new ragdoll with a body centre under the terrain is lifted out
    /// as one (not when it is metres under, a floor sunk into a hill), and a thin limb falling at the speed cap still lands on the ground and rests
    /// on it.
    /// </summary>
    [Fact]
    public void A_ragdoll_that_starts_under_the_terrain_is_lifted_out_and_a_fast_thin_limb_rests_on_the_ground()
    {
        var ground = new Flat(100);
        RigidTransform At(float y) => new(new Vector3(0, y, 0), Quaternion.Identity);
        bool[] all = [true, true];

        Assert.Equal(0, RagdollSystem.OutOfGround(ground, [At(103), At(101)], all));
        Assert.Equal(2.25f, RagdollSystem.OutOfGround(ground, [At(103), At(98)], all), 3);          // the lowest ends 0.25 above the terrain
        Assert.Equal(0, RagdollSystem.OutOfGround(ground, [At(103), At(80)], all));                  // too deep to be a foot in a slope
        Assert.Equal(0, RagdollSystem.OutOfGround(ground, [At(103), At(98)], [true, false]));        // a pinned body does not count
        Assert.Equal(0, RagdollSystem.OutOfGround(null, [At(98)], [true]));

        using var physics = new RagdollPhysics();
        var capsule = new Capsule(0.75f, 4);
        var pose = new RigidPose(new Vector3(5, 160, 5), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1));
        var body = physics.AddBody(BodyDescription.CreateDynamic(pose, new BodyVelocity(new Vector3(0, -RagdollPhysics.MaxSpeed, 0)),
                capsule.ComputeInertia(1),
            RagdollPhysics.Collidable(physics.Simulation.Shapes.Add(capsule)), new BodyActivityDescription(0.01f)), new BodyTag());
        float lowest = float.MaxValue;
        for (int tick = 0; tick < 150; tick++)
        {
            physics.Cover(new Vector3(5, 100, 5), 12, ground, tick);
            physics.Step(1f / 30);
            lowest = MathF.Min(lowest, physics.Simulation.Bodies.GetBodyReference(body).Pose.Position.Y);
        }
        float rest = physics.Simulation.Bodies.GetBodyReference(body).Pose.Position.Y;

        Assert.True(lowest > 100, $"went through the ground to {lowest}");
        Assert.InRange(rest, 100.3f, 103);
    }
}
