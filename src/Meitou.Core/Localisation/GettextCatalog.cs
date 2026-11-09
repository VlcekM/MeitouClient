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
    /// <summary>
    /// Separator of a message context and its text in a key (<c>msgctxt</c> + 0x04 + <c>msgid</c>).
    /// </summary>
    const char ContextSeparator = '\u0004';

    /// <summary>
    /// Key (msgid, or msgctxt 0x04 msgid) -> translated forms; a message without plural has one form.
    /// </summary>
    readonly Dictionary<string, string[]> _messages = new(StringComparer.Ordinal);

    /// <summary>
    /// The same messages keyed without case, made on demand (see FindIgnoringCase): a UI layout captions a section "SKILLS" while the catalog's key
    /// is "Skills".
    /// </summary>
    Dictionary<string, string[]>? _byCase;

    /// <summary>
    /// The <see cref="_version"/> <see cref="_byCase"/> was made at.
    /// </summary>
    int _byCaseVersion = -1;

    /// <summary>
    /// Bumped by every stored message.
    /// </summary>
    int _version;

    /// <summary>
    /// Stores one message and marks the case-free index stale.
    /// </summary>
    void Set(string key, string[] forms)
    {
        _messages[key] = forms;
        _version++;
    }

    /// <summary>
    /// The plural rule of the catalog's <c>Plural-Forms</c> header; <see cref="PluralRule.Germanic"/> until a header names one.
    /// </summary>
    public PluralRule Plurals { get; private set; } = PluralRule.Germanic;

    /// <summary>
    /// How many messages have a translation.
    /// </summary>
    public int Count => _messages.Count;

    /// <summary>
    /// A catalog with no messages: every lookup returns null.
    /// </summary>
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

    /// <summary>
    /// Every message's key and first form.
    /// </summary>
    public IEnumerable<KeyValuePair<string, string>> Messages => _messages.Select(m => new KeyValuePair<string, string>(m.Key, m.Value[0]));

    /// <summary>
    /// Puts <paramref name="later"/>'s messages over this catalog's (a later file overrides an earlier one); the plural rule stays.
    /// </summary>
    public void Add(GettextCatalog later)
    {
        foreach (var (key, forms) in later._messages) Set(key, forms);
    }

    /// <summary>
    /// The translation of <paramref name="msgid"/> (first form), or null when the catalog has none.
    /// </summary>
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

    /// <summary>
    /// The translated plural form for <paramref name="n"/> of the message whose singular source text is <paramref name="singular"/>, or null.
    /// </summary>
    public string? FindPlural(string singular, long n, string? context = null)
    {
        if (!_messages.TryGetValue(context is null ? singular : context + ContextSeparator + singular, out var forms))
            return null;
        int index = Plurals.Index(n);
        return forms[index < forms.Length ? index : forms.Length - 1];
    }

    /// <summary>
    /// The catalog of a <c>.mo</c> file (either byte order; revision 0 or 1).
    /// </summary>
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
        if (originals + count * 8 > data.Length || translations + count * 8 > data.Length)
            throw new InvalidDataException("truncated .mo file");

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
            if (forms.Length == 0 || forms[0].Length == 0)
                continue;   // untranslated: msgfmt leaves these out, a hand-made file might not
            catalog.Set(key, forms);
        }
        return catalog;
    }

    /// <summary>
    /// The catalog of a <c>.po</c> file: entries with a non-empty <c>msgstr</c> that are not marked fuzzy or obsolete (what <c>msgfmt</c> compiles).
    /// </summary>
    public static GettextCatalog ReadPo(string text)
    {
        var reader = new PoReader();
        foreach (var raw in text.Split('\n')) reader.ReadLine(raw.Trim());
        reader.Finish();
        return reader.Catalog;
    }

    /// <summary>
    /// Reads a <c>.po</c> file line by line into <see cref="Catalog"/>: the entry being read, and the field whose quoted text is being gathered.
    /// </summary>
    sealed class PoReader
    {
        /// <summary>
        /// The entry's <c>msgctxt</c>, null when it has none.
        /// </summary>
        string? _context;

        /// <summary>
        /// The entry's <c>msgid</c>, null until read.
        /// </summary>
        string? _id;

        /// <summary>
        /// The entry's translated forms (<c>msgstr</c>, or <c>msgstr[n]</c> by index).
        /// </summary>
        readonly List<string> _plural = [];

        /// <summary>
        /// The text of the field being read, its continuation lines appended.
        /// </summary>
        readonly StringBuilder _current = new();

        /// <summary>
        /// The keyword of the field being read (<c>msgstr[]</c> for an indexed form), null between fields.
        /// </summary>
        string? _field;

        /// <summary>
        /// The entry is marked <c>#, fuzzy</c>.
        /// </summary>
        bool _fuzzy;

        /// <summary>
        /// The index of the <c>msgstr[n]</c> being read.
        /// </summary>
        int _pluralIndex;

        /// <summary>
        /// The messages read so far.
        /// </summary>
        public GettextCatalog Catalog { get; } = new();

        /// <summary>
        /// Reads one trimmed line: a blank line or a comment after the entry's text ends the entry, a quoted line continues the field, a keyword
        /// starts one.
        /// </summary>
        public void ReadLine(string line)
        {
            if (line.Length == 0)
            {
                Finish();
                return;
            }
            if (line.StartsWith("#~", StringComparison.Ordinal)) return;   // obsolete entry
            if (line[0] == '#')
            {
                // A comment after the entry's text starts the next entry.
                if (_field is not null || _id is not null) Finish();
                if (line.StartsWith("#,", StringComparison.Ordinal) && line.Contains("fuzzy", StringComparison.Ordinal))
                    _fuzzy = true;
                return;
            }
            if (line[0] == '"')
            {
                _current.Append(Unquote(line));
                return;
            }
            Flush();
            ReadKeyword(line);
        }

        /// <summary>
        /// Ends the entry: stores it when it has a translation (the empty <c>msgid</c> is the header), then clears it.
        /// </summary>
        public void Finish()
        {
            Flush();
            if (_id is not null)
            {
                if (_id.Length == 0)
                {
                    if (_plural.Count > 0) Catalog.ReadHeader(_plural[0]);
                }
                else if (!_fuzzy && _plural.Count > 0 && _plural[0].Length > 0)
                    Catalog.Set(_context is null ? _id : _context + ContextSeparator + _id, [.. _plural]);
            }
            _context = _id = null;
            _plural.Clear();
            _fuzzy = false;
            _pluralIndex = 0;
        }

        /// <summary>
        /// A keyword line (<c>msgctxt</c>, <c>msgid</c>, <c>msgid_plural</c>, <c>msgstr</c>, <c>msgstr[n]</c>) starts its field; a
        /// <c>msgctxt</c> or <c>msgid</c> after an entry's text starts the next entry. Other lines are ignored.
        /// </summary>
        void ReadKeyword(string line)
        {
            int space = line.IndexOf(' ');
            if (space < 0) return;
            string keyword = line[..space];
            if (keyword == "msgctxt" || keyword == "msgid" || keyword == "msgid_plural" || keyword == "msgstr")
            {
                if (keyword == "msgctxt" && (_id is not null || _plural.Count > 0)) Finish();
                if (keyword == "msgid" && _plural.Count > 0) Finish();
                _field = keyword;
                _current.Clear().Append(Unquote(line[(space + 1)..]));
            }
            else if (keyword.StartsWith("msgstr[", StringComparison.Ordinal) && keyword.EndsWith(']')
                && int.TryParse(keyword.AsSpan(7, keyword.Length - 8), out int index))
            {
                _field = "msgstr[]";
                _pluralIndex = index;
                _current.Clear().Append(Unquote(line[(space + 1)..]));
            }
        }

        /// <summary>
        /// Stores the field being read, if any.
        /// </summary>
        void Flush()
        {
            if (_field is null) return;
            Store(_field, _pluralIndex);
            _field = null;
        }

        /// <summary>
        /// Stores the gathered text as field <paramref name="name"/> (form <paramref name="index"/> of <c>msgstr[]</c>).
        /// </summary>
        void Store(string name, int index)
        {
            string value = _current.ToString();
            _current.Clear();
            switch (name)
            {
                case "msgctxt":
                    _context = value;
                    break;
                case "msgid":
                    _id = value;
                    break;
                case "msgstr":
                    _plural.Clear();
                    _plural.Add(value);
                    break;
                case "msgstr[]":
                    while (_plural.Count <= index) _plural.Add("");
                    _plural[index] = value;
                    break;
            }
        }
    }

    /// <summary>
    /// Reads the header message (msgid ""): "Content-Type: ...; charset=..." and "Plural-Forms: ...".
    /// </summary>
    void ReadHeader(string header)
    {
        foreach (var line in header.Split('\n'))
        {
            if (line.StartsWith("Plural-Forms:", StringComparison.OrdinalIgnoreCase))
                Plurals = PluralRule.Parse(line["Plural-Forms:".Length..]);
        }
    }

    /// <summary>
    /// The text inside the quotes of a .po string, with the C escapes resolved.
    /// </summary>
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
            if (c != '\\' || i + 1 >= inner.Length)
            {
                result.Append(c);
                continue;
            }
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