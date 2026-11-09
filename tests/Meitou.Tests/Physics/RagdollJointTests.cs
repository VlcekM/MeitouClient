using System.Numerics;
using Meitou.Content;
using Meitou.Data.Characters;
using Meitou.Data.Ogre;
using Meitou.Data.Physics;
using Meitou.Simulation.Ragdolls;

namespace Meitou.Tests.Physics;

public class RagdollJointTests
{
    /// <summary>
    /// The joint bases and limits of the human ragdoll, measured as Bepu's twist limit measures them (B's X about A's Z after aligning the Z axes),
    /// hold the poses of every clip of the male skeleton: a flipped axis or sign would bend knees and elbows backwards, and the clips would fall
    /// outside the file's ranges.
    /// </summary>
    [Fact]
    public void Human_limb_joints_hold_every_clip_of_the_male_skeleton()
    {
        var install = GameInstall.Locate();

        Assert.SkipWhen(install is null, "needs the Kenshi install");

        var skeleton = Directory.EnumerateFiles(install!.DataDirectory, "male_skeleton.skeleton", SearchOption.AllDirectories).FirstOrDefault();
        var file = Path.Combine(install.DataDirectory, "ragdoll", "hu_m_ragdoll_5.phs");

        Assert.SkipWhen(skeleton is null || !File.Exists(file), "no male skeleton or human ragdoll");

        var rig = new SkeletonRig(OgreSkeletonReader.ReadFile(skeleton!));
        var ragdoll = RagdollDefinition.Build(PhysicsFile.ReadFile(file), rig, 1, _ => null)!;
        string[] limbs = ["Bip01 L Thigh", "Bip01 R Thigh", "Bip01 L Calf", "Bip01 R Calf", "Bip01 L Forearm", "Bip01 R Forearm"];
        var joints = ragdoll.Joints.Where(j => j.Type == PhysicsJointType.Revolute && limbs.Contains(ragdoll.Bodies[j.B].Name)).ToArray();

        Assert.Equal(6, joints.Length);

        var inside = new int[joints.Length];
        var widest = new float[joints.Length];
        int poses = 0;
        var scratch = rig.Rent();
        foreach (var animation in rig.Skeleton.Animations)
        {
            if (rig.Layer(null, animation.Name) is not { } layer) continue;
            for (int k = 0; k < 4; k++, poses++)
            {
                scratch.PoseDerived(null, [new ActiveLayer(layer, layer.Clip.Length * k / 4, 1)]);
                RigidTransform Body(int b) => ragdoll.Bodies[b].BoneInBody.Inverse().Then(new(scratch.DerivedPosition[ragdoll.Bodies[b].Bone],
                        scratch.DerivedRotation[ragdoll.Bodies[b].Bone]));
                for (int j = 0; j < joints.Length; j++)
                {
                    float angle = Twist(Body(joints[j].A).Rotation * joints[j].BasisA, Body(joints[j].B).Rotation * joints[j].BasisB);
                    const float slack = 5 * MathF.PI / 180;
                    if (angle >= joints[j].Low - slack && angle <= joints[j].High + slack) inside[j]++;
                    if (MathF.Abs(angle) > MathF.Abs(widest[j])) widest[j] = angle;
                }
            }
        }
        rig.Return(scratch);

        Assert.True(poses > 100, $"{poses} poses");

        for (int j = 0; j < joints.Length; j++)
        {
            string name = ragdoll.Bodies[joints[j].B].Name;

            Assert.True(inside[j] >= poses * 99 / 100, $"{name}: {inside[j]} of {poses} poses inside {joints[j].Low} .. {joints[j].High}");

            // Each limb bends far in the clips (a knee past 90 degrees), on the side the file allows.
            Assert.True(MathF.Abs(widest[j]) > MathF.PI / 2 && widest[j] >= joints[j].Low - 0.1f && widest[j] <= joints[j].High + 0.1f,
                    $"{name}: widest {widest[j]}");
        }
    }

    /// <summary>
    /// B's X axis about A's Z after the shortest rotation taking B's Z onto A's (the angle Bepu's <c>TwistLimit</c> keeps between its minimum and
    /// maximum).
    /// </summary>
    static float Twist(Quaternion a, Quaternion b)
    {
        var za = Vector3.Transform(Vector3.UnitZ, a);
        var zb = Vector3.Transform(Vector3.UnitZ, b);
        var axis = Vector3.Cross(zb, za);
        var align = axis.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(new Quaternion(axis, 1 + Vector3.Dot(zb, za)));
        var xb = Vector3.Transform(Vector3.Transform(Vector3.UnitX, b), align);
        return MathF.Atan2(Vector3.Dot(xb, Vector3.Transform(Vector3.UnitY, a)), Vector3.Dot(xb, Vector3.Transform(Vector3.UnitX, a)));
    }
}
