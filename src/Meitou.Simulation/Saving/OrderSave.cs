using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Player;
using Meitou.Simulation.Production;

namespace Meitou.Simulation.Saving;

/// <summary>
/// One queued order as a save holds it: the row, the point, the target handle exactly as read (<see cref="Hand"/>: a character, a building or the
/// null handle), Meitou's own site id and detail code when it wrote them, and what a read could settle at once (<see cref="InstanceKey"/>, the
/// building a handle names; <see cref="Lost"/>, a handle that names nothing this save holds).
/// </summary>
public sealed record SavedOrder(TaskType Row, Vector3 Point, Hand Hand, string? Site, string? InstanceKey, int Kind, bool Lost);

/// <summary>
/// A player character's order queue in its GAMESTATE_AI record <c>ai</c>, next to the perma-jobs (<see cref="PermaJobSave"/>; <b>Verified</b>,
/// <c>AITaskSytem</c> vfuncs 1 and 2, writer <c>FUN_1405090b0</c>, loader <c>FUN_1405092d0</c>; docs/game/ai-tasks.md "Perma-jobs",
/// docs/simulation.md "Order queue"): per order i from 0 the int <c>tjob&lt;i&gt;</c> (task type), the vec3 <c>tjobP&lt;i&gt;</c> (point) and the
/// handle <c>tjobT&lt;i&gt;</c> (target: a character or a building). The queue is written front first, started or not; nothing is saved of it
/// running. Meitou's own keys carry what the keys above cannot: the string <c>meitou tjob&lt;i&gt;</c> (the <see cref="OrderEntry.Site"/> id, which
/// wins on load) and the int <c>meitou tjobK&lt;i&gt;</c> (the owner's detail code, <see cref="IOrderOwner.SaveKind"/>; absent: 0). A load reads the
/// orders into <see cref="CharacterCold.SavedOrders"/>; the decision step puts them in the second list (<see cref="Restore"/>, as the loader adds
/// each through <c>FUN_1405086d0</c> without the permanent flag), where the chooser scores them once the world they name exists. Only the queue is
/// written, so a save after the load writes the loaded orders no more.
/// </summary>
public static class OrderSave
{
    /// <summary>
    /// The GAMESTATE_AI key prefixes of an order: the task type, the point, the target handle and Meitou's site id and detail code.
    /// </summary>
    const string Type = "tjob", Point = "tjobP", Target = "tjobT", MeitouSite = "meitou tjob", MeitouKind = "meitou tjobK";

    /// <summary>
    /// Writes the queue over the old keys (<c>FUN_1405090b0</c> writes the deque alone: neither the second list nor orders loaded and not restored
    /// yet, which the original's load put in the second list). A target character's handle is given to <paramref name="target"/> to write once
    /// every character has one (the null handle is written first).
    /// </summary>
    public static void Write(
        FcsRecord ai, CharacterCold cold, World world, IReadOnlyDictionary<string, FcsRecord> buildings, Action<FcsRecord, string, CharacterId> target)
    {
        foreach (var key in ai.Ints.Keys
                     .Where(k => k.StartsWith(Type, StringComparison.Ordinal) || k.StartsWith(MeitouSite, StringComparison.Ordinal))
                     .ToList())
            ai.Ints.Remove(key);

        foreach (var key in ai.Strings.Keys.Where(k => k.StartsWith(MeitouSite, StringComparison.Ordinal)).ToList())
            ai.Strings.Remove(key);

        foreach (var key in ai.Vector3S.Keys.Where(k => k.StartsWith(Point, StringComparison.Ordinal)).ToList())
            ai.Vector3S.Remove(key);

        int i = 0;
        foreach (var e in cold.OrderQueue)
        {
            int kind = e.Row == TaskType.None ? -1 : e.Owner.SaveKind(e);
            if (kind < 0) continue;

            Put(ai, i, SavedRow(e.Row), e.Point == default ? e.TaskPoint : e.Point, e.Site is { } site ? SiteHand(world, site, buildings) : Hand.Null, e.Site, kind);
            if (!e.Target.IsNone) target(ai, Target + i, e.Target);
            i++;
        }
    }

    /// <summary>
    /// Writes order <paramref name="i"/> into the AI record; Meitou's site and detail keys only when set.
    /// </summary>
    static void Put(FcsRecord ai, int i, TaskType row, Vector3 point, Hand hand, string? site, int kind)
    {
        ai.Ints[Type + i] = (int)row;
        ai.Vector3S[Point + i] = point;
        hand.Write(ai, Target + i);
        if (site is not null) ai.Strings[MeitouSite + i] = site;
        if (kind != 0) ai.Ints[MeitouKind + i] = kind;
    }

    /// <summary>
    /// The row as the original numbers it: the Sleep order is 258 <c>USE_BED_ORDER</c> (Meitou's row 98 is the task it starts); the other rows are
    /// the same.
    /// </summary>
    internal static TaskType SavedRow(TaskType row) => row == TaskType.UseBed ? TaskType.UseBedOrder : row;

    /// <summary>
    /// An order on an object, whose saved point is where Meitou walks (the container's or the item's) but whose
    /// <see cref="OrderEntry.TaskPoint"/> is zero: 3 Pickup and 26 Loot.
    /// </summary>
    static bool ObjectRow(TaskType row) => row is TaskType.Pickup or TaskType.LootTarget;

    /// <summary>
    /// The handle of the building a site or fixture is, when it is the building itself (a bed the player built too) and the save holds its state;
    /// else the null handle (the site id names it).
    /// </summary>
    static Hand SiteHand(World world, string id, IReadOnlyDictionary<string, FcsRecord> buildings)
    {
        string? key = world.Buildings.Find(id) is { } site
            ? site.LevelPlacement is null
                ? BuildingKey.Of(site.Zone, id)
                : id
            : world.Fixtures.Find(id) is { } fixture && (fixture.Id == fixture.Building || fixture is { Kind: Locks.FixtureKind.Bed, Site: < 0 })
                ? fixture.Building
                : null;
        return key is not null && buildings.TryGetValue(key, out var state) && Hand.IsComplete(state, "handle")
            ? Hand.Read(state, "handle")
            : Hand.Null;
    }

    /// <summary>
    /// Reads the orders as the original's loader does (every <c>tjob&lt;i&gt;</c> from 0 until one is missing); only a player character's are read. A
    /// building handle is turned into its instance key now (the way <see cref="PermaJobSave.Read"/> does); a character is found when the decision
    /// step restores the queue, after every character is loaded.
    /// </summary>
    public static void Read(FcsRecord ai, CharacterCold cold, SaveHandles handles, IReadOnlyDictionary<FcsRecord, string> instanceOf)
    {
        List<SavedOrder>? orders = null;
        for (int i = 0; ai.Ints.TryGetValue(Type + i, out int row); i++)
        {
            var hand = Hand.Read(ai, Target + i);
            var point = ai.Vector3S.GetValueOrDefault(Point + i);
            string? site = ai.Strings.TryGetValue(MeitouSite + i, out var id) && id.Length > 0 ? id : null;
            string? key = null;
            bool lost = false;
            if (site is null && !hand.IsNull && hand.Type != (int)FcsRecordType.Character)
            {
                if (hand.Type == (int)FcsRecordType.Building && handles.Building(hand) is { } b && instanceOf.TryGetValue(b, out var instance))
                    key = instance;
                else
                    lost = true;
            }

            (orders ??= []).Add(new SavedOrder(
                (TaskType)row, point, hand, site, key, ai.Ints.GetValueOrDefault(MeitouKind + i), lost));
        }

        cold.SavedOrders = orders;
    }

    /// <summary>
    /// What became of one loaded order (<see cref="Make"/>).
    /// </summary>
    enum Restored
    {
        /// <summary>
        /// Made into a second-list entry.
        /// </summary>
        Added,
        /// <summary>
        /// Its target character or fixture is still to come into the world: it waits in the second list (<see cref="OrderEntry.Awaits"/>).
        /// </summary>
        Waits,
        /// <summary>
        /// Left out.
        /// </summary>
        Dropped,
    }

    /// <summary>
    /// Whether the character has loaded orders still to settle: read and not put in the second list yet, or waiting there for their target.
    /// </summary>
    internal static bool Pending(CharacterCold cold)
    {
        if (cold.SavedOrders is not null) return true;

        foreach (var e in cold.SecondList)
            if (e.Awaits is not null) return true;

        return false;
    }

    /// <summary>
    /// Puts the orders a load read in the character's second list, in the saved order (serial phases, from the decision step or a queued order,
    /// <see cref="PlayerOrders.Give"/>): nothing is started, its owner readies it (<see cref="IOrderOwner.Restored"/>), and the chooser scores it
    /// from its next look. The loader's add makes an entry whatever its hand names (<c>FUN_1405086d0</c> → <c>FUN_14032ebd0</c> copies the hand
    /// and tests only its type), and the scorer weighs each entry alone (<c>FUN_14050df10</c>), so an order whose target character is in one of the
    /// save's NPC platoons not made yet, or whose fixture is a town's not registered yet, waits in its place in the list as no candidate while the
    /// orders around it are scored, and is made on the first look that finds its target (<see cref="Settle"/>). An order whose row no system owns,
    /// whose target character or site is neither in the world nor still to come, or whose handle named nothing is left out.
    /// </summary>
    internal static void Restore(World world, int slot, CharacterCold cold)
    {
        bool settled = Settle(world, slot, cold);
        bool loaded = Load(world, slot, cold);
        if (settled || loaded) DecisionSystem.Replan(cold);   // each second-list add replans (FUN_1405086d0)
    }

    /// <summary>
    /// Adds the orders a load read to the second list, those whose target is still to come as waiting entries; true when one was made.
    /// </summary>
    static bool Load(World world, int slot, CharacterCold cold)
    {
        var saved = cold.SavedOrders;
        cold.SavedOrders = null;
        if (saved is null) return false;

        bool added = false;
        foreach (var o in saved)
        {
            var step = Make(world, slot, o, out var entry);
            if (step == Restored.Waits) entry = Waiting(world, o);
            if (entry is null) continue;

            cold.SecondList.Add(entry);
            added |= step == Restored.Added;
        }

        return added;
    }

    /// <summary>
    /// Makes the waiting entries whose target came into the world, in their place; one whose target will come no more stays there spent (no
    /// candidate until the list is cleared, as the original's entry whose hand names nothing; in place, so a look's pick index holds); true when one
    /// was made.
    /// </summary>
    static bool Settle(World world, int slot, CharacterCold cold)
    {
        var list = cold.SecondList;
        bool added = false;
        for (int k = list.Count - 1; k >= 0; k--)
        {
            if (list[k].Awaits is not { } o) continue;

            var step = Make(world, slot, o, out var entry);
            if (step == Restored.Waits) continue;

            list[k] = entry ?? Spent(list[k]);
            added |= step == Restored.Added;
        }

        return added;
    }

    /// <summary>
    /// The second-list entry of one loaded order when its row has an owner and what it names is there (its owner readied it); says what became of
    /// it.
    /// </summary>
    static Restored Make(World world, int slot, SavedOrder o, out OrderEntry? entry)
    {
        entry = null;
        bool onSite = (o.Site ?? o.InstanceKey) is not null;
        var row = LoadedRow(o, onSite);
        if (row == TaskType.None || world.OrderOwnerOf(row, onSite) is not { } owner) return Restored.Dropped;

        // A finder row's saved hand only feeds its finder (the plan treats the finder's pick): its entry stays when the hand names nothing.
        var target = CharacterId.None;
        bool finder = TaskDataTable.Of(row)?.Is(TaskDataFlags.Finder) == true;
        if (o.Lost && !finder) return Restored.Dropped;
        if (o is { Hand: { IsNull: false, Type: (int)FcsRecordType.Character } } && (target = Find(world.Characters, o.Hand)).IsNone && !finder)
            return Awaited(world, o.Hand) ? Restored.Waits : Restored.Dropped;

        string? site = null;
        if (onSite && (site = SiteOf(world, o)) is null && !finder) return SiteAwaited(world, o) ? Restored.Waits : Restored.Dropped;

        entry = Entry(world, slot, o, row, owner, target, site);
        owner.Restored(world, entry);
        return Restored.Added;
    }

    /// <summary>
    /// A waiting entry whose target will come no more, spent (<see cref="OrderEntry.Spent"/>).
    /// </summary>
    static OrderEntry Spent(OrderEntry waiting) => new(waiting.Row, waiting.Owner) { Point = waiting.Point, Spent = true };

    /// <summary>
    /// The waiting entry of a loaded order whose target is still to come: its row, owner and point, no target, and the order kept to make it from.
    /// </summary>
    static OrderEntry Waiting(World world, SavedOrder o)
    {
        bool onSite = (o.Site ?? o.InstanceKey) is not null;
        var row = LoadedRow(o, onSite);
        return new OrderEntry(row, world.OrderOwnerOf(row, onSite)!) { Point = o.Point, Awaits = o };
    }

    /// <summary>
    /// The second-list entry of a loaded order. A body order is built as the body gives it (<see cref="Combat.BodyOrders"/>): it lets no duty go,
    /// and its stumble is forced.
    /// </summary>
    static OrderEntry Entry(World world, int slot, SavedOrder o, TaskType row, IOrderOwner owner, CharacterId target, string? site)
    {
        bool body = Combat.BodyOrders.Rows.Contains(row);
        return new OrderEntry(row, owner)
        {
            Interrupts = body,
            Forced = body && row == TaskType.StumbleTaskForced,
            Target = target,
            Site = site,
            Point = o.Point,
            TaskPoint = ObjectRow(row) ? default : o.Point,
            Detail = owner.LoadDetail(world, world.Characters.IdOf(slot), row, o.Kind, target),
        };
    }

    /// <summary>
    /// Whether a character the save holds with <paramref name="hand"/> is still to come into the world: a member of one of its NPC platoons
    /// (<see cref="SavedPopulation.Platoons"/>) that is not wiped out, not despawned and not made yet, or out of the world since, a dead one too,
    /// whose body is made again with its platoon (<see cref="SavedCharacters.Prepare"/>), as the original's handle lookup finds a dead character
    /// (<c>FUN_140284510</c>: the platoon's list, then the dead characters' map at <c>DAT_142134110 + 0x708</c>, which <c>FUN_140623920</c> fills
    /// for a character with the dead byte <c>+0x5bc</c>); one whose body is gone (<see cref="SavedMember.Gone"/>) comes no more.
    /// </summary>
    static bool Awaited(World world, Hand hand) =>
        world.Saved is { } saved
        && saved.Platoons.Exists(sp => sp.PlatoonId >= 0 && !sp.Despawned && sp.Members.Exists(m => !m.Gone && m.Source.Handle == hand));

    /// <summary>
    /// Whether the fixture an order names (its site id, else the instance a building handle named) is one of a town's whose fixtures are registered
    /// only when the town comes into the world (<see cref="Locks.FixtureRegistry.Unregistered"/>).
    /// </summary>
    static bool SiteAwaited(World world, SavedOrder o) =>
        world.SystemOf<Population.PopulationSystem>()?.Data is { } data && (o.Site ?? o.InstanceKey) is { } id && world.Fixtures.Unregistered(data, id);

    /// <summary>
    /// The queue row of a saved order. A container order an older Meitou wrote as row 3 on a site is 26 (kind 0, opening it), and one that only
    /// walked there (kind 1) is left out (<see cref="TaskType.None"/>). Then the loader's add takes the row's job remap whatever the hand names
    /// (<c>FUN_1405086d0</c>, TaskData <c>+4</c> and <c>+0xc</c>, <b>Verified</b>; <see cref="PermaJobs.Remapped"/>), the saved hand kept: 2 and 95
    /// load as 125 Engineer, 4, 5 and 61 as 16 Attacking enemies, 25 as 58 Medic, 60 as 57 Robotics, 249 as 250 Splint Rigging, 26 as 238
    /// Foraging animals, 136 as 137 Corpse disposal (which no system owns as a loaded order yet, so it is dropped); Meitou leaves 146 as it is
    /// (<see cref="Unremapped"/>). Last, the original's sleep order 258 (no job form, so it loads as itself) is Meitou's 98.
    /// </summary>
    static TaskType LoadedRow(SavedOrder o, bool onSite)
    {
        var row = o.Row == TaskType.Pickup && onSite
            ? o.Kind == 0 ? TaskType.LootTarget : TaskType.None
            : o.Row;
        if (!Unremapped(row)) row = PermaJobs.Remapped(row);

        return row == TaskType.UseBedOrder ? TaskType.UseBed : row;
    }

    /// <summary>
    /// The remap Meitou does not take: 146 Turret gunner to 234 Turret guard (234's player-faction desire and its unused-turret finder,
    /// <c>BuildingFinderTurrets_Unused</c> in <c>FUN_1408ccfa0</c>, are not built). It loads as the order of its own row.
    /// </summary>
    static bool Unremapped(TaskType row) => row is TaskType.UseTurret;

    /// <summary>
    /// The world character whose saved handle is <paramref name="hand"/>; <see cref="CharacterId.None"/> when none is.
    /// </summary>
    static CharacterId Find(CharacterTable table, Hand hand)
    {
        for (int s = 0; s < table.HighWater; s++)
            if (table.Next[s].Alive && table.Cold(s)?.Save?.Source.Handle == hand) return table.IdOf(s);

        return CharacterId.None;
    }

    /// <summary>
    /// The site an order names: Meitou's id when the world has it (a build site, a fixture, a rock, a town dummy or turret), else the instance a building handle named, as a
    /// build site or a fixture.
    /// </summary>
    static string? SiteOf(World world, SavedOrder o)
    {
        if (o.Site is { } id) return RowStates.SiteGone(world, id) ? null : id;

        var key = o.InstanceKey!;
        int colon = key.IndexOf(':');
        if (colon >= 0 && world.Buildings.Find(key[(colon + 1)..]) is not null) return key[(colon + 1)..];

        return world.Fixtures.Find(key) is not null ? key : null;
    }
}
