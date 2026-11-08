using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;
using Meitou.Game.Ui;
using Meitou.Simulation.Factions;

namespace Meitou.Game;

/// <summary>
/// Squad management (docs/game/ui-screens.md "Squads tab", the original's <c>SquadManagementScreen</c>): the HUD's squad tabs and the overview's SQUADS tab, a
/// <c>Kenshi_SquadBox</c> per squad with its name box, size, DELETE and portraits that drag between squads (Shift swaps) or onto DRAG HERE TO DISMISS. The changes are
/// <see cref="SquadCommand"/>s.
/// </summary>
sealed partial class PlayerInterface
{
    const int SquadsTab = 4;
    /// <summary>A squad box's height in the squads list (<c>FUN_14049b7c0</c>: 0.28 of the list's height) and the scroll bar's width the boxes leave free.</summary>
    const float SquadBoxShare = 0.28f, SquadScrollBar = 17;
    /// <summary>A portrait cell of the squads screen (<c>Kenshi_SquadsPanel_PortraitCharacter</c>: 0.0416667 x 0.0740741 of the screen).</summary>
    const float SquadCellWidth = 0.0416667f, SquadCellHeight = 0.0740741f;

    /// <summary>The CONSTANTS limits (max squads, squad size, faction size); null before a world with game data.</summary>
    public Func<Meitou.Data.Gameplay.GameConstants?>? SquadLimits { get; set; }

    MyGuiLayout? squadBoxLayout;
    float squadsScroll;
    /// <summary>A portrait pressed on the squads screen (dragged once the pointer moves) or a squad box pressed on its frame (dragged to reorder).</summary>
    (CharacterId Character, Vector2 From)? portraitDrag;
    (int Squad, Vector2 From)? squadBoxDrag;

    bool SquadsShown => faithfulScreens.IsOpen("overview") && faithfulScreens.GetTab("overview", "TabsMain") == SquadsTab;

    /// <summary>The HUD's squad tabs: one per squad with members, captioned with its name, the active one checked (<c>Kenshi_MainPanel</c> <c>SquadPanel</c>).</summary>
    void BindSquadTabs(UiBindings b)
    {
        var tabs = SquadTabSquads();
        // No player character at all (the --battle, --duel, a sandbox of NPCs): nothing to select, so no empty bar.
        if (PlayerCharacters().Count == 0) { b.Hidden.Add("SquadPanel"); return; }
        b.TabCaptions["SquadPanel"] = [.. tabs.Select(s => s.Name)];
        b.SelectedTabs["SquadPanel"] = Math.Max(0, tabs.FindIndex(s => s.Id == session.CurrentSnapshot.ActiveSquad));
    }

    /// <summary>The squads the HUD has a tab for: those with members (an empty squad has none, even when active, <c>FUN_140416140</c>).</summary>
    List<SquadView> SquadTabSquads() => [.. session.CurrentSnapshot.PlayerSquads.Where(s => s.Members.Count > 0)];

    /// <summary>A squad tab pressed: the squad bar shows that squad.</summary>
    bool SquadTabClick(UiControl c)
    {
        const string prefix = "mygui:tab:SquadPanel:";
        if (!c.Action.StartsWith(prefix, StringComparison.Ordinal) || !int.TryParse(c.Action.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int i)) return false;
        var tabs = SquadTabSquads();
        if (i < tabs.Count) Say(new ActiveSquadCommand(tabs[i].Id) { Tick = session.World.Tick });
        return true;
    }

    /// <summary>The overview's SQUADS tab widgets: the faction's count against <c>max faction size</c>, the button and the dismiss label (both upper-cased by the original).</summary>
    void BindSquadsTab(UiBindings b)
    {
        var limits = SquadLimits?.Invoke();
        b.Text["FactionSizeText"] = $"{LivingPlayerCharacters()}/{limits?.MaxFactionSize ?? 30}";
        b.Text["NewSquadButton"] = "ADD SQUAD";
        b.Text["lbSquadsDismiss"] = "DISMISS";
        b.Actions["NewSquadButton"] = "squad-new";
        b.Disabled.Remove("NewSquadButton");
        if (portraitDrag is { } drag && Vector2.Distance(drag.From, mouse) >= DragThreshold && OverDismiss(mouse))
            b.Properties["DismissCharPanel"] = new Dictionary<string, string> { ["State"] = "highlighted" };
    }

    void AddSquadsContent(List<UiControl> controls)
    {
        if (!SquadsShown || squadBoxLayout is null || controls.FirstOrDefault(c => c.Name == "SquadsPanel") is not { } panel) return;
        var snapshot = session.CurrentSnapshot;
        var squads = snapshot.PlayerSquads;
        var limits = SquadLimits?.Invoke();
        int maxSize = limits?.MaxSquadSize ?? 20;
        var cells = new Dictionary<CharacterId, int>();
        var portraits = PortraitMembers();
        for (int i = 0; i < portraits.Count; i++) cells[portraits[i].Id] = i;
        var byId = snapshot.Characters.Where(c => c.IsPlayer).ToDictionary(c => c.Id);
        var area = panel.Bounds;
        var clip = panel.Clip is { } outer ? area.Intersect(outer) : area;
        float boxHeight = area.Height * SquadBoxShare, boxWidth = area.Width - SquadScrollBar * InventoryScale;
        squadsScroll = Math.Clamp(squadsScroll, 0, Math.Max(0, squads.Count * boxHeight - area.Height));
        var focus = faithfulScreens.Focus is { Screen: "overview" } f ? f : default((string, string, int, int, int)?);
        bool dragging = portraitDrag is { } pressed && Vector2.Distance(pressed.From, mouse) >= DragThreshold;
        for (int i = 0; i < squads.Count; i++)
        {
            var squad = squads[i];
            var box = new UiRect(area.X, area.Y + i * boxHeight - squadsScroll, boxWidth, boxHeight);
            if (box.Intersect(clip).Height <= 0) continue;
            string nameBox = SquadNameBox(squad.Id);
            bool typing = focus is { } fc && fc.Item2 == nameBox;
            // The box shows the squad's name, or what is being typed; a box not being typed in starts from the name when it is next pressed.
            if (!typing) faithfulScreens.SetText("overview", nameBox, squad.Name);
            var b = new UiBindings { TemplateScale = InventoryScale };
            b.Text["SquadNameText"] = typing ? faithfulScreens.GetText("overview", nameBox) : squad.Name;
            int count = squad.Members.Count;
            b.Text["SquadSizeText"] = $"{count}/{maxSize}";
            if (count >= maxSize) b.Properties["SquadSizeText"] = new Dictionary<string, string> { ["TextColour"] = BadColour };
            b.Actions["SquadRemoveButton"] = "squad-remove:" + squad.Id.ToString(CultureInfo.InvariantCulture);
            if (typing && focus is { } edit)
                foreach (var widget in squadBoxLayout.Widgets.Where(w => w.Name == "SquadNameText" || w.Properties.GetValueOrDefault("CaptionOwner") == "SquadNameText"))
                    b.Properties[widget.Name] = new Dictionary<string, string>
                    {
                        ["Focused"] = "true", ["CaretIndex"] = edit.Item3.ToString(CultureInfo.InvariantCulture),
                        ["SelectionStart"] = edit.Item4.ToString(CultureInfo.InvariantCulture), ["SelectionLength"] = edit.Item5.ToString(CultureInfo.InvariantCulture),
                    };
            string squadId = squad.Id.ToString(CultureInfo.InvariantCulture);
            UiRect? portraitsArea = null;
            foreach (var c in MyGuiPresentation.Arrange(squadBoxLayout, (int)MathF.Round(box.Width), (int)MathF.Round(box.Height), b, uiResources.Layers))
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
            float size = Math.Min(Math.Min(Width * SquadCellWidth, Height * SquadCellHeight), cellsArea.Height - 4);
            if (members.Count * size > cellsArea.Width) size = cellsArea.Width / members.Count;
            var cellClip = cellsArea.Intersect(clip);
            for (int k = 0; k < members.Count; k++)
            {
                if (dragging && portraitDrag!.Value.Character == members[k]) continue;   // it follows the pointer
                var cell = new UiRect(cellsArea.X + 2 + k * size, cellsArea.Y + (cellsArea.Height - size) / 2, size, size);
                AddSquadPortrait(controls, $"squad-{squadId}-cell-{k}", cell, byId[members[k]], cells.GetValueOrDefault(members[k], -1), cellClip,
                    new Dictionary<string, string> { ["SquadOf"] = squadId, ["SquadPortrait"] = $"{squadId}:{k}" });
            }
        }
        if (dragging && byId.TryGetValue(portraitDrag!.Value.Character, out var held))
        {
            float size = Math.Min(Width * SquadCellWidth, Height * SquadCellHeight);
            AddSquadPortrait(controls, "squad-drag", new(mouse.X - size / 2, mouse.Y - size / 2, size, size), held, cells.GetValueOrDefault(held.Id, -1), null, null);
        }
    }

    /// <summary>The player's characters alive (what the faction's count and the dismiss rule count, <see cref="SquadManagement.FactionHeads"/>).</summary>
    int LivingPlayerCharacters() => session.CurrentSnapshot.Characters.Count(c => c.IsPlayer && c.Body is not { Dead: true });

    static string SquadNameBox(int squad) => "squad-name-" + squad.ToString(CultureInfo.InvariantCulture);

    /// <summary>One portrait cell as <c>Kenshi_SquadsPanel_PortraitCharacter</c> lays it out: the grey back, the portrait, the name along the bottom, the frame (checked when selected).</summary>
    void AddSquadPortrait(List<UiControl> controls, string name, UiRect cell, CharacterSnapshot member, int atlasCell, UiRect? clip, Dictionary<string, string>? tags)
    {
        UiRect At(float x, float y, float w, float h) => new(cell.X + x * cell.Width, cell.Y + y * cell.Height, w * cell.Width, h * cell.Height);
        controls.Add(new($"{name}-back", "Widget", At(0, 0, 1, 1), "", "", "", true, new Dictionary<string, string>(), new Vector4(0.458f, 0.423f, 0.396f, 1), Pickable: false, Clip: clip));
        if (atlasCell >= 0)
        {
            controls.Add(new($"{name}-image", "ImageBox", At(0.0375f, 0.0375f, 0.9125f, 0.9125f), "", "", "", true,
                new Dictionary<string, string> { ["ImageTexture"] = PortraitTexture, ["ImageFlipV"] = "true", ["ImageRect"] = PortraitRect(atlasCell) },
                Vector4.Zero, Pickable: false, Clip: clip));
        }
        controls.Add(new($"{name}-name", "TextBox", At(0.05f, 0.05f, 0.9f, 0.8875f), member.Name, "", "Kenshi_TextboxStandardText", true,
            new Dictionary<string, string> { ["TextAlign"] = "HCenter Bottom", ["TextColour"] = "0.707602 0.707602 0.707602", ["TextShadow"] = "true", ["FontName"] = "Kenshi_StandardFont_MediumSmall" },
            Vector4.Zero, Pickable: false, Clip: clip));
        var frame = new Dictionary<string, string>(tags ?? [], StringComparer.Ordinal) { ["Checked"] = member.Selected ? "true" : "false" };
        controls.Add(new($"{name}-frame", "Button", At(-0.0375f, -0.025f, 1.0625f, 1.05f), "", "", "Kenshi_PortraitFrameSkin", true, frame, Vector4.Zero, Pickable: tags is not null, Clip: clip));
    }

    /// <summary>A press on the squads screen: a portrait (to select or drag) or a squad box's frame (to reorder). True: taken.</summary>
    bool SquadPointerDown(Vector2 p)
    {
        if (!SquadsShown || faithfulScreens.HitTest(p) is not { Screen: "overview" } hit) return false;
        var c = hit.Control;
        if (c.Properties.TryGetValue("SquadPortrait", out var at) && PortraitAt(at) is { } member) { faithfulScreens.Blur(); portraitDrag = (member, p); return true; }
        if (c.Properties.TryGetValue("SquadBox", out var box) && int.TryParse(box, NumberStyles.None, CultureInfo.InvariantCulture, out int squad)) { faithfulScreens.Blur(); squadBoxDrag = (squad, p); return true; }
        return false;
    }

    /// <summary>The member a portrait's <c>squad:index</c> tag names.</summary>
    CharacterId? PortraitAt(string tag)
    {
        var parts = tag.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int squad) || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int index)) return null;
        var present = PlayerCharacters().ToHashSet();
        var members = session.CurrentSnapshot.PlayerSquads.FirstOrDefault(s => s.Id == squad)?.Members.Where(present.Contains).ToList();
        return members is not null && index < members.Count ? members[index] : null;
    }

    bool OverDismiss(Vector2 p) => faithfulScreens.HitTest(p) is { Screen: "overview" } hit && hit.Control.Name is "DismissCharPanel" or "lbSquadsDismiss";

    /// <summary>
    /// The button let go over the squads screen: a portrait that did not move selects its character; one dragged onto a portrait goes in at its place (Shift: they swap),
    /// onto a squad box at its end, onto DRAG HERE TO DISMISS asks to dismiss it. A squad box dropped on another takes its place in the order.
    /// </summary>
    void SquadPointerUp(Vector2 p, bool shift)
    {
        var portrait = portraitDrag;
        var box = squadBoxDrag;
        portraitDrag = null;
        squadBoxDrag = null;
        if (!SquadsShown) return;
        var target = faithfulScreens.HitTest(p) is { Screen: "overview" } hit ? hit.Control : null;
        int? targetSquad = target is not null && target.Properties.TryGetValue("SquadOf", out var of) && int.TryParse(of, NumberStyles.None, CultureInfo.InvariantCulture, out int s) ? s : null;
        if (portrait is { } drag)
        {
            if (Vector2.Distance(drag.From, p) < DragThreshold) { Select([drag.Character], shift); return; }
            if (OverDismiss(p)) { AskDismiss(drag.Character); return; }
            if (targetSquad is not { } into) return;
            int index = -1;
            if (target!.Properties.TryGetValue("SquadPortrait", out var at) && at.Split(':') is [_, var place]) index = int.Parse(place, CultureInfo.InvariantCulture);
            if (index >= 0 && PortraitAt(target.Properties["SquadPortrait"]) == drag.Character) return;
            Say(new MoveToSquadCommand(drag.Character, into, index, shift) { Tick = session.World.Tick });
        }
        else if (box is { } moved && targetSquad is { } onto && onto != moved.Squad && Vector2.Distance(moved.From, p) >= DragThreshold)
        {
            int index = session.CurrentSnapshot.PlayerSquads.ToList().FindIndex(q => q.Id == onto);
            Say(new ReorderSquadCommand(moved.Squad, index) { Tick = session.World.Tick });
        }
    }

    /// <summary>Dismiss: refused for the faction's last character, else asked first ("Dismiss character '{1}'?").</summary>
    void AskDismiss(CharacterId id)
    {
        if (LivingPlayerCharacters() <= 1) { Confirm("Dismiss character", "Cannot dismiss the only remaining character in the faction.", () => { }, "OK"); return; }
        string name = session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id)?.Name ?? "";
        Confirm("Dismiss character", Loc.Tr("Dismiss character '{1}'?", name), () => Say(new DismissCommand(id) { Tick = session.World.Tick }));
    }

    bool SquadsWheel(Vector2 p, float steps)
    {
        if (!SquadsShown || faithfulScreens.HitTest(p) is not { Screen: "overview" } hit || !(hit.Control.Name == "SquadsPanel" || hit.Control.Properties.ContainsKey("SquadOf"))) return false;
        squadsScroll -= steps * 44 * InventoryScale;   // clamped when the list is laid out
        return true;
    }

    void SquadsAction(MyGuiEvent e)
    {
        if (e.Action == "squad-new" || e.Widget == "NewSquadButton")
        {
            var view = session.Camera.Current;
            Say(new NewSquadCommand(view.Target) { Tick = session.World.Tick });
        }
        else if (e.Action.StartsWith("squad-remove:", StringComparison.Ordinal) && int.TryParse(e.Action.AsSpan(13), NumberStyles.None, CultureInfo.InvariantCulture, out int doomed))
            Say(new RemoveSquadCommand(doomed) { Tick = session.World.Tick });
        else if (e.Action == "text" && e.Widget.StartsWith("squad-name-", StringComparison.Ordinal) && int.TryParse(e.Widget.AsSpan(11), NumberStyles.None, CultureInfo.InvariantCulture, out int named))
        {
            string text = e.Text.Length > SquadManagement.NameLength ? e.Text[..SquadManagement.NameLength] : e.Text;
            if (text != e.Text) faithfulScreens.SetText("overview", e.Widget, text);
            Say(new RenameSquadCommand(named, text) { Tick = session.World.Tick });
        }
    }
}
