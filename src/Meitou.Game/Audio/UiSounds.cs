using Meitou.Data;
using Meitou.Data.Fcs;

namespace Meitou.Game.Audio;

/// <summary>
/// One interface sound (docs/audio.md "Interface"): an event on the GUI object, with the switch (or state) the game sets first. <see cref="Item"/> is
/// the record id of an inventory item whose <c>Inventory_Items</c> value the host looks up (<see cref="UiSounds.ItemSound"/>); <see cref="Building"/>
/// is the record id of a selected building whose <c>select sound</c> is the <c>Buildings_Click</c> value (<see cref="UiSounds.BuildingSound"/>; a
/// building without one posts nothing).
/// </summary>
public readonly record struct UiSoundEvent(
    string Event, string? Group = null, string? Value = null, bool State = false, string? Item = null, string? Building = null);

/// <summary>
/// Plays the interface's sounds on the GUI object, 110 (<b>Verified</b>: every handler of the exe posts on it; <c>FUN_140257d00</c> and the handlers
/// listed in docs/audio.md "Interface"). Unconditional like the original's handlers; the callers decide when.
/// </summary>
public sealed class UiSounds(WwisePlayer player, Func<GameDatabase?> database)
{
    /// <summary>
    /// The game object of the interface.
    /// </summary>
    public const ulong GuiObject = 110;

    /// <summary>
    /// The <c>Inventory_Items</c> values of a WEAPON or CROSSBOW by its <c>material</c>.
    /// </summary>
    static readonly string[] Weapon = ["Weapon_Metal", "Weapon_Wood"];
    /// <summary>
    /// The <c>Inventory_Items</c> values of an ARMOUR by its <c>material type</c>.
    /// </summary>
    static readonly string[] Armour = ["Fabric", "Leather", "Robotic_Component", "Steel_Bar"];
    /// <summary>
    /// The <c>Inventory_Items</c> values of an ITEM, CONTAINER, NEST_ITEM or MAP_ITEM by its <c>inventory sound</c>.
    /// </summary>
    static readonly string[] Items =
    [
        "Backpack", "Building_Material", "Fabric", "Flour", "Food", "Kits", "Leather", "Luxury", "Narcotics", "Ore", "Potato",
        "Robotic_Component", "Rum", "Steel_Bar", "Tools", "Water", "Wheat", "Armor_Plating", "Blueprints", "Meat",
    ];

    /// <summary>
    /// The <c>Buildings_Click</c> values by a BUILDING's <c>select sound</c> (<b>Verified</b>: the table at 0x141e17308 of kenshi_x64.exe, read by
    /// <c>FUN_1405504a0</c>; 0 posts nothing, the duplicate names are the exe's).
    /// </summary>
    static readonly string[] BuildingClicks =
    [
        "", "Bed", "Dummy", "Farm", "Generator", "Grain_Silo", "House", "Mine_Ore", "Mine_Stone", "Research_Bench", "Store_Armor",
        "Store_Building_Materials", "Store_Flour", "Store_Ore", "Store_Prisoner", "Store_Rum", "Store_Stones", "Stove", "Well",
        "Wind_Generator", "Chain_Bench", "Fabric_Loom", "Leather_Bench", "Medical_Desk", "Plate_Station", "Shop_Counter", "Smithy",
        "Stone_Processor", "Armorer", "Bar", "Boxes", "Campfire", "Gate", "Gen_Goods", "Hive_Hut", "Lamp", "Long_House", "Outhouse",
        "Store_Armor", "Store_Building_Materials", "Store_Flour", "Store_Ore", "Store_Prisoner", "Tailor", "Tower", "Travel", "Turret",
        "Wall_Metal", "Wall_Station_House", "Wall_Stone", "Wall_Wood", "Wood_Obj_Big", "Wood_Obj_Small",
    ];

    /// <summary>
    /// Events posted since the start.
    /// </summary>
    public int Posted { get; private set; }

    /// <summary>
    /// Sets the event's switch or state (an item's <c>Inventory_Items</c> or a building's <c>Buildings_Click</c> when it names one) and posts it on
    /// the GUI object; a building without a click sound posts nothing.
    /// </summary>
    public void Play(in UiSoundEvent e)
    {
        if (e.Group is { } group && e.Value is { } value)
        {
            if (e.State) player.SetState(group, value);
            else player.SetSwitch(group, value, GuiObject);
        }
        else if (e.Item is { } item && ItemSound(item) is { } sound) player.SetSwitch("Inventory_Items", sound, GuiObject);
        else if (e.Building is { } building)
        {
            if (BuildingSound(building) is not { } click) return;
            player.SetSwitch("Buildings_Click", click, GuiObject);
        }
        player.Post(e.Event, GuiObject);
        Posted++;
    }

    /// <summary>
    /// The <c>Buildings_Click</c> value of a BUILDING record (its <c>select sound</c>, 1 to 52); null for 0, a missing field or an unknown record.
    /// </summary>
    public string? BuildingSound(string recordId)
    {
        if (database()?.Find(recordId) is not { } r) return null;

        int i = r.GetInt("select sound");
        return i > 0 && i < BuildingClicks.Length ? BuildingClicks[i] : null;
    }

    /// <summary>
    /// The <c>Inventory_Items</c> value of an item (<b>Verified</b>, <c>FUN_14075e360</c> and its tables): a WEAPON or CROSSBOW by <c>material</c>
    /// (Weapon_Metal, Weapon_Wood), ARMOUR by <c>material type</c> (Fabric, Leather, Robotic_Component, Steel_Bar), ITEM, CONTAINER, NEST_ITEM and
    /// MAP_ITEM by <c>inventory sound</c> (Backpack to Meat), LIMB_REPLACEMENT Robotic_Component; null for any other record.
    /// </summary>
    public string? ItemSound(string recordId)
    {
        if (database()?.Find(recordId) is not { } r) return null;
        static string? Of(string[] names, int i) => (uint)i < names.Length ? names[i] : null;
        return r.Type switch
        {
            FcsRecordType.Weapon or FcsRecordType.Crossbow => Of(Weapon, r.GetInt("material")),
            FcsRecordType.Armour => Of(Armour, r.GetInt("material type")),
            FcsRecordType.Item or FcsRecordType.Container or FcsRecordType.NestItem or FcsRecordType.MapItem =>
                Of(Items, r.GetInt("inventory sound")),
            FcsRecordType.LimbReplacement => "Robotic_Component",
            _ => null,
        };
    }
}