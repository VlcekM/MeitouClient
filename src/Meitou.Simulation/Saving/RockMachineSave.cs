using System.Globalization;
using Meitou.Data.Fcs;
using Meitou.Simulation.Production;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The machines of worked rocks on the CAMERA record (docs/formats/save.md "Rock machines"): a rock has no building state in the zone files, so
/// its machine is Meitou-only keys: <c>meitou rock&lt;i&gt;</c> (the rock's natural site id), bool <c>meitou rock&lt;i&gt; own</c> (the player's
/// faction has claimed it), int <c>meitou rock&lt;i&gt; town</c> (its player town's number, absent: none) and the status, output and inputs as the
/// town machines' parts (<see cref="TownMachineSave"/>). The original ignores them (keys are read by name).
/// </summary>
public static class RockMachineSave
{
    /// <summary>
    /// The prefix of every rock machine key.
    /// </summary>
    const string Prefix = "meitou rock";

    /// <summary>
    /// The key of entry <paramref name="i"/>.
    /// </summary>
    static string Key(int i) => Prefix + i.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Writes the rocks' machines over the camera record's entries (the old ones removed first).
    /// </summary>
    public static void Write(FcsRecord camera, World world, ProductionSystem production)
    {
        TownMachineSave.Clear(camera, Prefix);

        int n = 0;
        foreach (var m in world.Machines.All)
        {
            if (!ProductionSystem.IsNatural(m.Site)) continue;

            var (owner, town) = production.RockClaim(m.Site);
            camera.Strings[Key(n)] = m.Site;
            if (owner >= 0 && owner == world.Player.Faction) camera.Bools[Key(n) + " own"] = true;
            if (town != 0) camera.Ints[Key(n) + " town"] = town;
            TownMachineSave.WriteState(camera, Key(n), m);
            n++;
        }
    }

    /// <summary>
    /// Makes the rocks' machines the save holds (an id naming no rock of the data is dropped); a claimed rock is the player's faction's.
    /// </summary>
    public static void Read(FcsRecord camera, World world, ProductionSystem production)
    {
        if (!camera.Strings.ContainsKey(Key(0))) return;
        for (int i = 0; camera.Strings.TryGetValue(Key(i), out var id); i++)
        {
            int owner = camera.Bools.GetValueOrDefault(Key(i) + " own") ? world.Player.Faction : -1;
            int town = camera.Ints.GetValueOrDefault(Key(i) + " town");
            if (production.RestoreRock(world, id, owner, town) is not { } m) continue;
            TownMachineSave.ReadState(camera, Key(i), m);
        }

        world.Machines.Touch();
    }
}
