using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Production;
using Meitou.Game.Ui;

namespace Meitou.Game;

/// <summary>
/// The HUD's town panel (<c>TownPanel</c> of <c>Kenshi_MainPanel</c>, docs/game/ui-screens.md "Town panel"): the outpost the camera is in, its name box, the Open to public tick, the
/// research, power and battery bars. It is taken out of the HUD as a screen panel so that its name box takes typing.
/// </summary>
sealed partial class PlayerInterface
{
    const string TownScreen = "town", TownNameBox = "OutpostNameEditBox", TownPublicTick = "OpenToPublicCheckBox";

    /// <summary>The outpost the panel shows (<see cref="PlayerTown.Number"/>), 0 for none.</summary>
    int shownTown;
    /// <summary>What was typed in the name box and is not yet accepted (null: nothing), and for which outpost.</summary>
    (int Town, string Name)? townDraft;

    /// <summary>The town panel's subtree as a layout of its own, on the HUD's layer and shown (the HUD layout hides it until the code shows it).</summary>
    static MyGuiLayout TownPanelLayout(MyGuiLayout hud)
    {
        var panel = hud.Find("TownPanel") ?? throw new InvalidDataException("Kenshi_MainPanel.layout has no TownPanel.");
        var properties = new Dictionary<string, string>(panel.Properties, StringComparer.Ordinal);
        properties.Remove("Visible");
        return hud with { Roots = [panel with { Layer = hud.Roots[0].Layer, Properties = properties }] };
    }

    /// <summary>
    /// Once a frame (<c>MainBarGUI</c>'s update, <c>FUN_140728d50</c>): the outpost still shown while the camera pivot is within its radius, else the nearest outpost of the player's
    /// faction if the pivot is within its radius (<c>FUN_140927e40</c>), else none; the panel shows while there is one and the HUD does.
    /// </summary>
    void SyncTownPanel(bool hud)
    {
        var outposts = session.CurrentSnapshot.Outposts;
        var pivot = session.Camera.Current.Target;
        static bool Within(OutpostSnapshot t, Vector3 p) => (t.Position.X - p.X) * (t.Position.X - p.X) + (t.Position.Z - p.Z) * (t.Position.Z - p.Z) <= t.Radius * t.Radius;
        var town = outposts.FirstOrDefault(t => t.Number == shownTown) is { } kept && Within(kept, pivot) ? kept
            : outposts.Where(t => t.Owner == session.World.Player.Faction)
                .MinBy(t => (t.Position.X - pivot.X) * (t.Position.X - pivot.X) + (t.Position.Z - pivot.Z) * (t.Position.Z - pivot.Z)) is { } nearest && Within(nearest, pivot) ? nearest : null;
        if (!hud) town = null;
        shownTown = town?.Number ?? 0;
        if (town is null) { if (faithfulScreens.IsOpen(TownScreen)) faithfulScreens.Close(TownScreen); return; }
        if (!faithfulScreens.IsOpen(TownScreen)) faithfulScreens.Open(TownScreen);
        if (faithfulScreens.Focus is not { Screen: TownScreen, Widget: TownNameBox }) faithfulScreens.SetText(TownScreen, TownNameBox, town.Name);
    }

    /// <summary>
    /// The panel's lines (<c>FUN_140722de0</c>, <c>FUN_140727380</c>): the name box and the tick, live only for a town of the player's faction; the research being done and its
    /// progress, else "None"; generated / demanded power; the batteries' charge / capacity, " &gt;&gt;" after it while they charge, "&lt;&lt; " before it while they drain.
    /// </summary>
    void BindTownScreen(UiBindings b)
    {
        if (session.CurrentSnapshot.Outposts.FirstOrDefault(t => t.Number == shownTown) is not { } town) return;
        bool own = town.Owner == session.World.Player.Faction;
        if (own) { b.Disabled.Remove(TownPublicTick); b.Actions[TownPublicTick] = TownPublicTick; }
        else b.Disabled.Add(TownNameBox);
        b.Properties[TownPublicTick] = new Dictionary<string, string> { ["StateSelected"] = town.Public ? "true" : "false", ["Checked"] = town.Public ? "true" : "false" };
        var research = session.CurrentSnapshot.Research.Queue;
        b.Text["ResearchProgressBarCaption"] = research.Count > 0 ? research[0].Name : Loc.Tr("None");
        b.Progress["ResearchProgressBar"] = research.Count > 0 ? Percent(research[0].Progress) : 0;
        var p = town.Power;
        b.Progress["PowerProgressBar"] = Percent(p.Demand > 0 ? p.Generated / p.Demand : p.Generated > 0 ? 1 : 0);
        b.Text["PowerProgressBarCaption"] = Loc.Tr("{1,num}/{2,num}", (int)p.Generated, (int)p.Demand);
        string battery = Loc.Tr("{1,num}/{2,num}", (int)p.Charge, (int)p.Capacity);
        b.Text["BatteryProgressBarCaption"] = p.Charging > 0 ? battery + " >>" : p.Draining > 0 ? "<< " + battery : battery;
        b.Progress["BatteryProgressBar"] = p.Capacity > 0 ? Percent(p.Charge / p.Capacity) : 0;
    }

    /// <summary>A bar's position as the panel sets it: whole percent of its range of 100.</summary>
    static float Percent(float fraction) => MathF.Round(fraction * 100) / 100;

    /// <summary>A text change in the name box is kept as the draft; the tick flips Open to public (<c>FUN_140721e50</c>, with <c>General_Click</c>).</summary>
    void TownAction(MyGuiEvent e)
    {
        if (e.Widget == TownNameBox && e.Action == "text" && shownTown > 0) townDraft = (shownTown, e.Text);
        else if (e.Widget == TownPublicTick && session.CurrentSnapshot.Outposts.FirstOrDefault(t => t.Number == shownTown) is { } town)
        {
            Sound("General_Click");
            Say(new SetTownPublic(town.Number, !town.Public) { Tick = session.World.Tick });
        }
    }

    /// <summary>Once a frame: a draft whose box no longer has the key focus is sent as <see cref="RenameTown"/> (the box's accept, <c>FUN_140722d60</c>).</summary>
    void UpdateTownName()
    {
        if (townDraft is not { } draft || faithfulScreens.Focus is { Screen: TownScreen, Widget: TownNameBox }) return;
        townDraft = null;
        Say(new RenameTown(draft.Town, draft.Name) { Tick = session.World.Tick });
    }
}
