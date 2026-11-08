using System.Numerics;
using Meitou.Game.Audio;
using Meitou.Data;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Simulation.Production;

namespace Meitou.Game;

/// <summary>
/// The world's own sounds (docs/audio.md "Interiors" and "Point emitters"): the building under the camera's pivot (the interior ambience and the reverb switch) and the sound
/// emitters of buildings (campfires, torches, ovens, water tanks, …) that play while the camera eye is near. Driven each frame from <see cref="UpdateAudio"/>.
/// </summary>
sealed partial class GameHost
{
    AudioSystem? worldAudioFor;
    InteriorSounds? interiorSounds;
    WorldEmitters? worldEmitters;
    DoorSounds? doorSounds;
    // The world the door sounds follow, and each door record's `door type` (DoorMaterial).
    object? doorSoundsFor;
    readonly Dictionary<string, int> doorTypes = new(StringComparer.Ordinal);
    // The world the emitters were made for, the level's emitters (read on a worker: every building with sounds, positioned on the heightmap) and whether they were added.
    object? emittersFor;
    Task<List<PlacedEmitter>>? levelEmitters;
    bool levelEmittersAdded;
    // The player-built buildings the emitters follow: what each was when its emitter was decided.
    readonly Dictionary<string, (string Record, bool Complete, Vector3 Position)> runtimeEmitters = new(StringComparer.Ordinal);
    readonly HashSet<string> runtimeSeen = new(StringComparer.Ordinal);
    // The level's building records as machines (null for one that is not), for whether their emitters play.
    readonly Dictionary<string, MachineData?> levelMachines = new(StringComparer.Ordinal);
    // The level's wind generators' emitters, told their blade's wind share each frame.
    readonly List<(PlacedEmitter Emitter, (float Min, float Max) Rotor)> levelWind = [];

    /// <summary>Once a frame, after the ambience: <paramref name="pivot"/> is the camera's target (the game tests the building under it), <paramref name="eye"/> the camera's position (the emitters' distance);
    /// both are the selected character's ears under the Audio settings' Hear from switch.</summary>
    void UpdateWorldAudio(AudioSystem sound, Vector3 pivot, Vector3 eye)
    {
        if (!ReferenceEquals(worldAudioFor, sound))
        {
            worldAudioFor = sound;
            interiorSounds = new InteriorSounds(sound.Player);
            worldEmitters = new WorldEmitters(sound.Player);
            doorSounds = new DoorSounds(sound.Player);
            doorSoundsFor = null;
            ForgetEmitters();
        }

        // The building whose indoor volume lies under the pivot, whatever the pivot's height (a ray cast straight down from far above).
        var volume = ActiveObjects?.InteriorUnder(pivot.X, pivot.Z);
        interiorSounds!.Update(volume is null ? null : new InteriorHit(volume.PlacementId, volume.Ambience, volume.Integrity, volume.Cavernous));

        UpdateEmitters();
        UpdateEffectEmitters(worldEmitters!);
        UpdateCorpseFlyEmitters(worldEmitters!);
        worldEmitters!.Update(eye);
        doorSounds!.FixBreak = config.FixDoorBreak;
        UpdateDoorSounds(eye);
    }

    /// <summary>The doors' open, close, shut and break sounds from the snapshot's doors (docs/audio.md "Other world-side events").</summary>
    void UpdateDoorSounds(Vector3 eye)
    {
        var sounds = doorSounds!;
        if (!ReferenceEquals(doorSoundsFor, session.World))
        {
            sounds.Clear();
            doorTypes.Clear();
            doorSoundsFor = session.World;
        }
        var db = scene.Database;
        sounds.Begin();
        foreach (var (key, door) in session.CurrentSnapshot.Doors)
            sounds.Update(key, new DoorSoundState(door.Position, TypeOf(door.Record), (int)door.Motion, door.Broken), eye);
        sounds.End(eye);

        int TypeOf(string record)
        {
            if (!doorTypes.TryGetValue(record, out int type))
                doorTypes[record] = type = db?.Find(record)?.GetInt("door type") ?? 0;
            return type;
        }
    }

    /// <summary>Keeps the emitters in step with the world's buildings: the level's once read, the player's as their sites change.</summary>
    void UpdateEmitters()
    {
        var emitters = worldEmitters!;
        if (!ReferenceEquals(emittersFor, session.World))
        {
            emitters.Clear();
            ForgetEmitters();
            emittersFor = session.World;
            levelEmitters = scene.Objects is { } objects ? Task.Run(objects.Emitters) : null;
        }
        if (!levelEmittersAdded && levelEmitters is { IsCompleted: true } read)
        {
            levelEmittersAdded = true;
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
                Console.WriteLine($"audio     no building emitters: {read.Exception?.GetBaseException().Message}");
        }
        if (scene.Database is not { } db) return;

        runtimeSeen.Clear();
        foreach (var site in session.CurrentSnapshot.Buildings)
        {
            runtimeSeen.Add(site.Id);
            var now = (site.Record, site.Complete, site.Position);
            if (runtimeEmitters.TryGetValue(site.Id, out var before) && before == now) continue;
            runtimeEmitters[site.Id] = now;
            emitters.Remove(site.Id);
            // A player-built building gets its emitter when it is complete (the game's completion step starts it).
            if (site.Complete && db.Find(site.Record) is { } record && BuildingEmitters.For(db, record, site.Id, site.Position, site.Rotation) is { } emitter)
                emitters.Add(emitter);
        }
        if (runtimeEmitters.Count > runtimeSeen.Count)
            foreach (string id in runtimeEmitters.Keys.Where(id => !runtimeSeen.Contains(id)).ToArray())
            {
                runtimeEmitters.Remove(id);
                emitters.Remove(id);
            }

        // The machines of the player's buildings tell their emitters what they do (the exe's per-frame call, docs/audio.md "Point emitters"); one without an emitter is ignored.
        foreach (var machine in session.CurrentSnapshot.Machines)
            if (!machine.IsContainer) emitters.SetPower(machine.Site, machine.Sound.Efficiency, machine.Sound.Powered);
        foreach (var (emitter, rotor) in levelWind) emitters.SetPower(emitter.Key, LevelWindShare(emitter.Position, rotor), true);
    }

    /// <summary>
    /// Whether a level building's emitter plays although the simulation does not run the building (docs/audio.md "Point emitters", Meitou's choice): one that needs power and whose class powers it once
    /// when built, a generator, a battery and a furnace-like machine that needs no operator (their efficiency is the data's); not the machines that need operators or town power.
    /// </summary>
    bool LevelPowered(PlacedEmitter emitter, out float efficiency)
    {
        efficiency = 0;
        if (!emitter.Sound.NeedsPower || scene.Database is not { } db) return false;
        if (!levelMachines.TryGetValue(emitter.Building, out var data))
            levelMachines[emitter.Building] = data = db.Find(emitter.Building) is { } record ? MachineData.From(db, record) : null;
        if (data is null || !data.PowersEmitterEachFrame) return true;
        if (data.IsWind)
        {
            if (db.Find(emitter.Building) is { } record && WorldObjectLayout.WindRotor(db, record, emitter.Origin) is { } rotor) { levelWind.Add((emitter, rotor)); efficiency = LevelWindShare(emitter.Position, rotor); }
        }
        else if (data.IsGenerator || data.IsBattery || (data.PowerOperators == 0 && data.PowerOutput == 0)) efficiency = data.OutputRate;
        else return false;
        return true;
    }

    /// <summary>A level wind generator's share: its blade's <see cref="RotorSpec.WindShare"/> of the scheduled wind at the place (0 without weather), as the simulation's own generators.</summary>
    float LevelWindShare(Vector3 at, (float Min, float Max) rotor) =>
        gpu.Weather?.World is { } weather ? RotorSpec.WindShare(weather.WindAt(at.X, at.Z).Speed, rotor.Min, rotor.Max) : 0;

    void ForgetEmitters()
    {
        levelMachines.Clear();
        levelWind.Clear();
        emittersFor = null;
        levelEmitters = null;
        levelEmittersAdded = false;
        runtimeEmitters.Clear();
    }

    /// <summary>The main menu (Meitou's choice, see <see cref="AudioMainMenu"/>): the interior ambience ends and the emitters stop.</summary>
    void ResetWorldAudio()
    {
        interiorSounds?.Silence();
        worldEmitters?.Clear();
        doorSounds?.Clear();
        doorSoundsFor = null;
        ForgetEmitters();
    }

    /// <summary>One statistics line: where the pivot is and how many emitters play; empty without audio.</summary>
    string WorldAudioStats() => interiorSounds is null || worldEmitters is null ? "" :
        $"{(interiorSounds.Playing ? $"indoors ({interiorSounds.Indoors})" : interiorSounds.Inside ? "in a building without an ambience" : "outdoors")}, {worldEmitters.Started} of {worldEmitters.Count} emitters playing" +
        (worldEmitters.NearestName is { } near ? $", nearest {near} {worldEmitters.NearestDistance:0} units from the eye" : "") +
        (doorSounds is { Posted: > 0 } doors ? $", {doors.Posted} door sounds" : "");
}
