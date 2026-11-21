using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Player;

/// <summary>
/// Meitou's stand-in for the original's order giver <c>FUN_1407f9e20</c> (and the nearest-character giver <c>FUN_1407fae70</c>) over the interface's
/// order commands (docs/game/ai-tasks.md "Order givers", <b>Verified</b>): the characters each right-click order goes to, and the giver's skip of a
/// player's character that is getting up (Character <c>+0x278</c>, set by Task_GetUp's start <c>FUN_14033ca10</c> and cleared at its end; Meitou
/// <see cref="CharacterFacts.GettingUp"/>), which gets neither the order nor <c>playerWantsMeToGetUp</c>. The job giver <c>FUN_1407f5a90</c> has no
/// such skip, and its orders are not listed here.
/// </summary>
public static class OrderGiver
{
    #region Recipients

    /// <summary>
    /// The characters a right-click order goes to (an empty list: the selection), null for any other command.
    /// </summary>
    public static IReadOnlyList<CharacterId>? Ordered(SimCommand command) => command switch
    {
        MoveOrder o => o.Characters,
        FollowOrder o => o.Characters,
        AttackOrder o => o.Attackers,
        ShootAtOrder o => o.Gunners,
        Carrying.PickUpOrder o => o.Carriers,
        Carrying.PutDownOrder o => o.Carriers,
        Carrying.LootOrder o => o.Looters,
        Carrying.OpenLootOrder o => o.Looters,
        Carrying.StealthKoOrder o => o.Attackers,
        FirstAid.TreatOrder o => o.Medics,
        FirstAid.SplintOrder o => o.Medics,
        Locks.PickLockOrder o => o.Characters,
        Locks.UseBedOrder o => o.Characters,
        Production.OperateOrder o => o.Characters,
        Construction.BuildOrder o => o.Characters,
        // Every other right-click verb that names characters goes through the same dispatcher (FUN_1407f9e20 :96 sets the flag before any
        // order-specific code); only the job giver's orders (45, 0x111, 0xdf) skip it.
        Dialogue.TalkOrder o => [o.Talker],
        Trading.OpenTrade o => [o.Customer],
        Production.UseTurretOrder o => o.Characters,
        FirstAid.RestOrder o => o.Characters,
        Items.OpenContainerOrder o => o.Characters,
        Items.PickUpItemOrder o => o.Characters,
        Locks.DoorOrder o => o.Characters,
        Locks.DoorLockOrder o => o.Characters,
        Locks.ForceLockOrder o => o.Characters,
        Locks.PutShacklesOrder o => o.Characters,
        Locks.ShackleOrder o => o.Characters,
        Locks.SetFreeOrder o => o.Characters,
        Locks.GetInCageOrder o => o.Characters,
        Locks.GetOutOrder o => o.Characters,
        Locks.CageClickOrder { Alt: true } o => o.Characters,
        Locks.PutInBedOrder o => o.Carriers,
        Crime.PutInCageOrder o => o.Carriers,
        Construction.DismantleOrder o => o.Characters,
        Production.ProspectOrder o => [o.Character],
        Production.DisposeCorpseOrder o => o.Characters,
        _ => null,
    };

    /// <summary>
    /// The order with only <paramref name="keep"/> as its characters; null for an order that names one character, which the skip drops.
    /// </summary>
    static SimCommand? With(SimCommand command, IReadOnlyList<CharacterId> keep) => command switch
    {
        MoveOrder o => o with { Characters = keep },
        FollowOrder o => o with { Characters = keep },
        AttackOrder o => o with { Attackers = keep },
        ShootAtOrder o => o with { Gunners = keep },
        Carrying.PickUpOrder o => o with { Carriers = keep },
        Carrying.PutDownOrder o => o with { Carriers = keep },
        Carrying.LootOrder o => o with { Looters = keep },
        Carrying.OpenLootOrder o => o with { Looters = keep },
        Carrying.StealthKoOrder o => o with { Attackers = keep },
        FirstAid.TreatOrder o => o with { Medics = keep },
        FirstAid.SplintOrder o => o with { Medics = keep },
        Locks.PickLockOrder o => o with { Characters = keep },
        Locks.UseBedOrder o => o with { Characters = keep },
        Production.OperateOrder o => o with { Characters = keep },
        Construction.BuildOrder o => o with { Characters = keep },
        FirstAid.RestOrder o => o with { Characters = keep },
        Items.OpenContainerOrder o => o with { Characters = keep },
        Items.PickUpItemOrder o => o with { Characters = keep },
        Locks.DoorOrder o => o with { Characters = keep },
        Locks.DoorLockOrder o => o with { Characters = keep },
        Locks.ForceLockOrder o => o with { Characters = keep },
        Locks.PutShacklesOrder o => o with { Characters = keep },
        Locks.ShackleOrder o => o with { Characters = keep },
        Locks.SetFreeOrder o => o with { Characters = keep },
        Locks.GetInCageOrder o => o with { Characters = keep },
        Locks.GetOutOrder o => o with { Characters = keep },
        Locks.CageClickOrder o => o with { Characters = keep },
        Locks.PutInBedOrder o => o with { Carriers = keep },
        Crime.PutInCageOrder o => o with { Carriers = keep },
        Construction.DismantleOrder o => o with { Characters = keep },
        Production.UseTurretOrder o => o with { Characters = keep },
        Production.DisposeCorpseOrder o => o with { Characters = keep },
        _ => null,
    };

    #endregion

    #region Getting-up skip

    /// <summary>
    /// The host's commands of this tick with the giver's skip applied: each right-click order leaves out the player's characters getting up, and one
    /// left with nobody is dropped. An order naming nobody (the selection) is resolved against the selection only while no selection or squad command
    /// came before it in the batch, which would change the selection its handler reads; after one it is passed on as it is.
    /// </summary>
    public static IReadOnlyList<SimCommand> SkipGettingUp(World world, IReadOnlyList<SimCommand> commands)
    {
        List<SimCommand>? kept = null;
        bool selectionChanged = false;
        for (int k = 0; k < commands.Count; k++)
        {
            var command = commands[k];
            if (command is SelectCommand or SquadCommand) selectionChanged = true;
            var given = Skipped(world, command, selectionChanged, out bool changed);
            if (changed && kept is null) kept = [.. commands.Take(k)];
            if (given is not null) kept?.Add(given);
        }

        return kept ?? commands;
    }

    /// <summary>
    /// One command after the skip (<paramref name="changed"/> when it differs): itself, the order without its characters getting up, or null when the
    /// order is left with nobody.
    /// </summary>
    static SimCommand? Skipped(World world, SimCommand command, bool selectionChanged, out bool changed)
    {
        changed = false;
        if (Ordered(command) is not { } named || named.Count == 0 && selectionChanged) return command;

        var who = named.Count > 0 ? named : world.Player.Selection;
        if (!who.Any(id => GettingUp(world, id))) return command;

        changed = true;
        var keep = who.Where(id => !GettingUp(world, id)).ToArray();
        return keep.Length == 0 ? null : With(command, keep);
    }

    /// <summary>
    /// Whether the character is one of the player's and getting up (the giver's <c>+0x278</c> test, after its faction test <c>+0x250</c>).
    /// </summary>
    static bool GettingUp(World world, CharacterId id)
    {
        var table = world.Characters;
        return table.TryResolveNext(id, out int slot) && table.Cold(slot) is { IsPlayer: true } cold && CharacterFacts.GettingUp(cold, world.Tick);
    }

    #endregion
}
