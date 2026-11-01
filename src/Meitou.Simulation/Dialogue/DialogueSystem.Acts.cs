using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;

namespace Meitou.Simulation.Dialogue;

public sealed partial class DialogueSystem
{
    // DA_FOLLOW_WHILE_TALKING: who follows whom while its conversation lasts (hashed).
    readonly SortedDictionary<CharacterId, CharacterId> followers = new(Comparer<CharacterId>.Create(CompareIds));
    // The tick the last "Alarm raised" was posted (DA_CRIME_ALARM; at most one per 30 s, FUN_1409335e0); hashed.

    /// <summary>The original's <c>Task_Runaway</c> first run (task 280, DA 34): 2000 + 400 x random units away from the threat (as <see cref="FleeSystem.FirstRun"/>).</summary>
    const float RunAwayDistance = FleeSystem.FirstRun, RunAwaySpread = FleeSystem.RunSpread;

    /// <summary>
    /// The short-term tag a character's squad sets on another (<c>FUN_1406744a0</c>): TEMP_ENEMY is not set on a character of an allied faction or one the speaker regards as friendly.
    /// </summary>
    void FlagFrom(World world, CharacterCold mc, CharacterId me, CharacterId who, CharacterCold wc, int tag)
    {
        if (tag == MemoryTag.TempEnemy && (data.Relations.Contains(mc.Faction) && data.Relations.Contains(wc.Faction) && data.Relations.IsAlly(mc.Faction, wc.Faction)
            || RegardsFriendly(world, me, mc, who, wc))) return;
        Memory.Flag(world, mc.SquadId, who, tag);
    }

    /// <summary>
    /// <paramref name="by"/>'s squad tags <paramref name="who"/> TEMP_ENEMY, so every mate takes it for an enemy whatever the relations say (docs/game/dialogue.md "Squad memory"): an
    /// attacked character on its attacker (<c>AI::underMeleeAttack</c> / <c>underRangedAttack</c>, <c>Character::hitByMeleeAttack</c>, <c>iShotYou</c>), and a character that
    /// <c>Character::attackTarget</c> sends at someone on its target.
    /// </summary>
    internal void TagEnemy(World world, int by, int who)
    {
        var table = world.Characters;
        if (table.Cold(by) is not { SquadId: >= 0 } bc || table.Cold(who) is not { } wc) return;
        FlagFrom(world, bc, table.IdOf(by), table.IdOf(who), wc, MemoryTag.TempEnemy);
    }

    /// <summary>
    /// "A regards B as friendly" (Character slot 126, <c>FUN_1407923d0</c>): the same faction; never when A's squad holds TEMP_ENEMY on B (or, for a player-faction A, B's squad on A);
    /// always when B's squad holds TEMP_ALLY on a player-faction A, A's squad TEMP_ALLY on B, or A loves B (DC 52); else A's faction is allied to B's. Engine: the uniform disguise
    /// the original can substitute for B's faction is not read.
    /// </summary>
    bool RegardsFriendly(World world, CharacterId a, CharacterCold ac, CharacterId b, CharacterCold bc)
    {
        if (ac.Faction == bc.Faction) return true;
        // One memory lookup each way (A's squad on B, B's on A) for all the tag tests.
        var (ab, abEntry) = Memory.Read(world, ac.SquadId, b);
        if (Has(ab, MemoryTag.TempEnemy)) return false;
        if (ac.IsPlayer || ac.Faction == world.Player.Faction)
        {
            var (ba, _) = Memory.Read(world, bc.SquadId, a);
            if (Has(ba, MemoryTag.TempEnemy)) return false;
            if (Has(ba, MemoryTag.TempAlly)) return true;
        }
        if (Has(ab, MemoryTag.TempAlly) || (abEntry?.Long & MemoryTag.LoveMask) is > 0) return true;
        return data.Relations.Contains(ac.Faction) && data.Relations.Contains(bc.Faction) && data.Relations.IsAlly(ac.Faction, bc.Faction);
    }

    /// <summary>
    /// A character put <paramref name="captive"/> in a cage (<c>Task_PutInSomething</c> for the cage, <c>FUN_140358670</c>, <b>Observed</b>): the captive's squad remembers the captor with
    /// the long-term tag 5 MY_CAPTOR. Wired to <see cref="FactionDeeds.CagedHook"/>.
    /// </summary>
    public void Captured(World world, int captor, int captive)
    {
        var table = world.Characters;
        if (table.Cold(captive) is { SquadId: >= 0 } cc && table.Cold(captor) is not null) Memory.Remember(world, cc.SquadId, table.IdOf(captor), MemoryTag.Captor);
    }

    /// <summary>
    /// Event 13 UNLOCK_MY_CAGE_OR_SHACKLES fired by a freed prisoner at its liberator (<c>FUN_140684990</c>, <b>Observed</b>): unless the speaker's squad already remembers the liberator as its captor,
    /// it remembers it with the long-term tag 3 FREED_ME and the short-term TEMP_ALLY. The original does this before choosing a line, whether or not one is said; only a speaker that is
    /// awake gets that far (a dead or unconscious one only for the events of a mask that excludes 13).
    /// </summary>
    void Freed(World world, CharacterId speaker, CharacterId liberator)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(speaker, out int s) || !Awake(table, s) || table.Cold(s) is not { SquadId: >= 0 } sc) return;
        if (Memory.Find(world, sc.SquadId, liberator) is { } known && (known.Long >> MemoryTag.Captor & 1) != 0) return;
        Memory.Remember(world, sc.SquadId, liberator, MemoryTag.FreedMe);
        Memory.Flag(world, sc.SquadId, liberator, MemoryTag.TempAlly);
    }

    /// <summary>The medic finders' slot 126 (<c>isAlly</c>, <see cref="RegardsFriendly(World, CharacterId, CharacterCold, CharacterId, CharacterCold)"/>) for two characters by slot.</summary>
    public bool RegardsFriendly(World world, int a, int b)
    {
        var table = world.Characters;
        return table.Cold(a) is { } ac && table.Cold(b) is { } bc && RegardsFriendly(world, table.IdOf(a), ac, table.IdOf(b), bc);
    }

    /// <summary>The loot finder's <c>FUN_14059a7b0</c> (DC 54, <see cref="ScrewOver(World, CharacterId, CharacterCold, CharacterId, CharacterCold)"/>) for two characters by slot.</summary>
    public bool ScrewOver(World world, int a, int b)
    {
        var table = world.Characters;
        return table.Cold(a) is { } ac && table.Cold(b) is { } bc && ScrewOver(world, table.IdOf(a), ac, table.IdOf(b), bc);
    }

    /// <summary>The medic finders' <c>FUN_14059a670</c> (DC 55, <see cref="Helps"/>) for two characters by slot.</summary>
    public bool Helps(World world, int a, int b)
    {
        var table = world.Characters;
        return table.Cold(a) is { } ac && table.Cold(b) is { } bc && Helps(world, table.IdOf(a), ac, table.IdOf(b), bc);
    }

    /// <summary>
    /// What the squad memory says to slot 125 (<c>isEnemy</c>, <c>FUN_14079c950</c>): 1 when a's squad holds TEMP_ENEMY on b (or, for a player-faction a, b's squad on a), -1 when it holds TEMP_ALLY on b, else 0
    /// (the faction relation decides). Another faction's squads only; the same faction is never an enemy.
    /// </summary>
    public int EnemyMemory(World world, int a, int b)
    {
        var table = world.Characters;
        if (table.Cold(a) is not { } ac || table.Cold(b) is not { } bc || ac.Faction == bc.Faction) return 0;
        var (ida, idb) = (table.IdOf(a), table.IdOf(b));
        var (ab, _) = Memory.Read(world, ac.SquadId, idb);
        if (Has(ab, MemoryTag.TempEnemy) || (ac.IsPlayer || ac.Faction == world.Player.Faction) && Has(Memory.Read(world, bc.SquadId, ida).Short, MemoryTag.TempEnemy)) return 1;
        return Has(ab, MemoryTag.TempAlly) ? -1 : 0;
    }

    static bool Has(uint tags, int tag) => (tags >> tag & 1) != 0;

    /// <summary>DC 52 I_LOVE_THIS_GUY (<c>FUN_140599590</c>): the squad's long-term tags on the character hold LIFESAVER or FREED_ME.</summary>
    bool Loves(World world, CharacterCold mc, CharacterId who) => (Memory.Find(world, mc.SquadId, who)?.Long & MemoryTag.LoveMask) is > 0;

    /// <summary>DC 53 I_HATE_THIS_GUY (<c>FUN_1405995d0</c>): STOLE_FROM_ME, MY_CAPTOR, DEFEATED_MY_SQUAD_ONCE or KILLED_MY_FRIEND.</summary>
    bool Hates(World world, CharacterCold mc, CharacterId who) => (Memory.Find(world, mc.SquadId, who)?.Long & MemoryTag.HateMask) is > 0;

    /// <summary>
    /// DC 54 I_SHOULD_SCREW_THIS_GUY_OVER (<c>FUN_14059a7b0</c>), each rule overriding the ones before: either is a bandit; not when both have the same NPC class; yes for a traitorous
    /// speaker; not when it regards S as friendly; not for an honourable speaker; yes when S's faction owns the speaker as a slave; not when it loves S; yes when it hates S; yes when S is dead.
    /// </summary>
    bool ScrewOver(World world, CharacterId me, CharacterCold mc, CharacterId s, CharacterCold sc)
    {
        bool r = mc.NpcClass == FundamentalType.Bandit || sc.NpcClass == FundamentalType.Bandit;
        if (mc.NpcClass == sc.NpcClass) r = false;
        int p = PersonalityOf(world, me, mc);
        if (p == Personality.Traitorous) r = true;
        if (RegardsFriendly(world, me, mc, s, sc)) r = false;
        if (p == Personality.Honorable) r = false;
        if (mc.SlaveOwner >= 0 && mc.SlaveOwner == sc.Faction) r = true;
        if (Loves(world, mc, s)) r = false;
        if (Hates(world, mc, s)) r = true;
        if (sc.Medical?.Dead == true) r = true;
        return r;
    }

    /// <summary>
    /// DC 55 I_SHOULD_HELP_THIS_GUY (<c>FUN_14059a670</c>), each rule overriding the ones before: the same NPC class; not for a traitorous or bandit speaker; yes when it regards S as
    /// friendly; yes when both are honourable; yes when it loves S; not when it hates S.
    /// </summary>
    bool Helps(World world, CharacterId me, CharacterCold mc, CharacterId s, CharacterCold sc)
    {
        bool r = mc.NpcClass == sc.NpcClass;
        int p = PersonalityOf(world, me, mc);
        if (p == Personality.Traitorous || mc.NpcClass == FundamentalType.Bandit) r = false;
        if (RegardsFriendly(world, me, mc, s, sc)) r = true;
        if (p == Personality.Honorable && PersonalityOf(world, s, sc) == Personality.Honorable) r = true;
        if (Loves(world, mc, s)) r = true;
        if (Hates(world, mc, s)) r = false;
        return r;
    }

    /// <summary>
    /// DA_RUN_AWAY (34): the speaker drops what it does and runs from the target (task 280, <c>Task_Runaway</c>'s first run; engine: one run at full speed, then its AI takes over), and its
    /// squad tags the target AGGRESSOR.
    /// </summary>
    void RunAway(World world, CharacterId me, CharacterCold mc, CharacterId target, CharacterCold tc)
    {
        FlagFrom(world, mc, me, target, tc, MemoryTag.Aggressor);
        var table = world.Characters;
        if (mc.IsPlayer || !table.TryResolveNext(me, out int s) || Position(world, target) is not { } from) return;
        var here = table.Next[s].Position;
        var away = new Vector2(here.X - from.X, here.Z - from.Z);
        away = away.LengthSquared() > 1e-6f ? Vector2.Normalize(away) : Vector2.UnitX;
        float r = Rng.Float(NextRoll(world, me));
        TaskStart.Walk(world, s, new Vector2(from.X, from.Z) + away * (RunAwayDistance + RunAwaySpread * r), SpeedMode.Free, TaskType.RUN_AWAY_FORCED, TaskSource.Npc);
    }

    /// <summary>
    /// DA_GO_HOME (7): the speaker walks home (task 19; Meitou: its squad's home building, else its town, else its home centre, as the AI's going home) and its squad tags the target
    /// AGGRESSOR.
    /// </summary>
    void GoHome(World world, CharacterId me, CharacterCold mc, CharacterId target, CharacterCold? tc)
    {
        if (tc is not null) FlagFrom(world, mc, me, target, tc, MemoryTag.Aggressor);
        var table = world.Characters;
        if (mc.IsPlayer || !table.TryResolveNext(me, out int s) || world.Squads.Find(mc.SquadId) is not { } squad) return;
        Vector2 home = squad.Ai?.Home is { } h ? new(h.Position.X, h.Position.Z)
            : squad.Ai is { Town: >= 0 } board && board.Town < data.Sites.Count ? new(data.Sites[board.Town].Position.X, data.Sites[board.Town].Position.Z)
            : squad.HomeCentre;
        TaskStart.Walk(world, s, home, SpeedMode.Walk, TaskType.GO_HOMEBUILDING, TaskSource.Npc);
    }

    /// <summary>DA_SUMMON_MY_SQUAD (52): the speaker's squadmates run to it (task 67, "Running").</summary>
    void Summon(World world, CharacterId me)
    {
        var table = world.Characters;
        if (Position(world, me) is not { } at) return;
        foreach (var m in SquadOf(world, me))
            if (m != me && table.TryResolveNext(m, out int s) && Awake(table, s) && table.Cold(s) is { IsPlayer: false, InCombat: false })
                TaskStart.Walk(world, s, new Vector2(at.X, at.Z), SpeedMode.Free, TaskType.MOVE_ON_FREE_WILL_FAST, TaskSource.Npc);
    }

    /// <summary>
    /// DA_SEPARATE_TO_MY_OWN_SQUAD (28, <c>FUN_1405ce4e0</c>): the speaker (the target when the speaker is of the player's faction) leaves for a new squad of its faction made where it
    /// stands, which takes a copy of the old squad's memory; nothing when it is alone already. Engine: the new NPC squad keeps the old one's template and home radius, no town, and the
    /// AI makes it a fresh mind.
    /// </summary>
    void Separate(World world, CharacterId who)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(who, out int s) || table.Cold(s) is not { SquadId: >= 0 } c || world.Squads.Find(c.SquadId) is not { } old || old.Members.Count <= 1) return;
        var at = table.Next[s].Position;
        Squad squad;
        if (c.IsPlayer) squad = SquadManagement.NewSquad(world, data.Db, at, data.Relations.Contains(c.Faction) && data.Factions[c.Faction].Name is { Length: > 0 } f ? f : "Nameless");
        else
        {
            squad = new Squad { Id = world.Squads.NextId(), TemplateId = old.TemplateId, Faction = old.Faction, Position = at, HomeCentre = new(at.X, at.Z), HomeRadius = old.HomeRadius };
            world.Squads.Add(squad);
            c.Ai = null;
            c.Role = (int)SquadRole.Leader;
        }
        Memory.Copy(old.Id, squad.Id);
        Leave(world, who, c);
        c.SquadId = squad.Id;
        squad.Members.Add(who);
        if (squad.Leader.IsNone) squad.Leader = who;
        if (!c.IsPlayer) TaskStart.Stand(table, s);
    }

    /// <summary>
    /// DA_MASSIVE_ALARM (56): when the speaker is in a town, every member of its faction's squads tags the target TEMP_ENEMY, fires event 66 at it and goes for it (task 29 to its
    /// position; Meitou: an attack order), and its hearing of the target is alarmed for 5 s (<c>FUN_1405cb0a0</c> over <c>FUN_140858f40</c>). Engine: only loaded squads.
    /// </summary>
    void MassiveAlarm(World world, CharacterId me, CharacterCold mc, CharacterId target, CharacterCold tc)
    {
        if (Position(world, target) is null || Position(world, me) is not { } here || TownAt(here) < 0) return;
        var table = world.Characters;
        var fighters = new List<CharacterId>();
        foreach (var squad in world.Squads.All)
        {
            if (squad.Faction != mc.Faction) continue;
            foreach (var m in squad.Members)
            {
                if (!table.TryResolveNext(m, out int s) || !Awake(table, s) || table.Cold(s) is not { IsPlayer: false } c) continue;
                FlagFrom(world, c, m, target, tc, MemoryTag.TempEnemy);
                Aggro?.Alarm(world, m, target);
                world.Defer(new DialogueEvent(m, target, 66) { Tick = world.Tick + 1 });
                if (c.Fighter?.CanAttack == true) fighters.Add(m);
            }
        }
        if (fighters.Count > 0) world.Defer(new Combat.AttackOrder(fighters, target) { Tick = world.Tick + 1, Source = TaskSource.Npc });
    }

    /// <summary>DA_OPEN_NEAREST_GATE (54): the wall gate of the speaker's town nearest it starts opening (<c>FUN_140297040</c>, which tests no lock).</summary>
    void OpenNearestGate(World world, CharacterId me)
    {
        if (Position(world, me) is not { } at || TownAt(at) is not (>= 0 and var town)) return;
        Meitou.Data.World.TownBuilding? gate = null;
        float best = float.MaxValue;
        foreach (var b in data.Sites[town].Buildings)
        {
            float d = Vector2.DistanceSquared(new(b.Position.X, b.Position.Z), new(at.X, at.Z));
            if (b.IsGate && d < best) (gate, best) = (b, d);
        }
        if (gate is null) return;
        foreach (var door in world.Fixtures.OfBuilding(gate.Key)) Locks.FixtureRegistry.OpenDoor(door);
    }

    /// <summary>DA_FOLLOW_WHILE_TALKING (16, task 164): the speaker walks after the target while its conversation lasts (engine: re-aimed every 15 ticks when more than 40 units off).</summary>
    void StepFollowers(World world)
    {
        if (followers.Count == 0) return;
        var table = world.Characters;
        foreach (var (who, whom) in followers.ToArray())
        {
            bool talking = bubbles.Any(b => b.C.Npc == who) || open?.Npc == who;
            if (!talking || !table.TryResolveNext(who, out int s) || !Awake(table, s) || Position(world, whom) is not { } at) { followers.Remove(who); continue; }
            if (world.Tick % 15 != 0 || Vector3.DistanceSquared(table.Next[s].Position, at) <= 40 * 40 || table.Cold(s)!.InCombat) continue;   // no walk to end a fight it is in
            TaskStart.Walk(world, s, new Vector2(at.X, at.Z), SpeedMode.Walk, TaskType.FOLLOW_WHILE_TALKING, TaskSource.Npc);
        }
    }

    /// <summary>A town stalker's state (<c>Task_TownStalker</c>, 165): its victim, the waypoint past the town gate once picked, and whether it has harassed / found no gate.</summary>
    sealed class Stalk(CharacterId target)
    {
        public CharacterId Target { get; } = target;
        public Vector2? Waypoint { get; set; }
        public bool Harassed { get; set; }
        public bool NoGate { get; set; }
    }

    readonly SortedDictionary<CharacterId, Stalk> stalkers = new(Comparer<CharacterId>.Create(CompareIds));

    /// <summary>EV_HARASSMENT, what a stalker opens on its victim outside town; how far past the gate it waits (<c>Task_TownStalker</c>, <b>Verified</b> constant).</summary>
    const int Harassment = 26;
    const float StalkPastGate = 300;

    /// <summary>
    /// DA_THUG_HUNTER (17, order 165 <c>Task_TownStalker</c>, docs/game/dialogue.md): in a town, the stalker waits <see cref="StalkPastGate"/> outside the town's first gate (on the line
    /// from the town centre through it), facing its victim, while the victim is no farther from the centre than that point; with no gate it just stays. Once the victim is farther
    /// out, or anywhere outside a town, it follows it and, once, when it is not talking, sends it event 26. It is done when, after that, neither is talking. Engine choices: re-aimed
    /// every 15 ticks; the first gate is the town's first gate door in fixture order; the gate-code test on the gate's outside is not made.
    /// </summary>
    void StepStalkers(World world)
    {
        if (stalkers.Count == 0) return;
        var table = world.Characters;
        foreach (var (who, stalk) in stalkers.ToArray())
        {
            if (!table.TryResolveNext(who, out int s) || !Awake(table, s) || !table.TryResolveNext(stalk.Target, out int t) || !Awake(table, t)) { stalkers.Remove(who); Release(world, who); continue; }
            bool talking = bubbles.Any(b => b.C.Npc == who || b.C.Player == who) || open?.Npc == who || open?.Player == who;
            bool victimTalking = bubbles.Any(b => b.C.Npc == stalk.Target || b.C.Player == stalk.Target) || open?.Npc == stalk.Target || open?.Player == stalk.Target;
            if (stalk.Harassed && !talking && !victimTalking) { stalkers.Remove(who); Release(world, who); continue; }
            table.Cold(s)!.Engaged = true;   // the order holds it, not the AI
            if (table.Cold(s)!.InCombat) continue;   // its walks would end the fight it is in (a new task drops the fight task)
            ref var sn = ref table.Next[s];
            var victim = new Vector2(table.Next[t].Position.X, table.Next[t].Position.Z);
            int site = Ai.AiSystem.TownAt(data, world, sn.Position).Site;
            if (site >= 0 && !stalk.NoGate)
            {
                var centre = new Vector2(data.Sites[site].Position.X, data.Sites[site].Position.Z);
                if (stalk.Waypoint is null)
                {
                    var gate = world.Fixtures.All.FirstOrDefault(f => f.Kind == Locks.FixtureKind.Door && f.IsGate && f.Site == site);
                    if (gate is null) { stalk.NoGate = true; continue; }
                    var g = new Vector2(gate.Position.X, gate.Position.Z);
                    var outward = g - centre;
                    stalk.Waypoint = g + (outward.LengthSquared() > 1e-6f ? Vector2.Normalize(outward) : Vector2.UnitX) * StalkPastGate;
                }
                var w = stalk.Waypoint.Value;
                if (Vector2.Distance(victim, centre) <= Vector2.Distance(w, centre))
                {
                    if (world.Tick % 15 == 0 && Vector2.Distance(new Vector2(sn.Position.X, sn.Position.Z), w) > TalkReach && (sn.Task != (byte)CharacterTask.GoTo || Vector2.Distance(sn.Goal, w) > TalkReach))
                        TaskStart.Walk(world, s, w, SpeedMode.Walk, TaskType.TOWN_STALKER, TaskSource.Npc);
                    else if (sn.Task != (byte)CharacterTask.GoTo)
                    {
                        var to = table.Next[t].Position - sn.Position;
                        if (to.X * to.X + to.Z * to.Z > 1e-4f) sn.Yaw = MathF.Atan2(to.X, to.Z);
                    }
                    continue;
                }
            }
            else if (site >= 0) continue;   // no gate: it only stays
            // Outside: follow and harass.
            if (!stalk.Harassed && !talking)
            {
                world.Defer(new DialogueEvent(who, stalk.Target, Harassment) { Tick = world.Tick + 1 });
                stalk.Harassed = true;
            }
            if (world.Tick % 15 == 0 && Vector2.Distance(new Vector2(sn.Position.X, sn.Position.Z), victim) > TalkReach)
                TaskStart.Walk(world, s, victim, SpeedMode.Walk, TaskType.TOWN_STALKER, TaskSource.Npc);
        }
    }

    void HashActs(ref StateHasher hasher)
    {
        foreach (var (who, whom) in followers) { hasher.Add(who); hasher.Add(whom); }
        foreach (var (who, stalk) in stalkers) { hasher.Add(who); hasher.Add(stalk.Target); hasher.Add(stalk.Harassed); hasher.Add(stalk.NoGate); if (stalk.Waypoint is { } w) hasher.Add(w); }
    }
}
