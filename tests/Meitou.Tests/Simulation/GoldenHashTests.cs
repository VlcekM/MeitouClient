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
        ["player"] = [0x77EDC81BF937F092UL, 0x3FF9527AADD43966UL, 0x89CACA76DCC1EC7AUL, 0x1A9B7358E6B58A3BUL, 0x178A294D4950D88BUL, 0xA491081AE7A6D43CUL],
        ["roaming"] = [0x87AAB9387BD673ECUL, 0x769AB3F7431EC9F8UL, 0x0D261CD991B70264UL, 0xB92BA69F988F3103UL, 0x4B692DED6BE6D727UL, 0xC4C106DF7DB14989UL],
        ["bodies"] = [0x18A1C10FEAACA2B6UL, 0xD4940B65AAA84877UL, 0x22E84AB7D372DC41UL, 0x731A0EA52DC2142DUL, 0xAD761FAF78E10B3FUL, 0x2991D0A85C0261A9UL, 0x06A49093540288DBUL],
        ["fight"] = [0x80E7AB8257D1F605UL, 0x6F61AFBE15EBA342UL, 0x3D5F1C0F147F2EE3UL, 0x60479381998C5E74UL, 0xD08036E269C6E441UL],
        ["duels"] = [0x93FF705B23BF7AA7UL, 0x014160EF7BECC9F2UL, 0x385E394C150AF811UL, 0x794A365EA4399DE3UL, 0xA826F99D0141F3BCUL, 0x563E37C80BB9CDECUL],
        ["wander-parts"] = [0x326800801ABC5589UL, 0x21EE176FA3513AFDUL, 0xE6BF51A0B879E539UL, 0x98E5EB8154770A27UL],
        ["town-parts"] = [0xF5A6D33BA820564CUL, 0x8397B3D009DC8DA5UL, 0xF2947CDA0860B56CUL, 0x8774175A1A6C9216UL],
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
