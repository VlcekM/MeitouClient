using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Ogre;

namespace Meitou.Tests.Characters;

public class CharacterAppearanceTests
{
    static GameDatabase? s_cached;
    static readonly Lock Gate = new();

    static GameDatabase Database(GameInstall install)
    {
        lock (Gate) return s_cached ??= GameDatabase.Load(LoadOrder.BaseGame(install));
    }

    static string DataFile(GameInstall install, params string[] parts) => Path.Combine([install.DataDirectory, .. parts]);

    [Fact]
    [Slow]
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
            Assert.True(
                Vector3.Distance(position, actor.BonePosition) < 0.01f,
                $"{actor.Name}: {position} vs {actor.BonePosition}");
            Assert.True(
                MathF.Abs(Quaternion.Dot(rotation, actor.BoneRotation)) > 0.9999f,
                $"{actor.Name}: {rotation} vs {actor.BoneRotation}");
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
    [Slow]
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
        // Wooden Sandals (0, 400): quantity 0 never spawns.
        Assert.Contains(worn, p => p.Slot == AttachSlot.Boots && p.Record.Name == "Samurai Boots");

        // The weapon in hand comes as its sheathed mesh and its drawn bare blade and sheath;
        // the renderer shows one set by the fight.
        var weapon = Assert.Single(c.Parts, p => p.Mode == AttachMode.Bone && p.Shown == WeaponShown.BackSheathed);

        // Horse Chopper (1, 100, 0): val1 ≠ 0 is the back pool.
        Assert.Equal("back", weapon.Point);
        Assert.Equal(["hands", "back"], c.Parts.Where(p => p.Shown == WeaponShown.BackDrawn).Select(p => p.Point));
        Assert.True(c.AttachmentPoints.ContainsKey("hip"));
        Assert.Equal("Bip01 Prop2", c.AttachmentPoints["hands"].Bone);
    }

    /// <summary>
    /// The editor's EXPORT writes a body file in the game's layout that IMPORT reads back into another character's draft unchanged
    /// (docs/characters.md "Import and export").
    /// </summary>
    [Fact]
    [Slow]
    public void Exported_look_imports_back_unchanged()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var db = Database(install);
        var ruka = AppearanceFile.Read(DataFile(install, "character", "bodies", "Ruka.bod2")).Record;
        var source = new CharacterDraft(db, install.Root, ruka, "Ruka");
        Assert.True(source.Import(ruka));
        var stream = new MemoryStream();
        FcsWriter.Write(source.Export(), stream);
        stream.Position = 0;
        var file = FcsReader.Read(stream);
        Assert.Equal(FcsFileType.V15, file.FileType);
        var exported = Assert.Single(file.Records);
        Assert.Equal((FcsRecordType.CharacterAppearance, 1, "1--INGAME"), (exported.RecordType, exported.Id, exported.StringId));

        var other = AppearanceFile.Read(DataFile(install, "character", "bodies", "Beep.bod2")).Record;
        var target = new CharacterDraft(db, install.Root, other, "Beep");
        Assert.True(target.Import(exported));
        Assert.Equal(source.Race.StringId, target.Race.StringId);
        Assert.Equal(source.Female, target.Female);
        Assert.Equal(source.Record.Floats, target.Record.Floats);
        Assert.Equal(source.Record.Strings, target.Record.Strings);
        Assert.Equal(source.Record.Vector3S, target.Record.Vector3S);
        Assert.Equal(other.StringId, target.Record.StringId);
    }

    /// <summary>
    /// A prosthetic built from a <see cref="LoadoutLimb"/> shows its item's colour, else the wearer's scheme, like clothing
    /// (docs/characters.md "Clothing colours", #279).
    /// </summary>
    [Fact]
    [Slow]
    public void A_prosthetic_limb_takes_its_item_colour_else_the_wearers_scheme()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var db = Database(install);
        var faction = db.OfType(FcsRecordType.Faction).First(f => Data.Gameplay.ItemColours.Of(db, f) is not null);
        var scheme = Data.Gameplay.ItemColours.Of(db, faction)!;
        var own = db.OfType(FcsRecordType.ColorData).First(c => c.StringId != scheme.StringId);
        var prosthetic = db.OfType(FcsRecordType.LimbReplacement)
            .First(l => Data.Gameplay.ItemColours.Of(db, l) is null && l.GetPath("mesh").Length > 0);
        var race = db.OfType(FcsRecordType.Race).First(r => r.Name == "Greenlander");
        var loadout = CharacterGenerator.Generate(db, install.Root, race, 1, female: false, faction);
        loadout.Limbs[0] = new LoadoutLimb(2, prosthetic, 0, own);
        loadout.Limbs[1] = new LoadoutLimb(2, prosthetic);

        var look = CharacterAppearance.Build(db, install.Root, race.StringId, new CharacterOptions { Loadout = loadout });

        var limbs = look.Parts.Where(p => p.Record == prosthetic).ToList();
        Assert.Equal(2, limbs.Count);
        Assert.Equal(own.StringId, limbs[0].Colour?.StringId);
        Assert.Equal(scheme.StringId, limbs[1].Colour?.StringId);
    }
}
