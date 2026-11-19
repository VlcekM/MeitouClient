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
        ["player"] = [0x74254279DFD75408UL, 0xB5B4779EBF9E15A4UL, 0xB6B7407D5AA23E81UL, 0x890D5735BD963E34UL, 0x91FF76EB7D8418A4UL,
                0xC3824A1F388CF804UL],
        ["roaming"] = [0xA6EC10E6B0A08AD6UL, 0xA25EC97DC87939D4UL, 0x60CA0D2C7E7E0E79UL, 0x36FA72C96F950E49UL, 0xDC37004E55036B50UL,
                0x70C3313266D285FCUL],
        ["bodies"] = [0x484AEA867DB5F323UL, 0xF5C5965FFA5972BAUL, 0xF22261078143D51AUL, 0xA1833A7E8694ADFDUL, 0x4C29BC73DBDEA041UL,
                0xFEA6F3C0CF4AF0B7UL, 0x1B738B5A9FC05233UL],
        ["fight"] = [0x70A3EF5351BE6749UL, 0x7E2707193D05E961UL, 0x46A93D1621A5A7E5UL, 0xD7719DA02D8398A7UL, 0xA22A591C980DF944UL],
        ["duels"] = [0x24C90E8A8C0F5977UL, 0x29D3404ABCA353BBUL, 0xCB22C97FE85A681EUL, 0xF1A3E804B2460711UL, 0x184F4D63AA33A311UL,
                0x275012871FBEAC2BUL],
        ["wander-parts"] = [0x207C09DDD8D8C79DUL, 0xE812753F34AF26C5UL, 0x02676EDB7F8150D0UL, 0x93618B2A03EE44FDUL],
        ["town-parts"] = [0xC7FB54D1AAA5059DUL, 0x8492949D02D119F1UL, 0x325072AB2909DD95UL, 0x4ED56DF04D750B13UL],
        ["first-aid"] = [0x88F05178D6C3CAABUL, 0x55AE3C0B5D1084E9UL, 0xB258314B24F0D522UL, 0xB92588B035F90A3DUL, 0x3483C8FD02E0C14CUL,
                0xEF985C9EFF0DF3B0UL],
        ["aggro"] = [0x11318D10573CA782UL, 0x1609FF6A30338CC3UL, 0x8DC126371014B131UL, 0x3E8403E1BC9F7986UL, 0xDE66CD8E1A2AA699UL],
        ["goal-fights"] = [0xB22C386BCAEF3182UL, 0x35D7CFE50A3FE9C7UL, 0xAF826F74F259D09EUL, 0x72217F1622C9FEFCUL],
        ["ranged"] = [0x948194BC52BFC9FFUL, 0xB371E2C216E9C5DDUL, 0xAB259463EEBB068CUL, 0xD41F47DAA4F2E003UL, 0xFC93D6DD9D00E523UL],
        ["carry"] = [0x765386AA86A9187DUL, 0x28275A4D7A573B34UL, 0xEF5990CFAA0DFA93UL, 0xA1D64A5C84E478B3UL, 0x51B6EB1AD165EEA9UL,
                0x0E50E74A592F111EUL, 0x557267E29D2FE867UL],
        ["crime"] = [0x3A87E886EA8FD2F4UL, 0x39C88748052F6EBDUL, 0x3631BC53544F9D8FUL, 0xAB86FF63DB195F14UL, 0xC2246CDDB7E44F0BUL,
                0xB456D22C1B46C42DUL],
        // NO_TOWN is now hashed for new wilderness placements; refreshed on Linux, Windows verification remains outstanding.
        ["build"] = [0x0BF1D83B68110C29UL, 0x7A9C9611B54FBDE2UL, 0xDDDD08F79CFCFA98UL, 0x01F3592E86210A80UL, 0x2545F79055F49862UL,
                0x05288983DF35F782UL, 0x4D85FF6D07ADFD87UL, 0xD818A16ED0D71CDBUL],
        ["dialogue"] = [0x3490F7D063916E12UL, 0x9165C58C27428685UL, 0x5DCEA8BAFCDD306CUL, 0x28D5A6F8A15142FFUL, 0x305C1BADD424C1D6UL,
                0x782880A716BDB7C4UL],
        ["ai"] = [0xFDE613B1C45E0892UL, 0xFE75B4F6A57CB519UL],
        ["arrest"] = [0x912120167060713DUL, 0xB39DF1E30378D799UL, 0xA55AFBD6BBDB7EB7UL, 0x5BF56D7D4B6CCB70UL, 0x6B72615167DA5595UL],
        ["kidnap"] = [0x56BE37DB055E15C3UL, 0xDEE7755353692C41UL, 0x27CDFC24DE046BA0UL, 0xF3A11C28CDBE64B2UL, 0x2C27307ABC689396UL],
        ["production"] = [0x1983D5E11E44FBBDUL, 0x6437150938A59691UL, 0xD3DAC15542606D85UL, 0xF0C54CFC6457AD25UL, 0xEF0BFDB536E75BB0UL,
                0xA83A3A82A72D35FBUL],
        ["wild"] = [0xDA6C7E921C57F2DAUL, 0xBE8FC736EA719D59UL, 0xBBD8D4D65A7B6F56UL, 0x91C831298EC478E6UL],
        ["luckyren"] = [0xBFA31226B2817D01UL],
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
        output.WriteLine($"[\"{name}\"] = [{string.Join(", ", one.Select(h => $"0x{h:X16}UL"))}],");

        Assert.Equal(one, run(4));
        Assert.Equal(one, run(16));
    }
}
