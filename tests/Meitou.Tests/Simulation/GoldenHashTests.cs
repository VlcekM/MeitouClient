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
        ["bodies"] = [0x892940C79C068252UL, 0xEA3B2ECF84DE067FUL, 0x9A20122FCC00DDE5UL, 0x3B63DA6E6F7A49FEUL, 0x228886E266E0DE3BUL, 0x628515479C3C03ABUL, 0xDD44500882B008ACUL],
        ["fight"] = [0x05BFBD830401C4ABUL, 0x7E2707193D05E961UL, 0x46A93D1621A5A7E5UL, 0xD6B80E7154AC36F5UL, 0x5C0F836239F00378UL],
        ["duels"] = [0x0CC88EE80042C575UL, 0x43D85A9E7227153EUL, 0x08CF3339AD7D832AUL, 0x198D71956AA23A97UL, 0xAE6195BE26C62BA1UL, 0x8505B813FA364CEDUL],
        ["wander-parts"] = [0x207C09DDD8D8C79DUL, 0xE812753F34AF26C5UL, 0x02676EDB7F8150D0UL, 0x93618B2A03EE44FDUL],
        ["town-parts"] = [0x42498BE2E2956429UL, 0xAF5C90956BC28ADCUL, 0x6EF8969394B37C9AUL, 0x7DE97CEDBBAC50EFUL],
        ["first-aid"] = [0xCA29483DD3B9E847UL, 0x25A58E045415ABC2UL, 0x4728A8B0CEE7535FUL, 0x96D617B41EBE956DUL, 0x2DD7D98A7D087504UL, 0x88CA55010E7CF17AUL],
        ["aggro"] = [0xC0D72DCB52945584UL, 0x45859FD5DFF15FD9UL, 0x7DC4D3C4BF64E0B1UL, 0xDC4C98699C6B8A89UL, 0xE21ACADE30EBEE47UL],
        ["goal-fights"] = [0xF29BC41039881922UL, 0x94B8185196117255UL, 0xD1DB5E0C122CC882UL, 0x3ADE22DA1C9B21E1UL],
        ["ranged"] = [0x948194BC52BFC9FFUL, 0x4BEFCED7F9283D58UL, 0x5DE04F7F80246FA8UL, 0x544C56338E310356UL, 0xBC73A46237B044C6UL],
        ["carry"] = [0x4C62265CA46BFB15UL, 0x22455AEB160283B1UL, 0x4FB53D64DECD59FFUL, 0xF71CA572273E6C9CUL, 0xECD1739F43324BC5UL, 0x6787B67B8CCA3B1FUL, 0x782A41347929972AUL],
        ["crime"] = [0x5F1E7FA7DCA3336FUL, 0xA934417AEE41A268UL, 0x395D8D19A5178CFAUL, 0x607194B0B61E8459UL, 0xEA063294B5E3AC48UL, 0x6A74589D39415E36UL],
        ["build"] = [0x0BF1D83B68110C29UL, 0x8CDF4E3FDDB903F9UL, 0x6709321EE82A44C3UL, 0xB3CE185610D2C597UL, 0x6F72AD8287A51649UL, 0xE022324A21B2E6F4UL, 0x4EA708C861457CBCUL, 0xE2979945B2E5291DUL],
        ["dialogue"] = [0x3490F7D063916E12UL, 0x9165C58C27428685UL, 0xE63BC85E1E354E13UL, 0x47CADC420684AAE2UL, 0x06164A262EFB318DUL, 0xB77F31DDD48E154DUL],
        ["ai"] = [0x90C285AD09628A41UL, 0x26934666D7A5A98DUL],
        ["arrest"] = [0x1E1925902C48F03FUL, 0xAD7E5FEC8D7FFFAFUL, 0x1925547A74707632UL, 0xE89422838B28176BUL, 0x3E2268FCBC797C21UL],
        ["kidnap"] = [0xFD04CF330B4476E6UL, 0x6BA3C07CAF3DC3CAUL, 0xE15E0A6FEA7389A0UL, 0x5B084604E123BCF2UL, 0x6C0B3B35EC6AA1B3UL],
        ["production"] = [0x837C9EECE3802F75UL, 0xBC4BF6A1887697A3UL, 0xE737F9A955009B38UL, 0xDB4DBABA1E7D1393UL, 0x68392DC2E5592616UL, 0xBA0E8E5482719C25UL],
        ["wild"] = [0x3C395F19CCA6FC94UL, 0x7D49B0F8F4E0FF4BUL, 0xB12D1081ADECBF8CUL, 0xD9E5DA9CDCC201ABUL],
        ["luckyren"] = [0xB73EBB853F3A6BC5UL],
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
