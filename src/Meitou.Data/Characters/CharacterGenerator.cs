using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.Characters;

/// <summary>
/// Rolls a character from a CHARACTER (or RACE) record the way Kenshi spawns an NPC (docs/characters.md, "Generating a
/// character"): race and gender, then for characters without a body file a random head, hair, beard, hair colour, skin
/// tone, sliders and face poses from the race's editor limits, then backpack, clothing (with quality and material) and
/// weapons (with manufacturer and model). Deterministic for a seed; it follows the game's rules, not its random
/// sequence.
/// </summary>
public static class CharacterGenerator
{
    /// <summary>Kenshi's float-to-int rounding for sliders: halves round away from zero.</summary>
    public static int Round(float value) => value < 0 ? (int)MathF.Ceiling(value - 0.5f) : (int)MathF.Floor(value + 0.5f);

    /// <summary>Item quality of an armour grade (ArmourRarity 0–5); other values give 20.</summary>
    public static int QualityOfGrade(int grade) => grade switch { 0 => 5, 2 => 40, 3 => 60, 4 => 80, 5 => 95, _ => 20 };

    /// <summary>
    /// Rolls <paramref name="nameOrId"/> (CHARACTER or RACE, by string id or name). <paramref name="female"/> forces the gender;
    /// <paramref name="faction"/> is the FACTION it spawns in (its <c>hairstyles</c> limit hair and beards), default the CHARACTER's own.
    /// </summary>
    public static Loadout Generate(GameDatabase db, string installRoot, string nameOrId, int seed, bool? female = null, GameRecord? faction = null)
    {
        var record = CharacterAppearance.Find(db, nameOrId, FcsRecordType.CHARACTER, FcsRecordType.RACE)
            ?? throw new KeyNotFoundException($"No CHARACTER or RACE record '{nameOrId}'.");
        return Generate(db, installRoot, record, seed, female, faction);
    }

    public static Loadout Generate(GameDatabase db, string installRoot, GameRecord record, int seed, bool? female = null, GameRecord? faction = null)
    {
        var rng = new GeneratorRandom(seed ^ Fnv1a(record.StringId)); // different records roll differently for one seed
        var notes = new List<string>();
        GameRecord? character = record.Type == FcsRecordType.CHARACTER ? record : null;

        // A body file, if the CHARACTER names one that reads.
        AppearanceFile? bodyFile = null;
        if (character?.GetPath("body") is { Length: > 0 } bodyPath)
        {
            try { bodyFile = AppearanceFile.Read(Resolve(installRoot, bodyPath)); }
            catch (Exception e) when (e is IOException or FcsFormatException or UnauthorizedAccessException) { notes.Add($"body file {bodyPath}: {e.Message}"); }
        }

        // Race: the CHARACTER's "race" list (val0 = relative weight, entries with val0 > 0), else the body file's.
        GameRecord? race = record.Type == FcsRecordType.RACE ? record : null;
        if (race is null && character is not null)
            race = PickRelative(rng, character.GetReferences("race").Where(r => r.Values.Value0 > 0)
                .Select(r => (db.Find(r.TargetStringId), (float)r.Values.Value0)));
        if (race is null && bodyFile?.RaceId is { } bodyRace) race = db.Find(bodyRace);
        race ??= CharacterAppearance.Find(db, "Greenlander", FcsRecordType.RACE) ?? throw new KeyNotFoundException($"'{record.Name}' has no race.");

        // Gender: "female chance" percent, never for a single-gender race.
        bool isFemale = female ?? bodyFile?.Female ?? (!race.GetBool("single gender") && rng.Unit() < (character?.GetInt("female chance") ?? 50) * 0.01f);
        if (isFemale && race.GetBool("single gender")) isFemale = false;

        AppearanceFile appearance;
        int morph = -1;
        if (bodyFile is not null) appearance = bodyFile;
        else (appearance, morph) = RollAppearance(db, installRoot, character, faction, race, isFemale, rng, notes);

        var loadout = new Loadout
        {
            Seed = seed, Character = character, Race = race, Female = isFemale, Appearance = appearance,
            Generated = bodyFile is null, MorphIndex = morph,
        };
        loadout.Notes.AddRange(notes);
        if (character is not null) RollEquipment(db, character, race, rng, loadout);
        return loadout;
    }

    // ---- Appearance --------------------------------------------------------------------------------------------

    static (AppearanceFile, int) RollAppearance(GameDatabase db, string installRoot, GameRecord? character, GameRecord? faction, GameRecord race, bool female, GeneratorRandom rng, List<string> notes)
    {
        string g = female ? "female" : "male";
        var a = new FcsRecord { RecordType = FcsRecordType.CHARACTER_APPEARANCE, Name = "generated", StringId = "generated" };
        a.Bools["sex female"] = female;
        a.References["race"] = [new FcsReference(race.StringId, 0, 0, 0)];

        // Head: the race's heads of that gender that have a texture map; val0 is the weight.
        var head = PickCumulative(rng, race.GetReferences($"heads {g}")
            .Select(r => (Item: db.Find(r.TargetStringId), Weight: (float)r.Values.Value0))
            .Where(h => h.Item is not null && h.Item.GetPath("texture map").Length > 0));
        if (head is not null) a.Strings["head"] = head.StringId;

        // Hair and beard: the race's "hairs" by attach slot, weight val0 clamped to 1–100, restricted to the faction's
        // "hairstyles" when that leaves anything. Women get no beard.
        faction ??= character?.GetReferences("faction").Select(r => db.Find(r.TargetStringId)).FirstOrDefault(f => f is not null);
        var allowed = faction?.GetReferences("hairstyles").Select(r => r.TargetStringId).ToHashSet(StringComparer.Ordinal);
        string? Hair(AttachSlot slot)
        {
            var options = race.GetReferences("hairs").Select(r => (Item: db.Find(r.TargetStringId), Weight: (float)Math.Clamp(r.Values.Value0, 1, 100)))
                .Where(o => o.Item is { Type: FcsRecordType.ATTACHMENT } && (AttachSlot)o.Item.GetInt("attach slot", (int)AttachSlot.Hair) == slot).ToList();
            if (allowed is { Count: > 0 } && options.Any(o => allowed.Contains(o.Item!.StringId)))
                options = options.Select(o => (o.Item, allowed.Contains(o.Item!.StringId) ? o.Weight : 0f)).ToList();
            return PickCumulative(rng, options)?.StringId;
        }
        a.Strings["beard"] = female ? "" : Hair(AttachSlot.Beard) ?? "";
        if (Hair(AttachSlot.Hair) is { } hair) a.Strings["hair style"] = hair;

        // Hair colour: a weighted "hair colors" COLOR_DATA (val0, 0 counts as 100) stored as HSB sliders × 100;
        // without one, brightness 100 and saturation 0.
        var colour = PickRelative(rng, race.GetReferences("hair colors")
            .Select(r => (db.Find(r.TargetStringId), r.Values.Value0 == 0 ? 100f : r.Values.Value0)));
        if (colour is null)
        {
            a.Floats["Hair Brightness"] = 100;
            a.Floats["Hair Saturation"] = 0;
        }
        else
        {
            var (h, s, v) = ToHsb(CharacterAppearance.Rgb(colour.GetInt("color 1")));
            a.Floats["Hair Colour"] = h * 100;
            a.Floats["Hair Saturation"] = s * 100;
            a.Floats["Hair Brightness"] = v * 100;
        }

        // Sliders, colours and face poses from the race's editor limits.
        int morph = -1;
        AppearanceLimits? limits = null;
        if (race.GetPath("editor limits") is { Length: > 0 } limitsPath)
        {
            try { limits = AppearanceLimits.Read(Resolve(installRoot, limitsPath), female, DefaultDeviation(db)); }
            catch (Exception e) when (e is IOException or System.Xml.XmlException or UnauthorizedAccessException) { notes.Add($"editor limits {limitsPath}: {e.Message}"); }
            if (limits is null) notes.Add($"editor limits {limitsPath}: no {g} config");
        }
        else notes.Add($"race '{race.Name}' has no editor limits: no random sliders");
        if (limits is not null)
        {
            foreach (var category in new[] { AppearanceCategory.Face, AppearanceCategory.Body })
            {
                var groups = new Dictionary<int, float>();
                foreach (var range in limits.Ranges.Where(r => r.Category == category && r.Target is null))
                    a.Floats[range.Name] = Roll(rng, groups, range.Group, range.Mid, range.Variation);
                foreach (var range in limits.Colours.Where(r => r.Category == category))
                    a.Vector3s[range.Name] = range.ColourAt(Roll(rng, groups, range.Group, range.Mid, range.Variation));
            }
            // NPCs: the skin tone is uniform over its whole range instead.
            if (limits.Colours.FirstOrDefault(c => c.Name == "Skin Tone") is { } skin)
                a.Vector3s["Skin Tone"] = skin.ColourAt(rng.Int(skin.Min, skin.Max));

            // Face poses: one of the race's "morph num" faces per gender, each a fixed roll of the pose sliders.
            int count = Math.Max(race.GetInt("morph num", 5), 1);
            morph = Math.Min((int)(rng.Unit() * count), count - 1);
            var face = new GeneratorRandom(Fnv1a($"{race.StringId}/{(female ? "female" : "male")}/{morph}"));
            var faceGroups = new Dictionary<int, float>();
            foreach (var range in limits.Ranges.Where(r => r.Category == AppearanceCategory.Face && r.Target is not null))
            {
                int v = Roll(face, faceGroups, range.Group, range.Mid, range.Variation);
                if (range.TargetOpposite is { } opposite)
                {
                    a.Floats[range.Target!] = v > 0 ? v * 0.01f : 0;
                    a.Floats[opposite] = v < 0 ? v * -0.01f : 0;
                }
                else a.Floats[range.Target!] = v * 0.01f;
            }
        }
        a.Strings["idle stance"] = "idle_stand_normal";
        return (new AppearanceFile { Record = a }, morph);
    }

    /// <summary>round(mid + variation × (2r − 1)), one r per random group (−1 = its own); not clamped to min/max.</summary>
    static int Roll(GeneratorRandom rng, Dictionary<int, float> groups, int group, int mid, int variation)
    {
        float r;
        if (group < 0) r = rng.Unit();
        else if (!groups.TryGetValue(group, out r)) groups[group] = r = rng.Unit();
        return Round(variation * (r * 2 - 1) + mid);
    }

    static float DefaultDeviation(GameDatabase db) =>
        db.OfType(FcsRecordType.CONSTANTS).Select(c => c.Floats.TryGetValue("appearance random deviation percentage", out var f) ? f : (float?)null)
            .FirstOrDefault(f => f is not null) ?? 0.5f;

    // ---- Equipment ---------------------------------------------------------------------------------------------

    static void RollEquipment(GameDatabase db, GameRecord character, GameRecord race, GeneratorRandom rng, Loadout loadout)
    {
        // Backpack: entries with val0 > 0; val1 is an absolute chance (out of 100).
        var pack = PickAbsolute(rng, character.GetReferences("backpack").Where(r => r.Values.Value0 > 0)
            .Select(r => (db.Find(r.TargetStringId), (float)r.Values.Value1)).Where(p => p.Item1 is not null && RaceAllowed(p.Item1, race)));
        if (pack is not null) loadout.Backpack = new LoadoutItem(pack);

        // Clothing, one per slot in the game's order; race flags drop hats, shirts and shoes.
        int grade = character.GetInt("armour grade", 1), upgrade = character.GetInt("armour upgrade chance", 5);
        var slots = new List<AttachSlot>();
        if (!race.GetBool("no shirts")) slots.Add(AttachSlot.Shirt);
        if (!race.GetBool("no hats")) slots.Add(AttachSlot.Hat);
        if (!race.GetBool("no shoes")) slots.Add(AttachSlot.Boots);
        slots.AddRange([AttachSlot.Body, AttachSlot.Legs, AttachSlot.Belt]);
        var clothing = character.GetReferences("clothing");
        foreach (var slot in slots)
        {
            // Eligible: quantity ≠ 0, chance > 0, ARMOUR of this slot the race may wear. Chances are relative; a negative
            // quantity is a "no item" option.
            var options = clothing.Where(r => r.Values.Value0 != 0 && r.Values.Value1 > 0)
                .Select(r => (Ref: r, Item: db.Find(r.TargetStringId)))
                .Where(o => o.Item is { Type: FcsRecordType.ARMOUR } && o.Item.GetInt("slot", (int)AttachSlot.Body) == (int)slot && RaceAllowed(o.Item, race))
                .ToList();
            var chosen = PickRelative(rng, options.Select(o => ((GameReference?)o.Ref, (float)o.Ref.Values.Value1)));
            if (chosen is not { } c || c.Values.Value0 < 1) continue;
            var item = db.Find(c.TargetStringId)!;
            int up = grade < 5 && rng.Int(0, 100) < upgrade ? 1 : 0;
            var material = PickRelative(rng, item.GetReferences("material")
                .Select(r => (db.Find(r.TargetStringId), r.Values.Value1 == 0 ? 100f : r.Values.Value1)));
            loadout.Clothing.Add(new LoadoutItem(item) { Quality = QualityOfGrade(grade + up), Material = material });
        }

        // Crossbow: entries with val0 > 0, val1 an absolute chance. Shown on the back (viewer choice).
        var crossbow = PickAbsolute(rng, character.GetReferences("crossbows").Where(r => r.Values.Value0 > 0)
            .Select(r => (db.Find(r.TargetStringId), (float)r.Values.Value1)).Where(p => p.Item1 is { Type: FcsRecordType.CROSSBOW }));
        if (crossbow is not null) loadout.Crossbow = new LoadoutWeapon(crossbow) { Section = "back" };

        // Weapons: entries with val0 (quantity) > 0 split by val1 into a hip pool (0) and a back pool (anything else);
        // val2 is an absolute chance, 0 counting as 100. One weapon per pool.
        var weapons = character.GetReferences("weapons").Where(r => r.Values.Value0 > 0).ToList();
        foreach (var back in new[] { false, true })
        {
            var pick = PickAbsolute(rng, weapons.Where(r => (r.Values.Value1 != 0) == back)
                .Select(r => ((GameReference?)r, r.Values.Value2 == 0 ? 100f : r.Values.Value2)));
            if (pick is not { } wr || db.Find(wr.TargetStringId) is not { } weapon) continue;
            if (loadout.Crossbow is not null && weapon.GetInt("inventory footprint height", 1) >= 2)
            {
                loadout.Notes.Add($"{weapon.Name}: too long to carry with a crossbow");
                continue;
            }
            var made = MakeWeapon(db, character, weapon, rng, loadout);
            if (made is null) continue;
            int row = back && loadout.Crossbow is not null ? 1 : 0;
            loadout.Weapons.Add(made with { Section = back ? "back" : "hip", Row = row });
            if (wr.Values.Value0 > 1)
            {
                if (back && row == 0) loadout.Weapons.Add(made with { Section = "back", Row = 1 });
                else loadout.Notes.Add($"{weapon.Name} × {wr.Values.Value0}: extra copies not shown");
            }
        }
    }

    /// <summary>Manufacturer from CHARACTER "weapon level" (val0 relative, 0 = 100; none listed: "Ancient"), model from its "weapon models" (val1).</summary>
    static LoadoutWeapon? MakeWeapon(GameDatabase db, GameRecord character, GameRecord weapon, GeneratorRandom rng, Loadout loadout)
    {
        var levels = character.GetReferences("weapon level");
        var maker = levels.Count == 0 ? db.Find("917-gamedata.base")
            : PickRelative(rng, levels.Select(r => (db.Find(r.TargetStringId), r.Values.Value0 == 0 ? 100f : r.Values.Value0)));
        if (maker is null)
        {
            loadout.Notes.Add($"{weapon.Name}: no manufacturer");
            return null;
        }
        var modelRef = PickRelative(rng, maker.GetReferences("weapon models")
            .Where(r => db.Find(r.TargetStringId) is not null).Select(r => ((GameReference?)r, r.Values.Value1 == 0 ? 100f : r.Values.Value1)));
        if (modelRef is { } m && m.Values.Value0 != 0)
            return new LoadoutWeapon(weapon) { Section = "hip", Manufacturer = maker, Model = db.Find(m.TargetStringId), Level = m.Values.Value0 };
        // No model or level 0: the game falls back to a fixed manufacturer and model.
        var fallbackMaker = db.Find("1057-gamedata.base") ?? maker;
        var fallbackModel = db.Find("1058-gamedata.base");
        int level = fallbackMaker.GetReferences("weapon models").Where(r => r.TargetStringId == fallbackModel?.StringId).Select(r => r.Values.Value0).FirstOrDefault();
        level += (int)(fallbackModel?.GetFloat("overall level") ?? 0);
        return new LoadoutWeapon(weapon) { Section = "hip", Manufacturer = fallbackMaker, Model = fallbackModel, Level = level };
    }

    /// <summary>ARMOUR / item <c>races</c> (only these may equip it) and <c>races exclude</c>.</summary>
    static bool RaceAllowed(GameRecord item, GameRecord race)
    {
        var only = item.GetReferences("races");
        if (only.Count > 0 && !only.Any(r => r.TargetStringId == race.StringId)) return false;
        return !item.GetReferences("races exclude").Any(r => r.TargetStringId == race.StringId);
    }

    // ---- Weighted picks ----------------------------------------------------------------------------------------

    /// <summary>Relative weights: roll in [0, total) and take the first entry whose running sum exceeds it.</summary>
    static T? PickRelative<T>(GeneratorRandom rng, IEnumerable<(T? Item, float Weight)> options) where T : class
    {
        var list = options.Where(o => o.Item is not null && o.Weight > 0).ToList();
        if (list.Count == 0) return null;
        if (list.Count == 1) return list[0].Item;
        return Running(list, rng.Range(0, list.Sum(o => o.Weight)));
    }

    static GameReference? PickRelative(GeneratorRandom rng, IEnumerable<(GameReference? Ref, float Weight)> options)
    {
        var list = options.Where(o => o.Ref is not null && o.Weight > 0).ToList();
        if (list.Count == 0) return null;
        if (list.Count == 1) return list[0].Ref;
        return RunningRef(list, rng.Range(0, list.Sum(o => o.Weight)));
    }

    /// <summary>Absolute chances out of 100: roll in [0, 100); nothing when the roll is not below the total.</summary>
    static T? PickAbsolute<T>(GeneratorRandom rng, IEnumerable<(T? Item, float Weight)> options) where T : class
    {
        var list = options.Where(o => o.Item is not null && o.Weight > 0).ToList();
        if (list.Count == 0) return null;
        float roll = rng.Range(0, 100);
        return roll < list.Sum(o => o.Weight) ? Running(list, roll) : null;
    }

    static GameReference? PickAbsolute(GeneratorRandom rng, IEnumerable<(GameReference? Ref, float Weight)> options)
    {
        var list = options.Where(o => o.Ref is not null && o.Weight > 0).ToList();
        if (list.Count == 0) return null;
        float roll = rng.Range(0, 100);
        return roll < list.Sum(o => o.Weight) ? RunningRef(list, roll) : null;
    }

    static T? Running<T>(List<(T? Item, float Weight)> list, float roll) where T : class
    {
        float sum = 0;
        foreach (var (item, weight) in list)
            if ((sum += weight) > roll) return item;
        return list[^1].Item;
    }

    static GameReference? RunningRef(List<(GameReference? Ref, float Weight)> list, float roll)
    {
        float sum = 0;
        foreach (var (r, weight) in list)
            if ((sum += weight) > roll) return r;
        return list[^1].Ref;
    }

    /// <summary>Weights normalised to a running sum; roll in [0, 1) and take the first entry whose sum reaches it, else the last (heads, hair).</summary>
    static GameRecord? PickCumulative(GeneratorRandom rng, IEnumerable<(GameRecord? Item, float Weight)> options)
    {
        var list = options.Where(o => o.Item is not null).ToList();
        if (list.Count == 0) return null;
        float total = list.Sum(o => o.Weight), roll = rng.Unit(), sum = 0;
        if (total <= 0) return list[^1].Item;
        foreach (var (item, weight) in list)
            if (roll <= (sum += weight / total)) return item;
        return list[^1].Item;
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------

    /// <summary>RGB 0–1 to hue, saturation, brightness 0–1 (the inverse of <see cref="CharacterAppearance.FromHsb"/>).</summary>
    public static (float Hue, float Saturation, float Brightness) ToHsb(Vector3 rgb)
    {
        float max = MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z)), min = MathF.Min(rgb.X, MathF.Min(rgb.Y, rgb.Z)), delta = max - min;
        if (delta < 1e-6f) return (0, 0, max);
        float hue = max == rgb.X ? (rgb.Y - rgb.Z) / delta : max == rgb.Y ? 2 + (rgb.Z - rgb.X) / delta : 4 + (rgb.X - rgb.Y) / delta;
        hue /= 6;
        if (hue < 0) hue += 1;
        return (hue, delta / max, max);
    }

    static int Fnv1a(string s)
    {
        uint h = 0x811c9dc5;
        foreach (char c in s) h = (h ^ (byte)c) * 0x01000193;
        return (int)h;
    }

    static string Resolve(string installRoot, string fcsPath) =>
        Path.Combine(installRoot, fcsPath.Replace('\\', Path.DirectorySeparatorChar).TrimStart('.', Path.DirectorySeparatorChar));

    /// <summary>Deterministic random numbers for one seed (System.Random's seeded algorithm is fixed).</summary>
    sealed class GeneratorRandom(int seed)
    {
        readonly Random random = new(seed);
        /// <summary>Uniform in [0, 1).</summary>
        public float Unit() => (float)random.NextDouble();
        /// <summary>Uniform in [min, max).</summary>
        public float Range(float min, float max) => min + (float)random.NextDouble() * (max - min);
        /// <summary>Uniform integer in [min, max].</summary>
        public int Int(int min, int max) => max < min ? min : random.Next(min, max + 1);
    }
}
