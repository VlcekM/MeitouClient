using System.Collections;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using HarmonyLib;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Ogre;
using Meitou.Data.Physics;
using Meitou.Simulation;
using Meitou.Simulation.Ragdolls;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Physics;

public sealed class RagdollHandoffTests
{
    static SimWorld Empty(params ITickSystem[] systems) => new(new WorldSettings(), new OpenGroundWalkability((_, _) => 0), systems);
    static RagdollSystem Native() => new(new CharacterRigs(new GameDatabase(), _ => null, _ => null));

    [Fact]
    public void A_pending_launch_transfers_once_and_competing_owners_cannot_claim_or_release_it()
    {
        using var native = Native(); using var world = Empty(native);
        var cold = new CharacterCold { Blow = (new(11, 12, 13), new(2, 3, 4)) };
        var id = world.Characters.Spawn(new() { Position = new(20, 30, 40) }, cold, 0);
        ulong before = world.StateHash();
        Assert.True(native.TryCapture(world, id, out var captured));
        Assert.Equal(new RagdollLaunch(new(11, 12, 13), new(2, 3, 4)), captured.PendingLaunch);
        Assert.Equal(before, world.StateHash());
        Assert.NotNull(cold.Blow);
        var owner = new object(); var rival = new object();
        Assert.True(native.TryClaim(world, id, owner, out var claimed));
        Assert.Null(cold.Blow);
        Assert.Equal(captured.PendingLaunch, claimed.PendingLaunch);
        Assert.NotEqual(before, world.StateHash());
        Assert.False(native.TryClaim(world, id, rival, out _));
        Assert.False(native.ReleaseClaim(id, rival));
        Assert.True(native.TryClaim(world, id, owner, out var repeated));
        Assert.Same(claimed, repeated);
        Assert.True(native.ReleaseClaim(id, owner));
        Assert.True(native.TryClaim(world, id, rival, out var next));
        Assert.Null(next.PendingLaunch);
        Assert.True(native.ReleaseClaim(id, rival));
    }

    [Fact]
    public void The_handoff_copies_actual_body_motion_and_unphysical_child_pose_before_removing_native_actors()
    {
        using var native = Native(); using var world = Empty(native);
        var id = world.Characters.Spawn(new() { Position = new(10, 20, 30), Yaw = .4f }, new(), 0);
        var (physics, handle, published) = SeedNativeBody(native, id, world.Tick);
        var expectedBody = new RigidTransform(new(8, 12, 16), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .3f));
        Assert.True(native.TryCapture(world, id, out var first));
        var motion = Assert.Single(first.Bodies);
        Assert.Equal(expectedBody, motion.Body);
        Assert.Equal(new Vector3(4, 5, 6), motion.Linear);
        Assert.Equal(new Vector3(1, 2, 3), motion.Angular);
        Assert.Equal(new Vector3(0, 2, 0), motion.BoneWorld.Position - motion.Body.Position);
        Assert.Equal(new RagdollLaunch(new(12, 13, 14), new(2, 3, 4)), first.AppliedLaunch);
        Assert.NotSame(published, first.Bones);
        Assert.Equal(published.Position[1], first.Bones!.Position[1]);
        first.Bones.Position[1] = new(999);
        first.Bones.Components![1] = 999;
        Assert.True(native.TryCapture(world, id, out var second));
        Assert.Equal(published.Position[1], second.Bones!.Position[1]);
        Assert.Equal(0, second.Bones.Components![1]);
        Assert.True(native.TryClaim(world, id, new object(), out var owned));
        Assert.Equal(0, native.Count);
        Assert.False(physics.Simulation.Bodies.BodyExists(handle));
        Assert.Equal(motion, Assert.Single(owned.Bodies));
        Assert.Equal(published.Position[1], owned.Bones!.Position[1]);
    }

    [Fact]
    public void Removed_generations_and_parallel_or_unregistered_handoffs_are_rejected()
    {
        using var native = Native(); using var world = Empty(native);
        var id = world.Characters.Spawn(new(), new(), 0); var owner = new object();
        Assert.True(native.TryClaim(world, id, owner, out _));
        Assert.True(world.Characters.Remove(id));
        var replacement = world.Characters.Spawn(new(), new(), 0);
        Assert.NotEqual(id, replacement);
        Assert.False(native.TryClaim(world, id, owner, out _));
        Assert.False(native.TryCapture(world, id, out _));
        Assert.True(native.TryClaim(world, replacement, new object(), out _));
        world.Characters.Phase = TablePhase.Parallel;
        try
        {
            Assert.Throws<InvalidOperationException>(() => native.TryCapture(world, replacement, out _));
            Assert.Throws<InvalidOperationException>(() => native.TryClaim(world, replacement, owner, out _));
            Assert.Throws<InvalidOperationException>(() => native.ReleaseClaim(id, owner));
        }
        finally { world.Characters.Phase = TablePhase.Outside; }
        using var foreign = Empty();
        Assert.Throws<ArgumentException>(() => native.TryCapture(foreign, replacement, out _));
    }

    [Fact]
    public void Returning_a_survivor_blends_from_the_complete_last_pose_and_releases_the_claim()
    {
        using var native = Native(); using var world = Empty(native);
        var id = world.Characters.Spawn(new(), new(), 0);
        SeedNativeBody(native, id, world.Tick); var owner = new object();
        Assert.True(native.TryClaim(world, id, owner, out _));
        var final = new BonePose(2) { AbsolutePositions = true, Components = [0, 0] };
        final.Set[0] = final.Set[1] = true;
        final.Position[0] = new(2, 3, 4); final.Position[1] = new(6, 7, 8);
        final.Rotation[0] = final.Rotation[1] = Quaternion.Identity;
        var placement = Matrix4x4.CreateTranslation(30, 40, 50);
        Assert.False(native.ReturnClaim(world, id, new object(), final, placement));
        Assert.True(native.ReturnClaim(world, id, owner, final, placement));
        Assert.False(native.ReleaseClaim(id, owner));
        Assert.True(native.TryCapture(world, id, out var restored));
        var initial = Assert.IsType<BonePose>(restored.Bones);
        Assert.True(initial.AbsolutePositions); Assert.Equal(1, initial.Weight);
        for (int bone = 0; bone < 2; bone++)
            Assert.InRange(Vector3.Distance(Vector3.Transform(final.Position[bone], placement), Vector3.Transform(initial.Position[bone], restored.Placement)), 0, .0001f);
        final.Position[1] = new(999); // Return copied even the nonphysical child, not a live module array.
        world.RunTicks(1);
        var blending = Assert.IsType<BonePose>(native.Publish(id));
        Assert.InRange(blending.Weight, .001f, .999f);
        Assert.True(native.TryCapture(world, id, out var current));
        Assert.InRange(Vector3.Distance(new(36, 47, 58), Vector3.Transform(blending.Position[1], current.Placement)), 0, .0001f);
        world.RunTicks(60);
        Assert.Null(native.Publish(id)); Assert.Equal(0, native.Count);
        Assert.True(native.TryClaim(world, id, new object(), out _));
    }

    [Fact]
    public void Capture_distinguishes_a_newly_assigned_launch_from_kept_and_later_body_motion()
    {
        using var native = Native(); using var world = Empty(native);
        var id = world.Characters.Spawn(new() { Position = new(10, 20, 30), Yaw = .4f }, new(), 0);
        var (physics, oldHandle, _) = SeedNativeBody(native, id, world.Tick);
        var body = ((IDictionary)AccessTools.Field(typeof(RagdollSystem), "ragdolls").GetValue(native)!)[id]!;
        var type = body.GetType();
        void Set(string field, object value) => AccessTools.Field(type, field).SetValue(body, value);
        var original = (RagdollDefinition)AccessTools.Field(type, "Definition").GetValue(body)!;
        var added = new RagdollBody { Bone = 1, Name = "Bip01 L UpperArm", Type = PhysicsShapeType.Box, Size = new(2), Mass = 10,
            Bind = new(new(20, 20, 20), Quaternion.Identity), BoneInBody = RigidTransform.Identity };
        var definition = new RagdollDefinition { Bodies = [original.Bodies[0], added], Joints = [], BodyOfBone = [0, 1], Joined = [0, 0] };
        var at = (RigidTransform[])AccessTools.Field(type, "Pose").GetValue(body)!;
        Set("Definition", definition); Set("Bodies", new BodyHandle?[] { oldHandle, null }); Set("Dynamic", new[] { true, false });
        Set("Pose", new[] { at[0], added.Bind });
        var priorMovement = new Vector3(80, 3, 4); var push = new Vector3(50, 10, 0);
        var raw = new RagdollLaunch(priorMovement + push, push);
        AccessTools.Method(typeof(RagdollSystem), "Rebuild").Invoke(native,
            [body, RagdollPart.Whole, new[] { at[0], added.Bind }, (raw.Velocity, raw.Push), true, world.Seed]);
        Set("LaunchTick", world.Tick); Set("AppliedLaunch", raw);
        var actors = (BodyHandle?[])AccessTools.Field(type, "Bodies").GetValue(body)!;
        Assert.True(native.TryCapture(world, id, out var initial));
        Assert.Null(initial.Bodies[0].AssignedLaunch);
        var assigned = Assert.IsType<RagdollAssignedLaunch>(initial.Bodies[1].AssignedLaunch);
        Assert.InRange(assigned.Linear.Length(), 99.999f, 100.001f); // Native clamps the complete movement + push.
        var contact = new Vector3(-3, -4, 2); var angularContact = new Vector3(.1f, .2f, -.3f);
        var launched = physics.Simulation.Bodies[actors[1]!.Value];
        launched.Velocity = new BodyVelocity(assigned.Linear + contact, assigned.Angular + angularContact);
        Assert.True(native.TryCapture(world, id, out var captured));
        Assert.Equal(new Vector3(4, 5, 6), captured.Bodies[0].Linear);
        Assert.Equal(new Vector3(1, 2, 3), captured.Bodies[0].Angular);
        Assert.Null(captured.Bodies[0].AssignedLaunch);
        Assert.Equal(assigned, captured.Bodies[1].AssignedLaunch);
        Assert.Equal(assigned.Linear + contact, captured.Bodies[1].Linear);
        Assert.Equal(assigned.Angular + angularContact, captured.Bodies[1].Angular);
        world.RunTick();
        Assert.True(native.TryCapture(world, id, out var later));
        Assert.All(later.Bodies, actor => Assert.Null(actor.AssignedLaunch));
    }

    [Fact]
    public void Partial_ragdoll_virtual_and_pinned_entries_keep_skin_positions_but_do_not_claim_dynamic_motion()
    {
        using var native = Native(); using var world = Empty(native);
        var id = world.Characters.Spawn(new() { Position = new(10, 20, 30), Yaw = .4f }, new(), 0);
        var (physics, handle, _) = SeedNativeBody(native, id, world.Tick);
        var body = ((IDictionary)AccessTools.Field(typeof(RagdollSystem), "ragdolls").GetValue(native)!)[id]!;
        var type = body.GetType();
        void Set(string field, object value) => AccessTools.Field(type, field).SetValue(body, value);
        var original = (RagdollDefinition)AccessTools.Field(type, "Definition").GetValue(body)!;
        var virtualBody = new RagdollBody { Bone = 1, Name = "Bip01 L Forearm", Type = PhysicsShapeType.Box, Size = new(2), Mass = 10,
            Bind = new(new(20, 10, 0), Quaternion.Identity), BoneInBody = RigidTransform.Identity };
        var pin = new RagdollBody { Bone = 2, Name = "Bip01 Spine1", Type = PhysicsShapeType.Box, Size = new(2), Mass = 10,
            Bind = new(new(40, 10, 0), Quaternion.Identity), BoneInBody = RigidTransform.Identity };
        var box = new Box(2, 2, 2); var shape = physics.Simulation.Shapes.Add(box);
        var pinned = physics.AddBody(BodyDescription.CreateKinematic(new RigidPose(pin.Bind.Position), RagdollPhysics.Collidable(shape), new BodyActivityDescription(.01f)), new BodyTag { Ragdoll = 1, Index = 2 });
        var definition = new RagdollDefinition { Bodies = [original.Bodies[0], virtualBody, pin], Joints = [], BodyOfBone = [0, 1, 2], Joined = [0, 0, 0] };
        var oldPose = (RigidTransform[])AccessTools.Field(type, "Pose").GetValue(body)!;
        Set("Definition", definition); Set("Bodies", new BodyHandle?[] { handle, null, pinned }); Set("Dynamic", new[] { true, false, false });
        Set("Pose", new[] { oldPose[0], virtualBody.Bind, pin.Bind });
        var skeleton = new OgreSkeleton();
        skeleton.Bones.Add(new() { Handle = 0, Name = "Bip01", Position = Vector3.Zero });
        skeleton.Bones.Add(new() { Handle = 1, Parent = 0, Name = virtualBody.Name, Position = virtualBody.Bind.Position });
        skeleton.Bones.Add(new() { Handle = 2, Parent = 0, Name = pin.Name, Position = pin.Bind.Position });
        Set("Setup", new CharacterPoseSetup { Rig = new(skeleton) }); Set("Published", new BonePose(3));
        Assert.True(native.TryCapture(world, id, out var captured));
        Assert.True(captured.Bodies[0].HasMotion); Assert.False(captured.Bodies[1].HasMotion); Assert.False(captured.Bodies[2].HasMotion);
        Assert.Equal(virtualBody.Bind.Position, captured.Bodies[1].BoneWorld.Position); Assert.Equal(pin.Bind.Position, captured.Bodies[2].BoneWorld.Position);
        Assert.True(new RagdollBodyMotion(0, RigidTransform.Identity, RigidTransform.Identity, Vector3.Zero, Vector3.Zero).HasMotion);

    }

    static (RagdollPhysics Physics, BodyHandle Handle, BonePose Published) SeedNativeBody(RagdollSystem native, CharacterId id, long tick)
    {
        var physics = (RagdollPhysics)AccessTools.Field(typeof(RagdollSystem), "physics").GetValue(native)!;
        var box = new Box(2, 2, 2); var shape = physics.Simulation.Shapes.Add(box);
        var at = new RigidTransform(new(8, 12, 16), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .3f));
        var handle = physics.AddBody(BodyDescription.CreateDynamic(new RigidPose(at.Position, at.Rotation),
            new BodyVelocity(new(4, 5, 6), new(1, 2, 3)), box.ComputeInertia(10), RagdollPhysics.Collidable(shape), new BodyActivityDescription(.01f)), new BodyTag { Ragdoll = 1 });
        var definition = new RagdollDefinition { Bodies = [new RagdollBody { Bone = 0, Name = "Bip01", Type = PhysicsShapeType.Box, Size = new(2), Mass = 10,
            Bind = RigidTransform.Identity, BoneInBody = new(new(0, 2, 0), Quaternion.Identity) }], Joints = [], BodyOfBone = [0, -1], Joined = [0] };
        var skeleton = new OgreSkeleton();
        skeleton.Bones.Add(new() { Handle = 0, Name = "Bip01", Position = Vector3.Zero });
        skeleton.Bones.Add(new() { Handle = 1, Parent = 0, Name = "unphysical child", Position = new(0, 1, 0) });
        var published = new BonePose(2) { AbsolutePositions = true, Components = [0, 0] };
        for (int b = 0; b < 2; b++) { published.Set[b] = true; published.Position[b] = new(1, 2 + b, 3); published.Rotation[b] = Quaternion.Identity; }
        var type = AccessTools.Inner(typeof(RagdollSystem), "Ragdoll");
        var body = Activator.CreateInstance(type, nonPublic: true)!;
        void Set(string field, object value) => AccessTools.Field(type, field).SetValue(body, value);
        Set("Id", id); Set("Definition", definition); Set("Setup", new CharacterPoseSetup { Rig = new(skeleton) }); Set("Scale", 1f);
        Set("Bodies", new BodyHandle?[] { handle }); Set("Dynamic", new[] { true }); Set("Pose", new[] { at }); Set("Published", published);
        Set("PublishedIn", Matrix4x4.CreateRotationY(.4f) * Matrix4x4.CreateTranslation(10, 20, 30));
        Set("LaunchTick", tick); Set("AppliedLaunch", new RagdollLaunch(new(12, 13, 14), new(2, 3, 4)));
        ((IDictionary)AccessTools.Field(typeof(RagdollSystem), "ragdolls").GetValue(native)!).Add(id, body);
        ((IList)AccessTools.Field(typeof(RagdollSystem), "order").GetValue(native)!).Add(id);
        return (physics, handle, published);
    }
}
