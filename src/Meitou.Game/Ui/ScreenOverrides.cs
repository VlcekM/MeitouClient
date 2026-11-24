using System.IO.Enumeration;
using System.Numerics;
using Meitou.Data.Ui;

namespace Meitou.Game.Ui;

/// <summary>
/// One arranged widget of a screen as an extension sees it (<see cref="UiScreenContext.Widget"/>): its name, MyGUI type, caption, action, argument,
/// rectangle and whether it takes clicks.
/// </summary>
public sealed record UiScreenWidget(string Name, string Type, string Caption, string Action, string Argument, UiRect Bounds, bool Enabled)
{
    /// <summary>
    /// The view of <paramref name="c"/>.
    /// </summary>
    internal static UiScreenWidget Of(UiControl c) => new(c.Name, c.Type, c.Caption, c.Action, c.Argument, c.Bounds, c.Enabled);
}

/// <summary>
/// What an extension changes on one of a screen's own widgets (<see cref="UiRegistration.AddScreenOverrides"/>): each member left null keeps the
/// screen's own. <see cref="Caption"/> replaces its text (inline <c>#RRGGBB</c> tags colour it, as the game's texts), <see cref="TextColour"/> its
/// text colour (0 to 1 each), <see cref="Visible"/> false leaves the widget out (not drawn, not clicked), and <see cref="Bounds"/> places it.
/// </summary>
public sealed record UiWidgetOverride
{
    /// <summary>
    /// The text the widget shows instead of its own; null keeps it.
    /// </summary>
    public string? Caption { get; init; }

    /// <summary>
    /// The widget's text colour (red, green, blue, alpha, each 0 to 1); null keeps it.
    /// </summary>
    public Vector4? TextColour { get; init; }

    /// <summary>
    /// False leaves the widget out of the screen; null or true keeps it.
    /// </summary>
    public bool? Visible { get; init; }

    /// <summary>
    /// The widget's rectangle on screen (pixels, finite, size not below 0) instead of its arranged one; null keeps it. Its skin's template parts
    /// move with its top-left corner; its child widgets are widgets of their own. It keeps the clip its parent gives it.
    /// </summary>
    public UiRect? Bounds { get; init; }
}

/// <summary>
/// The extensions' changes to the widgets of the faithful screens and the HUD (docs/modding.md "Screen overrides"): each source is asked for every
/// arranged widget of its screen whose name matches its pattern, after the screen laid out its own content and before the extensions' screen
/// controls are added.
/// </summary>
sealed class ScreenOverrides
{
    /// <summary>
    /// The most widgets one source is asked for per screen and frame.
    /// </summary>
    public const int MaxWidgets = 512;

    /// <summary>
    /// The longest caption an override may set.
    /// </summary>
    public const int MaxCaption = 4096;

    /// <summary>
    /// A source: its owner, screen, widget pattern and override.
    /// </summary>
    sealed record Entry(string Owner, string Screen, string Pattern, Func<UiScreenContext, UiWidgetOverride?> Override);

    /// <summary>
    /// The sources, in registration order.
    /// </summary>
    readonly List<Entry> _entries = [];

    /// <summary>
    /// Whether any extension overrides a screen's widgets.
    /// </summary>
    public bool Any => _entries.Count > 0;

    /// <summary>
    /// Adds <paramref name="owner"/>'s source (screen and pattern checked: not blank).
    /// </summary>
    public void Add(string owner, string screen, string pattern, Func<UiScreenContext, UiWidgetOverride?> over)
    {
        if (string.IsNullOrWhiteSpace(screen) || string.IsNullOrWhiteSpace(pattern))
            throw new ArgumentException("Screen overrides need a screen and a widget name or pattern.");

        _entries.Add(new(owner, screen, pattern, over));
    }

    /// <summary>
    /// Removes <paramref name="owner"/>'s sources.
    /// </summary>
    public void Remove(string owner) => _entries.RemoveAll(e => e.Owner == owner);

    /// <summary>
    /// Applies the sources of <paramref name="view"/>'s screen (or its kind) to its arranged <paramref name="controls"/>, in registration order. A
    /// source that throws or gives an invalid override changes nothing this frame and is reported once and dropped.
    /// </summary>
    public void Apply(ScreenView view, List<UiControl> controls, UiFrameContext frame, Action<UiDiagnostic>? report)
    {
        foreach (var entry in _entries.ToArray())
        {
            if (entry.Screen != view.Screen && entry.Screen != view.Kind) continue;

            try
            {
                Commit(controls, Ask(entry, view, controls, frame));
            }
            catch (Exception e)
            {
                _entries.Remove(entry);
                report?.Invoke(new UiDiagnostic(entry.Owner, $"screen overrides {entry.Screen}/{entry.Pattern} (dropped)", e));
            }
        }
    }

    /// <summary>
    /// <paramref name="entry"/>'s overrides for the matching widgets, at most <see cref="MaxWidgets"/>, each checked.
    /// </summary>
    static List<(int Index, UiWidgetOverride Override)> Ask(Entry entry, ScreenView view, List<UiControl> controls, UiFrameContext frame)
    {
        var asked = new List<(int, UiWidgetOverride)>();
        int matched = 0;
        for (int i = 0; i < controls.Count && matched < MaxWidgets; i++)
        {
            var c = controls[i];
            if (!FileSystemName.MatchesSimpleExpression(entry.Pattern, c.Name, ignoreCase: false)) continue;

            matched++;
            var context = new UiScreenContext(view, c.Name, c.Bounds, view.ItemOf?.Invoke(c), frame, view.PortraitOf?.Invoke(c), c);
            if (entry.Override(context) is not { } over) continue;

            Validate(over);
            asked.Add((i, over));
        }

        return asked;
    }

    /// <summary>
    /// Throws for an override whose caption is too long or whose colour is not finite or outside 0 to 1.
    /// </summary>
    static void Validate(UiWidgetOverride over)
    {
        if (over.Caption is { Length: > MaxCaption })
            throw new InvalidOperationException($"A screen override's caption is at most {MaxCaption} characters.");
        if (over.TextColour is { } c && !InUnit(c.X, c.Y, c.Z, c.W))
            throw new InvalidOperationException("A screen override's colour is 0 to 1 each.");
        if (over.Bounds is { } b && !(float.IsFinite(b.X + b.Y + b.Width + b.Height) && b.Width >= 0 && b.Height >= 0))
            throw new InvalidOperationException("A screen override's bounds are finite, their size not below 0.");
    }

    /// <summary>
    /// Whether every value is finite and within 0 to 1.
    /// </summary>
    static bool InUnit(params ReadOnlySpan<float> values)
    {
        foreach (float v in values)
            if (!float.IsFinite(v) || v < 0 || v > 1) return false;
        return true;
    }

    /// <summary>
    /// Puts <paramref name="asked"/> into <paramref name="controls"/>: the moved widgets' template parts moved with them, the hidden widgets left
    /// out, the others changed.
    /// </summary>
    static void Commit(List<UiControl> controls, List<(int Index, UiWidgetOverride Override)> asked)
    {
        foreach (var (i, over) in asked)
            if (over.Bounds is { } to && over.Visible != false) MoveParts(controls, controls[i], to);

        for (int k = asked.Count - 1; k >= 0; k--)
        {
            var (i, over) = asked[k];
            if (over.Visible == false) controls.RemoveAt(i);
            else controls[i] = Changed(controls[i], over);
        }
    }

    /// <summary>
    /// Moves the template parts of <paramref name="widget"/> (the controls whose <c>TemplateOwner</c> is its name) by its move to
    /// <paramref name="to"/>.
    /// </summary>
    static void MoveParts(List<UiControl> controls, UiControl widget, UiRect to)
    {
        float dx = to.X - widget.Bounds.X, dy = to.Y - widget.Bounds.Y;
        if (widget.Name.Length == 0 || dx == 0 && dy == 0) return;

        for (int j = 0; j < controls.Count; j++)
        {
            var c = controls[j];
            if (c.Properties.GetValueOrDefault("TemplateOwner") != widget.Name) continue;

            controls[j] = c with { Bounds = c.Bounds with { X = c.Bounds.X + dx, Y = c.Bounds.Y + dy } };
        }
    }

    /// <summary>
    /// <paramref name="c"/> with <paramref name="over"/>'s rectangle, caption and colour (the colour also as its <c>TextColour</c> property, which
    /// comes before a skin's).
    /// </summary>
    static UiControl Changed(UiControl c, UiWidgetOverride over)
    {
        c = c with { Bounds = over.Bounds ?? c.Bounds };
        if (over.TextColour is not { } colour) return c with { Caption = over.Caption ?? c.Caption };

        var properties = new Dictionary<string, string>(c.Properties, StringComparer.Ordinal) { ["TextColour"] = UiHost.Format(colour) };
        return c with { Caption = over.Caption ?? c.Caption, TextColour = colour, Properties = properties };
    }
}
