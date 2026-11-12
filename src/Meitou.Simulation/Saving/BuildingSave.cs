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
    /// Meitou's key on the town instance list for the highest outpost handle container ever given (the original has none).
    /// </summary>
    const string TownContainerKey = "meitou town container";
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
        var townOf = ReadTowns(save, world, db, playerFactionId, playerIndex, population);
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

        // The saved centre and radius stay as saved: the original refits a town only on a placement or dismantle (loadGame has no reassessment).
        return made;
    }

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
    static Dictionary<(int C, int CS), int> ReadTowns(SaveGame save, World world, GameDatabase db, string playerFactionId, int playerIndex,
        PopulationData? population)
    {
        var byId = save.Quick.Records.ToDictionary(record => record.StringId, StringComparer.Ordinal);
        var byInstance = new Dictionary<string, PlayerTown>(StringComparer.Ordinal);

        if (save.TownInstanceList is { } list)
            world.Buildings.TownContainerFloor = list.Ints.GetValueOrDefault(TownContainerKey);

        // The original instantiates the towns of the level data first, then the save's list entries (FUN_140373f00), and normalises overlaps in that
        // order; a level town that also has a list entry is read from the entry, at the level town's place in the order.
        var read = new HashSet<string>(StringComparer.Ordinal);
        ReadLevelTowns(save, population, world, db, playerFactionId, playerIndex, byInstance, byId, read);
        if (save.TownInstanceList is { } instances)
            foreach (var instance in instances.Instances.Where(i => !read.Contains(i.Id)))
                ReadTownInstance(instance, byId, save, world, db, playerFactionId, playerIndex, byInstance);

        NormalizeTownOverlap(world);
        return TownHandles(world);
    }

    /// <summary>
    /// Imports one nonremoved player-owned town after collecting all its runtime states.
    /// </summary>
    static void ReadTownInstance(FcsInstance instance, Dictionary<string, FcsRecord> byId, SaveGame save, World world,
        GameDatabase db, string playerFactionId, int playerIndex, Dictionary<string, PlayerTown> byInstance)
    {
        if (instance.IsRemoved) return;

        var states = PlayerTownStates(save, instance, instance.Id, instance.Target, byId, db, playerFactionId);
        if (states is null) return;

        var town = byInstance.GetValueOrDefault(instance.Id) ?? world.Buildings.AddTown(instance.Position, playerIndex);
        byInstance[instance.Id] = town;
        world.Buildings.ImportedTownInstances.Add(instance.Id);
        if (instance.Target != PlayerTown.Template) town.SaveTemplate = instance.Target;
        ApplyTownStates(town, instance.Id, instance.Position, states);
    }

    /// <summary>
    /// Enumerates town states linked by state id or instance id, without repeating the same record.
    /// </summary>
    static IEnumerable<FcsRecord> TownStates(FcsInstance instance, Dictionary<string, FcsRecord> byId, SaveGame save)
    {
        var seen = new HashSet<FcsRecord>(ReferenceEqualityComparer.Instance);
        foreach (var id in instance.States)
            if (byId.GetValueOrDefault(id) is { RecordType: FcsRecordType.GamestateTown } state && seen.Add(state)) yield return state;

        foreach (var state in save.Towns.Select(town => town.Record).Where(state => state.Strings.GetValueOrDefault("instance") == instance.Id))
            if (seen.Add(state)) yield return state;
    }

    /// <summary>
    /// A town state as the original applies it: the state, whether its owner was the player at that point and the TOWN template's
    /// <c>is public</c> (what a foreign owner's application leaves <c>public</c> as).
    /// </summary>
    readonly record struct AppliedState(FcsRecord Record, bool Player, bool TemplatePublic);

    /// <summary>
    /// Applies the attached state, then the full runtime states in save order, returning them (with the owner at each) only if the final owner is
    /// the player. Of the attached states only the LAST listed one applies (<b>Verified</b>, <c>FUN_1400f40e0</c>: the instance keeps its states in
    /// a map by record type).
    /// </summary>
    static List<AppliedState>? PlayerTownStates(SaveGame save, FcsInstance? instance, string instanceId, string? template,
        Dictionary<string, FcsRecord> byId, GameDatabase? db, string playerFactionId)
    {
        var applied = new List<AppliedState>();
        string? owner = TownFaction(null, template, db, playerFactionId);
        bool templatePublic = template is not null && db?.Find(template) is { Type: FcsRecordType.Town } record && record.GetBool("is public");
        var attached = instance?.States.Select(id => byId.GetValueOrDefault(id)).LastOrDefault(state => state is { RecordType: FcsRecordType.GamestateTown });
        if (attached is not null && !attached.Bools.GetValueOrDefault("is nest"))
        {
            owner = TownFaction(attached, template, db, playerFactionId);
            applied.Add(new AppliedState(attached, owner == playerFactionId, templatePublic));
        }

        foreach (var town in save.Towns)
        {
            if (town.Instance != instanceId || town.IsNest || owner == playerFactionId && !town.Discovered) continue;

            owner = TownFaction(town.Record, template, db, playerFactionId);
            applied.Add(new AppliedState(town.Record, owner == playerFactionId, templatePublic));
        }

        return owner == playerFactionId ? applied : null;
    }

    /// <summary>
    /// Resolves this state's faction, then the TOWN template's faction reference, otherwise no faction.
    /// </summary>
    static string? TownFaction(FcsRecord? state, string? template, GameDatabase? db, string playerFactionId)
    {
        string? faction = ResolveFaction(state?.Strings.GetValueOrDefault("faction"), db, playerFactionId);
        if (faction is not null) return faction;

        var record = template is null ? null : db?.Find(template);
        string? fallback = record is { Type: FcsRecordType.Town }
            ? record.GetReferences("faction").FirstOrDefault().TargetStringId
            : null;
        return ResolveFaction(fallback, db, playerFactionId);
    }

    /// <summary>
    /// The caller's player id is already resolved; other ids must resolve to a FACTION in the game database.
    /// </summary>
    static string? ResolveFaction(string? id, GameDatabase? db, string playerFactionId) =>
        id == playerFactionId || id is not null && db?.Find(id) is { Type: FcsRecordType.Faction } ? id : null;

    /// <summary>
    /// Imports the level towns in the population's order, using the same ordered state rules as dynamic instances: a level town with an entry in the
    /// save's town list is read from that entry. The instance ids read are added to <paramref name="read"/>.
    /// </summary>
    static void ReadLevelTowns(SaveGame save, PopulationData? population, World world, GameDatabase db, string playerFactionId, int playerIndex,
        Dictionary<string, PlayerTown> byInstance, Dictionary<string, FcsRecord> byId, HashSet<string> read)
    {
        if (population is null) return;

        var linked = save.Towns.Select(town => town.Instance).ToHashSet(StringComparer.Ordinal);
        foreach (var site in population.Sites)
        {
            string instanceId = site.InstanceId;
            if (save.TownInstanceList?.Instances.FirstOrDefault(instance => instance.Id == instanceId) is { } entry)
            {
                read.Add(instanceId);
                ReadTownInstance(entry, byId, save, world, db, playerFactionId, playerIndex, byInstance);
                continue;
            }

            if (!linked.Contains(instanceId)) continue;

            var states = PlayerTownStates(save, null, instanceId, site.Town.Id, byId, db, playerFactionId);
            if (states is null) continue;

            var town = byInstance.GetValueOrDefault(instanceId) ?? world.Buildings.AddTown(site.Position, playerIndex);
            byInstance[instanceId] = town;
            world.Buildings.ImportedTownInstances.Add(instanceId);
            town.SaveTemplate = site.Record.Id;
            ApplyTownStates(town, instanceId, site.Position, states);
        }
    }

    /// <summary>
    /// Combines linked town properties in save order, restoring runtime names and keeping the first nonnull handle.
    /// </summary>
    static void ApplyTownStates(PlayerTown town, string instanceId, Vector3 position, IEnumerable<AppliedState> states)
    {
        town.SaveInstance = instanceId;
        town.Position = position;
        foreach (var (state, player, templatePublic) in states)
        {
            string name = TownName(state);
            if (name.Length > 0 && (state.Strings.GetValueOrDefault("name", "").Length > 0 || town.Name == PlayerTown.DefaultName))
                town.Name = name;

            // FUN_1403715c0 (Verified): `public` is assigned by every application (a missing key is false) and for a foreign owner replaced by the
            // template's `is public`; `override` is read only for the player's own state and only when present.
            town.Public = player ? state.Bools.GetValueOrDefault("public") : templatePublic;
            if (player && state.Floats.TryGetValue("override", out float radius)) town.Override = radius;
            else if (player && state.Ints.TryGetValue("override", out int oldRadius)) town.Override = oldRadius;   // earlier Meitou builds wrote an integer

            var hand = Hand.Read(state, "hand");
            if (!hand.IsNull && town.SaveHandle is null) town.SaveHandle = hand;
        }
    }

    /// <summary>
    /// Restores the runtime display name, with a record-label fallback for older Meitou saves.
    /// </summary>
    static string TownName(FcsRecord state)
    {
        if (state.Strings.GetValueOrDefault("name") is { Length: > 0 } name) return name;
        if (!state.Name.StartsWith(TownStatePrefix, StringComparison.Ordinal)) return "";

        return state.Name[TownStatePrefix.Length..];
    }

    /// <summary>
    /// Removes a player town strictly inside its nearest other player town without changing building handles or town numbers.
    /// </summary>
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

    /// <summary>
    /// Maps surviving saved town container identities to their live outpost numbers.
    /// </summary>
    static Dictionary<(int C, int CS), int> TownHandles(World world)
    {
        var map = new Dictionary<(int, int), int>();
        foreach (var town in world.Buildings.Towns)
            if (town.SaveHandle is { } hand) map.TryAdd((hand.C, hand.Cs), town.Number);

        return map;
    }

    /// <summary>
    /// The squared horizontal distance between two saved town centres.
    /// </summary>
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
    /// handle each player town (by number) was written with. The game database resolves template ownership when pruning obsolete outposts;
    /// production callers supply it, with the population system as a fallback.
    /// </summary>
    public static IReadOnlyDictionary<int, Hand> Write(SaveGame game, World world, string playerFactionId, ulong seed = 0, GameDatabase? db = null)
    {
        var reg = world.Buildings;
        var live = reg.All.ToDictionary(s => s.Id, s => s.LevelPlacement is null ? s.Zone : (ZoneCoordinate?)null, StringComparer.Ordinal);
        RemoveDismantled(game, playerFactionId, live);
        var townHands = WriteTowns(game, world, playerFactionId, seed, db ?? world.SystemOf<PopulationSystem>()?.Data.Db);
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
        state.Bools.Remove("meitou unresolved town");
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
    /// Writes player outpost placements and their states into quick.save (docs/formats/save.md "Player outposts"). The original has sparse
    /// instance-linked states and separate full runtime states; Meitou synchronizes live properties across existing non-nest states. Saved
    /// instances and handles are retained; new outposts receive the next town container. Returns handles for the buildings' town references.
    /// </summary>
    static Dictionary<int, Hand> WriteTowns(SaveGame game, World world, string playerFactionId, ulong seed, GameDatabase? db)
    {
        var hands = new Dictionary<int, Hand>();
        var towns = world.Buildings.Towns;
        var quick = game.Quick;
        var byId = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
        foreach (var r in quick.Records) byId[r.StringId] = r;
        RemoveObsoleteTowns(game, world, playerFactionId, byId, db);
        if (towns.Count == 0) return hands;

        FcsRecord? list = null;
        int container = Math.Max(world.Buildings.TownContainerFloor, game.Towns.Select(t => t.Handle).Where(h => !h.IsNull).Select(h => h.C).DefaultIfEmpty(0).Max());
        foreach (var town in towns)
        {
            list ??= game.TownInstanceList ?? AddTownInstanceList(game, quick);
            var instance = town.SaveInstance is { } known ? list.Instances.FirstOrDefault(i => i.Id == known) : null;
            if (instance is null)
            {
                instance = new FcsInstance { Id = town.SaveInstance ?? QuickId(NextQuick(quick)) };
                list.Instances.Add(instance);
            }

            string id = instance.Id;
            var states = TownStates(instance, byId, game).Where(state => !state.Bools.GetValueOrDefault("is nest")).ToList();
            var state = states.FirstOrDefault();
            bool fresh = state is null;
            if (state is null)
            {
                int n = NextQuick(quick);
                state = new FcsRecord { Type = (int)FcsRecordType.GamestateTown, Id = n, StringId = id + TownStateSuffix };

                // With the other town states, before the list (the original's order).
                int at = quick.Records.IndexOf(list);
                quick.Records.Insert(at < 0 ? quick.Records.Count : at, state);
                byId[state.StringId] = state;
                game.Towns.Add(new SaveTown(state));
                states.Add(state);
            }

            // Every player town gets an entry, a level town too (the writer walks the player list, FUN_14092a6c0), with the TOWN it is made from.
            instance.Target = town.SaveTemplate ?? PlayerTown.Template;
            instance.Position = town.Position;
            instance.Rotation = new Quaternion(0, 0, 0, 0);
            instance.States.Clear();
            instance.States.AddRange(states.Select(linked => linked.StringId));

            var hand = town.SaveHandle ?? (Hand.Read(state, "hand") is { IsNull: false } h
                ? h
                : new Hand((int)FcsRecordType.Town, ++container, (int)Rng.Mix(Rng.StableHash(id) ^ seed), 0, 0));
            hands[town.Number] = hand;
            world.Buildings.ImportedTownInstances.Add(id);

            if (fresh) WriteFreshTownKeys(state);
            foreach (var linked in states) WriteTownState(world, town, id, linked, hand, playerFactionId);
        }

        world.Buildings.TownContainerFloor = container;
        if (list is not null) list.Ints[TownContainerKey] = container;
        return hands;
    }

    /// <summary>
    /// Writes the same live town properties to each linked runtime state so neither load pass can restore stale values.
    /// </summary>
    static void WriteTownState(World world, PlayerTown town, string instanceId, FcsRecord state, Hand hand, string playerFactionId)
    {
        state.Name = TownStatePrefix + town.Name;
        state.Strings["name"] = town.Name;
        state.Bools["public"] = town.Public;   // Town +0x380, the panel's Open to public
        hand.Write(state, "hand");
        state.Strings["faction"] = playerFactionId;
        state.Strings["instance"] = instanceId;
        state.Ints.Remove("override");
        state.Floats["override"] = town.Override;
        if (world.SystemOf<PopulationSystem>() is { } population)
            state.Ints["plevel"] = population.TownLevel(world, town.Number);
        var zone = WorldLayout.ZoneOf(town.Position.X, town.Position.Z);
        state.Ints["zzX0"] = zone.X;
        state.Ints["zzY0"] = zone.Y;
    }

    /// <summary>
    /// Reconciles saved player outposts with the live registry: removes only what was imported as a player town and is gone now, preserving
    /// foreign towns, nests and player-faction states that were never imported.
    /// </summary>
    static void RemoveObsoleteTowns(SaveGame game, World world, string playerFactionId, Dictionary<string, FcsRecord> byId, GameDatabase? db)
    {
        var imported = world.Buildings.ImportedTownInstances;
        var live = world.Buildings.Towns.Select(town => town.SaveInstance).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (game.TownInstanceList is { } list)
        {
            foreach (var instance in list.Instances.ToList())
            {
                if (live.Contains(instance.Id) || !imported.Contains(instance.Id)) continue;

                list.Instances.Remove(instance);
                RemoveTownStates(game, TownStates(instance, byId, game).Where(state => !state.Bools.GetValueOrDefault("is nest")).ToList());
            }
        }

        foreach (var group in game.Towns.GroupBy(town => town.Instance).ToList())
        {
            if (live.Contains(group.Key) || !imported.Contains(group.Key)) continue;
            if (game.TownInstanceList?.Instances.Any(instance => instance.Id == group.Key) == true) continue;

            RemoveTownStates(game, group.Where(town => !town.IsNest).Select(town => town.Record).ToList());
        }

        imported.ExceptWith(imported.Where(id => !live.Contains(id)).ToList());
    }

    /// <summary>
    /// Removes obsolete town states from both the serialized records and their indexed views.
    /// </summary>
    static void RemoveTownStates(SaveGame game, List<FcsRecord> states)
    {
        foreach (var state in states) game.Quick.Records.Remove(state);
        game.Towns.RemoveAll(town => states.Contains(town.Record));
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
        state.Floats["override"] = 0;
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
