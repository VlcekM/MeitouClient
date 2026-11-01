using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Carrying;

// NPC looting: goal 177 "Looting the dead" and its step Task_Loot_AI (docs/game/carry-loot.md "NPC looting", docs/simulation.md "Carrying, rescue and looting").
public sealed partial class CarrySystem
{
    /// <summary>How near a body must be for the finder (<b>Observed</b>: squared distance 10000, <c>FUN_14099ae10</c>).</summary>
    public const float ScavengeRange = 100;
    /// <summary>Seconds at the body before the take (<b>Observed</b>, <c>Task_Loot_AI</c> update): 8, an animal 20; a weapon grab at 0.8.</summary>
    public const float NpcLootSeconds = 8, NpcLootAnimalSeconds = 20, NpcWeaponGrabSeconds = 0.8f;
    /// <summary>A looter whose hunger floor is below this takes only food (<b>Observed</b>, the start's slot 0x348 test).</summary>
    public const float FoodOnlyFloor = 2.8f;

    /// <summary>
    /// Each tick (serial, slot order), every <see cref="CarrySettings.AutoEveryTicks"/> ticks per character on its slot's turn (slot mod the period, so the looks spread over the ticks): each member of an NPC squad whose running package lists goal 177 for its role in the hour's window
    /// (<see cref="PopulationSystem.GoalDuty"/>) and that the AI runs now (<see cref="Ai.AiSystem.Holds"/>) walks to the body <see cref="LootTarget"/> picks and loots it
    /// (<see cref="CharacterTask.Loot"/>, <see cref="NpcLoot"/>).
    /// </summary>
    void Scavenge(World world, CharacterTable table)
    {
        if (world.SystemOf<PopulationSystem>() is not { } population) return;
        var memory = world.SystemOf<DialogueSystem>()?.Memory;
        int every = Settings.AutoEveryTicks, phase = (int)(world.Tick % every);
        HashSet<int>? taken = null;
        List<int>? bodies = null;
        Span<int> duty = stackalloc int[5];
        foreach (var squad in world.Squads.All)
        {
            if (squad.Members.Count == 0) continue;
            duty.Fill(-1);
            foreach (var member in squad.Members)
            {
                if (!table.TryResolveNext(member, out int slot) || slot % every != phase || table.Cold(slot) is not { IsPlayer: false, Inventory: not null } cold || !Ai.AiSystem.Holds(table.Next[slot], cold)) continue;
                int role = (int)Ai.AiSystem.RoleOf(squad, member, cold);
                if (duty[role] < 0) duty[role] = population.GoalDuty(world, squad, (AiRole)role, TaskType.LOOT_THE_DEAD) ? 1 : 0;
                if (duty[role] == 0) continue;
                taken ??= Looting(table);
                bodies ??= LootableBodies(table);
                if (!TaskStart.Eligible(world, cold, TaskType.LOOT_THE_DEAD)) continue;
                int target = LootTarget(world, table, slot, cold, taken, bodies, memory);
                if (target < 0) continue;
                taken.Add(target);
                var task = (CharacterTask)table.Next[slot].Task;
                Start(world, slot, CharacterTask.Loot, TaskType.LOOT_THE_DEAD, TaskSource.Npc, table.IdOf(target), TaskStart.Standing(cold, task));
            }
        }
    }

    /// <summary>
    /// Goal 177's finder (<c>FUN_14099ae10(ai, 0)</c>): within <see cref="ScavengeRange"/>, not carried, not looted by another and not tagged <see cref="MemoryTag.Looted"/> by the looter's squad;
    /// first the dead, then the downed living of factions neither its own nor allied (the kidnap finder's masks), not of the player's faction unless <see cref="PlayerState.BanditsLoot"/>,
    /// whom <see cref="ScrewsOver"/> allows. Engine choices: the nearest of each pass (the original takes its sensed-list order); a dead body of the looter's own faction is left. -1: none.
    /// </summary>
    int LootTarget(World world, CharacterTable table, int slot, CharacterCold cold, HashSet<int> taken, List<int> bodies, SquadMemory? memory)
    {
        var next = table.Next;
        var at = new Vector2(next[slot].Position.X, next[slot].Position.Z);
        int dead = -1, down = -1;
        float dead2 = ScavengeRange * ScavengeRange, down2 = dead2;
        foreach (int s in bodies)
        {
            if (s == slot || !next[s].Alive || taken.Contains(s) || table.Cold(s) is not { } body || !CanBeLooted(body) || !body.CarriedBy.IsNone || body.IsImprisoned) continue;
            float d2 = Vector2.DistanceSquared(at, new Vector2(next[s].Position.X, next[s].Position.Z));
            if (d2 > down2 && d2 > dead2) continue;
            if (memory is not null && memory.HasShort(world, cold.SquadId, table.IdOf(s), MemoryTag.Looted)) continue;
            if (body.Medical!.Dead)
            {
                if (body.Faction != cold.Faction && d2 <= dead2 && Reachable(world, slot, s)) (dead, dead2) = (s, d2);
                continue;
            }
            if (d2 > down2 || PlayerSide(world, body) && !world.Player.BanditsLoot || !Reachable(world, slot, s) || !ScrewsOver(world, slot, cold, s, body, memory)) continue;
            (down, down2) = (s, d2);
        }
        return dead >= 0 ? dead : down;
    }

    /// <summary>The lootable characters (carried ones too: a looter that starts may drop its load), in slot order: what <see cref="LootTarget"/> tests, collected once a pass.</summary>
    static List<int> LootableBodies(CharacterTable table)
    {
        var bodies = new List<int>();
        for (int s = 0; s < table.HighWater; s++)
            if (table.Next[s].Alive && table.Cold(s) is { } body && CanBeLooted(body)) bodies.Add(s);
        return bodies;
    }

    /// <summary>
    /// The finder's two per-body tests, both passes (<c>FUN_14099ae10</c>, <b>Verified</b>): <c>FUN_140597160</c> (DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS: the gate codes of the two match, or
    /// the route between them is open, <see cref="Locks.LockSystem.NotBlocked"/>) and <c>FUN_140597030</c> (<c>AI::isDestinationAccessible</c>: a body in a building the looter is not in
    /// needs that building to have an open door, <c>Building::hasAnOpenDoor</c>; a human looter's "may use it" test, slot 0x328, is always true). Not modelled: the building-link branch of the first,
    /// the animal looter's slot 0x328, a body in a part building with a parent.
    /// </summary>
    static bool Reachable(World world, int looter, int body)
    {
        var next = world.Characters.Next;
        if (world.SystemOf<Locks.LockSystem>() is { } locks && !locks.NotBlocked(world, next[looter].Position, next[body].Position)) return false;
        return world.Sight.BuildingOf(body) is not { } building || world.Sight.BuildingOf(looter) == building || world.Fixtures.HasOpenDoor(building);
    }

    static bool PlayerSide(World world, CharacterCold c) => c.IsPlayer || world.Player.Faction >= 0 && c.Faction == world.Player.Faction;

    /// <summary>
    /// <c>Character::shouldIScrewThisGuyOver</c> (<c>FUN_14059a7b0</c>, <b>Verified</b>), the only test of a downed body in the loot finder (no friendliness filter before it): the dialogue
    /// system's DC 54 (personality, slave owner; docs/game/dialogue.md). Without it: yes when either is a bandit; no for the same class; no when friendly; yes when the looter's
    /// slave owner is the target's faction; no when its squad loves the target, yes when it hates it; yes when the target is dead.
    /// </summary>
    bool ScrewsOver(World world, int looterSlot, CharacterCold looter, int targetSlot, CharacterCold target, SquadMemory? memory)
    {
        if (world.SystemOf<Dialogue.DialogueSystem>() is { } dialogue) return dialogue.ScrewOver(world, looterSlot, targetSlot);
        var targetId = world.Characters.IdOf(targetSlot);
        bool yes = looter.NpcClass == FundamentalType.Bandit || target.NpcClass == FundamentalType.Bandit;
        if (looter.NpcClass == target.NpcClass) yes = false;
        if (Friendly(looter, target)) yes = false;
        if (looter.SlaveOwner >= 0 && looter.SlaveOwner == target.Faction) yes = true;
        if (memory?.Find(world, looter.SquadId, targetId) is { } known)
        {
            if ((known.Long & MemoryTag.LoveMask) != 0) yes = false;
            if ((known.Long & MemoryTag.HateMask) != 0) yes = true;
        }
        if (target.Medical is { Dead: true }) yes = true;
        return yes;
    }

    /// <summary>
    /// An NPC at the body it loots (<c>Task_Loot_AI</c>, <b>Observed</b>): kneels at the medic clip while <see cref="CharacterCold.LiftTime"/> counts (from its arrival). Past
    /// <see cref="NpcWeaponGrabSeconds"/>, a looter without a weapon that takes more than food from a living player-side body takes one weapon of its hip and back and is done; else past
    /// <see cref="NpcLootSeconds"/> (an animal: <see cref="NpcLootAnimalSeconds"/>) it takes (<see cref="NpcTake"/>). Then its squad tags the body <see cref="MemoryTag.Looted"/> and the task ends.
    /// What it takes follows <see cref="CharacterCold.LootFlags"/>, fixed when the task starts (the update only reads them, <b>Verified</b> <c>FUN_14034d930</c>).
    /// </summary>
    void NpcLoot(World world, CharacterTable table, int i, int t, float dt)
    {
        var cold = table.Cold(i)!;
        var victim = table.Cold(t)!;
        if (cold.LiftTime < 0)
        {
            cold.LiftTime = 0;
            var start = cold.LootFlags = LootStartFlags(world, cold, victim);
            Crimes?.NpcLooting(world, i, t, (start & (LootStart.NotPlayerSide | LootStart.FoodOnly)) != 0, (start & LootStart.WantsWeapon) != 0);
            if ((start & LootStart.Animal) != 0 && PlayerSide(world, victim)) world.Post($"{cold.Name} is butchering {victim.Name} for meat!!");
        }
        var flags = cold.LootFlags;
        bool animal = (flags & LootStart.Animal) != 0, police = (flags & LootStart.Police) != 0, notPlayerSide = (flags & LootStart.NotPlayerSide) != 0, foodOnly = (flags & LootStart.FoodOnly) != 0;
        cold.LiftTime += dt;
        int death = DeathUnits(victim.Inventory!);
        int moved;
        if (police && !animal)
        {
            if (cold.LiftTime <= ConfiscateSeconds) return;
            moved = Confiscate(cold, victim);
            Looted(world, table, i, t, moved, 0, crime: false);
            // Tagged only when it found nothing to take.
            if (moved == 0 && cold.SquadId >= 0) world.SystemOf<DialogueSystem>()?.Memory.Flag(world, cold.SquadId, table.IdOf(t), MemoryTag.Looted);
            End(world, i);
            return;
        }
        if (!foodOnly && !notPlayerSide && !animal && cold.LiftTime > NpcWeaponGrabSeconds && (flags & LootStart.WantsWeapon) != 0)
            moved = Take(cold, victim, victim.Inventory!.Items.Where(x => x.Type == FcsRecordType.WEAPON && x.Section is "hip" or "back").Take(1).ToList());
        else if (cold.LiftTime > (animal ? NpcLootAnimalSeconds : NpcLootSeconds)) moved = NpcTake(cold, victim, notPlayerSide && !foodOnly);
        else return;
        Butcher(world, i, t, death);
        Looted(world, table, i, t, moved, 0, crime: false);   // the crime was set at the start (CrimeSystem.NpcLooting)
        if (!police && cold.SquadId >= 0) world.SystemOf<DialogueSystem>()?.Memory.Flag(world, cold.SquadId, table.IdOf(t), MemoryTag.Looted);
        End(world, i);
    }

    /// <summary>
    /// The flags the loot task fixes at its start (<c>FUN_140355ad0</c>): the victim an animal; not on the player's side or dead; a Bandit or malnourished looter (food only); a looter without a weapon
    /// facing a victim with one; a law-enforcement looter confiscates and then has neither of the middle two. The original also clears "not player side" from a value of the looter's senses (<b>Unknown</b>, not modelled).
    /// </summary>
    LootStart LootStartFlags(World world, CharacterCold looter, CharacterCold victim)
    {
        var flags = LootStart.None;
        if (victim.Fighter?.IsAnimal ?? victim.Appearance?.Animal is not null) flags |= LootStart.Animal;
        if (!HasWeapon(looter) && HasWeapon(victim)) flags |= LootStart.WantsWeapon;
        if (Crimes?.IsPoliceLike(world, looter) == true) return flags | LootStart.Police;
        if (!PlayerSide(world, victim) || victim.Medical!.Dead) flags |= LootStart.NotPlayerSide;
        if (looter.NpcClass == FundamentalType.Bandit || looter.Medical is { } own && world.SystemOf<BodySystem>() is { } bodies && bodies.HungerFloor(world, looter, own) < FoodOnlyFloor) flags |= LootStart.FoodOnly;
        return flags;
    }

    /// <summary>Seconds at the body before a law-enforcement looter confiscates (<b>Observed</b>, <c>Task_Loot_AI</c> update: the 0.8 s branch).</summary>
    public const float ConfiscateSeconds = 0.8f;

    /// <summary>
    /// A law-enforcement looter's take (<c>Task_Loot_AI</c> update with the confiscate flag, <c>Inventory::getAllStolenItems(inventory, false)</c>; <b>Observed</b>): one unit of each item of
    /// the victim's own inventory (worn gear included, not what is inside its backpack) that is stolen for it, in list order, into the looter's inventory; the first that does not fit
    /// stays and ends the take. Returns the items moved.
    /// </summary>
    public int Confiscate(CharacterCold looter, CharacterCold victim)
    {
        var from = victim.Inventory!;
        var into = looter.Inventory!;
        int moved = 0;
        bool worn = false, wear = false;
        foreach (var item in from.Items.Where(x => x.IsStolenFor(victim.Faction, Data)).ToList())
        {
            if (item.LockedOn || !InventoryCommands.IsMovableSection(item.Section)) continue;
            bool gear = InventoryCommands.IsEquipmentSection(item.Section);
            if (InventoryCommands.MoveInto(from, item, 1, into, bagFirst: false, out bool equipped, looter) == 0) break;
            moved++;
            worn |= gear;
            wear |= equipped;
        }
        if (worn) Assembly?.RefreshEquipment(victim);
        if (wear) Assembly?.RefreshEquipment(looter);
        return moved;
    }

    /// <summary>
    /// The take (<c>FUN_14034d930</c> / <c>FUN_140342de0</c>): the body's weapons when the looter has none equipped, each only while a weapon section of the looter is empty (the original
    /// also from an imprisoned body, which the finder never picks here); its food (<c>item function</c> 3, then 15),
    /// then its backpack's food; with <paramref name="everything"/> also every item and armour of it and every item of its backpack. Returns the items moved.
    /// </summary>
    int NpcTake(CharacterCold looter, CharacterCold victim, bool everything)
    {
        var from = victim.Inventory!;
        var bag = from.Backpack?.Contents;
        var items = new List<ItemInstance>();
        int moved = 0;
        // One weapon at a time, each only while one of the looter's weapon sections is still empty (FUN_140342de0 mode 2; its police and imprisoned exceptions never reach here).
        if (!WeaponEquipped(looter))
            foreach (var weapon in from.Items.Where(x => x.Type == FcsRecordType.WEAPON).ToList())
                if (WeaponSectionFree(looter)) moved += Take(looter, victim, [weapon]);
        foreach (int function in (ReadOnlySpan<int>)[FeedSystem.FoodFunction, FeedSystem.RawFoodFunction])
            items.AddRange(from.Items.Where(x => x.Type == FcsRecordType.ITEM && x.ItemFunction == function));
        if (bag is not null) items.AddRange(bag.Items.Where(x => x.Type == FcsRecordType.ITEM && x.ItemFunction == FeedSystem.FoodFunction));
        if (everything)
        {
            items.AddRange(from.Items.Where(x => x.Type is FcsRecordType.ITEM or FcsRecordType.ARMOUR && !items.Contains(x)));
            if (bag is not null) items.AddRange(bag.Items.Where(x => x.Type == FcsRecordType.ITEM && !items.Contains(x)));
        }
        return moved + Take(looter, victim, items);
    }

    /// <summary>One of the looter's weapon sections (back, hip; not those its race closes) holds nothing.</summary>
    static bool WeaponSectionFree(CharacterCold looter) =>
        looter.Inventory is { } inv && WeaponSections.Any(s => !InventoryCommands.SectionClosed(looter, s) && !inv.Items.Any(i => i.Section == s));

    static readonly string[] WeaponSections = ["back", "hip"];

    /// <summary>Moves <paramref name="items"/> of the body (its inventory or backpack) to the looter: its inventory (equipping where a slot is free), else its backpack; what fits nowhere stays.</summary>
    int Take(CharacterCold looter, CharacterCold victim, List<ItemInstance> items)
    {
        var from = victim.Inventory!;
        var into = looter.Inventory!;
        int moved = 0;
        bool worn = false, wear = false;
        foreach (var item in items)
        {
            if (item.LockedOn || !InventoryCommands.IsMovableSection(item.Section)) continue;
            var owner = from.Items.Contains(item) ? from : from.Backpack?.Contents;
            if (owner is null || !owner.Items.Contains(item)) continue;
            bool gear = InventoryCommands.IsEquipmentSection(item.Section);
            int units = InventoryCommands.MoveInto(owner, item, 0, into, bagFirst: false, out bool equipped, looter);
            bool alsoWorn = false;
            if (owner.Items.Contains(item)) units += InventoryCommands.MoveInto(owner, item, 0, into, bagFirst: true, out alsoWorn, looter);
            if (units == 0) continue;
            moved++;
            worn |= gear;
            wear |= equipped || alsoWorn;
        }
        if (worn) Assembly?.RefreshEquipment(victim);
        if (wear) Assembly?.RefreshEquipment(looter);
        return moved;
    }

    /// <summary><c>AI::hasWeapon</c> (engine reading): an animal (its natural weapon), or any weapon in the inventory or backpack.</summary>
    static bool HasWeapon(CharacterCold c) => c.Fighter is { IsAnimal: true } || c.Inventory?.All().Any(x => x.Type == FcsRecordType.WEAPON) == true;

    /// <summary><c>Inventory::hasWeaponEquipped</c>: a weapon in a worn section (hip or back).</summary>
    static bool WeaponEquipped(CharacterCold c) => c.Inventory?.Items.Any(x => x.Type == FcsRecordType.WEAPON && InventoryCommands.IsEquipmentSection(x.Section)) == true;
}
