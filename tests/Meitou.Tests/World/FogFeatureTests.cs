using System.Numerics;
using System.Text;
using Meitou.Content;
using Meitou.Data.World;
using Meitou.Rendering;

namespace Meitou.Tests.World;

/// <summary>docs/formats/fogfeatures.md: the placed fog volumes of <c>fogfeatures.dat</c>.</summary>
public class FogFeatureTests
{
    static void Floats(BinaryWriter w, params float[] values) { foreach (var v in values) w.Write(v); }

    static void Name(BinaryWriter w, string name) { w.Write((byte)name.Length); w.Write(Encoding.Latin1.GetBytes(name)); }

    /// <summary>A file with one of each type; the block is the box 0..1000 in x and z under a roof at 500, as the game stores planes: (normal, d) with inside n·p &lt; -d.</summary>
    static byte[] SampleFile()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("FF02"));
        w.Write(3);
        Name(w, "ball");
        w.Write((byte)2);
        Floats(w, 1, 0.5f, 0.25f, 1, 2000);   // colour, distance
        Floats(w, 10, 20, 30, 400);            // position, radius
        Name(w, "beam");
        w.Write((byte)3);
        Floats(w, 1, 1, 1, 1, 7500);
        Floats(w, 0, -100, 0, 0, 900, 0, 5000, 1000);   // start, end, radius, edge
        Name(w, "Swamp test");
        w.Write((byte)4);
        Floats(w, 0.68f, 0.56f, 0.43f, 1, 2000, 900);   // colour, distance, edge
        (Vector3 N, float D)[] planes =
        [
            (Vector3.UnitY, -500), (Vector3.UnitX, -1000), (-Vector3.UnitX, 0), (Vector3.UnitZ, -1000), (-Vector3.UnitZ, 0),
            (Vector3.Normalize(new Vector3(1, 0, 1)), -2000), (-Vector3.UnitY, -2000),   // a cut far outside the box; the floor at y = -2000
        ];
        foreach (var (n, d) in planes) Floats(w, n.X, n.Y, n.Z, d);
        w.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void Reads_every_type_with_the_games_field_order()
    {
        var list = FogFeatures.Read(SampleFile());
        Assert.Equal(3, list.Count);
        var ball = list[0];
        Assert.Equal(("ball", FogFeatureType.Sphere, new Vector3(10, 20, 30), 400f, 2000f), (ball.Name, ball.Type, ball.Position, ball.Radius, ball.Distance));
        Assert.Equal(new Vector3(1, 0.5f, 0.25f), ball.Colour);
        var beam = list[1];
        Assert.Equal((FogFeatureType.Beam, new Vector3(0, -100, 0), new Vector3(0, 900, 0), 5000f, 1000f), (beam.Type, beam.Position, beam.End, beam.Radius, beam.Edge));
        var block = list[2];
        Assert.Equal(FogFeatureType.Block, block.Type);
        Assert.Equal(1f / 2000, block.Density, 6);
        Assert.Equal(1f / 900, block.EdgeBlur, 6);
        // The stored d becomes w = -d: inside is n·p < 500 under the roof.
        Assert.Equal(new Vector4(0, 1, 0, 500), block.Planes[0]);
    }

    [Fact]
    public void A_block_is_the_space_inside_its_planes()
    {
        var block = FogFeatures.Read(SampleFile())[2];
        Assert.True(block.Contains(new Vector3(500, 0, 500)));
        Assert.False(block.Contains(new Vector3(500, 600, 500)));    // above the roof
        Assert.False(block.Contains(new Vector3(-10, 0, 500)));      // outside a wall
        Assert.False(block.Contains(new Vector3(500, -2200, 500)));  // below the floor
        // A box: eight corners (the diagonal cut touches nothing).
        var corners = block.Corners();
        Assert.Equal(8, corners.Count);
        Assert.Contains(corners, c => Vector3.Distance(c, new Vector3(1000, 500, 1000)) < 0.01f);
        var section = block.SectionBounds(0)!.Value;
        Assert.Equal(Vector2.Zero, section.Min);
        Assert.Equal(new Vector2(1000, 1000), section.Max);
        Assert.Null(block.SectionBounds(600));   // above the roof the section is empty
    }

    [Fact]
    public void Rejects_an_unknown_magic_or_type()
    {
        Assert.Throws<FormatException>(() => FogFeatures.Read(Encoding.ASCII.GetBytes("FF03\0\0\0\0")));
        var bad = SampleFile();
        bad[4 + 4 + 1 + 4] = 9;   // the first volume's type byte
        Assert.Throws<FormatException>(() => FogFeatures.Read(bad));
    }

    [Fact]
    public void The_volumes_light_follows_the_sun_with_a_night_floor()
    {
        // post/fog.hlsl fog_planes_fs: max(0.08, sunColour.w * 1.6 * saturate(3 sunY + 0.2)).
        Assert.Equal(KenshiLighting.Daylight(1) * 1.6f, FogVolumes.Light(1), 5);
        Assert.Equal(0.08f, FogVolumes.Light(-0.5f), 5);
        Assert.True(FogVolumes.Light(0.1f) < FogVolumes.Light(0.4f));
    }

    static FogVolumes.Volume Volume(FogVolumes volumes, string name) => volumes.Statics.Single(v => v.Name == name);

    [Fact]
    public void Spheres_and_beams_take_the_games_shader_radii()
    {
        // fog_sphere_vs measures 0.48 of unit_sphere.mesh scaled by 2r; fog_beam_vs 0.4 of cylinder.mesh scaled by 2r across and the length along.
        var volumes = new FogVolumes(FogFeatures.Read(SampleFile()));
        var ball = Volume(volumes, "ball").Data;
        Assert.Equal(new Vector4(10, 20, 30, FogVolumeShaders.SphereType), ball[0]);
        Assert.Equal(new Vector4(400 * 0.96f, 400, 1, 0), ball[1]);
        Assert.Equal(new Vector4(1, 0.5f, 0.25f, 1f / 2000), ball[2]);
        var beam = Volume(volumes, "beam").Data;
        Assert.Equal(new Vector4(0, -100, 0, FogVolumeShaders.BeamType), beam[0]);
        Assert.Equal(new Vector4(0, 1, 0, 1000), beam[1]);   // unit axis, length
        Assert.Equal(new Vector4(5000 * 0.8f, 1f / 1000, 1, 0), beam[2]);
        var block = Volume(volumes, "Swamp test").Data;
        Assert.Equal(FogVolumeShaders.BlockLength, block.Length);
        Assert.Equal(new Vector4(0, 1, 0, 500), block[3]);
    }

    [Fact]
    public void Every_volume_in_view_is_drawn_farthest_first()
    {
        var volumes = new FogVolumes(FogFeatures.Read(SampleFile()));
        // From x = -20000 looking along +x, all three lie ahead; the block (centre 500, 500) is farther than the sphere at (10, 30) and the beam at 0.
        var eye = new Vector3(-20000, 300, 0);
        volumes.Update(eye, Vector3.UnitX, 50 * MathF.PI / 180, 16f / 9, 450000, 0.5f, on: true);
        Assert.Equal(["Swamp test", "ball", "beam"], volumes.Active);
        Assert.Equal(FogVolumeShaders.BlockLength + FogVolumeShaders.SphereLength + FogVolumeShaders.BeamLength, volumes.UsedData);
        Assert.Equal(FogVolumeShaders.BlockType, (int)volumes.Data[0].W);
        Assert.Equal(FogVolumeShaders.SphereType, (int)volumes.Data[FogVolumeShaders.BlockLength].W);
        // Looking away (along -x) the camera's wedge holds none of them; looking straight down, the wedge is off and all are kept.
        volumes.Update(eye, -Vector3.UnitX, 50 * MathF.PI / 180, 16f / 9, 450000, 0.5f, on: true);
        Assert.Empty(volumes.Active);
        Assert.Equal(0, volumes.UsedData);
        volumes.Update(eye, -Vector3.UnitY, 50 * MathF.PI / 180, 16f / 9, 450000, 0.5f, on: true);
        Assert.Equal(3, volumes.Active.Count);
        // Beyond the far clip a volume adds nothing (the game's path is capped at the depth), so it is left out.
        volumes.Update(eye, Vector3.UnitX, 50 * MathF.PI / 180, 16f / 9, 10000, 0.5f, on: true);
        Assert.Empty(volumes.Active);
        volumes.Update(eye, Vector3.UnitX, 50 * MathF.PI / 180, 16f / 9, 450000, 0.5f, on: false);
        Assert.Empty(volumes.Active);
    }

    [Fact]
    public void The_nearest_volumes_are_kept_when_the_list_is_full()
    {
        var one = FogFeatures.Read(SampleFile())[2];
        // 60 copies of the block, each 2000 farther along x: 51 fit the 512 vec4s, the 9 farthest are left out.
        var many = Enumerable.Range(0, 60).Select(i => one with
        {
            Name = $"b{i}",
            Planes = [.. one.Planes.Select(p => p with { W = p.W + p.X * i * 2000 })],   // moved by (2000 i, 0, 0): w + n · shift
        }).ToList();
        var volumes = new FogVolumes(many);
        volumes.Update(new Vector3(-5000, 300, 500), Vector3.UnitX, 50 * MathF.PI / 180, 16f / 9, 450000, 0.5f, on: true);
        Assert.Equal(FogVolumeShaders.MaxData / FogVolumeShaders.BlockLength, volumes.Active.Count);
        Assert.Equal(60 - volumes.Active.Count, volumes.Dropped);
        Assert.Equal("b50", volumes.Active[0]);   // the farthest kept, drawn first
        Assert.Equal("b0", volumes.Active[^1]);
    }

    [Fact]
    public void An_effect_volume_fades_by_size_and_density_not_alpha()
    {
        // FogFadeSphere: radius 0 -> r, density distance 10 r -> D; FogFadeCylinder: 4 D -> D.
        Assert.Equal((0f, 5000f), FogVolumes.Faded(false, 500, 2000, 0));
        Assert.Equal((250f, 3500f), FogVolumes.Faded(false, 500, 2000, 0.5f));
        Assert.Equal((500f, 2000f), FogVolumes.Faded(false, 500, 2000, 1));
        Assert.Equal((250f, 5000f), FogVolumes.Faded(true, 500, 2000, 0.5f));
    }

    [Fact]
    public void An_effects_sphere_is_not_drawn_from_inside()
    {
        var volumes = new FogVolumes([]);
        var ball = new FogVolumes.EffectFog("Twister-Chuff01 DustBall", false, new Vector3(1000, 0, 0), default, 500, 2000, Vector3.One, 0.5f, false, 1);
        volumes.Update(Vector3.Zero, Vector3.UnitX, 1, 1.5f, 450000, 0.5f, true, [ball]);
        Assert.Equal(["Twister-Chuff01 DustBall"], volumes.Active);
        Assert.Equal(new Vector4(480, 500, 0.5f, 0), volumes.Data[1]);   // 0.96 r, the hull's r, the record's alpha, not additive
        volumes.Update(new Vector3(900, 0, 0), Vector3.UnitX, 1, 1.5f, 450000, 0.5f, true, [ball]);
        Assert.Empty(volumes.Active);
    }

    [Fact]
    public void The_shaders_array_matches_the_frame_block()
    {
        Assert.Contains($"uFogVolumeData[{FogVolumeShaders.MaxData}]", FogVolumeShaders.Functions);
        Assert.Contains($"fogVolumeData[{FogVolumeShaders.MaxData}]", NativeShaders.FrameBlock);
    }

    [Fact]
    public void The_installs_file_puts_Shark_inside_the_swamps_fog()
    {
        var install = InstallData.Install;
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var list = FogFeatures.Load(install!);
        // The whole file parses (the reader throws on a short file or an unknown type): 28 volumes, 25 blocks, 2 beams, 1 sphere.
        Assert.Equal(28, list.Count);
        Assert.Equal(25, list.Count(f => f.Type == FogFeatureType.Block));
        Assert.Equal(2, list.Count(f => f.Type == FogFeatureType.Beam));
        Assert.Single(list, f => f.Type == FogFeatureType.Sphere);
        var db = InstallData.BaseGame!;
        var towns = InstallData.Levels!.Towns().Select(t => (Name: db.Find(t.TownId)?.Name ?? "", t.Position)).ToList();
        var shark = towns.First(t => t.Name == "Shark").Position;
        var inside = list.Where(f => f.Contains(shark + new Vector3(0, 10, 0))).Select(f => f.Name).ToList();
        Assert.Contains(inside, n => n.Contains("Swamp[SOUTH]"));
        // The Great Desert's town sits in the desert's own faint blue block, not in the swamp's.
        var heft = towns.First(t => t.Name == "Heft").Position;
        var atHeft = list.Where(f => f.Contains(heft + new Vector3(0, 10, 0))).Select(f => f.Name).ToList();
        Assert.Contains(atHeft, n => n.StartsWith("Desert01"));
        Assert.DoesNotContain(atHeft, n => n.Contains("Swamp", StringComparison.OrdinalIgnoreCase));
    }
}
