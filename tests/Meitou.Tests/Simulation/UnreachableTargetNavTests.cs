using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Navigation;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Tests.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
using static Meitou.Tests.Combat.CombatFixtures;
using Xunit.v3;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Fighters chasing a still target on the Hub's real navmesh (the install's data): one on a slope too steep to climb, whose path ends well short of
/// it, is given up within a second (before, the fighter walked to the foot of the slope and stood on the attack task for good); one upstairs in a
/// building is still chased up the stairs.
/// </summary>
[Slow]
public class UnreachableTargetNavTests
{
    static readonly Vector3 Start = new(-51290, 1566, 2625);

    static async Task<(NavSystem Nav, TerrainHeightmap Map, string Dir)> Load(GameInstall install)
    {
        var dir = Path.Combine(Path.GetTempPath(), "meitou-unr-" + Guid.NewGuid().ToString("N"));
        var map = TerrainHeightmap.Open(install);
        var nav = new NavSystem(install, InstallData.FullLoadOrder!, InstallData.Levels!, (x, z) => (float)map.HeightAt(x, z),
                cache: new NavMeshCache(dir));
        await nav.LoadZone(new ZoneCoordinate(20, 32));
        await nav.LoadZone(new ZoneCoordinate(21, 32));
        return (nav, map, dir);
    }

    static (SimWorld World, CharacterId A, CharacterId B) Fight(NavSystem nav, Vector3 target, bool player)
    {
        var combat = new CombatSystem(KatanaTechniques(), Constants, BaseGame, BodyOptions.Default, DuelFixture.Lengths,
                new CombatOptions { SelfApproach = false, TickMedical = false });
        var walk = nav.Walkability;
        var world = new SimWorld(new WorldSettings { Seed = 1, Threads = 1, PublishSnapshots = false, MinPartitionSize = 1 }, walk,
            [new PursuitSystem(combat), new MovementSystem(new PathService(walk, true)), new BodySystem(BaseGame), combat,
                new RetaliationSystem(combat)]);
        var cold = new Combatant { Name = "a" }.MakeCold();
        cold.IsPlayer = player;
        var a = world.Characters.Spawn(new CharacterHot { Position = Start, Health = 100, MaxSpeed = 60, WalkSpeed = 15, Mode = SpeedMode.Free },
                cold, 0);
        var b = world.Characters.Spawn(new CharacterHot { Position = target, Health = 100, MaxSpeed = 0, WalkSpeed = 0, Mode = SpeedMode.Free },
                new Combatant { Name = "b" }.MakeCold(), 0);
        world.Commands.Enqueue(new AttackOrder([a], b) { Tick = 0, Picked = player });
        return (world, a, b);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_target_on_a_slope_too_steep_to_climb_is_given_up_within_a_second(bool player)
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var (nav, map, dir) = await Load(install!);
        try
        {
            // The first steep spot (45 degrees and over, 150 or more from the start) whose navmesh path ends over 40 units short of it.
            var rnd = new Random(3);
            Vector3? spot = null;
            for (int k = 0; k < 20000 && spot is null; k++)
            {
                float x = Start.X + (float)(rnd.NextDouble() * 2400 - 1200), z = Start.Z + (float)(rnd.NextDouble() * 2400 - 1200);
                float h = (float)map.HeightAt(x, z);
                float gx = ((float)map.HeightAt(x + 18, z) - (float)map.HeightAt(x - 18, z)) / 36, gz = ((float)map.HeightAt(x, z + 18)
                        - (float)map.HeightAt(x, z - 18)) / 36;
                if (MathF.Atan(MathF.Sqrt(gx * gx + gz * gz)) * 180 / MathF.PI < 45
                        || Vector2.Distance(new(Start.X, Start.Z), new(x, z)) < 150) continue;
                var goal = new Vector3(x, h, z);
                var path = nav.Walkability.FindPath(Start, goal);
                if (path.Found && Vector3.Distance(path.Points[^1], goal) > 40) spot = goal;
            }
            Assert.NotNull(spot);
            var (world, a, b) = Fight(nav, spot!.Value, player);
            using var _ = world;
            world.RunTicks((int)(1 / world.TickSeconds));
            Assert.True(PursuitSystem.PathImpossible(world.Characters.Cold(a.Slot)!, b, world.Tick), "the target is a path failure");
            world.RunTicks((int)(4 / world.TickSeconds));
            Assert.NotEqual((byte)CharacterTask.Attack, world.Characters.Previous[a.Slot].Task);
        }
        finally
        {
            nav.Dispose();
            map.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// A target on a floor above is walked to up its stairs (the path goes to the target's floor, not to the ground under it) and is never taken for
    /// an unreachable one.
    /// </summary>
    [Fact]
    public async Task A_target_upstairs_is_chased_up_the_stairs_not_given_up()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var (nav, map, dir) = await Load(install!);
        try
        {
            var rnd = new Random(3);
            Vector3? spot = null;
            for (int k = 0; k < 20000 && spot is null; k++)
            {
                float x = Start.X + (float)(rnd.NextDouble() * 1600 - 800), z = Start.Z + (float)(rnd.NextDouble() * 1600 - 800);
                if (!nav.Walkability.TryStand(x, z, 1650, out float y) || !nav.Walkability.IsWalkable(x, z, 1650) || y < Start.Y + 40) continue;
                if (Vector2.Distance(new(Start.X, Start.Z), new(x, z)) is > 150 and < 500) spot = new Vector3(x, y, z);
            }
            Assert.NotNull(spot);
            var (world, a, b) = Fight(nav, spot!.Value, player: false);
            using var _ = world;
            world.RunTicks((int)(30 / world.TickSeconds));
            Assert.False(PursuitSystem.PathImpossible(world.Characters.Cold(a.Slot)!, b, world.Tick));
            Assert.True(Vector3.Distance(world.Characters.Previous[a.Slot].Position, world.Characters.Previous[b.Slot].Position) < 40,
                    "reached the target upstairs");
        }
        finally
        {
            nav.Dispose();
            map.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
