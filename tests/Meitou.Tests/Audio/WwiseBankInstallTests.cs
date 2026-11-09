using Meitou.Data.Audio;

namespace Meitou.Tests.Audio;

/// <summary>
/// The Wwise bank reader against the install's 24 banks in data/audio (docs/formats/wwise-bnk.md).
/// </summary>
[Slow]
public class WwiseBankInstallTests
{
    static readonly string[] Biomes = ["Ashlands", "Boneyard", "Canyons", "Coast", "Desert", "Forest", "Jungle", "Plains", "Swamplands", "Tarsands"];

    static readonly Lazy<List<(string Name, WwiseBank Bank)>?> BankCache = new(() => InstallData.Install is { } install
        ? Directory.GetFiles(Path.Combine(install.DataDirectory, "audio"), "*.bnk")
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(f => (Path.GetFileNameWithoutExtension(f), WwiseBank.ReadFile(f)))
            .ToList()
        : null);

    static List<(string Name, WwiseBank Bank)> Banks()
    {
        Assert.SkipWhen(BankCache.Value is null, "Kenshi install not found");
        return BankCache.Value!;
    }

    static WwiseBank Bank(string name) => Banks().Single(b => b.Name == name).Bank;

    [Fact]
    public void Every_bank_is_version_112_except_SFX_Bank_and_is_named_by_the_hash_of_its_file_name()
    {
        var all = Banks();
        Assert.Equal(24, all.Count);
        foreach (var (name, bank) in all)
        {
            Assert.Equal(name == "SFX_Bank" ? 88u : 112u, bank.Version);
            Assert.Equal(WwiseHash.Id(name), bank.Id);
            Assert.Equal(0u, bank.LanguageId);
            // STID lists the bank itself, plus banks its events play from (MT_Music_Bank also names SFX_General_Bank).
            if (bank.BankNames.Count > 0) Assert.Equal(name, bank.BankNames[bank.Id], ignoreCase: true);
            foreach (var (id, other) in bank.BankNames) Assert.Equal(WwiseHash.Id(other), id);
        }
    }

    [Fact]
    public void Every_version_112_object_of_a_decoded_type_uses_exactly_its_size()
    {
        WwiseObjectType[] decoded = [WwiseObjectType.Event, WwiseObjectType.Action, WwiseObjectType.Sound, WwiseObjectType.ActorMixer,
            WwiseObjectType.RandomSequenceContainer, WwiseObjectType.SwitchContainer, WwiseObjectType.LayerContainer,
            WwiseObjectType.MusicSegment, WwiseObjectType.MusicTrack, WwiseObjectType.MusicSwitch, WwiseObjectType.MusicRandomSequence,
            WwiseObjectType.Attenuation, WwiseObjectType.FxShareSet, WwiseObjectType.FxCustom, WwiseObjectType.AuxBus];
        int count = 0;
        foreach (var (name, bank) in Banks().Where(b => b.Bank.Version == WwiseBank.DecodedVersion))
        {
            foreach (var o in bank.Objects)
            {
                if (o is WwiseRawObject raw)
                {
                    Assert.True(raw.Error is null, $"{name} {raw.Type} {raw.Id:X8}: {raw.Error}");
                    Assert.DoesNotContain(raw.Type, decoded);
                }
                else count++;

                if (o is not WwiseSound s) continue;

                // Every sound plays a WEM embedded in its own bank at the stored file offset.
                Assert.Equal(0, s.Source.StreamType);
                if (!s.Source.IsVorbis)
                {
                    Assert.Equal(0x00650002u, s.Source.PluginId); // the silence source: no media
                    continue;
                }

                Assert.Equal(bank.Id, s.Source.FileId);
                var media = bank.FindMedia(s.Source.SourceId);
                Assert.True(media is not null, $"{name} sound {s.Id:X8} has no media {s.Source.SourceId:X8}");
                Assert.Equal(media.Value.Offset, (int)s.Source.Offset);
                Assert.Equal(media.Value.Length, (int)s.Source.Size);
            }
        }

        Assert.Equal(3260, count); // with the buses, the 687 music objects, attenuations, effects and aux buses
    }

    [Fact]
    public void Each_biome_bank_has_day_and_night_enter_events_and_a_leave_event()
    {
        foreach (var biome in Biomes)
        {
            var bank = Bank($"SFX_Amb_{biome}");
            var events = bank.Objects.OfType<WwiseEvent>().Select(e => e.Id).ToHashSet();
            var expected = new HashSet<uint> { WwiseHash.Id($"Enter_{biome}_Day"), WwiseHash.Id($"Enter_{biome}_Night"),
                WwiseHash.Id($"Leave_{biome}") };

            Assert.Equal(expected, events);
            foreach (var time in new[] { "Day", "Night" })
            {
                var plays = bank.Find<WwiseEvent>(WwiseHash.Id($"Enter_{biome}_{time}"))!.Actions
                    .Select(a => bank.Find<WwiseAction>(a)!)
                    .Where(a => a.Kind == WwiseActionKind.Play)
                    .ToList();
                Assert.NotEmpty(plays);
                Assert.All(plays, p => Assert.Equal(bank.Id, p.BankId));
            }
        }

        var indoors = Bank("SFX_Amb_Indoors_Bank").Objects.OfType<WwiseEvent>().Select(e => e.Id).ToHashSet();
        var indoorEvents = new HashSet<uint> { WwiseHash.Id("Enter_Building"), WwiseHash.Id("Enter_Building_Big"), WwiseHash.Id("Leave_Building") };
        Assert.Equal(indoorEvents, indoors);

        // The weather bank has the rain and wind start events but no wind stop.
        var weather = Bank("SFX_Amb_Weather_Bank").Objects.OfType<WwiseEvent>().Select(e => e.Id).ToHashSet();
        Assert.Contains(WwiseHash.Id("Weather_Rain_Start"), weather);
        Assert.Contains(WwiseHash.Id("Weather_Rain_End"), weather);
        Assert.Contains(WwiseHash.Id("Weather_Wind_Start"), weather);
        Assert.DoesNotContain(WwiseHash.Id("Weather_Wind_Stop"), weather);
        Assert.Equal(4, weather.Count);
    }
}
