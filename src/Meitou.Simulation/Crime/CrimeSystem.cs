using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Crime;

/// <summary>
/// Crimes and bounties (docs/simulation.md "Crimes and bounties"; the original's rules in docs/game/economy.md section 11 and docs/game/carry-loot.md
/// 4.3 and 4.6). Everything here runs in serial phases: the deeds are reported by the systems that see them (combat's <c>Inputs</c> and commit, the
/// carry system's <c>SlowWorld</c>), the witness sweep and the bounty expiry run in this system's <c>SlowWorld</c>, in slot order. The state lives on
/// the characters (<see cref="CharacterCold.Pending"/>,
/// <see cref="CharacterCold.Bounties"/>) and is hashed only while set.
/// <list type="bullet">
/// <item><b>Pending</b> (<see cref="SetPending"/>, the game's <c>FUN_140852c80</c>): a deed first becomes a pending crime on the culprit, unless the
/// culprit is police-like, the victim is of the culprit's own faction or of a lawless one. An Assault does not replace another pending crime.</item>
/// <item><b>Witnesses</b> (<see cref="Sweep"/>, after <c>FUN_140854d10</c>): an awake NPC of another faction that sees the culprit and cares about
/// the victim (by the crime) sets the timer to 20; only a police-like witness of a faction that books bounties books it (<see cref="Book"/>,
/// <c>FUN_140853ec0</c>): the crime table's amount into the ledger entry of the
/// <b>witness's</b> faction (<c>FUN_140852010</c>).</item>
/// <item><b>Police</b>: a hit between a police-like character and another books an Assault on the other at once (<c>FUN_140665fa0</c>); police-like
/// NPCs spot, fine and arrest a character wanted by their faction (<see cref="ArrestSystem"/>).</item>
/// <item><b>Expiry</b>: an entry lasts 0.04 hours per cat since its last raise, for ever from 10000 (<see cref="CrimeTable.LifetimeHours"/>).</item>
/// </list>
/// The setter has no NPC test: an NPC is the culprit of every crime its callers set (a booking witness books an NPC's crime too, FUN_140854d10 has no
/// player test; its attacks on characters not hostile to them, such as hungry
/// bandits on a town's visitors, are witnessed so the town's police step in).
/// </summary>
public sealed partial class CrimeSystem(FactionRelations relations, CrimeSettings? settings = null) : ITickSystem, IStateHashed
{
    /// <summary>
    /// The freshness a pending crime starts with at least (the setter's minimum, <b>Observed</b>), and the timers the game gives some deeds.
    /// </summary>
    public const float MinTimer = 3, WitnessTimer = 20, PoliceHitTimer = 30, KidnapTimer = 20;
    /// <summary>
    /// The "recently wanted" marker a booking or a witnessed crime sets (<see cref="CharacterCold.RecentlyWanted"/>; the game's 250.0, counted down
    /// here as game seconds: engine choice).
    /// </summary>
    public const float RecentlyWantedSeconds = 250;

    /// <summary>
    /// The bounties booked: the ledger entries' raises and expiry, this tick's bookings by a witness (<see cref="BountyBook"/>).
    /// </summary>
    readonly BountyBook _bounties = new();

    /// <summary>
    /// The bookings of this tick by a witness (the police-like NPC that booked, the culprit, the faction): the arrest system's witnessed-crime
    /// trigger. Not world state (<see cref="BountyBook.BookingsOf"/>).
    /// </summary>
    public IReadOnlyList<Booking> BookingsOf(long tick) => _bounties.BookingsOf(tick);

    /// <summary>
    /// The crime log, kept while <see cref="CrimeSettings.RecordLog"/> is on.
    /// </summary>
    readonly List<CrimeEvent> _log = [];

    /// <summary>
    /// The settings; the defaults when none were given.
    /// </summary>
    public CrimeSettings Settings { get; } = settings ?? new CrimeSettings();
    /// <summary>
    /// Who books bounties and whose goods are free, built from the relations.
    /// </summary>
    public CrimeFactions Factions { get; } = new(relations);
    /// <summary>
    /// The faction relations the crime rules read.
    /// </summary>
    public FactionRelations Relations => Factions.Relations;

    /// <summary>
    /// What happened (with <see cref="CrimeSettings.RecordLog"/>).
    /// </summary>
    public IReadOnlyList<CrimeEvent> Log => _log;

    #region Who is who

    /// <summary>
    /// Police-like (the game's <c>FUN_140599600</c>, <b>Observed</b>): never of a BANDIT-type faction; <c>NPC class</c> LAW_ENFORCEMENT; or MILITARY
    /// when the character's own faction's object matches its squad's (read here as: a member of a resident squad of a town, of its own faction;
    /// roaming military are not police: engine reading). The player's characters never are (engine: their status object has no such link).
    /// </summary>
    public bool IsPoliceLike(World world, CharacterCold c)
    {
        if (c.IsPlayer || Factions.Bandit(c.Faction)) return false;
        if (c.NpcClass == FundamentalType.LawEnforcement) return true;
        return c.NpcClass == FundamentalType.Military && c.SquadId >= 0 && world.Squads.Find(c.SquadId) is { TownOwn: true } squad
            && squad.Faction == c.Faction;
    }

    /// <summary>
    /// A faction regards another as friendly: the same, or allied (relation 50 or more), as the carry system reads the game's "regards as friendly".
    /// </summary>
    public bool Friendly(int a, int b) => a == b || (Relations.Contains(a) && Relations.Contains(b) && Relations.IsAlly(a, b));

    /// <summary>
    /// The character has an unclaimed bounty of <paramref name="faction"/> (what a police-like NPC of that faction attacks it for).
    /// </summary>
    public bool IsWantedBy(CharacterCold c, int faction) => c.Bounties?.BookedFor(Factions.Relations, faction) is { Amount: > 0, Claimed: false };

    #endregion

    #region Deeds

    /// <summary>
    /// Sets a pending crime on the culprit (the game's setter <c>FUN_140852c80</c>, <b>Verified</b>): refused for a police-like culprit, an
    /// imprisoned one (<c>+0x2f8</c> = 2), a victim of the culprit's own faction, for a free culprit (slave state 0) a victim of the faction it is
    /// seen as (<see cref="ArrestSystem.SeenFaction"/>: a working uniform's; <c>FUN_140852c20</c>) and a lawless victim faction; an Assault on a
    /// diplomat becomes code 9 (<see cref="DiplomatVictim"/>); an Assault does not
    /// replace another pending crime (it raises that one's timer to 20); the same code again keeps the booked flag; a new code clears it. The timer
    /// is at least <see cref="MinTimer"/>. True when the crime is now pending. <paramref name="viaWrapper"/>: the original's callers that go through
    /// its wrapper <c>FUN_140852e10</c> (police hit, caught stealing, fencing, kidnapping seen, a uniform impostor seen) are refused for a culprit
    /// that is down (vfunc <c>0x30</c>, <see cref="Bodies.CharacterFacts.Down"/>) or an animal (vfunc <c>0x248</c>), and drop the culprit's
    /// <see cref="CharacterCold.Freed"/> flag when the crime is set (the other callers do not; <b>Verified</b>). The wrapper also writes the
    /// recently-wanted marker first, which makes the uniform test a no-op for its callers, so it is skipped for them. <paramref name="victimHolder"/>:
    /// a victim that is a building's holder of goods rather than a character (<see cref="PendingCrime.VictimHolder"/>). <paramref name="wasUp"/>: the
    /// caller's deed ran before the culprit could go down (the police hit: the game's hit outcome runs before the blow's damage).
    /// </summary>
    public bool SetPending(World world, int culprit, CrimeCode code, int victimFaction, CharacterId victim, float timer, bool viaWrapper = false,
        string? victimHolder = null, bool wasUp = false)
    {
        var c = world.Characters.Cold(culprit);
        if (c is null || viaWrapper && (!wasUp && Bodies.CharacterFacts.Down(c) || c.Fighter?.IsAnimal == true)) return false;
        if (IsPoliceLike(world, c) || c.IsImprisoned || victimFaction == c.Faction
            || !viaWrapper && c.SlaveState == 0 && victimFaction == ArrestSystem.SeenFaction(c) || Factions.Lawless(victimFaction)) return false;
        timer = MathF.Max(timer, MinTimer);
        if (code == CrimeCode.Assault && DiplomatVictim(world, victim)) code = CrimeCode.Terrorism2;
        if (c.Pending is { } p && p.Code != code && code == CrimeCode.Assault)
        {
            p.Timer = MathF.Max(p.Timer, WitnessTimer);
            return false;
        }
        if (c.Pending is { } same && same.Code == code)
        {
            same.VictimFaction = victimFaction;
            same.Victim = victim;
            same.VictimHolder = victimHolder;
            same.Timer = MathF.Max(same.Timer, timer);
            if (viaWrapper) c.Freed = false;
            return true;
        }
        var replaced = c.Pending;
        c.Pending = new PendingCrime { Code = code, VictimFaction = victimFaction, Victim = victim, VictimHolder = victimHolder, Timer = timer };
        if (viaWrapper) c.Freed = false;

        // the crime of another code it replaced ended, told once the new one is set
        if (replaced is not null) Lapsed(world, culprit, replaced);
        Note(world, CrimeEventKind.Pending, culprit, code, victimFaction, 0, CharacterId.None);
        return true;
    }

    /// <summary>
    /// The setter's Assault on a diplomat (<c>FUN_140852c80</c>, <b>Verified</b>): an Assault whose victim is a character with diplomatic status
    /// in its faction's own biomes (<c>isDiplomaticStatus(victim, 1)</c>, <see cref="Bodies.CharacterFacts.DiplomaticStatus"/>) is booked as code
    /// 9, the second Terrorism, which no other pending crime holds off.
    /// </summary>
    static bool DiplomatVictim(World world, CharacterId victim)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(victim, out int v) || table.Cold(v) is not { } vc) return false;

        var data = world.SystemOf<Population.PopulationSystem>()?.Data;
        return Bodies.CharacterFacts.DiplomaticStatus(vc, table.Next[v].Position, data, true);
    }

    /// <summary>
    /// An attack order on <paramref name="victim"/> (not a defensive one): an Assault (the game's <c>FUN_140334070</c>, <b>Verified</b>, run when an
    /// attack task starts) unless the victim is wanted by the attacker's faction, is a bandit (<c>NPC class</c> 8), has a pending crime of its own,
    /// or is in melee combat mode (<paramref name="victimFighting"/>, <c>Character::isInCombatMode</c> <c>FUN_140436610</c>), or has the "ordered"
    /// byte <c>+0x114</c> set (<see cref="CharacterCold.Ordered"/>: an earlier ordered attack still running; <b>Verified</b>). The attacker gets that
    /// byte before the booking, whether or not the crime is then set. Engine choice, no counterpart in the original: a victim whose faction is
    /// hostile to the attacker's is not booked for. An NPC attacker counts too, but not an animal (engine choice), and the victim may be one of the
    /// player's characters.
    /// </summary>
    public void Assault(World world, int attacker, int victim, bool victimFighting = false)
    {
        var table = world.Characters;
        if (table.Cold(attacker) is not { } a || a.Fighter?.IsAnimal == true || table.Cold(victim) is not { } v || a.IsPlayer && v.IsPlayer) return;
        if (IsWantedBy(v, a.Faction) || v.NpcClass == FundamentalType.Bandit || v.Pending is not null || victimFighting || v.Ordered) return;
        a.Ordered = true;
        if (AggroSystem.IsEnemy(Relations, v.Faction, a.Faction)) return;
        SetPending(world, attacker, CrimeCode.Assault, v.Faction, table.IdOf(victim), MinTimer);
    }

    /// <summary>
    /// Items taken from a downed character (the carry system's take-all): Looting (code 10, the right-click transfer's code for a downed owner,
    /// <b>Verified</b> call site) unless the game's permission test (<c>FUN_140793940</c>, <b>Observed</b>) allows it: the victim is a slave (<c>NPC
    /// class</c> SLAVE, or slave state 1 or 2; see <see cref="LootAllowed"/>), a bandit, or has any bounty; or the taker is LAW_ENFORCEMENT, MILITARY
    /// or DIPLOMAT.
    /// </summary>
    public void Loot(World world, int looter, int victim)
    {
        var table = world.Characters;
        if (table.Cold(looter) is not { } l || table.Cold(victim) is not { } v || !IsLootTheft(world, l, v)) return;
        SetPending(world, looter, CrimeCode.Looting, v.Faction, table.IdOf(victim), MinTimer);
    }

    /// <summary>
    /// Whether the player's taking from a downed character is a crime: a take that is stealing at all
    /// (<see cref="IsStealing(World, CharacterCold)"/>) and the permission test of <see cref="Loot"/> (<c>FUN_140793940</c>, <b>Observed</b>)
    /// failing.
    /// </summary>
    public static bool IsLootTheft(World world, CharacterCold looter, CharacterCold victim) =>
        MarksTheft(world, looter, victim) && !LootAllowed(looter, victim);

    /// <summary>
    /// Whether what the player takes from a character is marked stolen from it (<c>Item::notifyTheftFrom</c>, <b>Verified</b> call sites):
    /// <see cref="IsStealing(World, CharacterCold)"/> alone, the loot permission aside, so a bandit's gear is stolen from the bandits too. Right
    /// click and drag mark only an item with no owner yet; a give overwrites.
    /// </summary>
    public static bool MarksTheft(World world, CharacterCold taker, CharacterCold victim) =>
        taker.IsPlayer && !victim.IsPlayer && IsStealing(world, victim);

    /// <summary>
    /// <c>Character::isItOkForMeToLoot</c> (<c>FUN_140793940</c>, <b>Observed</b>) asked with no item: yes when the victim is a slave or escaped
    /// slave (slave state 1 or 2; Meitou also takes <c>NPC class</c> SLAVE), a bandit or has any bounty, or the looter is LAW_ENFORCEMENT, MILITARY
    /// or DIPLOMAT. The original's "my slave" test is covered by the slave state.
    /// </summary>
    public static bool LootAllowed(CharacterCold looter, CharacterCold victim) =>
        victim.SlaveState is 1 or 2 || victim.NpcClass is FundamentalType.Slave or FundamentalType.Bandit || victim.Bounties is { Any: true }
        || looter.NpcClass is FundamentalType.LawEnforcement or FundamentalType.Military or FundamentalType.Diplomat;

    /// <summary>
    /// An NPC starting to loot a body (<c>Task_Loot_AI</c> start, <c>FUN_140355ad0</c>, <b>Observed</b>; not for a police-like looter, which
    /// confiscates instead): a pending Looting against the victim's faction when the looter has any bounty or is a bandit (<c>NPC class</c> 8); then,
    /// when it takes more than one weapon (<paramref name="everything"/>: food only, or a body that is not the player's side or is dead), event 33
    /// LOOTING_EVERYTHING at the victim and a pending Looting unless <see cref="LootAllowed"/>; else, when it is after a weapon, event 32
    /// LOOTING_WEAPON_ONLY (the original's crime test there needs the looter to hold a weapon, which it never does on that path).
    /// </summary>
    public void NpcLooting(World world, int looter, int victim, bool everything, bool wantsWeapon)
    {
        var table = world.Characters;
        if (table.Cold(looter) is not { } l || table.Cold(victim) is not { } v || IsPoliceLike(world, l)) return;
        var victimId = table.IdOf(victim);
        if (l.Bounties is { Any: true } || l.NpcClass == FundamentalType.Bandit)
            SetPending(world, looter, CrimeCode.Looting, v.Faction, victimId, MinTimer);
        if (everything)
        {
            world.Defer(new Dialogue.DialogueEvent(table.IdOf(looter), victimId, LootingEverythingEvent) { Tick = world.Tick + 1 });
            if (!LootAllowed(l, v)) SetPending(world, looter, CrimeCode.Looting, v.Faction, victimId, MinTimer);
        }
        else if (wantsWeapon) world.Defer(new Dialogue.DialogueEvent(table.IdOf(looter), victimId, LootingWeaponEvent) { Tick = world.Tick + 1 });
    }

    /// <summary>
    /// The dialogue events an NPC looter says at the body (<b>Verified</b> literals in <c>FUN_140355ad0</c>): 32 LOOTING_WEAPON_ONLY, 33
    /// LOOTING_EVERYTHING.
    /// </summary>
    public const int LootingWeaponEvent = 32, LootingEverythingEvent = 33;

    /// <summary>
    /// A landed hit between <paramref name="attacker"/> and <paramref name="defender"/> (the game's hit outcome <c>FUN_140665fa0</c>,
    /// <b>Verified</b>): when exactly one of them is police-like, the other, player's or not, gets a pending Assault (timer 30) against the police
    /// faction through the setter's wrapper, and whatever crime it has pending is booked with that faction at once, whichever way the blow went
    /// (being hit by the police counts too). <paramref name="defenderWasDown"/>: the defender was down before the blow.
    /// </summary>
    public void Hit(World world, int attacker, int defender, bool defenderWasDown = false)
    {
        var table = world.Characters;
        if (table.Cold(attacker) is not { } a || table.Cold(defender) is not { } d) return;
        // Engine choice: an NPC's landed blow renews its Assault, which its attack order set with 3 s that often run out before the attacker reaches
        // its victim.
        if (!a.IsPlayer) Assault(world, attacker, defender);
        bool pa = IsPoliceLike(world, a), pd = IsPoliceLike(world, d);
        if (pa == pd) return;
        int culprit = pd ? attacker : defender, police = pd ? defender : attacker;
        var c = table.Cold(culprit)!;
        var p = table.Cold(police)!;
        // The outcome runs before the damage: the striker is up, the struck one as it stood before the blow.
        bool wasUp = culprit == attacker || !defenderWasDown;
        SetPending(world, culprit, CrimeCode.Assault, p.Faction, table.IdOf(police), PoliceHitTimer, viaWrapper: true, wasUp: wasUp);
        if (c.Pending is not null) Book(world, culprit, p.Faction, table.IdOf(police));
    }

    #endregion

    #region Booking

    /// <summary>
    /// Books the culprit's pending crime with <paramref name="faction"/> (the game's <c>FUN_140853ec0</c>, <see cref="BountyBook.Book"/>); true when
    /// a bounty was raised.
    /// </summary>
    public bool Book(World world, int culprit, int faction, CharacterId witness) => _bounties.Book(this, world, culprit, faction, witness);

    /// <summary>
    /// A code mod's bounty (engine API; the original's <c>FUN_140853e20</c>, which dialogue's assigned bounty and a new NPC's rolled one use): adds
    /// <paramref name="amount"/> to the character's ledger entry of <paramref name="faction"/>'s booking faction
    /// (<see cref="CrimeFactions.BookingOf"/>: its <c>legal system</c>, else itself when it books bounties), stamps the time of the last raise, so
    /// the entry lasts as a witnessed one does
    /// (<see cref="CrimeTable.LifetimeHours"/>: for ever from 10000), and reports it as <see cref="CrimeEventKind.Booked"/> with no crime and no
    /// witness. No pending crime is touched and no message is posted. Any character, the player's or not, alive or dead. Returns the entry's
    /// amount after the raise; -1, changing nothing, for an amount of 0 or less, a character that is not in the world, a faction with no booking
    /// faction, or during the parallel phase. Serial phases and <see cref="IWorldReady"/>.
    /// </summary>
    public int AssignBounty(World world, CharacterId character, int faction, int amount)
    {
        var table = world.Characters;
        if (table.Phase == TablePhase.Parallel || amount <= 0) return -1;
        if (!table.TryResolveNext(character, out int slot) || table.Cold(slot) is not { } c) return -1;

        return AddBounty(world, slot, c, faction, amount)?.Amount ?? -1;
    }

    /// <summary>
    /// A code mod's pardon (engine API): removes the character's ledger entry of <paramref name="faction"/>'s booking faction, reported as
    /// <see cref="CrimeEventKind.Cleared"/>, as a paid fine removes it. Relations and recognition are left as they are. Returns the amount removed;
    /// 0 when there was no such entry, during the parallel phase or for a character that is not in the world. Serial phases and
    /// <see cref="IWorldReady"/>.
    /// </summary>
    public int ClearBounty(World world, CharacterId character, int faction)
    {
        var table = world.Characters;
        if (table.Phase == TablePhase.Parallel || !table.TryResolveNext(character, out int slot)) return 0;
        if (table.Cold(slot) is not { Bounties: { } ledger } c || ledger.Of(Factions.BookingOf(faction)) is not { } entry) return 0;

        ClearBounty(world, slot, c, ledger, entry);
        return entry.Amount;
    }

    /// <summary>
    /// A mod's bounty on the character in <paramref name="slot"/>: <see cref="Raise"/> on <paramref name="faction"/>'s booking faction, reported as
    /// a booking with no crime, no witness and no victim (the character's pending crime, if any, is not this booking's). Null, changing nothing,
    /// when the faction has no booking faction (the original's entry has a null key nothing reads).
    /// </summary>
    BountyEntry? AddBounty(World world, int slot, CharacterCold c, int faction, int amount)
    {
        int key = Factions.BookingOf(faction);
        if (key < 0) return null;

        var entry = Raise(world, c, key, Factions.NameOf(key), amount);
        Note(world, CrimeEventKind.Booked, slot, CrimeCode.None, key, entry.Amount, CharacterId.None, withPending: false);
        return entry;
    }

    /// <summary>
    /// Raises the character's ledger entry of booking faction <paramref name="key"/> (named <paramref name="name"/>) by <paramref name="amount"/>,
    /// no higher than <see cref="int.MaxValue"/>, and stamps the time of its last raise (<c>FUN_140853e20</c>: added and stamped unconditionally, no
    /// crime bit), as dialogue's assigned bounty and a mod's do. Returns the entry.
    /// </summary>
    internal static BountyEntry Raise(World world, CharacterCold c, int key, string name, int amount)
    {
        var entry = (c.Bounties ??= new BountyLedger()).Get(key, name);
        entry.Amount = (int)Math.Min(int.MaxValue, (long)entry.Amount + amount);
        entry.RaisedTick = world.Tick;
        return entry;
    }

    #endregion

    #region The sweep

    /// <summary>
    /// The slow-world step: the witness sweep for the characters whose turn it is; on the shared beat the investigators and the alarms' countdown.
    /// </summary>
    public void SlowWorld(World world)
    {
        int every = world.Every(Settings.SweepEveryTicks);
        // Each character every SweepEveryTicks on its slot's turn (slot mod the period), so the witness looks spread over the ticks (the original's
        // AI updates run 8 characters a frame in turn, FUN_140786e30); a character's timers still lose the whole period each visit. The alarms keep
        // the shared beat.
        int phase = (int)(world.Tick % every);
        Sweep(world, every * world.TickSeconds, every);
        if (phase != 0) return;
        if (_alarms.Count > 0) Investigate(world, every * world.TickSeconds);
        // Alarms die 8 game seconds after their last raise, whether answered or not (AlarmManager update FUN_14092bfe0, Verified).
        for (int k = _alarms.Count - 1; k >= 0; k--)
            if ((_alarms[k].Life -= every * world.TickSeconds) < 0) _alarms.RemoveAt(k);
    }

    /// <summary>
    /// One sweep over the living characters on their turn: the recently wanted marker, the bounties' expiry and the pending crime's timer (none for
    /// the dead), then the witnesses of a pending crime, or for a player character of a kidnapping or premises.
    /// </summary>
    void Sweep(World world, float seconds, int every)
    {
        var table = world.Characters;
        var next = table.Next;
        double hoursPerTick = world.TickSeconds * 11.0 / 1200.0;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!next[s].Alive || !world.Turn(s, every) || table.Cold(s) is not { } c) continue;   // its turn (the far cadence, World.Turn)
            if (c.RecentlyWanted > 0) c.RecentlyWanted = MathF.Max(0, c.RecentlyWanted - seconds);
            Locks.FixtureRegistry.LayLow(world, c);
            // The crime tick FUN_1405cbb80 is skipped for a dead character (FUN_1405d00b0): its bounties never expire and its pending crime stays
            // frozen (no countdown, no witness search).
            if (c.Medical is { Dead: true }) continue;

            // paused while imprisoned (economy.md 11.3)
            if (c.Bounties is { } ledger && !c.IsImprisoned) BountyBook.Expire(this, world, s, c, ledger, hoursPerTick, every);
            if (c.Pending is { } p)
            {
                // Check, then subtract (FUN_1405cbb80 tail): cleared on the first call after the timer reached 0.
                if (p.Timer <= 0)
                {
                    c.Pending = null;
                    Lapsed(world, s, p);
                }
                else p.Timer -= seconds;
            }
            // A carried or imprisoned suspect is not looked at (the game's witness function): a prisoner's crime runs out and its bounty shows.
            if (!c.CarriedBy.IsNone || c.IsImprisoned) continue;
            if (!c.IsPlayer)
            {
                if (c.Pending is not null) Witnesses(world, s, c, false, false, seconds, every);
                continue;
            }
            bool kidnapping = !c.Carrying.IsNone && table.TryResolveNext(c.Carrying, out int body) && Bodies.CharacterFacts.Kidnapped(table.Cold(body)!);
            // The sight pass sets trespassing itself (SightCrimes); without it the witnesses do.
            bool premises = !SightSystem.Running(world) && PremisesOf(world, s) is not null;
            if (c.Pending is null && !kidnapping && !premises) continue;
            Witnesses(world, s, c, kidnapping, premises, seconds, every);
        }
    }

    /// <summary>
    /// The aggro system whose spot meters <see cref="Spotted"/> reads; looked up once.
    /// </summary>
    AggroSystem? _aggro;
    /// <summary>
    /// Whether <see cref="_aggro"/> has been looked up.
    /// </summary>
    bool _aggroLooked;

    /// <summary>
    /// Whether the witness has spotted the sneaking culprit: its spot meter, kept by <see cref="AggroSystem"/> for every looker, is full (without
    /// one, never).
    /// </summary>
    internal bool Spotted(World world, int witness, CharacterId culprit)
    {
        if (!_aggroLooked)
        {
            _aggroLooked = true;
            _aggro = world.SystemOf<AggroSystem>();
        }
        return _aggro?.Spotted(witness, culprit) == true;
    }

    /// <summary>
    /// Whether a witness of faction <paramref name="wf"/> takes the carrying of a kidnapped <paramref name="body"/> for a kidnapping
    /// (<c>FUN_1408549f0</c>, <b>Verified</b>; carry-loot.md 4.6): never an enemy of the body's faction; one that is friendly to it always; any
    /// other only while the carrier stands in a town, the body's faction is real and the body has no bounty with the witness's faction.
    /// </summary>
    bool MindsKidnapping(int wf, CharacterCold carrier, CharacterCold body)
    {
        if (AggroSystem.IsEnemy(Relations, wf, body.Faction)) return false;
        if (Friendly(wf, body.Faction)) return true;

        return !carrier.StatusTown.IsNone && !Factions.NotReal(body.Faction) && !(body.Bounties?.AmountFor(wf) > 0);
    }

    /// <summary>
    /// Every awake NPC that sees the culprit, in slot order, reacts as the game's witness function does (<c>FUN_140854d10</c>, <b>Observed</b>): not
    /// of the culprit's faction, not carried, not imprisoned (nor is the culprit), with no pending crime of its own; nothing when its faction is hostile to the victim's, when the
    /// victim's is lawless, when it books bounties and the victim's is a bandit faction, or when it regards the culprit as friendly and the victim is
    /// of another faction. Theft and Looting need it to care about the victim (same faction or allied); Looting also that it does not regard the
    /// culprit as friendly; Kidnapping a victim and that care; an Assault a victim that it does not regard as an enemy; a Murder nothing more. One
    /// that regards the culprit as friendly costs it <see cref="BetrayalDelta"/>. A witness then sets the timer to 20 (not for a culprit that is
    /// down) and, police-like of a faction that books bounties, books the crime. With <paramref name="kidnapping"/>, a witness that cares about
    /// the carried body first sets a pending Kidnapping (timer 20) on the carrier (the carrying check that runs before the crime check); with <paramref name="premises"/> (the culprit
    /// inside premises while the sight pass does not run: <b>engine</b> fallback), a witness that does not regard it as an enemy and
    /// <see cref="Trespasses"/> sets a pending Trespassing against its own faction. A culprit is seen by the sight test with the sight ray
    /// (<see cref="SightRay.Sees"/>), a sneaking one only through a full spot meter (<see cref="Spotted"/>). A witness that is the crime's victim keeps
    /// the culprit on its threats list for the sweep's period <paramref name="every"/>, until the next sweep renews it.
    /// </summary>
    void Witnesses(World world, int culprit, CharacterCold c, bool kidnapping, bool premises, float seconds, int every)
    {
        var table = world.Characters;
        var next = table.Next;
        var at = next[culprit].Position;
        bool sneaking = (c.Orders & CharacterOrders.Sneak) != 0;
        float stealth = sneaking ? Orders.Stealth(c) : 0;
        var culpritId = table.IdOf(culprit);
        CharacterCold? body = kidnapping && table.TryResolveNext(c.Carrying, out int b) ? table.Cold(b) : null;
        for (int w = 0; w < table.HighWater; w++)
        {
            if (w == culprit || !next[w].Alive || MayWitness(table, w, c) is not { } wc) continue;
            int wf = wc.Faction;
            float range = AggroSystem.RangeOf(wc, Settings.Sight);
            if (sneaking)
            {
                float v = SightRay.Sees(world, w, culprit, next[w].Position, at, range, stealth);
                if (v <= 0 || !Spotted(world, w, culpritId)) continue;
            }
            else if (SightRay.Sees(world, w, culprit, next[w].Position, at, range) <= 0) continue;
            if (body is not null && !Friendly(wf, c.Faction) && MindsKidnapping(wf, c, body))
                SetPending(world, culprit, CrimeCode.Kidnapping, body.Faction, c.Carrying, KidnapTimer, viaWrapper: true);
            if (premises && !AggroSystem.IsEnemy(Relations, wf, c.Faction) && Trespasses(world, w, culprit))
                SetPending(world, culprit, CrimeCode.Trespassing, wf, table.IdOf(w), MinTimer);
            if (c.Pending is { } p) React(world, w, wc, culprit, c, p, world.Tick + every);
        }
    }

    /// <summary>
    /// The character in slot <paramref name="w"/> as a witness of <paramref name="c"/>'s crime (<see cref="Witnesses"/>' tests on the witness): an
    /// awake NPC, not carried or imprisoned, with no pending crime of its own, of another faction known to the relations; null when it is not one.
    /// </summary>
    CharacterCold? MayWitness(CharacterTable table, int w, CharacterCold c)
    {
        if (table.Cold(w) is not { IsPlayer: false, IsImprisoned: false, Medical.Incapacitated: false } wc) return null;
        if (!wc.CarriedBy.IsNone || wc.Pending is not null) return null;

        return wc.Faction == c.Faction || !Relations.Contains(wc.Faction) ? null : wc;
    }

    /// <summary>
    /// The forced sense made <paramref name="looker"/> a new entry of <paramref name="culprit"/> (<c>FUN_140858f40</c>, <b>Verified</b>: with no entry
    /// it runs the perception update <c>FUN_1408580d0</c> as seen, which runs the witness function <c>FUN_140854d10</c>): a looker that may witness
    /// the culprit's pending crime reacts at once (<see cref="React"/>), as at a sweep. So a failed stealth knock-out's victim, whose Assault is set
    /// before the miss notices the attacker, witnesses it then and keeps it pending (timer 20). Serial.
    /// </summary>
    internal void Sensed(World world, int looker, int culprit)
    {
        var table = world.Characters;
        if (looker == culprit || table.Cold(culprit) is not { Pending: { } p, IsImprisoned: false } c || !c.CarriedBy.IsNone) return;
        if (MayWitness(table, looker, c) is not { } wc) return;

        React(world, looker, wc, culprit, c, p, world.Tick + world.Every(Settings.SweepEveryTicks));
    }

    /// <summary>
    /// One witness's reaction to the culprit's pending crime <paramref name="p"/> (the game's witness function <c>FUN_140854d10</c>, <b>Observed</b>;
    /// see <see cref="Witnesses"/>): the victim among the witnesses keeps the culprit on its threats list until <paramref name="until"/>.
    /// </summary>
    void React(World world, int w, CharacterCold wc, int culprit, CharacterCold c, PendingCrime p, long until)
    {
        var table = world.Characters;
        var culpritId = table.IdOf(culprit);
        int wf = wc.Faction;
        bool friendly = RegardsFriendly(world, w, culprit);
        if (!Reacts(world, wc, c, p, friendly)) return;

        if (friendly && Betrays(p.Code)) Betrayed(world, wc, culpritId, c.Faction, p.Code);
        // The refresh (FUN_140851f40) skips a culprit that is down: a knocked-out culprit's crime runs out under its witnesses' eyes.
        if (!Bodies.CharacterFacts.Down(c))
        {
            p.Timer = WitnessTimer;
            c.RecentlyWanted = RecentlyWantedSeconds;
        }

        var witness = table.IdOf(w);
        // Its squad tags the culprit short-term CRIMINAL (7).
        if (wc.SquadId >= 0) world.SystemOf<Dialogue.DialogueSystem>()?.Memory.Flag(world, wc.SquadId, culpritId, Dialogue.MemoryTag.Criminal);

        // A witness that is the crime's own victim (a trespass's is the witness that called it) takes the culprit as its threat
        // (Character::attackTarget, FUN_140854d10) from inside its own sense pass, so the culprit stays on the list its chooser reads while it
        // witnesses, until the next sweep: its own chooser answers it.
        if (p.Victim == witness) AttackTarget(world, w, culprit, until);
        bool first = !p.Witnessed;
        if (first)
        {
            p.Witnessed = true;
            Note(world, CrimeEventKind.Witnessed, culprit, p.Code, wf, 0, witness);
            // The loot already says "was seen looting".
            if (c.IsPlayer && p.Code != CrimeCode.Looting)
            {
                world.Post(Loc.Tr("{1} has been caught committing {2}!", world.ShownName(c, ShownPlace.Notice), CrimeTable.Name(p.Code)));
            }
        }
        // The original fires the event on every reacting run (docs/game/dialogue.md "Witnesses"). Meitou fires it once per witness at the
        // player's characters, so the victim's guards that see the crime after the first witness still say their attack lines (#1251); at an NPC
        // culprit again on every sweep from a police-like witness not yet fighting, as the guards' lines want a culprit within talking range that
        // does not run (engine choice: lines' repetition limits are not kept). Not a Trespassing: its event-17 line ("intruder law", DA 6 chase
        // forever) needs no range, and repeating it only restarts the chase (#519).
        if (FiresWitnessEvent(world, wc, witness, c, p))
            world.Defer(new Dialogue.DialogueEvent(witness, table.IdOf(culprit), WitnessEvent(world, wc, p)) { Tick = world.Tick + 1 });

        // No stop once booked: the original runs the witness function per looker, so every witness reacts. A booking police far off
        // (in sight range, too far for its lines) would otherwise keep the guards beside the crime from ever reacting.
        if (!p.Booked && IsPoliceLike(world, wc) && Factions.Books(wf)) Book(world, culprit, wf, witness);
    }

    /// <summary>
    /// Whether the reacting witness fires its witness event at the culprit now: at a player's character once per witness and crime
    /// (<see cref="PendingCrime.Told"/>, which it joins); at an NPC culprit when the witness is police-like and not fighting, and the crime is no
    /// Trespassing.
    /// </summary>
    bool FiresWitnessEvent(World world, CharacterCold wc, CharacterId witness, CharacterCold c, PendingCrime p)
    {
        if (!c.IsPlayer) return p.Code != CrimeCode.Trespassing && !wc.InCombat && IsPoliceLike(world, wc);
        if (p.Told.Contains(witness)) return false;

        p.Told.Add(witness);
        return true;
    }

    /// <summary>
    /// A building with residents that a character is inside: its key, the residents' faction and leader, whether it is public
    /// (<see cref="HomePublic"/>), whether it is a shop (<see cref="IsShop"/>), and whether the residents have locked it up
    /// (<see cref="Locks.LockSystem.IsHouseLocked"/>, the original's squad flag <c>homelok</c>).
    /// </summary>
    internal readonly record struct Premises(string Key, int Faction, CharacterId Leader, bool Public, bool Shop, bool LockedUp)
    {
        /// <summary>
        /// Open to visitors: a shop or public, from 7:00 to 22:59 (the original's resident test, <b>Verified</b> <c>FUN_140852350</c>: designation
        /// <c>+0xc4</c> 1 or <c>public day</c>, and the hour).
        /// </summary>
        public bool Open(int hour) => (Shop || Public) && hour is >= OpensAt and <= ClosesAfter;
    }

    /// <summary>
    /// The editor's BuildingDesignation BD_SHOP.
    /// </summary>
    const int ShopDesignation = 1;

    /// <summary>
    /// The hours a public building is open: the first and the last (the game clock's hour int, <c>DAT_1421303d0</c>+0).
    /// </summary>
    public const int OpensAt = 7, ClosesAfter = 22;

    /// <summary>
    /// The key of the building the squad lives in (its AI's own home, else its home building); null for none.
    /// </summary>
    internal static string? HomeKey(Squad q) => (q.Ai?.OwnHome ?? q.HomeBuilding)?.Key;

    /// <summary>
    /// Whether the squad's home <paramref name="key"/> is public (Building vfunc <c>0x290</c>, docs/game/ai-tasks.md "Stranger states"): the
    /// player's Open to public toggle, else the residents' template <c>public day</c>.
    /// </summary>
    internal static bool HomePublic(World world, Squad q, string key) => world.Fixtures.PublicOf(key) ?? q.Ai?.HomePublic ?? true;

    /// <summary>
    /// Whether the house <paramref name="key"/> is a shop: its designation (Building <c>+0xc4</c>, which the squads housed in it set,
    /// <see cref="Locks.BuildingRecord.Designation"/>) is BD_SHOP; none before any squad was housed in it.
    /// </summary>
    static bool IsShop(World world, string key) => world.Fixtures.RecordOf(key)?.Designation == ShopDesignation;

    /// <summary>
    /// The character is chained to <paramref name="looker"/> (<c>FUN_14032e1e0</c>: the chain flag and the chain's owner is the looker; Meitou's
    /// owner is <see cref="Kidnap.ObedientSlave.OwnerOf"/>).
    /// </summary>
    internal static bool ChainedTo(World world, CharacterId looker, CharacterCold c) =>
        Locks.LockSystem.Chained(c) && Kidnap.ObedientSlave.OwnerOf(world, c) == looker;

    /// <summary>
    /// The lock system whose house locks <see cref="LockedUp"/> reads; looked up once.
    /// </summary>
    Locks.LockSystem? _locks;
    /// <summary>
    /// Whether <see cref="_locks"/> has been looked up.
    /// </summary>
    bool _locksLooked;

    /// <summary>
    /// Whether the house of building <paramref name="key"/> is locked up (false without a lock system).
    /// </summary>
    bool LockedUp(World world, string key)
    {
        if (!_locksLooked)
        {
            _locksLooked = true;
            _locks = world.SystemOf<Locks.LockSystem>();
        }
        return _locks?.IsHouseLocked(world, key) ?? false;
    }

    /// <summary>
    /// The premises the character stands inside (<see cref="SightMap.BuildingOf"/>): a town building some squad lives in, the first such squad's (the
    /// original reads the flags of the building's owner platoon, <c>building +0xd0</c>); null outdoors, on a roof, in a building nobody lives in, or
    /// caged.
    /// </summary>
    internal Premises? PremisesOf(World world, int slot)
    {
        if (world.Characters.Cold(slot) is not { IsImprisoned: false } || world.Sight.BuildingOf(slot) is not { } key) return null;
        foreach (var q in world.Squads.All)
            if (HomeKey(q) == key) return new Premises(key, q.Faction, q.Leader, HomePublic(world, q, key), IsShop(world, key), LockedUp(world, key));
        return null;
    }

    /// <summary>
    /// Whether the looker in slot <paramref name="w"/> calls the character in slot <paramref name="t"/> a trespasser (<c>FUN_140852350</c>,
    /// <b>Verified</b>): never outdoors or in premises nobody lives in (<see cref="PremisesOf"/>), never when the looker regards it as friendly
    /// (slot 126) or it is an animal (the access pass tested next is never given, docs/game/economy.md 11.2). In the looker's own home
    /// (<see cref="Resident"/>), else in another's building: a police-like looker, residents whose faction does not regard the target's as an
    /// ally and whose leader does not regard the target as friendly; then when the residents have locked up, or the building is not public and the
    /// target has a slave state. No hour test there.
    /// </summary>
    internal bool Trespasses(World world, int w, int t)
    {
        var table = world.Characters;
        if (PremisesOf(world, t) is not { } house || table.Cold(w) is not { } wc || table.Cold(t) is not { } c) return false;
        if (RegardsFriendly(world, w, t) || SquadRegistry.IsAnimal(c)) return false;
        if (wc.SquadId >= 0 && world.Squads.Find(wc.SquadId) is { } squad && HomeKey(squad) == house.Key)
            return Resident(world, table.IdOf(w), squad, c, house);

        if (!IsPoliceLike(world, wc) || Friendly(house.Faction, c.Faction)) return false;
        if (table.TryResolveNext(house.Leader, out int leader) && RegardsFriendly(world, leader, t)) return false;
        return house.LockedUp || !house.Public && c.SlaveState != 0;
    }

    /// <summary>
    /// The resident case of <see cref="Trespasses"/>: not a target chained to the looker, nor one of the looker's town (ownerships <c>+0x30</c>,
    /// a resident squad's town; none for both counts as the same) of NPC class LAW_ENFORCEMENT, MILITARY or DIPLOMAT; else the residents have
    /// locked up and the home is not <see cref="Premises.Open"/>.
    /// </summary>
    static bool Resident(World world, CharacterId looker, Squad home, CharacterCold c, Premises house)
    {
        if (ChainedTo(world, looker, c)) return false;

        int theirs = c.SquadId >= 0 && world.Squads.Find(c.SquadId) is { TownOwn: true } q ? q.Town : -1;
        bool sameTown = (home.TownOwn ? home.Town : -1) == theirs;
        if (sameTown && c.NpcClass is FundamentalType.LawEnforcement or FundamentalType.Military or FundamentalType.Diplomat) return false;

        return house.LockedUp && !house.Open(world.Hour);
    }

    /// <summary>
    /// Slot 126 (<c>isAlly</c>): the dialogue's reading with the squad memory, else the same or allied factions.
    /// </summary>
    internal bool RegardsFriendly(World world, int a, int b)
    {
        if (world.SystemOf<Dialogue.DialogueSystem>() is { } talk) return talk.RegardsFriendly(world, a, b);

        var table = world.Characters;
        return Friendly(table.Cold(a)!.Faction, table.Cold(b)!.Faction);
    }

    /// <summary>
    /// What a witness's faction loses towards a culprit it regards as friendly that it sees commit a crime against its own faction (the witness
    /// function's ally branch, <c>FUN_140854d10</c>: slot 4 of its table, -20 x 1, <b>Observed</b>; skipped there when the witness's platoon object
    /// <c>+0x210</c> has <c>+0xa8</c> set, not modelled). Once per witness squad while its TEMP_ENEMY tag on the culprit lasts (<see cref="Betrayed"/>).
    /// </summary>
    public const float BetrayalDelta = -20;

    /// <summary>
    /// The witness function's ally branch (<c>FUN_140854d10</c>, <b>Verified</b>): the witness's squad drops TEMP_ALLY on the culprit and tags it
    /// TEMP_ENEMY, so the squad no longer regards it as friendly (slot 126) and the branch does not run again on the next sweep, then its faction
    /// loses <see cref="BetrayalDelta"/> towards the culprit's. Without the tag every witness took 20 off on every sweep, and a town's allies went
    /// hostile to it within seconds of one of them hitting a townsman (#948).
    /// </summary>
    void Betrayed(World world, CharacterCold witness, CharacterId culprit, int culpritFaction, CrimeCode code)
    {
        if (witness.SquadId >= 0 && world.SystemOf<Dialogue.DialogueSystem>() is { } talk)
        {
            talk.Memory.ClearTempAlly(world, witness.SquadId, culprit);
            talk.Memory.Flag(world, witness.SquadId, culprit, Dialogue.MemoryTag.TempEnemy);
        }

        var cause = new RelationCause(RelationCauseType.Crime, culprit);
        PlayerRelations.Add(world, Relations, witness.Faction, culpritFaction, BetrayalDelta, cause, $"a friend's crime witnessed ({code})");
    }

    /// <summary>
    /// The crimes whose witness path reaches the ally branch: every code but Burglary, Looting, Trespassing, Crop Theft and Uniform Theft (their
    /// cases leave the switch elsewhere).
    /// </summary>
    static bool Betrays(CrimeCode code) =>
        code is not (CrimeCode.Burglary or CrimeCode.Looting or CrimeCode.Trespassing or CrimeCode.CropTheft or CrimeCode.UniformTheft);

    /// <summary>
    /// DA 33 CRIME_ALARM said by <paramref name="speaker"/> at <paramref name="target"/> (<c>FUN_140680560</c>, <b>Observed</b>): the speaker
    /// notices the target (the forced sense with its alarm flag, <c>FUN_140858f40</c>, <b>Verified</b>: the sense entry is marked seen and alert
    /// 5 s; no attack order and no squad tag, so a Holy Nation civilian crying out at a skeleton starts no fight, #648), the target's crime timer
    /// is set to 30 and it is recently wanted (<c>FUN_140851f40</c>); with no crime pending, a target inside the speaker's own home that is not
    /// public gets a pending Trespassing. Then the alarm of the speaker's town is raised at the target for its pending crime
    /// (<see cref="TownAlarm"/>).
    /// </summary>
    public void Alarm(World world, int speaker, int target)
    {
        var table = world.Characters;
        if (table.Cold(speaker) is not { } s || table.Cold(target) is not { } t) return;
        var targetId = table.IdOf(target);
        world.SystemOf<AggroSystem>()?.Alarm(world, table.IdOf(speaker), targetId);
        var squad = s.SquadId >= 0 ? world.Squads.Find(s.SquadId) : null;
        int town = squad?.Town ?? -1;
        if (t.Pending is { } p) p.Timer = AlarmTimer;
        t.RecentlyWanted = RecentlyWantedSeconds;
        if (town < 0) return;   // the original also needs the speaker's faction not hostile to its town's (a town squad's is its town's here)
        if (t.Pending is null && squad!.Ai?.HomePublic == false && HomeKey(squad) is { } home && world.Sight.BuildingOf(target) == home
            && SetPending(world, target, CrimeCode.Trespassing, s.Faction, table.IdOf(speaker), MinTimer)) t.Pending!.Timer = AlarmTimer;
        if (t.Pending is { } crime) TownAlarm(world, target, town, crime.Code, crime.VictimFaction);
    }

    /// <summary>
    /// The alarm of <paramref name="town"/> raised at <paramref name="target"/> (<c>FUN_1409335e0</c>, <b>Observed</b>), for the crime
    /// <paramref name="code"/> against <paramref name="faction"/>: nothing for code 0; "Alarm raised" at most every 30 s for the player's, every
    /// inventory window closing with it (<see cref="World.CloseInventories"/>); the town
    /// keeps one alarm per code and faction, a repeated raise only moving it to the target (the life, set at the first raise, is not extended:
    /// <b>Verified</b>, the merge path of <c>FUN_1409335e0</c> leaves <c>+0x24</c> alone). The town's investigators answer it
    /// (<see cref="Investigate"/>).
    /// </summary>
    public void TownAlarm(World world, int target, int town, CrimeCode code, int faction)
    {
        var table = world.Characters;
        if (town < 0 || code == CrimeCode.None || table.Cold(target) is not { } t) return;
        if (t.IsPlayer && world.GameHours - _lastAlarmHours >= AlarmPostSeconds / World.SecondsPerGameHour)
        {
            _lastAlarmHours = world.GameHours;
            world.CloseInventories();   // the raise queues system message 9, close all inventories, with the message (Verified)
            world.Post(Loc.Tr("Alarm raised"));
        }

        var at = table.Next[target].Position;
        int place = world.Sight.LocationOf(target), floor = FloorOf(world, target, at);
        foreach (var a in _alarms)
            if (a.Town == town && a.Code == code && a.Faction == faction)
            {
                a.Position = at;
                a.Place = place;
                a.Floor = floor;
                return;
            }
        _alarms.Add(new TownAlarm { Town = town, Code = code, Faction = faction, Position = at, Place = place, Floor = floor });
    }

    /// <summary>
    /// The towns' raised alarms, oldest first.
    /// </summary>
    readonly List<TownAlarm> _alarms = [];

    /// <summary>
    /// The towns' raised alarms, oldest first.
    /// </summary>
    public IReadOnlyList<TownAlarm> Alarms => _alarms;

    /// <summary>
    /// How far an investigator answers an alarm (<c>FUN_14092a810</c>'s 375, <b>Verified</b> literal): at that range for an alarm raised outdoors, at
    /// half of it for one raised in a building, whatever the crime.
    /// </summary>
    public const float AlarmReach = 375;
    /// <summary>
    /// Within this of the alarm an investigator stops and looks about (100, <b>Verified</b>: the squared 10000), for <see cref="LookAboutSeconds"/>
    /// (10; 15 at the task's start).
    /// </summary>
    public const float AlarmLookRange = 100, LookAboutSeconds = 10, FirstLookAboutSeconds = 15;
    /// <summary>
    /// An investigator runs to this far short of the alarm, on its own side (30, <b>Verified</b>).
    /// </summary>
    public const float AlarmApproach = 30;

    /// <summary>
    /// An alarm in a building whose entrances are all closed is skipped for an investigator in the same place.
    /// </summary>
    internal static bool AlarmSkipped(bool closed, int alarmPlace, int investigatorPlace) =>
        closed && alarmPlace >= 0 && alarmPlace == investigatorPlace;

    /// <summary>
    /// The reach to an alarm: <see cref="AlarmReach"/>, halved for one raised in a building, halved again when that building is closed.
    /// </summary>
    internal static float ReachOfAlarm(bool inBuilding, bool closed) => AlarmReach * (inBuilding ? 0.5f : 1) * (inBuilding && closed ? 0.5f : 1);

    /// <summary>
    /// The alarm an investigator of <paramref name="town"/> at <paramref name="at"/> on <paramref name="floor"/> answers (<c>FUN_14092a810</c>,
    /// <b>Verified</b>): the nearest within <see cref="AlarmReach"/>, half of it when the alarm was raised in a building
    /// (<see cref="TownAlarm.Place"/>), whatever the crime; with <paramref name="faction"/> (goal 210, allies only) only its faction's. An alarm in a
    /// building is skipped when its floor is more than one from the investigator's. An alarm inside a closed building (no open entrance,
    /// <see cref="Locks.FixtureRegistry.HasOpenDoor"/>) is skipped for an investigator in the same place and halves the reach a second time otherwise
    /// (needs <paramref name="place"/> and <paramref name="world"/>). Engine: its skip of any other floor in some buildings (building byte
    /// <c>+0xc2</c>, meaning <b>Unknown</b>), are not modelled. Null when none.
    /// </summary>
    public TownAlarm? AlarmFor(int town, Vector3 at, int faction = -1, int floor = 0, int place = -1, World? world = null)
    {
        TownAlarm? best = null;
        float bestD = float.MaxValue;
        foreach (var a in _alarms)
        {
            if (a.Town != town || faction >= 0 && a.Faction != faction) continue;
            if (a.Place >= 0 && Math.Abs(a.Floor - floor) > 1) continue;
            // An alarm in a building whose every entrance is closed (FUN_1405499a0 false, FUN_14092a810:52-62): skipped for an investigator inside
            // it, else halved a second time.
            bool closed = a.Place >= 0 && world is not null && !world.Fixtures.HasOpenDoor(world.Sight.PlaceKey(a.Place));
            if (AlarmSkipped(closed, a.Place, place)) continue;
            float reach = ReachOfAlarm(a.Place >= 0, closed);
            float d = Vector3.DistanceSquared(a.Position, at);
            if (d < reach * reach && d < bestD) (best, bestD) = (a, d);
        }
        return best;
    }

    /// <summary>
    /// The floor of the building the character stands in, or on top of, at <paramref name="at"/> (<c>Character</c> vfunc <c>+0x60</c>, an alarm's
    /// <c>+0x60</c>; <see cref="SightMap.FloorOf"/>, #796); 0 outdoors.
    /// </summary>
    public static int FloorOf(World world, int slot, Vector3 at) =>
        world.Sight.LocationOf(slot) is >= 0 and var place ? world.Sight.FloorAt(world.Sight.PlaceKey(place), at) : 0;

    /// <summary>
    /// The investigators' serial step (<c>Task_InvestigateAlarms</c> update, <b>Observed</b>), for each NPC whose AI acts out goal 209 or 210
    /// (<see cref="Ai.AiBehaviour.Investigate"/>): within <see cref="AlarmLookRange"/> of its alarm it counts <see cref="Ai.AiMind.LookAbout"/> down
    /// and clears the alarm when it runs out; on the way there it keeps the count at <see cref="LookAboutSeconds"/> and looks for suspects
    /// (<see cref="LookForSuspects"/>). Engine: "sees" is the witnesses' sight test (<see cref="SuspectView"/>), standing for the original's sense
    /// list (seen or heard).
    /// </summary>
    void Investigate(World world, float seconds)
    {
        var table = world.Characters;
        var next = table.Next;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!next[i].Alive || table.Cold(i) is not { Ai: { Behaviour: AiBehaviour.Investigate, Goal: { } goal, Squad.Ai: { } board } mind } cold
                || !AiSystem.Holds(next[i], cold))
            {
                continue;
            }
            var alarm = AlarmFor(board.Town, next[i].Position,
                goal.Task.Type == Data.Gameplay.Ai.TaskType.InvestigateAlarmsAlliesOnly ? cold.Faction : -1, FloorOf(world, i, next[i].Position),
                world.Sight.LocationOf(i), world);
            if (alarm is null) continue;
            if (Vector3.DistanceSquared(alarm.Position, next[i].Position) <= AlarmLookRange * AlarmLookRange)
            {
                mind.LookAbout -= seconds;
                if (mind.LookAbout < 0) _alarms.Remove(alarm);
                continue;
            }
            mind.LookAbout = LookAboutSeconds;
            LookForSuspects(world, i, cold);
        }
    }

    /// <summary>
    /// The investigator's look on the way to its alarm (<c>FUN_140354260</c>, <b>Verified</b>): every character it sees that is not down, not of its
    /// faction or an ally, with a pending crime against its faction or an ally, its squad tags TEMP_ENEMY and it takes as a threat
    /// (<see cref="AttackTarget"/>, all of them); its own chooser picks whom it fights. No can-attack test on this path.
    /// </summary>
    void LookForSuspects(World world, int i, CharacterCold cold)
    {
        var table = world.Characters;
        var next = table.Next;
        float range = AggroSystem.RangeOf(cold, Settings.Sight);
        for (int c = 0; c < table.HighWater; c++)
        {
            if (c == i || !next[c].Alive || table.Cold(c) is not { Pending: { } p, Medical.Incapacitated: false } cc
                || Friendly(cold.Faction, cc.Faction) || !Friendly(cold.Faction, p.VictimFaction))
            {
                continue;
            }

            if (Vector3.DistanceSquared(next[c].Position, next[i].Position) > range * range || SuspectView(world, i, c, cc, range) <= 0) continue;

            AttackTarget(world, i, c);
        }
    }

    /// <summary>
    /// <c>Character::attackTarget</c> (<c>FUN_1405cb0a0</c>, <b>Verified</b>): <paramref name="target"/> goes on <paramref name="slot"/>'s threats
    /// list (AI <c>+0x78</c>, SensoryData <c>+0x50</c>; <see cref="AggroSystem.Threaten"/>: forced sense, the entry), its squad tags the target
    /// TEMP_ENEMY, and its AI is updated at once (Meitou: it thinks at the next tick). No order and no fight start: the list's readers are 32 Self
    /// preservation's desire and row 13's finder, the tag the attack-on-sight finders' (docs/game/economy.md "Witnesses"). With
    /// <paramref name="witnessedUntil"/> (a crime's victim witnessing it, the push from its own sense pass) the target stays on the list its chooser
    /// reads until that tick (<see cref="AggroSystem.Witness"/>); else the push raises only the immediate-threat flag. Serial.
    /// </summary>
    static void AttackTarget(World world, int slot, int target, long witnessedUntil = -1)
    {
        var aggro = world.SystemOf<AggroSystem>();
        if (witnessedUntil >= 0) aggro?.Witness(world, slot, target, witnessedUntil);
        else
            aggro?.Threaten(world, slot, target);
        world.SystemOf<Dialogue.DialogueSystem>()?.TagEnemy(world, slot, target);
        if (world.Characters.Cold(slot) is not { } cold) return;

        Ai.DecisionSystem.Request(cold);
        if (cold.Ai is { } mind) mind.Replan = true;
    }

    /// <summary>
    /// How well the looker in <paramref name="looker"/> sees the suspect in <paramref name="suspect"/> as a witness does
    /// (<see cref="SightRay.Sees"/>'s view factor: the sight test, with the ray where it applies, and for a sneaker a full spot meter); 0 when it
    /// does not. Read by the alarm's investigators (#897: one went after a sneaking criminal through a floor) and the arrest system's bounty
    /// spotting.
    /// </summary>
    internal float SuspectView(World world, int looker, int suspect, CharacterCold c, float range)
    {
        var next = world.Characters.Next;
        bool sneaking = (c.Orders & CharacterOrders.Sneak) != 0;
        if (sneaking && !Spotted(world, looker, world.Characters.IdOf(suspect))) return 0;

        return SightRay.Sees(world, looker, suspect, next[looker].Position, next[suspect].Position, range, sneaking ? Orders.Stealth(c) : -1);
    }

    /// <summary>
    /// Adds the town alarms (town, code, faction, position, life; place and floor for one raised in a building) to the world hash, only while there
    /// are any.
    /// </summary>
    public void Hash(ref StateHasher hasher)
    {
        if (_alarms.Count == 0) return;
        hasher.Add(_alarms.Count);
        foreach (var a in _alarms)
        {
            hasher.Add(a.Town);
            hasher.Add((int)a.Code);
            hasher.Add(a.Faction);
            hasher.Add(a.Position);
            hasher.Add(a.Life);
            // only an alarm raised in a building, so the others hash as before
            if (a.Place >= 0)
            {
                hasher.Add(a.Place);
                hasher.Add(a.Floor);
            }
        }
    }

    /// <summary>
    /// The crime timer DA 33 sets (the literal 30.0 passed to <c>FUN_140851f40</c>), and how often "Alarm raised" may be posted (real seconds in the
    /// original; game seconds here).
    /// </summary>
    public const float AlarmTimer = 30, AlarmPostSeconds = 30;
    /// <summary>
    /// The game seconds a town alarm lives after it is created (<c>FUN_1409335e0</c> sets 8.0 for a new alarm only, a repeated raise does not extend
    /// it; the town update <c>FUN_14092bfe0</c> counts it down and deletes it below 0: <b>Verified</b>).
    /// </summary>
    public const float AlarmLife = 8;
    /// <summary>
    /// The game hour "Alarm raised" was last posted at (<see cref="AlarmPostSeconds"/> apart).
    /// </summary>
    double _lastAlarmHours = double.NegativeInfinity;

    /// <summary>
    /// The dialogue event the witness says at the culprit (<c>FUN_140854d10</c>'s switch on the crime code, docs/game/dialogue.md "Event sources").
    /// </summary>
    static int WitnessEvent(World world, CharacterCold w, PendingCrime p) => p.Code switch
    {
        CrimeCode.Burglary or CrimeCode.Terrorism => 43,   // WITNESS_THIEF_OR_LOCKPICK
        CrimeCode.Theft or CrimeCode.Fencing or CrimeCode.Looting =>
            // THIEF_CAUGHT_STEALING_FROM_ME, WITNESS_LOOTING_ALLY
            world.Characters.TryResolveNext(p.Victim, out int v) && world.Characters.Cold(v) is { SquadId: >= 0 } vc && vc.SquadId == w.SquadId
                ? 8 : 42,
        CrimeCode.Trespassing => 17,
        CrimeCode.CropTheft => 57,
        CrimeCode.Enslaving or CrimeCode.Kidnapping => 58,
        CrimeCode.UniformTheft => 34,
        _ => 41,   // WITNESS_GENERIC_ASSAULT
    };

    /// <summary>
    /// The witness function's tests for one witness and one pending crime (see <see cref="Witnesses"/>).
    /// </summary>
    bool Reacts(World world, CharacterCold w, CharacterCold culprit, PendingCrime p, bool friendlySuspect)
    {
        int wf = w.Faction, vf = p.VictimFaction;
        if (!Relations.Contains(vf) || Relations.IsHostile(wf, vf) || Factions.Lawless(vf)) return false;
        if (Factions.Books(wf) && Factions.Bandit(vf)) return false;
        if (friendlySuspect && vf != wf) return false;
        bool cares = Friendly(wf, vf);
        var victim = world.Characters.TryResolveNext(p.Victim, out int v) ? world.Characters.Cold(v) : null;
        return p.Code switch
        {
            CrimeCode.Theft or CrimeCode.Fencing => cares,
            CrimeCode.Looting => cares && !friendlySuspect,
            CrimeCode.Kidnapping => victim is not null && cares,
            CrimeCode.Assault => victim is not null && !AggroSystem.IsEnemy(Relations, wf, victim.Faction),
            _ => true,
        };
    }

    /// <summary>
    /// The bounty lines of a character for the HUD (<see cref="BountyBook.Lines"/>).
    /// </summary>
    public static IReadOnlyList<BountyLine> Lines(CharacterCold c, long tick, float tickSeconds, int local = -1) =>
        BountyBook.Lines(c, tick, tickSeconds, local);

    /// <summary>
    /// Records a pending, witnessed, booked or expired crime: <see cref="Report"/> (with the culprit's pending crime, none for an expiry or without
    /// <paramref name="withPending"/>), then the world event log's line.
    /// </summary>
    internal void Note(World world, CrimeEventKind kind, int culprit, CrimeCode code, int faction, int amount, CharacterId witness,
        bool withPending = true)
    {
        var pending = kind == CrimeEventKind.Expired || !withPending ? null : world.Characters.Cold(culprit)?.Pending;
        Report(world, kind, culprit, code, faction, amount, witness, pending);
        world.Events.Line(world, "crime", CrimeLine(world, kind, culprit, code, faction, amount, witness));
    }

    /// <summary>
    /// Adds an event to <see cref="Log"/> (with <see cref="CrimeSettings.RecordLog"/>) and passes it to the crime observers
    /// (<see cref="ICrimeObserver"/>), once the change it reports is made, with <paramref name="pending"/>'s victim; neither builds the event
    /// without a log or an observer.
    /// </summary>
    void Report(World world, CrimeEventKind kind, int culprit, CrimeCode code, int faction, int amount, CharacterId witness, PendingCrime? pending)
    {
        if (!Settings.RecordLog && !world.CrimeObservers.Active) return;

        var crime = new CrimeEvent(world.Tick, kind, world.Characters.IdOf(culprit), code, faction, amount, witness)
        {
            Victim = pending?.Victim ?? CharacterId.None,
            VictimFaction = pending?.VictimFaction ?? -1,
            VictimHolder = pending?.VictimHolder,
        };
        if (Settings.RecordLog) _log.Add(crime);
        world.CrimeObservers.Observe(world, crime);
    }

    /// <summary>
    /// Reports <paramref name="ended"/>, the pending crime the culprit no longer has (<see cref="CrimeEventKind.Lapsed"/>); no event log line, as
    /// lapses are routine.
    /// </summary>
    void Lapsed(World world, int culprit, PendingCrime ended) =>
        Report(world, CrimeEventKind.Lapsed, culprit, ended.Code, ended.VictimFaction, 0, CharacterId.None, ended);

    /// <summary>
    /// Drops the pending crime of the character in <paramref name="slot"/> (a change of side: recruited, bought free, dismissed, stored off screen)
    /// and reports it (<see cref="CrimeEventKind.Lapsed"/>); nothing without one. Serial phases.
    /// </summary>
    internal static void DropPending(World world, int slot, CharacterCold c)
    {
        if (c.Pending is not { } ended) return;

        c.Pending = null;
        world.SystemOf<CrimeSystem>()?.Lapsed(world, slot, ended);
    }

    /// <summary>
    /// Removes <paramref name="entry"/> from the ledger of the character in <paramref name="slot"/> other than by expiry (a fine, a bail, a release,
    /// dialogue, the debug menu), the ledger going once empty, and reports it (<see cref="CrimeEventKind.Cleared"/>, with the entry's faction and
    /// amount). Serial phases.
    /// </summary>
    internal static void ClearBounty(World world, int slot, CharacterCold c, BountyLedger ledger, BountyEntry entry)
    {
        ledger.Remove(entry);
        if (ledger.Count == 0 && ReferenceEquals(c.Bounties, ledger)) c.Bounties = null;

        world.SystemOf<CrimeSystem>()?.Report(world, CrimeEventKind.Cleared, slot, CrimeCode.None, entry.Faction, entry.Amount, CharacterId.None,
            null);
    }

    /// <summary>
    /// The event log's line of a crime event.
    /// </summary>
    static string CrimeLine(World world, CrimeEventKind kind, int culprit, CrimeCode code, int faction, int amount, CharacterId witness)
    {
        string who = EventLog.Who(world, culprit), against = EventLog.FactionName(world, faction);
        return kind switch
        {
            CrimeEventKind.Pending => $"{who} committed {code} against {against}",
            CrimeEventKind.Witnessed => $"{EventLog.Who(world, witness)} saw {who} commit {code}",   // the faction noted is the witness's
            CrimeEventKind.Booked => $"{against} put a bounty on {who} for {code}: {amount} in all",
            CrimeEventKind.Expired => $"{who}'s bounty with {against} expired",
            _ => $"{kind}: {who}, {code}, {against}",
        };
    }

    #endregion
}