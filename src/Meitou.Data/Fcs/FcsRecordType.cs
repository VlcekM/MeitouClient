namespace Meitou.Data.Fcs;

/// <summary>
/// Record type numbers (<see cref="FcsRecord.Type"/>). Names are the fcs.def section names, matched by
/// comparing the fields records use against each section (<c>meitou-tools fcs-types</c>); the evidence
/// per type is in docs/formats/fcs-mod.md. Numbers not listed here aren't used by the base game, or
/// (53, 56, 63, 92) have no matching fcs.def section.
/// </summary>
public enum FcsRecordType
{
    BUILDING = 0,
    CHARACTER = 1,
    WEAPON = 2,
    ARMOUR = 3,
    ITEM = 4,
    ANIMAL_ANIMATION = 5,
    ATTACHMENT = 6,
    RACE = 7,
    FACTION = 10,
    TOWN = 13,
    LOCATIONAL_DAMAGE = 16,
    COMBAT_TECHNIQUE = 17,
    DIALOGUE = 18,
    DIALOGUE_LINE = 19,
    RESEARCH = 21,
    AI_TASK = 22,
    ANIMATION = 24,
    STATS = 25,
    PERSONALITY = 26,
    CONSTANTS = 27,
    BIOMES = 28,
    BUILDING_PART = 29,

    /// <summary>Dialogue conditions and effects; not an fcs.def section (named by the records themselves).</summary>
    DIALOG_ACTION = 31,

    /// <summary>Tentative: matched on a single field.</summary>
    REPEATABLE_BUILDING_PART_SLOT = 43,
    MATERIAL_SPEC = 44,

    /// <summary>Tentative: matched on a single field.</summary>
    MATERIAL_SPECS_COLLECTION = 45,
    CONTAINER = 46,
    MATERIAL_SPECS_CLOTHING = 47,
    VENDOR_LIST = 49,
    MATERIAL_SPECS_WEAPON = 50,
    WEAPON_MANUFACTURER = 51,
    SQUAD_TEMPLATE = 52,
    COLOR_DATA = 55,

    /// <summary>Tentative: half the fields used are not in fcs.def.</summary>
    FOLIAGE_LAYER = 59,
    FOLIAGE_MESH = 60,
    GRASS = 61,
    BUILDING_FUNCTIONALITY = 62,
    NEW_GAME_STARTOFF = 64,
    WILDLIFE_BIRDS = 68,
    MAP_FEATURES = 69,

    /// <summary>Tentative: matched on a single field.</summary>
    DIPLOMATIC_ASSAULTS = 70,
    SINGLE_DIPLOMATIC_ASSAULT = 71,
    AI_PACKAGE = 72,
    DIALOGUE_PACKAGE = 73,
    GUN_DATA = 74,
    ANIMAL_CHARACTER = 76,
    UNIQUE_SQUAD_TEMPLATE = 77,
    FACTION_TEMPLATE = 78,
    WEATHER = 80,
    SEASON = 81,
    EFFECT = 82,
    ITEM_PLACEMENT_GROUP = 83,
    WORD_SWAPS = 84,
    NEST_ITEM = 86,
    CHARACTER_PHYSICS_ATTACHMENT = 87,
    LIGHT = 88,
    HEAD = 89,
    FACTION_CAMPAIGN = 93,
    BIOME_GROUP = 95,
    EFFECT_FOG_VOLUME = 96,
    FARM_DATA = 97,
    FARM_PART = 98,
    ENVIRONMENT_RESOURCES = 99,

    /// <summary>Tentative: matched on 2 of 3 fields.</summary>
    RACE_GROUP = 100,
    ARTIFACTS = 101,
    MAP_ITEM = 102,
    BUILDINGS_SWAP = 103,
    ITEMS_CULTURE = 104,
    ANIMATION_EVENT = 105,
    CROSSBOW = 107,
    AMBIENT_SOUND = 109,
    WORLD_EVENT_STATE = 110,
    LIMB_REPLACEMENT = 111,
    ANIMATION_FILE = 112,
}
