using System.Collections.Concurrent;

namespace Meitou.Localisation;

using Asked = (ShownTextKind Kind, string Key, string Text, string Tag, string Field, ulong Character, ShownPlace Place);

/// <summary>
/// What kind of text a mod's text source is asked about (<see cref="ShownText"/>, docs/modding.md "Shown text").
/// </summary>
public enum ShownTextKind : byte
{
    /// <summary>
    /// A message of the interface or of the game's notices: <see cref="ShownTextContext.Key"/> is the English source text the code asks the
    /// translation catalog for (its <c>msgid</c>, placeholders such as <c>{1}</c> still in it), <see cref="ShownTextContext.Text"/> that text in the
    /// player's language, <see cref="ShownTextContext.Tag"/> the message context (<c>msgctxt</c>, "" none).
    /// </summary>
    Message,

    /// <summary>
    /// Text a game record gives: <see cref="ShownTextContext.Key"/> is the record's string id, <see cref="ShownTextContext.Field"/> which of its
    /// texts (<c>name</c> for its name, else the record's own field: <c>description</c>, <c>unexplored name</c>, <c>display name</c>...),
    /// <see cref="ShownTextContext.Text"/> that text (in the player's language when a translation renamed it), <see cref="ShownTextContext.Tag"/>
    /// the item's code-mod tag for one item ("" none). Items, buildings, research, races, factions, towns, body parts, starts, biomes and the rest,
    /// wherever the game shows them, including names put into messages. A character, squad or place still called by its record's name is asked
    /// here too, as that record's <c>name</c>.
    /// </summary>
    Record,

    /// <summary>
    /// A name of its own, not its record's: a character's (rolled from a name list, or given by the player), a squad's, an outpost's, the player's
    /// faction's. <see cref="ShownTextContext.Field"/> says what it names (<c>character</c>, <c>squad</c>, <c>outpost</c>, <c>faction</c>,
    /// <c>place</c>), <see cref="ShownTextContext.Key"/> the record it was made from ("" none), <see cref="ShownTextContext.Text"/> the name,
    /// <see cref="ShownTextContext.Tag"/> "".
    /// </summary>
    Name,

    /// <summary>
    /// A dialogue line's text, before its tokens are filled: <see cref="ShownTextContext.Key"/> is the line's record string id,
    /// <see cref="ShownTextContext.Text"/> the text the line was given (one of its texts, tokens such as <c>/YOURNAME/</c> still in it),
    /// <see cref="ShownTextContext.Tag"/> "". The names its tokens fill in are asked on their own, as <see cref="Record"/> or <see cref="Name"/>.
    /// </summary>
    DialogueLine,
}

/// <summary>
/// Where a character's name is being shown (<see cref="ShownTextContext.Place"/>), so a source can give a name one way in some places and another
/// way, or leave it, in others (a title on the head label but not in dialogue).
/// </summary>
public enum ShownPlace : byte
{
    /// <summary>
    /// Not said: anything other than a character's name, and a caller that does not tell.
    /// </summary>
    Unknown,

    /// <summary>
    /// The name above a character's head in the world.
    /// </summary>
    Label,

    /// <summary>
    /// The squad bar's portraits and the squad lists.
    /// </summary>
    Squad,

    /// <summary>
    /// The character's own panels and windows: the selected character's status panel, its inventory and stats windows, its jobs list.
    /// </summary>
    Panel,

    /// <summary>
    /// Other windows that name characters: trade, slaves and animals for sale, crafting, prospecting, faction information and the rest.
    /// </summary>
    Window,

    /// <summary>
    /// A tooltip (an item's owner, a crafter).
    /// </summary>
    Tooltip,

    /// <summary>
    /// The right-click menu's title.
    /// </summary>
    Menu,

    /// <summary>
    /// The game's notices and messages, the floaters over the world and the event log.
    /// </summary>
    Notice,

    /// <summary>
    /// Conversation: the dialogue window and its log, speech bubbles, dialogue choices, and a name a line's token fills in (<c>/YOURNAME/</c>).
    /// </summary>
    Dialogue,
}

/// <summary>
/// What a mod's text source gets: the kind of text, its key, the text it would show (the game's, or an earlier source's), a tag and a field, whose
/// meanings each <see cref="ShownTextKind"/> gives; for a character's name also which character and where it is shown.
/// </summary>
public sealed record ShownTextContext(ShownTextKind Kind, string Key, string Text, string Tag, string Field)
{
    /// <summary>
    /// For a character's name, the character's lasting id (<c>LastingId.Value</c>, docs/modding.md "Character ids"); 0 for any other text and for
    /// a character no mod has given an id. A source that names one character alone gives it an id first and answers by it.
    /// </summary>
    public ulong Character { get; init; }

    /// <summary>
    /// For a character's name, where it is shown; <see cref="ShownPlace.Unknown"/> for any other text.
    /// </summary>
    public ShownPlace Place { get; init; }
}

/// <summary>
/// The mods' text sources (docs/modding.md "Shown text"): asked when the game shows a message, a record's text, a name or a dialogue line, each in
/// registration order getting the text so far and able to replace it. Display only: records, saves, the state hash and every decision the game makes
/// keep the game's own text. Free when no mod adds one (one check). Answers are kept until <see cref="Refresh"/>, so a source runs once per text,
/// not every frame. It may be asked from any thread (the simulation's messages and dialogue are made on its threads), one at a time.
/// </summary>
public static class ShownText
{
    #region Sources

    /// <summary>
    /// The sources, with the interface that registered them and their owners, in registration order; replaced whole on a change, so a reader needs
    /// no lock.
    /// </summary>
    static volatile (object Host, string Owner, Func<ShownTextContext, string?> Source)[] s_sources = [];

    /// <summary>
    /// Held while the sources are changed or asked, so they run one at a time.
    /// </summary>
    static readonly Lock Asking = new();

    /// <summary>
    /// Told when a source throws and is dropped: the interface that added it, its owner, what it was asked and the error. May be called on any
    /// thread.
    /// </summary>
    public static event Action<object, string, ShownTextContext, Exception>? Dropped;

    /// <summary>
    /// Whether any mod adds a text source.
    /// </summary>
    public static bool Any => s_sources.Length > 0;

    /// <summary>
    /// Adds <paramref name="owner"/>'s source after the others, for the interface <paramref name="host"/> (the engine's; a mod adds one through its
    /// interface registration). The game has one interface at a time: the sources of an earlier one, which a new world's interface replaced without
    /// removing them, are dropped.
    /// </summary>
    public static void Add(object host, string owner, Func<ShownTextContext, string?> source)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(source);
        lock (Asking)
        {
            s_sources = [.. Array.FindAll(s_sources, s => ReferenceEquals(s.Host, host)), (host, owner, source)];
            Forget();
        }
    }

    /// <summary>
    /// Removes the sources <paramref name="owner"/> added for <paramref name="host"/>.
    /// </summary>
    public static void Remove(object host, string owner)
    {
        lock (Asking)
        {
            var kept = Array.FindAll(s_sources, s => !ReferenceEquals(s.Host, host) || s.Owner != owner);
            if (kept.Length == s_sources.Length) return;
            s_sources = kept;
            Forget();
        }
    }

    /// <summary>
    /// Removes every source added for <paramref name="host"/> (an interface that is done).
    /// </summary>
    public static void Remove(object host)
    {
        lock (Asking)
        {
            var kept = Array.FindAll(s_sources, s => !ReferenceEquals(s.Host, host));
            if (kept.Length == s_sources.Length) return;
            s_sources = kept;
            Forget();
        }
    }

    #endregion

    #region Asking

    /// <summary>
    /// The field of a <see cref="ShownTextKind.Record"/> that is the record's name.
    /// </summary>
    public const string RecordName = "name";

    /// <summary>
    /// The most answers kept; past it the cache starts over (tagged items and texts the code builds can make the set open-ended), and only the texts
    /// shown after that are asked again.
    /// </summary>
    const int MaxCached = 16384;

    /// <summary>
    /// The texts shown so far.
    /// </summary>
    static readonly ConcurrentDictionary<Asked, string> Answers = new();

    /// <summary>
    /// How many answers <see cref="Answers"/> holds (its own count locks every bucket).
    /// </summary>
    static int s_count;

    /// <summary>
    /// True on a thread inside a source, so a source that shows a text itself (through <see cref="Loc.Tr(string)"/>) gets the game's text.
    /// </summary>
    [ThreadStatic] static bool s_inside;

    /// <summary>
    /// Counts the times the answers were forgotten (<see cref="Revision"/>).
    /// </summary>
    static volatile int s_revision;

    /// <summary>
    /// Moves whenever shown text may have changed (a source added, removed or dropped, <see cref="Refresh"/>): a view that keeps shown text
    /// compares it with the value it was built at and rebuilds when it differs.
    /// </summary>
    public static int Revision => s_revision;

    /// <summary>
    /// Forgets the texts shown so far, so every source is asked again (a source whose answers changed). Texts already handed out (a dialogue line
    /// said, a notice posted) keep what they were given.
    /// </summary>
    public static void Refresh()
    {
        lock (Asking) Forget();
    }

    /// <summary>
    /// Record <paramref name="record"/>'s <paramref name="field"/> (<see cref="RecordName"/> for its name), <paramref name="text"/>, as shown, for
    /// one item tagged <paramref name="tag"/> ("" none); see <see cref="ShownTextKind.Record"/>.
    /// </summary>
    public static string Record(string record, string text, string field = RecordName, string tag = "") =>
        s_sources.Length == 0 ? text : Ask((ShownTextKind.Record, record ?? "", text, tag ?? "", field ?? "", 0, ShownPlace.Unknown));

    /// <summary>
    /// The name of a <paramref name="what"/> (<c>character</c>, <c>squad</c>, <c>outpost</c>, <c>faction</c>, <c>place</c>) made from
    /// <paramref name="record"/> ("" none) whose record is called <paramref name="recordName"/>, as shown: a name that is still its record's is
    /// asked as that record's name (<see cref="ShownTextKind.Record"/>), any other as a <see cref="ShownTextKind.Name"/>.
    /// </summary>
    public static string Name(string what, string record, string recordName, string name) =>
        Name(what, record, recordName, name, 0, ShownPlace.Unknown);

    /// <summary>
    /// As <see cref="Name(string, string, string, string)"/>, for the name of one character: <paramref name="character"/> its lasting id (0: none
    /// given), <paramref name="place"/> where it is shown. Answers are kept per character and place.
    /// </summary>
    public static string Name(string what, string record, string recordName, string name, ulong character, ShownPlace place)
    {
        if (s_sources.Length == 0) return name;

        bool asRecord = record.Length > 0 && string.Equals(name, recordName, StringComparison.Ordinal);
        return asRecord
            ? Ask((ShownTextKind.Record, record, name, "", RecordName, character, place))
            : Ask((ShownTextKind.Name, record, name, "", what, character, place));
    }

    /// <summary>
    /// The text shown for <paramref name="text"/> of <paramref name="kind"/> under <paramref name="key"/>, <paramref name="tag"/> and
    /// <paramref name="field"/>: each source in registration order gets the text so far and may replace it (null or blank: keep it); the result is
    /// cut (a name to <see cref="MaxName"/> characters on one line, anything else to <see cref="MaxText"/>) and kept until <see cref="Refresh"/>. A
    /// source that throws is dropped and reported through <see cref="Dropped"/>. With no source, or a blank <paramref name="text"/>, the text as it
    /// is.
    /// </summary>
    public static string Of(ShownTextKind kind, string key, string text, string tag = "", string field = "") =>
        s_sources.Length == 0 ? text : Ask((kind, key ?? "", text, tag ?? "", field ?? "", 0, ShownPlace.Unknown));

    /// <summary>
    /// The text shown for <paramref name="asked"/> (<see cref="Of"/>): the answer kept, else each source asked.
    /// </summary>
    static string Ask(Asked asked)
    {
        if (s_sources.Length == 0 || s_inside || string.IsNullOrWhiteSpace(asked.Text)) return asked.Text;
        if (Answers.TryGetValue(asked, out var known)) return known;

        lock (Asking)
        {
            if (Answers.TryGetValue(asked, out known)) return known;

            string shown = AskSources(asked);
            Store(asked, shown);
            return shown;
        }
    }

    /// <summary>
    /// Asks each source in registration order about <paramref name="asked"/>, each getting the text so far; returns the final text. Under
    /// <see cref="Asking"/>.
    /// </summary>
    static string AskSources(Asked asked)
    {
        string shown = asked.Text;
        s_inside = true;
        try
        {
            foreach (var (host, owner, source) in s_sources) shown = AskSource(host, owner, source, asked, shown);
        }
        finally
        {
            s_inside = false;
        }

        return shown;
    }

    /// <summary>
    /// Asks one <paramref name="source"/> about <paramref name="asked"/>, with <paramref name="shown"/> the text so far; returns its cleaned answer,
    /// or <paramref name="shown"/> when it gave none. A source that throws is dropped and reported through <see cref="Dropped"/>.
    /// </summary>
    static string AskSource(object host, string owner, Func<ShownTextContext, string?> source, Asked asked, string shown)
    {
        var context = new ShownTextContext(asked.Kind, asked.Key, shown, asked.Tag, asked.Field) { Character = asked.Character, Place = asked.Place };
        try
        {
            string? given = source(context);
            if (given is null) return shown;

            string clean = Clean(OneLine(asked.Kind, asked.Field), given);
            return string.IsNullOrWhiteSpace(clean) ? shown : clean;
        }
        catch (Exception e)
        {
            s_sources = Array.FindAll(s_sources, s => !ReferenceEquals(s.Source, source));
            Forget();
            Dropped?.Invoke(host, owner, context, e);
            return shown;
        }
    }

    /// <summary>
    /// Keeps <paramref name="shown"/> as the answer to <paramref name="asked"/>, starting the cache over first when it is full. Under
    /// <see cref="Asking"/>.
    /// </summary>
    static void Store(Asked asked, string shown)
    {
        if (s_count >= MaxCached) Forget(changed: false);
        if (Answers.TryAdd(asked, shown)) s_count++;
    }

    /// <summary>
    /// Empties the cache (under <see cref="Asking"/>); with <paramref name="changed"/> (not a cache that only grew too big), moves
    /// <see cref="Revision"/>.
    /// </summary>
    static void Forget(bool changed = true)
    {
        Answers.Clear();
        s_count = 0;
        if (changed) s_revision++;
    }

    #endregion

    #region Cleaning

    /// <summary>
    /// The most characters a shown name keeps (one line).
    /// </summary>
    public const int MaxName = 80;

    /// <summary>
    /// The most characters a shown message or dialogue line keeps.
    /// </summary>
    public const int MaxText = 4000;

    /// <summary>
    /// Whether text of <paramref name="kind"/> and <paramref name="field"/> is a name, kept to one line: a <see cref="ShownTextKind.Name"/>, or a
    /// record's <c>name</c>.
    /// </summary>
    static bool OneLine(ShownTextKind kind, string field) =>
        kind == ShownTextKind.Name || (kind == ShownTextKind.Record && field == RecordName);

    /// <summary>
    /// A source's answer as shown: a name on one line (line breaks, the interface's written <c>\n</c> and tabs made spaces) and cut to
    /// <see cref="MaxName"/>; any other text cut to <see cref="MaxText"/>. Never cut between the two halves of a character outside the basic plane.
    /// A blank result keeps the text so far.
    /// </summary>
    static string Clean(bool oneLine, string given)
    {
        if (!oneLine) return Cut(given, MaxText);
        return Cut(given, MaxName).ReplaceLineEndings(" ").Replace(@"\n", " ", StringComparison.Ordinal).Replace('\t', ' ').Trim();
    }

    /// <summary>
    /// <paramref name="text"/> cut to at most <paramref name="max"/> characters, never between the two halves of a surrogate pair.
    /// </summary>
    static string Cut(string text, int max)
    {
        if (text.Length <= max) return text;
        return text[..(char.IsHighSurrogate(text[max - 1]) ? max - 1 : max)];
    }

    #endregion
}
