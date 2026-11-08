using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Audio;
using Meitou.Simulation;

namespace Meitou.Game.Effects;

/// <summary>
/// Corpse flies (docs/game/character-stats.md "Corpses"; <c>Character::periodicUpdate</c>, <b>Verified</b>): a corpse the simulation gave flies shows the EFFECT
/// <c>55393-Newwworld.mod</c> "Fly-Particle" and plays the AMBIENT_SOUND <c>95425-sfx.mod</c> "Corpse Flies" at its place, both following it, until it loses them.
/// </summary>
sealed class CorpseFlies(GameHost host)
{
    const string FlyEffectId = "55393-Newwworld.mod", FlySoundId = "95425-sfx.mod";
    /// <summary>A carried corpse's flies are placed again once it moved this far (units).</summary>
    const float FlyMove = 5;

    // The corpses with flies shown (their effect key and where it stands), the records (resolved once per database) and this frame's set.
    readonly Dictionary<CharacterId, Vector3> flyCorpses = [];
    readonly HashSet<CharacterId> flyCorpsesSeen = [];
    readonly List<CharacterId> flyCorpsesGone = [];
    object? flyRecordsFor;
    Meitou.Data.GameRecord? flyEffect;
    AmbientSound? flySound;

    static string FlyKey(CharacterId id) => $"flies/{id}";

    /// <summary>Once a frame with the buildings: the effect at each corpse with flies, gone with them.</summary>
    internal void UpdateCorpseFlies()
    {
        if (!ReferenceEquals(flyRecordsFor, host.scene.Database))
        {
            flyRecordsFor = host.scene.Database;
            flyEffect = host.scene.Database?.Find(FlyEffectId);
            flySound = host.scene.Database?.Find(FlySoundId) is { } sound ? AmbientSound.From(sound) : null;
        }
        var particles = host.gpu?.Particles;
        flyCorpsesSeen.Clear();
        foreach (var c in host.session.CurrentSnapshot.Characters)
        {
            if (!c.HasFlies) continue;
            flyCorpsesSeen.Add(c.Id);
            if (flyCorpses.TryGetValue(c.Id, out var at) && Vector3.DistanceSquared(at, c.Position) < FlyMove * FlyMove) continue;
            flyCorpses[c.Id] = c.Position;
            if (particles is not null && flyEffect is { } effect)
                particles.SetBuildingEffects(FlyKey(c.Id), [new PlacedEffect(effect, effect, effect, FlyKey(c.Id), c.Position, Quaternion.Identity)]);
        }
        if (flyCorpses.Count == flyCorpsesSeen.Count) return;
        flyCorpsesGone.Clear();
        foreach (var id in flyCorpses.Keys)
            if (!flyCorpsesSeen.Contains(id)) flyCorpsesGone.Add(id);
        foreach (var id in flyCorpsesGone)
        {
            flyCorpses.Remove(id);
            particles?.RemoveBuildingEffects(FlyKey(id));
            host.sound.worldEmitters?.Remove(FlyKey(id));
        }
    }

    /// <summary>With the world audio: the Corpse Flies emitter at each corpse that has its effect.</summary>
    internal void UpdateCorpseFlyEmitters(WorldEmitters emitters)
    {
        if (flySound is not { } sound) return;
        foreach (var (id, at) in flyCorpses)
        {
            string key = FlyKey(id);
            if (!emitters.Contains(key)) emitters.Add(new PlacedEmitter(key, sound, at));
            else emitters.Move(key, at);
        }
    }
}
