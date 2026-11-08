using System.Globalization;
using Meitou.Data.Fcs;
using Meitou.Data.World;
using Meitou.Simulation.Population;
using Meitou.Simulation.Production;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The machines of NPC town buildings on the CAMERA record (docs/formats/save.md "Town machines"): the original keeps the production state of every building in its GAMESTATE_BUILDING
/// (<c>production amount</c>, <c>consumption amount-&lt;k&gt;</c>, the items in an INVENTORY_STATE); Meitou's town buildings of a new game have no state record to put them in, so they are Meitou-only keys:
/// <c>meitou tm&lt;i&gt;</c> (the building's run-time key), <c>meitou tm&lt;i&gt; out</c> (progress, float), <c>meitou tm&lt;i&gt; outn</c> (output items, int), <c>meitou tm&lt;i&gt; st</c> (status, int),
/// <c>meitou tm&lt;i&gt; in&lt;k&gt;</c> (items of input k, int) and <c>meitou tm&lt;i&gt; lv&lt;k&gt;</c> (its level, float). The original ignores them (keys are read by name).
/// </summary>
public static class TownMachineSave
{
    const string Prefix = "meitou tm";

    static string Key(int i, string part = "") => Prefix + i.ToString(CultureInfo.InvariantCulture) + part;

    public static void Write(FcsRecord camera, World world)
    {
        for (int i = 0; camera.Strings.ContainsKey(Key(i)); i++)
            foreach (var k in camera.Strings.Keys.Concat(camera.Floats.Keys).Concat(camera.Ints.Keys).Where(k => k.StartsWith(Key(i), StringComparison.Ordinal)).ToList())
            {
                camera.Strings.Remove(k); camera.Floats.Remove(k); camera.Ints.Remove(k);
            }
        int n = 0;
        foreach (var m in world.Machines.All)
        {
            if (!ProductionSystem.IsTownWork(m.Site)) continue;
            camera.Strings[Key(n)] = m.Site[ProductionSystem.TownWorkPrefix.Length..];
            camera.Ints[Key(n, " st")] = m.Status;
            if (m.Output is { } o)
            {
                camera.Floats[Key(n, " out")] = o.Progress;
                camera.Ints[Key(n, " outn")] = o.Count;
            }
            for (int k = 0; k < m.Inputs.Length; k++)
            {
                camera.Ints[Key(n, " in" + k.ToString(CultureInfo.InvariantCulture))] = m.Inputs[k].Count;
                camera.Floats[Key(n, " lv" + k.ToString(CultureInfo.InvariantCulture))] = m.Inputs[k].Level;
            }
            n++;
        }
    }

    /// <summary>The town building of a run-time key and the faction owning its town now, over the population's sites (built at the first use).</summary>
    public static Func<string, (TownBuilding Building, int Owner)?> Resolver(PopulationData data)
    {
        Dictionary<string, (TownBuilding, int)>? map = null;
        return key =>
        {
            if (map is null)
            {
                map = new Dictionary<string, (TownBuilding, int)>(StringComparer.Ordinal);
                foreach (var site in data.Sites)
                    foreach (var b in site.Buildings) map[b.Key] = (b, site.Index < data.SiteFactions.Count ? data.SiteFactions[site.Index] : -1);
            }
            return map.TryGetValue(key, out var found) ? found : null;
        };
    }

    /// <summary>Makes the machines the save holds (a key naming no town building of the data is dropped).</summary>
    public static void Read(FcsRecord camera, World world, ProductionSystem production, Func<string, (TownBuilding Building, int Owner)?> resolve)
    {
        if (!camera.Strings.ContainsKey(Key(0))) return;
        for (int i = 0; camera.Strings.TryGetValue(Key(i), out var key); i++)
        {
            if (resolve(key) is not { } found || production.TownWork(world, found.Building, found.Owner) is not { } m) continue;
            m.Status = (byte)camera.Ints.GetValueOrDefault(Key(i, " st"), m.Status);
            if (m.Output is { } o)
            {
                o.Progress = camera.Floats.GetValueOrDefault(Key(i, " out"));
                o.Count = Math.Clamp(camera.Ints.GetValueOrDefault(Key(i, " outn")), 0, o.Recipe.Capacity);
            }
            for (int k = 0; k < m.Inputs.Length; k++)
            {
                string part = k.ToString(CultureInfo.InvariantCulture);
                m.Inputs[k].Count = Math.Clamp(camera.Ints.GetValueOrDefault(Key(i, " in" + part)), 0, m.Inputs[k].Recipe.Capacity);
                m.Inputs[k].Level = camera.Floats.GetValueOrDefault(Key(i, " lv" + part));
            }
        }
        world.Machines.Touch();
    }
}
