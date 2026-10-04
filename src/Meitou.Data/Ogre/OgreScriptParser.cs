namespace Meitou.Data.Ogre;

public enum OgreConcreteNodeType { Word, Quote, Variable, VariableAssign, Import, LeftBrace, RightBrace, Colon }

/// <summary>
/// Ogre's "concrete" syntax tree: one node per word, with the rest of its line (and a following <c>{ }</c> block)
/// as children. <see cref="OgreScriptCompiler"/> turns it into objects and properties.
/// </summary>
public sealed class OgreConcreteNode
{
    public OgreConcreteNodeType Type { get; init; }

    /// <summary>The token text; quotes are stripped for <see cref="OgreConcreteNodeType.Quote"/> (except in base lists).</summary>
    public string Token { get; set; } = "";

    public string File { get; init; } = "";
    public int Line { get; init; }
    public OgreConcreteNode? Parent { get; set; }
    public List<OgreConcreteNode> Children { get; } = [];

    public override string ToString() => $"{Type} '{Token}' ({File}:{Line})";
}

/// <summary>
/// Builds the concrete tree from tokens, following Ogre's ScriptParser (MIT) state machine. Malformed
/// <c>import</c>, <c>set</c> or <c>:</c> lines throw <see cref="OgreScriptException"/>, which in Ogre aborts the file.
/// </summary>
public static class OgreScriptParser
{
    public static List<OgreConcreteNode> Parse(string text, string file) => Parse(OgreScriptLexer.Tokenize(text, file));

    public static List<OgreConcreteNode> Parse(IReadOnlyList<OgreScriptToken> tokens)
    {
        var nodes = new List<OgreConcreteNode>();
        bool inObject = false;
        OgreConcreteNode? parent = null;

        void Insert(OgreConcreteNode node)
        {
            node.Parent = parent;
            (parent?.Children ?? nodes).Add(node);
        }

        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!inObject)
            {
                if (token.Type == OgreScriptTokenType.Word)
                {
                    if (token.Lexeme == "import")
                    {
                        var node = New(OgreConcreteNodeType.Import, token, token.Lexeme);
                        i++;
                        if (i >= tokens.Count || tokens[i].Type is not (OgreScriptTokenType.Word or OgreScriptTokenType.Quote))
                            throw new OgreScriptException($"expected import target at line {node.Line}", node.File, node.Line);
                        // Ogre strips a quoted import target using the length of the "import" token instead of the
                        // target's own (ScriptParser::parse), so `import "Foo" from ...` asks for `Foo"`. Kept as is.
                        node.Children.Add(Child(node, tokens[i], tokens[i].Type == OgreScriptTokenType.Quote ? SafeSubstring(tokens[i].Lexeme, 1, token.Lexeme.Length - 2) : null));
                        // The word in between ("from") is skipped without being checked.
                        i += 2;
                        if (i >= tokens.Count || tokens[i].Type is not (OgreScriptTokenType.Word or OgreScriptTokenType.Quote))
                            throw new OgreScriptException($"expected import source at line {node.Line}", node.File, node.Line);
                        node.Children.Add(Child(node, tokens[i], null));
                        Insert(node);
                    }
                    else if (token.Lexeme == "set")
                    {
                        var node = New(OgreConcreteNodeType.VariableAssign, token, token.Lexeme);
                        i++;
                        if (i >= tokens.Count || tokens[i].Type != OgreScriptTokenType.Variable)
                            throw new OgreScriptException($"expected variable name at line {node.Line}", node.File, node.Line);
                        node.Children.Add(new OgreConcreteNode
                        {
                            Type = OgreConcreteNodeType.Variable, Token = tokens[i].Lexeme, File = tokens[i].File,
                            Line = tokens[i].Line, Parent = node,
                        });
                        i++;
                        if (i >= tokens.Count || tokens[i].Type is not (OgreScriptTokenType.Word or OgreScriptTokenType.Quote))
                            throw new OgreScriptException($"expected variable value at line {node.Line}", node.File, node.Line);
                        node.Children.Add(Child(node, tokens[i], null));
                        Insert(node);
                    }
                    else
                    {
                        var node = New(OgreConcreteNodeType.Word, token, token.Lexeme);
                        Insert(node);
                        parent = node;
                        inObject = true;
                    }
                }
                else if (token.Type == OgreScriptTokenType.RightBrace)
                {
                    // Up from the '{' to its owner, attach the '}' there, then up again.
                    parent = parent?.Parent;
                    Insert(New(OgreConcreteNodeType.RightBrace, token, token.Lexeme));
                    parent = parent?.Parent;
                }
                // Quotes, variables, '{' and ':' at the start of a line are ignored, as in Ogre.
            }
            else
            {
                switch (token.Type)
                {
                    case OgreScriptTokenType.Newline:
                    {
                        // A line ends the current word's node unless the next real token opens a block.
                        int next = SkipNewlines(tokens, i);
                        if (next >= tokens.Count || tokens[next].Type != OgreScriptTokenType.LeftBrace)
                        {
                            parent = parent?.Parent;
                            inObject = false;
                        }
                        break;
                    }
                    case OgreScriptTokenType.Colon:
                    {
                        var node = New(OgreConcreteNodeType.Colon, token, token.Lexeme);
                        int j = SkipNewlines(tokens, i + 1);
                        if (j >= tokens.Count || tokens[j].Type is not (OgreScriptTokenType.Word or OgreScriptTokenType.Quote))
                            throw new OgreScriptException($"expected object identifier at line {node.Line}", node.File, node.Line);
                        // Base names keep their quotes: Ogre does not strip them here.
                        for (; j < tokens.Count && tokens[j].Type is OgreScriptTokenType.Word or OgreScriptTokenType.Quote; j++)
                            node.Children.Add(new OgreConcreteNode
                            {
                                Type = tokens[j].Type == OgreScriptTokenType.Word ? OgreConcreteNodeType.Word : OgreConcreteNodeType.Quote,
                                Token = tokens[j].Lexeme, File = tokens[j].File, Line = tokens[j].Line, Parent = node,
                            });
                        i = j - 1;
                        Insert(node);
                        break;
                    }
                    case OgreScriptTokenType.LeftBrace:
                    {
                        var node = New(OgreConcreteNodeType.LeftBrace, token, token.Lexeme);
                        Insert(node);
                        parent = node;
                        inObject = false;
                        break;
                    }
                    case OgreScriptTokenType.RightBrace:
                    {
                        parent = parent?.Parent;
                        if (parent is { Type: OgreConcreteNodeType.LeftBrace, Parent: not null })
                            parent = parent.Parent;
                        Insert(New(OgreConcreteNodeType.RightBrace, token, token.Lexeme));
                        parent = parent?.Parent;
                        inObject = false;
                        break;
                    }
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
        }
        return nodes;
    }

    /// <summary>
    /// Parses a variable's value for expansion (Ogre's ScriptParser::parseChunk): words and variables only. A
    /// quoted phrase throws, because Ogre's switch falls through into its error case.
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
}
