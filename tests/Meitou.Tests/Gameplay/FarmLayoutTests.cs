using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Construction;

namespace Meitou.Tests.Gameplay;

/// <summary>
/// Farm plants (docs/game/buildings-production.md "Farm plants"): the grid and the plant count the simulation sizes its farms by,
/// and the growth look.
/// </summary>
public class FarmLayoutTests
{
    /// <summary>
    /// A crop grows from <c>scale start</c> 0.1 to <c>scale end</c> 1 over growth 0..1, stays full while ripe, is hidden once harvested
    /// and sinks d² x height when dying.
    /// </summary>
    [Fact]
    public void A_plant_scales_with_growth_and_is_hidden_once_harvested()
    {
        var part = new FarmPart(null!, "crop.mesh", null, 0, 100, 0.1f, 1, 0, 0, 0, 0);
        float? Scale(float growth)
        {
            var matrix = FarmLayout.Transform(part, Vector3.Zero, 0.3f, growth, 0, 5);
            return matrix is { } m ? new Vector3(m.M11, m.M12, m.M13).Length() : null;
        }

        Assert.Equal(0.1f, Scale(-0.1f)!.Value, 4);
        Assert.Equal(0.1f, Scale(0)!.Value, 4);
        Assert.Equal(0.55f, Scale(0.5f)!.Value, 4);
        Assert.Equal(1f, Scale(1)!.Value, 4);
        Assert.Equal(1f, Scale(1.5f)!.Value, 4);
        Assert.Null(Scale(-10));
        Assert.Equal(-1.25f, FarmLayout.Transform(part, Vector3.Zero, 0.3f, 1.8f, 0.5f, 5)!.Value.M42, 4);
    }

    /// <summary>
    /// The installed Wheat Farm S has its 25 plants on a 5 x 5 grid at <c>spacing</c> pitch centred on the building,
    /// 20 % of them the first wheat mesh; Wheat Farm M's 50 become a 7 x 7 grid of 49 (across = round(sqrt(50)), 50 / 7 = 7).
    /// </summary>
    [Fact, Slow]
    public void The_installed_wheat_farms_lay_out_their_grids()
    {
        var db = InstallData.BaseGame;
        Assert.SkipWhen(db is null, "Kenshi install not found");
        FarmData Farm(string name) =>
            MachineData.From(db!, db!.Records.Values.First(r => r.Type == FcsRecordType.Building && r.Name == name))!.Farm!;
        var small = Farm("Wheat Farm S");
        Assert.Equal(25, FarmLayout.Count(small));
        Assert.Equal(49, FarmLayout.Count(Farm("Wheat Farm M")));

        var at = new Vector3(1000, 50, 2000);
        var plants = FarmLayout.Plant(small, at, Quaternion.Identity, (x, z) => 77, 1);
        Assert.Equal(25, plants.Positions.Length);
        Assert.Equal(25, plants.Meshes.Length);
        Assert.All(plants.Positions, p => Assert.Equal(77, p.Y));
        // Each cell moves at most jitter x spacing / 2 from its grid point, which lies on (i - 2) x spacing about the building.
        float s = small.Spacing;
        float slack = small.Jitter * s / 2 + 1e-3f;
        Assert.All(plants.Positions, p =>
        {
            float i = (p.X - at.X) / s + 2, k = (p.Z - at.Z) / s + 2;
            Assert.InRange(MathF.Abs(i - MathF.Round(i)) * s, 0, slack);
            Assert.InRange(MathF.Abs(k - MathF.Round(k)) * s, 0, slack);
            Assert.InRange(MathF.Round(i), 0, 4);
            Assert.InRange(MathF.Round(k), 0, 4);
        });
        Assert.Equal([5, 20], FarmLayout.PartCounts(small));
        Assert.Equal(5, plants.Meshes.Count(m => m.Part == 0));
    }
}
