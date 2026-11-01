using System.Numerics;
using Meitou.Content;
using Meitou.Rendering;

namespace Meitou.Game;

static class Program
{
    /// <summary>The launcher's preset detection (<see cref="HardwareProbe"/>), named in the settings schema.</summary>
    internal const string HardwareProbeFlag = "--recommend-preset";

    static int Main(string[] args)
    {
        GameConsole.AttachToParent();
        // The launcher's settings description (package-release.ps1): no log, window or game files.
        if (args is ["--settings-schema", var schema]) return LauncherSchema.Write(schema);
        if (args is [HardwareProbeFlag, var report]) return HardwareProbe.Write(report);
        GameConsole.StartLog();
        RenderJobs.RaiseRenderThread();
        GameOptions game;
        string[] rest;
        WorldOptions? world;
        try
        {
            (game, rest) = GameOptions.Split(args);
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
        if (game.UiShowcase && world.Screenshot is null)
        {
            Console.Error.WriteLine("--ui-showcase needs --screenshot <path>.");
            return 2;
        }
        if (game.TitlePreview && world.Screenshot is null)
        {
            Console.Error.WriteLine("--title-preview needs --screenshot <path>.");
            return 2;
        }
        // Booting to the Meitou title: the world is loaded round the title's view, which it shows first (docs/engine.md).
        bool toTitle = game.TitlePreview || world.Screenshot is null && !game.NewGame && game.Load is null && game.Sandbox == 0 && !game.Duel && game.Battle is null && !game.NoPopulation;
        if (world.X is null && world.Zone is null && world.Town is null && toTitle) (world.X, world.Z) = TitleComposition.Focus;
        if (world.X is null && world.Zone is null && world.Town is null) world.Town = "The Hub";
        var install = GameInstall.Locate();
        if (install is null)
            using (GameConsole.ForPrompt()) install = GameInstall.LocateOrAsk();
        if (install is null)
        {
            Console.Error.WriteLine($"Kenshi install not found: set {GameInstall.EnvironmentVariable} or create {GameInstall.LocalConfigFile}.");
            return 1;
        }
        // Steamworks must start before Vulkan creates its device so Steam can hook the renderer for Shift+Tab. Not for pictures or a
        // listing: those must not count as Kenshi play time or get the overlay.
        using var steam = world.Screenshot is null && !game.ListStarts ? SteamSession.TryStart(install) : null;
        // A native splash at once, until the game window draws (docs/engine.md "Loading"); not for pictures or a listing.
        using var splash = world.Screenshot is null && !game.ListStarts ? SplashWindow.Show(install.DataDirectory) : null;
        var config = UserConfig.Load();
        Meitou.Localisation.Loc.Use(install);   // the language of settings.cfg: texts from locale/<code>/LC_MESSAGES/main.mo (docs/formats/localisation.md)
        // The saved texture quality before the world loads: the terrain's layer arrays are sized from it once (the Tab panel's restore after
        // the boot reaches only the other textures). --texture-quality and --low-end choose it for this run instead.
        if (!args.Contains("--texture-quality") && !args.Contains("--low-end") && config.Graphics.TryGetValue(WorldFrame.TextureQualitySlider, out float savedQuality))
            world.TextureQuality = Math.Clamp((int)MathF.Round(savedQuality), 0, Meitou.Data.Textures.TextureQuality.Maximum);
        Meitou.Data.GameDatabase? db = null;
        Meitou.Data.Gameplay.NewGameStart? start = null;
        if (game.Battle is not null && (game.NewGame || game.NoPopulation || game.Load is not null || game.Duel || game.Sandbox > 0))
        {
            Console.Error.WriteLine("--battle makes its own characters: drop --new-game, --no-population, --load, --duel and --sandbox (--battle-map sandbox is its flat floor).");
            return 2;
        }
        if (game.Sandbox > 0 || game.Battle is not null && game.BattleFlat)
        {
            // The animation sandbox: no world, a flat floor (docs/simulation.md "Sandbox"). The real game data is still loaded for the characters and animations.
            db = Meitou.Data.GameDatabase.Load(Meitou.Data.LoadOrder.FromInstall(install));
            (world.NoTextures, world.NoObjects, world.NoFoliage, world.NoWater, world.NoFogFeatures, world.WaterReflection, world.DistantZones) = (true, true, true, true, true, 0, 0);
            world.Post.Ssao = false;   // SSAO bands a perfectly flat floor; the Tab panel can turn it back on
            world.NoParticles = true;   // no weather or map effects to show (the effect library and every map placer would be read)
            world.Clouds ??= 0;   // no cloud layer: the volume march and the cloud shadows skip at density 0
            world.Weather ??= "Default";   // clear; the scheduler would roll the weather of the world region under the floor
            world.Distance ??= game.Battle is null ? 70 : 150;
            world.Pitch ??= 22;
            world.Yaw ??= 35;
            using var flat = WorldFrame.LoadFlat(world, db);
            return new GameHost(install, flat, new AssetLocator(install), world, game, config, null) { Splash = splash }.Run();
        }
        if (game.TerrainSandbox is { } field)
        {
            // The terrain sandbox (docs/render-terrain.md "Texture field"): the empty world round the spot, its ground the field of every ground texture.
            if (game.NewGame || game.Load is not null || game.Duel || game.Battle is not null)
            {
                Console.Error.WriteLine("--terrain-sandbox is an empty world: drop --new-game, --load, --duel and --battle.");
                return 2;
            }
            game.NoPopulation = true;
            (world.X, world.Z, world.Town, world.Zone) = (field.X, field.Z, null, null);
            Meitou.Rendering.GroundRelief.FieldCentre = new Vector2(field.X, field.Z);
            Meitou.Rendering.GroundRelief.FieldCliffs = game.TerrainSandboxCliffs;
            (world.NoFoliage, world.NoObjects) = (true, true);   // the ground alone: no grass, rocks or scattered objects over the tiles
            world.Weather ??= "Default";
            if (!rest.Contains("--time")) world.Hour = 10;
            world.Distance ??= 250;
            world.Pitch ??= game.TerrainSandboxCliffs ? 10 : 30;
            if (game.TerrainSandboxCliffs) world.Yaw ??= 180;   // from below the face, looking up it (it faces −Z)
        }
        if (game.Duel)
        {
            // The trailer duel (docs/simulation.md "Duel"): the world round its spot at sunset, clear, the camera low beside the pair looking into the sun.
            if (game.NewGame || game.NoPopulation)
            {
                Console.Error.WriteLine("--duel makes its own two characters: drop --new-game and --no-population.");
                return 2;
            }
            var spot = game.DuelAt is { } at ? new Vector2(at.X, at.Z) : GameHost.DuelSpot;
            (world.X, world.Z, world.Town) = (spot.X, spot.Y, null);
            if (!rest.Contains("--time")) world.Hour = GameHost.DuelHour;
            world.Weather ??= "Default";
            world.Distance ??= 90;
            world.Pitch ??= 8;
            world.Yaw ??= 90;   // looking west into the sunset, the pair left and right
        }
        if (game.Battle is not null)
        {
            // The battle on the map (docs/simulation.md "Battle"): the world round its spot, with no towns; the camera across the two blocks.
            var spot = game.BattleAt is { } at ? new Vector2(at.X, at.Z) : GameHost.BattleSpot;
            (world.X, world.Z, world.Town) = (spot.X, spot.Y, null);
            world.Distance ??= 150;
            world.Yaw ??= 90;
            if (!rest.Contains("--time")) world.Hour = GameHost.BattleHour;
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
            // Load the world round the place the squad will stand: the start's town drawn with the world's seed, as StartPlayer draws it.
            var at = Meitou.Simulation.PopulationSystem.StartPlace(
                Meitou.Simulation.PopulationData.Create(db, Meitou.Data.World.WorldLevelData.Load(install).Towns()), game.Seed, start).Centre;
            (world.X, world.Z) = (at.X, at.Y);
            world.Town = null;
            if (!rest.Contains("--time")) world.Hour = (float)Meitou.Engine.Time.GameClock.DefaultStartHour;
            Console.WriteLine($"new game  {start.Name}: {start.Money} cats, {start.Squad.Count} squad link(s), at {at.X:0}, {at.Y:0}");
        }
        Meitou.Data.Save.SaveGame? save = null;
        if (game.Load is { } folder)
        {
            if (game.NewGame || game.NoPopulation || game.Duel)
            {
                Console.Error.WriteLine("--load starts in the save: drop --new-game, --no-population and --duel.");
                return 2;
            }
            try { save = Meitou.Data.Save.SaveGame.Load(folder); }
            catch (Exception e) when (e is IOException or FormatException or ArgumentException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"--load {folder}: {e.Message}");
                return 1;
            }
            // The world round the saved camera; the load then puts the camera on the player's leader (GameHost.FinishQuickLoad).
            var at = save.Camera.Position;
            (world.X, world.Z, world.Town, world.Zone) = (at.X, at.Z, null, null);
        }
        // Interactive: the host reads the world behind its loading screen (docs/engine.md "Loading"); a picture reads it first.
        if (world.Screenshot is null)
            return new GameHost(install, null!, new AssetLocator(install), world, game, config, start) { StartSave = save, Splash = splash, LoadScene = () => WorldFrame.Load(install, world, db) }.Run();
        using var scene = WorldFrame.Load(install, world, db);
        if (scene is null) return 1;
        return new GameHost(install, scene, new AssetLocator(install), world, game, config, start) { StartSave = save }.Run();
    }
}
