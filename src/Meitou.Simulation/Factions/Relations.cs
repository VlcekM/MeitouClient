using Meitou.Data.Gameplay;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Factions;

/// <summary>
/// Relation changes with the game's messages (docs/game/factions-squads-towns.md sections 3.3 to 3.5). Serial phases only: the table is shared world
/// state.
/// </summary>
public static class PlayerRelations
{
    /// <summary>
    /// Applies an event of <paramref name="kind"/> to <paramref name="faction"/>'s view of the player (the player's faction has no table, so a
    /// deed against a faction changes that faction's entry about the player) and posts the messages of a threshold it crosses. Does nothing
    /// without a player faction, for the player's own faction or for one outside the table.
    /// </summary>
    public static void Event(World world, FactionRelations relations, int faction, RelationEventKind kind, float x = 1)
    {
        int player = relations.PlayerFaction;
        if (player < 0 || faction == player || !relations.Contains(faction)) return;
        Event(world, relations, faction, player, kind, x, CharacterId.None, CharacterId.None);
    }

    /// <summary>
    /// Slot 5 of <paramref name="a"/>'s table about <paramref name="b"/> (<see cref="FactionRelations.Event"/>); when <paramref name="b"/> is the
    /// player's faction the threshold message of the change is posted, as the game's slot does (<c>FUN_1406b2d20</c> tests the other faction's player
    /// flag). <paramref name="doer"/> and <paramref name="victim"/> are passed on to the observers.
    /// </summary>
    public static void Event(
        World world, FactionRelations relations, int a, int b, RelationEventKind kind, float x, CharacterId doer, CharacterId victim)
    {
        var (before, after) = relations.Event(a, b, kind, x);
        if (before == after) return;

        Posted(world, relations, a, b, before, after);
        Observed(world, relations, a, b, before, after, new(RelationCauseType.Deed, kind, doer, victim));

        // A blow or a bandage changes the relation a little each time: only a crossing is logged, its text made only then.
        bool always = kind is not (RelationEventKind.Hit or RelationEventKind.FirstAid);
        if (!always && Crossing(before, after).Length == 0) return;

        Logged(world, relations, a, b, before, after, Describe(world, new(RelationCauseType.Deed, kind, doer, victim)));
    }

    /// <summary>
    /// Slot 4 (<see cref="FactionRelations.Add"/>) with the threshold message when the player's faction is one side; the observers get
    /// <paramref name="cause"/>.
    /// </summary>
    public static void Add(World world, FactionRelations relations, int a, int b, float delta, RelationCause cause, string? log = null)
    {
        var (before, after) = relations.Add(a, b, delta);
        if (before == after) return;

        Posted(world, relations, a, b, before, after);
        Observed(world, relations, a, b, before, after, cause);
        Logged(world, relations, a, b, before, after, log ?? Describe(world, cause));
    }

    /// <summary>
    /// Faction <paramref name="a"/> declares war on <paramref name="b"/> (<see cref="FactionRelations.DeclareWar"/>, the dialogue's DECLARE_WAR):
    /// "War breaks out between {1} and {2}" when the war flag was clear, and the threshold message when <paramref name="b"/> is the player's faction.
    /// Nothing when <paramref name="a"/> is already hostile. The observers see a change of the relation or of the war flag.
    /// </summary>
    public static void DeclareWar(World world, FactionRelations relations, int a, int b, CharacterId speaker)
    {
        var (before, after, started) = relations.DeclareWar(a, b);
        if (started) world.Post(Loc.Tr("War breaks out between {1} and {2}", relations.Factions[a].Name, relations.Factions[b].Name));
        if (before != after) Posted(world, relations, a, b, before, after);
        if (started || before != after) Observed(world, relations, a, b, before, after, new(RelationCauseType.WarDeclared, speaker));
        if (before != after) Logged(world, relations, a, b, before, after, "war declared (dialogue)");
    }

    /// <summary>
    /// Faction <paramref name="a"/> ends its war with <paramref name="b"/> (<see cref="FactionRelations.EndWar"/>, the dialogue's END_WAR), with the
    /// threshold message as above. The observers see a change of the relation or of the war flag.
    /// </summary>
    public static void EndWar(World world, FactionRelations relations, int a, int b, CharacterId speaker)
    {
        bool atWar = relations.AtWar(a, b);
        var (before, after) = relations.EndWar(a, b);
        if (before != after) Posted(world, relations, a, b, before, after);
        if (atWar || before != after) Observed(world, relations, a, b, before, after, new(RelationCauseType.WarEnded, speaker));
        if (before != after) Logged(world, relations, a, b, before, after, "war ended (dialogue)");
    }

    /// <summary>
    /// The event log's line of a relation change (<see cref="EventLog"/>): faction <paramref name="a"/>'s relation with <paramref name="b"/>, before
    /// and after, with the cause (a blow's or a bandage's is logged only when it crosses the hostile or the ally line, <see cref="Event"/>).
    /// </summary>
    public static void Logged(World world, FactionRelations relations, int a, int b, float before, float after, string cause)
    {
        string crossing = Crossing(before, after);
        string text = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{relations.Factions[a].Name} towards {relations.Factions[b].Name}: {before:0.##} -> {after:0.##}{crossing}; {cause}");
        world.Events.Line(world, "relation", text);
    }

    /// <summary>
    /// The event log's cause text of <paramref name="cause"/>: a deed's kind with its victim and doer when both are known.
    /// </summary>
    static string Describe(World world, RelationCause cause) => cause.Type switch
    {
        RelationCauseType.Deed when !cause.Actor.IsNone && !cause.Target.IsNone =>
            $"{cause.Deed} of {EventLog.Who(world, cause.Target.Slot)} by {EventLog.Who(world, cause.Actor.Slot)}",
        RelationCauseType.Deed => cause.Deed.ToString(),
        RelationCauseType.Crime => "a friend's crime witnessed",
        RelationCauseType.Trade => "a character sold or bought free",
        RelationCauseType.Dialogue => "a dialogue line",
        RelationCauseType.WarDeclared => "war declared (dialogue)",
        RelationCauseType.WarEnded => "war ended (dialogue)",
        _ => "a change",
    };

    /// <summary>
    /// " (now hostile)" and the like when the change crosses the hostile or the ally line; empty otherwise.
    /// </summary>
    static string Crossing(float before, float after)
    {
        const float hostile = FactionRelations.HostileThreshold, ally = FactionRelations.AllyThreshold;
        if (before > hostile && after <= hostile) return " (now hostile)";
        if (before <= hostile && after > hostile) return " (no longer hostile)";
        if (before <= ally && after > ally) return " (now allied)";
        if (before > ally && after <= ally) return " (no longer allied)";
        return "";
    }

    /// <summary>
    /// Posts the threshold message of a relation change between the player's faction and another (<see cref="Notice"/>).
    /// </summary>
    static void Posted(World world, FactionRelations relations, int a, int b, float before, float after)
    {
        // The player's row is kept in the other faction's table: either way round the other faction names the message.
        int other = a == relations.PlayerFaction ? b : b == relations.PlayerFaction ? a : -1;
        if (other >= 0 && Notice(relations.Factions[other].Name, before, after) is { } text) world.Post(text);
    }

    /// <summary>
    /// Passes a change to the world's observers (<see cref="IRelationChangeObserver"/>), the player's row as the other faction's view of the player.
    /// </summary>
    static void Observed(World world, FactionRelations relations, int a, int b, float before, float after, RelationCause cause)
    {
        if (a == relations.PlayerFaction && a >= 0) (a, b) = (b, a);
        world.RelationObservers.Observe(world, new RelationChange(world.Tick, a, b, before, after, cause));
    }

    /// <summary>
    /// The message of a change of a faction's relation to the player (<c>FUN_1406b28f0</c>, <b>Verified</b> thresholds): hostile at -30 or below,
    /// allies above 50 (the message's own test; <c>ally()</c> accepts 50). Null when no threshold is crossed.
    /// </summary>
    public static string? Notice(string factionName, float before, float after)
    {
        const float hostile = FactionRelations.HostileThreshold, ally = FactionRelations.AllyThreshold;
        if (before > hostile && after <= hostile) return Loc.Tr("{1} are now hostile towards you", factionName);
        if (before <= hostile && after > hostile) return Loc.Tr("{1} are no longer hostile towards you", factionName);
        if (before <= ally && after > ally) return Loc.Tr("{1} are now your allies", factionName);
        if (before > ally && after <= ally) return Loc.Tr("{1} are no longer your ally", factionName);
        return null;
    }
}

/// <summary>
/// The deeds that move faction relations (docs/game/factions-squads-towns.md section 3.5, <b>Observed</b>): every one is an event of the victim's
/// faction's table about the doer's faction, of the victim's <see cref="Weight"/>, for any two factions (the player's or not). Shared by the systems
/// that see the deeds (combat, first aid, the cagings); serial phases only.
/// </summary>
public sealed class FactionDeeds(FactionRelations relations, PopulationData? data = null)
{
    /// <summary>
    /// The faction relations the deeds move.
    /// </summary>
    public FactionRelations Relations { get; } = relations;

    /// <summary>
    /// The striker's rating the blows' events test against 0.1 and 0.15 (<c>FUN_140999f00</c>, <c>FUN_1404394f0</c>, <b>Verified</b>): its status
    /// float <c>+0xac</c>, the intended aggression of its current goal (<see cref="Ai.TaskAggression"/>; the rows hold 1, 0.5, 0 or less). The goal
    /// of a fight its AI started is the mind's (16, 21, 144 rate 1; 34 and the protect goals 0), not the fight's row. Any other fight rates by its
    /// row (an order's 5 rates 0, 61 rates 1), but a defence (row 13) rates 0: the original plans it under Self preservation (32) or a protect
    /// goal, which rate 0. Not fighting (a bolt still in flight), the goal as elsewhere.
    /// </summary>
    static float Rating(CharacterCold striker)
    {
        if (striker.Running is not { Task: Movement.CharacterTask.Attack } fight) return Ai.TaskAggression.OfGoal(striker);
        if (striker.Ai is { Goal: { } goal } mind && Ai.AiSystem.OwnsFight(striker, mind))
            return Ai.TaskAggression.Of(goal.Task.Type);

        return fight.Row == Data.Gameplay.Ai.TaskType.AttackAttackersOf ? 0 : Ai.TaskAggression.Of(fight.Row);
    }

    /// <summary>
    /// The amount of a deed against <paramref name="victim"/> (<c>FUN_140796020</c>, <b>Observed</b>): its CHARACTER <c>faction importance</c> times
    /// 1 inside its own squad's town, 0.6 inside a town whose faction is allied with (or is) the victim's, else 0.025 (outside every town too). The
    /// town is the one the victim stands in (the status object's <c>+0x88</c>, read here as <see cref="PopulationData.TownAt"/>); its own town is its
    /// squad's (the game's ownership object's <c>+0x30</c>, <b>Unknown</b> beyond that).
    /// </summary>
    public float Weight(World world, int victim)
    {
        if (world.Characters.Cold(victim) is not { } v || v.FactionImportance == 0) return 0;
        float w = 0.025f;
        if (data is not null && data.TownAt(world.Characters.Next[victim].Position) is >= 0 and var town)
        {
            if (v.SquadId >= 0 && world.Squads.Find(v.SquadId) is { Town: var home } && home == town) w = 1;
            else if (town < data.SiteFactions.Count && data.SiteFactions[town] is >= 0 and var owner && Relations.Contains(owner)
                && Relations.Contains(v.Faction)
                && Relations.IsAlly(owner, v.Faction)) w = 0.6f;
        }
        return w * v.FactionImportance;
    }

    /// <summary>
    /// Applies a deed of <paramref name="doer"/> against <paramref name="victim"/> as a relation event of the victim's faction about the doer's (none
    /// within one faction or of amount 0).
    /// </summary>
    void Apply(World world, int doer, int victim, RelationEventKind kind, float x)
    {
        var table = world.Characters;
        if (table.Cold(doer) is not { } d || table.Cold(victim) is not { } v || d.Faction == v.Faction || x == 0) return;
        PlayerRelations.Event(world, Relations, v.Faction, d.Faction, kind, x, table.IdOf(doer), table.IdOf(victim));
    }

    /// <summary>
    /// A blow that landed on <paramref name="victim"/> (the melee hit <c>FUN_1404394f0</c> and the bolt hit <c>FUN_140439c90</c>, through
    /// <c>FUN_140999f00</c> / <c>FUN_140999d50</c>): in this order: <see cref="RelationEventKind.Killed"/> when the blow killed (the death function
    /// runs inside the damage, <c>FUN_1407a6200</c>, crediting the killer); <see cref="RelationEventKind.Hit"/> when the striker rates above 0.1
    /// (<see cref="Rating"/>); when the victim is now down, <see cref="RelationEventKind.Knockout"/>
    /// (<see cref="RelationEventKind.KnockoutMinor"/> for a striker rated 0.15 or less, 0.1 or less for a bolt, and for a bolt not aimed at the
    /// victim) and, dead, <see cref="RelationEventKind.KillingBlow"/>. A stray bolt never counts as a hit. The test is the victim's state after the
    /// blow, so a blow on one already down counts again.
    /// </summary>
    public void Blow(World world, int striker, int victim, bool down, bool dead, bool bolt = false, bool aimed = true)
    {
        if (down || dead) Remember(world, striker, victim, dead);
        float x = Weight(world, victim);
        if (x == 0 || world.Characters.Cold(striker) is not { } doer) return;

        float rating = Rating(doer);
        if (dead) Apply(world, striker, victim, RelationEventKind.Killed, x);
        if (aimed && rating > 0.1f) Apply(world, striker, victim, RelationEventKind.Hit, x);
        if (!down && !dead) return;

        bool minor = bolt ? rating <= 0.1f || !aimed : rating <= 0.15f;
        Apply(world, striker, victim, minor ? RelationEventKind.KnockoutMinor : RelationEventKind.Knockout, x);
        if (dead) Apply(world, striker, victim, RelationEventKind.KillingBlow, x);
    }

    /// <summary>
    /// <paramref name="captor"/> put <paramref name="captive"/> in a cage (<c>Task_PutInSomething</c> made for the cage, <c>FUN_140358670</c>, whose
    /// kind is 12; the bed put-in's 9 has no row): <see cref="RelationEventKind.Caged"/>.
    /// </summary>
    public void Caged(World world, int captor, int captive)
    {
        // The same task makes the captive's squad remember the captor (long-term tag 5 MY_CAPTOR, FUN_140673a20 in FUN_140358670, Observed): the
        // dialogue system's memory.
        CagedHook?.Invoke(world, captor, captive);
        Apply(world, captor, captive, RelationEventKind.Caged, Weight(world, captive));
    }

    /// <summary>
    /// The squads' memories the deeds write (the dialogue system's; null: none are written).
    /// </summary>
    public Dialogue.SquadMemory? Memory { get; set; }

    /// <summary>
    /// The long-term tags of a downing or killing blow (<c>FUN_140673a20</c>, docs/game/factions-squads-towns.md 3.5, <b>Observed</b>): a death makes
    /// the victim's squad tag the striker 9 KILLED_MY_FRIEND; when the victim's whole squad is now down (<c>FUN_140793160</c>), its squad tags the
    /// striker 7 DEFEATED_MY_SQUAD and the striker's squad tags the victim 8.
    /// </summary>
    void Remember(World world, int striker, int victim, bool dead)
    {
        var table = world.Characters;
        if (Memory is not { } memory || table.Cold(striker) is not { } s || table.Cold(victim) is not { } v || v.SquadId < 0
            || v.SquadId == s.SquadId)
        {
            return;
        }
        var strikerId = table.IdOf(striker);
        if (dead) memory.Remember(world, v.SquadId, strikerId, Dialogue.MemoryTag.KilledMyFriend);
        if (world.Squads.Find(v.SquadId) is not { } squad) return;
        foreach (var m in squad.Members)
            if (table.TryResolveNext(m, out int ms) && table.Next[ms].Alive && table.Cold(ms) is { Medical.Incapacitated: false }) return;
        memory.Remember(world, v.SquadId, strikerId, Dialogue.MemoryTag.DefeatedMySquad);
        if (s.SquadId >= 0) memory.Remember(world, s.SquadId, table.IdOf(victim), Dialogue.MemoryTag.SquadLostToMe);
    }

    /// <summary>
    /// Called with the captor's and the captive's slots at every <see cref="Caged"/>, before the relation event (the dialogue system's squad memory:
    /// <c>MY_CAPTOR</c>).
    /// </summary>
    public Action<World, int, int>? CagedHook { get; set; }

    /// <summary>
    /// One first-aid update of <paramref name="seconds"/> by <paramref name="medic"/> on an unconscious or leg-collapsed
    /// (<see cref="Bodies.MedicalState.Crawling"/>, medical <c>+0x160</c>) <paramref name="patient"/> (<c>FUN_140649a00</c> / <c>FUN_14064d800</c>):
    /// <see cref="RelationEventKind.FirstAid"/>, +0.15 x per second up to +5.
    /// </summary>
    public void FirstAid(World world, int medic, int patient, float seconds)
    {
        if (world.Characters.Cold(patient) is not { Medical: { Dead: false } med } || !(med.Unconscious || med.Crawling)) return;
        Apply(world, medic, patient, RelationEventKind.FirstAid, Weight(world, patient) * seconds);
    }
}