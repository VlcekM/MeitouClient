using System.Buffers.Binary;
using System.Text;
using Meitou.Data.Audio;

namespace Meitou.Tests.Audio;

/// <summary>
/// The Wwise bank reader on a hand-built bank and the name hash (docs/formats/wwise-bnk.md).
/// </summary>
public class WwiseBankTests
{
    [Theory]
    [InlineData("SFX_Amb_Desert", 0xA467AF85u)]
    [InlineData("Enter_Desert_Day", 0x87CDD31Cu)]
    public void Ids_are_fnv1_of_the_lowercased_name(string name, uint id)
    {
        Assert.Equal(id, WwiseHash.Id(name));
        Assert.Equal(id, WwiseHash.Id(name.ToUpperInvariant()));
    }

    static byte[] Section(string tag, byte[] body) => [.. Encoding.ASCII.GetBytes(tag), .. BitConverter.GetBytes(body.Length), .. body];

    static byte[] HircObject(WwiseObjectType type, uint id, byte[] body) =>
        [(byte)type, .. BitConverter.GetBytes(body.Length + 4), .. BitConverter.GetBytes(id), .. body];

    static byte[] U32(uint v) => BitConverter.GetBytes(v);

    [Fact]
    public void A_built_bank_reads_back_its_header_media_names_and_objects()
    {
        byte[] wem = [.. "RIFF"u8, 1, 2, 3, 4];
        byte[] play = [0x03, 0x04, .. U32(0x30), 0, 1, 0x0F, .. U32(500), 0, 4, .. U32(0xB0)];
        byte[] sound =
        [
            .. U32(0x00040001), 0, .. U32(0x77), .. U32(0xB0), .. U32(0), .. U32((uint)wem.Length), 0, // source; offset patched below
            0, 0, 0, .. U32(0), .. U32(0x40), 0, 1, 0x3A, .. U32(0), 0, // fx, unknown byte, bus, parent, priority, loop forever, no ranges
            0xC0, 0, 0, 1, 0, 0, 0, 0, .. U32(0), 0, 0, // positioning, aux, advanced settings, no states, no RTPCs
        ];
        byte[] mixer = [0, 0, 0, .. U32(0), .. U32(0), 0, 0, 0, 0xC0, 0, 0, 1, 0, 0, 0, 0, .. U32(0), 0, 0, .. U32(1), .. U32(0x30)];
        byte[] Build(uint wemOffset)
        {
            var s = (byte[])sound.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(13), wemOffset);
            byte[] hirc = [.. U32(4), .. HircObject(WwiseObjectType.Event, 0x10, [.. U32(1), .. U32(0x20)]),
                .. HircObject(WwiseObjectType.Action, 0x20, play), .. HircObject(WwiseObjectType.Sound, 0x30, s),
                .. HircObject(WwiseObjectType.ActorMixer, 0x40, mixer)];
            byte[] stid = [.. U32(1), .. U32(1), .. U32(0xB0), 4, .. "Test"u8];
            return
            [
                .. Section("BKHD", [.. U32(112), .. U32(0xB0), .. U32(0), .. U32(0)]),
                .. Section("DIDX", [.. U32(0x77), .. U32(0), .. U32((uint)wem.Length)]),
                .. Section("DATA", wem),
                .. Section("HIRC", hirc),
                .. Section("STID", stid),
            ];
        }

        // The sound stores the WEM's offset in the file: BKHD (8 + 16) + DIDX (8 + 12) + the DATA header (8).
        var bank = WwiseBank.Read(Build(24 + 20 + 8));

        Assert.Equal(112u, bank.Version);
        Assert.Equal(0xB0u, bank.Id);
        Assert.Equal("Test", bank.BankNames[0xB0]);
        var media = Assert.Single(bank.Media);
        Assert.Equal(wem, bank.MediaBytes(media).ToArray());
        Assert.Equal(4, bank.Objects.Count);
        Assert.Empty(bank.Objects.OfType<WwiseRawObject>());

        var e = bank.Find<WwiseEvent>(0x10)!;
        var action = bank.Find<WwiseAction>(e.Actions[0])!;
        Assert.Equal(WwiseActionKind.Play, action.Kind);
        Assert.Equal(3, action.Scope);
        Assert.Equal(500, action.Properties.Int(WwisePropertyId.FadeTime));
        Assert.Equal(0xB0u, action.BankId);
        var s = bank.Find<WwiseSound>(action.Target)!;
        Assert.True(s.Source.IsVorbis);
        Assert.Equal(media.Offset, (int)s.Source.Offset);
        Assert.Equal(0x40u, s.Node.ParentId);
        Assert.Equal(0, s.Node.Properties.Int(WwisePropertyId.Loop, -1));
        Assert.Equal([0x30u], bank.Find<WwiseActorMixer>(0x40)!.Children);
    }

    [Fact]
    public void An_object_that_does_not_use_exactly_its_size_is_kept_raw_with_the_reason()
    {
        byte[] hirc = [.. U32(1), .. HircObject(WwiseObjectType.Event, 0x10, [.. U32(1), .. U32(0x20), 0xAA])];
        byte[] file = [.. Section("BKHD", [.. U32(112), .. U32(1), .. U32(0), .. U32(0)]), .. Section("HIRC", hirc)];
        var raw = Assert.IsType<WwiseRawObject>(Assert.Single(WwiseBank.Read(file).Objects));
        Assert.Equal("1 bytes left over", raw.Error);
    }
}
