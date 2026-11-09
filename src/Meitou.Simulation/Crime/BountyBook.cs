using Meitou.Data.Gameplay;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Crime;

/// <summary>
/// The bounties of the crime system (docs/game/economy.md section 11): a witnessed crime booked into the culprit's <see cref="BountyLedger"/>, this
/// tick's bookings by a witness for the arrest system, the entries' expiry and the HUD's bounty lines. The <see cref="CrimeSystem"/> owns it and
/// passes itself for the factions and its event log.
/// </summary>
sealed class BountyBook
{
    /// <summary>
    /// This tick's bookings by a witness.
    /// </summary>
    readonly List<Booking> _bookings = [];

    /// <summary>
    /// The tick <see cref="_bookings"/> holds (-1: none yet).
    /// </summary>
    long _bookingsTick = -1;

    /// <summary>
    /// The bookings of this tick by a witness (the police-like NPC that booked, the culprit, the faction): the arrest system's witnessed-crime
    /// trigger. Not world state.
    /// </summary>
    internal IReadOnlyList<Booking> BookingsOf(long tick) => _bookingsTick == tick ? _bookings : [];

    /// <summary>
    /// Books the culprit's pending crime with <paramref name="faction"/> (the game's <c>FUN_140853ec0</c>, <b>Verified</b>): once per pending crime
    /// (the booked flag), not for a culprit of <c>NPC class</c> BANDIT, DIPLOMAT or LAW_ENFORCEMENT, and Enslaving adds nothing. The amount of the
    /// crime table is added to the ledger entry, the crime's bit set and the time of the last raise reset. The entry is the booking faction's
    /// (<c>FUN_140852010</c>, <see cref="CrimeFactions.BookingOf"/>): of the culprit's prison owner while it is imprisoned in a cage that resolves,
    /// else of <paramref name="faction"/>; its legal system when it has one, else itself when it books bounties. With no booking faction the crime is
    /// still marked booked and the culprit recently wanted, but the original's entry has a null key that nothing reads, so no bounty is kept.
    /// </summary>
    internal bool Book(CrimeSystem crimes, World world, int culprit, int faction, CharacterId witness)
    {
        var c = world.Characters.Cold(culprit);
        if (c?.Pending is not { Booked: false } p) return false;
        if (c.NpcClass is FundamentalType.Bandit or FundamentalType.Diplomat or FundamentalType.LawEnforcement) return false;
        p.Booked = true;
        if (p.Code == CrimeCode.Enslaving) return false;
        int owner = c.IsImprisoned && c.InWhat is { } cageId && world.Fixtures.Find(cageId) is { } cage ? cage.Owner : faction;
        int key = crimes.Factions.BookingOf(owner);
        if (key < 0)
        {
            c.RecentlyWanted = CrimeSystem.RecentlyWantedSeconds;
            return false;
        }
        faction = key;
        var entry = (c.Bounties ??= new BountyLedger()).Get(faction, crimes.Factions.NameOf(faction));
        entry.Amount += CrimeTable.Amount(p.Code);
        entry.Crimes |= 1 << (int)p.Code;
        entry.RaisedTick = world.Tick;
        c.RecentlyWanted = CrimeSystem.RecentlyWantedSeconds;
        if (!witness.IsNone)
        {
            if (_bookingsTick != world.Tick)
            {
                _bookings.Clear();
                _bookingsTick = world.Tick;
            }
            _bookings.Add(new Booking(witness, world.Characters.IdOf(culprit), faction));
        }
        crimes.Note(world, CrimeEventKind.Booked, culprit, p.Code, faction, entry.Amount, witness);
        if (c.IsPlayer)
        {
            world.Post(Loc.Tr("{1} is wanted by {2} for {3}: bounty c.{4}", c.Name, entry.FactionName, CrimeTable.Name(p.Code), entry.Amount));
        }

        return true;
    }

    /// <summary>
    /// Removes entries whose lifetime since their last raise is over (<c>FUN_1405cbb80</c>, <b>Verified</b>): the original removes the first expired
    /// entry it meets and stops, once per character tick; this sweep runs every <paramref name="ticks"/> ticks, so it removes at most that many.
    /// </summary>
    internal static void Expire(CrimeSystem crimes, World world, int slot, CharacterCold c, BountyLedger ledger, double hoursPerTick, int ticks)
    {
        for (int k = ledger.Count - 1; k >= 0 && ticks > 0; k--)
        {
            var e = ledger.Entries[k];
            if (e.Amount <= 0) continue;
            double elapsed = (world.Tick - e.RaisedTick) * hoursPerTick;
            if (elapsed >= 0 && elapsed <= CrimeTable.LifetimeHours(e.Amount)) continue;
            ledger.Remove(e);
            ticks--;
            crimes.Note(world, CrimeEventKind.Expired, slot, CrimeCode.None, e.Faction, e.Amount, CharacterId.None);
            if (c.IsPlayer) world.Post(Loc.Tr("{1} is no longer wanted by {2}", c.Name, e.FactionName));
        }
        if (ledger.Count == 0) c.Bounties = null;
    }

    /// <summary>
    /// The bounty lines of a character for the HUD, with the hours left at <paramref name="tick"/>; <paramref name="local"/> is the local faction
    /// (-1: none).
    /// </summary>
    public static IReadOnlyList<BountyLine> Lines(CharacterCold c, long tick, float tickSeconds, int local = -1)
    {
        if (c.Bounties is not { Count: > 0 } ledger) return [];
        double hoursPerTick = tickSeconds * 11.0 / 1200.0;
        var lines = new List<BountyLine>(ledger.Count);
        foreach (var e in ledger.Entries)
        {
            if (e.Amount > 0)
            {
                lines.Add(new BountyLine(e.FactionName, e.Amount, CrimeTable.Names(e.Crimes),
                    CrimeTable.LifetimeHours(e.Amount) - (tick - e.RaisedTick) * hoursPerTick, e.Faction == local));
            }
        }
        return lines;
    }
}
