using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Player;

namespace Meitou.Game.Ui;

/// <summary>
/// The Overview's faction tab (<c>FactionsScreen</c>, docs/game/ui-screens.md "Faction tab"): the player faction's name, a
/// <c>Kenshi_FactionRelationsLine</c> per faction with the relation's number and its bar, and the information panel's two headings.
/// </summary>
sealed class FactionWindow(PlayerInterface player)
{
    internal const int FactionTab = 1;

    #region Info panel lines

    /// <summary>
    /// A contract or event row in the original's colours: names #492620, details and headings Main, queued campaigns Greyed.
    /// </summary>
    static PlayerInterface.HudLine InfoLine(FactionInfoLine l)
    {
        string value = double.IsNaN(l.Hours) ? l.Value : TotalTime(l.Hours);
        string? colour = l.Kind switch
        {
            FactionInfoKind.Name => MyGuiColours.Named("ContractName", "492620"),
            FactionInfoKind.Queued => PlayerInterface.GreyColour,
            FactionInfoKind.Heading or FactionInfoKind.Detail => PlayerInterface.MainColour,
            _ => null,
        };
        string? tint = l.Kind == FactionInfoKind.Name ? colour
            : l.Kind == FactionInfoKind.Queued ? PlayerInterface.GreyColour
            : PlayerInterface.MainColour;
        return new(Loc.Tr(l.Label), value, colour, tint);
    }

    /// <summary>
    /// A time left as <c>TimeOfDay::getTotalTimeString</c> (<c>FUN_1407a6940</c>) prints it: below 0 "Expired", above a day "{d} Days, {h}hrs", above
    /// an hour "{h}hrs", else "{m} mins" (each part floored, the day branch's hours as (uint)h % 24).
    /// </summary>
    static string TotalTime(double hours) =>
        hours < 0 ? Loc.Tr("Expired")
        : hours > 24 ? Loc.Tr("{1,num} Days, {2,num}hrs", (int)(hours / 24), (int)(hours % 24))
        : hours > 1 ? Loc.Tr("{1,num}hrs", (int)hours)
        : Loc.Tr("{1,num} mins", (int)(hours * 60));

    #endregion

    #region Relations list

    /// <summary>
    /// The factions the faction tab lists with the player's relation towards each (-100..100); null before a world with game data. The host sets it.
    /// </summary>
    public Func<IReadOnlyList<(string Name, float Relation)>>? FactionRelations { get; set; }

    /// <summary>
    /// The info panel's contract and Upcoming Events rows (docs/game/ui-screens.md "Faction tab").
    /// </summary>
    public Func<(IReadOnlyList<FactionInfoLine> Contracts, IReadOnlyList<FactionInfoLine> Events)>? FactionInfo { get; set; }

    internal MyGuiLayout? RelationsLineLayout;
    float _relationsScroll;

    bool FactionShown => player.FaithfulScreens.IsOpen("overview") && player.FaithfulScreens.GetTab("overview", "TabsMain") == FactionTab;

    /// <summary>
    /// The relations list (<c>FUN_140499960</c> / <c>FUN_140499630</c>): the lines sorted by faction name, each the panel's width less the scroll bar
    /// and stacked with 3 px (at 1080 lines) between them; the number in BadBright below -30, Good above 60, else Main; a negative relation fills the
    /// left bar leftwards from the middle, a positive one the right bar rightwards, |relation| % of their width.
    /// </summary>
    internal void AddFactionContent(List<UiControl> controls)
    {
        if (!FactionShown) return;

        var scale = player.Height / 1080f;
        if (controls.FirstOrDefault(c => c.Name == "FactionInfoPanel") is { } info) AddFactionInfo(controls, info, scale);
        if (RelationsLineLayout is not { } layout || controls.FirstOrDefault(c => c.Name == "RelationsPanel") is not { } panel) return;

        AddRelationsLines(controls, panel, layout, scale);
    }

    /// <summary>
    /// The faction tab's info panel: the contracts, then the upcoming events.
    /// </summary>
    void AddFactionInfo(List<UiControl> controls, UiControl info, float scale)
    {
        // The contracts clear the panel before their rows (FUN_1404927f0), so the Active Contracts heading written before them never shows.
        var (contracts, events) = FactionInfo?.Invoke() ?? ([], []);
        var lines = contracts.Select(InfoLine).ToList();
        lines.Add(new(Loc.Tr("---Upcoming Events---"), "", PlayerInterface.GreyColour));
        lines.AddRange(events.Select(InfoLine));
        // A DatapanelGUI's rows start 0.03 of its height down (AddDatapanel).
        player.AddLines(controls, "FactionInfoPanel", lines, 20 * scale, player.StandardPixels, top: 0.03f * info.Bounds.Height);
    }

    /// <summary>
    /// The relations list: a line per faction, by name, on the scroll view's client.
    /// </summary>
    void AddRelationsLines(List<UiControl> controls, UiControl panel, MyGuiLayout layout, float scale)
    {
        var factions = (FactionRelations?.Invoke() ?? []).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        // The lines go on the scroll view's client (Kenshi_ScrollView: 10 px inside the frame at the template's scale), not on its frame.
        var client = controls.FirstOrDefault(c => c.Properties.GetValueOrDefault("TemplateOwner") == panel.Name
            && c.Properties.GetValueOrDefault("TemplatePart") == "Client");
        var area = client?.Bounds ?? panel.Bounds;
        var clip = panel.Clip is { } outer ? area.Intersect(outer) : area;
        var root = layout.Roots.FirstOrDefault();
        float lineHeight = root is null ? 24 * scale : root.Position.RelativeTo(new(0, 0, player.Width, player.Height), root.RealPosition).Height;
        float lineWidth = area.Width - SquadPanel.SquadScrollBar * scale;
        float step = lineHeight + 3 * scale;
        _relationsScroll = Math.Clamp(_relationsScroll, 0, Math.Max(0, factions.Count * step - area.Height));
        for (int i = 0; i < factions.Count; i++)
        {
            var (name, relation) = factions[i];
            var line = new UiRect(area.X, area.Y + i * step - _relationsScroll, lineWidth, lineHeight);
            if (line.Intersect(clip).Height <= 0) continue;

            var b = new UiBindings { TemplateScale = scale };
            if (root is not null) b.Positions[root.Name] = new(0, 0, lineWidth, lineHeight);
            b.Text["FactionName"] = name;
            b.Text["FactionValue"] = ((int)relation).ToString(CultureInfo.InvariantCulture);
            string valueColour = relation < -30 ? PlayerInterface.BadBrightColour
                : relation > 60 ? PlayerInterface.GoodColour
                : PlayerInterface.MainColour;
            b.Properties["FactionValue"] = new Dictionary<string, string> { ["TextColour"] = valueColour };
            SetBarPositions(b, layout, relation, lineWidth, lineHeight);
            int shownWidth = (int)MathF.Round(lineWidth);
            int shownHeight = (int)MathF.Round(lineHeight);
            foreach (var c in MyGuiPresentation.Arrange(layout, shownWidth, shownHeight, b, player.UiResources.Layers))
            {
                var own = c.Clip is { } inner ? (inner with { X = inner.X + line.X, Y = inner.Y + line.Y }).Intersect(clip) : clip;
                controls.Add(c with
                {
                    Name = c.Name.Length > 0 ? $"faction-{i}-{c.Name}" : c.Name,
                    Bounds = c.Bounds with { X = c.Bounds.X + line.X, Y = c.Bounds.Y + line.Y },
                    Clip = own,
                    Properties = new Dictionary<string, string>(c.Properties, StringComparer.Ordinal) { ["FactionLine"] = "1" }
                });
            }
        }
    }

    /// <summary>
    /// Sizes a relations line's bars: the left one for a negative relation, the right one for a positive.
    /// </summary>
    static void SetBarPositions(UiBindings b, MyGuiLayout layout, float relation, float lineWidth, float lineHeight)
    {
        foreach (var (bar, negative) in new[] { ("LeftBar", true), ("RightBar", false) })
        {
            if (layout.Find(bar) is not { } widget) continue;

            var full = widget.Position.RelativeTo(new(0, 0, lineWidth, lineHeight), widget.RealPosition);
            float share = negative ? Math.Max(0, -relation) / 100 : Math.Max(0, relation) / 100;
            float width = full.Width * Math.Min(share, 1);
            b.Positions[bar] = negative ? full with { X = full.X + full.Width - width, Width = width } : full with { Width = width };
        }
    }

    /// <summary>
    /// The wheel over the relations list scrolls it.
    /// </summary>
    internal bool FactionWheel(Vector2 p, float steps)
    {
        if (!FactionShown) return false;
        if (player.FaithfulScreens.HitTest(p) is not { Screen: "overview" } hit) return false;
        if (hit.Control.Name != "RelationsPanel" && !hit.Control.Properties.ContainsKey("FactionLine")) return false;

        _relationsScroll -= steps * 44 * (player.Height / 1080f);   // clamped when the list is laid out
        return true;
    }

    #endregion

    #region Faction name

    internal const string FactionNameBox = "FactionNameText";

    /// <summary>
    /// What was typed in the name box and is not yet accepted (null: nothing).
    /// </summary>
    string? _factionDraft;

    /// <summary>
    /// Whether another faction already has a name (the host's data); null: the simulation alone decides.
    /// </summary>
    public Func<string, bool>? FactionNameTaken { get; set; }

    /// <summary>
    /// The player faction's name in the tab's name box (the original's text is the player interface's faction name). The box takes typing; accepting
    /// it or losing the key focus renames (<see cref="UpdateFactionName"/>, <c>FUN_1404923d0</c>), and a box not being typed in shows the faction's
    /// current name.
    /// </summary>
    internal void BindFactionTab(UiBindings b)
    {
        string name = player.FactionName?.Invoke(player.Session.World.Player.Faction) ?? "Nameless";
        if (player.FaithfulScreens.Focus is not { Screen: "overview", Widget: FactionNameBox })
            player.FaithfulScreens.SetText("overview", FactionNameBox, name);
        b.Text[FactionNameBox] = name;
    }

    /// <summary>
    /// A text change in the name box is kept as the draft; the box losing the focus (or the tab closing) sends the rename of the last one.
    /// </summary>
    internal void FactionAction(MyGuiEvent e)
    {
        if (e.Widget == FactionNameBox && e.Action == "text") _factionDraft = e.Text;
    }

    /// <summary>
    /// Once a frame: a draft whose box no longer has the key focus is sent as <see cref="RenameFactionCommand"/>; a name another faction has opens
    /// the original's Ok box "Faction Name" / "This faction name already exists" instead (<c>FUN_1404923d0</c>, button mask 1), and the box shows the
    /// old name again.
    /// </summary>
    internal void UpdateFactionName()
    {
        if (_factionDraft is not { } draft || player.FaithfulScreens.Focus is { Screen: "overview", Widget: FactionNameBox }) return;

        _factionDraft = null;
        if (FactionNameTaken?.Invoke(draft) == true)
        {
            player.MessageBox("Faction Name", "This faction name already exists");
            return;
        }

        player.Say(new RenameFactionCommand(draft) { Tick = player.Session.World.Tick });
    }

    #endregion
}