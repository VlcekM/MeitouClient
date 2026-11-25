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
        ["player"] = [0x7E246DE209605F8EUL, 0xE9D0708580CE6E0DUL, 0x6C20FBE6EEA361FDUL, 0x993CD7DC29D209CBUL, 0x432B2BF38D63D404UL,
                0xD44103CBAC251243UL],
        ["roaming"] = [0xF1320B161441F66EUL, 0xEB24260F403A42C6UL, 0xEB5E6A88D22A4798UL, 0x91A45C86A6116350UL, 0x9ED482137A472780UL,
                0x08244983F8782F05UL],
        ["bodies"] = [0x484AEA867DB5F323UL, 0x6A1C22BE311734C4UL, 0x37FA9390CE6A3D1BUL, 0x85D6AC2B7EE5339AUL, 0x431225E15773DE1FUL,
                0x004649AEE8490CC7UL, 0xE4E8A110ADCE38FFUL],
        ["fight"] = [0x781B4979FCF561C6UL, 0x04822C71D1128AB4UL, 0x06BD2D4F7D1C2518UL, 0x6331F4B18594868CUL, 0xD40E29E8294F56BCUL],
        ["duels"] = [0x24C90E8A8C0F5977UL, 0x29D3404ABCA353BBUL, 0xFD9F3F63EE44D0CCUL, 0x414872690B9D5481UL, 0x4F2CC011E6CD5B28UL,
                0xAF06E422CBB6432CUL],
        ["wander-parts"] = [0x207C09DDD8D8C79DUL, 0xE812753F34AF26C5UL, 0x02676EDB7F8150D0UL, 0x93618B2A03EE44FDUL],
        ["town-parts"] = [0xC8B31EFDCBB8BD69UL, 0xBECFDC69DED666AAUL, 0xCCD74F6CD33B67ECUL, 0x13356E5854C3BEABUL],
        ["first-aid"] = [0xD7B3BEED483A520CUL, 0x40F92EF766677194UL, 0xFCC19255AA7EA8EBUL, 0xC62C5EF88DEB96CCUL, 0x070F911E02A6AD0FUL,
                0x8063B90B3C50A8CDUL],
        ["aggro"] = [0x5537D718DEFA7391UL, 0xCDE3BD748926D22AUL, 0x04C5EE18A52720B3UL, 0x8D2B55B7D6E3CBBEUL, 0xEBFEE5C68EFE2BB4UL],
        ["goal-fights"] = [0x56C1635D09495ED3UL, 0x21375D3805B10DE3UL, 0x19DF8D87AA29CB62UL, 0x3D9517ACFBB71246UL],
        ["ranged"] = [0x15213C7B8471D8EFUL, 0xBBD1D5A50A611C69UL, 0xA4AD07F57D4709F3UL, 0xEBB2DA1229E54D34UL, 0xDD17AE0D0D64886BUL],
        ["carry"] = [0xCC521F17E90CA339UL, 0x87244168094E75F7UL, 0x36E1FC07B1AE4E74UL, 0xE0EF85568A5A7D30UL, 0x316EF9D7408869F2UL,
                0x3FDD337B66791425UL, 0x644AA8E0D3230F8DUL],
        ["crime"] = [0x3A87E886EA8FD2F4UL, 0x39C88748052F6EBDUL, 0x70037F63050D37A5UL, 0xD7402376D3654235UL, 0xEC5637191238576BUL,
                0xFE4D4EAA71FCE3D6UL],
        ["build"] = [0x0BF1D83B68110C29UL, 0x7A9C9611B54FBDE2UL, 0x8799576544B7E322UL, 0x3977BF117F598AB6UL, 0x081C4574B55194F9UL,
                0x293820EC4CB2972BUL, 0x3A232AA7BC48ED37UL, 0xCBD1387E9726A015UL],
        ["dialogue"] = [0x3490F7D063916E12UL, 0xA250EC938DC3C6D5UL, 0x7CC026619FF75E28UL, 0x0B137C5FB9CEBB18UL, 0x7A26EB261AB236F6UL,
                0xBED27C781646AC0EUL],
        ["ai"] = [0xB560B089A9236C70UL, 0xBBBC2A86F20FEC1CUL],
        ["arrest"] = [0x12D9758289442879UL, 0x53B9A4C0DE601B8DUL, 0xBB1C241DB20924F4UL, 0x322DB94725F5976AUL, 0x81398E0D315BB686UL],
        ["kidnap"] = [0x54F274A4B581F635UL, 0x264C993A797915C5UL, 0x88AA0FBF54EF7EB7UL, 0xBCAAD6159E8C2DC0UL, 0xA2A9C98A344ABFC5UL],
        ["production"] = [0xF46D7DE37FEF7B21UL, 0x80C58E38D58BFDE8UL, 0xE5F9765265F8B205UL, 0x03EA1FA7E86D9C97UL, 0xC065F1F4954FB8B8UL,
                0xC1CFAC8D5A1AD1F5UL],
        ["wild"] = [0x79306CBDB5F1484BUL, 0x19542C1E15EAA6B0UL, 0x7E9C590C0F308332UL, 0x58FED5B413DBB129UL],
        ["luckyren"] = [0xEB6703596C53798DUL],
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
