using Meitou.Game.Audio;
using Meitou.Data.Particles;
using Meitou.Data.World;

namespace Meitou.Game;

/// <summary>
/// The sound emitters of effects (docs/audio.md "Point emitters"): every live unit of an effect whose EFFECT record has a <c>sound</c> (the sky beams) is a
/// <see cref="WorldEmitters"/> emitter that follows it and ends with it. Driven each frame from <see cref="UpdateWorldAudio"/>.
/// </summary>
sealed partial class GameHost
{
    // The emitter key of each effect unit that has one, the units seen this frame and the emitters' owner (a new one forgets them).
    readonly Dictionary<EffectUnit, string> effectEmitterKeys = [];
    readonly HashSet<EffectUnit> effectUnitsSeen = [];
    readonly List<EffectUnit> effectUnitsGone = [];
    WorldEmitters? effectEmittersFor;
    int effectEmitterNumber;

    /// <summary>
    /// A unit gets its emitter when it is first seen (the exe makes it when the handler first updates) at the unit's place, which it follows every frame
    /// (<c>EffectHandler::vfunc_2</c>); it loses it when the unit is done, that is, stopped with no particles left (the exe ends it with the handler's particle system).
    /// </summary>
    void UpdateEffectEmitters(WorldEmitters emitters)
    {
        if (!ReferenceEquals(effectEmittersFor, emitters))
        {
            effectEmitterKeys.Clear();
            effectEmittersFor = emitters;
        }
        effectUnitsSeen.Clear();
        if (gpu is not null && gpu.Particles is { } particles)
            foreach (var group in particles.Groups.Concat(particles.PlacerGroups))
            {
                if (group.Effect.Sound is not { } sound) continue;
                foreach (var unit in group.Units)
                {
                    if (unit.Done) continue;
                    effectUnitsSeen.Add(unit);
                    if (!effectEmitterKeys.TryGetValue(unit, out string? key))
                        effectEmitterKeys[unit] = key = $"effect/{++effectEmitterNumber}";
                    // Missing from the emitters after the main menu cleared them: back at the unit's place.
                    if (!emitters.Contains(key)) emitters.Add(new PlacedEmitter(key, sound, unit.Position));
                    else emitters.Move(key, unit.Position);
                }
            }
        if (effectEmitterKeys.Count == effectUnitsSeen.Count) return;
        effectUnitsGone.Clear();
        foreach (var unit in effectEmitterKeys.Keys)
            if (!effectUnitsSeen.Contains(unit)) effectUnitsGone.Add(unit);
        foreach (var unit in effectUnitsGone)
        {
            emitters.Remove(effectEmitterKeys[unit]);
            effectEmitterKeys.Remove(unit);
        }
    }
}
