using System.Numerics;
using Meitou.Data;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.Particles;
using Meitou.Data.World;
using Meitou.Simulation.Production;

namespace Meitou.Game.Audio;

/// <summary>
/// The world's own sounds (docs/audio.md "Interiors" and "Point emitters"): the building under the camera's pivot (the interior ambience and the
/// reverb switch) and the sound emitters of buildings (campfires, torches, ovens, water tanks, …) that play while the camera eye is near. Driven
/// each frame from <see cref="GameSound.UpdateAudio"/>.
/// </summary>
sealed class WorldAudio(GameHost host)
{
    #region Interiors and doors

    /// <summary>
    /// The audio the sound objects were made for; another one makes them again.
    /// </summary>
    AudioSystem? _worldAudioFor;

    /// <summary>
    /// The interior ambience and reverb of the building under the pivot.
    /// </summary>
    InteriorSounds? _interiorSounds;

    /// <summary>
    /// The point emitters of the buildings, the effects and the corpse flies.
    /// </summary>
    internal WorldEmitters? Emitters;

    /// <summary>
    /// The doors' sounds.
    /// </summary>
    DoorSounds? _doorSounds;

    /// <summary>
    /// The world the door sounds follow.
    /// </summary>
    object? _doorSoundsFor;

    /// <summary>
    /// Each door record's `door type` (DoorMaterial).
    /// </summary>
    readonly Dictionary<string, int> _doorTypes = new(StringComparer.Ordinal);

    /// <summary>
    /// Once a frame, after the ambience: <paramref name="pivot"/> is the camera's target (the game tests the building under it),
    /// <paramref name="eye"/> the camera's position (the emitters' distance); both are the selected character's ears under the Audio settings' Hear
    /// from switch.
    /// </summary>
    internal void Update(AudioSystem sound, Vector3 pivot, Vector3 eye)
    {
        if (!ReferenceEquals(_worldAudioFor, sound))
        {
            _worldAudioFor = sound;
            _interiorSounds = new InteriorSounds(sound.Player);
            Emitters = new WorldEmitters(sound.Player);
            _doorSounds = new DoorSounds(sound.Player);
            _doorSoundsFor = null;
            ForgetEmitters();
        }

        // The building whose indoor volume lies under the pivot, whatever the pivot's height (a ray cast straight down from far above).
        var volume = host.ActiveObjects?.InteriorUnder(pivot.X, pivot.Z);
        InteriorHit? hit = volume is null ? null : new InteriorHit(volume.PlacementId, volume.Ambience, volume.Integrity, volume.Cavernous);
        _interiorSounds!.Update(hit);

        UpdateEmitters();
        UpdateEffectEmitters(Emitters!);
        host.CorpseFlies.UpdateCorpseFlyEmitters(Emitters!);
        Emitters!.Update(eye);
        _doorSounds!.FixBreak = host.Config.FixDoorBreak;
        UpdateDoorSounds(eye);
    }

    /// <summary>
    /// The main menu (Meitou's choice, see <see cref="GameSound.AudioMainMenu"/>): the interior ambience ends and the emitters stop.
    /// </summary>
    internal void Reset()
    {
        _interiorSounds?.Silence();
        Emitters?.Clear();
        _doorSounds?.Clear();
        _doorSoundsFor = null;
        ForgetEmitters();
    }

    /// <summary>
    /// One statistics line: where the pivot is and how many emitters play; empty without audio.
    /// </summary>
    internal string Stats()
    {
        if (_interiorSounds is null || Emitters is null) return "";

        string place = _interiorSounds.Playing ? $"indoors ({_interiorSounds.Indoors})"
            : _interiorSounds.Inside ? "in a building without an ambience"
            : "outdoors";
        string emitters = $"{place}, {Emitters.Started} of {Emitters.Count} emitters playing";
        string nearest = Emitters.NearestName is { } near ? $", nearest {near} {Emitters.NearestDistance:0} units from the eye" : "";
        string doors = _doorSounds is { Posted: > 0 } door ? $", {door.Posted} door sounds" : "";
        return emitters + nearest + doors;
    }

    /// <summary>
    /// The doors' open, close, shut and break sounds from the snapshot's doors (docs/audio.md "Other world-side events").
    /// </summary>
    void UpdateDoorSounds(Vector3 eye)
    {
        var sounds = _doorSounds!;
        if (!ReferenceEquals(_doorSoundsFor, host.Session.World))
        {
            sounds.Clear();
            _doorTypes.Clear();
            _doorSoundsFor = host.Session.World;
        }

        var db = host.Scene.Database;
        sounds.Begin();
        foreach (var (key, door) in host.Session.CurrentSnapshot.Doors)
            sounds.Update(key, new DoorSoundState(door.Position, TypeOf(door.Record), (int)door.Motion, door.Broken), eye);
        sounds.End(eye);

        int TypeOf(string record)
        {
            if (!_doorTypes.TryGetValue(record, out int type))
                _doorTypes[record] = type = db?.Find(record)?.GetInt("door type") ?? 0;
            return type;
        }
    }

    #endregion

    #region Building emitters

    /// <summary>
    /// The world the emitters were made for.
    /// </summary>
    object? _emittersFor;

    /// <summary>
    /// The level's emitters (read on a worker: every building with sounds, positioned on the heightmap).
    /// </summary>
    Task<List<PlacedEmitter>>? _levelEmitters;

    /// <summary>
    /// Whether the level's emitters were added.
    /// </summary>
    bool _levelEmittersAdded;

    /// <summary>
    /// The player-built buildings the emitters follow: what each was when its emitter was decided.
    /// </summary>
    readonly Dictionary<string, (string Record, bool Complete, Vector3 Position)> _runtimeEmitters = new(StringComparer.Ordinal);

    /// <summary>
    /// The player-built buildings seen this frame.
    /// </summary>
    readonly HashSet<string> _runtimeSeen = new(StringComparer.Ordinal);

    /// <summary>
    /// The level's building records as machines (null for one that is not), for whether their emitters play.
    /// </summary>
    readonly Dictionary<string, MachineData?> _levelMachines = new(StringComparer.Ordinal);

    /// <summary>
    /// The level's wind generators' emitters, told their blade's wind share each frame.
    /// </summary>
    readonly List<(PlacedEmitter Emitter, (float Min, float Max) Rotor)> _levelWind = [];

    /// <summary>
    /// Keeps the emitters in step with the world's buildings: the level's once read, the player's as their sites change.
    /// </summary>
    void UpdateEmitters()
    {
        var emitters = Emitters!;
        if (!ReferenceEquals(_emittersFor, host.Session.World))
        {
            emitters.Clear();
            ForgetEmitters();
            _emittersFor = host.Session.World;
            _levelEmitters = host.Scene.Objects is { } objects ? Task.Run(objects.Emitters) : null;
        }

        if (!_levelEmittersAdded && _levelEmitters is { IsCompleted: true } read) AddLevelEmitters(emitters, read);
        if (host.Scene.Database is not { } db) return;

        UpdateRuntimeEmitters(emitters, db);

        // The machines of the player's buildings tell their emitters what they do (the exe's per-frame call, docs/audio.md "Point emitters");
        // one without an emitter is ignored.
        foreach (var machine in host.Session.CurrentSnapshot.Machines)
        {
            if (!machine.IsContainer) emitters.SetPower(machine.Site, machine.Sound.Efficiency, machine.Sound.Powered);
        }

        foreach (var (emitter, rotor) in _levelWind)
            emitters.SetPower(emitter.Key, LevelWindShare(emitter.Position, rotor), true);
    }

    /// <summary>
    /// The level's emitters once read: each added with whether it plays, and the outcome logged.
    /// </summary>
    void AddLevelEmitters(WorldEmitters emitters, Task<List<PlacedEmitter>> read)
    {
        _levelEmittersAdded = true;
        if (read.IsCompletedSuccessfully)
        {
            foreach (var emitter in read.Result)
            {
                bool powered = LevelPowered(emitter, out float efficiency);
                emitters.Add(emitter, powered, efficiency);
            }

            Console.WriteLine($"audio     {read.Result.Count} sound emitters in the level's buildings");
        }
        else
        {
            Console.WriteLine($"audio     no building emitters: {read.Exception?.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// The player-built buildings' emitters: made again when a site changes, dropped when it is gone.
    /// </summary>
    void UpdateRuntimeEmitters(WorldEmitters emitters, GameDatabase db)
    {
        _runtimeSeen.Clear();
        foreach (var site in host.Session.CurrentSnapshot.Buildings)
        {
            _runtimeSeen.Add(site.Id);
            var now = (site.Record, site.Complete, site.Position);
            if (_runtimeEmitters.TryGetValue(site.Id, out var before) && before == now) continue;
            _runtimeEmitters[site.Id] = now;
            emitters.Remove(site.Id);
            // A player-built building gets its emitter when it is complete (the game's completion step starts it).
            if (!site.Complete || db.Find(site.Record) is not { } record) continue;
            if (BuildingEmitters.For(db, record, site.Id, site.Position, site.Rotation) is { } emitter) emitters.Add(emitter);
        }

        if (_runtimeEmitters.Count <= _runtimeSeen.Count) return;

        foreach (string id in _runtimeEmitters.Keys.Where(id => !_runtimeSeen.Contains(id)).ToArray())
        {
            _runtimeEmitters.Remove(id);
            emitters.Remove(id);
        }
    }

    /// <summary>
    /// Whether a level building's emitter plays although the simulation does not run the building (docs/audio.md "Point emitters", Meitou's choice):
    /// one that needs power and whose class powers it once when built, a generator, a battery and a furnace-like machine that needs no operator
    /// (their efficiency is the data's); not the machines that need operators or town power.
    /// </summary>
    bool LevelPowered(PlacedEmitter emitter, out float efficiency)
    {
        efficiency = 0;
        if (!emitter.Sound.NeedsPower || host.Scene.Database is not { } db) return false;
        if (!_levelMachines.TryGetValue(emitter.Building, out var data))
        {
            var record = db.Find(emitter.Building);
            _levelMachines[emitter.Building] = data = record is null ? null : MachineData.From(db, record);
        }

        if (data is null || !data.PowersEmitterEachFrame) return true;
        if (data.IsWind)
        {
            if (db.Find(emitter.Building) is { } record && WorldObjectLayout.WindRotor(db, record, emitter.Origin) is { } rotor)
            {
                _levelWind.Add((emitter, rotor));
                efficiency = LevelWindShare(emitter.Position, rotor);
            }
        }
        else if (data.IsGenerator || data.IsBattery || (data.PowerOperators == 0 && data.PowerOutput == 0)) efficiency = data.OutputRate;
        else return false;
        return true;
    }

    /// <summary>
    /// A level wind generator's share: its blade's <see cref="RotorSpec.WindShare"/> of the scheduled wind at the place (0 without weather), as the
    /// simulation's own generators.
    /// </summary>
    float LevelWindShare(Vector3 at, (float Min, float Max) rotor) =>
        host.Gpu.Weather?.World is { } weather ? RotorSpec.WindShare(weather.WindAt(at.X, at.Z).Speed, rotor.Min, rotor.Max) : 0;

    /// <summary>
    /// Forgets the emitters' world, the level's emitters and the player's buildings, so they are made again.
    /// </summary>
    void ForgetEmitters()
    {
        _levelMachines.Clear();
        _levelWind.Clear();
        _emittersFor = null;
        _levelEmitters = null;
        _levelEmittersAdded = false;
        _runtimeEmitters.Clear();
    }

    #endregion

    #region Effect emitters

    // The sound emitters of effects (docs/audio.md "Point emitters"): every live unit of an effect whose EFFECT record has a <c>sound</c> (the sky
    // beams) is a <see cref="WorldEmitters"/> emitter that follows it and ends with it.

    /// <summary>
    /// The emitter key of each effect unit that has one.
    /// </summary>
    readonly Dictionary<EffectUnit, string> _effectEmitterKeys = [];

    /// <summary>
    /// The effect units seen this frame.
    /// </summary>
    readonly HashSet<EffectUnit> _effectUnitsSeen = [];

    /// <summary>
    /// The effect units whose emitters end this frame (reused).
    /// </summary>
    readonly List<EffectUnit> _effectUnitsGone = [];

    /// <summary>
    /// The emitters' owner the keys are for (a new one forgets them).
    /// </summary>
    WorldEmitters? _effectEmittersFor;

    /// <summary>
    /// The last effect emitter key's number.
    /// </summary>
    int _effectEmitterNumber;

    /// <summary>
    /// A unit gets its emitter when it is first seen (the exe makes it when the handler first updates) at the unit's place, which it follows every
    /// frame (<c>EffectHandler::vfunc_2</c>); it loses it when the unit is done, that is, stopped with no particles left (the exe ends it with the
    /// handler's particle system).
    /// </summary>
    void UpdateEffectEmitters(WorldEmitters emitters)
    {
        if (!ReferenceEquals(_effectEmittersFor, emitters))
        {
            _effectEmitterKeys.Clear();
            _effectEmittersFor = emitters;
        }

        _effectUnitsSeen.Clear();
        if (host.Gpu is not null && host.Gpu.Particles is { } particles)
            foreach (var group in particles.Groups.Concat(particles.PlacerGroups))
            {
                if (group.Effect.Sound is not { } sound) continue;
                foreach (var unit in group.Units) FollowEffectUnit(emitters, unit, sound);
            }

        if (_effectEmitterKeys.Count == _effectUnitsSeen.Count) return;
        _effectUnitsGone.Clear();
        foreach (var unit in _effectEmitterKeys.Keys)
            if (!_effectUnitsSeen.Contains(unit)) _effectUnitsGone.Add(unit);
        foreach (var unit in _effectUnitsGone)
        {
            emitters.Remove(_effectEmitterKeys[unit]);
            _effectEmitterKeys.Remove(unit);
        }
    }

    /// <summary>
    /// A live unit is seen and its emitter follows it (made when first seen).
    /// </summary>
    void FollowEffectUnit(WorldEmitters emitters, EffectUnit unit, AmbientSound sound)
    {
        if (unit.Done) return;

        _effectUnitsSeen.Add(unit);
        if (!_effectEmitterKeys.TryGetValue(unit, out string? key))
            _effectEmitterKeys[unit] = key = $"effect/{++_effectEmitterNumber}";
        // Missing from the emitters after the main menu cleared them: back at the unit's place.
        if (!emitters.Contains(key))
            emitters.Add(new PlacedEmitter(key, sound, unit.Position));
        else
            emitters.Move(key, unit.Position);
    }

    #endregion
}
