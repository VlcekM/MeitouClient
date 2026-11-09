namespace Meitou.Data.Fcs;

/// <summary>
/// Record type numbers (<see cref="FcsRecord.Type"/>), as numbered by the Forgotten Construction Set (its <c>itemType</c> enum, consecutive from 0).
/// Field evidence for the types the base game uses is in docs/formats/fcs-mod.md. Types the base game never uses (GAMESTATE_*, *_STATE, PLATOON, ...)
/// are probably for save games.
/// </summary>
public enum FcsRecordType
{
    /// <summary>
    /// <c>BUILDING</c> (fcs.def section): buildings and furniture; also a save handle's type (docs/formats/save.md "Handles"). Read by
    /// <see cref="Gameplay.Construction.BuildingBuildData"/> and the world layout.
    /// </summary>
    Building = 0,
    /// <summary>
    /// <c>CHARACTER</c> (fcs.def section): character templates; also a save handle's type. Read by <see cref="Characters.CharacterGenerator"/>
    /// and <see cref="Characters.CharacterAppearance"/>.
    /// </summary>
    Character = 1,
    /// <summary>
    /// <c>WEAPON</c> (fcs.def section): melee weapons; read by <see cref="Gameplay.Combat.CombatDatabase"/>.
    /// </summary>
    Weapon = 2,
    /// <summary>
    /// <c>ARMOUR</c> (fcs.def section): clothing and armour; read by <see cref="Gameplay.Combat.CombatDatabase"/>.
    /// </summary>
    Armour = 3,
    /// <summary>
    /// <c>ITEM</c> (fcs.def section): generic inventory items.
    /// </summary>
    Item = 4,
    /// <summary>
    /// <c>ANIMAL_ANIMATION</c> (fcs.def section): animal animation clips; read by <see cref="Gameplay.AnimationLibrary"/>.
    /// </summary>
    AnimalAnimation = 5,
    /// <summary>
    /// <c>ATTACHMENT</c> (fcs.def section): character attachments; read by <see cref="Characters.CharacterGenerator"/>
    /// and <see cref="Characters.CharacterDraft"/>.
    /// </summary>
    Attachment = 6,
    /// <summary>
    /// <c>RACE</c> (fcs.def section): races; read by <see cref="Gameplay.Bodies.RaceData"/>.
    /// </summary>
    Race = 7,
    /// <summary>
    /// <c>LOCATION</c>: no fcs.def section; not referenced in Meitou.
    /// </summary>
    Location = 8,
    /// <summary>
    /// <c>WAR_SAVESTATE</c>: a save's faction campaign state (docs/formats/save.md "WAR_SAVESTATE"); no fcs.def section.
    /// </summary>
    WarSavestate = 9,
    /// <summary>
    /// <c>FACTION</c> (fcs.def section): factions; read by <see cref="Gameplay.FactionData"/>.
    /// </summary>
    Faction = 10,
    /// <summary>
    /// <c>NULL_ITEM</c>: the type of the null save handle (docs/formats/save.md "Handles", <see cref="Save.Hand"/>); no fcs.def section.
    /// </summary>
    NullItem = 11,
    /// <summary>
    /// <c>ZONE_MAP</c>: no fcs.def section; not referenced in Meitou.
    /// </summary>
    ZoneMap = 12,
    /// <summary>
    /// <c>TOWN</c> (fcs.def section): towns and other places; also a save handle's type. Read by <see cref="Gameplay.TownData"/>.
    /// </summary>
    Town = 13,
    /// <summary>
    /// <c>WORLDMAP_CHARACTER</c>: no fcs.def section; not referenced in Meitou.
    /// </summary>
    WorldmapCharacter = 14,
    /// <summary>
    /// <c>CHARACTER_APPEARANCE_OLD</c>: no fcs.def section; not referenced in Meitou.
    /// </summary>
    CharacterAppearanceOld = 15,
    /// <summary>
    /// <c>LOCATIONAL_DAMAGE</c> (fcs.def section): a race's hit locations; read by <see cref="Gameplay.Bodies.RaceData"/>.
    /// </summary>
    LocationalDamage = 16,
    /// <summary>
    /// <c>COMBAT_TECHNIQUE</c> (fcs.def section): melee moves; read by <see cref="Gameplay.Combat.CombatDatabase"/>.
    /// </summary>
    CombatTechnique = 17,
    /// <summary>
    /// <c>DIALOGUE</c> (fcs.def section): conversations; read by <see cref="Gameplay.Dialogue.DialogueDatabase"/>.
    /// </summary>
    Dialogue = 18,
    /// <summary>
    /// <c>DIALOGUE_LINE</c> (fcs.def section): the lines of a <see cref="Dialogue"/>; read by <see cref="Gameplay.Dialogue.DialogueDatabase"/>.
    /// </summary>
    DialogueLine = 19,
    /// <summary>
    /// <c>TECHTREE</c>: no fcs.def section; not referenced in Meitou.
    /// </summary>
    Techtree = 20,
    /// <summary>
    /// <c>RESEARCH</c> (fcs.def section): research entries; read by <see cref="Gameplay.Construction.ResearchData"/>.
    /// </summary>
    Research = 21,
    /// <summary>
    /// <c>AI_TASK</c> (fcs.def section): AI task rows, the <c>enum</c> field a <see cref="Gameplay.Ai.TaskType"/>; read
    /// by <see cref="Gameplay.Ai.AiData"/>.
    /// </summary>
    AiTask = 22,
    /// <summary>
    /// <c>AI_STATE</c>: no fcs.def section; not referenced in Meitou.
    /// </summary>
    AiState = 23,
    /// <summary>
    /// <c>ANIMATION</c> (fcs.def section): character animation clips; read by <see cref="Gameplay.AnimationLibrary"/>.
    /// </summary>
    Animation = 24,
    /// <summary>
    /// <c>STATS</c> (fcs.def section): skill sets; a save's characters carry one (<see cref="Save.SaveBuilder"/>).
    /// </summary>
    Stats = 25,
    /// <summary>
    /// <c>PERSONALITY</c> (fcs.def section): dialogue personalities, read by the Simulation's dialogue.
    /// </summary>
    Personality = 26,
    /// <summary>
    /// <c>CONSTANTS</c> (fcs.def section): global constants, looked up by name and this type (docs/formats/fcs-mod.md "Game loader").
    /// </summary>
    Constants = 27,
    /// <summary>
    /// <c>BIOMES</c> (fcs.def section): the world's biome regions; a save keeps the region weather in one (<see cref="Save.SaveWeather"/>).
    /// </summary>
    Biomes = 28,
    /// <summary>
    /// <c>BUILDING_PART</c> (fcs.def section): the parts a building is assembled from.
    /// </summary>
    BuildingPart = 29,
    /// <summary>
    /// <c>INSTANCE_COLLECTION</c>: a list of placed instances, e.g. a zone file's <c>0-buildinglist</c> (docs/formats/zones.md); no fcs.def section.
    /// </summary>
    InstanceCollection = 30,
    /// <summary>
    /// <c>DIALOG_ACTION</c>: a dialogue condition or effect a <see cref="DialogueLine"/> references (docs/game/dialogue.md); no fcs.def section.
    /// </summary>
    DialogAction = 31,
    /// <summary>
    /// <c>TEMPORARY_INFO</c>: no fcs.def section; not referenced in Meitou.
    /// </summary>
    TemporaryInfo = 32,
    /// <summary>
    /// <c>MOD_FILENAME</c>: no fcs.def section; not referenced in Meitou.
    /// </summary>
    ModFilename = 33,
    /// <summary>
    /// <c>PLATOON</c>: a save's squad, naming its <c>.platoon</c> file; also a save handle's type (docs/formats/save.md); no fcs.def section.
    /// </summary>
    Platoon = 34,
    /// <summary>
    /// <c>GAMESTATE_BUILDING</c>: a save's state of one building (docs/formats/save.md); no fcs.def section.
    /// </summary>
    GamestateBuilding = 35,
    /// <summary>
    /// <c>GAMESTATE_CHARACTER</c>: a save's state of one character (docs/formats/save.md); no fcs.def section.
    /// </summary>
    GamestateCharacter = 36,
    /// <summary>
    /// <c>GAMESTATE_FACTION</c>: a save's state of one faction (docs/formats/save.md); no fcs.def section.
    /// </summary>
    GamestateFaction = 37,
    /// <summary>
    /// <c>GAMESTATE_TOWN_INSTANCE_LIST</c>: a save's list of player outposts and nests (docs/formats/save.md); no fcs.def section.
    /// </summary>
    GamestateTownInstanceList = 38,
    /// <summary>
    /// <c>STATE</c>: no fcs.def section; not referenced in Meitou.
    /// </summary>
    State = 39,
    /// <summary>
    /// <c>SAVED_STATE</c>: a save record (<see cref="Save.SaveGame"/>); no fcs.def section.
    /// </summary>
    SavedState = 40,
    /// <summary>
    /// <c>INVENTORY_STATE</c>: a save's inventory (<see cref="Save.SaveInventory"/>); no fcs.def section.
    /// </summary>
    InventoryState = 41,
    /// <summary>
    /// <c>INVENTORY_ITEM_STATE</c>: one item of a save's inventory (<see cref="Save.SaveInventory"/>); no fcs.def section.
    /// </summary>
    InventoryItemState = 42,
    /// <summary>
    /// <c>REPEATABLE_BUILDING_PART_SLOT</c> (fcs.def section); not referenced in Meitou.
    /// </summary>
    RepeatableBuildingPartSlot = 43,
    /// <summary>
    /// <c>MATERIAL_SPEC</c> (fcs.def section): materials meshes and items refer to.
    /// </summary>
    MaterialSpec = 44,
    /// <summary>
    /// <c>MATERIAL_SPECS_COLLECTION</c> (fcs.def section): sets of materials, read by the renderer's material resolver.
    /// </summary>
    MaterialSpecsCollection = 45,
    /// <summary>
    /// <c>CONTAINER</c> (fcs.def section): items that hold items.
    /// </summary>
    Container = 46,
    /// <summary>
    /// <c>MATERIAL_SPECS_CLOTHING</c> (fcs.def section): clothing materials, read by the item icons and the character renderer.
    /// </summary>
    MaterialSpecsClothing = 47,
    /// <summary>
    /// <c>GAMESTATE_BUILDING_INTERIOR</c>: no fcs.def section; not referenced in Meitou.
    /// </summary>
    GamestateBuildingInterior = 48,
    /// <summary>
    /// <c>VENDOR_LIST</c> (fcs.def section): shop stock lists; read by <see cref="Gameplay.Economy.VendorList"/>.
    /// </summary>
    VendorList = 49,
    /// <summary>
    /// <c>MATERIAL_SPECS_WEAPON</c> (fcs.def section): weapon material grades; read by <see cref="Gameplay.Combat.CombatDatabase"/>.
    /// </summary>
    MaterialSpecsWeapon = 50,
    /// <summary>
    /// <c>WEAPON_MANUFACTURER</c> (fcs.def section): weapon makers; read by <see cref="Gameplay.Combat.CombatDatabase"/>.
    /// </summary>
    WeaponManufacturer = 51,
    /// <summary>
    /// <c>SQUAD_TEMPLATE</c> (fcs.def section): squads; read by <see cref="Gameplay.SquadTemplate"/>.
    /// </summary>
    SquadTemplate = 52,
    /// <summary>
    /// <c>ROAD</c>: road segments of the world (docs/formats/zones.md); no fcs.def section.
    /// </summary>
    Road = 53,
    /// <summary>
    /// <c>LOCATION_NODE</c>: no fcs.def section; not referenced in Meitou.
    /// </summary>
    LocationNode = 54,
    /// <summary>
    /// <c>COLOR_DATA</c> (fcs.def section): colours items are tinted with (<see cref="Gameplay.ItemColours"/>).
    /// </summary>
    ColorData = 55,
    /// <summary>
    /// <c>CAMERA</c>: a save's camera view (<see cref="Save.SaveCamera"/>); no fcs.def section.
    /// </summary>
    Camera = 56,
    /// <summary>
    /// <c>MEDICAL_STATE</c>: a save's medical state of a character (<see cref="Save.SaveLimbs"/>); no fcs.def section.
    /// </summary>
    MedicalState = 57,
    /// <summary>
    /// <c>MEDICAL_PART_STATE</c>: no fcs.def section; not referenced in Meitou.
    /// </summary>
    MedicalPartState = 58,
    /// <summary>
    /// <c>FOLIAGE_LAYER</c> (fcs.def section): foliage layers of the world (docs/formats/foliage.md).
    /// </summary>
    FoliageLayer = 59,
    /// <summary>
    /// <c>FOLIAGE_MESH</c> (fcs.def section): the meshes a <see cref="FoliageLayer"/> places.
    /// </summary>
    FoliageMesh = 60,
    /// <summary>
    /// <c>GRASS</c> (fcs.def section): grass types of the foliage.
    /// </summary>
    Grass = 61,
    /// <summary>
    /// <c>BUILDING_FUNCTIONALITY</c> (fcs.def section): building functions, read by the town buildings and the lock fixtures.
    /// </summary>
    BuildingFunctionality = 62,
    /// <summary>
    /// <c>DAY_SCHEDULE</c>: no fcs.def section; not referenced in Meitou.
    /// </summary>
    DaySchedule = 63,
    /// <summary>
    /// <c>NEW_GAME_STARTOFF</c> (fcs.def section): new-game starts; read by <see cref="Gameplay.NewGameStart"/>.
    /// </summary>
    NewGameStartoff = 64,
    /// <summary>
    /// <c>GAMESTATE_CRAFTING</c>: no fcs.def section; not referenced in Meitou.
    /// </summary>
    GamestateCrafting = 65,
    /// <summary>
    /// <c>CHARACTER_APPEARANCE</c>: a character's look in a save's <c>.platoon</c> file (<see cref="Characters.AppearanceFile"/>); no
    /// fcs.def section.
    /// </summary>
    CharacterAppearance = 66,
    /// <summary>
    /// <c>GAMESTATE_AI</c>: a save's AI state of a character, its job list and order queue (docs/formats/save.md); no fcs.def section.
    /// </summary>
    GamestateAi = 67,
    /// <summary>
    /// <c>WILDLIFE_BIRDS</c> (fcs.def section): bird flocks; read by <see cref="Gameplay.BirdFlockData"/>.
    /// </summary>
    WildlifeBirds = 68,
    /// <summary>
    /// <c>MAP_FEATURES</c> (fcs.def section): placed world features, read by the world layout and the renderer.
    /// </summary>
    MapFeatures = 69,
    /// <summary>
    /// <c>DIPLOMATIC_ASSAULTS</c> (fcs.def section); not referenced in Meitou.
    /// </summary>
    DiplomaticAssaults = 70,
    /// <summary>
    /// <c>SINGLE_DIPLOMATIC_ASSAULT</c> (fcs.def section); not referenced in Meitou.
    /// </summary>
    SingleDiplomaticAssault = 71,
    /// <summary>
    /// <c>AI_PACKAGE</c> (fcs.def section): AI packages; read by <see cref="Gameplay.Ai.AiData"/>.
    /// </summary>
    AiPackage = 72,
    /// <summary>
    /// <c>DIALOGUE_PACKAGE</c> (fcs.def section): dialogue sets; read by <see cref="Gameplay.Dialogue.DialogueDatabase"/>.
    /// </summary>
    DialoguePackage = 73,
    /// <summary>
    /// <c>GUN_DATA</c> (fcs.def section): gun stats; read by <see cref="Gameplay.Combat.CombatDatabase"/> and
    /// <see cref="Gameplay.Combat.TurretGun"/>.
    /// </summary>
    GunData = 74,
    /// <summary>
    /// <c>HUMAN_CHARACTER</c>: no fcs.def section; not referenced in Meitou.
    /// </summary>
    HumanCharacter = 75,
    /// <summary>
    /// <c>ANIMAL_CHARACTER</c> (fcs.def section): animal species; read by <see cref="Gameplay.Bodies.AnimalData"/>.
    /// </summary>
    AnimalCharacter = 76,
    /// <summary>
    /// <c>UNIQUE_SQUAD_TEMPLATE</c> (fcs.def section): squads of unique characters; read by <see cref="Gameplay.SquadTemplate"/>.
    /// </summary>
    UniqueSquadTemplate = 77,
    /// <summary>
    /// <c>FACTION_TEMPLATE</c> (fcs.def section); not referenced in Meitou.
    /// </summary>
    FactionTemplate = 78,
    /// <summary>
    /// <c>AI_SCHEDULE</c> (fcs.def section); not referenced in Meitou.
    /// </summary>
    AiSchedule = 79,
    /// <summary>
    /// <c>WEATHER</c> (fcs.def section): weather types (docs/formats/weather.md).
    /// </summary>
    Weather = 80,
    /// <summary>
    /// <c>SEASON</c> (fcs.def section): seasons of the weather (docs/formats/weather.md).
    /// </summary>
    Season = 81,
    /// <summary>
    /// <c>EFFECT</c> (fcs.def section): particle effects weather and map features place.
    /// </summary>
    Effect = 82,
    /// <summary>
    /// <c>ITEM_PLACEMENT_GROUP</c> (fcs.def section): what a loose-item slot of an interior layout holds; read
    /// as <see cref="Gameplay.Economy.ItemPlacementGroup"/>.
    /// </summary>
    ItemPlacementGroup = 83,
    /// <summary>
    /// <c>WORD_SWAPS</c> (fcs.def section): word lists dialogue text swaps in; read by <see cref="Gameplay.Dialogue.DialogueDatabase"/>.
    /// </summary>
    WordSwaps = 84,
    /// <summary>
    /// <c>NEST</c>: an animal nest; also a save handle's type (docs/formats/save.md "Handles"); no fcs.def section.
    /// </summary>
    Nest = 85,
    /// <summary>
    /// <c>NEST_ITEM</c> (fcs.def section): nest items, read by the item icons and the item slots.
    /// </summary>
    NestItem = 86,
    /// <summary>
    /// <c>CHARACTER_PHYSICS_ATTACHMENT</c> (fcs.def section); not referenced in Meitou.
    /// </summary>
    CharacterPhysicsAttachment = 87,
    /// <summary>
    /// <c>LIGHT</c> (fcs.def section): lights; read by <see cref="Characters.CarriedLights"/> and the world layout.
    /// </summary>
    Light = 88,
    /// <summary>
    /// <c>HEAD</c> (fcs.def section); not referenced in Meitou.
    /// </summary>
    Head = 89,
    /// <summary>
    /// <c>BLUEPRINT</c>: research blueprints (docs/game/buildings-production.md); no fcs.def section.
    /// </summary>
    Blueprint = 90,
    /// <summary>
    /// <c>SHOP_TRADER_CLASS</c>: no fcs.def section; not referenced in Meitou.
    /// </summary>
    ShopTraderClass = 91,
    /// <summary>
    /// <c>FOLIAGE_BUILDING</c>: no fcs.def section; not referenced in Meitou.
    /// </summary>
    FoliageBuilding = 92,
    /// <summary>
    /// <c>FACTION_CAMPAIGN</c> (fcs.def section): faction campaigns; read by <see cref="Gameplay.CampaignData"/>.
    /// </summary>
    FactionCampaign = 93,
    /// <summary>
    /// <c>GAMESTATE_TOWN</c>: a save's state of one town (docs/formats/save.md); no fcs.def section.
    /// </summary>
    GamestateTown = 94,
    /// <summary>
    /// <c>BIOME_GROUP</c> (fcs.def section): biome groups; read by <see cref="Gameplay.BiomeGroupData"/>.
    /// </summary>
    BiomeGroup = 95,
    /// <summary>
    /// <c>EFFECT_FOG_VOLUME</c> (fcs.def section): fog volumes of the weather effects.
    /// </summary>
    EffectFogVolume = 96,
    /// <summary>
    /// <c>FARM_DATA</c> (fcs.def section): farm data; read by <see cref="Gameplay.Construction.MachineData"/>.
    /// </summary>
    FarmData = 97,
    /// <summary>
    /// <c>FARM_PART</c> (fcs.def section): the plants of a <see cref="FarmData"/>; read by <see cref="Gameplay.Construction.MachineData"/>.
    /// </summary>
    FarmPart = 98,
    /// <summary>
    /// <c>ENVIRONMENT_RESOURCES</c> (fcs.def section); not referenced in Meitou.
    /// </summary>
    EnvironmentResources = 99,
    /// <summary>
    /// <c>RACE_GROUP</c> (fcs.def section): race groups; read by <see cref="Characters.PlayableRaces"/>, <see cref="Gameplay.Bodies.SpecialFood"/>
    /// and the dialogue.
    /// </summary>
    RaceGroup = 100,
    /// <summary>
    /// <c>ARTIFACTS</c> (fcs.def section): artifact pools; read by <see cref="Gameplay.Economy.Artifacts"/>.
    /// </summary>
    Artifacts = 101,
    /// <summary>
    /// <c>MAP_ITEM</c> (fcs.def section): map items, one of the item types a vendor list sells
    /// (<see cref="Gameplay.Economy.VendorList.IsItemType"/>).
    /// </summary>
    MapItem = 102,
    /// <summary>
    /// <c>BUILDINGS_SWAP</c> (fcs.def section): building swap lists.
    /// </summary>
    BuildingsSwap = 103,
    /// <summary>
    /// <c>ITEMS_CULTURE</c> (fcs.def section): trade cultures; read by <see cref="Gameplay.Economy.TradeCulture"/>.
    /// </summary>
    ItemsCulture = 104,
    /// <summary>
    /// <c>ANIMATION_EVENT</c> (fcs.def section): animation events, read by the animation sounds.
    /// </summary>
    AnimationEvent = 105,
    /// <summary>
    /// <c>TUTORIAL</c>: no fcs.def section; not referenced in Meitou.
    /// </summary>
    Tutorial = 106,
    /// <summary>
    /// <c>CROSSBOW</c> (fcs.def section): ranged weapons; read by <see cref="Gameplay.Combat.CombatDatabase"/>.
    /// </summary>
    Crossbow = 107,
    /// <summary>
    /// <c>TERRAIN_DECALS</c>: a save's blood decals on the terrain (<see cref="Save.SaveDecals"/>); no fcs.def section.
    /// </summary>
    TerrainDecals = 108,
    /// <summary>
    /// <c>AMBIENT_SOUND</c> (fcs.def section): ambient sounds; read by <see cref="World.AmbientSound"/>.
    /// </summary>
    AmbientSound = 109,
    /// <summary>
    /// <c>WORLD_EVENT_STATE</c> (fcs.def section): world states; read by <see cref="Gameplay.WorldEventStateData"/>.
    /// </summary>
    WorldEventState = 110,
    /// <summary>
    /// <c>LIMB_REPLACEMENT</c> (fcs.def section): prosthetic limbs; read by <see cref="Gameplay.Bodies.LimbReplacement"/>.
    /// </summary>
    LimbReplacement = 111,
    /// <summary>
    /// <c>ANIMATION_FILE</c> (fcs.def section): skeleton animation files; read by <see cref="Characters.AnimationFiles"/>.
    /// </summary>
    AnimationFile = 112,
}