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
    /// Game seconds of being seen that fill the recognition meter (<b>engine</b>: the original runs the per-looker spot meter with a = 5 + 40 k,
    /// docs/game/perception.md "Disguise", which bounties do not use here; read as 45 s). A booked or witnessed crime skips it
    /// (<see cref="CharacterCold.RecentlyWanted"/>).
    /// </summary>
    public float RecognitionSeconds { get; init; } = 45;
    /// <summary>
    /// A guard fining a character talks within this distance; the fine lapses into an arrest when the target gets farther than
    /// <see cref="FineLeash"/> or after <see cref="FineSeconds"/>.
    /// </summary>
    public float TalkRange { get; init; } = 40;
    /// <summary>
    /// A fining guard's target farther than this turns the fine into an arrest.
    /// </summary>
    public float FineLeash { get; init; } = 800;
    /// <summary>
    /// Game seconds a guard waits on a fine before it becomes an arrest.
    /// </summary>
    public float FineSeconds { get; init; } = 60;
    /// <summary>
    /// A guard counts as in his own town within this multiple of his resident squad's home radius of its centre (<b>engine reading</b> of the
    /// original's status <c>+0x88</c> test).
    /// </summary>
    public float TownSlack { get; init; } = 2;
    /// <summary>
    /// Walks to the cage before the carrier gives up and drops the captive.
    /// </summary>
    public int CageWalks { get; init; } = 3;
    /// <summary>
    /// A player character within this of the cage pays a bail (the original pays at a law NPC's dialogue in that town).
    /// </summary>
    public float BailRange { get; init; } = 500;
    /// <summary>
    /// The player's "Put in Cage" takes the best free cage within this distance of the carrier.
    /// </summary>
    public float CageSearchRange { get; init; } = AggroSystem.OutdoorSight;
    /// <summary>
    /// A bounty hunter stops chasing a target farther than this; after a hunt he leaves that target alone for <see cref="HuntRestSeconds"/> game
    /// seconds.
    /// </summary>
    public float HuntLeash { get; init; } = 3000;
    /// <summary>
    /// Game seconds a hunter leaves a target alone after hunting it.
    /// </summary>
    public float HuntRestSeconds { get; init; } = 600;
    /// <summary>
    /// After a guard fires event 44 or 45 at a target, nobody fires again at it for this many game seconds, longer while the guard is still saying or
    /// walking up to say it.
    /// </summary>
    public float SpotRetrySeconds { get; init; } = 60;
    /// <summary>
    /// Keep a <see cref="ArrestSystem.Log"/> (tests).
    /// </summary>
    public bool RecordLog { get; init; }
}

/// <summary>
/// Where an arrest is: the guard walking up to fine, fighting until the target is down, lifting it, carrying it to a cage.
/// </summary>
public enum ArrestPhase : byte
{
    /// <summary>
    /// The guard walks up to take a fine (a bounty below <see cref="ArrestSystem.FineLimit"/>).
    /// </summary>
    Fine,
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
    /// <summary>
    /// Row 181 Stripping weapons between the fight (7) and the lift (240); appended so the earlier numbers keep their meaning.
    /// </summary>
    Strip,
}

/// <summary>
/// One guard's arrest of one character (serial state of the <see cref="ArrestSystem"/>).
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
    /// Game seconds in the phase (the fine's patience, the wait between attack orders, the lift's retries).
    /// </summary>
    public float Time { get; set; }
    /// <summary>
    /// The fine to take in the <see cref="ArrestPhase.Fine"/> phase (<see cref="ArrestSystem.FineFor"/>), in cats.
    /// </summary>
    public int Fine { get; set; }
    /// <summary>
    /// Failed lifts (5 end the arrest), or walks to the cage (up to <see cref="ArrestSettings.CageWalks"/>).
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
    /// A slaver's take-over (<c>DA_ENSLAVE_TARGETS_CARRIED_PERSON</c>): the captive becomes the guard faction's slave when caged.
    /// </summary>
    public bool Enslave { get; init; }
    /// <summary>
    /// A dialogue's hand-over (DA 30 / 40): the guard takes a body over, so the planner chain's strip (181) is not part of it.
    /// </summary>
    public bool Takeover { get; init; }
    /// <summary>
    /// Row 181 ran: once per arrest (a locked weapon item stays on the target, which the strip does not move).
    /// </summary>
    public bool Stripped { get; set; }

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
        h.Add(Fine);
        h.Add(Tries);
        h.Add(Waited);
        h.Add(Cage is { } c ? Rng.StableHash(c) : 0UL);
        if (Enslave) h.Add(0x536C6176);
        if (Takeover) h.Add(0x54616B65);
        if (Stripped) h.Add(0x53747270);
    }
}

/// <summary>
/// What the arrest system did, for its <see cref="ArrestSystem.Log"/>.
/// </summary>
public enum ArrestEventKind
{
    /// <summary>
    /// A guard attacks instead of arresting (outside his town, or an animal target).
    /// </summary>
    Attack,
    /// <summary>
    /// A guard went to take a fine.
    /// </summary>
    Fine,
    /// <summary>
    /// The fine was paid and the bounty cleared.
    /// </summary>
    Paid,
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
    /// No free cage: the guard dropped the captive.
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
    /// A bounty hunter began a hunt.
    /// </summary>
    Hunt,
    /// <summary>
    /// A hunt ended.
    /// </summary>
    HuntOver,
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
/// <item><b>Fine or arrest</b> (11.7; the engine's fallback for a world without dialogue events): outside his town the guard just attacks, with his
/// squad. In it, a bounty of 1..1049 is a fine (<see cref="FineFor"/>): he walks up and takes it when the player's money is more than it, clearing
/// his faction's entry; otherwise, and for a bigger bounty, he arrests: fights until the target is down, lifts it
/// (<see cref="Carrying.CarrySystem"/>), carries it to the best free cage (<see cref="FixtureRegistry.FindCage"/>) and imprisons it with its
/// sentence. No free cage: he drops the captive (engine choice; the original's is Unknown).</item>
/// <item><b>Release</b> (11.5, goal 211): a player character whose sentence is served and who is awake is freed by the jail's faction (an NPC of it
/// near the cage that holds goal 211, else the system in its name for a faction that books bounties; engine choice), which erases its and the jail's
/// bounty entries. NPC prisoners stay (their jailers' AI is not built).</item>
/// <item><b>Bail, claims, hunters</b> (11.4, 11.5, 11.8; ArrestSystem.Bounties.cs): <see cref="PayBailOrder"/>, the player's
/// <see cref="PutInCageOrder"/> with its reward
/// (<see cref="Claim"/>), and bounty hunters that chase a recognised bounty above <see cref="HuntLimit"/> until the target is down.</item>
/// </list>
/// </summary>
public sealed partial class ArrestSystem(CrimeSystem crimes, GameConstants constants, ArrestSettings? settings = null) : ITickSystem, IStateHashed,
    ITaskOwner, ITaskHandler
{
    /// <summary>
    /// Bounties below this are fined (the "Small bounty" dialogue's <c>0 &lt; b &lt; 1050</c>, <b>Verified</b> by probe).
    /// </summary>
    public const int FineLimit = 1050;

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
    /// The dialogue spotting fires events 44 and 45 through; null, or its event lines off (<see cref="Dialogue.DialogueSettings.AmbientEvents"/>):
    /// the engine's fine or arrest instead.
    /// </summary>
    public Dialogue.DialogueSystem? Talk { get; set; }
    /// <summary>
    /// A dialogue with its event lines on takes the spotting (the engine's fine or arrest otherwise).
    /// </summary>
    bool Talks => Talk is { Settings.AmbientEvents: true };
    /// <summary>
    /// The arrest log (empty unless <see cref="ArrestSettings.RecordLog"/>).
    /// </summary>
    public IReadOnlyList<ArrestEvent> Log => _log;

    /// <summary>
    /// The fine of the "Bounty extortion pay" dialogue (<b>Verified</b> by probe of the base load order): 100 more for every started 100 above 250,
    /// from 400; 300 up to 250 (<b>engine</b>: the data's 300 line tests the speaker's bounty and never shows) (251-350: 400 ... 951-1050: 1100).
    /// </summary>
    public static int FineFor(int bounty) => bounty <= 250 ? 300 : 300 + 100 * (int)Math.Ceiling((bounty - 250) / 100.0);

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
    /// The slow-world step: books the crime system's new bookings, steps the spottings; every <see cref="ArrestSettings.SpotEveryTicks"/> the
    /// disguise, the spotting and the status-town passes; then the bounty duty, the arrests, the cagings and the hunts, each dropping the
    /// finished ones.
    /// </summary>
    public void SlowWorld(World world)
    {
        foreach (var b in Crimes.BookingsOf(world.Tick)) Booked(world, b);
        for (int k = 0; k < _spottings.Count;)
        {
            if (StepSpotting(world, _spottings[k])) k++;
            else _spottings.RemoveAt(k);
        }
        if (world.Tick % Math.Max(1, Settings.SpotEveryTicks) == 0)
        {
            Disguise(world, Math.Max(1, Settings.SpotEveryTicks) * world.TickSeconds);
            Spot(world, Math.Max(1, Settings.SpotEveryTicks) * world.TickSeconds);
            UpdateStatusTowns(world);
            DropFreedOutsideTowns(world);
            EscapeFarFromOwner(world);
        }
        BountyDuty(world);
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
        for (int k = 0; k < _hunts.Count;)
        {
            if (StepHunt(world, _hunts[k])) k++;
            else _hunts.RemoveAt(k);
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
    /// A freed character stops being freed once it is out of every town: the status update <c>FUN_1405a4f50</c> clears status <c>+0xea</c> on each
    /// update while the status object's current town (<c>+0x88</c>, <see cref="CharacterCold.StatusTown"/>) is null (<b>Verified</b>). Without town
    /// data the flag stays.
    /// </summary>
    void DropFreedOutsideTowns(World world)
    {
        if (Towns is null) return;
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
            if (table.Next[s].Alive && table.Cold(s) is { Freed: true } c && c.StatusTown.IsNone) c.Freed = false;
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
    /// too (<c>FUN_1404fe6e0</c> rejects a null handle); a master squad or saved owner still waiting to load is kept.
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
                if (c.SlaveMaster < 0 && c.Save is not { Slaver.IsNull: false }) FixtureRegistry.TagEscaped(world, c);

                continue;
            }

            if (owner.Slot == s || !c.StatusTown.IsNone) continue;

            var at = table.Next[s].Position;
            if (Vector3.DistanceSquared(at, table.Next[owner.Slot].Position) > EscapeDistanceSquared) FixtureRegistry.TagEscaped(world, c);
        }
    }

    /// <summary>
    /// Whether the character is a guard or target of an arrest, or the hunter of a hunt that is not resting.
    /// </summary>
    bool Busy(CharacterId id)
    {
        foreach (var a in _arrests) if (a.Guard == id || a.Target == id) return true;
        foreach (var h in _hunts) if (h.Rest <= 0 && h.Hunter == id) return true;
        return false;
    }

    /// <summary>
    /// A police-like NPC free to start an arrest: awake, not fighting, fleeing, carrying, held or treating, not in another arrest.
    /// </summary>
    bool FreeGuard(World world, int slot)
    {
        var table = world.Characters;
        if (!table.Next[slot].Alive || table.Next[slot].Task is (byte)CharacterTask.Attack or (byte)CharacterTask.Treat) return false;
        return table.Cold(slot) is { IsPlayer: false, InCombat: false, Fleeing: false, IsImprisoned: false, Medical.Incapacitated: false,
            Carrying.IsNone: true, CarriedBy.IsNone: true, Fighter.CanAttack: true } c
            && Crimes.IsPoliceLike(world, c) && !Busy(table.IdOf(slot));
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
        Start(world, g, t, force: fighting);
    }

    #endregion

    #region Spotting

    /// <summary>
    /// The spotting pass over the suspects in slot order: slave catching, the recognition meter from the lookers that see the target, then event
    /// 44/45 or the engine's arrest, and a hunt for a seen bounty.
    /// </summary>
    void Spot(World world, float seconds)
    {
        var table = world.Characters;
        var next = table.Next;
        float range2 = Settings.SpotRange * Settings.SpotRange;
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
            bool sneaking = (c.Orders & CharacterOrders.Sneak) != 0;
            float stealth = sneaking ? Orders.Stealth(c) : 0;
            bool seen = false;
            int best = -1, escapeGuard = -1, hunter = -1;
            float bestD2 = float.MaxValue, escapeD2 = float.MaxValue, hunterD2 = float.MaxValue;
            var target = table.IdOf(s);
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
                // A working disguise hides the bounty: only a looker that sees through it recognises it (Disguise; disguiseMods' bounty branch needs
                // no disguise level).
                bool spots = bounty > 0 && bounty >= SpotThreshold(wc.NpcClass) && SeenFaction(c) == c.Faction;
                bool escapee = c.Escaped && Crimes.IsPoliceLike(world, wc) && InOwnTown(world, w);
                bool hunts = HuntsFor(wc, c);
                if (!spots && !escapee && !hunts) continue;
                float v = world.Sight.Sees(w, s, next[w].Position, at, Settings.SpotRange, sneaking ? stealth : -1);
                if (v <= 0) continue;
                if (escapee && FreeGuard(world, w) && d2 < escapeD2) (escapeGuard, escapeD2) = (w, d2);
                if (hunts)
                {
                    seen = true;
                    if (d2 < hunterD2 && FreeHunter(world, w, target)) (hunter, hunterD2) = (w, d2);
                }
                if (!spots) continue;
                seen = true;
                if (d2 < bestD2 && FreeGuard(world, w)) (best, bestD2) = (w, d2);
            }
            if (escapeGuard >= 0)
            {
                if (Talks) Spotted(world, escapeGuard, s, EscapedPrisonerSpotted);
                else Start(world, escapeGuard, s, force: true);
                continue;
            }
            float step = seconds / Math.Max(Settings.RecognitionSeconds, 1e-3f);
            c.Recognition = seen ? MathF.Min(1, c.Recognition + step) : MathF.Max(0, c.Recognition - step);
            bool recognised = seen && (c.RecentlyWanted > 0 || c.Recognition >= 1);
            if (recognised && best >= 0)
            {
                if (Talks) Spotted(world, best, s, BountySpotted);
                else Start(world, best, s, force: false);
            }
            if (recognised && hunter >= 0 && hunter != best) BeginHunt(world, hunter, s);
        }
    }

    /// <summary>
    /// EV_BOUNTY_SPOTTED, EV_ESCAPED_PRISONER_SPOTTED, EV_ESCAPING_SLAVE_SPOTTED.
    /// </summary>
    public const int BountySpotted = 44, EscapedPrisonerSpotted = 45, EscapingSlaveSpotted = 36;

    /// <summary>
    /// Slave catching (the packages' Capture Slaves, goal 167; <b>engine</b> rules, the original's finder and escape test are <b>Unknown</b>,
    /// docs/simulation.md "Arrest and jail"): an awake NPC of the slave's owner faction in its own town that sees the slave (its daylight sight test,
    /// full range) tags a slave (state 1) as escaping when it wears no locked shackles or stands outside that NPC's town; the nearest such
    /// police-like or slaver NPC then captures an escaping slave (state 2): it fights it down and cages it, and the caging makes it the faction's
    /// slave again. True when a capture started (the slave is then the arrest's, not the bounty spotting's).
    /// </summary>
    bool CatchSlave(World world, int s, CharacterCold c)
    {
        if (c.SlaveOwner < 0) return false;
        var table = world.Characters;
        var next = table.Next;
        var at = next[s].Position;
        bool sneaking = (c.Orders & CharacterOrders.Sneak) != 0;
        float stealth = sneaking ? Orders.Stealth(c) : 0;
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
            float v = world.Sight.Sees(w, s, next[w].Position, at, range, sneaking ? stealth : -1);
            if (v <= 0) continue;
            if (c.SlaveState == 1 && (LockSystem.WornShackles(c) is null || !InTownOf(world, w, at))) FixtureRegistry.TagEscaped(world, c);
            if (c.SlaveState == 2 && d2 < catcherD2
                && (Crimes.IsPoliceLike(world, wc) || wc.NpcClass == FundamentalType.Slaver) && FreeGuard(world, w))
            {
                (catcher, catcherD2) = (w, d2);
            }
        }
        if (catcher < 0) return false;
        var gc = table.Cold(catcher)!;
        var arrest = new Arrest { Guard = table.IdOf(catcher), Target = table.IdOf(s), Faction = gc.Faction, Enslave = true };
        _arrests.Add(arrest);
        gc.Engaged = true;
        BeginFight(world, arrest, catcher, s);
        if (Talks) Talk!.FireEvent(world, arrest.Guard, arrest.Target, EscapingSlaveSpotted);
        return true;
    }

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
    /// target is left to it for <see cref="ArrestSettings.SpotRetrySeconds"/> (engine: the original's recognition fires once; the data's own DC 18
    /// keeps the bounty lines from repeating within 2 hours).
    /// </summary>
    void Spotted(World world, int g, int t, int ev)
    {
        var table = world.Characters;
        var guard = table.IdOf(g);
        var target = table.IdOf(t);
        if (Talk!.FireEvent(world, guard, target, ev)) Note(world, ArrestEventKind.Spotted, guard, target, table.Cold(g)!.Faction, ev);
        if (!Busy(target))
        {
            _spottings.Add(new Spotting { Guard = guard, Target = target, Event = ev,
                Until = world.Tick + (long)MathF.Ceiling(Settings.SpotRetrySeconds / world.TickSeconds) });
        }
    }

    /// <summary>
    /// One tick of a spotting; false when it is over: an arrest took the target, the bounty is paid (DA 49), or the time is up while the guard no
    /// longer talks.
    /// </summary>
    bool StepSpotting(World world, Spotting p)
    {
        if (Busy(p.Target) || !world.Characters.TryResolveNext(p.Target, out int t) || world.Characters.Cold(t) is not { } tc) return false;
        if (p.Event == BountySpotted && tc.Bounties is not { Any: true }) return false;
        // Still talking, or held by the dialogue (walking up to talk: DA 2).
        if (Talk?.Engages(p.Guard) == true || world.Characters.TryResolveNext(p.Guard, out int g) && world.Characters.Cold(g) is { Engaged: true })
            p.Until = Math.Max(p.Until, world.Tick + 1);
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

    #region Fine or arrest

    /// <summary>
    /// <paramref name="capture"/> false: the guard attacks instead of arresting, in his own town too (a dialogue's animal target).
    /// </summary>
    void Start(World world, int g, int t, bool force, bool capture = true)
    {
        var table = world.Characters;
        var gc = table.Cold(g)!;
        var tc = table.Cold(t)!;
        var guard = table.IdOf(g);
        var target = table.IdOf(t);
        int bounty = tc.Bounties?.BookedAmount(Crimes.Factions.Relations, gc.Faction) ?? 0;
        if (!capture || !InOwnTown(world, g))
        {
            Attack(world, g, t);
            Note(world, ArrestEventKind.Attack, guard, target, gc.Faction, bounty);
            return;
        }
        var arrest = new Arrest { Guard = guard, Target = target, Faction = gc.Faction };
        _arrests.Add(arrest);
        gc.Engaged = true;   // the AI leaves him alone until the arrest ends
        if (!force && bounty is > 0 and < FineLimit)
        {
            arrest.Phase = ArrestPhase.Fine;
            arrest.Fine = FineFor(bounty);
            TaskStart.Walk(world, g, new Vector2(table.Next[t].Position.X, table.Next[t].Position.Z), SpeedMode.Free,
                TaskType.ArrestTarget, TaskSource.Npc);
            Note(world, ArrestEventKind.Fine, guard, target, gc.Faction, arrest.Fine);
        }
        else BeginFight(world, arrest, g, t);
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
        if (table.Cold(t)!.IsPlayer && !FoughtByOther(arrest)) world.Post(Loc.Tr("{1} is arresting {2}!", table.Cold(g)!.Name, table.Cold(t)!.Name));
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
    /// The guard and the free members of his squad within twice their sight of the target attack it from the next tick (the squad marking the target,
    /// engine reading).
    /// </summary>
    void Attack(World world, int g, int t)
    {
        var table = world.Characters;
        var fighters = new List<CharacterId> { table.IdOf(g) };
        if (world.Squads.Find(table.Cold(g)!.SquadId) is { } squad)
        {
            var at = table.Next[t].Position;
            foreach (var m in squad.Members)
            {
                if (!table.TryResolveNext(m, out int s) || s == g || !table.Next[s].Alive
                    || table.Next[s].Task is (byte)CharacterTask.Attack or (byte)CharacterTask.Treat)
                {
                    continue;
                }
                if (table.Cold(s) is not { IsPlayer: false, InCombat: false, Fleeing: false, IsImprisoned: false, Medical.Incapacitated: false,
                    Carrying.IsNone: true, Fighter.CanAttack: true } mc || Busy(m))
                {
                    continue;
                }
                float r = 2 * AggroSystem.RangeOf(mc, AggroSystem.OutdoorSight);
                if (Vector3.DistanceSquared(table.Next[s].Position, at) <= r * r) fighters.Add(m);
            }
        }
        world.Defer(new AttackOrder(fighters, table.IdOf(t)) { Tick = world.Tick + 1, Source = TaskSource.Npc });
    }

    /// <summary>
    /// One tick of an arrest; false when it is over.
    /// </summary>
    bool Step(World world, Arrest a)
    {
        var table = world.Characters;
        float dt = world.TickSeconds;
        if (!table.TryResolveNext(a.Guard, out int g) || table.Cold(g) is not { Medical.Incapacitated: false } gc)
            return End(world, a, ArrestEventKind.GaveUp);
        if (!table.TryResolveNext(a.Target, out int t) || table.Cold(t) is not { } tc || tc.Medical is { Dead: true })
            return End(world, a, ArrestEventKind.GaveUp);
        if (tc.IsImprisoned && a.Phase != ArrestPhase.Walk) return End(world, a, ArrestEventKind.GaveUp);

        a.Time += dt;
        ref var gn = ref table.Next[g];
        float gap = Vector2.Distance(new Vector2(gn.Position.X, gn.Position.Z), new Vector2(table.Next[t].Position.X, table.Next[t].Position.Z));
        switch (a.Phase)
        {
            case ArrestPhase.Fine: return StepFine(world, a, g, t, gc, tc, ref gn, gap);
            case ArrestPhase.Fight: return StepFight(world, a, g, t, gc, tc, ref gn, gap, dt);
            case ArrestPhase.Strip: return StepStrip(world, a, g, t);
            case ArrestPhase.Lift: return StepLift(world, a, g, t, gc, ref gn);
            case ArrestPhase.Walk: return StepWalk(world, a, g, t);
        }
        return true;
    }

    /// <summary>
    /// The fine: the guard walks after the target and, in talking range, takes the fine or, unpaid, fights; a fight, the leash or the time also
    /// end it.
    /// </summary>
    bool StepFine(World world, Arrest a, int g, int t, CharacterCold gc, CharacterCold tc, ref CharacterHot gn, float gap)
    {
        var table = world.Characters;
        if (!tc.CarriedBy.IsNone) return End(world, a, ArrestEventKind.GaveUp);
        if (gc.InCombat || gn.Task == (byte)CharacterTask.Attack)
        {
            // Pulled into a fight: with the target itself fighting, it is an arrest; else he gives up the fine.
            if (!tc.InCombat) return End(world, a, ArrestEventKind.GaveUp);
            BeginFight(world, a, g, t);
            return true;
        }
        if (gap <= Settings.TalkRange) return TakeFine(world, a, g, t, tc);
        if (gap > Settings.FineLeash || a.Time > Settings.FineSeconds)
        {
            BeginFight(world, a, g, t);
            return true;
        }

        // Keep walking after the target (a new path when it moved on, the first-aid pattern's cadence).
        var goal = new Vector2(table.Next[t].Position.X, table.Next[t].Position.Z);
        if ((world.Tick + g) % 15 == 0 && (gn.Task != (byte)CharacterTask.GoTo || Vector2.DistanceSquared(goal, gn.Goal) > 20 * 20))
            TaskStart.Walk(world, g, goal, SpeedMode.Free, TaskType.ArrestTarget, TaskSource.Npc);
        return true;
    }

    /// <summary>
    /// The guard in talking range stops and takes the fine, which clears the bounty; one who cannot pay is fought.
    /// </summary>
    bool TakeFine(World world, Arrest a, int g, int t, CharacterCold tc)
    {
        TaskStart.Stand(world.Characters, g);
        if (world.Player.Money > a.Fine && tc.Bounties is { } ledger && ledger.BookedFor(Crimes.Factions.Relations, a.Faction) is { } entry)
        {
            world.Player.Money -= a.Fine;
            ledger.Remove(entry);
            if (ledger.Count == 0) tc.Bounties = null;
            tc.Recognition = 0;
            world.Post(Loc.Tr("{1} paid a fine of c.{2} to {3}; the bounty is cleared", tc.Name, a.Fine, entry.FactionName));
            return End(world, a, ArrestEventKind.Paid);
        }
        world.Post(Loc.Tr("{1} cannot pay the fine of c.{2}", tc.Name, a.Fine));
        BeginFight(world, a, g, t);
        return true;
    }

    /// <summary>
    /// The fight: once the target is down the guard strips or lifts it (when there is a cage to take it to); a fight that stopped with the target
    /// still up starts again while it is near.
    /// </summary>
    bool StepFight(World world, Arrest a, int g, int t, CharacterCold gc, CharacterCold tc, ref CharacterHot gn, float gap, float dt)
    {
        if (tc.Medical is { Incapacitated: true }) return StepTargetDown(world, a, g, gc, tc, dt);
        if (gn.Task != (byte)CharacterTask.Attack && !gc.InCombat && a.Time >= 1)
        {
            // The fight stopped (the leash, a lost target) with the target still up: again while it is near, else he gives up.
            if (gap > 2 * Settings.SpotRange) return End(world, a, ArrestEventKind.GaveUp);
            a.Time = 0;
            Attack(world, g, t);
        }
        return true;
    }

    /// <summary>
    /// The target is down: the guard strips it when it must, then lifts it, giving up after 10 s of failed lifts.
    /// </summary>
    bool StepTargetDown(World world, Arrest a, int g, CharacterCold gc, CharacterCold tc, float dt)
    {
        if (Carry is null) return End(world, a, ArrestEventKind.GaveUp);
        if (gc.Carrying == a.Target)
        {
            a.Phase = ArrestPhase.Lift;
            return true;
        }
        if (gc.InCombat) return true;   // still trading blows (the KO tick, a squad mate): wait

        // 203 needs 118 target unarmed: its provider 181 comes before the lift
        if (NeedsStrip(a, tc))
        {
            BeginStrip(a);
            return true;
        }

        // Not from an ally who carries it already, nor with no cage to take it to: a lift then would only be dropped again, and guards passed
        // the bodies round a full station (#339; the original's arrest goal wants a free cage, docs/game/economy.md "Arrest goal").
        if (CarriedByAlly(world, gc, tc) || !CageFor(world, a, g)) return End(world, a, ArrestEventKind.GaveUp);

        // Row 240 Capture Target is the chain's lift (snatch byte set): it takes a body another carries too.
        if (Carry.BeginPickUp(world, g, a.Target, source: TaskSource.Npc, row: TaskType.LiftPersonSnatchingAllowedInTownOnly))
        {
            a.Phase = ArrestPhase.Lift;
            a.Waited = 0;
            return true;
        }
        a.Waited += dt;
        return a.Waited <= 10 || End(world, a, ArrestEventKind.GaveUp);
    }

    /// <summary>
    /// The lift: once the guard carries the target he takes it to the cage; a lift that failed 5 times gives up, else the fight resumes.
    /// </summary>
    bool StepLift(World world, Arrest a, int g, int t, CharacterCold gc, ref CharacterHot gn)
    {
        if (gc.Carrying == a.Target)
        {
            Note(world, ArrestEventKind.Lifted, a.Guard, a.Target, a.Faction, 0);
            a.Phase = ArrestPhase.Walk;
            a.Tries = 0;
            return ToCage(world, a, g, t);
        }
        if (gn.Task == (byte)CharacterTask.PickUp) return true;
        if (++a.Tries > 5) return End(world, a, ArrestEventKind.GaveUp);
        a.Phase = ArrestPhase.Fight;
        a.Time = 0;
        return true;
    }

    /// <summary>
    /// The cage errand (112 / 108): the guard walks onto the cage's floor (a cage upstairs is reached by the stairs, not stood under:
    /// <see cref="FixtureRegistry.AtCage"/> tests the height) and locks the captive up there. After <see cref="ArrestSettings.CageWalks"/> walks that
    /// end short the cage counts as unreachable for his faction and the captive is dropped.
    /// </summary>
    bool StepWalk(World world, Arrest a, int g, int t)
    {
        var table = world.Characters;
        var gc = table.Cold(g)!;
        var tc = table.Cold(t)!;
        ref var gn = ref table.Next[g];
        if (gc.Carrying != a.Target)
        {
            // Dropped (the guard was hit, the captive taken): lift it again while it is down and free.
            if (tc.Medical is { Incapacitated: true } && tc.CarriedBy.IsNone && !tc.IsImprisoned)
            {
                a.Phase = ArrestPhase.Fight;
                a.Time = 0;
                return true;
            }
            return End(world, a, ArrestEventKind.GaveUp);
        }
        var cage = a.Cage is { } id ? world.Fixtures.Find(id) : null;
        if (cage is null || !FixtureRegistry.IsFree(table, cage)) return ToCage(world, a, g, t);
        bool walking = gn.Task == (byte)CharacterTask.GoTo;
        if (FixtureRegistry.AtCage(cage, gn.Position, walking)) return LockUp(world, a, g, t, cage);
        if (walking) return true;
        if (++a.Tries > Settings.CageWalks)
        {
            world.Fixtures.MarkUnreachable(world, cage, a.Faction);
            return Drop(world, a, g);
        }
        TaskStart.Walk(world, g, cage.Position, SpeedMode.Free, TaskType.FindCageAndPutInIfBounty, TaskSource.Npc);
        return true;
    }

    /// <summary>
    /// At the cage: the guard puts the captive down into it with its sentence (a slaver's take-over enslaves it instead of posting the arrest).
    /// </summary>
    bool LockUp(World world, Arrest a, int g, int t, Fixture cage)
    {
        var table = world.Characters;
        var tc = table.Cold(t)!;
        TaskStart.Stand(table, g);
        Carry!.LetGo(world, g);
        if (!world.Fixtures.Imprison(world, a.Target, cage, constants.PrisonTime)) return ToCage(world, a, g, t);

        Deeds?.Caged(world, g, t);
        tc.Recognition = 0;
        if (a.Enslave) Enslave(world, a, g, tc);
        else if (tc.IsPlayer) world.Post(Loc.Tr("{1} has been imprisoned by {2}", tc.Name, Crimes.Factions.NameOf(a.Faction)));
        return End(world, a, ArrestEventKind.Jailed);
    }

    /// <summary>
    /// A caged captive of an enslaving arrest becomes the guard faction's slave; a recaught escaped slave is also chained (engine: the original's
    /// slave catching cuffs it before the carry, <see cref="Kidnap.CaptiveSystem.Chain"/>). A take-over's body is not: whether the original's
    /// <c>DA_ENSLAVE_TARGETS_CARRIED_PERSON</c> chains it is <b>Unknown</b>.
    /// </summary>
    void Enslave(World world, Arrest a, int g, CharacterCold tc)
    {
        FixtureRegistry.Enslave(world, tc, a.Faction, world.Characters.Cold(g)?.SquadId ?? -1);
        if (!a.Takeover) Captives?.Chain(tc);
    }

    /// <summary>
    /// Row 181's step: 118 does not hold (the target wields or holds a weapon in its weapon sections) and the strip can run (needs the tool,
    /// <see cref="Captives"/>).
    /// </summary>
    bool NeedsStrip(Arrest a, CharacterCold target) =>
        Captives is not null && !a.Takeover && !a.Enslave && !a.Stripped && target.CarriedBy.IsNone && !CharacterFacts.TargetUnarmed(target);

    /// <summary>
    /// Game seconds the guard may take to reach the target to strip it (engine choice).
    /// </summary>
    const float StripWalkSeconds = 60;

    /// <summary>
    /// Puts the arrest in its strip phase.
    /// </summary>
    void BeginStrip(Arrest a)
    {
        a.Phase = ArrestPhase.Strip;
        a.Time = 0;
    }

    /// <summary>
    /// Row 181 Stripping weapons (needs 38 one working arm, at location, 7 target down; <c>Task_StripWeapons</c>): the guard walks up, plays
    /// <c>medic</c> for the strip's seconds, and the target's weapon sections go to him (<see cref="Kidnap.CaptiveSystem.StripWeapons"/>); then the
    /// chain goes on to the lift. A guard without a working arm cannot plan the chain: the arrest ends.
    /// </summary>
    bool StepStrip(World world, Arrest a, int g, int t)
    {
        var table = world.Characters;
        var gc = table.Cold(g)!;
        var tc = table.Cold(t)!;
        if (tc.Medical is not { Incapacitated: true } || !tc.CarriedBy.IsNone || Captives is null)
        {
            a.Phase = ArrestPhase.Fight;
            a.Time = 0;
            return true;
        }
        if (gc.Medical is { } arms && !arms.ArmWorks(left: true) && !arms.ArmWorks(left: false)) return End(world, a, ArrestEventKind.GaveUp);
        if (gc.InCombat) return true;
        ref var gn = ref table.Next[g];
        var to = new Vector2(table.Next[t].Position.X, table.Next[t].Position.Z);
        var here = new Vector2(gn.Position.X, gn.Position.Z);
        if (Vector2.Distance(here, to) > Captives.Settings.Reach)
        {
            if ((world.Tick + g) % 15 == 0 && (gn.Task != (byte)CharacterTask.GoTo || Vector2.DistanceSquared(to, gn.Goal) > 20 * 20))
                TaskStart.Walk(world, g, to, SpeedMode.Free, TaskType.StripTargetsWeapons, TaskSource.Npc);
            a.Time = 0;
            a.Waited += world.TickSeconds;   // the walk up: the lift's patience
            return a.Waited <= StripWalkSeconds || End(world, a, ArrestEventKind.GaveUp);
        }
        if (gc.ActionClip != Kidnap.CaptiveSystem.ProcessClip)
        {
            TaskStart.Stand(table, g);
            gc.ActionClip = Kidnap.CaptiveSystem.ProcessClip;
            a.Time = 0;
            return true;
        }
        if (a.Time < Captives.Settings.StripSeconds) return true;
        gc.ActionClip = null;
        Captives.StripWeapons(world, t, gc, tc);
        a.Stripped = true;
        a.Phase = ArrestPhase.Fight;
        a.Time = 0;
        a.Waited = 0;
        return true;
    }

    /// <summary>
    /// Chooses the cage (the original's finder; other arrests' cages are reserved) and walks there; none: the captive is dropped.
    /// </summary>
    bool ToCage(World world, Arrest a, int g, int t)
    {
        var cage = GuardCage(world, g, a);
        if (cage is null) return Drop(world, a, g);
        a.Cage = cage.Id;
        TaskStart.Walk(world, g, cage.Position, SpeedMode.Free, TaskType.FindCageAndPutInIfBounty, TaskSource.Npc);
        return true;
    }

    /// <summary>
    /// Whether a free cage the arrest could take its captive to exists (<see cref="ToCage"/>'s finder).
    /// </summary>
    bool CageFor(World world, Arrest a, int g) => GuardCage(world, g, a) is not null;

    /// <summary>
    /// The guard's cage (<c>FUN_1408d0770</c> over the gatherer <c>FUN_1408cbea0</c>, economy.md 11.7): the best free one among his town candidates
    /// (<see cref="FixtureRegistry.CageCandidates"/>), the cages of arrests other than <paramref name="self"/> reserved; every cage without town
    /// data.
    /// </summary>
    Fixture? GuardCage(World world, int g, Arrest? self)
    {
        var table = world.Characters;
        var gc = table.Cold(g)!;
        var at = table.Next[g].Position;
        var candidate = Towns is { } data ? world.Fixtures.CageCandidates(world, data, gc, at) : null;
        return world.Fixtures.FindCage(world, gc, at, gc.Ai?.Squad.Ai?.Home?.Key, f => candidate?.Invoke(f) == false || Reserved(f, self));
    }

    /// <summary>
    /// Whether <paramref name="target"/> is carried by someone of the guard's faction.
    /// </summary>
    static bool CarriedByAlly(World world, CharacterCold guard, CharacterCold target) =>
        world.Characters.TryResolveNext(target.CarriedBy, out int carrier) && world.Characters.Cold(carrier) is { } c && c != guard
            && c.Faction == guard.Faction;

    /// <summary>
    /// Whether another arrest than <paramref name="self"/> has taken cage <paramref name="f"/>.
    /// </summary>
    bool Reserved(Fixture f, Arrest? self)
    {
        foreach (var a in _arrests) if (!ReferenceEquals(a, self) && a.Cage == f.Id) return true;
        return false;
    }

    /// <summary>
    /// No free cage: the guard stands, lets go of the captive and the arrest ends as dropped.
    /// </summary>
    bool Drop(World world, Arrest a, int g)
    {
        TaskStart.Stand(world.Characters, g);
        Carry?.LetGo(world, g);
        if (world.Characters.TryResolveNext(a.Target, out int t) && world.Characters.Cold(t) is { IsPlayer: true } tc)
            world.Post(Loc.Tr("{1} found no free cage for {2}", world.Characters.Cold(g)!.Name, tc.Name));
        return End(world, a, ArrestEventKind.Dropped);
    }

    /// <summary>
    /// Ends an arrest: the guard goes back to his routine (the AI takes him again).
    /// </summary>
    bool End(World world, Arrest a, ArrestEventKind kind)
    {
        var table = world.Characters;
        if (table.TryResolveNext(a.Guard, out int g) && table.Cold(g) is { } gc)
        {
            gc.Engaged = false;
            if (table.Next[g].Task == (byte)CharacterTask.GoTo) TaskStart.Stand(table, g);
        }
        Note(world, kind, a.Guard, a.Target, a.Faction, 0);
        return false;
    }

    /// <summary>
    /// Logs an arrest event when <see cref="ArrestSettings.RecordLog"/> is on.
    /// </summary>
    void Note(World world, ArrestEventKind kind, CharacterId guard, CharacterId target, int faction, int amount)
    {
        if (Settings.RecordLog) _log.Add(new ArrestEvent(world.Tick, kind, guard, target, faction, amount));
    }

    /// <summary>
    /// Adds the bounty, disguise, spotting and arrest state to the world hash; the spottings and arrests only while there are any.
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
        if (_arrests.Count == 0) return;   // only while an arrest runs, so worlds without them hash as before
        hasher.Add(0x41727273);
        hasher.Add(_arrests.Count);
        foreach (var a in _arrests) a.Hash(ref hasher);
    }

    #endregion
}
