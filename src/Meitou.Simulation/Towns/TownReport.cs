using System.Numerics;
using System.Text;
using System.Text.Json;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Population;
using Meitou.Simulation.Production;

namespace Meitou.Simulation.Towns;

/// <summary>
/// One town the simulation knows, as the debug commands name it: a town placed in the level data or a player outpost.
/// </summary>
/// <param name="Number">
/// The number the debug commands use: 1, 2, ... over the placed towns in level order, then the outposts in creation order.
/// </param>
/// <param name="Placed">
/// The placed town (null for an outpost).
/// </param>
/// <param name="Outpost">
/// The player outpost (null for a placed town).
/// </param>
public sealed record TownEntry(int Number, TownSite? Placed, PlayerTown? Outpost)
{
    /// <summary>
    /// The town's name.
    /// </summary>
    public string Name => Outpost?.Name ?? Placed!.Town.Name;

    /// <summary>
    /// The town's centre.
    /// </summary>
    public Vector3 Position => Outpost?.Position ?? Placed!.Position;

    /// <summary>
    /// The town's radius.
    /// </summary>
    public float Radius => Outpost?.Radius ?? Placed!.Town.Radius;
}

/// <summary>
/// Read-only description of every town the world knows (the agent control channel's <c>towns</c>, <c>town</c> and <c>machines-power</c> commands,
/// docs/procedures/agent-play-api.md): placed towns of the level data and the player's outposts, with their power grids, buildings and machines.
/// Reads the world on the caller's thread; call it between ticks.
/// </summary>
public static class TownReport
{
    /// <summary>
    /// The entries a number or name can pick: placed towns that are not nest markers or nameless, in level order, then the outposts.
    /// </summary>
    public static IReadOnlyList<TownEntry> Entries(World world, PopulationData? data)
    {
        var list = new List<TownEntry>();
        foreach (var site in data?.Sites ?? [])
            list.Add(new TownEntry(list.Count + 1, site, null));

        foreach (var outpost in world.Buildings.Towns)
            list.Add(new TownEntry(list.Count + 1, null, outpost));

        return list;
    }

    /// <summary>
    /// The town a typed number or name means (ignoring case): a number picks that entry; a name the exact one, else the shortest name starting with
    /// it. Nest markers and nameless placed towns are found by number only. <paramref name="others"/> lists up to five other names the typed text
    /// also matched.
    /// </summary>
    public static TownEntry? Find(World world, PopulationData? data, string typed, out List<string> others)
    {
        others = [];
        var all = Entries(world, data);
        if (typed.Length == 0) return null;
        if (int.TryParse(typed, out int number)) return number >= 1 && number <= all.Count ? all[number - 1] : null;

        var named = all.Where(e => e.Outpost is not null || e.Placed!.Town.Type is not (TownType.NestMarker or TownType.Null) && e.Name.Length > 0).ToList();
        var exact = named.Where(e => string.Equals(e.Name, typed, StringComparison.OrdinalIgnoreCase)).ToList();
        var hits = exact.Count > 0 ? exact : [.. named.Where(e => e.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Name.Length)
            .ThenBy(e => e.Name, StringComparer.Ordinal)];
        if (hits.Count == 0) return null;

        others = [.. hits.Select(e => e.Name).Distinct().Where(n => n != hits[0].Name).Take(5)];
        return hits[0];
    }

    /// <summary>
    /// The <c>towns</c> reply: <c>{count, towns[]}</c>, one object per town (see <see cref="WriteTown"/>).
    /// </summary>
    public static string ListJson(World world, PopulationData? data)
    {
        var power = PowerOf(world);
        return Write(w =>
        {
            var entries = Entries(world, data);
            w.WriteStartObject();
            w.WriteNumber("count", entries.Count);
            w.WriteStartArray("towns");
            foreach (var e in entries) WriteTown(w, world, data, e, power, sites: false);
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    /// <summary>
    /// The <c>town</c> reply: the town's object with its <c>sites</c> (a player outpost's buildings and sites, or a placed town's level buildings).
    /// </summary>
    public static string TownJson(World world, PopulationData? data, TownEntry entry)
    {
        var power = PowerOf(world);
        return Write(w => WriteTown(w, world, data, entry, power, sites: true));
    }

    /// <summary>
    /// The <c>machines-power</c> reply: <c>{count, machines[]}</c>, each machine with its site, town and grid (the key of the grid it joins, as the production system shares it,
    /// null when none: unfinished, town work or no power part) and the power it gets; <paramref name="town"/> limits it to one outpost.
    /// </summary>
    public static string MachinesJson(World world, TownEntry? town)
    {
        var reg = world.Buildings;
        var rows = new List<(Machine Machine, BuildSite? Site)>();
        foreach (var m in world.Machines.All)
        {
            var site = reg.Find(m.Site);
            if (town is not null && (town.Outpost is null || site?.Town != town.Outpost.Number)) continue;
            rows.Add((m, site));
        }

        return Write(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("count", rows.Count);
            w.WriteStartArray("machines");
            foreach (var (m, site) in rows) WriteMachine(w, world, m, site);
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    #region Writing

    /// <summary>
    /// Runs <paramref name="body"/> against a JSON writer and returns the text.
    /// </summary>
    static string Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream)) body(w);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// A number rounded to two decimals.
    /// </summary>
    static double R2(double v) => Math.Round(v, 2);

    /// <summary>
    /// The outposts' power totals after the last tick, by outpost number.
    /// </summary>
    static Dictionary<int, TownPower> PowerOf(World world) =>
        world.SystemOf<ProductionSystem>()?.OutpostView(world).ToDictionary(o => o.Number, o => o.Power) ?? [];

    /// <summary>
    /// One town: number, kind (<c>placed</c> or <c>outpost</c>), saved instance id (null until a save names an outpost), name, owner faction, whether
    /// the player owns it, centre, radius, public flag, level (outposts), building count, nest flag and the power grid (outposts: generated, demand,
    /// charging, draining, charge, capacity; null for a placed town); with <paramref name="sites"/> also the buildings.
    /// </summary>
    static void WriteTown(Utf8JsonWriter w, World world, PopulationData? data, TownEntry e, Dictionary<int, TownPower> power, bool sites)
    {
        w.WriteStartObject();
        w.WriteNumber("number", e.Number);
        w.WriteString("kind", e.Outpost is null ? "placed" : "outpost");
        if (e.Outpost is { } o) w.WriteNumber("outpost", o.Number);
        else w.WriteNumber("siteIndex", e.Placed!.Index);
        w.WriteString("instance", e.Outpost is null ? e.Placed!.InstanceId : e.Outpost.SaveInstance);
        w.WriteString("name", e.Name);

        int owner = e.Outpost?.Owner ?? data?.SiteFactions.ElementAtOrDefault(e.Placed!.Index) ?? -1;
        w.WriteString("owner", data is not null && owner >= 0 && owner < data.Factions.Count ? data.Factions[owner].Name : null);
        w.WriteBoolean("playerOwned", owner >= 0 && owner == world.Player.Faction);
        w.WriteNumber("x", R2(e.Position.X));
        w.WriteNumber("z", R2(e.Position.Z));
        w.WriteNumber("radius", R2(e.Radius));
        w.WriteBoolean("public", e.Outpost?.Public ?? e.Placed!.Town.IsPublic);
        if (e.Outpost is { } level) w.WriteNumber("level", world.SystemOf<PopulationSystem>()?.TownLevel(world, level.Number) ?? -1);
        else w.WriteNull("level");

        WriteCounts(w, world, e);
        w.WriteBoolean("nest", e.Placed?.Town.Type == TownType.NestMarker);
        WritePower(w, e, power);
        if (sites) WriteSites(w, world, e);
        w.WriteEndObject();
    }

    /// <summary>
    /// The building count: an outpost's sites (<c>sites</c>, of which <c>complete</c>), a placed town's level buildings (<c>sites</c>).
    /// </summary>
    static void WriteCounts(Utf8JsonWriter w, World world, TownEntry e)
    {
        if (e.Outpost is not { } o)
        {
            w.WriteNumber("sites", e.Placed!.Buildings.Count);
            return;
        }

        var own = world.Buildings.All.Where(s => s.Town == o.Number).ToList();
        w.WriteNumber("sites", own.Count);
        w.WriteNumber("complete", own.Count(s => s.Complete));
    }

    /// <summary>
    /// The <c>power</c> object of an outpost: grid generation, use (demand), battery flows, charge and capacity; null for a placed town.
    /// </summary>
    static void WritePower(Utf8JsonWriter w, TownEntry e, Dictionary<int, TownPower> power)
    {
        if (e.Outpost is not { } o)
        {
            w.WriteNull("power");
            return;
        }

        var p = power.GetValueOrDefault(o.Number);
        w.WriteStartObject("power");
        w.WriteNumber("generated", R2(p.Generated));
        w.WriteNumber("use", R2(p.Demand));
        w.WriteNumber("charging", R2(p.Charging));
        w.WriteNumber("draining", R2(p.Draining));
        w.WriteNumber("batteryCharge", R2(p.Charge));
        w.WriteNumber("batteryCapacity", R2(p.Capacity));
        w.WriteEndObject();
    }

    /// <summary>
    /// The <c>buildings</c> array: an outpost's sites (id, building key, complete, indoors, position) or a placed town's level buildings
    /// (id, building key, position).
    /// </summary>
    static void WriteSites(Utf8JsonWriter w, World world, TownEntry e)
    {
        w.WriteStartArray("buildings");
        if (e.Outpost is { } o)
        {
            foreach (var s in world.Buildings.All.Where(s => s.Town == o.Number))
            {
                w.WriteStartObject();
                w.WriteString("id", s.Id);
                w.WriteString("building", s.Record);
                w.WriteString("name", s.Name);
                w.WriteBoolean("complete", s.Complete);
                w.WriteBoolean("indoors", world.Machines.Find(s.Id)?.Indoors ?? s.Parent is not null);
                WritePosition(w, s.Position);
                w.WriteEndObject();
            }
        }
        else
        {
            foreach (var b in e.Placed!.Buildings)
            {
                w.WriteStartObject();
                w.WriteString("id", b.InstanceId);
                w.WriteString("building", b.BuildingId);
                w.WriteString("name", b.Name);
                WritePosition(w, b.Position);
                w.WriteEndObject();
            }
        }

        w.WriteEndArray();
    }

    /// <summary>
    /// The <c>x</c>, <c>y</c>, <c>z</c> of a position.
    /// </summary>
    static void WritePosition(Utf8JsonWriter w, Vector3 p)
    {
        w.WriteNumber("x", R2(p.X));
        w.WriteNumber("y", R2(p.Y));
        w.WriteNumber("z", R2(p.Z));
    }

    /// <summary>
    /// One machine: site, building record and name, town (outpost number or 0), grid (the key of the power grid it is on, as the production system shares it; null when not on one), its power
    /// part (<c>consumer</c>, <c>generator</c>, <c>battery</c> or <c>none</c>), demand and granted power, the satisfaction (granted / demand, 1 with no
    /// demand), whether it has power now, and its power switch.
    /// </summary>
    static void WriteMachine(Utf8JsonWriter w, World world, Machine m, BuildSite? site)
    {
        bool onGrid = site is { Complete: true } && !ProductionSystem.IsTownWork(m.Site)
            && (m.Data.IsConsumer || m.Data.IsGenerator || m.Data.IsBattery);
        w.WriteStartObject();
        w.WriteString("site", m.Site);
        w.WriteString("building", site?.Record);
        w.WriteString("name", m.Data.Name);
        w.WriteNumber("town", site?.Town ?? 0);
        w.WriteString("townName", site is { } s && world.Buildings.FindTown(s.Town) is { } t ? t.Name : null);
        if (onGrid) w.WriteNumber("grid", world.SystemOf<ProductionSystem>()?.GridOf(world, site!) ?? site!.Town);
        else w.WriteNull("grid");
        w.WriteString("kind", m.Data.IsBattery ? "battery" : m.Data.IsGenerator ? "generator" : m.Data.IsConsumer ? "consumer" : "none");
        w.WriteNumber("demand", R2(m.Demand));
        w.WriteNumber("granted", R2(m.Granted));
        w.WriteNumber("satisfaction", R2(m.Power));
        w.WriteBoolean("hasPower", m.HasPower);
        w.WriteBoolean("powerOn", m.PowerOn);
        w.WriteEndObject();
    }

    #endregion
}
