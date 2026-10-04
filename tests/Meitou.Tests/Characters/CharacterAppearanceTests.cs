using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Ogre;

namespace Meitou.Tests.Characters;

public class CharacterAppearanceTests
{
    static GameDatabase? cached;
    static readonly Lock gate = new();

    static GameDatabase Database(GameInstall install)
    {
        lock (gate) return cached ??= GameDatabase.Load(LoadOrder.BaseGame(install));
    }

    static string DataFile(GameInstall install, params string[] parts) => Path.Combine([install.DataDirectory, .. parts]);

    [Fact]
    public void Hsb_matches_ogre_rules()
    {
        Assert.Equal(Vector3.Zero, CharacterAppearance.FromHsb(0.47f, 0.02f, -0.35f)); // brightness clamps to 0
        Assert.Equal(new Vector3(0.5f), CharacterAppearance.FromHsb(0.3f, 0, 0.5f));
        var red = CharacterAppearance.FromHsb(1.0f, 1, 1); // hue wraps to 0
        Assert.Equal(new Vector3(1, 0, 0), red);
        var green = CharacterAppearance.FromHsb(1 / 3f, 1, 1);
        Assert.True(Vector3.Distance(green, new Vector3(0, 1, 0)) < 1e-5f);
    }

    [Fact]
    public void Quaternion_from_columns_maps_axes()
    {
        // Columns (0,1,0), (-1,0,0), (0,0,1): 90 degrees about Z, so X goes to Y.
        var q = PhysicsAttachmentFile.FromColumns(new Vector3(0, 1, 0), new Vector3(-1, 0, 0), new Vector3(0, 0, 1));
        Assert.True(Vector3.Distance(Vector3.Transform(Vector3.UnitX, q), Vector3.UnitY) < 1e-5f);
    }

    [Fact]
    public void Weapon_attachment_points_match_the_male_skeleton()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var phs = PhysicsAttachmentFile.ReadFile(DataFile(install, "ragdoll", "attachments-weapons.phs"));
        Assert.Equal(1.4f, phs.Version, 1e-5f);
        Assert.Equal(["hip", "back", "back2"], phs.Actors.Select(a => a.Name));
        Assert.Equal(["Bip01 Pelvis", "Bip01 Spine2", "Bip01 Spine2"], phs.Actors.Select(a => a.Bone));
        Assert.All(phs.Actors, a => Assert.Single(a.Points));
        Assert.EndsWith("katana04F.mesh", phs.Actors[0].Points[0].Mesh);

        // The bone transform stored with each actor is the skeleton's binding pose of that bone (model space).
        var skeleton = OgreSkeletonReader.ReadFile(DataFile(install, "character", "meshes", "male_skeleton", "male_skeleton.skeleton"));
        foreach (var actor in phs.Actors)
        {
            var (position, rotation) = Derived(skeleton, skeleton.Bones.Single(b => b.Name == actor.Bone).Handle);
            Assert.True(Vector3.Distance(position, actor.BonePosition) < 0.01f, $"{actor.Name}: {position} vs {actor.BonePosition}");
            Assert.True(MathF.Abs(Quaternion.Dot(rotation, actor.BoneRotation)) > 0.9999f, $"{actor.Name}: {rotation} vs {actor.BoneRotation}");
        }
        // The hip point sits beside the pelvis at hip height.
        Assert.InRange(phs.Actors[0].Points[0].Position.Y, 10f, 11f);
    }

    static (Vector3, Quaternion) Derived(OgreSkeleton skeleton, ushort handle)
    {
        var bone = skeleton.FindBone(handle)!;
        if (bone.Parent is not { } parent) return (bone.Position, bone.Orientation);
        var (pp, pr) = Derived(skeleton, parent);
        return (pp + Vector3.Transform(bone.Position, pr), pr * bone.Orientation);
    }

    [Fact]
    public void Every_attachment_phs_reads_or_is_refused()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        int read = 0;
        foreach (var file in Directory.EnumerateFiles(install.DataDirectory, "*.phs", SearchOption.AllDirectories))
        {
            try
            {
                PhysicsAttachmentFile.ReadFile(file);
                read++;
            }
            catch (NotSupportedException) { }
            catch (FormatException) { }
        }
        Assert.True(read > 0);
    }

    [Fact]
    public void Ruka_body_file_reads()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var body = AppearanceFile.Read(DataFile(install, "character", "bodies", "Ruka.bod2"));
        Assert.True(body.Female);
        var db = Database(install);
        Assert.Equal(FcsRecordType.RACE, db.Find(body.RaceId!)!.Type);
        Assert.Equal(FcsRecordType.HEAD, db.Find(body.HeadId!)!.Type);
        Assert.Equal(FcsRecordType.ATTACHMENT, db.Find(body.HairId!)!.Type);
        Assert.NotNull(body.SkinTone);
        Assert.InRange(body.SkinTone!.Value.X, 0.9f, 1f);
        Assert.Equal(16f, body.Get("Age"));
    }

    [Fact]
    public void Modern_body_files_read_and_legacy_ones_are_counted()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var db = Database(install);
        var bodies = db.OfType(FcsRecordType.CHARACTER).Select(c => c.GetPath("body")).Where(b => b.Length > 0).Distinct().ToList();
        Assert.NotEmpty(bodies);
        int missing = 0, old = 0, read = 0;
        foreach (var body in bodies)
        {
            var path = Path.Combine(install.Root, body.TrimStart('.', '\\', '/'));
            if (!File.Exists(path)) { missing++; continue; }
            int type = BitConverter.ToInt32(File.ReadAllBytes(path), 0);
            if (type < 15) { old++; continue; } // legacy FCS layouts (types 5-13), not read
            Assert.NotNull(AppearanceFile.Read(path).Record);
            read++;
        }
        Assert.True(read > old + missing, $"{read} read, {old} legacy, {missing} missing");
    }

    [Fact]
    public void Dust_bandit_assembles()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var c = CharacterAppearance.Build(Database(install), install.Root, "Dust Bandit");
        Assert.Equal("Greenlander", c.Race.Name);
        Assert.False(c.Female);
        Assert.EndsWith("human_male.mesh", c.BodyMesh);
        Assert.NotNull(c.Head);
        var worn = c.Parts.Where(p => p.Mode == AttachMode.SharedSkeleton).ToList();
        Assert.Contains(worn, p => p.Slot == AttachSlot.Hat);
        Assert.Contains(worn, p => p.Slot == AttachSlot.Boots && p.Record.Name == "Samurai Boots"); // Wooden Sandals (0, 400): quantity 0 never spawns
        var weapon = Assert.Single(c.Parts, p => p.Mode == AttachMode.Bone);
        Assert.Equal("back", weapon.Point); // Horse Chopper (1, 100, 0): val1 ≠ 0 is the back pool
        Assert.True(c.AttachmentPoints.ContainsKey("hip"));
        Assert.Equal("Bip01 Prop2", c.AttachmentPoints["hands"].Bone);
    }

    [Fact]
    public void Drawn_weapon_uses_bare_sword_and_sheath()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var c = CharacterAppearance.Build(Database(install), install.Root, "Greenlander", new CharacterOptions { Equip = ["Katana"], WeaponDrawn = true });
        Assert.Contains(c.Parts, p => p.Field == "bare sword" && p.Point == "hands");
        Assert.Contains(c.Parts, p => p.Field == "sheath" && p.Point == "hip");
    }

    [Fact]
    public void Walk_records_split_the_body_between_lower_and_upper()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var db = Database(install);
        var lower = AnimationMask.Find(db, "walk lower");
        var upper = AnimationMask.Find(db, "walk upper");
        Assert.Equal("lower", lower.Layer);
        Assert.Equal("upper", upper.Layer);
        Assert.Contains("Bip01 L Thigh", upper.DeletedBones);
        Assert.DoesNotContain("Bip01 L Thigh", lower.DeletedBones);
        Assert.Contains("Bip01 Head", lower.DeletedBones);
        Assert.DoesNotContain("Bip01 Head", upper.DeletedBones);
        // "run upper stealth" plays the Ogre animation "ninjarun".
        Assert.Equal("ninjarun", AnimationMask.Find(db, "run upper stealth").AnimationName);
        Assert.True(AnimationMask.Plain("postures").DeletedBones.SetEquals(["Bip01 L Hand", "Bip01 R Hand", "Bip01 Prop1", "Bip01 Prop2"]));
    }

    [Fact]
    public void Body_mesh_poses_are_named_morphs()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var mesh = OgreMeshReader.ReadFile(DataFile(install, "character", "meshes", "human", "human_male.mesh"));
        Assert.Equal(23, mesh.Poses.Count);
        Assert.Contains(mesh.Poses, p => p.Name == "wide_cheekbones");
        Assert.All(mesh.Poses, p => Assert.Equal(1, p.Target));
        int vertices = (int)mesh.SubMeshes[0].VertexData!.VertexCount;
        Assert.All(mesh.Poses, p => Assert.All(p.Vertices, v => Assert.True(v.Index < vertices)));
    }

    [Fact]
    public void Beep_uses_her_body_file()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var c = CharacterAppearance.Build(Database(install), install.Root, "Beep");
        Assert.NotNull(c.Body);
        Assert.Equal("Hive Worker Drone", c.Race.Name);
        Assert.Contains(c.Parts, p => p.Record.Name == "Rag Loincloth");
        Assert.NotEmpty(c.PoseWeights);
    }
}
