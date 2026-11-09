using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Data.World;
using static Meitou.Tests.Save.SaveSamples;

namespace Meitou.Tests.Save;

/// <summary>
/// Reading saves: the file layer on synthetic data, then the user's saves in place (docs/formats/save.md).
/// </summary>
public class SaveReadTests
{
    [Fact]
    public void Strings_that_are_not_utf8_survive_a_round_trip()
    {
        // The ids of TERRAIN_DECALS instances are raw 4-byte integers in a string slot.
        var file = new SaveFile();
        var decals = new FcsRecord { Type = (int)FcsRecordType.TerrainDecals, StringId = "1--INGAME" };
        decals.Instances.Add(new FcsInstance { Id = LosslessUtf8.GetString([0x81, 0, 0, 0]), Target = "-" });
        decals.Instances.Add(new FcsInstance { Id = LosslessUtf8.GetString([0xC3, 0xA9, 0xFF, 0xFE]), Target = "é" });
        file.Records.Add(decals);
        var bytes = file.ToBytes();
        var back = SaveFile.Read(bytes);

        Assert.Equal(bytes, back.ToBytes());
        Assert.Equal("é\udcff\udcfe", back.Records[0].Instances[1].Id);
    }

    [Fact]
    public void Terrain_decals_unpack_and_pack_to_the_same_bits()
    {
        // The second decal of a real quicksave (negative local X, splash variant 7), and a colour whose bits are a NaN.
        static float F(uint bits) => BitConverter.UInt32BitsToSingle(bits);
        var saved = new FcsInstance
        {
            Id = LosslessUtf8.GetString([2, 0, 0, 0]), Target = "-",
            Position = new Vector3(F(0xFFD8037A), F(0x06760676), F(0x00000837)),
            Rotation = new Quaternion(F(0x00192161), F(0x00000701), 96.79353f, F(0xFF8000FF)),
        };
        var d = SaveDecals.Unpack(saved)!.Value;

        Assert.Equal(new TerrainDecal(0x00192161, TerrainDecalType.Splash, 7, 0x037A, -40, 0x0676, 0x0676, 0x0837, 0xFF8000FF, 96.79353f), d);

        var record = new FcsRecord { Type = (int)FcsRecordType.TerrainDecals, StringId = "1-quick.save-INGAME" };
        SaveDecals.Write(record, [d, d]);
        var file = new SaveFile();
        file.Records.Add(record);
        var back = SaveFile.Read(file.ToBytes()).Records[0];

        Assert.Equal(2, back.Ints["total"]);

        var packed = back.Instances[1];

        Assert.Equal(saved.Id, packed.Id);
        Assert.Equal("-", packed.Target);

        static uint[] Bits(FcsInstance i) => [.. new[] { i.Position.X, i.Position.Y, i.Position.Z, i.Rotation.W, i.Rotation.X, i.Rotation.Y,
                i.Rotation.Z }
            .Select(BitConverter.SingleToUInt32Bits)];

        Assert.Equal(Bits(saved), Bits(packed));
    }

    [Fact]
    public void Surface_blood_marks_survive_a_save_round_trip_beside_the_terrain_decals()
    {
        // A splash on an upper floor and a floor pool, the colour with its top bit set; a second write with fewer marks leaves none of the first's
        // behind.
        SurfaceBloodDecal[] marks =
        [
            new(TerrainDecalType.Splash, 7, new Vector3(-1234.5f, 210.9f, 88.25f), 1.25f, 0.6f, 42.5f, 0xFF8000FF, false),
            new(TerrainDecalType.Pool, 3, new Vector3(10, 20, 30), -0.5f, 1.1f, 99, 0x80102030, true),
        ];
        var record = new FcsRecord { Type = (int)FcsRecordType.TerrainDecals, StringId = "1-quick.save-INGAME" };
        SaveDecals.Write(record, []);
        SaveSurfaceDecals.Write(record, [marks[0], marks[0], marks[0]]);
        SaveSurfaceDecals.Write(record, marks);
        var file = new SaveFile();
        file.Records.Add(record);
        var back = SaveFile.Read(file.ToBytes()).Records[0];

        Assert.Equal(marks, SaveSurfaceDecals.Read(back));
        Assert.Empty(SaveDecals.Read(back));
    }
}
