namespace Meitou.Data.Ogre;

public enum OgreScriptTokenType
{
    Word,
    /// <summary>A quoted phrase; the lexeme still includes the quotes.</summary>
    Quote,
    /// <summary>A <c>$name</c> variable reference; the lexeme includes the <c>$</c>.</summary>
    Variable,
    LeftBrace,
    RightBrace,
    Colon,
    Newline,
}

public sealed record OgreScriptToken(OgreScriptTokenType Type, string Lexeme, string File, int Line)
{
    public override string ToString() => $"{Type} '{Lexeme}' ({File}:{Line})";
}

/// <summary>A script error that stops a whole file, as an exception in Ogre does (lexer and parser errors).</summary>
public sealed class OgreScriptException(string message, string file, int line)
    : FormatException($"{file}({line}): {message}")
{
    public string File { get; } = file;
    public int Line { get; } = line;
}

/// <summary>
/// Splits an Ogre script (<c>.material</c>, <c>.program</c>, <c>.compositor</c>...) into tokens. Follows the
/// state machine of Ogre's ScriptLexer (MIT), including its quirks; see docs/formats/ogre-material.md.
/// </summary>
public static class OgreScriptLexer
{
    enum State { Ready, Comment, MultiComment, Word, Quote, Variable, PossibleComment }

    /// <summary>
    /// Script files are read byte for byte (Latin-1), as Ogre does: no encoding or BOM handling. A few
    /// base-game files contain non-ASCII bytes in comments.
    /// </summary>
    public static string ReadText(string path) => System.Text.Encoding.Latin1.GetString(System.IO.File.ReadAllBytes(path));

    public static List<OgreScriptToken> Tokenize(string text, string file)
    {
        var tokens = new List<OgreScriptToken>();
        var lexeme = new System.Text.StringBuilder();
        var state = State.Ready;
        int line = 1, lastQuote = 0;
        char c = '\0';

        void Emit()
        {
            var s = lexeme.ToString();
            OgreScriptTokenType type;
            if (s is "\n" or "\r")
            {
                // Runs of newlines collapse into one token.
                if (tokens.Count > 0 && tokens[^1].Type == OgreScriptTokenType.Newline) return;
                type = OgreScriptTokenType.Newline;
            }
            else if (s == "{") type = OgreScriptTokenType.LeftBrace;
            else if (s == "}") type = OgreScriptTokenType.RightBrace;
            else if (s == ":") type = OgreScriptTokenType.Colon;
            else if (s.Length > 0 && s[0] == '$') type = OgreScriptTokenType.Variable;
            else if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') type = OgreScriptTokenType.Quote;
            else type = OgreScriptTokenType.Word;
            tokens.Add(new OgreScriptToken(type, s, file, line));
        }

        void EmitChar(char ch)
        {
            lexeme.Clear().Append(ch);
            Emit();
        }

        foreach (char ch in text)
        {
            char lastc = c;
            c = ch;
            if (c == '"') lastQuote = line;

            switch (state)
            {
                case State.Ready:
                    if (c == '/' && lastc == '/') { lexeme.Clear(); state = State.Comment; }
                    else if (c == '*' && lastc == '/') { lexeme.Clear(); state = State.MultiComment; }
                    else if (c == '"') { lexeme.Clear().Append(c); state = State.Quote; }
                    else if (c == '$') { lexeme.Clear().Append(c); state = State.Variable; }
                    else if (IsNewline(c)) EmitChar(c);
                    else if (!IsWhitespace(c))
                    {
                        // Note: braces and colons also start a word here; only the characters after the first
                        // one split on them.
                        lexeme.Clear().Append(c);
                        state = c == '/' ? State.PossibleComment : State.Word;
                    }
                    break;
                case State.Comment:
                    if (IsNewline(c)) { EmitChar(c); state = State.Ready; }
                    break;
                case State.MultiComment:
                    // Newlines inside a block comment produce no token.
                    if (c == '/' && lastc == '*') state = State.Ready;
                    break;
                case State.PossibleComment:
                    if (c == '/' && lastc == '/') { lexeme.Clear(); state = State.Comment; break; }
                    if (c == '*' && lastc == '/') { lexeme.Clear(); state = State.MultiComment; break; }
                    state = State.Word;
                    WordOrVariable(c);
                    break;
                case State.Word:
                case State.Variable:
                    WordOrVariable(c);
                    break;
                case State.Quote:
                    if (c != '\\')
                    {
                        if (c == '"' && lastc == '\\') lexeme.Append(c);
                        else if (c == '"') { lexeme.Append(c); Emit(); state = State.Ready; }
                        else if (lastc == '\\') lexeme.Append('\\').Append(c);
                        else lexeme.Append(c);
                    }
                    break;
            }

            if (c == '\r' || (c == '\n' && lastc != '\r')) line++;
        }

        // As in Ogre, a lone '/' at the very end (PossibleComment) is dropped.
        if (state is State.Word or State.Variable)
        {
            if (lexeme.Length > 0) Emit();
        }
        else if (state == State.Quote)
            throw new OgreScriptException($"no matching \" found for \" at line {lastQuote}", file, lastQuote);
        return tokens;

        void WordOrVariable(char ch)
        {
            if (IsNewline(ch))
            {
                Emit();
                EmitChar(ch);
                state = State.Ready;
            }
            else if (IsWhitespace(ch))
            {
                Emit();
                state = State.Ready;
            }
            else if (ch is '{' or '}' or ':')
            {
                Emit();
                EmitChar(ch);
                state = State.Ready;
            }
            else lexeme.Append(ch);
        }
    }

    static bool IsWhitespace(char c) => c is ' ' or '\r' or '\t';
    static bool IsNewline(char c) => c is '\n' or '\r';
}
