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
    /// <c>OrdersRescueButton</c> for certain, docs/game/ui-screens.md section 4; the rest are matched by the button's word, so a differently spelt
    /// name still binds). Prospect is its own action (ProspectingPanel).
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
    /// The jobs list's row layout (<c>Kenshi_OrdersPanelOrder.layout</c>: a <c>Kenshi_ConnectBarSkin</c> bar, <c>OrderText</c> and
    /// <c>RemoveButton</c>).
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

    /// <summary>
    /// Where the dragged job row was pressed, and that point less the row's corner (the drawn copy keeps it under the pointer).
    /// </summary>
    Vector2 _jobDragPress, _jobDragOffset;

    /// <summary>
    /// The code mods' buttons beside the jobs list (docs/modding.md "Character panel lines and job panel buttons").
    /// </summary>
    internal JobModButtons ModButtons { get; } = new(player);

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
    /// The jobs rows' parts by what they are made from (<see cref="JobRowParts"/> depends on nothing else), kept from one arrangement to the next:
    /// the parts only change when the jobs, the window or the language do.
    /// </summary>
    Dictionary<JobRowKey, List<UiControl>> _jobRowParts = [];

    /// <summary>
    /// The parts the arrangement under way uses; they become <see cref="_jobRowParts"/> once it is done, so rows gone are dropped.
    /// </summary>
    Dictionary<JobRowKey, List<UiControl>> _jobRowPartsNext = [];

    /// <summary>
    /// What a jobs row's parts are made from: the row's name, place, clip, caption and argument, whether it is dimmed, the row layout and the
    /// layers it is arranged with, and the language its captions are translated into.
    /// </summary>
    readonly record struct JobRowKey(string Name, UiRect Bounds, UiRect? Clip, string Caption, string Argument, bool Dimmed, MyGuiLayout Layout,
        IReadOnlyList<string> Layers, GameLanguage Language);

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
            ["LineHeight"] = ((int)(24 * player.UiScale)).ToString(CultureInfo.InvariantCulture),
        };
        b.ScrollOffsets["JobsPanel"] = player.ScrollBars.InventoryScroll.GetValueOrDefault("JobsPanel");
        ModButtons.Bind(b, lead);

        // The brackets are the panel's, not the catalog's (its keys have none: "No jobs assigned", "To add jobs, hold shift key when giving an
        // order").
        if (jobs.Count > 0)
        {
            b.Hidden.Add("JobsPanelInfo");
            return;
        }

        b.Text["JobsPanelInfo"] = "[" + Loc.Tr("No jobs assigned") + "]\n["
            + Loc.Tr("To add jobs, hold shift key when giving an order") + "]";
    }

    /// <summary>
    /// The wheel over the jobs list scrolls it a line a step; false elsewhere.
    /// </summary>
    internal bool JobsWheel(Vector2 p, float steps)
    {
        if (!player.ScrollBars.InventoryViews.TryGetValue("JobsPanel", out var view) || !view.View.Contains(p.X, p.Y)) return false;

        float line = 24 * player.UiScale;
        float scroll = player.ScrollBars.InventoryScroll.GetValueOrDefault("JobsPanel");
        player.ScrollBars.InventoryScroll["JobsPanel"] = Math.Clamp(scroll - steps * line, 0, view.Maximum);
        return true;
    }

    /// <summary>
    /// Adds the jobs list's scroll bar when its lines do not fit.
    /// </summary>
    internal void AddJobScrollBar(List<UiControl> controls)
    {
        int panelIndex = controls.FindIndex(c => c.Name == "JobsPanel");
        if (panelIndex < 0) return;

        var panel = controls[panelIndex];
        float line = MyGuiPresentation.Num(panel.Properties, "ListLine", 24 * player.UiScale);
        float maximum = MyGuiPresentation.Num(panel.Properties, "ScrollMax");
        if (maximum <= 0)
        {
            player.ScrollBars.InventoryViews.Remove("JobsPanel");
            player.ScrollBars.InventoryTracks.Remove("JobsPanel");
            return;
        }

        float scroll = Math.Clamp(player.ScrollBars.InventoryScroll.GetValueOrDefault("JobsPanel"), 0, maximum);
        player.ScrollBars.InventoryScroll["JobsPanel"] = scroll;
        float barWidth = 17 * player.UiScale;
        float gap = 3 * player.UiScale;
        var view = panel.Bounds with { Width = Math.Max(0, panel.Bounds.Width - barWidth - gap) };
        player.ScrollBars.InventoryViews["JobsPanel"] = (view, maximum);
        NarrowJobRows(controls, view, barWidth, gap);

        var track = new UiRect(panel.Bounds.X + panel.Bounds.Width - barWidth, panel.Bounds.Y, barWidth, panel.Bounds.Height);
        player.ScrollBars.AddScrollBar("JobsPanel", controls, track, view.Height, view.Height + maximum, scroll, maximum, panel.Clip, "hud-jobs");
    }

    /// <summary>
    /// Narrows the jobs rows and their parts by the scroll bar's width (<paramref name="barWidth"/> and <paramref name="gap"/>) and clips them to the
    /// list's view.
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
    /// The name a mod registered for its own job type (docs/modding.md "Mod jobs"); null for any other type.
    /// </summary>
    string? ModJobName(Data.Gameplay.Ai.TaskType type) => player.Session.World.Systems
        .OfType<Simulation.Player.PlayerSystem>()
        .FirstOrDefault()?.Extensions?.JobTypes.GetValueOrDefault(type);

    /// <summary>
    /// A jobs row: the type's text (a mod's job: the name it registered) and the building, town machine, rock, training dummy or character it
    /// targets. The engineer works every site, not the one it was ordered on, so its row is plain "Engineer" (<b>Observed</b> in the original by players, #348/#353).
    /// </summary>
    string JobRowText(Simulation.Production.PermaJob job)
    {
        string name = Simulation.Production.PermaJobs.Text(job.Type) ?? ModJobName(job.Type) ?? job.Type.ToString();
        string text = Loc.Tr(name);
        if (job.Type == Data.Gameplay.Ai.TaskType.JobBuilder) return text;

        if (job.Site is { } id && player.Session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == id) is { } site)
            return text + ": " + player.Build.ShownSiteName(site);

        // A rock is no registry building: its id names its BUILDING (an Iron Ore deposit, a Stone outcrop).
        if (job.Site is { } rockId && Simulation.Production.ProductionSystem.NaturalRecord(rockId) is { } rock)
            return text + ": " + (player.Build.Services?.ShownName(rock) ?? rock);

        // A town building's machine (a well, a farm: townwork:<key>) is no registry building either: its machine names its BUILDING (#1039).
        if (player.Production.Machine(job.Site) is { IsContainer: false } machine)
            return text + ": " + (player.Build.Services?.ShownName(machine.Record) ?? machine.Record);

        if (job.Site is { } dummyId && player.Session.CurrentSnapshot.Dummies.FirstOrDefault(d => d.Id == dummyId) is { } dummy)
            return text + ": " + (player.Build.Services?.ShownName(dummy.Record) ?? dummy.Record);

        // A job per target (TaskData +8: Bodyguard, Staying close) names its character (docs/game/ui-screens.md, jobs list); Obedient Slave does not.
        if (!job.Character.IsNone && Simulation.Production.PermaJobs.PerTarget(job.Type)
            && player.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == job.Character) is { Name.Length: > 0 } target)
        {
            return text + ": " + player.ShownName(target);
        }

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
            controls.InsertRange(i + 1, KeptJobRowParts(row, dimmed));
        }

        (_jobRowParts, _jobRowPartsNext) = (_jobRowPartsNext, _jobRowParts);
        _jobRowPartsNext.Clear();
    }

    /// <summary>
    /// <see cref="JobRowParts"/> of <paramref name="row"/>, reused while the row is made from the same things.
    /// </summary>
    List<UiControl> KeptJobRowParts(UiControl row, bool dimmed)
    {
        var key = new JobRowKey(row.Name, row.Bounds, row.Clip, row.Caption, row.Argument, dimmed, OrdersRowLayout!, player.UiResources.Layers,
            Loc.Language);
        if (!_jobRowParts.TryGetValue(key, out var parts)) parts = JobRowParts(row, dimmed);

        _jobRowPartsNext[key] = parts;
        return parts;
    }

    /// <summary>
    /// The row layout's text and X at <paramref name="row"/>: the layout is 300 x 20 px and sized to the row, so the text keeps its 10 px left and 35
    /// px right margins and the X (Right VStretch) stays 30 px wide at the right end, a pixel short of the row's height. The text is the Main colour
    /// (the original's <c>#afa68b</c> prefix).
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
    static int? JobRowAt(IReadOnlyList<UiControl> controls, Vector2 p) =>
        JobRowControlAt(controls, p) is { } row
        && int.TryParse(row.Argument.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out int i) ? i : null;

    /// <summary>
    /// The jobs row under a point (not its X), or null.
    /// </summary>
    static UiControl? JobRowControlAt(IReadOnlyList<UiControl> controls, Vector2 p)
    {
        if (controls.Any(c => c.Action == "meitou:job-remove" && c.Bounds.Contains(p.X, p.Y))) return null;

        var row = controls.FirstOrDefault(c => c.Properties.GetValueOrDefault("ListOwner") == "JobsPanel" && c.Bounds.Contains(p.X, p.Y));
        return row is not null && row.Argument.StartsWith("job:", StringComparison.Ordinal) ? row : null;
    }

    /// <summary>
    /// A press on a job row starts dragging it (true: taken).
    /// </summary>
    internal bool JobDragStart(IReadOnlyList<UiControl> controls, Vector2 p)
    {
        JobDrag = JobRowAt(controls, p);
        if (JobDrag is null) return false;

        var row = JobRowControlAt(controls, p)!;
        _jobDragPress = p;
        _jobDragOffset = p - new Vector2(row.Bounds.X, row.Bounds.Y);
        return true;
    }

    /// <summary>
    /// The dragged jobs row drawn again under the pointer, over everything, from the first move after the press; the row stays in the list. The
    /// original's jobs list is a MyGUI ItemBox, whose drag draws the item's cell again on its drag layer at the pointer, keeping where it was taken
    /// (MyGUI <c>ItemBox::updateDropItems</c>), and its cell draws the same dragged or not (<c>FUN_140723ee0</c> sets only the text and the
    /// alpha; docs/game/ui-screens.md "Orders panel").
    /// </summary>
    internal void AddJobDrag(List<UiControl> controls)
    {
        if (JobDrag is not { } index || player.Mouse == _jobDragPress) return;

        string argument = "job:" + index.ToString(CultureInfo.InvariantCulture);
        int at = controls.FindIndex(c => c.Properties.GetValueOrDefault("ListOwner") == "JobsPanel" && c.Argument == argument);
        if (at < 0) return;

        var row = controls[at];
        var corner = player.Mouse - _jobDragOffset;
        var shift = corner - new Vector2(row.Bounds.X, row.Bounds.Y);
        var moved = row.Bounds with { X = corner.X, Y = corner.Y };
        string parts = row.Name + "-";
        controls.Add(DraggedJobPart(row, "JobDrag", shift, moved));
        for (int i = at + 1; i < controls.Count && controls[i].Name.StartsWith(parts, StringComparison.Ordinal); i++)
            controls.Add(DraggedJobPart(controls[i], "JobDrag-" + controls[i].Name[parts.Length..], shift, moved));
    }

    /// <summary>
    /// A part of the dragged row's copy: moved by <paramref name="shift"/>, clipped to the copy's box <paramref name="row"/>, neither picked nor
    /// acting, and no longer one of the list's rows.
    /// </summary>
    static UiControl DraggedJobPart(UiControl part, string name, Vector2 shift, UiRect row)
    {
        var bounds = part.Bounds with { X = part.Bounds.X + shift.X, Y = part.Bounds.Y + shift.Y };
        var properties = new Dictionary<string, string>(part.Properties, StringComparer.Ordinal);
        properties.Remove("ListOwner");
        return part with { Name = name, Bounds = bounds, Clip = bounds.Intersect(row), Action = "", Pickable = false, Properties = properties };
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