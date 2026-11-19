using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.Characters;

/// <summary>
/// The kinds of a code mod's character editor row (<see cref="LookRow"/>).
/// </summary>
public enum LookRowKind
{
    /// <summary>
    /// A slider of whole numbers, drawn as the game's <c>Kenshi_Slider</c>.
    /// </summary>
    Slider,
    /// <summary>
    /// A list of named choices, drawn as the game's <c>Kenshi_ListSlider</c>.
    /// </summary>
    Choice,
    /// <summary>
    /// A colour of three sliders (colour, saturation, brightness), drawn as the game's <c>Kenshi_MultiSlider</c> as the hair colour is.
    /// </summary>
    Colour
}

/// <summary>
/// A row a code mod adds to the character editor (docs/modding.md "Character editor rows"): on <see cref="Tab"/>, captioned <see cref="Caption"/>,
/// its value kept on the look record under the mod's <see cref="Key"/>. Made with <see cref="Slider"/>, <see cref="Choice"/> or
/// <see cref="Colour"/>.
/// </summary>
public sealed record LookRow
{
    /// <summary>
    /// The tab the row is shown on, after the game's own rows.
    /// </summary>
    public EditorTab Tab { get; init; }

    /// <summary>
    /// The mod's name of the value; the look record keeps it under <c>mod id:key</c> (<see cref="ModLooks.KeyOf"/>).
    /// </summary>
    public string Key { get; init; } = "";

    /// <summary>
    /// The row's caption.
    /// </summary>
    public string Caption { get; init; } = "";

    /// <summary>
    /// What the row edits.
    /// </summary>
    public LookRowKind Kind { get; init; }

    /// <summary>
    /// A slider's lowest value.
    /// </summary>
    public int Min { get; init; }

    /// <summary>
    /// A slider's highest value.
    /// </summary>
    public int Max { get; init; }

    /// <summary>
    /// A slider's value, or a choice's index, while the look has none of its own (RESET puts it back).
    /// </summary>
    public int Default { get; init; }

    /// <summary>
    /// A choice row's choices, in order.
    /// </summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>
    /// A colour row's colour (hue, saturation, brightness, each 0..1) while the look has none of its own.
    /// </summary>
    public Vector3 DefaultColour { get; init; }

    /// <summary>
    /// A slider from <paramref name="min"/> to <paramref name="max"/>, at <paramref name="default"/> until set.
    /// </summary>
    public static LookRow Slider(EditorTab tab, string key, string caption, int min, int max, int @default) =>
        new() { Tab = tab, Key = key, Caption = caption, Kind = LookRowKind.Slider, Min = min, Max = max, Default = @default };

    /// <summary>
    /// A list of <paramref name="choices"/>, at index <paramref name="default"/> until set.
    /// </summary>
    public static LookRow Choice(EditorTab tab, string key, string caption, IReadOnlyList<string> choices, int @default = 0) =>
        new()
        {
            Tab = tab,
            Key = key,
            Caption = caption,
            Kind = LookRowKind.Choice,
            Max = choices.Count - 1,
            Default = @default,
            Choices = choices,
        };

    /// <summary>
    /// A colour, <paramref name="hue"/>, <paramref name="saturation"/> and <paramref name="brightness"/> (each 0..1) until set.
    /// </summary>
    public static LookRow Colour(EditorTab tab, string key, string caption, float hue, float saturation, float brightness) =>
        new()
        {
            Tab = tab,
            Key = key,
            Caption = caption,
            Kind = LookRowKind.Colour,
            Max = 100,
            DefaultColour = new(hue, saturation, brightness),
        };
}

/// <summary>
/// What a row source is asked with (<see cref="LookRowSource"/>): the character shown in the editor.
/// </summary>
/// <param name="Race">The RACE the look is for.</param>
/// <param name="Female">Whether the look is female.</param>
/// <param name="Look">The look being edited (read only: change it only through the rows).</param>
/// <param name="Creation">True in a new game's character creation, false for an edit in play (a plastic surgeon, a recruit, a slave bought).</param>
public sealed record LookRowContext(GameRecord Race, bool Female, FcsRecord Look, bool Creation);

/// <summary>
/// Who a look is rolled for (<see cref="ModLooks.Roll"/>): a generated character, or the editor's RANDOMISE.
/// </summary>
/// <param name="Character">The CHARACTER record of a generated character; null in the editor.</param>
/// <param name="Faction">The faction a generated character is made for; null when none, and in the editor.</param>
/// <param name="FromBodyFile">True for a look that is its CHARACTER's own body file (a unique character).</param>
/// <param name="Tab">The editor's RANDOMISE of this tab; null for RANDOMISE ALL and for a generated character.</param>
/// <param name="InEditor">True for the editor's RANDOMISE and RANDOMISE ALL.</param>
public sealed record LookRollSubject(GameRecord? Character, GameRecord? Faction, bool FromBodyFile, EditorTab? Tab, bool InEditor);

/// <summary>
/// A code mod's character editor rows for the character shown (<see cref="ModLooks"/>); none leaves that character's editor as it was.
/// </summary>
public delegate IEnumerable<LookRow> LookRowSource(LookRowContext context);

/// <summary>
/// A code mod's values for a look (<see cref="ModLooks"/>): a generated character's, or the editor's RANDOMISE. It writes only through
/// <see cref="LookRollContext.Set(string, int)"/> and <see cref="LookRollContext.SetColour"/>.
/// </summary>
public delegate void LookRoll(LookRollContext context);

/// <summary>
/// What a roll is asked with (<see cref="LookRoll"/>), and the only way it writes: its own keys on <see cref="Look"/>.
/// </summary>
public sealed class LookRollContext
{
    /// <summary>
    /// The mod the roll is of; its keys are kept under its id.
    /// </summary>
    readonly string _mod;

    /// <summary>
    /// The look being rolled, already rolled by the game.
    /// </summary>
    public FcsRecord Look { get; }

    /// <summary>
    /// The RACE the look is for.
    /// </summary>
    public GameRecord Race { get; }

    /// <summary>
    /// Whether the look is female.
    /// </summary>
    public bool Female { get; }

    /// <summary>
    /// The CHARACTER record of a generated character; null in the editor.
    /// </summary>
    public GameRecord? Character { get; init; }

    /// <summary>
    /// The faction a generated character is made for; null when none, and in the editor.
    /// </summary>
    public GameRecord? Faction { get; init; }

    /// <summary>
    /// True for a character whose look is its CHARACTER's own body file (a unique character), false for a rolled one.
    /// </summary>
    public bool FromBodyFile { get; init; }

    /// <summary>
    /// The editor's RANDOMISE of this tab; null for RANDOMISE ALL and for a generated character.
    /// </summary>
    public EditorTab? Tab { get; init; }

    /// <summary>
    /// True for the editor's RANDOMISE and RANDOMISE ALL, false for a generated character.
    /// </summary>
    public bool InEditor { get; init; }

    /// <summary>
    /// The mod's own random sequence for this look, apart from the game's, so the game's own rolls never change.
    /// </summary>
    public MsvcRandom Random { get; }

    /// <summary>
    /// The context of <paramref name="mod"/>'s roll of <paramref name="look"/>.
    /// </summary>
    public LookRollContext(string mod, FcsRecord look, GameRecord race, bool female, MsvcRandom random)
    {
        _mod = mod;
        Look = look;
        Race = race;
        Female = female;
        Random = random;
    }

    /// <summary>
    /// Writes a slider's value or a choice's index under the mod's <paramref name="key"/>.
    /// </summary>
    public void Set(string key, int value) => Look.Ints[ModLooks.KeyOf(_mod, key)] = value;

    /// <summary>
    /// Writes a colour (hue, saturation, brightness, each 0..1) under the mod's <paramref name="key"/>.
    /// </summary>
    public void SetColour(string key, Vector3 hsb) => Look.Vector3S[ModLooks.KeyOf(_mod, key)] = Vector3.Clamp(hsb, Vector3.Zero, Vector3.One);

    /// <summary>
    /// The mod's value under <paramref name="key"/>, or <paramref name="fallback"/>.
    /// </summary>
    public int Get(string key, int fallback) => ModLooks.Int(Look, _mod, key, fallback);
}

/// <summary>
/// Which texture a <see cref="LookTint"/> is placed on: the body texture, or the head's (<c>texture map</c> of the HEAD).
/// </summary>
public enum LookArea
{
    /// <summary>
    /// The body texture.
    /// </summary>
    Body,
    /// <summary>
    /// The head texture (the head is part of the body mesh, its texture coordinates beside the body's).
    /// </summary>
    Head
}

/// <summary>
/// Which half of the body a <see cref="LookTint"/> reaches: both, or only the character's left or right (both eyes share one patch of the head
/// texture, so a tint per eye tells them apart by side).
/// </summary>
public enum LookSide
{
    /// <summary>
    /// Both halves.
    /// </summary>
    Both,
    /// <summary>
    /// The character's own left half.
    /// </summary>
    Left,
    /// <summary>
    /// The character's own right half.
    /// </summary>
    Right
}

/// <summary>
/// A colour a code mod lays over a disk of a character's skin (<see cref="LookExtras"/>): on <see cref="Area"/>'s texture, centred at
/// <see cref="Centre"/> (0..1 across and down the texture image) with <see cref="Radius"/> (a fraction of its width). The texel keeps its lightness
/// and takes <see cref="Colour"/>'s hue and saturation, by <see cref="Strength"/>, fading out over the disk's outer <see cref="Softness"/>; texels
/// lighter than <see cref="MaxLightness"/> are left out (an eye's white round its iris).
/// </summary>
/// <param name="Area">The texture the disk is on.</param>
/// <param name="Centre">The disk's centre, 0..1 across and down the texture image.</param>
/// <param name="Radius">The disk's radius, a fraction of the texture's width.</param>
/// <param name="Colour">The colour, red, green and blue 0..1.</param>
public sealed record LookTint(LookArea Area, Vector2 Centre, float Radius, Vector3 Colour)
{
    /// <summary>
    /// How much of the colour is taken, 0..1.
    /// </summary>
    public float Strength { get; init; } = 1;

    /// <summary>
    /// The share of the radius over which the colour fades out at the disk's edge, 0..1.
    /// </summary>
    public float Softness { get; init; } = 0.2f;

    /// <summary>
    /// The lightness (0..1) above which texels are left as they are; 1 tints them all.
    /// </summary>
    public float MaxLightness { get; init; } = 1;

    /// <summary>
    /// The half of the body the tint reaches.
    /// </summary>
    public LookSide Side { get; init; }
}

/// <summary>
/// What a code mod adds to how a look is drawn (<see cref="LookExtrasSource"/>): pose weights (face and body shape keys, by pose name, set over the
/// look's own) and colour tints.
/// </summary>
public sealed record LookExtras
{
    /// <summary>
    /// Pose weights by pose name, each set over the look's own (0 takes the pose off).
    /// </summary>
    public IReadOnlyDictionary<string, float> PoseWeights { get; init; } = new Dictionary<string, float>();

    /// <summary>
    /// Colour tints, drawn in order.
    /// </summary>
    public IReadOnlyList<LookTint> Tints { get; init; } = [];
}

/// <summary>
/// What an extras source is asked with (<see cref="LookExtrasSource"/>): a person's look as it is built.
/// </summary>
/// <param name="Look">The look record (read only).</param>
/// <param name="Race">The RACE.</param>
/// <param name="Female">Whether the look is female.</param>
/// <param name="Head">The HEAD record drawn, or null.</param>
public sealed record LookExtrasContext(FcsRecord Look, GameRecord Race, bool Female, GameRecord? Head);

/// <summary>
/// A code mod's additions to how a person's look is drawn, asked each time the look is built (<see cref="ModLooks"/>); null adds nothing.
/// </summary>
public delegate LookExtras? LookExtrasSource(LookExtrasContext context);

/// <summary>
/// The character editor rows, look rolls and look extras code mods added (docs/modding.md "Character editor rows", "Look extras"). Filled at
/// startup before the first world is built; empty without mods, so the editor and the generator are as before. A mod's values live on the look
/// record (CHARACTER_APPEARANCE) under <c>mod id:key</c>, sliders and choices as whole numbers and colours as vectors, which nothing else reads: the
/// editor's CONFIRM, the save and EXPORT carry every key of the record.
/// </summary>
public static class ModLooks
{
    #region Registrations

    /// <summary>
    /// A row source or roll with the mod that added it.
    /// </summary>
    sealed class Entry<T>(string owner, T hook)
    {
        /// <summary>
        /// The mod's id: its keys' prefix and the log line's name.
        /// </summary>
        public string Owner { get; } = owner;

        /// <summary>
        /// The source or roll.
        /// </summary>
        public T Hook { get; } = hook;

        /// <summary>
        /// 1 once it failed: logged then; a row source is asked no more for the session, a roll stays.
        /// </summary>
        public int Dropped;
    }

    /// <summary>
    /// The row sources in registration order; replaced whole on a change, so a reader on any thread sees a complete list.
    /// </summary>
    static volatile Entry<LookRowSource>[] s_rows = [];

    /// <summary>
    /// The rolls in registration order; replaced whole on a change.
    /// </summary>
    static volatile Entry<LookRoll>[] s_rolls = [];

    /// <summary>
    /// The extras sources in registration order; replaced whole on a change.
    /// </summary>
    static volatile Entry<LookExtrasSource>[] s_extras = [];

    /// <summary>
    /// Serializes the changes.
    /// </summary>
    static readonly Lock Gate = new();

    /// <summary>
    /// Whether any mod added a row source.
    /// </summary>
    public static bool HasRows => s_rows.Length > 0;

    /// <summary>
    /// Whether any mod added a roll.
    /// </summary>
    public static bool HasRolls => s_rolls.Length > 0;

    /// <summary>
    /// Whether any mod added an extras source; read when the characters' shaders are built (the tints' code is left out without one).
    /// </summary>
    public static bool HasExtras => s_extras.Length > 0;

    /// <summary>
    /// Adds <paramref name="source"/> for mod <paramref name="owner"/>.
    /// </summary>
    public static void AddRows(string owner, LookRowSource source)
    {
        lock (Gate)
        {
            s_rows = [.. s_rows, new Entry<LookRowSource>(owner, source)];
        }
    }

    /// <summary>
    /// Adds <paramref name="roll"/> for mod <paramref name="owner"/>.
    /// </summary>
    public static void AddRoll(string owner, LookRoll roll)
    {
        lock (Gate)
        {
            s_rolls = [.. s_rolls, new Entry<LookRoll>(owner, roll)];
        }
    }

    /// <summary>
    /// Adds <paramref name="source"/> for mod <paramref name="owner"/>.
    /// </summary>
    public static void AddExtras(string owner, LookExtrasSource source)
    {
        lock (Gate)
        {
            s_extras = [.. s_extras, new Entry<LookExtrasSource>(owner, source)];
        }
    }

    #endregion

    #region Rows

    /// <summary>
    /// The mods' rows for <paramref name="context"/>, in registration order, each with its mod's id. A source that throws or returns a row that is
    /// not valid (<see cref="Problem"/>) is logged and asked no more for the session.
    /// </summary>
    public static IReadOnlyList<(string Mod, LookRow Row)> Rows(LookRowContext context)
    {
        var entries = s_rows;
        if (entries.Length == 0) return [];

        var list = new List<(string, LookRow)>();
        foreach (var entry in entries)
        {
            if (Volatile.Read(ref entry.Dropped) != 0) continue;
            if (Ask(entry, context) is { } rows) list.AddRange(rows.Select(r => (entry.Owner, r)));
        }

        return list;
    }

    /// <summary>
    /// One source's rows, checked; null when it failed (logged, and the source dropped).
    /// </summary>
    static List<LookRow>? Ask(Entry<LookRowSource> entry, LookRowContext context)
    {
        try
        {
            var rows = entry.Hook(context)?.ToList() ?? [];
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (RowProblem(row, keys) is not { } problem) continue;

                Drop(entry, "editor rows", problem);
                return null;
            }

            return rows;
        }
        catch (Exception e)
        {
            Drop(entry, "editor rows", $"threw {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// What makes one of a source's rows unusable, or null: a null row, a row that is not valid (<see cref="Problem(LookRow)"/>), or a key in
    /// <paramref name="keys"/> already (each key is added).
    /// </summary>
    static string? RowProblem(LookRow? row, HashSet<string> keys)
    {
        if (row is null) return "a null row";
        if (Problem(row) is { } problem) return problem;

        return keys.Add(row.Key) ? null : $"the key '{row.Key}' twice";
    }

    /// <summary>
    /// What makes a row unusable, or null: no caption, a key that is empty or not letters, digits, spaces, dashes, underscores and dots, a slider
    /// whose lowest value is above its highest, a choice row without choices, or a default outside the row's range.
    /// </summary>
    static string? Problem(LookRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Caption)) return $"a row without a caption (key '{row.Key}')";
        if (row.Key.Length == 0 || !row.Key.All(IsKeyChar)) return $"the key '{row.Key}'";
        if (!Enum.IsDefined(row.Tab) || !Enum.IsDefined(row.Kind)) return $"the row '{row.Key}''s tab or kind";
        if (row.Kind == LookRowKind.Choice && (row.Choices.Count == 0 || row.Choices.Any(string.IsNullOrEmpty)))
            return $"the choice row '{row.Key}' without choices";
        if (row.Kind == LookRowKind.Slider && row.Min > row.Max) return $"the slider '{row.Key}' from {row.Min} to {row.Max}";
        if (row.Kind != LookRowKind.Colour && (row.Default < row.Min || row.Default > row.Max)) return $"the row '{row.Key}''s default";

        return null;
    }

    /// <summary>
    /// Whether <paramref name="c"/> may be in a row's key: a letter, digit, space, dash, underscore or dot.
    /// </summary>
    static bool IsKeyChar(char c) => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.';

    #endregion

    #region Rolls

    /// <summary>
    /// Every mod's roll of <paramref name="look"/>, in registration order, each with its own sequence from <paramref name="seed"/> and its id. A roll
    /// that throws keeps what it wrote and is logged the first time; it stays, so the same look always gets the same values whichever thread
    /// generates it first.
    /// </summary>
    public static void Roll(FcsRecord look, GameRecord race, bool female, int seed, LookRollSubject subject)
    {
        var entries = s_rolls;
        if (entries.Length == 0) return;

        foreach (var entry in entries)
        {
            var random = MsvcRandom.FromSeed(seed ^ Mix(entry.Owner));
            var context = new LookRollContext(entry.Owner, look, race, female, random)
            {
                Character = subject.Character,
                Faction = subject.Faction,
                FromBodyFile = subject.FromBodyFile,
                Tab = subject.Tab,
                InEditor = subject.InEditor,
            };

            try
            {
                entry.Hook(context);
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref entry.Dropped, 1) == 0)
                    Console.Error.WriteLine($"mod       {entry.Owner}: look roll threw (what it wrote is kept): {e.GetType().Name}: {e.Message}");
            }
        }
    }

    /// <summary>
    /// The 32-bit FNV-1a hash of <paramref name="s"/>'s characters, so each mod's sequence differs for one seed.
    /// </summary>
    static int Mix(string s)
    {
        uint h = 0x811c9dc5;
        foreach (char c in s) h = (h ^ c) * 0x01000193;
        return (int)h;
    }

    #endregion

    #region Extras

    /// <summary>
    /// The most tints one look draws, of all mods together; the ones past it are left out.
    /// </summary>
    public const int MaxTints = 4;

    /// <summary>
    /// Every mod's extras for <paramref name="context"/>, merged in registration order: pose weights set over one another, tints in order, at most
    /// <see cref="MaxTints"/>; null without a source. A source that throws or answers a tint that is not valid (<see cref="Problem(LookTint)"/>)
    /// adds nothing for that look and is logged once; it stays, so the same look always gets the same extras whichever thread builds it.
    /// </summary>
    public static (Dictionary<string, float> PoseWeights, List<LookTint> Tints)? Extras(LookExtrasContext context)
    {
        var entries = s_extras;
        if (entries.Length == 0) return null;

        var poses = new Dictionary<string, float>(StringComparer.Ordinal);
        var tints = new List<LookTint>();
        foreach (var entry in entries)
        {
            if (AskExtras(entry, context) is not { } extras) continue;
            foreach (var (name, weight) in extras.PoseWeights) poses[name] = weight;
            tints.AddRange(extras.Tints);
        }

        if (tints.Count > MaxTints) tints.RemoveRange(MaxTints, tints.Count - MaxTints);

        return (poses, tints);
    }

    /// <summary>
    /// One source's extras, checked; null when it has none or failed (logged once).
    /// </summary>
    static LookExtras? AskExtras(Entry<LookExtrasSource> entry, LookExtrasContext context)
    {
        string? problem;
        try
        {
            var extras = entry.Hook(context);
            if (extras is null) return null;
            problem = ExtrasProblem(extras);
            if (problem is null) return extras;
        }
        catch (Exception e)
        {
            problem = $"threw {e.GetType().Name}: {e.Message}";
        }

        if (Interlocked.Exchange(ref entry.Dropped, 1) == 0)
            Console.Error.WriteLine($"mod       {entry.Owner}: look extras left out: {problem}");

        return null;
    }

    /// <summary>
    /// What makes a source's extras unusable, or null: a pose weight that is not a number, or a tint that is not valid.
    /// </summary>
    static string? ExtrasProblem(LookExtras extras)
    {
        if (extras.PoseWeights is null || extras.Tints is null) return "no pose weights or tints list";
        if (extras.PoseWeights.Any(p => !float.IsFinite(p.Value))) return "a pose weight that is not a number";
        return extras.Tints.Select(t => t is null ? "a null tint" : Problem(t)).FirstOrDefault(p => p is not null);
    }

    /// <summary>
    /// What makes a tint unusable, or null: an area or side that does not exist, a value that is not a number, a radius not above 0, or a strength,
    /// softness or lightness outside 0..1.
    /// </summary>
    static string? Problem(LookTint t)
    {
        if (!Enum.IsDefined(t.Area) || !Enum.IsDefined(t.Side)) return "a tint's area or side";
        float[] values = [t.Centre.X, t.Centre.Y, t.Radius, t.Colour.X, t.Colour.Y, t.Colour.Z, t.Strength, t.Softness, t.MaxLightness];
        if (values.Any(v => !float.IsFinite(v))) return "a tint value that is not a number";
        if (t.Radius <= 0) return "a tint radius not above 0";
        if (t.Strength is < 0 or > 1 || t.Softness is < 0 or > 1 || t.MaxLightness is < 0 or > 1)
            return "a tint strength, softness or lightness outside 0..1";

        return null;
    }

    #endregion

    #region Values

    /// <summary>
    /// The look record's key of <paramref name="mod"/>'s value <paramref name="key"/>.
    /// </summary>
    public static string KeyOf(string mod, string key) => mod + ":" + key;

    /// <summary>
    /// <paramref name="mod"/>'s slider value or choice index <paramref name="key"/> on <paramref name="look"/>, or <paramref name="fallback"/>.
    /// </summary>
    public static int Int(FcsRecord look, string mod, string key, int fallback) =>
        look.Ints.TryGetValue(KeyOf(mod, key), out int v) ? v : fallback;

    /// <summary>
    /// <paramref name="mod"/>'s colour <paramref name="key"/> on <paramref name="look"/> as hue, saturation and brightness (each 0..1), or null.
    /// </summary>
    public static Vector3? Hsb(FcsRecord look, string mod, string key) =>
        look.Vector3S.TryGetValue(KeyOf(mod, key), out var v) ? Vector3.Clamp(v, Vector3.Zero, Vector3.One) : null;

    /// <summary>
    /// <paramref name="mod"/>'s colour <paramref name="key"/> on <paramref name="look"/> as red, green and blue (each 0..1), or null.
    /// </summary>
    public static Vector3? Rgb(FcsRecord look, string mod, string key) =>
        Hsb(look, mod, key) is { } hsb ? CharacterAppearance.FromHsb(hsb.X, hsb.Y, hsb.Z) : null;

    #endregion

    #region Failures

    /// <summary>
    /// Logs <paramref name="entry"/>'s first failure and stops asking it.
    /// </summary>
    static void Drop<T>(Entry<T> entry, string what, string problem)
    {
        if (Interlocked.Exchange(ref entry.Dropped, 1) == 0)
            Console.Error.WriteLine($"mod       {entry.Owner}: {what} dropped for the session: {problem}");
    }

    #endregion
}
