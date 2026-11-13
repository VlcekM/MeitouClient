using Meitou.Simulation;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Navigation;
using Meitou.Simulation.Towns;

namespace Meitou.Game.DevTools.Agents;

/// <summary>
/// Agent-playtest control channel, player towns (docs/procedures/agent-play-api.md "Towns"): <c>towns</c>, <c>town</c> and <c>machines-power</c>
/// read what the simulation knows of placed towns and the player's outposts (<see cref="TownReport"/>); <c>dismantle-site</c> dismantles one of the
/// player's buildings through the command the building panel's Dismantle gives.
/// </summary>
sealed class AgentTowns(GameHost host)
{
    /// <summary>
    /// Registers the town commands with the control channel's command table.
    /// </summary>
    internal void RegisterControlTownCommands(Action<string, string, string, Action<IReadOnlyList<string>, DevConsole>> add)
    {
        add("towns", "towns", "JSON of every town the simulation knows (placed towns, then the player's outposts): number, kind, saved instance id, "
            + "name, owner faction, playerOwned, x, z, radius, public, level (outposts), site count, nest flag, power (outposts: generated, use, "
            + "battery charge and capacity)", ControlTowns);

        add("town", "town <number|name>", "the same JSON for one town (number from towns, or its name) with its buildings: id, building key, "
            + "complete, indoors, position", ControlTown);

        add("machines-power", "machines-power [town number|name]", "JSON per machine of the player: site, town, power grid it is on (null: none), "
            + "kind (consumer, generator, battery, none), demand, granted, satisfaction, hasPower; one outpost only when given", ControlMachinesPower);

        add("dismantle-site", "dismantle-site <site id>", "debug: dismantle one of the player's buildings at once, through the building panel's "
            + "confirmed Dismantle (refund, furniture lost, the outpost reassessed or removed), not the level editor's clear; queued, runs on the next tick "
            + "(step when paused); ids from town or observe.buildings.own", ControlDismantleSite);

        add("navnear", "navnear <x> <z> [radius]", "debug: JSON of the navmesh polygons of the loaded zones within radius (default 200) of the point: "
            + "zone, polygon, area, walkable flag, vertices; the zones under it and whether they are loaded; and the point the town refit's "
            + "snap takes for it (the 3D snap where the navmesh faces are known, else the 2D closest walkable point; snap.via says which; started at the "
            + "ground height, 500 box); radius at most 1000", ControlNavNear);
    }

    /// <summary>
    /// The <c>towns</c> command (usage and help at its registration in <see cref="RegisterControlTownCommands"/>).
    /// </summary>
    void ControlTowns(IReadOnlyList<string> args, DevConsole con)
    {
        if (!host.AgentWorld.Loaded(con)) return;
        if (args.Count > 0)
        {
            con.Error("usage: towns");
            return;
        }

        host.AgentChannel.PrintData(con, TownReport.ListJson(host.Session.World, host.PopData));
    }

    /// <summary>
    /// The <c>town</c> command (usage and help at its registration in <see cref="RegisterControlTownCommands"/>).
    /// </summary>
    void ControlTown(IReadOnlyList<string> args, DevConsole con)
    {
        if (!host.AgentWorld.Loaded(con)) return;
        if (args.Count == 0)
        {
            con.Error("usage: town <number|name>");
            return;
        }

        string typed = string.Join(' ', args);
        if (TownReport.Find(host.Session.World, host.PopData, typed, out _) is not { } town)
        {
            con.Error($"no town matches '{typed}'");
            return;
        }

        host.AgentChannel.PrintData(con, TownReport.TownJson(host.Session.World, host.PopData, town));
    }

    /// <summary>
    /// The <c>machines-power</c> command (usage and help at its registration in <see cref="RegisterControlTownCommands"/>).
    /// </summary>
    void ControlMachinesPower(IReadOnlyList<string> args, DevConsole con)
    {
        if (!host.AgentWorld.Loaded(con)) return;

        TownEntry? town = null;
        if (args.Count > 0)
        {
            string typed = string.Join(' ', args);
            town = TownReport.Find(host.Session.World, host.PopData, typed, out _);
            if (town is null)
            {
                con.Error($"no town matches '{typed}'");
                return;
            }
        }

        host.AgentChannel.PrintData(con, TownReport.MachinesJson(host.Session.World, town));
    }

    /// <summary>
    /// The <c>dismantle-site</c> command (usage and help at its registration in <see cref="RegisterControlTownCommands"/>): the checks the
    /// simulation makes (a site of the player's, not a level building) are made here too so that a refusal has a reason.
    /// </summary>
    void ControlDismantleSite(IReadOnlyList<string> args, DevConsole con)
    {
        if (!host.AgentWorld.Loaded(con)) return;
        if (args.Count != 1)
        {
            con.Error("usage: dismantle-site <site id>");
            return;
        }

        var world = host.Session.World;
        if (world.Buildings.Find(args[0]) is not { } site)
        {
            con.Error($"no site '{args[0]}' (ids: town <number|name>)");
            return;
        }

        if (site.LevelPlacement is not null || site.Owner != world.Player.Faction)
        {
            con.Error($"refused: {args[0]} is not a building of the player's (a level building cannot be dismantled)");
            return;
        }

        world.Commands.Enqueue(new ConfirmDismantle(site.Id) { Tick = world.Tick });
        con.Print($"dismantle of {site.Id} ({site.Record}, outpost {site.Town}) queued; it runs on the next tick");
    }

    /// <summary>
    /// The <c>navnear</c> command (usage and help at its registration in <see cref="RegisterControlTownCommands"/>).
    /// </summary>
    void ControlNavNear(IReadOnlyList<string> args, DevConsole con)
    {
        if (!host.AgentWorld.Loaded(con)) return;
        if (args.Count is < 2 or > 3 || !TryNumber(args[0], out float x) || !TryNumber(args[1], out float z))
        {
            Usage(con);
            return;
        }

        float radius = NavNear.DefaultRadius;
        if (args.Count == 3 && (!TryNumber(args[2], out radius) || radius <= 0))
        {
            Usage(con);
            return;
        }

        host.AgentChannel.PrintData(con, NavNear.Json(host.Session.World.Walkability, x, z, Math.Min(radius, NavNear.MaxRadius)));
    }

    /// <summary>
    /// Prints the usage of <c>navnear</c>.
    /// </summary>
    static void Usage(DevConsole con) => con.Error("usage: navnear <x> <z> [radius]");

    /// <summary>
    /// Reads a finite number written with a dot.
    /// </summary>
    static bool TryNumber(string text, out float value) =>
        float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) && float.IsFinite(value);
}
