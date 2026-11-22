using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Particles;
using Meitou.Game.Rendering.Particles;
using Meitou.Modding;
using Meitou.Simulation;

namespace Meitou.Game.Effects;

/// <summary>
/// Plays the code mods' effects (docs/modding.md "Visual effects") with the renderer's particle bursts, the path the blood and dust take: a
/// point's at the point, a character's at its bone as last drawn, moved there each frame while it emits. Display only; on the host's thread.
/// </summary>
sealed class ModEffectPlayer(GameHost host) : IModEffectSink
{
    /// <summary>
    /// The bone an attached effect follows when the mod names none.
    /// </summary>
    const string DefaultBone = "Bip01 Spine2";
    /// <summary>
    /// Once a frame: ages, moves and starts the mods' effects by the particles' frame clock (<see cref="ParticleRenderer.LastStep"/>), so pause
    /// and game speed match the bursts. This runs before the frame's particle update, so the step is the previous frame's.
    /// </summary>
    public void Advance(ModEffects effects) => effects.Advance(host.Gpu?.Particles?.LastStep ?? 0, this);

    /// <summary>
    /// An effect id to its particle system: an EFFECT record's <c>particle system</c>, else the id as a system name; null when the particles (once
    /// loaded) have no such system.
    /// </summary>
    public string? Resolve(string id)
    {
        string system = host.Scene.Database?.Find(id) is { Type: FcsRecordType.Effect } record ? EffectRecord.From(record).ParticleSystem : id;
        if (system.Length == 0) return null;
        if (host.Gpu?.Particles is { } particles && particles.Library.FindSystem(system) is null) return null;
        return system;
    }

    /// <summary>
    /// Fires the request's system as a burst at its point or its character's bone; null when the character is not drawn or there are no
    /// particles.
    /// </summary>
    public object? Start(in ModEffectRequest request)
    {
        if (host.Gpu?.Particles is not { } particles || Where(request) is not { } at) return null;
        return particles.Burst(request.System, at, Quaternion.Identity, request.Seconds, recolour: request.Tint);
    }

    /// <summary>
    /// Moves an attached burst's emitter to its character's bone, where it was drawn this frame (left in place while it is not).
    /// </summary>
    public void Follow(object playing, in ModEffectRequest request) => Track(playing, request);

    /// <summary>
    /// Moves the burst as <see cref="Follow"/> does; false when its character or blade is not drawn this frame or it stopped emitting.
    /// </summary>
    public bool Track(object playing, in ModEffectRequest request)
    {
        if (host.Gpu?.Particles is not { } particles || playing is not EffectUnit { Stopped: false } unit || Where(request) is not { } at)
            return false;

        particles.MoveBurst(unit, at, Quaternion.Identity);
        return true;
    }

    /// <summary>
    /// Makes the burst emit for <paramref name="seconds"/> from now (0: it stops at the next particle update, its particles live out).
    /// </summary>
    public void Renew(object playing, float seconds)
    {
        if (playing is EffectUnit unit) unit.Life = seconds;
    }

    /// <summary>
    /// Where a request plays now: its point, its character's held blade or bone as last drawn (null: not drawn, no blade drawn, or no such bone).
    /// </summary>
    Vector3? Where(in ModEffectRequest request)
    {
        if (!request.Attached) return request.At;
        if (host.Gpu?.Characters is not { } crowd) return null;
        if (request.OnBlade) return crowd.BladePoint(host.DrawKey(request.On), request.Along);
        return crowd.BonePosition(host.DrawKey(request.On), request.Bone ?? DefaultBone);
    }
}
