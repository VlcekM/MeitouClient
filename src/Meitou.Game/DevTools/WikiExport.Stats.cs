using System.Globalization;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Combat;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;

namespace Meitou.Game.DevTools;

/// <summary>
/// The third layer of <c>--wiki-export</c> (docs/agent-playtest.md, "The wiki"): item stats (weapons, crossbows, armour, other items with their
/// blueprints), the research tree, animals, and the biome groups with the squads they spawn. All of it is read from the player's own game data (mods
/// applied); nothing comes from outside the data files.
/// </summary>
static partial class WikiExport
{
    #region Helpers

    static void Put(SortedDictionary<string, object?> d, string key, object? value)
    {
        if (value is not null) d[key] = value;
    }

    static object? Round(float v) => MathF.Round(v * 1000) / 1000;

    /// <summary>
    /// A research that unlocks a record: its name and the repetition (0 = the first) that does.
    /// </summary>
    sealed record UnlockedBy(string Research, string ResearchId, int Repetition);

    // --- Research tree.
    #endregion

    #region Research

    static (List<Row> Rows, Dictionary<string, List<UnlockedBy>> Unlocks) ResearchRows(GameDatabase db, Func<string, string, string> id)
    {
        var rows = new List<Row>();
        var unlocks = new Dictionary<string, List<UnlockedBy>>(StringComparer.Ordinal);
        var leadsTo = new Dictionary<string, List<string>>(StringComparer.Ordinal);   // research id -> names of research that list it as a requirement
        var all = db.OfType(FcsRecordType.Research)
            .Where(r => r.Name.Length > 0)
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ThenBy(r => r.StringId, StringComparer.Ordinal)
            .ToList();
        foreach (var r in all)
        {
            foreach (var field in ResearchData.UnlockFields)
                foreach (var l in r.GetReferences(field))
                {
                    if (!unlocks.TryGetValue(l.TargetStringId, out var list)) unlocks[l.TargetStringId] = list = [];
                    list.Add(new UnlockedBy(r.Name, r.StringId, l.Values.Value0));
                }
            foreach (var req in r.GetReferences("requirements"))
            {
                if (!leadsTo.TryGetValue(req.TargetStringId, out var l)) leadsTo[req.TargetStringId] = l = [];
                l.Add(r.Name);
            }
        }
        foreach (var r in all)
        {
            var requires = new List<object?>();
            foreach (var req in r.GetReferences("requirements"))
            {
                var target = db.Find(req.TargetStringId);
                if (target is null) continue;
                requires.Add(new SortedDictionary<string, object?>
                {
                    ["id"] = req.TargetStringId,
                    ["name"] = target.Name,
                    ["kind"] = target.Type == FcsRecordType.Research ? "research" : target.Type.ToString().ToLowerInvariant(),
                    ["repeat"] = req.Values.Value0,
                });
            }
            var cost = new List<object?>();
            var books = new List<string>();
            foreach (var c in r.GetReferences("cost"))
            {
                if (c.Values.Value0 < 0 || db.Find(c.TargetStringId) is not { } item) continue;
                cost.Add(new SortedDictionary<string, object?> { ["item"] = item.Name, ["count"] = c.Values.Value0 });
                if (item.Name.Contains("book", StringComparison.OrdinalIgnoreCase)) books.Add(item.Name);
            }
            var unlockRows = new List<object?>();
            foreach (var field in ResearchData.UnlockFields)
                foreach (var l in r.GetReferences(field))
                {
                    if (db.Find(l.TargetStringId) is not { } target) continue;
                    unlockRows.Add(new SortedDictionary<string, object?>
                    {
                        ["kind"] = field.Replace("enable ", "", StringComparison.Ordinal),
                        ["name"] = target.Name,
                        ["repetition"] = l.Values.Value0,
                    });
                }
            var improves = Names(db, r.GetReferences("improve buildings").Select(l => l.TargetStringId));
            var data = new SortedDictionary<string, object?>
            {
                ["category"] = r.GetString("category"),
                ["level"] = r.GetInt("level"),
                ["hours"] = r.GetInt("time"),
                ["repeats"] = Math.Max(0, r.GetInt("repeats")),
                ["repeatMult"] = Round(r.GetFloat("repeat mult", 1)),
                ["levelUpgrade"] = r.GetBool("is level upgrade"),
                ["blueprintOnly"] = r.GetBool("blueprint only"),
                ["money"] = r.GetInt("money"),
                ["requires"] = requires,
                ["cost"] = cost,
                ["books"] = books.Distinct().ToList(),
                ["unlocks"] = unlockRows,
                ["leadsTo"] = leadsTo.GetValueOrDefault(r.StringId)?.Distinct().Take(20).ToList() ?? [],
                ["record"] = r.StringId,
            };
            if (!s_factsOnly && r.GetString("description") is { Length: > 0 } rdesc) data["description"] = Trim(rdesc, 300);
            if (improves.Count > 0)
            {
                data["improves"] = improves;
                if (r.GetFloat("production mult", 1) != 1) data["productionMult"] = Round(r.GetFloat("production mult", 1));
                if (r.GetInt("power increase") != 0) data["powerIncrease"] = r.GetInt("power increase");
                if (r.GetInt("power capacity increase") != 0) data["capacityIncrease"] = r.GetInt("power capacity increase");
            }
            string desc = s_factsOnly ? "" : r.GetString("description");
            string needs = cost.Count == 0 ? "no items" : string.Join(", ", cost.OfType<SortedDictionary<string, object?>>().Select(c => $"{c["count"]} {c["item"]}"));
            rows.Add(new Row(id(r.Name, r.StringId), r.Name, "research", null, null, null, null, "Observed",
                $"Research ({r.GetString("category")}), {r.GetInt("time")} base hours, costs {needs}." + (requires.Count > 0 ? " Needs " + string.Join(", ", requires.OfType<SortedDictionary<string, object?>>().Select(q => (string)q["name"]!)) + "." : "") + (desc.Length > 0 ? " " + Trim(desc, 240) : ""),
                r.StringId, data));
        }
        return (rows, unlocks);
    }

    #endregion

    #region Item names

    static string Trim(string text, int max)
    {
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= max ? text : text[..max].TrimEnd() + "...";
    }

    // --- Items.
    static string WeaponClassName(int category) => category switch
    {
        (int)WeaponCategory.Katanas => "Katana",
        (int)WeaponCategory.Sabres => "Sabre",
        (int)WeaponCategory.Blunt => "Blunt",
        (int)WeaponCategory.Heavy => "Heavy weapon",
        (int)WeaponCategory.Hackers => "Hacker",
        (int)WeaponCategory.Unarmed => "Unarmed",
        (int)WeaponCategory.Bow => "Bow",
        (int)WeaponCategory.Turret => "Turret",
        (int)WeaponCategory.Polearms => "Polearm",
        (int)WeaponCategory.AttackNull => "No class",
        _ => "Creature attack " + category,
    };

    // Armour slot numbers: not named in the data. Read from which records use each number (hats 3, body armour 5, legs 6, shirts 8, boots 9); the
    // base data uses no others.
    static string ArmourSlotName(int slot) => slot switch { 3 => "head", 5 => "body armour", 6 => "legs", 8 => "shirt",
        9 => "feet", _ => "slot " + slot };

    // ITEM `item function`: 3 food, 15 special food, 13 book, 14 money, 16 ammunition, 9 tool (the game's tooltip and tool rules), 11 blueprint (save
    // format), 17 severed limb; the rest named from the items that use the number in the base data.
    static (string Name, string Basis) ItemCategory(int function) => function switch
    {
        3 => ("food", "the game's tooltip rules"),
        15 => ("special food", "the game's tooltip rules"),
        13 => ("book or letter", "the game's tooltip rules"),
        14 => ("money", "the game's item rules"),
        16 => ("ammunition", "the game's tooltip rules"),
        9 => ("tool", "the game's lock-breaking rule"),
        11 => ("blueprint", "the save format"),
        17 => ("severed limb", "the items that use it"),
        1 => ("first aid kit", "the items that use it"),
        2 => ("splint kit", "the items that use it"),
        7 => ("resource", "the items that use it"),
        8 => ("drug or drink", "the items that use it"),
        0 => ("other", "no function"),
        _ => ("function " + function, "not named in the data"),
    };

    #endregion

    #region Crafting

    /// <summary>
    /// A building that makes an item: for a weapon the models it makes it in, and what one item takes.
    /// </summary>
    sealed record MadeAt(GameRecord Building, string Kind, List<(string Model, int Level)> Models, IReadOnlyList<CraftInput> Inputs);

    /// <summary>
    /// Every crafting bench and refinery that makes each item (docs/game/buildings-production.md "Crafting", "Production"): a bench's recipe as <see
    /// cref="CraftBench.Recipe"/> draws it, a refinery's <c>consumes</c> at v1 / 100 per item of its first <c>produces</c>. Farms, mines and research
    /// benches are left out.
    /// </summary>
    static Dictionary<string, List<MadeAt>> MadeAtIndex(GameDatabase db)
    {
        var constants = GameConstants.FromDatabase(db);
        var index = new Dictionary<string, List<MadeAt>>(StringComparer.Ordinal);
        List<MadeAt> Of(string item) => index.TryGetValue(item, out var l) ? l : index[item] = [];
        foreach (var building in db.OfType(FcsRecordType.Building).Where(b => b.Name.Length > 0).OrderBy(b => b.StringId, StringComparer.Ordinal))
        {
            if (MachineData.From(db, building) is not { } machine) continue;
            if (machine.Craft is { } bench)
            {
                foreach (var group in bench.Options.GroupBy(o => o.Item))
                {
                    var recipe = bench.Recipe(db, constants, group.First(), machine.Inputs);
                    var models = group.Where(o => o.Model is not null)
                        .Select(o => (db.Find(o.Model!)?.Name ?? o.Model!, o.Level))
                        .OrderBy(m => m.Level)
                        .ToList();
                    Of(group.Key).Add(new MadeAt(building, "craft", models, recipe.Inputs));
                }
            }
            else if (machine.Function == MachineData.RefineryFunction && machine.Output is { } output && machine.Inputs.Count > 0)
                Of(output.Item).Add(new MadeAt(building, "refine", [], [.. machine.Inputs.Select(i => new CraftInput(i.Item, i.Rate))]));
        }
        return index;
    }

    /// <summary>
    /// The <c>madeAt</c> rows of an item: building, its record and the research that unlocks it, the weapon models, and each input per item made.
    /// </summary>
    static List<object?> MadeAtRows(GameDatabase db, List<MadeAt> made, Dictionary<string, List<UnlockedBy>> unlocks) =>
        [.. made.Select(m => (object?)new SortedDictionary<string, object?>
        {
            ["building"] = m.Building.Name,
            ["buildingId"] = m.Building.StringId,
            ["buildingResearch"] = unlocks.GetValueOrDefault(m.Building.StringId)?.Select(u => u.Research).Distinct().ToList() ?? [],
            ["kind"] = m.Kind,
            ["models"] = m.Models.Select(x => (object?)new SortedDictionary<string, object?> { ["model"] = x.Model, ["level"] = x.Level }).ToList(),
            ["inputs"] = m.Inputs.Select(i => (object?)new SortedDictionary<string, object?>
            {
                ["item"] = db.Find(i.Item)?.Name ?? i.Item,
                ["itemId"] = i.Item,
                ["perItem"] = Round(i.PerItem),
            }).ToList(),
        })];

    #endregion

    #region Items

    static List<Row> Items(GameDatabase db, Func<string, string, string> id, Dictionary<string, List<UnlockedBy>> unlocks)
    {
        var madeAt = MadeAtIndex(db);
        // Who makes each weapon type: the manufacturers' `weapon types`, their price/weight/damage modifiers and their `weapon models` (grades with
        // their level numbers).
        var makers = new Dictionary<string, List<GameRecord>>(StringComparer.Ordinal);
        foreach (var m in db.OfType(FcsRecordType.WeaponManufacturer))
            foreach (var t in m.GetReferences("weapon types"))
            {
                if (!makers.TryGetValue(t.TargetStringId, out var l)) makers[t.TargetStringId] = l = [];
                l.Add(m);
            }
        object? Blueprint(string recordId)
        {
            if (!unlocks.TryGetValue(recordId, out var l)) return null;
            return l.GroupBy(u => u.ResearchId)
                .Select(g => (object?)new SortedDictionary<string, object?> { ["research"] = g.First().Research, ["researchId"] = g.Key,
                    ["repetition"] = g.Min(u => u.Repetition) })
                .ToList();
        }

        var items = new List<Row>();
        var records = db.OfType(FcsRecordType.Weapon)
            .Concat(db.OfType(FcsRecordType.Crossbow))
            .Concat(db.OfType(FcsRecordType.Armour))
            .Concat(db.OfType(FcsRecordType.Item))
            .Concat(db.OfType(FcsRecordType.Container)).Concat(db.OfType(FcsRecordType.LimbReplacement))
            .Where(r => r.Name.Length > 0).OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.StringId, StringComparer.Ordinal);
        foreach (var record in records)
        {
            var data = new SortedDictionary<string, object?> { ["value"] = Num(record, "value"), ["weightKg"] = Num(record, "weight kg"),
                ["record"] = record.StringId };
            string kind, summary;
            string desc = s_factsOnly ? "" : record.GetString("description");
            if (record.Type == FcsRecordType.Weapon)
            {
                kind = "weapon";
                var w = WeaponData.From(record);
                string cls = WeaponClassName(w.SkillCategory);
                data["class"] = cls;
                data["skillCategory"] = w.SkillCategory;
                data["cutMult"] = Round(w.CutDamageMultiplier);
                data["bluntMult"] = Round(w.BluntDamageMultiplier);
                data["pierceMult"] = Round(w.PierceDamageMultiplier);
                data["armourPenetration"] = Round(w.ArmourPenetration);
                data["bleedMult"] = Round(w.BleedMult);
                data["attackMod"] = w.AttackMod;
                data["defenceMod"] = w.DefenceMod;
                data["indoorsMod"] = w.IndoorsMod;
                data["length"] = w.Length;
                data["canBlock"] = w.CanBlock;
                data["humanMult"] = Round(w.HumanDamageMult);
                data["animalMult"] = Round(w.AnimalDamageMult);
                data["robotMult"] = Round(w.RobotDamageMult);
                data["materialCost"] = record.GetInt("material cost");
                if (w.MinCutDamageMult != 1) data["minCutMult"] = Round(w.MinCutDamageMult);
                if (w.RaceDamage.Count > 0)
                    data["raceDamagePct"] = w.RaceDamage.Select(kv => (object?)new SortedDictionary<string, object?> { ["race"] = db.Find(kv.Key)?.Name ?? kv.Key, ["pct"] = kv.Value }).ToList();
                var made = makers.GetValueOrDefault(record.StringId) ?? [];
                var grades = new SortedDictionary<string, int>(StringComparer.Ordinal);
                var makerRows = new List<object?>();
                foreach (var m in made)
                {
                    var models = m.GetReferences("weapon models")
                        .Where(x => db.Find(x.TargetStringId) is { Name.Length: > 0 })
                        .Select(x => (Name: db.Find(x.TargetStringId)!.Name, Level: x.Values.Value0))
                        .ToList();
                    foreach (var (n, level) in models) grades[n] = level;
                    makerRows.Add(new SortedDictionary<string, object?>
                    {
                        ["maker"] = m.Name,
                        ["cutMod"] = Round(m.GetFloat("cut damage mod", 1)),
                        ["bluntMod"] = Round(m.GetFloat("blunt damage mod", 1)),
                        ["priceMod"] = Round(m.GetFloat("price mod", 1)),
                        ["weightMod"] = Round(m.GetFloat("weight mod", 1)),
                        ["models"] = models.OrderBy(x => x.Level).Select(x => x.Name).ToList(),
                    });
                }
                data["makers"] = makerRows;
                data["grades"] = grades.OrderBy(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => (object?)new SortedDictionary<string, object?> { ["model"] = kv.Key, ["level"] = kv.Value })
                    .ToList();
                Put(data, "blueprint", Blueprint(record.StringId));
                summary = $"{cls} weapon, cut x{Round(w.CutDamageMultiplier)}, blunt x{Round(w.BluntDamageMultiplier)}, {(w.CanBlock ? "can block" : "cannot block")}, value {record.GetInt("value")}, weight {Num(record, "weight kg") ?? 0} kg.";
            }
            else if (record.Type == FcsRecordType.Crossbow)
            {
                kind = "crossbow";
                string[] keys =
                [
                    "pierce damage min 0", "pierce damage max 0", "pierce damage min 1", "pierce damage max 1", "range", "range 1", "reload time min",
                    "reload time max", "reload time min 1", "reload time max 1", "accuracy deviation at 0 skill", "accuracy deviation at 0 skill 1",
                    "shot speed", "shot speed 1", "bleed mult", "accuracy perfect skill", "num shots", "value 1", "human damage mult",
                    "animal damage mult", "robot damage mult", "material cost", "craft time hrs",
                ];
                foreach (var key in keys)
                    Put(data, key.Replace(' ', '_'), Num(record, key));
                data["ammo"] = Names(db, record.GetReferences("ammo").Select(r => r.TargetStringId));
                data["ingredients"] = record.GetReferences("ingredients")
                    .Where(r => db.Find(r.TargetStringId) is not null)
                    .Select(r => (object?)new SortedDictionary<string, object?> { ["item"] = db.Find(r.TargetStringId)!.Name,
                        ["count"] = r.Values.Value0 })
                    .ToList();
                Put(data, "blueprint", Blueprint(record.StringId));
                summary = $"Crossbow, damage {Num(record, "pierce damage min 0")} to {Num(record, "pierce damage max 0")} at the lowest grade ({Num(record, "pierce damage min 1")} to {Num(record, "pierce damage max 1")} at the highest), range {Num(record, "range")}.";
            }
            else if (record.Type == FcsRecordType.Armour)
            {
                kind = "armour";
                var a = ArmourData.From(record);
                data["class"] = a.Class.ToString();
                data["className"] = record.GetString("class name");
                data["material"] = a.Material.ToString();
                data["slot"] = a.Slot;
                data["slotName"] = ArmourSlotName(a.Slot);
                data["levelBonus"] = a.LevelBonus;
                data["cutDefBonus"] = Round(a.CutDefBonus);
                data["bluntDefBonus"] = Round(a.BluntDefBonus);
                data["pierceDefMult"] = Round(a.PierceDefMult);
                data["coverage"] = a.Coverage.Select(c => (object?)new SortedDictionary<string, object?> { ["part"] = db.Find(c.PartStringId)?.Name
                    ?? c.PartStringId, ["pct"] = c.Percent }).ToList();
                var mods = new SortedDictionary<string, object?>();
                void Mult(string key, float v) { if (v != 1) mods[key] = Round(v); }
                Mult("dodge", a.DodgeMult);
                Mult("dexterity", a.DexterityMult);
                Mult("damageOutput", a.DamageOutputMult); Mult("athletics", a.AthleticsMult); Mult("combatSpeed", a.CombatSpeedMult);
                Mult("stealth", a.StealthMult);
                Mult("rangedSkill", a.RangedSkillMult);
                Mult("assassination", a.AssassinationMult); Mult("fistInjury", a.FistInjuryMult);
                if (a.UnarmedBonus != 0) mods["unarmedBonus"] = a.UnarmedBonus;
                if (a.PerceptionBonus != 0) mods["perceptionBonus"] = a.PerceptionBonus;
                if (a.CombatAttackBonus != 0) mods["combatAttackBonus"] = a.CombatAttackBonus;
                if (a.CombatDefenceBonus != 0) mods["combatDefenceBonus"] = a.CombatDefenceBonus;
                if (a.CombatSkillBonus != 0) mods["combatSkillBonus"] = a.CombatSkillBonus;
                if (a.CutIntoStun != 0) mods["cutIntoStun"] = Round(a.CutIntoStun);
                if (a.WeatherProtectionAmount != 0) mods["weatherProtection"] = Round(a.WeatherProtectionAmount);
                data["modifiers"] = mods;
                Put(data, "blueprint", Blueprint(record.StringId));
                summary = $"{a.Class} {a.Material} armour ({ArmourSlotName(a.Slot)}), cut +{Round(a.CutDefBonus)}, blunt +{Round(a.BluntDefBonus)}, value {record.GetInt("value")}, weight {Num(record, "weight kg") ?? 0} kg.";
            }
            else if (record.Type is FcsRecordType.Container or FcsRecordType.LimbReplacement)
            {
                kind = record.Type == FcsRecordType.Container ? "backpack" : "limb";
                Put(data, "blueprint", Blueprint(record.StringId));
                summary = $"{(kind == "limb" ? "Robotic limb" : "Backpack")}, value {record.GetInt("value")}, weight {Num(record, "weight kg") ?? 0} kg.";
            }
            else
            {
                kind = "item";
                int function = record.GetInt("item function");
                var (category, basis) = ItemCategory(function);
                data["category"] = category;
                data["categoryBasis"] = basis;
                data["itemFunction"] = function;
                data["slot"] = record.GetInt("slot");
                data["stackable"] = record.GetInt("stackable");
                if (record.GetFloat("charges") > 0) data["charges"] = Round(record.GetFloat("charges"));
                data["artifact"] = record.GetBool("artifact");
                data["tradeItem"] = record.GetBool("trade item");
                var ingredients = record.GetReferences("ingredients")
                    .Where(r => db.Find(r.TargetStringId) is not null)
                    .Select(r => (object?)new SortedDictionary<string, object?> { ["item"] = db.Find(r.TargetStringId)!.Name,
                        ["count"] = r.Values.Value0 })
                    .ToList();
                if (ingredients.Count > 0) data["ingredients"] = ingredients;
                Put(data, "blueprint", Blueprint(record.StringId));
                summary = $"Item ({category}), value {record.GetInt("value")}, weight {Num(record, "weight kg") ?? 0} kg.";
            }
            if (desc.Length > 0) data["description"] = Trim(desc, 300);
            if (madeAt.TryGetValue(record.StringId, out var benches)) data["madeAt"] = MadeAtRows(db, benches, unlocks);
            items.Add(new Row(id(record.Name, record.StringId), record.Name, kind, null, null, null, null, "Observed",
                summary, record.StringId, data));
        }
        return items;
    }

    // --- Biome groups and what they spawn.
    #endregion

    #region Spawns

    sealed record SpawnIndex(List<Row> Rows, Dictionary<string, List<(string Biome, double Share)>> BiomesOf,
        Dictionary<string, List<string>> NestsOf, Dictionary<string, List<string>> TownsOf, BiomeMap Map);

    static SpawnIndex Spawns(GameDatabase db, GameInstall install, List<PlacedTown> placed, Dictionary<string, SquadTemplate> templates,
        Func<string, string, string> id)
    {
        WeatherAreas? areas = null;
        try { areas = WeatherAreas.Load(install); } catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException) { Console.Error.WriteLine("wiki-export: no areas map, biomes unplaced: " + ex.Message); }
        var map = BiomeMap.Create(db, areas);
        var cellsOf = new Dictionary<int, List<string>>();
        for (int cz = 0; cz < WeatherAreas.Cells; cz++)
            for (int cx = 0; cx < WeatherAreas.Cells; cx++)
                if (map.GroupOf(cx, cz) is var g and >= 0)
                {
                    if (!cellsOf.TryGetValue(g, out var l)) cellsOf[g] = l = [];
                    l.Add($"{cx}-{cz}");
                }
        var townsInCell = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var pt in placed)
        {
            var (cx, cz) = WeatherAreas.CellOf(pt.Placement.Position.X, pt.Placement.Position.Z);
            string key = $"{cx}-{cz}";
            if (!townsInCell.TryGetValue(key, out var l)) townsInCell[key] = l = [];
            l.Add(pt.Town.Name);
        }

        // Nests: every TOWN record of type Nest, with the squads that roam around it.
        var nestSquads = new Dictionary<string, List<string>>(StringComparer.Ordinal);   // nest record -> squad ids
        var nestsOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);      // squad id -> nest names
        foreach (var r in db.OfType(FcsRecordType.Town))
        {
            var t = TownData.From(r);
            if (t.Type != TownType.Nest || t.Name.Length == 0) continue;
            nestSquads[r.StringId] = [.. t.RoamingSquads.Select(l => l.Id)];
            foreach (var l in t.RoamingSquads)
            {
                if (!nestsOf.TryGetValue(l.Id, out var n)) nestsOf[l.Id] = n = [];
                if (!n.Contains(t.Name)) n.Add(t.Name);
            }
        }
        var townsOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var pt in placed)
            foreach (var l in pt.Town.Residents.Concat(pt.Town.BarSquads).Concat(pt.Town.RoamingSquads))
            {
                if (!townsOf.TryGetValue(l.Id, out var n)) townsOf[l.Id] = n = [];
                if (!n.Contains(pt.Town.Name)) n.Add(pt.Town.Name);
            }

        object? SquadRow(RecordLink link, double share)
        {
            var rec = db.Find(link.Id);
            if (rec is null || rec.Name.Length == 0) return null;
            templates.TryGetValue(link.Id, out var t);
            string faction = t is null ? "" : FactionNameOf(db, t.Faction);
            string leader = t?.Leader is { } ll ? db.Find(ll.Id)?.Name ?? "" : "";
            if (faction.Length == 0 && t?.Leader is { } l2 && db.Find(l2.Id) is { } lc && lc.GetReferences("faction") is { Count: > 0 } lf)
                faction = FactionNameOf(db, lf[0].TargetStringId);
            var members = t is null ? [] : Names(db, t.Squad.Concat(t.Squad2).Concat(t.ChooseFrom).Select(x => x.Id)).Take(8).ToList();
            var animals = t is null ? [] : Names(db, t.Animals.Concat(t.Animals2).Select(x => x.Id));
            var states = t is null ? [] : Names(db, t.WorldState.Select(x => x.Id));
            var d = new SortedDictionary<string, object?>
            {
                ["squad"] = rec.Name,
                ["record"] = link.Id,
                ["sharePct"] = Math.Round(share, 1),
                ["faction"] = faction,
                ["leader"] = leader,
                ["members"] = members,
                ["animals"] = animals,
                ["worldState"] = states,
            };
            return d;
        }

        var rows = new List<Row>();
        var biomesOf = new Dictionary<string, List<(string Biome, double Share)>>(StringComparer.Ordinal);
        for (int g = 0; g < map.Groups.Count; g++)
        {
            var group = map.Groups[g];
            var cells = cellsOf.GetValueOrDefault(g) ?? [];
            double total = group.HomelessSpawns.Sum(l => (double)Math.Max(1, l.V0));
            var squadRows = new List<object?>();
            foreach (var l in group.HomelessSpawns)
            {
                double share = Math.Max(1, l.V0) / Math.Max(1, total) * 100;
                if (SquadRow(l, share) is { } row) squadRows.Add(row);
                if (db.Find(l.Id) is { } sq)
                {
                    if (!biomesOf.TryGetValue(l.Id, out var list)) biomesOf[l.Id] = list = [];
                    list.Add((group.Name, Math.Round(share, 1)));
                }
            }
            double nestTotal = group.Nests.Sum(l => (double)(l.V0 == 0 ? 100 : l.V0));
            var nestRows = new List<object?>();
            foreach (var l in group.Nests)
            {
                if (db.Find(l.Id) is not { } nest || nest.Name.Length == 0) continue;
                nestRows.Add(new SortedDictionary<string, object?>
                {
                    ["nest"] = nest.Name,
                    ["weightPct"] = Math.Round((l.V0 == 0 ? 100 : l.V0) / Math.Max(1, nestTotal) * 100, 1),
                    ["squads"] = Names(db, nestSquads.GetValueOrDefault(l.Id) ?? []),
                });
            }
            var towns = cells.SelectMany(c => townsInCell.GetValueOrDefault(c) ?? []).Distinct().ToList();
            var data = new SortedDictionary<string, object?>
            {
                ["cellCount"] = cells.Count,
                ["cells"] = cells,
                ["homelessAmount"] = Round(group.HomelessSpawnAmount),
                ["numNests"] = group.NumNests,
                ["nestsAtFixedMarkersOnly"] = group.NestsAtFixedMarkersOnly,
                ["squads"] = squadRows,
                ["nests"] = nestRows,
                ["towns"] = towns.Take(40).ToList(),
                ["townCount"] = towns.Count,
                ["record"] = group.Id,
            };
            if (cells.Count == 0 && group.HomelessSpawns.Count == 0 && group.Nests.Count == 0) continue;
            string where = cells.Count == 0 ? "No zone cell is painted with this group in the areas map." : $"Covers {cells.Count} zone cell(s) and {towns.Count} placed town(s).";
            rows.Add(new Row(id(group.Name + " biome", group.Id), group.Name, "biome", null, null, null, null, "Observed",
                $"Biome group {group.Name}: {where} Homeless squads per loaded sector {Round(group.HomelessSpawnAmount)} (times the population setting); {squadRows.Count} squad template(s) can spawn, {group.NumNests} nest(s).", group.Id, data));
        }
        return new SpawnIndex(rows, biomesOf, nestsOf, townsOf, map);
    }

    // --- Animals.
    #endregion

    #region Animals

    static List<Row> Animals(GameDatabase db, Func<string, string, string> id, Dictionary<string, SquadTemplate> templates, SpawnIndex spawn)
    {
        var withAnimal = new Dictionary<string, List<(SquadTemplate T, RecordLink L)>>(StringComparer.Ordinal);
        foreach (var t in templates.Values)
            foreach (var l in t.Animals.Concat(t.Animals2))
            {
                if (!withAnimal.TryGetValue(l.Id, out var list)) withAnimal[l.Id] = list = [];
                list.Add((t, l));
            }
        var rows = new List<Row>();
        foreach (var record in db.OfType(FcsRecordType.AnimalCharacter).Where(r => r.Name.Length > 0).OrderBy(r => r.Name, StringComparer.Ordinal)
                .ThenBy(r => r.StringId, StringComparer.Ordinal))
        {
            var race = record.GetReferences("race") is { Count: > 0 } rl ? db.Find(rl[0].TargetStringId) : null;
            var data = new SortedDictionary<string, object?> { ["record"] = record.StringId };
            if (race is not null)
            {
                var r = new SortedDictionary<string, object?>
                {
                    ["name"] = race.Name,
                    ["walkSpeed"] = Round(race.GetFloat("walk speed")),
                    ["swimSpeedMult"] = Round(race.GetFloat("swim speed mult", 1)),
                    ["hungerRate"] = Round(race.GetFloat("hunger rate")),
                    ["maxBlood"] = Round(race.GetFloat("max blood")),
                    ["minBlood"] = Round(race.GetFloat("min blood")),
                    ["bleedRate"] = Round(race.GetFloat("bleed rate", 1)),
                    ["healRate"] = Round(race.GetFloat("heal rate", 1)),
                    ["strengthMult"] = Round(race.GetFloat("strength", 1)),
                    ["dexterityMult"] = Round(race.GetFloat("dexterity", 1)),
                    ["extraAttackSlots"] = race.GetInt("extra attack slots"),
                    ["carriable"] = race.GetBool("carriable"),
                    ["cantEnterBuildings"] = race.GetBool("cant enter buildings"),
                    ["swims"] = race.GetBool("swims"),
                    ["selfHealing"] = race.GetBool("self healing"),
                    ["isRobot"] = race.GetBool("is robot"),
                    ["gigantic"] = race.GetBool("gigantic"),
                    ["buyValue"] = race.GetInt("buy value"),
                    ["anatomy"] = race.GetReferences("combat anatomy")
                        .Where(x => db.Find(x.TargetStringId) is not null)
                        .Select(x => (object?)new SortedDictionary<string, object?> { ["part"] = db.Find(x.TargetStringId)!.Name, ["hp"] = x.Values.Value0 })
                        .ToList(),
                };
                data["race"] = r;
            }
            data["combatStats"] = record.GetInt("combat stats");
            data["animalStrength"] = record.GetInt("animal strength");
            Put(data, "strength", record.GetInt("strength") is var s and > 0 ? s : null);
            Put(data, "unarmedStats", record.GetInt("unarmed stats") is var us and > 0 ? us : null);
            Put(data, "stealthStats", record.GetInt("stealth stats") is var st and > 0 ? st : null);
            Put(data, "rangedStats", record.GetInt("ranged stats") is var rs and > 0 ? rs : null);
            data["hpMult"] = Round(record.GetFloat("HP mult", 1));
            data["lifespanDays"] = Round(record.GetFloat("lifespan"));
            data["scaleMin"] = Round(record.GetFloat("scale min"));
            data["scaleMax"] = Round(record.GetFloat("scale max"));
            data["femaleChancePct"] = record.GetInt("female chance");
            data["inventory"] = $"{record.GetInt("inventory w")}x{record.GetInt("inventory h")}";
            data["smellBlood"] = Round(record.GetFloat("smell blood"));
            data["smellEggs"] = Round(record.GetFloat("smell eggs"));
            data["turnRate"] = Round(record.GetFloat("turn rate"));
            data["unique"] = record.GetBool("unique");
            if (record.GetReferences("weapon") is { Count: > 0 } wl && db.Find(wl[0].TargetStringId) is { Type: FcsRecordType.Weapon } wr)
            {
                var w = WeaponData.From(wr);
                data["weapon"] = new SortedDictionary<string, object?>
                {
                    ["name"] = wr.Name,
                    ["class"] = WeaponClassName(w.SkillCategory),
                    ["cutMult"] = Round(w.CutDamageMultiplier),
                    ["bluntMult"] = Round(w.BluntDamageMultiplier),
                    ["pierceMult"] = Round(w.PierceDamageMultiplier),
                    ["bleedMult"] = Round(w.BleedMult),
                    ["length"] = w.Length,
                    ["humanMult"] = Round(w.HumanDamageMult),
                    ["animalMult"] = Round(w.AnimalDamageMult),
                };
            }
            if (record.GetReferences("weapon level") is { Count: > 0 } lv) data["weaponLevel"] = FactionNameOf(db, lv[0].TargetStringId);
            data["deathItems"] = record.GetReferences("death items").Where(x => x.Values.Value0 > 0 && db.Find(x.TargetStringId) is not null)
                .Select(x => (object?)new SortedDictionary<string, object?> { ["item"] = db.Find(x.TargetStringId)!.Name,
                    ["count"] = x.Values.Value0 }).ToList();
            int chance = record.GetInt("bounty chance"), amount = record.GetInt("bounty amount");
            if (chance > 0 && amount > 0)
                data["bounty"] = new SortedDictionary<string, object?> { ["chancePct"] = chance, ["amount"] = amount,
                    ["amountMax"] = amount + Math.Max(0, record.GetInt("bounty amount fuzz")) };

            var uses = withAnimal.GetValueOrDefault(record.StringId) ?? [];
            var squadRows = new List<object?>();
            var biomes = new List<object?>();
            var nests = new SortedSet<string>(StringComparer.Ordinal);
            var towns = new SortedSet<string>(StringComparer.Ordinal);
            var seenBiome = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (t, l) in uses.OrderBy(u => u.T.Name, StringComparer.Ordinal))
            {
                squadRows.Add(new SortedDictionary<string, object?> { ["squad"] = t.Name, ["count"] = l.V0 == l.V1 || l.V1 == 0 ? l.V0.ToString(CultureInfo.InvariantCulture) : $"{l.V0}-{l.V1}", ["record"] = t.Id });
                foreach (var (biome, share) in spawn.BiomesOf.GetValueOrDefault(t.Id) ?? [])
                    if (seenBiome.Add(biome + "|" + t.Name))
                        biomes.Add(new SortedDictionary<string, object?> { ["biome"] = biome, ["sharePct"] = share, ["squad"] = t.Name });
                foreach (var n in spawn.NestsOf.GetValueOrDefault(t.Id) ?? []) nests.Add(n);
                foreach (var n in spawn.TownsOf.GetValueOrDefault(t.Id) ?? []) towns.Add(n);
            }
            data["squads"] = squadRows.Take(30).ToList();
            data["spawnsIn"] = new SortedDictionary<string, object?>
            {
                ["biomes"] = biomes.Take(40).ToList(),
                ["nests"] = nests.Take(20).ToList(),
                ["towns"] = towns.Take(20).ToList(),
            };
            string faction = record.GetReferences("faction") is { Count: > 0 } fl ? FactionNameOf(db, fl[0].TargetStringId) : "";
            if (faction.Length == 0) faction = uses.Select(u => FactionNameOf(db, u.T.Faction)).FirstOrDefault(n => n.Length > 0) ?? "";
            string where = biomes.Count + nests.Count + towns.Count == 0 ? "No squad that spawns on its own (biome, nest or town list) uses it." : $"Spawns through {squadRows.Count} squad template(s): {biomes.Count} biome entries, {nests.Count} nest(s), {towns.Count} town(s).";
            rows.Add(new Row(id(record.Name, record.StringId), record.Name, "animal-type", faction.Length == 0 ? null : faction, null, null, null,
                "Observed",
                $"Animal{(race is null ? "" : ", race " + race.Name)}, combat stats {record.GetInt("combat stats")}, animal strength {record.GetInt("animal strength")}, {Round(record.GetFloat("HP mult", 1))}x health. {where}",
                record.StringId, data));
        }
        return rows;
    }
    #endregion

}