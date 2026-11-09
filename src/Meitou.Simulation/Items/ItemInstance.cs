using System.Collections.Concurrent;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Items;

/// <summary>
/// One item a character carries (an INVENTORY_ITEM_STATE of a save). The fields keep the save's names (docs/formats/save.md): <c>base data sid</c> =
/// <see cref="Record"/>, <c>material sid</c>, <c>company sid</c>, <c>section</c>, <c>inventory x</c> / <c>inventory y</c>, <c>quantity</c>,
/// <c>level</c>, <c>item function</c>, <c>quality</c>, <c>charges</c>.
/// </summary>
public sealed class ItemInstance
{
    /// <summary>
    /// The item's record id (the save's <c>base data sid</c>).
    /// </summary>
    public required string Record { get; init; }
    /// <summary>
    /// The record's name.
    /// </summary>
    public string Name { get; init; } = "";
    /// <summary>
    /// The item's type: its record's, or <see cref="FcsRecordType.Blueprint"/> for a blueprint.
    /// </summary>
    public FcsRecordType Type { get; init; }
    /// <summary>
    /// The armour section allowed by the record's slot; empty for other items.
    /// </summary>
    public string EquipmentSection { get; init; } = "";
    /// <summary>
    /// ARMOUR <c>is locked</c> (the original makes it a <c>LockedArmour</c>: shackles); worn, it shows the portrait's <c>Slave</c> overlay
    /// (docs/game/ui-screens.md "Portrait cells").
    /// </summary>
    public bool IsLocked { get; init; }
    /// <summary>
    /// The record's race limits (<see cref="ItemInfo.WornOnlyBy"/>, <see cref="ItemInfo.WornNeverBy"/>): who may put it in an equipment section.
    /// </summary>
    public string[] WornOnlyBy { get; init; } = [];
    /// <summary>
    /// The races that may not wear it (<see cref="ItemInfo.WornNeverBy"/>).
    /// </summary>
    public string[] WornNeverBy { get; init; } = [];
    /// <summary>
    /// Whether a character of the RACE <paramref name="race"/> may wear it (null: no wearer, no limit).
    /// </summary>
    public bool WearableBy(string? race, bool animal = false) => Wearable(WornOnlyBy, WornNeverBy, race, animal);
    /// <summary>
    /// Whether a wearer of <paramref name="race"/> passes the only and never lists (<see cref="RaceLimiter.Allows"/>).
    /// </summary>
    internal static bool Wearable(string[] only, string[] never, string? race, bool animal = false) => RaceLimiter.Allows(only, never, race, animal);
    /// <summary>
    /// Shackles' lock (level = ARMOUR <c>lock level</c> clamped 0..100, its <c>hardness</c>, locked when made: <c>FUN_14089a440</c>); null for other
    /// items.
    /// </summary>
    public Locks.LockState? Lock { get; set; }
    /// <summary>
    /// Worn and effectively locked: the item cannot be taken off, sold or moved (LockedArmour's tooltip: "locked in place and cannot be removed").
    /// </summary>
    public bool LockedOn => Lock is { EffectivelyLocked: true } && InventoryCommands.IsEquipmentSection(Section);
    /// <summary>
    /// The material record (the save's <c>material sid</c>; a blueprint's research); empty: none.
    /// </summary>
    public string MaterialId { get; set; } = "";
    /// <summary>
    /// The manufacturer record (the save's <c>company sid</c>; a blueprint's research); empty: none.
    /// </summary>
    public string CompanyId { get; set; } = "";
    /// <summary>
    /// Where it is: <c>main</c>, <c>shirt</c>, <c>head</c>, <c>boots</c>, <c>armour</c>, <c>legs</c>, <c>hip</c>, <c>back</c>, <c>backpack_attach</c>
    /// (a worn backpack), <c>backpack_content</c> (inside it).
    /// </summary>
    public string Section { get; set; } = "main";
    /// <summary>
    /// The cell column in its section's grid (the save's <c>inventory x</c>).
    /// </summary>
    public int X { get; set; }
    /// <summary>
    /// The cell row (the save's <c>inventory y</c>).
    /// </summary>
    public int Y { get; set; }
    /// <summary>
    /// The units in the stack.
    /// </summary>
    int _quantity = 1;
    /// <summary>
    /// The units in the stack (the save's <c>quantity</c>); fewer drop that many from the buy-back receipt, more drop the receipt.
    /// </summary>
    public int Quantity
    {
        get => _quantity;
        set
        {
            if (value < _quantity && Buyback is { } receipt) Buyback = receipt.Skip(_quantity - value);
            else if (value > _quantity) Buyback = null;
            _quantity = value;
        }
    }
    /// <summary>
    /// The receipt of the trade that bought the stack; read only while its trade window is open.
    /// </summary>
    TradeReceipt? _buyback;
    /// <summary>
    /// The receipt of the trade that bought the stack, for undoing it at the same coins; null once its trade window has closed.
    /// </summary>
    internal TradeReceipt? Buyback
    {
        get => _buyback is { Session.Active: true } ? _buyback : null;
        set => _buyback = value;
    }
    /// <summary>
    /// The level (the save's <c>level</c>).
    /// </summary>
    public int Level { get; set; }
    /// <summary>
    /// The level the original's item vfunc 0x2b8 reports, which its item price (<c>FUN_1407a8f90</c>) takes as the quality and its grade colour reads
    /// (<b>Verified</b>, vtables): a gear piece's (weapon, armour, crossbow, robotic limb) <see cref="Level"/> (<c>Gear+0x224</c>), 0 for any other
    /// item (plain, map, nest, money, blueprint, severed limb, backpack).
    /// </summary>
    public int GearLevel => GearLevelOf(Type, Level);
    /// <summary>
    /// <see cref="GearLevel"/> for an item of <paramref name="type"/> at <paramref name="level"/>.
    /// </summary>
    public static int GearLevelOf(FcsRecordType type, int level) =>
        type is FcsRecordType.Weapon or FcsRecordType.Armour or FcsRecordType.Crossbow or FcsRecordType.LimbReplacement ? level : 0;
    /// <summary>
    /// The name of the smith who crafted the item at a bench (the save key <c>crafter</c>; empty: not crafted by the player's smith). The tooltip
    /// prints it with the player faction's name.
    /// </summary>
    public string Crafter { get; set; } = "";
    /// <summary>
    /// The faction whose uniform the piece is (the save key <c>uniform</c>, the faction record id; empty: none): armour with one sells and buys at
    /// 0.3 of its price (<c>Armour+0x1e8</c>, <c>FUN_1408a9c20</c>, <b>Verified</b>).
    /// </summary>
    public string Uniform { get; set; } = "";
    /// <summary>
    /// The COLOR_DATA it is dyed with (the save key <c>color sid</c>; empty: none): its record's <c>color</c> when made, else the scheme of the first
    /// wearer (<see cref="Meitou.Data.Gameplay.ItemColours"/>).
    /// </summary>
    public string ColourId { get; set; } = "";
    /// <summary>
    /// The item function (the save's <c>item function</c>, <see cref="ItemInfo.Function"/> when made).
    /// </summary>
    public int ItemFunction { get; set; }
    /// <summary>
    /// Quality 0 to 100 (clothing from the character's armour grade).
    /// </summary>
    public float Quality { get; set; }
    /// <summary>
    /// Charges left; for food the record's charges times CONSTANTS <c>food quality mult</c>, which is what is eaten (100 points are one hunger
    /// level).
    /// </summary>
    public float Charges { get; set; } = 1;
    /// <summary>
    /// The charges of a full unit (the record's, as <see cref="Charges"/> starts); a stack of ammunition refills <see cref="Charges"/> from it when
    /// one unit is used up. 0 when not known (an item made elsewhere).
    /// </summary>
    public float MaxCharges { get; set; }
    /// <summary>
    /// A prosthetic's condition while it is not fitted, the save's <c>dam</c>, <c>wear</c> and <c>stun</c>
    /// (<see cref="Bodies.MedicalState.FitReplacement"/>); 0 for other items.
    /// </summary>
    public float LimbDamage { get; set; }
    /// <summary>
    /// A prosthetic's wear while not fitted (the save's <c>wear</c>).
    /// </summary>
    public float LimbWear { get; set; }
    /// <summary>
    /// A prosthetic's stun while not fitted (the save's <c>stun</c>).
    /// </summary>
    public float LimbStun { get; set; }
    /// <summary>
    /// Its width in inventory cells.
    /// </summary>
    public int Width { get; init; } = 1;
    /// <summary>
    /// Its height in inventory cells.
    /// </summary>
    public int Height { get; init; } = 1;
    /// <summary>
    /// Weight of one unit in kg as it counts in the inventory total (weapons already scaled by <c>weapon inventory weight mult</c>).
    /// </summary>
    public float UnitWeight { get => DynamicWeight is null ? _baseWeight : WorkedWeight(); init => _baseWeight = value; }
    /// <summary>
    /// The record weight of one unit.
    /// </summary>
    float _baseWeight;
    /// <summary>
    /// <see cref="DynamicWeight"/>'s answer for the inputs it was asked with (the inventory total reads it every tick); one object, so a reader on
    /// another thread sees a whole one.
    /// </summary>
    sealed record WeightMemo(int Level, string Material, string Company, float Weight);
    /// <summary>
    /// The last <see cref="DynamicWeight"/> answer and its inputs.
    /// </summary>
    WeightMemo? _weightMemo;
    /// <summary>
    /// The <see cref="DynamicWeight"/> weight, asked again only when the level, material or manufacturer changed.
    /// </summary>
    float WorkedWeight()
    {
        if (_weightMemo is { } m && m.Level == Level && m.Material == MaterialId && m.Company == CompanyId)
            return m.Weight;
        float weight = DynamicWeight!(this);
        _weightMemo = new(Level, MaterialId, CompanyId, weight);
        return weight;
    }
    /// <summary>
    /// The record weight (<see cref="UnitWeight"/> when nothing replaces it).
    /// </summary>
    public float RecordWeight => _baseWeight;
    /// <summary>
    /// What replaces <see cref="RecordWeight"/> for items whose weight is worked out from the item: an armour piece's by its level and part coverage
    /// (<see cref="Meitou.Data.Gameplay.Combat.ArmourData.WeightAt"/>), a melee weapon's by its level, manufacturer and material
    /// (<c>WeaponStats</c>); null for anything else.
    /// </summary>
    public Func<ItemInstance, float>? DynamicWeight { get; init; }
    /// <summary>
    /// How many units one cell holds (<see cref="ItemInfo.StackLimit"/>; 1: it does not stack). Inside a backpack the bag's bonus applies
    /// (<see cref="Inventory.LimitOf"/>).
    /// </summary>
    public int StackLimit { get; init; } = 1;
    /// <summary>
    /// The item is food (<see cref="ItemInfo.IsFood"/>).
    /// </summary>
    public bool IsFood { get; init; }
    /// <summary>
    /// The faction the item was stolen from (-1: none), the faction of the item's "taken from" owner (docs/game/carry-loot.md 4.4): set when the
    /// player's looting is a theft (<see cref="Carrying.CarrySystem"/>). The item counts as stolen while its holder is of another faction
    /// (<see cref="IsStolenFor"/>). Setting it alone leaves no owner character (a container's or the ground's mark); <see cref="Mark"/> sets both.
    /// </summary>
    public int StolenFrom { get => Mark.Faction; set => Mark = StolenMark.OfFaction(value); }
    /// <summary>
    /// The whole stolen mark: <see cref="StolenFrom"/> with the robbed character and its squad (the original's owner handle <c>ownedby</c>, item
    /// +0x140, whose container ids name the character's platoon). The fence rules test the owner's squad (docs/game/economy.md "Fencing, stealing and
    /// smuggling").
    /// </summary>
    public StolenMark Mark { get; set; } = StolenMark.None;
    /// <summary>
    /// The character the item was stolen from (<see cref="CharacterId.None"/>: none known).
    /// </summary>
    public CharacterId Owner => Mark.Owner;
    /// <summary>
    /// The squad <see cref="Owner"/> was in when robbed (-1: none known).
    /// </summary>
    public int OwnerSquad => Mark.Squad;
    /// <summary>
    /// The <c>ownedby</c> of <see cref="Saved"/> became <see cref="Owner"/> at load, so a save writes it from the mark (the null handle once the mark
    /// is cleared). Not hashed.
    /// </summary>
    internal bool OwnerFromSave { get; set; }
    /// <summary>
    /// One of its owner's CHARACTER / ANIMAL_CHARACTER <c>death items</c> (the save's <c>death</c>, docs/game/animals.md "Death items"): a live
    /// owner's own food search skips it, taking it from a live owner that is down butchers the owner, and it stops being one when it leaves the
    /// owner's inventory. Its stack does not merge with ordinary units.
    /// </summary>
    public bool Death { get; set; }
    /// <summary>
    /// The original's <c>Item::isUnique</c> (the save's <c>unique</c>), set on a town's artifacts (docs/game/economy.md "Artifacts"): a shop's fills
    /// and restocks leave it in place. It stacks only with other unique items (engine choice).
    /// </summary>
    public bool Unique { get; set; }
    /// <summary>
    /// A worn backpack's contents (null for other items).
    /// </summary>
    public Inventory? Contents { get; init; }
    /// <summary>
    /// The factor of the contents' weight (a bag's <c>encumbrance effect</c>).
    /// </summary>
    public float ContentsFactor { get; init; } = 1;
    /// <summary>
    /// The INVENTORY_ITEM_STATE a loaded item was read from; saving copies it so the keys the engine does not model survive (null for an item made in
    /// play). Not hashed.
    /// </summary>
    public FcsRecord? Saved { get; set; }

    /// <summary>
    /// A copy of this item with another quantity (a stack split off; never used for a backpack, whose contents are not copied).
    /// </summary>
    public ItemInstance WithQuantity(int quantity) => new()
    {
        Record = Record,
        Name = Name,
        Type = Type,
        EquipmentSection = EquipmentSection,
        IsLocked = IsLocked,
        WornOnlyBy = WornOnlyBy,
        WornNeverBy = WornNeverBy,
        MaterialId = MaterialId,
        CompanyId = CompanyId,
        Section = Section,
        X = X,
        Y = Y,
        Quantity = quantity,
        Level = Level,
        Crafter = Crafter,
        Uniform = Uniform,
        ColourId = ColourId,
        ItemFunction = ItemFunction,
        Quality = Quality,
        Charges = Charges,
        MaxCharges = MaxCharges,
        Width = Width,
        Height = Height,
        UnitWeight = _baseWeight,
        DynamicWeight = DynamicWeight,
        StackLimit = StackLimit,
        IsFood = IsFood,
        ContentsFactor = ContentsFactor,
        Mark = Mark,
        Death = Death,
        Unique = Unique,
        Buyback = Buyback?.Take(quantity),
    };

    /// <summary>
    /// Whether the item is stolen while held by a character of <paramref name="holderFaction"/> (<c>FUN_14079e550</c>, <b>Verified</b>: the
    /// from-owner is set and of another faction, and the holder's faction is not lawless: <c>not real</c> or BANDIT). <paramref name="data"/> null:
    /// no lawless test.
    /// </summary>
    public bool IsStolenFor(int holderFaction, PopulationData? data) => StolenFrom >= 0 && StolenFrom != holderFaction
            && data?.IsLawless(holderFaction) != true;

    /// <summary>
    /// The weight this item and what is in it add to the load: a bag's contents count times its <see cref="ContentsFactor"/> only while it is worn,
    /// in full anywhere else (<c>FUN_140749160</c>, docs/game/ui-screens.md "Backpack rows").
    /// </summary>
    public float Weight => WeightOf(skipDeath: false);

    /// <summary>
    /// <see cref="Weight"/>; with <paramref name="skipDeath"/> the death items among its contents weigh nothing
    /// (<see cref="Inventory.ContentWeight(bool)"/>).
    /// </summary>
    public float WeightOf(bool skipDeath) =>
        UnitWeight * Quantity + (Contents?.ContentWeight(skipDeath) ?? 0) * (Section == "backpack_attach" ? ContentsFactor : 1);

    /// <summary>
    /// How full the item (a stack's partly used unit) is, 0 to 1, for the inventory cell's charge bar, whose width is charges / maximum
    /// (<c>FUN_140711200</c>, <b>Observed</b>); null when the maximum is 1 or less (no bar: gear, plain items).
    /// </summary>
    public float? ChargeFraction => MaxCharges > 1 ? Math.Clamp(Charges / MaxCharges, 0f, 1f) : null;

    /// <summary>
    /// The hunger levels eating the item gives: <see cref="Charges"/> / 100.
    /// </summary>
    public float Nutrition => IsFood ? Charges / 100f : 0;
}

/// <summary>
/// An item's stolen mark (<see cref="ItemInstance.Mark"/>): the faction it was stolen from (-1: not stolen), and when known the robbed character and
/// its squad (the save's <c>ownedby</c> handle). Stacks merge only with the same mark.
/// </summary>
public readonly record struct StolenMark(int Faction, CharacterId Owner, int Squad)
{
    /// <summary>
    /// Not stolen.
    /// </summary>
    public static readonly StolenMark None = new(-1, CharacterId.None, -1);
    /// <summary>
    /// A mark naming only a faction (-1: none).
    /// </summary>
    public static StolenMark OfFaction(int faction) => new(faction, CharacterId.None, -1);
    /// <summary>
    /// The mark of an item taken from <paramref name="victim"/> (id <paramref name="id"/>): its faction, itself and its squad.
    /// </summary>
    public static StolenMark Of(CharacterCold victim, CharacterId id) => new(victim.Faction, id, victim.SquadId);
}