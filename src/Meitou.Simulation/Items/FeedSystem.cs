using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Items;

/// <summary>Settings of <see cref="FeedSystem"/>.</summary>
public sealed class FeedSettings
{
    /// <summary>
    /// Ticks between looks at who is hungry. Engine choice: the original looks on every character update (<c>FUN_1405dc650</c>); a bite fills the stomach for hours, so a second's
    /// delay changes nothing that shows.
    /// </summary>
    public int CheckEveryTicks { get; init; } = 30;
}

/// <summary>
/// Eating (docs/game/character-stats.md "Eating", <c>FUN_1405d29c0</c> and <c>FUN_1405d0780</c>, <b>Verified</b> decompile). In the slow-world step, every
/// <see cref="FeedSettings.CheckEveryTicks"/> ticks, each living character (knocked out too) with an inventory looks for a bite:
/// <list type="bullet">
/// <item><b>Who</b>: a character of the player's faction or a slave wants food at any hunger, anyone else below <see cref="NpcWantsBelow"/>; it eats when it wants food, its stomach is
/// empty and its hunger is below <see cref="HungryBelow"/>. An animal does not while the squad option Feed Animals is off. NPCs are kept fed by their free food
/// (<see cref="MedicalContext.HungerFloor"/>), so they rarely get this hungry.</item>
/// <item><b>Where</b>: its own items, then its backpack; failing both, a character of the player's side whose hunger is below <see cref="ShareBelow"/> eats from the backpack of a squad mate
/// within <see cref="ShareReach"/> while the squad option Share Food is on.</item>
/// <item><b>What</b> (<c>FUN_140748d70</c>): first raw food (<c>item function</c> 15) the race lists in <c>special food</c> (an animal any), then ordinary food (3). An opened item first
/// (the more eaten the sooner), else the one with the most nutrition; death items of a living owner are left.</item>
/// <item><b>A bite</b>: min(charges, 50) nutrition comes off the item (gone at 0) and the stomach gets a hundredth of it; "Ate {1}" floats over the character and it makes the eating
/// sound. No animation or delay: the stomach then digests (<see cref="MedicalState.Tick"/>) before the next bite.</item>
/// </list>
/// Serial, in slot order, so it does not depend on threads.
/// </summary>
public sealed class FeedSystem(ItemFactory items, FeedSettings? settings = null) : ITickSystem, IDecisionOwner
{
    /// <summary>The hunger an NPC (not of the player's faction, not a slave) starts wanting food below (character slot <c>0x340</c>, <c>FUN_1405c9d90</c>, Verified literal).</summary>
    public const float NpcWantsBelow = 2.1f;
    /// <summary>The hunger below which a character that wants food eats, with an empty stomach (<c>FUN_1405c7f70</c>, Verified literal).</summary>
    public const float HungryBelow = 2.5f;
    /// <summary>The hunger below which a character of the player's side takes food from a squad mate's backpack (Verified literal).</summary>
    public const float ShareBelow = 2.25f;
    /// <summary>How far a squad mate's backpack is shared (200 units: the squared distance 40000, Verified literal).</summary>
    public const float ShareReach = 200;
    /// <summary>The most nutrition one bite takes off an item (Verified literal).</summary>
    public const float BiteSize = 50;
    /// <summary>The <c>item function</c> of food and of raw food (eaten only by the races that list it in <c>special food</c>, and by animals).</summary>
    public const int FoodFunction = 3, RawFoodFunction = 15;

    public FeedSettings Settings { get; } = settings ?? new FeedSettings();
    public ItemFactory Items { get; } = items;

    public void SlowWorld(World world)
    {
        if (world.Tick % Math.Max(Settings.CheckEveryTicks, 1) != 0) return;
        var table = world.Characters;
        var state = table.Next;
        var options = world.Player.SquadAi;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!state[i].Alive || table.Cold(i) is not { Medical: { Dead: false } med, Race: { } race, Inventory: { } own } cold) continue;
            bool animal = !med.HasStatsObject;
            bool playerSide = PlayerSide(world, cold);
            // A limb in the mouth is all it does (FUN_1405dc650): the usual look for food comes only when the mouth is empty, and the Feed Animals option does not matter to it.
            if (cold.Mouth is { } held)
            {
                if (MouthDue(world, cold, med, playerSide)) EatFromMouth(world, i, cold, med, held);
                continue;
            }
            // Goal 259 Picking up food (every player animal has it, Character::setupAI): hungry with no food on it, it fetches food from the ground. A player character's is the
            // decision step's (PickingUpFood); this pass runs it for the other animals of the player's faction, idle or following.
            if (animal && playerSide && !cold.IsPlayer && WantsGroundFood(cold, race, med, own) && world.Characters.Next[i].Task is (byte)CharacterTask.Idle or (byte)CharacterTask.Follow
                && GroundFood(world, i, cold) is { } ground)
                FetchFood(world, i, cold, ground);
            if (animal && (options & SquadAi.Feed) == 0) continue;
            bool wants = playerSide || cold.SlaveState == 1 || med.Hunger < NpcWantsBelow;
            if (!wants || med.Fed > 0 || !(med.Hunger < HungryBelow)) continue;
            Inventory? from = own;
            var food = Best(own.Items, race, animal);
            if (food is null && own.Backpack?.Contents is { } bag && Best(bag.Items, race, animal) is { } packed) (from, food) = (bag, packed);
            if (food is null && playerSide && med.Hunger < ShareBelow && (options & SquadAi.Share) != 0) (from, food) = FromMates(world, i, cold, race, animal);
            if (food is null || from is null) continue;
            Eat(world, i, med, from, food);
        }
    }

    /// <summary>The hunger at or under which a player animal looks for food on the ground (259's desire, medical hunger <= 2.4, <b>Verified</b>), and under which it does so in a fight (1.2).</summary>
    public const float FoodOnGroundBelow = 2.4f, FoodOnGroundInFight = 1.2f;
    /// <summary>How far it looks (<c>FUN_14099a930</c>: squared distance 10000, <b>Verified</b>).</summary>
    public const float FoodOnGroundRange = 100;

    /// <summary>
    /// 259 EAT_FOOD_ON_GROUND's desire (<c>FUN_14099a930</c>, docs/game/ai-tasks.md; priority 3, weight 0.5): an animal (no stats object), stomach empty, hunger at most
    /// <see cref="FoodOnGroundBelow"/>, no limb in its mouth and no food it would bite on it, not already picking something up, not fighting unless starving (the 0.1 desire), awake and not carried.
    /// </summary>
    static bool WantsGroundFood(CharacterCold cold, RaceData race, MedicalState med, Inventory own) =>
        !med.HasStatsObject && med.Fed <= 0 && med.Hunger <= FoodOnGroundBelow && cold.Mouth is null && cold.PickingUpItem is null && Best(own.Items, race, true) is null
        && !(cold.InCombat && med.Hunger >= FoodOnGroundInFight) && cold.CarriedBy.IsNone && !med.Incapacitated;

    /// <summary>
    /// 259's finder (<c>FUN_14099a930</c>): the nearest food item on the ground within <see cref="FoodOnGroundRange"/> it may eat (function 3, or 15 for an animal), nobody else's (engine
    /// reading of the stealing check); null when none. Engine choices: the original's pass 2 (a corpse with food) and same-location test are not done.
    /// </summary>
    static GroundItem? GroundFood(World world, int slot, CharacterCold cold)
    {
        var at = world.Characters.Next[slot].Position;
        GroundItem? best = null;
        float d2best = FoodOnGroundRange * FoodOnGroundRange;
        foreach (var g in world.GroundItems.All)
        {
            if (g.Item.ItemFunction is not (FoodFunction or RawFoodFunction) || g.Slot is not null || g.OwnedBy >= 0 && g.OwnedBy != cold.Faction) continue;
            float d2 = Vector3.DistanceSquared(at, g.Position);
            if (d2 < d2best) (best, d2best) = (g, d2);
        }
        return best;
    }

    /// <summary>It walks to the food and picks it up; it eats it on a later look.</summary>
    static void FetchFood(World world, int slot, CharacterCold cold, GroundItem food)
    {
        cold.PickingUpItem = food.Id;
        if (Vector3.Distance(world.Characters.Next[slot].Position, food.Position) > PlayerSystem.PickUpReach)
            TaskStart.Walk(world, slot, food.Position, SpeedMode.Run, Meitou.Data.Gameplay.Ai.TaskType.EAT_FOOD_ON_GROUND, TaskSource.Goal);
    }

    public void RegisterSources(DecisionSystem decisions) => decisions.Register(new PickingUpFood());

    /// <summary>259 Picking up food of a player character's animal (priority 3, weight 0.5): it runs while it picks the food up.</summary>
    sealed class PickingUpFood : IDecisionSource
    {
        public string Name => "259 Picking up food";
        public DecisionStep Step => DecisionStep.Goals;
        public int Key => 259;
        public float Weight => 0.5f;

        public DecisionCheck CanRun(World world, int slot)
        {
            var cold = world.Characters.Cold(slot)!;
            if (cold is not { Medical: { Dead: false } med, Race: { } race, Inventory: { } own } || med.HasStatsObject) return DecisionCheck.No("not an animal");
            if (cold.PickingUpItem is not null)
                return cold.Running is { Row: Meitou.Data.Gameplay.Ai.TaskType.EAT_FOOD_ON_GROUND } ? DecisionCheck.Runs : DecisionCheck.No("picking up an item");
            if (!WantsGroundFood(cold, race, med, own)) return DecisionCheck.No("desire 0 (not hungry, food on it, or fighting)");
            return GroundFood(world, slot, cold) is not null ? DecisionCheck.Run : DecisionCheck.No("no food on the ground");
        }

        public void Start(World world, int slot)
        {
            var cold = world.Characters.Cold(slot)!;
            if (GroundFood(world, slot, cold) is { } food) FetchFood(world, slot, cold, food);
        }
    }

    /// <summary>The <c>item function</c> of a limb: the one thing an animal picks up into its mouth (<c>FUN_14034f340</c>, item +0x124 = 0x11, Verified).</summary>
    public const int LimbFunction = 17;

    /// <summary>
    /// Whether a pick-up of <paramref name="item"/> is a mouth pick-up (<c>FUN_14034f340</c>, <b>Verified</b> decompile): a limb picked up by an animal never goes in its inventory. It is held
    /// in the mouth when the animal <see cref="HasMouth"/> (<see cref="HoldInMouth"/>), else it stays on the ground (<c>FUN_1405d0b50</c>). Anything else goes in the inventory as for anyone.
    /// </summary>
    public static bool IsMouthPickUp(CharacterCold cold, ItemInstance item) => cold.Medical is { HasStatsObject: false } && item.ItemFunction == LimbFunction;

    /// <summary>The name of the attachment point a limb is held at (only the Bonedog and Boneyard Wolf races' <c>attachment points</c> file has it).</summary>
    public const string MouthPoint = "mouth";

    /// <summary>Whether the character's look has the <see cref="MouthPoint"/> attachment point (<c>FUN_1405d0b50</c> looks it up by name); false without a look.</summary>
    public static bool HasMouth(CharacterCold cold) => cold.Appearance?.AttachmentPoints.ContainsKey(MouthPoint) == true;

    /// <summary>
    /// Takes the limb into the mouth (a <see cref="IsMouthPickUp"/> by an animal that <see cref="HasMouth"/>); the caller has taken it off the ground. A limb held before is let go at its
    /// feet; the new one is eaten later (<see cref="MouthDue"/>). <paramref name="tone"/> is the skin tone of the character it was cut from (<see cref="FallSource.SkinTone"/>), kept for
    /// its drawing at the animal's <c>mouth</c> point. Engine choice: a held limb is not saved.
    /// </summary>
    public static void HoldInMouth(World world, int slot, CharacterCold cold, ItemInstance item, Vector3 tone = default)
    {
        if (cold.Mouth is { } before) world.GroundItems.Drop(before, world.Characters.Next[slot].Position, world.Characters.Next[slot].Yaw);
        cold.Mouth = item;
        cold.MouthSinceTick = world.Tick;
        cold.MouthTone = tone;
    }

    /// <summary>
    /// Whether the limb in the mouth is eaten now (<c>FUN_1405dc650</c>, <b>Verified</b>; minutes are game minutes since it was taken, f the animal's live age <see cref="CharacterCold.Age"/>).
    /// An adult (f above 0.9) of the player's side eats it at once when its stomach is empty and its hunger below 2.6, else after 15 minutes; any other animal after 45 minutes with an empty
    /// stomach, else after 60 + 120 x (1 - f) minutes.
    /// </summary>
    static bool MouthDue(World world, CharacterCold cold, MedicalState med, bool playerSide)
    {
        double minutes = (world.Tick - cold.MouthSinceTick) * BodySystem.HoursPerTick * 60;
        float f = cold.Age;
        if (f > 0.9f && playerSide) return med.Fed <= 0 && med.Hunger < 2.6f || minutes > 15;
        return med.Fed <= 0 && minutes > 45 || minutes > (1 - f) * 120 + 60;
    }

    /// <summary>The mouth is emptied and the limb bitten (<c>FUN_1405d0d20</c>): eaten whole, for 30.</summary>
    static void EatFromMouth(World world, int slot, CharacterCold cold, MedicalState med, ItemInstance limb)
    {
        cold.Mouth = null;
        Eat(world, slot, med, null, limb);
    }

    /// <summary>The player's side for eating (<c>FUN_140856bc0</c>): a character of the player's faction (the original also counts one an AI of the player's owns, not modelled).</summary>
    static bool PlayerSide(World world, CharacterCold cold) => cold.IsPlayer || world.Player.Faction >= 0 && cold.Faction == world.Player.Faction;

    (Inventory? From, ItemInstance? Food) FromMates(World world, int slot, CharacterCold cold, RaceData race, bool animal)
    {
        if (cold.SquadId < 0 || world.Squads.Find(cold.SquadId) is not { } squad) return (null, null);
        var table = world.Characters;
        var here = table.Next[slot].Position;
        foreach (var m in squad.Members)
        {
            if (!table.TryResolveNext(m, out int s) || table.Cold(s) is not { Inventory.Backpack.Contents: { } bag }) continue;
            if (Vector3.DistanceSquared(table.Next[s].Position, here) > ShareReach * ShareReach) continue;
            if (Best(bag.Items, race, animal, ownerDead: table.Cold(s)!.Medical?.Dead == true) is { } food) return (bag, food);
        }
        return (null, null);
    }

    /// <summary>Whether a character holds food it would bite in its own items (the "no food on it" test of the 243 / 259 desires, <c>FUN_1403344c0</c>).</summary>
    internal static bool CarriesFood(CharacterCold cold, RaceData race, bool animal) => cold.Inventory is { } own && Best(own.Items, race, animal) is not null;

    /// <summary>The bite of <c>FUN_140748d70</c>: raw food the race eats first, then food.</summary>
    static ItemInstance? Best(List<ItemInstance> items, RaceData race, bool animal, bool ownerDead = false) =>
        Best(items, RawFoodFunction, r => animal || race.SpecialFood.Contains(r), ownerDead) ?? Best(items, FoodFunction, null, ownerDead);

    /// <summary>
    /// The item of <paramref name="function"/> with the highest score: an opened item (charges below its full) scores (2 - charges / full) x 1000, a full one its nutrition (full / 100).
    /// Ties keep the first. A death item counts only when the inventory's owner is dead (FUN_140748d70, FUN_1407465d0).
    /// </summary>
    internal static ItemInstance? Best(List<ItemInstance> items, int function, Func<string, bool>? eats, bool ownerDead = false)
    {
        ItemInstance? best = null;
        float top = -1;
        foreach (var item in items)
        {
            if (item.ItemFunction != function || item.Quantity <= 0 || item.Death && !ownerDead || eats is not null && !eats(item.Record)) continue;
            float full = item.MaxCharges;
            float score = item.Charges == full ? full * 0.01f : full > 0 ? (1 - item.Charges / full + 1) * 1000 : 0;
            if (score > 0 && score > top) (best, top) = (item, score);
        }
        return best;
    }

    /// <summary>
    /// One bite (<c>FUN_1405d0780</c>): min(charges, 300 - hunger, 50) comes off the item (a limb, <c>item function</c> 17, is eaten whole for 30), which goes when nothing is left; the
    /// stomach gets a hundredth of it. With more than one unit in the stack the next unit starts full (engine choice: food does not stack in the data).
    /// </summary>
    static void Eat(World world, int slot, MedicalState med, Inventory? from, ItemInstance item)
    {
        bool limb = item.ItemFunction == 17;
        float amount = limb ? 30 : item.Charges;
        amount = MathF.Min(MathF.Min(amount, 300 - med.Hunger), BiteSize);
        item.Charges -= amount;
        if (item.Charges <= 0 || limb)
        {
            bool more = item.Quantity > 1;
            from?.RemoveOne(item);
            if (more) item.Charges = item.MaxCharges;
        }
        med.Fed = amount * 0.01f;
        var at = world.Characters.Next[slot].Position;
        world.Floaters?.Enqueue(Floater.Text(world.Characters.IdOf(slot), at, Meitou.Localisation.Loc.Tr("Ate {1}", item.Name)));
        world.EmitSound(SoundKind.Eat, slot, at);
    }
}
