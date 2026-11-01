using System.Globalization;
using Meitou.Audio;
using Meitou.Simulation;
using Meitou.Ui;

namespace Meitou.Game;

/// <summary>
/// The interface's sounds (docs/audio.md "Interface"). The original has no central click hook either: every handler posts its own sound, so this maps Meitou's actions to the same events
/// (<see cref="ExecuteUiAction"/>, <see cref="FaithfulAction"/>, the commands <see cref="Say"/> sends) and watches the windows open and close once a frame (<see cref="PollUiSounds"/>).
/// The host plays what <see cref="UiSound"/> receives; nothing here changes the game.
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>Where interface sounds go (the host's audio); null without audio.</summary>
    public Action<UiSoundEvent>? UiSound { get; set; }

    void Sound(string name, string? group = null, string? value = null, bool state = false, string? item = null, string? building = null) =>
        UiSound?.Invoke(new UiSoundEvent(name, group, value, state, item, building));

    readonly HashSet<string> soundScreens = [];
    int soundOverviewTab = -1;
    string? soundHover;
    object? soundWorld;
    double soundSpeed = 1;
    bool speedSounded;
    int soundUnlocked = -1;
    long soundBuildingsRevision = -1;
    readonly Dictionary<string, bool> soundBuildings = new(StringComparer.Ordinal);

    /// <summary>Once a frame: windows opening and closing, the title buttons under the pointer, buildings finishing.</summary>
    void PollUiSounds()
    {
        if (UiSound is null) return;
        var open = faithfulScreens.OpenScreens;
        foreach (string id in soundScreens.ToArray())
        {
            if (open.Contains(id)) continue;
            soundScreens.Remove(id);
            switch (IsPinnedInventory(id) ? "inventory" : id)
            {
                case "inventory" or "bag" or "limbs" or "trader" or "building" or "bench" or "storage": Sound("Inventory_Close"); break;
                case "stats": Sound("Stats_Close"); break;
                case "overview": if (soundOverviewTab == 0) Sound("Map_Close"); else if (soundOverviewTab == 2) Sound("Tech_Close"); soundOverviewTab = -1; break;
                case "menu": Sound("General_Click"); break;
            }
        }
        foreach (string id in open)
        {
            if (!soundScreens.Add(id)) continue;
            switch (IsPinnedInventory(id) ? "inventory" : id)
            {
                case "inventory" or "bag" or "limbs" or "trader" or "building" or "bench" or "storage": Sound("Inventory_Open"); break;
                case "stats": Sound("Stats_Open"); break;
                case "build": Sound("Building_Button"); break;
                case "menu": Sound("General_Click"); break;
                case "overview": soundOverviewTab = -1; break;
            }
        }
        if (soundScreens.Contains("overview"))
        {
            // The overview's tabs (14049bf90): the map and research tabs have their own sounds, the others are tabs.
            int tab = faithfulScreens.GetTab("overview", "TabsMain");
            if (tab != soundOverviewTab)
            {
                soundOverviewTab = tab;
                Sound(tab == 0 ? "Map_Open" : tab == 2 ? "Tech_Open" : "Tab");
            }
        }

        // The title buttons: a sound when the pointer enters one (the title screen's constructor binds only these seven).
        string? hover = null;
        if (faithfulScreens.IsOpen("title") && faithfulScreens.HitTest(mouse) is { Screen: "title", Control: { Type: "Button", Enabled: true } button }) hover = button.Name;
        if (hover != soundHover)
        {
            soundHover = hover;
            if (hover is not null) Sound("Hover");
        }

        // The speed keys (F2 to F4) change the clock without a button: the same sound when the speed moves to a new running one (the buttons did it already).
        double speedNow = session.Simulation.Speed;
        if (speedNow > 0 && speedNow != soundSpeed && !speedSounded && (speedNow == 1 || speedNow == 2 || speedNow == 5))
        {
            string name = speedNow == 1 ? "Normal" : speedNow == 2 ? "x2" : "x4";
            Sound("Game_Speed_" + name, "Game_Speed", name, state: true);
        }
        if (speedNow > 0) soundSpeed = speedNow;
        speedSounded = false;

        // A research finished (also one that unlocks nothing) or a blueprint read: "Research complete" (140834550). The first look only takes the baseline.
        if (!ReferenceEquals(soundWorld, session.World)) soundUnlocked = -1;
        int unlocked = session.CurrentSnapshot.UnlockedBuildings.Count + session.CurrentSnapshot.Research.Finished;
        if (soundUnlocked >= 0 && unlocked > soundUnlocked) Sound("Notifications", "Notifications", "Thing_Complete");
        soundUnlocked = unlocked;

        // A building of the player's finishing: the notification (140559110). The first look only takes the baseline.
        var snapshot = session.CurrentSnapshot;
        if (!ReferenceEquals(soundWorld, session.World)) { soundWorld = session.World; soundBuildingsRevision = -1; soundBuildings.Clear(); soundUnlocked = -1; }
        if (snapshot.BuildingsRevision != soundBuildingsRevision || soundBuildings.Count != snapshot.Buildings.Count)
        {
            bool baseline = soundBuildingsRevision < 0;
            soundBuildingsRevision = snapshot.BuildingsRevision;
            bool finished = false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var site in snapshot.Buildings)
            {
                seen.Add(site.Id);
                if (!baseline && site.Complete && soundBuildings.TryGetValue(site.Id, out bool was) && !was) finished = true;
                soundBuildings[site.Id] = site.Complete;
            }
            foreach (string id in soundBuildings.Keys.Where(id => !seen.Contains(id)).ToArray()) soundBuildings.Remove(id);
            if (finished) Sound("Notifications", "Notifications", "Building_Complete");
        }
    }

    /// <summary>A HUD action, before it runs (so a toggle's state is still the old one).</summary>
    void SoundForAction(string action, string argument)
    {
        if (UiSound is null) return;
        if (action.StartsWith("speed:", StringComparison.Ordinal))
        {
            // Game_Speed is a state; the exe sets it and posts for 1, 2 and 5 only (140787e40).
            int index = int.Parse(action[6..], CultureInfo.InvariantCulture);
            if (index > 0 && ButtonSpeeds[index] is var speed && (speed == 1 || speed == 2 || speed == 5))
            {
                string name = speed == 1 ? "Normal" : speed == 2 ? "x2" : "x4";
                Sound("Game_Speed_" + name, "Game_Speed", name, state: true);
                speedSounded = true;
            }
        }
        else if (action is "floor-up" or "floor-down") Sound("Change_Level");
        else if (action == "extendedstats") Sound("Stats_Preview", "Stats_Preview", infoExpanded ? "Stats_Down" : "Stats_Up");
        else if (action is "member" or "job-remove" || action.StartsWith("runspeed.", StringComparison.Ordinal)) Sound("General_Click");
        else if (action.StartsWith("order.", StringComparison.Ordinal) && Enum.TryParse(action[6..], out CharacterOrders order))
        {
            // The orders panel (1407214e0 and neighbours): Block, Hold, Passive and Ranged make Blocks_Only on and off; Sneak makes Stealth on and a click off.
            if (order is CharacterOrders.Block or CharacterOrders.Hold or CharacterOrders.Passive or CharacterOrders.Ranged) Sound("Blocks_Only");
            else if (order == CharacterOrders.Taunt) Sound("Taunt");
            else if (order == CharacterOrders.Sneak) Sound(Selected().FirstOrDefault() is { } lead && lead.Orders.HasFlag(CharacterOrders.Sneak) ? "General_Click" : "Stealth");
            else if (order == CharacterOrders.Jobs) Sound("General_Click");
        }
    }

    /// <summary>A button, tab or list row of one of the original's screens, before it runs.</summary>
    void SoundForScreen(MyGuiEvent e)
    {
        if (UiSound is null || e.Action.StartsWith("inventory-scroll", StringComparison.Ordinal)) return;
        switch (e.Screen)
        {
            case "title" or "menu" or "new-game" or "save" or "load" or "message" or "file": Sound("General_Click"); break;
            case "inventory" or "bag" or "trader": if (e.Widget == "ArrangeButton") Sound("General_Click"); break;
            // A pick in the research type list (FUN_14049b5e0): Research_Tabs is set to the item's text, the category; the same row again is no change.
            case "overview": if (e is { Widget: "ResearchTypeList", Action: "select" } && e.Text != researchCategory) Sound("Research_Tab", "Research_Tabs", e.Text); break;
            case "build":
                if (e.Widget is "CategoriesList" or "BuildingsList" or "BuldingTypePrevButton" or "BuldingTypeNextButton") Sound("Building_Navigate");
                break;
            case "editor":
                if (e.Widget == "ConfirmButton") Sound("Create_Character");
                else if (e.Widget is "BodyTabButton" or "FaceTabButton" or "HairTabButton") Sound("Tab");
                // Slider: the first event of each drag (a row's bar, named by its row). A list row's arrows click.
                else if (e.Action == "scroll") { if (e.Widget != lastSliderSound) Sound("Slider"); lastSliderSound = e.Widget; }
                else if (e.Action == "step" || e.Widget.EndsWith("Button", StringComparison.Ordinal) || e.Widget.EndsWith("Arrow", StringComparison.Ordinal)) Sound("General_Click");
                break;
        }
        if (e.Screen != "editor") lastSliderSound = null;
    }

    string? lastSliderSound;

    /// <summary>The player's orders (the commands that give a character a job), by type name: they make the order dispatcher's click.</summary>
    static readonly HashSet<string> OrderCommands = new(StringComparer.Ordinal)
    {
        "MoveOrder", "AttackOrder", "BuildOrder", "DismantleOrder", "GetOutOrder", "LootOrder", "OpenLootOrder", "PayBailOrder", "PickLockOrder", "PickUpOrder", "PutDownOrder", "PutInCageOrder", "PutShacklesOrder",
        "OperateOrder", "PutInBedOrder", "RestOrder", "SplintOrder", "StealthKoOrder", "TalkOrder", "TreatOrder", "UseBedOrder",
    };

    /// <summary>A command the player is about to send: orders sound as the exe's order dispatcher (1407f9e20), item moves and trades as the inventory handlers.</summary>
    void SoundForCommand(SimCommand command)
    {
        if (UiSound is null) return;
        switch (command)
        {
            case Meitou.Simulation.Trading.BuyItem buy: Sound("Sell_Item", item: buy.Item.Record); return;
            case Meitou.Simulation.Trading.SellItem sell: Sound("Sell_Item", item: sell.Item.Record); return;
            // A right-click move (FUN_140713d20) sounds as a lift, not a drop; money turned into cats in the player's own window is silent.
            case Meitou.Simulation.Items.QuickMoveItem quick:
                if (quick.Into != CharacterId.None || gameData?.Find(quick.Item.Record)?.GetInt("item function") != PlayerSystem.MoneyFunction) Sound("Item_Lift", item: quick.Item.Record);
                return;
            case Meitou.Simulation.Carrying.TakeLootItem take: Sound("Item_Lift", item: take.Item.Record); return;
            case Meitou.Simulation.Carrying.PutLootItem put: Sound("Item_Lift", item: put.Item.Record); return;
            case Meitou.Simulation.Production.PutInMachine machine: Sound("Item_Lift", item: machine.Item.Record); return;
            case Meitou.Simulation.Production.TakeFromMachine: Sound("Item_Lift"); return;
            // The sound the exe posts after giving the selection a job (FUN_1407f5a90).
            case Meitou.Simulation.Production.AddJob or Meitou.Simulation.Production.AddOrderJob: Sound("Permanent_Job"); return;
            case Meitou.Simulation.FirstAid.MedicJobCommand { Job: true } or Meitou.Simulation.Carrying.RescueJobCommand { Job: true }: Sound("Permanent_Job"); return;
        }
        string name = command.GetType().Name;
        if (name == "DialogueChoice") Sound("Dialogue_Choice", "Dialogue_Choice", "Continue_Conversation");
        // The exe skips the click for a move order given while steering.
        else if (OrderCommands.Contains(name) && !(steering && name == "MoveOrder")) Sound("Right_Click");
    }
}
