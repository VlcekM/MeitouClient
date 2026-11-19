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
        new() { Tab = tab, Key = key, Caption = caption, Kind = LookRowKind.Choice, Max = choices.Count - 1, Default = @default, Choices = choices };

    /// <summary>
    /// A colour, <paramref name="hue"/>, <paramref name="saturation"/> and <paramref name="brightness"/> (each 0..1) until set.
    /// </summary>
    public static LookRow Colour(EditorTab tab, string key, string caption, float hue, float saturation, float brightness) =>
        new() { Tab = tab, Key = key, Caption = caption, Kind = LookRowKind.Colour, Max = 100, DefaultColour = new(hue, saturation, brightness) };
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
/// The character editor rows and look rolls code mods added (docs/modding.md "Character editor rows"). Filled at startup before the first world
/// is built; empty without mods, so the editor and the generator are as before. A mod's values live on the look record (CHARACTER_APPEARANCE) under
/// <c>mod id:key</c>, sliders and choices as whole numbers and colours as vectors, which nothing else reads: the editor's CONFIRM, the save and
/// EXPORT carry every key of the record.
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
                if ((row is null ? "a null row" : Problem(row) ?? (keys.Add(row.Key) ? null : $"the key '{row.Key}' twice")) is { } problem)
                {
                    Drop(entry, "editor rows", problem);
                    return null;
                }
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
    /// What makes a row unusable, or null: no caption, a key that is empty or not letters, digits, spaces, dashes, underscores and dots, a slider
    /// whose lowest value is above its highest, a choice row without choices, or a default outside the row's range.
    /// </summary>
    static string? Problem(LookRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Caption)) return $"a row without a caption (key '{row.Key}')";
        if (row.Key.Length == 0 || !row.Key.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.')) return $"the key '{row.Key}'";
        if (!Enum.IsDefined(row.Tab) || !Enum.IsDefined(row.Kind)) return $"the row '{row.Key}''s tab or kind";
        if (row.Kind == LookRowKind.Choice && (row.Choices.Count == 0 || row.Choices.Any(string.IsNullOrEmpty)))
            return $"the choice row '{row.Key}' without choices";
        if (row.Kind == LookRowKind.Slider && row.Min > row.Max) return $"the slider '{row.Key}' from {row.Min} to {row.Max}";
        if (row.Kind != LookRowKind.Colour && (row.Default < row.Min || row.Default > row.Max)) return $"the row '{row.Key}''s default";
        return null;
    }

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
