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
    /// and bushes medium, trees (any size under 1000) medium, ruins, wrecks, rocks, pillars, hoodoos and rock stacks large (sizes as the renderer measures them, from the decoded meshes).</summary>
    [Fact]
    [Slow]
    public void Base_game_meshes_fall_into_the_expected_classes()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var db = GameDatabase.Load(LoadOrder.BaseGame(install!));
        var catalog = FoliageCatalog.Load(db);
        var assets = new AssetLocator(install!);
        var expected = new (string Name, FoliageSizeClass Class)[]
        {
            ("Human_Skeleton_Part01", FoliageSizeClass.Small), ("Robotics-Junk12", FoliageSizeClass.Small), ("Bleached_Skull01", FoliageSizeClass.Small),
            ("FOLIAGE_Bouldersmall19", FoliageSizeClass.Small), ("CactiTrumpet01", FoliageSizeClass.Small), ("Thorny Plant Singles", FoliageSizeClass.Small),
            ("TechJunk04", FoliageSizeClass.Medium), ("TechRustyJunk_05", FoliageSizeClass.Medium), ("FOLIAGE_Boulder_GREY-01", FoliageSizeClass.Medium),
            ("SageBrush", FoliageSizeClass.Medium), ("RuinBlocks_01", FoliageSizeClass.Medium), ("CacTreeTu_01", FoliageSizeClass.Medium),
            ("HugeRuinWall04", FoliageSizeClass.Large), ("Crumble-Building_Corner01", FoliageSizeClass.Large), ("JunkSat01", FoliageSizeClass.Large),
            ("Rock01 Foliage-Rockstack01", FoliageSizeClass.Large), ("Foliage_CYPRUS-TYPE", FoliageSizeClass.Medium), ("CraggyTree", FoliageSizeClass.Medium),
            ("Thin Tree [Wide]", FoliageSizeClass.Medium), ("FlatTop_Hoodoo04", FoliageSizeClass.Large),
            ("FOLIAGE_Plant_Deep-Fir 01", FoliageSizeClass.Medium), ("Foliage_PINE_SCRAGGY01", FoliageSizeClass.Medium), ("Thin Craggy Tree", FoliageSizeClass.Medium),
            ("FOLIAGE_DeadPineType", FoliageSizeClass.Medium), ("Roaming_Tree01", FoliageSizeClass.Medium), ("Barkworm_Pillar01", FoliageSizeClass.Large),
            ("Crumble-Building_Corner01", FoliageSizeClass.Large), ("ResourceRock-IRON01", FoliageSizeClass.Large), ("Giant_MultiLimbTree", FoliageSizeClass.Large),
        };
        foreach (var (name, cls) in expected)
        {
            var mesh = catalog.Meshes.Values.FirstOrDefault(m => m.Name == name);
            Assert.SkipWhen(mesh is null, $"{name} is not in this game's foliage (a mod or another version)");
            float radius = Radius(assets, mesh!);
            var got = FoliageSizes.Classify(radius, mesh!);
            Assert.True(got == cls, $"{name}: size {FoliageSizes.Size(radius, mesh!):0} is {got}, expected {cls}");
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
