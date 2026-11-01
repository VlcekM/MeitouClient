using System.Numerics;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation.Crime;

/// <summary>
/// Crimes and bounties (docs/simulation.md "Crimes and bounties"; the original's rules in docs/game/economy.md section 11 and docs/game/carry-loot.md 4.3 and 4.6). Everything here runs in
/// serial phases: the deeds are reported by the systems that see them (combat's <c>Inputs</c> and commit, the carry system's <c>SlowWorld</c>), the witness sweep and the bounty expiry run in
/// this system's <c>SlowWorld</c>, in slot order. The state lives on the characters (<see cref="CharacterCold.Pending"/>, <see cref="CharacterCold.Bounties"/>) and is hashed only while set.
/// <list type="bullet">
/// <item><b>Pending</b> (<see cref="SetPending"/>, the game's <c>FUN_140852c80</c>): a deed first becomes a pending crime on the culprit, unless the culprit is police-like, the victim is
/// of the culprit's own faction or of a lawless one. An Assault does not replace another pending crime.</item>
/// <item><b>Witnesses</b> (<see cref="Sweep"/>, after <c>FUN_140854d10</c>): an awake NPC of another faction that sees the culprit and cares about the victim (by the crime) sets the timer
/// to 20; only a police-like witness of a faction that books bounties books it (<see cref="Book"/>, <c>FUN_140853ec0</c>): the crime table's amount into the ledger entry of the
/// <b>witness's</b> faction (<c>FUN_140852010</c>).</item>
/// <item><b>Police</b>: a hit between a police-like character and another books an Assault on the other at once (<c>FUN_140665fa0</c>); police-like NPCs spot, fine and arrest a character wanted by
/// their faction (<see cref="ArrestSystem"/>).</item>
/// <item><b>Expiry</b>: an entry lasts 0.04 hours per cat since its last raise, for ever from 10000 (<see cref="CrimeTable.LifetimeHours"/>).</item>
/// </list>
/// An NPC is a culprit only of an Assault, never booked (engine choice: an NPC's looting and carrying raise no crime; its attacks on characters not hostile to them, such as hungry
/// bandits on a town's visitors, are witnessed so the town's police step in).
/// </summary>
public sealed partial class CrimeSystem(FactionRelations relations, CrimeSettings? settings = null) : ITickSystem, IStateHashed
{
    /// <summary>The freshness a pending crime starts with at least (the setter's minimum, <b>Observed</b>), and the timers the game gives some deeds.</summary>
    public const float MinTimer = 3, WitnessTimer = 20, PoliceHitTimer = 30, KidnapTimer = 20;
    /// <summary>The "recently wanted" marker a booking or a witnessed crime sets (<see cref="CharacterCold.RecentlyWanted"/>; the game's 250.0, counted down here as game seconds: engine choice).</summary>
    public const float RecentlyWantedSeconds = 250;

    readonly List<Booking> bookings = [];
    long bookingsTick = -1;

    /// <summary>The bookings of this tick by a witness (the police-like NPC that booked, the culprit, the faction): the arrest system's witnessed-crime trigger. Not world state.</summary>
    public IReadOnlyList<Booking> BookingsOf(long tick) => bookingsTick == tick ? bookings : [];

    readonly List<CrimeEvent> log = [];

    public CrimeSettings Settings { get; } = settings ?? new CrimeSettings();
    public CrimeFactions Factions { get; } = new(relations);
    public FactionRelations Relations => Factions.Relations;

    /// <summary>What happened (with <see cref="CrimeSettings.RecordLog"/>).</summary>
    public IReadOnlyList<CrimeEvent> Log => log;

    // ------------------------------------------------------------------ who is who

    /// <summary>
    /// Police-like (the game's <c>FUN_140599600</c>, <b>Observed</b>): never of a BANDIT-type faction; <c>NPC class</c> LAW_ENFORCEMENT; or MILITARY when the character's own
    /// faction's object matches its squad's (read here as: a member of a resident squad of a town, of its own faction; roaming military are not police: engine reading). The
    /// player's characters never are (engine: their status object has no such link).
    /// </summary>
    public bool IsPoliceLike(World world, CharacterCold c)
    {
        if (c.IsPlayer || c.SlaveState != 0 || Factions.Bandit(c.Faction)) return false;   // engine: a slave is never police, whatever its record's class
        if (c.NpcClass == FundamentalType.LawEnforcement) return true;
        return c.NpcClass == FundamentalType.Military && c.SquadId >= 0 && world.Squads.Find(c.SquadId) is { TownOwn: true } squad && squad.Faction == c.Faction;
    }

    /// <summary>A faction regards another as friendly: the same, or allied (relation 50 or more), as the carry system reads the game's "regards as friendly".</summary>
    public bool Friendly(int a, int b) => a == b || (Relations.Contains(a) && Relations.Contains(b) && Relations.IsAlly(a, b));

    /// <summary>The character has an unclaimed bounty of <paramref name="faction"/> (what a police-like NPC of that faction attacks it for).</summary>
    public static bool IsWantedBy(CharacterCold c, int faction) => c.Bounties?.Of(faction) is { Amount: > 0, Claimed: false };

    // ------------------------------------------------------------------ deeds

    /// <summary>
    /// Sets a pending crime on the culprit (the game's setter <c>FUN_140852c80</c>, <b>Verified</b>): refused for a police-like culprit, an imprisoned one (<c>+0x2f8</c> = 2), a victim of the culprit's own faction,
    /// for a free culprit (slave state 0) a victim of the faction it is seen as (<see cref="ArrestSystem.SeenFaction"/>: a working uniform's; <c>FUN_140852c20</c>) and a lawless
    /// victim faction; an Assault does not replace another pending crime (it raises that one's timer to 20); the same code again keeps the booked flag; a new code clears it. The timer
    /// is at least <see cref="MinTimer"/>. True when the crime is now pending. <paramref name="viaWrapper"/>: the original's callers that go through its wrapper <c>FUN_140852e10</c> (police hit,
    /// caught stealing, fencing, kidnapping seen) also drop the culprit's <see cref="CharacterCold.Freed"/> flag when the crime is set (the other callers do not; <b>Verified</b>). The wrapper also writes the recently-wanted marker first,
    /// which makes the uniform test a no-op for its callers, so it is skipped for them.
    /// </summary>
    public bool SetPending(World world, int culprit, CrimeCode code, int victimFaction, CharacterId victim, float timer, bool viaWrapper = false)
    {
        var c = world.Characters.Cold(culprit);
        if (c is null || !c.IsPlayer && code is not (CrimeCode.Assault or CrimeCode.Looting) || IsPoliceLike(world, c) || c.IsImprisoned || victimFaction == c.Faction
            || !viaWrapper && c.SlaveState == 0 && victimFaction == ArrestSystem.SeenFaction(c) || Factions.Lawless(victimFaction)) return false;
        timer = MathF.Max(timer, MinTimer);
        if (c.Pending is { } p && p.Code != code && code == CrimeCode.Assault)
        {
            p.Timer = MathF.Max(p.Timer, WitnessTimer);
            return false;
        }
        if (c.Pending is { } same && same.Code == code)
        {
            same.VictimFaction = victimFaction;
            same.Victim = victim;
            same.Timer = MathF.Max(same.Timer, timer);
            if (viaWrapper) c.Freed = false;
            return true;
        }
        c.Pending = new PendingCrime { Code = code, VictimFaction = victimFaction, Victim = victim, Timer = timer };
        if (viaWrapper) c.Freed = false;
        Note(world, CrimeEventKind.Pending, culprit, code, victimFaction, 0, CharacterId.None);
        return true;
    }

    /// <summary>
    /// An attack order on <paramref name="victim"/> (not a defensive one): an Assault (the game's <c>FUN_140334070</c>, <b>Verified</b>, run when an attack task starts) unless the victim is
    /// wanted by the attacker's faction, is a bandit (<c>NPC class</c> 8), has a pending crime of its own, or is in melee combat mode (<paramref name="victimFighting"/>,
    /// <c>Character::isInCombatMode</c> <c>FUN_140436610</c>), or has the "ordered" byte <c>+0x114</c> set (<see cref="CharacterCold.Ordered"/>: an earlier ordered attack still running; <b>Verified</b>).
    /// The attacker gets that byte before the booking, whether or not the crime is then set. Engine choice, no counterpart in the original: a victim whose faction is hostile to the attacker's is not booked for. An NPC attacker counts too, but not an animal (engine
    /// choice), and the victim may be one of the player's characters.
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
    /// Items taken from a downed character (the carry system's take-all): Looting (code 10, the right-click transfer's code for a downed owner, <b>Verified</b> call site) unless the game's
    /// permission test (<c>FUN_140793940</c>, <b>Observed</b>) allows it: the victim is a slave (<c>NPC class</c> SLAVE, or slave state 1 or 2; see <see cref="LootAllowed"/>), a bandit, or has any bounty; or the taker is LAW_ENFORCEMENT,
    /// MILITARY or DIPLOMAT.
    /// </summary>
    public void Loot(World world, int looter, int victim)
    {
        var table = world.Characters;
        if (table.Cold(looter) is not { } l || table.Cold(victim) is not { } v || !IsLootTheft(l, v)) return;
        SetPending(world, looter, CrimeCode.Looting, v.Faction, table.IdOf(victim), MinTimer);
    }

    /// <summary>
    /// Whether the player's taking from a downed character is a theft: the permission test of <see cref="Loot"/> (<c>FUN_140793940</c>, <b>Observed</b>). The carry system marks what such a
    /// loot takes as stolen from the victim's faction (<see cref="Items.ItemInstance.StolenFrom"/>).
    /// </summary>
    public static bool IsLootTheft(CharacterCold looter, CharacterCold victim) => looter.IsPlayer && !victim.IsPlayer && !LootAllowed(looter, victim);

    /// <summary>
    /// <c>Character::isItOkForMeToLoot</c> (<c>FUN_140793940</c>, <b>Observed</b>) asked with no item: yes when the victim is a slave or escaped slave (slave state 1 or 2; Meitou also
    /// takes <c>NPC class</c> SLAVE), a bandit or has any bounty, or the looter is LAW_ENFORCEMENT, MILITARY or DIPLOMAT. The original's "my slave" test is covered by the slave state.
    /// </summary>
    public static bool LootAllowed(CharacterCold looter, CharacterCold victim) =>
        victim.SlaveState is 1 or 2 || victim.NpcClass is FundamentalType.Slave or FundamentalType.Bandit || victim.Bounties is { Any: true }
        || looter.NpcClass is FundamentalType.LawEnforcement or FundamentalType.Military or FundamentalType.Diplomat;

    /// <summary>
    /// An NPC starting to loot a body (<c>Task_Loot_AI</c> start, <c>FUN_140355ad0</c>, <b>Observed</b>; not for a police-like looter, which confiscates instead): a pending Looting against
    /// the victim's faction when the looter has any bounty or is a bandit (<c>NPC class</c> 8); then, when it takes more than one weapon (<paramref name="everything"/>: food only, or a body
    /// that is not the player's side or is dead), event 33 LOOTING_EVERYTHING at the victim and a pending Looting unless <see cref="LootAllowed"/>; else, when it is after a weapon,
    /// event 32 LOOTING_WEAPON_ONLY (the original's crime test there needs the looter to hold a weapon, which it never does on that path).
    /// </summary>
    public void NpcLooting(World world, int looter, int victim, bool everything, bool wantsWeapon)
    {
        var table = world.Characters;
        if (table.Cold(looter) is not { } l || table.Cold(victim) is not { } v || IsPoliceLike(world, l)) return;
        var victimId = table.IdOf(victim);
        if (l.Bounties is { Any: true } || l.NpcClass == FundamentalType.Bandit) SetPending(world, looter, CrimeCode.Looting, v.Faction, victimId, MinTimer);
        if (everything)
        {
            world.Defer(new Dialogue.DialogueEvent(table.IdOf(looter), victimId, LootingEverythingEvent) { Tick = world.Tick + 1 });
            if (!LootAllowed(l, v)) SetPending(world, looter, CrimeCode.Looting, v.Faction, victimId, MinTimer);
        }
        else if (wantsWeapon) world.Defer(new Dialogue.DialogueEvent(table.IdOf(looter), victimId, LootingWeaponEvent) { Tick = world.Tick + 1 });
    }

    /// <summary>The dialogue events an NPC looter says at the body (<b>Verified</b> literals in <c>FUN_140355ad0</c>): 32 LOOTING_WEAPON_ONLY, 33 LOOTING_EVERYTHING.</summary>
    public const int LootingWeaponEvent = 32, LootingEverythingEvent = 33;

    /// <summary>
    /// A landed hit between <paramref name="attacker"/> and <paramref name="defender"/> (the game's hit outcome <c>FUN_140665fa0</c>, <b>Observed</b>): when exactly one of them is
    /// police-like, the other gets a pending Assault (timer 30) against the police faction, and whatever crime it has pending is booked with that faction at once. The game's code reads
    /// as charging the non-police side whichever way the blow went (being hit by the police counts too).
    /// </summary>
    public void Hit(World world, int attacker, int defender)
    {
        var table = world.Characters;
        if (table.Cold(attacker) is not { } a || table.Cold(defender) is not { } d) return;
        // Engine choice: an NPC's landed blow renews its Assault, which its attack order set with 3 s that often run out before the attacker reaches its victim.
        if (!a.IsPlayer) Assault(world, attacker, defender);
        bool pa = IsPoliceLike(world, a), pd = IsPoliceLike(world, d);
        if (pa == pd) return;
        int culprit = pd ? attacker : defender, police = pd ? defender : attacker;
        var c = table.Cold(culprit)!;
        var p = table.Cold(police)!;
        if (!c.IsPlayer) return;
        SetPending(world, culprit, CrimeCode.Assault, p.Faction, table.IdOf(police), PoliceHitTimer, viaWrapper: true);
        if (c.Pending is not null) Book(world, culprit, p.Faction, table.IdOf(police));
    }

    // ------------------------------------------------------------------ booking

    /// <summary>
    /// Books the culprit's pending crime with <paramref name="faction"/> (the game's <c>FUN_140853ec0</c>, <b>Observed</b>): once per pending crime (the booked flag), not for a culprit of
    /// <c>NPC class</c> BANDIT, DIPLOMAT or LAW_ENFORCEMENT, and Enslaving adds nothing. The amount of the crime table is added to the faction's entry, the crime's bit set and the time of
    /// the last raise reset. The entry is the faction's own when it books bounties (<c>FUN_140852010</c>; its parent faction <c>+0x38</c> is <b>Unknown</b> and not used).
    /// </summary>
    public bool Book(World world, int culprit, int faction, CharacterId witness)
    {
        var c = world.Characters.Cold(culprit);
        if (c?.Pending is not { Booked: false } p || !Factions.Books(faction)) return false;
        if (c.NpcClass is FundamentalType.Bandit or FundamentalType.Diplomat or FundamentalType.LawEnforcement) return false;
        p.Booked = true;
        if (p.Code == CrimeCode.Enslaving) return false;
        var entry = (c.Bounties ??= new BountyLedger()).Get(faction, Factions.NameOf(faction));
        entry.Amount += CrimeTable.Amount(p.Code);
        entry.Crimes |= 1 << (int)p.Code;
        entry.RaisedTick = world.Tick;
        c.RecentlyWanted = RecentlyWantedSeconds;
        if (!witness.IsNone)
        {
            if (bookingsTick != world.Tick) { bookings.Clear(); bookingsTick = world.Tick; }
            bookings.Add(new Booking(witness, world.Characters.IdOf(culprit), faction));
        }
        Note(world, CrimeEventKind.Booked, culprit, p.Code, faction, entry.Amount, witness);
        if (c.IsPlayer) world.Post($"{c.Name} is wanted by {entry.FactionName} for {CrimeTable.Name(p.Code)}: bounty c.{entry.Amount}");
        return true;
    }

    // ------------------------------------------------------------------ the sweep

    public void SlowWorld(World world)
    {
        int every = Math.Max(1, Settings.SweepEveryTicks);
        // Each character every SweepEveryTicks on its slot's turn (slot mod the period), so the witness looks spread over the ticks (the original's AI updates run 8 characters a
        // frame in turn, FUN_140786e30); a character's timers still lose the whole period each visit. The alarms keep the shared beat.
        int phase = (int)(world.Tick % every);
        Sweep(world, every * world.TickSeconds, phase, every);
        if (phase != 0) return;
        if (alarms.Count > 0) Investigate(world, every * world.TickSeconds);
        // Alarms die 8 game seconds after their last raise, whether answered or not (AlarmManager update FUN_14092bfe0, Verified).
        for (int k = alarms.Count - 1; k >= 0; k--)
            if ((alarms[k].Life -= every * world.TickSeconds) < 0) alarms.RemoveAt(k);
    }

    void Sweep(World world, float seconds, int phase, int every)
    {
        var table = world.Characters;
        var next = table.Next;
        double hoursPerTick = world.TickSeconds * 11.0 / 1200.0;
        for (int s = phase; s < table.HighWater; s += every)
        {
            if (!next[s].Alive || table.Cold(s) is not { } c) continue;
            if (c.Bounties is { } ledger && !c.IsImprisoned) Expire(world, s, c, ledger, hoursPerTick);   // paused while imprisoned (economy.md 11.3)
            if (c.RecentlyWanted > 0) c.RecentlyWanted = MathF.Max(0, c.RecentlyWanted - seconds);
            if (c.Pending is { } p)
            {
                p.Timer -= seconds;
                if (p.Timer <= 0) c.Pending = null;
            }
            if (!c.CarriedBy.IsNone) continue;   // a carried suspect is not looked at (the game's witness function)
            if (!c.IsPlayer)
            {
                if (c.Pending is not null) Witnesses(world, s, c, false, null, seconds);
                continue;
            }
            bool kidnapping = !c.Carrying.IsNone && table.TryResolveNext(c.Carrying, out int body) && Innocent(c, table.Cold(body)!);
            var premises = PremisesOf(world, s);
            if (c.Pending is null && !kidnapping && premises is null) continue;
            Witnesses(world, s, c, kidnapping, premises, seconds);
        }
    }

    AggroSystem? aggro;
    bool aggroLooked;

    /// <summary>Whether the witness has spotted the sneaking culprit: its spot meter, kept by <see cref="AggroSystem"/> for every looker, is full (without one, never).</summary>
    bool Spotted(World world, int witness, CharacterId culprit)
    {
        if (!aggroLooked) { aggroLooked = true; aggro = world.SystemOf<AggroSystem>(); }
        return aggro?.Spotted(witness, culprit) == true;
    }

    /// <summary>
    /// A carried body whose carrying is a kidnapping when seen (carry-loot.md 4.6, the game's "innocent victim", <b>Observed</b>; its status flag <c>+0xe9</c> is <b>Unknown</b> and read
    /// here as: an NPC of another, real, non-bandit faction, not a bandit and with no bounty).
    /// </summary>
    bool Innocent(CharacterCold carrier, CharacterCold body) =>
        !body.IsPlayer && body.Faction != carrier.Faction && !Factions.Lawless(body.Faction) && body.NpcClass != FundamentalType.Bandit && body.Bounties is not { Any: true };

    /// <summary>
    /// Every awake NPC that sees the culprit, in slot order, reacts as the game's witness function does (<c>FUN_140854d10</c>, <b>Observed</b>): not of the culprit's faction, not carried,
    /// with no pending crime of its own; nothing when its faction is hostile to the victim's, when the victim's is lawless, when it books bounties and the victim's is a bandit faction, or
    /// when it regards the culprit as friendly and the victim is of another faction. Theft and Looting need it to care about the victim (same faction or allied); Looting also that it
    /// does not regard the culprit as friendly; Kidnapping a victim and that care; an Assault a victim that it does not regard as an enemy; a Murder nothing more. One that regards the culprit as friendly costs it <see cref="BetrayalDelta"/>. A witness then sets the
    /// timer to 20 and, police-like of a faction that books bounties, books the crime. With <paramref name="kidnapping"/>, a witness that cares about the carried body first sets a pending
    /// Kidnapping (timer 20) on the carrier (the carrying check that runs before the crime check); with <paramref name="premises"/>, a witness that <see cref="Trespasses"/> sets a pending
    /// Trespassing against its own faction. A sneaking culprit is seen only through a full spot meter (<see cref="Spotted"/>).
    /// </summary>
    void Witnesses(World world, int culprit, CharacterCold c, bool kidnapping, Premises? premises, float seconds)
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
            if (w == culprit || !next[w].Alive) continue;
            if (table.Cold(w) is not { IsPlayer: false, Medical.Incapacitated: false } wc || !wc.CarriedBy.IsNone || wc.Pending is not null) continue;
            int wf = wc.Faction;
            if (wf == c.Faction || !Relations.Contains(wf)) continue;
            float range = AggroSystem.RangeOf(wc, Settings.Sight);
            if (sneaking)
            {
                float v = world.Sight.Sees(w, culprit, next[w].Position, at, range, stealth);
                if (v <= 0 || !Spotted(world, w, culpritId)) continue;
            }
            else if (world.Sight.Sees(w, culprit, next[w].Position, at, range) <= 0) continue;
            if (body is not null && !Friendly(wf, c.Faction) && Friendly(wf, body.Faction))
                SetPending(world, culprit, CrimeCode.Kidnapping, body.Faction, c.Carrying, KidnapTimer, viaWrapper: true);
            if (premises is { } house && Trespasses(world, wc, c, house)) SetPending(world, culprit, CrimeCode.Trespassing, wf, table.IdOf(w), MinTimer);
            if (c.Pending is not { } p) continue;
            if (!Reacts(world, wc, c, p)) continue;
            if (Friendly(wf, c.Faction) && Betrays(p.Code)) PlayerRelations.Add(world, Relations, wf, c.Faction, BetrayalDelta);
            p.Timer = WitnessTimer;
            c.RecentlyWanted = RecentlyWantedSeconds;
            var witness = table.IdOf(w);
            // Its squad tags the culprit short-term CRIMINAL (7).
            if (wc.SquadId >= 0) world.SystemOf<Dialogue.DialogueSystem>()?.Memory.Flag(world, wc.SquadId, culpritId, Dialogue.MemoryTag.Criminal);
            // A witness that is the crime's own victim (a trespass's is the witness that called it) goes for the culprit (Character::attackTarget: its squad tags the culprit TEMP_ENEMY).
            if (p.Victim == witness && wc.Fighter?.CanAttack == true
                && !(world.SystemOf<Combat.CombatSystem>()?.StateOf(witness) is { } fight && fight.TargetSlot == culprit && fight.TargetGeneration == culpritId.Generation))
            {
                world.SystemOf<Dialogue.DialogueSystem>()?.TagEnemy(world, w, culprit);
                world.Defer(new Combat.AttackOrder([witness], culpritId) { Tick = world.Tick + 1, Source = TaskSource.Npc });
            }
            bool first = !p.Witnessed;
            if (first)
            {
                p.Witnessed = true;
                Note(world, CrimeEventKind.Witnessed, culprit, p.Code, wf, 0, witness);
                if (c.IsPlayer && p.Code != CrimeCode.Looting) world.Post($"{c.Name} has been caught committing {CrimeTable.Name(p.Code)}!");   // the loot already says "was seen looting"
            }
            // The original fires the event on every reacting run (docs/game/dialogue.md "Witnesses"). Meitou fires it once at the player's characters; at an NPC culprit again on every
            // sweep from a police-like witness not yet fighting, as the guards' lines want a culprit within talking range that does not run (engine choice: lines' repetition limits are not kept).
            if (first || !c.IsPlayer && !wc.InCombat && IsPoliceLike(world, wc))
                world.Defer(new Dialogue.DialogueEvent(witness, table.IdOf(culprit), WitnessEvent(world, wc, p)) { Tick = world.Tick + 1 });
            if (!c.IsPlayer) continue;   // an NPC culprit is never booked (engine choice)
            if (!p.Booked && IsPoliceLike(world, wc) && Factions.Books(wf)) Book(world, culprit, wf, witness);
            if (p.Booked) return;
        }
    }

    /// <summary>
    /// A building with residents that a character is inside: its key, the residents' faction, whether it is public (<see cref="Ai.Blackboard.HomePublic"/>), and whether the residents
    /// have locked it up (<see cref="Locks.LockSystem.IsHouseLocked"/>, the original's squad flag <c>homelok</c>).
    /// </summary>
    readonly record struct Premises(string Key, int Faction, bool Public, bool LockedUp)
    {
        /// <summary>Open to visitors: public, from 7:00 to 22:59 (the original's hour test, <b>Observed</b>).</summary>
        public bool Open(int hour) => Public && hour is >= OpensAt and <= ClosesAfter;
    }

    /// <summary>The hours a public building is open: the first and the last (the game clock's hour int, <c>DAT_1421303d0</c>+0).</summary>
    public const int OpensAt = 7, ClosesAfter = 22;

    static string? HomeKey(Squad q) => (q.Ai?.OwnHome ?? q.HomeBuilding)?.Key;

    Locks.LockSystem? locks;
    bool locksLooked;

    bool LockedUp(World world, string key)
    {
        if (!locksLooked) { locksLooked = true; locks = world.SystemOf<Locks.LockSystem>(); }
        return locks?.IsHouseLocked(world, key) ?? false;
    }

    /// <summary>
    /// The premises the character stands inside (<see cref="SightMap.BuildingOf"/>): a town building some squad lives in, the first such squad's (the original reads the flags of the
    /// building's owner platoon, <c>building +0xd0</c>); null outdoors, on a roof, in a building nobody lives in, or caged.
    /// </summary>
    Premises? PremisesOf(World world, int slot)
    {
        if (world.Characters.Cold(slot) is not { IsImprisoned: false } || world.Sight.BuildingOf(slot) is not { } key) return null;
        foreach (var q in world.Squads.All)
            if (HomeKey(q) == key) return new Premises(key, q.Faction, q.Ai?.HomePublic ?? true, LockedUp(world, key));
        return null;
    }

    /// <summary>
    /// Whether <paramref name="w"/>, seeing <paramref name="c"/> inside <paramref name="house"/>, calls it trespassing (the perception's test <c>FUN_140852350</c>, <b>Observed</b>, then
    /// <c>FUN_140859680</c> sets the pending crime against the witness's faction). Never a witness that regards the culprit as an ally (the character's <c>vfunc 126</c>: same faction or
    /// allied) or as an enemy (<c>vfunc 125</c>, read here as <see cref="AggroSystem.IsEnemy"/>; an enemy is fought instead). A resident (the witness's squad lives there): the residents
    /// have locked up and the building is not <see cref="Premises.Open"/>. A police-like witness in someone else's building, at any hour: it is not public, the residents' faction does
    /// not regard the culprit's as an ally, and the culprit is a slave or the residents have locked up.
    /// </summary>
    bool Trespasses(World world, CharacterCold w, CharacterCold c, Premises house)
    {
        if (Friendly(w.Faction, c.Faction) || AggroSystem.IsEnemy(Relations, w.Faction, c.Faction)) return false;
        if (w.SquadId >= 0 && world.Squads.Find(w.SquadId) is { } squad && HomeKey(squad) == house.Key) return house.LockedUp && !house.Open(world.Hour);
        return !house.Public && IsPoliceLike(world, w) && !Friendly(house.Faction, c.Faction) && (c.SlaveState != 0 || house.LockedUp);
    }

    /// <summary>
    /// What a witness's faction loses towards a culprit it regards as friendly that it sees commit a crime against its own faction (the witness function's ally branch, <c>FUN_140854d10</c>:
    /// slot 4 of its table, -20 x 1, <b>Observed</b>; skipped there when the witness's platoon object <c>+0x210</c> has <c>+0xa8</c> set, <b>Unknown</b>, not modelled). The game runs it on
    /// every perception update while the crime is fresh, here on every sweep, so it repeats until the culprit is no longer an ally.
    /// </summary>
    public const float BetrayalDelta = -20;

    /// <summary>The crimes whose witness path reaches the ally branch: every code but Burglary, Looting, Trespassing, Crop Theft and Uniform Theft (their cases leave the switch elsewhere).</summary>
    static bool Betrays(CrimeCode code) => code is not (CrimeCode.Burglary or CrimeCode.Looting or CrimeCode.Trespassing or CrimeCode.CropTheft or CrimeCode.UniformTheft);

    /// <summary>
    /// DA 33 CRIME_ALARM said by <paramref name="speaker"/> at <paramref name="target"/> (<c>FUN_140680560</c>, <b>Observed</b>): the speaker turns on the target (its perception marks it
    /// hostile, <c>FUN_140858f40</c>), the target's crime timer is set to 30 and it is recently wanted (<c>FUN_140851f40</c>); with no crime pending, a target inside the speaker's own
    /// home that is not public gets a pending Trespassing. Then the alarm of the speaker's town is raised at the target for its pending crime (<see cref="TownAlarm"/>).
    /// </summary>
    public void Alarm(World world, int speaker, int target)
    {
        var table = world.Characters;
        if (table.Cold(speaker) is not { } s || table.Cold(target) is not { } t) return;
        var targetId = table.IdOf(target);
        world.Defer(new Combat.AttackOrder([table.IdOf(speaker)], targetId) { Tick = world.Tick + 1, Source = TaskSource.Npc });
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
    /// The alarm of <paramref name="town"/> raised at <paramref name="target"/> (<c>FUN_1409335e0</c>, <b>Observed</b>), for the crime <paramref name="code"/> against
    /// <paramref name="faction"/>: nothing for code 0; "Alarm raised" at most every 30 s for the player's; the town keeps one alarm per code and faction, a new one moving it to the target and restarting its <see cref="AlarmLife"/>.
    /// The town's investigators answer it (<see cref="Investigate"/>).
    /// </summary>
    public void TownAlarm(World world, int target, int town, CrimeCode code, int faction)
    {
        var table = world.Characters;
        if (town < 0 || code == CrimeCode.None || table.Cold(target) is not { } t) return;
        if (t.IsPlayer && world.GameHours - lastAlarmHours >= AlarmPostSeconds / World.SecondsPerGameHour)
        {
            lastAlarmHours = world.GameHours;
            world.Post(Loc.Tr("Alarm raised"));
        }
        var at = table.Next[target].Position;
        int place = world.Sight.PlaceOf(target), floor = FloorOf(world, target, at);
        foreach (var a in alarms)
            if (a.Town == town && a.Code == code && a.Faction == faction)
            {
                a.Position = at;
                a.Place = place;
                a.Floor = floor;
                a.Life = AlarmLife;
                return;
            }
        alarms.Add(new TownAlarm { Town = town, Code = code, Faction = faction, Position = at, Place = place, Floor = floor });
    }

    readonly List<TownAlarm> alarms = [];

    /// <summary>The towns' raised alarms, oldest first.</summary>
    public IReadOnlyList<TownAlarm> Alarms => alarms;

    /// <summary>How far an investigator answers an alarm (<c>FUN_14092a810</c>'s 375, <b>Verified</b> literal): at that range for an alarm raised outdoors, at half of it for one raised in a building, whatever the crime.</summary>
    public const float AlarmReach = 375;
    /// <summary>Within this of the alarm an investigator stops and looks about (100, <b>Verified</b>: the squared 10000), for <see cref="LookAboutSeconds"/> (10; 15 at the task's start).</summary>
    public const float AlarmLookRange = 100, LookAboutSeconds = 10, FirstLookAboutSeconds = 15;
    /// <summary>An investigator runs to this far short of the alarm, on its own side (30, <b>Verified</b>).</summary>
    public const float AlarmApproach = 30;

    /// <summary>
    /// The alarm an investigator of <paramref name="town"/> at <paramref name="at"/> on <paramref name="floor"/> answers (<c>FUN_14092a810</c>, <b>Verified</b>): the nearest within
    /// <see cref="AlarmReach"/>, half of it when the alarm was raised in a building (<see cref="TownAlarm.Place"/>), whatever the crime; with <paramref name="faction"/> (goal 210, allies
    /// only) only its faction's. An alarm in a building is skipped when its floor is more than one from the investigator's. Engine: the original's further halving for an alarm inside a
    /// closed building the investigator is not in, and its skip of any other floor in some buildings (building byte <c>+0xc2</c>, meaning <b>Unknown</b>), are not modelled. Null when none.
    /// </summary>
    public TownAlarm? AlarmFor(int town, Vector3 at, int faction = -1, int floor = 0)
    {
        TownAlarm? best = null;
        float bestD = float.MaxValue;
        foreach (var a in alarms)
        {
            if (a.Town != town || faction >= 0 && a.Faction != faction) continue;
            if (a.Place >= 0 && Math.Abs(a.Floor - floor) > 1) continue;
            float reach = a.Place >= 0 ? AlarmReach * 0.5f : AlarmReach;
            float d = Vector3.DistanceSquared(a.Position, at);
            if (d < reach * reach && d < bestD) (best, bestD) = (a, d);
        }
        return best;
    }

    /// <summary>The floor of the building the character stands in at <paramref name="at"/> (<c>Character</c> vfunc <c>+0x60</c>, an alarm's <c>+0x60</c>); 0 outdoors.</summary>
    public static int FloorOf(World world, int slot, Vector3 at) => world.Sight.BuildingOf(slot) is { } building ? world.Sight.FloorAt(building, at) : 0;

    /// <summary>
    /// The investigators' serial step (<c>Task_InvestigateAlarms</c> update, <b>Observed</b>), for each NPC whose AI acts out goal 209 or 210 (<see cref="Ai.AiBehaviour.Investigate"/>):
    /// within <see cref="AlarmLookRange"/> of its alarm it counts <see cref="Ai.AiMind.LookAbout"/> down and clears the alarm when it runs out; on the way there it keeps the count at
    /// <see cref="LookAboutSeconds"/>, and every character it sees that is not down, not of its faction or an ally, and has a pending crime against its faction or an ally, its squad
    /// tags TEMP_ENEMY and it goes for (<c>Character::attackTarget</c>). Engine: "sees" is the witnesses' sight test, the original's sense list (seen or heard) is not.
    /// </summary>
    void Investigate(World world, float seconds)
    {
        var table = world.Characters;
        var next = table.Next;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!next[i].Alive || table.Cold(i) is not { Ai: { Behaviour: Ai.AiBehaviour.Investigate, Goal: { } goal, Squad.Ai: { } board } mind } cold || !Ai.AiSystem.Holds(next[i], cold)) continue;
            var alarm = AlarmFor(board.Town, next[i].Position, goal.Task.Type == Data.Gameplay.Ai.TaskType.INVESTIGATE_ALARMS_ALLIES_ONLY ? cold.Faction : -1, FloorOf(world, i, next[i].Position));
            if (alarm is null) continue;
            if (Vector3.DistanceSquared(alarm.Position, next[i].Position) <= AlarmLookRange * AlarmLookRange)
            {
                mind.LookAbout -= seconds;
                if (mind.LookAbout < 0) alarms.Remove(alarm);
                continue;
            }
            mind.LookAbout = LookAboutSeconds;
            if (cold.Fighter?.CanAttack != true) continue;
            float range = AggroSystem.RangeOf(cold, Settings.Sight);
            for (int c = 0; c < table.HighWater; c++)
            {
                if (c == i || !next[c].Alive || table.Cold(c) is not { Pending: { } p, Medical.Incapacitated: false } cc || Friendly(cold.Faction, cc.Faction) || !Friendly(cold.Faction, p.VictimFaction)) continue;
                if (Vector3.DistanceSquared(next[c].Position, next[i].Position) > range * range || world.Sight.Sees(i, c, next[i].Position, next[c].Position, range) <= 0) continue;
                world.SystemOf<Dialogue.DialogueSystem>()?.TagEnemy(world, i, c);
                world.Defer(new Combat.AttackOrder([table.IdOf(i)], table.IdOf(c)) { Tick = world.Tick + 1, Source = TaskSource.Npc });
                break;
            }
        }
    }

    public void Hash(ref StateHasher hasher)
    {
        if (alarms.Count == 0) return;
        hasher.Add(alarms.Count);
        foreach (var a in alarms)
        {
            hasher.Add(a.Town); hasher.Add((int)a.Code); hasher.Add(a.Faction); hasher.Add(a.Position); hasher.Add(a.Life);
            if (a.Place >= 0) { hasher.Add(a.Place); hasher.Add(a.Floor); }   // only an alarm raised in a building, so the others hash as before
        }
    }

    /// <summary>The crime timer DA 33 sets (the literal 30.0 passed to <c>FUN_140851f40</c>), and how often "Alarm raised" may be posted (real seconds in the original; game seconds here).</summary>
    public const float AlarmTimer = 30, AlarmPostSeconds = 30;
    /// <summary>The game seconds a town alarm lives after its last raise (<c>FUN_1409335e0</c> sets 8.0, <b>Verified</b>).</summary>
    public const float AlarmLife = 8;
    double lastAlarmHours = double.NegativeInfinity;

    /// <summary>The dialogue event the witness says at the culprit (<c>FUN_140854d10</c>'s switch on the crime code, docs/game/dialogue.md "Event sources").</summary>
    static int WitnessEvent(World world, CharacterCold w, PendingCrime p) => p.Code switch
    {
        CrimeCode.Burglary or CrimeCode.Terrorism => 43,   // WITNESS_THIEF_OR_LOCKPICK
        CrimeCode.Theft or CrimeCode.Fencing or CrimeCode.Looting =>
            world.Characters.TryResolveNext(p.Victim, out int v) && world.Characters.Cold(v) is { SquadId: >= 0 } vc && vc.SquadId == w.SquadId ? 8 : 42,   // THIEF_CAUGHT_STEALING_FROM_ME, WITNESS_LOOTING_ALLY
        CrimeCode.Trespassing => 17,
        CrimeCode.CropTheft => 57,
        CrimeCode.Enslaving or CrimeCode.Kidnapping => 58,
        CrimeCode.UniformTheft => 34,
        _ => 41,   // WITNESS_GENERIC_ASSAULT
    };

    /// <summary>The witness function's tests for one witness and one pending crime (see <see cref="Witnesses"/>).</summary>
    bool Reacts(World world, CharacterCold w, CharacterCold culprit, PendingCrime p)
    {
        int wf = w.Faction, vf = p.VictimFaction;
        if (!Relations.Contains(vf) || Relations.IsHostile(wf, vf) || Factions.Lawless(vf)) return false;
        if (Factions.Books(wf) && Factions.Bandit(vf)) return false;
        bool friendlySuspect = Friendly(wf, culprit.Faction);
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

    /// <summary>Removes entries whose lifetime since their last raise is over (the game removes one per tick in its per-character tick; all at once here).</summary>
    void Expire(World world, int slot, CharacterCold c, BountyLedger ledger, double hoursPerTick)
    {
        for (int k = ledger.Count - 1; k >= 0; k--)
        {
            var e = ledger.Entries[k];
            if (e.Amount <= 0) continue;
            double elapsed = (world.Tick - e.RaisedTick) * hoursPerTick;
            if (elapsed >= 0 && elapsed <= CrimeTable.LifetimeHours(e.Amount)) continue;
            ledger.Remove(e);
            Note(world, CrimeEventKind.Expired, slot, CrimeCode.None, e.Faction, e.Amount, CharacterId.None);
            if (c.IsPlayer) world.Post($"{c.Name} is no longer wanted by {e.FactionName}");
        }
        if (ledger.Count == 0) c.Bounties = null;
    }

    /// <summary>The bounty lines of a character for the HUD, with the hours left at <paramref name="tick"/>; <paramref name="local"/> is the local faction (-1: none).</summary>
    public static IReadOnlyList<BountyLine> Lines(CharacterCold c, long tick, float tickSeconds, int local = -1)
    {
        if (c.Bounties is not { Count: > 0 } ledger) return [];
        double hoursPerTick = tickSeconds * 11.0 / 1200.0;
        var lines = new List<BountyLine>(ledger.Count);
        foreach (var e in ledger.Entries)
            if (e.Amount > 0)
                lines.Add(new BountyLine(e.FactionName, e.Amount, CrimeTable.Names(e.Crimes), CrimeTable.LifetimeHours(e.Amount) - (tick - e.RaisedTick) * hoursPerTick, e.Faction == local));
        return lines;
    }

    void Note(World world, CrimeEventKind kind, int culprit, CrimeCode code, int faction, int amount, CharacterId witness)
    {
        if (Settings.RecordLog) log.Add(new CrimeEvent(world.Tick, kind, world.Characters.IdOf(culprit), code, faction, amount, witness));
    }
}
