namespace Meitou.Data.Ogre;

/// <summary>
/// What a script token is (Ogre's <c>ScriptToken</c> types).
/// </summary>
public enum OgreScriptTokenType
{
    /// <summary>
    /// A run of characters up to whitespace, a newline, a brace or a colon.
    /// </summary>
    Word,
    /// <summary>
    /// A quoted phrase; the lexeme still includes the quotes.
    /// </summary>
    Quote,
    /// <summary>
    /// A <c>$name</c> variable reference; the lexeme includes the <c>$</c>.
    /// </summary>
    Variable,
    /// <summary>
    /// A <c>{</c>.
    /// </summary>
    LeftBrace,
    /// <summary>
    /// A <c>}</c>.
    /// </summary>
    RightBrace,
    /// <summary>
    /// A <c>:</c>.
    /// </summary>
    Colon,
    /// <summary>
    /// The end of a line (a carriage return counts too).
    /// </summary>
    Newline,
}

/// <summary>
/// A token: its type, its text, and the file and line it came from.
/// </summary>
public sealed record OgreScriptToken(OgreScriptTokenType Type, string Lexeme, string File, int Line)
{
    public override string ToString() => $"{Type} '{Lexeme}' ({File}:{Line})";
}

/// <summary>
/// A script error that stops a whole file, as an exception in Ogre does (lexer and parser errors).
/// </summary>
public sealed class OgreScriptException(string message, string file, int line)
    : FormatException($"{file}({line}): {message}")
{
    /// <summary>
    /// The script file the error is in.
    /// </summary>
    public string File { get; } = file;
    /// <summary>
    /// The line the error is at.
    /// </summary>
    public int Line { get; } = line;
}

/// <summary>
/// Splits an Ogre script (<c>.material</c>, <c>.program</c>, <c>.compositor</c>...) into tokens. Follows the state machine of Ogre's ScriptLexer
/// (MIT), including its quirks; see docs/formats/ogre-material.md.
/// </summary>
public static class OgreScriptLexer
{
    /// <summary>
    /// Where the lexer's state machine is (Ogre's ScriptLexer states).
    /// </summary>
    enum State
    {
        /// <summary>
        /// Between tokens.
        /// </summary>
        Ready,

        /// <summary>
        /// Inside a line comment, up to the newline.
        /// </summary>
        Comment,

        /// <summary>
        /// Inside a block comment, up to its closing star and slash.
        /// </summary>
        MultiComment,

        /// <summary>
        /// Inside a word.
        /// </summary>
        Word,

        /// <summary>
        /// Inside a quoted phrase.
        /// </summary>
        Quote,

        /// <summary>
        /// Inside a <c>$variable</c>.
        /// </summary>
        Variable,

        /// <summary>
        /// After a word's leading slash: a second slash or a star makes it a comment.
        /// </summary>
        PossibleComment
    }

    /// <summary>
    /// Script files are read byte for byte (Latin-1), as Ogre does: no encoding or BOM handling. A few base-game files contain non-ASCII bytes in
    /// comments.
    /// </summary>
    public static string ReadText(string path) => System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(Content.GameFiles.Resolve(path)));

    /// <summary>
    /// The tokens of the script <paramref name="text"/> of <paramref name="file"/>; <see cref="OgreScriptException"/> for a quote left open.
    /// </summary>
    public static List<OgreScriptToken> Tokenize(string text, string file)
    {
        var lexer = new Lexer(file);
        foreach (char c in text) lexer.Next(c);
        return lexer.Finish();
    }

    /// <summary>
    /// Whitespace that separates tokens (a carriage return is also a newline).
    /// </summary>
    static bool IsWhitespace(char c) => c is ' ' or '\r' or '\t';

    /// <summary>
    /// The characters that end a line and make a newline token.
    /// </summary>
    static bool IsNewline(char c) => c is '\n' or '\r';

    /// <summary>
    /// The state machine over one script: the tokens so far, the lexeme being read, the state and the line count.
    /// </summary>
    ref struct Lexer(string file)
    {
        /// <summary>
        /// The tokens emitted so far.
        /// </summary>
        readonly List<OgreScriptToken> _tokens = [];

        /// <summary>
        /// The text of the token being read.
        /// </summary>
        readonly System.Text.StringBuilder _lexeme = new();

        /// <summary>
        /// The file name the tokens carry.
        /// </summary>
        readonly string _file = file;

        /// <summary>
        /// Where the state machine is.
        /// </summary>
        State _state = State.Ready;

        /// <summary>
        /// The current line, and the line of the last quote character (for the unmatched-quote error).
        /// </summary>
        int _line = 1, _lastQuote = 0;

        /// <summary>
        /// The character read before the current one.
        /// </summary>
        char _previous = '\0';

        /// <summary>
        /// Feeds one character through the state machine.
        /// </summary>
        public void Next(char c)
        {
            char lastc = _previous;
            _previous = c;
            if (c == '"') _lastQuote = _line;

            switch (_state)
            {
                case State.Ready:
                    Ready(c, lastc);
                    break;
                case State.Comment:
                    Comment(c);
                    break;
                case State.MultiComment:
                    // Newlines inside a block comment produce no token.
                    if (c == '/' && lastc == '*') _state = State.Ready;
                    break;
                case State.PossibleComment:
                    PossibleComment(c, lastc);
                    break;
                case State.Word:
                case State.Variable:
                    WordOrVariable(c);
                    break;
                case State.Quote:
                    Quote(c, lastc);
                    break;
            }

            if (c == '\r' || (c == '\n' && lastc != '\r')) _line++;
        }

        /// <summary>
        /// Ends the text: a word or variable still being read is emitted, an open quote throws.
        /// </summary>
        public readonly List<OgreScriptToken> Finish()
        {
            // As in Ogre, a lone '/' at the very end (PossibleComment) is dropped.
            if (_state is State.Word or State.Variable)
            {
                if (_lexeme.Length > 0) Emit();
            }
            else if (_state == State.Quote)
                throw new OgreScriptException($"no matching \" found for \" at line {_lastQuote}", _file, _lastQuote);
            return _tokens;
        }

        /// <summary>
        /// Between tokens: a comment, quote or variable starts, a newline is a token, any other non-whitespace starts a word.
        /// </summary>
        void Ready(char c, char lastc)
        {
            if (c == '/' && lastc == '/')
            {
                _lexeme.Clear();
                _state = State.Comment;
            }
            else if (c == '*' && lastc == '/')
            {
                _lexeme.Clear();
                _state = State.MultiComment;
            }
            else if (c == '"')
            {
                _lexeme.Clear().Append(c);
                _state = State.Quote;
            }
            else if (c == '$')
            {
                _lexeme.Clear().Append(c);
                _state = State.Variable;
            }
            else if (IsNewline(c)) EmitChar(c);
            else if (!IsWhitespace(c))
            {
                // Note: braces and colons also start a word here; only the characters after the first
                // one split on them.
                _lexeme.Clear().Append(c);
                _state = c == '/' ? State.PossibleComment : State.Word;
            }
        }

        /// <summary>
        /// A line comment: it ends at the newline, which is still a token.
        /// </summary>
        void Comment(char c)
        {
            if (IsNewline(c))
            {
                EmitChar(c);
                _state = State.Ready;
            }
        }

        /// <summary>
        /// After a word's leading '/': a second '/' or a '*' makes it a comment, anything else goes on with the word.
        /// </summary>
        void PossibleComment(char c, char lastc)
        {
            if (c == '/' && lastc == '/')
            {
                _lexeme.Clear();
                _state = State.Comment;
                return;
            }
            if (c == '*' && lastc == '/')
            {
                _lexeme.Clear();
                _state = State.MultiComment;
                return;
            }
            _state = State.Word;
            WordOrVariable(c);
        }

        /// <summary>
        /// Inside a quote: a backslash escapes the next character (and is kept), an unescaped quote ends the token.
        /// </summary>
        void Quote(char c, char lastc)
        {
            if (c != '\\')
            {
                if (c == '"' && lastc == '\\') _lexeme.Append(c);
                else if (c == '"')
                {
                    _lexeme.Append(c);
                    Emit();
                    _state = State.Ready;
                }
                else if (lastc == '\\') _lexeme.Append('\\').Append(c);
                else _lexeme.Append(c);
            }
        }

        /// <summary>
        /// Inside a word or variable: a newline, whitespace, brace or colon ends it (the newline, brace or colon as a token of its own).
        /// </summary>
        void WordOrVariable(char ch)
        {
            if (IsNewline(ch))
            {
                Emit();
                EmitChar(ch);
                _state = State.Ready;
            }
            else if (IsWhitespace(ch))
            {
                Emit();
                _state = State.Ready;
            }
            else if (ch is '{' or '}' or ':')
            {
                Emit();
                EmitChar(ch);
                _state = State.Ready;
            }
            else _lexeme.Append(ch);
        }

        /// <summary>
        /// Adds the lexeme as a token of the type its text gives.
        /// </summary>
        readonly void Emit()
        {
            var s = _lexeme.ToString();
            OgreScriptTokenType type;
            if (s is "\n" or "\r")
            {
                // Runs of newlines collapse into one token.
                if (_tokens.Count > 0 && _tokens[^1].Type == OgreScriptTokenType.Newline) return;
                type = OgreScriptTokenType.Newline;
            }
            else if (s == "{") type = OgreScriptTokenType.LeftBrace;
            else if (s == "}") type = OgreScriptTokenType.RightBrace;
            else if (s == ":") type = OgreScriptTokenType.Colon;
            else if (s.Length > 0 && s[0] == '$') type = OgreScriptTokenType.Variable;
            else if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') type = OgreScriptTokenType.Quote;
            else type = OgreScriptTokenType.Word;
            _tokens.Add(new OgreScriptToken(type, s, _file, _line));
        }

        /// <summary>
        /// Emits one character as a token.
        /// </summary>
        readonly void EmitChar(char ch)
        {
            _lexeme.Clear().Append(ch);
            Emit();
        }
    }
}
