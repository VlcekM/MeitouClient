using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;

namespace Meitou.Lua;

/// <summary>
/// Applies one <see cref="LuaWorldAction"/> to a live <see cref="World"/> on the simulation tick.
/// </summary>
internal static class LuaActionApplier
{
    /// <summary>
    /// The most items one <c>item.give</c> action creates.
    /// </summary>
    const int MaxGiveCount = 20;

    /// <summary>
    /// The most items one <c>item.remove</c> action removes.
    /// </summary>
    const int MaxRemoveCount = 50;

    /// <summary>
    /// Applies <paramref name="act"/>; an action of an unknown kind, or without the target it needs, does nothing.
    /// </summary>
    public static void Apply(World world, LuaWorldAction act, long tick)
    {
        switch (act.Kind)
        {
            case "door.set_open" when !string.IsNullOrEmpty(act.TargetId):
                SetDoorsOpen(world, act, tick);
                break;
            case "door.set_locked" when !string.IsNullOrEmpty(act.TargetId):
                SetDoorsLocked(world, act, tick);
                break;
            case "world.floater" when !string.IsNullOrEmpty(act.Text):
                world.Floaters?.Enqueue(Floater.Label(new Vector3((float)act.X, (float)act.Y, (float)act.Z), act.Text));
                break;
            case "item.give" when !string.IsNullOrEmpty(act.TargetId):
                GiveItems(world, act);
                break;
            case "item.remove" when !string.IsNullOrEmpty(act.TargetId):
                RemoveItems(world, act);
                break;
            case "medical.amputate":
                Amputate(world, act);
                break;
            case "medical.set_blood":
                SetBlood(world, act);
                break;
            case "medical.add_fed":
                AddFed(world, act);
                break;
        }
    }

    /// <summary>
    /// The cold state of the character the action targets; null when it names none in the world.
    /// </summary>
    static CharacterCold? TargetCold(World world, LuaWorldAction act)
    {
        CharacterId cid = new(act.TargetSlot, act.TargetGen);
        return world.Characters.TryResolveNext(cid, out int slot) ? world.Characters.Cold(slot) : null;
    }

    /// <summary>
    /// Toggles the building's doors when any working door is not yet in the wanted open state.
    /// </summary>
    static void SetDoorsOpen(World world, LuaWorldAction act, long tick)
    {
        bool anyNeedsToggle = false;
        foreach (Fixture f in world.Fixtures.OfBuilding(act.TargetId!))
        {
            if (f.Kind != FixtureKind.Door || f.DoorBroken) continue;

            bool isOpen = f.Motion is DoorMotion.Open or DoorMotion.Opening || f.OpenAmount > 0.5f;
            if (isOpen != act.Flag)
            {
                anyNeedsToggle = true;
                break;
            }
        }

        if (anyNeedsToggle) world.Commands.Enqueue(new ToggleDoorsOrder(act.TargetId!) { Tick = tick });
    }

    /// <summary>
    /// Toggles the building's door locks when any working door is not yet in the wanted locked state.
    /// </summary>
    static void SetDoorsLocked(World world, LuaWorldAction act, long tick)
    {
        bool anyNeedsToggle = false;
        foreach (Fixture f in world.Fixtures.OfBuilding(act.TargetId!))
        {
            if (f.Kind != FixtureKind.Door || f.DoorBroken) continue;

            bool isLocked = f.Lock?.EffectivelyLocked ?? false;
            if (isLocked != act.Flag)
            {
                anyNeedsToggle = true;
                break;
            }
        }

        if (anyNeedsToggle) world.Commands.Enqueue(new ToggleDoorLocksOrder(act.TargetId!) { Tick = tick });
    }

    /// <summary>
    /// Creates up to <see cref="MaxGiveCount"/> items of the action's record in the target's inventory, stopping at the first that does not fit.
    /// </summary>
    static void GiveItems(World world, LuaWorldAction act)
    {
        if (TargetCold(world, act)?.Inventory is not { } inv) return;
        if (world.Fixtures.Items is not { } factory) return;

        int count = Math.Clamp(act.Amount <= 0 ? 1 : act.Amount, 1, MaxGiveCount);
        int lvl = Math.Clamp(act.Level, 0, 100);
        for (int i = 0; i < count; i++)
        {
            ItemInstance? item = factory.Create(act.TargetId!, lvl, 1f);
            if (item is null || !inv.Add(item, true)) break;
        }
    }

    /// <summary>
    /// Removes up to <see cref="MaxRemoveCount"/> items whose record or name is the action's id from the target's inventory.
    /// </summary>
    static void RemoveItems(World world, LuaWorldAction act)
    {
        if (TargetCold(world, act)?.Inventory is not { } inv) return;

        int left = Math.Clamp(act.Amount <= 0 ? 1 : act.Amount, 1, MaxRemoveCount);
        while (left > 0)
        {
            ItemInstance? match = FindItem(inv, act.TargetId!);
            if (match is null || !inv.RemoveOne(match)) break;

            left--;
        }
    }

    /// <summary>
    /// The first item of <paramref name="inv"/> whose record or name is <paramref name="id"/> (ignoring case); null when there is none.
    /// </summary>
    static ItemInstance? FindItem(Inventory inv, string id)
    {
        foreach (ItemInstance it in inv.Items)
        {
            bool named = string.Equals(it.Record, id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(it.Name, id, StringComparison.OrdinalIgnoreCase);
            if (named) return it;
        }

        return null;
    }

    /// <summary>
    /// Turns the limb the action's amount picks (0 left arm, 1 right arm, 2 left leg, 3 right leg) into a stump.
    /// </summary>
    static void Amputate(World world, LuaWorldAction act)
    {
        if (TargetCold(world, act)?.Medical is not { } med) return;

        string want = act.Amount switch { 0 => "left arm", 1 => "right arm", 2 => "left leg", 3 => "right leg", _ => "" };
        foreach (HealthPart pt in med.Parts)
        {
            string pid = pt.Template?.StringId ?? "";
            if (want.Length == 0 || !pid.Contains(want, StringComparison.OrdinalIgnoreCase)) continue;

            pt.Limb = LimbState.Stump;
            pt.Flesh = Math.Min(pt.Flesh, -pt.MaxHp);
            break;
        }
    }

    /// <summary>
    /// Sets the target's blood to the action's X, clamped to -200..200; a non-finite X does nothing.
    /// </summary>
    static void SetBlood(World world, LuaWorldAction act)
    {
        if (TargetCold(world, act)?.Medical is not { } med) return;
        if (!double.IsFinite(act.X)) return;

        med.Blood = (float)Math.Clamp(act.X, -200, 200);
    }

    /// <summary>
    /// Adds the action's X (clamped to -10..10) to the target's fed level, keeping it within 0..100; a non-finite X does nothing.
    /// </summary>
    static void AddFed(World world, LuaWorldAction act)
    {
        if (TargetCold(world, act)?.Medical is not { } med) return;
        if (!double.IsFinite(act.X)) return;

        med.Fed = Math.Clamp(med.Fed + (float)Math.Clamp(act.X, -10, 10), 0, 100);
    }
}
