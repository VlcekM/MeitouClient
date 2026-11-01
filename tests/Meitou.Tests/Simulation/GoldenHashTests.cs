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
        ["wander"] = [0x44CB25E4214EEA88UL, 0x57DED28AC0BC9600UL, 0x739367FD0615AA45UL, 0x95FBB22778CDB85BUL, 0x0123140C9590640FUL, 0x891B10A11E43323FUL],
        ["player"] = [0x16F9CCB24EDE7690UL, 0x8107CCF5D73C572EUL, 0x339443CD6BE4A61DUL, 0x11964BA8705B5068UL, 0xAC777071C589064EUL, 0xA6B501D97B118EA4UL],
        ["roaming"] = [0x7179214F3570AF72UL, 0x4C140D89BC4147E7UL, 0xC513CD4CDC7FCD98UL, 0xF3638FC961CC74F6UL, 0x516A414F2E74F867UL, 0xFA5EBA00F21BAD24UL],
        ["bodies"] = [0xB6F7BCA74DC2E2A6UL, 0x12AA7F07EA841195UL, 0x9923B3BC7075B3E6UL, 0x49B95DA0970C7637UL, 0x18CFFFEA6355FED2UL, 0x75D93D3E9CB60D3DUL, 0x5B08370490AE5DB5UL],
        ["fight"] = [0x6DFE49AA37EEA854UL, 0xD4E13ECEA81A91C1UL, 0x6016E0A03C36FFE7UL, 0x1B9204F41E5D8E2EUL, 0x0C18AFB73E6302CAUL],
        ["duels"] = [0x26D7A7BBB15F6BFFUL, 0x90CA8543580D4461UL, 0x15DEA462F5C1298DUL, 0xFB94E85F02E5E576UL, 0xD7EF74CD167C82C1UL, 0xF008FAA5204833EDUL],
        ["wander-parts"] = [0x17289CD90BCF66A6UL, 0x73AD5EC8E02A7EE2UL, 0xDFB339410E851704UL, 0x6152BCF768E279D6UL],
        ["town-parts"] = [0x5C68D985E1DA9C91UL, 0x5BCAA4EB049EC38EUL, 0x6EF8969394B37C9AUL, 0x7DE97CEDBBAC50EFUL],
        ["first-aid"] = [0x0F41B3E74060594DUL, 0xDC1D1CF6ED491AAAUL, 0x98FFBE95D273228DUL, 0x974367F36AA61258UL, 0xD5B9963C7B3A31BFUL, 0xC833774D8ECE57B6UL],
        ["aggro"] = [0x2F8F2B8C8810D276UL, 0xBAE46698B2C8F690UL, 0x97600DBA00852865UL, 0xBFFF0DF32E239A31UL, 0x14E21E86347CA15CUL],
        ["goal-fights"] = [0x16E9FB7CE052E27BUL, 0x05612F1DA2B06675UL, 0x98FBC9146906C3C7UL, 0x1B285C9E82EAD94DUL],
        ["ranged"] = [0x08236AD8F467FE9DUL, 0xF5F038FA2282E580UL, 0xCDB883C68E8AADB3UL, 0xEBFA3E0ED090C091UL, 0x67B4B65235E5D551UL],
        ["carry"] = [0x755FEB141A8C01C7UL, 0x6E12306765EB7A5EUL, 0xB6A43E301817E49BUL, 0xAEC0165BA41CE6E5UL, 0x37F26ACF538F3177UL, 0x4B8F393B52C6DE48UL, 0x5F13B403420B711CUL],
        ["crime"] = [0x36E18D828CE72C4BUL, 0x0094B9AC308C96D6UL, 0x62A4671A7087FD8DUL, 0x853626394803604DUL, 0x21865C26DD29BFA7UL, 0x9EAD5125D79E0F2CUL],
        ["build"] = [0xDE809C88C7EC3C69UL, 0x9CB372987DC359EAUL, 0x4447F2E034B23198UL, 0x1D14987F6688F3F2UL, 0x65F2A68C6EA7ED4EUL, 0x05DE4BD06EEA910AUL, 0x8B82F70EBC5399C8UL, 0x99DE76A799C7EDEAUL],
        ["dialogue"] = [0x85487A6E7401FB36UL, 0x02B0FABFF0AB298BUL, 0x1A96548EA0E341C5UL, 0x621BF0B693CF4A72UL, 0x5181225FBA9C5D97UL, 0xD13EDC2381F6CC64UL],
        ["ai"] = [0x2474DB836F30F738UL, 0x0C3727D7D1C7DD0CUL],
        ["arrest"] = [0x24B1DC13087621DFUL, 0xE0A9D9387F22B213UL, 0x705DA92D20AE3DD7UL, 0xBA71A9FD5090E51FUL, 0x64564F94C81D9B26UL],
        ["kidnap"] = [0x41669FA0C7251F65UL, 0xE056DA4E7BB0F837UL, 0x1B2FF026C7C4F051UL, 0x270184B34C1FE0F9UL, 0x493B89974BE243C8UL],
        ["production"] = [0xF8ACD9EEC9A0472FUL, 0x62CFB57EC6117B28UL, 0x42B13E4E867A9648UL, 0x47565339B3528B76UL, 0x4EB5E81033F3750EUL, 0xC8BFD8A9A83BDF53UL],
        ["wild"] = [0xA8ED9B2732D1A158UL, 0x02543B181F3877C1UL, 0x541F679A92DC31D9UL, 0x889F728F7BF8E44EUL],
        ["luckyren"] = [0x9968C5EC4BF60437UL],
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
