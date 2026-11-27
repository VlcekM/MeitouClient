using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Ui;

namespace Meitou.Game.Building;

/// <summary>
/// Meitou's search row over the build window (docs/game/ui-screens.md "Build menu search"): a text box, a clear button and a tick for each of
/// categories, buildings and subtypes, which choose what the query looks through (<see cref="BuildMenuSearch"/>). The query and ticks stay while the
/// game runs, also after the window closes.
/// </summary>
sealed class BuildSearchBar(PlayerInterface player)
{
    #region State

    /// <summary>
    /// The build window's screen id.
    /// </summary>
    const string Screen = "build";

    /// <summary>
    /// The text box's widget name.
    /// </summary>
    internal const string Box = "build-search-box";

    /// <summary>
    /// The clear button's widget name.
    /// </summary>
    internal const string ClearButton = "build-search-clear";

    /// <summary>
    /// The ticks' widget name prefix, followed by the scope's index in <see cref="Ticks"/>.
    /// </summary>
    internal const string TickPrefix = "build-search-tick-";

    /// <summary>
    /// The longest query.
    /// </summary>
    const int MaxLength = 60;

    /// <summary>
    /// The ticks' scopes, in the row's order.
    /// </summary>
    static readonly BuildSearchScope[] Ticks = [BuildSearchScope.Categories, BuildSearchScope.Buildings, BuildSearchScope.Subtypes];

    /// <summary>
    /// The query and what it looks through.
    /// </summary>
    internal BuildMenuSearch Filter { get; private set; } = BuildMenuSearch.Empty;

    #endregion

    #region Events

    /// <summary>
    /// Whether <paramref name="e"/> is an event of the search row.
    /// </summary>
    internal static bool Owns(MyGuiEvent e) => e.Widget is Box or ClearButton || e.Widget.StartsWith(TickPrefix, StringComparison.Ordinal);

    /// <summary>
    /// An event of the row: text typed, the clear button, or a tick; true when the filter changed.
    /// </summary>
    internal bool Action(MyGuiEvent e)
    {
        var before = Filter;
        if (e.Widget == Box && e.Action == "text") Filter = Filter with { Query = e.Text };
        else if (e.Widget == ClearButton) Clear();
        else if (TickIndex(e.Widget) is { } tick) Filter = Filter with { Scope = Filter.Scope ^ Ticks[tick] };

        return Filter != before;
    }

    /// <summary>
    /// Empties the query (the ticks stay) and leaves the box.
    /// </summary>
    internal void Clear()
    {
        Filter = Filter with { Query = "" };
        if (!player.FaithfulScreens.IsRegistered(Screen)) return;

        player.FaithfulScreens.SetText(Screen, Box, "");
        if (Focused) player.FaithfulScreens.Blur();
    }

    /// <summary>
    /// Esc while the box has the keys: empties a filled box, leaves an empty one; false (not taken) while the box does not have the keys.
    /// </summary>
    internal bool Escape()
    {
        if (!Focused) return false;

        if (Filter.Query.Length > 0)
        {
            Filter = Filter with { Query = "" };
            player.FaithfulScreens.SetText(Screen, Box, "");
        }
        else player.FaithfulScreens.Blur();

        return true;
    }

    /// <summary>
    /// Whether the box has the keys.
    /// </summary>
    bool Focused => player.FaithfulScreens.Focus is { Screen: Screen, Widget: Box };

    /// <summary>
    /// The index in <see cref="Ticks"/> of tick widget <paramref name="widget"/>; null for another widget.
    /// </summary>
    static int? TickIndex(string widget) =>
        widget.StartsWith(TickPrefix, StringComparison.Ordinal)
        && int.TryParse(widget.AsSpan(TickPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int tick)
        && tick < Ticks.Length
            ? tick
            : null;

    #endregion

    #region Row

    /// <summary>
    /// Adds the row over <paramref name="window"/>, the build window's rectangle: a panel the window's width holding the box (its hint while empty),
    /// the clear button and the ticks with their labels.
    /// </summary>
    internal void AddRow(List<UiControl> controls, UiRect window)
    {
        float s = player.UiScale;
        float height = 34 * s, pad = 4 * s;
        var row = new UiRect(window.X, window.Y - height, window.Width, height);
        controls.Add(new("build-search-panel", "Widget", row, "", "", "Kenshi_FloatingPanelSkin", true, new Dictionary<string, string>(), Vector4.Zero));

        float ticksWidth = MathF.Min(row.Width * .5f, 390 * s);
        float boxHeight = height - 2 * pad;
        var box = new UiRect(row.X + 2 * pad, row.Y + pad, row.Width - ticksWidth - boxHeight - 5 * pad, boxHeight);
        AddBox(controls, box);
        AddClearButton(controls, new(box.X + box.Width + pad, box.Y, boxHeight, boxHeight));
        AddTicks(controls, new(row.X + row.Width - ticksWidth - pad, row.Y, ticksWidth, height));
    }

    /// <summary>
    /// The text box with the query in the frame and ink of the window's building name (<c>Kenshi_GenericTextBoxFlatSkin</c>,
    /// <c>Kenshi_TextboxStandardText</c>), the caret while it has the keys, and a hint (<see cref="HintInk"/>) while it is empty and does not.
    /// </summary>
    void AddBox(List<UiControl> controls, UiRect at)
    {
        string ink = MyGuiColours.TemplateInk(player.UiResources, "Kenshi_TextboxStandardText");
        controls.Add(new("build-search-frame", "Widget", at, "", "", "Kenshi_GenericTextBoxSkin", true, new Dictionary<string, string>(),
            Vector4.Zero, Pickable: false));

        var inset = new UiRect(at.X + 8 * player.UiScale, at.Y, at.Width - 16 * player.UiScale, at.Height);
        var properties = new Dictionary<string, string>
        {
            ["FontName"] = "Kenshi_StandardFont_Medium",
            ["TextColour"] = ink,
            ["TextAlign"] = "Left VCenter",
            ["MaxTextLength"] = MaxLength.ToString(CultureInfo.InvariantCulture),
        };
        controls.Add(WithCaret(new UiControl(Box, "EditBox", inset, Filter.Query, "", "Kenshi_EditBoxStrechEmpty", true, properties, Vector4.Zero)));
        if (Filter.Query.Length > 0 || Focused) return;

        var hint = new Dictionary<string, string>
        {
            ["FontName"] = "Kenshi_StandardFont_Medium", ["TextColour"] = HintInk(), ["TextAlign"] = "Left VCenter",
        };
        controls.Add(new("build-search-hint", "TextBox", inset, Loc.Tr("Search buildings"), "", "", true, hint, Vector4.Zero, Pickable: false));
    }

    /// <summary>
    /// The hint's ink: the light tan of the selected line in the window's CATEGORIES and BUILDINGS lists (their line skin
    /// <c>Kenshi_ListBoxItemSkin</c> in its <c>normal_checked</c> state), so a skin mod recolours both together.
    /// </summary>
    string HintInk() => MyGuiColours.SkinText(player.UiResources, "Kenshi_ListBoxItemSkin", "normal_checked") ?? "0.626 0.605 0.578";

    /// <summary>
    /// <paramref name="box"/> drawn focused with the caret and selection while it has the keys.
    /// </summary>
    UiControl WithCaret(UiControl box)
    {
        if (player.FaithfulScreens.Focus is not { Screen: Screen, Widget: Box } focus) return box;

        var properties = new Dictionary<string, string>(box.Properties, StringComparer.Ordinal)
        {
            ["Focused"] = "true",
            ["CaretIndex"] = focus.Caret.ToString(CultureInfo.InvariantCulture),
            ["SelectionStart"] = focus.SelectionStart.ToString(CultureInfo.InvariantCulture),
            ["SelectionLength"] = focus.SelectionLength.ToString(CultureInfo.InvariantCulture),
        };
        return box with { Properties = properties };
    }

    /// <summary>
    /// The clear button, the windows' close cross, enabled while the box holds text.
    /// </summary>
    void AddClearButton(List<UiControl> controls, UiRect at) =>
        controls.Add(new(ClearButton, "Button", at, "", ClearButton, "Kenshi_CloseButtonSkin", Filter.Query.Length > 0,
            new Dictionary<string, string>(), Vector4.Zero));

    /// <summary>
    /// The three ticks, each a box with its label after it in the window's header look (<c>Kenshi_TextboxPaintedText</c>, as CATEGORIES),
    /// sharing <paramref name="area"/> evenly.
    /// </summary>
    void AddTicks(List<UiControl> controls, UiRect area)
    {
        float s = player.UiScale;
        float slot = area.Width / Ticks.Length;
        float tickSize = MathF.Min(area.Height - 10 * s, 22 * s);
        var label = new Dictionary<string, string> { ["TextAlign"] = "Left VCenter" };

        for (int i = 0; i < Ticks.Length; i++)
        {
            string index = i.ToString(CultureInfo.InvariantCulture);
            string ticked = Filter.Scope.HasFlag(Ticks[i]) ? "true" : "false";
            var tick = new UiRect(area.X + i * slot, area.Y + (area.Height - tickSize) / 2, tickSize, tickSize);
            controls.Add(new(TickPrefix + index, "Button", tick, "", TickPrefix + index, "Kenshi_TickBoxSkin", true,
                new Dictionary<string, string> { ["Checked"] = ticked, ["StateSelected"] = ticked }, Vector4.Zero));

            var text = new UiRect(tick.X + tickSize + 5 * s, area.Y, slot - tickSize - 6 * s, area.Height);
            controls.Add(new("build-search-tick-label-" + index, "TextBox", text, TickLabel(Ticks[i]).ToUpperInvariant(), "", "Kenshi_TextboxPaintedText", true,
                label,
                Vector4.Zero, Pickable: false));
        }
    }


    /// <summary>
    /// The label of the tick for <paramref name="scope"/>.
    /// </summary>
    static string TickLabel(BuildSearchScope scope) => scope switch
    {
        BuildSearchScope.Categories => Loc.Tr("Categories"),
        BuildSearchScope.Buildings => Loc.Tr("Buildings"),
        _ => Loc.Tr("Subtypes"),
    };

    #endregion
}
