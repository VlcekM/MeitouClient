using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Player;

namespace Meitou.Game.Ui;

/// <summary>
/// Squad management (docs/game/ui-screens.md "Squads tab", the original's <c>SquadManagementScreen</c>): the HUD's squad tabs and the overview's
/// SQUADS tab, a <c>Kenshi_SquadBox</c> per squad with its name box, size, DELETE and portraits that drag between squads (Shift swaps) or onto DRAG
/// HERE TO DISMISS. The changes are <see cref="SquadCommand"/>s.
/// </summary>
sealed class SquadPanel(PlayerInterface player)
{
    /// <summary>
    /// The overview's AI tab (its own state: the formation combo's place).
    /// </summary>
    internal readonly SquadAiTab Ai = new(player);

    #region Squads tab

    /// <summary>
    /// The Squads tab's index in the overview's tabs.
    /// </summary>
    const int SquadsTab = 4;

    /// <summary>
    /// A squad box's height in the squads list (<c>FUN_14049b7c0</c>: 0.28 of the list's height) and the scroll bar's width the boxes leave free.
    /// </summary>
    internal const float SquadBoxShare = 0.28f, SquadScrollBar = 17;

    /// <summary>
    /// A portrait cell of the squads screen (<c>Kenshi_SquadsPanel_PortraitCharacter</c>: 0.0416667 x 0.0740741 of the screen).
    /// </summary>
    const float SquadCellWidth = 0.0416667f, SquadCellHeight = 0.0740741f;

    /// <summary>
    /// The squad box layout; null before the UI is read.
    /// </summary>
    internal MyGuiLayout? SquadBoxLayout;

    /// <summary>
    /// How far the squads list is scrolled, pixels (clamped when it is laid out).
    /// </summary>
    float _squadsScroll;

    /// <summary>
    /// A portrait pressed on the squads screen (dragged once the pointer moves): the character and where the press was.
    /// </summary>
    (CharacterId Character, Vector2 From)? _portraitDrag;

    /// <summary>
    /// A squad box pressed on its frame: the squad and where the press was.
    /// </summary>
    (int Squad, Vector2 From)? _squadBoxDrag;

    /// <summary>
    /// The CONSTANTS limits (max squads, squad size, faction size); null before a world with game data.
    /// </summary>
    public Func<Data.Gameplay.GameConstants?>? SquadLimits { get; set; }

    /// <summary>
    /// The overview is open on its Squads tab.
    /// </summary>
    internal bool SquadsShown => player.FaithfulScreens.IsOpen("overview") && player.FaithfulScreens.GetTab("overview", "TabsMain") == SquadsTab;

    /// <summary>
    /// The HUD's squad tabs: one per squad with members, captioned with its name, the active one checked (<c>Kenshi_MainPanel</c> <c>SquadPanel</c>).
    /// </summary>
    internal void BindSquadTabs(UiBindings b)
    {
        var tabs = SquadTabSquads();

        // No player character at all (the --battle, --duel, a sandbox of NPCs): nothing to select, so no empty bar.
        if (player.PlayerCharacters().Count == 0)
        {
            b.Hidden.Add("SquadPanel");
            return;
        }

        b.TabCaptions["SquadPanel"] = [.. tabs.Select(s => s.Name)];
        b.SelectedTabs["SquadPanel"] = Math.Max(0, tabs.FindIndex(s => s.Id == player.Session.CurrentSnapshot.ActiveSquad));
    }

    /// <summary>
    /// The squads the HUD has a tab for: those with members (an empty squad has none, even when active, <c>FUN_140416140</c>).
    /// </summary>
    List<SquadView> SquadTabSquads() => [.. player.Session.CurrentSnapshot.PlayerSquads.Where(s => s.Members.Count > 0)];

    /// <summary>
    /// A squad tab pressed: the squad bar shows that squad.
    /// </summary>
    internal bool SquadTabClick(UiControl c)
    {
        const string prefix = "mygui:tab:SquadPanel:";

        if (!c.Action.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(c.Action.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int i))
        {
            return false;
        }

        var tabs = SquadTabSquads();
        if (i < tabs.Count) player.Say(new ActiveSquadCommand(tabs[i].Id) { Tick = player.Session.World.Tick });

        return true;
    }

    /// <summary>
    /// The overview's SQUADS tab widgets: the faction's count against <c>max faction size</c>, the button and the dismiss label (both upper-cased by
    /// the original).
    /// </summary>
    internal void BindSquadsTab(UiBindings b)
    {
        var limits = SquadLimits?.Invoke();
        b.Text["FactionSizeText"] = $"{LivingPlayerCharacters()}/{limits?.MaxFactionSize ?? 30}";
        b.Text["NewSquadButton"] = "ADD SQUAD";
        b.Text["lbSquadsDismiss"] = "DISMISS";
        b.Actions["NewSquadButton"] = "squad-new";
        b.Disabled.Remove("NewSquadButton");
        if (_portraitDrag is { } drag && Vector2.Distance(drag.From, player.Mouse) >= PlayerInterface.DragThreshold && OverDismiss(player.Mouse))
            b.Properties["DismissCharPanel"] = new Dictionary<string, string> { ["State"] = "highlighted" };
    }

    /// <summary>
    /// The SQUADS tab's list: a squad box per squad (scrolled, clipped to the panel) with its members' portraits, and the portrait being dragged.
    /// </summary>
    internal void AddSquadsContent(List<UiControl> controls)
    {
        if (!SquadsShown || SquadBoxLayout is null || controls.FirstOrDefault(c => c.Name == "SquadsPanel") is not { } panel)
            return;

        var snapshot = player.Session.CurrentSnapshot;
        var squads = snapshot.PlayerSquads;
        var limits = SquadLimits?.Invoke();
        int maxSize = limits?.MaxSquadSize ?? 20;
        var cells = new Dictionary<CharacterId, int>();
        var portraits = player.PortraitBar.PortraitMembers();
        for (int i = 0; i < portraits.Count; i++) cells[portraits[i].Id] = i;

        var byId = snapshot.Characters.Where(c => c.IsPlayer).ToDictionary(c => c.Id);
        var area = panel.Bounds;
        var clip = panel.Clip is { } outer ? area.Intersect(outer) : area;
        float boxHeight = area.Height * SquadBoxShare, boxWidth = area.Width - SquadScrollBar * player.UiScale;
        _squadsScroll = Math.Clamp(_squadsScroll, 0, Math.Max(0, squads.Count * boxHeight - area.Height));
        var focus = player.FaithfulScreens.Focus is { Screen: "overview" } f ? f : default((string, string, int, int, int)?);
        bool dragging = _portraitDrag is { } pressed && Vector2.Distance(pressed.From, player.Mouse) >= PlayerInterface.DragThreshold;

        for (int i = 0; i < squads.Count; i++)
        {
            var squad = squads[i];
            var box = new UiRect(area.X, area.Y + i * boxHeight - _squadsScroll, boxWidth, boxHeight);
            if (box.Intersect(clip).Height <= 0) continue;

            string nameBox = SquadNameBox(squad.Id);
            var b = BindSquadBox(squad, nameBox, maxSize, focus);
            string squadId = squad.Id.ToString(CultureInfo.InvariantCulture);
            if (AddSquadBox(controls, b, box, clip, squadId, nameBox) is not { } cellsArea) continue;

            AddSquadMembers(controls, squad, cellsArea, clip, squadId, byId, cells, dragging);
        }

        if (dragging && byId.TryGetValue(_portraitDrag!.Value.Character, out var held))
        {
            float size = Math.Min(player.Width * SquadCellWidth, player.Height * SquadCellHeight);
            AddSquadPortrait(controls, "squad-drag", new(player.Mouse.X - size / 2, player.Mouse.Y - size / 2, size, size), held,
                cells.GetValueOrDefault(held.Id, -1), null, null);
        }
    }

    /// <summary>
    /// A squad box's bindings: its name (or what is being typed, with the caret), its size against the limit (red when full) and its DELETE.
    /// </summary>
    UiBindings BindSquadBox(SquadView squad, string nameBox, int maxSize, (string, string, int, int, int)? focus)
    {
        bool typing = focus is { } fc && fc.Item2 == nameBox;

        // The box shows the squad's name, or what is being typed; a box not being typed in starts from the name when it is next pressed.
        if (!typing) player.FaithfulScreens.SetText("overview", nameBox, squad.Name);

        var b = new UiBindings { TemplateScale = player.UiScale };
        b.Text["SquadNameText"] = typing ? player.FaithfulScreens.GetText("overview", nameBox) : squad.Name;
        int count = squad.Members.Count;
        b.Text["SquadSizeText"] = $"{count}/{maxSize}";
        if (count >= maxSize)
            b.Properties["SquadSizeText"] = new Dictionary<string, string> { ["TextColour"] = PlayerInterface.BadColour };

        b.Actions["SquadRemoveButton"] = "squad-remove:" + squad.Id.ToString(CultureInfo.InvariantCulture);

        if (typing && focus is { } edit)
        {
            foreach (var widget in SquadBoxLayout!.Widgets.Where(IsSquadNameWidget))
            {
                b.Properties[widget.Name] = new Dictionary<string, string>
                {
                    ["Focused"] = "true",
                    ["CaretIndex"] = edit.Item3.ToString(CultureInfo.InvariantCulture),
                    ["SelectionStart"] = edit.Item4.ToString(CultureInfo.InvariantCulture),
                    ["SelectionLength"] = edit.Item5.ToString(CultureInfo.InvariantCulture),
                };
            }
        }

        return b;
    }

    /// <summary>
    /// The squad box's name box or a part of its skin.
    /// </summary>
    static bool IsSquadNameWidget(MyGuiWidget w) => w.Name == "SquadNameText" || w.Properties.GetValueOrDefault("CaptionOwner") == "SquadNameText";

    /// <summary>
    /// The squad box's controls, laid out at <paramref name="box"/> and tagged with the squad; returns its portraits panel (null: none).
    /// </summary>
    UiRect? AddSquadBox(List<UiControl> controls, UiBindings b, UiRect box, UiRect clip, string squadId, string nameBox)
    {
        UiRect? portraitsArea = null;

        foreach (var c in MyGuiPresentation.Arrange(SquadBoxLayout!, (int)MathF.Round(box.Width), (int)MathF.Round(box.Height), b,
            player.UiResources.Layers))
        {
            var bounds = c.Bounds with { X = c.Bounds.X + box.X, Y = c.Bounds.Y + box.Y };
            var own = c.Clip is { } inner ? (inner with { X = inner.X + box.X, Y = inner.Y + box.Y }).Intersect(clip) : clip;
            var properties = new Dictionary<string, string>(c.Properties, StringComparer.Ordinal) { ["SquadOf"] = squadId };

            // The box is dragged by its frame and panels: anything but the name box, DELETE and their skins' parts.
            if (c.Type is not ("EditBox" or "Button") && c.Action.Length == 0
                && c.Properties.GetValueOrDefault("TemplateOwner") is not ("SquadNameText" or "SquadRemoveButton"))
            {
                properties["SquadBox"] = squadId;
            }

            string name = c.Name == "SquadNameText" ? nameBox : c.Name.Length > 0 ? $"squad-{squadId}-{c.Name}" : c.Name;
            controls.Add(c with { Name = name, Bounds = bounds, Clip = own, Properties = properties });
            if (c.Name == "PortraitsPanel") portraitsArea = bounds;
        }

        return portraitsArea;
    }

    /// <summary>
    /// The squad's present members' portraits in a row of its portraits panel, shrunk to fit; the one being dragged follows the pointer instead.
    /// </summary>
    void AddSquadMembers(List<UiControl> controls, SquadView squad, UiRect cellsArea, UiRect clip, string squadId,
        Dictionary<CharacterId, CharacterSnapshot> byId, Dictionary<CharacterId, int> cells, bool dragging)
    {
        var members = squad.Members.Where(byId.ContainsKey).ToList();
        float size = Math.Min(Math.Min(player.Width * SquadCellWidth, player.Height * SquadCellHeight), cellsArea.Height - 4);
        if (members.Count * size > cellsArea.Width) size = cellsArea.Width / members.Count;

        var cellClip = cellsArea.Intersect(clip);

        for (int k = 0; k < members.Count; k++)
        {
            if (dragging && _portraitDrag!.Value.Character == members[k]) continue;   // it follows the pointer

            var cell = new UiRect(cellsArea.X + 2 + k * size, cellsArea.Y + (cellsArea.Height - size) / 2, size, size);
            AddSquadPortrait(controls, $"squad-{squadId}-cell-{k}", cell, byId[members[k]], cells.GetValueOrDefault(members[k], -1), cellClip,
                new Dictionary<string, string> { ["SquadOf"] = squadId, ["SquadPortrait"] = $"{squadId}:{k}" });
        }
    }

    /// <summary>
    /// The player's characters alive (what the faction's count and the dismiss rule count, <see cref="SquadManagement.FactionHeads"/>).
    /// </summary>
    int LivingPlayerCharacters() => player.Session.CurrentSnapshot.Characters.Count(c => c.IsPlayer && c.Body is not { Dead: true });

    /// <summary>
    /// The name edit box of squad <paramref name="squad"/>.
    /// </summary>
    static string SquadNameBox(int squad) => "squad-name-" + squad.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// One portrait cell as <c>Kenshi_SquadsPanel_PortraitCharacter</c> lays it out: the grey back, the portrait, the name along the bottom, the
    /// frame (checked when selected).
    /// </summary>
    void AddSquadPortrait(List<UiControl> controls, string name, UiRect cell, CharacterSnapshot member, int atlasCell, UiRect? clip,
        Dictionary<string, string>? tags)
    {
        UiRect At(float x, float y, float w, float h) => new(cell.X + x * cell.Width, cell.Y + y * cell.Height, w * cell.Width, h * cell.Height);

        controls.Add(new($"{name}-back", "Widget", At(0, 0, 1, 1), "", "", "", true, new Dictionary<string, string>(),
            new Vector4(0.458f, 0.423f, 0.396f, 1), Pickable: false, Clip: clip));

        if (atlasCell >= 0)
        {
            controls.Add(new($"{name}-image", "ImageBox", At(0.0375f, 0.0375f, 0.9125f, 0.9125f), "", "", "", true,
                new Dictionary<string, string>
                {
                    ["ImageTexture"] = PortraitBar.PortraitTexture,
                    ["ImageFlipV"] = "true",
                    ["ImageRect"] = player.PortraitBar.PortraitRect(atlasCell),
                },
                Vector4.Zero, Pickable: false, Clip: clip));
        }

        controls.Add(new($"{name}-name", "TextBox", At(0.05f, 0.05f, 0.9f, 0.8875f), member.Name, "", "Kenshi_TextboxStandardText", true,
            new Dictionary<string, string>
            {
                ["TextAlign"] = "HCenter Bottom",
                ["TextColour"] = "0.707602 0.707602 0.707602",
                ["TextShadow"] = "true",
                ["FontName"] = "Kenshi_StandardFont_MediumSmall",
            },
            Vector4.Zero, Pickable: false, Clip: clip));
        var frame = new Dictionary<string, string>(tags ?? [], StringComparer.Ordinal) { ["Checked"] = member.Selected ? "true" : "false" };
        controls.Add(new($"{name}-frame", "Button", At(-0.0375f, -0.025f, 1.0625f, 1.05f), "", "", "Kenshi_PortraitFrameSkin", true, frame,
            Vector4.Zero, Pickable: tags is not null, Clip: clip));
    }

    /// <summary>
    /// A press on the squads screen: a portrait (to select or drag) or a squad box's frame (to reorder). True: taken.
    /// </summary>
    internal bool SquadPointerDown(Vector2 p)
    {
        if (!SquadsShown || player.FaithfulScreens.HitTest(p) is not { Screen: "overview" } hit) return false;

        var c = hit.Control;

        if (c.Properties.TryGetValue("SquadPortrait", out var at) && PortraitAt(at) is { } member)
        {
            player.FaithfulScreens.Blur();
            _portraitDrag = (member, p);
            return true;
        }

        if (c.Properties.TryGetValue("SquadBox", out var box) && int.TryParse(box, NumberStyles.None, CultureInfo.InvariantCulture, out int squad))
        {
            player.FaithfulScreens.Blur();
            _squadBoxDrag = (squad, p);
            return true;
        }

        return false;
    }

    /// <summary>
    /// The member a portrait's <c>squad:index</c> tag names.
    /// </summary>
    CharacterId? PortraitAt(string tag)
    {
        var parts = tag.Split(':');

        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int squad)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int index))
        {
            return null;
        }

        var present = player.PlayerCharacters().ToHashSet();
        var members = player.Session.CurrentSnapshot.PlayerSquads.FirstOrDefault(s => s.Id == squad)?.Members.Where(present.Contains).ToList();
        return members is not null && index < members.Count ? members[index] : null;
    }

    /// <summary>
    /// Whether <paramref name="p"/> is over DRAG HERE TO DISMISS in the top screen. Its transparent ItemBox and mouse-disabled caption cannot
    /// be identified by the ordinary widget hit test, so use the arranged drop panel's bounds and clip.
    /// </summary>
    bool OverDismiss(Vector2 p)
    {
        if (player.FaithfulScreens.HitTest(p) is not { Screen: "overview" }) return false;

        var controls = player.FaithfulScreens.LastFrame.FirstOrDefault(f => f.Id == "overview")?.Controls;
        return controls?.FirstOrDefault(c => c.Name == "DismissCharPanel") is { } panel
            && panel.Bounds.Contains(p.X, p.Y) && (panel.Clip is null || panel.Clip.Value.Contains(p.X, p.Y));
    }

    /// <summary>
    /// The button let go over the squads screen: a portrait that did not move selects its character; one dragged onto a portrait goes in at its place
    /// (Shift: they swap), onto a squad box at its end, onto DRAG HERE TO DISMISS asks to dismiss it. A squad box dropped on another takes its place
    /// in the order.
    /// </summary>
    internal void SquadPointerUp(Vector2 p, bool shift)
    {
        var portrait = _portraitDrag;
        var box = _squadBoxDrag;
        _portraitDrag = null;
        _squadBoxDrag = null;
        if (!SquadsShown) return;

        var target = player.FaithfulScreens.HitTest(p) is { Screen: "overview" } hit ? hit.Control : null;
        int? targetSquad = SquadOf(target);

        if (portrait is { } drag)
        {
            if (Vector2.Distance(drag.From, p) < PlayerInterface.DragThreshold)
            {
                player.Select([drag.Character], shift);
                return;
            }

            if (OverDismiss(p))
            {
                AskDismiss(drag.Character);
                return;
            }

            if (targetSquad is not { } into) return;

            int index = -1;
            if (target!.Properties.TryGetValue("SquadPortrait", out var at) && at.Split(':') is [_, var place])
                index = int.Parse(place, CultureInfo.InvariantCulture);
            if (index >= 0 && PortraitAt(target.Properties["SquadPortrait"]) == drag.Character) return;

            player.Say(new MoveToSquadCommand(drag.Character, into, index, shift) { Tick = player.Session.World.Tick });
        }
        else if (box is { } moved && targetSquad is { } onto && onto != moved.Squad && Vector2.Distance(moved.From, p) >= PlayerInterface.DragThreshold)
        {
            int index = player.Session.CurrentSnapshot.PlayerSquads.ToList().FindIndex(q => q.Id == onto);
            player.Say(new ReorderSquadCommand(moved.Squad, index) { Tick = player.Session.World.Tick });
        }
    }

    /// <summary>
    /// The squad a control is tagged with (<c>SquadOf</c>); null for none.
    /// </summary>
    static int? SquadOf(UiControl? target)
    {
        if (target is null || !target.Properties.TryGetValue("SquadOf", out var of)) return null;

        return int.TryParse(of, NumberStyles.None, CultureInfo.InvariantCulture, out int s) ? s : null;
    }

    /// <summary>
    /// Dismiss: refused for the faction's last character, else asked first ("Dismiss character '{1}'?").
    /// </summary>
    void AskDismiss(CharacterId id)
    {
        if (LivingPlayerCharacters() <= 1)
        {
            player.Confirm("Dismiss character", "Cannot dismiss the only remaining character in the faction.", () => { }, "OK");
            return;
        }

        string name = player.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id)?.Name ?? "";
        player.Confirm("Dismiss character", Loc.Tr("Dismiss character '{1}'?", name),
            () => player.Say(new DismissCommand(id) { Tick = player.Session.World.Tick }));
    }

    /// <summary>
    /// The wheel over the squads list scrolls it; false elsewhere or when the tab is not shown.
    /// </summary>
    internal bool SquadsWheel(Vector2 p, float steps)
    {
        if (!SquadsShown || player.FaithfulScreens.HitTest(p) is not { Screen: "overview" } hit
            || !(hit.Control.Name == "SquadsPanel" || hit.Control.Properties.ContainsKey("SquadOf")))
        {
            return false;
        }

        _squadsScroll -= steps * 44 * player.UiScale;   // clamped when the list is laid out
        return true;
    }

    /// <summary>
    /// A Squads tab event: a new squad, a squad removed, renamed, or its other buttons.
    /// </summary>
    internal void SquadsAction(MyGuiEvent e)
    {
        if (e.Action == "squad-new" || e.Widget == "NewSquadButton")
        {
            var view = player.Session.Camera.Current;
            player.Say(new NewSquadCommand(view.Target) { Tick = player.Session.World.Tick });
        }
        else if (e.Action.StartsWith("squad-remove:", StringComparison.Ordinal)
            && int.TryParse(e.Action.AsSpan(13), NumberStyles.None, CultureInfo.InvariantCulture, out int doomed))
        {
            player.Say(new RemoveSquadCommand(doomed) { Tick = player.Session.World.Tick });
        }
        else if (e.Action == "text" && e.Widget.StartsWith("squad-name-", StringComparison.Ordinal)
            && int.TryParse(e.Widget.AsSpan(11), NumberStyles.None, CultureInfo.InvariantCulture, out int named))
        {
            string text = e.Text.Length > SquadManagement.NameLength ? e.Text[..SquadManagement.NameLength] : e.Text;
            if (text != e.Text) player.FaithfulScreens.SetText("overview", e.Widget, text);

            player.Say(new RenameSquadCommand(named, text) { Tick = player.Session.World.Tick });
        }
    }

    #endregion

    #region Orders and jobs

    /// <summary>
    /// The order a character verb gives, by its caption, for <see cref="OrderOrJob"/>.
    /// </summary>
    static readonly Dictionary<string, Data.Gameplay.Ai.TaskType> JobVerbs = new()
    {
        ["Attack Target"] = Data.Gameplay.Ai.TaskType.MeleeAttack,
        ["Attack Unprovoked"] = Data.Gameplay.Ai.TaskType.UnprovokedFocusedMeleeAttack,
        ["First Aid"] = Data.Gameplay.Ai.TaskType.FirstAidOrder,
        ["Repairs"] = Data.Gameplay.Ai.TaskType.FirstAidRobot,
        ["Splint Injuries"] = Data.Gameplay.Ai.TaskType.SplintOrder,
        ["Loot"] = Data.Gameplay.Ai.TaskType.LootTarget,
        ["Follow"] = Data.Gameplay.Ai.TaskType.StayCloseToTarget,
        ["Bodyguard"] = Data.Gameplay.Ai.TaskType.Bodyguard,
    };

    /// <summary>
    /// Gives an order on a character, or with Shift held the job it becomes (the original's order dispatcher <c>FUN_1407fa650</c>;
    /// docs/game/ai-tasks.md "Perma-jobs"): First Aid, Splint Injuries, an attack (Attack Unprovoked too) and a loot become Medic (Robotics on a
    /// robot), Splint Rigging, Attacking enemies and Foraging animals. Bodyguard is always the job, Shift or not (the dispatcher adds 45 as a job at
    /// once).
    /// </summary>
    internal void OrderOrJob(Data.Gameplay.Ai.TaskType ordered, CharacterId target, Action order)
    {
        if ((player.SlowModifier || ordered == Data.Gameplay.Ai.TaskType.Bodyguard) && Simulation.Production.PermaJobs.JobOf(ordered) is not null)
            player.Say(new Simulation.Production.AddOrderJob(player.SelectedIds(), ordered, target) { Tick = player.Session.World.Tick });
        else order();
    }

    /// <summary>
    /// A context-menu verb on <paramref name="target"/> that adds its job with Shift held (<see cref="OrderOrJob"/>); other verbs as they are.
    /// </summary>
    internal Action JobOrOrder(string caption, CharacterId target, Action give) =>
        JobVerbs.TryGetValue(caption, out var ordered) ? () => OrderOrJob(ordered, target, give) : give;

    #endregion
}
