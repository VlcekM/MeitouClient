using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;
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
    /// The command line: <c>&lt;save folder&gt; &lt;mode&gt; [mode arguments] [--ticks n] [--nav] [--at x,z] [--load-order file] [--mod-dir folder]...</c>. Returns 0, or 2 for a bad command line.
    /// </summary>
    public static int Run(GameInstall install, string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var rest = new List<string>(args);
        bool nav = rest.Remove("--nav");
        int ticks = ProbeLoadOrder.TakeOption(rest, "--ticks") is { } t ? int.Parse(t) : SettleTicks;
        Vector2? at = ProbeLoadOrder.TakeOption(rest, "--at") is { } spot ? Spot(spot) : null;
        var order = ProbeLoadOrder.Take(install, rest);
        if (rest.Count < 2) return Usage();

        using var probe = ProbeWorld.Load(install, order, rest[0], nav, at);
        probe.Tick(ticks);
        return Mode(probe, rest[1], rest[2..]) ? 0 : Usage();
    }

    /// <summary>
    /// An <c>x,z</c> spot.
    /// </summary>
    static Vector2 Spot(string text)
    {
        var parts = text.Split(',');
        return new Vector2(float.Parse(parts[0]), float.Parse(parts[1]));
    }

    /// <summary>
    /// Runs one mode; false for an unknown mode or missing arguments.
    /// </summary>
    static bool Mode(ProbeWorld probe, string mode, List<string> a)
    {
        switch (mode, a.Count)
        {
            case ("near", >= 1): Near(probe, float.Parse(a[0])); return true;
            case ("crowd", >= 1): Crowd(probe, float.Parse(a[0]), a.Count > 1 ? int.Parse(a[1]) : 10, a.Count > 2 ? int.Parse(a[2]) : 40); return true;
            case ("fight", >= 1): Fight(probe, int.Parse(a[0]), a.Count > 1 ? int.Parse(a[1]) : 10, a.Count > 2 ? int.Parse(a[2]) : 40); return true;
            case ("bait", >= 1): Bait(probe, float.Parse(a[0]), a.Count > 1 ? int.Parse(a[1]) : 10); return true;
            case ("squads", >= 1): Squads(probe, float.Parse(a[0]), a.Count > 1 ? int.Parse(a[1]) : 10); return true;
            case ("watch", >= 1): Watch(probe, a[0], a.Count > 1 ? int.Parse(a[1]) : 10); return true;
            case ("move", >= 3): Move(probe, a[0], float.Parse(a[1]), float.Parse(a[2]), a.Count > 3 ? int.Parse(a[3]) : 10); return true;
            case ("premises", _): Premises(probe, a.Count > 0 ? int.Parse(a[0]) : 10, a.Count > 1 ? int.Parse(a[1]) : 40); return true;
            case ("slaves", >= 1): Slaves(probe, float.Parse(a[0])); return true;
            case ("cages", >= 1): Cages(probe, a[0]); return true;
            case ("kit", >= 1): Kit(probe, a[0]); return true;
            case ("cage", _): PutInCage(probe, a.Count > 0 ? a[0] : null); return true;
            case ("talk", >= 1): Talk(probe, a[0]); return true;
            case ("storage", >= 2): Storage(probe, a[0], float.Parse(a[1])); return true;
            case ("resave", _): Resave(probe); return true;
            case ("sites", >= 1): Sites(probe, float.Parse(a[0])); return true;
            case ("research", _): Research(probe); return true;
            case ("nests", _): Nests(probe); return true;
            case ("heights", >= 2) when probe.Nav is not null: Heights(probe, float.Parse(a[0]), float.Parse(a[1])); return true;
            case ("layout", >= 1) when probe.Nav is not null: Layout(probe, a[0]); return true;
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
    /// Every character within <paramref name="radius"/> of the player's squad leader every <paramref name="interval"/> ticks,
    /// <paramref name="rounds"/> times: whether it is down, its goal, what it carries and whom it fights.
    /// </summary>
    static void Crowd(ProbeWorld probe, float radius, int rounds, int interval)
    {
        for (int round = 0; round < rounds; round++)
        {
            Console.WriteLine($"tick {probe.World.Tick}");
            foreach (int i in probe.Near(probe.LeaderPosition, radius))
            {
                var c = probe.Characters.Cold(i)!;
                Console.WriteLine($"  {probe.Who(i)}{Down(c)} in={c.InSomething} carrying={c.Carrying} lifting={c.CarryTarget} behaviour={c.Ai?.Behaviour} "
                    + $"goal={c.Ai?.Goal?.Task.Type} fight={c.Ai?.FightGoal} target={Target(probe, i)}");
            }

            probe.PrintMessages();
            probe.Tick(interval);
        }
    }

    /// <summary>
    /// The reach of the <c>premises</c> mode's list of homes round the leader: a town.
    /// </summary>
    const float HomesRadius = 2500;

    /// <summary>
    /// The homes round the leader (<see cref="Homes"/>), then the building the player's squad leader stands in (needs <c>--nav</c> for the building
    /// sight) every <paramref name="interval"/> ticks, <paramref name="rounds"/> times: the hour, the squads living there with their public flags,
    /// the house lock and doors, everyone inside with goal and pending crime, and the lines said meanwhile.
    /// </summary>
    static void Premises(ProbeWorld probe, int rounds, int interval)
    {
        var world = probe.World;
        var locks = world.Systems.OfType<LockSystem>().FirstOrDefault();
        Homes(probe, HomesRadius);
        for (int round = 0; round < rounds; round++)
        {
            int leader = world.Squads.Find(world.Player.Squad)!.Leader.Slot;
            string? key = world.Sight.BuildingOf(leader);
            Console.WriteLine($"tick {world.Tick} hour={world.Hour} leader in building={key ?? "none"}");
            if (key is not null) PremisesState(probe, locks, key);

            foreach (var line in world.TakeSaid()) Console.WriteLine($"  SAID {line.Owner}: {line.Text}");
            probe.PrintMessages();
            probe.Tick(interval);
        }
    }

    /// <summary>
    /// The homes of the squads within <paramref name="radius"/> of the leader: the level state's public flags (what the building panel shows), the
    /// residents' template flag and the registry's (what the AI reads), and which shoo goals each role holds at this hour.
    /// </summary>
    static void Homes(ProbeWorld probe, float radius)
    {
        var world = probe.World;
        var population = world.Systems.OfType<PopulationSystem>().First();
        foreach (var q in world.Squads.All)
        {
            if ((q.Ai?.OwnHome ?? q.HomeBuilding) is not { } home || Vector3.Distance(home.Position, probe.LeaderPosition) > radius) continue;

            var shoo = Enum.GetValues<Meitou.Data.Gameplay.Ai.AiRole>()
                .Select(r => (Role: r, Always: population.GoalDuty(world, q, r, Meitou.Data.Gameplay.Ai.TaskType.ShooStrangersOutOfMyBuilding),
                    Private: population.GoalDuty(world, q, r, Meitou.Data.Gameplay.Ai.TaskType.ShooStrangersOutOfMyBuildingIfPrivate)))
                .Where(x => x.Always || x.Private)
                .Select(x => $"{x.Role}:{(x.Always ? "158" : "")}{(x.Private ? "+283" : "")}");
            Console.WriteLine($"home {home.Key} squad {q.Id} '{q.Name}' fac={probe.Faction(q.Faction)} template={q.TemplateId} "
                + $"panel day/night={home.LevelState?.GetBool("is public day")}/{home.LevelState?.GetBool("is public night")} "
                + $"homePublic={q.Ai?.HomePublic} registry={world.Fixtures.IsPublic(home.Key)} package={q.Ai?.Current?.Name} "
                + $"shoo=[{string.Join(" ", shoo)}]");
        }
    }

    /// <summary>
    /// One round of <see cref="Premises"/> for building <paramref name="key"/>: its flags and doors, its resident squads, and who is inside.
    /// </summary>
    static void PremisesState(ProbeWorld probe, LockSystem? locks, string key)
    {
        var world = probe.World;
        var doors = world.Fixtures.OfBuilding(key).Where(f => f.Kind == FixtureKind.Door).Select(f => $"{f.Lock?.Locked}/{f.Motion}");
        Console.WriteLine($"  public: player flag={world.Fixtures.PublicOf(key)} state={world.Fixtures.IsPublic(key)} "
            + $"houseLocked={locks?.IsHouseLocked(world, key)} doors=[{string.Join(", ", doors)}]");
        foreach (var q in world.Squads.All.Where(q => (q.Ai?.OwnHome ?? q.HomeBuilding)?.Key == key))
        {
            Console.WriteLine($"  resident squad {q.Id} '{q.Name}' fac={probe.Faction(q.Faction)} template={q.TemplateId} homePublic={q.Ai?.HomePublic} "
                + $"package={q.Ai?.Current?.Name}");
        }

        for (int i = 0; i < probe.Characters.HighWater; i++)
        {
            if (!probe.Characters.Next[i].Alive || probe.Characters.Cold(i) is not { } c || world.Sight.BuildingOf(i) != key) continue;
            Console.WriteLine($"  inside {probe.Who(i)} goal={c.Ai?.Goal?.Task.Type} pending={c.Pending?.Code} target={Target(probe, i)}");
        }
    }

    /// <summary>
    /// The fight round the character in <paramref name="slot"/> every <paramref name="interval"/> ticks, <paramref name="rounds"/> times: its own
    /// combat state and body, then everyone who targets it with the gap to it and the blows thrown and landed.
    /// </summary>
    static void Fight(ProbeWorld probe, int slot, int rounds, int interval)
    {
        var t = probe.Characters;
        var combat = probe.World.Systems.OfType<CombatSystem>().First();
        for (int round = 0; round < rounds; round++)
        {
            var at = t.Next[slot].Position;
            var parts = t.Cold(slot)?.Medical?.PartSpan.ToArray().Select(p => $"{p.Flesh:F0}/{p.MaxHp:F0}") ?? [];
            Console.WriteLine($"tick {probe.World.Tick} {probe.Who(slot)}{Down(t.Cold(slot)!)} {FightLine(combat.StateOf(t.IdOf(slot)))} "
                + $"parts=[{string.Join(" ", parts)}]");
            foreach (int i in probe.Near(at, 400))
            {
                var s = combat.StateOf(t.IdOf(i));
                if (i == slot || s.TargetSlot != slot) continue;

                var p = t.Next[i].Position;
                Console.WriteLine($"  {i} '{t.Cold(i)!.Name}' {(CharacterTask)t.Next[i].Task} gap={Vector2.Distance(new(p.X, p.Z), new(at.X, at.Z)):F1} "
                    + $"dy={p.Y - at.Y:F1} {FightLine(s)}");
            }

            probe.Tick(interval);
        }
    }

    /// <summary>
    /// The fields of a combat state that say why blows do or do not land.
    /// </summary>
    static string FightLine(CombatSlot s) =>
        $"engaged={s.Engaged} circle={s.CircleState} tech={s.AttackTech} reach={s.Reach:F1} thrown={s.Thrown} landed={s.Landed} parried={s.Parried} "
        + $"evaded={s.Evaded} taken={s.Taken} blocks={s.Blocks} dodges={s.Dodges} ranged={s.Ranged}";

    /// <summary>
    /// The characters named like <paramref name="name"/>, with their squad once and then their task, AI and fight target every 40 ticks,
    /// <paramref name="rounds"/> times.
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
                    + $"orders=[{string.Join(", ", c.OrderQueue.Select(o => o.Row))}] saved={c.SavedOrders?.Count} target={Target(probe, i)}");
            }

            probe.PrintMessages();
            probe.Tick(40);
        }
    }

    /// <summary>
    /// The other squads whose leader stands within <paramref name="radius"/> of the player's leader, every 40 ticks <paramref name="rounds"/> times:
    /// the leader's task, AI behaviour, goal and package, whether its mind is the engine's, and how far its members stand from it.
    /// </summary>
    static void Squads(ProbeWorld probe, float radius, int rounds, Action<ProbeWorld, float>? eachRound = null)
    {
        var t = probe.Characters;
        for (int round = 0; round < rounds; round++)
        {
            eachRound?.Invoke(probe, radius);
            Console.WriteLine($"tick {probe.World.Tick}");
            foreach (var squad in probe.World.Squads.All)
            {
                if (squad.Id == probe.World.Player.Squad || !t.TryResolveNext(squad.Leader, out int lead)) continue;
                var at = t.Next[lead].Position;
                if (Vector3.Distance(at, probe.LeaderPosition) > radius) continue;
                Console.WriteLine($"  {SquadLine(probe, squad, lead)}");
            }

            foreach (var m in probe.World.Squads.Find(probe.World.Player.Squad)?.Members ?? [])
                if (t.TryResolveNext(m, out int s)) Console.WriteLine($"  player {probe.Who(s)} down={t.Cold(s)!.Medical?.Incapacitated}");

            probe.PrintMessages();
            probe.Tick(40);
        }
    }

    /// <summary>
    /// Sends the player's squad after the nearest other squad's leader within <paramref name="radius"/> each round, to start a fight, and lists the
    /// squads as <see cref="Squads"/> does.
    /// </summary>
    static void Bait(ProbeWorld probe, float radius, int rounds) => Squads(probe, radius, rounds, Chase);

    /// <summary>
    /// Orders the player's squad to the nearest other squad leader within <paramref name="radius"/>.
    /// </summary>
    static void Chase(ProbeWorld probe, float radius)
    {
        var t = probe.Characters;
        var mine = probe.World.Squads.Find(probe.World.Player.Squad)!;
        var leaders = probe.World.Squads.All
            .Where(s => s.Id != mine.Id && t.TryResolveNext(s.Leader, out _))
            .Select(s => t.Next[s.Leader.Slot].Position)
            .Where(p => Vector3.Distance(p, probe.LeaderPosition) < radius)
            .OrderBy(p => Vector3.Distance(p, probe.LeaderPosition))
            .ToList();
        if (leaders.Count > 0) probe.World.Commands.Enqueue(new MoveOrder([.. mine.Members], leaders[0]) { Tick = probe.World.Tick });
    }

    /// <summary>
    /// One squad of <see cref="Squads"/>: the leader's state and each member's task and distance from it.
    /// </summary>
    static string SquadLine(ProbeWorld probe, Squad squad, int lead)
    {
        var t = probe.Characters;
        var c = t.Cold(lead)!;
        var at = t.Next[lead].Position;
        var members = squad.Members
            .Where(m => t.TryResolveNext(m, out _))
            .Select(m => $"{(CharacterTask)t.Next[m.Slot].Task}{Down(t.Cold(m.Slot)!)}:{t.Cold(m.Slot)!.Ai?.Goal?.Task.Type}"
                + $"@{Vector3.Distance(t.Next[m.Slot].Position, at):F0}");
        return $"sq {squad.Id} '{squad.Name}' {probe.Faction(squad.Faction)} platoon={squad.PlatoonId} leader={(CharacterTask)t.Next[lead].Task} "
            + $"engine={c.Ai?.Engine} behaviour={c.Ai?.Behaviour} goal={c.Ai?.Goal?.Task.Type} fight={c.Ai?.FightGoal} "
            + $"target={Target(probe, lead)} package={squad.Ai?.Current?.Name} "
            + $"home={squad.HomeCentre:F0}/{squad.HomeRadius:F0} pos={at:F0} [{string.Join(" ", members)}]";
    }

    /// <summary>
    /// "/dead" or "/ko" for a character down, else empty.
    /// </summary>
    static string Down(CharacterCold c) =>
        c.Medical is { Dead: true } ? "/dead"
        : c.Medical is { Unconscious: true } ? "/ko"
        : "";

    /// <summary>
    /// The name and faction of the character <paramref name="i"/> fights, or "-".
    /// </summary>
    static string Target(ProbeWorld probe, int i)
    {
        var combat = probe.World.Systems.OfType<CombatSystem>().First();
        var state = combat.StateOf(probe.Characters.IdOf(i));
        if (!state.Fighting || probe.Characters.Cold(state.TargetSlot) is not { } c) return "-";

        return $"{state.TargetSlot} '{c.Name}' {probe.Faction(c.Faction)}";
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
    /// The characters named like <paramref name="name"/> with their slave state, owner, shaven head and every item they carry (section, locked).
    /// </summary>
    static void Kit(ProbeWorld probe, string name)
    {
        foreach (int i in probe.Named(name))
        {
            var c = probe.Characters.Cold(i)!;
            Console.WriteLine($"{probe.Who(i)} slave={c.SlaveState} owner={probe.Faction(c.SlaveOwner)} master={c.SlaveMaster} shaved={c.Shaved} "
                + $"in={c.InSomething} bandage=[{string.Join(" ", c.Medical?.PartSpan.ToArray().Select(p => $"{p.Bandage:F0}/{p.Flesh:F0}") ?? [])}]");
            foreach (var item in c.Inventory?.All() ?? [])
                Console.WriteLine($"  {item.Record} {item.Type} section={item.Section} locked={item.LockedOn} lock={item.Lock is not null}");
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
    /// The research state and each research bench with the factors of its rate: health, power, the research production mult and operators.
    /// </summary>
    static void Research(ProbeWorld probe)
    {
        var world = probe.World;
        var research = world.Research;
        var queue = string.Join(", ", research.Queue.Select(q => $"{q.Id} {q.Seconds:F0}s"));
        Console.WriteLine($"tech level {research.TechLevel}, bench level {research.BenchLevel}, rate {research.Rate:F3}, queue: {queue}");
        foreach (var m in world.Machines.All.Where(m => m.Data.IsResearch))
        {
            var owner = world.Buildings.Find(m.Site)?.Owner ?? -1;
            var upgrade = research.Upgrade(world, m.Data.Id, owner);
            Console.WriteLine($"bench {m.Site} '{m.Data.Id}' at {m.Position:F0} owner={owner} rate={m.Data.Rate} health={m.Health:F2} power={m.Power:F2} "
                + $"powerOn={m.PowerOn} active={m.Active} mult={upgrade.ProductionMult} operators={m.LastOperators}");
            if (world.Buildings.Find(m.Site) is { } site) Grid(world, site.Town);
        }
    }

    /// <summary>
    /// The powered machines of one town: generators, batteries and consumers with their demand, grant, charge and power share.
    /// </summary>
    static void Grid(World world, int town)
    {
        foreach (var m in world.Machines.All.Where(m => m.Data.IsGenerator || m.Data.IsBattery || m.Data.IsConsumer))
        {
            if (world.Buildings.Find(m.Site) is not { } site || site.Town != town) continue;

            string kind = m.Data.IsGenerator ? "generator" : m.Data.IsBattery ? "battery" : "consumer";
            var inputs = string.Join(" ", m.Inputs.Select(i => $"{i.Item}:{i.Count}/{i.Level:F2}"));
            Console.WriteLine($"  {kind} {m.Site} '{m.Data.Id}' complete={site.Complete} on={m.PowerOn} demand={m.Demand:F1} granted={m.Granted:F1} "
                + $"charge={m.Charge:F1} store={m.Store:F1} power={m.Power:F2} operators={m.LastOperators} inputs={inputs}");
        }
    }

    /// <summary>
    /// Every nest of the wild layout with the terrain height at each candidate spot; flags a first spot or all spots the original's spot finder
    /// refuses: under its floor (95) or on a black pixel of the areas map (docs/game/factions-squads-towns.md "Spot finder").
    /// </summary>
    static void Nests(ProbeWorld probe)
    {
        var population = probe.World.Systems.OfType<PopulationSystem>().First();
        var walk = probe.World.Walkability;
        var biomes = probe.Data.Biomes!;
        int nests = 0, firstWet = 0, allWet = 0;
        foreach (var site in population.WildSites.Where(s => s.Kind == WildKind.Nest && s.Spots.Count > 0))
        {
            var heights = site.Spots.Select(s => walk.TerrainHeight(s.X, s.Y)).ToArray();
            var refused = site.Spots.Select((s, i) => heights[i] < 95 || !biomes.IsPainted(s.X, s.Y)).ToArray();
            bool first = refused[0], all = refused.All(r => r);
            nests++;
            if (first) firstWet++;
            if (all) allWet++;
            if (!first) continue;

            string flag = all ? "ALL REFUSED" : "first refused";
            string spots = string.Join(" ", heights.Select(h => h.ToString("F0")));
            Console.WriteLine($"{flag} nest {site.Index} '{site.Nest?.Name}' at {site.Spots[0]:F0} spots [{spots}]");
        }

        Console.WriteLine($"{nests} nests, first spot refused: {firstWet}, every spot refused: {allWet}");
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
    /// A town building's layout names now (a town override or the save may have relaid it) and each object of its interior layout as placed: record,
    /// floor, path mode, world position. Needs <c>--nav</c>, which hands the population its layouts.
    /// </summary>
    static void Layout(ProbeWorld probe, string key)
    {
        var b = probe.Data.Sites.SelectMany(s => s.Buildings).FirstOrDefault(t => t.Key == key);
        if (b?.Resolved is not { } resolved || probe.Data.Layouts is not { } layouts)
        {
            Console.WriteLine($"no laid-out town building {key}");
            return;
        }

        string name = b.LayoutName(exterior: false);
        Console.WriteLine($"{key} {b.Name}: interior '{name}', exterior '{b.LayoutName(exterior: true)}'");
        if (layouts.Find(resolved.Record, name, exterior: false) is not { } layout) return;

        foreach (var item in layouts.Items(layout, resolved.Placement))
        {
            var (at, _) = BuildingLayouts.Place(resolved.Position, resolved.Placement.Rotation, item);
            Console.WriteLine($"  {item.InstanceId} '{item.Building.Name}' floor={item.Floor} path={item.Building.GetInt("path mode", 2)} at {at:F0}");
        }
    }

    /// <summary>
    /// The modes, on standard error; returns 2.
    /// </summary>
    static int Usage()
    {
        Console.Error.WriteLine("""
            meitou-tools probe <save folder> <mode> [args] [--ticks n (default 300)] [--nav] [--at x,z] [--load-order <file>] [--mod-dir <folder>]...
              near <radius>            characters near the player's squad leader
              crowd <radius> [rounds] [interval]  characters near the leader every interval (40) ticks: down, goal, load, fight target
              fight <slot> [rounds] [interval]  a character's combat state and body, and everyone targeting it: gap, blows thrown and landed
              nests                    the wild layout's nests whose first candidate spot the original would refuse
              bait <radius> [rounds]   send the player's squad after the nearest other squad each round, listing squads as squads does
              squads <radius> [rounds]  the other squads near the leader: leader state, goal, members' spread every 40 ticks
              watch <name> [rounds]    a character's task, AI and fight target every 40 ticks (default 10 rounds)
              move <name> <x> <z> [rounds]  order a character to walk to a point, then watch it
              premises [rounds] [interval]  the building the leader stands in (needs --nav): public flags, house lock, doors, residents, who is inside, lines said
              slaves <radius>          slave state, owner faction, master squad and squad town near the leader
              cages <name>             fixtures within 300 of a character, with occupants
              cage [cage id]           the carrying player character puts its load in a cage; 600 ticks of it
              talk <name>              whether a character can be talked to, and its talk roots
              storage <name> <radius>  storages near a character: type limit, grid, items and whether each is accepted
              sites <radius>           the player's buildings near the leader with floor and parent
              research                 the research state and each bench's rate factors
              resave                   re-write the buildings into the save and list ids held by two zones
              heights <x> <z>          terrain and navmesh heights at a point (needs --nav)
              layout <building key>    a town building's layout names now and its interior layout's objects (needs --nav)
            """);
        return 2;
    }
}
