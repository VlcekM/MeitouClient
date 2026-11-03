using Meitou.Tests.Combat;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Pins the state hashes of the scripted scenarios the other tests use, so a refactor that is meant to change nothing is checked to
/// change nothing (<see cref="A_scenario_hashes_the_same_at_1_4_and_16_threads"/> only checks that the threads agree with each other). A change of behaviour that is meant must
/// update these numbers in the same commit and say so.
/// </summary>
public class GoldenHashTests(ITestOutputHelper output)
{
    static readonly (string Name, Func<int, List<ulong>> Run)[] Scenarios =
    [
        ("wander", t => DeterminismTests.Hashes(7, t)),
        ("player", PlayerTests.Scripted),
        ("roaming", RoamingTests.Scripted),
        ("bodies", t => BodyWiringTests.Hashes(9, t, 2000)),
        ("fight", FightWiringTests.Hashes),
        ("duels", t => DuelTests.Hashes(5, t, out _)),
        ("wander-parts", t => Parts(WanderSystem.Create(11, t, 200, minPartition: 4), 1, 30, 90, 300)),
        ("town-parts", t => Parts(TownParts(t), 1, 40, 200, 700)),
        ("first-aid", FirstAidTests.Scripted),
        ("aggro", t => AggroTests.Hashes(t)),
        ("goal-fights", AggroTests.GoalFightHashes),
        ("ranged", t => RangedTests.Hashes(t)),
        ("carry", CarryTests.Scripted),
        ("crime", CrimeTests.Scripted),
        ("build", ConstructionTests.Scripted),
        ("dialogue", DialogueTests.Scripted),
        ("ai", AiTests.Scripted),
        ("arrest", ArrestTests.Scripted),
        ("kidnap", KidnapTests.Scripted),
        ("production", ProductionTests.Scripted),
        ("wild", WildTests.Scripted),
        ("luckyren", LuckyrenTests.Scripted),
    ];

    public static TheoryData<string> ScenarioNames => [.. Scenarios.Select(s => s.Name)];

    /// <summary>A roaming town with bodies and feeding, cut into many partitions (the other worlds here are mostly one partition at the default size).</summary>
    static Meitou.Simulation.World TownParts(int threads)
    {
        var db = SyntheticTown.Database(roaming: true, anatomy: true, items: true);
        return SyntheticTown.World(21, threads, db: db, bodies: true, bodyTimeScale: 2000, feed: new Meitou.Simulation.Items.FeedSettings(), minPartition: 4);
    }

    static List<ulong> Parts(Meitou.Simulation.World w, params int[] checkpoints)
    {
        using var world = w;
        var hashes = new List<ulong>();
        int ran = 0;
        foreach (int target in checkpoints)
        {
            world.RunTicks(target - ran);
            ran = target;
            hashes.Add(world.StateHash());
        }
        return hashes;
    }

    static readonly Dictionary<string, ulong[]> Golden = new()
    {
        ["wander"] = [0xD1500034628C41D4UL, 0xC2340E8F70A18BFEUL, 0xB81E6A160F9D1F38UL, 0xBBE20F6A3B73104CUL, 0xAB6267B04ABC2401UL, 0xEF2473425CE9AC92UL],
        ["player"] = [0x9CFE10DB4462928DUL, 0x75DC1A8965EA835BUL, 0x339443CD6BE4A61DUL, 0x11964BA8705B5068UL, 0xAC777071C589064EUL, 0xA6B501D97B118EA4UL],
        ["roaming"] = [0x3FB009AE6FB13999UL, 0x4C140D89BC4147E7UL, 0xC513CD4CDC7FCD98UL, 0xF3638FC961CC74F6UL, 0x516A414F2E74F867UL, 0xFA5EBA00F21BAD24UL],
        ["bodies"] = [0x892940C79C068252UL, 0xEA3B2ECF84DE067FUL, 0xD85DE96E651899A3UL, 0xC5FBFEE9DC822E97UL, 0xCBB3845282F4478AUL, 0xF05FAC175BC067D1UL, 0xD1445AE59FC4C3CFUL],
        ["fight"] = [0xBFA51B3F606727BDUL, 0x864AC1252EF908C0UL, 0xFC15A63400619308UL, 0x1100408680FDC65CUL, 0x77B31546F8B479C9UL],
        ["duels"] = [0x0CC88EE80042C575UL, 0x43D85A9E7227153EUL, 0x08CF3339AD7D832AUL, 0x198D71956AA23A97UL, 0xDE29B9BC2B903AD1UL, 0xB743FCDCC313540DUL],
        ["wander-parts"] = [0x207C09DDD8D8C79DUL, 0xE812753F34AF26C5UL, 0x02676EDB7F8150D0UL, 0x93618B2A03EE44FDUL],
        ["town-parts"] = [0x42498BE2E2956429UL, 0xAF5C90956BC28ADCUL, 0x6EF8969394B37C9AUL, 0x7DE97CEDBBAC50EFUL],
        ["first-aid"] = [0xD43B31BADDB8C09FUL, 0xDC93083E304BE810UL, 0x24148D2EA6D5D8D1UL, 0xFE5CD1DE4E9CAF14UL, 0xAB0306AEAD282770UL, 0x6B981AFCDA26FEFDUL],
        ["aggro"] = [0xB14FCC0F85E575F9UL, 0x37670B7C66D40D3AUL, 0x39CF2AA30C825773UL, 0x0CFA3E0A1E17B6E7UL, 0x972F71FA9FFA6205UL],
        ["goal-fights"] = [0x1EA481D297F30828UL, 0x03FF5815D47AC48AUL, 0xFEDDDF745A9A9629UL, 0xBA1285D1E84F22B3UL],
        ["ranged"] = [0x948194BC52BFC9FFUL, 0xEC422AB26D8580D5UL, 0x02054D422CE31524UL, 0x4F5C1982E330F361UL, 0xC77F60C40C62E666UL],
        ["carry"] = [0x567D03762A25393FUL, 0x06CC86B8FA26E8EBUL, 0xFCC8EF19066574C7UL, 0x27B05FE2B17E9A89UL, 0x572E6DBB014934D7UL, 0x845B54E9030ED339UL, 0x2248E19C6A91754BUL],
        ["crime"] = [0x73B2DCE5F914D359UL, 0x591C4C41FD4ADA2AUL, 0xD63FFFEA2034C570UL, 0x51D03A1A62FCF45AUL, 0x22D1C648AEF1A55CUL, 0x08F4576DAE4B5934UL],
        ["build"] = [0x0BF1D83B68110C29UL, 0x8CDF4E3FDDB903F9UL, 0x6709321EE82A44C3UL, 0xB3CE185610D2C597UL, 0x6F72AD8287A51649UL, 0xE022324A21B2E6F4UL, 0x4EA708C861457CBCUL, 0xE2979945B2E5291DUL],
        ["dialogue"] = [0x3490F7D063916E12UL, 0x9165C58C27428685UL, 0xE63BC85E1E354E13UL, 0x47CADC420684AAE2UL, 0x06164A262EFB318DUL, 0xB77F31DDD48E154DUL],
        ["ai"] = [0x90C285AD09628A41UL, 0x26934666D7A5A98DUL],
        ["arrest"] = [0x3606EFF083DCEFF3UL, 0x612A862D5957A4D4UL, 0x6208EF47067FEE4DUL, 0x0D1F1B16B9D9FFFBUL, 0xC3BF6D265CE7D6EEUL],
        ["kidnap"] = [0xB52E0A3F4B550AEFUL, 0x486FDE4E7EB58D9FUL, 0x1B2FF026C7C4F051UL, 0x270184B34C1FE0F9UL, 0x493B89974BE243C8UL],
        ["production"] = [0x837C9EECE3802F75UL, 0xBC4BF6A1887697A3UL, 0xE737F9A955009B38UL, 0xDB4DBABA1E7D1393UL, 0x68392DC2E5592616UL, 0xBA0E8E5482719C25UL],
        ["wild"] = [0x3C395F19CCA6FC94UL, 0x7D49B0F8F4E0FF4BUL, 0xB12D1081ADECBF8CUL, 0xD9E5DA9CDCC201ABUL],
        ["luckyren"] = [0xA749CB6A6206FA32UL],
    };

    [Fact]
    public void Scripted_scenarios_hash_as_pinned()
    {
        // The pins were made on Windows; the wandering scenarios use MathF.Sin/Cos/Atan2, which come from the platform's C library and can differ in the last bit elsewhere.
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "hashes are pinned on Windows");
        var failures = new List<string>();
        foreach (var (name, run) in Scenarios)
        {
            var got = run(4);
            output.WriteLine($"[\"{name}\"] = [{string.Join(", ", got.Select(h => $"0x{h:X16}UL"))}],");
            if (!Golden.TryGetValue(name, out var want)) failures.Add($"{name}: not pinned");
            else if (!want.SequenceEqual(got)) failures.Add($"{name}: hashes changed");
        }
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    [Theory, MemberData(nameof(ScenarioNames))]
    public void A_scenario_hashes_the_same_at_1_4_and_16_threads(string name)
    {
        var run = Scenarios.Single(s => s.Name == name).Run;
        var one = run(1);
        Assert.Equal(one, run(4));
        Assert.Equal(one, run(16));
    }
}
