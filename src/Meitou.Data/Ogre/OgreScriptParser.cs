namespace Meitou.Data.Ogre;

public enum OgreConcreteNodeType { Word, Quote, Variable, VariableAssign, Import, LeftBrace, RightBrace, Colon }

/// <summary>
/// Ogre's "concrete" syntax tree: one node per word, with the rest of its line (and a following <c>{ }</c> block) as children.
/// <see cref="OgreScriptCompiler"/> turns it into objects and properties.
/// </summary>
public sealed class OgreConcreteNode
{
    public OgreConcreteNodeType Type { get; init; }

    /// <summary>
    /// The token text; quotes are stripped for <see cref="OgreConcreteNodeType.Quote"/> (except in base lists).
    /// </summary>
    public string Token { get; set; } = "";

    public string File { get; init; } = "";
    public int Line { get; init; }
    public OgreConcreteNode? Parent { get; set; }
    public List<OgreConcreteNode> Children { get; } = [];

    public override string ToString() => $"{Type} '{Token}' ({File}:{Line})";
}

/// <summary>
/// Builds the concrete tree from tokens, following Ogre's ScriptParser (MIT) state machine. Malformed <c>import</c>, <c>set</c> or <c>:</c> lines
/// throw <see cref="OgreScriptException"/>, which in Ogre aborts the file.
/// </summary>
public static class OgreScriptParser
{
    public static List<OgreConcreteNode> Parse(string text, string file) => Parse(OgreScriptLexer.Tokenize(text, file));

    public static List<OgreConcreteNode> Parse(IReadOnlyList<OgreScriptToken> tokens)
    {
        var parser = new Parser(tokens);
        return parser.Run();
    }

    /// <summary>
    /// Parses a variable's value for expansion (Ogre's ScriptParser::parseChunk): words and variables only. A quoted phrase throws, because Ogre's
    /// switch falls through into its error case.
    /// </summary>
    public static List<OgreConcreteNode> ParseChunk(IReadOnlyList<OgreScriptToken> tokens)
    {
        var nodes = new List<OgreConcreteNode>();
        foreach (var token in tokens)
        {
            nodes.Add(token.Type switch
            {
                OgreScriptTokenType.Variable => New(OgreConcreteNodeType.Variable, token, token.Lexeme),
                OgreScriptTokenType.Word => New(OgreConcreteNodeType.Word, token, token.Lexeme),
                _ => throw new OgreScriptException($"unexpected token {token.Lexeme} at line {token.Line}", token.File, token.Line),
            });
        }
        return nodes;
    }

    static OgreConcreteNode New(OgreConcreteNodeType type, OgreScriptToken token, string text) =>
        new() { Type = type, Token = text, File = token.File, Line = token.Line };

    static OgreConcreteNode Child(OgreConcreteNode parent, OgreScriptToken token, string? quotedText) => new()
    {
        Type = token.Type == OgreScriptTokenType.Word ? OgreConcreteNodeType.Word : OgreConcreteNodeType.Quote,
        Token = token.Type == OgreScriptTokenType.Quote ? quotedText ?? token.Lexeme[1..^1] : token.Lexeme,
        File = token.File,
        Line = token.Line,
        Parent = parent,
    };

    static string SafeSubstring(string s, int start, int length) =>
        start >= s.Length ? "" : s.Substring(start, Math.Min(Math.Max(length, 0), s.Length - start));

    static int SkipNewlines(IReadOnlyList<OgreScriptToken> tokens, int i)
    {
        while (i < tokens.Count && tokens[i].Type == OgreScriptTokenType.Newline) i++;
        return i;
    }

    /// <summary>
    /// The state machine over one token list: the nodes made, the node new ones go into, whether a word's line is being read, and the token index
    /// (the steps that read ahead move it).
    /// </summary>
    ref struct Parser(IReadOnlyList<OgreScriptToken> tokens)
    {
        /// <summary>
        /// The tokens being parsed.
        /// </summary>
        readonly IReadOnlyList<OgreScriptToken> _tokens = tokens;

        /// <summary>
        /// The top-level nodes.
        /// </summary>
        readonly List<OgreConcreteNode> _nodes = [];

        /// <summary>
        /// True while the rest of a word's line is read as its children.
        /// </summary>
        bool _inObject;

        /// <summary>
        /// The node new nodes are added to (null: the top level).
        /// </summary>
        OgreConcreteNode? _parent;

        /// <summary>
        /// The token being read.
        /// </summary>
        int _i;

        /// <summary>
        /// Parses every token; returns the top-level nodes.
        /// </summary>
        public List<OgreConcreteNode> Run()
        {
            for (_i = 0; _i < _tokens.Count; _i++)
            {
                var token = _tokens[_i];
                if (!_inObject) LineStart(token);
                else InObject(token);
            }
            return _nodes;
        }

        /// <summary>
        /// A token at the start of a line: <c>import</c>, <c>set</c>, a word that opens a node, or a closing brace.
        /// </summary>
        void LineStart(OgreScriptToken token)
        {
            if (token.Type == OgreScriptTokenType.Word)
            {
                if (token.Lexeme == "import") Import(token);
                else if (token.Lexeme == "set") Set(token);
                else
                {
                    var node = New(OgreConcreteNodeType.Word, token, token.Lexeme);
                    Insert(node);
                    _parent = node;
                    _inObject = true;
                }
            }
            else if (token.Type == OgreScriptTokenType.RightBrace)
            {
                // Up from the '{' to its owner, attach the '}' there, then up again.
                _parent = _parent?.Parent;
                Insert(New(OgreConcreteNodeType.RightBrace, token, token.Lexeme));
                _parent = _parent?.Parent;
            }
            // Quotes, variables, '{' and ':' at the start of a line are ignored, as in Ogre.
        }

        /// <summary>
        /// <c>import target from source</c>.
        /// </summary>
        void Import(OgreScriptToken token)
        {
            var node = New(OgreConcreteNodeType.Import, token, token.Lexeme);
            _i++;
            if (_i >= _tokens.Count || _tokens[_i].Type is not (OgreScriptTokenType.Word or OgreScriptTokenType.Quote))
                throw new OgreScriptException($"expected import target at line {node.Line}", node.File, node.Line);
            // Ogre strips a quoted import target using the length of the "import" token instead of the
            // target's own (ScriptParser::parse), so `import "Foo" from ...` asks for `Foo"`. Kept as is.
            node.Children.Add(Child(node, _tokens[_i],
                _tokens[_i].Type == OgreScriptTokenType.Quote ? SafeSubstring(_tokens[_i].Lexeme, 1, token.Lexeme.Length - 2) : null));
            // The word in between ("from") is skipped without being checked.
            _i += 2;
            if (_i >= _tokens.Count || _tokens[_i].Type is not (OgreScriptTokenType.Word or OgreScriptTokenType.Quote))
                throw new OgreScriptException($"expected import source at line {node.Line}", node.File, node.Line);
            node.Children.Add(Child(node, _tokens[_i], null));
            Insert(node);
        }

        /// <summary>
        /// <c>set $name value</c>.
        /// </summary>
        void Set(OgreScriptToken token)
        {
            var node = New(OgreConcreteNodeType.VariableAssign, token, token.Lexeme);
            _i++;
            if (_i >= _tokens.Count || _tokens[_i].Type != OgreScriptTokenType.Variable)
                throw new OgreScriptException($"expected variable name at line {node.Line}", node.File, node.Line);
            node.Children.Add(new OgreConcreteNode
            {
                Type = OgreConcreteNodeType.Variable,
                Token = _tokens[_i].Lexeme,
                File = _tokens[_i].File,
                Line = _tokens[_i].Line,
                Parent = node,
            });
            _i++;
            if (_i >= _tokens.Count || _tokens[_i].Type is not (OgreScriptTokenType.Word or OgreScriptTokenType.Quote))
                throw new OgreScriptException($"expected variable value at line {node.Line}", node.File, node.Line);
            node.Children.Add(Child(node, _tokens[_i], null));
            Insert(node);
        }

        /// <summary>
        /// A token on a word's line: words, quotes and variables become its children; a newline, brace or colon steps the tree.
        /// </summary>
        void InObject(OgreScriptToken token)
        {
            switch (token.Type)
            {
                case OgreScriptTokenType.Newline:
                    EndLine();
                    break;
                case OgreScriptTokenType.Colon:
                    BaseList(token);
                    break;
                case OgreScriptTokenType.LeftBrace:
                    {
                        var node = New(OgreConcreteNodeType.LeftBrace, token, token.Lexeme);
                        Insert(node);
                        _parent = node;
                        _inObject = false;
                        break;
                    }
                case OgreScriptTokenType.RightBrace:
                    CloseBlock(token);
                    break;
                case OgreScriptTokenType.Variable:
                    Insert(New(OgreConcreteNodeType.Variable, token, token.Lexeme));
                    break;
                case OgreScriptTokenType.Quote:
                    Insert(New(OgreConcreteNodeType.Quote, token, token.Lexeme[1..^1]));
                    break;
                case OgreScriptTokenType.Word:
                    Insert(New(OgreConcreteNodeType.Word, token, token.Lexeme));
                    break;
            }
        }

        /// <summary>
        /// The end of a word's line.
        /// </summary>
        void EndLine()
        {
            // A line ends the current word's node unless the next real token opens a block.
            int next = SkipNewlines(_tokens, _i);
            if (next >= _tokens.Count || _tokens[next].Type != OgreScriptTokenType.LeftBrace)
            {
                _parent = _parent?.Parent;
                _inObject = false;
            }
        }

        /// <summary>
        /// <c>: base names</c>, the names as the colon node's children.
        /// </summary>
        void BaseList(OgreScriptToken token)
        {
            var node = New(OgreConcreteNodeType.Colon, token, token.Lexeme);
            int j = SkipNewlines(_tokens, _i + 1);
            if (j >= _tokens.Count || _tokens[j].Type is not (OgreScriptTokenType.Word or OgreScriptTokenType.Quote))
                throw new OgreScriptException($"expected object identifier at line {node.Line}", node.File, node.Line);
            // Base names keep their quotes: Ogre does not strip them here.
            for (; j < _tokens.Count && _tokens[j].Type is OgreScriptTokenType.Word or OgreScriptTokenType.Quote; j++)
                node.Children.Add(new OgreConcreteNode
                {
                    Type = _tokens[j].Type == OgreScriptTokenType.Word ? OgreConcreteNodeType.Word : OgreConcreteNodeType.Quote,
                    Token = _tokens[j].Lexeme,
                    File = _tokens[j].File,
                    Line = _tokens[j].Line,
                    Parent = node,
                });
            _i = j - 1;
            Insert(node);
        }

        /// <summary>
        /// A closing brace on a word's line: up past the block's '{', the '}' attached there, then up again.
        /// </summary>
        void CloseBlock(OgreScriptToken token)
        {
            _parent = _parent?.Parent;
            if (_parent is { Type: OgreConcreteNodeType.LeftBrace, Parent: not null })
                _parent = _parent.Parent;
            Insert(New(OgreConcreteNodeType.RightBrace, token, token.Lexeme));
            _parent = _parent?.Parent;
            _inObject = false;
        }

        /// <summary>
        /// Adds a node to the current parent, or to the top level.
        /// </summary>
        readonly void Insert(OgreConcreteNode node)
        {
            node.Parent = _parent;
            (_parent?.Children ?? _nodes).Add(node);
        }
    }
}
