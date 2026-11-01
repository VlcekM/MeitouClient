using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay;

/// <summary>The editor's <c>CharacterTypeEnum</c>, the FACTION <c>fundamental type</c> (docs/game/factions-squads-towns.md section 2).</summary>
public enum FundamentalType
{
    None = 0, LawEnforcement = 1, Military = 2, Trader = 3, Civilian = 4, Diplomat = 5, Slave = 6, Slaver = 7, Bandit = 8, Adventurer = 9,
}

/// <summary>An entry of a FACTION list that points at another record, with the list's three numbers.</summary>
public readonly record struct RecordLink(string Id, int V0, int V1, int V2)
{
    internal static RecordLink From(GameReference r) => new(r.TargetStringId, r.Values.Value0, r.Values.Value1, r.Values.Value2);
}

/// <summary>
/// A FACTION record: the fields the game reads that the simulation needs first (docs/game/factions-squads-towns.md section 2); the
/// lists that other stages consume (<c>squads</c>, <c>campaigns</c>, <c>biomes</c>, ...) are kept as links to read when those
/// stages come. The fields <c>business relations</c>, <c>enemy classification</c>, <c>effect of anger</c>, <c>effect of happy</c> and
/// <c>emotion fade rate</c> are never read by the game (Observed) and are not modelled.
/// </summary>
public sealed record FactionData
{
    /// <summary>The player's faction (Nameless): found by this id, as the original does, so a translation that renames it changes nothing.</summary>
    public const string PlayerId = "204-gamedata.base";

    /// <summary>The record's string id (<c>204-gamedata.base</c>).</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>The name in the data files (<see cref="GameRecord.BaseName"/>): to find a faction by its English name in any language.</summary>
    public string BaseName { get => baseName ?? Name; init => baseName = value; }
    string? baseName;
    /// <summary><c>default relation</c>: the relation towards a faction without an explicit entry, capped by the other side's default (section 3.2).</summary>
    public int DefaultRelation { get; init; }
    /// <summary><c>relations</c>: explicit entries, faction id to the value (v0, an int; v1 and v2 are unused).</summary>
    public IReadOnlyDictionary<string, int> Relations { get; init; } = new Dictionary<string, int>();
    /// <summary><c>coexistence</c>: factions this one tolerates.</summary>
    public IReadOnlyList<string> Coexistence { get; init; } = [];
    public float Trustworthy { get; init; }
    public bool AllowSlavesWeapons { get; init; }
    public bool AntiSlavery { get; init; }
    public FundamentalType FundamentalType { get; init; }
    public float RoadPreference { get; init; }
    /// <summary><c>not real</c>: wildlife, ruins, slaves, "No Faction"...</summary>
    public bool NotReal { get; init; }
    public bool OffersBounties { get; init; }
    /// <summary>
    /// The first <c>legal system</c> reference (a FACTION id; null: none): the faction whose ledger entry the faction's bounties use (the loader stores it at <c>+0x38</c>,
    /// docs/game/economy.md 11.2, <b>Observed</b>; e.g. Bounty Hunters and Medics Guild -> United Cities).
    /// </summary>
    public string? LegalSystem { get; init; }
    /// <summary>
    /// The faction books crimes as bounties: the faction loader <c>FUN_1407fe270</c> sets the faction's byte <c>+0x40</c> to true for <c>fundamental type</c> LAW_ENFORCEMENT and
    /// otherwise to <c>offers bounties</c> (<b>Observed</b>, docs/game/economy.md 11.6). In the base data: Police, Mercenary Police, United Cities, The Holy Nation, Shek Kingdom, Republic of Tertius.
    /// </summary>
    public bool BooksBounties => FundamentalType == FundamentalType.LawEnforcement || OffersBounties;
    public float RunAwayRatioOfSquadSize { get; init; }
    public float RunAwayRatioRelativeToEnemy { get; init; }
    public int MaxProsperity { get; init; }
    /// <summary><c>roaming population</c> (50 for most factions).</summary>
    public int RoamingPopulation { get; init; }
    public bool HealsStrangers { get; init; }
    public int SquadFormation { get; init; }
    public IReadOnlyList<RecordLink> Races { get; init; } = [];
    public IReadOnlyList<RecordLink> Squads { get; init; } = [];
    public IReadOnlyList<RecordLink> SquadDefault { get; init; } = [];
    public IReadOnlyList<RecordLink> SpecialSquads { get; init; } = [];
    public IReadOnlyList<RecordLink> Residents { get; init; } = [];
    public IReadOnlyList<RecordLink> BarSquads { get; init; } = [];
    public IReadOnlyList<RecordLink> DefaultResident { get; init; } = [];
    public IReadOnlyList<RecordLink> Campaigns { get; init; } = [];
    public IReadOnlyList<RecordLink> Biomes { get; init; } = [];
    public IReadOnlyList<RecordLink> NoGoZones { get; init; } = [];
    public IReadOnlyList<RecordLink> TradeCulture { get; init; } = [];
    /// <summary><c>doors lock level</c> (fcs.def default 40) and <c>lock level random</c> (10): base and spread of every building lock the faction owns (docs/game/locks.md "Lock object").</summary>
    public int DoorsLockLevel { get; init; } = 40;
    public int LockLevelRandom { get; init; } = 10;
    public IReadOnlyList<RecordLink> BuildingsReplacements { get; init; } = [];

    public static FactionData From(GameRecord r)
    {
        var relations = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var link in r.GetReferences("relations")) relations[link.TargetStringId] = link.Values.Value0;
        IReadOnlyList<RecordLink> Links(string list) => [.. r.GetReferences(list).Select(RecordLink.From)];
        return new FactionData
        {
            Id = r.StringId,
            Name = r.Name,
            BaseName = r.BaseName,
            DefaultRelation = r.GetInt("default relation"),
            Relations = relations,
            Coexistence = [.. r.GetReferences("coexistence").Select(l => l.TargetStringId)],
            Trustworthy = r.GetFloat("trustworthy"),
            AllowSlavesWeapons = r.GetBool("allow slaves weapons"),
            AntiSlavery = r.GetBool("anti slavery"),
            FundamentalType = (FundamentalType)r.GetInt("fundamental type"),
            RoadPreference = r.GetFloat("road preference"),
            NotReal = r.GetBool("not real"),
            OffersBounties = r.GetBool("offers bounties"),
            LegalSystem = r.GetReferences("legal system") is { Count: > 0 } legal ? legal[0].TargetStringId : null,
            RunAwayRatioOfSquadSize = r.GetFloat("run away ratio of squad size"),
            RunAwayRatioRelativeToEnemy = r.GetFloat("run away ratio relative to enemy"),
            MaxProsperity = r.GetInt("max prosperity"),
            RoamingPopulation = r.GetInt("roaming population"),
            HealsStrangers = r.GetBool("heals strangers"),
            SquadFormation = r.GetInt("squad formation"),
            Races = Links("races"),
            Squads = Links("squads"),
            SquadDefault = Links("squad default"),
            SpecialSquads = Links("special squads"),
            Residents = Links("residents"),
            BarSquads = Links("bar squads"),
            DefaultResident = Links("default resident"),
            Campaigns = Links("campaigns"),
            Biomes = Links("biomes"),
            NoGoZones = Links("no-go zones"),
            TradeCulture = Links("trade culture"),
            BuildingsReplacements = Links("buildings replacements"),
            DoorsLockLevel = RecordReading.Int(r, "doors lock level", 40),
            LockLevelRandom = RecordReading.Int(r, "lock level random", 10),
        };
    }

    /// <summary>Every FACTION record of the database, in the database's order.</summary>
    public static List<FactionData> LoadAll(GameDatabase db) => [.. db.OfType(FcsRecordType.FACTION).Select(From)];
}

/// <summary>
/// The relations between factions at the start of a world (docs/game/factions-squads-towns.md section 3, <b>Verified</b>): for every
/// ordered pair A to B the relation is the value of B in A's explicit <c>relations</c> list if there is one, else the smaller of the
/// two factions' <c>default relation</c>; A to A is 100. Relations then change at run time (trust, wars, deeds: section 3.4, a later
/// stage); this is the table they start from. Faction numbers are positions in the list given to <see cref="Build"/>.
/// The player's faction has no table in the game (section 3.1, <b>Observed</b>: "player towards X" is answered by X's table about the
/// player, "player towards player" is 100 with the allied flag). With <see cref="PlayerFaction"/> set every query of the player's row is
/// answered that way; the row itself is still built by the same rule (callers that count NPC pairs leave it out) but is then never read.
/// This is the live table of a game: <see cref="Event"/> applies the section 3.4 changes and counts them in <see cref="Changes"/>.
/// </summary>
public sealed class FactionRelations
{
    /// <summary>At or below this a faction is hostile to another (section 3.3, <b>Verified</b>).</summary>
    public const float HostileThreshold = -30;
    /// <summary>At or above this a faction counts as an ally (section 3.3). The game's "now your allies" message fires only above it.</summary>
    public const float AllyThreshold = 50;

    readonly Dictionary<string, int> index;
    readonly float[] relation;
    readonly bool[] coexist;
    readonly bool[] war;
    readonly int[] rank;
    readonly float[] trust;

    FactionRelations(IReadOnlyList<FactionData> factions, Dictionary<string, int> index, float[] relation, bool[] coexist)
    {
        Factions = factions;
        this.index = index;
        this.relation = relation;
        this.coexist = coexist;
        war = new bool[relation.Length];
        rank = new int[factions.Count];
        trust = new float[factions.Count];
    }

    public IReadOnlyList<FactionData> Factions { get; }
    public int Count => Factions.Count;

    /// <summary>The player's faction (a position in <see cref="Factions"/>), whose row is answered by the other side's table; -1: none.</summary>
    public int PlayerFaction { get; set; } = -1;

    /// <summary>How many <see cref="Event"/>s changed a value so far (the state hash covers the table once this is above 0).</summary>
    public long Changes { get; private set; }

    /// <summary>The position of a faction id in <see cref="Factions"/>, or -1.</summary>
    public int IndexOf(string id) => index.GetValueOrDefault(id, -1);

    /// <summary>Whether <paramref name="f"/> is a faction of this table.</summary>
    public bool Contains(int f) => (uint)f < (uint)Count;

    /// <summary>The relation of faction <paramref name="a"/> towards <paramref name="b"/>, -100 to 100 (the player's row is asked of the other side).</summary>
    public float Get(int a, int b)
    {
        if (a == PlayerFaction && a >= 0) return a == b ? 100 : relation[b * Count + a];
        return relation[a * Count + b];
    }

    /// <summary>
    /// The relation event of slot 5 (<c>FUN_1406b2d20</c>, section 3.4, <b>Observed</b>): faction <paramref name="a"/>'s view of <paramref name="b"/> moves by the kind's delta times
    /// <paramref name="x"/>, only while it is above the kind's floor (a fall) or below its ceiling (a rise), so one event can overshoot the floor by up to the delta; then it is clamped
    /// to [-100, 100]. Nothing happens when <paramref name="a"/> is <c>not real</c>, the kind has no row, or <paramref name="a"/> is the player's faction (the player's table,
    /// <c>PlayerFactionRelations</c>, has empty slots 2 to 5). <see cref="RelationEventKind.FirstAid"/>'s <paramref name="x"/> carries the update's seconds. Returns the value before
    /// and after.
    /// </summary>
    public (float Before, float After) Event(int a, int b, RelationEventKind kind, float x)
    {
        if (!Contains(a) || !Contains(b)) return (0, 0);
        float before = Get(a, b);
        if (a == b || a == PlayerFaction || Factions[a].NotReal) return (before, before);
        (float delta, float floor, float ceiling) = kind switch
        {
            RelationEventKind.Hit => (-0.05f * x, -20f, 100f),
            RelationEventKind.KnockoutMinor => (-2.5f * x, -50f, 100f),
            RelationEventKind.Knockout => (-10f * x, -50f, 100f),
            RelationEventKind.KillingBlow => (-20f * x, -100f, 100f),
            RelationEventKind.Killed => (-20f * x, -75f, 100f),
            RelationEventKind.FirstAid => (0.15f * x, -100f, 5f),
            RelationEventKind.Caged => (-10f * x, -60f, 100f),
            _ => (0f, 0f, 0f),
        };
        float after = before;
        if ((delta < 0 && before > floor) || (delta > 0 && before < ceiling)) after = Math.Clamp(before + delta, -100, 100);
        if (after != before)
        {
            relation[a * Count + b] = after;
            Changes++;
        }
        return (before, after);
    }

    /// <summary>
    /// Adds <paramref name="delta"/> to <paramref name="a"/>'s relation towards <paramref name="b"/>, clamped to [-100, 100] (dialogue's <c>change relations</c> and DA_AFFECT_RELATIONS,
    /// docs/game/dialogue.md "Effects"); a change asked of the player's row goes to the other side's table, as in <see cref="Event"/>. Returns the value before and after.
    /// </summary>
    public (float Before, float After) Add(int a, int b, float delta)
    {
        if (a == PlayerFaction && a >= 0) (a, b) = (b, a);
        float before = Get(a, b);
        if (a == b || !Contains(a) || !Contains(b)) return (before, before);
        float after = Math.Clamp(before + delta, -100, 100);
        if (after != before)
        {
            relation[a * Count + b] = after;
            Changes++;
        }
        return (before, after);
    }

    /// <summary>
    /// Declare war (slot 8, <c>FUN_1406b3000</c>, section 3.4, <b>Observed</b>): only while <paramref name="a"/>'s relation towards <paramref name="b"/> is -30 or more (not hostile yet); then
    /// the war flag is set and a relation of -35 or more drops to -35. <c>Started</c>: the flag was clear (the game then logs "War breaks out between {1} and {2}"). One direction only, as the
    /// dialogue's DECLARE_WAR calls it on the speaker's table. A change asked of the player's row goes to the other side's table, as in <see cref="Add"/>.
    /// </summary>
    public (float Before, float After, bool Started) DeclareWar(int a, int b)
    {
        if (a == PlayerFaction && a >= 0) (a, b) = (b, a);
        if (a == b || !Contains(a) || !Contains(b)) return (0, 0, false);
        float before = Get(a, b);
        if (before < HostileThreshold) return (before, before, false);
        bool started = !war[a * Count + b];
        war[a * Count + b] = true;
        if (before >= WarRelation) relation[a * Count + b] = WarRelation;
        Changes++;
        return (before, Get(a, b), started);
    }

    /// <summary>
    /// End war (slot 7, <c>FUN_1406b2f90</c>, <b>Observed</b>): clears the war flag and lifts a relation of -25 or less to -25 (just above hostile). One direction; the player's row as in
    /// <see cref="DeclareWar"/>.
    /// </summary>
    public (float Before, float After) EndWar(int a, int b)
    {
        if (a == PlayerFaction && a >= 0) (a, b) = (b, a);
        if (a == b || !Contains(a) || !Contains(b)) return (0, 0);
        float before = Get(a, b);
        war[a * Count + b] = false;
        if (before <= PeaceRelation) relation[a * Count + b] = PeaceRelation;
        Changes++;
        return (before, Get(a, b));
    }

    /// <summary>
    /// The war flag of <paramref name="a"/> towards <paramref name="b"/> (the entry's byte +2; nothing else found reads it). The original's save has no key for it (the relation writer
    /// <c>FUN_1406b3300</c> keeps <c>relation</c>, <c>trust</c>, <c>trustNeg</c> and <c>relationSID</c> only, <b>Observed</b>); Meitou saves it as <c>meitou war&lt;k&gt;</c> (<see cref="SetWar"/>).
    /// </summary>
    public bool AtWar(int a, int b)
    {
        if (a == PlayerFaction && a >= 0) (a, b) = (b, a);
        return a != b && Contains(a) && Contains(b) && war[a * Count + b];
    }

    /// <summary>Sets the war flag of <paramref name="a"/> towards <paramref name="b"/> (a loaded save does this; no relation changes).</summary>
    public void SetWar(int a, int b, bool value)
    {
        if (a != b && Contains(a) && Contains(b)) war[a * Count + b] = value;
    }

    /// <summary>
    /// The faction's global trust (FactionRelations +0x18, a float starting 0): what DC_REPUTATION compares (as an int, <c>FUN_140671530</c>) and DA_AFFECT_REPUTATION raises (slot 9,
    /// <c>FUN_1406b2530</c>, <b>Observed</b>). The original saves no +0x18 (the player table's <c>global trust</c> key reads and writes +0x14, whose link to +0x18 is <b>Unknown</b>);
    /// Meitou saves it as <c>meitou global trust</c>.
    /// </summary>
    public float GlobalTrust(int f) => Contains(f) ? trust[f] : 0;

    /// <summary>DA_AFFECT_REPUTATION: faction <paramref name="f"/>'s global trust += <paramref name="delta"/> (no clamp, as the original's slot 9). Returns the value after.</summary>
    public float AddGlobalTrust(int f, float delta)
    {
        if (!Contains(f)) return 0;
        trust[f] += delta;
        if (delta != 0) Changes++;
        return trust[f];
    }

    /// <summary>Sets a faction's global trust (a loaded save does this).</summary>
    public void SetGlobalTrust(int f, float value)
    {
        if (Contains(f)) trust[f] = value;
    }

    /// <summary>The relation a declaration of war sets, and the one the end of a war lifts a lower relation to (<c>FUN_1406b3000</c>, <c>FUN_1406b2f90</c>).</summary>
    public const float WarRelation = -35, PeaceRelation = -25;

    /// <summary>Sets the relation of faction <paramref name="a"/> towards <paramref name="b"/> (a loaded save does this; trust and the other state live outside this table).</summary>
    public void Set(int a, int b, float value) => relation[a * Count + b] = value;

    /// <summary>
    /// The player's rank in faction <paramref name="f"/> (the int at FactionRelations +0x10, saved as GAMESTATE_FACTION <c>rank</c>; docs/game/factions-squads-towns.md
    /// "Faction rank"): 0 until a dialogue's DA_INCREASE_FACTION_RANK raises it, read by DC_FACTION_RANK.
    /// </summary>
    public int Rank(int f) => Contains(f) ? rank[f] : 0;

    /// <summary>DA_INCREASE_FACTION_RANK: the rank +1 while it is below <paramref name="cap"/> (0: no cap). Returns whether it rose.</summary>
    public bool RaiseRank(int f, int cap)
    {
        if (!Contains(f) || (cap != 0 && rank[f] >= cap)) return false;
        rank[f]++;
        Changes++;
        return true;
    }

    /// <summary>Sets a faction's rank (a loaded save does this).</summary>
    public void SetRank(int f, int value)
    {
        if (Contains(f)) rank[f] = value;
    }

    /// <summary>A to B: B is another faction and the relation is at or below <see cref="HostileThreshold"/>.</summary>
    public bool IsHostile(int a, int b) => a != b && Get(a, b) <= HostileThreshold;

    /// <summary>A to B: B is A itself, or the relation is at or above <see cref="AllyThreshold"/>.</summary>
    public bool IsAlly(int a, int b) => a == b || Get(a, b) >= AllyThreshold;

    /// <summary>A to B: B is A itself, or A lists B in <c>coexistence</c> (for the player's row: B lists the player).</summary>
    public bool Coexists(int a, int b)
    {
        if (a == b) return true;
        return a == PlayerFaction && a >= 0 ? coexist[b * Count + a] : coexist[a * Count + b];
    }

    public static FactionRelations Build(IReadOnlyList<FactionData> factions)
    {
        int n = factions.Count;
        var index = new Dictionary<string, int>(n, StringComparer.Ordinal);
        for (int i = 0; i < n; i++) index[factions[i].Id] = i;
        var relation = new float[n * n];
        var coexist = new bool[n * n];
        Defaults(factions, index, relation, coexist);
        return new FactionRelations(factions, index, relation, coexist);
    }

    static void Defaults(IReadOnlyList<FactionData> factions, Dictionary<string, int> index, float[] relation, bool[] coexist)
    {
        int n = factions.Count;
        for (int a = 0; a < n; a++)
        {
            for (int b = 0; b < n; b++)
            {
                relation[a * n + b] = a == b ? 100
                    : factions[a].Relations.TryGetValue(factions[b].Id, out int explicitValue) ? explicitValue
                    : Math.Min(factions[a].DefaultRelation, factions[b].DefaultRelation);
            }
            foreach (string other in factions[a].Coexistence)
                if (index.TryGetValue(other, out int b)) coexist[a * n + b] = true;
        }
    }

    /// <summary>Back to the FACTION records' start values: every relation, no war flag, every rank 0 (an imported game that does not take the save's relations, docs/formats/save.md "Importing a game").</summary>
    public void Reset()
    {
        Array.Clear(coexist);
        Defaults(Factions, index, relation, coexist);
        Array.Clear(war);
        Array.Clear(rank);
        Changes = 0;
    }

    public static FactionRelations Build(GameDatabase db) => Build(FactionData.LoadAll(db));
}

/// <summary>
/// The event kinds of the relation table's slot 5 (docs/game/factions-squads-towns.md sections 3.4 and 3.5, <b>Observed</b>). The numbers are the game's, the names say what fires
/// each; every other number has no row and changes nothing (the bed put-in passes 9).
/// </summary>
public enum RelationEventKind
{
    /// <summary>A landed blow on one of the faction's people (-0.05 x, while above -20).</summary>
    Hit = 1,
    /// <summary>A blow that leaves the victim down, struck by a character of low threat rating (-2.5 x, while above -50).</summary>
    KnockoutMinor = 2,
    /// <summary>A blow that leaves the victim down (-10 x, while above -50).</summary>
    Knockout = 3,
    /// <summary>A blow that leaves the victim dead, on top of the knock-out kind (-20 x, while above -100).</summary>
    KillingBlow = 4,
    /// <summary>The death of one of the faction's people with a known killer (-20 x, while above -75).</summary>
    Killed = 5,
    /// <summary>First aid to an unconscious member (+0.15 x per second, while below +5).</summary>
    FirstAid = 8,
    /// <summary>One of the faction's people put in a cage (-10 x, while above -60).</summary>
    Caged = 12,
}
