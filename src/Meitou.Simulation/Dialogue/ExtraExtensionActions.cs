using Meitou.Data.Gameplay.Dialogue;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// BFrizz's Extra Extensions' item actions (docs/game/dialogue.md "Extra Extensions item actions"): <c>take item</c>, <c>take item from squad</c>,
/// <c>destroy item</c> and <c>destroy item from squad</c>, run by its <c>_doActions</c> hook ahead of every other effect of the line.
/// </summary>
sealed class ExtraExtensionActions(PopulationData data)
{
    /// <summary>
    /// Makes a giver's fighter again when a worn piece leaves its inventory.
    /// </summary>
    readonly CharacterAssembly _assembly = new(data);

    /// <summary>
    /// The line's item actions in the hook's order, with <paramref name="me"/> the dialogue's owner (the taker) and <paramref name="target"/> the
    /// conversation's target (the giver); nothing without a target.
    /// </summary>
    public void Run(World world, DialogueNode line, CharacterId me, CharacterId target)
    {
        foreach (var action in line.ItemActions) Run(world, action, me, target);
    }

    /// <summary>
    /// One action. The <c>from squad</c> ones repeat the plain one once per member of the target's squad nearer than 900 (to the speaker for a
    /// take, to the target for a destroy), each time on the target's own inventory, never the member's, until the count is reached.
    /// </summary>
    void Run(World world, ItemAction action, CharacterId me, CharacterId target)
    {
        if (DialogueSystem.Cold(world, target) is not { Inventory: not null } giver) return;
        var taker = DialogueSystem.Cold(world, me);
        bool canTake = taker?.Inventory is not null;

        switch (action.Kind)
        {
            case ItemActionKind.Take when canTake:
                Take(world, giver, me, taker!, action.Item, action.Count);
                break;
            case ItemActionKind.TakeFromSquad when canTake:
                Repeat(ExtraExtensionChecks.SquadNear(world, target, me), action.Count, left => Take(world, giver, me, taker!, action.Item, left));
                break;
            case ItemActionKind.Destroy:
                Destroy(giver, action.Item, action.Count);
                break;
            case ItemActionKind.DestroyFromSquad:
                Repeat(ExtraExtensionChecks.SquadNear(world, target, target), action.Count, left => Destroy(giver, action.Item, left));
                break;
        }
    }

    /// <summary>
    /// The plugin's squad loop: <paramref name="step"/> once per member, its result taken off what is left, stopping when nothing is. No squad: no
    /// step (engine choice: every character of the original has one).
    /// </summary>
    static void Repeat(List<CharacterId>? members, int count, Func<int, int> step)
    {
        int left = count;
        foreach (var _ in members ?? [])
        {
            left -= step(left);
            if (left == 0) break;
        }
    }

    /// <summary>
    /// <c>takeItems</c>: up to <paramref name="count"/> units of <paramref name="record"/> from the giver's own item list (worn gear included, not
    /// what its backpack holds), first stack first: a bigger stack is split, a smaller one moves whole. Each part goes into the taker's inventory,
    /// else to the ground at its feet. Returns the units taken when the giver runs out, else <paramref name="count"/>.
    /// </summary>
    int Take(World world, CharacterCold giver, CharacterId takerId, CharacterCold taker, string record, int count)
    {
        var from = giver.Inventory!;
        int taken = 0;
        bool worn = false;
        while (taken < count)
        {
            if (from.Items.Find(i => i.Record == record) is not { } item)
            {
                Refit(giver, worn);
                return taken;
            }

            int left = count - taken;
            if (item.Quantity > left)
            {
                item.Quantity -= left;
                Give(world, takerId, taker, item.WithQuantity(left));
                break;
            }

            taken += item.Quantity;
            worn |= InventoryCommands.IsEquipmentSection(item.Section);
            from.Remove(item);
            Give(world, takerId, taker, item);
        }
        Refit(giver, worn);
        return count;
    }

    /// <summary>
    /// <c>destroyItems</c>: units of <paramref name="record"/> taken off the target's own item list, first stack first, and destroyed. A bigger stack
    /// loses what is left and the call returns that; when the target runs out it returns what was left to destroy (the plugin's two meanings).
    /// </summary>
    int Destroy(CharacterCold target, string record, int count)
    {
        var from = target.Inventory!;
        bool worn = false;
        while (from.Items.Find(i => i.Record == record) is { } item)
        {
            if (item.Quantity > count)
            {
                item.Quantity -= count;
                break;
            }

            count -= item.Quantity;
            worn |= InventoryCommands.IsEquipmentSection(item.Section);
            from.Remove(item);
        }
        Refit(target, worn);
        return count;
    }

    /// <summary>
    /// Puts <paramref name="item"/> into the taker's inventory (<c>giveItem(item, true, false)</c>); what does not fit drops at its feet.
    /// </summary>
    static void Give(World world, CharacterId takerId, CharacterCold taker, ItemInstance item)
    {
        if (taker.Inventory!.Add(item)) return;
        if (DialogueSystem.Position(world, takerId) is { } at) world.GroundItems.Drop(item, at, 0);
    }

    /// <summary>
    /// Makes the character's fighter again from its inventory when a worn piece left it.
    /// </summary>
    void Refit(CharacterCold c, bool worn)
    {
        if (worn) _assembly.RefreshEquipment(c);
    }
}
