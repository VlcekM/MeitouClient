using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation.Player;

namespace Meitou.Game.Ui;

/// <summary>
/// The HUD's orders panel (docs/game/ui-screens.md section 4): the order toggles, the run speed arrows and the jobs list.
/// </summary>
sealed class OrdersPanel(PlayerInterface player)
{
    #region State

    /// <summary>
    /// The orders panel's toggles by the words in their widget names (the layout names only <c>OrdersChaseButton</c>, <c>OrdersMedicButton</c> and
    /// <c>OrdersRescueButton</c> for certain, docs/game/ui-screens.md section 4; the rest are matched by the button's word, so a differently spelt name still
    /// binds). Prospect is its own action (ProspectingPanel).
    /// </summary>
    static readonly (string Word, CharacterOrders Order)[] OrderWords =
    [
        ("Block", CharacterOrders.Block), ("Hold", CharacterOrders.Hold), ("Chase", CharacterOrders.Jobs), ("Passive", CharacterOrders.Passive),
        ("Ranged", CharacterOrders.Ranged), ("Taunt", CharacterOrders.Taunt), ("Sneak", CharacterOrders.Sneak), ("Stealth", CharacterOrders.Sneak),
        ("Jobs", CharacterOrders.Jobs),
    ];

    /// <summary>
    /// The speed icon per mode: the <c>Kenshi_CharacterSpeedSet</c> image names, in mode order.
    /// </summary>
    static readonly string[] RunSpeedIcons = ["Walk", "Jog", "Run", "Group"];

    /// <summary>
    /// The jobs list's row layout (<c>Kenshi_OrdersPanelOrder.layout</c>: a <c>Kenshi_ConnectBarSkin</c> bar, <c>OrderText</c> and <c>RemoveButton</c>).
    /// </summary>
    internal MyGuiLayout? OrdersRowLayout;

    /// <summary>
    /// The first Obedient Slave job of the jobs list (the tutorial highlight PanelOrdersJobObedience), -1: none.
    /// </summary>
    internal int ObedienceJob = -1;

    /// <summary>
    /// The jobs rows drawn at half alpha: the jobs switch is off and the job does not run without it (<see cref="BindJobs"/>).
    /// </summary>
    readonly HashSet<string> _dimmedJobs = [];

    /// <summary>
    /// The job row (its list index) pressed and being dragged to a new place in the list; null: none.
    /// </summary>
    internal int? JobDrag;

    #endregion

    #region Order toggles

    /// <summary>
    /// Binds the order toggles (checked while the lead selected character has the order) and the speed arrows of the HUD.
    /// </summary>
    internal void BindOrders(UiBindings b, Simulation.CharacterSnapshot? lead)
    {
        foreach (var w in player.FaithfulHud.Widgets)
        {
            if (w.Type != "Button" || !w.Name.StartsWith("Orders", StringComparison.Ordinal)) continue;

            foreach (var (word, order) in OrderWords)
            {
                if (!w.Name.Contains(word, StringComparison.Ordinal)) continue;

                b.Actions[w.Name] = $"meitou:order.{order}";
                if (lead is null) b.Disabled.Add(w.Name);
                if (lead is not null && (lead.Orders & order) != 0)
                    b.Properties[w.Name] = new Dictionary<string, string> { ["Checked"] = "true" };
                break;
            }
        }

        b.Actions["SpeedArrowLeft"] = "meitou:runspeed.-1";
        b.Actions["SpeedArrowRight"] = "meitou:runspeed.1";

        // The icon shows the lead's mode (docs/game/ui-screens.md); with nobody selected it keeps the layout's.
        if (lead is null) return;

        int icon = Math.Min((int)lead.RunSpeed, RunSpeedIcons.Length - 1);
        b.Properties["SpeedPanel"] = new Dictionary<string, string> { ["ImageName"] = RunSpeedIcons[icon] };
    }

    #endregion

    #region Jobs list

    /// <summary>
    /// The jobs list (<c>JobsPanel</c>, docs/game/ui-screens.md section 4): the lead's jobs, one <c>Kenshi_OrdersPanelOrder</c> row each, int(24 x
    /// height / 1080) tall (<see cref="AddJobRowParts"/>), numbered from 1, with an X that removes it from that character; with none, the layout's
    /// hint. Rows are dragged to reorder.
    /// </summary>
    internal void BindJobs(UiBindings b, Simulation.CharacterSnapshot? lead)
    {
        ObedienceJob = -1;
        _dimmedJobs.Clear();
        var jobs = new List<UiListItem>();
        bool switchedOn = lead is not null && (lead.Orders & CharacterOrders.Jobs) != 0;
        for (int i = 0; i < (lead?.Jobs.Count ?? 0); i++)
        {
            var job = lead!.Jobs[i];
            if (job.Type == Data.Gameplay.Ai.TaskType.SlaveObedience && ObedienceJob < 0) ObedienceJob = i;

            string id = "job:" + i.ToString(CultureInfo.InvariantCulture);
            if (!switchedOn && !Simulation.Production.PermaJobs.IgnoresSwitch(job.Type)) _dimmedJobs.Add(id);

            jobs.Add(new(id, (i + 1).ToString(CultureInfo.InvariantCulture) + ": " + JobRowText(job)));
        }

        b.Lists["JobsPanel"] = jobs;
        b.Properties["JobsPanel"] = new Dictionary<string, string>
        {
            ["LineSkin"] = "Kenshi_ConnectBarSkin",
            ["LineHeight"] = ((int)(24 * player.Height / 1080f)).ToString(CultureInfo.InvariantCulture),
        };
        b.ScrollOffsets["JobsPanel"] = player.ScrollBars.InventoryScroll.GetValueOrDefault("JobsPanel");

        // The brackets are the panel's, not the catalog's (its keys have none: "No jobs assigned", "To add jobs, hold shift key when giving an order").
        if (jobs.Count > 0)
        {
            b.Hidden.Add("JobsPanelInfo");
            return;
        }

        b.Text["JobsPanelInfo"] = "[" + Loc.Tr("No jobs assigned") + "]\n["
            + Loc.Tr("To add jobs, hold shift key when giving an order") + "]";
    }

    internal bool JobsWheel(Vector2 p, float steps)
    {
        if (!player.ScrollBars.InventoryViews.TryGetValue("JobsPanel", out var view) || !view.View.Contains(p.X, p.Y)) return false;

        float line = 24 * player.Height / 1080f;
        float scroll = player.ScrollBars.InventoryScroll.GetValueOrDefault("JobsPanel");
        player.ScrollBars.InventoryScroll["JobsPanel"] = Math.Clamp(scroll - steps * line, 0, view.Maximum);
        return true;
    }

    internal void AddJobScrollBar(List<UiControl> controls)
    {
        int panelIndex = controls.FindIndex(c => c.Name == "JobsPanel");
        if (panelIndex < 0) return;

        var panel = controls[panelIndex];
        float line = MyGuiPresentation.Num(panel.Properties, "ListLine", 24 * player.Height / 1080f);
        float maximum = MyGuiPresentation.Num(panel.Properties, "ScrollMax");
        if (maximum <= 0)
        {
            player.ScrollBars.InventoryViews.Remove("JobsPanel");
            player.ScrollBars.InventoryTracks.Remove("JobsPanel");
            return;
        }

        float scroll = Math.Clamp(player.ScrollBars.InventoryScroll.GetValueOrDefault("JobsPanel"), 0, maximum);
        player.ScrollBars.InventoryScroll["JobsPanel"] = scroll;
        float barWidth = 17 * player.InventoryScale;
        float gap = 3 * player.InventoryScale;
        var view = panel.Bounds with { Width = Math.Max(0, panel.Bounds.Width - barWidth - gap) };
        player.ScrollBars.InventoryViews["JobsPanel"] = (view, maximum);
        NarrowJobRows(controls, view, barWidth, gap);

        var track = new UiRect(panel.Bounds.X + panel.Bounds.Width - barWidth, panel.Bounds.Y, barWidth, panel.Bounds.Height);
        player.ScrollBars.AddScrollBar("JobsPanel", controls, track, view.Height, view.Height + maximum, scroll, maximum, panel.Clip, "hud-jobs");
    }

    /// <summary>
    /// Narrows the jobs rows and their parts by the scroll bar's width (<paramref name="barWidth"/> and <paramref name="gap"/>) and clips them to the list's view.
    /// </summary>
    static void NarrowJobRows(List<UiControl> controls, UiRect view, float barWidth, float gap)
    {
        for (int i = 0; i < controls.Count; i++)
        {
            bool ownedByList = controls[i].Properties.GetValueOrDefault("ListOwner") == "JobsPanel";
            if (!ownedByList && !controls[i].Name.StartsWith("JobsPanel@row", StringComparison.Ordinal)) continue;

            float width = Math.Max(0, controls[i].Bounds.Width - barWidth - gap);
            controls[i] = controls[i] with { Bounds = controls[i].Bounds with { Width = width }, Clip = controls[i].Clip?.Intersect(view) ?? view };
        }
    }

    /// <summary>
    /// A jobs row: the type's text and the building it targets. The engineer works every site, not the one it was ordered on, so its row is plain
    /// "Engineer" (<b>Observed</b> in the original by players, #348/#353).
    /// </summary>
    string JobRowText(Simulation.Production.PermaJob job)
    {
        string text = Loc.Tr(Simulation.Production.PermaJobs.Text(job.Type) ?? job.Type.ToString());
        if (job.Type == Data.Gameplay.Ai.TaskType.JobBuilder) return text;

        if (job.Site is { } id && player.Session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == id) is { } site)
            return text + ": " + player.Build.SiteName(site);

        if (job.Site is { } dummyId && player.Session.CurrentSnapshot.Dummies.FirstOrDefault(d => d.Id == dummyId) is { } dummy)
            return text + ": " + (player.Build.Services?.ItemName(dummy.Record) ?? dummy.Record);

        return text;
    }

    /// <summary>
    /// Each jobs row's text and X over its bar (the original's cell layout <c>Kenshi_OrdersPanelOrder</c>; the X's tutorial: "remove a job at any
    /// time by pressing the X button"). The row keeps the list's picking (selection, drag); only the X takes a click of its own.
    /// </summary>
    internal void AddJobRowParts(List<UiControl> controls)
    {
        if (OrdersRowLayout is null) return;

        for (int i = controls.Count - 1; i >= 0; i--)
        {
            var row = controls[i];
            if (row.Properties.GetValueOrDefault("ListOwner") != "JobsPanel") continue;

            bool dimmed = _dimmedJobs.Contains(row.Argument);
            controls[i] = row with { Caption = "", Properties = Dimmed(row.Properties, dimmed) };
            controls.InsertRange(i + 1, JobRowParts(row, dimmed));
        }
    }

    /// <summary>
    /// The row layout's text and X at <paramref name="row"/>: the layout is 300 x 20 px and sized to the row, so the text keeps its 10 px left and 35
    /// px right margins and the X (Right VStretch) stays 30 px wide at the right end, a pixel short of the row's height. The text is the Main colour
    /// (the original's
    /// <c>#afa68b</c> prefix).
    /// </summary>
    List<UiControl> JobRowParts(UiControl row, bool dimmed)
    {
        var box = row.Bounds;
        var b = new UiBindings();
        var root = OrdersRowLayout!.Roots.FirstOrDefault();
        if (root is not null) b.Positions[root.Name] = new(0, 0, box.Width, box.Height);
        b.Positions["OrderText"] = new(10, 0, Math.Max(0, box.Width - 45), box.Height);
        b.Positions["RemoveButton"] = new(box.Width - 30, 0, 30, Math.Max(0, box.Height - 1));
        b.Text["OrderText"] = row.Caption;
        b.Properties["OrderText"] = new Dictionary<string, string> { ["TextColour"] = PlayerInterface.MainColour };

        var parts = new List<UiControl>();
        foreach (var c in MyGuiPresentation.Arrange(OrdersRowLayout, (int)box.Width, (int)box.Height, b, player.UiResources.Layers))
        {
            if (c.Name == root?.Name) continue;   // the row draws the bar

            var clip = c.Clip is { } inner
                ? (inner with { X = inner.X + box.X, Y = inner.Y + box.Y }).Intersect(row.Clip ?? box)
                : row.Clip;
            bool remove = c.Name == "RemoveButton";
            parts.Add(c with
            {
                Name = $"{row.Name}-{c.Name}",
                Bounds = c.Bounds with { X = c.Bounds.X + box.X, Y = c.Bounds.Y + box.Y },
                Clip = clip,
                Action = remove ? "meitou:job-remove" : c.Action,
                Argument = row.Argument,
                Pickable = remove,
                Properties = Dimmed(c.Properties, dimmed),
            });
        }

        return parts;
    }

    /// <summary>
    /// A jobs row's properties at half alpha when <paramref name="dimmed"/> (the original's cell alpha 0.5).
    /// </summary>
    static IReadOnlyDictionary<string, string> Dimmed(IReadOnlyDictionary<string, string> properties, bool dimmed) =>
        dimmed ? new Dictionary<string, string>(properties, StringComparer.Ordinal) { ["Alpha"] = "0.5" } : properties;

    #endregion

    #region Drag and remove

    /// <summary>
    /// The job-list index of the jobs row under a point (not its X), or null.
    /// </summary>
    static int? JobRowAt(IReadOnlyList<UiControl> controls, Vector2 p)
    {
        if (controls.Any(c => c.Action == "meitou:job-remove" && c.Bounds.Contains(p.X, p.Y))) return null;

        var row = controls.FirstOrDefault(c => c.Properties.GetValueOrDefault("ListOwner") == "JobsPanel" && c.Bounds.Contains(p.X, p.Y));
        if (row is null || !row.Argument.StartsWith("job:", StringComparison.Ordinal)) return null;

        return int.TryParse(row.Argument.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out int i) ? i : null;
    }

    /// <summary>
    /// A press on a job row starts dragging it (true: taken).
    /// </summary>
    internal bool JobDragStart(IReadOnlyList<UiControl> controls, Vector2 p)
    {
        JobDrag = JobRowAt(controls, p);
        return JobDrag is not null;
    }

    /// <summary>
    /// The button let go: a dragged job dropped on another row of the list moves there.
    /// </summary>
    internal void JobDragEnd(Vector2 p)
    {
        if (JobDrag is not { } from) return;

        JobDrag = null;
        if (JobRowAt(player.ArrangeUi(), p) is not { } to || to == from) return;
        if ((player.Selected().FirstOrDefault() ?? player.Lead()) is not { IsPlayer: true } lead) return;

        player.Say(new Simulation.Production.MoveJob(lead.Id, from, to) { Tick = player.Session.World.Tick });
    }

    /// <summary>
    /// Takes a job (the jobs list's row id) off the lead whose list shows it.
    /// </summary>
    internal void RemoveJob(string job)
    {
        if ((player.Selected().FirstOrDefault() ?? player.Lead()) is not { IsPlayer: true } lead) return;
        if (!job.StartsWith("job:", StringComparison.Ordinal)) return;
        if (!int.TryParse(job.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out int index)) return;

        player.Say(new Simulation.Production.RemoveJob(lead.Id, index) { Tick = player.Session.World.Tick });
    }

    #endregion
}