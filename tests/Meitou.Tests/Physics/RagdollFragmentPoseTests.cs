using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Data.Ogre;

namespace Meitou.Tests.Physics;

public class RagdollFragmentPoseTests
{
    [Fact]
    public void A_detached_child_can_move_to_its_own_physics_position_without_its_former_parent_tether()
    {
        var skeleton = new OgreSkeleton();
        skeleton.Bones.Add(new() { Handle = 0, Name = "Bip01", Position = new(0, 10, 0) });
        skeleton.Bones.Add(new() { Handle = 1, Parent = 0, Name = "Bip01 L UpperArm", Position = new(3, 0, 0) });
        skeleton.Bones.Add(new() { Handle = 2, Parent = 1, Name = "Bip01 L Forearm", Position = new(0, -3, 0) });
        var rig = new SkeletonRig(skeleton);
        var overrides = new BonePose(3) { AbsolutePositions = true };
        overrides.Set[0] = overrides.Set[1] = true;
        overrides.Position[0] = new(0, 10, 0);
        overrides.Position[1] = new(7, 1, -2);
        overrides.Rotation[0] = overrides.Rotation[1] = Quaternion.Identity;
        var scratch = rig.Rent();
        try
        {
            scratch.PoseDerived(null, [], overrides);
            Assert.Equal(new Vector3(7, 1, -2), scratch.DerivedPosition[1]);
            Assert.Equal(new Vector3(7, -2, -2), scratch.DerivedPosition[2]);
            // Ordinary ragdolls still use parent offsets; the explicit mode is required only for cut fragments.
            overrides.AbsolutePositions = false;
            scratch.PoseDerived(null, [], overrides);
            Assert.Equal(new Vector3(3, 10, 0), scratch.DerivedPosition[1]);
            Assert.Equal(new Vector3(3, 7, 0), scratch.DerivedPosition[2]);
        }
        finally { rig.Return(scratch); }
    }
}
