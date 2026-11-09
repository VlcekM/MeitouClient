using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;

namespace Meitou.Game.Title;

/// <summary>
/// Leaving the game for the main menu, and starting a new game of the chosen start, both under the loading screen.
/// </summary>
sealed class NewGameLaunch(GameHost host)
{
    internal string? NewGameRequested;
    internal bool MainMenuRequested;

    /// <summary>
    /// Leaves the game for the main menu: a new world with the town population and no player squad (what a run without a start has behind the title),
    /// the camera where it is; the faithful mode then shows the title screen, the Meitou mode its game menu. Under the loading screen
    /// (GameHost.Transition.cs).
    /// </summary>
    internal void HandleMainMenu()
    {
        if (!MainMenuRequested) return;

        MainMenuRequested = false;
        if (!host.Saves.SavesAvailable(out string why) || host.Saves.Busy())
        {
            host.Player.Notice(why.Length > 0 ? why : "Save or load still in progress", true);
            return;
        }

        host.StartSceneLoad(mainMenuTransition: true, host.TitleScreen.TitleHour, GameHost.Stage("build", () =>
        {
            var built = host.Saves.BuildSystems(interactive: true);
            host.Session.ReplaceWorld(built.Systems);
            host.Population = built.Population;
            host.PlayerSquad = null;
            if (host.Nav is not null) host.Population!.ZoneGate = zone => host.Nav.Walkability.World.Contains(zone);

            host.Saves.SnapshotPlayerTowns();
            host.Saves.LoadedSave = null;
            host.Saves.PreparedBase = null;
            host.Saves.SaveInUse = null;
            host.Saves.WorldEpoch++;
            host.SettleNewWorld();

            // re-read the latest Continue save after the campaign or its character appearances changed
            host.TitleScreen.DisposeTitleScene();
            host.Player.Frontend.ShowMainMenu();
            host.Sound.AudioMainMenu();
            return null;
        }));
    }

    /// <summary>
    /// A new game of the chosen start, under the loading screen: the systems and the navmesh round the start first, then the world with the player's
    /// squad.
    /// </summary>
    internal void HandleNewGame()
    {
        if (NewGameRequested is not { } id) return;

        if (!host.Saves.SavesAvailable(out string why))
        {
            NewGameRequested = null;
            host.Player.Notice(why, true);
            return;
        }

        // the original's dispatcher leaves the request queued while a save or load runs (FUN_14047c310): it starts when that ends
        if (host.Saves.Busy()) return;

        NewGameRequested = null;
        if (host.Scene.Database is not { } database || NewGameStart.Find(NewGameStart.LoadAll(database), id) is not { } chosen) return;

        StandardSystemSet? built = null;
        var nextDifficulty = host.Player.AdvancedOptions.NewGameOptions;
        var nextData = nextDifficulty.Apply(host.PopData!, host.Saves.Gameplay.Dismemberment);

        // A fresh seed per game, as the original rolls its names and squads anew each time (docs/characters.md "Names"); --seed keeps one.
        ulong seed = host.GameOptions.SeedGiven ? host.GameOptions.Seed : (ulong)Random.Shared.NextInt64(1, long.MaxValue);
        host.Sound.AudioGameStarts();
        host.StartSceneLoad(host.Player.TitleOpen, null,
            GameHost.Stage("build", () =>
            {
                built = host.Saves.BuildSystems(interactive: true, nextData, nextDifficulty);

                // The navmesh round the place the squad will stand, drawn with the same seed as StartPlayer.
                var at = PopulationSystem.StartPlace(nextData, seed, chosen).Centre;
                return host.Saves.NavmeshAround(new(at.X, 0, at.Y));
            }, duringAscent: true),
            GameHost.Stage("people", () =>
            {
                Squad? spawned = null;
                try
                {
                    host.Session.ReplaceWorld(built!.Systems, world =>
                    {
                        world.RestoreSeed(seed);
                        world.Player.BanditsLoot = nextDifficulty.BanditsLoot;
                        spawned = built.Population!.StartPlayer(world, chosen);
                    });
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"new game  {e}");
                    throw new GameHost.SceneLoadFailed($"New game failed: {e.Message}");
                }

                host.PopData = nextData;
                host.Saves.Difficulty = nextDifficulty;
                host.Population = built.Population;
                host.PlayerSquad = spawned;
                if (host.Nav is not null) host.Population!.ZoneGate = zone => host.Nav.Walkability.World.Contains(zone);

                host.Saves.SnapshotPlayerTowns();
                host.Saves.LoadedSave = null;
                host.Saves.PreparedBase = null;
                host.Saves.SaveInUse = null;
                host.Saves.WorldEpoch++;
                host.Session.Clock.SetTime(Simulation.Session.GameClock.DefaultStartDay, Simulation.Session.GameClock.DefaultStartHour);

                var lead = host.Session.World.Characters.Previous[spawned!.Leader.Slot].Position;
                var view = host.Session.Camera.Current;
                host.Session.Camera.Place(new Vector2(lead.X, lead.Z), view.Yaw, view.Pitch, view.Distance);
                host.SettleNewWorld();
                host.Player.Notice(Loc.Tr("Started {1}", chosen.Name));
                ShowStartHints();
                // The start's characters are made in the editor first (docs/characters.md "Character editor").
                host.CharacterEditor.PendingCreation = [.. spawned.Members];
                return null;
            }));
        host.SkyArrival?.ClimbAtOnce();
    }

    /// <summary>
    /// No tutorial file was read at start: the camera hints, queued (FUN_140872e00).
    /// </summary>
    void ShowStartHints()
    {
        if (host.Saves.Tutorials is not { Fresh: true }) return;

        string[] hints =
        [
            "Hold middle mouse button to rotate the camera",
            "Use mouse wheel to zoom",
            "Now create your first character. Use the 'Confirm' button when finished.",
        ];
        foreach (string hint in hints)
        {
            host.Player.Notice(hint);
        }
    }
}
