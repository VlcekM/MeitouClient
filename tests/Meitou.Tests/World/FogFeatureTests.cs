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
            (Vector3.Normalize(new Vector3(1, 0, 1)), -2000), (-Vector3.UnitY, -1000),   // a cut far outside the box; the floor at y = -1000
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
        Assert.False(block.Contains(new Vector3(500, -1200, 500)));  // below the floor
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
