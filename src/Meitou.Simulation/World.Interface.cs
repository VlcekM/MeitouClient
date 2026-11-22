using Meitou.Simulation.Factions;
using Meitou.Simulation.Player;

namespace Meitou.Simulation;

/// <summary>
/// The world's interface steps between ticks: the paused commands, the system lookup and the held item.
/// </summary>
public sealed partial class World
{
    /// <summary>
    /// The first system of each type (see <see cref="SystemOf"/>).
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<Type, object?> _systemsByType = new();

    /// <summary>
    /// The character under the pointer when the last snapshot was published (<see cref="PlayerState.Pointed"/>).
    /// </summary>
    CharacterId _publishedPointed;

    /// <summary>
    /// <see cref="PlayerState.AllSelectedGrids"/> when the last snapshot was published.
    /// </summary>
    bool _publishedAllGrids;

    /// <summary>
    /// The shown machines the last machine views were made for (<see cref="PlayerState.ShownMachines"/>; null: none made yet).
    /// </summary>
    IReadOnlyList<string>? _publishedShownMachines;

    /// <summary>
    /// Applies the interface's commands between ticks while paused, without advancing time or running simulation phases:
    /// selection, order toggles, job lists, crafting and research queues, squads, inventory, open-shop transactions, building settings,
    /// the Stop order and every order (<see cref="OrderCommand"/>: each goes into its characters' queues through the one order path,
    /// <see cref="PlayerOrders.Give"/>, and sets their tasks; nobody moves or fights until the next tick).
    /// </summary>
    public void ApplyPausedInterfaceCommands()
    {
        if (Characters.Phase != TablePhase.Outside)
            throw new InvalidOperationException("interface commands require a tick boundary");

        var due = Commands.TakeDue(Tick, c => c is OrderCommand or StopCommand or SelectCommand or OrdersCommand or RunSpeedCommand
            or StandUpCommand or ManoeuvreCommand or SquadCommand or SquadAiCommand
            or SquadFormationCommand or RenameFactionCommand or SeenDiscoveriesCommand
            or Production.AddJob or Production.AddOrderJob or Production.RemoveJob or Production.MoveJob
            or Production.QueueCraft or Production.CancelCraft or Production.RepeatCrafts or Production.QueueResearch or Production.CancelResearch
            or Items.CloseContainer or Locks.BuyBuilding or Construction.UpgradeSite or Construction.DowngradeSite or Construction.SetBuildingPublic
            or InspectCommand or InspectBuildingCommand or ShowInventoryCommand or Items.HeldCommand or Items.QuickMoveItem or Items.ReadMapItem
            or Items.ArrangeInventory or Items.RotateItem
            or Carrying.TakeLootItem or Carrying.PutLootItem or Production.PutInMachine or Production.TakeFromMachine or Production.ArrangeMachine
            or Trading.BuyItem or Trading.SellItem or Trading.ArrangeStock or Trading.CloseTrade or Dialogue.DialogueChoice
            or Dialogue.EndConversation or Dialogue.ConfirmCharacterTrade or Dialogue.CloseCharacterTrade
            or Construction.PlaceBuildings or Construction.CancelSite or Construction.ConfirmDismantle or Construction.PauseSite
            or Editing.EditCharacter or Editing.FinishCreation or Editing.InPlayEditorOpened
            or Locks.ToggleDoorsOrder or Locks.ToggleDoorLocksOrder or DebugGiveItem or DebugPlaceItem or DebugAnimalAge);
        due = Simulation.Player.OrderGiver.SkipGettingUp(this, due);
        if (due.Count == 0)
        {
            // The pointer moved onto another character, the selection's grids are wanted or no longer, or a machine window opened or closed:
            // publish again, as what its menu offers with a chance (stealth KO, kidnap) is published only for the character pointed at, selected or
            // inspected (#878), and a storage's items only for a shown machine.
            bool shownChanged = !ReferenceEquals(Player.ShownMachines, _publishedShownMachines);
            bool gridsChanged = Player.AllSelectedGrids != _publishedAllGrids;
            if (Player.Pointed != _publishedPointed || gridsChanged || shownChanged) PublishPaused();
            return;
        }

        Characters.BeginTick();
        Characters.Phase = TablePhase.Inputs;
        try
        {
            // The systems that take these commands: every order's owner, and the others' handlers.
            for (int k = 0; k < _systems.Count; k++)
            {
                var system = _systems[k];
                bool takesCommands = system is IOrderOwner or PlayerSystem or Trading.TradeSystem or Dialogue.DialogueSystem or Combat.CombatSystem;
                if (!takesCommands) continue;

                if (ModFaults.IsMod(k))
                    ModFaults.Run(k, "inputs", system, this, due, static (s, w, d) => s.Inputs(w, d));
                else
                    system.Inputs(this, due);
            }

            Decisions.Run(this, paused: true);   // addOrder replans at once: the order queues' fronts
        }
        finally
        {
            Characters.Phase = TablePhase.Outside;
            Characters.EndTick();
        }

        Events.EndTick(this);
        ActionLog?.Observe(this);
        foreach (var trade in _systems.OfType<Trading.TradeSystem>()) trade.RefreshView(this);
        foreach (var talk in _systems.OfType<Dialogue.DialogueSystem>()) talk.RefreshView(this);
        PublishPaused();
    }

    /// <summary>
    /// The machines' views for a publish, the shown machines' storages with their items (<see cref="PlayerState.ShownMachines"/>, remembered for
    /// the paused publish).
    /// </summary>
    IReadOnlyList<Production.MachineSnapshot> MachineViews()
    {
        var shown = _publishedShownMachines = Player.ShownMachines;
        return _production is null ? [] : Machines.Published(_production.Items, shown);
    }

    /// <summary>
    /// Publishes the snapshot between ticks while paused, keeping the previous one the renderer interpolates from.
    /// </summary>
    void PublishPaused()
    {
        if (!Settings.PublishSnapshots) return;

        var (previous, buffer) = (PreviousSnapshot, _previousBuffer);
        Publish();
        KeepPrevious(previous, buffer);
    }

    /// <summary>
    /// The first system of type <typeparamref name="T"/> this world runs, or null. Remembered per type (the finders ask once per candidate pair; any
    /// thread).
    /// </summary>
    internal T? SystemOf<T>() where T : class =>
        (T?)_systemsByType.GetOrAdd(typeof(T), static (_, list) => list.OfType<T>().FirstOrDefault(), _systems);

    /// <summary>
    /// Closes every inventory window, as the original's close-all (<c>FUN_1406e5740</c>, after a town alarm against the player's or an item move
    /// caught stealing, selling stolen goods or smuggling; docs/game/economy.md "Trading range"): the open container, loot and trade now, the
    /// interface's windows when it sees <see cref="PlayerState.InventoriesClosed"/> change. A walk to open one goes on. Serial phases only.
    /// </summary>
    internal void CloseInventories()
    {
        Player.OpenContainer = null;
        Player.OpenLoot = CharacterId.None;
        SystemOf<Trading.TradeSystem>()?.EndTrade(this);
        Player.InventoriesClosed++;
    }

    /// <summary>
    /// Puts the item on the mouse back where it came from (<see cref="PlayerState.Held"/>, <see cref="PlayerSystem.ReturnHeldItem"/>):
    /// a save does this first, so the item is saved in its window. Only at a tick boundary; nothing when none is held.
    /// </summary>
    public void ReturnHeldItem()
    {
        if (Player.Held is null || Characters.Phase != TablePhase.Outside || SystemOf<PlayerSystem>() is not { } players) return;

        Characters.BeginTick();
        Characters.Phase = TablePhase.Inputs;
        try
        {
            players.ReturnHeldItem(this);
        }
        finally
        {
            Characters.Phase = TablePhase.Outside;
            Characters.EndTick();
        }
    }
}
