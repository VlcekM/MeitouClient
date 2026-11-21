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
        ["roaming"] = [0x6CF06DB0F377BC23UL, 0x5BCDAA059EC704BDUL, 0xBBC30E207447423DUL, 0x6236ABA1A97B353FUL, 0xD6AD80CD6DE9FE74UL,
                0x267C7DD82975088FUL],
        ["bodies"] = [0x484AEA867DB5F323UL, 0xF5C5965FFA5972BAUL, 0xF22261078143D51AUL, 0xA1833A7E8694ADFDUL, 0x4C29BC73DBDEA041UL,
                0xFEA6F3C0CF4AF0B7UL, 0x1B738B5A9FC05233UL],
        ["fight"] = [0x781B4979FCF561C6UL, 0xEB011DD888B98469UL, 0xF86FC105B61A6C6EUL, 0x1458A24979EE63ADUL, 0x7EACD65B4E1E8638UL],
        ["duels"] = [0x24C90E8A8C0F5977UL, 0x29D3404ABCA353BBUL, 0xCB22C97FE85A681EUL, 0xF1A3E804B2460711UL, 0x184F4D63AA33A311UL,
                0x55978D92060D5F88UL],
        ["wander-parts"] = [0x207C09DDD8D8C79DUL, 0xE812753F34AF26C5UL, 0x02676EDB7F8150D0UL, 0x93618B2A03EE44FDUL],
        ["town-parts"] = [0xC8B31EFDCBB8BD69UL, 0x4059B7D2056D9A3EUL, 0x67D07977E7274CF4UL, 0x42E0A17B1BBACE93UL],
        ["first-aid"] = [0xD7B3BEED483A520CUL, 0x40F92EF766677194UL, 0xFCC19255AA7EA8EBUL, 0xC62C5EF88DEB96CCUL, 0x070F911E02A6AD0FUL,
                0x8063B90B3C50A8CDUL],
        ["aggro"] = [0xB2A2E6C8FA3C3098UL, 0x4D9C7508672F56EFUL, 0x888687418C55BCDFUL, 0xFE3F68373B875842UL, 0x3172A3E33EE9EAA4UL],
        ["goal-fights"] = [0x0EB37B06D19167FAUL, 0xE05E69ACFD8D7396UL, 0xDD44DFACCD04D37BUL, 0x91DBFE2C976E50E5UL],
        ["ranged"] = [0x15213C7B8471D8EFUL, 0xBBD1D5A50A611C69UL, 0xA4AD07F57D4709F3UL, 0xD909DE8B21FC8874UL, 0xA3BFE0E3BBFB10C7UL],
        ["carry"] = [0x236E2CCF5B92F0DAUL, 0x5765822C6A3D9AE5UL, 0x1C357FE666213AB2UL, 0x03CD358AEF5A6379UL, 0x4717A8AD02FAB6E8UL,
                0x91F5E6920AA2CF59UL, 0x74D15EFAA073943CUL],
        ["crime"] = [0x3A87E886EA8FD2F4UL, 0x39C88748052F6EBDUL, 0x694D1B100A69FF58UL, 0x3D478490D25D53A9UL, 0x3980568B4FDE2CD0UL,
                0xCBCFEF4EE782805CUL],
        ["build"] = [0x0BF1D83B68110C29UL, 0x7A9C9611B54FBDE2UL, 0x8799576544B7E322UL, 0x3977BF117F598AB6UL, 0x081C4574B55194F9UL,
                0x293820EC4CB2972BUL, 0x3A232AA7BC48ED37UL, 0xCBD1387E9726A015UL],
        ["dialogue"] = [0x3490F7D063916E12UL, 0xA250EC938DC3C6D5UL, 0x7CC026619FF75E28UL, 0x0B137C5FB9CEBB18UL, 0x7A26EB261AB236F6UL,
                0xBED27C781646AC0EUL],
        ["ai"] = [0xB560B089A9236C70UL, 0xBBBC2A86F20FEC1CUL],
        ["arrest"] = [0x3EE91F66D54A3340UL, 0x09636E3811E62A98UL, 0x02E6C8E0A6988471UL, 0x1C57084300B7E2B6UL, 0x856900DF7E859310UL],
        ["kidnap"] = [0x54F274A4B581F635UL, 0xA16FF8F1AF4B262CUL, 0x52C37FE1D9D31067UL, 0x58DEE4499FEAC90DUL, 0x19BC73CCE4FEDDDDUL],
        ["production"] = [0xF9D47197C3F1685FUL, 0xC09F50D88EFA8F02UL, 0xCFE4CA831DD4C03FUL, 0x7111C9132D03EFE7UL, 0x9AF1E89816A42895UL,
                0x2F422F3B3DFA6EDFUL],
        ["wild"] = [0xBE2048D398FDD2B8UL, 0x8A5DAB77C473D41DUL, 0x263218C7BA5C5FE3UL, 0xCCD1F628FC14CF13UL],
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
