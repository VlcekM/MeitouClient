using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.Save;
using Meitou.Simulation;
using Meitou.Simulation.Population;
using Meitou.Simulation.Ragdolls;
using Meitou.Simulation.Saving;
using Meitou.Tests.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Physics;

/// <summary>
/// Whole-body ragdolls of the install's characters freezing (docs/game/ragdolls.md "Sleep and limits", "Saves"): a body still falling when the freeze
/// window ends lands before it freezes, and a saved body frozen in the air falls again on load (upstream #1574).
/// </summary>
public class RagdollFallTests
{
    /// <summary>
    /// The open ground's height.
    /// </summary>
    const float Ground = 300;

    /// <summary>
    /// At 5x the ragdoll timers run on game time and the physics on real time: a body thrown up at 100 units/s is at the top of its arc, 50 over the
    /// ground and nearly still, when the still share is first asked, and still sinking when the freeze window ends. With advanced ragdolls it falls
    /// on and freezes on the ground. Catches a corpse frozen in mid-air beside the wall it was knocked off.
    /// </summary>
    [Fact, Slow]
    public void A_body_still_falling_when_the_freeze_window_ends_lands_before_it_freezes()
    {
        var install = GameInstall.Locate();

        Assert.SkipWhen(install is null, "Kenshi install not found");

        var (data, make) = Worlds(install!);
        using var w = make();
        var downed = KnockedOutAndSettled(w, data);
        w.PhysicsSpeed = 5;
        w.Characters.Cold(downed.Slot)!.Launch = new Vector3(0, 100, 0);
        w.RunTicks(2);

        Assert.False(Ragdolls(w).SaveState(downed)!.Frozen, "the launch wakes the body");

        for (int t = 0; t < 1200 && Ragdolls(w).SaveState(downed) is not { Frozen: true }; t++) w.RunTicks(1);
        var settled = Ragdolls(w).SaveState(downed);

        Assert.True(settled is { Frozen: true }, "the body freezes");
        Assert.InRange(Lowest(settled!), Ground - 1, Ground + RagdollSystem.RestGap);

        foreach (var s in w.Systems) (s as IDisposable)?.Dispose();
    }

    /// <summary>
    /// A save holding a frozen pose 40 over the ground (frozen in the air before the falling test) loads as an unsettled pose: the body falls and
    /// freezes on the ground. Catches the saved corpse staying in the air on every load.
    /// </summary>
    [Fact, Slow]
    public void A_saved_body_frozen_in_the_air_falls_on_load()
    {
        var install = GameInstall.Locate();

        Assert.SkipWhen(install is null, "Kenshi install not found");

        var (data, make) = Worlds(install!);
        var levels = InstallData.Levels!;
        using var a = make();
        var downed = KnockedOutAndSettled(a, data);
        string root = Path.Combine(Path.GetTempPath(), "meitou-ragdoll-fall-" + Guid.NewGuid().ToString("N"));
        try
        {
            var save = SaveCapture.Capture(a, data, new SaveClock(1, 13, 39), null, new SaveCaptureOptions { Placements = levels.Towns().ToList() });
            var state = save.Characters.Single(c => c.GameState?.Strings.ContainsKey(RagdollSave.Key) == true).GameState!;
            var frozen = RagdollSave.Read(state)!;
            var lifted = frozen.Pose.Select(p => new RigidTransform(p.Position + new Vector3(0, 40, 0), p.Rotation)).ToArray();
            RagdollSave.Write(state, new SavedRagdoll(true, 7.0333f, lifted));
            string folder = QuickSave.Folder(root);
            save.Write(folder);

            using var b = make();
            SaveLoader.Load(b, SaveGame.Load(folder), data, new SaveLoadOptions { Appearances = data.Appearances });
            b.Player.Faction = data.PlayerFaction;
            var downedB = b.Squads.Find(b.Player.Squad)!.Members[0];
            b.RunTicks(30);

            Assert.False(Ragdolls(b).SaveState(downedB)!.Frozen, "the pose in the air settles again");

            for (int t = 0; t < 600 && Ragdolls(b).SaveState(downedB) is not { Frozen: true }; t++) b.RunTicks(1);
            var settled = Ragdolls(b).SaveState(downedB);

            Assert.True(settled is { Frozen: true }, "the body freezes");
            Assert.InRange(Lowest(settled!), Ground - 1, Ground + RagdollSystem.RestGap);

            foreach (var s in b.Systems) (s as IDisposable)?.Dispose();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            foreach (var s in a.Systems) (s as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The install's population data and a maker of worlds on open ground with bodies, animation and ragdolls, paths found at once.
    /// </summary>
    static (PopulationData, Func<SimWorld>) Worlds(GameInstall install)
    {
        var db = InstallData.BaseGame!;
        var data = PopulationData.Create(db, InstallData.Levels!.Towns(), new GeneratedAppearances(db, install.Root));
        var assets = new Meitou.Data.Ogre.AssetLocator(install);
        var rigs = new Meitou.Data.Characters.CharacterRigs(db, p => assets.Configured.Find(Path.GetFileName(p.Replace('\\', '/'))) ?? assets.Find(p),
                n => assets.Find(n));
        var lengths = AnimationLengths.Load(install.Root, db);
        var walk = new OpenGroundWalkability((_, _) => Ground);

        return (data, Make);

        SimWorld Make() =>
            new(new WorldSettings { Seed = 5, Threads = 2, PublishSnapshots = false }, walk, StandardSystems.Build(data, walk, new StandardSystemOptions
            {
                Parts = StandardParts.Population | StandardParts.Player | StandardParts.Trade | StandardParts.Movement
                    | StandardParts.Body | StandardParts.Animation,
                SynchronousPaths = true, Population = new PopulationSettings { CheckEveryTicks = 1 }, Ragdolls = rigs, AnimationLengths = lengths,
            }).Systems);
    }

    /// <summary>
    /// Starts the player's squad and knocks its first member out until its ragdoll has frozen on the ground; returns that member.
    /// </summary>
    static CharacterId KnockedOutAndSettled(SimWorld w, PopulationData data)
    {
        var squad = w.Systems.OfType<PopulationSystem>().Single().StartPlayer(w,
                NewGameStart.Find(NewGameStart.LoadAll(InstallData.BaseGame!), NewGameStart.DefaultName)!);
        w.Player.Faction = data.PlayerFaction;
        w.RunTicks(30);
        var downed = squad.Members[0];
        w.Characters.Cold(downed.Slot)!.Medical!.KnockOut(6000);
        for (int t = 0; t < 600 && Ragdolls(w).SaveState(downed) is not { Frozen: true }; t++) w.RunTicks(1);

        Assert.True(Ragdolls(w).SaveState(downed) is { Frozen: true }, "the knocked-out character's ragdoll settles");

        return downed;
    }

    /// <summary>
    /// The height of the lowest body centre.
    /// </summary>
    static float Lowest(SavedRagdoll ragdoll) => ragdoll.Pose.Min(p => p.Position.Y);

    /// <summary>
    /// The world's ragdoll system.
    /// </summary>
    static RagdollSystem Ragdolls(SimWorld w) => w.Systems.OfType<RagdollSystem>().Single();
}
