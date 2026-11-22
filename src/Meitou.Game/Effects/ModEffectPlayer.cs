using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Particles;
using Meitou.Game.Rendering.Particles;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Construction;

namespace Meitou.Game.Effects;

/// <summary>
/// Plays the code mods' effects (docs/modding.md "Visual effects") with the renderer's particle bursts, the path the blood and dust take: at their
/// anchor's frame (<see cref="EffectFrames"/>), moved and turned there each frame while they emit. Locates bones and weapons as the character
/// renderer last drew them and buildings from the world's snapshot. Display only; on the host's thread.
/// </summary>
sealed class ModEffectPlayer(GameHost host) : IModEffectSink, IEffectFrameSource
{
    /// <summary>
    /// The snapshot's buildings by id.
    /// </summary>
    readonly Dictionary<string, BuildingSnapshot> _buildings = new(StringComparer.Ordinal);
    /// <summary>
    /// The world and the revisions of its buildings and nest buildings that <see cref="_buildings"/> was gathered at.
    /// </summary>
    (World? World, long Built, long Nests) _buildingsAt = (null, -1, -1);

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
    /// Whether <paramref name="id"/> names a particle system the particles have now (false before they load or while they are off).
    /// </summary>
    public bool Exists(string id) => host.Gpu?.Particles is { Enabled: true } && Resolve(id) is not null;

    /// <summary>
    /// Fires the request's system as a burst at its anchor's frame, its particles kept in that frame on a bone or weapon (they move and turn with
    /// it); null when the anchor is not there or there are no particles.
    /// </summary>
    public object? Start(in ModEffectRequest request)
    {
        if (host.Gpu?.Particles is not { } particles || EffectFrames.Locate(request.Anchor, this) is not { } frame) return null;
        bool keepLocal = request.Anchor.Kind is EffectAnchorKind.Bone or EffectAnchorKind.Weapon;
        return particles.Burst(request.System, frame.At, frame.Turn, request.Seconds, recolour: request.Tint, stretch: frame.Stretch,
            keepLocal: keepLocal);
    }

    /// <summary>
    /// Moves and turns a burst to its anchor's frame this frame; false when the anchor is not there or the burst stopped emitting.
    /// </summary>
    public bool Follow(object playing, in ModEffectRequest request)
    {
        if (host.Gpu?.Particles is not { } particles || playing is not EffectUnit { Stopped: false } unit) return false;
        if (EffectFrames.Locate(request.Anchor, this) is not { } frame) return false;

        particles.MoveBurst(unit, frame.At, frame.Turn);
        unit.Stretch = frame.Stretch;
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
    /// The character's bone as last drawn (<see cref="Rendering.Characters.CharacterRenderer.BoneFrame"/>).
    /// </summary>
    public EffectFrame? Bone(CharacterId on, string bone, Vector3 offset, Quaternion turn) =>
        host.Gpu?.Characters?.BoneFrame(host.DrawKey(on), bone, offset, turn) is var (at, rotation) ? new EffectFrame(at, rotation, 1) : null;

    /// <summary>
    /// The character's held weapon as last drawn (<see cref="Rendering.Characters.CharacterRenderer.HeldBladeFrame"/>).
    /// </summary>
    public EffectFrame? Weapon(CharacterId on) =>
        host.Gpu?.Characters?.HeldBladeFrame(host.DrawKey(on)) is { } blade ? new EffectFrame(blade.Base, blade.Turn, blade.Stretch) : null;

    /// <summary>
    /// A player-built or nest building of the world's snapshot by id.
    /// </summary>
    public EffectFrame? Building(string id)
    {
        if (host.Session?.CurrentSnapshot is not { } snapshot) return null;

        var at = (World: host.Session.World, Built: snapshot.BuildingsRevision, Nests: snapshot.NestBuildingsRevision);
        if (!ReferenceEquals(at.World, _buildingsAt.World) || at.Built != _buildingsAt.Built || at.Nests != _buildingsAt.Nests) Gather(snapshot, at);
        return _buildings.TryGetValue(id, out var b) ? new EffectFrame(b.Position, b.Rotation, 1) : null;
    }

    /// <summary>
    /// Gathers the snapshot's buildings by id, as of <paramref name="at"/>.
    /// </summary>
    void Gather(WorldSnapshot snapshot, (World World, long Built, long Nests) at)
    {
        _buildings.Clear();
        foreach (var b in snapshot.Buildings.Concat(snapshot.NestBuildings)) _buildings[b.Id] = b;
        _buildingsAt = at;
    }
}
