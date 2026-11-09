using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Rendering.Bench;

/// <summary>
/// The benchmark harness's runtime switches (<c>--ab &lt;name&gt;</c>, docs/bench.md "Benchmark harness"): a name, how to read it and how to set it.
/// <b>A is <c>true</c></b> (Meitou, the feature on), <b>B is <c>false</c></b> (Faithful, the feature off). The Faithful / Meitou switches
/// (<see cref="Enhancements"/>) are registered under their ids, and the viewer adds the culls and passes that can flip between two frames.
/// </summary>
public static class AbToggles
{
    /// <summary>
    /// One switch: its name, how to read it, how to set it (true is side A) and what A and B mean.
    /// </summary>
    public sealed record Toggle(string Name, Func<bool> Get, Action<bool> Set, string Note);

    /// <summary>
    /// The registered switches, by name (case-insensitive).
    /// </summary>
    static readonly Dictionary<string, Toggle> Toggles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every registered switch.
    /// </summary>
    public static IEnumerable<Toggle> All => Toggles.Values;

    /// <summary>
    /// Adds (or replaces) a switch; <paramref name="set"/>(true) is side A, <paramref name="set"/>(false) side B.
    /// </summary>
    public static void Register(string name, Func<bool> get, Action<bool> set, string note = "") => Toggles[name] = new Toggle(name, get, set, note);

    /// <summary>
    /// Registers every Faithful / Meitou switch under its id (A = Meitou, B = Faithful).
    /// </summary>
    public static void RegisterEnhancements(IEnumerable<Enhancement> switches)
    {
        foreach (var e in switches)
        {
            var enhancement = e;
            string note = $"{enhancement.Name}: A = {enhancement.Meitou}, B = {enhancement.Faithful}";

            Register(enhancement.Id, () => enhancement.IsMeitou, v => enhancement.IsMeitou = v, note);
        }
    }

    /// <summary>
    /// The switch registered under <paramref name="name"/> (case-insensitive), if any.
    /// </summary>
    public static bool TryGet(string name, out Toggle toggle) => Toggles.TryGetValue(name, out toggle!);

    /// <summary>
    /// The registered names, sorted and comma-separated.
    /// </summary>
    public static string Names => string.Join(", ", Toggles.Keys.Order(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Removes every switch.
    /// </summary>
    public static void Clear() => Toggles.Clear();
}