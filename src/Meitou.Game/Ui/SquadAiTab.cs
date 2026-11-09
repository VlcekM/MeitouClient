using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Game.Ui;

/// <summary>
/// The overview's AI tab (docs/game/ai-tasks.md "Squad AI options"): a tick button per squad AI option in <c>AIOptions</c>, the hovered one's
/// tooltip (a static tooltip, PlayerInterface.TooltipRuntime.AddHoverLabel) in <c>AIToolTip</c> (captions and tooltips are the original's,
/// <c>FUN_140005e11</c>; the row order follows their order in the exe, <b>Observed</b>), and the Follow Formation combo. The changes are
/// <see cref="SquadAiCommand"/>s and <see cref="SquadFormationCommand"/>s.
/// </summary>
sealed class SquadAiTab(PlayerInterface player)
{
    /// <summary>
    /// The AI tab's index in the overview's <c>TabsMain</c>.
    /// </summary>
    const int AiTab = 6;

    /// <summary>
    /// The tick buttons in order: the option each toggles, its caption and its tooltip.
    /// </summary>
    static readonly (SquadAi Option, string Caption, string Tip)[] AiOptionRows =
    [
        (SquadAi.Ditch, "Ditch items", "If they have nothing else to do, characters will automatically put any excess materials they are carrying "
            + "into storage.  This can help to un-block the base AI when things grind to a halt from over-filled inventories."),
        (SquadAi.Sleep, "Sleep when injured", "Characters will automatically go to bed if they are injured and have nothing else to do"),
        (SquadAi.Sit, "Sit when idle", "Idle characters at home will sit down on chairs and benches, if you have any"),
        (SquadAi.Heal, "Heal Allies", "Characters with the medic job will also heal characters of allied factions"),
        (SquadAi.Help, "Help Allies", "Characters will help out characters from other allied factions if they are under attack"),
        (SquadAi.Rescue, "Rescue Allies", "Characters with the rescue job will also rescue allied characters from other factions"),
        (SquadAi.Share, "Share Food", "Characters in a squad will automatically share any of their food that is in a backpack (if in range)."),
        (SquadAi.Feed, "Feed Animals", "If food is running low, you can elect not to feed your animals."),
        (SquadAi.ShootFirst, "Shoot first, ask later", "Your turret gunners will shoot at any potentially dangerous but neutral targets, such as "
            + "wild animals or bandits, without waiting for them to attack the base first."),
        // Meitou-only lines (not in the original's tab).
        (SquadAi.HealNeutrals, "Heal Neutrals", "Characters with the medic job will also heal downed characters of factions that are not hostile"),
        (SquadAi.HealAll, "Heal All", "Characters with the medic job will heal any downed character, enemies included"),
    ];

    /// <summary>
    /// The Follow Formation items in the original's order (<c>FUN_140494ce0</c>: Random 0, Military 2, Caravan 1).
    /// </summary>
    static readonly (SquadFormation Formation, string Caption)[] FormationItems =
        [(SquadFormation.Random, "Random"), (SquadFormation.Military, "Military"), (SquadFormation.Caravan, "Caravan")];

    /// <summary>
    /// The formation combo box's widget name in the overview layout.
    /// </summary>
    const string FormationCombo = "FormationCombo";

    /// <summary>
    /// Where the formation combo box was put into the overview layout (fractions of <c>AIOptions</c>); it is put there again when the panel's size
    /// moves it.
    /// </summary>
    string? _formationComboPlace;

    /// <summary>
    /// The AI tab's content while it is the overview's open tab: a tick button per option (ticked when the player faction has it on), then the
    /// formation line.
    /// </summary>
    internal void AddAiContent(List<UiControl> controls)
    {
        if (player.FaithfulScreens.GetTab("overview", "TabsMain") != AiTab || controls.FirstOrDefault(c => c.Name == "AIOptions") is not { } panel)
            return;

        var on = player.Session.CurrentSnapshot.SquadAi;

        // The original's panel is sized for its nine lines and the formation; lines shrink when the Meitou-only ones do not fit.
        float line = MathF.Min(MathF.Max(22, 30 * player.InventoryScale), panel.Bounds.Height / (AiOptionRows.Length + 1));

        for (int i = 0; i < AiOptionRows.Length; i++)
        {
            bool ticked = (on & AiOptionRows[i].Option) != 0;
            var bounds = new UiRect(panel.Bounds.X + 6, panel.Bounds.Y + i * line, panel.Bounds.Width - 12, line - 4);
            controls.Add(new($"ai-option-{i}", "Button", bounds, Loc.Tr(AiOptionRows[i].Caption),
                "ai-option:" + i.ToString(CultureInfo.InvariantCulture), "Kenshi_TickButton1Skin", true,
                new Dictionary<string, string>(UiTooltips.TextProperties(AiOptionRows[i].Tip, "static:AIToolTip"))
                {
                    ["Checked"] = ticked ? "true" : "false",
                    ["StateSelected"] = ticked ? "true" : "false",
                    ["FontName"] = "Kenshi_PaintedTextFont_Medium",
                    ["TextAlign"] = "Center",
                },
                Vector4.Zero, Pickable: true, Clip: panel.Bounds));
        }

        AddFormationRow(controls, panel.Bounds, AiOptionRows.Length * line, line - 4);
    }

    /// <summary>
    /// The AI tab's last line (<c>FUN_140494ce0</c>, a <c>DataPanelLine_DropBox</c>): "Follow Formation" with a <c>Kenshi_ComboBox</c> right-aligned
    /// at 0.97 of the panel's width, its tooltip "Formation used when squad members are following a leader" in <c>AIToolTip</c>. The combo is a
    /// widget of the overview layout (the screen runs its popup); its width, 0.35 of the panel, is an engine choice (the original's size is
    /// <b>Unknown</b>).
    /// </summary>
    void AddFormationRow(List<UiControl> controls, UiRect panel, float top, float height)
    {
        if (panel.Height <= 0 || panel.Width <= 0) return;

        const float width = 0.35f;
        float y = top / panel.Height, h = height / panel.Height;
        var labelBounds = new UiRect(panel.X + 6, panel.Y + top, panel.Width * (0.97f - width) - 6, height);
        var labelTip = UiTooltips.TextProperties("Formation used when squad members are following a leader", "static:AIToolTip");
        controls.Add(new("ai-formation-label", "TextBox", labelBounds, Loc.Tr("Follow Formation"), "", "Kenshi_TextboxStandardText", true,
            new Dictionary<string, string>(labelTip) { ["TextAlign"] = "Left VCenter" }, Vector4.Zero, Pickable: true, Clip: panel));
        string place = string.Create(CultureInfo.InvariantCulture, $"{y:0.0000} {h:0.0000}");
        if (place == _formationComboPlace) return;

        _formationComboPlace = place;
        var layout = player.FaithfulScreens.Layout("overview");

        // The screen's layout is expanded already (its expansion stops at resolved templates), so the combo's template is expanded on its own first.
        var widget = new MyGuiWidget("ComboBox", FormationCombo, "Kenshi_ComboBox", "", "Left Top", new(0.97f - width, y, width, h), true,
            new Dictionary<string, string> { ["ReadOnly"] = "true" }, new Dictionary<string, string>(), []);
        var combo = MyGuiTemplates.Expand(new MyGuiLayout(layout.Version, [widget]), player.UiResources).Roots[0];

        MyGuiWidget Put(MyGuiWidget w) => w.Name == "AIOptions"
            ? w with { Children = [.. w.Children.Where(c => c.Name != FormationCombo), combo] }
            : w with { Children = [.. w.Children.Select(Put)] };

        player.FaithfulScreens.Replace("overview", layout with { Roots = [.. layout.Roots.Select(Put)] });
    }

    /// <summary>
    /// The combo's items and the player faction's formation (<see cref="SquadAiKeys.FormationOf"/>), once the combo is in the layout.
    /// </summary>
    internal void BindFormation(UiBindings b)
    {
        if (_formationComboPlace is null) return;

        int current = SquadAiKeys.FormationOf(player.Session.World, player.Session.World.Player.Faction, null);
        b.Lists[FormationCombo] =
            [.. FormationItems.Select(f => new UiListItem(((int)f.Formation).ToString(CultureInfo.InvariantCulture), Loc.Tr(f.Caption)))];
        b.SelectedItems[FormationCombo] = Array.FindIndex(FormationItems, f => (int)f.Formation == current);
    }

    /// <summary>
    /// An overview event for the AI tab: a formation picked in the combo, or a tick button pressed (the option flips).
    /// </summary>
    internal void AiAction(MyGuiEvent e)
    {
        if (e.Widget == FormationCombo && e.Action == "select")
        {
            if (int.TryParse(e.Text, CultureInfo.InvariantCulture, out int formation) && Enum.IsDefined((SquadFormation)formation))
                player.Say(new SquadFormationCommand((SquadFormation)formation) { Tick = player.Session.World.Tick });

            return;
        }

        if (!e.Action.StartsWith("ai-option:", StringComparison.Ordinal)
            || !int.TryParse(e.Action.AsSpan(10), CultureInfo.InvariantCulture, out int i) || (uint)i >= (uint)AiOptionRows.Length)
        {
            return;
        }

        var option = AiOptionRows[i].Option;
        player.Say(new SquadAiCommand(option, (player.Session.CurrentSnapshot.SquadAi & option) == 0) { Tick = player.Session.World.Tick });
    }
}
