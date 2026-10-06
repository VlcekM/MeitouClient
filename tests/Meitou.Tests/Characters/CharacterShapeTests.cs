using System.Numerics;
using Meitou.Content;
using Meitou.Data.Characters;
using Meitou.Data.Ogre;

namespace Meitou.Tests.Characters;

public class CharacterShapeTests
{
    static readonly string[] Bones =
    [
        "Bip01 Pelvis", "Bip01 Spine", "Bip01 Spine1", "Bip01 Spine2", "Bip01 L Thigh", "Bip01 R Thigh", "Bip01 L Calf", "Bip01 R Calf",
        "Bip01 L Foot", "Bip01 R Foot", "Bip01 L Toe0", "Bip01 R Toe0", "Bip01 L Clavicle", "Bip01 R Clavicle", "Bip01 L UpperArm",
        "Bip01 R UpperArm", "Bip01 L Forearm", "Bip01 R Forearm", "Bip01 L Hand", "Bip01 R Hand", "Bip01 Neck", "Bip01 Head",
    ];

    static CharacterShapeResult Compute(CharacterShapeInput input, bool female = false) =>
        CharacterShape.Compute(input with { Female = female }, name => female || (name != "L Boob" && name != "R Boob"));

    static void Near(Vector3 expected, Vector3 actual, string what) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-5f, $"{what}: expected {expected}, got {actual}");

    [Fact]
    public void Neutral_sliders_leave_every_bone_at_one_except_the_thigh_height()
    {
        var r = Compute(new CharacterShapeInput());
        Assert.Equal(1, r.MovementScale, 1e-6f);
        foreach (var bone in Bones)
        {
            var expected = bone.EndsWith("Thigh", StringComparison.Ordinal) ? new Vector3(1, 0.95f, 1) : Vector3.One;
            Near(expected, r.BoneSize[bone], bone);
        }
        foreach (var (bone, v) in r.PositionalSize) Near(Vector3.One, v, bone + " positional");
        Near(Vector3.One, r.BoneSize["Bip01 Jaw"], "jaw");
        Assert.DoesNotContain("L Boob", r.BoneSize.Keys); // no such bone in this call
    }

    [Fact]
    public void Female_breasts_are_neutral_at_100()
    {
        var r = Compute(new CharacterShapeInput(), female: true);
        Near(Vector3.One, r.BoneSize["L Boob"], "L Boob");
        Near(Vector3.One, r.PositionalSize["R Boob"], "R Boob positional");
    }

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
    public void Muscle_and_starvation_change_bulk()
    {
        var strong = Compute(new CharacterShapeInput { Muscle = 1 });
        Near(new Vector3(1.27f, 0.95f, 1.27f), strong.BoneSize["Bip01 R Thigh"], "male thigh at full muscle");
        Near(new Vector3(1.13f, 0.13f * 0.3f + 1, 1.13f), strong.BoneSize["Bip01 R Clavicle"], "male clavicle at full muscle");
        var starving = Compute(new CharacterShapeInput { Starvation = 1 });
        Near(new Vector3(0.7f, 1, 0.6f), starving.BoneSize["Bip01 Spine1"], "waist x 0.7, stomach x 0.6");
    }

    [Fact]
    public void Missing_arms_scale_forearm_and_hand_and_hide_stump_the_upper_arm()
    {
        var input = new CharacterShapeInput { Missing = new HashSet<Limb> { Limb.LeftArm, Limb.RightLeg } };
        var r = Compute(input);
        Near(Vector3.Zero, r.BoneSize["Bip01 L Forearm"], "L forearm");
        Near(Vector3.Zero, r.BoneSize["Bip01 L Hand"], "L hand");
        Near(Vector3.One, r.BoneSize["Bip01 L UpperArm"], "L upper arm (stump shows)");
        Near(Vector3.One, r.BoneSize["Bip01 R Forearm"], "R forearm");
        Near(Vector3.Zero, r.BoneSize["Bip01 R Calf"], "R calf");
        Near(Vector3.Zero, r.BoneSize["Bip01 R Foot"], "R foot");
        var hidden = Compute(input with { HideStump = HideStump.LeftArm });
        Near(Vector3.Zero, hidden.BoneSize["Bip01 L UpperArm"], "L upper arm with hide stump");
        var other = Compute(input with { HideStump = HideStump.RightArm });
        Near(Vector3.One, other.BoneSize["Bip01 L UpperArm"], "L upper arm, right stump hidden");
    }

    [Fact]
    public void Stat_and_nutrition_mappings()
    {
        Assert.Equal(0, CharacterShape.MuscleFromStats(20, 5, 0), 1e-5f);
        Assert.Equal(1, CharacterShape.MuscleFromStats(10, 99, 50), 1e-5f);
        Assert.True(CharacterShape.MuscleFromStats(0, 0, 0) < 0); // not clamped
        Assert.Equal(1, CharacterShape.StarvationFromNutrition(1));
        Assert.Equal(0.75f, CharacterShape.StarvationFromNutrition(1.5f), 1e-5f);
        Assert.Equal(0, CharacterShape.StarvationFromNutrition(2.5f));
        Assert.Equal(("nm male", "nm male strong", 0.5f), CharacterShape.NormalBlend(false, 0.5f, 0.1f));
        Assert.Equal(("nm female strong", "nm female skinny", 0.3f), CharacterShape.NormalBlend(true, 0.5f, 0.3f));
        Assert.Equal(("nm male", "nm male skinny", 0.2f), CharacterShape.NormalBlend(false, 0.1f, 0.2f));
    }

    [Fact]
    public void Pose_weights_are_baked_unscaled_except_cut_horns()
    {
        Assert.Equal(0.37f, CharacterShape.BakeWeight("bone_wide_jaw", 0.37f, false));
        Assert.Null(CharacterShape.BakeWeight("bone_wide_jaw", 0, false));
        Assert.Equal(1f, CharacterShape.BakeWeight("bone_horns_top_short", 0.2f, true));
        Assert.Equal(1f, CharacterShape.BakeWeight("bone_horns_bottom_short", -0.5f, true));
        Assert.Null(CharacterShape.BakeWeight("bone_horns_curved", 0.8f, true));
        Assert.Equal(0.8f, CharacterShape.BakeWeight("bone_horns_curved", 0.8f, false));
        Assert.Null(CharacterShape.BakeWeight("bone_horns_top_short", 0, true));
    }

    [Fact]
    public void Overrides_accept_kenshi_units_and_fractions()
    {
        var input = CharacterShape.Override(new CharacterShapeInput(), ["height=0.8", "Arm bulk=120; legs_bulk=1.1", "muscle=0.5", "missing=larm,rleg"], out var applied);
        Assert.Equal(80, input.Sliders["Height"], 1e-4f);
        Assert.Equal(120, input.Sliders["Arm bulk"], 1e-4f);
        Assert.Equal(110, input.Sliders["Legs bulk"], 1e-4f);
        Assert.Equal(0.5f, input.Muscle);
        Assert.Equal(new HashSet<Limb> { Limb.LeftArm, Limb.RightLeg }, input.Missing);
        Assert.Equal(5, applied.Count);
        Assert.Throws<FormatException>(() => CharacterShape.Override(new CharacterShapeInput(), ["wingspan=2"], out _));
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
        float h = body.Get("Height") / 100, frame = body.Get("Frame") / 100, hips = body.Get("Hips") / 100;
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
    public void Human_male_body_has_one_reduced_level_at_200()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var mesh = OgreMeshReader.ReadFile(Path.Combine(install.DataDirectory, "character", "meshes", "human", "human_male.mesh"));
        var levels = CharacterLod.Levels(mesh);
        Assert.Equal(2, levels.Count);
        Assert.Equal(200, levels[1].Distance);
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
