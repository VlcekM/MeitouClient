using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;

namespace Meitou.Tests;

/// <summary>
/// The Kenshi install and the game data the install tests share, each loaded once on first use (loading a database takes seconds). Two load orders:
/// the base game alone, and the install's own (the base game plus the enabled mods, which is what the game runs). Nothing here may be mutated by a test.
/// </summary>
static class InstallData
{
    static readonly Lazy<GameInstall?> install = new(GameInstall.Locate, LazyThreadSafetyMode.ExecutionAndPublication);
    static readonly Lazy<GameDatabase?> baseGame = new(() => Install is { } i ? GameDatabase.Load(LoadOrder.BaseGame(i)) : null, LazyThreadSafetyMode.ExecutionAndPublication);
    static readonly Lazy<GameDatabase?> fullLoadOrder = new(() => Install is { } i ? GameDatabase.Load(LoadOrder.FromInstall(i)) : null, LazyThreadSafetyMode.ExecutionAndPublication);
    static readonly Lazy<WorldLevelData?> levels = new(() => Install is { } i ? WorldLevelData.Load(i) : null, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The install, or null when none is found (tests then skip).</summary>
    public static GameInstall? Install => install.Value;

    /// <summary>The base game's records only.</summary>
    public static GameDatabase? BaseGame => baseGame.Value;

    /// <summary>The records of the install's whole load order.</summary>
    public static GameDatabase? FullLoadOrder => fullLoadOrder.Value;

    /// <summary>The world's zone files (placements, towns, roads).</summary>
    public static WorldLevelData? Levels => levels.Value;
}
