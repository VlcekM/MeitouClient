using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Movement;
using Meitou.Tests.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
using static Meitou.Tests.Combat.CombatFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class GetUpHoldTests
{
    /// <summary>
    /// A world with a knocked-out character (no ragdoll) and the get-up clip at <paramref name="playSpeed"/>; returns the woken character's slot, the
    /// animation and the tick it woke.
    /// </summary>
    static (SimWorld World, CharacterCold Cold, int Slot, CharacterAnimation Animation) Wake(float playSpeed, bool inBed = false)
    {
        var lib = new AnimationLibrary([new AnimationDefinition { Id = "a", Name = "stand 1", Clip = "stand 1", Area = AnimationArea.All, Idle = true,
                Loop = true, PlaySpeed = 1 },
            new AnimationDefinition { Id = "b", Name = "standing up 3", Clip = "standing up 3", Area = AnimationArea.All, PlaySpeed = playSpeed,
                    DisablesMovement = true }]);
        var lengths = new AnimationLengths().Add("standing up 3", 8f).Add("stand 1", 3f);
        var combat = new CombatSystem(KatanaTechniques(), Constants, BaseGame, BodyOptions.Default, lengths,
                new CombatOptions { SelfApproach = false, TickMedical = false });
        var walk = new OpenGroundWalkability((_, _) => 150);
        var world = new SimWorld(new WorldSettings { Seed = 3, Threads = 1, PublishSnapshots = false }, walk,
            [new MovementSystem(new PathService(walk, true)), new BodySystem(BaseGame), combat, new AnimationSystem(lib, lengths, combat: combat)]);
        var cold = new Combatant().MakeCold();
        cold.IsPlayer = true;
        var id = world.Characters.Spawn(new CharacterHot { Position = new Vector3(1000, 150, 1000), Health = 100, MaxSpeed = 60, WalkSpeed = 15,
                Mode = SpeedMode.Free }, cold, 0);
        world.RunTicks(5);
        cold.Medical!.KnockOut(2f);
        if (inBed) cold.InSomething = Meitou.Simulation.Locks.HeldState.Bed;
        while (!cold.Medical.Unconscious) world.RunTick();
        while (cold.Medical.Incapacitated) world.RunTick();
        return (world, cold, id.Slot, cold.Animation!);
    }

    /// <summary>
    /// Task_GetUp ends once the clip is past 0.86 (FUN_140334a20, docs/game/ragdolls.md "Getting up"): the hold on a waker with no whole-body ragdoll
    /// to blend out of ends on the tick the played clip does, at the clip's play speed, not a ragdoll blend later. Failed with the clip's end 23
    /// ticks before the hold's.
    /// </summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void The_hold_after_waking_ends_when_the_get_up_clip_does(float playSpeed)
    {
        var (world, cold, _, anim) = Wake(playSpeed);
        using var w = world;

        Assert.True(cold.Animation!.Action >= 0 && CharacterFacts.GettingUp(cold, world.Tick), "the get-up clip plays and holds");

        long clipEnd = -1, holdEnd = -1;
        for (int t = 0; t < 600 && (clipEnd < 0 || holdEnd < 0); t++)
        {
            world.RunTick();
            if (clipEnd < 0 && anim.Action < 0) clipEnd = world.Tick;
            if (holdEnd < 0 && !CharacterFacts.GettingUp(cold, world.Tick)) holdEnd = world.Tick;
        }

        Assert.InRange(holdEnd - clipEnd, 0, 2);
    }

    [Fact]
    public void A_waker_in_a_bed_plays_no_get_up_clip_and_is_not_held_for_one()
    {
        var (world, cold, _, anim) = Wake(1f, inBed: true);
        using var w = world;

        Assert.True(anim.Action < 0);
        Assert.False(CharacterFacts.GettingUp(cold, world.Tick));
    }
}
