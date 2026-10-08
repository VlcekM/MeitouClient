using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Data.World;
using Meitou.Rendering;

namespace Meitou.Tests.Rendering;

/// <summary>The size classes of the Meitou <c>range</c> switch (docs/formats/foliage.md, "Mesh sizes").</summary>
public class FoliageSizeTests
{
    [Fact]
    public void Thresholds_split_the_sizes()
    {
        Assert.Equal(FoliageSizeClass.Small, FoliageSizes.Classify(0));
        Assert.Equal(FoliageSizeClass.Small, FoliageSizes.Classify(39.9f));
        Assert.Equal(FoliageSizeClass.Medium, FoliageSizes.Classify(FoliageSizes.MediumFrom));
        Assert.Equal(FoliageSizeClass.Medium, FoliageSizes.Classify(124.9f));
        Assert.Equal(FoliageSizeClass.Large, FoliageSizes.Classify(FoliageSizes.LargeFrom));
        Assert.Equal(FoliageSizeClass.Large, FoliageSizes.Classify(5000));
        // The largest small mesh at the end of the small range looks as big as the largest medium one at the end of the medium range.
        Assert.Equal(FoliageSizes.MediumFrom / FoliageSizes.ThresholdSmallRange, FoliageSizes.LargeFrom / FoliageSizes.ThresholdMediumRange, 3);
    }

    /// <summary>Named base-game meshes land in the class the owner's examples put them in: litter and small plants small, junk, boulders
    /// and bushes medium, trees, ruins, wrecks, rocks, pillars, hoodoos and rock stacks large (sizes as the renderer measures them, from the decoded meshes);
    /// of the large ones only the trees under 1000 take the normal impostor distance (<see cref="FoliageSizes.LargeBillboard"/> false).</summary>
    [Fact]
    [Slow]
    public void Base_game_meshes_fall_into_the_expected_classes()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var db = GameDatabase.Load(LoadOrder.BaseGame(install!));
        var catalog = FoliageCatalog.Load(db);
        var assets = new AssetLocator(install!);
        const FoliageSizeClass S = FoliageSizeClass.Small, M = FoliageSizeClass.Medium, L = FoliageSizeClass.Large;
        var expected = new (string Name, FoliageSizeClass Class, bool LargeBillboard)[]
        {
            ("Human_Skeleton_Part01", S, false), ("Robotics-Junk12", S, false), ("Bleached_Skull01", S, false),
            ("FOLIAGE_Bouldersmall19", S, false), ("CactiTrumpet01", S, false), ("Thorny Plant Singles", S, false),
            ("TechJunk04", M, false), ("TechRustyJunk_05", M, false), ("FOLIAGE_Boulder_GREY-01", M, false),
            ("SageBrush", M, false), ("RuinBlocks_01", M, false), ("CacTreeTu_01", M, false),
            ("HugeRuinWall04", L, true), ("Crumble-Building_Corner01", L, true), ("JunkSat01", L, true),
            ("Rock01 Foliage-Rockstack01", L, true), ("FlatTop_Hoodoo04", L, true), ("Barkworm_Pillar01", L, true), ("ResourceRock-IRON01", L, true),
            ("Foliage_CYPRUS-TYPE", L, false), ("CraggyTree", L, false), ("Thin Tree [Wide]", L, false), ("Roaming_Tree01", L, false),
            ("FOLIAGE_Plant_Deep-Fir 01", L, false), ("Foliage_PINE_SCRAGGY01", L, false), ("Thin Craggy Tree", L, false),
            ("FOLIAGE_DeadPineType", L, false), ("Giant_MultiLimbTree", L, true),
        };
        foreach (var (name, cls, large) in expected)
        {
            var mesh = catalog.Meshes.Values.FirstOrDefault(m => m.Name == name);
            Assert.SkipWhen(mesh is null, $"{name} is not in this game's foliage (a mod or another version)");
            float radius = Radius(assets, mesh!);
            float size = FoliageSizes.Size(radius, mesh!);
            var got = FoliageSizes.Classify(size);
            Assert.True(got == cls, $"{name}: size {size:0} is {got}, expected {cls}");
            Assert.True(FoliageSizes.LargeBillboard(radius, mesh!) == large, $"{name}: large billboard should be {large}");
        }
    }

    [Fact]
    public void Vegetation_is_told_by_the_games_asset_folder()
    {
        Assert.True(FoliageSizes.IsVegetation(@".\data\newland\Assets\Plants\DeepFir01.mesh"));
        Assert.True(FoliageSizes.IsVegetation(@".\data\foliage\Trees\ThinTree01_Trunk.mesh"));
        Assert.True(FoliageSizes.IsVegetation("./DATA/newland/assets/plants/x.mesh"));
        Assert.False(FoliageSizes.IsVegetation(@".\data\newland\Assets\Rocks\FlatTop_Hoodoo01.mesh"));
        Assert.False(FoliageSizes.IsVegetation(@".\data\newland\Assets\Things\RUIN-RefineryHead04.mesh"));
        Assert.False(FoliageSizes.IsVegetation(@".\data\newland\Assets\Buildings\HugeRuinWall01.mesh"));
    }

    /// <summary>The renderer's bounding radius (FoliageRenderer.PumpMeshes): half the diagonal of the main and leaves meshes' box, at least 1.</summary>
    static float Radius(AssetLocator assets, FoliageMesh mesh)
    {
        Model Decode(string name) => Model.Build(OgreMeshReader.ReadFile(assets.Find(name) ?? assets.Find(Path.GetFileName(name.Replace('\\', '/')))!), null);
        var main = Decode(mesh.MeshPath);
        var (min, max) = (main.Min, main.Max);
        if (mesh.LeavesMesh is { } leavesName)
        {
            var leaves = Decode(leavesName);
            (min, max) = (Vector3.Min(min, leaves.Min), Vector3.Max(max, leaves.Max));
        }
        return Math.Max((max - min).Length() / 2, 1);
    }
}
