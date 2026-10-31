using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.Characters;

/// <summary>Kenshi's AttachSlot enum (fcs_enums.def): ARMOUR <c>slot</c> and ATTACHMENT <c>attach slot</c>.</summary>
public enum AttachSlot
{
    Weapon, Back, Hair, Hat, Eyes, Body, Legs, None, Shirt, Boots, Gloves, Neck, Backpack, Beard, Belt,
}

/// <summary>How a part joins the body (docs/formats/ogre-skeleton.md, "Binding worn meshes").</summary>
public enum AttachMode
{
    /// <summary>Skinned with the body's skeleton instance, by bone index (ARMOUR, CONTAINER, ATTACHMENT, LIMB_REPLACEMENT).</summary>
    SharedSkeleton,
    /// <summary>Rigidly attached to a bone, at an attachment point's offset (weapons and other items).</summary>
    Bone,
}

/// <summary>One mesh of an assembled character.</summary>
public sealed record CharacterPart
{
    public required GameRecord Record { get; init; }
    /// <summary>FCS file name of the mesh (as stored, e.g. <c>.\data\items\...\x.mesh</c>); the game reduces it to the bare name.</summary>
    public required string Mesh { get; init; }
    /// <summary>The record field the mesh came from (<c>mesh</c>, <c>mesh female</c>, <c>bare sword</c>, ...).</summary>
    public required string Field { get; init; }
    public required AttachMode Mode { get; init; }
    public AttachSlot? Slot { get; init; }
    /// <summary>For <see cref="AttachMode.Bone"/>: the attachment point name (<c>hands</c>, <c>hip</c>, <c>back</c>, <c>back2</c>).</summary>
    public string? Point { get; init; }
    /// <summary>Added to the attachment point's position, in the bone's frame: a carried crossbow's <c>equip offset</c> along z.</summary>
    public Vector3 PointOffset { get; init; }
    /// <summary>The part's own placement in the item it belongs to, before the attachment point's (a loaded bolt in its crossbow); null: none.</summary>
    public Matrix4x4? Local { get; init; }
    /// <summary>
    /// When the part shows by the bolts loaded in its crossbow: 0 always; n &gt; 0 while at least n are (the loaded string with 1, bolt n - 1); -1 while none is (the unloaded
    /// string).
    /// </summary>
    public int ShownShots { get; init; }
    /// <summary>The material the item was made with, if chosen (MATERIAL_SPECS_CLOTHING of clothing, MATERIAL_SPECS_WEAPON model of a weapon).</summary>
    public GameRecord? Material { get; init; }
    /// <summary>The COLOR_DATA a worn item is dyed with (<see cref="Gameplay.ItemColours.Shown"/>); null: undyed.</summary>
    public GameRecord? Colour { get; init; }
    /// <summary>When the part shows: always, or only while the weapon in hand is sheathed or drawn (the first weapon's meshes).</summary>
    public WeaponShown Shown { get; init; }
    /// <summary>
    /// Boots over one prosthetic leg (<c>AppearanceBase_SetupItemMaterial</c>, <c>COLLAPSE_SIDE</c>): 1 collapses the mesh's x &gt;= 0 half (the left leg's), 2 its x &lt; 0 half;
    /// 0 none.
    /// </summary>
    public int HideSide { get; init; }
}

/// <summary>
/// When a weapon part shows: always; while the hip melee weapon is sheathed (also while the crossbow or the back weapon is out) or drawn; while the crossbow is in the
/// hands (shooting) or not (on the back); the same for the back melee weapon (which of the two is drawn goes by the arms, docs/game/combat.md "Arms").
/// </summary>
public enum WeaponShown : byte { Always, Sheathed, Drawn, RangedSheathed, RangedDrawn, BackSheathed, BackDrawn }

/// <summary>A clothing texture layered onto the body (ARMOUR <c>vest texture</c>; character.hlsl "vest"), dyed with <paramref name="Colour"/> as the item's meshes are.</summary>
public sealed record BodyLayer(GameRecord Record, string Diffuse, string? Normal, string? ColourMap, GameRecord? Colour = null);

/// <summary>Where a bone-attached part sits: a bone and an offset from it in the bone's binding frame.</summary>
public sealed record AttachmentPoint(string Name, string Bone, Vector3 Position, Quaternion Rotation);

/// <summary>Options for <see cref="CharacterAppearance.Build"/>.</summary>
public sealed record CharacterOptions
{
    /// <summary>Force the gender; null = from the body file, else CHARACTER <c>female chance</c> ≥ 50.</summary>
    public bool? Female { get; init; }
    /// <summary>Extra items (record name or string id): ARMOUR / ATTACHMENT / CONTAINER worn, WEAPON / CROSSBOW attached.</summary>
    public IReadOnlyList<string> Equip { get; init; } = [];
    /// <summary>Skip the CHARACTER's own clothing and weapons.</summary>
    public bool Naked { get; init; }
    /// <summary>Draw the first weapon: <c>bare sword</c> in the hand and <c>sheath</c> at the hip, instead of the sheathed <c>mesh</c>.</summary>
    public bool WeaponDrawn { get; init; }
    /// <summary>Null: always the likeliest choice; otherwise <see cref="CharacterGenerator"/> rolls a <see cref="Loadout"/> with this seed.</summary>
    public int? Seed { get; init; }
    /// <summary>A rolled character (race, gender, appearance, clothing, weapons); overrides <see cref="Seed"/>.</summary>
    public Loadout? Loadout { get; init; }
    /// <summary>With <see cref="Seed"/>: the FACTION (name or string id) the character spawns in; default its own <c>faction</c>.</summary>
    public string? Faction { get; init; }
    /// <summary>With <see cref="Seed"/>: the squad template's <c>race override</c> (RACE references, val0 the weight), which wins over the CHARACTER's <c>race</c> list when it has any.</summary>
    public IReadOnlyList<GameReference>? RaceOverride { get; init; }
}

/// <summary>
/// The parts and looks of a character, assembled from FCS records the way docs/characters.md describes (body, head,
/// hair, clothing, weapons, colours, face poses). Resolution only; meshes and textures are left to the renderer.
/// </summary>
public sealed class CharacterAppearance
{
    public GameRecord? Character { get; private init; }
    /// <summary>The ANIMAL_CHARACTER this animal was built from (<see cref="BuildAnimal"/>); null for people.</summary>
    public GameRecord? Animal { get; private init; }
    public required GameRecord Race { get; init; }
    public bool Female { get; private init; }
    public AppearanceFile? Body { get; private init; }
    public string? BodyFile { get; private init; }
    /// <summary>The rolled character this was built from (with <see cref="CharacterOptions.Seed"/> or <see cref="CharacterOptions.Loadout"/>).</summary>
    public Loadout? Loadout { get; private init; }
    /// <summary>The FACTION it was made in (the generator's, else the CHARACTER's own <c>faction</c>): its <c>color</c> is the uniform scheme.</summary>
    public GameRecord? Faction { get; private init; }

    /// <summary>Race <c>male mesh</c> / <c>female mesh</c>, or CHARACTER <c>mesh</c>.</summary>
    public string BodyMesh { get; private init; } = "";
    public string? BodyTexture { get; private init; }
    public string? BodyNormal { get; private init; }
    /// <summary>Race <c>nm male strong</c> / <c>nm male skinny</c> (female: <c>nm female ...</c>): the body normal maps a well-defined or a starving body blends towards.</summary>
    public string? BodyNormalStrong { get; private init; }
    public string? BodyNormalSkinny { get; private init; }
    public string? BodyMask { get; private init; }
    /// <summary>Race <c>part map male</c> / <c>part map female</c>: the image whose colours mark the body regions worn items' <c>hide parts</c> hide (<see cref="HiddenParts"/>).</summary>
    public string? PartMap { get; private init; }
    public GameRecord? Head { get; private init; }
    public GameRecord? Hair { get; private set; }
    /// <summary>A fixed body shape for a staged character (the Meitou title's wanderer), used instead of <see cref="CharacterShape.InputFor"/>; null in play.</summary>
    public CharacterShapeInput? Shape { get; set; }
    public GameRecord? Beard { get; private set; }

    /// <summary>Linear RGB multiplier the body shader gets as <c>skintone</c>: 1 − <c>Skin Tone</c> (white = no change).</summary>
    public Vector3 SkinToneParameter { get; private init; } = Vector3.Zero;
    /// <summary>Hair colour (RGB 0–1) from the body file's HSB sliders, or the race's likeliest hair colour.</summary>
    public Vector3 HairColour { get; private init; } = new(0.3f, 0.22f, 0.15f);

    /// <summary>Visible meshes besides the body, in attach order.</summary>
    public List<CharacterPart> Parts { get; } = [];
    public List<BodyLayer> BodyLayers { get; } = [];
    /// <summary>The lanterns it carries (<see cref="CarriedLights"/>): worn clothing, backpacks and belt items, or an animal's backpack.</summary>
    public List<CarriedLight> Lights { get; } = [];
    /// <summary>Attachment points by name: <c>hands</c> (Bip01 Prop2) plus the race's <c>attachment points</c> file.</summary>
    public Dictionary<string, AttachmentPoint> AttachmentPoints { get; } = new(StringComparer.Ordinal);
    /// <summary>Pose weights from the body file, keyed by pose name (only non-zero values).</summary>
    public Dictionary<string, float> PoseWeights { get; } = new(StringComparer.Ordinal);
    public List<string> Notes { get; } = [];

    /// <summary>Finds a record by string id, else by exact name (case-insensitive; the shown name or the data files' English one) among <paramref name="types"/>.</summary>
    public static GameRecord? Find(GameDatabase db, string nameOrId, params FcsRecordType[] types)
    {
        if (db.Find(nameOrId) is { } byId && (types.Length == 0 || types.Contains(byId.Type))) return byId;
        return db.Records.Values.Where(r => (types.Length == 0 || types.Contains(r.Type))
                && (string.Equals(r.Name.Trim(), nameOrId.Trim(), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(r.BaseName.Trim(), nameOrId.Trim(), StringComparison.OrdinalIgnoreCase)))
            .OrderBy(r => Array.IndexOf(types, r.Type)).FirstOrDefault();
    }

    /// <summary>
    /// Builds a character from a CHARACTER or RACE record (name or string id). <paramref name="installRoot"/> resolves the
    /// <c>.\data\...</c> paths of body and physics files.
    /// </summary>
    public static CharacterAppearance Build(GameDatabase db, string installRoot, string nameOrId, CharacterOptions? options = null)
    {
        options ??= new CharacterOptions();
        var record = Find(db, nameOrId, FcsRecordType.CHARACTER, FcsRecordType.RACE, FcsRecordType.ANIMAL_CHARACTER)
            ?? throw new KeyNotFoundException($"No CHARACTER, RACE or ANIMAL_CHARACTER record '{nameOrId}'.");
        if (record.Type == FcsRecordType.ANIMAL_CHARACTER) return BuildAnimal(db, installRoot, record);
        // With a seed the generator rolls everything (docs/characters.md, "Generating a character").
        var faction = options.Faction is { } factionName ? Find(db, factionName, FcsRecordType.FACTION) : null;
        var loadout = options.Loadout ?? (options.Seed is { } seed ? CharacterGenerator.Generate(db, installRoot, record, seed, options.Female, faction, options.RaceOverride) : null);
        Random? random = null;
        var notes = new List<string>();
        GameRecord? character = record.Type == FcsRecordType.CHARACTER ? record : null;
        faction ??= loadout?.Faction ?? character?.GetReferences("faction").Select(r => db.Find(r.TargetStringId)).FirstOrDefault(f => f is { Type: FcsRecordType.FACTION });

        // Body file (.bod2). CHARACTER "body": empty = random body.
        AppearanceFile? body = loadout?.Appearance;
        string? bodyFile = character?.GetPath("body") is { Length: > 0 } b ? b : null;
        if (loadout is not null)
        {
            if (loadout.Generated) bodyFile = null;
            notes.AddRange(loadout.Notes);
        }
        else if (bodyFile is not null)
        {
            var path = CharacterGenerator.Resolve(installRoot, bodyFile);
            try { body = AppearanceFile.Read(path); }
            catch (Exception e) when (e is IOException or FcsFormatException or UnauthorizedAccessException)
            {
                notes.Add($"body file {bodyFile}: {e.Message}");
            }
        }

        // Race: CHARACTER "race" overrides the body file's (fcs.def); RACE records are their own race.
        GameRecord? race = loadout?.Race ?? (record.Type == FcsRecordType.RACE ? record : null);
        race ??= Pick(db, character!.GetReferences("race"), random, r => r.Values.Value0);
        if (race is null && body?.RaceId is { } bodyRace) race = db.Find(bodyRace);
        race ??= Find(db, "Greenlander", FcsRecordType.RACE);
        if (race is null) throw new KeyNotFoundException($"'{record.Name}' has no race.");

        bool female = options.Female ?? loadout?.Female ?? body?.Female ?? (character is not null && character.GetInt("female chance") >= 50);
        if (female && race.GetBool("single gender")) { female = false; notes.Add("race is single gender: male mesh used"); }
        string g = female ? "female" : "male";

        string bodyMesh = character?.GetPath("mesh") is { Length: > 0 } own ? own : race.GetPath($"{g} mesh");
        var head = (body?.HeadId is { } hid ? db.Find(hid) : null)
            ?? Pick(db, race.GetReferences($"heads {g}"), random, r => Math.Max(r.Values.Value0, 1));

        var result = new CharacterAppearance
        {
            Character = character, Race = race, Female = female, Body = body, BodyFile = bodyFile, Loadout = loadout,
            Faction = faction,
            BodyMesh = bodyMesh,
            BodyTexture = FilePath(race, $"body texture {g}"), BodyNormal = FilePath(race, $"nm {g}"),
            BodyNormalStrong = FilePath(race, $"nm {g} strong"), BodyNormalSkinny = FilePath(race, $"nm {g} skinny"),
            BodyMask = FilePath(race, $"body mask {g}"), PartMap = FilePath(race, $"part map {g}"),
            Head = head,
            SkinToneParameter = body?.SkinTone is { } tone ? Vector3.One - tone : Vector3.Zero,
            HairColour = HairColourOf(db, race, body, random),
        };
        result.Notes.AddRange(notes);
        if (body is not null)
            foreach (var (key, value) in body.Floats)
                if (value != 0) result.PoseWeights[key] = value;

        // Equipment: CHARACTER lists (unless naked) plus --equip, then hair/beard unless a worn item hides them.
        var items = new List<GameRecord>();
        var materials = new Dictionary<GameRecord, GameRecord>();
        // A loadout item's colour is its instance's (null: none, the scheme applies); any other item is new, coloured by its record.
        var colours = new Dictionary<GameRecord, GameRecord?>();
        if (loadout is not null && !options.Naked)
        {
            foreach (var c in loadout.Clothing)
            {
                items.Add(c.Record);
                if (c.Material is { } m) materials[c.Record] = m;
                colours[c.Record] = c.Colour;
            }
            if (loadout.Backpack is { } pack) { items.Add(pack.Record); colours[pack.Record] = pack.Colour; }
        }
        else if (character is not null && !options.Naked)
        {
            items.AddRange(ChooseClothing(db, character, random));
            // backpack: (1, chance), val1 an absolute chance (fcs.def). Without a seed: worn if the chance is 50 or more.
            foreach (var r in character.GetReferences("backpack"))
                if (db.Find(r.TargetStringId) is { } pack && (random is null ? r.Values.Value1 >= 50 : random.Next(100) < r.Values.Value1))
                {
                    items.Add(pack);
                    break;
                }
        }
        foreach (var name in options.Equip)
        {
            var item = Find(db, name, FcsRecordType.ARMOUR, FcsRecordType.WEAPON, FcsRecordType.CROSSBOW, FcsRecordType.ATTACHMENT,
                FcsRecordType.CONTAINER, FcsRecordType.LIMB_REPLACEMENT, FcsRecordType.ITEM);
            if (item is null) result.Notes.Add($"--equip '{name}': no such item");
            else items.Add(item);
        }
        foreach (var natural in race.GetReferences("natural armour").Select(r => db.Find(r.TargetStringId)))
            if (natural is not null) items.Add(natural);

        // An ARMOUR in a slot replaces an earlier one in the same slot (--equip comes last, so it wins).
        var worn = new List<GameRecord>();
        foreach (var item in items.Where(i => i.Type == FcsRecordType.ARMOUR))
        {
            worn.RemoveAll(w => w.GetInt("slot", 5) == item.GetInt("slot", 5));
            worn.Add(item);
        }
        // A cut-off head (Meitou decapitation) takes its hat along; the hat stays in the inventory.
        bool headless = loadout?.Decapitated == true;
        result.Headless = headless;
        if (headless) worn.RemoveAll(w => (AttachSlot)w.GetInt("slot", 5) == AttachSlot.Hat);
        // fcs.def: "hide hair" (default true) is for hats only.
        bool hideHair = worn.Any(w => (AttachSlot)w.GetInt("slot", 5) == AttachSlot.Hat && w.GetBool("hide hair", true));
        bool hideBeard = worn.Any(w => w.GetBool("hide beard"));
        foreach (var w in worn) result.HiddenParts |= w.GetInt("hide parts");

        var hair = body?.HairId is { } hairId ? db.Find(hairId) : character is null || body is null ? PickHair(db, race, AttachSlot.Hair, random) : null;
        var beard = body?.BeardId is { } beardId ? db.Find(beardId) : null;
        if (character?.GetBool("shaved") == true) { hair = null; result.Notes.Add("shaved: no hair"); }
        result.Hair = hideHair || headless ? null : hair;
        result.Beard = hideBeard || headless ? null : beard;
        if (hideHair && hair is not null) result.Notes.Add($"hair '{hair.Name}' hidden by a hat");

        foreach (var attachment in new[] { result.Hair, result.Beard })
            if (attachment is not null) result.AddWorn(attachment, female, worn);
        GameRecord? Dye(GameRecord item) => Gameplay.ItemColours.Shown(db, item, colours.TryGetValue(item, out var own) ? own : Gameplay.ItemColours.Of(db, item), character, faction);
        foreach (var item in worn) result.AddWorn(item, female, worn, materials.GetValueOrDefault(item), Dye(item));
        foreach (var item in items.Where(i => i.Type is FcsRecordType.ATTACHMENT or FcsRecordType.CONTAINER))
            result.AddWorn(item, female, worn, colour: Dye(item));
        foreach (var item in items.Where(i => i.Type == FcsRecordType.LIMB_REPLACEMENT))
            result.AddLimb(item, female);
        if (loadout is not null)
        {
            result.AddLimbs(db, race, female, loadout.Limbs);
            // Boots (not `is locked`) over exactly one prosthetic leg lose that leg's half.
            bool left = loadout.Limbs[2]?.State == 2, right = loadout.Limbs[3]?.State == 2;
            if (left != right)
                for (int i = 0; i < result.Parts.Count; i++)
                    if (result.Parts[i] is { Slot: AttachSlot.Boots } boots && boots.Record.Type == FcsRecordType.ARMOUR && !boots.Record.GetBool("is locked"))
                        result.Parts[i] = boots with { HideSide = left ? 1 : 2 };
        }

        // Weapons: the loadout's, else the likeliest: the first entry with quantity > 0 of the hip pool (val1 = 0) and of
        // the back pool (val1 ≠ 0), whose absolute chances make the first one win (docs/characters.md). Then --equip weapons.
        var weapons = new List<(GameRecord Weapon, string Point, GameRecord? Model)>();
        if (loadout is not null && !options.Naked)
        {
            weapons.AddRange(loadout.Weapons.Select(w => (w.Weapon, w.Point, w.Model)));
            if (loadout.Crossbow is { } bow) weapons.Add((bow.Weapon, bow.Point, bow.Model));
        }
        else if (character is not null && !options.Naked)
        {
            var listed = character.GetReferences("weapons").Where(r => r.Values.Value0 > 0)
                .Select(r => (Ref: r, Item: db.Find(r.TargetStringId))).Where(w => w.Item is not null).ToList();
            foreach (var back in new[] { false, true })
                if (listed.FirstOrDefault(w => (w.Ref.Values.Value1 != 0) == back).Item is { } weapon)
                    weapons.Add((weapon, back ? "back" : "hip", null));
        }
        foreach (var extra in items.Where(i => i.Type is FcsRecordType.WEAPON or FcsRecordType.CROSSBOW or FcsRecordType.ITEM))
        {
            string point = weapons.All(w => w.Point != "hip") ? "hip" : weapons.All(w => w.Point != "back") ? "back" : "back2";
            weapons.Add((extra, point, null));
        }
        result.LoadAttachmentPoints(installRoot, race);
        // A fight draws the first WEAPON of the hip or of the back, by the arms (CharacterAssembly.MakeFighter, Fighter.Weapon): each gets its sheathed and drawn meshes, the
        // renderer shows one set.
        int hipInHand = weapons.FindIndex(w => w.Weapon.Type == FcsRecordType.WEAPON && w.Point == "hip"), backInHand = weapons.FindIndex(w => w.Weapon.Type == FcsRecordType.WEAPON && w.Point == "back");
        int bowInHand = weapons.FindIndex(w => w.Weapon.Type == FcsRecordType.CROSSBOW);
        for (int i = 0; i < weapons.Count; i++)
            if (weapons[i].Weapon.Type == FcsRecordType.CROSSBOW) result.AddCrossbow(weapons[i].Weapon, weapons[i].Point, drawable: i == bowInHand, weapons[i].Model);
            else result.AddWeapon(weapons[i].Weapon, female, weapons[i].Point, drawn: options.WeaponDrawn && i == 0, backCount: 0, weapons[i].Model,
                both: !options.WeaponDrawn && (i == hipInHand || i == backInHand), back: i == backInHand);
        var lightRoll = new Random(loadout?.Seed ?? options.Seed ?? 0);
        foreach (var item in worn.Concat(items.Where(i => i.Type is FcsRecordType.ATTACHMENT or FcsRecordType.CONTAINER)).Concat(loadout?.Carried.Select(c => c.Record) ?? []))
            if (CarriedLights.From(db, installRoot, item, female, lightRoll) is { } carried) result.Lights.Add(carried);
        return result;
    }

    /// <summary>
    /// An animal's look (docs/game/animals.md "Appearance", <b>Verified</b> against the records): the ANIMAL_CHARACTER names no mesh or texture; its race does, as a
    /// person's: <c>male mesh</c> (every animal race names the same file for both genders, so the male one is used), <c>body texture male</c>, <c>nm male</c>, and the fur
    /// as its <c>hairs</c> ATTACHMENT (a mesh on the body's skeleton, coloured by the race's <c>hair colors</c>). No head, no body file, no clothing, no visible weapon (the
    /// natural weapon has no mesh). Nothing is rolled: every animal of a record looks the same, so callers can share one instance.
    /// </summary>
    public static CharacterAppearance BuildAnimal(GameDatabase db, string installRoot, GameRecord animal)
    {
        var race = animal.GetReferences("race").Where(r => r.Values.Value0 > 0).Select(r => db.Find(r.TargetStringId))
            .FirstOrDefault(r => r is { Type: FcsRecordType.RACE }) ?? throw new KeyNotFoundException($"'{animal.Name}' has no race.");
        var result = new CharacterAppearance
        {
            Animal = animal, Race = race, Female = false,
            BodyMesh = race.GetPath("male mesh"),
            BodyTexture = FilePath(race, "body texture male"), BodyNormal = FilePath(race, "nm male"),
            HairColour = HairColourOf(db, race, null, null),
        };
        result.Hair = PickHair(db, race, AttachSlot.Hair, null);
        if (result.Hair is not null) result.AddWorn(result.Hair, false, []);
        result.LoadAttachmentPoints(installRoot, race);
        // The record's backpack (a pack beast's luggage) carries a lantern; only its light is shown (the backpack mesh is not drawn on animals yet).
        foreach (var pack in animal.GetReferences("backpack").Where(r => r.Values.Value0 > 0).Select(r => db.Find(r.TargetStringId)))
            if (pack is not null && CarriedLights.From(db, installRoot, pack, false, new Random(0)) is { } carried) result.Lights.Add(carried);
        return result;
    }

    /// <summary>
    /// The limbs whose natural body vertices are hidden, bit k for limb slot k (docs/characters.md "Hidden body parts": the original's <c>hiddenMask</c> bits 12 to 15, set for
    /// every limb that is not in its natural state and for every worn LIMB_REPLACEMENT, <c>uses_hide_parts</c>).
    /// </summary>
    public int HiddenLimbs { get; private set; }

    /// <summary>The head was cut off (<see cref="Loadout.Decapitated"/>): the body's head vertices are hidden and its hat, hair and beard left out.</summary>
    public bool Headless { get; private set; }

    /// <summary>A cut-off head (<see cref="SeveredHead"/>): only the body's head vertices are drawn, with the hair, beard and hat.</summary>
    public bool HeadOnly { get; private set; }

    /// <summary>The head of this look as it falls when cut off (Meitou decapitation): the body (drawn head only, <see cref="HeadOnly"/>) with its hair, beard and hat.</summary>
    public CharacterAppearance SeveredHead()
    {
        var copy = new CharacterAppearance
        {
            Character = Character, Animal = Animal, Race = Race, Female = Female, Body = Body, BodyFile = BodyFile, Loadout = Loadout, Faction = Faction,
            BodyMesh = BodyMesh, BodyTexture = BodyTexture, BodyNormal = BodyNormal, BodyNormalStrong = BodyNormalStrong, BodyNormalSkinny = BodyNormalSkinny, BodyMask = BodyMask, PartMap = PartMap, Head = Head, Hair = Hair,
            Shape = Shape, Beard = Beard, SkinToneParameter = SkinToneParameter, HairColour = HairColour, HeadOnly = true,
        };
        copy.Parts.AddRange(Parts.Where(p => p.Record == Hair || p.Record == Beard || p.Slot == AttachSlot.Hat));
        copy.BodyLayers.AddRange(BodyLayers);
        foreach (var (name, weight) in PoseWeights) copy.PoseWeights[name] = weight;
        return copy;
    }

    /// <summary>The part-map regions the worn ARMOUR hides: the OR of their <c>hide parts</c> (bit per part-map colour, docs/characters.md "Hidden body parts").</summary>
    public int HiddenParts { get; private set; }

    /// <summary>
    /// The non-natural limbs of <paramref name="limbs"/> (<c>FUN_1400d0190</c>, <c>uses_limb_replacement</c>): a stump shows the race's <c>limb replacement</c> entry whose
    /// <c>slot</c> is 50 + k, a replaced limb its prosthetic (a stump's without one); a crushed limb gets no mesh (the original attaches none for state 3) but stays hidden.
    /// </summary>
    void AddLimbs(GameDatabase db, GameRecord race, bool female, IReadOnlyList<LoadoutLimb?> limbs)
    {
        for (int k = 0; k < limbs.Count; k++)
        {
            if (limbs[k] is not { State: > 0 } limb) continue;
            HiddenLimbs |= 1 << k;
            if (limb.State == 3) continue;
            var record = limb.State == 2 ? limb.Prosthetic : null;
            record ??= race.GetReferences("limb replacement").Select(r => db.Find(r.TargetStringId))
                .FirstOrDefault(r => r is { Type: FcsRecordType.LIMB_REPLACEMENT } && r.GetInt("slot") == 50 + k);
            if (record is not null) AddLimb(record, female);
        }
    }

    /// <summary>A LIMB_REPLACEMENT mesh on the body's skeleton (<c>mesh</c> / <c>mesh female</c>); hides its slot's natural limb.</summary>
    void AddLimb(GameRecord limb, bool female)
    {
        if (limb.GetInt("slot") - 50 is >= 0 and < 4 and var k) HiddenLimbs |= 1 << k;
        string field = female && limb.GetPath("mesh female").Length > 0 ? "mesh female" : "mesh";
        if (limb.GetPath(field) is { Length: > 0 } mesh)
            Parts.Add(new CharacterPart { Record = limb, Mesh = mesh, Field = field, Mode = AttachMode.SharedSkeleton });
    }

    void AddWorn(GameRecord item, bool female, List<GameRecord> worn, GameRecord? material = null, GameRecord? colour = null)
    {
        AttachSlot? slot = item.Type == FcsRecordType.ATTACHMENT ? (AttachSlot)item.GetInt("attach slot", (int)AttachSlot.Hair)
            : item.Ints.ContainsKey("slot") ? (AttachSlot)item.GetInt("slot") : null;
        // Mesh: by gender, no fallback for shared-skeleton items; "overlap mesh" when an "overlap items" item is worn too.
        string field = female ? "mesh female" : "mesh";
        string mesh = item.GetPath(field);
        if (item.Type == FcsRecordType.ARMOUR)
        {
            var overlaps = item.GetReferences("overlap items").Select(r => r.TargetStringId).ToHashSet(StringComparer.Ordinal);
            string overlapField = female ? "overlap mesh female" : "overlap mesh";
            if (worn.Any(w => overlaps.Contains(w.StringId)) && item.GetPath(overlapField) is { Length: > 0 } overlap)
            {
                mesh = overlap;
                field = overlapField;
            }
            string vest = female ? " female" : "";
            if (item.GetPath("vest texture" + vest) is { Length: > 0 } diffuse)
                BodyLayers.Add(new BodyLayer(item, diffuse, FilePath(item, "vest normalmap" + vest), FilePath(item, "vest colormap" + vest), colour));
        }
        if (mesh.Length == 0)
        {
            if (female && item.GetPath("mesh").Length > 0) Notes.Add($"[Appearance] No female mesh for '{item.Name}'");
            return;
        }
        Parts.Add(new CharacterPart { Record = item, Mesh = mesh, Field = field, Mode = AttachMode.SharedSkeleton, Slot = slot, Material = material, Colour = colour });
    }

    /// <param name="both">Both sets: the sheathed mesh tagged <see cref="WeaponShown.Sheathed"/>, the drawn ones <see cref="WeaponShown.Drawn"/> (a renderer that follows the fight);
    /// with <paramref name="back"/> <see cref="WeaponShown.BackSheathed"/> and <see cref="WeaponShown.BackDrawn"/>.</param>
    void AddWeapon(GameRecord weapon, bool female, string slot, bool drawn, int backCount, GameRecord? model = null, bool both = false, bool back = false)
    {
        if (slot == "back" && backCount > 0) slot = "back2";
        string Field(string f) => f == "mesh" && female && weapon.GetPath("mesh female").Length > 0 ? "mesh female" : f;
        bool bare = weapon.GetPath("bare sword").Length > 0;
        if ((drawn || both) && bare)
        {
            var shown = !both ? WeaponShown.Always : back ? WeaponShown.BackDrawn : WeaponShown.Drawn;
            Parts.Add(new CharacterPart { Record = weapon, Mesh = weapon.GetPath("bare sword"), Field = "bare sword", Mode = AttachMode.Bone, Slot = AttachSlot.Weapon, Point = "hands", Material = model, Shown = shown });
            if (weapon.GetPath("sheath") is { Length: > 0 } sheath)
                Parts.Add(new CharacterPart { Record = weapon, Mesh = sheath, Field = "sheath", Mode = AttachMode.Bone, Slot = AttachSlot.Weapon, Point = slot, Material = model, Shown = shown });
            if (!both) return;
        }
        // Mode-0 items fall back from "mesh female" to "mesh" (ogre-skeleton.md).
        string field = Field("mesh");
        if (weapon.GetPath(field) is not { Length: > 0 } mesh) { Notes.Add($"'{weapon.Name}' has no mesh"); return; }
        Parts.Add(new CharacterPart { Record = weapon, Mesh = mesh, Field = field, Mode = AttachMode.Bone, Slot = AttachSlot.Weapon, Point = slot, Material = model,
            Shown = !(both && bare) ? WeaponShown.Always : back ? WeaponShown.BackSheathed : WeaponShown.Sheathed });
    }

    /// <summary>
    /// A crossbow (docs/characters.md "Crossbows", <b>Observed</b>): its <c>mesh</c>, <c>extra mesh loaded</c> / <c>extra mesh unloaded</c> and a <c>live ammo</c> bolt per shot
    /// (by <see cref="CharacterPart.ShownShots"/>) at <paramref name="slot"/>, pushed out by its <c>equip offset</c> (default 1.2) along the point's z, and, for the one a fight
    /// draws (<paramref name="drawable"/>), the same in the <c>hands</c> point without the offset while shooting (<see cref="WeaponShown.RangedDrawn"/>): the original moves the
    /// whole crossbow there.
    /// </summary>
    void AddCrossbow(GameRecord bow, string slot, bool drawable, GameRecord? model = null)
    {
        if (bow.GetPath("mesh") is not { Length: > 0 } mesh) { Notes.Add($"'{bow.Name}' has no mesh"); return; }
        var offset = new Vector3(0, 0, bow.GetFloat("equip offset", 1.2f));
        string loaded = bow.GetPath("extra mesh loaded"), unloaded = bow.GetPath("extra mesh unloaded");
        string bolt = bow.GetPath("live ammo");
        // A loaded bolt sits at the barrel (barrel pos Y, barrel pos Z, 0 in the crossbow's frame), turned -90 degrees about x, then about z.
        var boltAt = Matrix4x4.CreateRotationZ(-MathF.PI / 2) * Matrix4x4.CreateRotationX(-MathF.PI / 2)
            * Matrix4x4.CreateTranslation(bow.GetFloat("barrel pos Y", 0), bow.GetFloat("barrel pos Z", 0), 0);
        int shots = Math.Max(bow.GetInt("num shots", 1), 1);
        foreach (bool hands in drawable ? new[] { false, true } : [false])
        {
            var shown = !drawable ? WeaponShown.Always : hands ? WeaponShown.RangedDrawn : WeaponShown.RangedSheathed;
            var point = hands ? "hands" : slot;
            var at = hands ? Vector3.Zero : offset;
            CharacterPart Part(string file, string field, int whenShots, Matrix4x4? local = null) =>
                new() { Record = bow, Mesh = file, Field = field, Mode = AttachMode.Bone, Slot = AttachSlot.Weapon, Point = point, PointOffset = at, Shown = shown, ShownShots = whenShots, Local = local, Material = model };
            Parts.Add(Part(mesh, "mesh", 0));
            if (loaded.Length > 0) Parts.Add(Part(loaded, "extra mesh loaded", 1));
            if (unloaded.Length > 0) Parts.Add(Part(unloaded, "extra mesh unloaded", -1));
            if (bolt.Length > 0)
                for (int i = 0; i < shots; i++) Parts.Add(Part(bolt, "live ammo", i + 1, boltAt));
        }
    }

    /// <summary>
    /// This look with <paramref name="item"/>'s <c>mesh</c> in the <c>hands</c> point, as the original attaches an operator's <c>special tool</c>
    /// (docs/game/buildings-production.md "Production"); this look itself when the item has no mesh.
    /// </summary>
    public CharacterAppearance WithHeld(GameRecord item)
    {
        if (item.GetPath("mesh") is not { Length: > 0 } mesh) return this;
        var copy = new CharacterAppearance
        {
            Character = Character, Animal = Animal, Race = Race, Female = Female, Body = Body, BodyFile = BodyFile, Loadout = Loadout, Faction = Faction,
            BodyMesh = BodyMesh, BodyTexture = BodyTexture, BodyNormal = BodyNormal, BodyNormalStrong = BodyNormalStrong, BodyNormalSkinny = BodyNormalSkinny, BodyMask = BodyMask, PartMap = PartMap, Head = Head, Hair = Hair,
            Shape = Shape, Beard = Beard, SkinToneParameter = SkinToneParameter, HairColour = HairColour, HiddenLimbs = HiddenLimbs, HiddenParts = HiddenParts, Headless = Headless,
        };
        copy.Parts.AddRange(Parts);
        copy.Parts.Add(new CharacterPart { Record = item, Mesh = mesh, Field = "mesh", Mode = AttachMode.Bone, Slot = AttachSlot.Weapon, Point = "hands" });
        copy.BodyLayers.AddRange(BodyLayers);
        copy.Lights.AddRange(Lights);
        foreach (var (name, point) in AttachmentPoints) copy.AttachmentPoints[name] = point;
        foreach (var (name, weight) in PoseWeights) copy.PoseWeights[name] = weight;
        return copy;
    }

    void LoadAttachmentPoints(string installRoot, GameRecord race)
    {
        AttachmentPoints["hands"] = new AttachmentPoint("hands", "Bip01 Prop2", Vector3.Zero, Quaternion.Identity);
        if (race.GetPath("attachment points") is not { Length: > 0 } file) return;
        var path = CharacterGenerator.Resolve(installRoot, file);
        try
        {
            var phs = PhysicsAttachmentFile.ReadFile(path);
            foreach (var actor in phs.Actors)
                if (actor.Bone is not null)
                    foreach (var point in actor.Points)
                    {
                        var (position, rotation) = actor.Offset(point);
                        AttachmentPoints[actor.Name] = new AttachmentPoint(actor.Name, actor.Bone, position, rotation);
                    }
        }
        catch (Exception e) when (e is IOException or FormatException or NotSupportedException or UnauthorizedAccessException)
        {
            Notes.Add($"attachment points {file}: {e.Message}");
        }
    }

    /// <summary>
    /// Clothing per ARMOUR slot: entries are (quantity, chance); a negative quantity is a "no item" option. Without a
    /// seed the likeliest entry of each slot wins (ties: the first listed).
    /// </summary>
    static IEnumerable<GameRecord> ChooseClothing(GameDatabase db, GameRecord character, Random? random)
    {
        var bySlot = new OrderedDictionary<int, List<(GameRecord? Item, int Chance)>>();
        foreach (var r in character.GetReferences("clothing"))
        {
            var item = db.Find(r.TargetStringId);
            if (item is null || r.Values.Value0 == 0 || r.Values.Value1 <= 0) continue; // quantity 0 or no chance: never chosen
            int slot = item.GetInt("slot", 5);
            if (!bySlot.TryGetValue(slot, out var list)) bySlot[slot] = list = [];
            list.Add((r.Values.Value0 < 0 ? null : item, Math.Max(r.Values.Value1, 0)));
        }
        foreach (var (_, list) in bySlot)
        {
            var chosen = random is null ? list.MaxBy(e => e.Chance).Item : Weighted(list, e => e.Chance, random).Item;
            if (chosen is not null) yield return chosen;
        }
    }

    static GameRecord? PickHair(GameDatabase db, GameRecord race, AttachSlot slot, Random? random)
    {
        var options = race.GetReferences("hairs").Select(r => (Item: db.Find(r.TargetStringId), Weight: r.Values.Value0))
            .Where(o => o.Item is not null && (AttachSlot)o.Item.GetInt("attach slot", (int)AttachSlot.Hair) == slot && o.Item.GetPath("mesh").Length > 0)
            .ToList();
        if (options.Count == 0) return null;
        return random is null ? options.MaxBy(o => o.Weight).Item : Weighted(options, o => o.Weight, random).Item;
    }

    static GameRecord? Pick(GameDatabase db, IReadOnlyList<GameReference> refs, Random? random, Func<GameReference, int> weight)
    {
        var options = refs.Select(r => (Ref: r, Item: db.Find(r.TargetStringId))).Where(o => o.Item is not null).ToList();
        if (options.Count == 0) return null;
        return random is null ? options[0].Item : Weighted(options, o => weight(o.Ref), random).Item;
    }

    static T Weighted<T>(List<T> options, Func<T, int> weight, Random random)
    {
        int total = options.Sum(o => Math.Max(weight(o), 0));
        if (total <= 0) return options[random.Next(options.Count)];
        int roll = random.Next(total);
        foreach (var o in options)
        {
            roll -= Math.Max(weight(o), 0);
            if (roll < 0) return o;
        }
        return options[^1];
    }

    /// <summary>
    /// Hair colour: body file sliders <c>Hair Colour</c>, <c>Hair Saturation</c>, <c>Hair Brightness</c> (each × 0.01) as
    /// hue, saturation, brightness (Ogre's ColourValue::setHSB). Without a body file: the race's likeliest
    /// <c>hair colors</c> COLOR_DATA <c>color 1</c> (viewer choice; the game's randomiser was not traced).
    /// </summary>
    static Vector3 HairColourOf(GameDatabase db, GameRecord race, AppearanceFile? body, Random? random)
    {
        if (body is not null && body.Floats.ContainsKey("Hair Colour"))
            return FromHsb(body.Get("Hair Colour") * 0.01f, body.Get("Hair Saturation") * 0.01f, body.Get("Hair Brightness") * 0.01f);
        var colours = race.GetReferences("hair colors").Select(r => (Item: db.Find(r.TargetStringId), Weight: r.Values.Value0)).Where(c => c.Item is not null).ToList();
        if (colours.Count == 0) return new Vector3(0.3f, 0.22f, 0.15f);
        var chosen = random is null ? colours.MaxBy(c => c.Weight).Item! : Weighted(colours, c => c.Weight, random).Item!;
        return Rgb(chosen.GetInt("color 1"));
    }

    /// <summary>An FCS colour int (0xRRGGBB, alpha ignored) as RGB 0–1.</summary>
    public static Vector3 Rgb(int colour) => new(((colour >> 16) & 0xFF) / 255f, ((colour >> 8) & 0xFF) / 255f, (colour & 0xFF) / 255f);

    /// <summary>HSB to RGB like Ogre's ColourValue::setHSB: hue wraps into 0–1, saturation and brightness are clamped to 0–1.</summary>
    public static Vector3 FromHsb(float hue, float saturation, float brightness)
    {
        hue -= MathF.Floor(hue);
        saturation = Math.Clamp(saturation, 0, 1);
        brightness = Math.Clamp(brightness, 0, 1);
        if (brightness == 0) return Vector3.Zero;
        if (saturation == 0) return new Vector3(brightness);
        float h = hue * 6;
        int sector = (int)MathF.Floor(h) % 6;
        float f = h - MathF.Floor(h);
        float p = brightness * (1 - saturation), q = brightness * (1 - saturation * f), t = brightness * (1 - saturation * (1 - f));
        return sector switch
        {
            0 => new Vector3(brightness, t, p),
            1 => new Vector3(q, brightness, p),
            2 => new Vector3(p, brightness, t),
            3 => new Vector3(p, q, brightness),
            4 => new Vector3(t, p, brightness),
            _ => new Vector3(brightness, p, q),
        };
    }

    static string? FilePath(GameRecord record, string field) => record.GetPath(field) is { Length: > 0 } p ? p : null;
}
