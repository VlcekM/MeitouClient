using System.Globalization;
using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Ui;

namespace Meitou.Game.Ui;

/// <summary>The character's stats window (docs/game/ui-screens.md): the skills by category with their levels and the next level's progress.</summary>
sealed class StatsWindow(PlayerInterface player)
{
    internal StatHelpCatalog statHelp = new();
    internal void BindStatsGeometry(MyGuiLayout layout, UiBindings bindings)
    {
        bindings.TemplateScale = player.InventoryScale;
        bindings.Positions["Root"] = new(0, 0, 980 * player.InventoryScale, 650 * player.InventoryScale);
        bindings.Hidden.Add("lbDerivedStats");
        // The installed layout includes an unused derived-statistics column. The runtime screen
        // gives that space to skills and description (observed in the original-game reference).
        foreach (var widget in layout.Widgets)
        {
            if (widget.Children.Any(c => c.Name == "StatisticsPanel")) bindings.HiddenWidgets.Add(widget);
            if (widget.Children.Any(c => c.Name == "Skills1Panel"))
                bindings.RelativePositions[widget] = new(0.272f, 0.0409836f, 0.723f, 0.508197f);
            if (widget.Children.Any(c => c.Name == "Description1Panel"))
                bindings.RelativePositions[widget] = new(0.0039557f, 0.555738f, 0.991f, 0.427869f);
            if (widget.Children.Any(c => c.Name == "AttributesPanel"))
                bindings.RelativePositions[widget] = new(0.00791139f, 0.0409836f, 0.25f, 0.295082f);
        }
        if (layout.Find("lbAttributes") is { } attributes)
            bindings.RelativePositions[attributes] = new(0.00791139f, 0.0163934f, 0.25f, 0.0327869f);
        if (layout.Find("lbSkills") is { } skills)
            bindings.RelativePositions[skills] = new(0.272f, 0.0163934f, 0.723f, 0.0327869f);
    }

    // The category titles are the catalog's own words in dashes ("-Weapons-"): the layout has no text for them (docs/native-ui.md).
    static readonly (int Before, string Caption)[][] StatHeadings =
    [
        [], [(0, "Weapons")], [(0, "Combat"), (4, "Ranged")],
        [(0, "Thievery"), (4, "Athletics")], [(0, "Sciences"), (4, "Trades")],
    ];

    internal void AddStatsContent(List<UiControl> controls)
    {
        var lead = player.Selected().FirstOrDefault();
        float scale = player.InventoryScale, rowHeight = 20 * scale;
        // The skill columns sit in Kenshi_GenericTextBoxSkin frames (the attributes column's frame has no text colour and keeps its grey).
        string statInk = MyGuiColours.TemplateInk(player.uiResources, "Kenshi_GenericTextBoxSkin");
        for (int column = 0; column < PlayerInterface.StatPanels.Length; column++)
        {
            string name = column == 0 ? "AttributesPanel" : $"Skills{column}Panel";
            if (controls.FirstOrDefault(c => c.Name == name) is not { } panel) continue;
            var clip = panel.Bounds.Intersect(panel.Clip ?? panel.Bounds);
            float y = panel.Bounds.Y;
            for (int i = 0; i < PlayerInterface.StatPanels[column].Length; i++)
            {
                foreach (var heading in StatHeadings[column].Where(h => h.Before == i))
                {
                    Text($"{name}-heading-{i}", "-" + Loc.Tr(heading.Caption) + "-", "Kenshi_StandardFont_Large", PlayerInterface.TitleColour, "Left VCenter", "", heading: true);
                    y += rowHeight;
                }
                var stat = PlayerInterface.StatPanels[column][i];
                string id = ((int)stat).ToString(CultureInfo.InvariantCulture), action = "stat:" + id;
                string label = column == 0 ? PlayerInterface.StatLabel(stat).ToUpperInvariant() : PlayerInterface.StatLabel(stat);
                string font = column == 0 ? "Kenshi_PaintedTextFont_Medium" : "Kenshi_StandardFont_Medium";
                string colour = column == 0 ? "0.4 0.380392 0.356863" : statInk;
                bool wrapLabel = stat is StatsEnumerated.FriendlyFire or StatsEnumerated.SmithingBow;
                Text($"{name}-label-{i}", label, font, colour, "Left VCenter", action, wrap: wrapLabel);
                Text($"{name}-value-{i}", (lead?.AllSkills.ElementAtOrDefault((int)stat) ?? 0).ToString("0", CultureInfo.InvariantCulture),
                    font, colour, "Right VCenter", action);
                y += column == 0 ? 20 * scale : rowHeight * (wrapLabel ? 2 : 1);
            }

            void Text(string controlName, string caption, string font, string colour, string align, string action, bool heading = false, bool wrap = false)
            {
                var properties = new Dictionary<string, string> { ["FontName"] = font, ["TextColour"] = colour, ["TextAlign"] = align };
                if (wrap) properties["WordWrap"] = "true";
                // Runtime category titles are larger than the surrounding standard text.
                if (heading) properties["FontPixels"] = Math.Max(8, (int)MathF.Round(18 * scale)).ToString(CultureInfo.InvariantCulture);
                bool value = align.StartsWith("Right", StringComparison.Ordinal);
                float valueWidth = 32 * scale;
                controls.Add(new(controlName, action.Length > 0 ? "Button" : "TextBox",
                    new(panel.Bounds.X + (heading ? 5 * scale : value ? panel.Bounds.Width - valueWidth : 0), y - (heading ? 2 * scale : 0),
                        heading ? panel.Bounds.Width - 5 * scale : value ? valueWidth : Math.Max(0, panel.Bounds.Width - valueWidth - 4 * scale),
                        heading ? 24 * scale : rowHeight * (wrap ? 2 : 1)), caption, action, "", true,
                    properties,
                    Vector4.Zero, Pickable: action.Length > 0, Clip: clip));
            }
        }
        AddStatDescription(controls);
    }

    void AddStatDescription(List<UiControl> controls)
    {
        if (!int.TryParse(player.selectedStat, out int selected) || selected <= 0 || selected >= StatsInfo.Count
            || controls.FirstOrDefault(c => c.Name == "Description1Panel") is not { } left
            || controls.FirstOrDefault(c => c.Name == "Description2Panel") is not { } right) return;
        var lead = player.Selected().FirstOrDefault();
        var stat = (StatsEnumerated)selected;
        var help = statHelp.For(stat);
        string statInk = MyGuiColours.TemplateInk(player.uiResources, "Kenshi_GenericTextBoxSkin");
        float scale = player.InventoryScale, row = Math.Min(20 * scale, (left.Bounds.Height - 4 * scale) / Math.Max(1, 3 + help.Affected.Count + help.Training.Count));
        float y = left.Bounds.Y + 4 * scale;
        var leftClip = left.Bounds.Intersect(left.Clip ?? left.Bounds);
        var rightClip = right.Bounds.Intersect(right.Clip ?? right.Bounds);

        void Text(string name, UiRect bounds, string caption, string colour, int pixels, UiRect clip, string align = "Left Top", bool wrap = false)
        {
            controls.Add(new(name, "TextBox", bounds, caption, "", "", true,
                new Dictionary<string, string> { ["FontPixels"] = Math.Max(8, (int)MathF.Round(pixels * scale)).ToString(CultureInfo.InvariantCulture),
                    ["TextColour"] = colour, ["TextAlign"] = align, ["WordWrap"] = wrap ? "true" : "false" },
                Vector4.Zero, Pickable: false, Clip: clip));
        }
        void Line(string name, string text, string colour, int pixels = 14)
        {
            Text(name, new(left.Bounds.X + 19 * scale, y, left.Bounds.Width - 27 * scale, pixels == 18 ? 24 * scale : row), text, colour, pixels, leftClip);
            y += row;
        }
        Line("stat-description-title", PlayerInterface.StatLabel(stat), PlayerInterface.TitleColour, 18);
        Line("stat-affected-heading", statHelp.TextAt(681), PlayerInterface.MainColour);
        // The rows' numbers (FUN_1408b1250, HudView.StatReadouts), matched to the catalog's rows by their label, right-aligned on the row.
        var readouts = lead?.Hud?.StatReadouts?.GetValueOrDefault(stat) ?? [];
        for (int i = 0; i < help.Affected.Count; i++)
        {
            var readout = readouts.FirstOrDefault(r => Loc.Tr(r.Label) == help.Affected[i]);
            string ink = readout.Main ? PlayerInterface.MainColour : statInk;
            if (readout.Value is { Length: > 0 } number)
                Text($"stat-affected-value-{i}", new(left.Bounds.X + 19 * scale, y, left.Bounds.Width - 27 * scale, row), number, ink, 14, leftClip, "Right Top");
            Line($"stat-affected-{i}", "-" + help.Affected[i].TrimEnd(':'), ink);
        }
        Line("stat-training-heading", statHelp.TextAt(684), PlayerInterface.MainColour);
        for (int i = 0; i < help.Training.Count; i++)
            Line($"stat-training-{i}", "-" + help.Training[i], statInk);

        float value = lead?.AllSkills.ElementAtOrDefault(selected) ?? 0;
        float progress = float.IsFinite(value) ? value - MathF.Floor(value) : 0;
        var bar = new UiRect(right.Bounds.X + 17 * scale, right.Bounds.Y + 8 * scale, Math.Max(0, right.Bounds.Width - 30 * scale), 12 * scale);
        controls.Add(new("stat-next-level-track", "Widget", bar, "", "", "Kenshi_ProgressBarEmptySkin", true,
            new Dictionary<string, string>(), Vector4.Zero, Pickable: false, Clip: rightClip));
        if (progress > 0)
            controls.Add(new("stat-next-level-fill", "Widget", bar with { Width = bar.Width * progress }, "", "", "Kenshi_ProgressBarFilledSkin", true,
                new Dictionary<string, string>(), Vector4.Zero, Pickable: false, Clip: rightClip));
        controls.Add(new("stat-next-level-frame", "Widget", bar, "", "", "Kenshi_ProgressBarSkin", true,
            new Dictionary<string, string>(), Vector4.Zero, Pickable: false, Clip: rightClip));
        var barText = new UiRect(bar.X + 10 * scale, bar.Y - 4 * scale, bar.Width - 20 * scale, row);
        Text("stat-next-level-label", barText, statHelp.TextAt(220), PlayerInterface.MainColour, 14, rightClip);
        Text("stat-next-level-percent", barText, ((int)(progress * 100)).ToString(CultureInfo.InvariantCulture) + "%", PlayerInterface.MainColour, 14, rightClip, "Right Top");
        Text("stat-description-heading", new(bar.X + 10 * scale, bar.Y + 12 * scale, bar.Width - 20 * scale, row),
            statHelp.TextAt(440), PlayerInterface.MainColour, 14, rightClip);
        string description = System.Text.RegularExpressions.Regex.Replace(help.Description, @"\{[12]\}", "");
        if (help.PhysicalEffect.Length > 0) description += "\n\n" + help.PhysicalEffect;
        float multiplier = lead?.SkillXpMultipliers.ElementAtOrDefault(selected) ?? 0;
        if (multiplier > 0 && Math.Abs(multiplier - 1) > 0.001f)
            description += "\n\n" + statHelp.TextAt(451) + " " + ((multiplier - 1) * 100).ToString("+0;-0;0", CultureInfo.InvariantCulture) + "%";
        Text("stat-description-body", new(bar.X + 10 * scale, bar.Y + 28 * scale, bar.Width - 20 * scale, Math.Max(0, right.Bounds.Height - 36 * scale)),
            description, statInk, 14, rightClip, wrap: true);
    }
}
