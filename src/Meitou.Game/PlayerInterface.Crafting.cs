using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation.Production;
using Meitou.Game.Ui;

namespace Meitou.Game;

/// <summary>
/// The overview's Craft tab (docs/game/ui-screens.md "Selected building", docs/game/buildings-production.md "Crafting"): STATIONS lists the player's crafting benches, ITEMS what the
/// selected one can craft (a click queues one; locked or too advanced rows are red and do nothing), QUEUE its queue (a click takes one off), Repeat Queue its repeat toggle, DESCRIPTION
/// the hovered or last clicked craft's time and inputs. The original's row widgets and colours are <b>Unknown</b>; Meitou draws plain text rows like the research tab.
/// </summary>
sealed partial class PlayerInterface
{
    const int CraftTab = 3;
    string? craftStation, craftFocus;

    List<MachineSnapshot> CraftStations() => [.. session.CurrentSnapshot.Machines.Where(m => m.Craft is not null)];

    string StationName(MachineSnapshot m) => session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == m.Site) is { } site ? SiteName(site) : m.Record;

    /// <summary>Opens the Craft tab on a bench (the building window's QUEUE... button).</summary>
    void OpenCraftTab(string site)
    {
        craftStation = site;
        faithfulScreens.Open("overview");
        faithfulScreens.SetTab("overview", "TabsMain", CraftTab);
    }

    void BindCraftTab(UiBindings b)
    {
        var stations = CraftStations();
        if (craftStation is null || stations.All(s => s.Site != craftStation)) craftStation = stations.FirstOrDefault()?.Site;
        b.Lists["CraftingStationsList"] = [.. stations.Select(s => new UiListItem(s.Site, StationName(s)))];
        b.SelectedItems["CraftingStationsList"] = stations.FindIndex(s => s.Site == craftStation);
        bool repeat = stations.FirstOrDefault(s => s.Site == craftStation)?.Craft?.Repeat == true;
        b.Actions["QueueRepeat"] = "QueueRepeat";
        b.Properties["QueueRepeat"] = new Dictionary<string, string> { ["Checked"] = repeat ? "true" : "false", ["StateSelected"] = repeat ? "true" : "false" };
        IndentLabel(b, "lbCraftingDescription");
    }

    void CraftAction(MyGuiEvent e)
    {
        if (e.Widget == "CraftingStationsList" && e.Action == "select")
        {
            var stations = CraftStations();
            craftStation = stations.FirstOrDefault(s => s.Site == e.Text)?.Site ?? stations.FirstOrDefault(s => StationName(s) == e.Text)?.Site ?? craftStation;
        }
        else if (e.Widget == "QueueRepeat" && craftStation is not null && Machine(craftStation)?.Craft is { } craft)
            Say(new RepeatCrafts(craftStation, !craft.Repeat) { Tick = session.World.Tick });
        else if (e.Action.StartsWith("craft-queue:", StringComparison.Ordinal) && craftStation is not null)
        {
            craftFocus = e.Action[12..];
            Say(new QueueCraft(craftStation, craftFocus) { Tick = session.World.Tick });
        }
        else if (e.Action.StartsWith("craft-focus:", StringComparison.Ordinal)) craftFocus = e.Action[12..];
        else if (e.Action.StartsWith("craft-cancel:", StringComparison.Ordinal) && craftStation is not null)
            Say(new CancelCraft(craftStation, int.Parse(e.Action.AsSpan(13), CultureInfo.InvariantCulture)) { Tick = session.World.Tick });
    }

    /// <summary>The item rows, the queue rows and the description, drawn into the Craft tab's panels.</summary>
    void AddCraftContent(List<UiControl> controls)
    {
        if (faithfulScreens.GetTab("overview", "TabsMain") != CraftTab || craftStation is null || Machine(craftStation) is not { Craft: { } craft }) return;
        var options = session.CurrentSnapshot.Crafts.GetValueOrDefault(craftStation) ?? [];
        var face = new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["TextAlign"] = "Left VCenter" };
        float line = MathF.Max(18, 24 * InventoryScale);
        var ink = new Vector4(0.86f, 0.82f, 0.71f, 1);
        if (controls.FirstOrDefault(c => c.Name == "QueueItemsPanel") is { } panel)
        {
            var rows = options.OrderBy(o => o.Kind).ThenBy(o => o.Level).ThenBy(o => o.Name, StringComparer.Ordinal).ToList();
            for (int i = 0; i < rows.Count; i++)
            {
                var o = rows[i];
                controls.Add(new($"craft-row-{i}", "Button", new(panel.Bounds.X + 6, panel.Bounds.Y + i * line, panel.Bounds.Width - 12, line), o.Name,
                    o.Unlocked ? "craft-queue:" + o.Key : "craft-focus:" + o.Key, "", true, face, Vector4.Zero,
                    TextColour: o.Unlocked ? ink : new Vector4(0.75f, 0.35f, 0.3f, 1), HoverColour: new(0, 1, 0, 1), Pickable: true, Clip: panel.Bounds));
            }
        }
        if (controls.FirstOrDefault(c => c.Name == "CraftQueue") is { } queue)
            for (int i = 0; i < craft.Queue.Count; i++)
            {
                var (_, name, progress, _) = craft.Queue[i];
                controls.Add(new($"craft-queue-{i}", "Button", new(queue.Bounds.X + 6, queue.Bounds.Y + i * line, queue.Bounds.Width - 12, line),
                    $"{name}  {(int)(progress * 100)}%", "craft-cancel:" + i.ToString(CultureInfo.InvariantCulture), "", true, face, Vector4.Zero,
                    TextColour: i == 0 ? new(0.66f, 0.72f, 0.45f, 1) : ink, HoverColour: new(1, 0.4f, 0.3f, 1), Pickable: true, Clip: queue.Bounds));
            }
        // A queued craft under the pointer has its own description (the CraftQueue box's tooltip, FUN_1402be650 -> FUN_1402bcf60).
        if (controls.LastOrDefault(c => c.Name.StartsWith("craft-queue-", StringComparison.Ordinal) && c.Bounds.Contains(mouse.X, mouse.Y)) is { } over
            && int.TryParse(over.Name.AsSpan(12), NumberStyles.None, CultureInfo.InvariantCulture, out int hoveredIndex) && hoveredIndex < craft.Queue.Count)
        {
            QueueDescription(controls, craftStation, craft, hoveredIndex, options);
            return;
        }
        string? hovered = controls.LastOrDefault(c => c.Name.StartsWith("craft-row-", StringComparison.Ordinal) && c.Bounds.Contains(mouse.X, mouse.Y))?.Action;
        string? focus = hovered is { } h ? h[(h.IndexOf(':') + 1)..] : craftFocus;
        if (focus is null || options.FirstOrDefault(o => o.Key == focus) is not { } option) return;
        var lines = new List<HudLine>
        {
            new(option.Name, ""),
            new("Time:", $"{option.Hours.ToString("0.#", CultureInfo.InvariantCulture)} h", MainColour),
        };
        if (option.Level > 0) lines.Add(new("Level:", option.Level.ToString(CultureInfo.InvariantCulture), MainColour));
        foreach (var (item, per) in option.Inputs) lines.Add(new(item, per.ToString("0.##", CultureInfo.InvariantCulture), MainColour));
        if (!option.Unlocked) lines.Add(new("Needs research", "", BadColour));
        DatapanelLines(controls, "QueueDescriptionPanel1", lines);
    }

    /// <summary>
    /// The description of a queued craft (<c>FUN_1402bcf60</c>, into <c>QueueDescriptionPanel2</c>; <b>Verified</b>): <c>State:</c> "In production..." and, by the bench's status, a second
    /// <c>State:</c> row in Bad (0 Out of resources, 1 Output full, 3 Upgrade required); <c>ETA:</c> the hours left, truncated to a tenth, "&lt;" before 0; and, only while the bench has had a smith,
    /// <c>-Current Smith:</c> with his level of the bench's stat and the multiplier's penalty in Bad, then by the item's class <c>-Material Costs:</c> (weapons), <c>-Predicted Result:</c>
    /// (the quality's name for armour and crossbows, in the colour of its grade; the model's for weapons, Bad when it is not the queued one) and <c>-Critical Success Chance:</c>.
    /// The ingredients go to Panel 1 (<c>FUN_1402b6b40</c>; Meitou lists the recipe as the item rows do). Not shown: "Not assigned to a craft bench" (the queue entry's bench hand is never
    /// null here) and the ETA's bar.
    /// </summary>
    void QueueDescription(List<UiControl> controls, string site, CraftView craft, int index, IReadOnlyList<CraftOptionView> options)
    {
        var (key, name, progress, hours) = craft.Queue[index];
        var ingredients = new List<HudLine> { new(name, "") };
        if (options.FirstOrDefault(o => o.Key == key) is { } option)
            foreach (var (item, per) in option.Inputs) ingredients.Add(new(item, per.ToString("0.##", CultureInfo.InvariantCulture), MainColour));
        DatapanelLines(controls, "QueueDescriptionPanel1", ingredients);
        var rows = new List<HudLine> { new(Loc.Tr("State:"), Loc.Tr("In production...")) };
        string? status = Machine(site)?.Status switch { 0 => "Out of resources", 1 => "Output full", 3 => "Upgrade required", _ => null };
        if (status is not null) rows.Add(new(Loc.Tr("State:"), Loc.Tr(status), ValueColour: BadColour));
        // FUN_1402bcf60: (int)(hours x (1 - progress) x 10) tenths, "{1,num} hr" for a whole 1 and "hrs" else, "<" in front at 0 (the entry's +0x30 divisor is Unknown: 0 unless the bench worked this tick).
        float left = (int)(hours * (1 - progress) * 10) * 0.1f;
        rows.Add(new(Loc.Tr("ETA:"), (left == 0 ? "<" : "") + Loc.Plural("{1,num} hr", "{1,num} hrs", (int)left, left)));
        if (session.CurrentSnapshot.Smiths.GetValueOrDefault(site) is { } smith)
        {
            string penalty = smith.Penalty == 0 ? "" : Tag(BadColour) + " (" + Loc.Format("{1,num=fix,p=0}", -smith.Penalty) + ")" + Tag(MyGuiColours.InkText);
            rows.Add(new(Loc.Tr("-Current Smith:"), smith.Name.Replace("#", "##", StringComparison.Ordinal) + Loc.Tr(" (skill: {1,num}{2})", smith.Skill, penalty)));
            if (index < smith.Entries.Count)
            {
                var guess = smith.Entries[index];
                if (guess.Model is not null)
                {
                    rows.Add(new(Loc.Tr("-Material Costs:"), Loc.Format("{1,num}", guess.MaterialCost)));
                    rows.Add(new(Loc.Tr("-Predicted Result:"), Tag(guess.ModelChanged ? BadColour : MainColour) + guess.Model.Replace("#", "##", StringComparison.Ordinal)));
                }
                else if (guess.Quality >= 0)
                {
                    int grade = Meitou.Simulation.Combat.ArmourTooltip.GradeOf(guess.Quality);
                    rows.Add(new(Loc.Tr("-Predicted Result:"), Tag(TipColour(GradeTone(grade), false)) + Loc.Tr(Meitou.Simulation.Combat.ArmourTooltip.GradeNames[grade])));
                }
                if (guess.Critical >= 0) rows.Add(new(Loc.Tr("-Critical Success Chance:"), Loc.Tr("{1,num}%", guess.Critical)));
            }
        }
        DatapanelLines(controls, "QueueDescriptionPanel2", rows);
    }
}
