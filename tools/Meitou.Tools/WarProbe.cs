using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.Save;
using Meitou.Simulation;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;

/// <summary>
/// <c>meitou-tools probe &lt;save&gt; wars [step...]</c>: the factions' running and queued campaigns (docs/game/factions-squads-towns.md
/// "Campaigns") with their names, phases, towns, platoons and the Upcoming Events lines, after each step in turn. Steps: <c>start &lt;campaign&gt;</c>
/// requests a campaign (string id or record name) of the faction that lists it against the player town nearest the leader and runs until it sets
/// out; <c>arrive</c> puts the platoons of every campaign travelling to that town at it and runs until they are there and loaded; <c>resave
/// &lt;folder&gt;</c> saves the world into the folder as the game saves it, loads it again and settles it as long (#861: raids lost on a reload).
/// </summary>
static class WarProbe
{
    /// <summary>
    /// The most ticks <c>start</c> and <c>arrive</c> run while waiting.
    /// </summary>
    const int WaitTicks = 6000;

    /// <summary>
    /// Lists the campaigns, then runs the steps in order, listing them again after each.
    /// </summary>
    public static void Run(ProbeWorld probe, GameInstall install, LoadOrder order, int ticks, List<string> args)
    {
        Console.WriteLine("== loaded");
        List(probe);

        var current = probe;
        for (int i = 0; i < args.Count; i++)
        {
            string step = args[i];
            bool done = step switch
            {
                "start" when i + 1 < args.Count => Start(current, args[++i]),
                "arrive" => Arrive(current),
                "resave" when i + 1 < args.Count => Resave(ref current, install, order, ticks, args[++i]),
                _ => Failed("unknown step " + step),
            };
            if (!done) break;

            Console.WriteLine("== after " + step);
            List(current);
        }

        if (!ReferenceEquals(current, probe)) current.Dispose();
    }

    /// <summary>
    /// Saves the world into <paramref name="folder"/> as the game saves it, loads it again and settles it <paramref name="ticks"/> ticks; the world
    /// loaded replaces <paramref name="current"/> (the one before, unless the first, is disposed). False when the save does not load.
    /// </summary>
    static bool Resave(ref ProbeWorld current, GameInstall install, LoadOrder order, int ticks, string folder)
    {
        var game = SaveCapture.Capture(current.World, current.Data, Clock(current.World), current.Loaded, Options(current, order));
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        game.Write(folder);

        if (ProbeWorld.Load(install, order, folder, navmesh: false) is not { } again) return Failed("the save did not load");

        again.Tick(ticks);
        current = again;
        return true;
    }

    /// <summary>
    /// The player town nearest the squad leader; null when the player has none.
    /// </summary>
    static PlayerTown? NearestTown(ProbeWorld probe)
    {
        var leader = probe.LeaderPosition;
        return probe.World.Buildings.Towns.MinBy(t => Vector3.DistanceSquared(t.Position, leader));
    }

    /// <summary>
    /// Requests the campaign named <paramref name="name"/> against the player town nearest the leader and runs until it is running. False, with the
    /// reason, when it cannot.
    /// </summary>
    static bool Start(ProbeWorld probe, string name)
    {
        var world = probe.World;
        var population = world.SystemOf<PopulationSystem>()!;
        var town = NearestTown(probe);
        var c = Campaign(probe, population, name);
        var factions = probe.Data.Factions;
        int faction = c is null ? -1 : Enumerable.Range(0, factions.Count).FirstOrDefault(f => factions[f].Campaigns.Any(l => l.Id == c.Id), -1);
        if (town is null || c is null || faction < 0)
            return Failed($"cannot start '{name}': town {town?.Name ?? "none"}, campaign {c?.Id ?? "none"}, faction {faction}");

        population.RequestCampaign(world, faction, c.Id, CampaignTown.OfPlayer(town.Number));
        var war = population.Wars[faction];
        Console.WriteLine($"requested '{c.Name}' of {factions[faction].Name} against '{town.Name}'");
        return RunUntil(probe, () => war.Actives.Any(a => a.CampaignId == c.Id)) || Failed("it never started");
    }

    /// <summary>
    /// Puts the platoons of every campaign travelling to the player town nearest the leader at the town and runs until each is in phase 1 or later
    /// with its platoons loaded, printing the messages posted meanwhile. False, with the reason, when none travels there or they never get there.
    /// </summary>
    static bool Arrive(ProbeWorld probe)
    {
        var world = probe.World;
        var population = world.SystemOf<PopulationSystem>()!;
        if (NearestTown(probe) is not { } town) return Failed("no player town");

        var target = CampaignTown.OfPlayer(town.Number);
        var raids = population.Wars.SelectMany(w => w.Actives).Where(a => a.Target == target && a.Phase == 0).ToList();
        if (raids.Count == 0) return Failed("no campaign travels to " + town.Name);

        var at = new Vector2(town.Position.X, town.Position.Z) + new Vector2(30, 30);
        foreach (int id in raids.SelectMany(a => a.Platoons))
            if (world.Platoons.Find(id) is { } p) p.Position = at;

        bool There() => raids.All(a => a.Phase >= 1 && a.Platoons.All(id => world.Platoons.Find(id) is { State: PlatoonState.Loaded }));
        world.TakeMessages();
        double from = world.GameHours;
        bool there = RunUntil(probe, There);
        Console.WriteLine($"after {world.GameHours - from:0.00} h: {string.Join(" | ", world.TakeMessages())}");
        return there || Failed("they never arrived and loaded");
    }

    /// <summary>
    /// The campaign of a string id, else of a FACTION_CAMPAIGN record name.
    /// </summary>
    static CampaignData? Campaign(ProbeWorld probe, PopulationSystem population, string name)
    {
        if (population.CampaignOf(name) is { } byId) return byId;

        var record = probe.Db.OfType(Meitou.Data.Fcs.FcsRecordType.FactionCampaign).FirstOrDefault(r => r.Name == name);
        return record is null ? null : population.CampaignOf(record.StringId);
    }

    /// <summary>
    /// Ticks 50 at a time until <paramref name="done"/>, at most <see cref="WaitTicks"/>.
    /// </summary>
    static bool RunUntil(ProbeWorld probe, Func<bool> done)
    {
        for (int t = 0; t < WaitTicks && !done(); t += 50) probe.Tick(50);
        return done();
    }

    /// <summary>
    /// Prints why a step gave up; false.
    /// </summary>
    static bool Failed(string why)
    {
        Console.WriteLine("wars: " + why);
        return false;
    }

    /// <summary>
    /// The save clock of the world's game hours.
    /// </summary>
    static SaveClock Clock(World world)
    {
        double hours = world.GameHours;
        return new SaveClock((int)(hours / 24), (int)(hours % 24), (int)(hours % 1 * 60));
    }

    /// <summary>
    /// The capture options the game's save uses that a headless world has: data files, town placements, levels.
    /// </summary>
    static SaveCaptureOptions Options(ProbeWorld probe, LoadOrder order) => new()
    {
        DataFiles = [.. order.Entries.Select(e => e.Name)],
        Placements = [.. probe.Levels.Towns()],
        Levels = probe.Levels,
        Seed = 861,
    };

    /// <summary>
    /// Every faction with running or queued campaigns: per running one its record and display names, phase, towns, men and platoons; per queued one
    /// its names, target and the hours until it starts; then the Faction tab's Upcoming Events lines.
    /// </summary>
    static void List(ProbeWorld probe)
    {
        var world = probe.World;
        if (world.SystemOf<PopulationSystem>() is not { } population) return;

        foreach (var war in population.Wars)
        {
            if (war.Actives.Count == 0 && war.Requests.Count == 0) continue;

            Console.WriteLine($"{probe.Data.Factions[war.Faction].Name}:");
            foreach (var inst in war.Actives) Active(probe, population, inst);
            foreach (var r in war.Requests)
            {
                var c = population.CampaignOf(r.CampaignId);
                Console.WriteLine($"  queued '{c?.Name}' shown '{c?.DisplayName}' target {Town(probe, r.Target)} in {r.Due - world.GameHours:0.0} h");
            }
        }

        Console.WriteLine("Upcoming Events:");
        foreach (var line in population.UpcomingEvents(world, f => f >= 0 && f < probe.Data.Factions.Count ? probe.Data.Factions[f].Name : null))
            Console.WriteLine($"  {line.Label} {line.Value}{(double.IsNaN(line.Hours) ? "" : $" in {line.Hours:0.0} h")}");
    }

    /// <summary>
    /// One running campaign and its platoons.
    /// </summary>
    static void Active(ProbeWorld probe, PopulationSystem population, CampaignInstance inst)
    {
        var world = probe.World;
        var c = population.CampaignOf(inst.CampaignId);
        Console.WriteLine($"  #{inst.Id} '{c?.Name}' shown '{c?.DisplayName}' phase {inst.Phase} target {Town(probe, inst.Target)} "
            + $"home {Town(probe, inst.Home)} men {inst.Men}/{inst.Required}");

        foreach (int id in inst.Platoons)
        {
            string what = world.Platoons.Find(id) is { } p
                ? $"{p.TemplateId} {p.State} at {p.Position:F0}, living {population.LivingOf(world, p)}, size {p.Size}"
                : "gone";
            Console.WriteLine($"    platoon {id}{(id == inst.Leader ? " (leader)" : "")}: {what}");
        }
    }

    /// <summary>
    /// A campaign town as text: the site's town name, the player town's number and name, or none.
    /// </summary>
    static string Town(ProbeWorld probe, CampaignTown t)
    {
        if (t.Site >= 0) return $"'{probe.Data.Sites[t.Site].Town.Name}'";
        if (t.IsNone) return "none";

        return probe.World.Buildings.FindTown(t.PlayerTown) is { } town ? $"player {t.PlayerTown} '{town.Name}'" : $"player {t.PlayerTown} (gone)";
    }
}
