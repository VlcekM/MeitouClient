using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Audio;
using Meitou.Simulation;

namespace Meitou.Game.Effects;

/// <summary>
/// Corpse flies (docs/game/character-stats.md "Corpses"; <c>Character::periodicUpdate</c>, <b>Verified</b>): a corpse the simulation gave flies shows
/// the EFFECT <c>55393-Newwworld.mod</c> "Fly-Particle" and plays the AMBIENT_SOUND <c>95425-sfx.mod</c> "Corpse Flies" at its place, both following
/// it, until it loses them.
/// </summary>
sealed class CorpseFlies(GameHost host)
{
    /// <summary>
    /// The EFFECT record of the flies ("Fly-Particle") and the AMBIENT_SOUND record of their sound ("Corpse Flies").
    /// </summary>
    const string FlyEffectId = "55393-Newwworld.mod", FlySoundId = "95425-sfx.mod";
    /// <summary>
    /// A carried corpse's flies are placed again once it moved this far (units).
    /// </summary>
    const float FlyMove = 5;

    /// <summary>
    /// The corpses with flies shown, by character, and where their effect stands (its key is <see cref="FlyKey"/>).
    /// </summary>
    readonly Dictionary<CharacterId, Vector3> _flyCorpses = [];
    /// <summary>
    /// This frame's corpses with flies.
    /// </summary>
    readonly HashSet<CharacterId> _flyCorpsesSeen = [];
    /// <summary>
    /// The corpses that lost their flies this frame, kept to avoid allocating.
    /// </summary>
    readonly List<CharacterId> _flyCorpsesGone = [];
    /// <summary>
    /// The database the records were resolved for.
    /// </summary>
    object? _flyRecordsFor;
    /// <summary>
    /// The flies' EFFECT record; null when the data has none.
    /// </summary>
    Data.GameRecord? _flyEffect;
    /// <summary>
    /// The flies' sound; null when the data has no such record.
    /// </summary>
    AmbientSound? _flySound;

    /// <summary>
    /// The key of a corpse's effect and emitter.
    /// </summary>
    static string FlyKey(CharacterId id) => $"flies/{id}";

    /// <summary>
    /// Once a frame with the buildings: the effect at each corpse with flies, gone with them.
    /// </summary>
    internal void UpdateCorpseFlies()
    {
        if (!ReferenceEquals(_flyRecordsFor, host.Scene.Database))
        {
            _flyRecordsFor = host.Scene.Database;
            _flyEffect = host.Scene.Database?.Find(FlyEffectId);
            _flySound = host.Scene.Database?.Find(FlySoundId) is { } sound ? AmbientSound.From(sound) : null;
        }
        var particles = host.Gpu?.Particles;
        _flyCorpsesSeen.Clear();
        foreach (var c in host.Session.CurrentSnapshot.Characters)
        {
            if (!c.HasFlies) continue;
            _flyCorpsesSeen.Add(c.Id);
            if (_flyCorpses.TryGetValue(c.Id, out var at) && Vector3.DistanceSquared(at, c.Position) < FlyMove * FlyMove) continue;
            _flyCorpses[c.Id] = c.Position;
            if (particles is not null && _flyEffect is { } effect)
                particles.SetBuildingEffects(FlyKey(c.Id), [new PlacedEffect(effect, effect, effect, FlyKey(c.Id), c.Position, Quaternion.Identity)]);
        }
        if (_flyCorpses.Count == _flyCorpsesSeen.Count) return;
        _flyCorpsesGone.Clear();
        foreach (var id in _flyCorpses.Keys)
            if (!_flyCorpsesSeen.Contains(id)) _flyCorpsesGone.Add(id);
        foreach (var id in _flyCorpsesGone)
        {
            _flyCorpses.Remove(id);
            particles?.RemoveBuildingEffects(FlyKey(id));
            host.Sound.World.Emitters?.Remove(FlyKey(id));
        }
    }

    /// <summary>
    /// With the world audio: the Corpse Flies emitter at each corpse that has its effect.
    /// </summary>
    internal void UpdateCorpseFlyEmitters(WorldEmitters emitters)
    {
        if (_flySound is not { } sound) return;
        foreach (var (id, at) in _flyCorpses)
        {
            string key = FlyKey(id);
            if (!emitters.Contains(key)) emitters.Add(new PlacedEmitter(key, sound, at));
            else emitters.Move(key, at);
        }
    }
}