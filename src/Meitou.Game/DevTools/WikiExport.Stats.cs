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

    /// <summary>
    /// Sets <paramref name="key"/> to <paramref name="value"/> unless the value is null (the key is then left out).
    /// </summary>
    static void Put(SortedDictionary<string, object?> d, string key, object? value)
    {
        if (value is not null) d[key] = value;
    }

    /// <summary>
    /// <paramref name="v"/> rounded to three decimals, boxed for a data object.
    /// </summary>
    static object? Round(float v) => MathF.Round(v * 1000) / 1000;

    /// <summary>
    /// A research that unlocks a record: its name and the repetition (0 = the first) that does.
    /// </summary>
    sealed record UnlockedBy(string Research, string ResearchId, int Repetition);

    #endregion

    #region Research

    /// <summary>
    /// The research tree: one row per named research (requirements, cost, unlocks, what it leads to), and record id to the research that unlock it.
    /// </summary>
    static (List<Row> Rows, Dictionary<string, List<UnlockedBy>> Unlocks) ResearchRows(GameDatabase db, Func<string, string, string> id)
    {
        var rows = new List<Row>();
        var unlocks = new Dictionary<string, List<UnlockedBy>>(StringComparer.Ordinal);
        // research id -> names of research that list it as a requirement
        var leadsTo = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var all = db.OfType(FcsRecordType.Research)
            .Where(r => r.Name.Length > 0)
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ThenBy(r => r.StringId, StringComparer.Ordinal)
            .ToList();
        IndexResearch(all, unlocks, leadsTo);
        foreach (var r in all) rows.Add(ResearchRow(db, id, r, leadsTo));
        return (rows, unlocks);
    }

    /// <summary>
    /// Fills <paramref name="unlocks"/> (record id to the research that unlock it) and <paramref name="leadsTo"/> (research id to the research
    /// that require it).
    /// </summary>
    static void IndexResearch(List<GameRecord> all, Dictionary<string, List<UnlockedBy>> unlocks, Dictionary<string, List<string>> leadsTo)
    {
        foreach (var r in all)
        {
            foreach (var field in ResearchData.UnlockFields)
            {
                foreach (var l in r.GetReferences(field))
                {
                    if (!unlocks.TryGetValue(l.TargetStringId, out var list)) unlocks[l.TargetStringId] = list = [];
                    list.Add(new UnlockedBy(r.Name, r.StringId, l.Values.Value0));
                }
            }
            foreach (var req in r.GetReferences("requirements"))
            {
                if (!leadsTo.TryGetValue(req.TargetStringId, out var l)) leadsTo[req.TargetStringId] = l = [];
                l.Add(r.Name);
            }
        }
    }

    /// <summary>
    /// One research's row: its numbers, requirements, item cost, unlocks and the buildings it improves.
    /// </summary>
    static Row ResearchRow(GameDatabase db, Func<string, string, string> id, GameRecord r, Dictionary<string, List<string>> leadsTo)
    {
        var requires = ResearchRequires(db, r);
        var books = new List<string>();
        var cost = ResearchCost(db, r, books);
        var unlockRows = ResearchUnlocks(db, r);
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
        if (!s_factsOnly && r.GetString("description") is { Length: > 0 } rdesc)
            data["description"] = Trim(rdesc, 300);
        if (improves.Count > 0) PutImproves(r, data, improves);
        string desc = s_factsOnly ? "" : r.GetString("description");
        string needs = cost.Count == 0 ? "no items" : string.Join(", ", cost.OfType<SortedDictionary<string, object?>>()
            .Select(c => $"{c["count"]} {c["item"]}"));
        return new Row(id(r.Name, r.StringId), r.Name, "research", null, null, null, null, "Observed",
            $"Research ({r.GetString("category")}), {r.GetInt("time")} base hours, costs {needs}."
            + (requires.Count > 0 ? " Needs " + string.Join(", ", requires.OfType<SortedDictionary<string, object?>>()
            .Select(q => (string)q["name"]!)) + "." : "") + (desc.Length > 0 ? " " + Trim(desc, 240) : ""),
            r.StringId, data);
    }

    /// <summary>
    /// A research's requirements that name a record: id, name, kind and repetition.
    /// </summary>
    static List<object?> ResearchRequires(GameDatabase db, GameRecord r)
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
        return requires;
    }

    /// <summary>
    /// A research's item cost (item and count, negative counts skipped); items named like a book also go into <paramref name="books"/>.
    /// </summary>
    static List<object?> ResearchCost(GameDatabase db, GameRecord r, List<string> books)
    {
        var cost = new List<object?>();
        foreach (var c in r.GetReferences("cost"))
        {
            if (c.Values.Value0 < 0 || db.Find(c.TargetStringId) is not { } item) continue;
            cost.Add(new SortedDictionary<string, object?> { ["item"] = item.Name, ["count"] = c.Values.Value0 });
            if (item.Name.Contains("book", StringComparison.OrdinalIgnoreCase)) books.Add(item.Name);
        }
        return cost;
    }

    /// <summary>
    /// What a research unlocks, per unlock field (<see cref="ResearchData.UnlockFields"/>): kind, name and repetition.
    /// </summary>
    static List<object?> ResearchUnlocks(GameDatabase db, GameRecord r)
    {
        var unlockRows = new List<object?>();
        foreach (var field in ResearchData.UnlockFields)
        {
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
        }
        return unlockRows;
    }

    /// <summary>
    /// The buildings a research improves, with the production multiplier and power increases it gives them (each only when not neutral).
    /// </summary>
    static void PutImproves(GameRecord r, SortedDictionary<string, object?> data, List<string> improves)
    {
        data["improves"] = improves;
        if (r.GetFloat("production mult", 1) != 1)
            data["productionMult"] = Round(r.GetFloat("production mult", 1));
        if (r.GetInt("power increase") != 0) data["powerIncrease"] = r.GetInt("power increase");
        if (r.GetInt("power capacity increase") != 0)
            data["capacityIncrease"] = r.GetInt("power capacity increase");
    }

    #endregion

    #region Item names

    /// <summary>
    /// <paramref name="text"/> on one line, cut to <paramref name="max"/> characters with "..." when longer.
    /// </summary>
    static string Trim(string text, int max)
    {
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= max ? text : text[..max].TrimEnd() + "...";
    }

    /// <summary>
    /// The weapon class name of a skill category (<see cref="WeaponCategory"/>); other numbers are creature attacks.
    /// </summary>
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

    /// <summary>
    /// Armour slot numbers: not named in the data. Read from which records use each number (hats 3, body armour 5, legs 6, shirts 8, boots 9); the
    /// base data uses no others.
    /// </summary>
    static string ArmourSlotName(int slot) => slot switch { 3 => "head", 5 => "body armour", 6 => "legs", 8 => "shirt",
        9 => "feet", _ => "slot " + slot };

    /// <summary>
    /// ITEM `item function`: 3 food, 15 special food, 13 book, 14 money, 16 ammunition, 9 tool (the game's tooltip and tool rules), 11 blueprint
    /// (save format), 17 severed limb; the rest named from the items that use the number in the base data.
    /// </summary>
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

    /// <summary>
    /// Item stats: one row per named weapon, crossbow, armour, item, backpack and robotic limb, with blueprints and where it is made.
    /// </summary>
    static List<Row> Items(GameDatabase db, Func<string, string, string> id, Dictionary<string, List<UnlockedBy>> unlocks)
    {
        var pass = new ItemPass(db, id, unlocks);
        var items = new List<Row>();
        var records = db.OfType(FcsRecordType.Weapon)
            .Concat(db.OfType(FcsRecordType.Crossbow))
            .Concat(db.OfType(FcsRecordType.Armour))
            .Concat(db.OfType(FcsRecordType.Item))
            .Concat(db.OfType(FcsRecordType.Container)).Concat(db.OfType(FcsRecordType.LimbReplacement))
            .Where(r => r.Name.Length > 0).OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.StringId, StringComparer.Ordinal);
        foreach (var record in records) items.Add(pass.Row(record));
        return items;
    }

    /// <summary>
    /// The item pass of <see cref="Items"/>: the indexes every item row reads (benches that make it, weapon manufacturers, unlocking research) and
    /// the per-kind stat readers.
    /// </summary>
    sealed class ItemPass
    {
        /// <summary>
        /// The game data the rows are read from.
        /// </summary>
        readonly GameDatabase _db;

        /// <summary>
        /// The export's id allocator (name, record id to a unique entry id).
        /// </summary>
        readonly Func<string, string, string> _id;

        /// <summary>
        /// Record id to the research that unlock it (<see cref="ResearchRows"/>).
        /// </summary>
        readonly Dictionary<string, List<UnlockedBy>> _unlocks;

        /// <summary>
        /// Item id to the benches and refineries that make it (<see cref="MadeAtIndex"/>).
        /// </summary>
        readonly Dictionary<string, List<MadeAt>> _madeAt;

        /// <summary>
        /// Who makes each weapon type: the manufacturers' `weapon types`, their price/weight/damage modifiers and their `weapon models` (grades with
        /// their level numbers).
        /// </summary>
        readonly Dictionary<string, List<GameRecord>> _makers = new(StringComparer.Ordinal);

        /// <summary>
        /// Builds the made-at and manufacturer indexes.
        /// </summary>
        public ItemPass(GameDatabase db, Func<string, string, string> id, Dictionary<string, List<UnlockedBy>> unlocks)
        {
            _db = db;
            _id = id;
            _unlocks = unlocks;
            _madeAt = MadeAtIndex(db);
            foreach (var m in db.OfType(FcsRecordType.WeaponManufacturer))
            {
                foreach (var t in m.GetReferences("weapon types"))
                {
                    if (!_makers.TryGetValue(t.TargetStringId, out var l)) _makers[t.TargetStringId] = l = [];
                    l.Add(m);
                }
            }
        }

        /// <summary>
        /// One item's row: value and weight, the stats of its kind, its description and where it is made.
        /// </summary>
        public Row Row(GameRecord record)
        {
            var data = new SortedDictionary<string, object?> { ["value"] = Num(record, "value"), ["weightKg"] = Num(record, "weight kg"),
                ["record"] = record.StringId };
            string kind, summary;
            string desc = s_factsOnly ? "" : record.GetString("description");
            if (record.Type == FcsRecordType.Weapon)
            {
                kind = "weapon";
                summary = WeaponFields(record, data);
            }
            else if (record.Type == FcsRecordType.Crossbow)
            {
                kind = "crossbow";
                summary = CrossbowFields(record, data);
            }
            else if (record.Type == FcsRecordType.Armour)
            {
                kind = "armour";
                summary = ArmourFields(record, data);
            }
            else if (record.Type is FcsRecordType.Container or FcsRecordType.LimbReplacement)
            {
                kind = record.Type == FcsRecordType.Container ? "backpack" : "limb";
                Put(data, "blueprint", Blueprint(record.StringId));
                summary = $"{(kind == "limb" ? "Robotic limb" : "Backpack")}, value {record.GetInt("value")}, "
                    + $"weight {Num(record, "weight kg") ?? 0} kg.";
            }
            else
            {
                kind = "item";
                summary = OtherItemFields(record, data);
            }
            if (desc.Length > 0) data["description"] = Trim(desc, 300);
            if (_madeAt.TryGetValue(record.StringId, out var benches)) data["madeAt"] = MadeAtRows(_db, benches, _unlocks);
            return new Row(_id(record.Name, record.StringId), record.Name, kind, null, null, null, null, "Observed",
                summary, record.StringId, data);
        }

        /// <summary>
        /// The research that unlock a record as its blueprint (each research once, its first repetition), null when none does.
        /// </summary>
        object? Blueprint(string recordId)
        {
            if (!_unlocks.TryGetValue(recordId, out var l)) return null;
            return l.GroupBy(u => u.ResearchId)
                .Select(g => (object?)new SortedDictionary<string, object?> { ["research"] = g.First().Research, ["researchId"] = g.Key,
                    ["repetition"] = g.Min(u => u.Repetition) })
                .ToList();
        }

        /// <summary>
        /// A weapon's class, damage multipliers, modifiers, race damage, makers and grades; returns its summary.
        /// </summary>
        string WeaponFields(GameRecord record, SortedDictionary<string, object?> data)
        {
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
            {
                data["raceDamagePct"] = w.RaceDamage
                    .Select(kv => (object?)new SortedDictionary<string, object?> { ["race"] = _db.Find(kv.Key)?.Name
                    ?? kv.Key, ["pct"] = kv.Value })
                    .ToList();
            }
            WeaponMakers(record, data);
            Put(data, "blueprint", Blueprint(record.StringId));
            return $"{cls} weapon, cut x{Round(w.CutDamageMultiplier)}, blunt "
                + $"x{Round(w.BluntDamageMultiplier)}, {(w.CanBlock ? "can block" : "cannot block")}, value {record.GetInt("value")}, "
                + $"weight {Num(record, "weight kg") ?? 0} kg.";
        }

        /// <summary>
        /// The manufacturers of a weapon type with their modifiers and models, and every grade (model) by its level.
        /// </summary>
        void WeaponMakers(GameRecord record, SortedDictionary<string, object?> data)
        {
            var made = _makers.GetValueOrDefault(record.StringId) ?? [];
            var grades = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var makerRows = new List<object?>();
            foreach (var m in made)
            {
                var models = m.GetReferences("weapon models")
                    .Where(x => _db.Find(x.TargetStringId) is { Name.Length: > 0 })
                    .Select(x => (Name: _db.Find(x.TargetStringId)!.Name, Level: x.Values.Value0))
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
        }

        /// <summary>
        /// A crossbow's numbers at both grades, its ammo and ingredients; returns its summary.
        /// </summary>
        string CrossbowFields(GameRecord record, SortedDictionary<string, object?> data)
        {
            string[] keys =
            [
                "pierce damage min 0", "pierce damage max 0", "pierce damage min 1", "pierce damage max 1", "range", "range 1", "reload time min",
                "reload time max", "reload time min 1", "reload time max 1", "accuracy deviation at 0 skill", "accuracy deviation at 0 skill 1",
                "shot speed", "shot speed 1", "bleed mult", "accuracy perfect skill", "num shots", "value 1", "human damage mult",
                "animal damage mult", "robot damage mult", "material cost", "craft time hrs",
            ];
            foreach (var key in keys)
                Put(data, key.Replace(' ', '_'), Num(record, key));
            data["ammo"] = Names(_db, record.GetReferences("ammo").Select(r => r.TargetStringId));
            data["ingredients"] = record.GetReferences("ingredients")
                .Where(r => _db.Find(r.TargetStringId) is not null)
                .Select(r => (object?)new SortedDictionary<string, object?> { ["item"] = _db.Find(r.TargetStringId)!.Name,
                    ["count"] = r.Values.Value0 })
                .ToList();
            Put(data, "blueprint", Blueprint(record.StringId));
            return $"Crossbow, damage {Num(record, "pierce damage min 0")} to {Num(record, "pierce damage max 0")} at the lowest grade "
                + $"({Num(record, "pierce damage min 1")} to {Num(record, "pierce damage max 1")} at the highest), range {Num(record, "range")}.";
        }

        /// <summary>
        /// An armour's class, material, slot, defence bonuses, coverage and skill modifiers; returns its summary.
        /// </summary>
        string ArmourFields(GameRecord record, SortedDictionary<string, object?> data)
        {
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
            data["coverage"] = a.Coverage.Select(c => (object?)new SortedDictionary<string, object?> { ["part"] = _db.Find(c.PartStringId)?.Name
                ?? c.PartStringId, ["pct"] = c.Percent }).ToList();
            data["modifiers"] = ArmourModifiers(a);
            Put(data, "blueprint", Blueprint(record.StringId));
            return $"{a.Class} {a.Material} armour ({ArmourSlotName(a.Slot)}), cut +{Round(a.CutDefBonus)}, blunt +{Round(a.BluntDefBonus)}, "
                + $"value {record.GetInt("value")}, weight {Num(record, "weight kg") ?? 0} kg.";
        }

        /// <summary>
        /// An armour's skill multipliers (only those not 1) and bonuses (only those not 0).
        /// </summary>
        static SortedDictionary<string, object?> ArmourModifiers(ArmourData a)
        {
            var mods = new SortedDictionary<string, object?>();
            Mult(mods, "dodge", a.DodgeMult);
            Mult(mods, "dexterity", a.DexterityMult);
            Mult(mods, "damageOutput", a.DamageOutputMult);
            Mult(mods, "athletics", a.AthleticsMult);
            Mult(mods, "combatSpeed", a.CombatSpeedMult);
            Mult(mods, "stealth", a.StealthMult);
            Mult(mods, "rangedSkill", a.RangedSkillMult);
            Mult(mods, "assassination", a.AssassinationMult);
            Mult(mods, "fistInjury", a.FistInjuryMult);
            if (a.UnarmedBonus != 0) mods["unarmedBonus"] = a.UnarmedBonus;
            if (a.PerceptionBonus != 0) mods["perceptionBonus"] = a.PerceptionBonus;
            if (a.CombatAttackBonus != 0) mods["combatAttackBonus"] = a.CombatAttackBonus;
            if (a.CombatDefenceBonus != 0) mods["combatDefenceBonus"] = a.CombatDefenceBonus;
            if (a.CombatSkillBonus != 0) mods["combatSkillBonus"] = a.CombatSkillBonus;
            if (a.CutIntoStun != 0) mods["cutIntoStun"] = Round(a.CutIntoStun);
            if (a.WeatherProtectionAmount != 0) mods["weatherProtection"] = Round(a.WeatherProtectionAmount);
            return mods;
        }

        /// <summary>
        /// Sets <paramref name="key"/> to the rounded multiplier <paramref name="v"/> unless it is 1.
        /// </summary>
        static void Mult(SortedDictionary<string, object?> mods, string key, float v)
        {
            if (v != 1) mods[key] = Round(v);
        }

        /// <summary>
        /// Any other item's category (from its item function), slot, stacking, charges, flags and ingredients; returns its summary.
        /// </summary>
        string OtherItemFields(GameRecord record, SortedDictionary<string, object?> data)
        {
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
                .Where(r => _db.Find(r.TargetStringId) is not null)
                .Select(r => (object?)new SortedDictionary<string, object?> { ["item"] = _db.Find(r.TargetStringId)!.Name,
                    ["count"] = r.Values.Value0 })
                .ToList();
            if (ingredients.Count > 0) data["ingredients"] = ingredients;
            Put(data, "blueprint", Blueprint(record.StringId));
            return $"Item ({category}), value {record.GetInt("value")}, weight {Num(record, "weight kg") ?? 0} kg.";
        }
    }

    #endregion

    #region Spawns

    /// <summary>
    /// The biome rows and what the animal rows read of them: squad id to the biome groups that spawn it (with its share), to the nests it roams
    /// around and to the towns that list it, and the biome map itself.
    /// </summary>
    sealed record SpawnIndex(List<Row> Rows, Dictionary<string, List<(string Biome, double Share)>> BiomesOf,
        Dictionary<string, List<string>> NestsOf, Dictionary<string, List<string>> TownsOf, BiomeMap Map);

    /// <summary>
    /// Biome groups and what they spawn: one row per group with its zone cells, homeless squads, nests and the placed towns inside.
    /// </summary>
    static SpawnIndex Spawns(GameDatabase db, GameInstall install, List<PlacedTown> placed, Dictionary<string, SquadTemplate> templates,
        Func<string, string, string> id)
    {
        WeatherAreas? areas = null;
        try
        {
            areas = WeatherAreas.Load(install);
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("wiki-export: no areas map, biomes unplaced: " + ex.Message);
        }

        var map = BiomeMap.Create(db, areas);
        var pass = new SpawnPass(db, templates, map, placed);
        var rows = new List<Row>();
        for (int g = 0; g < map.Groups.Count; g++)
        {
            if (pass.BiomeRow(g, id) is { } row) rows.Add(row);
        }
        return new SpawnIndex(rows, pass.BiomesOf, pass.NestsOf, pass.TownsOf, map);
    }

    /// <summary>
    /// The spawn pass of <see cref="Spawns"/>: indexes which zone cells each biome group paints, the towns in each cell, the nests and the town
    /// lists, then makes one row per biome group.
    /// </summary>
    sealed class SpawnPass
    {
        /// <summary>
        /// The game data the rows are read from.
        /// </summary>
        readonly GameDatabase _db;

        /// <summary>
        /// Every squad template by id.
        /// </summary>
        readonly Dictionary<string, SquadTemplate> _templates;

        /// <summary>
        /// The biome groups and the group each zone cell is painted with.
        /// </summary>
        readonly BiomeMap _map;

        /// <summary>
        /// Biome group index to its zone cells ("x-z").
        /// </summary>
        readonly Dictionary<int, List<string>> _cellsOf = new();

        /// <summary>
        /// Zone cell ("x-z") to the names of the placed towns in it.
        /// </summary>
        readonly Dictionary<string, List<string>> _townsInCell = new(StringComparer.Ordinal);

        /// <summary>
        /// Nest record to squad ids: every TOWN record of type Nest, with the squads that roam around it.
        /// </summary>
        readonly Dictionary<string, List<string>> _nestSquads = new(StringComparer.Ordinal);

        /// <summary>
        /// Squad id to nest names.
        /// </summary>
        public readonly Dictionary<string, List<string>> NestsOf = new(StringComparer.Ordinal);

        /// <summary>
        /// Squad id to the placed towns that list it (residents, bar, roaming).
        /// </summary>
        public readonly Dictionary<string, List<string>> TownsOf = new(StringComparer.Ordinal);

        /// <summary>
        /// Squad id to the biome groups that spawn it homeless, with its share in percent (filled by <see cref="BiomeRow"/>).
        /// </summary>
        public readonly Dictionary<string, List<(string Biome, double Share)>> BiomesOf = new(StringComparer.Ordinal);

        /// <summary>
        /// Builds the cell, town, nest and town-list indexes.
        /// </summary>
        public SpawnPass(GameDatabase db, Dictionary<string, SquadTemplate> templates, BiomeMap map, List<PlacedTown> placed)
        {
            _db = db;
            _templates = templates;
            _map = map;
            IndexCells();
            IndexTownCells(placed);
            IndexNests();
            IndexTownLists(placed);
        }

        /// <summary>
        /// Fills <see cref="_cellsOf"/> from the biome map, row by row.
        /// </summary>
        void IndexCells()
        {
            for (int cz = 0; cz < WeatherAreas.Cells; cz++)
            {
                for (int cx = 0; cx < WeatherAreas.Cells; cx++)
                {
                    if (_map.GroupOf(cx, cz) is var g and >= 0)
                    {
                        if (!_cellsOf.TryGetValue(g, out var l)) _cellsOf[g] = l = [];
                        l.Add($"{cx}-{cz}");
                    }
                }
            }
        }

        /// <summary>
        /// Fills <see cref="_townsInCell"/> with each placed town's zone cell.
        /// </summary>
        void IndexTownCells(List<PlacedTown> placed)
        {
            foreach (var pt in placed)
            {
                var (cx, cz) = WeatherAreas.CellOf(pt.Placement.Position.X, pt.Placement.Position.Z);
                string key = $"{cx}-{cz}";
                if (!_townsInCell.TryGetValue(key, out var l)) _townsInCell[key] = l = [];
                l.Add(pt.Town.Name);
            }
        }

        /// <summary>
        /// Fills <see cref="_nestSquads"/> and <see cref="NestsOf"/> from the named Nest town records.
        /// </summary>
        void IndexNests()
        {
            foreach (var r in _db.OfType(FcsRecordType.Town))
            {
                var t = TownData.From(r);
                if (t.Type != TownType.Nest || t.Name.Length == 0) continue;
                _nestSquads[r.StringId] = [.. t.RoamingSquads.Select(l => l.Id)];
                foreach (var l in t.RoamingSquads)
                {
                    if (!NestsOf.TryGetValue(l.Id, out var n)) NestsOf[l.Id] = n = [];
                    if (!n.Contains(t.Name)) n.Add(t.Name);
                }
            }
        }

        /// <summary>
        /// Fills <see cref="TownsOf"/> from the placed towns' resident, bar and roaming lists.
        /// </summary>
        void IndexTownLists(List<PlacedTown> placed)
        {
            foreach (var pt in placed)
            {
                foreach (var l in pt.Town.Residents.Concat(pt.Town.BarSquads).Concat(pt.Town.RoamingSquads))
                {
                    if (!TownsOf.TryGetValue(l.Id, out var n)) TownsOf[l.Id] = n = [];
                    if (!n.Contains(pt.Town.Name)) n.Add(pt.Town.Name);
                }
            }
        }

        /// <summary>
        /// Biome group <paramref name="g"/>'s row, null when it paints no cell and spawns nothing; records its squads in <see cref="BiomesOf"/>.
        /// </summary>
        public Row? BiomeRow(int g, Func<string, string, string> id)
        {
            var group = _map.Groups[g];
            var cells = _cellsOf.GetValueOrDefault(g) ?? [];
            var squadRows = HomelessRows(group);
            var nestRows = NestRows(group);
            var towns = cells.SelectMany(c => _townsInCell.GetValueOrDefault(c) ?? []).Distinct().ToList();
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
            if (cells.Count == 0 && group.HomelessSpawns.Count == 0 && group.Nests.Count == 0) return null;
            string where = cells.Count == 0
                ? "No zone cell is painted with this group in the areas map."
                : $"Covers {cells.Count} zone cell(s) and {towns.Count} placed town(s).";
            return new Row(id(group.Name + " biome", group.Id), group.Name, "biome", null, null, null, null, "Observed",
                $"Biome group {group.Name}: {where} Homeless squads per loaded sector {Round(group.HomelessSpawnAmount)} "
                + $"(times the population setting); {squadRows.Count} squad template(s) can spawn, {group.NumNests} nest(s).", group.Id, data);
        }

        /// <summary>
        /// The group's homeless squads with their share of the weights in percent; each squad record also goes into <see cref="BiomesOf"/>.
        /// </summary>
        List<object?> HomelessRows(BiomeGroupData group)
        {
            double total = group.HomelessSpawns.Sum(l => (double)Math.Max(1, l.V0));
            var squadRows = new List<object?>();
            foreach (var l in group.HomelessSpawns)
            {
                double share = Math.Max(1, l.V0) / Math.Max(1, total) * 100;
                if (SquadRow(l, share) is { } row) squadRows.Add(row);
                if (_db.Find(l.Id) is { } sq)
                {
                    if (!BiomesOf.TryGetValue(l.Id, out var list)) BiomesOf[l.Id] = list = [];
                    list.Add((group.Name, Math.Round(share, 1)));
                }
            }
            return squadRows;
        }

        /// <summary>
        /// The group's named nests with their weight in percent (0 counts as 100) and the squads that roam around each.
        /// </summary>
        List<object?> NestRows(BiomeGroupData group)
        {
            double nestTotal = group.Nests.Sum(l => (double)(l.V0 == 0 ? 100 : l.V0));
            var nestRows = new List<object?>();
            foreach (var l in group.Nests)
            {
                if (_db.Find(l.Id) is not { } nest || nest.Name.Length == 0) continue;
                nestRows.Add(new SortedDictionary<string, object?>
                {
                    ["nest"] = nest.Name,
                    ["weightPct"] = Math.Round((l.V0 == 0 ? 100 : l.V0) / Math.Max(1, nestTotal) * 100, 1),
                    ["squads"] = Names(_db, _nestSquads.GetValueOrDefault(l.Id) ?? []),
                });
            }
            return nestRows;
        }

        /// <summary>
        /// A spawned squad's entry: name, share, faction (the template's, else its leader's), leader, members, animals and world states; null when
        /// the record is missing or unnamed.
        /// </summary>
        object? SquadRow(RecordLink link, double share)
        {
            var rec = _db.Find(link.Id);
            if (rec is null || rec.Name.Length == 0) return null;
            _templates.TryGetValue(link.Id, out var t);
            string faction = t is null ? "" : FactionNameOf(_db, t.Faction);
            string leader = t?.Leader is { } ll ? _db.Find(ll.Id)?.Name ?? "" : "";
            if (faction.Length == 0 && t?.Leader is { } l2 && _db.Find(l2.Id) is { } lc && lc.GetReferences("faction") is { Count: > 0 } lf)
                faction = FactionNameOf(_db, lf[0].TargetStringId);
            var members = t is null ? [] : Names(_db, t.Squad.Concat(t.Squad2).Concat(t.ChooseFrom).Select(x => x.Id)).Take(8).ToList();
            var animals = t is null ? [] : Names(_db, t.Animals.Concat(t.Animals2).Select(x => x.Id));
            var states = t is null ? [] : Names(_db, t.WorldState.Select(x => x.Id));
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
    }

    #endregion

    #region Animals

    /// <summary>
    /// Animal types: one row per named animal character with its race, stats, weapon, drops and where it spawns.
    /// </summary>
    static List<Row> Animals(GameDatabase db, Func<string, string, string> id, Dictionary<string, SquadTemplate> templates, SpawnIndex spawn)
    {
        var withAnimal = new Dictionary<string, List<(SquadTemplate T, RecordLink L)>>(StringComparer.Ordinal);
        foreach (var t in templates.Values)
        {
            foreach (var l in t.Animals.Concat(t.Animals2))
            {
                if (!withAnimal.TryGetValue(l.Id, out var list)) withAnimal[l.Id] = list = [];
                list.Add((t, l));
            }
        }
        var rows = new List<Row>();
        foreach (var record in db.OfType(FcsRecordType.AnimalCharacter).Where(r => r.Name.Length > 0).OrderBy(r => r.Name, StringComparer.Ordinal)
                .ThenBy(r => r.StringId, StringComparer.Ordinal))
        {
            rows.Add(AnimalRow(db, id, record, withAnimal, spawn));
        }
        return rows;
    }

    /// <summary>
    /// One animal's row: race, stats, weapon, drops, the squads that bring it and where those spawn.
    /// </summary>
    static Row AnimalRow(GameDatabase db, Func<string, string, string> id, GameRecord record,
        Dictionary<string, List<(SquadTemplate T, RecordLink L)>> withAnimal, SpawnIndex spawn)
    {
        var race = record.GetReferences("race") is { Count: > 0 } rl ? db.Find(rl[0].TargetStringId) : null;
        var data = new SortedDictionary<string, object?> { ["record"] = record.StringId };
        if (race is not null) data["race"] = AnimalRace(db, race);
        AnimalStats(record, data);
        AnimalWeapon(db, record, data);
        AnimalDrops(db, record, data);

        var uses = withAnimal.GetValueOrDefault(record.StringId) ?? [];
        var (squads, biomes, nests, towns) = AnimalSpawns(uses, spawn, data);
        string faction = record.GetReferences("faction") is { Count: > 0 } fl ? FactionNameOf(db, fl[0].TargetStringId) : "";
        if (faction.Length == 0)
            faction = uses.Select(u => FactionNameOf(db, u.T.Faction)).FirstOrDefault(n => n.Length > 0) ?? "";
        string where = biomes + nests + towns == 0
            ? "No squad that spawns on its own (biome, nest or town list) uses it."
            : $"Spawns through {squads} squad template(s): {biomes} biome entries, {nests} nest(s), {towns} town(s).";
        return new Row(id(record.Name, record.StringId), record.Name, "animal-type", faction.Length == 0 ? null : faction, null, null, null,
            "Observed",
            $"Animal{(race is null ? "" : ", race " + race.Name)}, combat stats {record.GetInt("combat stats")}, "
            + $"animal strength {record.GetInt("animal strength")}, {Round(record.GetFloat("HP mult", 1))}x health. {where}",
            record.StringId, data);
    }

    /// <summary>
    /// An animal race's movement, blood, healing, strength, flags, value and anatomy.
    /// </summary>
    static SortedDictionary<string, object?> AnimalRace(GameDatabase db, GameRecord race) => new()
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
            .Select(x => (object?)new SortedDictionary<string, object?> { ["part"] = db.Find(x.TargetStringId)!.Name,
            ["hp"] = x.Values.Value0 })
            .ToList(),
    };

    /// <summary>
    /// An animal character's stats (the skill stats only when above 0), health, lifespan, scale, inventory, senses and uniqueness.
    /// </summary>
    static void AnimalStats(GameRecord record, SortedDictionary<string, object?> data)
    {
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
    }

    /// <summary>
    /// The animal's natural weapon (when its first weapon reference is a weapon record) and its weapon level.
    /// </summary>
    static void AnimalWeapon(GameDatabase db, GameRecord record, SortedDictionary<string, object?> data)
    {
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
        if (record.GetReferences("weapon level") is { Count: > 0 } lv)
            data["weaponLevel"] = FactionNameOf(db, lv[0].TargetStringId);
    }

    /// <summary>
    /// What the animal drops when it dies (items with a count above 0) and its spawn bounty, if any.
    /// </summary>
    static void AnimalDrops(GameDatabase db, GameRecord record, SortedDictionary<string, object?> data)
    {
        data["deathItems"] = record.GetReferences("death items").Where(x => x.Values.Value0 > 0 && db.Find(x.TargetStringId) is not null)
            .Select(x => (object?)new SortedDictionary<string, object?> { ["item"] = db.Find(x.TargetStringId)!.Name,
                ["count"] = x.Values.Value0 }).ToList();
        int chance = record.GetInt("bounty chance"), amount = record.GetInt("bounty amount");
        if (chance > 0 && amount > 0)
        {
            data["bounty"] = new SortedDictionary<string, object?> { ["chancePct"] = chance, ["amount"] = amount,
                ["amountMax"] = amount + Math.Max(0, record.GetInt("bounty amount fuzz")) };
        }
    }

    /// <summary>
    /// The squads that bring the animal (by name) and the biomes, nests and towns those spawn from; returns the four counts.
    /// </summary>
    static (int Squads, int Biomes, int Nests, int Towns) AnimalSpawns(List<(SquadTemplate T, RecordLink L)> uses, SpawnIndex spawn,
        SortedDictionary<string, object?> data)
    {
        var squadRows = new List<object?>();
        var biomes = new List<object?>();
        var nests = new SortedSet<string>(StringComparer.Ordinal);
        var towns = new SortedSet<string>(StringComparer.Ordinal);
        var seenBiome = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (t, l) in uses.OrderBy(u => u.T.Name, StringComparer.Ordinal))
        {
            squadRows.Add(new SortedDictionary<string, object?>
            {
                ["squad"] = t.Name,
                ["count"] = l.V0 == l.V1 || l.V1 == 0 ? l.V0.ToString(CultureInfo.InvariantCulture) : $"{l.V0}-{l.V1}",
                ["record"] = t.Id,
            });
            foreach (var (biome, share) in spawn.BiomesOf.GetValueOrDefault(t.Id) ?? [])
            {
                if (seenBiome.Add(biome + "|" + t.Name))
                    biomes.Add(new SortedDictionary<string, object?> { ["biome"] = biome, ["sharePct"] = share, ["squad"] = t.Name });
            }
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
        return (squadRows.Count, biomes.Count, nests.Count, towns.Count);
    }

    #endregion

}
