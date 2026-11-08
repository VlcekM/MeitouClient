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
/// The game's sound (docs/audio.md "In the game"): <see cref="AudioSystem"/> made beside the window, fed each frame from the camera, the game clock and the weather,
/// and the main theme on the title screen. A failure to start it is logged and the game runs silently.
/// </summary>
sealed class GameSound(GameHost host)
{
    Task<(AudioSystem? Audio, string? Error)>? audioStart;
    internal AudioSystem? audio;

    /// <summary>Starts making the audio on a worker (the codebook library and the sound device) while the window and the world load; nothing with <c>--no-audio</c>.</summary>
    internal void BeginAudio()
    {
        if (host.o.NoAudio) return;
        // The sliders as saved, else the install's settings.cfg (read once, here, so the settings menu shows the same values).
        var settings = host.config.Audio ??= AudioSettings.FromInstall(host.install);
        audioStart = Task.Run(() =>
        {
            try
            {
                var made = AudioSystem.TryCreate(host.install, settings, out var error);
                if (made is not null) made.Player.Gain = MathF.Pow(10, host.o.AudioGainDb / 20);
                return (made, error);
            }
            catch (Exception e)
            {
                // Whatever the device or the banks throw: the game goes on without sound.
                return ((AudioSystem?)null, e.Message);
            }
        });
    }

    /// <summary>Takes the audio made by <see cref="BeginAudio"/>, logs the outcome and starts the main theme when the title screen is up.</summary>
    internal void AttachAudio()
    {
        if (audioStart is null)
        {
            Console.WriteLine("audio     off (--no-audio)");
            return;
        }
        var (made, error) = audioStart.GetAwaiter().GetResult();
        audioStart = null;
        audio = made;
        Console.WriteLine(made is not null
            ? $"audio     {made.DeviceName}, ambience and music from the Wwise banks{(host.o.AudioGainDb != 0 ? $", gain {host.o.AudioGainDb:+0.#;-0.#} dB" : "")} (--no-audio turns it off)"
            : $"audio     off: {error}");
        if (audio is not null && host.player is not null && host.player.TitleOpen) audio.Music.StartMainTheme();
        AttachUiSounds();
    }

    /// <summary>
    /// Once a frame: the ambience and the biome music follow the camera as in the game's camera update. The pivot is the camera's target (the region under it
    /// is the biome), the eye its position; the hour is the game clock's. Not while the title screen is up: the game runs no camera update there, so the main
    /// theme is not stopped by an ambience event or the music bank's unload.
    /// </summary>
    /// <summary>The ears above a character's feet when sounds are heard from it (units; a person is about 180 tall).</summary>
    const float EarHeight = 160;

    internal void UpdateAudio(double realSeconds)
    {
        // The Audio settings' Sound voices slider (config.Audio.Voices) is applied by AudioSystem.Apply.
        if (audio is null || host.player is null || host.player.TitleOpen) return;
        var target = host.camera.Target;
        var eye = host.camera.Eye;
        var front = host.camera.Forward;
        var right = Vector3.Normalize(Vector3.Cross(front, Vector3.UnitY));
        // The Audio settings' Hear from switch: the listener and every distance limit move to the first selected character's ears; the camera keeps the orientation.
        var ears = host.config.HearFromSelected && host.player.SelectedPosition is { } lead ? lead + Vector3.UnitY * EarHeight : eye;
        audio.SetListener(ears, front, Vector3.Cross(right, front));
        audio.SetPaused(host.session.Simulation.IsPaused);
        var weather = host.ActiveWeather;
        var clock = host.session.Clock;
        audio.Ambience.FixDelayedSuffix = host.config.FixAmbienceSuffix;
        audio.Ambience.FixWindStop = host.config.FixWindStop;
        audio.Update(weather?.World.RegionAt(target.X, target.Z), weather?.State, clock.HourOfDay, clock.Sunrise, clock.Sunset,
            eye.Y - host.ActiveGround(eye.X, eye.Z), realSeconds);
        UpdateWorldAudio(audio, ears == eye ? target : ears, ears);
        UpdateCharacterAudio(audio, ears);
        UpdateTurretAudio(audio, ears, realSeconds);
    }

    /// <summary>The audio sliders changed in the settings menu: they apply at once.</summary>
    internal void AudioSettingsChanged()
    {
        if (audio is not null && host.config.Audio is { } settings) audio.Apply(settings);
    }

    /// <summary>A new game, load or import starts: the game posts <c>Create_or_Load_Game</c>, which fades the main theme out.</summary>
    internal void AudioGameStarts() => audio?.Music.OnCreateOrLoadGame();

    /// <summary>
    /// Back at the main menu (Meitou's choice: the game has no such path): the world's ambience ends and the main theme plays again, as at the start.
    /// </summary>
    internal void AudioMainMenu()
    {
        if (audio is null) return;
        audio.SetPaused(false);
        audio.Ambience.Silence();
        ResetWorldAudio();
        ResetCharacterAudio();
        ResetTurretAudio();
        audio.Music.StartMainTheme();
    }

    internal void DisposeAudio()
    {
        // A start still running (the window closed during loading) is waited for, so its device is released.
        if (audioStart is { } pending) { audio = pending.GetAwaiter().GetResult().Audio; audioStart = null; }
        audio?.Dispose();
        audio = null;
    }

    // <summary>
    // The characters' sounds (docs/audio.md "Characters"): every character near the camera is a Wwise game object kept at its place with the animation events of its clips
    // (footsteps, swings) fired from the snapshot, and the combat's happenings (<see cref="World.Sounds"/>) played as impacts and voices. Driven each frame from <see cref="UpdateAudio"/>;
    // it only reads the snapshots and empties the sound queue, so the simulation is not affected.
    // </summary>
    AudioSystem? characterAudioFor;
    internal CharacterSounds? characterSounds;
    GroundSurfaces? groundSurfaces;
    AnimationSoundEvents? animationSounds;
    AnimationSoundEvent[]?[]? definitionEvents;
    string?[]? definitionWeapons;
    bool[]? definitionLoops;
    /// <summary>A clip's events with a functionality's added, by functionality and clip definition (one array each, so a cursor keeps its place).</summary>
    readonly Dictionary<(string Functionality, int Definition), AnimationSoundEvent[]?> workEvents = [];
    /// <summary>
    /// Each working character's clips that carry machine events, kept while the clip plays (the exe adds them to the clip's instance, which lives until it is gone): a clip worked at
    /// one machine and then another while it still plays carries both machines' events.
    /// </summary>
    readonly Dictionary<ulong, List<WorkLayer>> workLayers = [];

    /// <summary>A clip (its definition) with the events of the functionalities worked on it added; <see cref="Events"/> is replaced only when another functionality joins.</summary>
    sealed class WorkLayer
    {
        public required int Definition;
        public required AnimationSoundEvent[] Events;
        public readonly List<string> Functionalities = [];
    }
    readonly Dictionary<ulong, SoundMemo> soundMemo = [];
    int soundFrame;

    /// <summary>What a character did the frame before, for the sounds that are edges (a loot started, a bed got into, a person lifted).</summary>
    struct SoundMemo { public CharacterTask Task; public bool InBed, Carried, PutDown; public int Frame; }
    World? soundsFor;
    readonly ConcurrentQueue<SoundEvent> soundQueue = new();
    readonly ConditionalWeakTable<CharacterAppearance, CharacterSoundProfile> soundProfiles = [];
    readonly List<SoundLayer> soundLayers = [];

    static ulong SoundKey(CharacterId id) => (ulong)(uint)id.Slot << 32 | (uint)id.Generation;

    /// <summary>Once a frame: the snapshot's characters are handed to the sound objects and the queued combat sounds are played.</summary>
    void UpdateCharacterAudio(AudioSystem sound, Vector3 eye)
    {
        if (!ReferenceEquals(characterAudioFor, sound))
        {
            characterAudioFor = sound;
            characterSounds = null;
            soundsFor = null;
        }
        if (host.scene.Database is not { } db || host.animationLibrary is not { } library) return;
        if (characterSounds is null)
        {
            animationSounds ??= AnimationSoundEvents.FromDatabase(db);
            groundSurfaces ??= new GroundSurfaces(host.install, db);
            var events = new AnimationSoundEvent[]?[library.Definitions.Count];
            for (int i = 0; i < events.Length; i++) events[i] = animationSounds.Of(library.Definitions[i].Id);
            definitionEvents = events;
            var weapons = new string?[events.Length];
            var techniques = db.OfType(FcsRecordType.COMBAT_TECHNIQUE).ToDictionary(r => r.StringId, CombatTechnique.From, StringComparer.Ordinal);
            for (int i = 0; i < weapons.Length; i++)
                if (techniques.TryGetValue(library.Definitions[i].Id, out var t) && t is { Kinds: WeaponKinds.Unarmed, IsBlock: false, IsDodge: false, Animal: 0 })
                    weapons[i] = t.LimbOf(t.Blows) is 0 or 1 ? "HtH_Punch" : "HtH_Kick";   // the last blow's limb: the arms are 0 and 1 (FUN_1402b4570)
            definitionWeapons = weapons;
            definitionLoops = [.. library.Definitions.Select(d => d.Loop)];
            workEvents.Clear();
            characterSounds = new CharacterSounds(sound.Player, groundSurfaces) { Floor = FloorSurface };
        }
        var sounds = characterSounds;
        var world = host.session.World;
        if (!ReferenceEquals(soundsFor, world))
        {
            // A new world: the old one's objects go, and its combat feeds this host's queue.
            sounds.Clear();
            workLayers.Clear();
            soundQueue.Clear();
            world.Sounds = soundQueue;
            soundsFor = world;
        }

        sounds.BeginFrame(eye);
        soundFrame++;
        host.IndexPrevious();
        float alpha = host.session.SimulationAlpha;
        float tick = world.TickSeconds;
        foreach (var c in host.session.CurrentSnapshot.Characters)
        {
            if (c.Appearance is not { } look) continue;
            var position = c.Position;
            float yaw = c.Yaw, speed = 0;
            if (host.previousById.TryGetValue(c.Id, out var before))
            {
                position = Vector3.Lerp(before.Position, c.Position, alpha);
                yaw = Meitou.Simulation.Session.Interp.LerpAngle(before.Yaw, c.Yaw, alpha);
                speed = new Vector2(c.Position.X - before.Position.X, c.Position.Z - before.Position.Z).Length() / tick;
            }
            position += GameHost.CarriedOffset(c, yaw);
            if (!soundProfiles.TryGetValue(look, out var profile))
                soundProfiles.Add(look, profile = CharacterSoundProfile.From(look, db));
            ulong key = SoundKey(c.Id);
            FillSoundLayers(c, key);
            var loop = c.Task == CharacterTask.Build ? CharacterLoop.Build : c.Task == CharacterTask.Treat ? CharacterLoop.Medic : (c.PickingLock || c.WorkingShackles) && c.Task != CharacterTask.GoTo ? CharacterLoop.Unlock : CharacterLoop.None;
            sounds.Update(key, profile, position, yaw, speed, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(soundLayers), loop, c.Down, c.CombatMode);
            JobSounds(sounds, key, c);
        }
        sounds.EndFrame();
        if (soundMemo.Count > host.session.CurrentSnapshot.Characters.Count)
            foreach (ulong stale in soundMemo.Where(m => m.Value.Frame != soundFrame).Select(m => m.Key).ToArray())
            {
                soundMemo.Remove(stale);
                workLayers.Remove(stale);
            }
        while (soundQueue.TryDequeue(out var happened)) PlayCombatSound(sounds, happened);
    }

    /// <summary>The character's animation layers as sound layers: each with its record's events (its machine's added on the clip it works with) and how far through its clip it is.</summary>
    void FillSoundLayers(CharacterSnapshot c, ulong key)
    {
        soundLayers.Clear();
        var work = WorkLayersOf(c, key);
        for (int k = 0; k < c.Animations.Count; k++)   // indexed: a foreach over the interface boxes its enumerator, per character per frame
        {
            var layer = c.Animations[k];
            bool known = (uint)layer.Definition < (uint)definitionEvents!.Length;
            var events = !known ? null : Find(work, layer.Definition)?.Events ?? definitionEvents[layer.Definition];
            float fraction = ClipFraction(layer, known && definitionLoops![layer.Definition]);
            soundLayers.Add(new SoundLayer(layer.Definition, events, fraction, layer.Weight, events is null ? null : definitionWeapons![layer.Definition]));
        }
    }

    /// <summary>
    /// How far through its clip a layer is. The simulation's time of a looping clip runs on past its length (only synched and side-step clips wrap), so it is wrapped here: a looping
    /// clip's events fire on every pass, as the exe's cursor does when the clip loops (<c>FUN_140064690</c>). A one-shot clip holds below its length.
    /// </summary>
    static float ClipFraction(AnimationLayer layer, bool loops)
    {
        if (layer.Length <= 0) return 0;
        float fraction = layer.Time / layer.Length;
        return loops ? fraction - MathF.Floor(fraction) : fraction;
    }

    /// <summary>
    /// The clips a working character's machines put their <c>animation events</c> on, with those added (<c>Task_OperateMachine</c> and <c>Task_UseTrainingDummy</c> add them to the
    /// playing clip each update, <c>FUN_140065280</c>; docs/audio.md "Machine work"), each kept until its clip stops playing; null for none.
    /// </summary>
    List<WorkLayer>? WorkLayersOf(CharacterSnapshot c, ulong key)
    {
        if (c.WorkEvents is { } work && LayerOf(c, work.Clip) is var definition && WorkEventsOf(work.Functionality, definition) is { } events)
            AddWork(key, definition, work.Functionality, events);
        if (!workLayers.TryGetValue(key, out var kept)) return null;
        for (int i = kept.Count - 1; i >= 0; i--)   // not RemoveAll: its lambda would capture the character, a closure per working character per frame
            if (!Plays(c, kept[i].Definition)) kept.RemoveAt(i);
        if (kept.Count > 0) return kept;
        workLayers.Remove(key);
        return null;
    }

    /// <summary>
    /// The functionality's events put on the clip: a clip that already carries another machine's keeps those and takes these too (the exe's insert skips only an event already there).
    /// </summary>
    void AddWork(ulong key, int definition, string functionality, AnimationSoundEvent[] events)
    {
        if (!workLayers.TryGetValue(key, out var kept)) workLayers[key] = kept = [];
        if (Find(kept, definition) is not { } layer)
        {
            layer = new WorkLayer { Definition = definition, Events = events };
            layer.Functionalities.Add(functionality);
            kept.Add(layer);
            return;
        }
        if (layer.Functionalities.Contains(functionality)) return;
        layer.Functionalities.Add(functionality);
        layer.Events = AnimationSoundEvents.Attach(layer.Events, animationSounds!.Of(functionality)!);
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

    /// <summary>The definition of the character's layer playing the ANIMATION record called <paramref name="clip"/>; -1 when none does.</summary>
    static int LayerOf(CharacterSnapshot c, string clip)
    {
        for (int k = 0; k < c.Animations.Count; k++)
            if (c.Animations[k].Name == clip) return c.Animations[k].Definition;
        return -1;
    }

    /// <summary>The clip's events with the functionality's added; null when the functionality has none or the clip is unknown.</summary>
    AnimationSoundEvent[]? WorkEventsOf(string functionality, int definition)
    {
        if ((uint)definition >= (uint)definitionEvents!.Length) return null;
        if (workEvents.TryGetValue((functionality, definition), out var events)) return events;
        events = animationSounds!.Of(functionality) is { } added ? AnimationSoundEvents.Attach(definitionEvents[definition], added) : null;
        workEvents[(functionality, definition)] = events;
        return events;
    }

    /// <summary>The job sounds that are edges: a loot that started (<c>Character_Loot</c>), a bed got into or out of (<c>Character_Bed</c>, <c>Bed</c> = <c>Get_In</c> / <c>Get_Out</c>), a person lifted or set down by a put-down order.</summary>
    void JobSounds(CharacterSounds sounds, ulong key, CharacterSnapshot c)
    {
        bool carried = !c.CarriedBy.IsNone;
        if (soundMemo.TryGetValue(key, out var before))
        {
            if (c.Task == CharacterTask.Loot && before.Task != CharacterTask.Loot) sounds.Job(key, "Character_Loot");
            if (c.InBed != before.InBed) sounds.Job(key, "Character_Bed", CharacterSounds.EventLimit, "Bed", c.InBed ? "Get_In" : "Get_Out");
            if (carried != before.Carried && (carried || before.PutDown)) sounds.Carried(key, carried);
        }
        bool putDown = carried && TaskOf(c.CarriedBy) == CharacterTask.PutDown;
        soundMemo[key] = new SoundMemo { Task = c.Task, InBed = c.InBed, Carried = carried, PutDown = putDown, Frame = soundFrame };
    }

    /// <summary>The shown task of <paramref name="id"/>, null when not in the snapshot. A plain loop: the lambda it replaced captured the character, a closure per character a frame.</summary>
    CharacterTask? TaskOf(CharacterId id)
    {
        var all = host.session.CurrentSnapshot.Characters;
        for (int i = 0; i < all.Count; i++)
            if (all[i].Id == id) return all[i].Task;
        return null;
    }

    void PlayCombatSound(CharacterSounds sounds, in SoundEvent e)
    {
        ulong subject = SoundKey(e.Subject), other = e.Other.IsNone ? 0 : SoundKey(e.Other);
        switch (e.Kind)
        {
            case SoundKind.Blocked: sounds.Blocked(subject, e.AttackerUnarmed, e.AttackerRobot); break;
            case SoundKind.Hit: sounds.Hit(subject, e.Heavy, e.AttackerUnarmed, e.AttackerRobot); break;
            case SoundKind.BoltHit: sounds.BoltHit(subject, e.Armoured); break;
            case SoundKind.BoltStuck: sounds.BoltStuck(e.Position, groundSurfaces?.At(e.Position.X, e.Position.Z) ?? -1); break;
            case SoundKind.Shot: sounds.Shot(subject); break;
            case SoundKind.TurretShot: sounds.TurretShot(e.Position); break;
            case SoundKind.WallBroken: sounds.WallBreak(e.Position, host.config.FixDoorBreak); break;
            case SoundKind.Down: sounds.Down(subject, other); break;
            case SoundKind.Alert: sounds.Alert(subject); break;
            case SoundKind.Eat: sounds.Job(subject, "Character_Consume", CharacterSounds.FootstepLimit, "Consume", "Eat"); break;
            case SoundKind.Unlocked: sounds.Job(subject, "Character_Unlock_Success", 0); break;
            case SoundKind.Locked: sounds.Job(subject, "Character_Lock"); break;
            case SoundKind.PickedUp: sounds.Job(subject, "Character_Pick_Up_Ground"); break;
            case SoundKind.ShacklesOn: if (e.Item is not null) uiSounds?.Play(new UiSoundEvent("Item_Drop", Item: e.Item)); break;
            case SoundKind.BountyPaid: uiSounds?.Play(new UiSoundEvent("Notifications", "Notifications", "Bounty_Fulfilled")); break;
            case SoundKind.DismantleDone: uiSounds?.Play(new UiSoundEvent("Notifications", "Notifications", "Thing_Complete")); break;
        }
    }

    /// <summary>The main menu or a new world: every character object ends.</summary>
    void ResetCharacterAudio()
    {
        characterSounds?.Clear();
        soundQueue.Clear();
        if (soundsFor is { } world) world.Sounds = null;
        soundsFor = null;
    }

    /// <summary>One statistics line; empty without audio.</summary>
    internal string CharacterAudioStats() => characterSounds?.StatsLine() ?? "";

    // <summary>
    // The world's own sounds (docs/audio.md "Interiors" and "Point emitters"): the building under the camera's pivot (the interior ambience and the reverb switch) and the sound
    // emitters of buildings (campfires, torches, ovens, water tanks, …) that play while the camera eye is near. Driven each frame from <see cref="UpdateAudio"/>.
    // </summary>
    AudioSystem? worldAudioFor;
    InteriorSounds? interiorSounds;
    internal WorldEmitters? worldEmitters;
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
        var volume = host.ActiveObjects?.InteriorUnder(pivot.X, pivot.Z);
        interiorSounds!.Update(volume is null ? null : new InteriorHit(volume.PlacementId, volume.Ambience, volume.Integrity, volume.Cavernous));

        UpdateEmitters();
        UpdateEffectEmitters(worldEmitters!);
        host.corpseFlies.UpdateCorpseFlyEmitters(worldEmitters!);
        worldEmitters!.Update(eye);
        doorSounds!.FixBreak = host.config.FixDoorBreak;
        UpdateDoorSounds(eye);
    }

    /// <summary>The doors' open, close, shut and break sounds from the snapshot's doors (docs/audio.md "Other world-side events").</summary>
    void UpdateDoorSounds(Vector3 eye)
    {
        var sounds = doorSounds!;
        if (!ReferenceEquals(doorSoundsFor, host.session.World))
        {
            sounds.Clear();
            doorTypes.Clear();
            doorSoundsFor = host.session.World;
        }
        var db = host.scene.Database;
        sounds.Begin();
        foreach (var (key, door) in host.session.CurrentSnapshot.Doors)
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
        if (!ReferenceEquals(emittersFor, host.session.World))
        {
            emitters.Clear();
            ForgetEmitters();
            emittersFor = host.session.World;
            levelEmitters = host.scene.Objects is { } objects ? Task.Run(objects.Emitters) : null;
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
        if (host.scene.Database is not { } db) return;

        runtimeSeen.Clear();
        foreach (var site in host.session.CurrentSnapshot.Buildings)
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
        foreach (var machine in host.session.CurrentSnapshot.Machines)
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
        if (!emitter.Sound.NeedsPower || host.scene.Database is not { } db) return false;
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
        host.gpu.Weather?.World is { } weather ? RotorSpec.WindShare(weather.WindAt(at.X, at.Z).Speed, rotor.Min, rotor.Max) : 0;

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
    internal string WorldAudioStats() => interiorSounds is null || worldEmitters is null ? "" :
        $"{(interiorSounds.Playing ? $"indoors ({interiorSounds.Indoors})" : interiorSounds.Inside ? "in a building without an ambience" : "outdoors")}, {worldEmitters.Started} of {worldEmitters.Count} emitters playing" +
        (worldEmitters.NearestName is { } near ? $", nearest {near} {worldEmitters.NearestDistance:0} units from the eye" : "") +
        (doorSounds is { Posted: > 0 } doors ? $", {doors.Posted} door sounds" : "");

    // <summary>
    // The turrets' movement sounds (docs/audio.md "Other world-side events"): a manned turret (<see cref="CharacterSnapshot.Turret"/>) turns toward the simulation's aim point by the exe's rule in
    // <see cref="TurretSounds"/>, which posts <c>Turret_Mvt_Start</c> / <c>Turret_Mvt_Stop</c>. Display only (frame time, its own copy of the parts): nothing here is read by the simulation; the turn that is drawn is the
    // simulation's (<see cref="GameHost.UpdateTurretPoses"/>). Driven each frame from <see cref="UpdateAudio"/>.
    // </summary>
    /// <summary>Turrets farther than this from the eye are not looked after (the movement sound is dropped beyond <see cref="TurretSounds.Range"/> anyway).</summary>
    const float TurretAudioRadius = 4000;

    AudioSystem? turretAudioFor;
    TurretSounds? turretSounds;
    World? turretSoundsWorld;
    // Each turret's mount and its gun's range, by the turret's key (null: its records are missing).
    readonly Dictionary<string, (TurretMount Mount, float Range)?> turretMounts = new(StringComparer.Ordinal);
    readonly List<TurretAim> mannedTurrets = [];

    void UpdateTurretAudio(AudioSystem sound, Vector3 eye, double realSeconds)
    {
        if (!ReferenceEquals(turretAudioFor, sound))
        {
            turretAudioFor = sound;
            turretSounds = new TurretSounds(sound.Player);
            turretSoundsWorld = null;
        }
        var sounds = turretSounds!;
        if (!ReferenceEquals(turretSoundsWorld, host.session.World))
        {
            sounds.Clear();
            turretMounts.Clear();
            turretSoundsWorld = host.session.World;
        }
        if (host.scene.Database is not { } db) return;
        mannedTurrets.Clear();
        var characters = host.session.CurrentSnapshot.Characters;
        foreach (var gunner in characters)
        {
            if (gunner.Turret is not { } post || Vector3.DistanceSquared(post.Position, eye) > TurretAudioRadius * TurretAudioRadius) continue;
            if (!turretMounts.TryGetValue(post.Key, out var made)) turretMounts[post.Key] = made = MountOf(db, post);
            if (made is not { } known) continue;
            mannedTurrets.Add(new TurretAim(post.Key, known.Mount, TargetOf(gunner, post.Position, known.Range, characters)));
        }
        // The turning is by game time: the frame time times the game speed (the exe's frame time is scaled by it; 0 while paused).
        sounds.Update(mannedTurrets, (float)(realSeconds * host.session.Simulation.Speed), eye);
    }

    /// <summary>
    /// The point the gunner aims at: the simulation's aim (<see cref="MannedTurret.Aim"/>, docs/game/combat.md "Turrets"); without one (a world without the combat system's turrets) the nearest
    /// living, free hostile character within the gun's range, 5 units above its place (<b>engine choice</b>). Null: none (the turret keeps its last aim).
    /// </summary>
    Vector3? TargetOf(CharacterSnapshot gunner, Vector3 turret, float range, IReadOnlyList<CharacterSnapshot> characters)
    {
        if (gunner.Turret is { Simulated: true } simulated) return simulated.Aim;
        if (host.popData is not { } data || !data.Relations.Contains(gunner.Faction)) return null;
        CharacterSnapshot? nearest = null;
        float best = range * range;
        foreach (var c in characters)
        {
            if (c.Id == gunner.Id || !data.Relations.Contains(c.Faction) || !data.Relations.IsHostile(gunner.Faction, c.Faction)) continue;
            if (c.Body is { Dead: true } or { Unconscious: true } || !c.CarriedBy.IsNone) continue;
            float d2 = Vector3.DistanceSquared(c.Position, turret);
            if (d2 > best) continue;
            best = d2;
            nearest = c;
        }
        return nearest is null ? null : nearest.Position + new Vector3(0, 5, 0);
    }

    /// <summary>
    /// The mount of a turret from its records (<see cref="TurretGun"/>): the building's <c>scale</c>, its rotating parts, the turning speed of an unharmed powered turret (<see cref="TurretFormulas.TurnRate"/>
    /// with efficiency 1) and the gun's <c>range</c>.
    /// </summary>
    static (TurretMount, float)? MountOf(GameDatabase db, MannedTurret post)
    {
        if (db.Find(post.BuildingId) is not { } building || TurretGun.From(db, building) is not { } gun) return null;
        return (new TurretMount(post.Position, post.Yaw, gun.Scale, TurretFormulas.TurnRate(1, gun.Gun.AimSpeed, gun.Damage), gun.Parts), gun.Gun.Range);
    }

    /// <summary>The main menu: the turrets' objects end.</summary>
    void ResetTurretAudio()
    {
        turretSounds?.Clear();
        turretMounts.Clear();
        turretSoundsWorld = null;
    }

    // <summary>
    // The sound emitters of effects (docs/audio.md "Point emitters"): every live unit of an effect whose EFFECT record has a <c>sound</c> (the sky beams) is a
    // <see cref="WorldEmitters"/> emitter that follows it and ends with it. Driven each frame from <see cref="UpdateWorldAudio"/>.
    // </summary>
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
        if (host.gpu is not null && host.gpu.Particles is { } particles)
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
    /// <summary>
    /// The GroundType of the building floor under a character's feet, -1 when the ground is not a building's (<see cref="Meitou.Game.Audio.CharacterSounds.Floor"/>, docs/audio.md "Surface"):
    /// the building's <c>interior material</c> when the character stands inside a building's indoor hull, else its <c>exterior material</c>.
    /// </summary>
    int FloorSurface(Vector3 feet) =>
        host.ActiveObjects is { } objects ? objects.FloorMaterialUnder(feet, objects.InteriorAt(feet, InteriorView.InsideMargin) is not null) : -1;

    // <summary>The interface's sounds (docs/audio.md "Interface"): <see cref="PlayerInterface.UiSound"/> goes to <see cref="UiSounds"/> on the GUI object while audio runs, also on the title screen.</summary>
    UiSounds? uiSounds;

    /// <summary>Called when the audio is attached: the interface starts sounding, or stays silent without audio.</summary>
    void AttachUiSounds()
    {
        if (host.player is null) return;
        uiSounds = audio is null ? null : new UiSounds(audio.Player, () => host.scene.Database);
        var sounds = uiSounds;
        host.player.UiSound = sounds is null ? null : e => sounds.Play(e);
    }

    /// <summary>One statistics line; empty without audio.</summary>
    internal string UiAudioStats() => uiSounds is null ? "" : $"{uiSounds.Posted} interface sounds";
}
