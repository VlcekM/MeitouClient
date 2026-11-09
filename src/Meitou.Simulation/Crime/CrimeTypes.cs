using Meitou.Data.Gameplay;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Crime;

/// <summary>
/// The game's crime codes (docs/game/economy.md 11.2: the codes, names and amounts of <c>FUN_140851e40</c> and the name table <c>FUN_140856530</c>,
/// <b>Verified</b>). The numbers are the game's (they are the bit numbers of a ledger entry's crime mask and of the save key <c>crimes&lt;n&gt;</c>).
/// </summary>
public enum CrimeCode
{
    None = 0,
    Enslaving = 1,
    Burglary = 2,
    Theft = 3,
    Murder = 4,
    Assault = 5,
    Treason = 6,
    Terrorism = 7,
    Smuggling = 8,
    /// <summary>
    /// A second code the game's name table also calls "Terrorism" (economy.md Unknowns); an Assault on some victims becomes it.
    /// </summary>
    Terrorism2 = 9,
    Looting = 10,
    Trespassing = 11,
    EscapingPrison = 12,
    Fencing = 13,
    CropTheft = 14,
    Kidnapping = 15,
    UniformTheft = 16,
}

/// <summary>
/// The crime table and the bounty lifetime (docs/game/economy.md 11.2 and 11.3).
/// </summary>
public static class CrimeTable
{
    /// <summary>
    /// The bounty a booked crime adds (<c>FUN_140851e40</c>, <b>Verified</b>): a switch on the code with no other input; 500 for everything not
    /// listed.
    /// </summary>
    public static int Amount(CrimeCode code) => code switch
    {
        CrimeCode.Enslaving or CrimeCode.Trespassing => 100,
        CrimeCode.Burglary or CrimeCode.Fencing => 1000,
        CrimeCode.Theft or CrimeCode.EscapingPrison or CrimeCode.UniformTheft => 2500,
        CrimeCode.Murder or CrimeCode.Terrorism or CrimeCode.Smuggling or CrimeCode.Terrorism2 or CrimeCode.Kidnapping => 5000,
        CrimeCode.Treason => 50000,
        _ => 500,
    };

    /// <summary>
    /// The game's name of a crime (its name table; code 9 is a second "Terrorism") in the player's language: the original looks each name up in its
    /// message catalogue (<c>sensoryData.cpp</c> 145-177). For display only; the ledger keeps the code, never this text.
    /// </summary>
    public static string Name(CrimeCode code) => Loc.Tr(code switch
    {
        CrimeCode.Terrorism2 => "Terrorism",
        CrimeCode.EscapingPrison => "Escaping Prison",
        CrimeCode.CropTheft => "Crop Theft",
        CrimeCode.UniformTheft => "Uniform Theft",
        _ => code.ToString(),
    });

    /// <summary>
    /// A bounty of this amount or more never expires ("Notorious").
    /// </summary>
    public const int Notorious = 10000;

    /// <summary>
    /// How long a bounty lasts after its last raise, in game hours (<c>FUN_140851f10</c>, <b>Observed</b>): 0.04 x amount below
    /// <see cref="Notorious"/>, never at or above it (<see cref="double.PositiveInfinity"/>; the game uses 9,999,999). So 500 cats last 20 hours and
    /// 5000 last 200.
    /// </summary>
    public static double LifetimeHours(int amount) => amount >= Notorious ? double.PositiveInfinity : 0.04 * amount;

    /// <summary>
    /// The names of the crimes in a ledger entry's mask, in code order ("Murder, Assault").
    /// </summary>
    public static string Names(int mask)
    {
        var names = new List<string>();
        for (int c = 1; c < 32; c++)
            if ((mask & (1 << c)) != 0) names.Add(Name((CrimeCode)c));
        return string.Join(", ", names);
    }
}

/// <summary>
/// One faction's entry in a character's bounty ledger (economy.md 11.1: amount, crime mask, claimed, the time of the last raise).
/// <see cref="RaisedTick"/> is the world tick of the last raise (the game keeps game hours; the save key <c>bountyexp</c> is those hours truncated,
/// converted at the save boundary). <see cref="FactionName"/> is kept for the HUD.
/// </summary>
public sealed class BountyEntry
{
    public required int Faction { get; init; }
    public required string FactionName { get; init; }
    public int Amount { get; set; }
    /// <summary>
    /// Bit <c>1 &lt;&lt; code</c> for each crime booked into this entry.
    /// </summary>
    public int Crimes { get; set; }
    /// <summary>
    /// The player turned the character in for it already (the claim of economy.md 11.4, <see cref="ArrestSystem.Claim"/>): it never pays twice, and
    /// the police no longer go after it.
    /// </summary>
    public bool Claimed { get; set; }
    public long RaisedTick { get; set; }
}

/// <summary>
/// A character's bounties, one entry per faction in faction order (so the ledger enumerates and hashes the same way however it was filled). Serial
/// phases change it.
/// </summary>
public sealed class BountyLedger
{
    readonly List<BountyEntry> _entries = [];

    public IReadOnlyList<BountyEntry> Entries => _entries;
    public int Count => _entries.Count;

    /// <summary>
    /// The entry of <paramref name="faction"/>, or null.
    /// </summary>
    public BountyEntry? Of(int faction)
    {
        foreach (var e in _entries) if (e.Faction == faction) return e;
        return null;
    }

    /// <summary>
    /// The bounty <paramref name="faction"/> has on the character (0: none).
    /// </summary>
    public int AmountFor(int faction) => Of(faction)?.Amount ?? 0;

    /// <summary>
    /// The entry <paramref name="faction"/>'s bounties are kept under: its booking faction's (<see cref="CrimeFactions.BookingKey"/>), or null.
    /// </summary>
    public BountyEntry? BookedFor(FactionRelations relations, int faction) => CrimeFactions.BookingKey(relations, faction) is var key and >= 0 ? Of(key) : null;

    /// <summary>
    /// The bounty <paramref name="faction"/> sees on the character: its booking faction's entry (0: none).
    /// </summary>
    public int BookedAmount(FactionRelations relations, int faction) => BookedFor(relations, faction)?.Amount ?? 0;

    /// <summary>
    /// Any faction has a bounty on the character (the game's "has any bounty", which gives others permission to loot it).
    /// </summary>
    public bool Any
    {
        get
        {
            foreach (var e in _entries) if (e.Amount > 0) return true;
            return false;
        }
    }

    /// <summary>
    /// The sum over all factions.
    /// </summary>
    public int Total
    {
        get
        {
            int t = 0;
            foreach (var e in _entries) t += e.Amount;
            return t;
        }
    }

    /// <summary>
    /// The entry of <paramref name="faction"/>, made (amount 0) when there is none.
    /// </summary>
    public BountyEntry Get(int faction, string name)
    {
        if (Of(faction) is { } found) return found;
        var entry = new BountyEntry { Faction = faction, FactionName = name };
        int at = 0;
        while (at < _entries.Count && _entries[at].Faction < faction) at++;
        _entries.Insert(at, entry);
        return entry;
    }

    public bool Remove(BountyEntry entry) => _entries.Remove(entry);

    internal void Hash(ref StateHasher h)
    {
        h.Add(0x426F756E);   // "Boun"
        h.Add(_entries.Count);
        foreach (var e in _entries)
        {
            h.Add(e.Faction);
            h.Add(e.Amount);
            h.Add(e.Crimes);
            h.Add(e.Claimed);
            h.Add(e.RaisedTick);
        }
    }
}

/// <summary>
/// A town's raised alarm (<c>FUN_1409335e0</c>'s record, <b>Observed</b>): the crime, the faction it was against, where it was raised. It stays until
/// an investigator has looked about at it (<see cref="CrimeSystem.Investigate"/>) or <see cref="CrimeSystem.AlarmLife"/> after its creation (the
/// original's field +0x24, <b>Verified</b>). Not saved (engine choice).
/// </summary>
public sealed class TownAlarm
{
    public required int Town { get; init; }
    public required CrimeCode Code { get; init; }
    public required int Faction { get; init; }
    public System.Numerics.Vector3 Position { get; set; }
    /// <summary>
    /// The indoor volume the culprit was in when the alarm was raised (<see cref="SightMap.PlaceOf"/>; the original's location handle <c>+0x40</c>,
    /// type 0xb = none), -1 outdoors. Overwritten by a repeated raise.
    /// </summary>
    public int Place { get; set; } = -1;
    /// <summary>
    /// The culprit's floor when it was raised (the original's <c>+0x60</c>); only read for an alarm with a <see cref="Place"/>. Overwritten by a
    /// repeated raise.
    /// </summary>
    public int Floor { get; set; }
    /// <summary>
    /// Game seconds it still lives: <see cref="CrimeSystem.AlarmLife"/> at creation (a repeated raise leaves it), counted down by the town update;
    /// gone below 0.
    /// </summary>
    public float Life { get; set; } = CrimeSystem.AlarmLife;
}

/// <summary>
/// A crime the character committed that nobody has booked yet (economy.md 11.6, carry-loot.md 4.3: code, victim faction, victim, a freshness timer;
/// the "booked" flag). One per character, as in the game. <see cref="Timer"/> is in game seconds and counts down (the tail of the per-character crime
/// tick <c>FUN_1405cbb80</c>: cleared once it is at or below 0, else reduced by the tick delta; <b>Verified</b>, not run for a dead character); a
/// witness sets it to 20 again.
/// </summary>
public sealed class PendingCrime
{
    public required CrimeCode Code { get; set; }
    public required int VictimFaction { get; set; }
    public CharacterId Victim { get; set; } = CharacterId.None;
    public float Timer { get; set; }
    /// <summary>
    /// Booked as a bounty already (the game's <c>+0xa4</c>): a crime is booked once; a new code clears it.
    /// </summary>
    public bool Booked { get; set; }
    /// <summary>
    /// Somebody has seen it (the HUD's "caught" message is posted once).
    /// </summary>
    public bool Witnessed { get; set; }

    internal void Hash(ref StateHasher h)
    {
        h.Add(0x50656E64);   // "Pend"
        h.Add((int)Code);
        h.Add(VictimFaction);
        h.Add(Victim);
        h.Add(Timer);
        h.Add(Booked);
        h.Add(Witnessed);
    }
}

/// <summary>
/// Pay the bail of a caged character (<see cref="ArrestSystem.BailFor"/>): a free player character near the cage pays, the jail's entry is erased and
/// the prisoner freed.
/// </summary>
public sealed record PayBailOrder(CharacterId Prisoner) : SimCommand;

/// <summary>
/// The carriers (the selection when empty) take the bodies they carry to the nearest free cage and put them in; a wanted captive pays its bounty
/// (<see cref="ArrestSystem.Claim"/>).
/// </summary>
public sealed record PutInCageOrder(IReadOnlyList<CharacterId> Carriers) : OrderCommand
{
    /// <summary>
    /// The cage to take them to (a clicked cage, Put in 108); null: the nearest free one.
    /// </summary>
    public string? Cage { get; init; }
}

/// <summary>
/// A bounty line for the HUD (selected characters only): who wants the character, how much, for what, and the hours left (infinity: never
/// expires).</summary> <summary>One bounty for the HUD; <see cref="Local"/>: the local faction's (the cage owner's while imprisoned, else the town
/// the character stands in; docs/game/ui-screens.md section 4).
/// </summary>
public readonly record struct BountyLine(string Faction, int Amount, string Crimes, double HoursLeft, bool Local = false);

/// <summary>
/// A crime booked by a police-like witness this tick (<see cref="CrimeSystem.BookingsOf"/>).
/// </summary>
public readonly record struct Booking(CharacterId Witness, CharacterId Culprit, int Faction);

/// <summary>
/// Settings of the <see cref="CrimeSystem"/>.
/// </summary>
public sealed record CrimeSettings
{
    /// <summary>
    /// The witness sweep and the bounty expiry run every this many ticks (engine choice: the game checks crimes in its per-character sense update,
    /// docs/game/perception.md).
    /// </summary>
    public int SweepEveryTicks { get; init; } = 15;
    /// <summary>
    /// A witness's daylight sight before its race's <c>vision range mult</c> (the original's 1500, <see cref="AggroSystem.OutdoorSight"/>).
    /// </summary>
    public float Sight { get; init; } = AggroSystem.OutdoorSight;
    /// <summary>
    /// Keep a <see cref="CrimeSystem.Log"/> of what happened (tests).
    /// </summary>
    public bool RecordLog { get; init; }
}

/// <summary>
/// What the crime system did: a crime set pending, seen, booked, or a bounty that expired.
/// </summary>
public enum CrimeEventKind { Pending, Witnessed, Booked, Expired }

public readonly record struct CrimeEvent(long Tick, CrimeEventKind Kind, CharacterId Culprit, CrimeCode Code, int Faction, int Amount, CharacterId Witness);

/// <summary>
/// What the crime rules need to know of the factions: who books bounties and whose goods are free.
/// </summary>
public sealed class CrimeFactions
{
    readonly bool[] _books, _lawless, _bandit, _hunters;
    readonly int[] _booking;

    public CrimeFactions(FactionRelations relations)
    {
        Relations = relations;
        int n = relations.Count;
        _books = new bool[n];
        _lawless = new bool[n];
        _bandit = new bool[n];
        _hunters = new bool[n];
        _booking = new int[n];
        for (int f = 0; f < n; f++)
        {
            var data = relations.Factions[f];
            _books[f] = data.BooksBounties;
            _bandit[f] = data.FundamentalType == FundamentalType.Bandit;
            _lawless[f] = data.NotReal || _bandit[f];
        }
        for (int f = 0; f < n; f++)
        {
            var data = relations.Factions[f];
            int legal = data.LegalSystem is { } id ? relations.IndexOf(id) : -1;
            _booking[f] = BookingKey(relations, f);
            _hunters[f] = data.FundamentalType == FundamentalType.Adventurer && legal >= 0 && !data.NotReal;
        }
    }

    /// <summary>
    /// The faction whose ledger entry <paramref name="f"/>'s bounties use (the game's <c>FUN_140851fb0</c> / <c>FUN_140852010</c>, economy.md 11.2,
    /// <b>Verified</b>): its <c>legal system</c> when it has one, else itself when it books bounties, else -1 (no entry). The booking, the claims and
    /// every bounty lookup by a faction go through it.
    /// </summary>
    public int BookingOf(int f) => Relations.Contains(f) ? _booking[f] : -1;

    /// <summary>
    /// <see cref="BookingOf"/> computed from the relations (for code without the crime system's cache).
    /// </summary>
    public static int BookingKey(FactionRelations relations, int f)
    {
        if (!relations.Contains(f)) return -1;
        var data = relations.Factions[f];
        int legal = data.LegalSystem is { } id ? relations.IndexOf(id) : -1;
        return legal >= 0 ? legal : data.BooksBounties ? f : -1;
    }

    /// <summary>
    /// The faction's NPCs hunt bounties (economy.md 11.8). <b>Engine choice</b>: nothing in the data flags bounty hunters; read here as
    /// <c>fundamental type</c> ADVENTURER with a <c>legal system</c>, which in the base data is exactly the faction Bounty Hunters (-> United Cities;
    /// <b>Verified</b> by probe of the FACTION records).
    /// </summary>
    public bool Hunts(int f) => Relations.Contains(f) && _hunters[f];

    public FactionRelations Relations { get; }

    /// <summary>
    /// The faction books bounties (the game's faction flag <c>+0x40</c>: <c>fundamental type</c> LAW_ENFORCEMENT or <c>offers bounties</c>).
    /// </summary>
    public bool Books(int f) => Relations.Contains(f) && _books[f];
    /// <summary>
    /// A "lawless" faction (<c>not real</c>, or of <c>fundamental type</c> BANDIT): crimes against it are no crimes.
    /// </summary>
    public bool Lawless(int f) => !Relations.Contains(f) || _lawless[f];
    /// <summary>
    /// The faction is <c>not real</c> (the game's faction byte <c>+0x1d0</c>): the cage leave routine writes no escaped flag for its cages
    /// (<c>FUN_1403554b0</c>).
    /// </summary>
    public bool NotReal(int f) => Relations.Contains(f) && Relations.Factions[f].NotReal;
    public bool Bandit(int f) => Relations.Contains(f) && _bandit[f];
    public string NameOf(int f) => Relations.Contains(f) ? Relations.Factions[f].Name : "?";
}