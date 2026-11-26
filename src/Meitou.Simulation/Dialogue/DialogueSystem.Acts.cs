using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// The dialogue system's regard and memory tags, and the orders and acts its lines give.
/// </summary>
public sealed partial class DialogueSystem
{
    #region Regard and memory tags

    /// <summary>
    /// The short-term tag a character's squad sets on another (<c>FUN_1406744a0</c>): TEMP_ENEMY is not set on a character of an allied faction or
    /// one the speaker regards as friendly.
    /// </summary>
    void FlagFrom(World world, CharacterCold mc, CharacterId me, CharacterId who, CharacterCold wc, int tag)
    {
        if (tag == MemoryTag.TempEnemy
            && (_data.Relations.Contains(mc.Faction) && _data.Relations.Contains(wc.Faction) && _data.Relations.IsAlly(mc.Faction, wc.Faction)
            || RegardsFriendly(world, me, mc, who, wc))) return;
        Memory.Flag(world, mc.SquadId, who, tag);
    }

    /// <summary>
    /// <paramref name="by"/>'s squad tags <paramref name="who"/> TEMP_ENEMY, so every mate takes it for an enemy whatever the relations say
    /// (docs/game/dialogue.md "Squad memory"): an attacked character on its attacker (<c>AI::underMeleeAttack</c> / <c>underRangedAttack</c>,
    /// <c>Character::hitByMeleeAttack</c>, <c>iShotYou</c>), and a character that <c>Character::attackTarget</c> sends at someone on its target.
    /// </summary>
    internal void TagEnemy(World world, int by, int who)
    {
        var table = world.Characters;
        if (table.Cold(by) is not { SquadId: >= 0 } bc || table.Cold(who) is not { } wc) return;
        FlagFrom(world, bc, table.IdOf(by), table.IdOf(who), wc, MemoryTag.TempEnemy);
    }

    /// <summary>
    /// "A regards B as friendly" (Character slot 126, <c>FUN_1407923d0</c>): the same faction; never when A's squad holds TEMP_ENEMY on B (or, for a
    /// player-faction A, B's squad on A); always when B's squad holds TEMP_ALLY on a player-faction A, A's squad TEMP_ALLY on B, or A loves B (DC
    /// 52); else A's faction is allied to B's, with <paramref name="disguise"/> the faction B's working disguise passes for (the third argument,
    /// <c>FUN_1407c0520</c>, <see cref="Crime.ArrestSystem.SeenFaction"/>; <b>Verified</b>). The same-faction test reads the real factions.
    /// </summary>
    internal bool RegardsFriendly(World world, CharacterId a, CharacterCold ac, CharacterId b, CharacterCold bc, bool disguise = false)
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

        int seen = disguise ? Crime.ArrestSystem.SeenFaction(bc) : bc.Faction;
        return _data.Relations.Contains(ac.Faction) && _data.Relations.Contains(seen) && _data.Relations.IsAlly(ac.Faction, seen);
    }

    /// <summary>
    /// A character put <paramref name="captive"/> in a cage (<c>Task_PutInSomething</c> for the cage, <c>FUN_140358670</c>, <b>Observed</b>): the
    /// captive's squad remembers the captor with the long-term tag 5 MY_CAPTOR. Wired to <see cref="FactionDeeds.CagedHook"/>. Engine choice, no
    /// counterpart in the original: the captive's squad drops its TEMP_ENEMY tags on the captor's faction too. With the captor's side alone
    /// cleared (the original's rule), a released player character on Hold or with the Attacking enemies job went for its jailers on sight for up
    /// to 7 hours, an Assault that jailed it again (#599).
    /// </summary>
    public void Captured(World world, int captor, int captive)
    {
        var table = world.Characters;
        if (table.Cold(captive) is { SquadId: >= 0 } cc && table.Cold(captor) is { } oc)
        {
            Memory.Remember(world, cc.SquadId, table.IdOf(captor), MemoryTag.Captor);
            Memory.ClearShortOfFaction(world, cc.SquadId, oc.Faction, MemoryTag.TempEnemy);
        }

        Forgive(world, captor, table.IdOf(captive));
    }

    /// <summary>
    /// <c>FUN_14067aca0</c> over <c>FUN_140289400</c> (<b>Verified</b>): the short-term tags TEMP_ENEMY, criminal and aggressor on
    /// <paramref name="target"/> are cleared in the memory of the observer's squad, of the squads of the allies it senses (stand-in: allied
    /// characters within sight) and of every squad of its faction. Run by the end of processing a slave (observer: the worker), by
    /// <c>Task_PutInSomething</c> for a cage (<c>FUN_140358670</c>: the putter forgives the captive) and by <c>Task_ReleasePrisoner</c>
    /// (<c>FUN_1403570a0</c>: the prisoner forgives the releaser, and when the releaser's faction books bounties the releaser forgives the prisoner).
    /// The original also clears the ENEMY bit of the target's entry in the observer's sensor (the scan recomputes it here) and empties a list of its
    /// town (purpose <b>Unknown</b>); neither is modelled.
    /// </summary>
    public void Forgive(World world, int observer, CharacterId target)
    {
        var table = world.Characters;
        if (table.Cold(observer) is not { } oc) return;
        var squads = new SortedSet<int>();
        if (oc.SquadId >= 0) squads.Add(oc.SquadId);
        foreach (var squad in world.Squads.All) if (squad.Faction == oc.Faction) squads.Add(squad.Id);
        var at = table.Next[observer].Position;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!table.Next[s].Alive || table.Cold(s) is not { SquadId: >= 0 } a || !AlliedFactions(oc.Faction, a.Faction)) continue;
            if (Vector3.DistanceSquared(at, table.Next[s].Position) <= AggroSystem.OutdoorSight * AggroSystem.OutdoorSight) squads.Add(a.SquadId);
        }
        foreach (int squad in squads)
        {
            Memory.ClearShort(squad, target, MemoryTag.TempEnemy);
            Memory.ClearShort(squad, target, MemoryTag.Criminal);
            Memory.ClearShort(squad, target, MemoryTag.Aggressor);
        }
    }

    /// <summary>
    /// Forgives <paramref name="target"/> across a booking faction (a paid fine in <see cref="ClearBounties"/> or an expired bounty in
    /// <see cref="Crime.BountyBook.Expire"/>). Engine choice, no counterpart known in the original: every squad of <paramref name="bookingFaction"/>
    /// or of a faction sharing its <c>legal system</c> drops its short-term TEMP_ENEMY, criminal and aggressor tags on <paramref name="target"/>, and
    /// <paramref name="target"/>'s squad drops its TEMP_ENEMY tags on those factions (as <see cref="Captured"/> does for #599). Otherwise a guard tag
    /// from an arrest attempt can outlive the bounty and start a fight whose police blows book Assault (<see cref="Crime.CrimeSystem.Hit"/>).
    /// </summary>
    internal void ForgiveBookingFaction(World world, int bookingFaction, CharacterId target)
    {
        if (bookingFaction < 0) return;

        int targetSquad = world.Characters.TryResolveNext(target, out int slot) && world.Characters.Cold(slot) is { SquadId: >= 0 } tc
            ? tc.SquadId : -1;
        HashSet<int>? cleared = targetSquad >= 0 ? [] : null;

        foreach (var squad in world.Squads.All)
        {
            if (!InLawSystem(squad.Faction, bookingFaction)) continue;

            Memory.ClearShort(squad.Id, target, MemoryTag.TempEnemy);
            Memory.ClearShort(squad.Id, target, MemoryTag.Criminal);
            Memory.ClearShort(squad.Id, target, MemoryTag.Aggressor);
            if (cleared is not null && cleared.Add(squad.Faction))
                Memory.ClearShortOfFaction(world, targetSquad, squad.Faction, MemoryTag.TempEnemy);
        }

        if (cleared is not null && cleared.Add(bookingFaction))
            Memory.ClearShortOfFaction(world, targetSquad, bookingFaction, MemoryTag.TempEnemy);
    }

    /// <summary>
    /// Whether <paramref name="faction"/> is <paramref name="bookingFaction"/> or books its bounties under it
    /// (<see cref="Crime.CrimeFactions.BookingKey"/>).
    /// </summary>
    bool InLawSystem(int faction, int bookingFaction) =>
        faction == bookingFaction || Crime.CrimeFactions.BookingKey(_data.Relations, faction) == bookingFaction;

    /// <summary>
    /// Whether factions <paramref name="a"/> and <paramref name="b"/> are the same or allied (false for a missing one).
    /// </summary>
    bool AlliedFactions(int a, int b) =>
        a >= 0 && b >= 0 && (a == b || _data.Relations.Contains(a) && _data.Relations.Contains(b) && _data.Relations.IsAlly(a, b));

    /// <summary>
    /// Event 13 UNLOCK_MY_CAGE_OR_SHACKLES fired by a freed prisoner at its liberator (<c>FUN_140684990</c>, <b>Observed</b>): unless the speaker's
    /// squad already remembers the liberator as its captor, it remembers it with the long-term tag 3 FREED_ME and the short-term TEMP_ALLY. The
    /// original does this before choosing a line, whether or not one is said; only a speaker that is awake gets that far (a dead or unconscious one
    /// only for the events of a mask that excludes 13).
    /// </summary>
    void Freed(World world, CharacterId speaker, CharacterId liberator)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(speaker, out int s) || !Awake(table, s) || table.Cold(s) is not { SquadId: >= 0 } sc) return;
        if (Memory.Find(world, sc.SquadId, liberator) is { } known && (known.Long >> MemoryTag.Captor & 1) != 0) return;
        Memory.Remember(world, sc.SquadId, liberator, MemoryTag.FreedMe);
        Memory.Flag(world, sc.SquadId, liberator, MemoryTag.TempAlly);
    }

    /// <summary>
    /// The medic finders' slot 126 (<c>isAlly</c>, <see cref="RegardsFriendly(World, CharacterId, CharacterCold, CharacterId, CharacterCold, bool)"/>)
    /// for two characters by slot; <paramref name="disguise"/> reads B's working disguise (the sight pass's friend test).
    /// </summary>
    public bool RegardsFriendly(World world, int a, int b, bool disguise = false)
    {
        var table = world.Characters;
        return table.Cold(a) is { } ac && table.Cold(b) is { } bc && RegardsFriendly(world, table.IdOf(a), ac, table.IdOf(b), bc, disguise);
    }

    /// <summary>
    /// The signature before <c>disguise</c>, kept for code mods built against it (docs/modding.md "API compatibility").
    /// </summary>
    public bool RegardsFriendly(World world, int a, int b) => RegardsFriendly(world, a, b, false);

    /// <summary>
    /// The loot finder's <c>FUN_14059a7b0</c> (DC 54, <see cref="ScrewOver(World, CharacterId, CharacterCold, CharacterId, CharacterCold)"/>) for two
    /// characters by slot.
    /// </summary>
    public bool ScrewOver(World world, int a, int b)
    {
        var table = world.Characters;
        return table.Cold(a) is { } ac && table.Cold(b) is { } bc && ScrewOver(world, table.IdOf(a), ac, table.IdOf(b), bc);
    }

    /// <summary>
    /// The medic finders' <c>FUN_14059a670</c> (DC 55, <see cref="Helps"/>) for two characters by slot.
    /// </summary>
    public bool Helps(World world, int a, int b)
    {
        var table = world.Characters;
        return table.Cold(a) is { } ac && table.Cold(b) is { } bc && Helps(world, table.IdOf(a), ac, table.IdOf(b), bc);
    }

    /// <summary>
    /// What the squad memory says to slot 125 (<c>isEnemy</c>, <c>FUN_14079c950</c>): 1 when a's squad holds TEMP_ENEMY on b (or, for a
    /// player-faction a, b's squad on a), -1 when it holds TEMP_ALLY on b, else 0 (the faction relation decides). Another faction's squads only; the
    /// same faction is never an enemy.
    /// </summary>
    public int EnemyMemory(World world, int a, int b)
    {
        var table = world.Characters;
        if (table.Cold(a) is not { } ac || table.Cold(b) is not { } bc || ac.Faction == bc.Faction) return 0;
        var (ida, idb) = (table.IdOf(a), table.IdOf(b));
        var (ab, _) = Memory.Read(world, ac.SquadId, idb);
        if (Has(ab, MemoryTag.TempEnemy)
            || (ac.IsPlayer || ac.Faction == world.Player.Faction) && Has(Memory.Read(world, bc.SquadId, ida).Short, MemoryTag.TempEnemy))
        {
            return 1;
        }
        return Has(ab, MemoryTag.TempAlly) ? -1 : 0;
    }

    /// <summary>
    /// Whether bit <paramref name="tag"/> is set in <paramref name="tags"/>.
    /// </summary>
    static bool Has(uint tags, int tag) => (tags >> tag & 1) != 0;

    /// <summary>
    /// DC 52 I_LOVE_THIS_GUY (<c>FUN_140599590</c>): the squad's long-term tags on the character hold LIFESAVER or FREED_ME.
    /// </summary>
    internal bool Loves(World world, CharacterCold mc, CharacterId who) => (Memory.Find(world, mc.SquadId, who)?.Long & MemoryTag.LoveMask) is > 0;

    /// <summary>
    /// DC 53 I_HATE_THIS_GUY (<c>FUN_1405995d0</c>): STOLE_FROM_ME, MY_CAPTOR, DEFEATED_MY_SQUAD_ONCE or KILLED_MY_FRIEND.
    /// </summary>
    bool Hates(World world, CharacterCold mc, CharacterId who) => (Memory.Find(world, mc.SquadId, who)?.Long & MemoryTag.HateMask) is > 0;

    /// <summary>
    /// DC 54 I_SHOULD_SCREW_THIS_GUY_OVER (<c>FUN_14059a7b0</c>), each rule overriding the ones before: either is a bandit; not when both have the
    /// same NPC class; yes for a traitorous speaker; not when it regards S as friendly; not for an honourable speaker; yes when S's faction owns the
    /// speaker as a slave; not when it loves S; yes when it hates S; yes when S is dead.
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
    /// DC 55 I_SHOULD_HELP_THIS_GUY (<c>FUN_14059a670</c>), each rule overriding the ones before: the same NPC class; not for a traitorous or bandit
    /// speaker; yes when it regards S as friendly; yes when both are honourable; yes when it loves S; not when it hates S.
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

    #endregion

    #region Orders and acts

    /// <summary>
    /// The original's <c>Task_Runaway</c> first run (task 280, DA 34): 2000 + 400 x random units away from the threat (as
    /// <see cref="FleeSystem.FirstRun"/>).
    /// </summary>
    const float RunAwayDistance = FleeSystem.FirstRun, RunAwaySpread = FleeSystem.RunSpread;

    /// <summary>
    /// Gives <paramref name="who"/> the dialogue's one-shot order <paramref name="row"/> on <paramref name="target"/> (<c>FUN_1405d20d0</c>, permanent
    /// 0, the clear flag, docs/game/dialogue.md "Effects"); nothing when no system owns the row. No faction, distance or state test: the queue's gate
    /// (<see cref="DecisionSystem.TakesOrders"/>) and the row's check decide when it starts.
    /// </summary>
    static void Order(World world, CharacterId who, TaskType row, CharacterId target, Vector3 point = default, string? site = null)
    {
        if (!world.Characters.TryResolveNext(who, out int s) || world.OrderOwnerOf(row) is not { } owner) return;

        PlayerOrders.Give(world, s, new OrderEntry(row, owner) { Target = target, Point = point, Site = site }, queued: false);
    }

    /// <summary>
    /// The stop the attack, go-home and run-away acts run on the speaker before their order (<c>FUN_1405c8330</c>, <see cref="Bodies.BodySystem.Stop"/>):
    /// its task ends, its queue stays with the front to be planned again.
    /// </summary>
    static void StopSpeaker(World world, CharacterId me)
    {
        if (world.Characters.TryResolveNext(me, out int s)) Bodies.BodySystem.Stop(world, s);
    }

    /// <summary>
    /// DA_RUN_AWAY (34): the speaker's goals are reset and it is stopped (<see cref="StopSpeaker"/>), then it gets order 280 from the target (clear),
    /// run as <c>Task_Runaway</c>'s first run to a point drawn here (engine: one run at full speed), and its squad tags the target AGGRESSOR.
    /// </summary>
    void RunAway(World world, CharacterId me, CharacterCold mc, CharacterId target, CharacterCold tc)
    {
        var table = world.Characters;
        StopSpeaker(world, me);
        if (table.TryResolveNext(me, out int s) && Position(world, target) is { } from)
        {
            var here = table.Next[s].Position;
            var away = new Vector2(here.X - from.X, here.Z - from.Z);
            away = away.LengthSquared() > 1e-6f ? Vector2.Normalize(away) : Vector2.UnitX;
            float r = Rng.Float(NextRoll(world, me));
            var to = new Vector2(from.X, from.Z) + away * (RunAwayDistance + RunAwaySpread * r);
            Order(world, me, TaskType.RunAwayForced, target, new Vector3(to.X, from.Y, to.Y));
        }

        FlagFrom(world, mc, me, target, tc, MemoryTag.Aggressor);
    }

    /// <summary>
    /// DA_GO_HOME (7): the speaker is stopped (<see cref="StopSpeaker"/>), then gets order 19 with the target (clear; walked as
    /// <see cref="Ai.EngineOrders"/> does) and its squad tags the target AGGRESSOR.
    /// </summary>
    void GoHome(World world, CharacterId me, CharacterCold mc, CharacterId target, CharacterCold? tc)
    {
        StopSpeaker(world, me);
        Order(world, me, TaskType.GoHomebuilding, target);
        if (tc is not null) FlagFrom(world, mc, me, target, tc, MemoryTag.Aggressor);
    }

    /// <summary>
    /// DA_SUMMON_MY_SQUAD (52): every other member of the speaker's squad gets order 67 on the speaker (clear; no target, faction, distance or state
    /// test).
    /// </summary>
    void Summon(World world, CharacterId me)
    {
        foreach (var m in SquadOf(world, me))
            if (m != me) Order(world, m, TaskType.MoveOnFreeWillFast, me);
    }

    /// <summary>
    /// DA_CRIME_ALARM's order (33, after the town alarm, <c>FUN_140680560</c>, <b>Verified</b>): a speaker of a town whose body ran no task at its
    /// last update (<see cref="NoTask"/>) gets order 67 on the target with the building the target is in (clear).
    /// </summary>
    void AlarmRun(World world, CharacterId me, CharacterCold mc, int alarmer, int alarmed, CharacterId target)
    {
        if (world.Squads.Find(mc.SquadId) is not { Town: >= 0 } || !NoTask(world.Characters, alarmer)) return;

        Order(world, me, TaskType.MoveOnFreeWillFast, target, site: world.Sight.BuildingOf(alarmed));
    }

    /// <summary>
    /// The body's no-task byte (CharBody <c>+0x70</c>, <b>Verified</b>: written only by its per-frame update, slot 1 <c>FUN_1405c6d20</c> from
    /// Character slot 27 <c>FUN_1405c7c30</c>, 1 when no task is installed at <c>+0x68</c>, 0 when one is, left alone while ragdolled or carried):
    /// Meitou, the character stands idle on no start's task.
    /// </summary>
    static bool NoTask(CharacterTable table, int slot) =>
        table.Next[slot].Task == (byte)CharacterTask.Idle && table.Cold(slot)!.Running?.Runs(CharacterTask.Idle) != true;

    /// <summary>
    /// DA_SEPARATE_TO_MY_OWN_SQUAD (28, <c>FUN_1405ce4e0</c>): the speaker (the target when the speaker is of the player's faction) leaves for a new
    /// squad of its faction made where it stands, which takes a copy of the old squad's memory; nothing when it is alone already. The split first
    /// destroys its perma-jobs, so a slave stops obeying its master (job 187, <see cref="Kidnap.ObedientSlave.Disobey"/>): a freed prisoner's "I'm
    /// free..." line leaves it to its own lists. The new squad is made from the old one's template (<c>FUN_1407f4420</c>, <b>Verified</b>). Engine: the
    /// new NPC squad keeps the old one's home radius, no town, and the AI makes it a fresh mind.
    /// </summary>
    internal void Separate(World world, CharacterId who)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(who, out int s) || table.Cold(s) is not { SquadId: >= 0 } c || world.Squads.Find(c.SquadId) is not { } old
            || old.Members.Count <= 1)
        {
            return;
        }
        Kidnap.ObedientSlave.Disobey(world, c);
        var at = table.Next[s].Position;
        Squad squad;
        if (c.IsPlayer)
        {
            squad = SquadManagement.NewSquad(world, _data.Db, at,
                _data.Relations.Contains(c.Faction) && _data.Factions[c.Faction].Name is { Length: > 0 } f ? f : "Nameless");
        }
        else
        {
            squad = new Squad { Id = world.Squads.NextId(), TemplateId = old.TemplateId, Faction = old.Faction, Position = at,
                HomeCentre = new(at.X, at.Z), HomeRadius = old.HomeRadius };
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
    /// DA_MASSIVE_ALARM (56): when the speaker has a status town, every member of every squad of its faction, the speaker included, tags the target
    /// TEMP_ENEMY and has its hearing of the target alarmed for 5 s (<c>FUN_1405cb0a0</c> over <c>FUN_140858f40</c>), fires event 66 at it and gets
    /// order 29 to the target's position, added behind its queued orders (clear 0, <b>Verified</b>). No distance, state or faction-of-member test.
    /// Engine: only loaded squads.
    /// </summary>
    void MassiveAlarm(World world, CharacterId me, CharacterCold mc, CharacterId target, CharacterCold tc)
    {
        if (mc.StatusTown.IsNone || Position(world, target) is not { } at || world.OrderOwnerOf(TaskType.MoveCusOrdered) is not { } movement) return;

        var table = world.Characters;
        foreach (var squad in world.Squads.All.Where(q => q.Faction == mc.Faction).ToList())
        {
            foreach (var m in squad.Members.ToList())
            {
                if (!table.TryResolveNext(m, out int s) || !table.Next[s].Alive || table.Cold(s) is not { } c) continue;

                FlagFrom(world, c, m, target, tc, MemoryTag.TempEnemy);
                Aggro?.Alarm(world, m, target);
                world.Defer(new DialogueEvent(m, target, 66) { Tick = world.Tick + 1 });
                var move = new OrderEntry(TaskType.MoveCusOrdered, movement) { Target = target, Point = at, TaskPoint = at };
                PlayerOrders.Give(world, s, move, queued: true);
            }
        }
    }

    /// <summary>
    /// DA_OPEN_NEAREST_GATE (54): the wall gate of the speaker's town nearest it starts opening (<c>FUN_140297040</c>, which tests no lock).
    /// </summary>
    void OpenNearestGate(World world, CharacterId me)
    {
        if (Position(world, me) is not { } at || TownAt(at) is not (>= 0 and var town)) return;
        Meitou.Data.World.TownBuilding? gate = null;
        float best = float.MaxValue;
        foreach (var b in _data.Sites[town].Buildings)
        {
            float d = Vector2.DistanceSquared(new(b.Position.X, b.Position.Z), new(at.X, at.Z));
            if (b.IsGate && d < best) (gate, best) = (b, d);
        }
        if (gate is null) return;
        foreach (var door in world.Fixtures.OfBuilding(gate.Key)) Locks.FixtureRegistry.OpenDoor(door);
    }

    #endregion
}
