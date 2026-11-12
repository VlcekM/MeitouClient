using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The player's buildings and construction sites in a save (docs/formats/zones.md "Placement keys", docs/formats/save.md). Each is an instance of its
/// zone file's <c>0-buildinglist</c>, id <c>&lt;n&gt;-zone&lt;X&gt;.&lt;Y&gt;-INGAME</c>, with a GAMESTATE_BUILDING state <c>&lt;id&gt;-S23</c> in
/// the keys of the original's writer (<c>FUN_1405547f0</c>, <b>Verified</b> by code): <c>name</c>, <c>owner faction ID</c>, <c>floornum</c>,
/// <c>destroyed</c>, <c>flags</c>, <c>is complete</c>, <c>pause</c>, <c>construction progress</c>, <c>&lt;i&gt;construction mats</c> (delivered of
/// entry i), <c>world Y pos</c> (absolute), <c>player furniture Y offset</c> (relative to the parent's origin) and the handles <c>handle</c>,
/// <c>isInsideBuilding</c>, <c>town</c>, <c>residents</c>. Instance Y is written relative to terrain outdoors, to the parent indoors
/// (docs/formats/save.md); reading takes <c>world Y pos</c>. Meitou's own key <c>meitou parent</c> (the parent's instance id) carries a parent the
/// original's handle cannot name, and wins on load. <c>town</c> is the handle of the player outpost the site belongs to (<see cref="WriteTowns"/>).
/// </summary>
public static class BuildingSave
{
    /// <summary>
    /// Meitou's GAMESTATE_BUILDING key naming the parent's instance id (wins over the original's handle on load).
    /// </summary>
    public const string MeitouParent = "meitou parent";

    #region Shared

    /// <summary>
    /// The container serial of every zone container (<b>Verified</b>, 1496 of 1496 handles).
    /// </summary>
    const int ContainerSerial = 11111;

    /// <summary>
    /// The <c>version</c> key of a building state (<b>Observed</b> 270 in every sample state).
    /// </summary>
    const int StateVersion = 270;

    /// <summary>
    /// The suffix of a building's GAMESTATE_BUILDING state id.
    /// </summary>
    const string StateSuffix = "-S23";   // the hex of GAMESTATE_BUILDING (35)
    /// <summary>
    /// The name prefix of a player town's GAMESTATE_TOWN state (the rest is the town's name).
    /// </summary>
    const string TownStatePrefix = "Town state ";
    /// <summary>
    /// The suffix of a player town's GAMESTATE_TOWN state id.
    /// </summary>
    const string TownStateSuffix = "-S5e";   // the hex of GAMESTATE_TOWN (94)

    /// <summary>
    /// A town without a material of its own uses this one (docs/formats/zones.md "Materials"; the template has none).
    /// </summary>
    const string DefaultTownMaterial = "742-gamedata.base";

    /// <summary>
    /// Whether a building instance is the player's: a registry id, a state that is not a town's construction and the player faction as its owner.
    /// </summary>
    static bool IsPlayerBuilt(string instanceId, FcsRecord? state, string playerFactionId) =>
        BuildingRegistry.TryParseId(instanceId, out _, out _) && state is not null && !state.Bools.GetValueOrDefault("meitou town construction")
        && state.Strings.GetValueOrDefault("owner faction ID") == playerFactionId;

    #endregion

    #region Reading

    /// <summary>
    /// Reserves the ids of every <c>-zone&lt;X&gt;.&lt;Y&gt;-INGAME</c> building of the save (generated interiors share the pattern) and reads the
    /// player faction's into the world's registry, in zone and list order. Returns how many sites it made.
    /// </summary>
    public static int Read(SaveGame save, World world, GameDatabase db, string playerFactionId, int playerIndex, PopulationData? population = null)
    {
        var reg = world.Buildings;
        ReserveIds(save, reg);
        var states = HeldSave.BuildingStates(save);
        var idOf = ParentIds(save, states, playerFactionId);
        var handles = save.BuildHandles();
        var townOf = ReadTowns(save, world, playerFactionId, playerIndex, population);
        int made = 0;
        foreach (var zone in save.Zones.Values)
        {
            if (zone.BuildingList is not { } list) continue;

            foreach (var instance in list.Instances)
            {
                if (instance.IsRemoved || IsStaleCopy(save, zone, instance)
                    || !states.TryGetValue(BuildingKey.Of(zone.Coordinate, instance.Id), out var state)
                    || !IsPlayerBuilt(instance.Id, state, playerFactionId))
                    continue;

                if (db.Find(instance.Target) is not { Type: FcsRecordType.Building } record) continue;

                var data = BuildingBuildData.From(record);
                var materials = new MaterialSlot[data.Materials.Count];
                for (int k = 0; k < materials.Length; k++)
                {
                    var material = data.Materials[k];
                    materials[k] = new MaterialSlot(material.Item, material.Required)
                    {
                        Delivered = state.Floats.GetValueOrDefault($"{k}construction mats")
                    };
                }

                float target = data.Target;
                bool complete = state.Bools.GetValueOrDefault("is complete", true);   // absent means complete
                string? parent = state.Strings.GetValueOrDefault(MeitouParent) is { Length: > 0 } own
                    ? own
                    : state.Bools.GetValueOrDefault("is inside building") && handles.Building(Hand.Read(state, "isInsideBuilding")) is { } building
                        ? idOf.GetValueOrDefault(building)
                        : null;
                float y = state.Floats.TryGetValue("world Y pos", out float absolute) ? absolute : instance.Position.Y;
                var townHand = Hand.Read(state, "town");
                reg.Add(new BuildSite
                {
                    Id = instance.Id,
                    Record = data.Id,
                    Name = data.Name,
                    Position = new Vector3(instance.Position.X, y, instance.Position.Z),
                    Rotation = instance.Rotation,
                    Floor = state.Ints.GetValueOrDefault("floornum"),
                    Parent = parent,
                    Town = townHand.IsNull ? 0 : townOf.GetValueOrDefault((townHand.C, CS: townHand.Cs)),
                    UnresolvedTown = state.Bools.GetValueOrDefault("meitou unresolved town") ||
                        !townHand.IsNull && !townOf.ContainsKey((townHand.C, townHand.Cs)) &&
                        !IsNpcTown(save, handles.Town(townHand), townHand, playerFactionId),
                    Owner = playerIndex,
                    Materials = materials,
                    Target = target,
                    Rate = data.Rate,
                    Progress = complete ? target : MathF.Min(state.Floats.GetValueOrDefault("construction progress"), target),
                    Complete = complete,
                    Paused = state.Bools.GetValueOrDefault("pause"),
                    Public = state.Bools.GetValueOrDefault("is public day", true),
                    Destroyed = state.Bools.GetValueOrDefault("destroyed"),
                });
                made++;
            }
        }

        AttachWalls(world, db);

        // Engine choice: towns are reassessed on load (a save may hold a centre that was never moved, as Meitou's before it reassessed).
        ConstructionSystem.ReassessTowns(world);
        return made;
    }

    /// <summary>
    /// A resolved non-player level town is valid even though it has no player-outpost number.
    /// </summary>
    static bool IsNpcTown(SaveGame save, SaveTown? town, Hand handle, string playerFactionId) =>
        town is { IsNest: false } && town.Handle.Cs == handle.Cs && town.FactionId != playerFactionId &&
        save.TownInstanceList?.Instances.Any(instance => instance.Id == town.Instance && instance.Target == PlayerTown.Template) != true;

    /// <summary>
    /// Whether <paramref name="instance"/> is listed outside the zone its position lies in while that zone lists it too. The original lists a
    /// building in the zone it was built from, which may not be its position's (<b>Observed</b>, an imported vanilla save of #493); Meitou before
    /// it removed that entry wrote a second one in the position's zone, and the newer copy wins.
    /// </summary>
    static bool IsStaleCopy(SaveGame save, SaveZone zone, FcsInstance instance)
    {
        var home = WorldLayout.ZoneOf(instance.Position.X, instance.Position.Z);
        if (home == zone.Coordinate || !save.Zones.TryGetValue(home, out var own)) return false;

        return own.BuildingList?.Instances.Any(i => i.Id == instance.Id && !i.IsRemoved) == true;
    }

    /// <summary>
    /// Reserves the instance id of every building in the save's zone building lists.
    /// </summary>
    static void ReserveIds(SaveGame save, BuildingRegistry reg)
    {
        foreach (var zone in save.Zones.Values)
        {
            if (zone.BuildingList is { } list)
            {
                foreach (var i in list.Instances) reg.Reserve(i.Id);
            }
        }
    }

    /// <summary>
    /// The id each building state's record stands for: a player building's instance id, else its zone-qualified runtime identity (how an interior's
    /// parent is named).
    /// </summary>
    static Dictionary<FcsRecord, string> ParentIds(SaveGame save, Dictionary<string, FcsRecord> states, string playerFactionId)
    {
        // Player sites have globally reserved ids; level houses use their zone-qualified runtime identity.
        var idOf = new Dictionary<FcsRecord, string>(ReferenceEqualityComparer.Instance);
        foreach (var zone in save.Zones.Values)
        {
            if (zone.BuildingList is not { } ids) continue;

            foreach (var i in ids.Instances)
            {
                if (states.TryGetValue(BuildingKey.Of(zone.Coordinate, i.Id), out var record))
                    idOf[record] = IsPlayerBuilt(i.Id, record, playerFactionId) ? i.Id : BuildingKey.Of(zone.Coordinate, i.Id);
            }
        }

        return idOf;
    }

    /// <summary>
    /// Attaches the wall subsections to the sites. The save holds no wall links; zone load rebuilds them.
    /// </summary>
    static void AttachWalls(World world, GameDatabase db)
    {
        // Wall subsection links are not saved; zone load rebuilds them (docs/game/buildings-production.md "Walls").
        var reg = world.Buildings;
        var wallData = new Dictionary<string, BuildingBuildData?>(StringComparer.Ordinal);
        BuildingBuildData? DataOf(string id) =>
            wallData.TryGetValue(id, out var d)
                ? d
                : wallData[id] = db.Find(id) is { Type: FcsRecordType.Building } r ? BuildingBuildData.From(r) : null;
        ConstructionSystem.AttachWalls(reg, DataOf, reg.All.ToList());
    }

    /// <summary>
    /// The player's outposts from town-instance entries and player-faction level-town states.
    /// </summary>
    static Dictionary<(int C, int CS), int> ReadTowns(SaveGame save, World world, string playerFactionId, int playerIndex,
        PopulationData? population)
    {
        var byId = save.Quick.Records.ToDictionary(record => record.StringId, StringComparer.Ordinal);
        var byInstance = new Dictionary<string, PlayerTown>(StringComparer.Ordinal);

        if (save.TownInstanceList is { } list)
            foreach (var instance in list.Instances)
                ReadTownInstance(instance, byId, save, world, playerFactionId, playerIndex, byInstance);

        ReadLevelTowns(save, population, world, playerFactionId, playerIndex, byInstance);
        NormalizeTownOverlap(world);
        return TownHandles(world);
    }

    static void ReadTownInstance(FcsInstance instance, Dictionary<string, FcsRecord> byId, SaveGame save, World world,
        string playerFactionId, int playerIndex, Dictionary<string, PlayerTown> byInstance)
    {
        if (instance.IsRemoved) return;

        var states = TownStates(instance, byId, save)
            .Where(state => IsPlayerTown(state, instance, playerFactionId))
            .ToList();
        if (states.Count == 0) return;

        var town = byInstance.GetValueOrDefault(instance.Id) ?? world.Buildings.AddTown(instance.Position, playerIndex);
        byInstance[instance.Id] = town;
        ApplyTownStates(town, instance.Id, instance.Position, states);
    }

    static IEnumerable<FcsRecord> TownStates(FcsInstance instance, Dictionary<string, FcsRecord> byId, SaveGame save)
    {
        var seen = new HashSet<FcsRecord>(ReferenceEqualityComparer.Instance);
        foreach (var id in instance.States)
            if (byId.GetValueOrDefault(id) is { RecordType: FcsRecordType.GamestateTown } state && seen.Add(state)) yield return state;

        foreach (var state in save.Towns.Select(town => town.Record).Where(state => state.Strings.GetValueOrDefault("instance") == instance.Id))
            if (seen.Add(state)) yield return state;
    }

    static bool IsPlayerTown(FcsRecord state, FcsInstance instance, string playerFactionId) =>
        !state.Bools.GetValueOrDefault("is nest") &&
        (state.Strings.GetValueOrDefault("faction") == playerFactionId ||
            instance.Target == PlayerTown.Template && !state.Strings.ContainsKey("faction"));

    static void ReadLevelTowns(SaveGame save, PopulationData? population, World world, string playerFactionId, int playerIndex,
        Dictionary<string, PlayerTown> byInstance)
    {
        if (population is null) return;

        foreach (var state in save.Towns.Where(town => !town.IsNest && town.FactionId == playerFactionId))
        {
            var site = population.Sites.FirstOrDefault(candidate => candidate.InstanceId == state.Instance);
            var instance = save.TownInstanceList?.Instances.FirstOrDefault(candidate => candidate.Id == state.Instance);
            if (state.Handle.IsNull || instance is null && site is null) continue;

            var town = byInstance.GetValueOrDefault(state.Instance);
            if (town is null)
            {
                town = world.Buildings.Towns.FirstOrDefault(candidate => candidate.SaveHandle is { } handle &&
                    handle.C == state.Handle.C && handle.Cs == state.Handle.Cs);
                town ??= world.Buildings.AddTown(instance?.Position ?? site!.Position, playerIndex);
                byInstance[state.Instance] = town;
            }

            ApplyTownStates(town, state.Instance, instance?.Position ?? site!.Position, [state.Record]);
        }
    }

    static void ApplyTownStates(PlayerTown town, string instanceId, Vector3 position, IEnumerable<FcsRecord> states)
    {
        town.SaveInstance = instanceId;
        town.Position = position;
        foreach (var state in states)
        {
            string name = TownName(state);
            if (town.Name == PlayerTown.DefaultName && name.Length > 0) town.Name = name;
            if (state.Bools.ContainsKey("public")) town.Public = state.Bools["public"];
            if (state.Ints.ContainsKey("override")) town.Override = state.Ints["override"];

            var hand = Hand.Read(state, "hand");
            if (!hand.IsNull && town.SaveHandle is null) town.SaveHandle = hand;
        }
    }

    static string TownName(FcsRecord state)
    {
        if (!state.Name.StartsWith(TownStatePrefix, StringComparison.Ordinal)) return "";

        return state.Name[TownStatePrefix.Length..].TrimEnd();
    }

    internal static void NormalizeTownOverlap(World world)
    {
        foreach (var current in world.Buildings.Towns.ToList())
        {
            if (!world.Buildings.Towns.Contains(current)) continue;

            var other = world.Buildings.Towns
                .Where(town => town != current)
                .MinBy(town => DistanceSquaredXz(current.Position, town.Position));
            if (other is not null && DistanceSquaredXz(current.Position, other.Position) < other.Radius * other.Radius)
                world.Buildings.RemoveTown(current);
        }
    }

    static Dictionary<(int C, int CS), int> TownHandles(World world)
    {
        var map = new Dictionary<(int, int), int>();
        foreach (var town in world.Buildings.Towns)
            if (town.SaveHandle is { } hand) map.TryAdd((hand.C, hand.Cs), town.Number);

        return map;
    }

    static float DistanceSquaredXz(Vector3 a, Vector3 b)
    {
        float x = a.X - b.X, z = a.Z - b.Z;
        return x * x + z * z;
    }

    #endregion

    #region Writing

    /// <summary>
    /// Writes the registry into the save: every site's instance and state in its zone file (a zone without a file gets one), and removes the
    /// player-built entries the world no longer has (a dismantled building). Everything else in the zone files is left as it was. Returns the town
    /// handle each player town (by number) was written with.
    /// </summary>
    public static IReadOnlyDictionary<int, Hand> Write(SaveGame game, World world, string playerFactionId, ulong seed = 0)
    {
        var reg = world.Buildings;
        var live = reg.All.ToDictionary(s => s.Id, s => s.LevelPlacement is null ? s.Zone : (ZoneCoordinate?)null, StringComparer.Ordinal);
        RemoveDismantled(game, playerFactionId, live);
        var townHands = WriteTowns(game, world, playerFactionId, seed);
        if (reg.Count == 0) return townHands;

        foreach (var site in reg.All.Where(s => s.LevelPlacement is null))
            WriteSite(game, site, playerFactionId, seed, townHands.GetValueOrDefault(site.Town, Hand.Null), world.Fixtures, world.Walkability);

        WriteParents(game, world);
        return townHands;
    }

    /// <summary>
    /// Removes the player-built buildings of the save that the world no longer has, with their state records and their slots, and a live site's
    /// entry in a zone other than the one it is written in (<paramref name="live"/> maps each site to that zone, null for a level house's).
    /// </summary>
    static void RemoveDismantled(SaveGame game, string playerFactionId, Dictionary<string, ZoneCoordinate?> live)
    {
        foreach (var zone in game.Zones.Values)
        {
            if (zone.BuildingList is not { } list) continue;

            var byId = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
            foreach (var r in zone.File.Records) byId[r.StringId] = r;
            foreach (var instance in list.Instances.ToList())
            {
                if (live.TryGetValue(instance.Id, out var home) && (home is null || home == zone.Coordinate)) continue;

                var state = instance.States.Count > 0 ? byId.GetValueOrDefault(instance.States[0]) : null;
                if (!IsPlayerBuilt(instance.Id, state, playerFactionId)) continue;

                list.Instances.Remove(instance);
                zone.File.Records.Remove(state!);
                if (Hand.Read(state!, "handle") is { IsNull: false } h && zone.File.SlotLists.Count > 0)
                    zone.File.SlotLists[0] = [.. zone.File.SlotLists[0].Where(slot => slot != h.I)];
            }
        }
    }

    /// <summary>
    /// Writes the parent of each site inside a building, and the height of the site above its parent's floor (the parent pass runs last, as the
    /// parents may be sites written before it).
    /// </summary>
    static void WriteParents(SaveGame game, World world)
    {
        var reg = world.Buildings;
        var states = HeldSave.BuildingStates(game);
        foreach (var site in reg.All.Where(s => s.LevelPlacement is null))
        {
            if (!states.TryGetValue(BuildingKey.Of(site.Zone, site.Id), out var state)) continue;

            if (site.Parent is { } parent)
            {
                state.Strings[MeitouParent] = parent;
                state.Bools["is inside building"] = true;
                if (!states.TryGetValue(parent, out var p)) states.TryGetValue(BuildingKey.Of(site.Zone, parent), out p);
                (p is not null && Hand.IsComplete(p, "handle") ? Hand.Read(p, "handle") : Hand.Null).Write(state, "isInsideBuilding");
                float? parentY = reg.Find(parent)?.Position.Y ?? world.Fixtures.PropertyPosition(parent)?.Y;
                if (parentY is null && p is not null && p.Floats.TryGetValue("world Y pos", out float savedY)) parentY = savedY;
                if (parentY is { } y)
                {
                    float offset = site.Position.Y - y;
                    state.Floats["player furniture Y offset"] = offset;

                    // Zero means "use instance Y" to the original loader; keep that fallback parent-relative too.
                    var instance = game.Zones[site.Zone].BuildingList!.Instances.First(i => i.Id == site.Id);
                    instance.Position = instance.Position with { Y = offset };
                }
            }
            else
            {
                state.Strings.Remove(MeitouParent);
                state.Bools["is inside building"] = false;
                state.Floats["player furniture Y offset"] = 0;
                Hand.Null.Write(state, "isInsideBuilding");
            }
        }
    }

    /// <summary>
    /// Writes one site: its instance in the zone's building list, its state record and its keys.
    /// </summary>
    static void WriteSite(SaveGame game, BuildSite site, string playerFactionId, ulong seed, Hand town, Locks.FixtureRegistry fixtures,
        IWalkability walkability)
    {
        var coordinate = site.Zone;
        if (!game.Zones.TryGetValue(coordinate, out var zone)) game.Zones[coordinate] = zone = new SaveZone(coordinate, new SaveFile());
        var file = zone.File;
        var list = zone.BuildingList;
        if (list is null)
        {
            list = new FcsRecord { Type = (int)FcsRecordType.InstanceCollection, Id = NextNumber(file), Name = "0",
                StringId = WorldLevelData.BuildingListId };
            file.Records.Add(list);
        }

        string stateId = site.Id + StateSuffix;
        var state = file.Records.FirstOrDefault(r => r.StringId == stateId && r.RecordType == FcsRecordType.GamestateBuilding);
        if (state is null)
        {
            state = new FcsRecord { Type = (int)FcsRecordType.GamestateBuilding, Id = NextNumber(file), Name = "0", StringId = stateId };
            file.Records.Add(state);
        }

        var instance = list.Instances.FirstOrDefault(i => i.Id == site.Id);
        if (instance is null)
        {
            instance = new FcsInstance { Id = site.Id };
            list.Instances.Add(instance);
        }

        var at = site.Position;
        instance.Target = site.Record;

        // Outdoor height above terrain; the parent pass supplies indoor height (docs/formats/save.md).
        instance.Position = at with { Y = at.Y - walkability.TerrainHeight(at.X, at.Z) };
        instance.Rotation = site.Rotation;
        instance.States.Clear();
        instance.States.Add(stateId);

        var hand = WriteSlot(file, zone, state, site, seed);
        WriteSiteKeys(state, site, playerFactionId, hand, town, fixtures);
    }

    /// <summary>
    /// The handle of a site's state: kept while the state has one in this zone, else the next free one of the zone's slot list. The slot is recorded
    /// in the zone's list.
    /// </summary>
    static Hand WriteSlot(SaveFile file, SaveZone zone, FcsRecord state, BuildSite site, ulong seed)
    {
        // The slot: kept while the state has one in this zone, else the next free one of the zone's slot list.
        if (file.SlotLists.Count == 0) file.SlotLists.Add([]);
        var hand = Hand.Read(state, "handle");
        if (hand.IsNull || hand.C != zone.ContainerId)
        {
            int listed = file.SlotLists[0].DefaultIfEmpty(0).Max();
            int stored = file.Records.Where(r => r.RecordType == FcsRecordType.GamestateBuilding)
                .Select(r => Hand.Read(r, "handle")).Where(h => !h.IsNull && h.C == zone.ContainerId).Select(h => h.I).DefaultIfEmpty(0).Max();
            int slot = Math.Max(listed, stored) + 1;
            hand = new Hand((int)FcsRecordType.Building, zone.ContainerId, ContainerSerial, slot, (int)Rng.Mix(Rng.StableHash(site.Id) ^ seed));
        }

        if (!file.SlotLists[0].Contains(hand.I)) file.SlotLists[0] = [.. file.SlotLists[0].Append(hand.I).Order()];
        return hand;
    }

    /// <summary>
    /// Writes a site's keys into its state record.
    /// </summary>
    static void WriteSiteKeys(FcsRecord state, BuildSite site, string playerFactionId, Hand hand, Hand town, Locks.FixtureRegistry fixtures)
    {
        state.Floats["world Y pos"] = site.Position.Y;
        state.Floats["construction progress"] = site.Progress;
        for (int i = 0; i < site.Materials.Length; i++)
            state.Floats[$"{i}construction mats"] = site.Materials[i].Delivered;

        state.Ints["version"] = StateVersion;
        state.Ints["flags"] = site.Parent is null ? 1 : 0;   // bit 0: is outside
        state.Ints["floornum"] = site.Floor;
        state.Ints["serialised interior"] = 1;
        hand.Write(state, "handle");
        town.Write(state, "town");
        if (site.Town == 0 && site.UnresolvedTown) state.Bools["meitou unresolved town"] = true;
        else state.Bools.Remove("meitou unresolved town");
        Hand.Null.Write(state, "residents");
        state.Bools["foliage"] = false;
        state.Bools["pause"] = site.Paused;
        state.Bools["is public day"] = site.Public;
        state.Bools["is complete"] = site.Complete;
        state.Bools["is inside building"] = site.Parent is not null;
        state.Bools["destroyed"] = site.Destroyed;
        state.Strings["squad template"] = "";
        state.Strings["owner faction ID"] = playerFactionId;
        state.Strings["residents template"] = "";
        state.Strings["name"] = site.Name;

        // Its doors (a finished building has them registered): the same keys as a town building's (docs/game/locks.md "Save keys").
        fixtures.WriteBuilding(site.Id, state);
    }

    /// <summary>
    /// Writes the player's outposts into <c>quick.save</c> (docs/formats/save.md "Player outposts"; the original's town-list writer
    /// <c>FUN_14092a6c0</c> and <c>Town::vfunc_17/85</c>, <b>Verified</b> by code, no sample save has one): an entry of the
    /// GAMESTATE_TOWN_INSTANCE_LIST (made when the save has none) with id <c>&lt;n&gt;-quick.save-INGAME</c>, target
    /// <see cref="PlayerTown.Template"/>, the absolute position and a zero quaternion, and its GAMESTATE_TOWN <c>&lt;id&gt;-S5e</c> named "Town state
    /// &lt;name&gt;". An outpost read from a save keeps its entry, state and handle; a new one gets the next town container. Returns each outpost's
    /// handle by number, for the buildings' <c>town</c>.
    /// </summary>
    static Dictionary<int, Hand> WriteTowns(SaveGame game, World world, string playerFactionId, ulong seed)
    {
        var hands = new Dictionary<int, Hand>();
        var towns = world.Buildings.Towns;
        if (towns.Count == 0) return hands;

        var quick = game.Quick;
        var byId = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
        foreach (var r in quick.Records) byId[r.StringId] = r;
        var list = game.TownInstanceList ?? AddTownInstanceList(game, quick);
        int container = game.Towns.Select(t => t.Handle).Where(h => !h.IsNull).Select(h => h.C).DefaultIfEmpty(0).Max();
        foreach (var town in towns)
        {
            var instance = town.SaveInstance is { } known ? list.Instances.FirstOrDefault(i => i.Id == known) : null;
            var state = instance is { States.Count: > 0 } ? byId.GetValueOrDefault(instance.States[0]) : null;
            if (state is { RecordType: not FcsRecordType.GamestateTown }) state = null;
            if (instance is null)
            {
                instance = new FcsInstance { Id = QuickId(NextQuick(quick)) };
                list.Instances.Add(instance);
            }

            bool fresh = state is null;
            if (state is null)
            {
                int n = NextQuick(quick);
                state = new FcsRecord { Type = (int)FcsRecordType.GamestateTown, Id = n, StringId = instance.Id + TownStateSuffix };

                // With the other town states, before the list (the original's order).
                int at = quick.Records.IndexOf(list);
                quick.Records.Insert(at < 0 ? quick.Records.Count : at, state);
                byId[state.StringId] = state;
                game.Towns.Add(new SaveTown(state));
            }

            instance.Target = PlayerTown.Template;
            instance.Position = town.Position;
            instance.Rotation = new Quaternion(0, 0, 0, 0);
            instance.States.Clear();
            instance.States.Add(state.StringId);

            var hand = town.SaveHandle ?? (Hand.Read(state, "hand") is { IsNull: false } h
                ? h
                : new Hand((int)FcsRecordType.Town, ++container, (int)Rng.Mix(Rng.StableHash(instance.Id) ^ seed), 0, 0));
            hands[town.Number] = hand;

            state.Name = TownStatePrefix + town.Name;
            if (fresh) WriteFreshTownKeys(state);

            state.Bools["public"] = town.Public;   // Town +0x380, the panel's Open to public
            hand.Write(state, "hand");
            state.Strings["faction"] = playerFactionId;
            state.Strings["instance"] = instance.Id;
            state.Ints["override"] = (int)town.Override;
            if (world.SystemOf<PopulationSystem>() is { } population)
                state.Ints["plevel"] = population.TownLevel(world, town.Number);
            var zone = WorldLayout.ZoneOf(town.Position.X, town.Position.Z);
            state.Ints["zzX0"] = zone.X;
            state.Ints["zzY0"] = zone.Y;
        }

        return hands;
    }

    /// <summary>
    /// The keys a new town state gets besides the ones every write sets.
    /// </summary>
    static void WriteFreshTownKeys(FcsRecord state)
    {
        // Town::vfunc_85 / TownBase's keys; FUN_140934e10 clears `public`. `discovered` and `explored` are Meitou's choice (the player made it).
        foreach (var k in new[] { "started", "recently discovered", "ded", "is nest", "public" }) state.Bools[k] = false;

        state.Bools["discovered"] = true;
        state.Bools["explored"] = true;
        foreach (var k in new[] { "pop", "pop2", "popd", "popd2", "tod" }) state.Floats[k] = 0;

        state.Ints["artifacts count"] = 0;
        state.Ints["plevel"] = 0;
        state.Ints["override"] = 0;
        state.Strings["building material"] = DefaultTownMaterial;
        state.Strings["replacementTown"] = "";
    }

    /// <summary>
    /// Adds the save's town instance list to <c>quick.save</c>, for a save that has none.
    /// </summary>
    static FcsRecord AddTownInstanceList(SaveGame game, SaveFile quick)
    {
        int n = NextQuick(quick);
        var list = new FcsRecord { Type = (int)FcsRecordType.GamestateTownInstanceList, Id = n, Name = "0", StringId = QuickId(n) };
        quick.Records.Add(list);
        game.TownInstanceList = list;
        return list;
    }

    /// <summary>
    /// The id of number <paramref name="n"/> in <c>quick.save</c>.
    /// </summary>
    static string QuickId(int n) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{n}-{SaveFolder.QuickSaveName}-INGAME");

    /// <summary>
    /// Takes the next number of <c>quick.save</c>'s id counter.
    /// </summary>
    static int NextQuick(SaveFile file)
    {
        int n = Math.Max(file.Data.NextId, file.Records.Count == 0 ? 0 : file.Records.Max(r => Math.Max(r.Id, SaveGame.NumberOf(r)))) + 1;
        file.Data.NextId = n;
        return n;
    }

    /// <summary>
    /// One more than the highest numeric id of the file's records.
    /// </summary>
    static int NextNumber(SaveFile file) =>
        file.Records.Count == 0 ? Math.Max(file.Data.NextId, 0) + 1 : Math.Max(file.Data.NextId, file.Records.Max(r => r.Id)) + 1;

    #endregion
}
