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
