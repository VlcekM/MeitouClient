using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Data;
using Meitou.Data.Fcs;

namespace Meitou.Tests.Characters;

public sealed class CharacterAppearanceSnapshotTests
{
    [Fact]
    public void Character_appearance_snapshot_copies_mutable_collections_without_changing_content_or_visibility()
    {
        var file = new FcsFile();
        file.Records.Add(new FcsRecord { StringId = "test-race", Type = (int)FcsRecordType.RACE, Name = "Test race" });
        var db = new GameDatabase(); db.Apply(file, "test.mod");
        var original = new CharacterAppearance { Race = db.Find("test-race")! };
        var part = new CharacterPart { Record = original.Race, Mesh = "shirt.mesh", Field = "shirt", Mode = AttachMode.SharedSkeleton };
        original.Parts.Add(part); original.PoseWeights["head_size"] = .4f; original.Notes.Add("source");
        original.AttachmentPoints.Add("hand", new("hand", "Bip01 L Hand", Vector3.One, Quaternion.Identity));
        original.BodyLayers.Add(new(original.Race, "shirt.dds", null, null));
        var copy = original.Snapshot();
        Assert.NotSame(original, copy); Assert.Same(original.Race, copy.Race);
        Assert.Same(part, Assert.Single(copy.Parts)); Assert.Equal(original.BodyMesh, copy.BodyMesh);
        Assert.Equal(original.Headless, copy.Headless); Assert.Equal(original.HeadOnly, copy.HeadOnly);
        Assert.Equal(original.HiddenLimbs, copy.HiddenLimbs);
        Assert.NotSame(original.Parts, copy.Parts); Assert.NotSame(original.BodyLayers, copy.BodyLayers);
        Assert.NotSame(original.Lights, copy.Lights); Assert.NotSame(original.AttachmentPoints, copy.AttachmentPoints);
        Assert.NotSame(original.PoseWeights, copy.PoseWeights); Assert.NotSame(original.Notes, copy.Notes);
        original.Parts.Clear(); original.BodyLayers.Clear(); original.AttachmentPoints.Clear(); original.PoseWeights["head_size"] = .9f; original.Notes.Clear();
        Assert.Single(copy.Parts); Assert.Single(copy.BodyLayers); Assert.Single(copy.AttachmentPoints);
        Assert.Equal(.4f, copy.PoseWeights["head_size"]); Assert.Equal("source", Assert.Single(copy.Notes));
        copy.PoseWeights["head_size"] = .1f; Assert.Equal(.9f, original.PoseWeights["head_size"]);
    }
}
