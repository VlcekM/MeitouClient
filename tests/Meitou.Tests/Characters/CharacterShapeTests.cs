using System.Numerics;
using Meitou.Content;
using Meitou.Data.Characters;
using Meitou.Data.Ogre;

namespace Meitou.Tests.Characters;

public class CharacterShapeTests
{
    static CharacterShapeResult Compute(CharacterShapeInput input, bool female = false) =>
        CharacterShape.Compute(input with { Female = female }, name => female || (name != "L Boob" && name != "R Boob"));

    static void Near(Vector3 expected, Vector3 actual, string what) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-5f, $"{what}: expected {expected}, got {actual}");

    [Fact]
    public void Height_scales_the_spine_and_legs_and_moves_the_hips()
    {
        var r = Compute(new CharacterShapeInput { Sliders = new Dictionary<string, float> { ["Height"] = 120 } });
        Assert.Equal(1.2f, r.MovementScale, 1e-5f);
        Near(new Vector3(1, 1.2f, 1), r.BoneSize["Bip01 Pelvis"], "pelvis");
        Near(new Vector3(1, 1.2f * 0.95f, 1), r.BoneSize["Bip01 L Thigh"], "thigh");
        Near(new Vector3(1, 0.8f, 1), r.PositionalSize["Bip01 L Thigh"], "thigh positional"); // (2 - h): cancels the pelvis' Y
        Near(new Vector3(1.2f), r.BoneSize["Bip01 L Toe0"], "toe (feet x height)");
        Near(new Vector3(1, 1, 1), r.BoneSize["Bip01 Neck"], "neck");
    }

    [Fact]
    [Slow]
    public void Ruka_gets_her_body_file_proportions()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var body = AppearanceFile.Read(Path.Combine(install.DataDirectory, "character", "bodies", "Ruka.bod2"));
        var input = new CharacterShapeInput { Sliders = body.Floats.ToDictionary(), Female = body.Female };
        Assert.All(CharacterShape.Sliders, s => Assert.True(body.Floats.ContainsKey(s), $"Ruka.bod2 has no '{s}'"));
        var r = CharacterShape.Compute(input, _ => true);
        float h = body.Get("Height") / 100;
        float frame = body.Get("Frame") / 100;
        float hips = body.Get("Hips") / 100;
        Near(new Vector3(hips * frame, h, hips * frame), r.BoneSize["Bip01 Pelvis"], "pelvis");
        Assert.Equal(h + body.Get("Leg length") / 100 - 1, r.MovementScale, 1e-5f);
        float headSize = body.Get("Head size") / 100;
        Assert.Equal(headSize, r.BoneSize["Bip01 Head"].Y, 1e-5f);
        Assert.Equal(body.Get("Jaw") / 100 * r.BoneSize["Bip01 Head"].X, r.BoneSize["Bip01 Jaw"].X, 1e-5f);
    }
}

public class CharacterLodTests
{
    [Fact]
    public void Select_follows_ogres_lower_bound()
    {
        List<MeshLodLevel> levels = [new(0, []), new(200, []), new(400, [])];
        Assert.Equal(0, CharacterLod.Select(levels, -5));
        Assert.Equal(0, CharacterLod.Select(levels, 200)); // strictly above the distance
        Assert.Equal(1, CharacterLod.Select(levels, 200.5f));
        Assert.Equal(2, CharacterLod.Select(levels, 1e6f));
        Assert.Equal(0, CharacterLod.Select([new MeshLodLevel(0, [])], 1e6f));
        Assert.Equal(90, CharacterLod.Value(new Vector3(0, 0, 100), Vector3.Zero, 10), 1e-4f);
    }

    [Fact]
    [Slow]
    public void Human_male_body_has_one_reduced_level_at_200_and_named_morph_poses()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var mesh = OgreMeshReader.ReadFile(Path.Combine(install.DataDirectory, "character", "meshes", "human", "human_male.mesh"));
        var levels = CharacterLod.Levels(mesh);
        Assert.Equal(2, levels.Count);
        Assert.Equal(200, levels[1].Distance);
        // The body's poses are named morphs.
        Assert.Equal(23, mesh.Poses.Count);
        Assert.Contains(mesh.Poses, p => p.Name == "wide_cheekbones");
        Assert.All(mesh.Poses, p => Assert.Equal(1, p.Target));
        int poseVertices = (int)mesh.SubMeshes[0].VertexData!.VertexCount;
        Assert.All(mesh.Poses, p => Assert.All(p.Vertices, v => Assert.True(v.Index < poseVertices)));
        for (int s = 0; s < mesh.SubMeshes.Count; s++)
        {
            var full = levels[0].Indices[s]!;
            var reduced = levels[1].Indices[s]!;
            Assert.True(reduced.Length > 0 && reduced.Length < full.Length, $"submesh {s}: {reduced.Length} vs {full.Length}");
            Assert.Equal(0, reduced.Length % 3);
            uint vertices = (mesh.SubMeshes[s].UseSharedVertices ? mesh.SharedVertexData : mesh.SubMeshes[s].VertexData)!.VertexCount;
            Assert.All(reduced, i => Assert.True(i < vertices));
        }
        // The female body ships without LOD.
        var female = OgreMeshReader.ReadFile(Path.Combine(install.DataDirectory, "character", "meshes", "human", "human_female.mesh"));
        Assert.Single(CharacterLod.Levels(female));
    }
}
