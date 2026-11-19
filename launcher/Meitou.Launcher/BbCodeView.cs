using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace Meitou.Launcher;

/// <summary>
/// A mod description as themed controls: the Steam Workshop's BBCode (headings, bold, italic, underline, strike, links, lists, quotes, code,
/// rules, tables, spoilers, noparse) drawn, plain text kept with its line breaks. An unknown or unmatched tag stays as literal text; only web
/// links open, and images are offered as links, never fetched.
/// </summary>
static class BbCodeView
{
    #region Parsing

    /// <summary>
    /// The tags drawn; any other bracketed text is literal.
    /// </summary>
    static readonly HashSet<string> Known =
    [
        "h1", "h2", "h3", "b", "i", "u", "s", "strike", "url", "img", "list", "olist", "*", "quote", "code", "hr", "spoiler", "noparse",
        "table", "tr", "td", "th",
    ];

    /// <summary>
    /// A part of the description: literal text, or a tag with its argument (<c>[url=…]</c>) and what it holds.
    /// </summary>
    abstract record Node;

    /// <summary>
    /// Literal text.
    /// </summary>
    sealed record TextNode(string Text) : Node;

    /// <summary>
    /// A tag and what it holds.
    /// </summary>
    sealed record TagNode(string Name, string? Argument) : Node
    {
        /// <summary>
        /// What the tag holds, in order.
        /// </summary>
        public List<Node> Children { get; } = [];
    }

    /// <summary>
    /// An opening or closing tag: <c>[name]</c>, <c>[name=argument]</c> or <c>[/name]</c>.
    /// </summary>
    static readonly Regex TagPattern = new(@"\[(/?)([a-zA-Z0-9*]+)(?:=([^\]\n]*))?\]", RegexOptions.Compiled);

    /// <summary>
    /// Three or more line breaks in a row, drawn as one blank line.
    /// </summary>
    static readonly Regex BlankLines = new(@"\n{3,}", RegexOptions.Compiled);

    /// <summary>
    /// The description as a tree under a root tag.
    /// </summary>
    static TagNode Parse(string text)
    {
        var root = new TagNode("", null);
        var open = new List<TagNode> { root };
        int at = 0;
        while (TagPattern.Match(text, at) is { Success: true } match)
        {
            Literal(open[^1], text[at..match.Index]);
            at = match.Index + match.Length;
            at = Tag(open, match, text, at);
        }

        Literal(open[^1], text[at..]);
        return root;
    }

    /// <summary>
    /// Applies one tag to the open tags; the index after what it consumed (a <c>[noparse]</c> takes the text up to its end).
    /// </summary>
    static int Tag(List<TagNode> open, Match match, string text, int at)
    {
        string name = match.Groups[2].Value.ToLowerInvariant();
        if (!Known.Contains(name))
        {
            Literal(open[^1], match.Value);
            return at;
        }

        if (match.Groups[1].Length > 0) Close(open, name, match.Value);
        else if (name == "noparse") return NoParse(open[^1], text, at);
        else Open(open, name, match.Groups[3].Success ? match.Groups[3].Value : null);

        return at;
    }

    /// <summary>
    /// Opens a tag inside the innermost open one; a list item first closes the item before it.
    /// </summary>
    static void Open(List<TagNode> open, string name, string? argument)
    {
        if (name == "*" && open.FindLastIndex(t => t.Name == "*") is var item and > 0 && !open.Skip(item).Any(IsList))
            open.RemoveRange(item, open.Count - item);

        var tag = new TagNode(name, argument);
        open[^1].Children.Add(tag);
        open.Add(tag);
    }

    /// <summary>
    /// Closes the innermost open tag of <paramref name="name"/> and those inside it; a closing tag with none open is literal.
    /// </summary>
    static void Close(List<TagNode> open, string name, string literal)
    {
        int index = open.FindLastIndex(t => t.Name == name);
        if (index <= 0)
        {
            Literal(open[^1], literal);
            return;
        }

        open.RemoveRange(index, open.Count - index);
    }

    /// <summary>
    /// A <c>[noparse]</c>: the text up to <c>[/noparse]</c> (or the end) as literal; the index after it.
    /// </summary>
    static int NoParse(TagNode parent, string text, int at)
    {
        const string end = "[/noparse]";
        int close = text.IndexOf(end, at, StringComparison.OrdinalIgnoreCase);
        Literal(parent, close < 0 ? text[at..] : text[at..close]);
        return close < 0 ? text.Length : close + end.Length;
    }

    /// <summary>
    /// Adds literal text to a tag, joined to the text before it.
    /// </summary>
    static void Literal(TagNode parent, string text)
    {
        if (text.Length == 0) return;

        if (parent.Children is [.., TextNode last]) parent.Children[^1] = new TextNode(last.Text + text);
        else parent.Children.Add(new TextNode(text));
    }

    /// <summary>
    /// Whether a tag is a list.
    /// </summary>
    static bool IsList(TagNode tag) => tag.Name is "list" or "olist";

    #endregion

    #region Drawing

    /// <summary>
    /// The description <paramref name="text"/> as controls; <paramref name="open"/> opens a clicked web link.
    /// </summary>
    public static Control Render(string text, Action<string> open)
    {
        var blocks = new StackPanel { Spacing = 6 };
        string lines = BlankLines.Replace(text.ReplaceLineEndings("\n"), "\n\n");
        new Writer(blocks, open).Blocks(Parse(lines).Children);
        return blocks;
    }

    /// <summary>
    /// Draws nodes into a panel of blocks: block tags each a control of their own, the text and inline tags between them gathered in
    /// paragraphs.
    /// </summary>
    sealed class Writer(StackPanel blocks, Action<string> open)
    {
        /// <summary>
        /// The paragraph text and inline tags are added to; null between blocks.
        /// </summary>
        TextBlock? _paragraph;

        /// <summary>
        /// Draws <paramref name="nodes"/>, then ends the open paragraph.
        /// </summary>
        public void Blocks(IEnumerable<Node> nodes)
        {
            foreach (var node in nodes)
            {
                if (node is TagNode tag && Block(tag) is { } control)
                {
                    End();
                    blocks.Children.Add(control);
                }
                else
                {
                    Inline(node, Paragraph().Inlines!);
                }
            }

            End();
        }

        /// <summary>
        /// The open paragraph, started when none is.
        /// </summary>
        TextBlock Paragraph()
        {
            if (_paragraph is not null) return _paragraph;

            _paragraph = Text(13, "Main");
            MarkdownView.HandleLinks(_paragraph, open);
            return _paragraph;
        }

        /// <summary>
        /// Ends the open paragraph: kept when it holds more than blank lines, its outer line breaks dropped.
        /// </summary>
        void End()
        {
            if (_paragraph is not { Inlines: { } inlines } paragraph) return;

            _paragraph = null;
            Trim(inlines);
            if (inlines.Count > 0) blocks.Children.Add(paragraph);
        }

        /// <summary>
        /// The control of a block tag; null for an inline one.
        /// </summary>
        Control? Block(TagNode tag) => tag.Name switch
        {
            "h1" => Heading(tag, 18),
            "h2" => Heading(tag, 16),
            "h3" => Heading(tag, 14),
            "list" or "olist" => ListOf(tag, open),
            "quote" => QuoteOf(tag, open),
            "code" => CodeOf(tag),
            "hr" => new Border { Height = 1, Margin = new Thickness(0, 4), Background = MarkdownView.Brush("PlateRim") },
            "table" or "tr" => Nested(tag.Children, open, 2),
            _ => null,
        };

        /// <summary>
        /// A heading: its text larger and lighter.
        /// </summary>
        static TextBlock Heading(TagNode tag, double size)
        {
            var heading = Text(size, "PaperLight");
            heading.FontWeight = FontWeight.SemiBold;
            heading.Margin = new Thickness(0, 4, 0, 0);
            foreach (var child in tag.Children) Inline(child, heading.Inlines!);
            Trim(heading.Inlines!);
            return heading;
        }

        /// <summary>
        /// A list: one row per item, marked with a bullet or (<c>[olist]</c>) its number; text outside the items is its first row.
        /// </summary>
        static StackPanel ListOf(TagNode list, Action<string> open)
        {
            var rows = new StackPanel { Spacing = 3 };
            int number = 0;
            foreach (var item in Items(list))
            {
                if (item is not TagNode { Name: "*" } tag)
                {
                    rows.Children.Add(Nested([item], open, 0));
                    continue;
                }

                number++;
                string marker = list.Name == "olist" ? $"{number}." : "•";
                var row = new DockPanel();
                var mark = Text(13, "BrassLight");
                mark.Text = marker;
                mark.Margin = new Thickness(0, 0, 8, 0);
                DockPanel.SetDock(mark, Dock.Left);
                row.Children.Add(mark);
                row.Children.Add(Nested(tag.Children, open, 3));
                rows.Children.Add(row);
            }

            return rows;
        }

        /// <summary>
        /// A list's items, without the blank text between them.
        /// </summary>
        static IEnumerable<Node> Items(TagNode list) => list.Children.Where(n => n is not TextNode { Text: var t } || !string.IsNullOrWhiteSpace(t));

        /// <summary>
        /// A quote: its blocks behind a brass rule, under its author when it names one.
        /// </summary>
        static Border QuoteOf(TagNode quote, Action<string> open)
        {
            var quoted = Nested(quote.Children, open, 6);
            if (quote.Argument is { Length: > 0 } author)
            {
                var by = Text(12, "Quiet");
                by.Text = author;
                quoted.Children.Insert(0, by);
            }

            return new Border
            {
                BorderBrush = MarkdownView.Brush("Brass"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 2, 0, 2),
                Child = quoted,
            };
        }

        /// <summary>
        /// Code: its text as written, in the monospace font.
        /// </summary>
        static Border CodeOf(TagNode code) =>
            new()
            {
                Background = MarkdownView.Brush("Field"), CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 6),
                Child = new SelectableTextBlock
                {
                    Text = PlainText(code).Trim('\n'), FontFamily = MarkdownView.Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                    Foreground = MarkdownView.Brush("PaperLight"),
                },
            };

        /// <summary>
        /// Nodes drawn as blocks of their own, in a panel.
        /// </summary>
        static StackPanel Nested(IEnumerable<Node> nodes, Action<string> open, double spacing)
        {
            var panel = new StackPanel { Spacing = spacing };
            new Writer(panel, open).Blocks(nodes);
            return panel;
        }

        /// <summary>
        /// Adds a node to <paramref name="target"/> as text: inline tags as styled spans, a block tag met inside one as its plain text.
        /// </summary>
        static void Inline(Node node, InlineCollection target)
        {
            if (node is TextNode text)
            {
                target.Add(new Run(text.Text)); // one Run per text, so link hit-testing counts plain characters
                return;
            }

            var tag = (TagNode)node;
            if (tag.Name == "img")
            {
                if (WebLink(PlainText(tag)) is { } image) target.Add(MarkdownView.Link(LauncherText.Translate("Image"), image));
                return;
            }

            if (Styled(tag) is not { } span)
            {
                target.Add(new Run(PlainText(tag)));
                return;
            }

            foreach (var child in tag.Children) Inline(child, span.Inlines);
            target.Add(span);
        }

        /// <summary>
        /// The span an inline tag draws its text in; null for a tag drawn as plain text (a block tag inside text, a link that is not to the
        /// web).
        /// </summary>
        static Span? Styled(TagNode tag) => tag.Name switch
        {
            "b" => new Bold { Foreground = MarkdownView.Brush("PaperLight") },
            "i" => new Italic(),
            "u" => new Underline(),
            "s" or "strike" => new Span { TextDecorations = TextDecorations.Strikethrough },
            "spoiler" => new Span { Foreground = MarkdownView.Brush("Quiet") },
            "td" or "th" => new Span { Inlines = { new Run("  ") } },
            "*" => new Span { Inlines = { new Run("\n• ") } },
            "url" when WebLink(tag.Argument ?? PlainText(tag)) is { } url => MarkdownView.Link("", url),
            _ => null,
        };

        /// <summary>
        /// The address, unquoted, when it is a web address (http or https); null otherwise.
        /// </summary>
        static string? WebLink(string url)
        {
            string trimmed = url.Trim().Trim('"');
            bool web = Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
            return web ? trimmed : null;
        }

        /// <summary>
        /// The text a tag holds, without its tags.
        /// </summary>
        static string PlainText(TagNode tag) =>
            string.Concat(tag.Children.Select(c => c is TextNode t ? t.Text : PlainText((TagNode)c)));

        /// <summary>
        /// A wrapping text block of the description's font, in the brush <paramref name="brush"/>.
        /// </summary>
        static TextBlock Text(double size, string brush) =>
            new()
            {
                TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.Medium, FontSize = size, LineHeight = size * 1.45,
                Foreground = MarkdownView.Brush(brush), Inlines = [],
            };

        /// <summary>
        /// Drops the blank runs and line breaks that open and close <paramref name="inlines"/>.
        /// </summary>
        static void Trim(InlineCollection inlines)
        {
            while (inlines is [Run first, ..] && TrimRun(first, start: true)) inlines.RemoveAt(0);
            while (inlines is [.., Run last] && TrimRun(last, start: false)) inlines.RemoveAt(inlines.Count - 1);
        }

        /// <summary>
        /// Trims a run's outer whitespace at one end; whether nothing is left.
        /// </summary>
        static bool TrimRun(Run run, bool start)
        {
            run.Text = start ? run.Text?.TrimStart() : run.Text?.TrimEnd();
            return string.IsNullOrEmpty(run.Text);
        }
    }

    #endregion
}
