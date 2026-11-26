using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Crime;

/// <summary>
/// Settings of the <see cref="ArrestSystem"/>; engine choices unless a doc label says otherwise.
/// </summary>
public sealed record ArrestSettings
{
    /// <summary>
    /// The bounty sweep runs every this many ticks (the original checks in its per-character sense update).
    /// </summary>
    public int SpotEveryTicks { get; init; } = 15;
    /// <summary>
    /// A looker recognises a bounty within this distance (the original's 160000 squared, economy.md 11.8, <b>Observed</b>).
    /// </summary>
    public float SpotRange { get; init; } = 400;
    /// <summary>
    /// Game seconds of being seen that fill the recognition meter (<b>engine</b>: one meter per suspect; the original's per-looker meter fills in
    /// lerp(v, 5a, a) x lerp(f, 4, 1) s with a = 5 + 40 k, docs/game/economy.md 11.8, <b>Verified</b>; 45 is a for a stealth-0 target in the
    /// looker's town, without the view and looker terms). A booked or witnessed crime skips it (<see cref="CharacterCold.RecentlyWanted"/>).
    /// </summary>
    public float RecognitionSeconds { get; init; } = 45;
    /// <summary>
    /// A guard counts as in his own town within this multiple of his resident squad's home radius of its centre (<b>engine reading</b> of the
    /// original's status <c>+0x88</c> test).
    /// </summary>
    public float TownSlack { get; init; } = 2;
    /// <summary>
    /// Walks to the cage before a player's Put in Cage gives up (the player's order; an NPC's 112 walks as a lost walk).
    /// </summary>
    public int CageWalks { get; init; } = 3;
    /// <summary>
    /// A player character within this of the cage pays a bail (the original pays at a law NPC's dialogue in that town).
    /// </summary>
    public float BailRange { get; init; } = 500;
    /// <summary>
    /// A Put in Cage order naming no cage takes the best free cage within this distance of the carrier.
    /// </summary>
    public float CageSearchRange { get; init; } = AggroSystem.OutdoorSight;
    /// <summary>
    /// Fixes option (Meitou, on by default): an arrest order does not lift its downed target while no cage is free for it or while another
    /// character of the guard's faction carries it (<see cref="ArrestSystem.LiftHeld"/>, #339). Off: it lifts either way, as the game does.
    /// </summary>
    public bool LiftFix { get; init; } = true;
    /// <summary>
    /// Fixes option (Meitou, on by default): an arrest whose fight stopped while the guard's senses hold no entry of the target gives up
    /// (<see cref="ArrestSystem.LostTarget"/>, #1312). Off: the fight starts again however far the target is, as the game does.
    /// </summary>
    public bool LostFix { get; init; } = true;
    /// <summary>
    /// Keep a <see cref="ArrestSystem.Log"/> (tests).
    /// </summary>
    public bool RecordLog { get; init; }
}

/// <summary>
/// Where an arrest is: the guard fighting until the target is down, lifting it, carrying it to a cage. No strip: the orders' list B (240, 213) has
/// no 118, which 181 alone provides (docs/game/ai-tasks.md "Arrest chain").
/// </summary>
public enum ArrestPhase : byte
{
    /// <summary>
    /// The guard fights the target until it is down.
    /// </summary>
    Fight,
    /// <summary>
    /// The guard lifts the downed target.
    /// </summary>
    Lift,
    /// <summary>
    /// The guard carries the target to the cage.
    /// </summary>
    Walk,
}

/// <summary>
/// Where a strip (row 181, <see cref="ArrestSystem.Strip"/>) is after one tick.
/// </summary>
internal enum StripStep : byte
{
    /// <summary>
    /// Going on: the guard trades blows, or kneels.
    /// </summary>
    Wait,
    /// <summary>
    /// The guard walks up to the target: the kneel's time starts again.
    /// </summary>
    Walk,
    /// <summary>
    /// The guard began to kneel: the kneel's time starts.
    /// </summary>
    Kneel,
    /// <summary>
    /// The target's weapon sections went to the guard.
    /// </summary>
    Stripped,
    /// <summary>
    /// The target got up or was taken, or there is no strip step.
    /// </summary>
    TargetUp,
    /// <summary>
    /// The guard has no working arm.
    /// </summary>
    GaveUp,
}

/// <summary>
/// One guard's arrest of one character (serial state of the <see cref="ArrestSystem"/>): the act-out of the guard's running order 240, 213 or 112
/// (<see cref="Order"/>).
/// </summary>
public sealed class Arrest
{
    /// <summary>
    /// The arresting guard.
    /// </summary>
    public required CharacterId Guard { get; init; }
    /// <summary>
    /// The character being arrested.
    /// </summary>
    public required CharacterId Target { get; init; }
    /// <summary>
    /// The guard's faction, whose bounty entry the arrest is about.
    /// </summary>
    public required int Faction { get; init; }
    /// <summary>
    /// Where the arrest is.
    /// </summary>
    public ArrestPhase Phase { get; set; }
    /// <summary>
    /// Game seconds in the phase (the wait between attack orders, the lift's retries).
    /// </summary>
    public float Time { get; set; }
    /// <summary>
    /// Failed lifts (5 end the arrest).
    /// </summary>
    public int Tries { get; set; }
    /// <summary>
    /// Game seconds the guard, free of fighting, has failed to start lifting the downed target.
    /// </summary>
    public float Waited { get; set; }
    /// <summary>
    /// The cage chosen (reserved while the arrest lasts).
    /// </summary>
    public string? Cage { get; set; }
    /// <summary>
    /// A code mod's take-over with its enslave flag (<see cref="ArrestSystem.TakeOver"/>): the captive becomes the guard faction's slave when caged.
    /// </summary>
    public bool Enslave { get; init; }
    /// <summary>
    /// The guard's order this arrest acts out (240 Capture Target, 213 Lifting/Snatching, 112 Find and put in cage). The record lives while that
    /// order is the guard's started front.
    /// </summary>
    internal OrderEntry Order { get; init; } = null!;

    /// <summary>
    /// Adds the arrest's state (the cage as a stable hash of its id, the flags as tags) to the world hash.
    /// </summary>
    internal void Hash(ref StateHasher h)
    {
        h.Add(Guard);
        h.Add(Target);
        h.Add(Faction);
        h.Add((int)Phase);
        h.Add(Time);
        h.Add(Tries);
        h.Add(Waited);
        h.Add(Cage is { } c ? Rng.StableHash(c) : 0UL);
        if (Enslave) h.Add(0x536C6176);
        h.Add((int)Order.Row);
    }
}

/// <summary>
/// What the arrest system did, for its <see cref="ArrestSystem.Log"/>.
/// </summary>
public enum ArrestEventKind
{
    /// <summary>
    /// A guard was ordered to attack instead of arresting (outside his town, or an animal target).
    /// </summary>
    Attack,
    /// <summary>
    /// A guard began fighting to arrest the target.
    /// </summary>
    Arrest,
    /// <summary>
    /// The guard lifted the downed target.
    /// </summary>
    Lifted,
    /// <summary>
    /// The target was caged.
    /// </summary>
    Jailed,
    /// <summary>
    /// No free cage: the order ended, the guard keeping the captive.
    /// </summary>
    Dropped,
    /// <summary>
    /// The arrest was given up: the guard or the target was lost, the target was carried off or caged otherwise, got away, or the guard could not
    /// go on.
    /// </summary>
    GaveUp,
    /// <summary>
    /// A prisoner was released (nothing logs it yet).
    /// </summary>
    Released,
    /// <summary>
    /// A player character paid a prisoner's bail.
    /// </summary>
    Bailed,
    /// <summary>
    /// A bounty was claimed for a caged captive.
    /// </summary>
    Claimed,
    /// <summary>
    /// A bounty hunter said its event 44 line at a target (the amount is the target's bounty with the hunter's booking faction).
    /// </summary>
    Hunt,
    /// <summary>
    /// A guard fired event 44 or 45 at the target (the amount is the event number).
    /// </summary>
    Spotted
}

/// <summary>
/// A guard that fired event 44 or 45 at a target (serial state of the <see cref="ArrestSystem"/>): the target is left to that dialogue until
/// <see cref="Until"/>.
/// </summary>
public sealed class Spotting
{
    /// <summary>
    /// The guard that fired the event.
    /// </summary>
    public required CharacterId Guard { get; init; }
    /// <summary>
    /// The character it was fired at.
    /// </summary>
    public required CharacterId Target { get; init; }
    /// <summary>
    /// The event fired: 44 BOUNTY_SPOTTED or 45 ESCAPED_PRISONER_SPOTTED.
    /// </summary>
    public required int Event { get; init; }
    /// <summary>
    /// The tick the target is left to the dialogue until (pushed on while the guard still talks).
    /// </summary>
    public long Until { get; set; }

    /// <summary>
    /// Adds the guard, target, event and end tick to the world hash.
    /// </summary>
    internal void Hash(ref StateHasher h)
    {
        h.Add(Guard);
        h.Add(Target);
        h.Add(Event);
        h.Add(Until);
    }
}

/// <summary>
/// One arrest log line: the tick, what happened, the guard, the target, the faction and an amount (bounty, fine, price or event number).
/// </summary>
public readonly record struct ArrestEvent(long Tick, ArrestEventKind Kind, CharacterId Guard, CharacterId Target, int Faction, int Amount);

/// <summary>
/// The police and wanted characters (docs/simulation.md "Arrest and jail"; the original's rules in docs/game/economy.md 11.5, 11.7, 11.8). Everything
/// runs in <c>SlowWorld</c>, serially, in slot order, after the crime system's sweep.
/// <list type="bullet">
/// <item><b>Spotting</b> (11.8): an awake NPC within 400 that sees a player character whose bounty with its faction reaches its class's threshold
/// (<see cref="SpotThreshold"/>) fills the target's recognition meter, or recognises it at once while it is recently wanted. The nearest free
/// police-like looker fires event 44 BOUNTY_SPOTTED at it through the
/// <see cref="Talk"/> dialogue; a police-like NPC in its own town fires event 45 ESCAPED_PRISONER_SPOTTED at an escaped prisoner it sees there. The
/// data's lines then fine (DA 2 and
/// the "Bounty extortion pay" talk), arrest (DA 29, <see cref="ArrestFromDialogue"/>) or do nothing. A booking witness is left to its witness event;
/// one already fighting arrests at once.</item>
/// <item><b>Arrest orders</b> (11.7, DA 29): in his own town the guard is ordered 240 Capture Target, then 112 Find and put in cage; elsewhere, or
/// for an animal, 4 Attacking (<see cref="OrderArrest"/>; the engine gives the same orders for a world without dialogue events). The orders' act-out
/// is an <see cref="Arrest"/>: he fights until the target is down, lifts it (<see cref="Carrying.CarrySystem"/>; no strip), carries it to the best
/// free cage (<see cref="FixtureRegistry.FindCage"/>) and imprisons it with its sentence.</item>
/// <item><b>Goal 204</b> Arrest bounties (11.7): a goal of the one chooser (<see cref="ArrestGoals"/>), acted out to the lift.</item>
/// <item><b>Release</b> (11.5, goal 211): a player character whose sentence is served and who is awake is freed by the jail's faction (an NPC of it
/// near the cage that holds goal 211, else the system in its name for a faction that books bounties; engine choice), which erases its and the jail's
/// bounty entries. NPC prisoners stay (their jailers' AI is not built).</item>
/// <item><b>Bail, claims, hunters</b> (11.4, 11.5, 11.8; ArrestSystem.Bounties.cs): <see cref="PayBailOrder"/>, the player's
/// <see cref="PutInCageOrder"/> with its reward
/// (<see cref="Claim"/>), and bounty hunters that recognise a bounty above <see cref="HuntLimit"/> (event 44, whose line orders them 101).</item>
/// </list>
/// </summary>
public sealed partial class ArrestSystem(CrimeSystem crimes, GameConstants constants, ArrestSettings? settings = null) : ITickSystem, IStateHashed,
    ITaskOwner, ITaskHandler, IOrderSteps, IDutyGoalOwner
{
    /// <summary>
    /// Goal 204's picks and act-outs, made at the registration.
    /// </summary>
    ArrestGoals? _goals;

    /// <summary>
    /// The running arrests, stepped in start order.
    /// </summary>
    readonly List<Arrest> _arrests = [];
    /// <summary>
    /// The targets left to a spotting's dialogue.
    /// </summary>
    readonly List<Spotting> _spottings = [];
    /// <summary>
    /// The arrest log, kept while <see cref="ArrestSettings.RecordLog"/> is on.
    /// </summary>
    readonly List<ArrestEvent> _log = [];

    /// <summary>
    /// The settings; the defaults when none were given.
    /// </summary>
    public ArrestSettings Settings { get; } = settings ?? new ArrestSettings();
    /// <summary>
    /// The crime system whose bookings and police tests the arrests use.
    /// </summary>
    public CrimeSystem Crimes { get; } = crimes;
    /// <summary>
    /// The carry system the guards lift and carry with; null: an arrest ends when the target is down.
    /// </summary>
    public Carrying.CarrySystem? Carry { get; set; }
    /// <summary>
    /// The strip of row 181 (the same task of the capture job); null: arrests skip it.
    /// </summary>
    public Kidnap.CaptiveSystem? Captives { get; set; }
    /// <summary>
    /// The relation event of a caging (<see cref="FactionDeeds.Caged"/>: the captive's faction about the one who caged it); null: none.
    /// </summary>
    public FactionDeeds? Deeds { get; set; }
    /// <summary>
    /// The running arrests.
    /// </summary>
    public IReadOnlyList<Arrest> Arrests => _arrests;
    /// <summary>
    /// The targets left to a spotting's dialogue.
    /// </summary>
    public IReadOnlyList<Spotting> Spottings => _spottings;
    /// <summary>
    /// The dialogue spotting fires event 44 through (45 and 36 are the sight pass's, <see cref="SightCrimes"/>); null, or its event lines off
    /// (<see cref="Dialogue.DialogueSettings.AmbientEvents"/>): the engine gives the arrest orders instead (<see cref="OrderArrest"/>).
    /// </summary>
    public Dialogue.DialogueSystem? Talk { get; set; }
    /// <summary>
    /// A dialogue with its event lines on takes the spotting (the engine gives the arrest orders otherwise).
    /// </summary>
    bool Talks => Talk is { Settings.AmbientEvents: true };
    /// <summary>
    /// The arrest log (empty unless <see cref="ArrestSettings.RecordLog"/>).
    /// </summary>
    public IReadOnlyList<ArrestEvent> Log => _log;

    /// <summary>
    /// The least bounty a looker of <paramref name="npcClass"/> recognises (<c>FUN_1408520c0</c>, <b>Observed</b>): LAW_ENFORCEMENT 1, MILITARY 2000
    /// (the original's is 4000 or 2000 by a test not decoded: <b>engine</b> takes 2000), ADVENTURER 1000, SLAVE never, others 10000.
    /// </summary>
    public static int SpotThreshold(FundamentalType npcClass) => npcClass switch
    {
        FundamentalType.LawEnforcement => 1,
        FundamentalType.Military => 2000,
        FundamentalType.Adventurer => 1000,
        FundamentalType.Slave => int.MaxValue,
        _ => 10000,
    };

    #region The tick

    /// <summary>
    /// Ticks between a member's looks for goal 204 (<see cref="ArrestGoals.Look"/>; engine choice: the AI's cadence).
    /// </summary>
    public const int DutyEveryTicks = 15;

    /// <summary>
    /// Registers goal 204 (<see cref="ArrestGoals.Register"/>).
    /// </summary>
    void IDutyGoalOwner.RegisterGoals(DutyGoals goals) => (_goals ??= new ArrestGoals(this)).Register(goals);

    /// <summary>
    /// The slow-world step: books the crime system's new bookings, steps the spottings; every <see cref="ArrestSettings.SpotEveryTicks"/> the
    /// disguise, the spotting and the status-town passes; then goal 204's act-outs and look, the arrests and the cagings, each dropping the finished
    /// ones.
    /// </summary>
    public void SlowWorld(World world)
    {
        foreach (var b in Crimes.BookingsOf(world.Tick)) Booked(world, b);
        for (int k = 0; k < _spottings.Count;)
        {
            if (StepSpotting(world, _spottings[k])) k++;
            else _spottings.RemoveAt(k);
        }
        if (world.Beat(Settings.SpotEveryTicks, TickPhases.ArrestSpot))
        {
            Disguise(world, Math.Max(1, Settings.SpotEveryTicks) * world.TickSeconds);
            Spot(world, Math.Max(1, Settings.SpotEveryTicks) * world.TickSeconds);
            UpdateStatusTowns(world);
            DropTownStatusOutsideTowns(world);
            UpdateKidnapVictims(world);
            EscapeFarFromOwner(world);
            Kidnap.SlaveEscape.Step(world);
        }
        _goals?.Step(world);
        _goals?.Look(world, world.Every(DutyEveryTicks));
        for (int k = 0; k < _arrests.Count;)
        {
            if (Step(world, _arrests[k])) k++;
            else _arrests.RemoveAt(k);
        }
        for (int k = 0; k < _cagings.Count;)
        {
            if (StepCaging(world, _cagings[k])) k++;
            else _cagings.RemoveAt(k);
        }
    }

    /// <summary>
    /// Each living character's status town (<see cref="CharacterCold.StatusTown"/>), written before the status tests as <c>FUN_1405a51e0</c> does
    /// before <c>FUN_1405a4f50</c> (<b>Verified</b>). The original runs it in each character's AI update, at a cadence that is <b>Unknown</b>; here
    /// on the spotting pass. Without town data nothing changes.
    /// </summary>
    void UpdateStatusTowns(World world)
    {
        if (Towns is not { } data) return;
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
            if (table.Next[s].Alive && table.Cold(s) is { } c)
                c.StatusTown = AiSystem.StatusTownAt(data, world, table.Next[s].Position, c.StatusTown);
    }

    /// <summary>
    /// An escaped prisoner or a freed character stops being one once it is out of every town: the status update <c>FUN_1405a4f50</c> clears status
    /// <c>+0xe8</c> and <c>+0xea</c> on each update while the status object's current town (<c>+0x88</c>, <see cref="CharacterCold.StatusTown"/>)
    /// is null (<b>Verified</b>). Without town data the flags stay.
    /// </summary>
    void DropTownStatusOutsideTowns(World world)
    {
        if (Towns is null) return;
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!table.Next[s].Alive || table.Cold(s) is not { } c || !c.StatusTown.IsNone) continue;

            c.Escaped = false;
            c.Freed = false;
        }
    }

    /// <summary>
    /// The kidnap victim flag's upkeep (<see cref="CharacterCold.KidnapVictim"/>; the per-update <c>FUN_140852a30</c>, <b>Verified</b>): a victim
    /// nobody carries loses the flag in a bed in its own town (engine reading: <see cref="InOwnTown"/>); a carried one loses it when it regards
    /// its carrier as friendly, else its squad tags the carrier TEMP_ENEMY each pass. A carried victim keeps the flag without a dialogue system.
    /// </summary>
    void UpdateKidnapVictims(World world)
    {
        var table = world.Characters;
        var talk = world.SystemOf<Dialogue.DialogueSystem>();
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!table.Next[s].Alive || table.Cold(s) is not { KidnapVictim: true } c) continue;

            if (c.CarriedBy.IsNone)
            {
                if (c.InSomething == HeldState.Bed && InOwnTown(world, s)) c.KidnapVictim = false;

                continue;
            }

            if (talk is null || !table.TryResolveNext(c.CarriedBy, out int carrier)) continue;

            if (CharacterFacts.RegardsFriendly(talk, world, s, carrier)) c.KidnapVictim = false;
            else talk.TagEnemy(world, s, carrier);
        }
    }

    /// <summary>
    /// The squared distance from its owner past which a slave outside every town escapes (<c>DAT_1416f016c</c>, <b>Verified</b>; about 3536).
    /// </summary>
    public const float EscapeDistanceSquared = 12_500_000f;

    /// <summary>
    /// A slave far from its owner outside every town escapes: the same status update <c>FUN_1405a4f50</c> sets slave state 2 through the setter for a
    /// slave (state 1) while the status object's current town (<c>+0x88</c>) is null and its squared distance to the owner is above
    /// <see cref="EscapeDistanceSquared"/> (<b>Verified</b>; the town is <see cref="CharacterCold.StatusTown"/>). The owner is the master squad's
    /// leader (<see cref="Kidnap.ObedientSlave.OwnerOf"/>, Meitou's stand-in for the owner handle). With no owner handle the slave escapes in town
    /// too (<c>FUN_1404fe6e0</c> rejects a null handle); a master squad or saved owner (the save's <c>slaver</c> handle, or the Obedient Slave job's,
    /// <see cref="Kidnap.ObedientSlave.PendingOwnerJob"/>) still waiting to load is kept, and so is one whose Meitou save lost its owner
    /// (<see cref="Saving.SavedCharacterLink.SlaverLost"/>) and a slave held in an owned cage (<see cref="CagedByOwner"/>).
    /// </summary>
    void EscapeFarFromOwner(World world)
    {
        if (Towns is null) return;
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!table.Next[s].Alive || table.Cold(s) is not { SlaveState: 1 } c) continue;
            if (Kidnap.ObedientSlave.OwnerOf(world, c) is not { } owner)
            {
                bool pending = c.Save is { Slaver.IsNull: false } or { SlaverLost: true } || Kidnap.ObedientSlave.PendingOwnerJob(c) >= 0;
                if (c.SlaveMaster < 0 && !pending && !CagedByOwner(world, c)) FixtureRegistry.TagEscaped(world, c);

                continue;
            }

            if (owner.Slot == s || !c.StatusTown.IsNone) continue;

            var at = table.Next[s].Position;
            if (Vector3.DistanceSquared(at, table.Next[owner.Slot].Position) > EscapeDistanceSquared) FixtureRegistry.TagEscaped(world, c);
        }
    }

    /// <summary>
    /// Whether a slave is held in a cage that has an owner. Entering a cage gives the slave the cage's owner handle (<c>FUN_140330600</c>), a
    /// registered object, so the original's ownerless escape never applies there; Meitou's stand-in for that handle, the resident squad of the
    /// cage's building, is missing for a cage without one (a Prisoner Pole), which made a start prisoner an escaped slave in its cage (#647).
    /// </summary>
    static bool CagedByOwner(World world, CharacterCold c) =>
        c is { IsImprisoned: true, InWhat: { } id } && world.Fixtures.Find(id) is { Kind: FixtureKind.Cage, Owner: >= 0 };

    /// <summary>
    /// Whether the character is a guard or target of an arrest.
    /// </summary>
    internal bool Busy(CharacterId id)
    {
        foreach (var a in _arrests) if (a.Guard == id || a.Target == id) return true;
        return false;
    }

    /// <summary>
    /// A police-like NPC free to start an arrest (<see cref="Free"/>).
    /// </summary>
    bool FreeGuard(World world, int slot) => Free(world, slot) && Crimes.IsPoliceLike(world, world.Characters.Cold(slot)!);

    /// <summary>
    /// An NPC free to act on a sighting: awake, not fighting, fleeing, carrying, held or treating, not in another arrest, with no order.
    /// </summary>
    bool Free(World world, int slot)
    {
        var table = world.Characters;
        if (!table.Next[slot].Alive || table.Next[slot].Task is (byte)CharacterTask.Attack or (byte)CharacterTask.Treat) return false;
        return table.Cold(slot) is { IsPlayer: false, InCombat: false, Fleeing: false, IsImprisoned: false, Medical.Incapacitated: false,
            Carrying.IsNone: true, CarriedBy.IsNone: true, Fighter.CanAttack: true } c
            && !Busy(table.IdOf(slot)) && !DecisionSystem.Ordered(c);
    }

    /// <summary>
    /// A target the police may go after: a living, awake player character, not carried or in a cage, not already being arrested or left to a
    /// spotting's dialogue.
    /// </summary>
    bool Suspect(World world, int slot) =>
        world.Characters.Next[slot].Alive
            && world.Characters.Cold(slot) is { IsPlayer: true, IsImprisoned: false, CarriedBy.IsNone: true, Medical.Incapacitated: false }
            && !Busy(world.Characters.IdOf(slot))
        && !_spottings.Exists(p => p.Target == world.Characters.IdOf(slot));

    /// <summary>
    /// A police-like witness booked a crime (the original's witnessed-crime dialogues): he deals with the culprit at once.
    /// </summary>
    void Booked(World world, Booking b)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(b.Witness, out int g) || !table.TryResolveNext(b.Culprit, out int t) || !Suspect(world, t)) return;
        var gc = table.Cold(g)!;
        bool fighting = table.Next[g].Task == (byte)CharacterTask.Attack || gc.InCombat;
        if (!fighting && Talks) return;   // the witness's dialogue event (CrimeSystem) decides, as the spotting's does
        if (fighting ? !Crimes.IsPoliceLike(world, gc) || Busy(b.Witness) || gc.Medical is { Incapacitated: true } : !FreeGuard(world, g)) return;
        OrderArrest(world, g, t, CapturesInTown(world, b.Witness, table.Cold(t)!));
    }

    #endregion

    #region Spotting

    /// <summary>
    /// Whether lookers can tell an escaped prisoner: event 45's test in <c>FUN_140859680</c> (<b>Verified</b>) wants the escaped flag and either
    /// no disguise level at all (status <c>+0x100</c> at 0: any uniform piece hides it, not only a working disguise) or a running blown cooldown
    /// (<c>+0x110</c>).
    /// </summary>
    internal static bool EscapeShows(CharacterCold c) => c.Escaped && (c.RecentlyWanted > 0 || c.DisguiseLevel <= 0);

    /// <summary>
    /// The spotting pass over the suspects in slot order: slave catching, the recognition meter from the lookers that see the target, then event
    /// 44 or the engine's arrest, the escapee's arrest without the sight pass, and a hunt for a seen bounty.
    /// </summary>
    void Spot(World world, float seconds)
    {
        var table = world.Characters;
        var next = table.Next;
        float range2 = Settings.SpotRange * Settings.SpotRange;
        // The sight pass fires event 45 itself (Perception.SightCrimes); without it the engine arrests the escapee.
        bool sight = SightSystem.Running(world);
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!Suspect(world, s)) continue;
            var c = table.Cold(s)!;
            bool wanted = c.Bounties is { Any: true };
            if (c.SlaveState != 0 && CatchSlave(world, s, c)) continue;
            if (!wanted && !c.Escaped)
            {
                c.Recognition = 0;
                continue;
            }
            var at = next[s].Position;
            bool seen = false;
            int best = -1, escapeGuard = -1, hunter = -1;
            float bestD2 = float.MaxValue, escapeD2 = float.MaxValue, hunterD2 = float.MaxValue;
            for (int w = 0; w < table.HighWater; w++)
            {
                if (w == s || !next[w].Alive) continue;
                // An animal looker never recognises (FUN_140856f00 returns at once, economy.md 11.8).
                if (table.Cold(w) is not { IsPlayer: false, IsImprisoned: false, Medical.Incapacitated: false, CarriedBy.IsNone: true } wc
                    || !Crimes.Relations.Contains(wc.Faction) || SquadRegistry.IsAnimal(wc))
                {
                    continue;
                }
                float d2 = Vector3.DistanceSquared(next[w].Position, at);
                if (d2 > range2) continue;
                int bounty = c.Bounties?.BookedFor(Crimes.Factions.Relations, wc.Faction) is { Claimed: false } e ? e.Amount : 0;
                // Any uniform piece hides the bounty, a partial or blown disguise too: FUN_140856f00's bounty branch needs the level at 0, and only
                // a looker that sees through a working disguise recognises it (Disguise).
                bool bare = c.DisguiseLevel <= 0;
                bool spots = bare && bounty > 0 && bounty >= SpotThreshold(wc.NpcClass);
                bool escapee = !sight && EscapeShows(c) && Crimes.IsPoliceLike(world, wc) && InOwnTown(world, w);
                bool hunts = bare && HuntsFor(wc, c);

                // A looker in combat mode or that regards the target as friendly (slot 126, its love included) neither recognises nor fires
                // (FUN_140856f00).
                if ((spots || hunts) && (InCombatMode(world, w, wc) || Crimes.RegardsFriendly(world, w, s))) (spots, hunts) = (false, false);
                if (!spots && !escapee && !hunts) continue;
                if (!SeesSuspect(world, w, s, c, Settings.SpotRange)) continue;

                if (escapee && FreeGuard(world, w) && d2 < escapeD2) (escapeGuard, escapeD2) = (w, d2);
                if (hunts)
                {
                    seen = true;
                    if (d2 < hunterD2 && FreeHunter(world, w)) (hunter, hunterD2) = (w, d2);
                }
                if (!spots) continue;
                seen = true;
                if (d2 < bestD2 && FreeGuard(world, w)) (best, bestD2) = (w, d2);
            }
            if (escapeGuard >= 0)
            {
                if (Talks) Spotted(world, escapeGuard, s, EscapedPrisonerSpotted);
                else OrderArrest(world, escapeGuard, s, CapturesInTown(world, table.IdOf(escapeGuard), c));
                continue;
            }
            float step = seconds / Math.Max(Settings.RecognitionSeconds, 1e-3f);
            c.Recognition = seen ? MathF.Min(1, c.Recognition + step) : MathF.Max(0, c.Recognition - step);
            bool recognised = seen && (c.RecentlyWanted > 0 || c.Recognition >= 1);
            if (recognised && best >= 0)
            {
                if (Talks) Spotted(world, best, s, BountySpotted);
                else OrderArrest(world, best, s, CapturesInTown(world, table.IdOf(best), c));
            }
            if (recognised && hunter >= 0 && hunter != best) BeginHunt(world, hunter, s);
        }
    }

    /// <summary>
    /// The looker is in combat mode, melee or ranged (<c>Character::isInCombatMode(1, 1)</c>, <c>FUN_140436610</c>): <c>FUN_140856f00</c> then
    /// returns at entry (<b>Verified</b>, disassembly at <c>140856f3a</c>), so it fills no recognition or disguise meter and fires nothing.
    /// </summary>
    static bool InCombatMode(World world, int slot, CharacterCold cold)
    {
        var table = world.Characters;
        return world.SystemOf<CombatSystem>() is { } combat && CharacterFacts.InCombatMode(combat, table, table.IdOf(slot), cold, true, true);
    }

    /// <summary>
    /// Whether the looker in slot <paramref name="w"/> sees the suspect <paramref name="c"/> in slot <paramref name="s"/> now: the shared sight
    /// test within <paramref name="range"/> with the sight ray (<see cref="SightRay.Sees"/>), and for a sneaking suspect only once the looker has spotted it (its spot meter is full,
    /// <see cref="CrimeSystem.Spotted"/>; docs/game/perception.md "Spotting a sneaker"), not at first sight (#649).
    /// </summary>
    internal bool SeesSuspect(World world, int w, int s, CharacterCold c, float range) => SuspectView(world, w, s, c, range) > 0;

    /// <summary>
    /// The spot value of the slave events (<c>FUN_140853620</c>): the looker's view factor of the target with its daylight sight range
    /// (<see cref="SuspectView"/>).
    /// </summary>
    internal float SpotValue(World world, int w, int s)
    {
        var table = world.Characters;
        return SuspectView(world, w, s, table.Cold(s)!, AggroSystem.RangeOf(table.Cold(w)!, AggroSystem.OutdoorSight));
    }

    /// <summary>
    /// The view factor of <see cref="SeesSuspect"/> (<see cref="SightRay.Sees"/>'s v), 0 when the looker does not see the suspect.
    /// </summary>
    float SuspectView(World world, int w, int s, CharacterCold c, float range) => Crimes.SuspectView(world, w, s, c, range);

    /// <summary>
    /// EV_BOUNTY_SPOTTED, EV_ESCAPED_PRISONER_SPOTTED, EV_ESCAPING_SLAVE_SPOTTED.
    /// </summary>
    public const int BountySpotted = 44, EscapedPrisonerSpotted = 45, EscapingSlaveSpotted = 36;

    /// <summary>
    /// Slave catching (docs/simulation.md "Arrest and jail"): an awake NPC of the slave's owner faction in its own town (<b>engine</b> gate) that
    /// sees a slave (state 1; its daylight sight test, full range) with a view factor above <see cref="EscapingSlave.SpotValue"/> tags it escaping
    /// when <see cref="Escapes"/> holds, unless the sight pass runs, which tags it and fires event 36 itself (<see cref="SightCrimes"/>). Without
    /// the sight pass the nearest such police-like or slaver NPC says event 36 ESCAPING_SLAVE_SPOTTED at an escaping slave (state 2), whose line's
    /// <c>DA_ATTACK_CHASE_FOREVER</c> orders it 101; with no line said nothing follows (the event is all the original's spotting does,
    /// <c>FUN_140685660</c>, <b>Verified</b>). The capture after the fight is the catchers' own goals' (167 cuffs, 168 lifts, 112 cages). True when
    /// a catcher sees the escaping slave (it is then left out of the bounty spotting): a free police-like NPC, or without the sight pass a free slaver.
    /// </summary>
    bool CatchSlave(World world, int s, CharacterCold c)
    {
        if (c.SlaveOwner < 0) return false;
        var table = world.Characters;
        var next = table.Next;
        var at = next[s].Position;
        bool sight = SightSystem.Running(world);
        int catcher = -1;
        float catcherD2 = float.MaxValue;
        for (int w = 0; w < table.HighWater; w++)
        {
            if (w == s || !next[w].Alive) continue;
            if (table.Cold(w) is not { IsPlayer: false, IsImprisoned: false, Medical.Incapacitated: false, CarriedBy.IsNone: true, SlaveState: 0 } wc
                || wc.Faction != c.SlaveOwner)
            {
                continue;
            }
            float d2 = Vector3.DistanceSquared(next[w].Position, at), range = AggroSystem.RangeOf(wc, AggroSystem.OutdoorSight);
            if (d2 > range * range || !InTownOf(world, w, next[w].Position)) continue;
            float view = SuspectView(world, w, s, c, range);
            if (view <= 0) continue;

            if (!sight && c.SlaveState == 1 && view > EscapingSlave.SpotValue && Escapes(world, w, s, c, at)) FixtureRegistry.TagEscaped(world, c);
            if (c.SlaveState == 2 && d2 < catcherD2
                && (FreeGuard(world, w) || !sight && wc.NpcClass == FundamentalType.Slaver && Free(world, w)))
            {
                (catcher, catcherD2) = (w, d2);
            }
        }
        if (catcher < 0) return false;

        if (Talks && !sight) Spotted(world, catcher, s, EscapingSlaveSpotted);
        return true;
    }

    /// <summary>
    /// The escape test of slave catching: <see cref="EscapingSlave.Test"/>, and for a slave whose owner Meitou does not know (a saved master that
    /// never loads) standing outside the looker's town (<b>engine</b>: the original's owner hand then resolves to nothing, which escapes at once).
    /// </summary>
    internal bool Escapes(World world, int w, int s, CharacterCold c, Vector3 at) =>
        EscapingSlave.Test(world, w, s, c, Captives, Crimes) || Kidnap.ObedientSlave.OwnerOf(world, c) is null && !InTownOf(world, w, at);

    /// <summary>
    /// The point is within the town of the guard's resident squad (engine choice: towns are not round, Rebirth's gates stand about 1500 from its
    /// centre and its radius is 184): within <see cref="ArrestSettings.TownSlack"/> times the squad's home radius, else, in a town with gates, no
    /// farther from the centre than the gate nearest the point plus <see cref="GateMargin"/>; a town without gates ends there.
    /// </summary>
    bool InTownOf(World world, int guard, Vector3 at)
    {
        if (world.Characters.Cold(guard) is not { SquadId: >= 0 } c || world.Squads.Find(c.SquadId) is not { TownOwn: true } squad) return false;
        var p = new Vector2(at.X, at.Z);
        float r = squad.HomeRadius * Settings.TownSlack;
        if (Vector2.DistanceSquared(p, squad.HomeCentre) <= r * r) return true;
        var gates = TownGates(world, squad.Town);
        if (gates.Length == 0) return false;
        var nearest = gates[0];
        foreach (var g in gates) if (Vector2.DistanceSquared(p, g) < Vector2.DistanceSquared(p, nearest)) nearest = g;
        return Vector2.Distance(p, squad.HomeCentre) <= Vector2.Distance(nearest, squad.HomeCentre) + GateMargin;
    }

    /// <summary>
    /// How far beyond its gates a town reaches for slave catching (an engine choice).
    /// </summary>
    public const float GateMargin = 100;

    /// <summary>
    /// The gate positions per town site, cached on first use.
    /// </summary>
    readonly Dictionary<int, Vector2[]> _townGates = [];

    /// <summary>
    /// Where the town's gates stand, in X/Z (none without building data).
    /// </summary>
    Vector2[] TownGates(World world, int site)
    {
        if (_townGates.TryGetValue(site, out var list)) return list;
        list = world.Systems.OfType<PopulationSystem>().FirstOrDefault()?.Data is { } data && (uint)site < (uint)data.Sites.Count
            ? [.. data.Sites[site].Buildings.Where(b => b.IsGate).Select(b => new Vector2(b.Position.X, b.Position.Z))] : [];
        return _townGates[site] = list;
    }

    /// <summary>
    /// The guard recognised the target: event <paramref name="ev"/> fired at it (<c>FUN_140856f00</c> / <c>FUN_140859680</c>, economy.md 11.8). What
    /// follows is the data's: the line said as a bubble arrests (DA 29), sends the guard to talk (DA 2 and an interrupt: the fine), or nothing. The
    /// original has no retry timer (<c>FUN_140856f00</c>, <b>Verified</b>): it fires at every call whose meter reads full, so here at every spotting
    /// pass the looker is free, and the lines' own <c>chance temporary</c> and <c>repetition limit</c> decide whether anything is said.
    /// </summary>
    void Spotted(World world, int g, int t, int ev)
    {
        var table = world.Characters;
        var guard = table.IdOf(g);
        var target = table.IdOf(t);
        if (Talk!.FireEvent(world, guard, target, ev)) Note(world, ArrestEventKind.Spotted, guard, target, table.Cold(g)!.Faction, ev);
        HoldSpotting(world, guard, target, ev);
    }

    /// <summary>
    /// Leaves the target to what <paramref name="guard"/>'s event <paramref name="ev"/> started while the guard still says it or walks up to say it
    /// (a <see cref="Spotting"/>; <b>engine</b>, with no time of its own), unless an arrest already works it.
    /// </summary>
    void HoldSpotting(World world, CharacterId guard, CharacterId target, int ev)
    {
        if (Busy(target)) return;

        _spottings.Add(new Spotting { Guard = guard, Target = target, Event = ev, Until = world.Tick + 1 });
    }

    /// <summary>
    /// One tick of a spotting; false when it is over: an arrest took the target, the bounty is paid (DA 49), or the guard no longer talks or walks
    /// up to talk.
    /// </summary>
    bool StepSpotting(World world, Spotting p)
    {
        if (Busy(p.Target) || !world.Characters.TryResolveNext(p.Target, out int t) || world.Characters.Cold(t) is not { } tc) return false;
        if (p.Event == BountySpotted && tc.Bounties is not { Any: true }) return false;
        // Still talking, or walking up to talk (DA 2's order 102 at the front of his queue).
        bool walksUp = world.Characters.TryResolveNext(p.Guard, out int g)
            && world.Characters.Cold(g) is { OrderQueue.Front.Row: TaskType.SeekAndTalkAndSendSignal };
        if (Talk?.Engages(p.Guard) == true || walksUp) p.Until = Math.Max(p.Until, world.Tick + 1);
        return world.Tick < p.Until;
    }

    /// <summary>
    /// The guard stands in the town his resident squad belongs to (dialogue action 29, <c>FUN_140680560</c> case 0x1d, <b>Verified</b>: the status
    /// object's current town <c>+0x88</c> is the speaker's ownership town <c>+0x30</c>, by pointer; event 45's test in <c>FUN_140859680</c> is the
    /// same compare). The town the point stands in is <see cref="Ai.AiSystem.TownAt"/>'s (its radius, as the original's lookup <c>FUN_1402688d0</c>);
    /// without town data, <b>engine</b> stand-in: <see cref="ArrestSettings.TownSlack"/> times the squad's home radius.
    /// </summary>
    public bool InOwnTown(World world, int guard)
    {
        var table = world.Characters;
        if (table.Cold(guard) is not { SquadId: >= 0 } c || world.Squads.Find(c.SquadId) is not { TownOwn: true } squad) return false;
        var p = table.Next[guard].Position;
        if (Towns is { } data) return AiSystem.TownAt(data, world, p).Site == squad.Town;
        float r = squad.HomeRadius * Settings.TownSlack;
        return Vector2.DistanceSquared(new Vector2(p.X, p.Z), squad.HomeCentre) <= r * r;
    }

    #endregion

    #region Arrest orders

    /// <summary>
    /// The detail of a 112 a code mod's <see cref="TakeOver"/> gives with its enslave flag: its caging makes the body the guard faction's slave.
    /// </summary>
    static readonly object Enslaving = new();

    /// <summary>
    /// The town duties, whose cage errands reserve their cages (resolved on first use; null in a world without them).
    /// </summary>
    Kidnap.TownDutySystem? _townDuties;

    /// <summary>
    /// The orders of <c>DA_ARREST_TARGET</c> (29, <c>FUN_140680560</c>, <b>Verified</b>), also the engine's arrest where no dialogue says the line:
    /// with <paramref name="capture"/> (<see cref="CapturesInTown"/>) the guard gets 240 Capture Target on the target, his queue cleared, then 112
    /// Find and put in cage queued behind it; otherwise his squad tags the target TEMP_ENEMY (<c>FUN_140674300</c> with 4, before the order, also
    /// when the order is refused), then 4 Attacking, his queue cleared, which the order check <c>FUN_1405d1940</c> refuses with no usable arm ("My
    /// arm is broken!"). The mates join through their own attack-on-sight goals on that tag, as anyone the tag makes an enemy.
    /// </summary>
    void OrderArrest(World world, int g, int t, bool capture)
    {
        var table = world.Characters;
        var gc = table.Cold(g)!;
        var guard = table.IdOf(g);
        var target = table.IdOf(t);
        if (capture)
        {
            PlayerOrders.Give(world, g, new OrderEntry(TaskType.LiftPersonSnatchingAllowedInTownOnly, this) { Target = target }, queued: false);
            PlayerOrders.Give(world, g, CageOrder(target), queued: true);
            return;
        }

        if (gc.SquadId >= 0) world.SystemOf<Dialogue.DialogueSystem>()?.Memory.Flag(world, gc.SquadId, target, Dialogue.MemoryTag.TempEnemy);
        if (gc.Medical is { } arms && !arms.ArmWorks(left: true) && !arms.ArmWorks(left: false))
        {
            world.Bark(guard, Loc.Tr("My arm is broken!"));
            return;
        }

        if (world.SystemOf<CombatSystem>() is { } combat)
            PlayerOrders.Give(world, g, new OrderEntry(TaskType.MeleeAttack, combat) { Target = target }, queued: false);

        int bounty = table.Cold(t)!.Bounties?.BookedAmount(Crimes.Factions.Relations, gc.Faction) ?? 0;
        Note(world, ArrestEventKind.Attack, guard, target, gc.Faction, bounty);
    }

    /// <summary>
    /// Order 112 Find and put in cage on <paramref name="body"/> (the body the guard carries stays carried through its start);
    /// <paramref name="enslave"/>: a code mod's <see cref="TakeOver"/>.
    /// </summary>
    OrderEntry CageOrder(CharacterId body, bool enslave = false) =>
        new(TaskType.FindCageAndPutIn, this) { Target = body, Detail = enslave ? Enslaving : null };

    /// <summary>
    /// The arrest orders' checks; 108 is the player's put in cage (<see cref="CanCage"/>). 240 and 213 need their target there and not caged (240
    /// also alive), else they leave the queue; a guard awake and not carried; and 240 its need 113 targetAtATown, which no row provides, so while the
    /// target stands in no town the order waits queued and the guard goes on with his package (with town data; docs/game/ai-tasks.md "Arrest and
    /// prison rows"). 112 needs a body carried and a free cage for it (its finder: none, the order leaves the queue).
    /// </summary>
    OrderCheck IOrderOwner.CanRun(World world, int slot, OrderEntry order)
    {
        var table = world.Characters;
        var c = table.Cold(slot)!;
        if (order.Row == TaskType.PutInCage) return CanCage(world, slot, order);
        if (Carry is null) return OrderCheck.Gone;

        if (order.Row == TaskType.FindCageAndPutIn)
        {
            if (c.Carrying.IsNone || GuardCage(world, slot, null) is null) return OrderCheck.Gone;
            return c.Medical is { Incapacitated: false } ? OrderCheck.Run : OrderCheck.Blocked();
        }

        bool capture = order.Row == TaskType.LiftPersonSnatchingAllowedInTownOnly;
        if (!table.TryResolveNext(order.Target, out int t) || t == slot || table.Cold(t) is not { IsImprisoned: false } tc) return OrderCheck.Gone;
        if (capture && tc.Medical is { Dead: true }) return OrderCheck.Gone;
        if (c.Medical is { Incapacitated: true } || !c.CarriedBy.IsNone) return OrderCheck.Blocked();

        return capture && !TargetInTown(world, slot, order) ? OrderCheck.Blocked() : OrderCheck.Run;
    }

    /// <summary>
    /// 240 is planned again on every update: its target gone into no town (113) ends its task and it waits queued.
    /// </summary>
    bool IOrderOwner.StillHolds(World world, int slot, OrderEntry order) =>
        order.Row != TaskType.LiftPersonSnatchingAllowedInTownOnly || TargetInTown(world, slot, order);

    /// <summary>
    /// 240's need 113 targetAtATown: the target's status town is set, or the guard carries it already; always true without town data.
    /// </summary>
    bool TargetInTown(World world, int slot, OrderEntry order)
    {
        var table = world.Characters;
        if (Towns is not { } towns || table.Cold(slot)!.Carrying == order.Target) return true;

        return table.TryResolveNext(order.Target, out int t) && !StatusTownNow(towns, world, t).IsNone;
    }

    /// <summary>
    /// Starts an arrest order (108: the player's caging, <see cref="StartPutInCage"/>): its act-out begins as an <see cref="Arrest"/>, which the slow
    /// world steps. 240 fights the target first (213, a hand-over of a body, has no fight: it lifts); 112 carries the body to the cage.
    /// </summary>
    void IOrderOwner.Start(World world, int slot, OrderEntry order)
    {
        if (order.Row == TaskType.PutInCage)
        {
            StartPutInCage(world, slot, order);
            return;
        }

        var table = world.Characters;
        var gc = table.Cold(slot)!;
        bool capture = order.Row == TaskType.LiftPersonSnatchingAllowedInTownOnly;
        var arrest = new Arrest
        {
            Guard = table.IdOf(slot), Target = order.Target, Faction = gc.Faction, Order = order, Enslave = ReferenceEquals(order.Detail, Enslaving),
        };
        _arrests.Add(arrest);
        if (!table.TryResolveNext(order.Target, out int t)) return;

        if (order.Row == TaskType.FindCageAndPutIn)
        {
            arrest.Phase = ArrestPhase.Walk;
            Note(world, ArrestEventKind.Lifted, arrest.Guard, arrest.Target, arrest.Faction, 0);
            ToCage(world, arrest, slot);
        }
        else if (capture) BeginFight(world, arrest, slot, t);
    }

    /// <summary>
    /// An arrest order goes on between the tasks of its plan (fight, strip, lift, the walks) while its arrest is there.
    /// </summary>
    bool IOrderSteps.Between(World world, int slot, OrderEntry order)
    {
        foreach (var a in _arrests) if (ReferenceEquals(a.Order, order)) return true;
        return false;
    }

    /// <summary>
    /// Puts the arrest in its fight: the guard attacks unless he already does, a player target gets the "is arresting" message once per target.
    /// </summary>
    void BeginFight(World world, Arrest arrest, int g, int t)
    {
        arrest.Phase = ArrestPhase.Fight;
        arrest.Time = 0;
        var table = world.Characters;
        if (table.Next[g].Task != (byte)CharacterTask.Attack) Attack(world, g, t);
        if (table.Cold(t)!.IsPlayer && !FoughtByOther(arrest))
        {
            string guardName = world.ShownName(table.Cold(g)!, ShownPlace.Notice);
            string targetName = world.ShownName(table.Cold(t)!, ShownPlace.Notice);
            world.Post(Loc.Tr("{1} is arresting {2}!", guardName, targetName));
        }
        Note(world, ArrestEventKind.Arrest, arrest.Guard, arrest.Target, arrest.Faction,
            table.Cold(t)!.Bounties?.BookedAmount(Crimes.Factions.Relations, arrest.Faction) ?? 0);
    }

    /// <summary>
    /// Another guard's arrest already fights this target (several guards arresting one character post one message, engine choice).
    /// </summary>
    bool FoughtByOther(Arrest self)
    {
        foreach (var a in _arrests) if (!ReferenceEquals(a, self) && a.Target == self.Target && a.Phase == ArrestPhase.Fight) return true;
        return false;
    }

    /// <summary>
    /// The guard attacks the target from the next tick as a step of his order (row 4, the order's provenance). The fight's start marks nothing for
    /// his squad (the melee and ranged attack tasks call no squad tag, <b>Verified</b>): his mates join only through their own goals (a protect
    /// goal once the target fights him, an attack-on-sight goal on a target their senses call an enemy).
    /// </summary>
    static void Attack(World world, int g, int t)
    {
        var table = world.Characters;
        var guard = new List<CharacterId> { table.IdOf(g) };
        world.Defer(new AttackOrder(guard, table.IdOf(t))
        {
            Tick = world.Tick + 1, Source = TaskSource.Order, Row = TaskType.MeleeAttack, Cause = "an arrest",
        });
    }

    /// <summary>
    /// One tick of an arrest; false when it is over. An ordered one is over once its order is no longer the guard's started front
    /// (<see cref="Leave"/>).
    /// </summary>
    bool Step(World world, Arrest a)
    {
        var table = world.Characters;
        float dt = world.TickSeconds;
        if (!table.TryResolveNext(a.Guard, out int g) || table.Cold(g) is not { Medical.Incapacitated: false } gc)
            return End(world, a, ArrestEventKind.GaveUp);
        // a body handed over (213) or caged (112) may be dead
        bool deadEnds = a.Order.Row == TaskType.LiftPersonSnatchingAllowedInTownOnly;
        if (!table.TryResolveNext(a.Target, out int t) || table.Cold(t) is not { } tc || deadEnds && tc.Medical is { Dead: true })
            return End(world, a, ArrestEventKind.GaveUp);
        if (!ReferenceEquals(gc.OrderQueue.Front, a.Order) || !a.Order.Started) return Leave(world, a, g, t, gc);

        if (tc.IsImprisoned && a.Phase != ArrestPhase.Walk) return End(world, a, ArrestEventKind.GaveUp);
        if (AwayFromHome(world, a, g, gc)) return RunHome(world, a, g, t, gc);

        a.Time += dt;
        ref var gn = ref table.Next[g];
        switch (a.Phase)
        {
            case ArrestPhase.Fight: return StepFight(world, a, g, t, gc, tc, ref gn, dt);
            case ArrestPhase.Lift: return StepLift(world, a, g, gc, ref gn);
            case ArrestPhase.Walk: return StepWalk(world, a, g, t);
        }
        return true;
    }

    /// <summary>
    /// The order an arrest acts out is no longer the guard's started front: done (240 or 213 once he carries the body, 112 at the cage), given up by
    /// the queue, replaced by another order, or waiting again (240's target in no town, <see cref="IOrderOwner.StillHolds"/>). The arrest goes; a
    /// waiting capture calls its fight off (<see cref="CallOff"/>). Always false.
    /// </summary>
    bool Leave(World world, Arrest a, int g, int t, CharacterCold gc)
    {
        if (ReferenceEquals(gc.OrderQueue.Front, a.Order)) CallOff(world, g, t);
        DropStripClip(gc);
        return false;
    }

    /// <summary>
    /// 240's need 95 isAtHomeTown (list B, <c>FUN_140997fe0</c>) fails until the guard carries the target: he stands in no town that is his faction's
    /// home (with town data).
    /// </summary>
    bool AwayFromHome(World world, Arrest a, int g, CharacterCold gc) =>
        a.Order is { Row: TaskType.LiftPersonSnatchingAllowedInTownOnly } && Towns is { } towns && gc.Carrying != a.Target
        && !AtHomeTown(towns, world, g, gc);

    /// <summary>
    /// The capture's plan out of a home town: 127 Retreating fast first, the only provider of 95 (docs/game/ai-tasks.md "Arrest and prison rows").
    /// The guard and the squad mates fighting the target with him leave that fight (those the target fights back keep theirs) and he runs to his
    /// squad's home under the order; back in a home town the capture goes on. A guard with no home to run to gives it up.
    /// </summary>
    bool RunHome(World world, Arrest a, int g, int t, CharacterCold gc)
    {
        var table = world.Characters;
        if (table.Next[g].Task == (byte)CharacterTask.GoTo && gc.Running is { Row: TaskType.RunAwayHometown }) return true;
        if (world.Squads.Find(gc.SquadId) is not { } squad) return End(world, a, ArrestEventKind.GaveUp);
        if (!CallOff(world, g, t)) return true;   // the target fights him: a fight of his own, with no leash

        DropStripClip(gc);
        a.Phase = ArrestPhase.Fight;
        a.Time = 0;
        TaskStart.Walk(world, g, squad.HomeCentre, SpeedMode.Run, TaskType.RunAwayHometown, TaskSource.Order);
        return true;
    }

    /// <summary>
    /// The character's status town as its next update would write it (<see cref="AiSystem.StatusTownAt"/>): the kept one is a spotting pass old.
    /// </summary>
    static CampaignTown StatusTownNow(PopulationData towns, World world, int slot) =>
        AiSystem.StatusTownAt(towns, world, world.Characters.Next[slot].Position, world.Characters.Cold(slot)!.StatusTown);

    /// <summary>
    /// State 95 isAtHomeTown (<c>FUN_140997fe0</c>, <b>Verified</b>): the character's status town is set and is its faction's or houses a squad of
    /// its faction (Town vfunc <c>0x2c0</c> <c>FUN_140952060</c>: the town's own faction, else its set of factions <c>+0xe8</c>, which the housing
    /// <c>FUN_14057f820</c> fills with each housed squad's faction). A player town is nobody else's home; a nest is its own faction's.
    /// </summary>
    static bool AtHomeTown(PopulationData towns, World world, int slot, CharacterCold c)
    {
        var town = StatusTownNow(towns, world, slot);
        if (town.Nest > 0) return town.Owner(world, towns) == c.Faction;

        int site = town.Site;
        if (site < 0) return false;
        if (site < towns.SiteFactions.Count && towns.SiteFactions[site] == c.Faction) return true;

        foreach (var squad in world.Squads.All)
            if (squad.Faction == c.Faction && squad.Town == site && squad.TownOwn) return true;

        return false;
    }

    /// <summary>
    /// The guard and the squad mates fighting the target with him leave that fight, but those the target itself fights: theirs is a fight of its
    /// own, which has no leash (docs/game/combat.md "Fight lifecycle and orders"). True when the guard is out of it.
    /// </summary>
    bool CallOff(World world, int g, int t)
    {
        if (world.SystemOf<CombatSystem>() is not { } combat) return true;

        var table = world.Characters;
        var guard = table.IdOf(g);
        var target = table.IdOf(t);
        bool free = true;
        foreach (var m in world.Squads.Find(table.Cold(g)!.SquadId)?.Members ?? [guard])
        {
            if (!Fights(combat, m, target)) continue;

            if (Fights(combat, target, m)) free &= m != guard;
            else combat.Disengage(table, m);
        }

        return free;
    }

    /// <summary>
    /// Whether <paramref name="who"/>'s combat target is <paramref name="whom"/>.
    /// </summary>
    static bool Fights(CombatSystem combat, CharacterId who, CharacterId whom) =>
        combat.TargetOf(who.Slot, who.Generation) == (whom.Slot, whom.Generation);

    /// <summary>
    /// The fight: once the target is down the guard lifts it; a fight that stopped with the target still up starts again, however far it is. The
    /// order stays queued and is planned again each update (<c>FUN_140510920</c> pops it only on its desire or its done test; a failed step pops
    /// the front only when it is the order's own row, <c>FUN_140506b10</c> comparing the task type, so the fight row 4 ending pops nothing,
    /// <b>Verified</b>), and its need 7 plans the fight again. Only the town needs (113, 95) or a new order stop it; with the Fixes option
    /// <see cref="ArrestSettings.LostFix"/> also a target the guard lost (<see cref="LostTarget"/>).
    /// </summary>
    bool StepFight(World world, Arrest a, int g, int t, CharacterCold gc, CharacterCold tc, ref CharacterHot gn, float dt)
    {
        if (tc.Medical is { Incapacitated: true }) return StepTargetDown(world, a, g, gc, dt);
        if (gn.Task == (byte)CharacterTask.Attack || gc.InCombat || a.Time < 1) return true;
        if (LostTarget(world, g, a.Target)) return End(world, a, ArrestEventKind.GaveUp);

        a.Time = 0;
        Attack(world, g, t);
        return true;
    }

    /// <summary>
    /// Whether the Fixes option <see cref="ArrestSettings.LostFix"/> ends the arrest of <paramref name="target"/> by the guard at <paramref name="g"/>:
    /// his senses hold no entry of it (<see cref="CharacterFacts.Knows"/>, the test that ended his fight, <c>CombatSystem.NothingLeft</c>). Meitou fix
    /// (#1312): the original plans the fight again at any distance, and with Meitou's end of a fight on a lost target the guard ran straight to a
    /// culprit who had slipped away unseen; now the order leaves the queue and he goes back to his routine.
    /// </summary>
    bool LostTarget(World world, int g, CharacterId target) => Settings.LostFix && !CharacterFacts.Knows(world, g, target);

    /// <summary>
    /// The target is down: the guard lifts it, weapons and all (the order's list B has no 118 TARGET_IS_UNARMED), giving up after 10 s of failed
    /// lifts. A guard whose left arm cannot carry gives up: the order's lift needs state 80 <c>isCarryingArmOK</c>, which no row provides
    /// (docs/game/ai-tasks.md "Arrest chain", <b>Verified</b>), so the original's plan fails there (#906).
    /// </summary>
    bool StepTargetDown(World world, Arrest a, int g, CharacterCold gc, float dt)
    {
        if (Carry is null) return End(world, a, ArrestEventKind.GaveUp);
        if (gc.Carrying == a.Target)
        {
            a.Phase = ArrestPhase.Lift;
            return true;
        }
        if (gc.InCombat) return true;   // still trading blows (the KO tick, a squad mate): wait
        if (!CanLift(gc)) return End(world, a, ArrestEventKind.GaveUp);

        // Row 240 Capture Target is the chain's lift (snatch byte set), 213 a hand-over's: it takes a body another carries too, with no cage free
        // as well (neither its list B nor its desire asks for one). A body his own squad carries ends the order by its desire (OrderDesire), as
        // a lift the Fixes option holds back does (LiftHeld): the guard waits for that pop rather than lift first.
        bool held = LiftHeld(world, g, a.Order);
        if (!held && Carry.BeginPickUp(world, g, a.Target, source: TaskSource.Order, row: a.Order.Row))
        {
            a.Phase = ArrestPhase.Lift;
            a.Waited = 0;
            return true;
        }
        a.Waited += dt;
        return a.Waited <= 10 || End(world, a, ArrestEventKind.GaveUp);
    }

    /// <summary>
    /// Whether the guard's left arm can carry (state 80 <c>isCarryingArmOK</c>, <see cref="Carrying.CarrySystem.LeftArmUsable"/>).
    /// </summary>
    internal static bool CanLift(CharacterCold guard) => guard.Medical is { } m && Carrying.CarrySystem.LeftArmUsable(m);

    /// <summary>
    /// Whether the Fixes option <see cref="ArrestSettings.LiftFix"/> holds back the lift of the arrest order <paramref name="order"/> (240, 213) of
    /// the guard at <paramref name="g"/>: its target is down and another character of his faction carries it, or no cage is free for it
    /// (<see cref="GuardCage"/>). Meitou fix (#339): the original's lift asks neither, so guards at a full station pass the same body round. Its
    /// desire is then 0 (<see cref="Ai.OrderDesire"/>). A pure read but for the lazy duty lookup (serial phases).
    /// </summary>
    internal bool LiftHeld(World world, int g, OrderEntry order)
    {
        var table = world.Characters;
        if (!Settings.LiftFix || !table.TryResolveNext(order.Target, out int t)) return false;
        if (table.Cold(t) is not { Medical.Incapacitated: true } tc) return false;

        var gc = table.Cold(g)!;
        return CarriedByAlly(world, gc, tc) || GuardCage(world, g, ArrestOf(order)) is null;
    }

    /// <summary>
    /// Whether <paramref name="target"/> is carried by another character of the guard's faction.
    /// </summary>
    static bool CarriedByAlly(World world, CharacterCold guard, CharacterCold target) =>
        world.Characters.TryResolveNext(target.CarriedBy, out int carrier) && world.Characters.Cold(carrier) is { } c && c != guard
            && c.Faction == guard.Faction;

    /// <summary>
    /// The arrest acting out <paramref name="order"/>; null before it starts.
    /// </summary>
    Arrest? ArrestOf(OrderEntry order) => _arrests.Find(a => ReferenceEquals(a.Order, order));

    /// <summary>
    /// The lift: once the guard carries the target his 112 takes it to the cage (the queue drops the lift order once he carries, its desire 0); a
    /// lift that failed 5 times gives up, else the fight resumes.
    /// </summary>
    bool StepLift(World world, Arrest a, int g, CharacterCold gc, ref CharacterHot gn)
    {
        if (gc.Carrying == a.Target || gn.Task == (byte)CharacterTask.PickUp) return true;
        if (++a.Tries > 5) return End(world, a, ArrestEventKind.GaveUp);
        a.Phase = ArrestPhase.Fight;
        a.Time = 0;
        return true;
    }

    /// <summary>
    /// The cage errand (112 / 108): the guard walks onto the cage's floor (a cage upstairs is reached by the stairs, not stood under:
    /// <see cref="FixtureRegistry.AtCage"/> tests the height) and locks the captive up there. A walk that ends short is a lost walk, as the original's
    /// plan step answers any running plan's idle mover away from its target (<c>FUN_14050d970</c>, <b>Verified</b>): the cage is ignored for 20 s and
    /// the finder picks another, none ending the order with the body on the shoulder. A 112 whose body left the shoulder is over (its desire is 0
    /// with nothing carried).
    /// </summary>
    bool StepWalk(World world, Arrest a, int g, int t)
    {
        var table = world.Characters;
        var gc = table.Cold(g)!;
        ref var gn = ref table.Next[g];
        if (gc.Carrying != a.Target) return End(world, a, ArrestEventKind.GaveUp);

        var cage = a.Cage is { } id ? world.Fixtures.Find(id) : null;
        if (cage is null || !FixtureRegistry.IsFree(table, cage)) return ToCage(world, a, g);

        bool walking = gn.Task == (byte)CharacterTask.GoTo;
        if (world.Fixtures.AtCage(cage, gn.Position, walking, world.Walkability)) return LockUp(world, a, g, t, cage);
        if (walking) return true;

        // A lost walk (FUN_14050d970): the cage goes into the guard's ignore map for 20 s and the finder picks again.
        gc.Ignore(CageKey(cage), world.Tick + (long)MathF.Ceiling(Construction.ConstructionSystem.UnreachableSeconds / world.TickSeconds));
        return ToCage(world, a, g);
    }

    /// <summary>
    /// The ignore-map key of a cage.
    /// </summary>
    static IgnoreKey CageKey(Fixture cage) => new(cage.Id, default);

    /// <summary>
    /// At the cage: the guard puts the captive down into it with its sentence, as 112's put-in row 108 does for any carrier (<c>FUN_140358670</c>,
    /// <b>Verified</b>): the captive's squad remembers him as captor, the relation event, then the bounty claim.
    /// </summary>
    bool LockUp(World world, Arrest a, int g, int t, Fixture cage)
    {
        var table = world.Characters;
        var tc = table.Cold(t)!;
        TaskStart.Stand(table, g);
        Carry!.LetGo(world, g);
        if (!world.Fixtures.Imprison(world, a.Target, cage, constants.PrisonTime, a.Guard)) return ToCage(world, a, g);

        Deeds?.Caged(world, g, t);
        Claim(world, g, a.Target, cage);
        tc.Recognition = 0;
        if (a.Enslave) FixtureRegistry.Enslave(world, tc, a.Faction, table.Cold(g)?.SquadId ?? -1);
        else if (tc.IsPlayer)
        {
            string by = world.ShownFaction(a.Faction, Crimes.Factions.NameOf(a.Faction));
            world.Post(Loc.Tr("{1} has been imprisoned by {2}", world.ShownName(tc, ShownPlace.Notice), by));
        }
        return End(world, a, ArrestEventKind.Jailed);
    }

    /// <summary>
    /// One tick of row 181 Stripping weapons (needs 38 one working arm, at location, 7 target down; <c>Task_StripWeapons</c>), a step of goal 204's
    /// plan (<see cref="ArrestGoals"/>, its 203 needing 118), its walks of <paramref name="source"/>: the guard walks up to within
    /// <see cref="Kidnap.CaptiveSettings.Reach"/>, plays <c>medic</c> for the strip's seconds (<paramref name="time"/>, the kneel's so far), and the
    /// target's weapon sections go to him (<see cref="Kidnap.CaptiveSystem.StripWeapons"/>).
    /// </summary>
    internal StripStep Strip(World world, int g, int t, float time, TaskSource source)
    {
        var table = world.Characters;
        var gc = table.Cold(g)!;
        var tc = table.Cold(t)!;
        if (!CharacterFacts.TargetDown(tc) || !tc.CarriedBy.IsNone || Captives is null)
        {
            DropStripClip(gc);
            return StripStep.TargetUp;
        }

        if (gc.Medical is { } arms && !arms.ArmWorks(left: true) && !arms.ArmWorks(left: false)) return StripStep.GaveUp;
        if (gc.InCombat) return StripStep.Wait;
        if (!InStripReach(world, g, t))
        {
            WalkUp(world, g, t, source);
            return StripStep.Walk;
        }

        return Kneel(world, g, t, gc, tc, time);
    }

    /// <summary>
    /// Whether the guard stands within the strip's reach of the target (<see cref="Kidnap.CaptiveSettings.Reach"/>), on this tick's places or, for
    /// the parallel think, the last tick's (<paramref name="previous"/>).
    /// </summary>
    internal bool InStripReach(World world, int g, int t, bool previous = false)
    {
        var places = previous ? world.Characters.Previous : world.Characters.Next;
        var to = new Vector2(places[t].Position.X, places[t].Position.Z);
        var here = new Vector2(places[g].Position.X, places[g].Position.Z);
        return Captives is { } captives && Vector2.Distance(here, to) <= captives.Settings.Reach;
    }

    /// <summary>
    /// The walk up to the target, every 15 ticks while the guard stands or its walk's goal lies more than 20 off the target.
    /// </summary>
    static void WalkUp(World world, int g, int t, TaskSource source)
    {
        var table = world.Characters;
        ref var gn = ref table.Next[g];
        var to = new Vector2(table.Next[t].Position.X, table.Next[t].Position.Z);
        if ((world.Tick + g) % 15 == 0 && (gn.Task != (byte)CharacterTask.GoTo || Vector2.DistanceSquared(to, gn.Goal) > 20 * 20))
            TaskStart.Walk(world, g, to, SpeedMode.Free, TaskType.StripTargetsWeapons, source);
    }

    /// <summary>
    /// In reach: the guard stands and kneels, and once the strip's seconds are over the target's weapon sections go to him.
    /// </summary>
    StripStep Kneel(World world, int g, int t, CharacterCold gc, CharacterCold tc, float time)
    {
        if (gc.ActionClip != Kidnap.CaptiveSystem.ProcessClip)
        {
            TaskStart.Stand(world.Characters, g);
            gc.ActionClip = Kidnap.CaptiveSystem.ProcessClip;
            return StripStep.Kneel;
        }

        if (time < Captives!.Settings.StripSeconds) return StripStep.Wait;

        gc.ActionClip = null;
        Captives.StripWeapons(world, t, gc, tc);
        return StripStep.Stripped;
    }

    /// <summary>
    /// Chooses the cage (the original's finder; other arrests' cages are reserved) and walks there; none: the captive is dropped.
    /// </summary>
    bool ToCage(World world, Arrest a, int g)
    {
        var cage = GuardCage(world, g, a);
        if (cage is null) return Drop(world, a, g);
        a.Cage = cage.Id;
        TaskStart.Walk(world, g, cage.Position, SpeedMode.Free, TaskType.FindCageAndPutIn, TaskSource.Order);
        return true;
    }

    /// <summary>
    /// The guard's cage (<c>FUN_1408d0770</c> over the gatherer <c>FUN_1408cbea0</c>, economy.md 11.7): the best free one among his town candidates
    /// (<see cref="FixtureRegistry.CageCandidates"/>), not one in his ignore map (a lost walk's), the cages of his squad mates' arrests other than
    /// <paramref name="self"/> and town errands (112 / 281 goals) reserved; every cage without town data. The reservation is the squad's work count
    /// (<c>FUN_1402690e0</c> on the asker's own platoon blackboard, <c>FUN_140507070</c>), so another squad's carrier may head for the same cage.
    /// </summary>
    internal Fixture? GuardCage(World world, int g, Arrest? self)
    {
        var table = world.Characters;
        var gc = table.Cold(g)!;
        var at = table.Next[g].Position;
        var candidate = Towns is { } data ? world.Fixtures.CageCandidates(world, data, gc, at) : null;

        // His squad's own home, not the bar a bar package has it sitting in: the finder reads the ownerships' home (economy.md 11.7).
        string? home = FixtureRegistry.HomeOf(world, gc);

        var guard = table.IdOf(g);
        _townDuties ??= world.SystemOf<Kidnap.TownDutySystem>();
        return world.Fixtures.FindCage(world, gc, at, home, f => candidate?.Invoke(f) == false || Reserved(world, f, self, gc.SquadId)
            || gc.IsIgnored(CageKey(f), world.Tick) || _townDuties?.CageTaken(world, f.Id, guard, gc.SquadId) == true);
    }

    /// <summary>
    /// Whether an arrest order of a member of <paramref name="squad"/> takes a body to the cage <paramref name="cage"/> (the town errands' cage
    /// finder skips it).
    /// </summary>
    internal bool CageReserved(World world, string cage, int squad)
    {
        foreach (var a in _arrests) if (a.Cage == cage && InSquad(world, a.Guard, squad)) return true;
        return false;
    }

    /// <summary>
    /// Whether another arrest than <paramref name="self"/> has taken cage <paramref name="f"/>: any guard's, or with <paramref name="squad"/> only a
    /// member's of that squad.
    /// </summary>
    bool Reserved(World world, Fixture f, Arrest? self, int? squad = null)
    {
        foreach (var a in _arrests)
            if (!ReferenceEquals(a, self) && a.Cage == f.Id && (squad is not { } s || InSquad(world, a.Guard, s))) return true;
        return false;
    }

    /// <summary>
    /// Whether the character <paramref name="id"/> is alive in the squad <paramref name="squad"/> (squadless characters share -1).
    /// </summary>
    internal static bool InSquad(World world, CharacterId id, int squad) =>
        world.Characters.TryResolveNext(id, out int s) && world.Characters.Cold(s)?.SquadId == squad;

    /// <summary>
    /// No free cage: the guard stands and the arrest ends as dropped. He keeps the captive, as his 112 leaves the queue when its finder finds no
    /// cage and his own goals deal with what he carries.
    /// </summary>
    bool Drop(World world, Arrest a, int g)
    {
        TaskStart.Stand(world.Characters, g);
        return End(world, a, ArrestEventKind.Dropped);
    }

    /// <summary>
    /// Ends an arrest: the guard goes back to his routine (the queue drops the order, whose act-out is over).
    /// </summary>
    bool End(World world, Arrest a, ArrestEventKind kind)
    {
        var table = world.Characters;
        if (table.TryResolveNext(a.Guard, out int g) && table.Cold(g) is { } gc)
        {
            DropStripClip(gc);
            if (table.Next[g].Task == (byte)CharacterTask.GoTo) TaskStart.Stand(table, g);
        }
        Note(world, kind, a.Guard, a.Target, a.Faction, 0);
        return false;
    }

    /// <summary>
    /// Takes the strip's kneel off a guard leaving it unfinished (the target died, got up or was taken, the guard ran home or got another order): an
    /// idle character plays its action clip, so a kept one had him kneel on by the body under his own goal's text (#698).
    /// </summary>
    internal static void DropStripClip(CharacterCold gc)
    {
        if (gc.ActionClip == Kidnap.CaptiveSystem.ProcessClip) gc.ActionClip = null;
    }

    /// <summary>
    /// Logs an arrest event when <see cref="ArrestSettings.RecordLog"/> is on.
    /// </summary>
    internal void Note(World world, ArrestEventKind kind, CharacterId guard, CharacterId target, int faction, int amount)
    {
        if (Settings.RecordLog) _log.Add(new ArrestEvent(world.Tick, kind, guard, target, faction, amount));
        string by = EventLog.Who(world, guard), whom = EventLog.Who(world, target), of = EventLog.FactionName(world, faction);
        world.Events.Line(world, "arrest", $"{kind}: {by} ({of}) and {whom}{AmountText(kind, amount)}");
    }

    /// <summary>
    /// The event log's reading of an arrest event's amount: a bail is money, a spotting's is a dialogue event (left out), else the bounty.
    /// </summary>
    static string AmountText(ArrestEventKind kind, int amount) => kind switch
    {
        ArrestEventKind.Spotted => "",
        ArrestEventKind.Bailed => $", c.{amount}",
        _ => $", bounty {amount}",
    };

    /// <summary>
    /// Adds the bounty, disguise, spotting, goal 204 and arrest state to the world hash; the spottings, goal records and arrests only while there are
    /// any.
    /// </summary>
    public void Hash(ref StateHasher hasher)
    {
        HashBounties(ref hasher);
        HashDisguise(ref hasher);
        if (_spottings.Count > 0)
        {
            hasher.Add(0x53706F74);
            hasher.Add(_spottings.Count);
            foreach (var p in _spottings) p.Hash(ref hasher);
        }

        _goals?.Hash(ref hasher);
        if (_arrests.Count == 0) return;   // only while an arrest runs, so worlds without them hash as before
        hasher.Add(0x41727273);
        hasher.Add(_arrests.Count);
        foreach (var a in _arrests) a.Hash(ref hasher);
    }

    #endregion
}
