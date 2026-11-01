using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;

namespace Meitou.Data.Characters;

/// <summary>The tabs of the character editor (<c>Kenshi_CharacterEditor</c>: BODY, FACE, HAIR).</summary>
public enum EditorTab { Body, Face, Hair }

/// <summary>What an editor control edits (docs/characters.md "Character editor").</summary>
public enum EditorControlKind
{
    /// <summary>A body-file slider of the same name (<see cref="AppearanceRange"/> without a target).</summary>
    Slider,
    /// <summary>A face slider driving pose weights: value × 0.01 into the target, a negative value × −0.01 into the opposite target.</summary>
    Pose,
    /// <summary>A colour slider (<see cref="AppearanceColourRange"/>), stored as the blended RGB.</summary>
    Colour,
    /// <summary><c>Hair Colour</c>, <c>Hair Saturation</c>, <c>Hair Brightness</c> (HSB × 100).</summary>
    HairHue, HairSaturation, HairBrightness,
    /// <summary>Pickers: the HEAD (<c>head</c>), the hair and the beard ATTACHMENTs (<c>hair style</c>, <c>beard</c>).</summary>
    Head, HairStyle, Beard,
    /// <summary><c>idle stance</c>: a slider over <see cref="CharacterDraft.IdleStances"/>, 1-based.</summary>
    IdleStance,
}

/// <summary>One row of an editor tab: a slider from <see cref="Min"/> to <see cref="Max"/> or a picker (<see cref="IsPicker"/>).</summary>
public sealed record EditorControl(string Label, EditorControlKind Kind, int Min, int Max, int Mid)
{
    public AppearanceRange? Range { get; init; }
    public AppearanceColourRange? Colour { get; init; }
    public bool IsPicker => Kind is EditorControlKind.Head or EditorControlKind.HairStyle or EditorControlKind.Beard;
}

/// <summary>A playable race in the editor's lists: its RACE_GROUP (the RACE buttons) and the group's playable RACEs (the SUBRACE buttons), in data order.</summary>
public sealed record EditorRaceGroup(GameRecord Group, IReadOnlyList<GameRecord> Races);

/// <summary>
/// The look being edited in the character editor: a copy of a CHARACTER_APPEARANCE that the controls read and write (docs/characters.md "Character editor"). The
/// copy keeps every key of the record it was made from, so writing it back keeps the original's key set. Pure data: the simulation validates the result
/// (<see cref="Sanitize"/>) when the editor confirms.
/// </summary>
public sealed class CharacterDraft
{
    readonly GameDatabase db;
    readonly string installRoot;
    readonly Dictionary<(string, bool), AppearanceLimits?> limitsCache;

    /// <summary>The record as edited so far.</summary>
    public FcsRecord Record { get; private set; }
    public string Name { get; set; }
    public GameRecord Race { get; private set; }
    public bool Female => Record.Bools.TryGetValue("sex female", out var f) && f;
    public AppearanceLimits? Limits { get; private set; }
    /// <summary>Counts the changes, so a preview is rebuilt only when the look changed.</summary>
    public int Version { get; private set; }

    public CharacterDraft(GameDatabase db, string installRoot, FcsRecord appearance, string name)
    {
        this.db = db;
        this.installRoot = installRoot;
        Record = Copy(appearance);
        Name = name;
        limitsCache = [];
        Race = RaceOf(db, Record) ?? db.OfType(FcsRecordType.RACE).First();
        Limits = LimitsFor(Race, Female);
    }

    /// <summary>A deep copy of an appearance record (keys and their order kept).</summary>
    public static FcsRecord Copy(FcsRecord source)
    {
        var copy = new FcsRecord { Type = source.Type, Id = source.Id, Name = source.Name, StringId = source.StringId, Flags = source.Flags, ByteSize = source.ByteSize };
        foreach (var (k, v) in source.Bools) copy.Bools[k] = v;
        foreach (var (k, v) in source.Floats) copy.Floats[k] = v;
        foreach (var (k, v) in source.Ints) copy.Ints[k] = v;
        foreach (var (k, v) in source.Vector3s) copy.Vector3s[k] = v;
        foreach (var (k, v) in source.Vector4s) copy.Vector4s[k] = v;
        foreach (var (k, v) in source.Strings) copy.Strings[k] = v;
        foreach (var (k, v) in source.Filenames) copy.Filenames[k] = v;
        foreach (var (k, v) in source.References) copy.References[k] = [.. v];
        return copy;
    }

    /// <summary>The RACE an appearance record names, or null.</summary>
    public static GameRecord? RaceOf(GameDatabase db, FcsRecord record) =>
        record.References.TryGetValue("race", out var refs) && refs.Count > 0 && db.Find(refs[0].TargetStringId) is { Type: FcsRecordType.RACE } race ? race : null;

    /// <summary>
    /// IMPORT: takes a body file's whole look, race and sex included, made valid for its race (<see cref="Sanitize"/>); the draft keeps its own record id.
    /// False (nothing changes) when the file names no known race.
    /// </summary>
    public bool Import(FcsRecord appearance)
    {
        if (RaceOf(db, appearance) is not { } race) return false;
        var taken = Sanitize(db, installRoot, appearance, race);
        (taken.Type, taken.Id, taken.Name, taken.StringId, taken.Flags) = (Record.Type, Record.Id, Record.Name, Record.StringId, Record.Flags);
        Record = taken;
        Race = race;
        Limits = LimitsFor(race, Female);
        Version++;
        return true;
    }

    /// <summary>EXPORT: the look as a body file, laid out as the game's own exports are (type 15, one record, id 1, <c>1--INGAME</c>; docs/characters.md).</summary>
    public FcsFile Export()
    {
        var record = Copy(Record);
        (record.Id, record.StringId, record.Flags) = (1, "1--INGAME", 0);
        var file = new FcsFile { FileType = FcsFileType.V15, NextId = 1 };
        file.Records.Add(record);
        return file;
    }

    AppearanceLimits? LimitsFor(GameRecord race, bool female)
    {
        if (limitsCache.TryGetValue((race.StringId, female), out var cached)) return cached;
        return limitsCache[(race.StringId, female)] = ReadLimits(db, installRoot, race, female);
    }

    /// <summary>The race's <c>editor limits</c> config of one gender, or null (no file, unreadable, no config).</summary>
    public static AppearanceLimits? ReadLimits(GameDatabase db, string installRoot, GameRecord race, bool female)
    {
        if (race.GetPath("editor limits") is not { Length: > 0 } path) return null;
        try
        {
            return AppearanceLimits.Read(CharacterGenerator.Resolve(installRoot, path), female, GameConstants.FromDatabase(db).AppearanceRandomDeviation);
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The races the editor offers: every RACE_GROUP in data order with its <c>races</c> that are <c>playable</c> and have an <c>editor limits</c> file (fcs.def: "For it
    /// to be completely playable it must have a editor limits file and be on any race group"); groups left empty are not offered.
    /// </summary>
    public static IReadOnlyList<EditorRaceGroup> PlayableRaces(GameDatabase db) =>
        playableRaces.GetValue(db, static db => [.. db.OfType(FcsRecordType.RACE_GROUP)
            .Select(g => new EditorRaceGroup(g, [.. g.GetReferences("races").Select(r => db.Find(r.TargetStringId))
                .Where(r => r is { Type: FcsRecordType.RACE } && r.GetBool("playable", true) && r.GetPath("editor limits").Length > 0).Select(r => r!)]))
            .Where(g => g.Races.Count > 0)]);

    /// <summary>The playable races by database, made once: the editor's interface asks every frame, and the search walks every record.</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameDatabase, IReadOnlyList<EditorRaceGroup>> playableRaces = new();

    /// <summary>
    /// <see cref="PlayableRaces(GameDatabase)"/> limited to <paramref name="only"/> (a start's <c>force race</c>); when that leaves none, the original says "Game start has
    /// no valid races" and offers every playable race (docs/characters.md "Character editor").
    /// </summary>
    public static IReadOnlyList<EditorRaceGroup> PlayableRaces(GameDatabase db, IReadOnlyCollection<string> only)
    {
        var all = PlayableRaces(db);
        if (only.Count == 0) return all;
        var limited = all.Select(g => new EditorRaceGroup(g.Group, [.. g.Races.Where(r => only.Contains(r.StringId))])).Where(g => g.Races.Count > 0).ToList();
        return limited.Count > 0 ? limited : all;
    }

    /// <summary>
    /// The choices of the Idle stance slider (the original's list, <c>FUN_14007d480</c> and <c>FUN_1405bbd40</c>, <b>Observed</b>): the record names of the ANIMATIONs,
    /// in data order, that are <c>idle</c>, not <c>disabled</c>, of no <c>weather type</c>, not on the <c>overlay</c> layer, with no weapon in either hand and not
    /// <c>crouched</c>.
    /// </summary>
    public IReadOnlyList<string> IdleStances => idleStances ??= [.. db.OfType(FcsRecordType.ANIMATION)
        .Where(a => a.GetBool("idle") && !a.GetBool("disabled") && a.GetInt("weather type") < 1 && a.GetString("layer", "") != "overlay"
            && a.GetInt("has weapon L") == 0 && a.GetInt("has weapon R") == 0 && !a.GetBool("crouched"))
        .Select(a => a.Name)];
    IReadOnlyList<string>? idleStances;

    /// <summary>
    /// The controls of a tab in the original's order (<c>FUN_1405ebd50</c>): BODY the Idle stance slider, <c>Skin Tone</c> (with more than one colour), the body
    /// sliders and the other body colours; FACE the head picker, the face sliders and colours; HAIR the hair and beard pickers (no beard for women or races without
    /// beards) and the hair colour sliders.
    /// </summary>
    public IReadOnlyList<EditorControl> Controls(EditorTab tab)
    {
        var list = new List<EditorControl>();
        if (tab == EditorTab.Face && Options(EditorControlKind.Head).Count > 1) list.Add(new("Head", EditorControlKind.Head, 0, 0, 0));
        if (tab == EditorTab.Body && IdleStances.Count > 0) list.Add(new("Idle stance", EditorControlKind.IdleStance, 1, IdleStances.Count, 1));
        if (tab == EditorTab.Hair)
        {
            if (Options(EditorControlKind.HairStyle).Count > 1) list.Add(new("Hair style", EditorControlKind.HairStyle, 0, 0, 0));
            if (!Female && Options(EditorControlKind.Beard).Count > 1) list.Add(new("Beard", EditorControlKind.Beard, 0, 0, 0));
            list.Add(new("Hair colour", EditorControlKind.HairHue, 0, 100, 0));
            list.Add(new("Hair saturation", EditorControlKind.HairSaturation, 0, 100, 0));
            list.Add(new("Hair brightness", EditorControlKind.HairBrightness, 0, 100, 100));
            return list;
        }
        if (Limits is null) return list;
        var category = tab == EditorTab.Body ? AppearanceCategory.Body : AppearanceCategory.Face;
        static bool IsSkin(AppearanceColourRange c) => c.Name == "Skin Tone";
        if (tab == EditorTab.Body)
            foreach (var c in Limits.Colours.Where(c => c.Category == category && IsSkin(c) && c.Colours.Count > 1))
                list.Add(new(c.Name, EditorControlKind.Colour, c.Min, c.Max, c.Mid) { Colour = c });
        foreach (var r in Limits.Ranges.Where(r => r.Category == category))
            list.Add(new(r.Name, r.Target is null ? EditorControlKind.Slider : EditorControlKind.Pose, r.Min, r.Max, r.Mid) { Range = r });
        foreach (var c in Limits.Colours.Where(c => c.Category == category && !IsSkin(c)))
            list.Add(new(c.Name, EditorControlKind.Colour, c.Min, c.Max, c.Mid) { Colour = c });
        return list;
    }

    /// <summary>A slider's value as the editor shows it.</summary>
    public int Value(EditorControl c)
    {
        float F(string key, float fallback) => Record.Floats.TryGetValue(key, out var v) ? v : fallback;
        switch (c.Kind)
        {
            case EditorControlKind.Slider: return CharacterGenerator.Round(F(c.Label, c.Mid));
            case EditorControlKind.Pose:
                var r = c.Range!;
                float plus = F(r.Target!, c.Mid * 0.01f), minus = r.TargetOpposite is { } o ? F(o, 0) : 0;
                return CharacterGenerator.Round((minus > 0 && plus <= 0 ? -minus : plus) * 100);
            case EditorControlKind.Colour:
                return Record.Vector3s.TryGetValue(c.Label, out var colour) ? Nearest(c.Colour!, colour) : c.Mid;
            case EditorControlKind.HairHue: return CharacterGenerator.Round(F("Hair Colour", 0));
            case EditorControlKind.HairSaturation: return CharacterGenerator.Round(F("Hair Saturation", 0));
            case EditorControlKind.HairBrightness: return CharacterGenerator.Round(F("Hair Brightness", 100));
            // A stance not in the list shows as the first, as in the original.
            case EditorControlKind.IdleStance:
                return Math.Max(IdleStances.ToList().IndexOf(Record.Strings.GetValueOrDefault("idle stance") ?? ""), 0) + 1;
            default: return OptionIndex(c.Kind);
        }
    }

    /// <summary>The colour slider position whose colour is nearest to <paramref name="colour"/>.</summary>
    static int Nearest(AppearanceColourRange range, Vector3 colour)
    {
        int best = range.Min;
        float bestDistance = float.MaxValue;
        for (int v = range.Min; v <= range.Max; v++)
        {
            float d = Vector3.DistanceSquared(range.ColourAt(v), colour);
            if (d < bestDistance) (best, bestDistance) = (v, d);
        }
        return best;
    }

    /// <summary>Sets a slider (clamped to its range) and writes its keys.</summary>
    public void Set(EditorControl c, int value)
    {
        if (c.IsPicker) { SetOption(c.Kind, value); return; }
        value = Math.Clamp(value, c.Min, c.Max);
        if (!c.IsPicker && Value(c) == value) return;
        if (c.Kind == EditorControlKind.IdleStance) Record.Strings["idle stance"] = IdleStances[value - 1];
        else Write(Record, c, value);
        Version++;
    }

    static void Write(FcsRecord record, EditorControl c, int value)
    {
        switch (c.Kind)
        {
            case EditorControlKind.Slider: record.Floats[c.Label] = value; break;
            case EditorControlKind.Pose:
                var r = c.Range!;
                if (r.TargetOpposite is { } opposite)
                {
                    record.Floats[r.Target!] = value > 0 ? value * 0.01f : 0;
                    record.Floats[opposite] = value < 0 ? value * -0.01f : 0;
                }
                else record.Floats[r.Target!] = value * 0.01f;
                break;
            case EditorControlKind.Colour: record.Vector3s[c.Label] = c.Colour!.ColourAt(value); break;
            case EditorControlKind.HairHue: record.Floats["Hair Colour"] = value; break;
            case EditorControlKind.HairSaturation: record.Floats["Hair Saturation"] = value; break;
            case EditorControlKind.HairBrightness: record.Floats["Hair Brightness"] = value; break;
        }
    }

    /// <summary>A picker's choices: the race's heads of the gender (with a texture map), its hairs of the slot; hair and beard start with "none".</summary>
    public IReadOnlyList<GameRecord?> Options(EditorControlKind kind) => OptionsFor(db, Race, Female, kind);

    static IReadOnlyList<GameRecord?> OptionsFor(GameDatabase db, GameRecord race, bool female, EditorControlKind kind)
    {
        if (kind == EditorControlKind.Head)
            return [.. race.GetReferences(female ? "heads female" : "heads male").Select(r => db.Find(r.TargetStringId))
                .Where(h => h is not null && h.GetPath("texture map").Length > 0)];
        var slot = kind == EditorControlKind.Beard ? AttachSlot.Beard : AttachSlot.Hair;
        var list = new List<GameRecord?> { null };
        list.AddRange(race.GetReferences("hairs").Select(r => db.Find(r.TargetStringId))
            .Where(h => h is { Type: FcsRecordType.ATTACHMENT } && (AttachSlot)h.GetInt("attach slot", (int)AttachSlot.Hair) == slot));
        return list;
    }

    static string KeyOf(EditorControlKind kind) => kind switch { EditorControlKind.Head => "head", EditorControlKind.Beard => "beard", _ => "hair style" };

    public int OptionIndex(EditorControlKind kind)
    {
        string id = Record.Strings.GetValueOrDefault(KeyOf(kind)) ?? "";
        var options = Options(kind);
        for (int i = 0; i < options.Count; i++)
            if ((options[i]?.StringId ?? "") == id) return i;
        return 0;
    }

    /// <summary>The name of a picker's current choice ("None" for no hair or beard).</summary>
    public string OptionName(EditorControlKind kind) => Options(kind).ElementAtOrDefault(OptionIndex(kind))?.Name ?? "None";

    public void SetOption(EditorControlKind kind, int index)
    {
        var options = Options(kind);
        if (options.Count == 0) return;
        index = ((index % options.Count) + options.Count) % options.Count;
        string id = options[index]?.StringId ?? "";
        if ((Record.Strings.GetValueOrDefault(KeyOf(kind)) ?? "") == id) return;
        Record.Strings[KeyOf(kind)] = id;
        Version++;
    }

    /// <summary>The look with a picker's choice <paramref name="index"/>, the draft unchanged (the editor prepares every choice's mesh ahead).</summary>
    public FcsRecord WithOption(EditorControlKind kind, int index)
    {
        var next = Copy(Record);
        next.Strings[KeyOf(kind)] = Options(kind).ElementAtOrDefault(index)?.StringId ?? "";
        return next;
    }

    /// <summary>Steps a picker to the previous or next choice.</summary>
    public void Step(EditorControlKind kind, int direction) => SetOption(kind, OptionIndex(kind) + direction);

    /// <summary>RANDOMISE (<paramref name="tab"/>) or RANDOMISE ALL (null) with a sequence seeded by <paramref name="seed"/> (<see cref="RandomisedLook"/>).</summary>
    public void Randomise(EditorTab? tab, int seed)
    {
        Record = RandomisedLook(seed, tab);
        Version++;
    }

    /// <summary>
    /// RESET ALL: the appearance template's defaults (<c>FUN_14007a490</c>, <b>Observed</b>: hair, beard, colours, <c>Posture</c>, <c>idle stance</c>
    /// "idle_stand_normal"). The template's slider values are not traced: Meitou puts each body/face slider at its editor-limits mid, each face pose at 0,
    /// <c>Posture</c> at 20, the skin tone at its mid colour, no hair or beard, hair colour (0, 0, 100). Race, gender and head stay.
    /// </summary>
    public void Reset()
    {
        Defaults(Record, Limits);
        Version++;
    }

    /// <summary><see cref="Reset"/>'s values written into <paramref name="record"/>.</summary>
    static void Defaults(FcsRecord record, AppearanceLimits? limits)
    {
        if (limits is not null)
        {
            foreach (var r in limits.Ranges)
                Write(record, new EditorControl(r.Name, r.Target is null ? EditorControlKind.Slider : EditorControlKind.Pose, r.Min, r.Max, r.Mid) { Range = r }, r.Target is null ? r.Mid : 0);
            foreach (var c in limits.Colours) record.Vector3s[c.Name] = c.ColourAt(c.Mid);
        }
        record.Floats["Posture"] = 20;
        record.Floats["Hair Colour"] = 0;
        record.Floats["Hair Saturation"] = 0;
        record.Floats["Hair Brightness"] = 100;
        record.Strings["hair style"] = "";
        record.Strings["beard"] = "";
        record.Strings["idle stance"] = "idle_stand_normal";
    }

    /// <summary>The look the character had as each race and gender it was shown as, by race id and gender (the original's per-character map, <c>FUN_1405eadc0</c>).</summary>
    readonly Dictionary<(string Race, bool Female), FcsRecord> looks = [];

    /// <summary>Changes race (a single-gender race makes the character male): the look comes back as it was for that race and gender, else a new one is rolled.</summary>
    public void SetRace(GameRecord race, int seed)
    {
        if (race.StringId == Race.StringId) return;
        SwitchLook(race, Female && !race.GetBool("single gender"), seed);
    }

    /// <summary>Changes gender (not for a single-gender race), keeping the look of each gender as <see cref="SetRace"/> does.</summary>
    public void SetFemale(bool female, int seed)
    {
        if (Race.GetBool("single gender")) female = false;
        if (female == Female) return;
        SwitchLook(Race, female, seed);
    }

    /// <summary>
    /// The original's switch (<c>FUN_1405ef390</c>, <c>FUN_1405eadc0</c>, <b>Observed</b>): the current look is kept for the old race and gender, the new pair's kept look
    /// replaces it (the idle stance stays the character's); a pair never shown gets a rolled look (the original rolls them when the editor opens).
    /// </summary>
    void SwitchLook(GameRecord race, bool female, int seed)
    {
        var next = SwitchedLook(race, female, seed);
        looks[(Race.StringId, Female)] = Copy(Record);
        Race = race;
        Limits = LimitsFor(Race, female);
        Record = next;
        Version++;
    }

    /// <summary>The look <see cref="SetRace"/> or <see cref="SetFemale"/> would give with <paramref name="seed"/>, the draft unchanged (the editor prepares it ahead).</summary>
    public FcsRecord SwitchedLook(GameRecord race, bool female, int seed)
    {
        FcsRecord next;
        if (looks.TryGetValue((race.StringId, female), out var kept))
        {
            next = Copy(kept);
            if (Record.Strings.TryGetValue("idle stance", out var stance)) next.Strings["idle stance"] = stance;
        }
        else
        {
            // The original makes every pair's look when the editor opens (FUN_1405f03c0): the appearance template's defaults (FUN_14007a490: the first head, no
            // hair or beard; Meitou: sliders at their mid, poses 0, skin at its mid colour), then only the hair rolled (FUN_14007adc0, no faction).
            next = Copy(Record);
            next.Bools["sex female"] = female;
            next.References["race"] = [new FcsReference(race.StringId, 0, 0, 0)];
            next.Floats.Clear();
            next.Vector3s.Clear();
            Defaults(next, LimitsFor(race, female));
            if (Record.Strings.TryGetValue("idle stance", out var own)) next.Strings["idle stance"] = own;
            next.Strings["head"] = CharacterGenerator.HeadOptions(db, race, next).FirstOrDefault().Item?.StringId ?? "";
            CharacterGenerator.RollHair(db, race, null, next, MsvcRandom.FromSeed(seed));
        }
        return next;
    }

    /// <summary>
    /// The look RANDOMISE ALL (<paramref name="tab"/> null) or a tab's RANDOMISE would give with <paramref name="seed"/>, the draft unchanged (the editor prepares it
    /// ahead). The original's randomisers in its draw order (<b>Verified</b> by decompilation, docs/characters.md "Character editor"): RANDOMISE ALL
    /// (<c>FUN_1405ef930</c>) the hair without the faction's limits, the face (head, face sliders), the body without the skin re-roll, then the face poses; FACE the
    /// face and its poses; BODY the body; HAIR the hair (<c>FUN_1405efa50</c>). Everything else stays.
    /// </summary>
    public FcsRecord RandomisedLook(int seed, EditorTab? tab = null)
    {
        var next = Copy(Record);
        var rng = MsvcRandom.FromSeed(seed);
        if (tab is null or EditorTab.Hair) CharacterGenerator.RollHair(db, Race, null, next, rng);
        if (tab is null or EditorTab.Face) CharacterGenerator.RollFace(db, Race, Limits, next, rng);
        if (tab is null or EditorTab.Body) CharacterGenerator.RollBody(Limits, next, rng, skinReroll: false);
        if (tab is null or EditorTab.Face) CharacterGenerator.RollFacePoses(Limits, next, rng);
        return next;
    }


    /// <summary>
    /// A proposed look made valid (what the simulation applies, docs/characters.md "Character editor"): race and gender as given (a single-gender race is male), head,
    /// hair and beard among the race's choices (else the first head, no hair, no beard; women have no beard), every editor slider, pose pair and colour clamped to its
    /// limits, hair colour to 0..100. Other keys are kept as they are. Returns a new record.
    /// </summary>
    public static FcsRecord Sanitize(GameDatabase db, string installRoot, FcsRecord proposed, GameRecord race)
    {
        var r = Copy(proposed);
        bool female = r.Bools.TryGetValue("sex female", out var f) && f && !race.GetBool("single gender");
        r.Bools["sex female"] = female;
        r.References["race"] = [new FcsReference(race.StringId, 0, 0, 0)];
        foreach (var kind in new[] { EditorControlKind.Head, EditorControlKind.HairStyle, EditorControlKind.Beard })
        {
            string key = KeyOf(kind), id = r.Strings.GetValueOrDefault(key) ?? "";
            var options = OptionsFor(db, race, female, kind);
            if (kind == EditorControlKind.Beard && female) id = "";
            else if (!options.Any(o => (o?.StringId ?? "") == id)) id = kind == EditorControlKind.Head ? options.FirstOrDefault()?.StringId ?? id : "";
            if (r.Strings.ContainsKey(key) || id.Length > 0) r.Strings[key] = id;
        }
        foreach (var key in new[] { "Hair Colour", "Hair Saturation", "Hair Brightness" })
            if (r.Floats.TryGetValue(key, out var v)) r.Floats[key] = float.IsFinite(v) ? Math.Clamp(v, 0, 100) : 0;
        if (ReadLimits(db, installRoot, race, female) is { } limits)
        {
            foreach (var range in limits.Ranges)
            {
                if (range.Target is null)
                {
                    if (r.Floats.TryGetValue(range.Name, out var v)) r.Floats[range.Name] = float.IsFinite(v) ? Math.Clamp(v, range.Min, range.Max) : range.Mid;
                    continue;
                }
                if (!r.Floats.ContainsKey(range.Target) && (range.TargetOpposite is not { } other || !r.Floats.ContainsKey(other))) continue;
                float plus = r.Floats.GetValueOrDefault(range.Target), minus = range.TargetOpposite is { } o ? r.Floats.GetValueOrDefault(o) : 0;
                float value = (minus > 0 && plus <= 0 ? -minus : plus) * 100;
                if (!float.IsFinite(value)) value = range.Mid;
                Write(r, new EditorControl(range.Name, EditorControlKind.Pose, range.Min, range.Max, range.Mid) { Range = range },
                    CharacterGenerator.Round(Math.Clamp(value, range.Min, range.Max)));
            }
        }
        foreach (var key in r.Vector3s.Keys.ToArray())
        {
            var v = r.Vector3s[key];
            r.Vector3s[key] = Vector3.Clamp(float.IsFinite(v.X + v.Y + v.Z) ? v : new Vector3(0.5f), Vector3.Zero, Vector3.One);
        }
        return r;
    }
}
