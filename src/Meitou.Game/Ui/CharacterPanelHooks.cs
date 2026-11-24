namespace Meitou.Game.Ui;

/// <summary>
/// A line an extension adds to the HUD's character panel under its stats lines (<see cref="UiRegistration.AddCharacterPanelLines"/>):
/// <paramref name="Label"/> in the stats labels' column; <paramref name="Value"/> (null or blank: none) in their value column, as text, over a bar
/// filled by <paramref name="Bar"/> (0 to 1; null: no bar), or as the caption of a button there when the line has a <paramref name="Press"/>; a line
/// with a press and no value is one button across the line, captioned with the label. A line with a press draws no bar. <paramref name="Tooltip"/>
/// (null: none) shows over the line. A press runs on the host's thread and changes the world only by queueing commands.
/// </summary>
public sealed record UiCharacterLine(string Label, string? Value = null, float? Bar = null, string? Tooltip = null, Action? Press = null)
{
    /// <summary>
    /// The line's colours and icon (<see cref="UiLineStyle"/>); null: the panel's own look.
    /// </summary>
    public UiLineStyle? Style { get; init; }

    /// <summary>
    /// The line is a divider: a rule across its row in the stats labels' colour (or <see cref="UiLineStyle.Label"/>), between the game's stats and
    /// the lines after it; its label may be empty and its value, bar, tooltip and press are not used.
    /// </summary>
    public bool Divider { get; init; }
}

/// <summary>
/// A button an extension adds beside the character's jobs list (<see cref="UiRegistration.AddJobPanelButtons"/>): its caption, its tooltip (null:
/// none) and what pressing it does (on the host's thread; it changes the world only by queueing commands).
/// </summary>
public sealed record UiJobButton(string Caption, string? Tooltip, Action Press)
{
    /// <summary>
    /// The button shows lit (the skin's selected state), as for the tab a character is on.
    /// </summary>
    public bool Lit { get; init; }

    /// <summary>
    /// The MyGUI skin the button is drawn with instead of the game's neutral <c>Kenshi_Button1Skin</c> (a skin the interface's resources hold); null
    /// or an unknown skin: the neutral one.
    /// </summary>
    public string? Skin { get; init; }

    /// <summary>
    /// A press plays no click (the mod plays one itself when it wants, <c>ModContext.PostInterfaceSound</c>).
    /// </summary>
    public bool Silent { get; init; }

    /// <summary>
    /// The caption's colour and an icon (a marker) before it (<see cref="UiLineStyle"/>: <c>Label</c> and <c>Icon</c>; <c>Value</c> unused); null:
    /// the panel's look.
    /// </summary>
    public UiLineStyle? Style { get; init; }
}

/// <summary>
/// How the job panel buttons are placed (<see cref="UiRegistration.AddJobPanelButtons(Func{UiCharacterContext, IEnumerable{UiJobButton}}, UiJobButtonLayout)"/>).
/// </summary>
public enum UiJobButtonArrangement
{
    /// <summary>
    /// In rows above the jobs list, which starts that many job rows lower.
    /// </summary>
    Rows,

    /// <summary>
    /// In columns left of the jobs list, which starts that many columns further right.
    /// </summary>
    Columns,
}

/// <summary>
/// The job panel buttons' layout: <paramref name="Arrangement"/>, at most <paramref name="PerLine"/> buttons in a row (or a column; 1 to
/// <see cref="CharacterPanelHooks.MaxButtons"/>), and for columns each column's width in job rows (<paramref name="ColumnWidth"/>, 0.5 to 6).
/// </summary>
public sealed record UiJobButtonLayout(UiJobButtonArrangement Arrangement = UiJobButtonArrangement.Rows, int PerLine = 4, float ColumnWidth = 1.5f)
{
    /// <summary>
    /// One row above the list, four buttons wide (the layout without a mod's choice).
    /// </summary>
    public static UiJobButtonLayout Default { get; } = new();

    /// <summary>
    /// The layout as used: <see cref="PerLine"/> and <see cref="ColumnWidth"/> clamped (a width not finite is the default's), an unknown
    /// arrangement rows.
    /// </summary>
    internal UiJobButtonLayout Clamped() => new(
        Enum.IsDefined(Arrangement) ? Arrangement : UiJobButtonArrangement.Rows,
        Math.Clamp(PerLine, 1, CharacterPanelHooks.MaxButtons),
        float.IsFinite(ColumnWidth) ? Math.Clamp(ColumnWidth, 0.5f, 6) : Default.ColumnWidth);
}

/// <summary>
/// What an extension's character panel lines and job panel buttons get: the character the panel shows, the snapshot it is laid out from, the tick
/// read when asked, and the command queue, through which a press changes the world.
/// </summary>
public sealed class UiCharacterContext(Simulation.CharacterSnapshot character, Simulation.WorldSnapshot snapshot, Func<long> tick,
    Action<Simulation.SimCommand> enqueue)
{
    /// <summary>
    /// The character the panel shows: its skills (<see cref="Simulation.CharacterSnapshot.AllSkills"/>), XP multipliers, jobs, orders and faction.
    /// </summary>
    public Simulation.CharacterSnapshot Character { get; } = character;

    /// <summary>
    /// The character's id, which commands name it by.
    /// </summary>
    public Simulation.CharacterId Id => Character.Id;

    /// <summary>
    /// The character's name.
    /// </summary>
    public string Name => Character.Name;

    /// <summary>
    /// The snapshot the panel is laid out from (the one on screen).
    /// </summary>
    public Simulation.WorldSnapshot Snapshot { get; } = snapshot;

    /// <summary>
    /// The world's tick now, for a command's <see cref="Simulation.SimCommand.Tick"/>.
    /// </summary>
    public long Tick => tick();

    /// <summary>
    /// Queues a command for the world.
    /// </summary>
    public void Enqueue(Simulation.SimCommand command) => enqueue(command);
}

/// <summary>
/// The extensions' character panel line and job panel button sources (docs/modding.md "Character panel lines and job panel buttons"), with the
/// town panel lines' limits, cuts and fault rules.
/// </summary>
sealed class CharacterPanelHooks
{
    /// <summary>
    /// The most lines the extensions together add to the character panel.
    /// </summary>
    public const int MaxLines = 4;

    /// <summary>
    /// The most buttons the extensions together add beside the jobs list.
    /// </summary>
    public const int MaxButtons = 9;

    /// <summary>
    /// Wraps an extension's press (its owner, what it is, the press) so a throw is reported and a removed extension's press does nothing.
    /// </summary>
    internal delegate Action PressGuard(string owner, string what, Action press);

    /// <summary>
    /// The line sources, with their owners, in registration order.
    /// </summary>
    readonly List<(string Owner, Func<UiCharacterContext, IEnumerable<UiCharacterLine>> Source)> _lines = [];

    /// <summary>
    /// The button sources, with their owners, in registration order.
    /// </summary>
    readonly List<(string Owner, Func<UiCharacterContext, IEnumerable<UiJobButton>> Source)> _buttons = [];

    /// <summary>
    /// The layouts the button sources asked for, with their owners, in registration order; the first is used.
    /// </summary>
    readonly List<(string Owner, UiJobButtonLayout Layout)> _layouts = [];

    /// <summary>
    /// The extensions (and what of theirs) left out past a limit, each reported once.
    /// </summary>
    readonly HashSet<(string Owner, string What)> _cut = [];

    /// <summary>
    /// Whether any extension adds character panel lines.
    /// </summary>
    public bool AnyLines => _lines.Count > 0;

    /// <summary>
    /// Whether any extension adds job panel buttons.
    /// </summary>
    public bool AnyButtons => _buttons.Count > 0;

    /// <summary>
    /// Adds <paramref name="owner"/>'s line source.
    /// </summary>
    public void AddLines(string owner, Func<UiCharacterContext, IEnumerable<UiCharacterLine>> source) => _lines.Add((owner, source));

    /// <summary>
    /// Adds <paramref name="owner"/>'s button source.
    /// </summary>
    public void AddButtons(string owner, Func<UiCharacterContext, IEnumerable<UiJobButton>> source, UiJobButtonLayout? layout = null)
    {
        _buttons.Add((owner, source));
        if (layout is not null) _layouts.Add((owner, layout.Clamped()));
    }

    /// <summary>
    /// The buttons' layout: the first registered source's that gave one, else <see cref="UiJobButtonLayout.Default"/>.
    /// </summary>
    public UiJobButtonLayout ButtonLayout => _layouts.Count > 0 ? _layouts[0].Layout : UiJobButtonLayout.Default;

    /// <summary>
    /// Removes <paramref name="owner"/>'s sources.
    /// </summary>
    public void Remove(string owner)
    {
        _lines.RemoveAll(s => s.Owner == owner);
        _buttons.RemoveAll(s => s.Owner == owner);
        _layouts.RemoveAll(l => l.Owner == owner);
    }

    /// <summary>
    /// The lines for <paramref name="context"/> (<see cref="Collect"/>): a label needed, a bar finite; label and value on one line and cut, a blank
    /// value none, the bar clamped to 0..1, the tooltip cut, the press guarded.
    /// </summary>
    public IReadOnlyList<UiCharacterLine> Lines(UiCharacterContext context, PressGuard guard, Action<UiDiagnostic>? report) =>
        Collect(_lines, context, MaxLines, "character panel lines", ValidLine, (owner, l) => ShownLine(owner, l, guard), report);

    /// <summary>
    /// The buttons for <paramref name="context"/> (<see cref="Collect"/>): a caption and a press needed; the caption on one line and cut, the tooltip
    /// cut, the press guarded.
    /// </summary>
    public IReadOnlyList<UiJobButton> Buttons(UiCharacterContext context, PressGuard guard, Action<UiDiagnostic>? report) =>
        Collect(_buttons, context, MaxButtons, "job panel buttons", ValidButton, (owner, b) => ShownButton(owner, b, guard), report);

    /// <summary>
    /// Whether a line has a label (or is a divider) and, when it has a bar, a finite one.
    /// </summary>
    static bool ValidLine(UiCharacterLine? l) =>
        l is not null && (l.Divider || !string.IsNullOrWhiteSpace(l.Label)) && (l.Bar is not { } bar || float.IsFinite(bar));

    /// <summary>
    /// Whether a button has a caption and a press.
    /// </summary>
    static bool ValidButton(UiJobButton? b) => b is not null && !string.IsNullOrWhiteSpace(b.Caption) && b.Press is not null;

    /// <summary>
    /// <paramref name="line"/> as the panel shows it.
    /// </summary>
    static UiCharacterLine ShownLine(string owner, UiCharacterLine line, PressGuard guard)
    {
        if (line.Divider) return new UiCharacterLine("") { Divider = true, Style = UiLineStyle.Shown(line.Style) };

        string label = UiHost.OneLine(line.Label);
        string? value = string.IsNullOrWhiteSpace(line.Value) ? null : UiHost.OneLine(line.Value);
        float? bar = line.Bar is { } b ? Math.Clamp(b, 0, 1) : null;
        var press = line.Press is null ? null : guard(owner, "character panel line " + label, line.Press);
        return new UiCharacterLine(label, value, bar, Tooltip(line.Tooltip), press) { Style = UiLineStyle.Shown(line.Style) };
    }

    /// <summary>
    /// <paramref name="button"/> as the panel shows it.
    /// </summary>
    static UiJobButton ShownButton(string owner, UiJobButton button, PressGuard guard)
    {
        string caption = UiHost.OneLine(button.Caption);
        return new UiJobButton(caption, Tooltip(button.Tooltip), guard(owner, "job panel button " + caption, button.Press))
        {
            Lit = button.Lit,
            Skin = string.IsNullOrWhiteSpace(button.Skin) ? null : button.Skin,
            Silent = button.Silent,
            Style = UiLineStyle.Shown(button.Style),
        };
    }

    /// <summary>
    /// A tooltip cut to <see cref="UiHost.MaxTownLineTooltip"/> characters; null for none.
    /// </summary>
    static string? Tooltip(string? tooltip) => tooltip is null ? null : UiHost.Cut(tooltip, UiHost.MaxTownLineTooltip);

    /// <summary>
    /// The items of <paramref name="sources"/> for <paramref name="context"/>, in registration order, at most <paramref name="max"/> of all sources
    /// together (a copy of the sources is walked, so a source may register or dispose). A source that throws, or gives an item
    /// <paramref name="valid"/> refuses, is reported once and dropped; one whose items pass the limit keeps those that fit, the rest left out and
    /// reported once.
    /// </summary>
    List<T> Collect<T>(List<(string Owner, Func<UiCharacterContext, IEnumerable<T>> Source)> sources, UiCharacterContext context, int max,
        string what, Func<T?, bool> valid, Func<string, T, T> shown, Action<UiDiagnostic>? report)
    {
        var items = new List<T>();
        foreach (var entry in sources.ToArray())
        {
            try
            {
                var added = entry.Source(context)
                    .Take(max + 1)
                    .ToList();
                if (!added.All(i => valid(i)))
                    throw new InvalidOperationException($"Invalid {what}: each needs its text (and a finite bar, a press).");

                int room = max - items.Count;
                if (added.Count > room) ReportCut(entry.Owner, what, max, report);
                items.AddRange(added
                    .Take(room)
                    .Select(i => shown(entry.Owner, i)));
            }
            catch (Exception e)
            {
                sources.Remove(entry);
                report?.Invoke(new UiDiagnostic(entry.Owner, what + " (dropped)", e));
            }
        }

        return items;
    }

    /// <summary>
    /// Reports once per extension and kind that some of its items were left out past <paramref name="max"/>.
    /// </summary>
    void ReportCut(string owner, string what, int max, Action<UiDiagnostic>? report)
    {
        if (!_cut.Add((owner, what))) return;

        var error = new InvalidOperationException($"The panel holds {max} {what} of all extensions together; the rest are left out.");
        report?.Invoke(new UiDiagnostic(owner, what + " (past the limit, left out)", error));
    }
}
