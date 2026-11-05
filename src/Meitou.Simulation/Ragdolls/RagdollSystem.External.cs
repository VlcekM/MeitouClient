using System.Collections.ObjectModel;
using System.Numerics;
using Meitou.Data.Characters;

namespace Meitou.Simulation.Ragdolls;

/// <summary>One actor at a physics handoff: its original bone handle, world transforms and centre-of-mass velocities.</summary>
public readonly record struct RagdollBodyMotion(int Bone, RigidTransform Body, RigidTransform BoneWorld, Vector3 Linear, Vector3 Angular)
{
    /// <summary>Velocity assigned by this tick's new-body launch, before the physics step. Null for actors carrying on with prior motion.</summary>
    public RagdollAssignedLaunch? AssignedLaunch { get; init; }
    /// <summary>False for a virtual or pinned body without simulated velocity. Its pose remains useful for skinning, not velocity inheritance.</summary>
    public bool HasMotion { get; init; } = true;
}
public readonly record struct RagdollAssignedLaunch(Vector3 Linear, Vector3 Angular);
public readonly record struct RagdollLaunch(Vector3 Velocity, Vector3 Push);

/// <summary>A detached copy of a native ragdoll. Bones are in Placement's model space; body transforms are in world space.
/// A pending launch has not yet been applied. Existing actor velocities already include any consumed launch.</summary>
public sealed record RagdollHandoff(IReadOnlyList<RagdollBodyMotion> Bodies, BonePose? Bones, Matrix4x4 Placement, RagdollLaunch? PendingLaunch)
{
    /// <summary>A launch already applied by the native system this same tick; use its Push for a new topology's separation, never add its Velocity again.</summary>
    public RagdollLaunch? AppliedLaunch { get; init; }
}

public sealed partial class RagdollSystem
{
    sealed record ExternalClaim(object Owner, RagdollHandoff Handoff, Ragdoll? Native);
    readonly Dictionary<CharacterId, ExternalClaim> externalClaims = [];
    World? claimWorld;

    /// <summary>Captures current native motion without changing ownership. Serial phases only; no live physics arrays escape.</summary>
    public bool TryCapture(World world, CharacterId id, out RagdollHandoff handoff)
    {
        ValidateClaimWorld(world);
        handoff = null!;
        if (!world.Characters.TryResolveNext(id, out int slot) || world.Characters.Cold(slot) is not { } cold) return false;
        if (externalClaims.TryGetValue(id, out var claimed)) { handoff = claimed.Handoff; return true; }
        var actors = new List<RagdollBodyMotion>(); BonePose? bones = null;
        var placement = Matrix4x4.CreateTranslation(world.Characters.Next[slot].Position);
        if (ragdolls.TryGetValue(id, out var body))
        {
            placement = Placement(body, cold, world.Characters.Next[slot]);
            for (int b = 0; b < body.Definition.Bodies.Length; b++)
            {
                var actor = body.Definition.Bodies[b];
                Vector3 linear = default, angular = default;
                if (b < body.Bodies.Length && body.Bodies[b] is { } handle)
                {
                    var velocity = physics.Simulation.Bodies[handle].Velocity;
                    linear = velocity.Linear; angular = velocity.Angular;
                }
                actors.Add(new(actor.Bone, body.Pose[b], actor.BoneInBody.Then(body.Pose[b]), linear, angular)
                {
                    HasMotion = b < body.Bodies.Length && body.Bodies[b] is not null && b < body.Dynamic.Length && body.Dynamic[b],
                    AssignedLaunch = body.LaunchTick == world.Tick && body.AppliedLaunch is not null && b < body.AssignedLaunch.Length ? body.AssignedLaunch[b] : null,
                });
            }
            if ((body.PublishedIn == placement ? body.Published ?? Build(body, placement) : Build(body, placement)) is { } pose) bones = CopyPose(pose);
        }
        var launch = cold.Blow is { } pending ? new RagdollLaunch(pending.Velocity, pending.Push) : (RagdollLaunch?)null;
        handoff = new(new ReadOnlyCollection<RagdollBodyMotion>(actors), bones, placement, launch)
        { AppliedLaunch = ragdolls.TryGetValue(id, out var nativeBody) && nativeBody.LaunchTick == world.Tick ? nativeBody.AppliedLaunch : null };
        return true;
    }

    /// <summary>Atomically captures and releases this character's native actors, then suppresses native re-creation until its owner releases the claim.
    /// A competing owner is refused. Repeating a claim by the same owner returns its original handoff.</summary>
    public bool TryClaim(World world, CharacterId id, object owner, out RagdollHandoff handoff)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ValidateClaimWorld(world);
        handoff = null!;
        if (!world.Characters.TryResolveNext(id, out _)) return false;
        if (externalClaims.TryGetValue(id, out var claim))
        {
            if (!ReferenceEquals(claim.Owner, owner)) return false;
            handoff = claim.Handoff; return true;
        }
        if (!TryCapture(world, id, out handoff)) return false;
        ragdolls.TryGetValue(id, out var body);
        externalClaims.Add(id, new(owner, handoff, body));
        if (body is not null) Destroy(body);
        order.Remove(id);
        world.Characters.Cold(id.Slot)!.Blow = null;
        return true;
    }

    /// <summary>Releases only this owner's claim. The ordinary native ragdoll rules resume on the next tick.</summary>
    public bool ReleaseClaim(CharacterId id, object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (claimWorld?.Characters.Phase == TablePhase.Parallel) throw new InvalidOperationException("Ragdoll ownership changes require a serial phase.");
        return externalClaims.TryGetValue(id, out var claim) && ReferenceEquals(claim.Owner, owner) && externalClaims.Remove(id);
    }

    /// <summary>Returns a claimed character from a complete external pose. The native get-up blend starts at this pose,
    /// including unphysical child bones, before ordinary native rules resume. The input is copied in world space.</summary>
    public bool ReturnClaim(World world, CharacterId id, object owner, BonePose pose, Matrix4x4 placement)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(pose);
        ValidateClaimWorld(world);
        if (!externalClaims.TryGetValue(id, out var claim) || !ReferenceEquals(claim.Owner, owner)
            || !world.Characters.TryResolveNext(id, out int slot) || world.Characters.Cold(slot) is not { } cold) return false;
        var body = claim.Native;
        if (body is null)
        {
            if (cold.Appearance is not { } look || rigs.For(look) is not { } setup) return false;
            float scale = cold is { Fighter.IsAnimal: true, Medical: { } size } ? size.ScaleK : 1;
            if (DefinitionOf(look, setup.Rig, scale) is not { } definition) return false;
            body = new Ragdoll { Id = id, Setup = setup, Definition = definition, Scale = scale, CarryBone = CarryBoneOf(look, setup.Rig) };
        }
        if (pose.Set.Length != body.Setup.Rig.BoneCount || pose.Position.Length != pose.Set.Length || pose.Rotation.Length != pose.Set.Length
            || !Matrix4x4.Invert(placement, out _)) return false;
        var rig = body.Setup.Rig; var scratch = rig.Rent();
        var bones = new RigidTransform[rig.BoneCount];
        try
        {
            scratch.PoseDerived(body.Setup.Shape, [], pose);
            var turn = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(placement with { M41 = 0, M42 = 0, M43 = 0 }));
            for (int b = 0; b < bones.Length; b++)
            {
                var position = Vector3.Transform(scratch.DerivedPosition[b], placement);
                var rotation = Quaternion.Normalize(turn * scratch.DerivedRotation[b]);
                if (!float.IsFinite(position.LengthSquared()) || !float.IsFinite(rotation.LengthSquared())) return false;
                bones[b] = new(position, rotation);
            }
        }
        finally { rig.Return(scratch); }
        body.Pose = [.. body.Definition.Bodies.Select(actor => actor.BoneInBody.Inverse().Then(bones[actor.Bone]))];
        body.Mask = RagdollPart.None; body.Frozen = false; body.ReturnedBones = bones;
        StartGetUp(world, body, ref world.Characters.Next[slot]);
        var nativePlacement = Placement(body, cold, world.Characters.Next[slot]);
        (body.Published, body.PublishedIn) = (Build(body, nativePlacement), nativePlacement);
        externalClaims.Remove(id); ragdolls.Add(id, body); order.Add(id);
        return true;
    }

    void ValidateClaimWorld(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (world.Characters.Phase == TablePhase.Parallel) throw new InvalidOperationException("Ragdoll handoffs require a serial phase.");
        if (!world.Systems.Contains(this)) throw new ArgumentException("This ragdoll system does not belong to the world.", nameof(world));
        if (claimWorld is not null && !ReferenceEquals(claimWorld, world)) throw new InvalidOperationException("A ragdoll system belongs to one world.");
        claimWorld = world;
    }
    void PruneExternalClaims(World world)
    {
        foreach (var id in externalClaims.Keys.Where(id => !world.Characters.TryResolveNext(id, out _)).ToArray()) externalClaims.Remove(id);
    }
    void HashExternalClaims(ref StateHasher hash)
    {
        if (externalClaims.Count == 0) return;
        hash.Add(0x52616743); hash.Add(externalClaims.Count);
        foreach (var id in externalClaims.Keys.OrderBy(id => Rng.Key(id))) hash.Add(id);
    }
    static BonePose CopyPose(BonePose source)
    {
        var result = new BonePose(source.Set.Length) { Weight = source.Weight, AbsolutePositions = source.AbsolutePositions, Components = source.Components?.ToArray() };
        source.Set.CopyTo(result.Set, 0); source.Position.CopyTo(result.Position, 0); source.Rotation.CopyTo(result.Rotation, 0);
        return result;
    }
}
