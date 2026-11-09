using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay;

/// <summary>
/// A SQUAD_TEMPLATE (or UNIQUE_SQUAD_TEMPLATE, which has the same squad fields) as docs/game/factions-squads-towns.md section 4 describes what the
/// game reads. The fields the squad factory (stage 2), the town residents and the shops (docs/game/economy.md section 9) need are typed; the
/// building-choice fields stay in the record. How the counts of <see cref="Squad"/>, <see cref="Squad2"/> and the random picks combine into a squad
/// is section 5.1, a later stage.
/// </summary>
public sealed record SquadTemplate
{
    /// <summary>
    /// The template's string id.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The template's name in the active language.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// True for a UNIQUE_SQUAD_TEMPLATE.
    /// </summary>
    public bool IsUnique { get; init; }

    /// <summary>
    /// <c>leader</c>: only the first entry is used by the game (<c>FUN_140583a10</c>); null when the list is empty. V1 = 100 marks a guaranteed
    /// leader in 24 templates.
    /// </summary>
    public RecordLink? Leader { get; init; }

    /// <summary>
    /// <c>squad</c> (CHARACTER; v0, v1 = the count range).
    /// </summary>
    public IReadOnlyList<RecordLink> Squad { get; init; } = [];

    /// <summary>
    /// <c>squad2</c>, the second half: member role 1.
    /// </summary>
    public IReadOnlyList<RecordLink> Squad2 { get; init; } = [];

    /// <summary>
    /// <c>choosefrom list</c>: characters picked at random, weighted by v0 (0 counts as 100).
    /// </summary>
    public IReadOnlyList<RecordLink> ChooseFrom { get; init; } = [];

    /// <summary>
    /// <c>num random chars</c>: how many <see cref="ChooseFrom"/> picks are made (with <see cref="NumRandomCharsMax"/> the range).
    /// </summary>
    public int NumRandomChars { get; init; }

    /// <summary>
    /// <c>num random chars max</c>.
    /// </summary>
    public int NumRandomCharsMax { get; init; }

    /// <summary>
    /// <c>animals</c> / <c>animals2</c> (ANIMAL_CHARACTER; v2 = age 0 to 100).
    /// </summary>
    public IReadOnlyList<RecordLink> Animals { get; init; } = [];

    /// <summary>
    /// <c>animals2</c>: the second half's animals (see <see cref="Animals"/>).
    /// </summary>
    public IReadOnlyList<RecordLink> Animals2 { get; init; } = [];

    /// <summary>
    /// <c>animal age random</c>: the template's own range of the random part of its animals' age (not the animal record's; all 961 base templates
    /// carry it).
    /// </summary>
    public float AnimalAgeRandom { get; init; }

    /// <summary>
    /// <c>slaves</c> and <c>prisoners</c>: whole squads created recursively.
    /// </summary>
    public IReadOnlyList<RecordLink> Slaves { get; init; } = [];

    /// <summary>
    /// <c>prisoners</c>: squads created as the squad's prisoners.
    /// </summary>
    public IReadOnlyList<RecordLink> Prisoners { get; init; } = [];

    /// <summary>
    /// <c>housemates</c>: further squads spawned with this one (v0, v1 = min, max).
    /// </summary>
    public IReadOnlyList<RecordLink> Housemates { get; init; } = [];

    /// <summary>
    /// <c>faction</c>: the owner; absent, the spawn code asks the town or searches the factions (section 4).
    /// </summary>
    public string? Faction { get; init; }

    /// <summary>
    /// <c>world state</c>: AND gate for the spawn (WORLD_EVENT_STATE links).
    /// </summary>
    public IReadOnlyList<RecordLink> WorldState { get; init; } = [];

    /// <summary>
    /// <c>roaming military</c>: forces squad mode 2 even with a home town.
    /// </summary>
    public bool RoamingMilitary { get; init; }

    /// <summary>
    /// <c>dont multiply</c>: exempt from the Squad size multiplier setting.
    /// </summary>
    public bool DontMultiply { get; init; }

    /// <summary>
    /// <c>AI packages</c>: AI_PACKAGEs with their slot (v0) (<see cref="Ai.AiDatabase.Packages"/>).
    /// </summary>
    public IReadOnlyList<RecordLink> AiPackages { get; init; } = [];

    /// <summary>
    /// <c>blood smell mult</c>.
    /// </summary>
    public float BloodSmellMult { get; init; }

    /// <summary>
    /// <c>patrol approaches towns</c>.
    /// </summary>
    public bool PatrolApproachesTowns { get; init; }

    /// <summary>
    /// <c>force speed</c> as the platoon keeps it: 0 walk, 1 jog, 2 run replace the members' speed mode (docs/game/pathfinding.md "Force speed"); 4
    /// none (a stored 3 grouped becomes 4, the key absent is taken as 4).
    /// </summary>
    public int ForceSpeed { get; init; } = 4;

    /// <summary>
    /// The platoon's <c>force speed</c> of a template record (<c>FUN_1407ee2d0</c>).
    /// </summary>
    public static int ForceSpeedOf(GameRecord r) => RecordReading.Int(r, "force speed", 4) is var speed && speed == 3 ? 4 : speed;

    /// <summary>
    /// <c>building</c>: the home building a resident squad uses (first entry); <c>building designation</c> is the editor's BuildingDesignation.
    /// </summary>
    public RecordLink? Building { get; init; }

    /// <summary>
    /// <c>building designation</c> (the editor's BuildingDesignation).
    /// </summary>
    public int BuildingDesignation { get; init; }

    /// <summary>
    /// Every <c>building</c> entry (the preferred homes of a resident entry, docs/game/factions-squads-towns.md "Town residents") and <c>building
    /// dislike</c>.
    /// </summary>
    public IReadOnlyList<RecordLink> Buildings { get; init; } = [];

    /// <summary>
    /// <c>building dislike</c>.
    /// </summary>
    public IReadOnlyList<RecordLink> BuildingDislike { get; init; } = [];

    /// <summary>
    /// <c>layout interior</c> / <c>layout exterior</c>: the layout names the home building is given (empty: none).
    /// </summary>
    public string LayoutInterior { get; init; } = "";

    /// <summary>
    /// <c>layout exterior</c> (see <see cref="LayoutInterior"/>).
    /// </summary>
    public string LayoutExterior { get; init; } = "";

    /// <summary>
    /// <c>building name</c>: the home building's display name when set ("The Dancing Skeleton", docs/game/factions-squads-towns.md "Town residents").
    /// </summary>
    public string BuildingName { get; init; } = "";

    /// <summary>
    /// <c>initial door state</c> (DoorState: 0 CLOSED, 1 OPEN, 2 LOCKED, 3 BROKEN), applied to the home building when it is assigned
    /// (docs/game/locks.md "Doors").
    /// </summary>
    public int InitialDoorState { get; init; }

    /// <summary>
    /// <c>public day</c> (fcs.def default true): written over the home building's public flag when it is assigned (docs/game/locks.md "Data").
    /// </summary>
    public bool PublicDay { get; init; } = true;

    /// <summary>
    /// <c>building ruined</c>: the home building is ruined when assigned, if it can be (docs/game/buildings-production.md "Ruined by residents").
    /// </summary>
    public bool BuildingRuined { get; init; }

    /// <summary>
    /// <c>nest</c> links.
    /// </summary>
    public IReadOnlyList<RecordLink> Nest { get; init; } = [];

    // The shop (economy.md 9.1).
    /// <summary>
    /// <c>is trader</c>: the squad trades (a shop needs <see cref="Vendors"/> too).
    /// </summary>
    public bool IsTrader { get; init; }

    /// <summary>
    /// <c>vendors</c>: VENDOR_LIST records, v0 the list's weight, v1 its item count (-1 or less: the list's own <c>items count</c>).
    /// </summary>
    public IReadOnlyList<RecordLink> Vendors { get; init; } = [];

    /// <summary>
    /// <c>vendors fill total amount</c> as the stock filler reads it: below 1 means 100.
    /// </summary>
    public int VendorsFillTotalAmount { get; init; } = 100;

    /// <summary>
    /// <c>vendor money</c>: the purse of a building shop (0 or less: derived from the stock); the squad filler never reads it.
    /// </summary>
    public int VendorMoney { get; init; }

    /// <summary>
    /// <c>robotics levels</c>: the average grade 0..5 of the robotic limbs the squad's shop stocks (ArmourRarity, default 1 GEAR_CHEAP).
    /// </summary>
    public int RoboticsLevels { get; init; } = 1;

    /// <summary>
    /// <c>crossbow levels</c>: the same for the crossbows it stocks.
    /// </summary>
    public int CrossbowLevels { get; init; } = 1;

    /// <summary>
    /// <c>vendors refresh time</c> in game hours as the restock reads it: 0 or less means 24000.
    /// </summary>
    public float VendorsRefreshTime { get; init; } = 24;

    /// <summary>
    /// <c>buy mult</c>: the trader's price factor, both ways (section 6).
    /// </summary>
    public float BuyMult { get; init; } = 1;

    /// <summary>
    /// <c>buys stolen</c>: a knowing fence (section 8).
    /// </summary>
    public bool BuysStolen { get; init; }

    /// <summary>
    /// <c>buys illegal</c>: a smuggler.
    /// </summary>
    public bool BuysIllegal { get; init; }

    /// <summary>
    /// <c>item artifacts base value</c> and <c>gear artifacts base value</c>: the most an artifact given to the squad's home may be worth (0: none;
    /// docs/game/economy.md "Artifacts").
    /// </summary>
    public int ItemArtifactsBaseValue { get; init; }

    /// <summary>
    /// <c>gear artifacts base value</c> (see <see cref="ItemArtifactsBaseValue"/>).
    /// </summary>
    public int GearArtifactsBaseValue { get; init; }

    /// <summary>
    /// A squad with a shop: <see cref="IsTrader"/> and at least one vendor list.
    /// </summary>
    public bool HasShop => IsTrader && Vendors.Count > 0;

    /// <summary>
    /// Reads a SQUAD_TEMPLATE or UNIQUE_SQUAD_TEMPLATE record.
    /// </summary>
    public static SquadTemplate From(GameRecord r)
    {
        IReadOnlyList<RecordLink> Links(string list) => [.. r.GetReferences(list).Select(RecordLink.From)];
        var leader = r.GetReferences("leader");
        var building = r.GetReferences("building");
        var faction = r.GetReferences("faction");
        int fill = RecordReading.Int(r, "vendors fill total amount", 15);
        float refresh = RecordReading.Float(r, "vendors refresh time", 24);
        return new SquadTemplate
        {
            Id = r.StringId,
            Name = r.Name,
            IsUnique = r.Type == FcsRecordType.UniqueSquadTemplate,
            Leader = leader.Count > 0 ? RecordLink.From(leader[0]) : null,
            Squad = Links("squad"),
            Squad2 = Links("squad2"),
            ChooseFrom = Links("choosefrom list"),
            NumRandomChars = r.GetInt("num random chars"),
            NumRandomCharsMax = r.GetInt("num random chars max"),
            Animals = Links("animals"),
            Animals2 = Links("animals2"),
            AnimalAgeRandom = r.GetFloat("animal age random"),
            Slaves = Links("slaves"),
            Prisoners = Links("prisoners"),
            Housemates = Links("housemates"),
            Faction = faction.Count > 0 ? faction[0].TargetStringId : null,
            WorldState = Links("world state"),
            RoamingMilitary = r.GetBool("roaming military"),
            DontMultiply = r.GetBool("dont multiply"),
            AiPackages = Links("AI packages"),
            BloodSmellMult = r.GetFloat("blood smell mult"),
            PatrolApproachesTowns = r.GetBool("patrol approaches towns"),
            ForceSpeed = ForceSpeedOf(r),
            Building = building.Count > 0 ? RecordLink.From(building[0]) : null,
            Buildings = Links("building"),
            BuildingDislike = Links("building dislike"),
            LayoutInterior = r.GetString("layout interior"),
            LayoutExterior = r.GetString("layout exterior"),
            BuildingName = r.GetString("building name"),
            BuildingDesignation = r.GetInt("building designation"),
            InitialDoorState = r.GetInt("initial door state"),
            PublicDay = RecordReading.Bool(r, "public day", true),
            BuildingRuined = r.GetBool("building ruined"),
            Nest = Links("nest"),
            IsTrader = RecordReading.Bool(r, "is trader", false),
            Vendors = Links("vendors"),
            VendorsFillTotalAmount = fill < 1 ? 100 : fill,
            VendorMoney = RecordReading.Int(r, "vendor money", 0),
            RoboticsLevels = RecordReading.Int(r, "robotics levels", 1),
            CrossbowLevels = RecordReading.Int(r, "crossbow levels", 1),
            VendorsRefreshTime = refresh <= 0 ? 24000 : refresh,
            BuyMult = RecordReading.Float(r, "buy mult", 1),
            BuysStolen = RecordReading.Bool(r, "buys stolen", false),
            BuysIllegal = RecordReading.Bool(r, "buys illegal", false),
            ItemArtifactsBaseValue = r.GetInt("item artifacts base value"),
            GearArtifactsBaseValue = r.GetInt("gear artifacts base value"),
        };
    }

    /// <summary>
    /// Every SQUAD_TEMPLATE record, then every UNIQUE_SQUAD_TEMPLATE, in the database's order.
    /// </summary>
    public static List<SquadTemplate> LoadAll(GameDatabase db) =>
        [.. db.OfType(FcsRecordType.SquadTemplate).Select(From), .. db.OfType(FcsRecordType.UniqueSquadTemplate).Select(From)];
}