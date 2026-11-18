// Port of upstream/ProvingGrounds/plugin/src/PGConfig.cpp (Proving Grounds by Shutori, GPLv3): the rapidjson reader emulated over System.Text.Json.
using System.Globalization;
using System.Text.Json;

namespace ProvingGrounds.Core;

public static partial class PGConfig
{
    // ---- rapidjson emulation over System.Text.Json -----------------------------------------------------------------------------

    /// <summary>Why a parse failed: the byte offset (from the parse start, i.e. after a BOM) and a reason text.</summary>
    readonly record struct RjParseError(long Offset, string Message);

    /// <summary>A parsed document, as <c>rapidjson::Document::Parse&lt;kParseCommentsFlag | kParseTrailingCommasFlag&gt;(text.c_str()
    /// + start)</c> accepts it: <c>//</c> and <c>/* */</c> comments and trailing commas; NaN/Infinity rejected; the text ends at the
    /// first NUL byte (c_str); a number that does not fit a double is an error (rapidjson kParseErrorNumberTooBig).</summary>
    sealed class RjDocument : IDisposable
    {
        readonly JsonDocument _document;
        RjDocument(JsonDocument document) { _document = document; }
        public Rj Root => new Rj(_document.RootElement);
        public void Dispose() => _document.Dispose();

        static readonly JsonDocumentOptions Options = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            // comments are blanked before parsing (BlankComments); none reach the parser
            CommentHandling = JsonCommentHandling.Disallow,
            // rapidjson's reader has no depth limit; a 1 MiB file cannot nest deeper than this
            MaxDepth = 2 * 1024 * 1024,
        };

        public static RjDocument? Parse(byte[] text, int start, out RjParseError error)
        {
            int end = Array.IndexOf(text, (byte)0, start);
            if (end < 0) end = text.Length;
            // Comments are blanked in a COPY (the caller's bytes, which the settings save splices, are untouched), then parsed with
            // comments disallowed: rapidjson's kParseCommentsFlag skips a comment wherever it skips whitespace, including between a
            // member name and its ':', which System.Text.Json's own comment handling rejects.
            byte[] copy = new byte[end - start];
            Array.Copy(text, start, copy, 0, copy.Length);
            long unterminated = BlankComments(copy);
            if (unterminated >= 0)
            {
                // rapidjson: an unterminated /* */ is kParseErrorUnspecificSyntaxError
                error = new RjParseError(unterminated, "Unspecific syntax error.");
                return null;
            }
            var slice = new ReadOnlyMemory<byte>(copy);
            // rapidjson: kParseErrorDocumentEmpty "The document is empty." at the end of the leading whitespace/comments
            long empty = SkipWhitespace(slice.Span);
            if (empty == slice.Length)
            {
                error = new RjParseError(empty, "The document is empty.");
                return null;
            }
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(slice, Options);
            }
            catch (JsonException e)
            {
                error = new RjParseError(Offset(slice.Span, e.LineNumber ?? 0, e.BytePositionInLine ?? 0), Reason(e.Message));
                return null;
            }
            // rapidjson errors System.Text.Json defers: a number too big for a double, and a string escape it cannot decode
            string? late = Validate(document.RootElement);
            if (late != null)
            {
                document.Dispose();
                error = new RjParseError(0, late);
                return null;
            }
            error = default;
            return new RjDocument(document);
        }

        /// <summary>Replaces every comment outside a string with spaces, offsets unchanged, as rapidjson's SkipWhitespaceAndComments
        /// reads them: <c>//</c> runs to the next '\n' only (a lone '\r' does not end it, so a CR-only file's line comment swallows the
        /// rest of the file, as in the original), the '\n' itself is kept; <c>/* */</c> is blanked through its "*/". A '/' followed by
        /// anything else is left for the parser to reject. Strings are skipped with their escapes (an unterminated one is left to
        /// the parser). Returns the offset of an unterminated block comment, else -1.</summary>
        static long BlankComments(byte[] s)
        {
            int pos = 0;
            while (pos < s.Length)
            {
                byte b = s[pos];
                if (b == '"')
                {
                    ++pos;
                    while (pos < s.Length)
                    {
                        if (s[pos] == '\\') { pos += 2; continue; }
                        if (s[pos++] == '"') break;
                    }
                    continue;
                }
                if (b == '/' && pos + 1 < s.Length && s[pos + 1] == '/')
                {
                    while (pos < s.Length && s[pos] != '\n') s[pos++] = (byte)' ';
                    continue;
                }
                if (b == '/' && pos + 1 < s.Length && s[pos + 1] == '*')
                {
                    int open = pos;
                    int close = -1;
                    for (int i = pos + 2; i + 1 < s.Length; ++i)
                        if (s[i] == '*' && s[i + 1] == '/') { close = i; break; }
                    if (close < 0) return open;
                    for (; pos < close + 2; ++pos) s[pos] = (byte)' ';
                    continue;
                }
                ++pos;
            }
            return -1;
        }

        // Runs on the blanked copy (BlankComments has already replaced every comment with spaces), so only whitespace is left to skip
        // (rapidjson's whitespace: space, tab, '\n', '\r').
        static long SkipWhitespace(ReadOnlySpan<byte> s)
        {
            int pos = 0;
            while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\n' || s[pos] == '\r'))
                ++pos;
            return pos;
        }

        static long Offset(ReadOnlySpan<byte> s, long line, long bytePositionInLine)
        {
            long pos = 0;
            for (long l = 0; l < line && pos < s.Length; ++pos)
                if (s[(int)pos] == '\n') ++l;
            return pos + bytePositionInLine;
        }

        static string Reason(string message)
        {
            int cut = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
            if (cut < 0) cut = message.IndexOf(" Path:", StringComparison.Ordinal);
            return (cut < 0 ? message : message.Substring(0, cut)).TrimEnd();
        }

        static string? Validate(JsonElement root)
        {
            var stack = new Stack<JsonElement>();
            stack.Push(root);
            try
            {
                while (stack.Count > 0)
                {
                    JsonElement e = stack.Pop();
                    switch (e.ValueKind)
                    {
                    case JsonValueKind.Object:
                        foreach (JsonProperty p in e.EnumerateObject()) { _ = p.Name; stack.Push(p.Value); }
                        break;
                    case JsonValueKind.Array:
                        foreach (JsonElement item in e.EnumerateArray()) stack.Push(item);
                        break;
                    case JsonValueKind.String:
                        _ = e.GetString();
                        break;
                    case JsonValueKind.Number:
                        if (!double.IsFinite(Rj.ParseDouble(e.GetRawText())))
                            return "Number too big to be stored in double.";
                        break;
                    }
                }
            }
            catch (InvalidOperationException e)
            {
                return e.Message;
            }
            return null;
        }
    }

    /// <summary>A view of one value with the rapidjson <c>GenericValue</c> queries PGConfig.cpp uses.</summary>
    readonly struct Rj
    {
        readonly JsonElement _e;
        public Rj(JsonElement e) { _e = e; }

        public bool IsObject => _e.ValueKind == JsonValueKind.Object;
        public bool IsString => _e.ValueKind == JsonValueKind.String;
        public bool IsBool => _e.ValueKind == JsonValueKind.True || _e.ValueKind == JsonValueKind.False;
        public bool GetBool() => _e.ValueKind == JsonValueKind.True;
        public bool IsNumber => _e.ValueKind == JsonValueKind.Number;

        // rapidjson types a number by its literal: no '.', 'e' or 'E' and the value fits int32 -> kIntFlag (also "-0"); anything
        // with a fraction or an exponent ("5.0", "1e2") is a double, IsInt false; an integer beyond int32 is uint/int64/double.
        public bool IsInt => IsNumber && IntLiteral(_e.GetRawText(), out _);
        public int GetInt() { IntLiteral(_e.GetRawText(), out int value); return value; }
        public double GetDouble() => ParseDouble(_e.GetRawText());

        // GetString() is a const char*: cut at the first NUL like every std::string / strcmp the C++ builds from it
        public string GetString() => CStr(_e.GetString() ?? "");

        /// <summary><c>HasMember(name)</c> + <c>operator[](name)</c>: rapidjson's FindMember returns the FIRST member of that name
        /// (System.Text.Json's TryGetProperty returns the last), compared over the whole length.</summary>
        public bool TryMember(string name, out Rj value)
        {
            foreach (JsonProperty p in _e.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.Ordinal))
                {
                    value = new Rj(p.Value);
                    return true;
                }
            value = default;
            return false;
        }

        /// <summary>MemberBegin..MemberEnd in document order, duplicates included; names uncut (callers apply GetString's cut).</summary>
        public IEnumerable<(string Name, Rj Value)> Members()
        {
            foreach (JsonProperty p in _e.EnumerateObject())
                yield return (p.Name, new Rj(p.Value));
        }

        static readonly char[] NonIntegerChars = { '.', 'e', 'E' };

        static bool IntLiteral(string raw, out int value)
        {
            value = 0;
            if (raw.IndexOfAny(NonIntegerChars) >= 0)
                return false;
            if (!long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number) ||
                number < int.MinValue || number > int.MaxValue)
                return false;
            value = (int)number;
            return true;
        }

        public static double ParseDouble(string raw) => double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
