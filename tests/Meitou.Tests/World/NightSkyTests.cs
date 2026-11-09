using System.Numerics;
using Meitou.Content;
using Meitou.Data.Ogre;
using Meitou.Data.Textures;
using Meitou.Data.World;

namespace Meitou.Tests.World;

/// <summary>
/// The night sky (docs/formats/sky.md "Stars" and "Planets"): the starfield's dome mapping and shift, the two planets (the Meitou night air is in
/// NightAirTests).
/// </summary>
public class NightSkyTests
{
    [Fact]
    public void Starfield_maps_the_dome_round_the_zenith()
    {
        // The zenith is (0.4, 0.4); the horizon a circle of radius 0.4 round it, u along +x and v along +z; 45° up is half way.
        Assert.Equal(new Vector2(0.4f, 0.4f), Starfield.Uv(Vector3.UnitY, 0));
        AssertNear(new Vector2(0.8f, 0.4f), Starfield.Uv(Vector3.UnitX, 0));
        AssertNear(new Vector2(0.4f, 0f), Starfield.Uv(-Vector3.UnitZ, 0));
        AssertNear(new Vector2(0.6f, 0.4f), Starfield.Uv(Vector3.Normalize(new Vector3(1, 1, 0)), 0));
        // 0.05 along both axes per game hour, wrapped: a game day moves it 1.2 texture widths.
        AssertNear(new Vector2(0.45f, 0.45f), Starfield.Uv(Vector3.UnitY, 1));
        Assert.Equal(0.2f, Starfield.Shift(24), 4);
        Assert.Equal(Starfield.Shift(3), Starfield.Shift(23), 4);
    }

    [Fact]
    [Slow]
    public void Planet_mesh_is_a_uv_sphere_of_the_recorded_radius()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var mesh = OgreMeshReader.ReadFile(Path.Combine(install!.DataDirectory, "materials", "planet01.mesh"));
        var data = mesh.SubMeshes[0].VertexData ?? mesh.SharedVertexData!;
        var positions = data.ReadPositions()!;
        var uv = data.ReadFloats(data.Find(OgreVertexSemantic.TextureCoordinates)!);
        for (int i = 0; i < positions.Length; i++)
        {
            Assert.Equal(SkyPlanet.MeshRadius, positions[i].Length(), 3);
            var n = Vector3.Normalize(positions[i]);
            var expected = SkyPlanet.Uv(n);
            Assert.Equal(expected.Y, uv[2 * i + 1], 3);
            if (MathF.Abs(n.Y) > 0.999f) continue;   // the poles' u is arbitrary
            float du = uv[2 * i] - expected.X;
            Assert.True(MathF.Abs(du - MathF.Round(du)) < 2e-3f, $"vertex {i}: u {uv[2 * i]} against {expected.X}");
        }
        // The textures the materials name (materials/forward/moon.material): BC1 with their mips.
        foreach (var (planet, width) in new[] { (SkyPlanet.All[0], 4096), (SkyPlanet.All[1], 2048) })
        {
            var dds = DdsReader.ReadFile(Path.Combine(install.DataDirectory, "materials", planet.Texture));
            Assert.Equal((width, width / 2, DdsFormat.Bc1), (dds.Width, dds.Height, dds.Format));
        }
    }

    static void AssertNear(Vector2 expected, Vector2 actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
    }
}
