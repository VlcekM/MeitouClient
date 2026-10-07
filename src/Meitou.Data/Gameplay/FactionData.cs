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
    /// <summary>The record's string id (<c>204-gamedata.base</c>).</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
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
/// The player's faction has no table in the game (it asks the other side); here it is a row like any other, built by the same
/// rule, and callers that count NPC pairs leave it out.
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

    FactionRelations(IReadOnlyList<FactionData> factions, Dictionary<string, int> index, float[] relation, bool[] coexist)
    {
        Factions = factions;
        this.index = index;
        this.relation = relation;
        this.coexist = coexist;
    }

    public IReadOnlyList<FactionData> Factions { get; }
    public int Count => Factions.Count;

    /// <summary>The position of a faction id in <see cref="Factions"/>, or -1.</summary>
    public int IndexOf(string id) => index.GetValueOrDefault(id, -1);

    /// <summary>The relation of faction <paramref name="a"/> towards <paramref name="b"/>, -100 to 100.</summary>
    public float Get(int a, int b) => relation[a * Count + b];

    /// <summary>A to B: B is another faction and the relation is at or below <see cref="HostileThreshold"/>.</summary>
    public bool IsHostile(int a, int b) => a != b && Get(a, b) <= HostileThreshold;

    /// <summary>A to B: B is A itself, or the relation is at or above <see cref="AllyThreshold"/>.</summary>
    public bool IsAlly(int a, int b) => a == b || Get(a, b) >= AllyThreshold;

    /// <summary>A to B: B is A itself, or A lists B in <c>coexistence</c>.</summary>
    public bool Coexists(int a, int b) => a == b || coexist[a * Count + b];

    public static FactionRelations Build(IReadOnlyList<FactionData> factions)
    {
        int n = factions.Count;
        var index = new Dictionary<string, int>(n, StringComparer.Ordinal);
        for (int i = 0; i < n; i++) index[factions[i].Id] = i;
        var relation = new float[n * n];
        var coexist = new bool[n * n];
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
        return new FactionRelations(factions, index, relation, coexist);
    }

    public static FactionRelations Build(GameDatabase db) => Build(FactionData.LoadAll(db));
}
