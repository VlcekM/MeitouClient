using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Save;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;
using Meitou.Tests.Simulation;
using static Meitou.Tests.Save.SaveSamples;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Save;

/// <summary>
/// A town squad kept loaded as a pinned platoon when its town unloads (<c>PopulationSystem.KeepLoaded</c>) is saved and loaded as exactly one copy.
/// </summary>
public class PinnedPlatoonSaveTests
{
    static SimWorld NewWorld(PopulationData data)
    {
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var systems = SyntheticTown.Standard(data, walk,
                StandardParts.Population | StandardParts.Player | StandardParts.Movement | StandardParts.Body,
            new PopulationSettings { CheckEveryTicks = 1, UnloadGraceSeconds = 10 });
        return new SimWorld(new WorldSettings { Seed = 11, Threads = 1, PublishSnapshots = false }, walk, systems.Systems);
    }

    static void Dispose(SimWorld world)
    {
        world.Dispose();
        foreach (var s in world.Systems) (s as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Residents of the town (not the player's squad): in the world, stored by a platoon, or held by a loaded save's platoon that is not in the
    /// world.
    /// </summary>
    static int Copies(SimWorld world, int playerFaction) =>
        world.Squads.All.Where(s => s.Faction != playerFaction).Sum(s => s.Members.Count)
        + world.Platoons.All.Where(p => p.State != PlatoonState.Loaded).Sum(p => p.Stored?.Members.Count ?? p.Saved?.Members.Count ?? 0);

    [Fact]
    public void A_pinned_platoon_kept_loaded_by_its_unloading_town_is_saved_and_loaded_as_one_copy()
    {
        var data = SyntheticTown.Data(SyntheticTown.Database(anatomy: true));
        string root = Temp("pinned");
        var a = NewWorld(data);
        var b = NewWorld(data);
        try
        {
            var player = a.Systems.OfType<PopulationSystem>().Single().StartPlayer(a, NewGameStart.From(data.Db.Find("40-t")!));
            a.Commands.Enqueue(new FocusCommand(SyntheticTown.Centre) { Tick = 0 });
            a.RunTicks(40);
            var chaser = a.Squads.All.First(s => s.Id != player.Id && s.Town >= 0).Members[0];
            var far = new Vector3(-50000, 0, -50000);
            a.Commands.Enqueue(new FocusCommand(far) { Tick = a.Tick });
            a.RunTick();
            a.Characters.Place(chaser.Slot, far, 0);
            foreach (var m in player.Members) a.Characters.Place(m.Slot, far + new Vector3(30, 0, 0), 0);
            a.RunTicks(350);

            Assert.Equal(SiteStatus.Unloaded, a.Systems.OfType<PopulationSystem>().Single().StatusOf(0));
            Assert.Single(a.Platoons.All, p => p.State == PlatoonState.Loaded && p.SquadId >= 0 && a.Squads.Find(p.SquadId) is { Pinned: true });

            int before = Copies(a, data.PlayerFaction);

            string folder = Path.Combine(root, QuickSave.Name);
            SaveCapture.Capture(a, data, new SaveClock(3, 7, 45), null).Write(folder);
            var loaded = SaveLoader.Load(b, SaveGame.Load(folder), data);
            b.Commands.Enqueue(new FocusCommand(far) { Tick = b.Tick });
            for (int i = 0; i < 20 && !b.PreparePopulation(far); i++) { }
            b.RunTicks(5);

            Assert.Equal(before, Copies(b, data.PlayerFaction));
            Assert.Single(b.Squads.All, s => s.Pinned && s.Faction != data.PlayerFaction);
            Assert.NotNull(loaded);
        }
        finally
        {
            Dispose(a);
            Dispose(b);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
