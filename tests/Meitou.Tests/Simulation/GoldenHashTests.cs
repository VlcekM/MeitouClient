using Meitou.Tests.Combat;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Pins the state hashes of the scripted scenarios the other tests use, so a refactor that is meant to change nothing is checked to change nothing
/// (<see cref="A_scenario_hashes_the_same_at_1_4_and_16_threads"/> only checks that the threads agree with each other). A change of behaviour that is
/// meant must update these numbers in the same commit and say so.
/// </summary>
public class GoldenHashTests(ITestOutputHelper output)
{
    static readonly Dictionary<string, ulong[]> Golden = new()
    {
        ["wander"] = [0xD1500034628C41D4UL, 0xC2340E8F70A18BFEUL, 0xB81E6A160F9D1F38UL, 0xBBE20F6A3B73104CUL, 0xAB6267B04ABC2401UL,
                0xEF2473425CE9AC92UL],
        ["player"] = [0x1CA9CE45158464FCUL, 0x4F6F121166492830UL, 0xCFCCF03313DAE1DBUL, 0x22B85607077910BBUL, 0x41FE32CFA1DC1F98UL,
                0xE1F23221654EED5CUL],
        ["roaming"] = [0x00DE9021CE67B00FUL, 0x77642713BCBC7F89UL, 0x03EA1750DC4773D0UL, 0xB6932066CFBC426CUL, 0x32BF528B42515FE7UL,
                0x25130D2F8F9D2E8DUL],
        ["bodies"] = [0x484AEA867DB5F323UL, 0xF5C5965FFA5972BAUL, 0x30D31640ECDF1B38UL, 0xCFE38D55A4BAA43DUL, 0x8108EFFA5A46BC99UL,
                0x90C814E547D725A6UL, 0x4418490892C6DAD4UL],
        ["fight"] = [0x05BFBD830401C4ABUL, 0x7E2707193D05E961UL, 0x46A93D1621A5A7E5UL, 0xD6B80E7154AC36F5UL, 0xA22A591C980DF944UL],
        ["duels"] = [0x24C90E8A8C0F5977UL, 0x29D3404ABCA353BBUL, 0x27490F17A2D7815AUL, 0x2498ED11CF9BB3F7UL, 0x7C1F1B57043D1C92UL,
                0xA4EC62BF215973AEUL],
        ["wander-parts"] = [0x207C09DDD8D8C79DUL, 0xE812753F34AF26C5UL, 0x02676EDB7F8150D0UL, 0x93618B2A03EE44FDUL],
        ["town-parts"] = [0xC7FB54D1AAA5059DUL, 0x2D72B20953163804UL, 0x28FC32CFFE8865C3UL, 0xBC5A2C372ABDFCDCUL],
        ["first-aid"] = [0x426145F0F0CAD626UL, 0x74BFC2417E0C8CECUL, 0xD0EFB5F7BBCDD3C6UL, 0xE8B66A3AD66670FEUL, 0x7D56AD04CA3B1917UL,
                0x10C12D786D79154CUL],
        ["aggro"] = [0x2007E309FB8B19EEUL, 0xC9B04092346DAB7EUL, 0x2FE4EC2CA2B5E842UL, 0xFE774F39EF2A2F53UL, 0xA5493FBB17CC900BUL],
        ["goal-fights"] = [0xEC44666DACBE483FUL, 0xB80AC280B00909BEUL, 0x1BB389D1F8483E70UL, 0xB0A8E73B13332666UL],
        ["ranged"] = [0x948194BC52BFC9FFUL, 0xB371E2C216E9C5DDUL, 0xAB259463EEBB068CUL, 0x548D0A81C46E64A5UL, 0xDEF60B86549675D7UL],
        ["carry"] = [0x765386AA86A9187DUL, 0xA85E885F2AC2FAC0UL, 0xCD77164D980F81C6UL, 0xF9D6B1229E703AD2UL, 0xF06969CDD2F39A1AUL,
                0xDA3B5602FE390AAFUL, 0xC79F375D67DD2472UL],
        ["crime"] = [0x5F1E7FA7DCA3336FUL, 0x57BA11A34C4F7A6CUL, 0x688EEFA67194F2D8UL, 0xB92D3366FF5E3C8DUL, 0xB201D14B045E7150UL,
                0x178148CDE1FE7B07UL],
        ["build"] = [0x0BF1D83B68110C29UL, 0x8CDF4E3FDDB903F9UL, 0x6709321EE82A44C3UL, 0xB3CE185610D2C597UL, 0x6F72AD8287A51649UL,
                0xE022324A21B2E6F4UL, 0x4EA708C861457CBCUL, 0xE2979945B2E5291DUL],
        ["dialogue"] = [0x3490F7D063916E12UL, 0x9165C58C27428685UL, 0x3945A13DE6585665UL, 0xEACC172C541DFE42UL, 0xF7399D1586A7168EUL,
                0x19A50D35A2EFE793UL],
        ["ai"] = [0x2F2DEF3752748483UL, 0x0977E18299290134UL],
        ["arrest"] = [0x1E1925902C48F03FUL, 0x3985FCB3145B0194UL, 0x207EFACB2318BAD9UL, 0xB5CC538F7E16CDC0UL, 0xF6D5E45DBA2988C1UL],
        ["kidnap"] = [0xFD04CF330B4476E6UL, 0x6BA3C07CAF3DC3CAUL, 0x0BB4F1558CE0925BUL, 0xC7B59A86693909EEUL, 0xB4A2276A0DB268D0UL],
        ["production"] = [0x837C9EECE3802F75UL, 0xBC4BF6A1887697A3UL, 0xE737F9A955009B38UL, 0xDB4DBABA1E7D1393UL, 0x68392DC2E5592616UL,
                0xBA0E8E5482719C25UL],
        ["wild"] = [0x9D505726FB85CB4CUL, 0xDC75B5F6BFBC537BUL, 0x2B8406B34401829CUL, 0xB09477FBE4BE227BUL],
        ["luckyren"] = [0x8D0FB2B723B6FA3CUL],
    };

    public static TheoryData<string> ScenarioNames => [.. Scenarios.Select(s => s.Name)];

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

    /// <summary>
    /// A roaming town with bodies and feeding, cut into many partitions (the other worlds here are mostly one partition at the default size).
    /// </summary>
    static Meitou.Simulation.World TownParts(int threads)
    {
        var db = SyntheticTown.Database(roaming: true, anatomy: true, items: true);
        return SyntheticTown.World(21, threads, db: db, bodies: true, bodyTimeScale: 2000, feed: new Meitou.Simulation.Items.FeedSettings(),
                minPartition: 4);
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

    [Fact]
    public void Scripted_scenarios_hash_as_pinned()
    {
        // The pins were made on Windows; the wandering scenarios use MathF.Sin/Cos/Atan2, which come from the platform's C library and can differ in
        // the last bit elsewhere.
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
