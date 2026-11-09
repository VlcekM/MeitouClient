using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;

namespace Meitou.Tests;

/// <summary>
/// The Kenshi install and the game data the install tests share, each loaded once on first use (loading a database takes seconds). Two load
/// orders: the base game alone, and the install's own (the base game plus the enabled mods, which is what the game runs). Nothing here may be
/// mutated by a test.
/// </summary>
static class InstallData
{
    static readonly Lazy<GameInstall?> InstallCache = new(GameInstall.Locate, LazyThreadSafetyMode.ExecutionAndPublication);
    static readonly Lazy<GameDatabase?> BaseGameCache = new(
        () => Install is { } i ? GameDatabase.Load(LoadOrder.BaseGame(i)) : null,
        LazyThreadSafetyMode.ExecutionAndPublication);
    static readonly Lazy<GameDatabase?> FullLoadOrderCache = new(
        () => Install is { } i ? GameDatabase.Load(LoadOrder.FromInstall(i)) : null,
        LazyThreadSafetyMode.ExecutionAndPublication);
    static readonly Lazy<WorldLevelData?> LevelsCache = new(
        () => Install is { } i ? WorldLevelData.LoadBaseGame(i) : null,
        LazyThreadSafetyMode.ExecutionAndPublication);
    static readonly Lazy<WorldLevelData?> LevelsWithInteriorsCache = new(
        () => Install is { } i ? WorldLevelData.LoadBaseGame(i, includeInteriors: true) : null,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The install, or null when none is found (tests then skip).
    /// </summary>
    public static GameInstall? Install => InstallCache.Value;

    /// <summary>
    /// The base game's records only.
    /// </summary>
    public static GameDatabase? BaseGame => BaseGameCache.Value;

    /// <summary>
    /// The records of the install's whole load order.
    /// </summary>
    public static GameDatabase? FullLoadOrder => FullLoadOrderCache.Value;

    /// <summary>
    /// The world's zone files (placements, towns, roads).
    /// </summary>
    public static WorldLevelData? Levels => LevelsCache.Value;

    /// <summary>
    /// The zone files with the buildings' interiors too (a navmesh with interiors needs them).
    /// </summary>
    public static WorldLevelData? LevelsWithInteriors => LevelsWithInteriorsCache.Value;
}
