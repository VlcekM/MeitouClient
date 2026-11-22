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
        ["fight"] = [0x781B4979FCF561C6UL, 0x04822C71D1128AB4UL, 0x06BD2D4F7D1C2518UL, 0x6331F4B18594868CUL, 0xD40E29E8294F56BCUL],
        ["duels"] = [0x24C90E8A8C0F5977UL, 0x29D3404ABCA353BBUL, 0xCB22C97FE85A681EUL, 0x53D2B2EB74BF44F0UL, 0x3BD641F282982588UL,
                0xC922CD6D8A590259UL],
        ["wander-parts"] = [0x207C09DDD8D8C79DUL, 0xE812753F34AF26C5UL, 0x02676EDB7F8150D0UL, 0x93618B2A03EE44FDUL],
        ["town-parts"] = [0xC8B31EFDCBB8BD69UL, 0x4059B7D2056D9A3EUL, 0x67D07977E7274CF4UL, 0x42E0A17B1BBACE93UL],
        ["first-aid"] = [0xD7B3BEED483A520CUL, 0x40F92EF766677194UL, 0xFCC19255AA7EA8EBUL, 0xC62C5EF88DEB96CCUL, 0x070F911E02A6AD0FUL,
                0x8063B90B3C50A8CDUL],
        ["aggro"] = [0xB2A2E6C8FA3C3098UL, 0xD70B77B8CC60E60FUL, 0xD9D53F593E9D7F9BUL, 0x4D95F8B297B42FF8UL, 0x2F66BF04A53D3129UL],
        ["goal-fights"] = [0x0EB37B06D19167FAUL, 0x6680C88B481F2835UL, 0xF3E513458C7A6458UL, 0x85221985225EC20CUL],
        ["ranged"] = [0x15213C7B8471D8EFUL, 0xBBD1D5A50A611C69UL, 0xA4AD07F57D4709F3UL, 0xEBB2DA1229E54D34UL, 0xDD17AE0D0D64886BUL],
        ["carry"] = [0xCC521F17E90CA339UL, 0x87244168094E75F7UL, 0x36E1FC07B1AE4E74UL, 0xE0EF85568A5A7D30UL, 0x316EF9D7408869F2UL,
                0x3FDD337B66791425UL, 0x644AA8E0D3230F8DUL],
        ["crime"] = [0x3A87E886EA8FD2F4UL, 0x39C88748052F6EBDUL, 0xEF31810EFEA30CADUL, 0xDE8EC1BD0954D287UL, 0x6603408EF4691FE4UL,
                0x210A625F43E0BD67UL],
        ["build"] = [0x0BF1D83B68110C29UL, 0x7A9C9611B54FBDE2UL, 0x8799576544B7E322UL, 0x3977BF117F598AB6UL, 0x081C4574B55194F9UL,
                0x293820EC4CB2972BUL, 0x3A232AA7BC48ED37UL, 0xCBD1387E9726A015UL],
        ["dialogue"] = [0x3490F7D063916E12UL, 0xA250EC938DC3C6D5UL, 0x7CC026619FF75E28UL, 0x0B137C5FB9CEBB18UL, 0x7A26EB261AB236F6UL,
                0xBED27C781646AC0EUL],
        ["ai"] = [0xB560B089A9236C70UL, 0xBBBC2A86F20FEC1CUL],
        ["arrest"] = [0x12D9758289442879UL, 0x235A1C6CD0F6573DUL, 0xB90FA61ADEF6FD4DUL, 0xA0F18D43B792ACA4UL, 0x81398E0D315BB686UL],
        ["kidnap"] = [0x54F274A4B581F635UL, 0x264C993A797915C5UL, 0x88AA0FBF54EF7EB7UL, 0xBCAAD6159E8C2DC0UL, 0xA2A9C98A344ABFC5UL],
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
