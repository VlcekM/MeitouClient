using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay.Construction;

/// <summary>
/// The crafts lists of a BUILDING_FUNCTIONALITY (fcs.def, <b>Verified</b>): what kind of record a bench makes.
/// </summary>
public enum CraftKind : byte
{
    /// <summary>
    /// <c>item crafts</c>: ITEMs.
    /// </summary>
    Item,
    /// <summary>
    /// <c>armour crafts</c>: ARMOURs.
    /// </summary>
    Armour,
    /// <summary>
    /// <c>weapon crafts</c>: weapon models, each crossed with the player manufacturer's weapon types.
    /// </summary>
    Weapon,
    /// <summary>
    /// <c>crossbow crafts</c>: CROSSBOWs.
    /// </summary>
    Crossbow,
    /// <summary>
    /// <c>backpack crafts</c>: CONTAINERs.
    /// </summary>
    Backpack,
    /// <summary>
    /// <c>robotics crafts</c>: LIMB_REPLACEMENTs.
    /// </summary>
    Robotics
}

/// <summary>
/// Something a crafting bench can queue (docs/game/buildings-production.md "Crafting"): the record made, for a weapon the MATERIAL_SPECS_WEAPON model
/// and its level (the <c>PLAYER_WEAPONS</c> manufacturer's <c>weapon models</c> val0), and how many finished items the bench output holds (<c>item
/// crafts</c> val0; 1 for the others, engine choice).
/// </summary>
public sealed record CraftOption(CraftKind Kind, string Item, string? Model, int Level, int Store)
{
    /// <summary>
    /// The queue's key: the item, and for a weapon its model.
    /// </summary>
    public string Key => Model is null ? Item : Item + "|" + Model;
}

/// <summary>
/// The input an item being crafted draws: the ITEM and the units per crafted item.
/// </summary>
public readonly record struct CraftInput(string Item, float PerItem);

/// <summary>
/// A craft's cost and time, computed when it starts (<b>Observed</b>, <c>FUN_1402b5ca0</c> and <c>CraftingBuilding::vfunc_182</c>).
/// </summary>
public sealed record CraftRecipe(CraftOption Option, string Name, float Hours, IReadOnlyList<CraftInput> Inputs, float Quality);

/// <summary>
/// A crafting bench (function 10, <c>CraftingBuilding</c>; docs/game/buildings-production.md "Crafting"): its crafts lists, the <c>tech level</c>
/// that caps the level of what it makes, its input sections (<c>consumes</c>) and whether they override the items' own ingredients.
/// </summary>
public sealed record CraftBench
{
    /// <summary>
    /// The WEAPON_MANUFACTURER whose <c>weapon types</c> and <c>weapon models</c> a weapon bench crafts.
    /// </summary>
    public const string PlayerWeapons = "PLAYER_WEAPONS";

    /// <summary>
    /// The BUILDING_FUNCTIONALITY <c>function</c> of a crafting bench.
    /// </summary>
    public const int CraftingFunction = 10;

    /// <summary>
    /// The Fabrics item, which armour draws at <c>fabrics amount</c> of its main material (<b>Verified</b>: the id is hard-coded in
    /// <c>CraftingBuilding::vfunc_182</c>).
    /// </summary>
    public const string Fabrics = "584-gamedata.base";

    /// <summary>
    /// The armour qualities by crafting tier (<b>Verified</b>, the jump table of <c>FUN_14082e6b0</c>): prototype to masterwork.
    /// </summary>
    public static readonly int[] ArmourQualities = [5, 20, 40, 60, 80, 95];

    /// <summary>
    /// What the bench can queue, in list order (<c>item crafts</c>, <c>armour crafts</c>, <c>crossbow crafts</c>, <c>backpack crafts</c>, <c>robotics
    /// crafts</c>, then the weapons).
    /// </summary>
    public IReadOnlyList<CraftOption> Options { get; init; } = [];

    /// <summary>
    /// <c>tech level</c> (0: no cap, read as 100).
    /// </summary>
    public int TechLevel { get; init; } = 100;

    /// <summary>
    /// <c>overrides ingredients</c>: the bench's own input sections replace the items' <c>ingredients</c>.
    /// </summary>
    public bool OverridesIngredients { get; init; }

    /// <summary>
    /// The bench of BUILDING_FUNCTIONALITY <paramref name="function"/>, null when its <c>function</c> is not <see cref="CraftingFunction"/>: every
    /// listed record the database has, a weapon bench's models crossed with the <see cref="PlayerWeapons"/> weapon types, and the tech level.
    /// </summary>
    public static CraftBench? From(GameDatabase db, GameRecord function)
    {
        if (function.GetInt("function", -1) != CraftingFunction) return null;
        var options = new List<CraftOption>();
        void List(string field, CraftKind kind)
        {
            foreach (var r in function.GetReferences(field))
                if (db.Find(r.TargetStringId) is not null)
                    options.Add(new CraftOption(kind, r.TargetStringId, null, 0, kind == CraftKind.Item ? Math.Max(1, r.Values.Value0) : 1));
        }
        List("item crafts", CraftKind.Item);
        List("armour crafts", CraftKind.Armour);
        List("crossbow crafts", CraftKind.Crossbow);
        List("backpack crafts", CraftKind.Backpack);
        List("robotics crafts", CraftKind.Robotics);
        // A weapon bench lists models; it crafts every weapon type of the player's manufacturer in each (Observed, the WEAPON scan of FUN_1402b9aa0;
        // the types come from PLAYER_WEAPONS).
        var models = function.GetReferences("weapon crafts").Select(r => r.TargetStringId).ToHashSet(StringComparer.Ordinal);
        if (models.Count > 0 && db.Find(PlayerWeapons) is { } maker)
        {
            var levels = maker.GetReferences("weapon models").Where(m => models.Contains(m.TargetStringId)).OrderBy(m => m.Values.Value0).ToList();
            foreach (var type in maker.GetReferences("weapon types"))
                if (db.Find(type.TargetStringId) is { Type: FcsRecordType.Weapon })
                    foreach (var m in levels)
                        options.Add(new CraftOption(CraftKind.Weapon, type.TargetStringId, m.TargetStringId, m.Values.Value0, 1));
        }
        int tech = function.GetInt("tech level");
        return new CraftBench
        {
            Options = options,
            TechLevel = tech > 0 ? tech : 100,
            OverridesIngredients = function.GetBool("overrides ingredients")
        };
    }

    /// <summary>
    /// The armour quality a crafter of <paramref name="skill"/> makes (<b>Verified</b>, <c>FUN_14082e6b0</c>): tier 1 from 20, 2 from 40, 3 from 60,
    /// 4 from 80, one more on a critical, capped at 5, and the tier's quality.
    /// </summary>
    public static int ArmourQuality(float skill, bool critical)
    {
        int s = (int)skill, tier = s > 79 ? 4 : s > 59 ? 3 : s > 39 ? 2 : s > 19 ? 1 : 0;
        if (critical && tier < 5) tier++;
        return ArmourQualities[tier];
    }

    /// <summary>
    /// The chance of a critical (one tier or model better; <b>Observed</b>, <c>FUN_1402b5900</c>): (skill - threshold) / 50, nothing below 0. Armour
    /// smiths' threshold is the floor of their skill's tier (80, 60, 40, 20, else 5); for the other benches it is the item's level.
    /// </summary>
    public static float CriticalChance(float skill, int stat, int level)
    {
        float threshold = stat is 8 or 38 ? (skill >= 80 ? 80 : skill >= 60 ? 60 : skill >= 40 ? 40 : skill >= 20 ? 20 : 5) : level;
        return skill - threshold < 0 ? 0 : Math.Clamp((skill - threshold) / 50f, 0, 1);
    }

    /// <summary>
    /// The craft's inputs and hours (<b>Observed</b>): each <c>ingredients</c> entry at v0 / 100 per item (the bench's <c>consumes</c> at v1 / 100
    /// when the bench overrides ingredients or the item has none), times the item's material cost (a weapon's <c>material cost</c>, else 1); armour
    /// draws its <c>total coverage</c> / 100 of the main material and that times <c>fabrics amount</c> of Fabrics. Hours: an ITEM's <c>production
    /// time</c>, a crossbow's or robotic limb's <c>craft time hrs</c> / P, a weapon's clamp(q^2 x 100 x <c>value</c> / 300 x 2 / P, 2, 32) at q = its
    /// level / 100 (<c>FUN_1402b76f0</c>), armour's below; a container's 4 (the field default, engine reading).
    /// </summary>
    public CraftRecipe Recipe(GameDatabase db, GameConstants constants, CraftOption option, IReadOnlyList<RecipeInput> consumes)
    {
        var record = db.Find(option.Item);
        string name = record?.Name ?? option.Item;
        if (option.Model is { } model && db.Find(model) is { } spec) name = spec.Name + " " + name;
        float p = constants.ProductionSpeed > 0 ? constants.ProductionSpeed : 2;
        float quality = option.Kind == CraftKind.Weapon ? option.Level : option.Kind == CraftKind.Armour ? ArmourQuality(50, false) : 0;
        float hours = option.Kind switch
        {
            CraftKind.Item => record?.GetInt("production time", 4) ?? 4,
            CraftKind.Crossbow or CraftKind.Robotics => (record?.GetFloat("craft time hrs", option.Kind == CraftKind.Crossbow ? 20 : 10) ?? 10) / p,
            CraftKind.Weapon => Math.Clamp(
                option.Level / 100f * (option.Level / 100f) * 100 * ((record?.GetInt("value") ?? 0) / 300f) * 2 / p, 2, 32),
            CraftKind.Armour => ArmourHours(record, quality / 100f),
            _ => 4,
        };
        float cost = option.Kind == CraftKind.Weapon ? record?.GetInt("material cost", 4) ?? 4 : 1;
        var inputs = new List<CraftInput>();
        var ingredients = record?.GetReferences("ingredients") ?? [];
        if (option.Kind == CraftKind.Armour)
        {
            float coverage = TotalCoverage(record) * 0.01f;
            float fabrics = record?.GetFloat("fabrics amount", 0.2f) ?? 0.2f;
            foreach (var c in consumes)
                inputs.Add(new CraftInput(c.Item, c.Item == Fabrics && consumes.Count > 1 ? coverage * fabrics : coverage));
        }
        else if (!OverridesIngredients && ingredients.Count > 0)
            foreach (var i in ingredients) inputs.Add(new CraftInput(i.TargetStringId, i.Values.Value0 * 0.01f * cost));
        else
            foreach (var c in consumes) inputs.Add(new CraftInput(c.Item, c.Rate * cost));
        return new CraftRecipe(option, name, hours, inputs, quality);
    }

    /// <summary>
    /// An armour's <c>total coverage</c> (<b>Observed</b>, <c>FUN_14082fd80</c>): the sum of its <c>part coverage</c> v0, the chest
    /// (<c>101-gamedata.quack</c>) x 1.5 truncated; 100 when that is 0.
    /// </summary>
    public static int TotalCoverage(GameRecord? armour)
    {
        int total = 0;
        foreach (var part in armour?.GetReferences("part coverage") ?? [])
            total += part.TargetStringId == "101-gamedata.quack" ? (int)(part.Values.Value0 * 1.5) : part.Values.Value0;
        return total == 0 ? 100 : total;
    }

    /// <summary>
    /// Armour hours (<b>Observed</b>, the ARMOUR constructor <c>FUN_140898e90</c>): <c>total coverage</c> / 100 x lerp(q, 1, 5), at least 1; hats x
    /// 0.6, leggings x 0.45, shirts x 3, boots x 0.5; chain (material type 2) x 2. The constructor uses 3 h for armour without its crafting flag
    /// (untraced; Meitou always uses the formula).
    /// </summary>
    static float ArmourHours(GameRecord? armour, float q)
    {
        if (armour is null) return 3;
        float coverage = TotalCoverage(armour) * 0.01f;
        float hours = MathF.Max(1, coverage * (1 + 4 * q));
        hours *= armour.GetInt("slot", 5) switch { 3 => 0.6f, 6 => 0.45f, 8 => 3, 9 => 0.5f, _ => 1 };
        if (armour.GetInt("material type") == 2) hours *= 2;
        return hours;
    }
}