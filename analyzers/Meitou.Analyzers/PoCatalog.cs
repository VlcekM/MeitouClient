using System.Text.RegularExpressions;

namespace Meitou.Analyzers;

/// <summary>
/// One built-in catalog (src/Meitou.Core/Localisation/locale/&lt;code&gt;.po) and why it cannot translate a message, as
/// .github/scripts/loc-strings.mjs audits it (audit, problem; parsePo of po.mjs).
/// </summary>
sealed class PoCatalog
{
    /// <summary>
    /// One field line of an entry: its keyword (with a plural form's index) and its quoted text.
    /// </summary>
    static readonly Regex FieldLine = new(@"^(msgctxt|msgid_plural|msgid|msgstr(?:\[(\d+)\])?)[ \t]+""(.*)""$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The header's count of plural forms.
    /// </summary>
    static readonly Regex PluralCount = new(@"nplurals\s*=\s*(\d+)", RegexOptions.ECMAScript);

    /// <summary>
    /// A flags comment that marks the entry fuzzy (the runtime skips it).
    /// </summary>
    static readonly Regex Fuzzy = new(@"^#,.*\bfuzzy\b", RegexOptions.ECMAScript);

    /// <summary>
    /// A {N...} placeholder.
    /// </summary>
    static readonly Regex Placeholder = new(@"\{\d+[^{}]*\}", RegexOptions.ECMAScript);

    /// <summary>
    /// An escape of a gettext quoted string.
    /// </summary>
    static readonly Regex Escape = new(@"\\(.)", RegexOptions.CultureInvariant);

    /// <summary>
    /// The entries by context and id (a later duplicate replaces an earlier one).
    /// </summary>
    readonly Dictionary<(string Context, string Id), PoEntry> _entries = [];

    /// <summary>
    /// The language code: the file name without .po.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// The number of plural forms the language has (2 when the header does not say).
    /// </summary>
    public int PluralForms { get; }

    /// <summary>
    /// Reads catalog <paramref name="code"/> from its text.
    /// </summary>
    public PoCatalog(string code, string text)
    {
        Code = code;
        Match count = PluralCount.Match(text);
        PluralForms = count.Success ? int.Parse(count.Groups[1].Value) : 2;
        foreach (PoEntry entry in Parse(text))
            _entries[(entry.Context ?? "", entry.Id!)] = entry;
    }

    /// <summary>
    /// Why this catalog cannot translate <paramref name="message"/> (the runtime skips fuzzy entries and shows an empty form as nothing), or
    /// null when it can.
    /// </summary>
    public string? Problem(LocMessage message)
    {
        if (!_entries.TryGetValue((message.Context ?? "", message.Id), out PoEntry? entry)) return "no entry";
        if (entry.Comments.Any(c => Fuzzy.IsMatch(c))) return "fuzzy";
        if (message.Plural is not null && entry.Plural is null) return "singular entry for a plural message";
        if (message.Plural is null && entry.Plural is not null) return "plural entry for a singular message";

        List<string?> forms = message.Plural is null ? entry.Forms.Take(1).ToList() : entry.Forms;
        int wanted = message.Plural is null ? 1 : PluralForms;
        if (forms.Count != wanted || forms.Any(string.IsNullOrEmpty))
            return message.Plural is null ? "empty" : "needs " + PluralForms + " non-empty forms";

        return forms
            .Select(f => FormProblem(message, f!))
            .FirstOrDefault(p => p is not null);
    }

    /// <summary>
    /// Why one translated form does not fit the English: a placeholder it adds or drops, or a leading or trailing space that differs.
    /// </summary>
    static string? FormProblem(LocMessage message, string form)
    {
        var allowed = new HashSet<string>(Placeholders(message.Id).Concat(Placeholders(message.Plural ?? "")), StringComparer.Ordinal);
        List<string> used = Placeholders(form);
        string? foreign = used.FirstOrDefault(p => !allowed.Contains(p));
        if (foreign is not null) return "placeholder " + foreign + " is not in the English";
        if (message.Plural is null && used.Count != allowed.Count) return "drops a placeholder";
        if (Edges(form) != Edges(message.Id)) return "leading or trailing space or line break differs";

        return null;
    }

    /// <summary>
    /// The distinct placeholders of <paramref name="text"/>, sorted.
    /// </summary>
    static List<string> Placeholders(string text)
    {
        return Placeholder.Matches(text)
            .Cast<Match>()
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Whether <paramref name="text"/> starts and ends with a space or line break, as a short code.
    /// </summary>
    static string Edges(string text)
    {
        bool leading = text.Length > 0 && text[0] is ' ' or '\n';
        bool trailing = text.Length > 0 && text[text.Length - 1] is ' ' or '\n';
        return (leading ? "1" : "0") + (trailing ? "1" : "0");
    }

    /// <summary>
    /// The entries of a .po file; the header (empty msgid) is left out.
    /// </summary>
    static List<PoEntry> Parse(string text)
    {
        var parser = new PoParser();
        foreach (string line in text.Split('\n'))
            parser.Read(line.TrimEnd());

        parser.Flush();
        return parser.Entries;
    }

    /// <summary>
    /// Reads a .po file line by line: a blank line ends an entry, as does a comment after a field or a msgctxt or msgid after the msgid.
    /// </summary>
    sealed class PoParser
    {
        /// <summary>
        /// The entries read so far.
        /// </summary>
        public List<PoEntry> Entries { get; } = [];

        /// <summary>
        /// The entry being read.
        /// </summary>
        PoEntry _entry = new();

        /// <summary>
        /// The field a continuation line extends, or null.
        /// </summary>
        string? _field;

        /// <summary>
        /// Reads one line, its trailing whitespace trimmed.
        /// </summary>
        public void Read(string line)
        {
            if (line.Length == 0)
            {
                Flush();
                return;
            }

            if (line[0] == '#')
            {
                if (_field is not null) Flush();
                _entry.Comments.Add(line);
                return;
            }

            ReadField(line);
        }

        /// <summary>
        /// Keeps the entry when it has a non-empty msgid, and starts a fresh one.
        /// </summary>
        public void Flush()
        {
            if (!string.IsNullOrEmpty(_entry.Id)) Entries.Add(_entry);

            _entry = new PoEntry();
            _field = null;
        }

        /// <summary>
        /// Reads a field line or a continuation of the current field.
        /// </summary>
        void ReadField(string line)
        {
            Match m = FieldLine.Match(line);
            if (!m.Success)
            {
                if (line[0] == '"' && _field is not null)
                    Set(_entry, _field, null, Unquote(line.Substring(1, Math.Max(0, line.Length - 2))), append: true);
                return;
            }

            string keyword = m.Groups[1].Value;
            if (keyword is "msgctxt" or "msgid" && _entry.Id is not null) Flush();

            _field = keyword;
            Set(_entry, keyword, m.Groups[2].Success ? m.Groups[2].Value : null, Unquote(m.Groups[3].Value), append: false);
        }
    }

    /// <summary>
    /// Sets or extends one field of <paramref name="entry"/>; a msgstr without an index is form 0, a continuation extends the last form.
    /// </summary>
    static void Set(PoEntry entry, string field, string? index, string value, bool append)
    {
        if (field == "msgctxt") entry.Context = append ? entry.Context + value : value;
        else if (field == "msgid") entry.Id = append ? entry.Id + value : value;
        else if (field == "msgid_plural") entry.Plural = append ? entry.Plural + value : value;
        else SetForm(entry, string.IsNullOrEmpty(index) ? (append ? entry.Forms.Count - 1 : 0) : int.Parse(index), value, append);
    }

    /// <summary>
    /// Sets or extends translated form <paramref name="i"/>.
    /// </summary>
    static void SetForm(PoEntry entry, int i, string value, bool append)
    {
        if (i < 0) return;

        while (entry.Forms.Count <= i)
            entry.Forms.Add(null);

        entry.Forms[i] = append ? (entry.Forms[i] ?? "") + value : value;
    }

    /// <summary>
    /// The text of a gettext quoted string's inside.
    /// </summary>
    static string Unquote(string s)
    {
        return Escape.Replace(s, UnquoteEscape);
    }

    /// <summary>
    /// The character one escape stands for (an unknown escape keeps its character).
    /// </summary>
    static string UnquoteEscape(Match match)
    {
        return match.Groups[1].Value switch
        {
            "n" => "\n",
            "t" => "\t",
            "r" => "\r",
            string e => e,
        };
    }
}
