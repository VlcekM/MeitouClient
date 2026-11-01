using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation;

namespace Meitou.Game;

sealed partial class GameHost
{
    string? newGameRequested;
    bool mainMenuRequested;

    /// <summary>
    /// Leaves the game for the main menu: a new world with the town population and no player squad (what a run without a start has behind the title), the camera where it is;
    /// the faithful mode then shows the title screen, the Meitou mode its game menu. Under the loading screen (GameHost.Transition.cs).
    /// </summary>
    void HandleMainMenu()
    {
        if (!mainMenuRequested) return;
        mainMenuRequested = false;
        if (!SavesAvailable(out string why) || Busy()) { player.Notice(why.Length > 0 ? why : "Save or load still in progress", true); return; }
        StartSceneLoad(mainMenuTransition: true, TitleHour, Stage("build", () =>
        {
            var built = BuildSystems(interactive: true);
            session.ReplaceWorld(built.Systems);
            population = built.Population; playerSquad = null;
            if (nav is not null) population!.ZoneGate = zone => nav.Walkability.World.Contains(zone);
            loadedSave = null; preparedBase = null; saveInUse = null; worldEpoch++;
            SettleNewWorld();
            DisposeTitleScene(); // re-read the latest Continue save after the campaign or its character appearances changed
            player.ShowMainMenu();
            AudioMainMenu();
            return null;
        }));
    }

    /// <summary>A new game of the chosen start, under the loading screen: the systems and the navmesh round the start first, then the world with the player's squad.</summary>
    void HandleNewGame()
    {
        if (newGameRequested is not { } id) return;
        if (!SavesAvailable(out string why)) { newGameRequested = null; player.Notice(why, true); return; }
        if (Busy()) return;   // the original's dispatcher leaves the request queued while a save or load runs (FUN_14047c310): it starts when that ends
        newGameRequested = null;
        if (scene.Database is not { } database || NewGameStart.Find(NewGameStart.LoadAll(database), id) is not { } chosen) return;
        StandardSystemSet? built = null;
        var nextDifficulty = player.NewGameOptions;
        var nextData = nextDifficulty.Apply(popData!, Gameplay.Dismemberment);
        // A fresh seed per game, as the original rolls its names and squads anew each time (docs/characters.md "Names"); --seed keeps one.
        ulong seed = g.SeedGiven ? g.Seed : (ulong)Random.Shared.NextInt64(1, long.MaxValue);
        AudioGameStarts();
        StartSceneLoad(player.TitleOpen, null,
            Stage("build", () =>
            {
                built = BuildSystems(interactive: true, nextData, nextDifficulty);
                // The navmesh round the place the squad will stand, drawn with the same seed as StartPlayer.
                var at = PopulationSystem.StartPlace(nextData, seed, chosen).Centre;
                return NavmeshAround(new(at.X, 0, at.Y));
            }, duringAscent: true),
            Stage("people", () =>
            {
                Squad? spawned = null;
                try { session.ReplaceWorld(built!.Systems, world => { world.RestoreSeed(seed); world.Player.BanditsLoot = nextDifficulty.BanditsLoot; spawned =built.Population!.StartPlayer(world, chosen); }); }
                catch (Exception e) { Console.Error.WriteLine($"new game  {e}"); throw new SceneLoadFailed($"New game failed: {e.Message}"); }
                popData = nextData; difficulty = nextDifficulty;
                population = built.Population; playerSquad = spawned;
                if (nav is not null) population!.ZoneGate = zone => nav.Walkability.World.Contains(zone);
                loadedSave = null; preparedBase = null; saveInUse = null; worldEpoch++;
                session.Clock.SetTime(Meitou.Engine.Time.GameClock.DefaultStartDay, Meitou.Engine.Time.GameClock.DefaultStartHour);
                var lead = session.World.Characters.Previous[spawned!.Leader.Slot].Position;
                var view = session.Camera.Current;
                session.Camera.Place(new Vector2(lead.X, lead.Z), view.Yaw, view.Pitch, view.Distance);
                SettleNewWorld();
                player.Notice($"Started {chosen.Name}");
                // No tutorial file was read at start: the camera hints, queued (FUN_140872e00).
                if (tutorials is { Fresh: true })
                    foreach (string hint in (string[])["Hold middle mouse button to rotate the camera", "Use mouse wheel to zoom", "Now create your first character. Use the 'Confirm' button when finished."])
                        player.Notice(hint);
                pendingCreation = [.. spawned.Members];   // the start's characters are made in the editor first (docs/characters.md "Character editor")
                return null;
            }));
        skyArrival?.ClimbAtOnce();
    }
}
