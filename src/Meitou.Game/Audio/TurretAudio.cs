using System.Numerics;
using Meitou.Data;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation;
using Meitou.Simulation.Combat;

namespace Meitou.Game.Audio;

/// <summary>
/// The turrets' movement sounds (docs/audio.md "Other world-side events"): a manned turret (<see cref="CharacterSnapshot.Turret"/>) turns toward
/// the simulation's aim point by the exe's rule in <see cref="TurretSounds"/>, which posts <c>Turret_Mvt_Start</c> / <c>Turret_Mvt_Stop</c>.
/// Display only (frame time, its own copy of the parts): nothing here is read by the simulation; the turn that is drawn is the simulation's
/// (<see cref="Host.WorldSync.BuildingSync.UpdateTurretPoses"/>). Driven each frame from <see cref="GameSound.UpdateAudio"/>.
/// </summary>
sealed class TurretAudio(GameHost host)
{
    /// <summary>
    /// Turrets farther than this from the eye are not looked after (the movement sound is dropped beyond <see cref="TurretSounds.Range"/> anyway).
    /// </summary>
    const float TurretAudioRadius = 4000;

    /// <summary>
    /// The audio the turrets' objects were made for; another one makes them again.
    /// </summary>
    AudioSystem? _turretAudioFor;

    /// <summary>
    /// The turrets' sound objects.
    /// </summary>
    TurretSounds? _turretSounds;

    /// <summary>
    /// The world the turrets' objects follow.
    /// </summary>
    World? _turretSoundsWorld;

    /// <summary>
    /// Each turret's mount and its gun's range, by the turret's key (null: its records are missing).
    /// </summary>
    readonly Dictionary<string, (TurretMount Mount, float Range)?> _turretMounts = new(StringComparer.Ordinal);

    /// <summary>
    /// This frame's manned turrets near the eye (reused).
    /// </summary>
    readonly List<TurretAim> _mannedTurrets = [];

    /// <summary>
    /// Once a frame: the manned turrets near the eye turn toward their aim and post their movement sounds.
    /// </summary>
    internal void Update(AudioSystem sound, Vector3 eye, double realSeconds)
    {
        if (!ReferenceEquals(_turretAudioFor, sound))
        {
            _turretAudioFor = sound;
            _turretSounds = new TurretSounds(sound.Player);
            _turretSoundsWorld = null;
        }

        var sounds = _turretSounds!;
        if (!ReferenceEquals(_turretSoundsWorld, host.Session.World))
        {
            sounds.Clear();
            _turretMounts.Clear();
            _turretSoundsWorld = host.Session.World;
        }

        if (host.Scene.Database is not { } db) return;
        _mannedTurrets.Clear();
        var characters = host.Session.CurrentSnapshot.Characters;
        foreach (var gunner in characters)
        {
            if (gunner.Turret is not { } post) continue;
            if (Vector3.DistanceSquared(post.Position, eye) > TurretAudioRadius * TurretAudioRadius) continue;

            if (!_turretMounts.TryGetValue(post.Key, out var made)) _turretMounts[post.Key] = made = MountOf(db, post);
            if (made is not { } known) continue;
            _mannedTurrets.Add(new TurretAim(post.Key, known.Mount, TargetOf(gunner, post.Position, known.Range, characters)));
        }

        // The turning is by game time: the frame time times the game speed (the exe's frame time is scaled by it; 0 while paused).
        sounds.Update(_mannedTurrets, (float)(realSeconds * host.Session.Simulation.Speed), eye);
    }

    /// <summary>
    /// The main menu: the turrets' objects end.
    /// </summary>
    internal void Reset()
    {
        _turretSounds?.Clear();
        _turretMounts.Clear();
        _turretSoundsWorld = null;
    }

    /// <summary>
    /// The point the gunner aims at: the simulation's aim (<see cref="MannedTurret.Aim"/>, docs/game/combat.md "Turrets"); without one (a world
    /// without the combat system's turrets) the nearest living, free hostile character within the gun's range, 5 units above its place
    /// (<b>engine choice</b>). Null: none (the turret keeps its last aim).
    /// </summary>
    Vector3? TargetOf(CharacterSnapshot gunner, Vector3 turret, float range, IReadOnlyList<CharacterSnapshot> characters)
    {
        if (gunner.Turret is { Simulated: true } simulated) return simulated.Aim;
        if (host.PopData is not { } data || !data.Relations.Contains(gunner.Faction)) return null;

        CharacterSnapshot? nearest = null;
        float best = range * range;
        foreach (var c in characters)
        {
            if (c.Id == gunner.Id || !data.Relations.Contains(c.Faction)) continue;
            if (!data.Relations.IsHostile(gunner.Faction, c.Faction)) continue;

            if (c.Body is { Dead: true } or { Unconscious: true } || !c.CarriedBy.IsNone) continue;
            float d2 = Vector3.DistanceSquared(c.Position, turret);
            if (d2 > best) continue;
            best = d2;
            nearest = c;
        }

        return nearest is null ? null : nearest.Position + new Vector3(0, 5, 0);
    }

    /// <summary>
    /// The mount of a turret from its records (<see cref="TurretGun"/>): the building's <c>scale</c>, its rotating parts, the turning speed of an
    /// unharmed powered turret (<see cref="TurretFormulas.TurnRate"/> with efficiency 1) and the gun's <c>range</c>.
    /// </summary>
    static (TurretMount, float)? MountOf(GameDatabase db, MannedTurret post)
    {
        if (db.Find(post.BuildingId) is not { } building || TurretGun.From(db, building) is not { } gun) return null;
        float turnRate = TurretFormulas.TurnRate(1, gun.Gun.AimSpeed, gun.Damage);
        var mount = new TurretMount(post.Position, post.Yaw, gun.Scale, turnRate, gun.Parts);
        return (mount, gun.Gun.Range);
    }
}
