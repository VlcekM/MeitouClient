using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Combat;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.Particles;
using Meitou.Data.World;
using Meitou.Game.Host.WorldSync;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Production;

namespace Meitou.Game.Audio;

/// <summary>
/// The game's sound (docs/audio.md "In the game"): <see cref="AudioSystem"/> made beside the window, fed each frame from the camera, the game clock
/// and the weather, and the main theme on the title screen. A failure to start it is logged and the game runs silently.
/// </summary>
sealed class GameSound(GameHost host)
{
    Task<(AudioSystem? Audio, string? Error)>? _audioStart;
    internal AudioSystem? Audio;

    /// <summary>
    /// Starts making the audio on a worker (the codebook library and the sound device) while the window and the world load; nothing with
    /// <c>--no-audio</c>.
    /// </summary>
    internal void BeginAudio()
    {
        if (host.WorldOptions.NoAudio) return;
        // The sliders as saved, else the install's settings.cfg (read once, here, so the settings menu shows the same values).
        var settings = host.Config.Audio ??= AudioSettings.FromInstall(host.Install);
        _audioStart = Task.Run(() =>
        {
            try
            {
                var made = AudioSystem.TryCreate(host.Install, settings, out var error);
                if (made is not null) made.Player.Gain = MathF.Pow(10, host.WorldOptions.AudioGainDb / 20);
                return (made, error);
            }
            catch (Exception e)
            {
                // Whatever the device or the banks throw: the game goes on without sound.
                return ((AudioSystem?)null, e.Message);
            }
        });
    }

    /// <summary>
    /// Takes the audio made by <see cref="BeginAudio"/>, logs the outcome and starts the main theme when the title screen is up.
    /// </summary>
    internal void AttachAudio()
    {
        if (_audioStart is null)
        {
            Console.WriteLine("audio     off (--no-audio)");
            return;
        }
        var (made, error) = _audioStart.GetAwaiter().GetResult();
        _audioStart = null;
        Audio = made;
        Console.WriteLine(made is not null ? $"audio     {made.DeviceName}, ambience and music from the Wwise banks{GainText()} (--no-audio turns it off)" : $"audio     off: {error}");
        if (Audio is not null && host.Player is not null && host.Player.TitleOpen) Audio.Music.StartMainTheme();
        AttachUiSounds();
    }

    /// <summary>
    /// The gain part of the audio log line: ", gain N dB" when the gain option is set, else empty.
    /// </summary>
    string GainText() => host.WorldOptions.AudioGainDb != 0 ? $", gain {host.WorldOptions.AudioGainDb:+0.#;-0.#} dB" : "";

    /// <summary>
    /// The ears above a character's feet when sounds are heard from it (units; a person is about 180 tall).
    /// </summary>
    const float EarHeight = 160;

    /// <summary>
    /// Once a frame: the ambience and the biome music follow the camera as in the game's camera update. The pivot is the camera's target (the region
    /// under it is the biome), the eye its position; the hour is the game clock's. Not while the title screen is up: the game runs no camera update
    /// there, so the main theme is not stopped by an ambience event or the music bank's unload.
    /// </summary>
    internal void UpdateAudio(double realSeconds)
    {
        // The Audio settings' Sound voices slider (config.Audio.Voices) is applied by AudioSystem.Apply.
        if (Audio is null || host.Player is null || host.Player.TitleOpen) return;

        var target = host.Camera.Target;
        var eye = host.Camera.Eye;
        var front = host.Camera.Forward;
        var right = Vector3.Normalize(Vector3.Cross(front, Vector3.UnitY));

        // The Audio settings' Hear from switch: the listener and every distance limit move to the first selected character's ears; the camera
        // keeps the orientation.
        var ears = host.Config.HearFromSelected && host.Player.SelectedPosition is { } lead ? lead + Vector3.UnitY * EarHeight : eye;
        Audio.SetListener(ears, front, Vector3.Cross(right, front));
        Audio.SetPaused(host.Session.Simulation.IsPaused);
        var weather = host.ActiveWeather;
        var clock = host.Session.Clock;
        Audio.Ambience.FixDelayedSuffix = host.Config.FixAmbienceSuffix;
        Audio.Ambience.FixWindStop = host.Config.FixWindStop;
        Audio.Update(weather?.World.RegionAt(target.X, target.Z), weather?.State, clock.HourOfDay, clock.Sunrise, clock.Sunset,
            eye.Y - host.ActiveGround(eye.X, eye.Z), realSeconds);
        UpdateWorldAudio(Audio, ears == eye ? target : ears, ears);
        UpdateCharacterAudio(Audio, ears);
        UpdateTurretAudio(Audio, ears, realSeconds);
    }

    /// <summary>
    /// The audio sliders changed in the settings menu: they apply at once.
    /// </summary>
    internal void AudioSettingsChanged()
    {
        if (Audio is not null && host.Config.Audio is { } settings) Audio.Apply(settings);
    }

    /// <summary>
    /// A new game, load or import starts: the game posts <c>Create_or_Load_Game</c>, which fades the main theme out.
    /// </summary>
    internal void AudioGameStarts() => Audio?.Music.OnCreateOrLoadGame();

    /// <summary>
    /// Back at the main menu (Meitou's choice: the game has no such path): the world's ambience ends and the main theme plays again, as at the start.
    /// </summary>
    internal void AudioMainMenu()
    {
        if (Audio is null) return;
        Audio.SetPaused(false);
        Audio.Ambience.Silence();
        ResetWorldAudio();
        ResetCharacterAudio();
        ResetTurretAudio();
        Audio.Music.StartMainTheme();
    }

    internal void DisposeAudio()
    {
        // A start still running (the window closed during loading) is waited for, so its device is released.
        if (_audioStart is { } pending)
        {
            Audio = pending.GetAwaiter().GetResult().Audio;
            _audioStart = null;
        }

        Audio?.Dispose();
        Audio = null;
    }

    // The characters' sounds (docs/audio.md "Characters"): every character near the camera is a Wwise game object kept at its place with the
    // animation events of its clips (footsteps, swings) fired from the snapshot, and the combat's happenings (<see cref="World.Sounds"/>) played as
    // impacts and voices. Driven each frame from <see cref="UpdateAudio"/>; it only reads the snapshots and empties the sound queue, so the
    // simulation is not affected.
    AudioSystem? _characterAudioFor;
    internal CharacterSounds? CharacterSounds;
    GroundSurfaces? _groundSurfaces;
    AnimationSoundEvents? _animationSounds;
    AnimationSoundEvent[]?[]? _definitionEvents;
    string?[]? _definitionWeapons;
    bool[]? _definitionLoops;
    /// <summary>
    /// A clip's events with a functionality's added, by functionality and clip definition (one array each, so a cursor keeps its place).
    /// </summary>
    readonly Dictionary<(string Functionality, int Definition), AnimationSoundEvent[]?> _workEvents = [];
    /// <summary>
    /// Each working character's clips that carry machine events, kept while the clip plays (the exe adds them to the clip's instance, which lives
    /// until it is gone): a clip worked at one machine and then another while it still plays carries both machines' events.
    /// </summary>
    readonly Dictionary<ulong, List<WorkLayer>> _workLayers = [];

    /// <summary>
    /// A clip (its definition) with the events of the functionalities worked on it added; <see cref="Events"/> is replaced only when another
    /// functionality joins.
    /// </summary>
    sealed class WorkLayer
    {
        public required int Definition;
        public required AnimationSoundEvent[] Events;
        public readonly List<string> Functionalities = [];
    }
    readonly Dictionary<ulong, SoundMemo> _soundMemo = [];
    int _soundFrame;

    /// <summary>
    /// What a character did the frame before, for the sounds that are edges (a loot started, a bed got into, a person lifted).
    /// </summary>
    struct SoundMemo
    {
        public CharacterTask Task;
        public bool InBed, Carried, PutDown;
        public int Frame;
    }

    World? _soundsFor;
    readonly ConcurrentQueue<SoundEvent> _soundQueue = new();
    readonly ConditionalWeakTable<CharacterAppearance, CharacterSoundProfile> _soundProfiles = [];
    readonly List<SoundLayer> _soundLayers = [];

    static ulong SoundKey(CharacterId id) => (ulong)(uint)id.Slot << 32 | (uint)id.Generation;

    /// <summary>
    /// Once a frame: the snapshot's characters are handed to the sound objects and the queued combat sounds are played.
    /// </summary>
    void UpdateCharacterAudio(AudioSystem sound, Vector3 eye)
    {
        if (!ReferenceEquals(_characterAudioFor, sound))
        {
            _characterAudioFor = sound;
            CharacterSounds = null;
            _soundsFor = null;
        }
        if (host.Scene.Database is not { } db || host.AnimationLibrary is not { } library) return;
        if (CharacterSounds is null)
        {
            _animationSounds ??= AnimationSoundEvents.FromDatabase(db);
            _groundSurfaces ??= new GroundSurfaces(host.Install, db);
            var events = new AnimationSoundEvent[]?[library.Definitions.Count];
            for (int i = 0; i < events.Length; i++) events[i] = _animationSounds.Of(library.Definitions[i].Id);
            _definitionEvents = events;
            var weapons = new string?[events.Length];
            var techniques = db.OfType(FcsRecordType.CombatTechnique)
                .ToDictionary(r => r.StringId, CombatTechnique.From, StringComparer.Ordinal);
            for (int i = 0; i < weapons.Length; i++)
            {
                // The last blow's limb: the arms are 0 and 1 (FUN_1402b4570).
                if (techniques.TryGetValue(library.Definitions[i].Id, out var t)
                    && t is { Kinds: WeaponKinds.Unarmed, IsBlock: false, IsDodge: false, Animal: 0 })
                    weapons[i] = t.LimbOf(t.Blows) is 0 or 1 ? "HtH_Punch" : "HtH_Kick";
            }

            _definitionWeapons = weapons;
            _definitionLoops = [.. library.Definitions.Select(d => d.Loop)];
            _workEvents.Clear();
            CharacterSounds = new CharacterSounds(sound.Player, _groundSurfaces) { Floor = FloorSurface };
        }
        var sounds = CharacterSounds;
        var world = host.Session.World;
        if (!ReferenceEquals(_soundsFor, world))
        {
            // A new world: the old one's objects go, and its combat feeds this host's queue.
            sounds.Clear();
            _workLayers.Clear();
            _soundQueue.Clear();
            world.Sounds = _soundQueue;
            _soundsFor = world;
        }

        sounds.BeginFrame(eye);
        _soundFrame++;
        host.IndexPrevious();
        float alpha = host.Session.SimulationAlpha;
        float tick = world.TickSeconds;
        foreach (var c in host.Session.CurrentSnapshot.Characters)
        {
            if (c.Appearance is not { } look) continue;
            var position = c.Position;
            float yaw = c.Yaw, speed = 0;
            if (host.PreviousById.TryGetValue(c.Id, out var before))
            {
                position = Vector3.Lerp(before.Position, c.Position, alpha);
                yaw = Simulation.Session.Interp.LerpAngle(before.Yaw, c.Yaw, alpha);
                speed = new Vector2(c.Position.X - before.Position.X, c.Position.Z - before.Position.Z).Length() / tick;
            }
            position += GameHost.CarriedOffset(c, yaw);
            if (!_soundProfiles.TryGetValue(look, out var profile))
                _soundProfiles.Add(look, profile = CharacterSoundProfile.From(look, db));
            ulong key = SoundKey(c.Id);
            FillSoundLayers(c, key);
            var loop = LoopOf(c);
            var layers = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_soundLayers);
            sounds.Update(key, profile, position, yaw, speed, layers, loop, c.Down, c.CombatMode);
            JobSounds(sounds, key, c);
        }
        sounds.EndFrame();
        if (_soundMemo.Count > host.Session.CurrentSnapshot.Characters.Count)
            foreach (ulong stale in _soundMemo.Where(m => m.Value.Frame != _soundFrame).Select(m => m.Key).ToArray())
            {
                _soundMemo.Remove(stale);
                _workLayers.Remove(stale);
            }
        while (_soundQueue.TryDequeue(out var happened)) PlayCombatSound(sounds, happened);
    }

    /// <summary>
    /// The loop a character's task (and its unlocking or shackling) plays; none for the rest.
    /// </summary>
    static CharacterLoop LoopOf(CharacterSnapshot c)
    {
        if (c.Task == CharacterTask.Build) return CharacterLoop.Build;
        if (c.Task == CharacterTask.Treat) return CharacterLoop.Medic;
        if ((c.PickingLock || c.WorkingShackles) && c.Task != CharacterTask.GoTo) return CharacterLoop.Unlock;

        return CharacterLoop.None;
    }

    /// <summary>
    /// The character's animation layers as sound layers: each with its record's events (its machine's added on the clip it works with) and how far
    /// through its clip it is.
    /// </summary>
    void FillSoundLayers(CharacterSnapshot c, ulong key)
    {
        _soundLayers.Clear();
        var work = WorkLayersOf(c, key);

        // Indexed: a foreach over the interface boxes its enumerator, per character per frame.
        for (int k = 0; k < c.Animations.Count; k++)
        {
            var layer = c.Animations[k];
            bool known = (uint)layer.Definition < (uint)_definitionEvents!.Length;
            var events = !known ? null : Find(work, layer.Definition)?.Events ?? _definitionEvents[layer.Definition];
            float fraction = ClipFraction(layer, known && _definitionLoops![layer.Definition]);
            var weapons = events is null ? null : _definitionWeapons![layer.Definition];
            _soundLayers.Add(new SoundLayer(layer.Definition, events, fraction, layer.Weight, weapons));
        }
    }

    /// <summary>
    /// How far through its clip a layer is. The simulation's time of a looping clip runs on past its length (only synched and side-step clips wrap),
    /// so it is wrapped here: a looping clip's events fire on every pass, as the exe's cursor does when the clip loops (<c>FUN_140064690</c>). A
    /// one-shot clip holds below its length.
    /// </summary>
    static float ClipFraction(AnimationLayer layer, bool loops)
    {
        if (layer.Length <= 0) return 0;
        float fraction = layer.Time / layer.Length;
        return loops ? fraction - MathF.Floor(fraction) : fraction;
    }

    /// <summary>
    /// The clips a working character's machines put their <c>animation events</c> on, with those added (<c>Task_OperateMachine</c> and
    /// <c>Task_UseTrainingDummy</c> add them to the playing clip each update, <c>FUN_140065280</c>; docs/audio.md "Machine work"), each kept until
    /// its clip stops playing; null for none.
    /// </summary>
    List<WorkLayer>? WorkLayersOf(CharacterSnapshot c, ulong key)
    {
        if (c.WorkEvents is { } work && LayerOf(c, work.Clip) is var definition && WorkEventsOf(work.Functionality, definition) is { } events)
            AddWork(key, definition, work.Functionality, events);
        if (!_workLayers.TryGetValue(key, out var kept)) return null;

        // Not RemoveAll: its lambda would capture the character, a closure per working character per frame.
        for (int i = kept.Count - 1; i >= 0; i--)
        {
            if (!Plays(c, kept[i].Definition)) kept.RemoveAt(i);
        }

        if (kept.Count > 0) return kept;
        _workLayers.Remove(key);
        return null;
    }

    /// <summary>
    /// The functionality's events put on the clip: a clip that already carries another machine's keeps those and takes these too (the exe's insert
    /// skips only an event already there).
    /// </summary>
    void AddWork(ulong key, int definition, string functionality, AnimationSoundEvent[] events)
    {
        if (!_workLayers.TryGetValue(key, out var kept)) _workLayers[key] = kept = [];
        if (Find(kept, definition) is not { } layer)
        {
            layer = new WorkLayer { Definition = definition, Events = events };
            layer.Functionalities.Add(functionality);
            kept.Add(layer);
            return;
        }
        if (layer.Functionalities.Contains(functionality)) return;
        layer.Functionalities.Add(functionality);
        layer.Events = AnimationSoundEvents.Attach(layer.Events, _animationSounds!.Of(functionality)!);
    }

    static WorkLayer? Find(List<WorkLayer>? layers, int definition)
    {
        if (layers is null) return null;
        foreach (var layer in layers)
            if (layer.Definition == definition) return layer;
        return null;
    }

    static bool Plays(CharacterSnapshot c, int definition)
    {
        for (int k = 0; k < c.Animations.Count; k++)
            if (c.Animations[k].Definition == definition) return true;
        return false;
    }

    /// <summary>
    /// The definition of the character's layer playing the ANIMATION record called <paramref name="clip"/>; -1 when none does.
    /// </summary>
    static int LayerOf(CharacterSnapshot c, string clip)
    {
        for (int k = 0; k < c.Animations.Count; k++)
            if (c.Animations[k].Name == clip) return c.Animations[k].Definition;
        return -1;
    }

    /// <summary>
    /// The clip's events with the functionality's added; null when the functionality has none or the clip is unknown.
    /// </summary>
    AnimationSoundEvent[]? WorkEventsOf(string functionality, int definition)
    {
        if ((uint)definition >= (uint)_definitionEvents!.Length) return null;
        if (_workEvents.TryGetValue((functionality, definition), out var events)) return events;
        var added = _animationSounds!.Of(functionality);
        events = added is null ? null : AnimationSoundEvents.Attach(_definitionEvents[definition], added);
        _workEvents[(functionality, definition)] = events;
        return events;
    }

    /// <summary>
    /// The job sounds that are edges: a loot that started (<c>Character_Loot</c>), a bed got into or out of (<c>Character_Bed</c>, <c>Bed</c> =
    /// <c>Get_In</c> / <c>Get_Out</c>), a person lifted or set down by a put-down order.
    /// </summary>
    void JobSounds(CharacterSounds sounds, ulong key, CharacterSnapshot c)
    {
        bool carried = !c.CarriedBy.IsNone;
        if (_soundMemo.TryGetValue(key, out var before))
        {
            if (c.Task == CharacterTask.Loot && before.Task != CharacterTask.Loot) sounds.Job(key, "Character_Loot");
            if (c.InBed != before.InBed) sounds.Job(key, "Character_Bed", CharacterSounds.EventLimit, "Bed", c.InBed ? "Get_In" : "Get_Out");
            if (carried != before.Carried && (carried || before.PutDown)) sounds.Carried(key, carried);
        }
        bool putDown = carried && TaskOf(c.CarriedBy) == CharacterTask.PutDown;
        _soundMemo[key] = new SoundMemo { Task = c.Task, InBed = c.InBed, Carried = carried, PutDown = putDown, Frame = _soundFrame };
    }

    /// <summary>
    /// The shown task of <paramref name="id"/>, null when not in the snapshot. A plain loop: the lambda it replaced captured the character, a closure
    /// per character a frame.
    /// </summary>
    CharacterTask? TaskOf(CharacterId id)
    {
        var all = host.Session.CurrentSnapshot.Characters;
        for (int i = 0; i < all.Count; i++)
            if (all[i].Id == id) return all[i].Task;
        return null;
    }

    void PlayCombatSound(CharacterSounds sounds, in SoundEvent e)
    {
        ulong subject = SoundKey(e.Subject), other = e.Other.IsNone ? 0 : SoundKey(e.Other);
        switch (e.Kind)
        {
            case SoundKind.Blocked:
                sounds.Blocked(subject, e.AttackerUnarmed, e.AttackerRobot);
                break;
            case SoundKind.Hit:
                sounds.Hit(subject, e.Heavy, e.AttackerUnarmed, e.AttackerRobot);
                break;
            case SoundKind.BoltHit:
                sounds.BoltHit(subject, e.Armoured);
                break;
            case SoundKind.BoltStuck:
                sounds.BoltStuck(e.Position, _groundSurfaces?.At(e.Position.X, e.Position.Z) ?? -1);
                break;
            case SoundKind.Shot:
                sounds.Shot(subject);
                break;
            case SoundKind.TurretShot:
                sounds.TurretShot(e.Position);
                break;
            case SoundKind.WallBroken:
                sounds.WallBreak(e.Position, host.Config.FixDoorBreak);
                break;
            case SoundKind.Down:
                sounds.Down(subject, other);
                break;
            case SoundKind.Alert:
                sounds.Alert(subject);
                break;
            case SoundKind.Eat:
                sounds.Job(subject, "Character_Consume", CharacterSounds.FootstepLimit, "Consume", "Eat");
                break;
            case SoundKind.Unlocked:
                sounds.Job(subject, "Character_Unlock_Success", 0);
                break;
            case SoundKind.Locked:
                sounds.Job(subject, "Character_Lock");
                break;
            case SoundKind.PickedUp:
                sounds.Job(subject, "Character_Pick_Up_Ground");
                break;
            case SoundKind.ShacklesOn:
                if (e.Item is not null) _uiSounds?.Play(new UiSoundEvent("Item_Drop", Item: e.Item));
                break;
            case SoundKind.BountyPaid:
                _uiSounds?.Play(new UiSoundEvent("Notifications", "Notifications", "Bounty_Fulfilled"));
                break;
            case SoundKind.DismantleDone:
                _uiSounds?.Play(new UiSoundEvent("Notifications", "Notifications", "Thing_Complete"));
                break;
        }
    }

    /// <summary>
    /// The main menu or a new world: every character object ends.
    /// </summary>
    void ResetCharacterAudio()
    {
        CharacterSounds?.Clear();
        _soundQueue.Clear();
        if (_soundsFor is { } world) world.Sounds = null;
        _soundsFor = null;
    }

    /// <summary>
    /// One statistics line; empty without audio.
    /// </summary>
    internal string CharacterAudioStats() => CharacterSounds?.StatsLine() ?? "";

    // The world's own sounds (docs/audio.md "Interiors" and "Point emitters"): the building under the camera's pivot (the interior ambience and the
    // reverb switch) and the sound emitters of buildings (campfires, torches, ovens, water tanks, …) that play while the camera eye is near. Driven
    // each frame from <see cref="UpdateAudio"/>.
    AudioSystem? _worldAudioFor;
    InteriorSounds? _interiorSounds;
    internal WorldEmitters? WorldEmitters;
    DoorSounds? _doorSounds;
    // The world the door sounds follow, and each door record's `door type` (DoorMaterial).
    object? _doorSoundsFor;
    readonly Dictionary<string, int> _doorTypes = new(StringComparer.Ordinal);
    // The world the emitters were made for, the level's emitters (read on a worker: every building with sounds, positioned on the heightmap) and
    // whether they were added.
    object? _emittersFor;
    Task<List<PlacedEmitter>>? _levelEmitters;
    bool _levelEmittersAdded;

    // The player-built buildings the emitters follow: what each was when its emitter was decided.
    readonly Dictionary<string, (string Record, bool Complete, Vector3 Position)> _runtimeEmitters = new(StringComparer.Ordinal);
    readonly HashSet<string> _runtimeSeen = new(StringComparer.Ordinal);

    // The level's building records as machines (null for one that is not), for whether their emitters play.
    readonly Dictionary<string, MachineData?> _levelMachines = new(StringComparer.Ordinal);

    // The level's wind generators' emitters, told their blade's wind share each frame.
    readonly List<(PlacedEmitter Emitter, (float Min, float Max) Rotor)> _levelWind = [];

    /// <summary>
    /// Once a frame, after the ambience: <paramref name="pivot"/> is the camera's target (the game tests the building under it),
    /// <paramref name="eye"/> the camera's position (the emitters' distance); both are the selected character's ears under the Audio settings' Hear
    /// from switch.
    /// </summary>
    void UpdateWorldAudio(AudioSystem sound, Vector3 pivot, Vector3 eye)
    {
        if (!ReferenceEquals(_worldAudioFor, sound))
        {
            _worldAudioFor = sound;
            _interiorSounds = new InteriorSounds(sound.Player);
            WorldEmitters = new WorldEmitters(sound.Player);
            _doorSounds = new DoorSounds(sound.Player);
            _doorSoundsFor = null;
            ForgetEmitters();
        }

        // The building whose indoor volume lies under the pivot, whatever the pivot's height (a ray cast straight down from far above).
        var volume = host.ActiveObjects?.InteriorUnder(pivot.X, pivot.Z);
        InteriorHit? hit = volume is null ? null : new InteriorHit(volume.PlacementId, volume.Ambience, volume.Integrity, volume.Cavernous);
        _interiorSounds!.Update(hit);

        UpdateEmitters();
        UpdateEffectEmitters(WorldEmitters!);
        host.CorpseFlies.UpdateCorpseFlyEmitters(WorldEmitters!);
        WorldEmitters!.Update(eye);
        _doorSounds!.FixBreak = host.Config.FixDoorBreak;
        UpdateDoorSounds(eye);
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

    /// <summary>
    /// Keeps the emitters in step with the world's buildings: the level's once read, the player's as their sites change.
    /// </summary>
    void UpdateEmitters()
    {
        var emitters = WorldEmitters!;
        if (!ReferenceEquals(_emittersFor, host.Session.World))
        {
            emitters.Clear();
            ForgetEmitters();
            _emittersFor = host.Session.World;
            _levelEmitters = host.Scene.Objects is { } objects ? Task.Run(objects.Emitters) : null;
        }
        if (!_levelEmittersAdded && _levelEmitters is { IsCompleted: true } read)
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
                Console.WriteLine($"audio     no building emitters: {read.Exception?.GetBaseException().Message}");
        }
        if (host.Scene.Database is not { } db) return;

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
        if (_runtimeEmitters.Count > _runtimeSeen.Count)
            foreach (string id in _runtimeEmitters.Keys.Where(id => !_runtimeSeen.Contains(id)).ToArray())
            {
                _runtimeEmitters.Remove(id);
                emitters.Remove(id);
            }

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

    void ForgetEmitters()
    {
        _levelMachines.Clear();
        _levelWind.Clear();
        _emittersFor = null;
        _levelEmitters = null;
        _levelEmittersAdded = false;
        _runtimeEmitters.Clear();
    }

    /// <summary>
    /// The main menu (Meitou's choice, see <see cref="AudioMainMenu"/>): the interior ambience ends and the emitters stop.
    /// </summary>
    void ResetWorldAudio()
    {
        _interiorSounds?.Silence();
        WorldEmitters?.Clear();
        _doorSounds?.Clear();
        _doorSoundsFor = null;
        ForgetEmitters();
    }

    /// <summary>
    /// One statistics line: where the pivot is and how many emitters play; empty without audio.
    /// </summary>
    internal string WorldAudioStats()
    {
        if (_interiorSounds is null || WorldEmitters is null) return "";

        string place = _interiorSounds.Playing ? $"indoors ({_interiorSounds.Indoors})"
            : _interiorSounds.Inside ? "in a building without an ambience"
            : "outdoors";
        string emitters = $"{place}, {WorldEmitters.Started} of {WorldEmitters.Count} emitters playing";
        string nearest = WorldEmitters.NearestName is { } near ? $", nearest {near} {WorldEmitters.NearestDistance:0} units from the eye" : "";
        string doors = _doorSounds is { Posted: > 0 } door ? $", {door.Posted} door sounds" : "";
        return emitters + nearest + doors;
    }

    // The turrets' movement sounds (docs/audio.md "Other world-side events"): a manned turret (<see cref="CharacterSnapshot.Turret"/>) turns toward
    // the simulation's aim point by the exe's rule in <see cref="TurretSounds"/>, which posts <c>Turret_Mvt_Start</c> / <c>Turret_Mvt_Stop</c>.
    // Display only (frame time, its own copy of the parts): nothing here is read by the simulation; the turn that is drawn is the simulation's
    // (<see cref="GameHost.UpdateTurretPoses"/>). Driven each frame from <see cref="UpdateAudio"/>.

    /// <summary>
    /// Turrets farther than this from the eye are not looked after (the movement sound is dropped beyond <see cref="TurretSounds.Range"/> anyway).
    /// </summary>
    const float TurretAudioRadius = 4000;

    AudioSystem? _turretAudioFor;
    TurretSounds? _turretSounds;
    World? _turretSoundsWorld;
    // Each turret's mount and its gun's range, by the turret's key (null: its records are missing).
    readonly Dictionary<string, (TurretMount Mount, float Range)?> _turretMounts = new(StringComparer.Ordinal);
    readonly List<TurretAim> _mannedTurrets = [];

    void UpdateTurretAudio(AudioSystem sound, Vector3 eye, double realSeconds)
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
    /// The point the gunner aims at: the simulation's aim (<see cref="MannedTurret.Aim"/>, docs/game/combat.md "Turrets"); without one (a world
    /// without the combat system's turrets) the nearest living, free hostile character within the gun's range, 5 units above its place (<b>engine
    /// choice</b>). Null: none (the turret keeps its last aim).
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

    /// <summary>
    /// The main menu: the turrets' objects end.
    /// </summary>
    void ResetTurretAudio()
    {
        _turretSounds?.Clear();
        _turretMounts.Clear();
        _turretSoundsWorld = null;
    }

    // The sound emitters of effects (docs/audio.md "Point emitters"): every live unit of an effect whose EFFECT record has a <c>sound</c> (the sky
    // beams) is a <see cref="WorldEmitters"/> emitter that follows it and ends with it. Driven each frame from <see cref="UpdateWorldAudio"/>.
    //
    // The emitter key of each effect unit that has one, the units seen this frame and the emitters' owner (a new one forgets them).
    readonly Dictionary<EffectUnit, string> _effectEmitterKeys = [];
    readonly HashSet<EffectUnit> _effectUnitsSeen = [];
    readonly List<EffectUnit> _effectUnitsGone = [];
    WorldEmitters? _effectEmittersFor;
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
                foreach (var unit in group.Units)
                {
                    if (unit.Done) continue;
                    _effectUnitsSeen.Add(unit);
                    if (!_effectEmitterKeys.TryGetValue(unit, out string? key))
                        _effectEmitterKeys[unit] = key = $"effect/{++_effectEmitterNumber}";
                    // Missing from the emitters after the main menu cleared them: back at the unit's place.
                    if (!emitters.Contains(key))
                        emitters.Add(new PlacedEmitter(key, sound, unit.Position));
                    else
                        emitters.Move(key, unit.Position);
                }
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
    /// The GroundType of the building floor under a character's feet, -1 when the ground is not a building's
    /// (<see cref="Meitou.Game.Audio.CharacterSounds.Floor"/>, docs/audio.md "Surface"): the building's <c>interior material</c> when the character
    /// stands inside a building's indoor hull, else its <c>exterior material</c>.
    /// </summary>
    int FloorSurface(Vector3 feet)
    {
        if (host.ActiveObjects is not { } objects) return -1;

        bool inside = objects.InteriorAt(feet, InteriorView.InsideMargin) is not null;
        return objects.FloorMaterialUnder(feet, inside);
    }

    // The interface's sounds (docs/audio.md "Interface"): <see cref="PlayerInterface.UiSound"/> goes to <see cref="UiSounds"/> on the GUI object
    // while audio runs, also on the title screen.
    UiSounds? _uiSounds;

    /// <summary>
    /// Called when the audio is attached: the interface starts sounding, or stays silent without audio.
    /// </summary>
    void AttachUiSounds()
    {
        if (host.Player is null) return;
        _uiSounds = Audio is null ? null : new UiSounds(Audio.Player, () => host.Scene.Database);
        var sounds = _uiSounds;
        host.Player.InterfaceSounds.UiSound = sounds is null ? null : e => sounds.Play(e);
    }

    /// <summary>
    /// One statistics line; empty without audio.
    /// </summary>
    internal string UiAudioStats() => _uiSounds is null ? "" : $"{_uiSounds.Posted} interface sounds";
}