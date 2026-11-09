using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay;

/// <summary>
/// The editor's <c>CharacterTypeEnum</c>, the FACTION <c>fundamental type</c> (docs/game/factions-squads-towns.md section 2).
/// </summary>
public enum FundamentalType
{
    /// <summary>
    /// <c>OT_NONE</c> (docs/game/dialogue.md "Enums").
    /// </summary>
    None = 0,
    /// <summary>
    /// <c>OT_LAW_ENFORCEMENT</c>: books crimes as bounties (<see cref="FactionData.BooksBounties"/>).
    /// </summary>
    LawEnforcement = 1,
    /// <summary>
    /// <c>OT_MILITARY</c>.
    /// </summary>
    Military = 2,
    /// <summary>
    /// <c>OT_TRADER</c>.
    /// </summary>
    Trader = 3,
    /// <summary>
    /// <c>OT_CIVILIAN</c>.
    /// </summary>
    Civilian = 4,
    /// <summary>
    /// <c>OT_DIPLOMAT</c>.
    /// </summary>
    Diplomat = 5,
    /// <summary>
    /// <c>OT_SLAVE</c>.
    /// </summary>
    Slave = 6,
    /// <summary>
    /// <c>OT_SLAVER</c>.
    /// </summary>
    Slaver = 7,
    /// <summary>
    /// <c>OT_BANDIT</c>.
    /// </summary>
    Bandit = 8,
    /// <summary>
    /// <c>OT_ADVENTURER</c>.
    /// </summary>
    Adventurer = 9,
}

/// <summary>
/// An entry of a FACTION list that points at another record, with the list's three numbers.
/// </summary>
public readonly record struct RecordLink(string Id, int V0, int V1, int V2)
{
    /// <summary>
    /// A link from a reference entry: its target and its three values.
    /// </summary>
    internal static RecordLink From(GameReference r) => new(r.TargetStringId, r.Values.Value0, r.Values.Value1, r.Values.Value2);
}

/// <summary>
/// A FACTION record: the fields the game reads that the simulation needs first (docs/game/factions-squads-towns.md section 2); the lists that other
/// stages consume (<c>squads</c>, <c>campaigns</c>, <c>biomes</c>, ...) are kept as links to read when those stages come. The fields <c>business
/// relations</c>, <c>enemy classification</c>, <c>effect of anger</c>, <c>effect of happy</c> and <c>emotion fade rate</c> are never read by the game
/// (Observed) and are not modelled.
/// </summary>
public sealed record FactionData
{
    /// <summary>
    /// The player's faction (Nameless): found by this id, as the original does, so a translation that renames it changes nothing.
    /// </summary>
    public const string PlayerId = "204-gamedata.base";

    /// <summary>
    /// The data files' name when set apart from <see cref="Name"/>; null: the same.
    /// </summary>
    string? _baseName;

    /// <summary>
    /// The record's string id (<c>204-gamedata.base</c>).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The FACTION's name in the active language.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The name in the data files (<see cref="GameRecord.BaseName"/>): to find a faction by its English name in any language.
    /// </summary>
    public string BaseName { get => _baseName ?? Name; init => _baseName = value; }

    /// <summary>
    /// <c>default relation</c>: the relation towards a faction without an explicit entry, capped by the other side's default (section 3.2).
    /// </summary>
    public int DefaultRelation { get; init; }

    /// <summary>
    /// <c>relations</c>: explicit entries, faction id to the value (v0, an int; v1 and v2 are unused).
    /// </summary>
    public IReadOnlyDictionary<string, int> Relations { get; init; } = new Dictionary<string, int>();

    /// <summary>
    /// <c>coexistence</c>: factions this one tolerates.
    /// </summary>
    public IReadOnlyList<string> Coexistence { get; init; } = [];

    /// <summary>
    /// <c>trustworthy</c>.
    /// </summary>
    public float Trustworthy { get; init; }

    /// <summary>
    /// <c>allow slaves weapons</c>.
    /// </summary>
    public bool AllowSlavesWeapons { get; init; }

    /// <summary>
    /// <c>anti slavery</c>.
    /// </summary>
    public bool AntiSlavery { get; init; }

    /// <summary>
    /// <c>fundamental type</c>.
    /// </summary>
    public FundamentalType FundamentalType { get; init; }

    /// <summary>
    /// <c>road preference</c>.
    /// </summary>
    public float RoadPreference { get; init; }

    /// <summary>
    /// <c>not real</c>: wildlife, ruins, slaves, "No Faction"...
    /// </summary>
    public bool NotReal { get; init; }

    /// <summary>
    /// <c>offers bounties</c>: the faction books crimes as bounties even if it is not law enforcement (<see cref="BooksBounties"/>).
    /// </summary>
    public bool OffersBounties { get; init; }

    /// <summary>
    /// The first <c>legal system</c> reference (a FACTION id; null: none): the faction whose ledger entry the faction's bounties use (the loader
    /// stores it at <c>+0x38</c>, docs/game/economy.md 11.2, <b>Observed</b>; e.g. Bounty Hunters and Medics Guild -> United Cities).
    /// </summary>
    public string? LegalSystem { get; init; }

    /// <summary>
    /// The faction books crimes as bounties: the faction loader <c>FUN_1407fe270</c> sets the faction's byte <c>+0x40</c> to true for <c>fundamental
    /// type</c> LAW_ENFORCEMENT and otherwise to <c>offers bounties</c> (<b>Observed</b>, docs/game/economy.md 11.6). In the base data: Police,
    /// Mercenary Police, United Cities, The Holy Nation, Shek Kingdom, Republic of Tertius.
    /// </summary>
    public bool BooksBounties => FundamentalType == FundamentalType.LawEnforcement || OffersBounties;

    /// <summary>
    /// <c>run away ratio of squad size</c>.
    /// </summary>
    public float RunAwayRatioOfSquadSize { get; init; }

    /// <summary>
    /// <c>run away ratio relative to enemy</c>.
    /// </summary>
    public float RunAwayRatioRelativeToEnemy { get; init; }

    /// <summary>
    /// <c>max prosperity</c>.
    /// </summary>
    public int MaxProsperity { get; init; }

    /// <summary>
    /// <c>roaming population</c> (50 for most factions).
    /// </summary>
    public int RoamingPopulation { get; init; }

    /// <summary>
    /// <c>heals strangers</c>.
    /// </summary>
    public bool HealsStrangers { get; init; }

    /// <summary>
    /// <c>squad formation</c> (fcs_enums.def <c>SquadFormation</c>).
    /// </summary>
    public int SquadFormation { get; init; }

    /// <summary>
    /// <c>races</c>.
    /// </summary>
    public IReadOnlyList<RecordLink> Races { get; init; } = [];

    /// <summary>
    /// <c>squads</c>.
    /// </summary>
    public IReadOnlyList<RecordLink> Squads { get; init; } = [];

    /// <summary>
    /// <c>squad default</c>.
    /// </summary>
    public IReadOnlyList<RecordLink> SquadDefault { get; init; } = [];

    /// <summary>
    /// <c>special squads</c>: UNIQUE_SQUAD_TEMPLATEs.
    /// </summary>
    public IReadOnlyList<RecordLink> SpecialSquads { get; init; } = [];

    /// <summary>
    /// <c>residents</c>.
    /// </summary>
    public IReadOnlyList<RecordLink> Residents { get; init; } = [];

    /// <summary>
    /// <c>bar squads</c>.
    /// </summary>
    public IReadOnlyList<RecordLink> BarSquads { get; init; } = [];

    /// <summary>
    /// <c>default resident</c>.
    /// </summary>
    public IReadOnlyList<RecordLink> DefaultResident { get; init; } = [];

    /// <summary>
    /// <c>campaigns</c>: the FACTION_CAMPAIGNs the faction rolls (the values ignored).
    /// </summary>
    public IReadOnlyList<RecordLink> Campaigns { get; init; } = [];

    /// <summary>
    /// <c>biomes</c>: the BIOME_GROUPs its campaigns aim at first.
    /// </summary>
    public IReadOnlyList<RecordLink> Biomes { get; init; } = [];

    /// <summary>
    /// <c>no-go zones</c>.
    /// </summary>
    public IReadOnlyList<RecordLink> NoGoZones { get; init; } = [];

    /// <summary>
    /// <c>trade culture</c>: ITEMS_CULTURE records (<see cref="Economy.TradeCulture"/>).
    /// </summary>
    public IReadOnlyList<RecordLink> TradeCulture { get; init; } = [];

    /// <summary>
    /// <c>doors lock level</c> (fcs.def default 40) and <c>lock level random</c> (10): base and spread of every building lock the faction owns
    /// (docs/game/locks.md "Lock object").
    /// </summary>
    public int DoorsLockLevel { get; init; } = 40;

    /// <summary>
    /// <c>lock level random</c> (default 10; see <see cref="DoorsLockLevel"/>).
    /// </summary>
    public int LockLevelRandom { get; init; } = 10;

    /// <summary>
    /// <c>buildings replacements</c>: BUILDINGS_SWAP records (<see cref="BuildingSwaps"/>).
    /// </summary>
    public IReadOnlyList<RecordLink> BuildingsReplacements { get; init; } = [];

    /// <summary>
    /// Reads a FACTION record.
    /// </summary>
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

    /// <summary>
    /// Every FACTION record of the database, in the database's order.
    /// </summary>
    public static List<FactionData> LoadAll(GameDatabase db) => [.. db.OfType(FcsRecordType.Faction).Select(From)];
}
