using System.Globalization;
using Meitou.Game.Audio;
using Meitou.Simulation;
using Meitou.Simulation.Player;

namespace Meitou.Game.Ui;

/// <summary>
/// The interface's sounds (docs/audio.md "Interface"). The original has no central click hook either: every handler posts its own sound, so this maps
/// Meitou's actions to the same events (<see cref="PlayerInterface.ExecuteUiAction"/>, <see cref="ScreenRouter.FaithfulAction"/>, the commands
/// <see cref="PlayerInterface.Say"/> sends) and watches the windows open and close once a frame (<see cref="PollUiSounds"/>). The host plays what
/// <see cref="UiSound"/> receives; nothing here changes the game.
/// </summary>
sealed class InterfaceSounds(PlayerInterface player)
{
    /// <summary>
    /// The player's orders (the commands that give a character a job), by type name: they make the order dispatcher's click.
    /// </summary>
    static readonly HashSet<string> OrderCommands = new(StringComparer.Ordinal)
    {
        "MoveOrder", "AttackOrder", "BuildOrder", "DismantleOrder", "GetOutOrder", "LootOrder", "OpenLootOrder", "PayBailOrder", "PickLockOrder",
        "PickUpOrder", "PutDownOrder", "PutInCageOrder", "PutShacklesOrder",
        "OperateOrder", "PutInBedOrder", "RestOrder", "SplintOrder", "StealthKoOrder", "TalkOrder", "TreatOrder", "UseBedOrder",
    };

    /// <summary>
    /// The screens seen open at the last poll.
    /// </summary>
    internal readonly HashSet<string> SoundScreens = [];

    /// <summary>
    /// The overview's tab at the last poll; -1 while it is closed.
    /// </summary>
    int _soundOverviewTab = -1;

    /// <summary>
    /// The title button under the pointer at the last poll; null for none.
    /// </summary>
    string? _soundHover;

    /// <summary>
    /// The world the research and building baselines belong to.
    /// </summary>
    object? _soundWorld;

    /// <summary>
    /// The last running game speed.
    /// </summary>
    double _soundSpeed = 1;

    /// <summary>
    /// A speed button sounded this frame (the speed keys' check skips it).
    /// </summary>
    bool _speedSounded;

    /// <summary>
    /// The unlocked buildings and finished research counted at the last poll; -1 before the baseline.
    /// </summary>
    int _soundUnlocked = -1;

    /// <summary>
    /// The buildings revision last looked at; -1 before the baseline.
    /// </summary>
    long _soundBuildingsRevision = -1;

    /// <summary>
    /// Each player site's completion at the last look, by id.
    /// </summary>
    readonly Dictionary<string, bool> _soundBuildings = new(StringComparer.Ordinal);

    /// <summary>
    /// The slider that last sounded (it sounds once until another is moved); null for none.
    /// </summary>
    string? _lastSliderSound;

    /// <summary>
    /// Where interface sounds go (the host's audio); null without audio.
    /// </summary>
    public Action<UiSoundEvent>? UiSound { get; set; }

    /// <summary>
    /// Sends interface sound <paramref name="name"/> (with its switch group and value, a state, an item or a building) to the host.
    /// </summary>
    internal void Sound(string name, string? group = null, string? value = null, bool state = false, string? item = null, string? building = null) =>
        UiSound?.Invoke(new UiSoundEvent(name, group, value, state, item, building));

    /// <summary>
    /// Once a frame: windows opening and closing, the title buttons under the pointer, buildings finishing.
    /// </summary>
    internal void PollUiSounds()
    {
        if (UiSound is null) return;

        var open = player.FaithfulScreens.OpenScreens;
        SoundClosedScreens(open);
        SoundOpenedScreens(open);
        SoundOverviewTab();
        SoundTitleHover();
        SoundSpeedKeys();
        SoundFinishedResearch();
        SoundFinishedBuildings();
    }

    /// <summary>
    /// The windows closed since the last frame: the inventory, stats, map and research windows' close sounds, the Esc menu's click.
    /// </summary>
    void SoundClosedScreens(IReadOnlyList<string> open)
    {
        foreach (string id in SoundScreens.ToArray())
        {
            if (open.Contains(id)) continue;

            SoundScreens.Remove(id);
            switch (InventoryWindows.IsPinnedInventory(id) ? "inventory" : id)
            {
                case "inventory" or "bag" or "limbs" or "trader" or "building" or "bench" or "storage": Sound("Inventory_Close"); break;
                case "stats": Sound("Stats_Close"); break;
                case "overview":
                    if (_soundOverviewTab == 0) Sound("Map_Close");
                    else if (_soundOverviewTab == 2) Sound("Tech_Close");
                    _soundOverviewTab = -1;
                    break;
                case "menu": Sound("General_Click"); break;
            }
        }
    }

    /// <summary>
    /// The windows opened since the last frame: the inventory and stats windows' open sounds, the build window's button, the Esc menu's click.
    /// </summary>
    void SoundOpenedScreens(IReadOnlyList<string> open)
    {
        foreach (string id in open)
        {
            if (!SoundScreens.Add(id)) continue;

            switch (InventoryWindows.IsPinnedInventory(id) ? "inventory" : id)
            {
                case "inventory" or "bag" or "limbs" or "trader" or "building" or "bench" or "storage": Sound("Inventory_Open"); break;
                case "stats": Sound("Stats_Open"); break;
                case "build": Sound("Building_Button"); break;
                case "menu": Sound("General_Click"); break;
                case "overview":
                    _soundOverviewTab = -1;
                    break;
            }
        }
    }

    /// <summary>
    /// The overview's tabs (14049bf90): the map and research tabs have their own sounds, the others are tabs.
    /// </summary>
    void SoundOverviewTab()
    {
        if (!SoundScreens.Contains("overview")) return;

        int tab = player.FaithfulScreens.GetTab("overview", "TabsMain");
        if (tab != _soundOverviewTab)
        {
            _soundOverviewTab = tab;
            Sound(tab == 0 ? "Map_Open" : tab == 2 ? "Tech_Open" : "Tab");
        }
    }

    /// <summary>
    /// The title buttons: a sound when the pointer enters one (the title screen's constructor binds only these seven).
    /// </summary>
    void SoundTitleHover()
    {
        string? hover = null;
        if (player.FaithfulScreens.IsOpen("title")
            && player.FaithfulScreens.HitTest(player.Mouse) is { Screen: "title", Control: { Type: "Button", Enabled: true } button })
        {
            hover = button.Name;
        }

        if (hover != _soundHover)
        {
            _soundHover = hover;
            if (hover is not null) Sound("Hover");
        }
    }

    /// <summary>
    /// The speed keys (F2 to F4) change the clock without a button: the same sound when the speed moves to a new running one (the buttons did it
    /// already).
    /// </summary>
    void SoundSpeedKeys()
    {
        double speedNow = player.Session.Simulation.Speed;
        if (speedNow > 0 && speedNow != _soundSpeed && !_speedSounded && (speedNow == 1 || speedNow == 2 || speedNow == 5))
        {
            string name = speedNow == 1 ? "Normal" : speedNow == 2 ? "x2" : "x4";
            Sound("Game_Speed_" + name, "Game_Speed", name, state: true);
        }

        if (speedNow > 0) _soundSpeed = speedNow;
        _speedSounded = false;
    }

    /// <summary>
    /// A research finished (also one that unlocks nothing) or a blueprint read: "Research complete" (140834550). The first look only takes the
    /// baseline.
    /// </summary>
    void SoundFinishedResearch()
    {
        if (!ReferenceEquals(_soundWorld, player.Session.World)) _soundUnlocked = -1;
        int unlocked = player.Session.CurrentSnapshot.UnlockedBuildings.Count + player.Session.CurrentSnapshot.Research.Finished;
        if (_soundUnlocked >= 0 && unlocked > _soundUnlocked) Sound("Notifications", "Notifications", "Thing_Complete");
        _soundUnlocked = unlocked;
    }

    /// <summary>
    /// A building of the player's finishing: the notification (140559110). The first look only takes the baseline.
    /// </summary>
    void SoundFinishedBuildings()
    {
        var snapshot = player.Session.CurrentSnapshot;
        if (!ReferenceEquals(_soundWorld, player.Session.World))
        {
            _soundWorld = player.Session.World;
            _soundBuildingsRevision = -1;
            _soundBuildings.Clear();
            _soundUnlocked = -1;
        }

        if (snapshot.BuildingsRevision != _soundBuildingsRevision || _soundBuildings.Count != snapshot.Buildings.Count)
            NotifyFinishedBuildings(snapshot);
    }

    /// <summary>
    /// Plays the building notification when one of the player's buildings has just completed (the baseline is not a finish).
    /// </summary>
    void NotifyFinishedBuildings(WorldSnapshot snapshot)
    {
        bool baseline = _soundBuildingsRevision < 0;
        _soundBuildingsRevision = snapshot.BuildingsRevision;
        bool finished = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var site in snapshot.Buildings)
        {
            seen.Add(site.Id);
            if (!baseline && site.Complete && _soundBuildings.TryGetValue(site.Id, out bool was) && !was) finished = true;
            _soundBuildings[site.Id] = site.Complete;
        }

        foreach (string id in _soundBuildings.Keys.Where(id => !seen.Contains(id)).ToArray()) _soundBuildings.Remove(id);
        if (finished) Sound("Notifications", "Notifications", "Building_Complete");
    }

    /// <summary>
    /// A HUD action, before it runs (so a toggle's state is still the old one).
    /// </summary>
    internal void SoundForAction(string action, string argument)
    {
        if (UiSound is null) return;

        if (action.StartsWith("speed:", StringComparison.Ordinal))
        {
            // Game_Speed is a state; the exe sets it and posts for 1, 2 and 5 only (140787e40), its three speed buttons; Meitou posts per button,
            // whatever its speed.
            int index = int.Parse(action[6..], CultureInfo.InvariantCulture);
            if (index is > 0 and < PlayerInterface.SpeedButtons)
            {
                string name = index == 1 ? "Normal" : index == 2 ? "x2" : "x4";
                Sound("Game_Speed_" + name, "Game_Speed", name, state: true);
                _speedSounded = true;
            }
        }
        else if (action is "floor-up" or "floor-down") Sound("Change_Level");
        else if (action == "extendedstats") Sound("Stats_Preview", "Stats_Preview", player.InfoExpanded ? "Stats_Down" : "Stats_Up");
        else if (action is "member" or "job-remove" || action.StartsWith("runspeed.", StringComparison.Ordinal)) Sound("General_Click");
        else if (action.StartsWith("order.", StringComparison.Ordinal) && Enum.TryParse(action[6..], out CharacterOrders order))
            SoundForOrder(order);
    }

    /// <summary>
    /// The orders panel (1407214e0 and neighbours): Block, Hold, Passive and Ranged make Blocks_Only on and off; Sneak makes Stealth on and a click
    /// off.
    /// </summary>
    void SoundForOrder(CharacterOrders order)
    {
        if (order is CharacterOrders.Block or CharacterOrders.Hold or CharacterOrders.Passive or CharacterOrders.Ranged) Sound("Blocks_Only");
        else if (order == CharacterOrders.Taunt) Sound("Taunt");
        else if (order == CharacterOrders.Sneak)
        {
            bool sneaking = player.Selected().FirstOrDefault() is { } lead && lead.Orders.HasFlag(CharacterOrders.Sneak);
            Sound(sneaking ? "General_Click" : "Stealth");
        }
        else if (order == CharacterOrders.Jobs) Sound("General_Click");
    }

    /// <summary>
    /// A button, tab or list row of one of the original's screens, before it runs.
    /// </summary>
    internal void SoundForScreen(MyGuiEvent e)
    {
        if (UiSound is null || e.Action.StartsWith("inventory-scroll", StringComparison.Ordinal)) return;

        switch (e.Screen)
        {
            case "title" or "menu" or "new-game" or "save" or "load" or "message" or "file": Sound("General_Click"); break;
            case "inventory" or "bag" or "trader": if (e.Widget == "ArrangeButton") Sound("General_Click"); break;

            // A pick in the research type list (FUN_14049b5e0): Research_Tabs is set to the item's text, the category; the same row again is no
            // change.
            case "overview":
                if (e is { Widget: "ResearchTypeList", Action: "select" } && e.Text != player.Production.ResearchCategory)
                    Sound("Research_Tab", "Research_Tabs", e.Text);
                break;
            case "build":
                if (e.Widget is "CategoriesList" or "BuildingsList" or "BuldingTypePrevButton" or "BuldingTypeNextButton") Sound("Building_Navigate");
                break;
            case "editor":
                if (e.Widget == "ConfirmButton") Sound("Create_Character");
                else if (e.Widget is "BodyTabButton" or "FaceTabButton" or "HairTabButton") Sound("Tab");
                // Slider: the first event of each drag (a row's bar, named by its row). A list row's arrows click.
                else if (e.Action == "scroll")
                {
                    if (e.Widget != _lastSliderSound) Sound("Slider");
                    _lastSliderSound = e.Widget;
                }
                else if (e.Action == "step" || e.Widget.EndsWith("Button", StringComparison.Ordinal)
                    || e.Widget.EndsWith("Arrow", StringComparison.Ordinal))
                {
                    Sound("General_Click");
                }
                break;
        }

        if (e.Screen != "editor") _lastSliderSound = null;
    }

    /// <summary>
    /// A command the player is about to send: orders sound as the exe's order dispatcher (1407f9e20), item moves and trades as the inventory
    /// handlers.
    /// </summary>
    internal void SoundForCommand(SimCommand command)
    {
        if (UiSound is null) return;

        switch (command)
        {
            case Simulation.Trading.BuyItem buy: Sound("Sell_Item", item: buy.Item.Record); return;
            case Simulation.Trading.SellItem sell: Sound("Sell_Item", item: sell.Item.Record); return;

            // A right-click move (FUN_140713d20) sounds as a lift, not a drop; money turned into cats in the player's own window is silent.
            case Simulation.Items.QuickMoveItem quick:
                if (quick.Into != CharacterId.None
                    || player.Frontend.GameData?.Find(quick.Item.Record)?.GetInt("item function") != PlayerSystem.MoneyFunction)
                    Sound("Item_Lift", item: quick.Item.Record);
                return;
            case Simulation.Carrying.TakeLootItem take: Sound("Item_Lift", item: take.Item.Record); return;
            case Simulation.Carrying.PutLootItem put: Sound("Item_Lift", item: put.Item.Record); return;
            case Simulation.Production.PutInMachine machine: Sound("Item_Lift", item: machine.Item.Record); return;
            case Simulation.Production.TakeFromMachine: Sound("Item_Lift"); return;

            // The sound the exe posts after giving the selection a job (FUN_1407f5a90).
            case Simulation.Production.AddJob or Simulation.Production.AddOrderJob: Sound("Permanent_Job"); return;
            case Simulation.FirstAid.MedicJobCommand { Job: true } or Simulation.Carrying.RescueJobCommand { Job: true }:
                Sound("Permanent_Job");
                return;
        }

        string name = command.GetType().Name;
        if (name == "DialogueChoice") Sound("Dialogue_Choice", "Dialogue_Choice", "Continue_Conversation");
        // The exe skips the click for a move order given while steering.
        else if (OrderCommands.Contains(name) && !(player.Steering && name == "MoveOrder")) Sound("Right_Click");
    }
}