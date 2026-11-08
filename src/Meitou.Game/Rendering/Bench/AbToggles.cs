using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Rendering.Bench;

/// <summary>
/// The benchmark harness's runtime switches (<c>--ab &lt;name&gt;</c>, docs/bench.md "Benchmark harness"): a name, how to read it and how to set it.
/// <b>A is <c>true</c></b> (Meitou, the feature on), <b>B is <c>false</c></b> (Faithful, the feature off). The Faithful / Meitou switches
/// (<see cref="Enhancements"/>) are registered under their ids, and the viewer adds the culls and passes that can flip between two frames.
/// </summary>
public static class AbToggles
{
    public sealed record Toggle(string Name, Func<bool> Get, Action<bool> Set, string Note);

    static readonly Dictionary<string, Toggle> Toggles = new(StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<Toggle> All => Toggles.Values;

    /// <summary>Adds (or replaces) a switch; <paramref name="set"/>(true) is side A, <paramref name="set"/>(false) side B.</summary>
    public static void Register(string name, Func<bool> get, Action<bool> set, string note = "") => Toggles[name] = new Toggle(name, get, set, note);

    /// <summary>Registers every Faithful / Meitou switch under its id (A = Meitou, B = Faithful).</summary>
    public static void RegisterEnhancements(IEnumerable<Enhancement> switches)
    {
        foreach (var e in switches)
        {
            var enhancement = e;
            Register(enhancement.Id, () => enhancement.IsMeitou, v => enhancement.IsMeitou = v, $"{enhancement.Name}: A = {enhancement.Meitou}, B = {enhancement.Faithful}");
        }
    }

    public static bool TryGet(string name, out Toggle toggle) => Toggles.TryGetValue(name, out toggle!);

    public static string Names => string.Join(", ", Toggles.Keys.Order(StringComparer.OrdinalIgnoreCase));

    public static void Clear() => Toggles.Clear();
}