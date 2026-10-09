namespace Meitou.Rendering;

/// <summary>
/// <c>--view &lt;name&gt;</c>: a fixed camera, world window and weather for benchmarks and A/B pictures, expanded in place into the options it stands
/// for, so any option after <c>--view</c> overrides it. The one table of the views (docs/bench.md "Benchmark harness" lists it).
/// </summary>
public static class NamedViews
{
    public static readonly IReadOnlyDictionary<string, string[]> Table = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        // Shark from 2000 units at 30 degrees (the town, its water and the swamp round it, as in play), orbiting the town while measured.
        ["swamp"] = ["--world", "--town", "Shark", "--radius", "2", "--distance", "2000", "--pitch", "30", "--yaw", "300", "--time", "12", "--bench-motion", "orbit"],
        // The same under the swamp's rain (WEATHER "swamp rain no wind": rain particles, wet ground, rain ripples on the water).
        ["swamp-rain"] = ["--world", "--town", "Shark", "--radius", "2", "--distance", "2000", "--pitch", "30", "--yaw", "300", "--time", "12", "--bench-motion", "orbit", "--weather", "swamp rain no wind"],
        // Heft in the desert under a dust storm: big additive sprites (the weather particles' fill rate).
        ["dust"] = ["--world", "--town", "Heft", "--radius", "2", "--distance", "3000", "--pitch", "10", "--yaw", "300", "--weather", "Dust Storm Approach", "--time", "12"],
        // The Hub from 9000 units: the biggest town, objects and far terrain.
        ["hub"] = ["--world", "--town", "The Hub", "--radius", "2", "--distance", "9000", "--pitch", "10", "--yaw", "300"],
    };

    public static string Names => string.Join(", ", Table.Keys);

    /// <summary>The arguments with every <c>--view &lt;name&gt;</c> replaced by its options; the rest keep their places (so later options win).</summary>
    public static string[] Expand(string[] args)
    {
        if (!args.Contains("--view")) return args;
        var result = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] != "--view") { result.Add(args[i]); continue; }
            if (i + 1 >= args.Length) throw new ArgumentException("--view needs a name");
            string name = args[++i];
            if (!Table.TryGetValue(name, out var expansion)) throw new ArgumentException($"unknown view '{name}' ({Names})");
            result.AddRange(expansion);
        }
        return [.. result];
    }
}
