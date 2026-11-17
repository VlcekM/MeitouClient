using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Production;

/// <summary>
/// The machines of NPC towns (docs/game/buildings-production.md "NPC town machines"): a town building an NPC worker (goals 189 / 190, AiSystem Work)
/// stands at is a machine like a rock or a training dummy is: no registry site, so neither drawn nor built; a complete virtual site of the
/// town's faction, made when its town registers (a farm or producer, <see cref="TownMachines"/>) or when the first worker or the player's order
/// comes, its state saved in the building's GAMESTATE_BUILDING (Saving.TownMachineSave). The workers are the AI's: the decision step keeps
/// AiMind.WorkOperate (Task_OperateMachine, row 87) or AiMind.CollectFrom (Task_EmptyMachine, row 92) while they are at the building, and this
/// system applies them.
/// </summary>
public sealed partial class ProductionSystem
{
    /// <summary>
    /// The prefix of a town building's machine site id (followed by the building's run-time key).
    /// </summary>
    public const string TownWorkPrefix = "townwork:";

    /// <summary>
    /// The town buildings' machine sites by id, made with their machines.
    /// </summary>
    readonly Dictionary<string, BuildSite> _townWorkSites = new(StringComparer.Ordinal);

    /// <summary>
    /// The site id of a town building's machine.
    /// </summary>
    public static string TownWorkId(TownBuilding building) => TownWorkPrefix + building.Key;

    /// <summary>
    /// Whether a site id names a town building's machine (<see cref="TownWorkId"/>).
    /// </summary>
    public static bool IsTownWork(string id) => id.StartsWith(TownWorkPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The towns' buildings, for the town turrets the player's orders name (null: no town turret can be ordered).
    /// </summary>
    readonly PopulationData? _towns;

    /// <summary>
    /// The towns' buildings by run-time key with the faction owning their town, the objects of their layouts by <see cref="TownLayoutBuilding.Id"/>
    /// (with their building, the faction owning their town and the town's site index), and the revision of the towns' building lists they were
    /// made from (<see cref="TownBuildingIndex"/>).
    /// </summary>
    sealed record TownIndex(int Revision, Dictionary<string, (TownBuilding Building, int Owner, int Site)> Buildings,
        Dictionary<string, (TownBuilding House, TownLayoutBuilding Piece, int Owner, int Site)> Pieces);

    /// <summary>
    /// The current <see cref="TownIndex"/>, replaced whole: the think phase's power checks read it from every worker (<see cref="MachinePowered"/>).
    /// </summary>
    volatile TownIndex? _townIndex;

    /// <summary>
    /// Held while <see cref="_townIndex"/> is made again, so two workers that find it out of date do not both make it.
    /// </summary>
    readonly Lock _townIndexGate = new();

    /// <summary>
    /// The registered towns (site indices) whose buildings have been given their machines (<see cref="TownMachines"/>); cleared when the towns'
    /// lists change.
    /// </summary>
    readonly HashSet<int> _townSitesMade = [];

    /// <summary>
    /// The machine buildings and layout objects (<paramref name="Piece"/> set) of registered towns whose machine is not made yet (in a ruin until it
    /// stands), with the faction owning their town.
    /// </summary>
    readonly List<(TownBuilding Building, TownLayoutBuilding? Piece, int Owner)> _townWaiting = [];

    /// <summary>
    /// The ruins and towns' building lists the layout objects' machines were last checked against (<see cref="DropGonePieces"/>).
    /// </summary>
    (long Ruins, int Buildings) _piecesChecked = (-1, -1);

    /// <summary>
    /// The layout objects whose machine went with a ruin or a new layout: one made again is new, not the save's (<see cref="RestorePiece"/>).
    /// </summary>
    readonly HashSet<string> _piecesDestroyed = new(StringComparer.Ordinal);

    /// <summary>
    /// How many of <see cref="FixtureRegistry.RebuiltHouses"/> had their objects' machines dropped (<see cref="DropRebuilt"/>).
    /// </summary>
    int _rebuiltSeen;

    /// <summary>
    /// The research contributions the town and outpost workers made this tick (<see cref="TownOperate"/>), which the player's operators continue
    /// (<c>FUN_140836b70</c>'s falloff counts every bench worked in the tick; serial).
    /// </summary>
    int _townResearch;

    /// <summary>
    /// What a town machine just made takes from the loaded save: its building's production keys and items (set by the save loader,
    /// <c>TownMachineSave.Restorer</c>); null: nothing (a new game).
    /// </summary>
    public Action<TownBuilding, Machine>? RestoreTownMachine { get; set; }

    /// <summary>
    /// What the machine of a town building's layout object takes from the loaded save, as <see cref="RestoreTownMachine"/>: the state of the
    /// object's own building-list entry (<c>TownMachineSave.PieceRestorer</c>); null: nothing.
    /// </summary>
    public Action<TownLayoutBuilding, Machine>? RestorePieceMachine { get; set; }

    /// <summary>
    /// The counter of a town farm's random start among its farm draws (above the plantings' and the harvest rolls').
    /// </summary>
    const ulong RandomStart = 1UL << 41;

    /// <summary>
    /// How far a loaded farm's plants grow above its growth at most (<see cref="SpreadPlants"/>).
    /// </summary>
    const float PlantSpread = 0.2f;

    /// <summary>
    /// The virtual site of a town building made by <see cref="TownWork"/>, else null.
    /// </summary>
    internal BuildSite? TownWorkSite(string id) => _townWorkSites.GetValueOrDefault(id);

    /// <summary>
    /// The site of the town building a player's order names by its <see cref="TownWorkId"/> (a turret's 146 USE_TURRET, a machine's 87 or its
    /// loot, docs/game/ui-input.md "Context menu verbs": whoever owns it), made with its machine on the first order (serial: the commands' phase);
    /// null for an id naming no standing town building that is a machine.
    /// </summary>
    BuildSite? TownMachineSite(World world, string id)
    {
        if (!IsTownWork(id)) return null;
        if (TownWorkSite(id) is { } known) return known;
        if (TownBuildingOf(id[TownWorkPrefix.Length..]) is not { } found || world.Fixtures.IsDestroyed(found.Building)) return null;

        TownWork(world, found.Building, found.Owner);
        return TownWorkSite(id);
    }

    /// <summary>
    /// The town building of a run-time key and the faction owning its town; null for none.
    /// </summary>
    (TownBuilding Building, int Owner, int Site)? TownBuildingOf(string key) =>
        TownBuildingIndex() is { } index && index.Buildings.TryGetValue(key, out var found) ? found : null;

    /// <summary>
    /// The layout object of a town building of <see cref="TownLayoutBuilding.Id"/> <paramref name="key"/>, with its building, the faction owning its
    /// town and the town's site index; null for none.
    /// </summary>
    (TownBuilding House, TownLayoutBuilding Piece, int Owner, int Site)? TownPieceOf(string key) =>
        TownBuildingIndex() is { } index && index.Pieces.TryGetValue(key, out var found) ? found : null;

    /// <summary>
    /// The site index of the town a town machine's site id names: its building's, or for a layout object's machine its building's
    /// (<c>FUN_140296940</c> climbs from the object to the building it stands in); null for none.
    /// </summary>
    int? TownSiteOf(string siteId)
    {
        string key = siteId[TownWorkPrefix.Length..];
        return TownBuildingOf(key)?.Site ?? TownPieceOf(key)?.Site;
    }

    /// <summary>
    /// The towns' buildings by run-time key with the faction owning their town, and their layout objects (<see cref="TownIndex"/>), made again
    /// when the lists change (<see cref="PopulationData.BuildingsRevision"/>, which also has <see cref="TownMachines"/> look at every registered
    /// town again); null without towns.
    /// </summary>
    TownIndex? TownBuildingIndex()
    {
        if (_towns is not { } data) return null;
        if (_townIndex is { } current && current.Revision == data.BuildingsRevision) return current;

        lock (_townIndexGate)
        {
            if (_townIndex is { } made && made.Revision == data.BuildingsRevision) return made;

            var buildings = new Dictionary<string, (TownBuilding Building, int Owner, int Site)>(StringComparer.Ordinal);
            var pieces = new Dictionary<string, (TownBuilding House, TownLayoutBuilding Piece, int Owner, int Site)>(StringComparer.Ordinal);
            foreach (var site in data.Sites)
            {
                int owner = site.Index < data.SiteFactions.Count ? data.SiteFactions[site.Index] : -1;
                foreach (var b in site.Buildings)
                {
                    buildings[b.Key] = (b, owner, site.Index);
                    foreach (var piece in b.LayoutBuildings) pieces[piece.Id] = (b, piece, owner, site.Index);
                }
            }

            _townSitesMade.Clear();
            _townWaiting.Clear();
            return _townIndex = new TownIndex(data.BuildingsRevision, buildings, pieces);
        }
    }

    /// <summary>
    /// The machine of a town building or of a layout object of one by its key (<see cref="TownLayoutBuilding.Id"/>), made on the first call
    /// (<see cref="TownWork"/>, <see cref="PieceWork"/>); null for a key naming neither, one that is no machine, or an object of a ruin (which holds
    /// none, <see cref="DropGonePieces"/>). Serial phases only.
    /// </summary>
    public Machine? TownWork(World world, string key)
    {
        if (TownBuildingOf(key) is { } found) return TownWork(world, found.Building, found.Owner);
        if (TownPieceOf(key) is not { } piece || world.Fixtures.IsDestroyed(piece.House)) return null;

        return PieceWork(world, piece.House, piece.Piece, piece.Owner);
    }

    /// <summary>
    /// The machine of a town building (made on the first call: empty, owned by the town's faction <paramref name="owner"/>, or by the player's when
    /// the level data gives the building to it, <see cref="PopulationData.LevelOwnerOf"/>: a take out of that one is no theft), null for a building
    /// that neither works nor stores. Serial phases only.
    /// </summary>
    public Machine? TownWork(World world, TownBuilding building, int owner)
    {
        string id = TownWorkId(building);
        if (world.Machines.Find(id) is { } known) return known;
        if (!_townWorkSites.TryGetValue(id, out var site))
        {
            _townWorkSites[id] = site = new BuildSite
            {
                Id = id,
                Record = building.BuildingId,
                Name = building.Name,
                Position = building.Position,
                Owner = _towns?.LevelOwnerOf(building, owner) ?? owner,
                Materials = [],
                Complete = true,
                Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, building.Yaw),
            };
        }

        return Made(world, site, m => RestoreTownMachine?.Invoke(building, m));
    }

    /// <summary>
    /// The machine of a town building's layout object, as <see cref="TownWork"/> makes a building's: every object of a layout is a building of its
    /// own, made by the factory as its functionality's class (<c>FUN_14057cc70</c>, the interior's furniture callback), so a hydroponic farm,
    /// refinery or battery bank inside a house has its sections, progress and charge. Its site is complete, the house's owner's, inside the house
    /// for an interior object; its id <c>townwork:&lt;object id&gt;</c>. Serial phases only.
    /// </summary>
    public Machine? PieceWork(World world, TownBuilding house, TownLayoutBuilding piece, int owner)
    {
        string id = TownWorkPrefix + piece.Id;
        if (world.Machines.Find(id) is { } known) return known;
        if (!_townWorkSites.TryGetValue(id, out var site))
        {
            _townWorkSites[id] = site = new BuildSite
            {
                Id = id,
                Record = piece.RecordId,
                Name = _db.Find(piece.RecordId)?.Name ?? "",
                Position = piece.Position,
                Owner = _towns?.LevelOwnerOf(house, owner) ?? owner,
                Materials = [],
                Complete = true,
                Rotation = piece.Rotation,
                Parent = piece.Outdoors ? null : house.Key,
            };
        }

        return Made(world, site, m => RestorePiece(piece, m));
    }

    /// <summary>
    /// A layout object's new machine takes its saved entry (<see cref="RestorePieceMachine"/>), unless the object was destroyed since the load: the
    /// one a house stood up again or laid out anew makes is new.
    /// </summary>
    void RestorePiece(TownLayoutBuilding piece, Machine m)
    {
        if (!_piecesDestroyed.Contains(piece.Id)) RestorePieceMachine?.Invoke(piece, m);
    }

    /// <summary>
    /// A town site's new machine: a farm at its random start, then what the save holds for it (<paramref name="restore"/>), then the plants spread.
    /// </summary>
    Machine? Made(World world, BuildSite site, Action<Machine> restore)
    {
        if (Ensure(world, site) is not { } m) return null;

        // A farm with no saved keys starts at a random growth (Verified, FarmBuilding::loadFromSerialise FUN_1400e3de0: rand() / 32768); a save's
        // keys (TownMachineSave) replace it.
        if (m.Farm is { } farm) farm.Growth = Rng.Float(Rng.Hash(world.Seed, Rng.StableHash(site.Id), RngPurpose.Farm, RandomStart));

        restore(m);
        if (m.Farm is not null) SpreadPlants(world, site.Id, m.Farm);
        return m;
    }

    /// <summary>
    /// A town farm's plants after its load (<b>Verified</b>, decompile: the building factory <c>FUN_140581770</c> runs the farm's load before its
    /// setup, which gives each plant the farm's growth + 0.2 x U[0, 1), the constant at <c>14168a4a8</c>, then marks the first harvested ones -10).
    /// </summary>
    static void SpreadPlants(World world, string id, FarmState farm)
    {
        ulong key = Rng.StableHash(id);
        for (int i = 0; i < farm.Plants.Length; i++)
        {
            float spread = PlantSpread * Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Farm, RandomStart + 1 + (ulong)i));
            farm.Plants[i] = i < farm.Harvested ? -10 : farm.Growth + spread;
        }
    }

    /// <summary>
    /// Every registered town's buildings that are machines get them (serial): the original makes every building of a loaded zone as its class,
    /// ProductionBuilding and FarmBuilding among them, with its production state from the saved GAMESTATE_BUILDING, so a mine's output and a farm's
    /// crop are there from the start for its workers, the player's loot and the AI's input finder; a farm grows and an unattended producer (an
    /// automatic well, max operators 0) works whether anyone is there (ProductionBuilding::vfunc_28 runs it each frame, #763). A ruin waits until it
    /// stands. The objects of its buildings' layouts are buildings of their own and get theirs alike (<see cref="IsPieceMachine"/>).
    /// </summary>
    void TownMachines(World world)
    {
        if (_towns is not { } data || TownBuildingIndex() is null) return;

        DropGonePieces(world, data);
        DropRebuilt(world);
        foreach (var site in data.Sites)
        {
            if (!world.Fixtures.IsRegistered(site.Index) || !_townSitesMade.Add(site.Index)) continue;

            int owner = site.Index < data.SiteFactions.Count ? data.SiteFactions[site.Index] : -1;
            foreach (var b in site.Buildings) WaitForMachines(b, owner);
        }

        if (_townWaiting.Count > 0) _townWaiting.RemoveAll(w => MadeTownMachine(world, w.Building, w.Piece, w.Owner));
    }

    /// <summary>
    /// The machines of layout objects that are gone, checked when the ruins or the towns' lists change: setting a house destroyed destroys its
    /// interior's objects (<c>Building::setDestroyed</c> <c>FUN_140557280</c> -> <c>FUN_14054f4d0</c>, docs/game/buildings-production.md "A ruin
    /// holds no furniture"), and a house laid out again has other objects. Each such machine goes with its items; an object of a house that is a
    /// ruin waits for it to stand again (<see cref="MadeTownMachine"/>) and is made new then.
    /// </summary>
    void DropGonePieces(World world, PopulationData data)
    {
        var seen = (world.Fixtures.RuinsRevision, data.BuildingsRevision);
        if (seen == _piecesChecked) return;
        _piecesChecked = seen;

        List<string>? gone = null;
        foreach (var (id, site) in _townWorkSites)
            if (IsPieceGone(world, id, site)) (gone ??= []).Add(id);
        if (gone is null) return;

        foreach (string id in gone) DropPiece(world, id);
        world.Machines.Touch();
    }

    /// <summary>
    /// The machines of the objects of each house a town override furnished anew since the last look (<see cref="FixtureRegistry.RebuiltHouses"/>)
    /// go with their items, its layout's names the same or not: the override's housing clears the interior and lays it out again
    /// (<c>FUN_14092e4b0</c>), so each object made again is new.
    /// </summary>
    void DropRebuilt(World world)
    {
        var rebuilt = world.Fixtures.RebuiltHouses;
        if (_rebuiltSeen == rebuilt.Count) return;

        for (; _rebuiltSeen < rebuilt.Count; _rebuiltSeen++) DropHouse(world, rebuilt[_rebuiltSeen]);
        world.Machines.Touch();
    }

    /// <summary>
    /// Drops the machines of a house's layout objects and marks its objects destroyed, so none made again takes the save's entry.
    /// </summary>
    void DropHouse(World world, string house)
    {
        string prefix = TownWorkPrefix + house + "/";
        var made = _townWorkSites.Keys
            .Where(id => id.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
        foreach (string id in made) DropPiece(world, id);

        if (TownBuildingOf(house) is not { } found) return;
        foreach (var piece in found.Building.LayoutBuildings) _piecesDestroyed.Add(piece.Id);
    }

    /// <summary>
    /// Whether a town machine site is a layout object's that is gone: no longer in its house's layouts (another record or place under its id) or
    /// its house is a ruin.
    /// </summary>
    bool IsPieceGone(World world, string id, BuildSite site)
    {
        string key = id[TownWorkPrefix.Length..];
        if (!key.Contains('/')) return false;
        if (TownPieceOf(key) is not { } found) return true;

        return found.Piece.RecordId != site.Record || found.Piece.Position != site.Position || world.Fixtures.IsDestroyed(found.House);
    }

    /// <summary>
    /// Drops a gone layout object's machine and site; the object still in a ruin's layout waits for the house to stand.
    /// </summary>
    void DropPiece(World world, string id)
    {
        string key = id[TownWorkPrefix.Length..];
        _townWorkSites.Remove(id);
        world.Machines.Remove(id);
        _piecesDestroyed.Add(key);

        if (TownPieceOf(key) is { } found && IsPieceMachine(Data(found.Piece.RecordId))) _townWaiting.Add((found.House, found.Piece, found.Owner));
    }

    /// <summary>
    /// A registered town's building waits for its machine when it is one, and so does each machine object of its layouts.
    /// </summary>
    void WaitForMachines(TownBuilding b, int owner)
    {
        if (IsTownMachine(Data(b.BuildingId))) _townWaiting.Add((b, null, owner));

        foreach (var piece in b.LayoutBuildings)
            if (IsPieceMachine(Data(piece.RecordId))) _townWaiting.Add((b, piece, owner));
    }

    /// <summary>
    /// Whether a waiting town building or layout object has its machine now: it had one, or its building stands and it gets it (a ruin keeps
    /// waiting).
    /// </summary>
    bool MadeTownMachine(World world, TownBuilding building, TownLayoutBuilding? piece, int owner)
    {
        string id = piece is null ? TownWorkId(building) : TownWorkPrefix + piece.Id;
        if (world.Machines.Find(id) is not null) return true;
        if (world.Fixtures.IsDestroyed(building)) return false;

        if (piece is null) TownWork(world, building, owner);
        else PieceWork(world, building, piece, owner);
        return true;
    }

    /// <summary>
    /// Whether a layout object of <paramref name="data"/> gets its machine when its town registers: what a town building gets one for
    /// (<see cref="IsTownMachine"/>: an interior hydroponic farm, a refinery), and a battery bank, whose charge the town's frame moves
    /// (docs/game/buildings-production.md "Power"). A layout's generators are the frame's as a town's are (<see cref="NpcOutput"/>).
    /// </summary>
    static bool IsPieceMachine(MachineData? data) => IsTownMachine(data) || data is { IsBattery: true };

    /// <summary>
    /// Whether a town building of <paramref name="data"/> gets its machine when its town registers: a farm or a producer (a mine, well, refinery
    /// or rain collector), whose state its workers and the hours change. The other functionalities hold nothing an NPC changes: a research bench
    /// counts for the player's research alone, a crafting bench has no queue, a town's generators are the power frame's
    /// (<see cref="NpcGeneration"/>), and its storages are container fixtures; those get a machine only when a worker or the player's order
    /// comes (<see cref="TownWork"/>).
    /// </summary>
    static bool IsTownMachine(MachineData? data) => data is { Farm: not null } or { IsProducer: true };

    /// <summary>
    /// A town building's machine as the holder of its faction's goods (<see cref="Crime.StealSource"/>: the building is its own container, and it
    /// stands in the building its <c>isInsideBuilding</c> handle names, a layout object in its house, whose residents watch and own it); null for
    /// any other site.
    /// </summary>
    public Crime.StealSource? TownGoods(string site)
    {
        if (!IsTownWork(site) || TownWorkSite(site) is not { } s) return null;

        string key = site[TownWorkPrefix.Length..];
        return new(site, s.Owner, key, s.Position, s.Record, TownBuildingOf(key)?.Building.InsideOf ?? s.Parent);
    }

    /// <summary>
    /// A take out of another faction's town machine (<see cref="Take"/>): the stolen mark the taken items get
    /// (<see cref="Crime.CrimeSystem.OwnerMark"/>; none for any other take), then, when the taker has <paramref name="room"/> for them with it, the
    /// Theft and the steal roll (<see cref="Crime.CrimeSystem.Steal"/>), else the Theft alone (no roll, no thievery XP, #796). True: caught or no
    /// room, nothing moves.
    /// </summary>
    static bool CaughtTaking(World world, Crime.StealSource? goods, CharacterId taker, CharacterCold cold, Func<StolenMark, bool> room,
        out StolenMark mark)
    {
        mark = StolenMark.None;
        if (goods is not { Owner: >= 0 } g || g.Owner == cold.Faction || !Crime.CrimeSystem.IsStealing(world, g)) return false;

        mark = Crime.CrimeSystem.OwnerMark(world, g.Building, g.Owner);
        bool fits = room(mark);
        if (world.SystemOf<Crime.CrimeSystem>() is not { } crimes || !world.Characters.TryResolveNext(taker, out int thief)) return !fits;

        if (!fits) crimes.PendSteal(world, thief, g, Crime.CrimeSystem.TakeKind.RightClick);
        return !fits || crimes.Steal(world, thief, g, Crime.CrimeSystem.TakeKind.RightClick);
    }

    /// <summary>
    /// State 66 <c>haveSomeResourcesFrom</c> (<c>FUN_140597530</c>, <b>Observed</b>): the character carries the machine's output item.
    /// </summary>
    public static bool CarriesOutputOf(CharacterCold cold, Machine m) => m.Output is { } o && Carries(cold, o.Recipe.Item);

    /// <summary>
    /// State 67 <c>haveSomeResourcesFor</c> (<c>FUN_1405a45f0</c>, <b>Observed</b>): the character carries an item one of the machine's inputs takes.
    /// </summary>
    public static bool CarriesInputOf(CharacterCold cold, Machine m) => m.Inputs.Any(input => input.Rate > 0 && Carries(cold, input.Item));

    /// <summary>
    /// Whether the character carries <paramref name="item"/> (in its inventory or its backpack's).
    /// </summary>
    static bool Carries(CharacterCold cold, string item) =>
        cold.Inventory is { } inv && (inv.All().Any(i => i.Record == item) || inv.Backpack?.Contents?.All().Any(i => i.Record == item) == true);

    /// <summary>
    /// The finder of row 94 (<c>FUN_14059d9b0</c>, scored by <c>FUN_14059d000</c>, <b>Verified</b>): for each item type the target machine's inputs
    /// take, in list order, the nearest other machine whose output holds that item and has some; the first item with one decides. <b>Not
    /// modelled</b>: the cached earlier pick, loose items on the ground (searched first by the original), and the split of buildings by the owner
    /// test of vfunc <c>0x3b0</c> (<b>Unknown</b>); the candidates are the town's workplaces (the original's building mask is not decoded).
    /// </summary>
    public static TownBuilding? InputSource(World world, Machine target, IEnumerable<TownBuilding> candidates, Vector3 from)
    {
        foreach (var input in target.Inputs)
        {
            TownBuilding? best = null;
            float bestD = float.MaxValue;
            foreach (var b in candidates)
            {
                if (TownWorkOf(world, b) is not { Output: { Count: > 0 } o } other || ReferenceEquals(other, target) || o.Recipe.Item != input.Item)
                    continue;

                float d = Vector3.DistanceSquared(new Vector3(from.X, 0, from.Z), new Vector3(b.Position.X, 0, b.Position.Z));
                if (d < bestD) (best, bestD) = (b, d);
            }

            if (best is not null) return best;
        }

        return null;
    }

    /// <summary>
    /// <c>Task_FillMachine</c> (row 93, <c>FUN_140344510</c>, <b>Verified</b>) at a machine with a production component: the carried items (inventory
    /// and worn backpack) its inputs accept go into them up to each input's capacity, a unit at a time.
    /// </summary>
    void FillInputs(World world, CharacterCold cold, Machine m)
    {
        if (cold.Inventory is not { } inv) return;
        bool moved = false;
        foreach (var input in m.Inputs)
        {
            if (input.Rate <= 0) continue;
            foreach (var item in inv.All().Concat(inv.Backpack?.Contents?.All() ?? []).Where(i => i.Record == input.Item).ToList())
            {
                int n = Math.Min(input.Recipe.Capacity - input.Count, item.Quantity);
                if (n <= 0) break;
                input.Count += n;
                item.Quantity -= n;
                if (item.Quantity == 0) (inv.Items.Contains(item) ? inv : inv.Backpack!.Contents!).Remove(item);
                moved = true;
            }
        }

        if (moved) world.Machines.Touch();
    }

    /// <summary>
    /// The machine a town building has made, null before its town registered or a worker came; for an outpost's workplace (a stand-in known by its
    /// site id, <see cref="TownBuilding.RuntimeKey"/>) the player's own machine.
    /// </summary>
    public static Machine? TownWorkOf(World world, TownBuilding building) => world.Machines.Find(building.RuntimeKey ?? TownWorkId(building));

    /// <summary>
    /// The machine a worker works at a town building: <see cref="TownWorkOf"/>, else a town building's machine made now (<see cref="TownWork"/>);
    /// an outpost's workplace has no machine made for it.
    /// </summary>
    Machine? MachineFor(World world, TownBuilding building, int owner) =>
        TownWorkOf(world, building) ?? (building.RuntimeKey is null ? TownWork(world, building, owner) : null);

    /// <summary>
    /// The workers of town machines at their building this tick (serial, before the players' operators): the AI's workers that stand at their
    /// building doing row 87 or 221 operate it (<see cref="TownOperate"/>), those doing row 92 empty its output into their inventory and those doing
    /// row 93 fill its inputs.
    /// </summary>
    void TownWorkers(World world, float dt)
    {
        var table = world.Characters;
        _townResearch = 0;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive
                || table.Cold(i) is not { Ai: { WorkOperate: not null } or { CollectFrom: not null } or { FillTo: not null } } cold)
                continue;

            ref var n = ref table.Next[i];
            var mind = cold.Ai!;
            var building = mind.WorkOperate ?? mind.CollectFrom ?? mind.FillTo!;
            if (!Able(cold) || mind.Behaviour != Ai.AiBehaviour.Work || !AtTownWork(n, building, mind.WorkRange)) continue;
            if (MachineFor(world, building, mind.WorkOwner) is not { } m || !Live(world, m)) continue;
            if (mind.FillTo is not null) FillInputs(world, cold, m);
            else if (mind.CollectFrom is not null) TakeOutput(world, cold, m);
            else if (mind.WorkOperate is not null && MachinePowered(world, m)) TownOperate(world, i, cold, m, dt);
        }
    }

    /// <summary>
    /// The obedient player slaves at their workplace on goal 189 (<see cref="CharacterCold.SlaveWork"/>; serial, after the AI's workers): as the
    /// AI's plan for 189 (87's list B in order: <c>!63</c>, whose provider is 92; <c>!62</c>, whose provider is 93), a full output is emptied into
    /// the inventory (row 92), else an empty input is filled from what it carries (row 93, <see cref="FillInputs"/>), else the machine is operated
    /// (row 87, <see cref="TownOperate"/>). Fetching an input from another machine and putting output away are walks of the job
    /// (Kidnap/ObedientSlave.cs). The machine is the AI's workers' (<see cref="TownWork"/>). 190 pretends (row 221, no list B on the machine's
    /// state) and operates it as row 87 does, whatever it holds.
    /// </summary>
    void ObedientWorkers(World world, float dt)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { SlaveWork: { Place: var place } work } cold) continue;
            if (!Able(cold) || cold.ActionClip != place.Clip || !AtTownWork(table.Next[i], place.Building, place.Range)) continue;

            var m = world.Machines.Find(TownWorkId(place.Building)) ?? TownWork(world, place.Building, place.Owner);
            if (m is null || !Live(world, m)) continue;

            if (work.Operates && m.OutputBlocked) TakeOutput(world, cold, m);
            else if (work.Operates && m.HasEmptyInputs) FillInputs(world, cold, m);
            else TownOperate(world, i, cold, m, dt);
        }
    }

    /// <summary>
    /// Row 92 at another town machine for an obedient player slave's errand (the source row 94 found): its output goes into the inventory while
    /// there is room (<see cref="TakeOutput"/>). Serial phases only.
    /// </summary>
    internal void TakeTownOutput(World world, CharacterCold cold, TownBuilding building, int owner)
    {
        if (MachineFor(world, building, owner) is { } m && Live(world, m)) TakeOutput(world, cold, m);
    }

    /// <summary>
    /// State 82 <c>gotInventoryRoomForResources</c> of a machine (<c>FUN_14059bd80</c>, <b>Verified</b>): the inventory or the worn backpack has room
    /// for a unit of its output item.
    /// </summary>
    internal bool RoomForOutput(CharacterCold cold, Machine m) =>
        m.Output is { } o && cold.Inventory is { } inv && _items.Create(o.Recipe.Item, 1) is { } unit && inv.HasRoomFor(unit);

    /// <summary>
    /// Whether a worker stands at the town building within <paramref name="reach"/>, idle or resting.
    /// </summary>
    static bool AtTownWork(in CharacterHot n, TownBuilding building, float reach) =>
        Ai.AiSystem.AtWork(in n, building.Position, reach) && (CharacterTask)n.Task is CharacterTask.Idle or CharacterTask.Rest;

    /// <summary>
    /// One worker's frame at a town machine, rows 87 and 221 alike (<c>Task_OperateMachine</c>'s update reads no row, <c>FUN_14035bc90</c>,
    /// <b>Verified</b>): the building's work step at the worker's skill (<see cref="WorkStep"/>, as the player's operator) and the operating XP. A
    /// research bench's work counts only for a player-owned one (<c>FUN_14029b8d0</c>), so only at an outpost, where it adds to the tick's
    /// contributions (<see cref="_townResearch"/>); a crafting bench's only for a queued craft (<c>FUN_1402b5a40</c>). A crafting bench does not
    /// refuse its operator: its <c>dontNeedWorkRightNow</c> (slot <c>0x4e8</c>) is a constant 0 (<c>FUN_1402ab3c0</c>). At a training dummy
    /// (function 7) the operating XP is skipped and the work step is the dummy's training (<see cref="TrainingXp"/>).
    /// </summary>
    void TownOperate(World world, int slot, CharacterCold cold, Machine m, float dt)
    {
        // tryOperate (slot 0x4f8, FUN_1400f8030) refuses while the bench needs no work: the player's research bench with no current research.
        if (m.Data.IsResearch && CanResearch(world, m) && world.Research.Queue.Count == 0) return;

        if (m.Data.IsTraining)
        {
            m.Operators++;
            cold.Operating = true;
            TrainingXp(cold, m.Data, dt);
            return;
        }

        m.Operators++;
        cold.Operating = WorkStep(world, slot, cold, m, dt, ref _townResearch);
        OperatingXp(world, cold, m.Data.Stat, dt);
    }

    /// <summary>
    /// The residents pretend-operating a workstation or training dummy of their home (row 221, <see cref="Ai.AiMind.PretendAt"/>, set once the seat
    /// act has them there): at a dummy its training; at a workstation <see cref="TownOperate"/> on the piece's own machine (<see cref="PieceWork"/>),
    /// as <c>Task_OperateMachine</c>'s update does for any machine (<c>FUN_14035bc90</c>: the operating XP, then the building's work step, slot
    /// <c>0x4b0</c>), so a refinery or hydroponic farm at home fills its output; a research or crafting piece's step makes nothing for them (the
    /// player's faction only, a queued craft only). A piece with no machine data gives the operating XP alone.
    /// </summary>
    void HomePretenders(World world, float dt)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { Ai: { PretendAt: { Record: { } record } spot } mind } cold) continue;

            bool there = mind.Behaviour == Ai.AiBehaviour.Sit && (CharacterTask)table.Next[i].Task == CharacterTask.Idle;
            if (!Able(cold) || !there || Data(record) is not { } data) continue;

            if (data.IsTraining) TrainingXp(cold, data, dt);
            else if (spot.Piece is { } piece && TownWork(world, piece) is { } m) PretendOperate(world, i, cold, m, dt);
            else OperatingXp(world, cold, data.Stat, dt);
        }
    }

    /// <summary>
    /// One pretending resident's frame at its piece's machine: <see cref="TownOperate"/> while the piece has power (state 93, a start condition of
    /// 221 as of 87, which <see cref="TownWorkers"/> holds the AI's workers to alike).
    /// </summary>
    void PretendOperate(World world, int slot, CharacterCold cold, Machine m, float dt)
    {
        if (Live(world, m) && MachinePowered(world, m)) TownOperate(world, slot, cold, m, dt);
    }

    /// <summary>
    /// State 93 BUILDING_HAS_POWER of a house's layout object (<c>FUN_140596ab0</c> -> <c>FUN_140299370</c>): its machine's power, true for an
    /// object that is no machine (a building class below 2) or draws nothing. Makes the machine on the first ask (<see cref="TownWork(World,
    /// string)"/>); serial phases only.
    /// </summary>
    public bool PiecePowered(World world, string piece) => TownWork(world, piece) is not { } m || MachinePowered(world, m);

    /// <summary>
    /// <c>Task_EmptyMachine</c> (rows 92 and 284, <c>FUN_14033cd10</c>, <b>Verified</b>) at a machine with an output: the output's items go into the
    /// character's inventory (the worn backpack when the main grid is full) while there is room, a unit at a time; a producer whose status was 1
    /// (output full) then goes back to 2.
    /// </summary>
    void TakeOutput(World world, CharacterCold cold, Machine m)
    {
        if (cold.Inventory is not { } inv || m.Output is not { Count: > 0 } output) return;
        bool moved = false;
        while (output.Count > 0 && _items.Create(output.Recipe.Item, 1) is { } unit && inv.HasRoomFor(unit) && inv.Add(unit))
        {
            output.Count--;
            moved = true;
        }

        if (!moved) return;
        if (m.Status == 1) m.Status = 2;
        world.Machines.Touch();
    }
}