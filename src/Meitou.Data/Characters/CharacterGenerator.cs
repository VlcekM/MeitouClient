using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;

namespace Meitou.Data.Characters;

/// <summary>
/// Rolls a character from a CHARACTER (or RACE) record the way Kenshi spawns an NPC (docs/characters.md, "Generating a
/// character"): race and gender, then for characters without a body file a random head, hair, beard, hair colour, skin
/// tone, sliders and face poses from the race's editor limits, then backpack, clothing (with quality and material) and
/// weapons (with manufacturer and model). Deterministic for a seed: the original's generator (<see cref="MsvcRandom"/>) seeded from it, its draws in the
/// original's order (docs/characters.md "Original random sequence"). The step functions (<see cref="RollHead"/>, <see cref="RollFace"/>, <see cref="RollHair"/>,
/// <see cref="RollBody"/>, <see cref="RollFacePoses"/>) are also the character editor's randomisers.
/// </summary>
public static class CharacterGenerator
{
    /// <summary>The RACE the original falls back to when a character has none (by id, so a mod that renames Greenlander keeps it; docs/characters.md).</summary>
    public const string DefaultRaceId = "17-gamedata.quack";

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

    public static Loadout Generate(GameDatabase db, string installRoot, GameRecord record, int seed, bool? female = null, GameRecord? faction = null, IReadOnlyList<GameReference>? raceOverride = null)
    {
        var rng = MsvcRandom.FromSeed(seed ^ Fnv1a(record.StringId)); // different records roll differently for one seed
        var notes = new List<string>();
        GameRecord? character = record.Type == FcsRecordType.CHARACTER ? record : null;

        // A body file, if the CHARACTER names one that reads.
        AppearanceFile? bodyFile = null;
        if (character?.GetPath("body") is { Length: > 0 } bodyPath)
        {
            try { bodyFile = AppearanceFile.Read(Resolve(installRoot, bodyPath)); }
            catch (Exception e) when (e is IOException or FcsFormatException or UnauthorizedAccessException) { notes.Add($"body file {bodyPath}: {e.Message}"); }
        }

        // Race (Faction::chooseARace, Verified in outline): the squad template's "race override", else the CHARACTER's "race" list (val0 = relative weight, entries with
        // val0 > 0), else the body file's, else the faction's "races" (the original takes the faction's before any body file; engine order for characters with one).
        GameRecord? race = record.Type == FcsRecordType.RACE ? record : null;
        if (race is null && character is not null && raceOverride is { Count: > 0 })
            race = PickRelative(rng, raceOverride.Where(r => r.Values.Value0 > 0).Select(r => (db.Find(r.TargetStringId), (float)r.Values.Value0)));
        if (race is null && character is not null)
            race = PickRelative(rng, character.GetReferences("race").Where(r => r.Values.Value0 > 0)
                .Select(r => (db.Find(r.TargetStringId), (float)r.Values.Value0)));
        if (race is null && bodyFile?.RaceId is { } bodyRace) race = db.Find(bodyRace);
        if (race is null && (faction ?? (character?.GetReferences("faction") is { Count: > 0 } own ? db.Find(own[0].TargetStringId) : null)) is { } races)
            race = PickRelative(rng, races.GetReferences("races").Select(r => (db.Find(r.TargetStringId), r.Values.Value0 > 0 ? (float)r.Values.Value0 : 1f)));
        if (race is null)
        {
            race = CharacterAppearance.Find(db, DefaultRaceId, FcsRecordType.RACE) ?? throw new KeyNotFoundException($"'{record.Name}' ({record.StringId}) has no race.");
            notes.Add($"no race: default {DefaultRaceId}");
        }

        // Gender: "female chance" percent, never for a single-gender race.
        bool isFemale = female ?? bodyFile?.Female ?? (!race.GetBool("single gender") && rng.Unit() < (character?.GetInt("female chance") ?? 50) * 0.01f);
        if (isFemale && race.GetBool("single gender")) isFemale = false;

        AppearanceFile appearance;
        int morph = -1;
        if (bodyFile is not null) appearance = bodyFile;
        else (appearance, morph) = RollAppearance(db, installRoot, character, faction, race, isFemale, rng, notes);

        faction ??= character?.GetReferences("faction").Select(r => db.Find(r.TargetStringId)).FirstOrDefault(f => f is { Type: FcsRecordType.FACTION });
        var loadout = new Loadout
        {
            Seed = seed, Character = character, Faction = faction, Race = race, Female = isFemale, Appearance = appearance,
            Generated = bodyFile is null, MorphIndex = morph,
        };
        loadout.Notes.AddRange(notes);
        if (character is not null) RollEquipment(db, character, race, rng, loadout);
        return loadout;
    }

    // ---- Appearance --------------------------------------------------------------------------------------------

    /// <summary>
    /// A generated NPC's look in the original's draw order (<c>FUN_140581770</c>): head, head again with the face sliders, beard, hair and hair colour; for a CHARACTER
    /// the body sliders, colours and skin re-roll, then the morph index. The face poses come from the morph face's own sequence (<see cref="RollFacePoses"/>). A
    /// RACE (the viewer) takes the body and morph steps too (engine choice: the original never generates from a bare race).
    /// </summary>
    static (AppearanceFile, int) RollAppearance(GameDatabase db, string installRoot, GameRecord? character, GameRecord? faction, GameRecord race, bool female, MsvcRandom rng, List<string> notes)
    {
        string g = female ? "female" : "male";
        var a = new FcsRecord { RecordType = FcsRecordType.CHARACTER_APPEARANCE, Name = "generated", StringId = "generated" };
        a.Bools["sex female"] = female;
        a.References["race"] = [new FcsReference(race.StringId, 0, 0, 0)];

        AppearanceLimits? limits = null;
        if (race.GetPath("editor limits") is { Length: > 0 } limitsPath)
        {
            try { limits = AppearanceLimits.Read(Resolve(installRoot, limitsPath), female, GameConstants.FromDatabase(db).AppearanceRandomDeviation); }
            catch (Exception e) when (e is IOException or System.Xml.XmlException or UnauthorizedAccessException) { notes.Add($"editor limits {limitsPath}: {e.Message}"); }
            if (limits is null) notes.Add($"editor limits {limitsPath}: no {g} config");
        }
        else notes.Add($"race '{race.Name}' has no editor limits: no random sliders");

        faction ??= character?.GetReferences("faction").Select(r => db.Find(r.TargetStringId)).FirstOrDefault(f => f is not null);
        RollHead(db, race, a, rng);
        RollFace(db, race, limits, a, rng);
        RollHair(db, race, faction, a, rng);
        RollBody(limits, a, rng, skinReroll: true);

        // Face poses: one of the race's "morph num" faces per gender (morph index = (int)(unit × morph num)), each a fixed roll of the pose sliders: the original
        // seeds the global generator with the FNV-1a of the morph face's mesh name when it makes that mesh (FUN_14007c070); the name's form is Unknown, so Meitou's
        // stand-in is race/gender/index.
        int morph = -1;
        if (limits is not null)
        {
            int count = Math.Max(race.GetInt("morph num", 5), 1);
            morph = Math.Min((int)(rng.Unit() * count), count - 1);
            RollFacePoses(limits, a, new MsvcRandom((uint)Fnv1a($"{race.StringId}/{g}/{morph}")));
        }
        a.Strings["idle stance"] = "idle_stand_normal";
        return (new AppearanceFile { Record = a }, morph);
    }

    /// <summary>The head (<c>FUN_14007c880</c>): one cumulative pick among the race's heads of the look's gender that have a texture map; none: no draw.</summary>
    public static void RollHead(GameDatabase db, GameRecord race, FcsRecord look, MsvcRandom rng)
    {
        if (HeadOptions(db, race, look) is { } heads && PickCumulative(rng, heads) is { } head) look.Strings["head"] = head.StringId;
    }

    /// <summary>The race's heads of the look's gender that have a texture map, with their weights (val0), in list order.</summary>
    public static List<(GameRecord? Item, float Weight)> HeadOptions(GameDatabase db, GameRecord race, FcsRecord look) =>
        [.. race.GetReferences($"heads {(IsFemale(look) ? "female" : "male")}")
            .Select(r => (Item: db.Find(r.TargetStringId), Weight: (float)r.Values.Value0))
            .Where(h => h.Item is not null && h.Item.GetPath("texture map").Length > 0)];

    /// <summary>The face (<c>FUN_14007b310</c>): the head again, then the Face sliders (not the poses, not colours) as <see cref="Rolls"/> rolls them.</summary>
    public static void RollFace(GameDatabase db, GameRecord race, AppearanceLimits? limits, FcsRecord look, MsvcRandom rng)
    {
        RollHead(db, race, look, rng);
        if (limits is null) return;
        foreach (var (range, v) in Rolls(limits.Ranges.Where(r => r.Category == AppearanceCategory.Face && r.Target is null).ToList(), r => (r.Group, r.Mid, r.Variation), rng))
            look.Floats[range.Name] = v;
    }

    /// <summary>
    /// The hair (<c>FUN_14007adc0</c>): the beard (men; women get none, no draw), the hair, each a cumulative pick among the race's <c>hairs</c> of that attach slot
    /// (weight val0 clamped to 1–100, only the <paramref name="faction"/>'s <c>hairstyles</c> when that leaves any), then the hair colour: a relative pick among the
    /// race's <c>hair colors</c> COLOR_DATA (val0, 0 counting as 100) stored as HSB sliders × 100, without one brightness 100 and saturation 0.
    /// </summary>
    public static void RollHair(GameDatabase db, GameRecord race, GameRecord? faction, FcsRecord look, MsvcRandom rng)
    {
        var allowed = faction?.GetReferences("hairstyles").Select(r => r.TargetStringId).ToHashSet(StringComparer.Ordinal);
        string? Hair(AttachSlot slot)
        {
            var options = race.GetReferences("hairs").Select(r => (Item: db.Find(r.TargetStringId), Weight: (float)Math.Clamp(r.Values.Value0, 1, 100)))
                .Where(o => o.Item is { Type: FcsRecordType.ATTACHMENT } && (AttachSlot)o.Item.GetInt("attach slot", (int)AttachSlot.Hair) == slot).ToList();
            if (allowed is { Count: > 0 } && options.Any(o => allowed.Contains(o.Item!.StringId)))
                options = options.Select(o => (o.Item, allowed.Contains(o.Item!.StringId) ? o.Weight : 0f)).ToList();
            return PickCumulative(rng, options)?.StringId;
        }
        look.Strings["beard"] = IsFemale(look) ? "" : Hair(AttachSlot.Beard) ?? "";
        look.Strings["hair style"] = Hair(AttachSlot.Hair) ?? "";

        var colour = PickRelative(rng, race.GetReferences("hair colors")
            .Select(r => (db.Find(r.TargetStringId), r.Values.Value0 == 0 ? 100f : r.Values.Value0)));
        if (colour is null)
        {
            look.Floats["Hair Brightness"] = 100;
            look.Floats["Hair Saturation"] = 0;
        }
        else
        {
            var (h, s, v) = ToHsb(CharacterAppearance.Rgb(colour.GetInt("color 1")));
            look.Floats["Hair Colour"] = h * 100;
            look.Floats["Hair Saturation"] = s * 100;
            look.Floats["Hair Brightness"] = v * 100;
        }
    }

    /// <summary>
    /// The body (<c>FUN_14007b940</c>): the Body sliders, then the Body colours, each as <see cref="Rolls"/> rolls them (each list its own group draws); with
    /// <paramref name="skinReroll"/> (a generated NPC, not the editor) the skin tone again, uniform over its whole range; last <c>Posture</c> back to the appearance
    /// template's 20 (docs/characters.md "Random appearance": its draw is still made).
    /// </summary>
    public static void RollBody(AppearanceLimits? limits, FcsRecord look, MsvcRandom rng, bool skinReroll)
    {
        if (limits is not null)
        {
            foreach (var (range, v) in Rolls(limits.Ranges.Where(r => r.Category == AppearanceCategory.Body && r.Target is null).ToList(), r => (r.Group, r.Mid, r.Variation), rng))
                look.Floats[range.Name] = v;
            var colours = limits.Colours.Where(c => c.Category == AppearanceCategory.Body).ToList();
            foreach (var (c, v) in Rolls(colours, c => (c.Group, c.Mid, c.Variation), rng)) look.Vector3s[c.Name] = c.ColourAt(v);
            // The original rolls Int(max(min, mid - w), min(max, mid + w)) with w = round(max - min): the whole range.
            if (skinReroll && colours.FirstOrDefault(c => c.Name == "Skin Tone") is { } skin) look.Vector3s["Skin Tone"] = skin.ColourAt(rng.Int(skin.Min, skin.Max));
        }
        look.Floats["Posture"] = 20;
    }

    /// <summary>
    /// The face poses (<c>FUN_140070010</c>, the editor's <c>FUN_14007b7c0</c>): the Face pose sliders as <see cref="Rolls"/> rolls them, written as pose weights
    /// (value / 100; a pair with an opposite: the positive side on the target, the negative on the opposite, the other 0).
    /// </summary>
    public static void RollFacePoses(AppearanceLimits? limits, FcsRecord look, MsvcRandom rng)
    {
        if (limits is null) return;
        foreach (var (range, v) in Rolls(limits.Ranges.Where(r => r.Category == AppearanceCategory.Face && r.Target is not null).ToList(), r => (r.Group, r.Mid, r.Variation), rng))
        {
            if (range.TargetOpposite is { } opposite)
            {
                look.Floats[range.Target!] = v > 0 ? v * 0.01f : 0;
                look.Floats[opposite] = v < 0 ? v * -0.01f : 0;
            }
            else look.Floats[range.Target!] = v * 0.01f;
        }
    }

    /// <summary>
    /// The original's rolls of a list of ranges (<c>FUN_14006fcf0</c>, <c>FUN_140070460</c>, <c>FUN_140070010</c>, <b>Verified</b> by decompilation): the ranges
    /// without a <c>random_group</c> in list order, one draw each; then one draw per group, shared by its ranges, the groups in the order of the original's
    /// <c>boost::unordered_map&lt;short, …&gt;</c> (<see cref="BoostHashOrder"/>). A value is round(variation × (2r − 1) + mid), not clamped.
    /// </summary>
    static List<(T Range, int Value)> Rolls<T>(List<T> ranges, Func<T, (int Group, int Mid, int Variation)> of, MsvcRandom rng)
    {
        var result = new List<(T, int)>();
        var grouped = new Dictionary<int, List<T>>();
        var firstSeen = new List<int>();
        foreach (var range in ranges)
        {
            var (group, mid, variation) = of(range);
            if (group < 0) result.Add((range, Round(variation * (rng.Unit() * 2 - 1) + mid)));
            else if (grouped.TryGetValue(group, out var members)) members.Add(range);
            else { grouped[group] = [range]; firstSeen.Add(group); }
        }
        foreach (int group in BoostHashOrder.Of(firstSeen.Select(g => (long)g)))
        {
            float r = rng.Unit();
            foreach (var range in grouped[group])
            {
                var (_, mid, variation) = of(range);
                result.Add((range, Round(variation * (r * 2 - 1) + mid)));
            }
        }
        return result;
    }

    static bool IsFemale(FcsRecord look) => look.Bools.TryGetValue("sex female", out var f) && f;

    // ---- Equipment ---------------------------------------------------------------------------------------------

    static void RollEquipment(GameDatabase db, GameRecord character, GameRecord race, MsvcRandom rng, Loadout loadout)
    {
        // Put on new, an item keeps its record's colour, else takes the wearer's scheme (docs/characters.md "Clothing colours").
        GameRecord? Colour(GameRecord item) => Gameplay.ItemColours.Of(db, item) ?? Gameplay.ItemColours.Scheme(db, character, loadout.Faction);
        // Backpack: entries with val0 > 0; val1 is an absolute chance (out of 100).
        var pack = PickAbsolute(rng, character.GetReferences("backpack").Where(r => r.Values.Value0 > 0)
            .Select(r => (db.Find(r.TargetStringId), (float)r.Values.Value1)).Where(p => p.Item1 is not null && RaceAllowed(p.Item1, race)));
        if (pack is not null) loadout.Backpack = new LoadoutItem(pack) { Colour = Colour(pack) };

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
            loadout.Clothing.Add(new LoadoutItem(item) { Quality = QualityOfGrade(grade + up), Material = material, Colour = Colour(item) });
        }

        // Crossbow: entries with val0 > 0, val1 an absolute chance. Shown on the back (viewer choice).
        var crossbow = PickAbsolute(rng, character.GetReferences("crossbows").Where(r => r.Values.Value0 > 0)
            .Select(r => (db.Find(r.TargetStringId), (float)r.Values.Value1)).Where(p => p.Item1 is { Type: FcsRecordType.CROSSBOW }));
        if (crossbow is not null) loadout.Crossbow = new LoadoutWeapon(crossbow) { Section = "back" };

        // Weapons: entries with val0 (quantity) > 0 split by val1 into a hip pool (0) and a back pool (anything else);
        // val2 is an absolute chance, 0 counting as 100. One weapon per pool. Engine choice: a weapon goes where its footprint fits (hip 7x1,
        // back 10x2 by rows), so a large hip-pool weapon hangs on the back and a back-pool one falls back to the hip; one that fits neither is left out.
        var weapons = character.GetReferences("weapons").Where(r => r.Values.Value0 > 0).ToList();
        // The back weapon goes below the crossbow: a crossbow two rows tall fills the back section (10 x 2), so the weapon then takes the hip or is left out.
        int backRow = crossbow is null ? 0 : Math.Clamp(crossbow.GetInt("inventory footprint height", 1), 1, 2);
        bool hipFree = true;
        foreach (var back in new[] { false, true })
        {
            var pick = PickAbsolute(rng, weapons.Where(r => (r.Values.Value1 != 0) == back)
                .Select(r => ((GameReference?)r, r.Values.Value2 == 0 ? 100f : r.Values.Value2)));
            if (pick is not { } wr || db.Find(wr.TargetStringId) is not { } weapon) continue;
            int width = Math.Max(weapon.GetInt("inventory footprint width", 1), 1), height = Math.Max(weapon.GetInt("inventory footprint height", 1), 1);
            bool fitsHip = hipFree && width <= 7 && height <= 1, fitsBack = width <= 10 && backRow + height <= 2;
            string? section = !back && fitsHip ? "hip" : fitsBack ? "back" : fitsHip ? "hip" : null;
            if (section is null)
            {
                loadout.Notes.Add($"{weapon.Name}: no free weapon slot it fits");
                continue;
            }
            var made = MakeWeapon(db, character, weapon, rng, loadout);
            if (made is null) continue;
            int row = section == "back" ? backRow : 0;
            loadout.Weapons.Add(made with { Section = section, Row = row });
            if (section == "back") backRow += height;
            else hipFree = false;
            if (wr.Values.Value0 > 1)
            {
                if (back && section == "back" && backRow + height <= 2)
                {
                    loadout.Weapons.Add(made with { Section = "back", Row = backRow });
                    backRow += height;
                }
                else loadout.Notes.Add($"{weapon.Name} × {wr.Values.Value0}: extra copies not shown");
            }
        }
        if (loadout.Weapons.Count > 1 && loadout.Weapons[0].Section == "back" && loadout.Weapons[^1].Section == "hip")
            loadout.Weapons.Reverse();   // equip order: hip first, then back
    }

    /// <summary>Manufacturer from CHARACTER "weapon level" (val0 relative, 0 = 100; none listed: "Ancient"), model from its "weapon models" (val1).</summary>
    static LoadoutWeapon? MakeWeapon(GameDatabase db, GameRecord character, GameRecord weapon, MsvcRandom rng, Loadout loadout)
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
        var (fallbackMaker, fallbackModel, level) = FallbackWeaponMaker(db, maker);
        return new LoadoutWeapon(weapon) { Section = "hip", Manufacturer = fallbackMaker, Model = fallbackModel, Level = level };
    }

    /// <summary>
    /// The weapon factory's substitute when the picked manufacturer has no model or its model's level is 0 (<c>FUN_140580750</c>, <b>Verified</b>): manufacturer <c>1057-gamedata.base</c> with model
    /// <c>1058-gamedata.base</c>, the level that manufacturer's <c>weapon models</c> entry for the model gives plus the manufacturer's <c>overall level</c> (cut to an int; neither base record has it).
    /// <paramref name="orElse"/> stands in for a missing 1057 (a stripped data set).
    /// </summary>
    public static (GameRecord Maker, GameRecord? Model, int Level) FallbackWeaponMaker(GameDatabase db, GameRecord orElse)
    {
        var maker = db.Find("1057-gamedata.base") ?? orElse;
        var model = db.Find("1058-gamedata.base");
        int level = maker.GetReferences("weapon models").Where(r => r.TargetStringId == model?.StringId).Select(r => r.Values.Value0).FirstOrDefault();
        return (maker, model, level + (int)maker.GetFloat("overall level"));
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
    static T? PickRelative<T>(MsvcRandom rng, IEnumerable<(T? Item, float Weight)> options) where T : class
    {
        var list = options.Where(o => o.Item is not null && o.Weight > 0).ToList();
        if (list.Count == 0) return null;
        if (list.Count == 1) return list[0].Item;
        return Running(list, rng.Range(0, list.Sum(o => o.Weight)));
    }

    static GameReference? PickRelative(MsvcRandom rng, IEnumerable<(GameReference? Ref, float Weight)> options)
    {
        var list = options.Where(o => o.Ref is not null && o.Weight > 0).ToList();
        if (list.Count == 0) return null;
        if (list.Count == 1) return list[0].Ref;
        return RunningRef(list, rng.Range(0, list.Sum(o => o.Weight)));
    }

    /// <summary>Absolute chances out of 100: roll in [0, 100); nothing when the roll is not below the total.</summary>
    static T? PickAbsolute<T>(MsvcRandom rng, IEnumerable<(T? Item, float Weight)> options) where T : class
    {
        var list = options.Where(o => o.Item is not null && o.Weight > 0).ToList();
        if (list.Count == 0) return null;
        float roll = rng.Range(0, 100);
        return roll < list.Sum(o => o.Weight) ? Running(list, roll) : null;
    }

    static GameReference? PickAbsolute(MsvcRandom rng, IEnumerable<(GameReference? Ref, float Weight)> options)
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
    static GameRecord? PickCumulative(MsvcRandom rng, IEnumerable<(GameRecord? Item, float Weight)> options)
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

    /// <summary>
    /// The file a record's path names: under the install, or for <c>.\mods\Mod Name\x</c> in that mod's folder, which for a Workshop mod is its Workshop folder
    /// (<see cref="Physics.CollisionPaths.Resolve"/>; #93: a Workshop race's editor limits were not found); the install-relative path when neither has it.
    /// </summary>
    internal static string Resolve(string installRoot, string fcsPath) =>
        (Meitou.Content.GameInstall.IsValid(installRoot) ? Physics.CollisionPaths.Resolve(Meitou.Content.GameInstall.Open(installRoot), fcsPath) : null)
        ?? Path.Combine(installRoot, fcsPath.Replace('\\', Path.DirectorySeparatorChar).TrimStart('.', Path.DirectorySeparatorChar));
}
