using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Data.Gameplay.Dialogue;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Navigation;
using Meitou.Simulation.Perception;
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
    /// The command line: <c>&lt;save folder&gt; &lt;mode&gt; [mode arguments] [--ticks n] [--nav] [--stream] [--at x,z] [--seed n] [--load-order file] [--mod-dir folder]...</c>. Returns 0, 1 for a save without a player character, or 2 for a bad command line.
    /// </summary>
    public static int Run(GameInstall install, string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var rest = new List<string>(args);
        bool stream = rest.Remove("--stream");
        bool nav = rest.Remove("--nav") || stream;
        int ticks = ProbeLoadOrder.TakeOption(rest, "--ticks") is { } t ? int.Parse(t) : SettleTicks;
        Vector2? at = ProbeLoadOrder.TakeOption(rest, "--at") is { } spot ? Spot(spot) : null;
        ulong seed = ProbeLoadOrder.TakeOption(rest, "--seed") is { } s ? ulong.Parse(s) : 1;
        var order = ProbeLoadOrder.Take(install, rest);
        if (rest.Count < 2) return Usage();

        if (ProbeWorld.Load(install, order, rest[0], nav, at, seed, stream) is not { } loaded) return 1;
        using var probe = loaded;
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
            case ("crime", >= 1): Crime(probe, a[0], a.Count > 1 ? int.Parse(a[1]) : 10, a.Count > 2 ? int.Parse(a[2]) : 10); return true;
            case ("bait", >= 1): Bait(probe, float.Parse(a[0]), a.Count > 1 ? int.Parse(a[1]) : 10); return true;
            case ("squads", >= 1): Squads(probe, float.Parse(a[0]), a.Count > 1 ? int.Parse(a[1]) : 10); return true;
            case ("watch", >= 1): Watch(probe, a[0], a.Count > 1 ? int.Parse(a[1]) : 10); return true;
            case ("turrets", >= 4):
                TurretProbe.Run(probe, a[0], int.Parse(a[1]), new Vector2(float.Parse(a[2]), float.Parse(a[3])),
                    a.Count > 4 ? int.Parse(a[4]) : 10, a.Count > 5 ? int.Parse(a[5]) : 40);
                return true;
            case ("anim", >= 1): AnimationProbe.Run(probe, float.Parse(a[0]), a.Count > 1 ? int.Parse(a[1]) : 30, a.Count > 2 ? a[2] : null); return true;
            case ("assault", >= 1): Assault(probe, a[0], a.Count > 1 ? int.Parse(a[1]) : 10, a.Count > 2 ? int.Parse(a[2]) : 40); return true;
            case ("pace", >= 3): Pace(probe, a[0], float.Parse(a[1]), float.Parse(a[2]), a.Count > 3 ? int.Parse(a[3]) : 10); return true;
            case ("move", >= 3): Move(probe, a[0], float.Parse(a[1]), float.Parse(a[2]), a.Count > 3 ? int.Parse(a[3]) : 10); return true;
            case ("premises", _): Premises(probe, a.Count > 0 ? int.Parse(a[0]) : 10, a.Count > 1 ? int.Parse(a[1]) : 40); return true;
            case ("slaves", >= 1): Slaves(probe, float.Parse(a[0])); return true;
            case ("captors", _): Captors(probe, a.Count > 0 ? int.Parse(a[0]) : 15, a.Count > 1 && a[1] == "walk"); return true;
            case ("ko", >= 1): Knockout(probe, int.Parse(a[0]), a.Count > 1 ? int.Parse(a[1]) : 15); return true;
            case ("cages", >= 1): Cages(probe, a[0]); return true;
            case ("sleeper", >= 1): Sleeper(probe, a[0]); return true;
            case ("force", >= 2):
                Force(probe, a[0], a[1] == "tools" ? LockForce.Tools : LockForce.Strength, a.Count > 2 ? a[2] : null);
                return true;
            case ("kit", >= 1): Kit(probe, a[0]); return true;
            case ("uniques", _): Uniques(probe, a.Count > 0 ? a[0] : null); return true;
            case ("recruit", >= 1): Recruit(probe, a[0]); return true;
            case ("cage", _): PutInCage(probe, a.Count > 0 ? a[0] : null); return true;
            case ("escape", >= 1):
                Vector3? walk = a.Count > 4 ? new(float.Parse(a[3]), 0, float.Parse(a[4])) : null;
                Escape(probe, a[0], a.Count > 1 ? int.Parse(a[1]) : 20, a.Count > 2 ? int.Parse(a[2]) : 20, walk);
                return true;
            case ("talk", >= 1): Talk(probe, a[0], a.Count > 1 ? int.Parse(a[1]) : null); return true;
            case ("converse", >= 1): Converse(probe, a[0], a.Skip(1).Select(int.Parse).ToList()); return true;
            case ("storage", >= 2): Storage(probe, a[0], float.Parse(a[1])); return true;
            case ("containers", >= 1): Containers(probe, float.Parse(a[0])); return true;
            case ("resave", _): Resave(probe); return true;
            case ("sites", >= 1): Sites(probe, float.Parse(a[0])); return true;
            case ("machines", >= 1): Machines(probe, float.Parse(a[0])); return true;
            case ("research", _): Research(probe); return true;
            case ("nests", _): Nests(probe); return true;
            case ("heights", >= 2) when probe.Nav is not null: Heights(probe, float.Parse(a[0]), float.Parse(a[1])); return true;
            case ("ray", >= 6) when probe.Nav is not null: Ray(probe, Point(a, 0), Point(a, 3)); return true;
            case ("sees", >= 1) when probe.Nav is not null: SeesLeader(probe, float.Parse(a[0])); return true;
            case ("sightlines", >= 4) when probe.Nav is not null:
                Sightlines(probe, Point(a, 0), float.Parse(a[3]), a.Count > 4 ? float.Parse(a[4]) : 4, a.Count > 5 ? float.Parse(a[5]) : 0);
                return true;
            case ("places", >= 1) when probe.Nav is not null: Places(probe, float.Parse(a[0]), a.Count > 1 ? float.Parse(a[1]) : 4); return true;
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
    /// <paramref name="rounds"/> times: whether it is down, its water state, its goal, what it carries and whom it fights.
    /// </summary>
    static void Crowd(ProbeWorld probe, float radius, int rounds, int interval)
    {
        for (int round = 0; round < rounds; round++)
        {
            Console.WriteLine($"tick {probe.World.Tick}");
            foreach (int i in probe.Near(probe.LeaderPosition, radius))
            {
                var c = probe.Characters.Cold(i)!;
                Console.WriteLine($"  {probe.Who(i)}{Down(c)} in={c.InSomething} water={c.WaterState} carrying={c.Carrying} lifting={c.CarryTarget} "
                    + $"behaviour={c.Ai?.Behaviour} goal={c.Ai?.Goal?.Task.Type} fight={c.Ai?.FightGoal} target={Target(probe, i)}");
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
    /// The characters named like <paramref name="name"/> as the law sees them every <paramref name="interval"/> ticks, <paramref name="rounds"/>
    /// times: the pending crime, bounties and wanted flags, then everyone who targets one with its goal and its squad's memory tags of it.
    /// </summary>
    static void Crime(ProbeWorld probe, string name, int rounds, int interval)
    {
        var t = probe.Characters;
        var combat = probe.World.Systems.OfType<CombatSystem>().First();
        var memory = probe.World.Systems.OfType<DialogueSystem>().FirstOrDefault()?.Memory;
        SavedMemory(probe, name);

        for (int round = 0; round < rounds; round++)
        {
            foreach (int slot in probe.Named(name))
            {
                string law = CrimeLine(probe, memory, slot);
                Console.WriteLine($"tick {probe.World.Tick} {probe.Who(slot)}{Down(t.Cold(slot)!)} {law} target={Target(probe, slot)}");
                foreach (int i in probe.Near(t.Next[slot].Position, 400))
                {
                    if (i == slot || combat.StateOf(t.IdOf(i)).TargetSlot != slot || t.Cold(i) is not { } c) continue;

                    Console.WriteLine($"  {probe.Who(i)} goal={c.Ai?.Goal?.Task.Type} fight={c.Ai?.FightGoal} source={c.Running?.Source} "
                        + $"row={c.Running?.Row} tags=[{Tags(probe, memory, c.SquadId, t.IdOf(slot))}]");
                }
            }

            probe.PrintMessages();
            probe.Tick(interval);
        }
    }

    /// <summary>
    /// The saved squad memory of the platoons holding a character named like <paramref name="name"/>: per remembered character the game hour of
    /// the last note, the long and short tag bits and its platoon. The loader drops the entries whose character is not in the world yet.
    /// </summary>
    static void SavedMemory(ProbeWorld probe, string name)
    {
        var everyone = probe.Save.Platoons.SelectMany(p => p.Characters).ToList();
        Console.WriteLine($"saved squad memory at hour {probe.World.GameHours:F2}");
        foreach (var platoon in probe.Save.Platoons.Where(p => p.Characters.Any(c => c.GameState?.Name.Contains(name) == true)))
        {
            for (int n = 0; platoon.Record.Ints.ContainsKey(MemorySave.LongKey + n); n++)
            {
                var hand = Meitou.Data.Save.Hand.Read(platoon.Record, MemorySave.HandleKey + n);
                var who = everyone.FirstOrDefault(c => c.Handle.C == hand.C && c.Handle.I == hand.I);
                Console.WriteLine($"  {platoon.Name} hour={platoon.Record.Floats.GetValueOrDefault(MemorySave.TimeKey + n):F2} "
                    + $"long={platoon.Record.Ints[MemorySave.LongKey + n]:x} short={platoon.Record.Ints.GetValueOrDefault(MemorySave.ShortKey + n):x} "
                    + $"on '{who?.GameState?.Name}' of {who?.Platoon.Name}");
            }
        }
    }

    /// <summary>
    /// A character's pending crime, bounties with the booking faction's relation to its faction, the flags the police read and how many characters
    /// its own squad holds TEMP_ENEMY on.
    /// </summary>
    static string CrimeLine(ProbeWorld probe, SquadMemory? memory, int slot)
    {
        var c = probe.Characters.Cold(slot)!;
        int enemies = memory?.Snapshot(c.SquadId)?.Entries.Count(e => (e.Short >> MemoryTag.TempEnemy & 1) != 0) ?? 0;
        string pending = c.Pending is { } p
            ? $"{p.Code} vs {probe.Faction(p.VictimFaction)} victim={p.Victim.Slot} timer={p.Timer:F1} booked={p.Booked} witnessed={p.Witnessed}"
            : "-";
        var relations = probe.Data.Relations;
        var bounties = c.Bounties?.Entries.Select(e => $"{probe.Faction(e.Faction)}:{e.Amount}{(e.Claimed ? "/claimed" : "")}"
            + (relations.Contains(e.Faction) && relations.Contains(c.Faction) ? $" relation={relations.Get(e.Faction, c.Faction):F0}" : "")) ?? [];
        return $"pending=[{pending}] bounties=[{string.Join(", ", bounties)}] recentlyWanted={c.RecentlyWanted:F0} freed={c.Freed} "
            + $"escaped={c.Escaped} ordered={c.Ordered} row={c.Running?.Row} imprisoned={c.IsImprisoned} squadEnemies={enemies}";
    }

    /// <summary>
    /// The short-term memory tags squad <paramref name="squad"/> holds on <paramref name="who"/>.
    /// </summary>
    static string Tags(ProbeWorld probe, SquadMemory? memory, int squad, CharacterId who)
    {
        if (memory is null || squad < 0) return "";

        (int Tag, string Name)[] tags =
        [
            (MemoryTag.Aggressor, "AGGRESSOR"),
            (MemoryTag.TempAlly, "TEMP_ALLY"),
            (MemoryTag.TempEnemy, "TEMP_ENEMY"),
            (MemoryTag.Criminal, "CRIMINAL"),
        ];
        return string.Join(" ", tags.Where(x => memory.HasShort(probe.World, squad, who, x.Tag)).Select(x => x.Name));
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

        // A player character's decision trace: which source won and why each one before it could not run.
        probe.World.Decisions.Tracing = true;
        for (int round = 0; round < rounds; round++)
        {
            foreach (int i in slots)
            {
                var c = probe.Characters.Cold(i)!;
                Console.WriteLine($"tick {probe.World.Tick} {probe.Who(i)} in={c.InSomething} carrying={c.Carrying} behaviour={c.Ai?.Behaviour} "
                    + $"package={c.Ai?.Squad.Ai?.Current?.Name} goal={c.Ai?.Goal?.Task} fight={c.Ai?.FightGoal} "
                    + $"orders=[{string.Join(", ", c.OrderQueue.Select(o => o.Row))}] saved={c.SavedOrders?.Count} target={Target(probe, i)} "
                    + $"hunger={c.Medical?.Hunger:F2} fed={c.Medical?.Fed:F2}");
                if (c.Decision is { } d) Console.WriteLine($"  decision {d.Tick} {d.Winner}: {string.Join("; ", d.Lines)}");
            }

            probe.PrintMessages();
            probe.Tick(40);
        }
    }

    /// <summary>
    /// The player's squad attacks (Attack Unprovoked) the nearest character named like <paramref name="victim"/> that is up; then every
    /// <paramref name="interval"/> ticks the characters within 400 of the leader that are up and not the player's: indoors or out, task, goal, fight
    /// target and how their senses hold the leader (#665: who joins a fight at the gate from inside a shop).
    /// </summary>
    static void Assault(ProbeWorld probe, string victim, int rounds, int interval)
    {
        var t = probe.Characters;
        var mine = probe.World.Squads.Find(probe.World.Player.Squad)!;
        var up = probe.Named(victim)
            .Where(i => t.Cold(i) is { Medical.Incapacitated: false })
            .OrderBy(i => Vector3.Distance(t.Next[i].Position, probe.LeaderPosition))
            .ToList();
        if (up.Count == 0)
        {
            Console.WriteLine($"nobody named like '{victim}' is up");
            return;
        }

        Console.WriteLine($"attacking {probe.Who(up[0])}");
        probe.World.Commands.Enqueue(new AttackOrder([.. mine.Members], t.IdOf(up[0])) { Picked = true, Unprovoked = true, Tick = probe.World.Tick });

        for (int round = 0; round < rounds; round++)
        {
            probe.Tick(interval);
            Console.WriteLine($"tick {probe.World.Tick}");
            for (int i = 0; i < t.HighWater; i++)
            {
                if (!t.Next[i].Alive || t.Cold(i) is not { Medical.Incapacitated: false } c || c.SquadId == mine.Id
                        || Vector3.Distance(t.Next[i].Position, probe.LeaderPosition) > 400)
                    continue;

                Console.WriteLine($"  {i} '{c.Name}' {probe.Faction(c.Faction)} sq={c.SquadId} {(probe.World.Sight.PlaceOf(i) >= 0 ? "indoors" : "outdoors")} "
                    + $"task={(CharacterTask)t.Next[i].Task} goal={c.Ai?.Goal?.Task.Type} target={Target(probe, i)} senses={SensesOfLeader(probe, i)}");
            }

            probe.PrintMessages();
        }
    }

    /// <summary>
    /// How character <paramref name="i"/>'s senses hold the player's leader: seen or heard at the last look and its tags, "-" for no entry.
    /// </summary>
    static string SensesOfLeader(ProbeWorld probe, int i)
    {
        var t = probe.Characters;
        var leader = probe.World.Squads.Find(probe.World.Player.Squad)!.Leader;
        if (Meitou.Simulation.Bodies.CharacterFacts.Senses(probe.World, i) is not { } senses) return "-";

        foreach (var e in senses.Entries)
            if (e.Target == leader.Slot && e.Generation == leader.Generation) return $"{(e.Seen ? "seen" : e.Heard ? "heard" : "stale")}/{e.Tags}";

        return "-";
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
            + $"home={squad.HomeCentre:F0}/{squad.HomeRadius:F0} building={HomeName(probe, squad.Ai?.OwnHome ?? squad.HomeBuilding)} "
            + $"ai-home={HomeName(probe, squad.Ai?.Home)} table={squad.Ai?.BarTable} pinned={squad.Pinned} pos={at:F0} "
            + $"[{string.Join(" ", members)}]";
    }

    /// <summary>
    /// A squad's building as name and key, with the furniture its residents' nest stood round it ("+n"), "-" for none.
    /// </summary>
    static string HomeName(ProbeWorld probe, TownBuilding? home) =>
        home is null ? "-" : $"'{home.Name}'({home.Key})+{probe.World.NestBuildings.FurnitureOf(home.Key).Count}";

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
    /// Orders the character named like <paramref name="name"/> alone to walk to (<paramref name="x"/>, <paramref name="z"/>), then every 40 ticks,
    /// <paramref name="rounds"/> times, each of its squad mates' speed: order mode, walk mode, speed group, task, path, top and current speed (#729).
    /// </summary>
    static void Pace(ProbeWorld probe, string name, float x, float z, int rounds)
    {
        var t = probe.Characters;
        int walker = probe.Named(name)[0];
        var target = new Vector3(x, probe.World.Walkability.TerrainHeight(x, z), z);
        probe.World.Commands.Enqueue(new MoveOrder([t.IdOf(walker)], target) { Tick = probe.World.Tick });

        for (int round = 0; round < rounds; round++)
        {
            probe.Tick(40);
            foreach (var member in probe.World.Squads.Find(t.Cold(walker)!.SquadId)!.Members) PrintPace(probe, member.Slot);
        }
    }

    /// <summary>
    /// One character's line of <see cref="Pace"/>.
    /// </summary>
    static void PrintPace(ProbeWorld probe, int slot)
    {
        var t = probe.Characters;
        ref readonly var n = ref t.Next[slot];
        var c = t.Cold(slot)!;
        bool path = ((MoveFlags)n.Flags & MoveFlags.HasPath) != 0;
        float speed = new Vector2(n.Velocity.X, n.Velocity.Z).Length();
        Console.WriteLine($"tick {probe.World.Tick} {c.Name,-10} order={c.RunSpeed} mode={n.Mode} group={c.SpeedGroup} task={(CharacterTask)n.Task} "
            + $"path={path} top={n.MaxSpeed:F1} speed={speed:F1}");
    }

    /// <summary>
    /// The leader sneaks and is ordered to knock out the character in <paramref name="slot"/>; every 20 ticks, <paramref name="rounds"/> times, the
    /// chance shown, whether the target has spotted the leader, the leader's task and the swings rolled.
    /// </summary>
    static void Knockout(ProbeWorld probe, int slot, int rounds)
    {
        var t = probe.Characters;
        var leader = probe.World.Squads.Find(probe.World.Player.Squad)!.Leader;
        var aggro = probe.World.SystemOf<Meitou.Simulation.Aggro.AggroSystem>();
        var carry = probe.World.SystemOf<Meitou.Simulation.Carrying.CarrySystem>()!;
        t.Cold(leader.Slot)!.Orders |= Meitou.Simulation.Player.CharacterOrders.Sneak;
        Console.WriteLine($"target {probe.Who(slot)} knockable={Meitou.Simulation.Carrying.CarrySystem.CanBeKnockedOut(t.Cold(slot)!)}");
        probe.World.Commands.Enqueue(new Meitou.Simulation.Carrying.StealthKoOrder([leader], t.IdOf(slot)) { Tick = probe.World.Tick });

        for (int round = 0; round < rounds; round++)
        {
            probe.Tick(20);
            bool spotted = aggro?.Spotted(slot, leader) == true;
            float chance = Meitou.Simulation.Carrying.CarrySystem.StealthKoChance(t.Cold(leader.Slot)!, t.Cold(slot)!, spotted);
            float gap = Vector3.Distance(t.Next[leader.Slot].Position, t.Next[slot].Position);
            var task = (CharacterTask)t.Next[leader.Slot].Task;
            var targetTask = (CharacterTask)t.Next[slot].Task;
            Console.WriteLine($"tick {probe.World.Tick} leader task={task} gap={gap:F0} spotted={spotted} unseen={aggro?.Unseen(t, leader)} "
                + $"chance={chance:F0} target task={targetTask} senses={SensesOfLeader(probe, slot)}");
            foreach (var k in carry.TakeKnockouts()) Console.WriteLine($"  swing chance={k.Chance:F0} hit={k.Success}");
            probe.PrintMessages();
        }
    }

    /// <summary>
    /// Every 20 ticks <paramref name="rounds"/> times: the captive jobs on the player's leader (a slave watcher walking over to process it, #759)
    /// with each worker's phase, gap and how it senses the leader; <paramref name="walk"/> clears the leader's sneak first (the control run).
    /// </summary>
    static void Captors(ProbeWorld probe, int rounds, bool walk)
    {
        var t = probe.Characters;
        var leader = probe.World.Squads.Find(probe.World.Player.Squad)!.Leader;
        var captives = probe.World.SystemOf<Meitou.Simulation.Kidnap.CaptiveSystem>()!;
        if (walk) t.Cold(leader.Slot)!.Orders &= ~Meitou.Simulation.Player.CharacterOrders.Sneak;

        for (int round = 0; round < rounds; round++)
        {
            bool sneaking = (t.Cold(leader.Slot)!.Orders & Meitou.Simulation.Player.CharacterOrders.Sneak) != 0;
            Console.WriteLine($"tick {probe.World.Tick} leader {probe.Who(leader.Slot)} sneak={sneaking} jobs={captives.Jobs.Count}");
            foreach (var j in captives.Jobs)
                if (j.Captive == leader && t.TryResolveNext(j.Worker, out int w)) Console.WriteLine(CaptorLine(probe, j, w, leader.Slot));

            probe.PrintMessages();
            probe.Tick(20);
        }
    }

    /// <summary>
    /// One captive job on the leader: its kind and phase, the worker, their gap, the worker's sense entry of the leader and entry count, and
    /// whether it perceives the leader.
    /// </summary>
    static string CaptorLine(ProbeWorld probe, Meitou.Simulation.Kidnap.CaptiveJob j, int w, int leader)
    {
        var t = probe.Characters;
        float gap = Vector3.Distance(t.Next[w].Position, t.Next[leader].Position);
        int? entries = Meitou.Simulation.Bodies.CharacterFacts.Senses(probe.World, w)?.Entries.Count;
        bool perceives = Meitou.Simulation.Bodies.CharacterFacts.Perceives(probe.World, w, leader);

        return $"  {j.Kind} {j.Phase} by {probe.Who(w)} gap={gap:F0} senses={SensesOfLeader(probe, w)}/{entries} perceives={perceives}";
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
    /// Recruits the first character named like <paramref name="name"/> into the player's squad, then unloads the save's platoon it was made from
    /// and lists that platoon's members: one not gone would be made again when the platoon comes back (#701).
    /// </summary>
    static void Recruit(ProbeWorld probe, string name)
    {
        var world = probe.World;
        if (probe.Named(name) is not [int slot, ..]) { Console.WriteLine($"nobody named {name}"); return; }
        var cold = probe.Characters.Cold(slot)!;
        var saved = cold.Save is { } link ? world.Saved?.Platoons.FirstOrDefault(sp => sp.Name == link.PlatoonName) : null;
        Console.WriteLine($"recruiting {probe.Who(slot)} from {saved?.Name ?? "no saved platoon"}");
        world.Systems.OfType<DialogueSystem>().First().JoinPlayer(world, slot, cold);
        if (saved is null) return;

        SavedCharacters.Store(world, saved, saved.Anchor);
        foreach (var m in saved.Members)
            Console.WriteLine($"  member {m.Slot} {probe.Db.Find(m.Source.RecordId)?.Name ?? m.Source.RecordId} gone={m.Gone} dead={m.Dead}");
    }

    /// <summary>
    /// The unique-character table (entries whose record name contains <paramref name="name"/>, all when null): claimed, state, player flag and
    /// handle, then every character in the world made from that record with its squad, platoon and template, so a duplicated unique shows twice.
    /// </summary>
    static void Uniques(ProbeWorld probe, string? name)
    {
        var holders = UniqueHolders(probe);
        foreach (var (id, e) in probe.World.Uniques.All)
        {
            string recordName = probe.Db.Find(id)?.Name ?? id;
            if (name is not null && !recordName.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
            Console.WriteLine($"{recordName} ({id}) claimed={e.Claimed} state={e.State} player={e.Player} handle={e.Handle} saved={e.SavedHandle}");
            foreach (int i in holders.GetValueOrDefault(id) ?? []) Console.WriteLine("  " + UniqueHolder(probe, i));
        }
    }

    /// <summary>
    /// The slots of the characters in the world by CHARACTER record id.
    /// </summary>
    static Dictionary<string, List<int>> UniqueHolders(ProbeWorld probe)
    {
        var holders = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < probe.Characters.HighWater; i++)
        {
            if (!probe.Characters.Next[i].Alive || probe.Characters.Cold(i) is not { } c) continue;
            if (!holders.TryGetValue(c.RecordId, out var list)) holders[c.RecordId] = list = [];
            list.Add(i);
        }

        return holders;
    }

    /// <summary>
    /// One line for a character holding a unique record: who, its squad's platoon and template, and the character id.
    /// </summary>
    static string UniqueHolder(ProbeWorld probe, int i)
    {
        var c = probe.Characters.Cold(i)!;
        var squad = probe.World.Squads.Find(c.SquadId);
        return $"{probe.Who(i)} id={probe.Characters.IdOf(i)} platoon={squad?.PlatoonId} template={squad?.TemplateId} town={squad?.Town}";
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
    /// The fixtures within 300 units (on the ground plane) of the first character named like <paramref name="name"/>, with their occupants; a
    /// container also with its lock, grid and items.
    /// </summary>
    static void Cages(ProbeWorld probe, string name)
    {
        int who = probe.Named(name)[0];
        var c = probe.Characters.Cold(who)!;
        var at = probe.Characters.Next[who].Position;
        Console.WriteLine($"{probe.Who(who)} inWhat={c.InWhat} inSomething={c.InSomething}");

        foreach (var f in probe.World.Fixtures.All.Where(f => Vector2.Distance(new(f.Position.X, f.Position.Z), new(at.X, at.Z)) < 300))
        {
            Console.WriteLine($"fixture {f.Kind} {f.Id} record={f.RecordId} at {f.Position:F1} occupant={Occupant(probe, f)} "
                + $"designation={probe.World.Fixtures.RecordOf(f.Building)?.Designation}");
            if (f.Kind == FixtureKind.Container) Container(probe, f);
            if (f.Kind == FixtureKind.Door) Door(probe, f);
        }
    }

    /// <summary>
    /// A door fixture's motion, lock and the flags that decide them; a gate's also with the gate codes it joins and its gate state.
    /// </summary>
    static void Door(ProbeWorld probe, Fixture f)
    {
        Console.WriteLine($"  door motion={f.Motion} open={f.OpenAmount:F2} broken={f.DoorBroken} gate={f.IsGate} wantsLock={f.WantsLock} "
            + $"restored={f.Restored} lock={f.Lock?.Level} locked={f.Lock?.Locked} effective={f.Lock?.EffectivelyLocked}");
        if (probe.World.SystemOf<LockSystem>()?.Regions is not { } regions) return;

        var links = regions.Gates.Where(g => g.Building == f.Building).Select(g => string.Join("|", g.Codes));
        Console.WriteLine($"  gate codes=[{string.Join(" ", links)}] state={LockSystem.GateState(probe.World.Fixtures, f.Building)}");
    }

    /// <summary>
    /// The bed the first character named like <paramref name="name"/> lies in: its record, yaw and use node yaws, the lie spot and the sleeper's own
    /// place and yaw as loaded, in degrees (#718: a sleeper lying across a one-node bed).
    /// </summary>
    static void Sleeper(ProbeWorld probe, string name)
    {
        int who = probe.Named(name)[0];
        var c = probe.Characters.Cold(who)!;
        var me = probe.Characters.Next[who];
        Console.WriteLine($"{probe.Who(who)} inWhat={c.InWhat} at {me.Position:F1} yaw={Degrees(me.Yaw):F1}");

        // a player-built bed registers on the first tick, which may also see its sleeper's jobs get it up
        string? what = c.InWhat;
        if (what is not null && probe.World.Fixtures.Find(what) is null) probe.Tick(1);
        if (what is null || probe.World.Fixtures.Find(what) is not { Kind: FixtureKind.Bed } bed) return;

        var nodes = string.Join(", ", bed.Bed.Nodes?.Select(n => $"{n.At:F1} yaw {Degrees(n.Yaw):F1}") ?? []);
        var (spot, yaw) = FixtureRegistry.LieSpot(bed, me.Position, probe.World.GameHours);
        Console.WriteLine($"bed {bed.Id} '{probe.Db.Find(bed.RecordId)?.Name}' at {bed.Position:F1} marker={bed.Marker:F1} yaw={Degrees(bed.Yaw):F1} "
            + $"clip={bed.Bed.Clip} nodes=[{nodes}]");
        Console.WriteLine($"lie spot {spot:F1} yaw={Degrees(yaw):F1}");
    }

    /// <summary>
    /// Radians as degrees.
    /// </summary>
    static float Degrees(float radians) => radians * 180 / MathF.PI;

    /// <summary>
    /// A container fixture's record name, owner with its faction's lock level and spread, lock, storage fields and what it holds.
    /// </summary>
    static void Container(ProbeWorld probe, Fixture f)
    {
        var r = probe.Db.Find(f.RecordId);
        var owner = f.Owner >= 0 && f.Owner < probe.Data.Factions.Count ? probe.Data.Factions[f.Owner] : null;
        Console.WriteLine($"  '{r?.Name}' owner={probe.Faction(f.Owner)} ({owner?.DoorsLockLevel} +- {owner?.LockLevelRandom}) lock={f.Lock?.Level} "
            + $"locked={f.Lock?.Locked} units={f.Contents?.Items.Sum(i => i.Quantity)} "
            + $"grid={f.Contents?.Width}x{f.Contents?.Height} typeLimit={r?.GetInt("itemtype limit", 4)} "
            + $"limited={r?.GetReferences("limit inventory").Count}");
        foreach (var it in f.Contents?.Items ?? [])
            Console.WriteLine($"    {it.Quantity} x {it.Name} ({it.Record}) at {it.X},{it.Y} size {it.Width}x{it.Height}");
    }

    /// <summary>
    /// Orders the first character named like <paramref name="name"/> to cut or break the lock of <paramref name="fixture"/> (by id; none: its own
    /// cage's, for a prisoner) and follows it for 120 ticks.
    /// </summary>
    static void Force(ProbeWorld probe, string name, LockForce force, string? fixture = null)
    {
        int who = probe.Named(name)[0];
        var c = probe.Characters.Cold(who)!;
        var cage = probe.World.Fixtures.Find(fixture ?? c.InWhat!)!;
        Console.WriteLine($"{cage.Kind} {cage.Id} owner={cage.Owner} lock={cage.Lock?.Level} locked={cage.Lock?.EffectivelyLocked} "
            + $"tool={Lockpicking.BestTool(c)?.Name}");
        probe.World.Commands.Enqueue(new ForceLockOrder([probe.Characters.IdOf(who)], cage.Id, force) { Tick = probe.World.Tick });

        for (int round = 0; round < 6; round++)
        {
            probe.Tick(20);
            Console.WriteLine($"tick {probe.World.Tick} {probe.Who(who)} forcing={c.ForcingLock} locked={cage.Lock?.EffectivelyLocked} "
                + $"hardness={cage.Lock?.Hardness:F2} in={c.InSomething}");
            probe.PrintMessages();
        }
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
    /// The player's squad leader is put in the cage <paramref name="cage"/>, sneaks and gets out, then walks to <paramref name="walk"/> (X, Z on
    /// its floor) when given (#776: police at an unseen escapee); then every <paramref name="interval"/> ticks, <paramref name="rounds"/> times,
    /// its crime line and each NPC within 600 that sees it, has spotted it or fights it (<see cref="EscapeWatcher"/>).
    /// </summary>
    static void Escape(ProbeWorld probe, string cage, int rounds, int interval, Vector3? walk)
    {
        var t = probe.Characters;
        int leader = probe.World.Squads.Find(probe.World.Player.Squad)!.Leader.Slot;
        var id = t.IdOf(leader);
        var fixture = probe.World.Fixtures.Find(cage)!;
        bool caged = probe.World.Fixtures.Imprison(probe.World, id, fixture);
        Console.WriteLine($"imprison={caged} {fixture.Id} owner={probe.Faction(fixture.Owner)} at {fixture.Position:F1}");
        probe.Tick(100);

        if (fixture.Lock is { } lockState) lockState.Locked = false;
        t.Cold(leader)!.Orders |= Meitou.Simulation.Player.CharacterOrders.Sneak;
        probe.World.Commands.Enqueue(new GetOutOrder([id]) { Tick = probe.World.Tick });
        if (walk is { } to)
        {
            probe.Tick(20);
            var onFloor = new Vector3(to.X, t.Next[leader].Position.Y, to.Z);
            probe.World.Commands.Enqueue(new MoveOrder([id], onFloor) { Tick = probe.World.Tick });
        }

        for (int round = 0; round < rounds; round++)
        {
            probe.Tick(interval);
            EscapeRound(probe, leader);
        }
    }

    /// <summary>
    /// One round of <see cref="Escape"/>: the escapee's line, then its watchers'.
    /// </summary>
    static void EscapeRound(ProbeWorld probe, int leader)
    {
        var t = probe.Characters;
        var sight = probe.World.Sight;
        var memory = probe.World.Systems.OfType<DialogueSystem>().FirstOrDefault()?.Memory;
        var at = t.Next[leader].Position;
        var c = t.Cold(leader)!;
        Console.WriteLine($"tick {probe.World.Tick} {probe.Who(leader)} {Held(c)} escaped={c.Escaped} place={sight.PlaceOf(leader)} "
            + $"floor={sight.FloorOf(leader, at)} {CrimeLine(probe, memory, leader)}");

        foreach (int i in probe.Near(at, 600))
        {
            if (i != leader && EscapeWatcher(probe, i, leader) is { } line) Console.WriteLine("  " + line);
        }
        probe.PrintMessages();
    }

    /// <summary>
    /// An NPC in slot <paramref name="i"/> as it watches the escapee: its place, floor, plain sight factor, spot meter and goal, and when it fights
    /// the escapee what may hold it in place and its fight state; null when it neither sees, has spotted nor fights it.
    /// </summary>
    static string? EscapeWatcher(ProbeWorld probe, int i, int leader)
    {
        var t = probe.Characters;
        if (t.Cold(i) is not { IsPlayer: false } w) return null;
        var sight = probe.World.Sight;
        var aggro = probe.World.Systems.OfType<Meitou.Simulation.Aggro.AggroSystem>().First();
        var state = probe.World.Systems.OfType<CombatSystem>().First().StateOf(t.IdOf(i));
        float plain = sight.Sees(i, leader, t.Next[i].Position, t.Next[leader].Position, 600);
        bool spotted = aggro.Spotted(i, t.IdOf(leader));
        bool fights = state.Fighting && state.TargetSlot == leader;
        if (plain <= 0 && !spotted && !fights) return null;

        string fight = fights ? $" {Held(w)} {FightLine(state)}" : "";
        return $"{probe.Who(i)} place={sight.PlaceOf(i)} floor={sight.FloorOf(i, t.Next[i].Position)} sees={plain:F2} spotted={spotted} "
            + $"goal={w.Ai?.Goal?.Task.Type} row={w.Running?.Row}{fight}";
    }

    /// <summary>
    /// What may hold a character in place: its held state, behaviour, seat clip and path (length and end).
    /// </summary>
    static string Held(CharacterCold c)
    {
        string end = c.Path.Length > 0 ? c.Path[^1].ToString("F0") : "-";
        return $"held={c.InSomething} behaviour={c.Ai?.Behaviour} sit={c.Ai?.SitClip} path={c.Path.Length}->{end}";
    }

    /// <summary>
    /// Whether the characters named like <paramref name="name"/> can be talked to (by anyone, and by the nearest player character) and the roots
    /// of their talk event; with an event <paramref name="ev"/> (the talk event too), its roots with every line's conditions and effects.
    /// </summary>
    static void Talk(ProbeWorld probe, string name, int? ev)
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
            if (ev is not { } shown) continue;

            var seen = new HashSet<DialogueNode>();
            foreach (var root in dialogue.SetOf(probe.World, i).For(shown)) PrintLines(root, 1, seen);
        }
    }

    /// <summary>
    /// The player character nearest a character named like <paramref name="name"/> is ordered to talk to it; up to 60 s for the conversation
    /// to open, then each line said with its choices, clicking <paramref name="choices"/> in turn (#737: a caged prisoner's talk).
    /// </summary>
    static void Converse(ProbeWorld probe, string name, List<int> choices)
    {
        var t = probe.Characters;
        var players = Enumerable.Range(0, t.HighWater).Where(j => t.Next[j].Alive && t.Cold(j) is { IsPlayer: true }).ToList();
        var (talker, npc) = probe.Named(name)
            .SelectMany(n => players.Select(p => (p, n)))
            .MinBy(pair => Vector3.Distance(t.Next[pair.p].Position, t.Next[pair.n].Position));
        Console.WriteLine($"{probe.Who(talker)} talks to {probe.Who(npc)}");
        probe.World.Commands.Enqueue(new TalkOrder(t.IdOf(talker), t.IdOf(npc)) { Tick = probe.World.Tick });

        var dialogue = probe.World.Systems.OfType<DialogueSystem>().First();
        for (int waited = 0; dialogue.PublishConversation() is null && waited < 60 * 40; waited += 20) probe.Tick(20);
        foreach (int choice in choices.Prepend(-1))
        {
            if (choice >= 0)
            {
                probe.World.Commands.Enqueue(new DialogueChoice(choice) { Tick = probe.World.Tick });
                probe.Tick(1);
            }
            if (dialogue.PublishConversation() is not { } view) break;

            Console.WriteLine($"tick {probe.World.Tick} {view.SpeakerName}: {view.Text}");
            Console.WriteLine("  choices: " + string.Join(" | ", view.Choices.Select((text, i) => $"{i} {text}")));
        }

        Console.WriteLine($"open={dialogue.PublishConversation() is not null} gap={Vector3.Distance(t.Next[talker].Position, t.Next[npc].Position):F0} "
            + $"{probe.Who(npc)} player={t.Cold(npc)!.IsPlayer}");
        probe.PrintMessages();
    }

    /// <summary>
    /// A dialogue node and the lines under it: id, conditions (condition, compare, who, value), effects (action:value) and the first text.
    /// </summary>
    static void PrintLines(DialogueNode node, int depth, HashSet<DialogueNode> seen)
    {
        if (!seen.Add(node)) return;

        string conditions = string.Join(" ", node.Conditions.Select(c => $"C{c.Condition}/{c.CompareBy}/{c.Who}/{c.Value}"));
        string effects = string.Join(" ", node.Effects.Select(e => $"DA{e.Action}:{e.Value}"));
        string text = node.Texts.Count > 0 ? node.Texts[0] : "";
        Console.WriteLine($"{new string(' ', depth * 2)}{node.StringId} [{conditions}] [{effects}] {text}");
        foreach (var child in node.Children) PrintLines(child, depth + 1, seen);
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
    /// The containers within <paramref name="radius"/> of the leader: building, record, lock, own items, and the building shop whose stock
    /// container each is, with its rectangle and the stock items in it (what its window shows).
    /// </summary>
    static void Containers(ProbeWorld probe, float radius)
    {
        var world = probe.World;

        foreach (var f in world.Fixtures.All.Where(f => f.Contents is not null && Vector3.Distance(f.Position, probe.LeaderPosition) < radius))
        {
            Console.WriteLine($"container {f.Id} building={f.Building} '{probe.Db.Find(f.RecordId)?.Name}' at {f.Position:F0} "
                + $"locked={f.Lock?.Locked} own={f.Contents!.Items.Count}");

            foreach (var q in world.Squads.All)
            {
                if (q.Shop is not { } shop || shop.Building != f.Building || !shop.RegionOf(f.Id, out var region)) continue;

                int held = shop.Stock.Items.Count(region.Holds);
                Console.WriteLine($"  shop of squad {q.Id} '{q.Name}' template={q.TemplateId} region={region} stock={shop.Stock.Items.Count} "
                    + $"here={held}");
            }
        }

        foreach (var q in world.Squads.All.Where(q => q.HomeBuilding is { } h && Vector3.Distance(h.Position, probe.LeaderPosition) < radius))
        {
            string shop = q.Shop is { } s ? $"{s.Stock.Items.Count} items, fills={s.Fills} restock={s.NextRestockTick}" : "none";
            Console.WriteLine($"home {q.HomeBuilding!.Key} squad {q.Id} '{q.Name}' template={q.TemplateId} members={q.Members.Count} "
                + $"shop={shop} tick={world.Tick}");
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
            if (probe.World.Machines.Find(site.Id) is { } m) Console.WriteLine($"  {MachineLine(m)}");
        }
    }

    /// <summary>
    /// A machine's status, output, inputs and, for a crafting bench, its queue, repeat flag and finished items.
    /// </summary>
    static string MachineLine(Meitou.Simulation.Production.Machine m)
    {
        var inputs = string.Join(" ", m.Inputs.Select(i => $"{i.Item}:{i.Count}/{i.Level:F2}"));
        var output = m.Output is { } o ? $"{o.Recipe.Item}:{o.Count} ({o.Progress:F2})" : "none";
        var craft = m.Craft is { } c
            ? $" queue=[{string.Join(", ", c.Queue.Select(j => $"{j.Option.Item}:{j.Progress:F2}"))}] repeat={c.Repeat} "
                + $"done=[{string.Join(", ", c.Done.Select(d => $"{d.Name}x{d.Quantity}"))}]"
            : "";
        return $"machine '{m.Data.Id}' st={m.Status} out={output} in=[{inputs}]{craft} operators={m.LastOperators}";
    }

    /// <summary>
    /// The machines of town buildings (<see cref="Meitou.Simulation.Production.ProductionSystem.IsTownWork"/>) within <paramref name="radius"/> of the leader: status, output,
    /// inputs, a farm's growth, death and harvested plants, and last tick's operators.
    /// </summary>
    static void Machines(ProbeWorld probe, float radius)
    {
        foreach (var m in probe.World.Machines.All.Where(m => Meitou.Simulation.Production.ProductionSystem.IsTownWork(m.Site)))
        {
            if (Vector3.Distance(m.Position, probe.LeaderPosition) > radius) continue;

            var inputs = string.Join(" ", m.Inputs.Select(i => $"{i.Item}:{i.Count}/{i.Level:F2}"));
            var output = m.Output is { } o ? $"{o.Recipe.Item}:{o.Count} ({o.Progress:F2})" : "none";
            var farm = m.Farm is { } f ? $" growth={f.Growth:F2} death={f.Death:F2} harvested={f.Harvested}/{f.Plants.Length} farm={f.Status}" : "";
            Console.WriteLine($"{m.Site} '{m.Data.Id}' at {m.Position:F0} st={m.Status} out={output} in=[{inputs}]{farm} operators={m.LastOperators}");
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
    /// The point given as three numbers from <paramref name="first"/> on.
    /// </summary>
    static Vector3 Point(List<string> a, int first) => new(float.Parse(a[first]), float.Parse(a[first + 1]), float.Parse(a[first + 2]));

    /// <summary>
    /// The solid parts (<see cref="SolidHits"/>: terrain and the buildings' collision) on the segment between two points, as the spot meter's sight
    /// ray and bolts test them: where it first hits, terrain or building. Needs <c>--nav</c>, which gathers the buildings' collision.
    /// </summary>
    static void Ray(ProbeWorld probe, Vector3 from, Vector3 to)
    {
        var combat = probe.World.SystemOf<CombatSystem>();
        var seg = to - from;
        float hit = SolidHits.Hit(probe.World.Walkability, combat?.Buildings, from, seg, RangedTuning.TerrainStep, out bool building);
        string what = hit < 0 ? "clear" : $"{(building ? "building" : "terrain")} at {from + seg * hit:F1}, {hit * seg.Length():F1} from the start";
        Console.WriteLine($"{from:F1} -> {to:F1} ({seg.Length():F1}): {what}; buildings {(combat?.Buildings is null ? "not loaded" : "loaded")}");
    }

    /// <summary>
    /// How each NPC within <paramref name="radius"/> of the player's squad leader sees it after a tick: its place, the sight range between them, the
    /// plain sight test (<see cref="SightMap.Sees"/>), the sight ray and the test with the ray (<see cref="SightRay.Sees"/>). Needs <c>--nav</c>.
    /// </summary>
    static void SeesLeader(ProbeWorld probe, float radius)
    {
        probe.Tick(1);
        var table = probe.Characters;
        var combat = probe.World.SystemOf<CombatSystem>();
        var sight = probe.World.Sight;
        int leader = probe.Near(probe.LeaderPosition, 0.5f).First(i => table.Cold(i)!.IsPlayer);
        var at = table.Next[leader].Position;
        Console.WriteLine($"leader {at:F1} place {Place(sight, sight.LocationOf(leader))}");
        foreach (int i in probe.Near(at, radius))
        {
            if (table.Cold(i) is not { IsPlayer: false } c) continue;

            var eye = table.Next[i].Position;
            float outdoor = Meitou.Simulation.Aggro.AggroSystem.RangeOf(c, Meitou.Simulation.Aggro.AggroSystem.OutdoorSight);
            float plain = sight.Sees(i, leader, eye, at, outdoor);
            bool clear = SightRay.Clear(probe.World.Walkability, combat?.Buildings, eye, at);
            float rayed = SightRay.Sees(probe.World, i, leader, eye, at, outdoor);
            Console.WriteLine($"  {probe.Who(i)} at {eye:F1} place {Place(sight, sight.LocationOf(i))} range {sight.Range(i, leader, outdoor):F0} "
                + $"sees {plain:F2} ray {(clear ? "clear" : "blocked")} with ray {rayed:F2}");
        }
    }

    /// <summary>
    /// Every walkable point within <paramref name="radius"/> (every <paramref name="step"/>) from which a looker's spot-meter ray reaches a sneaker
    /// standing at <paramref name="centre"/> (<see cref="SightRay.Clear"/>), or one within <paramref name="spread"/> of it (every 16), with the place each
    /// point gets and whether it is the target's. Needs <c>--nav</c>.
    /// </summary>
    static void Sightlines(ProbeWorld probe, Vector3 centre, float radius, float step, float spread)
    {
        if (spread <= 0)
        {
            Sightlines(probe, centre, radius, step);
            return;
        }

        var heights = new List<float>();
        for (float x = centre.X - spread; x <= centre.X + spread; x += 16)
        {
            for (float z = centre.Z - spread; z <= centre.Z + spread; z += 16)
            {
                heights.Clear();
                probe.Nav!.Walkability.World.HeightsAt(x, z, heights);
                foreach (float h in heights) Sightlines(probe, new Vector3(x, h, z), radius, step);
            }
        }
    }

    /// <summary>
    /// <see cref="Sightlines(ProbeWorld, Vector3, float, float, float)"/> for one target.
    /// </summary>
    static void Sightlines(ProbeWorld probe, Vector3 target, float radius, float step)
    {
        var world = probe.Nav!.Walkability.World;
        var combat = probe.World.SystemOf<CombatSystem>();
        var sight = probe.World.Sight;
        int targetPlace = sight.PlaceAtPoint(target);
        var heights = new List<float>();
        Console.WriteLine($"target {target:F1} place {Place(sight, targetPlace)}");
        for (float x = target.X - radius; x <= target.X + radius; x += step)
        {
            for (float z = target.Z - radius; z <= target.Z + radius; z += step)
            {
                heights.Clear();
                world.HeightsAt(x, z, heights);
                foreach (float h in heights) Sightline(probe, combat, new Vector3(x, h, z), target, targetPlace);
            }
        }
    }

    /// <summary>
    /// Prints one looker point of <see cref="Sightlines(ProbeWorld, Vector3, float, float)"/> when its ray to the target is clear.
    /// </summary>
    static void Sightline(ProbeWorld probe, CombatSystem? combat, Vector3 eye, Vector3 target, int targetPlace)
    {
        if (!SightRay.Clear(probe.World.Walkability, combat?.Buildings, eye, target)) return;

        float d = Vector3.Distance(eye, target);
        int place = probe.World.Sight.PlaceAtPoint(eye);
        string nav = probe.World.Walkability.LineOfSight(eye, target) ? "" : " (navmesh line blocked)";
        Console.WriteLine($"  clear from {eye:F1} d={d:F0} place {Place(probe.World.Sight, place)}{(place == targetPlace ? " same" : "")}{nav}");
    }

    /// <summary>
    /// A place's building key, or "outdoors / on top".
    /// </summary>
    static string Place(SightMap sight, int place) => place >= 0 ? sight.PlaceKey(place) : "outdoors/on top";

    /// <summary>
    /// The place test (<see cref="SightMap.PlaceAtPoint"/>, what <c>SteersAt</c> compares) over the walkable surface round the leader, every
    /// <paramref name="step"/> within <paramref name="radius"/>: the points each building's place takes, the outdoor-mesh points it counts as
    /// indoors and the indoor-mesh points it counts as outdoors, with a sample of each. Needs <c>--nav</c>.
    /// </summary>
    static void Places(ProbeWorld probe, float radius, float step)
    {
        var world = probe.Nav!.Walkability.World;
        var centre = probe.LeaderPosition;
        var heights = new List<float>();
        var odd = new Dictionary<string, (int Count, Vector3 Sample)>();
        int outdoor = 0, indoor = 0;
        for (float x = centre.X - radius; x <= centre.X + radius; x += step)
        {
            for (float z = centre.Z - radius; z <= centre.Z + radius; z += step)
            {
                heights.Clear();
                world.HeightsAt(x, z, heights);
                foreach (float h in heights) NotePlace(probe, world, new Vector3(x, h, z), odd, ref outdoor, ref indoor);
            }
        }

        Console.WriteLine($"{outdoor} outdoor-mesh and {indoor} indoor-mesh points within {radius} of {centre:F0}");
        foreach (var (what, (count, sample)) in odd.OrderByDescending(e => e.Value.Count))
            Console.WriteLine($"  {count,6} {what}  e.g. {sample:F0}");
    }

    /// <summary>
    /// Counts one surface point for <see cref="Places"/>: the building whose place it gets, and a mismatch between the place and whether it lies on an
    /// interior polygon.
    /// </summary>
    static void NotePlace(ProbeWorld probe, NavWorld world, Vector3 at, Dictionary<string, (int Count, Vector3 Sample)> odd, ref int outdoor,
        ref int indoor)
    {
        if (!world.TryFindPolygon(at, 0.5f, out var face, out _)) return;

        bool interior = face.Polygon >= world.Mesh(face.Zone).InteriorStart;
        int place = probe.World.Sight.PlaceAtPoint(at);
        if (interior) indoor++;
        else outdoor++;

        string? key = place >= 0 ? probe.World.Sight.PlaceKey(place) : null;
        if (key is not null) Tally(odd, $"place {key}", at);
        if (interior == key is not null) return;

        Tally(odd, key is not null ? $"outdoor mesh, place {key}" : "indoor mesh, outdoors by the place test", at);
    }

    /// <summary>
    /// Adds one to <paramref name="what"/>'s count, keeping its first point as the sample.
    /// </summary>
    static void Tally(Dictionary<string, (int Count, Vector3 Sample)> odd, string what, Vector3 at) =>
        odd[what] = odd.TryGetValue(what, out var seen) ? (seen.Count + 1, seen.Sample) : (1, at);

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
        Console.WriteLine($"  {RuinState(probe, b)}");
        if (layouts.Find(resolved.Record, name, exterior: false) is not { } layout) return;

        foreach (var item in layouts.Items(layout, resolved.Placement))
        {
            var (at, _) = BuildingLayouts.Place(resolved.Position, resolved.Placement.Rotation, item);
            Console.WriteLine($"  {item.InstanceId} '{item.Building.Name}' floor={item.Floor} path={item.Building.GetInt("path mode", 2)} at {at:F0}");
        }
    }

    /// <summary>
    /// Whether a town building is a ruin now, with what decides it: the level file's and housing's state, and the save's <c>destroyed</c> and
    /// <c>residents template</c>.
    /// </summary>
    static string RuinState(ProbeWorld probe, TownBuilding b)
    {
        var saved = probe.World.Fixtures.Saved?.GetValueOrDefault(b.Key);
        string savedDestroyed = saved is not null && saved.Bools.TryGetValue("destroyed", out bool d) ? d.ToString() : "none";
        string savedTemplate = saved?.Strings.GetValueOrDefault(ResidentRuins.ResidentsTemplateKey) ?? "none";

        return $"destroyed={probe.World.Fixtures.IsDestroyed(b)} level={b.LevelDestroyed} restoredByResidents={b.RestoredByResidents} "
            + $"residents='{b.ResidentsTemplate}' saved destroyed={savedDestroyed} residents='{savedTemplate}'";
    }

    /// <summary>
    /// The modes, on standard error; returns 2.
    /// </summary>
    static int Usage()
    {
        Console.Error.WriteLine("""
            meitou-tools probe <save folder> <mode> [args] [--ticks n (default 300)] [--nav] [--stream] [--at x,z] [--seed n (default 1)] [--load-order <file>] [--mod-dir <folder>]...
              near <radius>            characters near the player's squad leader
              crowd <radius> [rounds] [interval]  characters near the leader every interval (40) ticks: down, water state, goal, load, fight target
              fight <slot> [rounds] [interval]  a character's combat state and body, and everyone targeting it: gap, blows thrown and landed
              crime <name> [rounds] [interval]  a character's saved squad memory, then its pending crime, bounties and who targets it with which tags
              nests                    the wild layout's nests whose first candidate spot the original would refuse
              bait <radius> [rounds]   send the player's squad after the nearest other squad each round, listing squads as squads does
              squads <radius> [rounds]  the other squads near the leader: leader state, goal, members' spread every 40 ticks
              watch <name> [rounds]    a character's task, AI, fight target, hunger and decision trace every 40 ticks (default 10 rounds)
              anim <radius> [ticks] [name]  the animation layers of the seated characters near the leader (or those named) each tick, then each clip's rate
              turrets <character> <count> <x> <z> [rounds] [interval]  spawn hostiles there; the turret squads near: goals, claims, enemies, gunners
              assault <name> [rounds] [interval]  the player's squad attacks a named character; who is near, indoors or out, and how each senses the leader
              move <name> <x> <z> [rounds]  order a character to walk to a point, then watch it
              premises [rounds] [interval]  the building the leader stands in (needs --nav): public flags, house lock, doors, residents, who is inside, lines said
              ko <slot> [rounds]       the sneaking leader's stealth KO order on a slot: the chance, the target's spot meter and the task every 20 ticks
              slaves <radius>          slave state, owner faction, master squad and squad town near the leader
              captors [rounds] [walk]  captive jobs on the leader: worker, phase, gap, its senses of the leader; walk clears sneak (#759)
              cages <name>             fixtures within 300 of a character, with occupants and building designation; a container's lock, grid and items
              sleeper <name>           the bed a character lies in: record, yaw, use node yaws, lie spot and the sleeper's own yaw
              force <name> <tools|strength> [fixture id]  a character cuts or breaks a fixture's lock (a prisoner its own cage's); 120 ticks of it
              cage [cage id]           the carrying player character puts its load in a cage; 600 ticks of it
              escape <cage id> [rounds] [interval] [x z]  the leader is caged, sneaks out and walks to x z: who sees, has spotted or fights it (#776)
              uniques [name]           the unique-character table (claimed, state, handle) and every character made from each record
              recruit <name>           a character joins the player, its save platoon unloads: the members it would make again
              places <radius> [step]   the place test over the walkable surface round the leader (needs --nav): outdoor-mesh points it puts indoors, and the reverse
              ray <x y z> <x y z>      the sight ray between two points (needs --nav): where it first hits the terrain or a building
              sightlines <x y z> <radius> [step] [spread]  walkable points whose spot-meter ray reaches the spot (or each spot within spread), with places
              sees <radius>            NPCs near the leader: place, sight range, the plain sight test and with the sight ray (needs --nav)
              talk <name> [event]      whether a character can be talked to, and its roots for the talk event; with an event, their lines' effects
              converse <name> [choice...]  the nearest player character talks to a character: each line said and its choices, clicking the given ones
              storage <name> <radius>  storages near a character: type limit, grid, items and whether each is accepted
              containers <radius>      containers near the leader: own items, building shop stock in each, homes
              sites <radius>           the player's buildings near the leader with floor and parent, and each machine's state and craft queue
              machines <radius>        town buildings' machines near the leader: status, output, inputs, farm growth and operators
              research                 the research state and each bench's rate factors
              resave                   re-write the buildings into the save and list ids held by two zones
              heights <x> <z>          terrain and navmesh heights at a point (needs --nav)
              layout <building key>    a town building (zone:instance id): layout names now, ruin state and its interior layout's objects (needs --nav)
            --stream: build only the player's box first and stream the other zones in as the game does, ticks at game speed (implies --nav)
            """);
        return 2;
    }
}
