namespace Meitou.Simulation;

public sealed partial class World
{
    /// <summary>
    /// Applies the interface's commands between ticks while paused, without advancing time or running simulation phases: selection, order toggles, job lists, crafting and
    /// research queues, squads, inventory, open-shop transactions, building settings, the Stop order and every order (<see cref="OrderCommand"/>: each goes into its characters'
    /// queues through the one order path, <see cref="PlayerOrders.Give"/>, and sets their tasks; nobody moves or fights until the next tick).
    /// </summary>
    public void ApplyPausedInterfaceCommands()
    {
        if (Characters.Phase != TablePhase.Outside) throw new InvalidOperationException("interface commands require a tick boundary");
        var due = Commands.TakeDue(Tick, c => c is OrderCommand or StopCommand or SelectCommand or OrdersCommand or RunSpeedCommand or StandUpCommand or SquadCommand or SquadAiCommand
            or SquadFormationCommand or RenameFactionCommand or SeenDiscoveriesCommand
            or Production.AddJob or Production.AddOrderJob or Production.RemoveJob or Production.MoveJob
            or Production.QueueCraft or Production.CancelCraft or Production.RepeatCrafts or Production.QueueResearch or Production.CancelResearch
            or Items.CloseContainer or Locks.BuyBuilding or Construction.UpgradeSite or Construction.DowngradeSite or Construction.SetBuildingPublic or InspectCommand or InspectBuildingCommand or ShowInventoryCommand or Items.HeldCommand or Items.QuickMoveItem or Items.ReadMapItem or Items.ArrangeInventory
            or Carrying.TakeLootItem or Carrying.PutLootItem or Production.PutInMachine or Production.TakeFromMachine
            or Trading.BuyItem or Trading.SellItem or Trading.ArrangeStock or Trading.CloseTrade or Dialogue.DialogueChoice or Dialogue.EndConversation or Dialogue.ConfirmCharacterTrade or Dialogue.CloseCharacterTrade
            or Construction.PlaceBuildings or Construction.CancelSite or Construction.ConfirmDismantle or Construction.PauseSite or Editing.EditCharacter or Editing.FinishCreation
            or Locks.ToggleDoorsOrder or Locks.ToggleDoorLocksOrder);
        if (due.Count == 0) return;
        Characters.BeginTick();
        Characters.Phase = TablePhase.Inputs;
        try
        {
            // The systems that take these commands: every order's owner, and the others' handlers.
            foreach (var system in systems)
                if (system is IOrderOwner or PlayerSystem or Trading.TradeSystem or Dialogue.DialogueSystem or Combat.CombatSystem)
                    system.Inputs(this, due);
            Decisions.Run(this, paused: true);   // addOrder replans at once: the order queues' fronts
        }
        finally
        {
            Characters.Phase = TablePhase.Outside;
            Characters.EndTick();
        }
        foreach (var trade in systems.OfType<Trading.TradeSystem>()) trade.RefreshView(this);
        foreach (var talk in systems.OfType<Dialogue.DialogueSystem>()) talk.RefreshView(this);
        if (Settings.PublishSnapshots)
        {
            var previous = PreviousSnapshot;
            Publish();
            PreviousSnapshot = previous;
        }
    }

    /// <summary>The first system of type <typeparamref name="T"/> this world runs, or null. Remembered per type (the finders ask once per candidate pair; any thread).</summary>
    internal T? SystemOf<T>() where T : class => (T?)systemsByType.GetOrAdd(typeof(T), static (_, list) => list.OfType<T>().FirstOrDefault(), systems);

    readonly System.Collections.Concurrent.ConcurrentDictionary<Type, object?> systemsByType = new();

    /// <summary>
    /// Puts the item on the mouse back where it came from (<see cref="PlayerState.Held"/>, <see cref="PlayerSystem.ReturnHeldItem"/>): a save does this first, so the item is saved in its
    /// window. Only at a tick boundary; nothing when none is held.
    /// </summary>
    public void ReturnHeldItem()
    {
        if (Player.Held is null || Characters.Phase != TablePhase.Outside || SystemOf<PlayerSystem>() is not { } players) return;
        Characters.BeginTick();
        Characters.Phase = TablePhase.Inputs;
        try { players.ReturnHeldItem(this); }
        finally
        {
            Characters.Phase = TablePhase.Outside;
            Characters.EndTick();
        }
    }
}
