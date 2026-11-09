using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Tests.Rendering;

/// <summary>
/// The size classes of the Meitou <c>range</c> switch (docs/formats/foliage.md, "Mesh sizes").
/// </summary>
public class FoliageSizeTests
{
    /// <summary>
    /// Named base-game meshes land in the class the owner's examples put them in: litter and small plants small, junk, boulders and bushes medium,
    /// trees, ruins, wrecks, rocks, pillars, hoodoos and rock stacks large (sizes as the renderer measures them, from the decoded meshes); of the
    /// large ones only the trees under 1000 take the normal impostor distance (<see cref="FoliageSizes.LargeBillboard"/> false).
    /// </summary>
    [Fact]
    [Slow]
    public void Base_game_meshes_fall_into_the_expected_classes()
    {
        var install = GameInstall.Locate();

        Assert.SkipWhen(install is null, "Kenshi install not found");

        var db = GameDatabase.Load(LoadOrder.BaseGame(install!));
        var catalog = FoliageCatalog.Load(db);
        var assets = new AssetLocator(install!);
        const FoliageSizeClass sm = FoliageSizeClass.Small, md = FoliageSizeClass.Medium, lg = FoliageSizeClass.Large;
        var expected = new (string Name, FoliageSizeClass Class, bool LargeBillboard)[]
        {
            ("Human_Skeleton_Part01", sm, false), ("Robotics-Junk12", sm, false), ("Bleached_Skull01", sm, false),
            ("FOLIAGE_Bouldersmall19", sm, false), ("CactiTrumpet01", sm, false), ("Thorny Plant Singles", sm, false),
            ("TechJunk04", md, false), ("TechRustyJunk_05", md, false), ("FOLIAGE_Boulder_GREY-01", md, false),
            ("SageBrush", md, false), ("RuinBlocks_01", md, false), ("CacTreeTu_01", md, false),
            ("HugeRuinWall04", lg, true), ("Crumble-Building_Corner01", lg, true), ("JunkSat01", lg, true),
            ("Rock01 Foliage-Rockstack01", lg, true), ("FlatTop_Hoodoo04", lg, true), ("Barkworm_Pillar01", lg, true),
            ("ResourceRock-IRON01", lg, true),
            ("Foliage_CYPRUS-TYPE", lg, false), ("CraggyTree", lg, false), ("Thin Tree [Wide]", lg, false), ("Roaming_Tree01", lg, false),
            ("FOLIAGE_Plant_Deep-Fir 01", lg, false), ("Foliage_PINE_SCRAGGY01", lg, false), ("Thin Craggy Tree", lg, false),
            ("FOLIAGE_DeadPineType", lg, false), ("Giant_MultiLimbTree", lg, true),
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

    /// <summary>
    /// The renderer's bounding radius (FoliageRenderer.PumpMeshes): half the diagonal of the main and leaves meshes' box, at least 1.
    /// </summary>
    static float Radius(AssetLocator assets, FoliageMesh mesh)
    {
        Model Decode(string name) => Model.Build(OgreMeshReader.ReadFile(assets.Find(name) ?? assets.Find(Path.GetFileName(name.Replace('\\',
                '/')))!), null);
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
