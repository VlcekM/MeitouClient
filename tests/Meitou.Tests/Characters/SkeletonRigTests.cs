using System.Numerics;
using Meitou.Content;
using Meitou.Data.Ogre;
using Meitou.Rendering.Characters;

namespace Meitou.Tests.Characters;

public class SkeletonRigTests
{
    [Fact]
    public void Bind_pose_without_layers_or_shape_gives_identity_skin_matrices()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "needs the Kenshi install");
        var path = Directory.EnumerateFiles(install!.DataDirectory, "*.skeleton", SearchOption.AllDirectories)
            .FirstOrDefault(p => Path.GetFileName(p).Equals("male_skeleton.skeleton", StringComparison.OrdinalIgnoreCase));
        Assert.SkipWhen(path is null, "no male_skeleton.skeleton");
        var rig = new SkeletonRig(OgreSkeletonReader.ReadFile(path!));
        var scratch = rig.Rent();
        var skin = new Matrix4x4[rig.BoneCount];
        scratch.Pose(null, [], skin);
        rig.Return(scratch);
        foreach (var h in rig.Order)
        {
            var m = skin[h];
            Assert.True(Math.Abs(m.M11 - 1) < 1e-3f && Math.Abs(m.M22 - 1) < 1e-3f && Math.Abs(m.M33 - 1) < 1e-3f
                && Math.Abs(m.M41) < 1e-2f && Math.Abs(m.M42) < 1e-2f && Math.Abs(m.M43) < 1e-2f, $"bone {h}: {m}");
        }
    }
}
