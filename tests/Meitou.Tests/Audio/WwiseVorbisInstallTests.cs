using System.Collections.Concurrent;
using Meitou.Data.Audio;

namespace Meitou.Tests.Audio;

/// <summary>
/// Wwise Vorbis decoding against the install: the codebook library in the exe and every ambience WEM (docs/formats/wwise-bnk.md#wwise-vorbis).
/// </summary>
[Slow]
public class WwiseVorbisInstallTests
{
    static readonly Lazy<WwiseCodebookLibrary?> LibraryCache = new(() =>
        InstallData.Install is { } i && File.Exists(Path.Combine(i.Root, "kenshi_x64.exe"))
            ? WwiseCodebookLibrary.FromExecutable(Path.Combine(i.Root, "kenshi_x64.exe"))
            : null);

    static WwiseCodebookLibrary Library()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");
        Assert.True(LibraryCache.Value is not null, "No codebook library found in kenshi_x64.exe");
        return LibraryCache.Value!;
    }

    [Fact]
    public void Every_ambience_WEM_decodes_to_exactly_its_sample_count()
    {
        var lib = Library();
        var audio = Path.Combine(InstallData.Install!.DataDirectory, "audio");
        var media = Directory.GetFiles(audio, "SFX_Amb_*.bnk").Select(WwiseBank.ReadFile)
            .SelectMany(b => b.Media.Select(m => (Bank: b, Media: m))).ToList();
        Assert.Equal(292, media.Count);

        // Encoded silence of 1, 5 and 10 s: the gaps in the interior ambience's random containers.
        uint[] silent = [0x12C0F32E, 0x0888326E, 0x34EC0D31];
        var failures = new ConcurrentBag<string>();
        Parallel.ForEach(media, item =>
        {
            var wem = WwiseVorbis.Read(item.Bank.MediaBytes(item.Media));
            var pcm = wem.Decode(lib);
            float peak = pcm.Max(MathF.Abs);
            bool peakOk = silent.Contains(item.Media.Id) ? peak == 0 : peak is > 0.001f and <= 2f;
            if (pcm.Length != wem.SampleCount * wem.Channels || !peakOk)
            {
                string? bankName = item.Bank.BankNames.GetValueOrDefault(item.Bank.Id);
                failures.Add($"{bankName} {item.Media.Id:X8}: {pcm.Length / wem.Channels} of {wem.SampleCount} frames, peak {peak}");
            }
        });
        Assert.Empty(failures);
    }

    [Fact]
    public void The_Desert_bed_has_the_header_values_the_doc_gives()
    {
        Library();
        var bank = WwiseBank.ReadFile(Path.Combine(InstallData.Install!.DataDirectory, "audio", "SFX_Amb_Desert.bnk"));
        var wem = WwiseVorbis.Read(bank.MediaBytes(bank.FindMedia(0x110A3A88)!.Value));
        Assert.Equal(2, wem.Channels);
        Assert.Equal(44100, wem.SampleRate);
        Assert.Equal(2280617, wem.SampleCount);
        Assert.Equal(0, wem.SetupOffset);
        Assert.Equal(8, wem.Blocksize0Exponent);
        Assert.Equal(11, wem.Blocksize1Exponent);
    }
}
