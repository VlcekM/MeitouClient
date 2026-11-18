namespace ProvingGrounds.Core;

/// <summary>Every record stringID the original's code names (task 1.1): 139 of the data mod (Workshop 3774745195,
/// `-Proving Grounds.mod`), 3 of `gamedata.base`, 2 of `Newwworld.mod`, 2 of `rebirth.mod`. The list is the literal ids in
/// `upstream/ProvingGrounds/plugin/src/*`; no id is built at runtime (no `"%d-..."` format or concatenation exists there).
/// `PgRecordsTests` re-greps the sources and fails if this list and the code drift apart. Each comment gives the record type and
/// name in the shipped .mod (DATA §1, PA §4, TW §4) and the C++ files that name the id.</summary>
public static class PgRecords
{
    const string M = "-Proving Grounds.mod";
    const string G = "-gamedata.base";
    const string N = "-Newwworld.mod";
    const string R = "-rebirth.mod";

    // Player-arena buildings and functions (PA §4)
    /// <summary>old 0.x arena, not in the 1.3.4 .mod; only accepted. C++: ArenaIdentity.cpp.</summary>
    public const string OldArena = "36" + M;
    /// <summary>BUILDING "Banner". C++: ArenaIdentity.cpp.</summary>
    public const string Banner = "42" + M;
    /// <summary>BUILDING "Registry". C++: ArenaIdentity.cpp.</summary>
    public const string Registry = "45" + M;
    /// <summary>BUILDING_FUNCTIONALITY "pg_reg_func". C++: ArenaIdentity.cpp.</summary>
    public const string RegistryFunc = "47" + M;
    /// <summary>BUILDING "Leaderboards". C++: ArenaIdentity.cpp, LeaderboardData.h.</summary>
    public const string Leaderboard = "53" + M;
    /// <summary>BUILDING_FUNCTIONALITY "pg_lead_func". C++: ArenaIdentity.cpp, LeaderboardData.h.</summary>
    public const string LeaderboardFunc = "57" + M;
    /// <summary>BUILDING_FUNCTIONALITY "pg_banner_func". C++: ArenaIdentity.cpp.</summary>
    public const string BannerFunc = "60" + M;
    /// <summary>BUILDING "Arena". C++: TownArenaRuntimePolicy.h.</summary>
    public const string Arena = "183" + M;
    /// <summary>BUILDING "Arena (small)". C++: ArenaIdentity.cpp.</summary>
    public const string SmallArena = "334" + M;
    /// <summary>TOWN "Scratch". C++: MarksHUD.cpp.</summary>
    public const string ScratchTown = "90" + M;
    /// <summary>CHARACTER "Null". C++: RewardsDialoguePolicy.h.</summary>
    public const string Null = "166" + M;

    // Town buildings, functions and residents (TW §4)
    /// <summary>BUILDING "Registry Town". C++: TownArenaRuntimePolicy.h.</summary>
    public const string TownRegistry = "195" + M;
    /// <summary>BUILDING "Leaderboards Town". C++: ArenaIdentity.cpp, LeaderboardData.h.</summary>
    public const string TownLeaderboard = "272" + M;
    /// <summary>BUILDING_FUNCTIONALITY "pg_lead_func_town". C++: ArenaIdentity.cpp, LeaderboardData.h.</summary>
    public const string TownLeaderboardFunc = "273" + M;
    /// <summary>BUILDING "Scratch Hospital". C++: TownAftercare.cpp.</summary>
    public const string ScratchHospital = "163" + M;
    /// <summary>BUILDING "Arena Pillow". C++: TownSpectators.cpp.</summary>
    public const string ArenaPillow = "192" + M;
    /// <summary>BUILDING "Announcer Spot". C++: TownAnnouncerPolicy.h.</summary>
    public const string AnnouncerSpot = "261" + M;
    /// <summary>CHARACTER "Scratch Medic". C++: TownArenaRuntimePolicy.h.</summary>
    public const string ScratchMedic = "100" + M;
    /// <summary>CHARACTER "Scratch Bookie". C++: TownBookie.cpp.</summary>
    public const string Bookie = "111" + M;
    /// <summary>CHARACTER "Scratch Civilian". C++: TownSpectatorPolicy.h.</summary>
    public const string ScratchCivilian = "103" + M;
    /// <summary>CHARACTER "Scratch Bar Resident". C++: TownSpectatorPolicy.h.</summary>
    public const string ScratchBarResident = "258" + M;
    /// <summary>CHARACTER "Scratch Pit Fighter Cage handler" (TownSpectatorPolicy.h CageHandler). C++: TownSpectatorPolicy.h.</summary>
    public const string CageHandler = "99" + M;
    /// <summary>CHARACTER "Scratch Robotics Trader" (TownLimbShop.cpp kTraderId). C++: TownLimbShop.cpp.</summary>
    public const string RoboticsTrader = "243" + M;
    /// <summary>CHARACTER "Scratch Announcer". C++: TownAnnouncerPolicy.h.</summary>
    public const string ScratchAnnouncer = "260" + M;

    // Unique town fighters (TownChallengePolicy.h Role, TownMatchmakingPolicy.h Named)
    /// <summary>CHARACTER "Ressa Vane" (easy unique). C++: TownChallengePolicy.h, TownMatchmakingPolicy.h.</summary>
    public const string RessaVane = "616" + M;
    /// <summary>CHARACTER "Bum-Per" (medium unique). C++: TownChallengePolicy.h, TownMatchmakingPolicy.h.</summary>
    public const string BumPer = "619" + M;
    /// <summary>CHARACTER "Veyr the Gilded" (hard unique). C++: TownChallengePolicy.h, TownMatchmakingPolicy.h.</summary>
    public const string VeyrTheGilded = "623" + M;
    /// <summary>CHARACTER "Senn" (hard unique). C++: TownAnnouncerPolicy.h, TownChallengePolicy.h, TownMatchmakingPolicy.h.</summary>
    public const string Senn = "169" + M;
    /// <summary>CHARACTER "Torka" (hard unique). C++: TownAnnouncerPolicy.h, TownChallengePolicy.h, TownMatchmakingPolicy.h.</summary>
    public const string Torka = "168" + M;
    /// <summary>CHARACTER "Skarn" (final challenge). C++: TownAnnouncerPolicy.h, TownChallengePolicy.h, TownMatchmakingPolicy.h.</summary>
    public const string Skarn = "167" + M;

    // Fighter catalog (TownFighterCatalog.h; character and the squad that spawns it, named from the catalog row type and tier)
    /// <summary>character. C++: TownFighterCatalog.h.</summary>
    public const string FighterPolearmEasy = "219" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterPolearmEasySquad = "221" + M;
    /// <summary>character. C++: TownFighterCatalog.h.</summary>
    public const string FighterPolearmMedium = "223" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterPolearmMediumSquad = "222" + M;
    /// <summary>character. C++: TownFighterCatalog.h.</summary>
    public const string FighterPolearmHard = "225" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterPolearmHardSquad = "224" + M;
    /// <summary>character. C++: TownFighterCatalog.h.</summary>
    public const string FighterSabreEasy = "206" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterSabreEasySquad = "208" + M;
    /// <summary>character. C++: TownChallengePolicy.h, TownFighterCatalog.h.</summary>
    public const string FighterSabreMedium = "96" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterSabreMediumSquad = "209" + M;
    /// <summary>character. C++: TownFighterCatalog.h.</summary>
    public const string FighterSabreHard = "207" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterSabreHardSquad = "105" + M;
    /// <summary>character. C++: TownFighterCatalog.h.</summary>
    public const string FighterKatanaEasy = "213" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterKatanaEasySquad = "214" + M;
    /// <summary>character. C++: TownChallengePolicy.h, TownFighterCatalog.h.</summary>
    public const string FighterKatanaMedium = "97" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterKatanaMediumSquad = "106" + M;
    /// <summary>character. C++: TownFighterCatalog.h.</summary>
    public const string FighterKatanaHard = "210" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterKatanaHardSquad = "211" + M;
    /// <summary>character. C++: TownFighterCatalog.h.</summary>
    public const string FighterChampionEasy = "218" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterChampionEasySquad = "217" + M;
    /// <summary>character. C++: TownChallengePolicy.h, TownFighterCatalog.h.</summary>
    public const string FighterChampionMedium = "98" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterChampionMediumSquad = "107" + M;
    /// <summary>character. C++: TownFighterCatalog.h.</summary>
    public const string FighterChampionHard = "216" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterChampionHardSquad = "215" + M;
    /// <summary>character. C++: TownFighterCatalog.h.</summary>
    public const string FighterMaceEasy = "232" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterMaceEasySquad = "231" + M;
    /// <summary>character. C++: TownFighterCatalog.h.</summary>
    public const string FighterMaceMedium = "230" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterMaceMediumSquad = "229" + M;
    /// <summary>character. C++: TownFighterCatalog.h.</summary>
    public const string FighterMaceHard = "227" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterMaceHardSquad = "226" + M;
    /// <summary>character. C++: TownFighterCatalog.h.</summary>
    public const string FighterGreatswordEasy = "238" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterGreatswordEasySquad = "237" + M;
    /// <summary>character. C++: TownFighterCatalog.h.</summary>
    public const string FighterGreatswordMedium = "240" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterGreatswordMediumSquad = "239" + M;
    /// <summary>character. C++: TownFighterCatalog.h.</summary>
    public const string FighterGreatswordHard = "236" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterGreatswordHardSquad = "235" + M;
    /// <summary>character. C++: TownFighterCatalog.h.</summary>
    public const string FighterMartialEasy = "298" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterMartialEasySquad = "297" + M;
    /// <summary>character. C++: TownFighterCatalog.h.</summary>
    public const string FighterMartialMedium = "302" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterMartialMediumSquad = "299" + M;
    /// <summary>character. C++: TownFighterCatalog.h.</summary>
    public const string FighterMartialHard = "300" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterMartialHardSquad = "301" + M;
    /// <summary>character (the catalog also lists 665 as a character and 666 as its squad). C++: TownFighterCatalog.h.</summary>
    public const string FighterChampionHardK = "666" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterChampionHardKSquad = "665" + M;
    /// <summary>character. C++: TownFighterCatalog.h.</summary>
    public const string FighterChampionHardS = "670" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterChampionHardSSquad = "669" + M;
    /// <summary>character. C++: TownFighterCatalog.h.</summary>
    public const string FighterChampionHardP = "673" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterChampionHardPSquad = "672" + M;
    /// <summary>character. C++: TownFighterCatalog.h.</summary>
    public const string FighterChampionHardMartial = "681" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterChampionHardMartialSquad = "680" + M;
    /// <summary>character. C++: TownFighterCatalog.h.</summary>
    public const string FighterHackerEasy = "675" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterHackerEasySquad = "674" + M;
    /// <summary>character. C++: TownFighterCatalog.h.</summary>
    public const string FighterHackerMedium = "677" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterHackerMediumSquad = "676" + M;
    /// <summary>character. C++: TownFighterCatalog.h.</summary>
    public const string FighterHackerHard = "679" + M;
    /// <summary>squad. C++: TownFighterCatalog.h.</summary>
    public const string FighterHackerHardSquad = "678" + M;

    // Rewards: research and blueprint items (ArenaRewards.cpp kGeneralRewards)
    /// <summary>RESEARCH "Proving Grounds". C++: ArenaRewards.cpp.</summary>
    public const string ResearchProvingGrounds = "50" + M;
    /// <summary>RESEARCH "Proving Grounds II". C++: ArenaRewards.cpp.</summary>
    public const string ResearchProvingGroundsII = "52" + M;
    /// <summary>ITEM "Arena Buildings" (code: "Arena Buildings I"). C++: ArenaRewards.cpp.</summary>
    public const string BlueprintArenaBuildingsI = "280" + M;
    /// <summary>ITEM "Arena Buildings II". C++: ArenaRewards.cpp.</summary>
    public const string BlueprintArenaBuildingsII = "281" + M;

    // Rewards: supplies (ArenaRewards.cpp kGeneralRewards; names as the code shows them)
    /// <summary>ITEM "Pit Surgeon Kit" in the .mod. C++: ArenaRewards.cpp.</summary>
    public const string PitSurgeonRoll = "291" + M;
    /// <summary>ITEM "Bone-Setting Kit" in the .mod. C++: ArenaRewards.cpp.</summary>
    public const string BoneSetterBraces = "293" + M;
    /// <summary>ITEM "Ironmending Kit" in the .mod. C++: ArenaRewards.cpp.</summary>
    public const string IronmenderCase = "295" + M;

    // Rewards: armour (ArenaRewards.cpp, RewardDelivery.cpp)
    /// <summary>ARMOUR. C++: ArenaRewards.cpp.</summary>
    public const string WarlordHelmet = "58" + M;
    /// <summary>ARMOUR. C++: ArenaRewards.cpp.</summary>
    public const string WarlordPlateArmour = "61" + M;
    /// <summary>ARMOUR. C++: ArenaRewards.cpp.</summary>
    public const string WarlordPlateskirt = "67" + M;
    /// <summary>ARMOUR. C++: ArenaRewards.cpp.</summary>
    public const string WarlordPlateBoots = "68" + M;
    /// <summary>ARMOUR "Warlord Cloak (Neck)", the EISP cloak. C++: ArenaRewards.cpp, RewardDelivery.cpp.</summary>
    public const string WarlordCloakNeck = "72" + M;
    /// <summary>ARMOUR, the vanilla cloak (RewardDelivery.cpp only). C++: RewardDelivery.cpp.</summary>
    public const string WarlordCloak = "89" + M;
    /// <summary>ARMOUR. C++: ArenaRewards.cpp.</summary>
    public const string EnforcerPlateChest = "119" + M;
    /// <summary>ARMOUR. C++: ArenaRewards.cpp.</summary>
    public const string EnforcerLegplates = "121" + M;
    /// <summary>ARMOUR. C++: ArenaRewards.cpp.</summary>
    public const string EnforcerPlatedBoots = "123" + M;
    /// <summary>ARMOUR. C++: ArenaRewards.cpp.</summary>
    public const string EnforcerHelmet = "125" + M;
    /// <summary>ARMOUR. C++: ArenaRewards.cpp.</summary>
    public const string RetainerLeatherPants = "127" + M;
    /// <summary>ARMOUR. C++: ArenaRewards.cpp.</summary>
    public const string RetainerLeatherArmour = "129" + M;
    /// <summary>ARMOUR. C++: ArenaRewards.cpp.</summary>
    public const string RetainerLeatherBoots = "131" + M;
    /// <summary>ARMOUR. C++: ArenaRewards.cpp.</summary>
    public const string RetainerLeatherHelmet = "134" + M;
    /// <summary>ARMOUR. C++: ArenaRewards.cpp.</summary>
    public const string PitfighterHarness = "282" + M;
    /// <summary>ARMOUR. C++: ArenaRewards.cpp.</summary>
    public const string PitfighterFootwraps = "283" + M;
    /// <summary>ARMOUR. C++: ArenaRewards.cpp.</summary>
    public const string PitfighterJawGuard = "284" + M;
    /// <summary>ARMOUR. C++: ArenaRewards.cpp.</summary>
    public const string PitfighterPants = "285" + M;

    // Rewards: weapons, weapon models per grade, manufacturers (ArenaRewards.cpp, RewardDelivery.cpp)
    /// <summary>WEAPON. C++: ArenaRewards.cpp.</summary>
    public const string WarlordGreatsword = "70" + M;
    /// <summary>WEAPON. C++: ArenaRewards.cpp.</summary>
    public const string WarlordNodachi = "81" + M;
    /// <summary>WEAPON. C++: ArenaRewards.cpp.</summary>
    public const string WarlordPolearm = "85" + M;
    /// <summary>WEAPON. C++: ArenaRewards.cpp.</summary>
    public const string WarlordMace = "137" + M;
    /// <summary>WEAPON. C++: ArenaRewards.cpp.</summary>
    public const string WarlordSabre = "138" + M;
    /// <summary>WEAPON "Warlord Cleaver" in the .mod; the C++ shows it as "Warlord Hacker" (ArenaRewards.cpp:273-275,
    /// PieceHacker). C++: ArenaRewards.cpp.</summary>
    public const string WarlordCleaver = "139" + M;
    /// <summary>WEAPON. C++: ArenaRewards.cpp.</summary>
    public const string WarlordKatana = "181" + M;
    /// <summary>WEAPON. C++: ArenaRewards.cpp.</summary>
    public const string WarlordLongsword = "182" + M;
    /// <summary>weapon model. C++: ArenaRewards.cpp.</summary>
    public const string PitReaver = "76" + M;
    /// <summary>weapon model. C++: ArenaRewards.cpp.</summary>
    public const string ArenaReaver = "77" + M;
    /// <summary>weapon model "Warlord's Judgment" in the .mod; the C++ display name is spelled "Warlord's Judgement"
    /// (ArenaRewards.cpp:131; the only spelling of it in the C++). C++: ArenaRewards.cpp.</summary>
    public const string WarlordsJudgment = "78" + M;
    /// <summary>weapon model. C++: ArenaRewards.cpp.</summary>
    public const string PitFang = "82" + M;
    /// <summary>weapon model. C++: ArenaRewards.cpp.</summary>
    public const string ArenaFang = "83" + M;
    /// <summary>weapon model "Warlord's Fang". C++: ArenaRewards.cpp.</summary>
    public const string WarlordsFang = "84" + M;
    /// <summary>weapon model. C++: ArenaRewards.cpp.</summary>
    public const string PitCleaver = "86" + M;
    /// <summary>weapon model. C++: ArenaRewards.cpp.</summary>
    public const string ArenaCleaver = "87" + M;
    /// <summary>weapon model "Warlord's Cleaver". C++: ArenaRewards.cpp.</summary>
    public const string WarlordsCleaver = "88" + M;
    /// <summary>weapon model. C++: ArenaRewards.cpp.</summary>
    public const string PitBreaker = "140" + M;
    /// <summary>weapon model. C++: ArenaRewards.cpp.</summary>
    public const string ArenaCrusher = "141" + M;
    /// <summary>weapon model "Warlord's Verdict". C++: ArenaRewards.cpp.</summary>
    public const string WarlordsVerdict = "142" + M;
    /// <summary>weapon model. C++: ArenaRewards.cpp.</summary>
    public const string PitCutter = "143" + M;
    /// <summary>weapon model. C++: ArenaRewards.cpp.</summary>
    public const string ArenaTalon = "144" + M;
    /// <summary>weapon model "Warlord's Talon". C++: ArenaRewards.cpp.</summary>
    public const string WarlordsTalon = "145" + M;
    /// <summary>weapon model "Pit Reaper" in the .mod; the C++ shows it as "Pit Cleaver" (ArenaRewards.cpp:273, the Hacker
    /// high-grade row). The C++ also shows <see cref="PitCleaver"/> (86) as "Pit Cleaver" (ArenaRewards.cpp:171, the Polearm row): pick by id. C++: ArenaRewards.cpp.</summary>
    public const string PitReaper = "146" + M;
    /// <summary>weapon model. C++: ArenaRewards.cpp.</summary>
    public const string ArenaReaper = "147" + M;
    /// <summary>weapon model "Warlord's Ruin". C++: ArenaRewards.cpp.</summary>
    public const string WarlordsRuin = "148" + M;
    /// <summary>WEAPON_MANUFACTURER "Arena Forge". C++: ArenaRewards.cpp.</summary>
    public const string ArenaForge = "149" + M;
    /// <summary>WEAPON_MANUFACTURER "Null Forged". C++: ArenaRewards.cpp, RewardDelivery.cpp.</summary>
    public const string NullForged = "71" + M;

    // Base game and other mods
    /// <summary>gamedata.base; supply reward and medic restock. C++: ArenaRewards.cpp, MedicSupplyPolicy.h.</summary>
    public const string AdvancedFirstAidKit = "1359" + G;
    /// <summary>gamedata.base; supply reward. C++: ArenaRewards.cpp.</summary>
    public const string SplintKit = "1435" + G;
    /// <summary>gamedata.base; supply reward and robot medic restock. C++: ArenaRewards.cpp, MedicSupplyPolicy.h.</summary>
    public const string SkeletonRepairKit = "18020" + G;
    /// <summary>Newwworld.mod; limb shop (TownLimbShop.cpp kLimbData). C++: TownLimbShop.cpp.</summary>
    public const string SkeletonArmLeft = "95747" + N;
    /// <summary>Newwworld.mod; limb shop. C++: TownLimbShop.cpp.</summary>
    public const string SkeletonArmRight = "95748" + N;
    /// <summary>rebirth.mod; limb shop. C++: TownLimbShop.cpp.</summary>
    public const string SkeletonLegLeft = "95872" + R;
    /// <summary>rebirth.mod; limb shop. C++: TownLimbShop.cpp.</summary>
    public const string SkeletonLegRight = "96022" + R;

    /// <summary>Must all exist for the mod to run (36 is not in 1.3.4 and is only accepted, so it is not here).</summary>
    public static readonly IReadOnlyList<string> Required = new string[] {Registry, RegistryFunc, Arena, SmallArena, Banner, BannerFunc, Leaderboard,
        LeaderboardFunc, TownLeaderboard, TownLeaderboardFunc, TownRegistry, ScratchMedic, ScratchTown, Null, Bookie };
}
