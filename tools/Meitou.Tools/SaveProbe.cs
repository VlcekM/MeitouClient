using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Simulation;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Saving;

/// <summary>
/// <c>meitou-tools probe</c>: loads a save headless (a feedback report's, say), settles it and prints what one question about it needs: who is near,
/// what a character does over time, slaves and their owners, cages, dialogue, storage contents, a building re-save's duplicates or the navmesh
/// heights at a point. It runs the install's load order unless <c>--load-order</c> gives the reporter's (<see cref="ProbeLoadOrder"/>).
/// </summary>
static class SaveProbe
{
    /// <summary>
    /// The ticks run before a mode by default: characters exist only once their zones activate.
    /// </summary>
    const int SettleTicks = 300;

    /// <summary>
    /// The command line: <c>&lt;save folder&gt; &lt;mode&gt; [mode arguments] [--ticks n] [--nav] [--load-order file] [--mod-dir folder]...</c>. Returns 0, or 2 for a bad command line.
    /// </summary>
    public static int Run(GameInstall install, string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var rest = new List<string>(args);
        bool nav = rest.Remove("--nav");
        int ticks = ProbeLoadOrder.TakeOption(rest, "--ticks") is { } t ? int.Parse(t) : SettleTicks;
        var order = ProbeLoadOrder.Take(install, rest);
        if (rest.Count < 2) return Usage();

        using var probe = ProbeWorld.Load(install, order, rest[0], nav);
        probe.Tick(ticks);
        return Mode(probe, rest[1], rest[2..]) ? 0 : Usage();
    }

    /// <summary>
    /// Runs one mode; false for an unknown mode or missing arguments.
    /// </summary>
    static bool Mode(ProbeWorld probe, string mode, List<string> a)
    {
        switch (mode, a.Count)
        {
            case ("near", >= 1): Near(probe, float.Parse(a[0])); return true;
            case ("watch", >= 1): Watch(probe, a[0], a.Count > 1 ? int.Parse(a[1]) : 10); return true;
            case ("move", >= 3): Move(probe, a[0], float.Parse(a[1]), float.Parse(a[2]), a.Count > 3 ? int.Parse(a[3]) : 10); return true;
            case ("slaves", >= 1): Slaves(probe, float.Parse(a[0])); return true;
            case ("cages", >= 1): Cages(probe, a[0]); return true;
            case ("cage", _): PutInCage(probe, a.Count > 0 ? a[0] : null); return true;
            case ("talk", >= 1): Talk(probe, a[0]); return true;
            case ("storage", >= 2): Storage(probe, a[0], float.Parse(a[1])); return true;
            case ("resave", _): Resave(probe); return true;
            case ("sites", >= 1): Sites(probe, float.Parse(a[0])); return true;
            case ("heights", >= 2) when probe.Nav is not null: Heights(probe, float.Parse(a[0]), float.Parse(a[1])); return true;
            default: return false;
        }
    }

    /// <summary>
    /// Every character within <paramref name="radius"/> of the player's squad leader.
    /// </summary>
    static void Near(ProbeWorld probe, float radius)
    {
        foreach (int i in probe.Near(probe.LeaderPosition, radius)) Console.WriteLine(probe.Who(i));
    }

    /// <summary>
    /// The characters named like <paramref name="name"/>, with their squad once and then their task and AI every 40 ticks, <paramref name="rounds"/>
    /// times.
    /// </summary>
    static void Watch(ProbeWorld probe, string name, int rounds)
    {
        var slots = probe.Named(name);
        foreach (int i in slots)
        {
            var c = probe.Characters.Cold(i)!;
            var squad = probe.World.Squads.Find(c.SquadId);
            Console.WriteLine($"squad {squad?.Id} '{squad?.Name}' template={squad?.TemplateId} members={squad?.Members.Count} town={squad?.Town} "
                + $"imprisoned={c.IsImprisoned} slave={c.SlaveState}");
            foreach (var p in c.Medical?.PartSpan.ToArray() ?? [])
                Console.WriteLine($"  part flesh={p.Flesh:F2} stun={p.Stun:F2} rig={p.Rig:F2} max={p.MaxHp:F2} wear={p.Wear:F2} fraction={p.Fraction:F3}");
        }

        for (int round = 0; round < rounds; round++)
        {
            foreach (int i in slots)
            {
                var c = probe.Characters.Cold(i)!;
                Console.WriteLine($"tick {probe.World.Tick} {probe.Who(i)} in={c.InSomething} carrying={c.Carrying} behaviour={c.Ai?.Behaviour} "
                    + $"package={c.Ai?.Squad.Ai?.Current?.Name} goal={c.Ai?.Goal?.Task} fight={c.Ai?.FightGoal} "
                    + $"orders=[{string.Join(", ", c.OrderQueue.Select(o => o.Row))}] saved={c.SavedOrders?.Count}");
            }

            probe.PrintMessages();
            probe.Tick(40);
        }
    }

    /// <summary>
    /// Orders the characters named like <paramref name="name"/> to walk to (<paramref name="x"/>, <paramref name="z"/>) at the terrain height, then
    /// watches them as <see cref="Watch"/> does.
    /// </summary>
    static void Move(ProbeWorld probe, string name, float x, float z, int rounds)
    {
        var t = probe.Characters;
        var ids = probe.Named(name).Select(t.IdOf).ToList();
        var target = new Vector3(x, probe.World.Walkability.TerrainHeight(x, z), z);
        probe.World.Commands.Enqueue(new MoveOrder(ids, target) { Tick = probe.World.Tick });
        Watch(probe, name, rounds);
    }

    /// <summary>
    /// Every character within <paramref name="radius"/> of the leader with its slave state, owner faction, master squad and its squad's town,
    /// platoon and home.
    /// </summary>
    static void Slaves(ProbeWorld probe, float radius)
    {
        foreach (int i in probe.Near(probe.LeaderPosition, radius))
        {
            var c = probe.Characters.Cold(i)!;
            var squad = probe.World.Squads.Find(c.SquadId);
            Console.WriteLine($"{probe.Who(i)} slave={c.SlaveState} owner={probe.Faction(c.SlaveOwner)} master={c.SlaveMaster} in={c.InSomething} "
                + $"town={squad?.Town} platoon={squad?.PlatoonId} home={squad?.HomeBuilding?.Position} template={squad?.TemplateId}");
        }
    }

    /// <summary>
    /// The fixtures within 300 units (on the ground plane) of the first character named like <paramref name="name"/>, with their occupants.
    /// </summary>
    static void Cages(ProbeWorld probe, string name)
    {
        int who = probe.Named(name)[0];
        var c = probe.Characters.Cold(who)!;
        var at = probe.Characters.Next[who].Position;
        Console.WriteLine($"{probe.Who(who)} inWhat={c.InWhat} inSomething={c.InSomething}");

        foreach (var f in probe.World.Fixtures.All.Where(f => Vector2.Distance(new(f.Position.X, f.Position.Z), new(at.X, at.Z)) < 300))
            Console.WriteLine($"fixture {f.Kind} {f.Id} record={f.RecordId} at {f.Position:F1} occupant={Occupant(probe, f)}");
    }

    /// <summary>
    /// A fixture's occupant's name and position, or "-".
    /// </summary>
    static string Occupant(ProbeWorld probe, Fixture f) =>
        probe.Characters.TryResolveNext(f.Occupant, out int o) ? $"{probe.Characters.Cold(o)!.Name} {probe.Characters.Next[o].Position:F1}" : "-";

    /// <summary>
    /// Orders the first player character carrying someone to put them in a cage (<paramref name="cage"/> by id, else the order's choice) and
    /// follows it for 600 ticks.
    /// </summary>
    static void PutInCage(ProbeWorld probe, string? cage)
    {
        var t = probe.Characters;
        int carrier = Enumerable.Range(0, t.HighWater).First(i => t.Next[i].Alive && t.Cold(i) is { IsPlayer: true, Carrying.IsNone: false });
        probe.World.Commands.Enqueue(new PutInCageOrder([t.IdOf(carrier)]) { Tick = probe.World.Tick, Cage = cage });

        for (int round = 0; round < 30; round++)
        {
            Console.WriteLine($"tick {probe.World.Tick} {probe.Who(carrier)} carrying={t.Cold(carrier)!.Carrying}");
            probe.PrintMessages();
            probe.Tick(20);
        }
    }

    /// <summary>
    /// Whether the characters named like <paramref name="name"/> can be talked to (by anyone, and by the nearest player character) and the roots
    /// of their talk event.
    /// </summary>
    static void Talk(ProbeWorld probe, string name)
    {
        var t = probe.Characters;
        var dialogue = probe.World.Systems.OfType<DialogueSystem>().First();
        foreach (int i in probe.Named(name))
        {
            int talker = Enumerable.Range(0, t.HighWater)
                .Where(j => t.Next[j].Alive && t.Cold(j) is { IsPlayer: true })
                .MinBy(j => Vector3.Distance(t.Next[j].Position, t.Next[i].Position));
            Console.WriteLine($"{probe.Who(i)} record={t.Cold(i)!.RecordId} canTalk={dialogue.CanTalk(probe.World, i)} "
                + $"withTalker={dialogue.CanTalk(probe.World, i, t.IdOf(talker))} talker={t.Cold(talker)!.Name}");
            Console.WriteLine("  talk roots: " + string.Join(", ", dialogue.SetOf(probe.World, i).For(1).Select(r => r.ToString())));
        }
    }

    /// <summary>
    /// The storages within <paramref name="radius"/> of the first character named like <paramref name="name"/>: their type limit, grid and items,
    /// each with whether the storage accepts it.
    /// </summary>
    static void Storage(ProbeWorld probe, string name, float radius)
    {
        var at = probe.Characters.Next[probe.Named(name)[0]].Position;
        foreach (var site in probe.World.Buildings.All.Where(b => Vector3.Distance(b.Position, at) < radius))
        {
            if (probe.World.Machines.Find(site.Id) is not { Storage: { } grid } m) continue;

            Console.WriteLine($"storage {site.Id} record={site.Record} '{probe.Db.Find(site.Record)?.Name}' typeLimit={m.Data.ItemTypeLimit} "
                + $"limit=[{string.Join(",", m.Data.StorageLimit)}] grid={grid.Width}x{grid.Height} items={grid.Items.Count}");
            foreach (var it in grid.Items)
            {
                Console.WriteLine($"  {it.Name} record={it.Record} type={it.Type} at {it.X},{it.Y} size {it.Width}x{it.Height} quantity={it.Quantity} "
                    + $"accepts={m.Data.Accepts(it.Record, it.Type)}");
            }
        }
    }

    /// <summary>
    /// The player's buildings within <paramref name="radius"/> of the leader: id, record, position, floor, the building they stand in and whether
    /// they are complete.
    /// </summary>
    static void Sites(ProbeWorld probe, float radius)
    {
        foreach (var site in probe.World.Buildings.All.Where(b => Vector3.Distance(b.Position, probe.LeaderPosition) < radius))
        {
            Console.WriteLine($"site {site.Id} '{site.Name}' at {site.Position:F1} floor={site.Floor} parent={site.Parent} complete={site.Complete} "
                + $"level={site.LevelPlacement is not null} town={site.Town}");
        }
    }

    /// <summary>
    /// Writes the world's buildings back into the save (in memory) and lists the instance ids that more than one zone's building list holds.
    /// </summary>
    static void Resave(ProbeWorld probe)
    {
        BuildingSave.Write(probe.Save, probe.World, probe.Save.PlayerFaction!.Id);
        var listed = probe.Save.Zones.Values
            .Where(z => z.BuildingList is not null)
            .SelectMany(z => z.BuildingList!.Instances.Select(i => (Zone: z.Coordinate, i.Id)));
        foreach (var g in listed.GroupBy(p => p.Id).Where(g => g.Count() > 1))
            Console.WriteLine($"duplicate {g.Key} in {string.Join(", ", g.Select(p => p.Zone))}");
    }

    /// <summary>
    /// The terrain height and every navmesh floor height at a world point.
    /// </summary>
    static void Heights(ProbeWorld probe, float x, float z)
    {
        var heights = new List<float>();
        probe.Nav!.Walkability.World.HeightsAt(x, z, heights);
        Console.WriteLine($"terrain {probe.Nav.Walkability.TerrainHeight(x, z):F1} navmesh [{string.Join(" ", heights.Select(h => h.ToString("F1")))}]");
    }

    /// <summary>
    /// The modes, on standard error; returns 2.
    /// </summary>
    static int Usage()
    {
        Console.Error.WriteLine("""
            meitou-tools probe <save folder> <mode> [args] [--ticks n (default 300)] [--nav] [--load-order <file>] [--mod-dir <folder>]...
              near <radius>            characters near the player's squad leader
              watch <name> [rounds]    a character's task and AI every 40 ticks (default 10 rounds)
              move <name> <x> <z> [rounds]  order a character to walk to a point, then watch it
              slaves <radius>          slave state, owner faction, master squad and squad town near the leader
              cages <name>             fixtures within 300 of a character, with occupants
              cage [cage id]           the carrying player character puts its load in a cage; 600 ticks of it
              talk <name>              whether a character can be talked to, and its talk roots
              storage <name> <radius>  storages near a character: type limit, grid, items and whether each is accepted
              sites <radius>           the player's buildings near the leader with floor and parent
              resave                   re-write the buildings into the save and list ids held by two zones
              heights <x> <z>          terrain and navmesh heights at a point (needs --nav)
            """);
        return 2;
    }
}
