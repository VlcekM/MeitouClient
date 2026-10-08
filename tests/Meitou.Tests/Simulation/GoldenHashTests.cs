using Meitou.Tests.Combat;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Pins the state hashes of the scripted scenarios the other tests use, so a refactor that is meant to change nothing is checked to
/// change nothing (the thread-count tests only check that the threads agree with each other). A change of behaviour that is meant must
/// update these numbers in the same commit and say so.
/// </summary>
public class GoldenHashTests(ITestOutputHelper output)
{
    static readonly (string Name, Func<List<ulong>> Run)[] Scenarios =
    [
        ("wander", () => DeterminismTests.Hashes(7, 4)),
        ("player", () => PlayerTests.Scripted(4)),
        ("roaming", () => RoamingTests.Scripted(4)),
        ("bodies", () => BodyWiringTests.Hashes(9, 4, 2000)),
        ("fight", () => FightWiringTests.Hashes(4)),
        ("duels", () => DuelTests.Hashes(5, 4, out _)),
        ("wander-parts", () => Parts(WanderSystem.Create(11, 4, 200, minPartition: 4), 1, 30, 90, 300)),
        ("town-parts", () => Parts(TownParts(), 1, 40, 200, 700)),
    ];

    /// <summary>A roaming town with bodies and feeding, cut into many partitions (the other worlds here are mostly one partition at the default size).</summary>
    static Meitou.Simulation.World TownParts()
    {
        var db = SyntheticTown.Database(roaming: true, anatomy: true, items: true);
        return SyntheticTown.World(21, 4, db: db, bodies: true, bodyTimeScale: 2000, feed: new Meitou.Simulation.Items.FeedSettings(), minPartition: 4);
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
        ["wander"] = [0xBC8A8D2555342F64UL, 0x01B9F04CEECFC9DCUL, 0xC245D0A02BE9C3C8UL, 0xB75698182A691537UL, 0x14A433DA8F141ADBUL, 0xA33D262B7012C799UL],
        ["player"] = [0x77EDC81BF937F092UL, 0x7D477EFB8DB7353FUL, 0x69D9D41BE32E1780UL, 0x0069F40DB64B44AAUL, 0x80786D16668D7987UL, 0x966CEF683F748F9DUL],
        ["roaming"] = [0x252F455D41FFFE3DUL, 0xC1EB305A0D51A9E1UL, 0x2D78CC87B6EBB44AUL, 0xC1BE29AB3D5742B2UL, 0xB77DEBC390023555UL, 0x54C8747A9F16771BUL],
        ["bodies"] = [0x18A1C10FEAACA2B6UL, 0xD4940B65AAA84877UL, 0x22E84AB7D372DC41UL, 0x5F86F3102564AAEFUL, 0xD0220C0254805C93UL, 0x4FBA0BF4F9DC09F1UL, 0xDFD5278FC1EEBBC2UL],
        ["fight"] = [0xC925E3AB9046BFC5UL, 0x3EABA97127DD12DBUL, 0xBC16ABFFD0BFF4E6UL, 0x4687C88996526443UL, 0xBFDDBB8111794756UL],
        ["duels"] = [0x53A9CB6AEB0E07FEUL, 0xD8708866C6424879UL, 0xCAF894A835FE9713UL, 0xD78293BF0C87D8F6UL, 0x1EE94C2115CD9CB7UL, 0x32D9ED7A81D20026UL],
        ["wander-parts"] = [0x326800801ABC5589UL, 0x21EE176FA3513AFDUL, 0xE6BF51A0B879E539UL, 0x98E5EB8154770A27UL],
        ["town-parts"] = [0xF5A6D33BA820564CUL, 0xD6D640EF5621D9D2UL, 0x218DB946177EF8E3UL, 0x3CD902A38E940C0EUL],
    };

    [Fact]
    public void Scripted_scenarios_hash_as_pinned()
    {
        var failures = new List<string>();
        foreach (var (name, run) in Scenarios)
        {
            var got = run();
            output.WriteLine($"[\"{name}\"] = [{string.Join(", ", got.Select(h => $"0x{h:X16}UL"))}],");
            if (!Golden.TryGetValue(name, out var want)) failures.Add($"{name}: not pinned");
            else if (!want.SequenceEqual(got)) failures.Add($"{name}: hashes changed");
        }
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }
}
