using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System.Runtime.CompilerServices;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Meitou.Launcher;

/// <summary>
/// A news post's Markdown as themed controls (Markdig's syntax tree, drawn here): headings, paragraphs with bold, italic, code and
/// links, lists, quotes, code blocks, rules and images (fetched through <see cref="NewsMediaCache"/>). Raw HTML is left out.
/// </summary>
static class MarkdownView
{
    /// <summary>
    /// The code font, with fallbacks for each platform.
    /// </summary>
    static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");

    /// <summary>
    /// The URL of each link span.
    /// </summary>
    static readonly ConditionalWeakTable<Span, string> LinkUrls = new();

    /// <summary>
    /// A post's body as controls, one per Markdown block.
    /// </summary>
    public static Control Render(NewsPost post, NewsMediaCache media, Action<string> open)
    {
        var panel = new StackPanel { Spacing = 10 };
        foreach (var block in Markdig.Markdown.Parse(post.Body))
            if (Block(block, post, media, open) is { } control) panel.Children.Add(control);
        return panel;
    }

    /// <summary>
    /// An image (or a <c>.webm</c> video, <see cref="VideoView"/>) loaded in the background, at most the post's width (shrunk to fit, never
    /// enlarged); until then (or when it fails) it takes no room.
    /// </summary>
    /// <param name="fullVideo">For a video: the page its open button goes to, and how links open.</param>
    public static Control Image(string url, NewsMediaCache media, (string Url, Action<string> Open)? fullVideo = null)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.AbsolutePath.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            return VideoView.Create(url, media, fullVideo);
        var image = new Image
        {
            Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left,
        };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
        var frame = new Border
        {
            CornerRadius = new CornerRadius(6), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left, Child = image, IsVisible = false,
        };
        _ = Task.Run(async () =>
        {
            var bytes = await media.ImageAsync(url);
            if (bytes is null) return;
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    image.Source = new Bitmap(new MemoryStream(bytes));
                    frame.IsVisible = true;
                }
                catch (Exception)
                {
                    // not an image the decoder knows: leave it out
                }
            });
        });
        return frame;
    }

    /// <summary>
    /// A block's control: a heading, paragraph (images in it too), list, quote, code block or rule; null for what is left out (raw HTML).
    /// </summary>
    static Control? Block(Block block, NewsPost post, NewsMediaCache media, Action<string> open)
    {
        switch (block)
        {
            case HeadingBlock heading:
                return Heading(heading, post, open);
            case ParagraphBlock paragraph:
                return Paragraph(paragraph, post, media, open);
            case ListBlock list:
                return ListOf(list, post, media, open);
            case QuoteBlock quote:
                return QuoteOf(quote, post, media, open);
            case CodeBlock code:
                return CodeOf(code);
            case ThematicBreakBlock:
                return new Border { Height = 1, Background = Brush("PlateRim"), Margin = new Thickness(0, 4) };
            default:
                return null;
        }
    }

    /// <summary>
    /// A heading: a larger text, the larger the higher its level.
    /// </summary>
    static TextBlock Heading(HeadingBlock heading, NewsPost post, Action<string> open)
    {
        var title = Text(heading.Inline, post, open);
        title.FontSize = heading.Level <= 1 ? 20 : heading.Level == 2 ? 18 : 16;
        title.Margin = new Thickness(0, 6, 0, 0);
        return title;
    }

    /// <summary>
    /// A paragraph; one of only images shows them as pictures.
    /// </summary>
    static Control? Paragraph(ParagraphBlock paragraph, NewsPost post, NewsMediaCache media, Action<string> open)
    {
        // A paragraph of only images shows them as pictures.
        var inlines = paragraph.Inline?.ToList() ?? [];
        if (inlines.Count > 0 && inlines.All(IsPictureInline))
        {
            var images = new StackPanel { Spacing = 8 };
            foreach (var link in inlines.OfType<LinkInline>())
                images.Children.Add(Image(NewsFeed.Resolve(post, link.Url ?? ""), media));
            return images;
        }

        return Text(paragraph.Inline, post, open);
    }

    /// <summary>
    /// Whether an inline belongs in a picture paragraph: an image, a line break or blank text.
    /// </summary>
    static bool IsPictureInline(Markdig.Syntax.Inlines.Inline inline) =>
        inline is LinkInline { IsImage: true } or LineBreakInline || inline is LiteralInline l && string.IsNullOrWhiteSpace(l.Content.ToString());

    /// <summary>
    /// A bulleted or numbered list: each item a marker beside its blocks.
    /// </summary>
    static StackPanel ListOf(ListBlock list, NewsPost post, NewsMediaCache media, Action<string> open)
    {
        var items = new StackPanel { Spacing = 4 };
        int number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var row = new DockPanel();
            var marker = new TextBlock { Text = list.IsOrdered ? $"{number++}." : "•", Width = 22, Foreground = Brush("BrassLight") };
            DockPanel.SetDock(marker, Dock.Left);
            row.Children.Add(marker);
            var body = new StackPanel { Spacing = 4 };
            foreach (var child in item)
                if (Block(child, post, media, open) is { } control) body.Children.Add(control);
            row.Children.Add(body);
            items.Children.Add(row);
        }

        return items;
    }

    /// <summary>
    /// A quote: its blocks beside a brass rule.
    /// </summary>
    static Border QuoteOf(QuoteBlock quote, NewsPost post, NewsMediaCache media, Action<string> open)
    {
        var quoted = new StackPanel { Spacing = 6 };
        foreach (var child in quote)
            if (Block(child, post, media, open) is { } control) quoted.Children.Add(control);
        return new Border
        {
            BorderBrush = Brush("Brass"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 2, 0, 2),
            Child = quoted,
        };
    }

    /// <summary>
    /// A code block: its lines in the monospace font.
    /// </summary>
    static Border CodeOf(CodeBlock code) =>
        new()
        {
            Background = Brush("Field"), CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 8),
            Child = new SelectableTextBlock
            {
                Text = code.Lines.ToString().TrimEnd(), FontFamily = Mono, FontSize = 12.5, FontWeight = FontWeight.Normal,
                TextWrapping = TextWrapping.Wrap,
            },
        };

    /// <summary>
    /// An inline run of text with its links clickable (a hand and a tooltip over one).
    /// </summary>
    static TextBlock Text(ContainerInline? container, NewsPost post, Action<string> open)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.Medium, FontSize = 14, LineHeight = 21, Inlines = [] };
        if (container is not null)
            foreach (var inline in container) Add(text.Inlines!, inline, post);
        // Links are runs in the text (so they sit on its baseline); the pointer finds them by hit-testing the layout.
        text.PointerMoved += (_, e) =>
        {
            var url = LinkAt(text, e.GetPosition(text));
            text.Cursor = url is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(text, url);
        };
        text.PointerPressed += (_, e) =>
        {
            if (LinkAt(text, e.GetPosition(text)) is not { } url) return;
            open(url);
            e.Handled = true;
        };
        return text;
    }

    /// <summary>
    /// The URL of the link at <paramref name="point"/> in <paramref name="text"/>, null when there is none.
    /// </summary>
    static string? LinkAt(TextBlock text, Point point)
    {
        var hit = text.TextLayout.HitTestPoint(point);
        if (!hit.IsInside || text.Inlines is null) return null;
        int index = hit.IsTrailing ? hit.TextPosition - 1 : hit.TextPosition, offset = 0;
        return Find(text.Inlines, null);

        string? Find(InlineCollection inlines, string? url)
        {
            foreach (var inline in inlines)
            {
                if (inline is Run run)
                {
                    int length = run.Text?.Length ?? 0;
                    if (index >= offset && index < offset + length) return url;
                    offset += length;
                }
                else if (inline is Span span && Find(span.Inlines, LinkUrls.TryGetValue(span, out var own) ? own : url) is { } found)
                {
                    return found;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Adds a Markdown inline to <paramref name="target"/>: text, emphasis, code, a line break, a link or autolink, or a container's inlines.
    /// </summary>
    static void Add(InlineCollection target, Markdig.Syntax.Inlines.Inline inline, NewsPost post)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(new Run(literal.Content.ToString()));
                break;
            case EmphasisInline emphasis:
                Span span = emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                if (emphasis.DelimiterCount >= 2) span.Foreground = Brush("PaperLight");
                foreach (var child in emphasis) Add(span.Inlines, child, post);
                target.Add(span);
                break;
            case CodeInline code:
                var run = new Run(code.Content)
                {
                    FontFamily = Mono, FontWeight = FontWeight.Normal, FontSize = 13, Foreground = Brush("PaperLight"),
                };
                target.Add(run);
                break;
            case LineBreakInline lineBreak:
                target.Add(new Run(lineBreak.IsHard ? "\n" : " ")); // a Run, so text positions are plain character counts
                break;
            case LinkInline { IsImage: false } link:
                string text = string.Concat(link.Descendants<LiteralInline>().Select(l => l.Content.ToString()));
                target.Add(Link(text, NewsFeed.Resolve(post, link.Url ?? "")));
                break;
            case AutolinkInline auto:
                target.Add(Link(auto.Url, auto.Url));
                break;
            case ContainerInline container:
                foreach (var child in container) Add(target, child, post);
                break;
        }
    }

    /// <summary>
    /// A link: underlined in brass, its URL kept for the pointer.
    /// </summary>
    static Span Link(string label, string url)
    {
        var link = new Underline { Foreground = Brush("BrassLight"), Inlines = { new Run(label) } };
        LinkUrls.Add(link, url);
        return link;
    }

    /// <summary>
    /// The app's brush <paramref name="key"/>; grey when the theme has none.
    /// </summary>
    internal static IBrush Brush(string key) =>
        Application.Current!.TryGetResource(key, null, out var value) && value is IBrush brush ? brush : Brushes.Gray;
}
