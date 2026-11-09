using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Ogre;
using Meitou.Simulation;
using Meitou.Simulation.Population;

namespace Meitou.Tests.Simulation;

/// <summary>
/// How animals look and move (docs/game/animals.md "Appearance" and "Animations"): every animal record gets a look whose mesh, skeleton and fur fit.
/// </summary>
[Slow]
public class AnimalAnimationTests
{
    const string Bonedog = "3979-gamedata.base";

    static readonly Lazy<AnimationLengths> Lengths = new(() => AnimationLengths.Load(InstallData.Install!.Root, InstallData.BaseGame));

    static (GameDatabase Db, string Root) Install()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");

        return (InstallData.BaseGame!, InstallData.Install!.Root);
    }

    static string Path(string root, string fcs) => System.IO.Path.Combine(root,
            fcs.Replace('\\', System.IO.Path.DirectorySeparatorChar).TrimStart('.', System.IO.Path.DirectorySeparatorChar));

    [Fact]
    public void Every_animal_has_a_look_from_its_race_whose_mesh_skeleton_and_fur_fit()
    {
        var (db, root) = Install();
        var animals = db.OfType(FcsRecordType.AnimalCharacter).ToList();

        Assert.NotEmpty(animals);

        foreach (var record in animals)
        {
            var look = CharacterAppearance.BuildAnimal(db, root, record);

            Assert.Same(record, look.Animal);
            Assert.Equal(look.Race.GetPath("male mesh"), look.Race.GetPath("female mesh"));   // the male mesh stands for both

            var mesh = OgreMeshReader.ReadFile(Path(root, look.BodyMesh));
            var skeleton = OgreSkeletonReader.ReadFile(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path(root, look.BodyMesh))!,
                    mesh.SkeletonName!));

            Assert.True(Lengths.Value.HasSpecies(look.Race.StringId), $"{record.Name}: its race's skeleton was read");
            Assert.NotNull(look.BodyTexture);

            // The fur (the race's hair ATTACHMENT) is skinned with the body's bone palette: its own skeleton's handles name the same bones.
            foreach (var fur in look.Parts.Where(p => p.Mode == AttachMode.SharedSkeleton))
            {
                var furMesh = OgreMeshReader.ReadFile(Path(root, fur.Mesh));
                var furSkeleton = OgreSkeletonReader.ReadFile(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path(root, fur.Mesh))!,
                        furMesh.SkeletonName!));

                Assert.All(furSkeleton.Bones, b => Assert.Equal(b.Name, skeleton.FindBone(b.Handle)?.Name));
            }
        }

        // One look per record: the population's source hands every animal of a record the same instance (one asset per species in the renderer).
        var source = new GeneratedAppearances(db, root);

        Assert.Same(source.Create(Bonedog, null, 1), source.Create(Bonedog, null, 2));
    }

    /// <summary>
    /// The Bonedog's race names <c>attachments-bonedog.phs</c>, so its look has the <c>mouth</c> point on <c>Bip01 Ponytail1</c> where a held limb is
    /// drawn (docs/formats/phs.md, #313).
    /// </summary>
    [Fact]
    public void The_bonedogs_look_has_a_mouth_point_on_its_ponytail_bone()
    {
        var (db, root) = Install();
        var look = CharacterAppearance.BuildAnimal(db, root, db.Find(Bonedog)!);

        Assert.Equal("Bip01 Ponytail1", look.AttachmentPoints["mouth"].Bone);
    }
}
