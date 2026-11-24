using System.Collections.Concurrent;

namespace Meitou.Localisation;

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
/// What a mod's text source gets: the kind of text, its key, the text it would show (the game's, or an earlier source's), a tag and a field, whose
/// meanings each <see cref="ShownTextKind"/> gives.
/// </summary>
public sealed record ShownTextContext(ShownTextKind Kind, string Key, string Text, string Tag, string Field);

/// <summary>
/// The mods' text sources (docs/modding.md "Shown text"): asked when the game shows a message, a record's text, a name or a dialogue line, each in
/// registration order getting the text so far and able to replace it. Display only: records, saves, the state hash and every decision the game makes
/// keep the game's own text. Free when no mod adds one (one check). Answers are kept until <see cref="Refresh"/>, so a source runs once per text,
/// not every frame. It may be asked from any thread (the simulation's messages and dialogue are made on its threads), one at a time.
/// </summary>
public static class ShownText
{
    /// <summary>
    /// The most characters a shown name keeps (one line).
    /// </summary>
    public const int MaxName = 80;

    /// <summary>
    /// The most characters a shown message or dialogue line keeps.
    /// </summary>
    public const int MaxText = 4000;

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
    /// The sources, with the interface that registered them and their owners, in registration order; replaced whole on a change, so a reader needs
    /// no lock.
    /// </summary>
    static volatile (object Host, string Owner, Func<ShownTextContext, string?> Source)[] s_sources = [];

    /// <summary>
    /// The texts shown so far.
    /// </summary>
    static readonly ConcurrentDictionary<(ShownTextKind Kind, string Key, string Text, string Tag, string Field), string> Answers = new();

    /// <summary>
    /// How many answers <see cref="Answers"/> holds (its own count locks every bucket).
    /// </summary>
    static int s_count;

    /// <summary>
    /// Held while the sources are asked, so they run one at a time.
    /// </summary>
    static readonly Lock Asking = new();

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
    /// Whether any mod adds a text source.
    /// </summary>
    public static bool Any => s_sources.Length > 0;

    /// <summary>
    /// Told when a source throws and is dropped: the interface that added it, its owner, what it was asked and the error. May be called on any
    /// thread.
    /// </summary>
    public static event Action<object, string, ShownTextContext, Exception>? Dropped;

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
        s_sources.Length == 0 ? text : Of(ShownTextKind.Record, record, text, tag, field);

    /// <summary>
    /// The name of a <paramref name="what"/> (<c>character</c>, <c>squad</c>, <c>outpost</c>, <c>faction</c>, <c>place</c>) made from
    /// <paramref name="record"/> ("" none) whose record is called <paramref name="recordName"/>, as shown: a name that is still its record's is
    /// asked as that record's name (<see cref="ShownTextKind.Record"/>), any other as a <see cref="ShownTextKind.Name"/>.
    /// </summary>
    public static string Name(string what, string record, string recordName, string name)
    {
        if (s_sources.Length == 0) return name;
        return record.Length > 0 && string.Equals(name, recordName, StringComparison.Ordinal)
            ? Of(ShownTextKind.Record, record, name, "", RecordName)
            : Of(ShownTextKind.Name, record, name, "", what);
    }

    /// <summary>
    /// The text shown for <paramref name="text"/> of <paramref name="kind"/> under <paramref name="key"/>, <paramref name="tag"/> and
    /// <paramref name="field"/>: each source in registration order gets the text so far and may replace it (null or blank: keep it); the result is
    /// cut (a name to <see cref="MaxName"/> characters on one line, anything else to <see cref="MaxText"/>) and kept until <see cref="Refresh"/>. A
    /// source that throws is dropped and reported through <see cref="Dropped"/>. With no source, or a blank <paramref name="text"/>, the text as it
    /// is.
    /// </summary>
    public static string Of(ShownTextKind kind, string key, string text, string tag = "", string field = "")
    {
        if (s_sources.Length == 0 || s_inside || string.IsNullOrWhiteSpace(text)) return text;

        var asked = (kind, key ?? "", text, tag ?? "", field ?? "");
        if (Answers.TryGetValue(asked, out var known)) return known;

        lock (Asking)
        {
            if (Answers.TryGetValue(asked, out known)) return known;

            string shown = text;
            s_inside = true;
            try
            {
                foreach (var (host, owner, source) in s_sources)
                {
                    var context = new ShownTextContext(kind, asked.Item2, shown, asked.Item4, asked.Item5);
                    try
                    {
                        string? given = source(context);
                        if (given is not null && Clean(OneLine(kind, asked.Item5), given) is var clean && !string.IsNullOrWhiteSpace(clean))
                            shown = clean;
                    }
                    catch (Exception e)
                    {
                        s_sources = Array.FindAll(s_sources, s => !ReferenceEquals(s.Source, source));
                        Forget();
                        Dropped?.Invoke(host, owner, context, e);
                    }
                }
            }
            finally
            {
                s_inside = false;
            }

            if (s_count >= MaxCached) Forget(changed: false);
            if (Answers.TryAdd(asked, shown)) s_count++;
            return shown;
        }
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
}
