using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;

namespace Meitou.Game.Title;

/// <summary>Leaving the game for the main menu, and starting a new game of the chosen start, both under the loading screen.</summary>
sealed class NewGameLaunch(GameHost host)
{
    internal string? newGameRequested;
    internal bool mainMenuRequested;

    /// <summary>
    /// Leaves the game for the main menu: a new world with the town population and no player squad (what a run without a start has behind the title), the camera where it is;
    /// the faithful mode then shows the title screen, the Meitou mode its game menu. Under the loading screen (GameHost.Transition.cs).
    /// </summary>
    internal void HandleMainMenu()
    {
        if (!mainMenuRequested) return;
        mainMenuRequested = false;
        if (!host.saves.SavesAvailable(out string why) || host.saves.Busy()) { host.player.Notice(why.Length > 0 ? why : "Save or load still in progress", true); return; }
        host.StartSceneLoad(mainMenuTransition: true, host.titleScreen.TitleHour, GameHost.Stage("build", () =>
        {
            var built = host.saves.BuildSystems(interactive: true);
            host.session.ReplaceWorld(built.Systems);
            host.population = built.Population; host.playerSquad = null;
            if (host.nav is not null) host.population!.ZoneGate = zone => host.nav.Walkability.World.Contains(zone);
            host.saves.SnapshotPlayerTowns();
            host.saves.loadedSave = null; host.saves.preparedBase = null; host.saves.saveInUse = null; host.saves.worldEpoch++;
            host.SettleNewWorld();
            host.titleScreen.DisposeTitleScene(); // re-read the latest Continue save after the campaign or its character appearances changed
            host.player.frontend.ShowMainMenu();
            host.sound.AudioMainMenu();
            return null;
        }));
    }

    /// <summary>A new game of the chosen start, under the loading screen: the systems and the navmesh round the start first, then the world with the player's squad.</summary>
    internal void HandleNewGame()
    {
        if (newGameRequested is not { } id) return;
        if (!host.saves.SavesAvailable(out string why)) { newGameRequested = null; host.player.Notice(why, true); return; }
        if (host.saves.Busy()) return;   // the original's dispatcher leaves the request queued while a save or load runs (FUN_14047c310): it starts when that ends
        newGameRequested = null;
        if (host.scene.Database is not { } database || NewGameStart.Find(NewGameStart.LoadAll(database), id) is not { } chosen) return;
        StandardSystemSet? built = null;
        var nextDifficulty = host.player.advancedOptions.NewGameOptions;
        var nextData = nextDifficulty.Apply(host.popData!, host.saves.Gameplay.Dismemberment);
        // A fresh seed per game, as the original rolls its names and squads anew each time (docs/characters.md "Names"); --seed keeps one.
        ulong seed = host.g.SeedGiven ? host.g.Seed : (ulong)Random.Shared.NextInt64(1, long.MaxValue);
        host.sound.AudioGameStarts();
        host.StartSceneLoad(host.player.TitleOpen, null,
            GameHost.Stage("build", () =>
            {
                built = host.saves.BuildSystems(interactive: true, nextData, nextDifficulty);
                // The navmesh round the place the squad will stand, drawn with the same seed as StartPlayer.
                var at = PopulationSystem.StartPlace(nextData, seed, chosen).Centre;
                return host.saves.NavmeshAround(new(at.X, 0, at.Y));
            }, duringAscent: true),
            GameHost.Stage("people", () =>
            {
                Squad? spawned = null;
                try { host.session.ReplaceWorld(built!.Systems, world => { world.RestoreSeed(seed); world.Player.BanditsLoot = nextDifficulty.BanditsLoot; spawned =built.Population!.StartPlayer(world, chosen); }); }
                catch (Exception e) { Console.Error.WriteLine($"new game  {e}"); throw new GameHost.SceneLoadFailed($"New game failed: {e.Message}"); }
                host.popData = nextData; host.saves.difficulty = nextDifficulty;
                host.population = built.Population; host.playerSquad = spawned;
                if (host.nav is not null) host.population!.ZoneGate = zone => host.nav.Walkability.World.Contains(zone);
            host.saves.SnapshotPlayerTowns();
                host.saves.loadedSave = null; host.saves.preparedBase = null; host.saves.saveInUse = null; host.saves.worldEpoch++;
                host.session.Clock.SetTime(Meitou.Simulation.Session.GameClock.DefaultStartDay, Meitou.Simulation.Session.GameClock.DefaultStartHour);
                var lead = host.session.World.Characters.Previous[spawned!.Leader.Slot].Position;
                var view = host.session.Camera.Current;
                host.session.Camera.Place(new Vector2(lead.X, lead.Z), view.Yaw, view.Pitch, view.Distance);
                host.SettleNewWorld();
                host.player.Notice(Loc.Tr("Started {1}", chosen.Name));
                // No tutorial file was read at start: the camera hints, queued (FUN_140872e00).
                if (host.saves.tutorials is { Fresh: true })
                    foreach (string hint in (string[])["Hold middle mouse button to rotate the camera", "Use mouse wheel to zoom", "Now create your first character. Use the 'Confirm' button when finished."])
                        host.player.Notice(hint);
                host.characterEditor.pendingCreation = [.. spawned.Members];   // the start's characters are made in the editor first (docs/characters.md "Character editor")
                return null;
            }));
        host.skyArrival?.ClimbAtOnce();
    }
}
