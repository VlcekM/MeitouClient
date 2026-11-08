using System.Diagnostics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.Ogre;
using Meitou.Data.World;
using Meitou.Simulation.Navigation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Population;

namespace Meitou.Simulation;

/// <summary>What <see cref="WorldBoot.Load"/> reads a world from.</summary>
/// <param name="Heights">The CPU heightmap the world walks and stands on (immutable, any thread), never the renderer's terrain.</param>
/// <param name="Assets">Finds the art files the ragdolls' skeletons, the animations and the mineable rocks are read from.</param>
public sealed record WorldBootOptions(GameInstall Install, GameDatabase Database, GroundHeights Heights, AssetLocator Assets)
{
    /// <summary>The world's level data with its building swaps applied (<see cref="WorldBoot.LoadLevels"/>); null: open ground with no towns (the sandbox, a duel).</summary>
    public WorldLevelData? Levels { get; init; }

    /// <summary>The buildings' layouts when the caller has read them already (the renderer shares its own); null: read here.</summary>
    public BuildingLayouts? Layouts { get; init; }

    public BodyOptions Bodies { get; init; } = BodyOptions.Default;

    /// <summary>Subscribed to <see cref="NavSystem.ZoneReady"/> before the navmesh starts building zones.</summary>
    public Action<NavZoneReady>? ZoneReady { get; init; }
}

/// <summary>
/// A world's content read before its systems are built, with no window, GPU or audio (docs/engine.md "Host and loading"): the navmesh, the towns'
/// buildings, the wild's areas, the population's data, the ragdolls' rigs and the animations. The game boots through it on a worker; the systems
/// come after from <see cref="StandardSystems.Build"/>.
/// </summary>
public sealed class WorldBoot
{
    public required IWalkability Walkability { get; init; }

    /// <summary>The navmesh, when the world has levels.</summary>
    public NavSystem? Nav { get; init; }

    /// <summary>The terrain and buildings ragdolls land on, when the world has levels.</summary>
    public RagdollGeometry? RagdollGeometry { get; init; }

    public required PopulationData Data { get; init; }

    public required IReadOnlyList<TownPlacement> Towns { get; init; }

    /// <summary>The skeleton rigs, shared with the renderer so a ragdoll starts from the drawn pose.</summary>
    public required CharacterRigs Rigs { get; init; }

    public required AnimationLengths AnimationLengths { get; init; }

    public required AnimationLibrary AnimationLibrary { get; init; }

    /// <summary>The level data read afresh, with the factions' building swaps applied as a scene's are.</summary>
    public static WorldLevelData LoadLevels(GameInstall install, GameDatabase db)
    {
        var levels = WorldLevelData.Load(install, includeInteriors: true);
        BuildingSwapPass.ApplyBuildingSwaps(levels, db);
        return levels;
    }

    /// <summary>Layouts of <paramref name="levels"/> read afresh, their furniture swapped as the placements were.</summary>
    public static BuildingLayouts LevelLayouts(GameDatabase db, WorldLevelData levels)
    {
        var layouts = new BuildingLayouts(db, levels.Interiors);
        layouts.UseSwaps(levels);
        return layouts;
    }

    public static WorldBoot Load(WorldBootOptions o)
    {
        var levels = o.Levels;
        var layouts = levels is null ? null : o.Layouts ?? LevelLayouts(o.Database, levels);

        // The homes' ruins and layouts before anything reads the levels (done already when the renderer read them).
        if (levels is not null)
        {
            ResidentRuins.Apply(o.Database, levels, layouts!);
        }

        var nav = levels is null ? null : StartNav(o, levels);
        IWalkability walkability = nav is null ? new OpenGroundWalkability(o.Heights.HeightAt) : new NavAdapter(nav.Walkability);

        // Ragdolls land on the terrain and the buildings' collision (gathered per zone on a worker when a ragdoll comes near).
        var ragdollGeometry = nav is null ? null : new RagdollGeometry(o.Install, o.Database, levels!, o.Heights.HeightAt, nav.Runtime);
        var towns = levels is null ? [] : levels.Towns().ToList();
        var data = CreateData(o, levels, layouts, towns);

        // One road network for both: on-screen long routes take the roads as the off-screen squads do.
        if (nav is not null)
        {
            nav.Walkability.Roads = data.Roads;
        }

        return new WorldBoot
        {
            Walkability = walkability, Nav = nav, RagdollGeometry = ragdollGeometry, Data = data, Towns = towns,
            Rigs = CharacterRigs.For(o.Database, o.Assets),
            // With the animals' own skeletons and the animation files.
            AnimationLengths = AnimationLengths.Load(o.Install.Root, o.Database, name => o.Assets.Find(name)),
            AnimationLibrary = AnimationLibrary.FromDatabase(o.Database),
        };
    }

    static NavSystem StartNav(WorldBootOptions o, WorldLevelData levels)
    {
        var nav = new NavSystem(o.Install, o.Database, levels, o.Heights.HeightAt);
        if (o.ZoneReady is { } ready)
        {
            nav.ZoneReady += ready;
        }

        // The long-range grid built behind the loading: built by the first long route instead, it held the path threads ~3 s (issue #124).
        _ = Task.Run(() => nav.Walkability.Coarse.Warm());
        return nav;
    }

    static PopulationData CreateData(WorldBootOptions o, WorldLevelData? levels, BuildingLayouts? layouts, List<TownPlacement> towns)
    {
        var biomes = levels is null ? null : Biomes(o);
        var buildings = levels is null ? null : ReadTownBuildings(o, levels, layouts!);
        var data = PopulationData.Create(o.Database, towns, new GeneratedAppearances(o.Database, o.Install.Root), bodyOptions: o.Bodies,
            biomes: biomes, buildings: buildings, roads: levels?.Roads(), townZones: levels?.TownZones(), level: levels?.Level);
        if (levels is not null)
        {
            data.Layouts = layouts;
            data.Sight = new BuildingSight(o.Install, o.Database, levels, layouts!);
            data.ResourceNodes = ResourceNodes(o, levels);
        }

        return data;
    }

    /// <summary>The wild (homeless squads and nests) needs the world's area sectors.</summary>
    static BiomeMap? Biomes(WorldBootOptions o)
    {
        try
        {
            return BiomeMap.Create(o.Database, WeatherAreas.Load(o.Install));
        }
        catch (Exception e) when (e is IOException or FormatException)
        {
            Console.Error.WriteLine($"population no areas map ({e.Message}): no wild squads");
            return null;
        }
    }

    /// <summary>The towns' buildings with their beds and nodes: the AI's homes (docs/simulation.md "AI").</summary>
    static Dictionary<string, List<TownBuilding>> ReadTownBuildings(WorldBootOptions o, WorldLevelData levels, BuildingLayouts layouts)
    {
        var watch = Stopwatch.StartNew();
        var buildings = Meitou.Data.World.TownBuildings.Build(o.Database, levels, layouts, (x, z) => o.Heights.HeightAt((float)x, (float)z));
        int beds = buildings.Values.Sum(l => l.Sum(b => b.Beds));
        int nodes = buildings.Values.Sum(l => l.Sum(b => b.Spots.Count - b.Beds));
        Console.WriteLine($"homes     {buildings.Values.Sum(l => l.Count)} town buildings, {beds} beds, {nodes} nodes in {buildings.Count} towns ({watch.ElapsedMilliseconds} ms)");
        return buildings;
    }

    /// <summary>The mineable rocks for ore mines, from the foliage placement (read through the renderer's layout cache when it has the zone).</summary>
    static FoliageResourceNodes ResourceNodes(WorldBootOptions o, WorldLevelData levels)
    {
        var catalog = FoliageCatalog.Load(o.Database);
        FoliageLayoutCache? cache = null;
        try
        {
            cache = new FoliageLayoutCache(o.Install, catalog, FoliageWorld.ReadTowns(o.Database, levels));
        }
        catch (IOException e)
        {
            Console.Error.WriteLine($"resource rocks without the layout cache: {e.Message}");
        }

        return new FoliageResourceNodes(new FoliageWorld(o.Install, o.Database, levels, catalog, cache: cache), o.Database, name => o.Assets.Find(name));
    }
}
