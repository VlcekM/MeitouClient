using System.Numerics;
using Meitou.Content;
using Meitou.Data.Fcs;

namespace Meitou.Tests.Fcs;

public class FcsRoundTripTests
{
    [Theory]
    [InlineData(FcsFileType.V16)]
    [InlineData(FcsFileType.V17)]
    public void Synthetic_file_round_trips(FcsFileType type)
    {
        var file = new FcsFile { FileType = type, Author = "me", Description = "test", HeaderTail = type == FcsFileType.V17 ? [1, 0, 0, 0, 0, 0, 0, 0, 0] : [] };
        file.Dependencies.AddRange(["gamedata.base", "Newwworld.mod"]);
        var record = new FcsRecord { Type = 19, Name = "Line", StringId = "1-test.mod", Flags = 0x11 };
        record.Bools["on"] = true;
        record.Floats["chance"] = 0.5f;
        record.Ints["count"] = -3;
        record.Vector3s["offset"] = new Vector3(1, 2, 3);
        record.Vector4s["colour"] = new Vector4(1, 2, 3, 4);
        record.Strings["text"] = "hello";
        record.Filenames["mesh"] = "a.mesh";
        record.References["conditions"] = [new("2-test.mod", 1, 0, 0)];
        var instance = new FcsInstance { Id = "i1", Target = "3-test.mod", Position = new(1, 2, 3), Rotation = new(0, 0, 0, 1) };
        instance.States.Add("open");
        record.Instances.Add(instance);
        file.Records.Add(record);

        var bytes = Write(file);
        var back = FcsReader.Read(new MemoryStream(bytes));

        Assert.Equal(type, back.FileType);
        Assert.Equal(["gamedata.base", "Newwworld.mod"], back.Dependencies);
        var r = Assert.Single(back.Records);
        Assert.Equal("1-test.mod", r.StringId);
        Assert.Equal(new Vector4(1, 2, 3, 4), r.Vector4s["colour"]);
        Assert.Equal(new FcsReference("2-test.mod", 1, 0, 0), Assert.Single(r.References["conditions"]));
        Assert.Equal("open", Assert.Single(Assert.Single(r.Instances).States));
        Assert.Equal(bytes, Write(back));
    }

    [Fact]
    public void Truncated_file_throws_format_exception()
    {
        var bytes = Write(new FcsFile { Records = { new FcsRecord { StringId = "1-x.mod" } } });
        Assert.Throws<FcsFormatException>(() => FcsReader.Read(new MemoryStream(bytes[..^5])));
    }

    public static TheoryData<string> BaseGameFiles => ["gamedata.base", "Newwworld.mod", "Dialogue.mod", "rebirth.mod"];

    [Theory]
    [MemberData(nameof(BaseGameFiles))]
    public void Base_game_file_round_trips_byte_for_byte(string name)
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");

        var original = File.ReadAllBytes(Path.Combine(install!.DataDirectory, name));
        var file = FcsReader.Read(new MemoryStream(original));

        Assert.Equal(FcsFile.KnownMarker, file.Marker);
        Assert.Equal(original, Write(file));
    }

    static byte[] Write(FcsFile file)
    {
        var ms = new MemoryStream();
        FcsWriter.Write(file, ms);
        return ms.ToArray();
    }
}
