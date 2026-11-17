using System.Globalization;
using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation.Items;
using Meitou.Simulation.Population;
using Meitou.Simulation.Production;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The machines of NPC town buildings in a save (docs/formats/save.md "Town machines"). As the original does, a town machine's state is in its
/// building's GAMESTATE_BUILDING of the zone file (<see cref="MachineSave.WriteProduction"/>: <c>resource mult</c>, <c>production amount</c>,
/// <c>consumption amount-&lt;k&gt;</c>, a farm's keys) and its items in the INVENTORY_STATE the building lists (sections <c>in1</c>, <c>in2</c>, ... and
/// <c>out</c>); a machine is read back from them when its town registers and the machine is made (<see cref="Restore"/>). The machine of a
/// building's layout object (a refinery, hydroponic farm or battery bank inside a house) uses its own entry the same way, matched by record and
/// position (<see cref="SavedPieces"/>), a battery bank's <c>batterycharge</c> with it. An object with no entry saves nothing: the original makes
/// that interior again from its layout (its zone has no file, or the house's interior was not serialised), so its objects start new, a battery
/// bank empty. A machine whose building has no state record in the save keeps Meitou-only keys on CAMERA, which older Meitou
/// saves wrote for every town machine: <c>meitou tm&lt;i&gt;</c> (the building's run-time key), <c>meitou tm&lt;i&gt; out</c>
/// (progress, float),
/// <c>meitou tm&lt;i&gt; outn</c> (output items, int), <c>meitou tm&lt;i&gt; st</c> (status, int),
/// <c>meitou tm&lt;i&gt; in&lt;k&gt;</c> (items of input k, int) and <c>meitou tm&lt;i&gt; lv&lt;k&gt;</c> (its level, float); a farm's the farm keys
/// as <c>meitou tm&lt;i&gt; pg</c> (<c>plant growth</c>), <c>pd</c> (<c>plant death</c>), <c>ps</c> (<c>plant start</c>), <c>pt</c>
/// (<c>save time</c>), floats, and <c>ph</c> (<c>plants harvested</c>), int. The original ignores them (keys are read by name).
/// </summary>
public static class TownMachineSave
{
    #region Zone states

    /// <summary>
    /// Writes the town machines into their buildings' states and inventories, the ones whose building has no state into CAMERA keys (the old
    /// CAMERA keys removed first); a layout object's machine with no entry of its own is not saved (<see cref="TownMachineSave"/>).
    /// </summary>
    public static void Write(SaveGame game, World world)
    {
        var camera = game.Camera.Record;
        Clear(camera, Prefix);

        var instances = Instances(game);
        var pieces = new SavedPieces(game);
        Dictionary<string, TownLayoutBuilding>? layouts = null;
        int n = 0;
        foreach (var m in world.Machines.All)
        {
            if (!ProductionSystem.IsTownWork(m.Site)) continue;

            string key = m.Site[ProductionSystem.TownWorkPrefix.Length..];
            if (instances.TryGetValue(key, out var placed) && placed.State is { } state)
            {
                WriteInto(placed.Zone, placed.Instance, state, m, world.GameHours);
                continue;
            }

            if (IsPieceKey(key))
            {
                layouts ??= Pieces(world);
                if (layouts.GetValueOrDefault(key) is { } piece && pieces.Of(piece) is { State: { } pieceState } entry)
                    WriteInto(entry.Zone, entry.Instance, pieceState, m, world.GameHours);
                continue;
            }

            camera.Strings[Key(n)] = key;
            WriteState(camera, Key(n), m);
            WriteFarm(camera, Key(n), m, world.GameHours);
            n++;
        }
    }

    /// <summary>
    /// Whether a town machine's key names a layout object (<see cref="TownLayoutBuilding.Id"/>: the house's key, "/" and its place), not a building.
    /// </summary>
    static bool IsPieceKey(string key) => key.Contains('/');

    /// <summary>
    /// Writes a town machine into its building-list entry: the production keys into its GAMESTATE_BUILDING, a battery bank's charge too
    /// (<c>batterycharge</c>, as the original's <c>FUN_1402a14b0</c> writes it), and its items into the INVENTORY_STATE the entry lists.
    /// </summary>
    static void WriteInto(SaveZone zone, FcsInstance instance, FcsRecord state, Machine m, double gameHours)
    {
        MachineSave.WriteProduction(state, m, gameHours);
        if (m.Data.IsBattery) state.Floats["batterycharge"] = m.Charge;

        MachineSave.WriteInventory(zone.File, zone.Coordinate, instance, null, MachineSave.Items(m));
    }

    /// <summary>
    /// The towns' layout objects by <see cref="TownLayoutBuilding.Id"/> (the house's key, "/" and its place in the layout), the keys of their
    /// machines.
    /// </summary>
    static Dictionary<string, TownLayoutBuilding> Pieces(World world)
    {
        var map = new Dictionary<string, TownLayoutBuilding>(StringComparer.Ordinal);
        foreach (var site in world.SystemOf<PopulationSystem>()?.Data.Sites ?? [])
            foreach (var b in site.Buildings)
                foreach (var piece in b.LayoutBuildings) map[piece.Id] = piece;

        return map;
    }

    /// <summary>
    /// What the machine of a town layout object just made takes from the loaded save (<see cref="ProductionSystem.RestorePieceMachine"/>): its own
    /// entry's state (<see cref="SavedPieces"/>), as a building's (<see cref="Restore"/>), and a battery bank's switch and charge
    /// (<c>power on</c>, <c>batterycharge</c>; <c>FUN_1402a1750</c>); nothing from an entry of a zone that loads fresh.
    /// </summary>
    public static Action<TownLayoutBuilding, Machine> PieceRestorer(SaveGame save, ItemFactory factory)
    {
        SavedPieces? pieces = null;
        return (piece, m) =>
        {
            pieces ??= new SavedPieces(save);
            if (pieces.Of(piece) is not { Fresh: false } entry) return;

            Restore(m, (entry.Zone, entry.Instance, entry.State), factory);
            if (entry.State is { } state && m.Data.IsBattery) ReadBattery(state, m);
        };
    }

    /// <summary>
    /// A battery bank's <c>power on</c> (missing: off) and <c>batterycharge</c> (missing: 0) from its state, as <c>FUN_1402a1750</c> loads them.
    /// </summary>
    static void ReadBattery(FcsRecord state, Machine m)
    {
        m.PowerOn = state.Bools.GetValueOrDefault("power on");
        m.Charge = Math.Max(0, state.Floats.GetValueOrDefault("batterycharge"));
    }

    /// <summary>
    /// What a town machine just made takes from the loaded save (<see cref="ProductionSystem.RestoreTownMachine"/>): its building's state, read by
    /// <see cref="Restore"/> from the zones' instances (indexed at the first call).
    /// </summary>
    public static Action<TownBuilding, Machine> Restorer(SaveGame save, ItemFactory factory)
    {
        Dictionary<string, (SaveZone Zone, FcsInstance Instance, FcsRecord? State)>? instances = null;
        return (building, m) =>
        {
            instances ??= Instances(save);
            if (instances.TryGetValue(building.Key, out var placed)) Restore(m, placed, factory);
        };
    }

    /// <summary>
    /// A town machine takes its building's saved state: the production keys of its GAMESTATE_BUILDING and the items of the INVENTORY_STATE its
    /// instance lists.
    /// </summary>
    static void Restore(Machine m, (SaveZone Zone, FcsInstance Instance, FcsRecord? State) placed, ItemFactory factory)
    {
        if (placed.State is { } state) MachineSave.ReadProduction(state, m);
        if (MachineSave.ListedInventory(placed.Zone.File, placed.Instance) is { } inventory)
            MachineSave.PutItems(m, SaveInventory.Read(placed.Zone.File, inventory), factory);
    }

    /// <summary>
    /// The zones' building instances with their zone and GAMESTATE_BUILDING (null when it lists none) by <see cref="BuildingKey"/>.
    /// </summary>
    static Dictionary<string, (SaveZone Zone, FcsInstance Instance, FcsRecord? State)> Instances(SaveGame save)
    {
        var map = new Dictionary<string, (SaveZone, FcsInstance, FcsRecord?)>(StringComparer.Ordinal);
        foreach (var zone in save.Zones.Values)
        {
            if (zone.BuildingList is not { } list) continue;

            var states = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
            foreach (var r in zone.BuildingStates) states[r.StringId] = r;

            foreach (var i in list.Instances)
            {
                var state = i.States.Select(id => states.GetValueOrDefault(id)).FirstOrDefault(r => r is not null);
                map[BuildingKey.Of(zone.Coordinate, i.Id)] = (zone, i, state);
            }
        }

        return map;
    }

    #endregion

    #region Camera keys

    /// <summary>
    /// The prefix of every town machine key.
    /// </summary>
    const string Prefix = "meitou tm";

    /// <summary>
    /// Makes the machines the save's CAMERA keys hold (<see cref="ProductionSystem.TownWork(World, string)"/>); a key naming no town building in the
    /// data is dropped, and so is a layout object's, whose interior the original makes again (<see cref="TownMachineSave"/>).
    /// </summary>
    public static void Read(FcsRecord camera, World world, ProductionSystem production)
    {
        if (!camera.Strings.ContainsKey(Key(0))) return;
        for (int i = 0; camera.Strings.TryGetValue(Key(i), out var key); i++)
        {
            if (IsPieceKey(key) || production.TownWork(world, key) is not { } m) continue;
            ReadState(camera, Key(i), m);
            ReadFarm(camera, Key(i), m);
        }

        world.Machines.Touch();
    }

    /// <summary>
    /// Removes the entries of the keys <paramref name="prefix"/>&lt;i&gt; (each with its parts) from the camera record.
    /// </summary>
    internal static void Clear(FcsRecord camera, string prefix)
    {
        for (int i = 0; camera.Strings.ContainsKey(prefix + i.ToString(CultureInfo.InvariantCulture)); i++)
        {
            string entry = prefix + i.ToString(CultureInfo.InvariantCulture);
            var keys = camera.Strings.Keys.Concat(camera.Floats.Keys).Concat(camera.Ints.Keys).Concat(camera.Bools.Keys)
                .Where(k => k.StartsWith(entry, StringComparison.Ordinal) && (k.Length == entry.Length || k[entry.Length] == ' '))
                .ToList();
            foreach (var k in keys)
            {
                camera.Strings.Remove(k);
                camera.Floats.Remove(k);
                camera.Ints.Remove(k);
                camera.Bools.Remove(k);
            }
        }
    }

    /// <summary>
    /// Writes a machine's status, output and inputs as the parts of the entry <paramref name="entry"/>.
    /// </summary>
    internal static void WriteState(FcsRecord camera, string entry, Machine m)
    {
        camera.Ints[entry + " st"] = m.Status;
        if (m.Output is { } o)
        {
            camera.Floats[entry + " out"] = o.Progress;
            camera.Ints[entry + " outn"] = o.Count;
        }

        for (int k = 0; k < m.Inputs.Length; k++)
        {
            string part = k.ToString(CultureInfo.InvariantCulture);
            camera.Ints[entry + " in" + part] = m.Inputs[k].Count;
            camera.Floats[entry + " lv" + part] = m.Inputs[k].Level;
        }
    }

    /// <summary>
    /// Reads a machine's status, output and inputs from the parts of the entry <paramref name="entry"/> (counts clamped to the slots' capacities).
    /// </summary>
    internal static void ReadState(FcsRecord camera, string entry, Machine m)
    {
        m.Status = (byte)camera.Ints.GetValueOrDefault(entry + " st", m.Status);
        if (m.Output is { } o)
        {
            o.Progress = camera.Floats.GetValueOrDefault(entry + " out");
            o.Count = Math.Clamp(camera.Ints.GetValueOrDefault(entry + " outn"), 0, o.Recipe.Capacity);
        }

        for (int k = 0; k < m.Inputs.Length; k++)
        {
            string part = k.ToString(CultureInfo.InvariantCulture);
            m.Inputs[k].Count = Math.Clamp(camera.Ints.GetValueOrDefault(entry + " in" + part), 0, m.Inputs[k].Recipe.Capacity);
            m.Inputs[k].Level = camera.Floats.GetValueOrDefault(entry + " lv" + part);
        }
    }

    /// <summary>
    /// The key of entry <paramref name="i"/>.
    /// </summary>
    static string Key(int i) => Prefix + i.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Writes a farm's growth, death, start, harvested plants and the hour it last grew (the game's hour when it has not grown yet) as the parts of
    /// the entry <paramref name="entry"/>; nothing for a machine that is no farm.
    /// </summary>
    static void WriteFarm(FcsRecord camera, string entry, Machine m, double gameHours)
    {
        if (m.Farm is not { } f) return;

        camera.Floats[entry + " pg"] = f.Growth;
        camera.Floats[entry + " pd"] = f.Death;
        camera.Floats[entry + " ps"] = f.Start;
        camera.Floats[entry + " pt"] = (float)(double.IsNaN(f.LiveHours) ? gameHours : f.LiveHours);
        camera.Ints[entry + " ph"] = f.Harvested;
    }

    /// <summary>
    /// Reads a farm's keys from the parts of the entry <paramref name="entry"/>, the plants as a player farm's load sets them
    /// (<see cref="MachineSave"/>: the harvested ones at -10, the rest at the growth); nothing without them (an older save keeps the random start).
    /// </summary>
    static void ReadFarm(FcsRecord camera, string entry, Machine m)
    {
        if (m.Farm is not { } f || !camera.Floats.TryGetValue(entry + " pg", out float growth)) return;

        f.Growth = growth;
        f.Death = camera.Floats.GetValueOrDefault(entry + " pd");
        f.Start = camera.Floats.GetValueOrDefault(entry + " ps");
        f.Harvested = Math.Clamp(camera.Ints.GetValueOrDefault(entry + " ph"), 0, f.Plants.Length);
        if (camera.Floats.TryGetValue(entry + " pt", out float saved)) f.LiveHours = saved;
        for (int i = 0; i < f.Plants.Length; i++) f.Plants[i] = i < f.Harvested ? -10 : f.Growth;
    }

    #endregion
}
