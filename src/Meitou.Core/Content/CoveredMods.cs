namespace Meitou.Content;

/// <summary>
/// A Steam Workshop original and, when checked against a port, the compared version's update time (Unix seconds).
/// </summary>
public sealed record MatchedItem(ulong Id, long? Updated = null);

/// <summary>
/// A mod whose feature Meitou has built in (docs/formats/rekenshi.md): its <c>.mod</c> name or its code mod's id (or neither: matched by its
/// workshop items alone), the Meitou setting the feature became, and the workshop items it was matched against (none when it is matched by its
/// code mod id, whatever its version).
/// </summary>
public sealed record BuiltInMod(string? ModName, string? CodeId, string Setting, MatchedItem[] Items);

/// <summary>
/// An RE_Kenshi mod whose plugin does not run in Meitou, with an endorsed Steam Workshop port. The original is identified by its
/// Workshop items or known <c>.mod</c> name. The port has a title, Workshop item, maintainer and an optional local <c>.mod</c> name.
/// </summary>
public sealed record EndorsedPort(string? ModName, MatchedItem[] Items, string Port, string? PortModName, ulong PortWorkshopId, string Maintainer);

/// <summary>
/// The mods Meitou covers, one table for the game (which takes in a built-in mod's settings and does not run its code mod) and the launcher
/// (which notes both kinds on the Mods page and offers to subscribe to the endorsed port of an RE_Kenshi mod).
/// </summary>
public static class CoveredMods
{
    /// <summary>
    /// xXDarkrusoXx's RE_Kenshi plugin "Player-Only XP x10" (workshop 3679678086, matched against its 2026-11-07 update).
    /// </summary>
    public static readonly BuiltInMod RekenshiXp =
        new("Player XP Only x10 RE", null, "Squad XP in the Advanced settings", [new(3679678086, 1794078276)]);

    /// <summary>
    /// Its code mod port "[MeitouClient] Player-Only XP x10" (workshop 3838916871), matched by its code mod id whatever its version.
    /// </summary>
    public static readonly BuiltInMod XpPort = new(null, "player-only-xp", "Squad XP in the Advanced settings", []);

    /// <summary>
    /// ikahurula's data mod "Swamp Turtle FIX" (workshop 2237731365, matched against its 2020-09-24 update): it gives the Swamp Turtle the
    /// elephant creature weapon, whose moves the Swamp turtle attacks fix gives it.
    /// </summary>
    public static readonly BuiltInMod SwampTurtleFix =
        new("Swamp Turtle FIX", null, "Swamp turtle attacks on the Fixes page", [new(2237731365, 1600962568)]);

    /// <summary>
    /// BFrizFoShiz's RE_Kenshi plugin "Character Highlight" (workshop 3688641949, matched against its 2026-06-23 update): Alt outlines the
    /// characters near, as Meitou's hover outline does.
    /// </summary>
    public static readonly BuiltInMod CharacterHighlight =
        new("Character Highlight", null, "Hover outline in the Advanced settings", [new(3688641949, 1782253588)]);

    /// <summary>
    /// The data mods that mute the corpse flies, matched by their workshop items: "Flies Are Muted" (1173024319, its 2017-10-18 update),
    /// "Silent Corpse Flies" (1661152630, its 2019-02-20 update) and "Removes fly buzzing sound" (3249760334, its 2024-05-19 update).
    /// </summary>
    public static readonly BuiltInMod MuteFlies = new(null, null, "Mute corpse flies in the Audio settings",
        [new(1173024319, 1508336963), new(1661152630, 1550624826), new(3249760334, 1716139203)]);

    /// <summary>
    /// The data mod "Immersive Flies" (workshop 2385009404, matched by its item against its 2021-02-05 update): the corpse flies are heard from
    /// less far.
    /// </summary>
    public static readonly BuiltInMod ImmersiveFlies =
        new(null, null, "Quiet corpse flies in the Audio settings", [new(2385009404, 1612536455)]);

    /// <summary>
    /// The data mods that raise the player faction, squad size and squad count caps, matched by <c>256 Squad Limit</c> or their workshop items:
    /// "256 Recruitment Limit" (705119823, 2018-04-19), "256 People" (1767015632, 2019-06-11), "Increased Party Size 256" (1701119801, 2019-04-02),
    /// "64 Faction Size Limit" (1723792132, 2019-04-26), "256 Recruits - V.1.0.68" (3405818838, 2025-01-12), "Wolves Max Squad limits" (1362433476,
    /// 2018-04-15), "_256 Squad Limit" (1578601833, 2018-11-30), "increased recruitment (60 version)" (773491398, 2019-10-02), "100 Character Limit"
    /// (1996458247, 2020-02-13), "Max Party Size (256)" (2133492547, 2020-06-17), "Maximized Faction size" (801089396, 2016-11-17), "48 Faction Size
    /// Limit" (1723790671, 2019-04-26), "256 Squad Limit" (3746322010, 2026-06-17), "100factions 22Squads" (2018675754, 2020-03-12), "Recruitment
    /// Limit 200" (2994957775, 2023-06-26) and "Increased Faction Limit &amp; Squad Size" (3551190789, 2025-09-20).
    /// </summary>
    public static readonly BuiltInMod SquadLimits = new("256 Squad Limit", null,
        "Max faction size, Max squad size and Max squads in the Advanced settings",
        [
            new(705119823, 1524166325),
            new(1767015632, 1560243898),
            new(1701119801, 1554176827),
            new(1723792132, 1556284426),
            new(3405818838, 1736679135),
            new(1362433476, 1523819046),
            new(1578601833, 1543600152),
            new(773491398, 1570003843),
            new(1996458247, 1581609184),
            new(2133492547, 1592424882),
            new(801089396, 1479354852),
            new(1723790671, 1556284391),
            new(3746322010, 1781657015),
            new(2018675754, 1583974579),
            new(2994957775, 1687786216),
            new(3551190789, 1758333424),
        ]);

    /// <summary>
    /// The mods Meitou has built in.
    /// </summary>
    public static IReadOnlyList<BuiltInMod> BuiltIn { get; } =
        [RekenshiXp, XpPort, SwampTurtleFix, CharacterHighlight, MuteFlies, ImmersiveFlies, SquadLimits];

    /// <summary>
    /// Emkej's "Loot-Scoot-Execute" (workshop 3675700748, matched against its 2026-04-26 update, and its "MCM fork" 3813852124, its 2026-11-13
    /// update), with Jame's "[MeitouClient] Execution".
    /// </summary>
    static readonly EndorsedPort Execution = new(
        "Loot-Scoot-Execute", [new(3675700748, 1777211773), new(3813852124, 1794544300)], "[MeitouClient] Execution", "Execution", 3838346946, "Jame");

    /// <summary>
    /// Shutori's "Proving Grounds" (workshop 3774745195, matched against its 2026-09-26 update), with roscatoday's "[Meitou] Proving Grounds".
    /// </summary>
    static readonly EndorsedPort ProvingGrounds = new(
        "Proving Grounds", [new(3774745195, 1790470536)], "[Meitou] Proving Grounds", "ProvingGroundsMeitou", 3841575072, "roscatoday");

    /// <summary>
    /// Dafi's "Disarm (RE_Kenshi)" (workshop 3759461630, matched against its 2026-08-12 update), with dxseba's "[Meitou Client] Disarm Port".
    /// </summary>
    static readonly EndorsedPort Disarm = new(
        "Disarm", [new(3759461630, 1786501591)], "[Meitou Client] Disarm Port", "Disarm", 3841646137, "dxseba");

    /// <summary>
    /// Grynn's "It. Tickles. Me" (workshop 3731488940, matched against its 2026-06-16 update), with dxseba's "[Meitou Client] It. Tickles. Me. Port".
    /// </summary>
    static readonly EndorsedPort ItTicklesMe = new(
        "It. Tickles. Me", [new(3731488940, 1781649808)], "[Meitou Client] It. Tickles. Me. Port", "ItTicklesMe", 3841647083, "dxseba");

    /// <summary>
    /// luckyren's "Kill Counter" (workshop 3779464387, matched against its 2026-08-09 update), with dxseba's "[Meitou Client] Kill Counter Port".
    /// </summary>
    static readonly EndorsedPort KillCounter = new(
        "KillCounter", [new(3779464387, 1786291833)], "[Meitou Client] Kill Counter Port", "KillCounter", 3841647109, "dxseba");

    /// <summary>
    /// Walnuts's "[RE_Kenshi] Level Up Notifications" (workshop 3701534964, matched against its 2026-10-10 update),
    /// with dxseba's "[Meitou Client] Level Up Notify Port".
    /// </summary>
    static readonly EndorsedPort LevelUpNotify = new(
        "LevelUpNotify", [new(3701534964, 1791669646)], "[Meitou Client] Level Up Notify Port", "LevelUpNotify", 3841647147, "dxseba");

    /// <summary>
    /// zuop's "Speed menu" (workshop 3818592083, matched against its 2026-10-14 update), with dxseba's "[Meitou Client] Speed Menu Port".
    /// </summary>
    static readonly EndorsedPort SpeedMenu = new(
        "Speed Menu", [new(3818592083, 1791936783)], "[Meitou Client] Speed Menu Port", "SpeedMenu", 3841647205, "dxseba");

    /// <summary>
    /// Jakematt2004's "Stack Sort" (workshop 3813810734, matched against its 2026-10-05 update), with dxseba's "[Meitou Client] Stack Sort Port".
    /// </summary>
    static readonly EndorsedPort StackSort = new(
        "StackSort", [new(3813810734, 1791166681)], "[Meitou Client] Stack Sort Port", "StackSort", 3841647266, "dxseba");

    /// <summary>
    /// tata's "Taunt Expansion" (workshop 3771499164, matched against its 2026-07-25 update), with dxseba's "[Meitou Client] Taunt Expansion Port".
    /// </summary>
    static readonly EndorsedPort TauntExpansion = new(
        "TauntExpansion", [new(3771499164, 1784997418)], "[Meitou Client] Taunt Expansion Port", "TauntExpansion", 3841647315, "dxseba");

    /// <summary>
    /// tata's "Area Guard" (workshop 3769985477, matched against its 2026-07-23 update), with dxseba's "[Meitou Client] Area Guard Port".
    /// </summary>
    static readonly EndorsedPort AreaGuard = new(
        "AreaGuard", [new(3769985477, 1784775327)], "[Meitou Client] Area Guard Port", "AreaGuard", 3844430966, "dxseba");

    /// <summary>
    /// LEOPARD's "Better Looting (MCM fork) (QoL)" (workshop 3816121198, matched against its 2026-11-21 update), with dxseba's "[Meitou Client]
    /// Better Looting Port".
    /// </summary>
    static readonly EndorsedPort BetterLooting = new(
        "BetterLooting", [new(3816121198, 1795304224)], "[Meitou Client] Better Looting Port", "BetterLooting", 3844430978, "dxseba");

    /// <summary>
    /// Asur's "Building Search Bar (RE_Kenshi)" (workshop 3786979230, matched against its 2026-08-21 update), with dxseba's "[Meitou Client] Building
    /// Search Bar Port".
    /// </summary>
    static readonly EndorsedPort BuildingSearchBar = new(
        "Search bar", [new(3786979230, 1787345024)], "[Meitou Client] Building Search Bar Port", "BuildingSearchBar", 3844430983, "dxseba");

    /// <summary>
    /// luckyren's "Cannibalism" (workshop 3797113265, matched against its 2026-09-08 update), with dxseba's "[Meitou Client] Cannibalism Port".
    /// </summary>
    static readonly EndorsedPort Cannibalism = new(
        "CannibalismTask", [new(3797113265, 1788868831)], "[Meitou Client] Cannibalism Port", "Cannibalism", 3844430991, "dxseba");

    /// <summary>
    /// nestore1961's "Elemental Enchantment" (workshop 3832978749, matched against its 2026-11-19 update), with dxseba's "[Meitou Client] Elemental
    /// Enchantment Port".
    /// </summary>
    static readonly EndorsedPort ElementalEnchantment = new(
        "Elemental Enchantment", [new(3832978749, 1795061091)],
        "[Meitou Client] Elemental Enchantment Port", "ElementalEnchantment", 3844431001, "dxseba");

    /// <summary>
    /// Manboy's "Equipment Durability" (workshop 3834344700, matched against its 2026-11-18 update), with dxseba's "[Meitou Client] Equipment
    /// Durability Port".
    /// </summary>
    static readonly EndorsedPort EquipmentDurability = new(
        "Durability", [new(3834344700, 1794969418)], "[Meitou Client] Equipment Durability Port", "EquipmentDurability", 3844431009, "dxseba");

    /// <summary>
    /// Nixxen's "Fix Shopping Wages (RE_Kenshi)" (workshop 3775703318, matched against its 2026-08-01 update), with dxseba's "[Meitou Client] Fix
    /// Shopping Wages Port".
    /// </summary>
    static readonly EndorsedPort FixShoppingWages = new(
        "FixShoppingWages", [new(3775703318, 1785598382)], "[Meitou Client] Fix Shopping Wages Port", "FixShoppingWages", 3844431026, "dxseba");

    /// <summary>
    /// LEOPARD's "Kenshi Job Management (MCM fork) (QoL)" (workshop 3818460323, matched against its 2026-11-01 update), with dxseba's "[Meitou
    /// Client] Job Management Port".
    /// </summary>
    static readonly EndorsedPort JobManagement = new(
        "KenshiJobManagement", [new(3818460323, 1793577496)], "[Meitou Client] Job Management Port", "JobManagement", 3844431040, "dxseba");

    /// <summary>
    /// Jakematt2004's "Job Tabs" (workshop 3813790518, matched against its 2026-10-05 update), with dxseba's "[Meitou Client] Job Tabs Port".
    /// </summary>
    static readonly EndorsedPort JobTabs = new(
        "JobTabs", [new(3813790518, 1791164245)], "[Meitou Client] Job Tabs Port", "JobTabs", 3844431048, "dxseba");

    /// <summary>
    /// SaturnVI_OuO's "Kenshi Affixes | 装备随机词条 [RE_Kenshi]" (workshop 3821983877, matched against its 2026-10-26 update), with dxseba's "[Meitou
    /// Client] Kenshi Affixes Port".
    /// </summary>
    static readonly EndorsedPort KenshiAffixes = new(
        "KenshiAffixes", [new(3821983877, 1793047348)], "[Meitou Client] Kenshi Affixes Port", "KenshiAffixes", 3844431053, "dxseba");

    /// <summary>
    /// Ekiuts's "Kenshi Compact" (workshop 3763089712, matched against its 2026-08-19 update), with dxseba's "[Meitou Client] Kenshi Compact Port".
    /// </summary>
    static readonly EndorsedPort KenshiCompact = new(
        "KenshiCompact", [new(3763089712, 1787167045)], "[Meitou Client] Kenshi Compact Port", "KenshiCompact", 3844431060, "dxseba");

    /// <summary>
    /// redderth's "LimbToughness - Max HP to Limb [RE_Kenshi]" (workshop 3787222853, matched against its 2026-08-29 update), with dxseba's "[Meitou
    /// Client] Limb Toughness Port".
    /// </summary>
    static readonly EndorsedPort LimbToughness = new(
        "LimbToughness", [new(3787222853, 1788042766)], "[Meitou Client] Limb Toughness Port", "LimbToughness", 3844431063, "dxseba");

    /// <summary>
    /// Edenflora's "属性上限 Limited Stats (RE_Kenshi)" (workshop 3807762285, matched against its 2026-10-19 update), with dxseba's "[Meitou Client]
    /// Limited Stats Port".
    /// </summary>
    static readonly EndorsedPort LimitedStats = new(
        "Limit Talent", [new(3807762285, 1792411807)], "[Meitou Client] Limited Stats Port", "LimitedStats", 3841647179, "dxseba");

    /// <summary>
    /// LEOPARD's "Map markers (MCM fork) (QoL)" (workshop 3813852161, matched against its 2026-11-04 update), with dxseba's "[Meitou Client] Map
    /// Markers Port".
    /// </summary>
    static readonly EndorsedPort MapMarkers = new(
        "Map-markers", [new(3813852161, 1793810767)], "[Meitou Client] Map Markers Port", "MapMarkers", 3844431073, "dxseba");

    /// <summary>
    /// MENTAL's "KENSHI: MENTORS" (workshop 3834394158, matched against its 2026-11-05 update), with dxseba's "[Meitou Client] Mentors Port".
    /// </summary>
    static readonly EndorsedPort Mentors = new(
        "Mentors", [new(3834394158, 1793917850)], "[Meitou Client] Mentors Port", "Mentors", 3844431079, "dxseba");

    /// <summary>
    /// john warosa's "Metabolic Traversal" (workshop 3709852838, matched against its 2026-04-19 update), with dxseba's "[Meitou Client] Metabolic
    /// Traversal Port".
    /// </summary>
    static readonly EndorsedPort MetabolicTraversal = new(
        "Metabolic Traversal", [new(3709852838, 1776588013)], "[Meitou Client] Metabolic Traversal Port", "MetabolicTraversal", 3844431090, "dxseba");

    /// <summary>
    /// Thenewcomer91's "PersistentPrisoners (RE_Kenshi)" (workshop 3798215878, matched against its 2026-09-13 update), with dxseba's "[Meitou Client]
    /// Persistent Prisoners Port".
    /// </summary>
    static readonly EndorsedPort PersistentPrisoners = new(
        "PersistentPrisoners", [new(3798215878, 1789307536)],
        "[Meitou Client] Persistent Prisoners Port", "PersistentPrisoners", 3844431102, "dxseba");

    /// <summary>
    /// гопсосмыком's "Posh Damage Numbers" (workshop 3821781631, matched against its 2026-10-31 update), with dxseba's "[Meitou Client] Posh Damage
    /// Numbers Port".
    /// </summary>
    static readonly EndorsedPort PoshDamageNumbers = new(
        "DamageNumbers", [new(3821781631, 1793486736)], "[Meitou Client] Posh Damage Numbers Port", "PoshDamageNumbers", 3844431107, "dxseba");

    /// <summary>
    /// luckyren's "Queen Start with Birthing" (workshop 3777203526, matched against its 2026-08-29 update), with dxseba's "[Meitou Client] Queen
    /// Birthing Port".
    /// </summary>
    static readonly EndorsedPort QueenBirthing = new(
        "QueenBirth", [new(3777203526, 1788026960)], "[Meitou Client] Queen Birthing Port", "QueenBirthing", 3844431111, "dxseba");

    /// <summary>
    /// Basusu's "SleepFix" (workshop 3787375792, matched against its 2026-09-06 update), with dxseba's "[Meitou Client] Sleep Fix Port".
    /// </summary>
    static readonly EndorsedPort SleepFix = new(
        "SleepFix", [new(3787375792, 1788679213)], "[Meitou Client] Sleep Fix Port", "SleepFix", 3844431114, "dxseba");

    /// <summary>
    /// Lingyang crow's "Sparring 切磋 - 队友互练 (RE_Kenshi)" (workshop 3776308916, matched against its 2026-08-29 update), with dxseba's "[Meitou Client]
    /// Sparring Port".
    /// </summary>
    static readonly EndorsedPort Sparring = new(
        "Sparring", [new(3776308916, 1788023150)], "[Meitou Client] Sparring Port", "Sparring", 3844431123, "dxseba");

    /// <summary>
    /// Au's "Squad Autonomy" (workshop 3800729618, matched against its 2026-10-16 update), with dxseba's "[Meitou Client] Squad Autonomy Port".
    /// </summary>
    static readonly EndorsedPort SquadAutonomy = new(
        "SquadAutonomy", [new(3800729618, 1792129131)], "[Meitou Client] Squad Autonomy Port", "SquadAutonomy", 3844431127, "dxseba");

    /// <summary>
    /// LEOPARD's "Vital Read (MCM fork) (QoL)" (workshop 3813852230, matched against its 2026-11-01 update), with dxseba's "[Meitou Client] Vital
    /// Read Port".
    /// </summary>
    static readonly EndorsedPort VitalRead = new(
        "Vital-Read", [new(3813852230, 1793577431)], "[Meitou Client] Vital Read Port", "VitalRead", 3844431130, "dxseba");

    /// <summary>
    /// LEOPARD's "Vital Sense (MCM fork) (QoL)" (workshop 3813852288, matched against its 2026-11-13 update), with dxseba's "[Meitou Client] Vital
    /// Sense Port".
    /// </summary>
    static readonly EndorsedPort VitalSense = new(
        "Vital-Sense", [new(3813852288, 1794544314)], "[Meitou Client] Vital Sense Port", "VitalSense", 3844431141, "dxseba");

    /// <summary>
    /// LEOPARD's "Wanted Map (QoL)" (workshop 3813761812, matched against its 2026-11-21 update), with dxseba's "[Meitou Client] Wanted Map Port".
    /// </summary>
    static readonly EndorsedPort WantedMap = new(
        "WantedMap", [new(3813761812, 1795297616)], "[Meitou Client] Wanted Map Port", "WantedMap", 3844431147, "dxseba");

    /// <summary>
    /// tata's "Weapon Dodge & Guard" (workshop 3767056805, matched against its 2026-08-22 update), with dxseba's "[Meitou Client] Weapon Dodge &
    /// Guard Port".
    /// </summary>
    static readonly EndorsedPort WeaponDodgeGuard = new(
        "WeaponDodgeGuard", [new(3767056805, 1787386406)], "[Meitou Client] Weapon Dodge & Guard Port", "WeaponDodgeGuard", 3844431155, "dxseba");

    /// <summary>
    /// klimsat's "Work for Cats - Get Hired at NPC Workbenches" (workshop 3802290991, matched against its 2026-09-15 update), with dxseba's "[Meitou
    /// Client] Work for Cats Port".
    /// </summary>
    static readonly EndorsedPort WorkForCats = new(
        "Work for Cats - Get Hired at NPC Workbenches", [new(3802290991, 1789500209)],
        "[Meitou Client] Work for Cats Port", "WorkForCats", 3844431159, "dxseba");

    /// <summary>
    /// Alexander's "XP Info - experience gain notifications" (workshop 3795213616, matched against its 2026-09-03 update), with dxseba's "[Meitou
    /// Client] XP Info Port".
    /// </summary>
    static readonly EndorsedPort XpInfo = new(
        "xp_info", [new(3795213616, 1788472284)], "[Meitou Client] XP Info Port", "XpInfo", 3844431170, "dxseba");

    /// <summary>
    /// Steam Workshop ports Meitou endorses. For originals with alternatives, the first entry is the default offer.
    /// </summary>
    public static IReadOnlyList<EndorsedPort> Endorsed { get; } =
    [
        Execution, ProvingGrounds, Disarm, ItTicklesMe, KillCounter, LevelUpNotify, SpeedMenu, StackSort, TauntExpansion, AreaGuard, BetterLooting,
        BuildingSearchBar, Cannibalism, ElementalEnchantment, EquipmentDurability, FixShoppingWages, JobManagement, JobTabs, KenshiAffixes,
        KenshiCompact, LimbToughness, LimitedStats, MapMarkers, Mentors, MetabolicTraversal, PersistentPrisoners, PoshDamageNumbers, QueenBirthing,
        SleepFix, Sparring, SquadAutonomy, VitalRead, VitalSense, WantedMap, WeaponDodgeGuard, WorkForCats, XpInfo,

        new(null, [new(3824431439)], "[Meitou Client] Action RPG Port", null, 3845649512, "dxseba"),
        new(null, [new(3742288543)], "[Meitou Client] Advanced Medic AI Port", null, 3845649561, "dxseba"),
        new(null, [new(3784221846)], "[Meitou Client] Arcane Magic Port", null, 3845649596, "dxseba"),
        new(null, [new(3798599010)], "[Meitou Client] Attack & Block Speed Uncapped Port", null, 3845649628, "dxseba"),
        new(null, [new(3813851956)], "[Meitou Client] Auto-Pause on Load Port", null, 3845649668, "dxseba"),
        new(null, [new(3824087152)], "[Meitou Client] Best Minimap Port", null, 3845649700, "dxseba"),
        new(null, [new(3800922376)], "[Meitou Client] BodyMorphCap Port", null, 3845649729, "dxseba"),
        new(null, [new(3838294517)], "[Meitou Client] BowMod Port", null, 3845649836, "dxseba"),
        new(null, [new(3813761881)], "[Meitou Client] Build Mode Gizmo Port", null, 3845650286, "dxseba"),
        new(null, [new(3807964959)], "[Meitou Client] Character Edit Tweaks Port", null, 3845650336, "dxseba"),
        new(null, [new(3791017374)], "[Meitou Client] Characters Emotions Port", null, 3845650357, "dxseba"),
        new(null, [new(3833747223)], "[Meitou Client] Cheat Menu Port", null, 3845650372, "dxseba"),
        new(null, [new(3833745044)], "[Meitou Client] City Builder Port", null, 3845650394, "dxseba"),
        new(null, [new(3805088430)], "[Meitou Client] Colored Stats Port", null, 3845650427, "dxseba"),
        new(null, [new(3813852014)], "[Meitou Client] Container Highlight Port", null, 3845650451, "dxseba"),
        new(null, [new(3796024680)], "[Meitou Client] Desolation System Port", null, 3845650481, "dxseba"),
        new(null, [new(3737240806)], "[Meitou Client] Direct Control Port", null, 3845650498, "dxseba"),
        new(null, [new(3805040918)], "[Meitou Client] Director Panel Port", null, 3845650517, "dxseba"),
        new(null, [new(3788937435)], "[Meitou Client] Dynamic Wasteland Port", null, 3845650554, "dxseba"),
        new(null, [new(3829117994)], "[Meitou Client] Earthworks Port", null, 3845650577, "dxseba"),
        new("Extra Inventory Sections Plugin", [], "[Meitou Client] Extra Inventory Sections Port", null, 3844431017, "dxseba"),
        new(null, [new(3798913525)], "[Meitou Client] Flowing Hair Port", null, 3845650600, "dxseba"),
        new(null, [new(3787399736)], "[Meitou Client] Gate Fix Port", null, 3845650647, "dxseba"),
        new(null, [new(3812150687)], "[Meitou Client] Gear Compare Alt Port", null, 3845650670, "dxseba"),
        new(null, [new(3833105321)], "[Meitou Client] Glowing Lightsabers Port", null, 3845650799, "dxseba"),
        new("Go Prone", [], "[Meitou Client] Go Prone Port", null, 3844431033, "dxseba"),
        new(null, [new(3775510993)], "[Meitou Client] Guard Break & Bash Port", null, 3845650698, "dxseba"),
        new(null, [new(3087291056)], "[Meitou Client] GunManufacture+ Sounds Port", null, 3845650731, "dxseba"),
        new(null, [new(3800064617)], "[Meitou Client] Happy Families Port", null, 3845650757, "dxseba"),
        new(null, [new(3816608451)], "[Meitou Client] Item Markers Port", null, 3845650777, "dxseba"),
        new(null, [new(3781354452)], "[Meitou Client] Medic Splint Rigging Port", null, 3845650824, "dxseba"),
        new(null, [new(3803535940)], "[Meitou Client] Medkit Merge Port", null, 3845650858, "dxseba"),
        new(null, [new(3812165089)], "[Meitou Client] Nitoryu Port", null, 3845650929, "dxseba"),
        new(null, [new(3778746024)], "[Meitou Client] Organ Harvesting Port", null, 3845650967, "dxseba"),
        new(null, [new(3816121254), new(3688602277)], "[Meitou Client] Organize the Crafting Stations Port", null, 3845651002, "dxseba"),
        new(null, [new(3813852189)], "[Meitou Client] Organize the Trader Port", null, 3845651032, "dxseba"),
        new("QuestsAndContracts", [new(3797459646)], "[Meitou Client] Quests & Contracts Port", null, 3845651071, "dxseba"),
        new(null, [new(3813761725)], "[Meitou Client] Quick Start Select Port", null, 3845651099, "dxseba"),
        new("SentientSandsRebirth", [new(3819590113)], "[Meitou Client] Sentient Sands Port", null, 3845651129, "dxseba"),
        new(null, [new(3826183068)], "[Meitou Client] Shop Counter Restock Port", null, 3845651150, "dxseba"),
        new("SlaverMod", [new(3828708454)], "[Meitou Client] SlaverMod Port", null, 3845651170, "dxseba"),
        new(null, [new(3810561672)], "[Meitou Client] Struggle Port", null, 3845651187, "dxseba"),
        new(null, [new(3836973645)], "[Meitou Client] The Force Awakening Port", null, 3845650628, "dxseba"),
        new("Guild Escort Contracts", [new(3798671709)], "[Meitou Client] The Mercenarie Port", null, 3845650896, "dxseba"),
        new(null, [new(3841482484)], "[Meitou Client] Transmog Port", null, 3845651204, "dxseba"),
        new(null, [new(3799167934)], "[Meitou Client] Uncap Defence Port", null, 3845651234, "dxseba"),
        new("Sparring", [new(3776308916)], "[Meitou] Sparring", "SparringMeitou", 3842016392, "BeatTheCount"),
    ];

    /// <summary>
    /// The public wishlist of RE_Kenshi mods for Meitou, where players ask for one to be covered.
    /// </summary>
    public static Uri Wishlist { get; } = new("https://docs.google.com/spreadsheets/d/1h6HXRgT1MbioPtgj3enuXIdevj2JWaRU8qSz-99Haoo/edit?gid=0#gid=0");

    /// <summary>
    /// The entry that has <paramref name="mod"/> built in, by its <c>.mod</c> name, its code mod's id or its workshop item; null when none does.
    /// </summary>
    public static BuiltInMod? BuiltInOf(ModInfo mod) => BuiltInOf(mod, mod.HasCode ? CodeModManifest.TryReadId(mod.CodeManifestPath) : null);

    /// <summary>
    /// The entry that has <paramref name="mod"/>, with code mod id <paramref name="codeId"/> (null: no code mod), built in, by its <c>.mod</c>
    /// name, that id or its workshop item; null when none does.
    /// </summary>
    public static BuiltInMod? BuiltInOf(ModInfo mod, string? codeId)
    {
        ulong? item = WorkshopId(mod);
        return BuiltIn.FirstOrDefault(c => c.ModName == mod.Name
            || (codeId is not null && c.CodeId == codeId)
            || (item is { } id && c.Items.Any(i => i.Id == id)));
    }

    /// <summary>
    /// The default endorsed port of an original mod, when available.
    /// </summary>
    public static EndorsedPort? PortOf(ModInfo mod) => PortsOf(mod).FirstOrDefault();

    /// <summary>
    /// The endorsed ports of an original mod, in preference order.
    /// </summary>
    public static IEnumerable<EndorsedPort> PortsOf(ModInfo mod)
    {
        if (mod.HasCode) return [];

        ulong? item = WorkshopId(mod);
        return Endorsed.Where(port => item is { } id && port.Items.Any(original => original.Id == id)
            || mod.HasNativePlugin && port.ModName == mod.Name);
    }

    /// <summary>
    /// When the workshop copy of <paramref name="mod"/> was updated, if after the version <paramref name="items"/> was matched against
    /// (<see cref="WorkshopUpdates"/>); null for a local mod, an item the table does not name, a record file that cannot be read or has no
    /// entry for it, or no newer update.
    /// </summary>
    public static DateTimeOffset? UpdatedSince(ModInfo mod, IReadOnlyList<MatchedItem> items)
    {
        if (WorkshopId(mod) is not { } id || items.FirstOrDefault(i => i.Id == id)?.Updated is not { } matched) return null;

        return WorkshopUpdates.TimeUpdated(mod.Folder, id) is { } updated && updated > matched
            ? DateTimeOffset.FromUnixTimeSeconds(updated)
            : null;
    }

    /// <summary>
    /// Whether <paramref name="mods"/> hold the port <paramref name="port"/> names: a workshop mod by its item, a local mod (a copy in Kenshi's
    /// mods folder) by its <c>.mod</c> name and its code (several ports keep the original's <c>.mod</c> name, so a local original is no port).
    /// </summary>
    public static bool HasPort(EndorsedPort port, IEnumerable<ModInfo> mods) =>
        mods.Any(m => WorkshopId(m) is { } id ? id == port.PortWorkshopId : m.HasCode && m.Name == port.PortModName);

    /// <summary>
    /// The workshop item of <paramref name="mod"/>: a workshop mod's folder is named by its published file id. Null for a local mod.
    /// </summary>
    public static ulong? WorkshopId(ModInfo mod) =>
        mod.Source == ModSource.Workshop && ulong.TryParse(Path.GetFileName(mod.Folder), out var id) ? id : null;

    /// <summary>
    /// The Steam workshop page of item <paramref name="id"/>.
    /// </summary>
    public static Uri WorkshopPage(ulong id) => new($"https://steamcommunity.com/sharedfiles/filedetails/?id={id}");
}
