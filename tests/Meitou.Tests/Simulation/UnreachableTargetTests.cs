using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Movement;
using Meitou.Tests.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
using static Meitou.Tests.Combat.CombatFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// A flat ground with a box nobody may stand in; a goal inside it is answered with the path to the nearest point outside, as the navmesh does.
/// </summary>
sealed class BoxWalk(Vector2 min, Vector2 max) : IWalkability
{
    public float GroundHeight(float x, float z) => 150;
    bool In(float x, float z) => x > min.X && x < max.X && z > min.Y && z < max.Y;
    public bool IsWalkable(float x, float z) => !In(x, z);
    public PathResult FindPath(Vector3 from, Vector3 to)
    {
        if (!In(to.X, to.Z)) return new([from, to]);
        float cx = Math.Clamp(to.X, min.X, max.X), cz = Math.Clamp(to.Z, min.Y, max.Y);
        // push to the nearest edge
        float dl = to.X - min.X, dr = max.X - to.X, dt = to.Z - min.Y, db = max.Y - to.Z;
        float m = MathF.Min(MathF.Min(dl, dr), MathF.Min(dt, db));
        var p = m == dl ? new Vector3(min.X, 150, to.Z) : m == dr ? new Vector3(max.X, 150, to.Z) : m == dt ? new Vector3(to.X, 150, min.Y)
                : new Vector3(to.X, 150, max.Y);
        return new([from, p]);
    }
}

/// <summary>
/// A fighter whose target stands where no path reaches (an island, a closed building, a slope too steep to walk) gives it up as the original's path
/// chase does.
/// </summary>
public class UnreachableTargetTests
{
    static (SimWorld World, CombatSystem Combat, CharacterId A, CharacterId B) Rig(bool player, bool blocked)
    {
        var combat = new CombatSystem(KatanaTechniques(), Constants, BaseGame, BodyOptions.Default, DuelFixture.Lengths,
                new CombatOptions { SelfApproach = false, TickMedical = false, RecordLog = true });
        IWalkability walk = blocked ? new BoxWalk(new(1080, 900), new(1200, 1100)) : new OpenGroundWalkability((_, _) => 150);
        var world = new SimWorld(new WorldSettings { Seed = 1, Threads = 1, PublishSnapshots = false, MinPartitionSize = 1 }, walk,
            [new PursuitSystem(combat), new MovementSystem(new PathService(walk, true)), new BodySystem(BaseGame), combat,
                new RetaliationSystem(combat)]);
        var cold = new Combatant { Name = "a" }.MakeCold();
        cold.IsPlayer = player;
        var a = world.Characters.Spawn(new CharacterHot { Position = new(1000, 150, 1000), Health = 100, MaxSpeed = 60, WalkSpeed = 15,
                Mode = SpeedMode.Free }, cold, 0);
        var b = world.Characters.Spawn(new CharacterHot { Position = new(1140, 150, 1000), Health = 100, MaxSpeed = 0, WalkSpeed = 0,
                Mode = SpeedMode.Free }, new Combatant { Name = "b" }.MakeCold(), 0);
        world.Commands.Enqueue(new AttackOrder([a], b) { Tick = 0, Picked = player });
        return (world, combat, a, b);
    }

    /// <summary>
    /// The walkability answers a goal nobody can stand at with the way to the nearest point outside; a chase on that path used to walk to the edge
    /// and stand there, still on the attack task, for good (the path "existed", so no failure was ever reported). Now the target goes into the path
    /// failures, the choice leaves it out and the fight ends, for an NPC and the player's pick alike.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_target_no_path_reaches_is_given_up(bool player)
    {
        var (world, combat, a, b) = Rig(player, true);
        using var _ = world;
        world.RunTicks((int)(5 / world.TickSeconds));
        var cold = world.Characters.Cold(a.Slot)!;
        Assert.True(PursuitSystem.PathImpossible(cold, b, world.Tick), "the target is a path failure");
        Assert.NotEqual((byte)CharacterTask.Attack, world.Characters.Previous[a.Slot].Task);
        Assert.True(combat.StateOf(a).TargetSlot < 0, "the fight has no target");
    }

    [Fact]
    public void A_reachable_target_is_still_chased()
    {
        var (world, _, a, b) = Rig(false, false);
        using var w = world;
        world.RunTicks((int)(5 / world.TickSeconds));
        Assert.False(PursuitSystem.PathImpossible(world.Characters.Cold(a.Slot)!, b, world.Tick));
        Assert.True(world.Characters.Previous[a.Slot].Position.X > 1050, "walked towards the target");
    }
}
