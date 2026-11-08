using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Game.Ui;

/// <summary>
/// Squad management (docs/game/ui-screens.md "Squads tab", the original's <c>SquadManagementScreen</c>): the HUD's squad tabs and the overview's SQUADS tab, a
/// <c>Kenshi_SquadBox</c> per squad with its name box, size, DELETE and portraits that drag between squads (Shift swaps) or onto DRAG HERE TO DISMISS. The changes are
/// <see cref="SquadCommand"/>s.
/// </summary>
sealed class SquadPanel(PlayerInterface player)
{
    const int SquadsTab = 4;
    /// <summary>A squad box's height in the squads list (<c>FUN_14049b7c0</c>: 0.28 of the list's height) and the scroll bar's width the boxes leave free.</summary>
    internal const float SquadBoxShare = 0.28f, SquadScrollBar = 17;
    /// <summary>A portrait cell of the squads screen (<c>Kenshi_SquadsPanel_PortraitCharacter</c>: 0.0416667 x 0.0740741 of the screen).</summary>
    const float SquadCellWidth = 0.0416667f, SquadCellHeight = 0.0740741f;

    /// <summary>The CONSTANTS limits (max squads, squad size, faction size); null before a world with game data.</summary>
    public Func<Data.Gameplay.GameConstants?>? SquadLimits { get; set; }

    internal MyGuiLayout? SquadBoxLayout;
    float _squadsScroll;
    /// <summary>A portrait pressed on the squads screen (dragged once the pointer moves) or a squad box pressed on its frame (dragged to reorder).</summary>
    (CharacterId Character, Vector2 From)? _portraitDrag;
    (int Squad, Vector2 From)? _squadBoxDrag;

    internal bool SquadsShown => player.FaithfulScreens.IsOpen("overview") && player.FaithfulScreens.GetTab("overview", "TabsMain") == SquadsTab;

    /// <summary>The HUD's squad tabs: one per squad with members, captioned with its name, the active one checked (<c>Kenshi_MainPanel</c> <c>SquadPanel</c>).</summary>
    internal void BindSquadTabs(UiBindings b)
    {
        var tabs = SquadTabSquads();
        // No player character at all (the --battle, --duel, a sandbox of NPCs): nothing to select, so no empty bar.
        if (player.PlayerCharacters().Count == 0) { b.Hidden.Add("SquadPanel"); return; }
        b.TabCaptions["SquadPanel"] = [.. tabs.Select(s => s.Name)];
        b.SelectedTabs["SquadPanel"] = Math.Max(0, tabs.FindIndex(s => s.Id == player.Session.CurrentSnapshot.ActiveSquad));
    }

    /// <summary>The squads the HUD has a tab for: those with members (an empty squad has none, even when active, <c>FUN_140416140</c>).</summary>
    List<SquadView> SquadTabSquads() => [.. player.Session.CurrentSnapshot.PlayerSquads.Where(s => s.Members.Count > 0)];

    /// <summary>A squad tab pressed: the squad bar shows that squad.</summary>
    internal bool SquadTabClick(UiControl c)
    {
        const string prefix = "mygui:tab:SquadPanel:";
        if (!c.Action.StartsWith(prefix, StringComparison.Ordinal) || !int.TryParse(c.Action.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int i)) return false;
        var tabs = SquadTabSquads();
        if (i < tabs.Count) player.Say(new ActiveSquadCommand(tabs[i].Id) { Tick = player.Session.World.Tick });
        return true;
    }

    /// <summary>The overview's SQUADS tab widgets: the faction's count against <c>max faction size</c>, the button and the dismiss label (both upper-cased by the original).</summary>
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

    internal void AddSquadsContent(List<UiControl> controls)
    {
        if (!SquadsShown || SquadBoxLayout is null || controls.FirstOrDefault(c => c.Name == "SquadsPanel") is not { } panel) return;
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
        float boxHeight = area.Height * SquadBoxShare, boxWidth = area.Width - SquadScrollBar * player.InventoryScale;
        _squadsScroll = Math.Clamp(_squadsScroll, 0, Math.Max(0, squads.Count * boxHeight - area.Height));
        var focus = player.FaithfulScreens.Focus is { Screen: "overview" } f ? f : default((string, string, int, int, int)?);
        bool dragging = _portraitDrag is { } pressed && Vector2.Distance(pressed.From, player.Mouse) >= PlayerInterface.DragThreshold;
        for (int i = 0; i < squads.Count; i++)
        {
            var squad = squads[i];
            var box = new UiRect(area.X, area.Y + i * boxHeight - _squadsScroll, boxWidth, boxHeight);
            if (box.Intersect(clip).Height <= 0) continue;
            string nameBox = SquadNameBox(squad.Id);
            bool typing = focus is { } fc && fc.Item2 == nameBox;
            // The box shows the squad's name, or what is being typed; a box not being typed in starts from the name when it is next pressed.
            if (!typing) player.FaithfulScreens.SetText("overview", nameBox, squad.Name);
            var b = new UiBindings { TemplateScale = player.InventoryScale };
            b.Text["SquadNameText"] = typing ? player.FaithfulScreens.GetText("overview", nameBox) : squad.Name;
            int count = squad.Members.Count;
            b.Text["SquadSizeText"] = $"{count}/{maxSize}";
            if (count >= maxSize) b.Properties["SquadSizeText"] = new Dictionary<string, string> { ["TextColour"] = PlayerInterface.BadColour };
            b.Actions["SquadRemoveButton"] = "squad-remove:" + squad.Id.ToString(CultureInfo.InvariantCulture);
            if (typing && focus is { } edit)
                foreach (var widget in SquadBoxLayout.Widgets.Where(w => w.Name == "SquadNameText" || w.Properties.GetValueOrDefault("CaptionOwner") == "SquadNameText"))
                    b.Properties[widget.Name] = new Dictionary<string, string>
                    {
                        ["Focused"] = "true",
                        ["CaretIndex"] = edit.Item3.ToString(CultureInfo.InvariantCulture),
                        ["SelectionStart"] = edit.Item4.ToString(CultureInfo.InvariantCulture),
                        ["SelectionLength"] = edit.Item5.ToString(CultureInfo.InvariantCulture),
                    };
            string squadId = squad.Id.ToString(CultureInfo.InvariantCulture);
            UiRect? portraitsArea = null;
            foreach (var c in MyGuiPresentation.Arrange(SquadBoxLayout, (int)MathF.Round(box.Width), (int)MathF.Round(box.Height), b, player.UiResources.Layers))
            {
                var bounds = c.Bounds with { X = c.Bounds.X + box.X, Y = c.Bounds.Y + box.Y };
                var own = c.Clip is { } inner ? (inner with { X = inner.X + box.X, Y = inner.Y + box.Y }).Intersect(clip) : clip;
                var properties = new Dictionary<string, string>(c.Properties, StringComparer.Ordinal) { ["SquadOf"] = squadId };
                // The box is dragged by its frame and panels: anything but the name box, DELETE and their skins' parts.
                if (c.Type is not ("EditBox" or "Button") && c.Action.Length == 0 && c.Properties.GetValueOrDefault("TemplateOwner") is not ("SquadNameText" or "SquadRemoveButton"))
                    properties["SquadBox"] = squadId;
                string name = c.Name == "SquadNameText" ? nameBox : c.Name.Length > 0 ? $"squad-{squadId}-{c.Name}" : c.Name;
                controls.Add(c with { Name = name, Bounds = bounds, Clip = own, Properties = properties });
                if (c.Name == "PortraitsPanel") portraitsArea = bounds;
            }
            if (portraitsArea is not { } cellsArea) continue;
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
        if (dragging && byId.TryGetValue(_portraitDrag!.Value.Character, out var held))
        {
            float size = Math.Min(player.Width * SquadCellWidth, player.Height * SquadCellHeight);
            AddSquadPortrait(controls, "squad-drag", new(player.Mouse.X - size / 2, player.Mouse.Y - size / 2, size, size), held, cells.GetValueOrDefault(held.Id, -1), null, null);
        }
    }

    /// <summary>The player's characters alive (what the faction's count and the dismiss rule count, <see cref="SquadManagement.FactionHeads"/>).</summary>
    int LivingPlayerCharacters() => player.Session.CurrentSnapshot.Characters.Count(c => c.IsPlayer && c.Body is not { Dead: true });

    static string SquadNameBox(int squad) => "squad-name-" + squad.ToString(CultureInfo.InvariantCulture);

    /// <summary>One portrait cell as <c>Kenshi_SquadsPanel_PortraitCharacter</c> lays it out: the grey back, the portrait, the name along the bottom, the frame (checked when selected).</summary>
    void AddSquadPortrait(List<UiControl> controls, string name, UiRect cell, CharacterSnapshot member, int atlasCell, UiRect? clip, Dictionary<string, string>? tags)
    {
        UiRect At(float x, float y, float w, float h) => new(cell.X + x * cell.Width, cell.Y + y * cell.Height, w * cell.Width, h * cell.Height);
        controls.Add(new($"{name}-back", "Widget", At(0, 0, 1, 1), "", "", "", true, new Dictionary<string, string>(), new Vector4(0.458f, 0.423f, 0.396f, 1), Pickable: false, Clip: clip));
        if (atlasCell >= 0)
        {
            controls.Add(new($"{name}-image", "ImageBox", At(0.0375f, 0.0375f, 0.9125f, 0.9125f), "", "", "", true,
                new Dictionary<string, string> { ["ImageTexture"] = PortraitBar.PortraitTexture, ["ImageFlipV"] = "true", ["ImageRect"] = player.PortraitBar.PortraitRect(atlasCell) },
                Vector4.Zero, Pickable: false, Clip: clip));
        }
        controls.Add(new($"{name}-name", "TextBox", At(0.05f, 0.05f, 0.9f, 0.8875f), member.Name, "", "Kenshi_TextboxStandardText", true,
            new Dictionary<string, string> { ["TextAlign"] = "HCenter Bottom", ["TextColour"] = "0.707602 0.707602 0.707602", ["TextShadow"] = "true", ["FontName"] = "Kenshi_StandardFont_MediumSmall" },
            Vector4.Zero, Pickable: false, Clip: clip));
        var frame = new Dictionary<string, string>(tags ?? [], StringComparer.Ordinal) { ["Checked"] = member.Selected ? "true" : "false" };
        controls.Add(new($"{name}-frame", "Button", At(-0.0375f, -0.025f, 1.0625f, 1.05f), "", "", "Kenshi_PortraitFrameSkin", true, frame, Vector4.Zero, Pickable: tags is not null, Clip: clip));
    }

    /// <summary>A press on the squads screen: a portrait (to select or drag) or a squad box's frame (to reorder). True: taken.</summary>
    internal bool SquadPointerDown(Vector2 p)
    {
        if (!SquadsShown || player.FaithfulScreens.HitTest(p) is not { Screen: "overview" } hit) return false;
        var c = hit.Control;
        if (c.Properties.TryGetValue("SquadPortrait", out var at) && PortraitAt(at) is { } member) { player.FaithfulScreens.Blur(); _portraitDrag = (member, p); return true; }
        if (c.Properties.TryGetValue("SquadBox", out var box) && int.TryParse(box, NumberStyles.None, CultureInfo.InvariantCulture, out int squad)) { player.FaithfulScreens.Blur(); _squadBoxDrag = (squad, p); return true; }
        return false;
    }

    /// <summary>The member a portrait's <c>squad:index</c> tag names.</summary>
    CharacterId? PortraitAt(string tag)
    {
        var parts = tag.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int squad) || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int index)) return null;
        var present = player.PlayerCharacters().ToHashSet();
        var members = player.Session.CurrentSnapshot.PlayerSquads.FirstOrDefault(s => s.Id == squad)?.Members.Where(present.Contains).ToList();
        return members is not null && index < members.Count ? members[index] : null;
    }

    bool OverDismiss(Vector2 p) => player.FaithfulScreens.HitTest(p) is { Screen: "overview" } hit && hit.Control.Name is "DismissCharPanel" or "lbSquadsDismiss";

    /// <summary>
    /// The button let go over the squads screen: a portrait that did not move selects its character; one dragged onto a portrait goes in at its place (Shift: they swap),
    /// onto a squad box at its end, onto DRAG HERE TO DISMISS asks to dismiss it. A squad box dropped on another takes its place in the order.
    /// </summary>
    internal void SquadPointerUp(Vector2 p, bool shift)
    {
        var portrait = _portraitDrag;
        var box = _squadBoxDrag;
        _portraitDrag = null;
        _squadBoxDrag = null;
        if (!SquadsShown) return;
        var target = player.FaithfulScreens.HitTest(p) is { Screen: "overview" } hit ? hit.Control : null;
        int? targetSquad = target is not null && target.Properties.TryGetValue("SquadOf", out var of) && int.TryParse(of, NumberStyles.None, CultureInfo.InvariantCulture, out int s) ? s : null;
        if (portrait is { } drag)
        {
            if (Vector2.Distance(drag.From, p) < PlayerInterface.DragThreshold) { player.Select([drag.Character], shift); return; }
            if (OverDismiss(p)) { AskDismiss(drag.Character); return; }
            if (targetSquad is not { } into) return;
            int index = -1;
            if (target!.Properties.TryGetValue("SquadPortrait", out var at) && at.Split(':') is [_, var place]) index = int.Parse(place, CultureInfo.InvariantCulture);
            if (index >= 0 && PortraitAt(target.Properties["SquadPortrait"]) == drag.Character) return;
            player.Say(new MoveToSquadCommand(drag.Character, into, index, shift) { Tick = player.Session.World.Tick });
        }
        else if (box is { } moved && targetSquad is { } onto && onto != moved.Squad && Vector2.Distance(moved.From, p) >= PlayerInterface.DragThreshold)
        {
            int index = player.Session.CurrentSnapshot.PlayerSquads.ToList().FindIndex(q => q.Id == onto);
            player.Say(new ReorderSquadCommand(moved.Squad, index) { Tick = player.Session.World.Tick });
        }
    }

    /// <summary>Dismiss: refused for the faction's last character, else asked first ("Dismiss character '{1}'?").</summary>
    void AskDismiss(CharacterId id)
    {
        if (LivingPlayerCharacters() <= 1) { player.Confirm("Dismiss character", "Cannot dismiss the only remaining character in the faction.", () => { }, "OK"); return; }
        string name = player.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id)?.Name ?? "";
        player.Confirm("Dismiss character", Loc.Tr("Dismiss character '{1}'?", name), () => player.Say(new DismissCommand(id) { Tick = player.Session.World.Tick }));
    }

    internal bool SquadsWheel(Vector2 p, float steps)
    {
        if (!SquadsShown || player.FaithfulScreens.HitTest(p) is not { Screen: "overview" } hit || !(hit.Control.Name == "SquadsPanel" || hit.Control.Properties.ContainsKey("SquadOf"))) return false;
        _squadsScroll -= steps * 44 * player.InventoryScale;   // clamped when the list is laid out
        return true;
    }

    internal void SquadsAction(MyGuiEvent e)
    {
        if (e.Action == "squad-new" || e.Widget == "NewSquadButton")
        {
            var view = player.Session.Camera.Current;
            player.Say(new NewSquadCommand(view.Target) { Tick = player.Session.World.Tick });
        }
        else if (e.Action.StartsWith("squad-remove:", StringComparison.Ordinal) && int.TryParse(e.Action.AsSpan(13), NumberStyles.None, CultureInfo.InvariantCulture, out int doomed))
            player.Say(new RemoveSquadCommand(doomed) { Tick = player.Session.World.Tick });
        else if (e.Action == "text" && e.Widget.StartsWith("squad-name-", StringComparison.Ordinal) && int.TryParse(e.Widget.AsSpan(11), NumberStyles.None, CultureInfo.InvariantCulture, out int named))
        {
            string text = e.Text.Length > SquadManagement.NameLength ? e.Text[..SquadManagement.NameLength] : e.Text;
            if (text != e.Text) player.FaithfulScreens.SetText("overview", e.Widget, text);
            player.Say(new RenameSquadCommand(named, text) { Tick = player.Session.World.Tick });
        }
    }

    // The overview's AI tab (docs/game/ai-tasks.md "Squad AI options"): a tick button per squad AI option in <c>AIOptions</c>, the hovered one's tooltip (a static tooltip, PlayerInterface.TooltipRuntime.AddHoverLabel) in <c>AIToolTip</c>
    // (captions and tooltips are the original's, <c>FUN_140005e11</c>; the row order follows their order in the exe, <b>Observed</b>).
    const int AiTab = 6;

    static readonly (SquadAi Option, string Caption, string Tip)[] AiOptionRows =
    [
        (SquadAi.Ditch, "Ditch items", "If they have nothing else to do, characters will automatically put any excess materials they are carrying into storage.  This can help to un-block the base AI when things grind to a halt from over-filled inventories."),
        (SquadAi.Sleep, "Sleep when injured", "Characters will automatically go to bed if they are injured and have nothing else to do"),
        (SquadAi.Sit, "Sit when idle", "Idle characters at home will sit down on chairs and benches, if you have any"),
        (SquadAi.Heal, "Heal Allies", "Characters with the medic job will also heal characters of allied factions"),
        (SquadAi.Help, "Help Allies", "Characters will help out characters from other allied factions if they are under attack"),
        (SquadAi.Rescue, "Rescue Allies", "Characters with the rescue job will also rescue allied characters from other factions"),
        (SquadAi.Share, "Share Food", "Characters in a squad will automatically share any of their food that is in a backpack (if in range)."),
        (SquadAi.Feed, "Feed Animals", "If food is running low, you can elect not to feed your animals."),
        (SquadAi.ShootFirst, "Shoot first, ask later", "Your turret gunners will shoot at any potentially dangerous but neutral targets, such as wild animals or bandits, without waiting for them to attack the base first."),
        // Meitou-only lines (not in the original's tab).
        (SquadAi.HealNeutrals, "Heal Neutrals", "Characters with the medic job will also heal downed characters of factions that are not hostile"),
        (SquadAi.HealAll, "Heal All", "Characters with the medic job will heal any downed character, enemies included"),
    ];

    internal void AddAiContent(List<UiControl> controls)
    {
        if (player.FaithfulScreens.GetTab("overview", "TabsMain") != AiTab || controls.FirstOrDefault(c => c.Name == "AIOptions") is not { } panel) return;
        var on = player.Session.CurrentSnapshot.SquadAi;
        // The original's panel is sized for its nine lines and the formation; lines shrink when the Meitou-only ones do not fit.
        float line = MathF.Min(MathF.Max(22, 30 * player.InventoryScale), panel.Bounds.Height / (AiOptionRows.Length + 1));
        for (int i = 0; i < AiOptionRows.Length; i++)
        {
            bool ticked = (on & AiOptionRows[i].Option) != 0;
            controls.Add(new($"ai-option-{i}", "Button", new(panel.Bounds.X + 6, panel.Bounds.Y + i * line, panel.Bounds.Width - 12, line - 4), Loc.Tr(AiOptionRows[i].Caption),
                "ai-option:" + i.ToString(CultureInfo.InvariantCulture), "Kenshi_TickButton1Skin", true,
                new Dictionary<string, string>(UiTooltips.TextProperties(AiOptionRows[i].Tip, "static:AIToolTip")) { ["Checked"] = ticked ? "true" : "false", ["StateSelected"] = ticked ? "true" : "false", ["FontName"] = "Kenshi_PaintedTextFont_Medium", ["TextAlign"] = "Center" },
                Vector4.Zero, Pickable: true, Clip: panel.Bounds));
        }
        AddFormationRow(controls, panel.Bounds, AiOptionRows.Length * line, line - 4);
    }

    /// <summary>The Follow Formation items in the original's order (<c>FUN_140494ce0</c>: Random 0, Military 2, Caravan 1).</summary>
    static readonly (SquadFormation Formation, string Caption)[] FormationItems = [(SquadFormation.Random, "Random"), (SquadFormation.Military, "Military"), (SquadFormation.Caravan, "Caravan")];
    const string FormationCombo = "FormationCombo";
    /// <summary>Where the formation combo box was put into the overview layout (fractions of <c>AIOptions</c>); it is put there again when the panel's size moves it.</summary>
    string? _formationComboPlace;

    /// <summary>
    /// The AI tab's last line (<c>FUN_140494ce0</c>, a <c>DataPanelLine_DropBox</c>): "Follow Formation" with a <c>Kenshi_ComboBox</c> right-aligned at 0.97 of the panel's width, its tooltip
    /// "Formation used when squad members are following a leader" in <c>AIToolTip</c>. The combo is a widget of the overview layout (the screen runs its popup); its width, 0.35 of the panel,
    /// is an engine choice (the original's size is <b>Unknown</b>).
    /// </summary>
    void AddFormationRow(List<UiControl> controls, UiRect panel, float top, float height)
    {
        if (panel.Height <= 0 || panel.Width <= 0) return;
        const float width = 0.35f;
        float y = top / panel.Height, h = height / panel.Height;
        controls.Add(new("ai-formation-label", "TextBox", new(panel.X + 6, panel.Y + top, panel.Width * (0.97f - width) - 6, height), Loc.Tr("Follow Formation"), "", "Kenshi_TextboxStandardText", true,
            new Dictionary<string, string>(UiTooltips.TextProperties("Formation used when squad members are following a leader", "static:AIToolTip")) { ["TextAlign"] = "Left VCenter" },
            Vector4.Zero, Pickable: true, Clip: panel));
        string place = string.Create(CultureInfo.InvariantCulture, $"{y:0.0000} {h:0.0000}");
        if (place == _formationComboPlace) return;
        _formationComboPlace = place;
        var layout = player.FaithfulScreens.Layout("overview");
        // The screen's layout is expanded already (its expansion stops at resolved templates), so the combo's template is expanded on its own first.
        var combo = MyGuiTemplates.Expand(new MyGuiLayout(layout.Version, [new MyGuiWidget("ComboBox", FormationCombo, "Kenshi_ComboBox", "", "Left Top", new(0.97f - width, y, width, h), true,
            new Dictionary<string, string> { ["ReadOnly"] = "true" }, new Dictionary<string, string>(), [])]), player.UiResources).Roots[0];
        MyGuiWidget Put(MyGuiWidget w) => w.Name == "AIOptions" ? w with { Children = [.. w.Children.Where(c => c.Name != FormationCombo), combo] } : w with { Children = [.. w.Children.Select(Put)] };
        player.FaithfulScreens.Replace("overview", layout with { Roots = [.. layout.Roots.Select(Put)] });
    }

    /// <summary>The combo's items and the player faction's formation (<see cref="SquadAiKeys.FormationOf"/>), once the combo is in the layout.</summary>
    internal void BindFormation(UiBindings b)
    {
        if (_formationComboPlace is null) return;
        int current = SquadAiKeys.FormationOf(player.Session.World, player.Session.World.Player.Faction, null);
        b.Lists[FormationCombo] = [.. FormationItems.Select(f => new UiListItem(((int)f.Formation).ToString(CultureInfo.InvariantCulture), Loc.Tr(f.Caption)))];
        b.SelectedItems[FormationCombo] = Array.FindIndex(FormationItems, f => (int)f.Formation == current);
    }

    /// <summary>
    /// Gives an order on a character, or with Shift held the job it becomes (the original's order dispatcher <c>FUN_1407fa650</c>; docs/game/ai-tasks.md "Perma-jobs"): First Aid,
    /// Splint Injuries, an attack (Attack Unprovoked too) and a loot become Medic (Robotics on a robot), Splint Rigging, Attacking enemies and Foraging animals. Bodyguard is always the
    /// job, Shift or not (the dispatcher adds 45 as a job at once).
    /// </summary>
    internal void OrderOrJob(Data.Gameplay.Ai.TaskType ordered, CharacterId target, Action order)
    {
        if ((player.SlowModifier || ordered == Data.Gameplay.Ai.TaskType.Bodyguard) && Simulation.Production.PermaJobs.JobOf(ordered) is not null)
            player.Say(new Simulation.Production.AddOrderJob(player.SelectedIds(), ordered, target) { Tick = player.Session.World.Tick });
        else order();
    }

    /// <summary>The order a character verb gives, by its caption, for <see cref="OrderOrJob"/>.</summary>
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

    /// <summary>A context-menu verb on <paramref name="target"/> that adds its job with Shift held (<see cref="OrderOrJob"/>); other verbs as they are.</summary>
    internal Action JobOrOrder(string caption, CharacterId target, Action give) =>
        JobVerbs.TryGetValue(caption, out var ordered) ? () => OrderOrJob(ordered, target, give) : give;

    internal void AiAction(MyGuiEvent e)
    {
        if (e.Widget == FormationCombo && e.Action == "select")
        {
            if (int.TryParse(e.Text, CultureInfo.InvariantCulture, out int formation) && Enum.IsDefined((SquadFormation)formation))
                player.Say(new SquadFormationCommand((SquadFormation)formation) { Tick = player.Session.World.Tick });
            return;
        }
        if (!e.Action.StartsWith("ai-option:", StringComparison.Ordinal) || !int.TryParse(e.Action.AsSpan(10), CultureInfo.InvariantCulture, out int i) || (uint)i >= (uint)AiOptionRows.Length) return;
        var option = AiOptionRows[i].Option;
        player.Say(new SquadAiCommand(option, (player.Session.CurrentSnapshot.SquadAi & option) == 0) { Tick = player.Session.World.Tick });
    }
}