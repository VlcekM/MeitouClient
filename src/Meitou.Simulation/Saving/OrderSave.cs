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
/// each through <c>FUN_1405086d0</c> without the permanent flag) once the world they name exists, where the chooser scores them. Only the queue is
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
    /// Puts the orders a load read in the character's second list (serial phases, from the decision step or a queued order,
    /// <see cref="PlayerOrders.Give"/>): nothing is started, its owner readies it (<see cref="IOrderOwner.Restored"/>), and the chooser scores it
    /// from its next look. An order whose row no system owns, whose target character or site is not in the world, or whose handle named nothing is
    /// left out.
    /// </summary>
    internal static void Restore(World world, int slot, CharacterCold cold)
    {
        var saved = cold.SavedOrders;
        cold.SavedOrders = null;
        if (saved is null) return;

        var table = world.Characters;
        var self = table.IdOf(slot);
        foreach (var o in saved)
        {
            bool onSite = (o.Site ?? o.InstanceKey) is not null;
            var row = LoadedRow(o, onSite);
            if (o.Lost || row == TaskType.None || world.OrderOwnerOf(row, onSite) is not { } owner) continue;

            // A finder row's saved hand only feeds its finder (the plan treats the finder's pick): its entry stays when the hand names nobody.
            var target = CharacterId.None;
            bool finder = TaskDataTable.Of(row)?.Is(TaskDataFlags.Finder) == true;
            if (o is { Hand: { IsNull: false, Type: (int)FcsRecordType.Character } } && (target = Find(table, o.Hand)).IsNone && !finder) continue;

            string? site = null;
            if ((o.Site ?? o.InstanceKey) is not null && (site = SiteOf(world, o)) is null) continue;

            // A body order is built as the body gives it (Combat.BodyOrders): it lets no duty go, and its stumble is forced.
            bool body = Combat.BodyOrders.Rows.Contains(row);
            var entry = new OrderEntry(row, owner)
            {
                Interrupts = body,
                Forced = body && row == TaskType.StumbleTaskForced,
                Target = target,
                Site = site,
                Point = o.Point,
                TaskPoint = ObjectRow(row) ? default : o.Point,
                Detail = owner.LoadDetail(world, self, row, o.Kind, target),
            };
            owner.Restored(world, entry);
            cold.SecondList.Add(entry);
        }

        DecisionSystem.Replan(cold);   // each second-list add replans (FUN_1405086d0)
    }

    /// <summary>
    /// The queue row of a saved order: the original's sleep order 258 is Meitou's 98 (258 has no job form, so it loads as itself); a container
    /// order an older Meitou wrote as row 3 on a site is 26 (kind 0, opening it), and one that only walked there (kind 1) is left out
    /// (<see cref="TaskType.None"/>). The loader's add takes a row's job remap (<c>FUN_1405086d0</c>, TaskData <c>+0xc</c>, <b>Verified</b>): 25 First aid loads as 58 Medic and 26 Loot on a character as 238 Foraging
    /// animals, the saved hand kept.
    /// </summary>
    static TaskType LoadedRow(SavedOrder o, bool onSite) => o.Row switch
    {
        TaskType.UseBedOrder => TaskType.UseBed,
        TaskType.FirstAidOrder => TaskType.JobMedic,
        TaskType.LootTarget when !onSite => TaskType.LootAnimalsJob,
        TaskType.Pickup when onSite => o.Kind == 0 ? TaskType.LootTarget : TaskType.None,
        _ => o.Row,
    };

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
        if (o.Site is { } id)
        {
            bool known = world.Buildings.Find(id) is not null || world.Fixtures.Find(id) is not null || ProductionSystem.IsNatural(id)
                || Locks.FixtureRegistry.IsDummy(id) || ProductionSystem.IsTownWork(id);
            return known ? id : null;
        }

        var key = o.InstanceKey!;
        int colon = key.IndexOf(':');
        if (colon >= 0 && world.Buildings.Find(key[(colon + 1)..]) is not null) return key[(colon + 1)..];

        return world.Fixtures.Find(key) is not null ? key : null;
    }
}
