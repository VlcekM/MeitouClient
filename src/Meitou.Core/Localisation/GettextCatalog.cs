using System.Buffers.Binary;
using System.Text;

namespace Meitou.Localisation;

/// <summary>
/// A gettext message catalog (docs/formats/gettext.md): the binary <c>.mo</c> the original loads through boost::locale
/// (<c>locale/&lt;code&gt;/LC_MESSAGES/main.mo</c>), with a <c>.po</c> text reader as a fallback. Keys are the English source
/// strings; a message that has a translation returns it, any other returns null here and the English at the call site.
/// </summary>
public sealed class GettextCatalog
{
    /// <summary>Separator of a message context and its text in a key (<c>msgctxt</c> + 0x04 + <c>msgid</c>).</summary>
    const char ContextSeparator = '\u0004';

    // Key (msgid, or msgctxt 0x04 msgid) -> translated forms; a message without plural has one form.
    readonly Dictionary<string, string[]> _messages = new(StringComparer.Ordinal);

    // The same messages keyed without case, made on demand (see FindIgnoringCase): a UI layout captions a section "SKILLS" while the catalog's key is "Skills".
    Dictionary<string, string[]>? _byCase;
    int _byCaseVersion = -1;
    int _version;

    /// <summary>Stores one message and marks the case-free index stale.</summary>
    void Set(string key, string[] forms)
    {
        _messages[key] = forms;
        _version++;
    }

    public PluralRule Plurals { get; private set; } = PluralRule.Germanic;

    /// <summary>How many messages have a translation.</summary>
    public int Count => _messages.Count;

    /// <summary>A catalog with no messages: every lookup returns null.</summary>
    public static GettextCatalog Empty { get; } = new();

    /// <summary>
    /// The catalog in <paramref name="messagesDirectory"/> (<c>.../LC_MESSAGES</c>): <c>main.mo</c> as the original reads it, else
    /// <c>main.po</c>. An unreadable or missing file gives <see cref="Empty"/> (the game then shows the English source strings).
    /// </summary>
    public static GettextCatalog Load(string? messagesDirectory)
    {
        if (messagesDirectory is null) return Empty;
        try
        {
            var mo = Path.Combine(messagesDirectory, "main.mo");
            if (File.Exists(mo)) return ReadMo(File.ReadAllBytes(mo));
            var po = Path.Combine(messagesDirectory, "main.po");
            if (File.Exists(po)) return ReadPo(File.ReadAllText(po, Encoding.UTF8));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine($"locale    {messagesDirectory}: {e.Message}; the English texts are used");
        }
        return Empty;
    }

    /// <summary>Every message's key and first form.</summary>
    public IEnumerable<KeyValuePair<string, string>> Messages => _messages.Select(m => new KeyValuePair<string, string>(m.Key, m.Value[0]));

    /// <summary>Puts <paramref name="later"/>'s messages over this catalog's (a later file overrides an earlier one); the plural rule stays.</summary>
    public void Add(GettextCatalog later)
    {
        foreach (var (key, forms) in later._messages) Set(key, forms);
    }

    /// <summary>The translation of <paramref name="msgid"/> (first form), or null when the catalog has none.</summary>
    public string? Find(string msgid, string? context = null) =>
        _messages.TryGetValue(context is null ? msgid : context + ContextSeparator + msgid, out var forms) ? forms[0] : null;

    /// <summary>
    /// The translation of <paramref name="msgid"/> ignoring case, or null: a UI layout captions what the catalog keys, but in capitals
    /// (<c>"SKILLS"</c>, <c>"WEAPON I"</c>). Built once after the catalog's last change, without a lock: UI thread only.
    /// </summary>
    public string? FindIgnoringCase(string msgid)
    {
        if (_byCaseVersion != _version || _byCase is null)
        {
            // Touched by hand: the catalog can hold the same word in two cases ("Map" and "MAP"), so the first spelling wins instead of throwing.
            _byCase = new(StringComparer.OrdinalIgnoreCase);
            foreach (var message in _messages) _byCase.TryAdd(message.Key, message.Value);
            _byCaseVersion = _version;
        }
        return _byCase.TryGetValue(msgid, out var forms) ? forms[0] : null;
    }

    /// <summary>The translated plural form for <paramref name="n"/> of the message whose singular source text is <paramref name="singular"/>, or null.</summary>
    public string? FindPlural(string singular, long n, string? context = null)
    {
        if (!_messages.TryGetValue(context is null ? singular : context + ContextSeparator + singular, out var forms)) return null;
        int index = Plurals.Index(n);
        return forms[index < forms.Length ? index : forms.Length - 1];
    }

    /// <summary>The catalog of a <c>.mo</c> file (either byte order; revision 0 or 1).</summary>
    public static GettextCatalog ReadMo(byte[] data)
    {
        if (data.Length < 28) throw new InvalidDataException("not a .mo file (too short)");
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        bool little = magic == 0x950412DE;
        if (!little && magic != 0xDE120495) throw new InvalidDataException("not a .mo file (bad magic number)");
        uint U32(int at) => at < 0 || at + 4 > data.Length ? throw new InvalidDataException("truncated .mo file")
            : little ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
        if (U32(4) >> 16 > 1) throw new InvalidDataException("unsupported .mo revision");
        long count = U32(8);
        long originals = U32(12), translations = U32(16);
        if (originals + count * 8 > data.Length || translations + count * 8 > data.Length) throw new InvalidDataException("truncated .mo file");

        ReadOnlySpan<byte> String(long table, long i)
        {
            long length = U32((int)(table + i * 8)), offset = U32((int)(table + i * 8 + 4));
            if (offset + length > data.Length) throw new InvalidDataException("truncated .mo file");
            return data.AsSpan((int)offset, (int)length);
        }

        var catalog = new GettextCatalog();
        for (long i = 0; i < count; i++)
        {
            var original = String(originals, i);
            var translated = String(translations, i);
            if (original.Length == 0)
            {
                catalog.ReadHeader(Encoding.UTF8.GetString(translated));
                continue;
            }
            // "context \x04 singular \0 plural" -> the key is "context \x04 singular"; the translation holds one string per form.
            int zero = original.IndexOf((byte)0);
            string key = Encoding.UTF8.GetString(zero < 0 ? original : original[..zero]);
            var forms = Encoding.UTF8.GetString(translated).Split('\0');
            if (forms.Length == 0 || forms[0].Length == 0) continue;   // untranslated: msgfmt leaves these out, a hand-made file might not
            catalog.Set(key, forms);
        }
        return catalog;
    }

    /// <summary>
    /// The catalog of a <c>.po</c> file: entries with a non-empty <c>msgstr</c> that are not marked fuzzy or obsolete (what <c>msgfmt</c> compiles).
    /// </summary>
    public static GettextCatalog ReadPo(string text)
    {
        var catalog = new GettextCatalog();
        string? context = null, id = null;
        var plural = new List<string>();
        var current = new StringBuilder();
        string? field = null;
        bool fuzzy = false;
        int pluralIndex = 0;

        void Flush()
        {
            if (field is null) return;
            Store(field, pluralIndex);
            field = null;
        }

        void Store(string name, int index)
        {
            string value = current.ToString();
            current.Clear();
            switch (name)
            {
                case "msgctxt": context = value; break;
                case "msgid": id = value; break;
                case "msgstr":
                    plural.Clear();
                    plural.Add(value);
                    break;
                case "msgstr[]":
                    while (plural.Count <= index) plural.Add("");
                    plural[index] = value;
                    break;
            }
        }

        void Finish()
        {
            Flush();
            if (id is not null)
            {
                if (id.Length == 0)
                {
                    if (plural.Count > 0) catalog.ReadHeader(plural[0]);
                }
                else if (!fuzzy && plural.Count > 0 && plural[0].Length > 0)
                    catalog.Set(context is null ? id : context + ContextSeparator + id, [.. plural]);
            }
            context = id = null;
            plural.Clear();
            fuzzy = false;
            pluralIndex = 0;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) { Finish(); continue; }
            if (line.StartsWith("#~", StringComparison.Ordinal)) continue;   // obsolete entry
            if (line[0] == '#')
            {
                // A comment after the entry's text starts the next entry.
                if (field is not null || id is not null) Finish();
                if (line.StartsWith("#,", StringComparison.Ordinal) && line.Contains("fuzzy", StringComparison.Ordinal)) fuzzy = true;
                continue;
            }
            if (line[0] == '"')
            {
                current.Append(Unquote(line));
                continue;
            }
            Flush();
            int space = line.IndexOf(' ');
            if (space < 0) continue;
            string keyword = line[..space];
            if (keyword == "msgctxt" || keyword == "msgid" || keyword == "msgid_plural" || keyword == "msgstr")
            {
                if (keyword == "msgctxt" && (id is not null || plural.Count > 0)) Finish();
                if (keyword == "msgid" && plural.Count > 0) Finish();
                field = keyword;
                current.Clear().Append(Unquote(line[(space + 1)..]));
            }
            else if (keyword.StartsWith("msgstr[", StringComparison.Ordinal) && keyword.EndsWith(']')
                && int.TryParse(keyword.AsSpan(7, keyword.Length - 8), out int index))
            {
                field = "msgstr[]";
                pluralIndex = index;
                current.Clear().Append(Unquote(line[(space + 1)..]));
            }
        }
        Finish();
        return catalog;
    }

    // The header message (msgid ""): "Content-Type: ...; charset=..." and "Plural-Forms: ...".
    void ReadHeader(string header)
    {
        foreach (var line in header.Split('\n'))
            if (line.StartsWith("Plural-Forms:", StringComparison.OrdinalIgnoreCase))
                Plurals = PluralRule.Parse(line["Plural-Forms:".Length..]);
    }

    // The text inside the quotes of a .po string, with the C escapes resolved.
    static string Unquote(string quoted)
    {
        quoted = quoted.Trim();
        if (quoted.Length < 2 || quoted[0] != '"') return "";
        int end = quoted.LastIndexOf('"');
        if (end < 1) return "";
        var inner = quoted.AsSpan(1, end - 1);
        if (!inner.Contains('\\')) return inner.ToString();
        var result = new StringBuilder(inner.Length);
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c != '\\' || i + 1 >= inner.Length) { result.Append(c); continue; }
            char next = inner[++i];
            result.Append(next switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                'a' => '\a',
                'b' => '\b',
                'f' => '\f',
                'v' => '\v',
                _ => next,   // \" \\ and unknown escapes
            });
        }
        return result.ToString();
    }
}