using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Production;

namespace Meitou.Game.Ui;

/// <summary>
/// The HUD's town panel (<c>TownPanel</c> of <c>Kenshi_MainPanel</c>, docs/game/ui-screens.md "Town panel"): the outpost the camera is in, its name
/// box, the Open to public tick, the research, power and battery bars. It is taken out of the HUD as a screen panel so that its name box takes
/// typing.
/// </summary>
sealed class TownPanel(PlayerInterface player)
{
    /// <summary>
    /// The panel's screen id, its name edit box and its Open to public tick.
    /// </summary>
    internal const string TownScreen = "town", TownNameBox = "OutpostNameEditBox", TownPublicTick = "OpenToPublicCheckBox";

    /// <summary>
    /// The outpost the panel shows (<see cref="PlayerTown.Number"/>), 0 for none.
    /// </summary>
    internal int ShownTown;

    /// <summary>
    /// What was typed in the name box and is not yet accepted (null: nothing), and for which outpost.
    /// </summary>
    (int Town, string Name)? _townDraft;

    /// <summary>
    /// The town panel's subtree as a layout of its own, on the HUD's layer and shown (the HUD layout hides it until the code shows it).
    /// </summary>
    internal static MyGuiLayout TownPanelLayout(MyGuiLayout hud)
    {
        var panel = hud.Find("TownPanel") ?? throw new InvalidDataException("Kenshi_MainPanel.layout has no TownPanel.");
        var properties = new Dictionary<string, string>(panel.Properties, StringComparer.Ordinal);
        properties.Remove("Visible");
        return hud with { Roots = [panel with { Layer = hud.Roots[0].Layer, Properties = properties }] };
    }

    /// <summary>
    /// Once a frame (<c>MainBarGUI</c>'s update, <c>FUN_140728d50</c>): the outpost still shown while the camera pivot is within its radius, else the
    /// nearest outpost of the player's faction if the pivot is within its radius (<c>FUN_140927e40</c>), else none; the panel shows while there is
    /// one and the HUD does.
    /// </summary>
    internal void SyncTownPanel(bool hud)
    {
        var outposts = player.Session.CurrentSnapshot.Outposts;
        var pivot = player.Session.Camera.Current.Target;
        var town = outposts.FirstOrDefault(t => t.Number == ShownTown) is { } kept && Within(kept, pivot) ? kept : NearestOwnOutpost(outposts, pivot);
        if (!hud) town = null;

        ShownTown = town?.Number ?? 0;
        if (town is null)
        {
            if (player.FaithfulScreens.IsOpen(TownScreen)) player.FaithfulScreens.Close(TownScreen);
            return;
        }

        if (!player.FaithfulScreens.IsOpen(TownScreen)) player.FaithfulScreens.Open(TownScreen);
        if (player.FaithfulScreens.Focus is not { Screen: TownScreen, Widget: TownNameBox })
            player.FaithfulScreens.SetText(TownScreen, TownNameBox, town.Name);
    }

    /// <summary>
    /// The panel's lines (<c>FUN_140722de0</c>, <c>FUN_140727380</c>): the name box and the tick, live only for a town of the player's faction; the
    /// research being done and its progress, else "None"; generated / demanded power; the batteries' charge / capacity, " &gt;&gt;" after it while
    /// they charge, "&lt;&lt; " before it while they drain.
    /// </summary>
    internal void BindTownScreen(UiBindings b)
    {
        if (player.Session.CurrentSnapshot.Outposts.FirstOrDefault(t => t.Number == ShownTown) is not { } town) return;

        bool own = town.Owner == player.Session.World.Player.Faction;
        if (own)
        {
            b.Disabled.Remove(TownPublicTick);
            b.Actions[TownPublicTick] = TownPublicTick;
        }
        else b.Disabled.Add(TownNameBox);

        string publicFlag = town.Public ? "true" : "false";
        b.Properties[TownPublicTick] = new Dictionary<string, string> { ["StateSelected"] = publicFlag, ["Checked"] = publicFlag };
        var research = player.Session.CurrentSnapshot.Research.Queue;
        b.Text["ResearchProgressBarCaption"] = research.Count > 0 ? research[0].Name : Loc.Tr("None");
        b.Progress["ResearchProgressBar"] = research.Count > 0 ? Percent(research[0].Progress) : 0;
        var p = town.Power;
        b.Progress["PowerProgressBar"] = Percent(p.Demand > 0 ? p.Generated / p.Demand : p.Generated > 0 ? 1 : 0);
        b.Text["PowerProgressBarCaption"] = Loc.Tr("{1,num}/{2,num}", (int)p.Generated, (int)p.Demand);
        string battery = Loc.Tr("{1,num}/{2,num}", (int)p.Charge, (int)p.Capacity);
        b.Text["BatteryProgressBarCaption"] = p.Charging > 0 ? battery + " >>" : p.Draining > 0 ? "<< " + battery : battery;
        b.Progress["BatteryProgressBar"] = p.Capacity > 0 ? Percent(p.Charge / p.Capacity) : 0;
    }

    /// <summary>
    /// The player's faction's outpost nearest <paramref name="pivot"/> (X/Z) when the pivot is within its radius, else null.
    /// </summary>
    OutpostSnapshot? NearestOwnOutpost(IReadOnlyList<OutpostSnapshot> outposts, Vector3 pivot)
    {
        var nearest = outposts.Where(t => t.Owner == player.Session.World.Player.Faction)
            .MinBy(t => (t.Position.X - pivot.X) * (t.Position.X - pivot.X) + (t.Position.Z - pivot.Z) * (t.Position.Z - pivot.Z));
        return nearest is not null && Within(nearest, pivot) ? nearest : null;
    }

    /// <summary>
    /// Whether <paramref name="p"/> is within the outpost's radius (X/Z).
    /// </summary>
    static bool Within(OutpostSnapshot t, Vector3 p) =>
        (t.Position.X - p.X) * (t.Position.X - p.X) + (t.Position.Z - p.Z) * (t.Position.Z - p.Z) <= t.Radius * t.Radius;

    /// <summary>
    /// A bar's position as the panel sets it: whole percent of its range of 100.
    /// </summary>
    static float Percent(float fraction) => MathF.Round(fraction * 100) / 100;

    /// <summary>
    /// A text change in the name box is kept as the draft; the tick flips Open to public (<c>FUN_140721e50</c>, with <c>General_Click</c>).
    /// </summary>
    internal void TownAction(MyGuiEvent e)
    {
        if (e.Widget == TownNameBox && e.Action == "text" && ShownTown > 0)
        {
            _townDraft = (ShownTown, e.Text);
        }
        else if (e.Widget == TownPublicTick && player.Session.CurrentSnapshot.Outposts.FirstOrDefault(t => t.Number == ShownTown) is { } town)
        {
            player.InterfaceSounds.Sound("General_Click");
            player.Say(new SetTownPublic(town.Number, !town.Public) { Tick = player.Session.World.Tick });
        }
    }

    /// <summary>
    /// Once a frame: a draft whose box no longer has the key focus is sent as <see cref="RenameTown"/> (the box's accept, <c>FUN_140722d60</c>).
    /// </summary>
    internal void UpdateTownName()
    {
        if (_townDraft is not { } draft) return;
        if (player.FaithfulScreens.Focus is { Screen: TownScreen, Widget: TownNameBox }) return;

        _townDraft = null;
        player.Say(new RenameTown(draft.Town, draft.Name) { Tick = player.Session.World.Tick });
    }
}