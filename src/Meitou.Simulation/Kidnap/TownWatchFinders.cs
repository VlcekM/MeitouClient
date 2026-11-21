using System.Numerics;
using Meitou.Data.Gameplay.Construction;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Kidnap;

/// <summary>
/// The finders of the watch goals (docs/simulation.md "Town duties"): the shoo's stranger in the home building (158 / 283) and the ransack's
/// building (216), with the per-tick map of who is inside each building and the ransack's door planner.
/// </summary>
internal sealed class TownWatchFinders(PopulationData data)
{
    /// <summary>
    /// The salt of the ransack's target roll.
    /// </summary>
    const ulong RansackSalt = 0x5261_6E73_6163_6B21;

    /// <summary>
    /// The senses (resolved on first use), for the shoo finder.
    /// </summary>
    AggroSystem? _aggro;

    /// <summary>
    /// Who is inside each building, by building key, as of <see cref="_insidersTick"/> (the shoo finder's pass).
    /// </summary>
    readonly Dictionary<string, List<int>> _insiders = new(StringComparer.Ordinal);

    /// <summary>
    /// The tick <see cref="_insiders"/> was taken (-1: never).
    /// </summary>
    long _insidersTick = -1;

    /// <summary>
    /// The door planner of the ransack's way in (<see cref="EntryTo"/>).
    /// </summary>
    readonly DoorPlan _doorPlan = new();

    /// <summary>
    /// The function key of each BUILDING record the ransack finder weighed (<see cref="FunctionKeyOf"/>); derived from the data, not hashed.
    /// </summary>
    readonly Dictionary<string, int> _functionKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// <c>AI::findStrangersInMyBuilding</c> (<c>FUN_1408ced70</c>, goals 158 / 283): in the squad's home building, the first character (slot order,
    /// the original takes its sensed-list order) neither of an allied faction nor an enemy, not down, dead or caged, not a slave of the shooer's
    /// faction and not law enforcement (AUTHORITY_FIGURE, engine reading); for an NPC squad also any of the player's characters there that is not
    /// sneaking or that the shooer has spotted. Not one another errand of <paramref name="duties"/> shoos. None: none.
    /// </summary>
    public CharacterId Stranger(World world, int slot, CharacterCold cold, Squad squad, IReadOnlyList<TownDuty> duties)
    {
        if (squad.HomeBuilding is not { } home) return CharacterId.None;
        _aggro ??= world.Systems.OfType<AggroSystem>().FirstOrDefault();
        TakeInsiders(world);
        if (!_insiders.TryGetValue(home.Key, out var inside)) return CharacterId.None;

        var table = world.Characters;
        foreach (int s in inside)
        {
            if (s == slot || table.Cold(s) is not { Medical.Incapacitated: false } c || c.IsImprisoned) continue;
            if (c.SlaveState == 1 && c.SlaveOwner == cold.Faction) continue;
            var id = table.IdOf(s);
            if (Shooed(duties, id)) continue;
            if (Neutral(cold, c) && c.NpcClass != Data.Gameplay.FundamentalType.LawEnforcement) return id;
            if (!cold.IsPlayer && c.IsPlayer && ((c.Orders & CharacterOrders.Sneak) == 0 || _aggro?.Spotted(slot, id) == true))
                return id;
        }

        return CharacterId.None;
    }

    /// <summary>
    /// <c>AI::findRandomBuildingInTown</c> (<c>FUN_1408cb510</c>, goal 216): the squad's town (<see cref="SquadTown"/>), a placed town or a player
    /// outpost alike; one of its houses (<see cref="Houses"/>), drawn uniformly at random among those the planner would reach: row 156 Entering
    /// building (the only provider of effect 107, which goal 216 wants) needs DESTINATION_IS_ACCESSIBLE (59: <c>hasAnOpenDoor</c>) and
    /// DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS (90: no closed gate between), <b>Observed</b>. The door sub-steps are planned (<see cref="EntryTo"/>:
    /// open the door, row 72, or bash it, row 81); a building with no way in is left out of the draw. A closed gate on the route is planned as for
    /// an attacker (<see cref="LockSystem.GateEntry"/>: bash it, row 123 / 290, or pass an unlocked one, row 122). Null: none, also when the squad's
    /// town has no house (the finder does not fall back to another town then).
    /// </summary>
    public (string Key, Vector3 At, string? Door)? RansackTarget(World world, Squad squad, int slot, LockSystem? locks)
    {
        var town = SquadTown(world, squad, slot);
        if (town.IsNone) return null;

        var rooms = WaysIn(world, slot, Houses(world, town), locks);
        if (rooms.Count == 0) return null;

        var roll = Rng.Hash(world.Seed, (ulong)Rng.Key(world.Characters.IdOf(slot)) ^ (ulong)world.Tick, RngPurpose.Slow, RansackSalt);
        return rooms[Rng.Int(roll, rooms.Count)];
    }

    /// <summary>
    /// The squad's town as <c>FUN_140269cd0</c> gives it, when it resolves (<c>FUN_1409f9100</c>): the override's mission town (a campaign's war
    /// package's is the campaign target, a player outpost or a placed town, <see cref="Blackboard.ContractMissionTown"/>), else the running package's
    /// mission town; else (character vfunc 0x78) the town the member stands in. None: no town.
    /// </summary>
    CampaignTown SquadTown(World world, Squad squad, int slot)
    {
        if (squad.Ai is { } board)
        {
            if (Resolves(world, board.ContractMissionTown)) return board.ContractMissionTown;
            if (Resolves(world, board.Destination)) return board.Destination;
        }

        return AiSystem.TownAt(data, world, world.Characters.Next[slot].Position);
    }

    /// <summary>
    /// Whether <paramref name="town"/> names a placed town or a player outpost that still stands.
    /// </summary>
    static bool Resolves(World world, CampaignTown town) =>
        town.Site >= 0 || town.PlayerTown > 0 && world.Buildings.FindTown(town.PlayerTown) is not null;

    /// <summary>
    /// The town's houses, each by its building key and position: its buildings of function key 19 BF_SHELL_WITH_INTERIOR (a building with an
    /// interior and no functionality of its own, so no wall, gate or machine), whatever their owner. A placed town's: its standing ones
    /// (<see cref="IsHouse"/>). An outpost's: those whose own town handle names it (<see cref="Construction.BuildSite.Town"/>; one that takes
    /// the outpost only as the nearest town is left out) that are complete and standing (<see cref="Construction.ConstructionSystem.FunctionKey(string)"/>).
    /// <b>Verified</b> (<c>FUN_1408cb510</c> asks the town's vfunc 0x2c8, <c>FUN_1408d10f0</c>, shared by every TownBase class, with
    /// <c>BuildingFinder_OnesWithInteriors</c> and mask 0x80000: the buildings of key 19 in the zones registered with the town whose town it is,
    /// <c>FUN_1409fb880</c>, then slot 5 <c>FUN_1408ca540</c>: the root object's stored town handle is the town's own, it has an interior object and it
    /// is not a WallBuilding; the mask's bits are the function keys the factory <c>FUN_14057cc70</c> files each building under). Complete and
    /// standing are an engine choice: the finder has no such test, and Meitou gives an unfinished or ruined building no doors.
    /// </summary>
    List<(string Key, Vector3 At)> Houses(World world, CampaignTown town)
    {
        var houses = new List<(string, Vector3)>();
        if (town.Site >= 0)
        {
            foreach (var x in data.Sites[town.Site].Buildings)
            {
                if (IsHouse(x) && !world.Fixtures.IsDestroyed(x)) houses.Add((x.Key, x.Position));
            }

            return houses;
        }

        foreach (var site in world.Buildings.All)
        {
            if (!site.Complete || site.Destroyed || FunctionKeyOf(site.Record) != Construction.TownRefit.Shell) continue;
            if (site.Town == town.PlayerTown) houses.Add((site.Id, site.Position));
        }

        return houses;
    }

    /// <summary>
    /// A placed building of function key 19 BF_SHELL_WITH_INTERIOR: an <c>interior</c>, no wall or gate, and no functionality of its own (none, BF_ANY,
    /// BF_FLUFF or BF_SHELL_WITH_INTERIOR itself).
    /// </summary>
    internal static bool IsHouse(Meitou.Data.World.TownBuilding x) =>
        x.HasInterior && !x.IsWall && !x.IsGate && x.Function is -1 or 0 or Construction.TownRefit.Fluff or Construction.TownRefit.Shell;

    /// <summary>
    /// The function key of a BUILDING record (<see cref="Construction.ConstructionSystem.FunctionKey(string)"/>), read once.
    /// </summary>
    int FunctionKeyOf(string record)
    {
        if (_functionKeys.TryGetValue(record, out int key)) return key;

        var building = data.Db.Find(record) is { Type: Data.Fcs.FcsRecordType.Building } r ? BuildingBuildData.From(r) : null;
        return _functionKeys[record] = Construction.ConstructionSystem.FunctionKey(data.Db, building, record);
    }

    /// <summary>
    /// Takes who is inside each building, once a tick.
    /// </summary>
    void TakeInsiders(World world)
    {
        if (_insidersTick == world.Tick) return;
        _insidersTick = world.Tick;
        _insiders.Clear();

        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Next[s].Alive && world.Sight.BuildingOf(s) is { } key)
                (_insiders.TryGetValue(key, out var list) ? list : _insiders[key] = []).Add(s);
        }
    }

    /// <summary>
    /// Whether another errand already shoos <paramref name="id"/>.
    /// </summary>
    static bool Shooed(IReadOnlyList<TownDuty> duties, CharacterId id)
    {
        foreach (var o in duties)
        {
            if (o.Kind == TownDutyKind.Shoo && o.Body == id) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="c"/> is of another faction that is neither allied with nor an enemy of the shooer's.
    /// </summary>
    bool Neutral(CharacterCold cold, CharacterCold c)
    {
        if (c.Faction == cold.Faction) return false;
        if (!data.Relations.Contains(cold.Faction) || !data.Relations.Contains(c.Faction)) return true;
        return !data.Relations.IsAlly(cold.Faction, c.Faction) && !AggroSystem.IsEnemy(data.Relations, cold.Faction, c.Faction);
    }

    /// <summary>
    /// The houses the member gets into, each with the door (or gate) it bashes first.
    /// </summary>
    List<(string Key, Vector3 At, string? Door)> WaysIn(World world, int slot, List<(string Key, Vector3 At)> houses, LockSystem? locks)
    {
        var from = world.Characters.Next[slot].Position;
        var rooms = new List<(string, Vector3, string?)>();
        foreach (var (key, at) in houses)
        {
            var gate = locks?.GateEntry(world, slot, from, at) ?? (DoorPlan.Entry.Walk, null);
            if (gate.Entry == DoorPlan.Entry.Blocked) continue;
            if (gate.Door is not null) rooms.Add((key, at, gate.Door));   // the gate is bashed first (rows 123 / 290)
            else if (EntryTo(world, slot, key, locks) is { } door) rooms.Add((key, at, door.Length == 0 ? null : door));
        }

        return rooms;
    }

    /// <summary>
    /// How the member gets into the building <paramref name="key"/>, by the planner (row 156's state 59 <c>hasAnOpenDoor</c>,
    /// <see cref="DoorPlan.PlansEntry"/>): "" to walk in (a door open, none, or a closed one the character opens as it passes, row 72), the id of
    /// the door it bashes first (row 81, a locked door of a building it is aggressive towards), null when the planner finds no way in.
    /// </summary>
    string? EntryTo(World world, int slot, string key, LockSystem? locks)
    {
        if (world.Fixtures.HasOpenDoor(key)) return "";
        if (LockSystem.MainGateDoor(world, key) is not { } main) return "";
        var entry = _doorPlan.PlansEntry(world, world.SystemOf<Combat.CombatSystem>(), slot, key, main.Door, main.Spot, locks);
        return entry switch { DoorPlan.Entry.Walk => "", DoorPlan.Entry.Bash => main.Door.Id, _ => null };
    }
}
