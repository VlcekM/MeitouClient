using System.Numerics;
using Meitou.Content;
using Meitou.Rendering;

namespace Meitou.Game;

static class Program
{
    static int Main(string[] args)
    {
        RenderJobs.RaiseRenderThread();
        GameOptions game;
        WorldOptions? world;
        try
        {
            (game, var rest) = GameOptions.Split(args);
            world = WorldOptions.Parse(rest);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or IndexOutOfRangeException)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
        if (world is null)
        {
            Console.WriteLine(GameOptions.Usage);
            return 2;
        }
        if (world.X is null && world.Zone is null && world.Town is null) world.Town = "The Hub";
        var install = GameInstall.LocateOrAsk();
        if (install is null)
        {
            Console.Error.WriteLine($"Kenshi install not found: set {GameInstall.EnvironmentVariable} or create {GameInstall.LocalConfigFile}.");
            return 1;
        }
        var config = UserConfig.Load();
        Meitou.Data.GameDatabase? db = null;
        Meitou.Data.Gameplay.NewGameStart? start = null;
        if (game.Sandbox > 0)
        {
            // The animation sandbox: no world, a flat floor (docs/simulation.md "Sandbox"). The real game data is still loaded for the characters and animations.
            db = Meitou.Data.GameDatabase.Load(Meitou.Data.LoadOrder.FromInstall(install));
            (world.NoTextures, world.NoObjects, world.NoFoliage, world.NoWater, world.NoReflections, world.NoDistant) = (true, true, true, true, true, true);
            world.Post.Ssao = false;   // SSAO bands a perfectly flat floor; the Tab panel can turn it back on
            world.Distance ??= 70;
            world.Pitch ??= 22;
            world.Yaw ??= 35;
            using var flat = WorldFrame.LoadFlat(world, db);
            return new GameHost(install, flat, new AssetLocator(install), world, game, config, null).Run();
        }
        if (game.ListStarts || game.NewGame)
        {
            db = Meitou.Data.GameDatabase.Load(Meitou.Data.LoadOrder.FromInstall(install));
            var starts = Meitou.Data.Gameplay.NewGameStart.LoadAll(db);
            if (game.ListStarts)
            {
                foreach (var s in starts.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
                    Console.WriteLine($"{s.Name,-28} {s.Money,7} cats  {s.Squad.Count} squad links  {(s.ForceStartPos ? "fixed position" : s.Towns.Count + " town(s)")}");
                return 0;
            }
            start = Meitou.Data.Gameplay.NewGameStart.Find(starts, game.NewGameName ?? Meitou.Data.Gameplay.NewGameStart.DefaultName);
            if (start is null)
            {
                Console.Error.WriteLine($"No start '{game.NewGameName ?? Meitou.Data.Gameplay.NewGameStart.DefaultName}'; --list-starts shows them.");
                return 2;
            }
            if (game.NoPopulation)
            {
                Console.Error.WriteLine("--new-game needs the population (it is the player's squad); drop --no-population.");
                return 2;
            }
            // Load the world around the start: its first listed town, else the fixed position.
            Vector2? at = start.ForceStartPos ? start.StartPosition : null;
            if (at is null)
            {
                var towns = Meitou.Data.World.WorldLevelData.Load(install).Towns().ToList();
                foreach (var link in start.Towns)
                    if (towns.FirstOrDefault(t => t.TownId == link.Id) is { } placed) { at = new Vector2(placed.Position.X, placed.Position.Z); break; }
            }
            at ??= start.StartPosition;
            (world.X, world.Z) = (at.Value.X, at.Value.Y);
            world.Town = null;
            Console.WriteLine($"new game  {start.Name}: {start.Money} cats, {start.Squad.Count} squad link(s), at {at.Value.X:0}, {at.Value.Y:0}");
        }
        using var scene = WorldFrame.Load(install, world, db);
        if (scene is null) return 1;
        if (world.Info) return 0;
        return new GameHost(install, scene, new AssetLocator(install), world, game, config, start).Run();
    }
}
