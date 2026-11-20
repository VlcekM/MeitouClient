using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Items;

/// <summary>
/// Settings of <see cref="FeedSystem"/>.
/// </summary>
public sealed class FeedSettings
{
    /// <summary>
    /// Ticks between looks at who is hungry. Engine choice: the original looks on every character update (<c>FUN_1405dc650</c>); a bite fills the
    /// stomach for hours, so a second's delay changes nothing that shows.
    /// </summary>
    public int CheckEveryTicks { get; init; } = 30;
}

/// <summary>
/// Eating (docs/game/character-stats.md "Eating", <c>FUN_1405d29c0</c> and <c>FUN_1405d0780</c>, <b>Verified</b> decompile). In the slow-world step,
/// every
/// <see cref="FeedSettings.CheckEveryTicks"/> ticks, each living character (knocked out too) with an inventory looks for a bite:
/// <list type="bullet">
/// <item><b>Who</b>: a character of the player's faction or a slave wants food at any hunger, anyone else below <see cref="NpcWantsBelow"/>; it eats
/// when it wants food, its stomach is empty and its hunger is below <see cref="HungryBelow"/>. An animal does not while the squad option Feed Animals
/// is off. NPCs are kept fed by their free food (<see cref="MedicalContext.HungerFloor"/>), so they rarely get this hungry.</item>
/// <item><b>Where</b>: its own items, then its backpack; failing both, a character of the player's side whose hunger is below
/// <see cref="ShareBelow"/> eats from the backpack of a squad mate
/// within <see cref="ShareReach"/> while the squad option Share Food is on.</item>
/// <item><b>What</b> (<c>FUN_140748d70</c>): first raw food (<c>item function</c> 15) the race lists in <c>special food</c> (an animal any), then
/// ordinary food (3). An opened item first
/// (the more eaten the sooner), else the one with the most nutrition; death items of a living owner are left.</item>
/// <item><b>A bite</b>: min(charges, 50) nutrition comes off the item (gone at 0) and the stomach gets a hundredth of it; "Ate {1}" floats over the
/// character and it makes the eating
/// sound. No animation or delay: the stomach then digests (<see cref="MedicalState.Tick"/>) before the next bite.</item>
/// </list>
/// Serial, in slot order, so it does not depend on threads.
/// </summary>
public sealed class FeedSystem(ItemFactory items, FeedSettings? settings = null) : ITickSystem
{
    /// <summary>
    /// The hunger an NPC (not of the player's faction, not a slave) starts wanting food below (character slot <c>0x340</c>, <c>FUN_1405c9d90</c>,
    /// Verified literal).
    /// </summary>
    public const float NpcWantsBelow = 2.1f;
    /// <summary>
    /// The hunger below which a character that wants food eats, with an empty stomach (<c>FUN_1405c7f70</c>, Verified literal).
    /// </summary>
    public const float HungryBelow = 2.5f;
    /// <summary>
    /// The hunger below which a character of the player's side takes food from a squad mate's backpack (Verified literal).
    /// </summary>
    public const float ShareBelow = 2.25f;
    /// <summary>
    /// How far a squad mate's backpack is shared (200 units: the squared distance 40000, Verified literal).
    /// </summary>
    public const float ShareReach = 200;
    /// <summary>
    /// The most nutrition one bite takes off an item (Verified literal).
    /// </summary>
    public const float BiteSize = 50;
    /// <summary>
    /// The <c>item function</c> of food and of raw food (eaten only by the races that list it in <c>special food</c>, and by animals).
    /// </summary>
    public const int FoodFunction = 3, RawFoodFunction = 15;

    /// <summary>
    /// The settings; the defaults when none were given.
    /// </summary>
    public FeedSettings Settings { get; } = settings ?? new FeedSettings();
    /// <summary>
    /// The item factory the system makes items with.
    /// </summary>
    public ItemFactory Items { get; } = items;

    /// <summary>
    /// Every <see cref="FeedSettings.CheckEveryTicks"/>: each living character with medical state, a race and an inventory eats from its mouth or
    /// eats a hungry bite from its own items, its backpack or a mate's.
    /// </summary>
    public void SlowWorld(World world)
    {
        if (world.Tick % Math.Max(Settings.CheckEveryTicks, 1) != 0) return;
        var table = world.Characters;
        var state = table.Next;
        var options = world.Player.SquadAi;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!state[i].Alive || table.Cold(i) is not { Medical: { Dead: false } med, Race: { } race, Inventory: { } own } cold)
                continue;
            bool animal = !med.HasStatsObject;
            bool playerSide = PlayerSide(world, cold);

            // A limb in the mouth is all it does (FUN_1405dc650): the usual look for food comes only when the mouth is empty, and the Feed Animals
            // option does not matter to it.
            if (cold.Mouth is { } held)
            {
                if (MouthDue(world, cold, med, playerSide)) EatFromMouth(world, i, cold, med, held);
                continue;
            }

            if (animal && (options & SquadAi.Feed) == 0) continue;
            if (!WantsFood(world, cold, med) || med.Fed > 0 || !(med.Hunger < HungryBelow)) continue;
            Inventory? from = own;
            var food = Best(own.Items, race, animal);
            if (food is null && own.Backpack?.Contents is { } bag && Best(bag.Items, race, animal) is { } packed)
                (from, food) = (bag, packed);
            if (food is null && playerSide && med.Hunger < ShareBelow && (options & SquadAi.Share) != 0)
                (from, food) = FromMates(world, i, cold, race, animal);
            if (food is null || from is null) continue;
            Eat(world, i, med, from, food);
        }
    }

    /// <summary>
    /// The hunger at or under which the eating desire wants food (AI slot 25, medical hunger &lt;= 2.4, <b>Verified</b>), and under which it still
    /// does at 0.1 while its allies are threatened (1.2).
    /// </summary>
    public const float FoodOnGroundBelow = 2.4f, FoodOnGroundInFight = 1.2f;
    /// <summary>
    /// How far 259's finder looks (<c>FUN_14099a930</c>: squared distance 10000, <b>Verified</b>).
    /// </summary>
    public const float FoodOnGroundRange = 100;

    /// <summary>
    /// AI slot 25 (<c>FUN_140597d30</c>, <b>Verified</b>), the eating desire of 243 (after slots 26 and 24) and of 259: 0 unless the character is
    /// hungry (<c>FUN_1405c7f70</c>: it wants food, its stomach is empty, hunger below <see cref="HungryBelow"/>) with hunger at most
    /// <see cref="FoodOnGroundBelow"/>; then 0 under a threat to it (AI <c>+0xb0</c>, <see cref="Perception.SenseBlock.ThreatToMe"/>); with its allies
    /// threatened (<c>+0xb4</c>, <see cref="Perception.SenseBlock.ThreatToAllies"/>) 0.1 below hunger <see cref="FoodOnGroundInFight"/>, else 0; with
    /// neither 1.0 while it carries no food (<c>FUN_1403344c0</c>: item types 15 and 3), else 0. Meitou counts its bag's food too
    /// (<see cref="CarriesFood"/>), as state 149 does, so a take that goes into the bag is counted before the bite.
    /// </summary>
    internal static float EatDesire(World world, int slot, CharacterCold c)
    {
        if (c is not { Medical: { Dead: false } med, Race: { } race }) return 0;
        if (!WantsFood(world, c, med) || med.Fed > 0 || !(med.Hunger < HungryBelow) || med.Hunger > FoodOnGroundBelow) return 0;

        var senses = CharacterFacts.Senses(world, slot);
        if (senses is { ThreatToMe: > 0 }) return 0;
        if (senses is { ThreatToAllies: > 0 }) return med.Hunger < FoodOnGroundInFight ? 0.1f : 0;

        return CarriesFood(c, race, !med.HasStatsObject) ? 0 : 1;
    }

    /// <summary>
    /// Whether the character wants food (character slot <c>0x340</c>, <b>Verified</b>): one of the player's side or a slave always, anyone else below
    /// <see cref="NpcWantsBelow"/>.
    /// </summary>
    static bool WantsFood(World world, CharacterCold c, MedicalState med) =>
        PlayerSide(world, c) || c.SlaveState == 1 || med.Hunger < NpcWantsBelow;

    /// <summary>
    /// 259's finder, pass 1 (<c>FUN_14099a930</c>, <b>Verified</b>; the goal's look and act-out are <see cref="Kidnap.CaptiveGoals"/>): the nearest food
    /// item on the ground strictly within <see cref="FoodOnGroundRange"/> (3D) it may eat (function 3, or 15 for an animal), lying where the character
    /// stands (the same building, or both outdoors), not behind a closed gate (<c>FUN_140597160</c>), and passing the ownership test
    /// (<see cref="MayTake"/>); null when none. Pass 2 is <see cref="FoodBody"/>. Serial (the captive look).
    /// </summary>
    internal static GroundItem? GroundFood(World world, int slot, CharacterCold cold)
    {
        var at = world.Characters.Next[slot].Position;
        int place = world.Sight.PlaceOf(slot);
        var locks = world.SystemOf<Locks.LockSystem>();
        GroundItem? best = null;
        float d2Best = FoodOnGroundRange * FoodOnGroundRange;
        foreach (var g in world.GroundItems.All)
        {
            if (!FreeFood(g, cold) || world.Sight.PlaceAt(g.Position) != place) continue;

            float d2 = Vector3.DistanceSquared(at, g.Position);
            if (d2 >= d2Best || locks is not null && !locks.NotBlocked(world, at, g.Position) || !MayTake(world, cold, g, place)) continue;

            (best, d2Best) = (g, d2);
        }

        return best;
    }

    /// <summary>
    /// 259's finder, pass 2 (<c>FUN_14099a930</c>, <b>Verified</b>), asked when pass 1 finds nothing: the first character the looker senses (its sense
    /// entries in slot order; the original's mask 0x10, the dead bit) that is dead, not carried, held in nothing, within
    /// <see cref="FoodOnGroundRange"/> (3D, inclusive), worked by no squad mate (<c>FUN_1402690e0</c>), reachable (<c>FUN_140597160</c>,
    /// <c>FUN_140597030</c>: <see cref="CarrySystem.Reachable"/>) and holding food among its items (function 3, or 15 the looker may eat,
    /// <c>FUN_1407465d0</c>); -1 when none. Serial.
    /// </summary>
    internal static int FoodBody(World world, int slot, CharacterCold cold)
    {
        if (CharacterFacts.Senses(world, slot) is not { } senses) return -1;

        var table = world.Characters;
        var at = table.Next[slot].Position;
        SquadGoals? goals = null;
        foreach (var e in senses.Entries)
        {
            int b = e.Target;
            if (b == slot || !table.Next[b].Alive || table.Next[b].Generation != e.Generation) continue;
            if (table.Cold(b) is not { Medical.Dead: true, CarriedBy.IsNone: true, InSomething: Locks.HeldState.None, Inventory: { } inv }) continue;
            if (Vector3.DistanceSquared(at, table.Next[b].Position) > FoodOnGroundRange * FoodOnGroundRange) continue;

            goals ??= SquadGoals.Of(world);
            if (goals.Working(cold.SquadId, table.IdOf(slot), GoalTarget.Of(table.IdOf(b))) > 0 || !CarrySystem.Reachable(world, slot, b)) continue;
            if (inv.Items.Exists(item => IsFood(item) && Edible(cold, item))) return b;
        }

        return -1;
    }

    /// <summary>
    /// Whether the character may eat <paramref name="item"/> (<c>FUN_14042ee50</c>): food (function 3) always, raw food (15) for an animal or when its
    /// race's <c>special food</c> lists it.
    /// </summary>
    static bool Edible(CharacterCold cold, ItemInstance item) =>
        item.ItemFunction == FoodFunction || cold.Medical is { HasStatsObject: false } || cold.Race?.SpecialFood.Contains(item.Record) == true;

    /// <summary>
    /// The ownership test of 259's ground pass (<c>FUN_140997d80</c>, <b>Verified</b>): an item that has an owner must be of the looker's faction,
    /// but for a player-faction looker on an item outdoors or in a building of the player's faction, which it may take whoever owns it.
    /// </summary>
    static bool MayTake(World world, CharacterCold cold, GroundItem g, int place)
    {
        if (g.OwnedBy < 0 || g.OwnedBy == cold.Faction) return true;
        if (world.Player.Faction < 0 || cold.Faction != world.Player.Faction) return false;
        if (place < 0) return true;

        return world.Buildings.Find(world.Sight.PlaceKey(place)) is { } building && building.Owner == world.Player.Faction;
    }

    /// <summary>
    /// Food the character may take to eat: a loose food item (function 3, or 15 it may eat) in no shop slot.
    /// </summary>
    internal static bool FreeFood(GroundItem g, CharacterCold cold) => IsFood(g.Item) && Edible(cold, g.Item) && g.Slot is null;

    /// <summary>
    /// Food or raw food (<c>item function</c> 3 or 15).
    /// </summary>
    internal static bool IsFood(ItemInstance item) => item.ItemFunction is FoodFunction or RawFoodFunction;

    /// <summary>
    /// The <c>item function</c> of a limb: the one thing an animal picks up into its mouth (<c>FUN_14034f340</c>, item +0x124 = 0x11, Verified).
    /// </summary>
    public const int LimbFunction = 17;

    /// <summary>
    /// Whether a pick-up of <paramref name="item"/> is a mouth pick-up (<c>FUN_14034f340</c>, <b>Verified</b> decompile): a limb picked up by an
    /// animal never goes in its inventory. It is held in the mouth when the animal <see cref="HasMouth"/> (<see cref="HoldInMouth"/>), else it stays
    /// on the ground (<c>FUN_1405d0b50</c>). Anything else goes in the inventory as for anyone.
    /// </summary>
    public static bool IsMouthPickUp(CharacterCold cold, ItemInstance item) => cold.Medical is { HasStatsObject: false }
            && item.ItemFunction == LimbFunction;

    /// <summary>
    /// The name of the attachment point a limb is held at (only the Bonedog and Boneyard Wolf races' <c>attachment points</c> file has it).
    /// </summary>
    public const string MouthPoint = "mouth";

    /// <summary>
    /// Whether the character's look has the <see cref="MouthPoint"/> attachment point (<c>FUN_1405d0b50</c> looks it up by name); false without a
    /// look.
    /// </summary>
    public static bool HasMouth(CharacterCold cold) => cold.Appearance?.AttachmentPoints.ContainsKey(MouthPoint) == true;

    /// <summary>
    /// Takes the limb into the mouth (a <see cref="IsMouthPickUp"/> by an animal that <see cref="HasMouth"/>); the caller has taken it off the
    /// ground. A limb held before is let go at its feet; the new one is eaten later (<see cref="MouthDue"/>). <paramref name="tone"/> is the skin
    /// tone of the character it was cut from (<see cref="FallSource.SkinTone"/>), kept for its drawing at the animal's <c>mouth</c> point. Engine
    /// choice: a held limb is not saved.
    /// </summary>
    public static void HoldInMouth(World world, int slot, CharacterCold cold, ItemInstance item, Vector3 tone = default)
    {
        if (cold.Mouth is { } before)
            world.GroundItems.Drop(before, world.Characters.Next[slot].Position, world.Characters.Next[slot].Yaw);
        cold.Mouth = item;
        cold.MouthSinceTick = world.Tick;
        cold.MouthTone = tone;
    }

    /// <summary>
    /// Whether the limb in the mouth is eaten now (<c>FUN_1405dc650</c>, <b>Verified</b>; minutes are game minutes since it was taken, f the animal's
    /// live age <see cref="CharacterCold.Age"/>). An adult (f above 0.9) of the player's side eats it at once when its stomach is empty and its
    /// hunger below 2.6, else after 15 minutes; any other animal after 45 minutes with an empty stomach, else after 60 + 120 x (1 - f) minutes.
    /// </summary>
    static bool MouthDue(World world, CharacterCold cold, MedicalState med, bool playerSide)
    {
        double minutes = (world.Tick - cold.MouthSinceTick) * BodySystem.HoursPerTick * 60;
        float f = cold.Age;
        if (f > 0.9f && playerSide) return med.Fed <= 0 && med.Hunger < 2.6f || minutes > 15;
        return med.Fed <= 0 && minutes > 45 || minutes > (1 - f) * 120 + 60;
    }

    /// <summary>
    /// The mouth is emptied and the limb bitten (<c>FUN_1405d0d20</c>): eaten whole, for 30.
    /// </summary>
    static void EatFromMouth(World world, int slot, CharacterCold cold, MedicalState med, ItemInstance limb)
    {
        cold.Mouth = null;
        Eat(world, slot, med, null, limb);
    }

    /// <summary>
    /// The player's side for eating (<c>FUN_140856bc0</c>): a character of the player's faction (the original also counts one an AI of the player's
    /// owns, not modelled).
    /// </summary>
    static bool PlayerSide(World world, CharacterCold cold) => cold.IsPlayer || world.Player.Faction >= 0 && cold.Faction == world.Player.Faction;

    /// <summary>
    /// A squad mate's backpack food within <see cref="ShareReach"/> the character would bite, the first mate in squad order; nulls when none.
    /// </summary>
    (Inventory? From, ItemInstance? Food) FromMates(World world, int slot, CharacterCold cold, RaceData race, bool animal)
    {
        if (cold.SquadId < 0 || world.Squads.Find(cold.SquadId) is not { } squad) return (null, null);
        var table = world.Characters;
        var here = table.Next[slot].Position;
        foreach (var m in squad.Members)
        {
            if (!table.TryResolveNext(m, out int s) || table.Cold(s) is not { Inventory.Backpack.Contents: { } bag })
                continue;
            if (Vector3.DistanceSquared(table.Next[s].Position, here) > ShareReach * ShareReach) continue;
            if (Best(bag.Items, race, animal, ownerDead: table.Cold(s)!.Medical?.Dead == true) is { } food)
                return (bag, food);
        }
        return (null, null);
    }

    /// <summary>
    /// Whether a character holds food it would bite in its own items or its worn bag, the two places the eating rule bites from (the "no food on
    /// it" test of the 243 food run): the item a food run puts in the bag counts at once, so no second run starts before the bite (the original
    /// takes one item per meal, <b>Observed</b>, docs/game/ai-tasks.md "Perma-jobs").
    /// </summary>
    internal static bool CarriesFood(CharacterCold cold, RaceData race, bool animal) => cold.Inventory is { } own
            && (Best(own.Items, race, animal) is not null || own.Backpack?.Contents is { } bag && Best(bag.Items, race, animal) is not null);

    /// <summary>
    /// The bite of <c>FUN_140748d70</c>: raw food the race eats first, then food.
    /// </summary>
    static ItemInstance? Best(List<ItemInstance> items, RaceData race, bool animal, bool ownerDead = false) =>
        Best(items, RawFoodFunction, r => animal || race.SpecialFood.Contains(r), ownerDead) ?? Best(items, FoodFunction, null, ownerDead);

    /// <summary>
    /// The item of <paramref name="function"/> with the highest score: an opened item (charges below its full) scores (2 - charges / full) x 1000, a
    /// full one its nutrition (full / 100). Ties keep the first. A death item counts only when the inventory's owner is dead (FUN_140748d70,
    /// FUN_1407465d0).
    /// </summary>
    internal static ItemInstance? Best(List<ItemInstance> items, int function, Func<string, bool>? eats, bool ownerDead = false)
    {
        ItemInstance? best = null;
        float top = -1;
        foreach (var item in items)
        {
            if (item.ItemFunction != function || item.Quantity <= 0 || item.Death && !ownerDead || eats is not null && !eats(item.Record))
                continue;
            float full = item.MaxCharges;
            float score = item.Charges == full ? full * 0.01f : full > 0 ? (1 - item.Charges / full + 1) * 1000 : 0;
            if (score > 0 && score > top) (best, top) = (item, score);
        }
        return best;
    }

    /// <summary>
    /// One bite (<c>FUN_1405d0780</c>): min(charges, 300 - hunger, 50) comes off the item (a limb, <c>item function</c> 17, is eaten whole for 30),
    /// which goes when nothing is left; the stomach gets a hundredth of it. With more than one unit in the stack the next unit starts full (engine
    /// choice: food does not stack in the data). Observers hear the bite (<see cref="ItemUseKind.Eaten"/>).
    /// </summary>
    static void Eat(World world, int slot, MedicalState med, Inventory? from, ItemInstance item)
    {
        bool limb = item.ItemFunction == 17;
        float amount = limb ? 30 : item.Charges;
        amount = MathF.Min(MathF.Min(amount, 300 - med.Hunger), BiteSize);
        item.Charges -= amount;
        bool finished = item.Charges <= 0 || limb;
        if (finished)
        {
            bool more = item.Quantity > 1;
            from?.RemoveOne(item);
            if (more) item.Charges = item.MaxCharges;
        }

        med.Fed = amount * 0.01f;
        var at = world.Characters.Next[slot].Position;
        world.Floaters?.Enqueue(Floater.Text(world.Characters.IdOf(slot), at, Loc.Tr("Ate {1}", item.Name)));
        world.EmitSound(SoundKind.Eat, slot, at);
        ItemUseObservers.Report(world, ItemUseKind.Eaten, item.Record, world.Characters.IdOf(slot), null, amount, finished);
    }
}