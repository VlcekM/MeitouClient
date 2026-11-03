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
        ["bodies"] = [0x892940C79C068252UL, 0xEA3B2ECF84DE067FUL, 0xD85DE96E651899A3UL, 0xC5FBFEE9DC822E97UL, 0xCBB3845282F4478AUL, 0x185CD82409337C08UL, 0x09423E111F760FBEUL],
        ["fight"] = [0x05BFBD830401C4ABUL, 0x7E2707193D05E961UL, 0x69F931711B263CDEUL, 0xD7EA712F7BAE3605UL, 0xCE2EA2BC566BB93AUL],
        ["duels"] = [0x0CC88EE80042C575UL, 0x43D85A9E7227153EUL, 0x08CF3339AD7D832AUL, 0x198D71956AA23A97UL, 0xDE29B9BC2B903AD1UL, 0xB743FCDCC313540DUL],
        ["wander-parts"] = [0x207C09DDD8D8C79DUL, 0xE812753F34AF26C5UL, 0x02676EDB7F8150D0UL, 0x93618B2A03EE44FDUL],
        ["town-parts"] = [0x42498BE2E2956429UL, 0xAF5C90956BC28ADCUL, 0x6EF8969394B37C9AUL, 0x7DE97CEDBBAC50EFUL],
        ["first-aid"] = [0x01B960FB4EB8E84AUL, 0x11D153D7BBC6E881UL, 0xC496582912B49717UL, 0x6EB05D9AB812A328UL, 0xDBD275EE707B1DC6UL, 0x81FD8C4BE56AD2D7UL],
        ["aggro"] = [0xC0D72DCB52945584UL, 0x45859FD5DFF15FD9UL, 0x7DC4D3C4BF64E0B1UL, 0x065AE4868692B034UL, 0x8757276B41F6440EUL],
        ["goal-fights"] = [0xF29BC41039881922UL, 0x94B8185196117255UL, 0x1725E139199D17A9UL, 0xD72C1BF10D0128BCUL],
        ["ranged"] = [0x948194BC52BFC9FFUL, 0xEC422AB26D8580D5UL, 0x02054D422CE31524UL, 0xCE1C84963283E46DUL, 0xE0F32647BBB04194UL],
        ["carry"] = [0xF2FB09BAF1B71741UL, 0x1ADEC9123169EA54UL, 0xBD94563F4EC84740UL, 0xCDE8B3C5FC638013UL, 0x0FD114B6A866415BUL, 0x848EC22D9C63AA4BUL, 0x31262265DCB3F953UL],
        ["crime"] = [0x5F1E7FA7DCA3336FUL, 0xA934417AEE41A268UL, 0x453EEE58BE114935UL, 0x85A722C88C6463DCUL, 0xC33AB58DADEABD47UL, 0xCB54A05A8C952B81UL],
        ["build"] = [0x0BF1D83B68110C29UL, 0x8CDF4E3FDDB903F9UL, 0x6709321EE82A44C3UL, 0xB3CE185610D2C597UL, 0x6F72AD8287A51649UL, 0xE022324A21B2E6F4UL, 0x4EA708C861457CBCUL, 0xE2979945B2E5291DUL],
        ["dialogue"] = [0x3490F7D063916E12UL, 0x9165C58C27428685UL, 0xE63BC85E1E354E13UL, 0x47CADC420684AAE2UL, 0x06164A262EFB318DUL, 0xB77F31DDD48E154DUL],
        ["ai"] = [0x90C285AD09628A41UL, 0x26934666D7A5A98DUL],
        ["arrest"] = [0x1E1925902C48F03FUL, 0xFC90BB90D7D1A4A2UL, 0xE1399A9F75EB5305UL, 0xD6449342FB18F380UL, 0xBFBF6EA93710CBA9UL],
        ["kidnap"] = [0x34110C7054443021UL, 0x2D5F39BCCBB6D6C2UL, 0x8918BBCC9F71B850UL, 0x8BDF2A946C65791BUL, 0x145B9D7B530C72E0UL],
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
