using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Combat;
using Meitou.Data.World;
using Meitou.Game.Host.WorldSync;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Movement;

namespace Meitou.Game.Audio;

/// <summary>
/// The characters' sounds (docs/audio.md "Characters"): every character near the camera is a Wwise game object kept at its place with the
/// animation events of its clips (footsteps, swings) fired from the snapshot, and the combat's happenings (<see cref="World.Sounds"/>) played as
/// impacts and voices. Driven each frame from <see cref="GameSound.UpdateAudio"/>; it only reads the snapshots and empties the sound queue, so the
/// simulation is not affected.
/// </summary>
sealed class CharacterAudio(GameHost host)
{
    #region Frame

    /// <summary>
    /// The audio the sound objects were made for; another one makes them again.
    /// </summary>
    AudioSystem? _characterAudioFor;

    /// <summary>
    /// The characters' sound objects; null before the first frame with a database and the animations.
    /// </summary>
    internal CharacterSounds? Sounds;

    /// <summary>
    /// The ground types of the terrain under the characters' feet.
    /// </summary>
    GroundSurfaces? _groundSurfaces;

    /// <summary>
    /// The ground lookups, once made.
    /// </summary>
    internal GroundSurfaces? Ground => _groundSurfaces;

    /// <summary>
    /// The animation events of the ANIMATION and functionality records.
    /// </summary>
    AnimationSoundEvents? _animationSounds;

    /// <summary>
    /// The biome and ground lookups the sounds use, shared with the ground effects.
    /// </summary>
    internal GroundSurfaces GroundSurfacesOf(GameDatabase db) => _groundSurfaces ??= new GroundSurfaces(host.Install, db);

    /// <summary>
    /// The animation sound events, shared with the ground effects.
    /// </summary>
    internal AnimationSoundEvents AnimationSoundsOf(GameDatabase db) => _animationSounds ??= AnimationSoundEvents.FromDatabase(db);

    /// <summary>
    /// Each clip definition's events.
    /// </summary>
    AnimationSoundEvent[]?[]? _definitionEvents;

    /// <summary>
    /// Each clip definition's unarmed weapon switch (<c>HtH_Punch</c> / <c>HtH_Kick</c>), null for the rest.
    /// </summary>
    string?[]? _definitionWeapons;

    /// <summary>
    /// Whether each clip definition loops.
    /// </summary>
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
    /// What each character did the frame before, by its sound key.
    /// </summary>
    readonly Dictionary<ulong, SoundMemo> _soundMemo = [];

    /// <summary>
    /// The frames counted, for which memos are stale.
    /// </summary>
    int _soundFrame;

    /// <summary>
    /// The world whose combat feeds <see cref="_soundQueue"/>.
    /// </summary>
    World? _soundsFor;

    /// <summary>
    /// The combat's happenings, filled by the simulation and emptied each frame.
    /// </summary>
    readonly ConcurrentQueue<SoundEvent> _soundQueue = new();

    /// <summary>
    /// Each appearance's sound profile.
    /// </summary>
    readonly ConditionalWeakTable<CharacterAppearance, CharacterSoundProfile> _soundProfiles = [];

    /// <summary>
    /// The sound layers of the character being updated (reused).
    /// </summary>
    readonly List<SoundLayer> _soundLayers = [];

    /// <summary>
    /// A clip (its definition) with the events of the functionalities worked on it added; <see cref="Events"/> is replaced only when another
    /// functionality joins.
    /// </summary>
    sealed class WorkLayer
    {
        /// <summary>
        /// The clip definition.
        /// </summary>
        public required int Definition;

        /// <summary>
        /// The clip's events with the functionalities' added.
        /// </summary>
        public required AnimationSoundEvent[] Events;

        /// <summary>
        /// The functionalities whose events were added.
        /// </summary>
        public readonly List<string> Functionalities = [];
    }

    /// <summary>
    /// What a character did the frame before, for the sounds that are edges (a loot started, a bed got into, a person lifted).
    /// </summary>
    struct SoundMemo
    {
        /// <summary>
        /// The shown task.
        /// </summary>
        public CharacterTask Task;

        /// <summary>
        /// Whether it lay in a bed, was carried, and was carried by a character putting it down.
        /// </summary>
        public bool InBed, Carried, PutDown;

        /// <summary>
        /// The frame the memo was written.
        /// </summary>
        public int Frame;
    }

    /// <summary>
    /// Once a frame: the snapshot's characters are handed to the sound objects and the queued combat sounds are played.
    /// </summary>
    internal void Update(AudioSystem sound, Vector3 eye)
    {
        if (!ReferenceEquals(_characterAudioFor, sound))
        {
            _characterAudioFor = sound;
            Sounds = null;
            _soundsFor = null;
        }

        if (host.Scene.Database is not { } db || host.AnimationLibrary is not { } library) return;
        if (Sounds is null) CreateSounds(sound, db, library);

        var sounds = Sounds!;
        var world = host.Session.World;
        if (!ReferenceEquals(_soundsFor, world)) FollowWorld(sounds, world);

        sounds.BeginFrame(eye);
        _soundFrame++;
        host.IndexPrevious();
        UpdateCharacters(sounds, db, world);
        sounds.EndFrame();
        ForgetStaleMemos();
        while (_soundQueue.TryDequeue(out var happened)) PlayCombatSound(sounds, happened);
    }

    /// <summary>
    /// The main menu or a new world: every character object ends.
    /// </summary>
    internal void Reset()
    {
        Sounds?.Clear();
        _soundQueue.Clear();
        if (_soundsFor is { } world) world.Sounds = null;
        _soundsFor = null;
    }

    /// <summary>
    /// One statistics line; empty without audio.
    /// </summary>
    internal string Stats() => Sounds?.StatsLine() ?? "";

    /// <summary>
    /// Makes the sound objects and each clip definition's events, unarmed weapon and loop.
    /// </summary>
    void CreateSounds(AudioSystem sound, GameDatabase db, AnimationLibrary library)
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
        Sounds = new CharacterSounds(sound.Player, _groundSurfaces) { Floor = FloorSurface };
    }

    /// <summary>
    /// A new world: the old one's objects go, and its combat feeds this host's queue.
    /// </summary>
    void FollowWorld(CharacterSounds sounds, World world)
    {
        sounds.Clear();
        _workLayers.Clear();
        _soundQueue.Clear();
        world.Sounds = _soundQueue;
        _soundsFor = world;
    }

    /// <summary>
    /// Hands every snapshot character with an appearance to its sound object at its drawn place, yaw and speed.
    /// </summary>
    void UpdateCharacters(CharacterSounds sounds, GameDatabase db, World world)
    {
        float alpha = host.Session.SimulationAlpha;
        float tick = world.TickSeconds;
        foreach (var c in host.Session.CurrentSnapshot.Characters)
        {
            if (c.Appearance is not { } look) continue;

            var position = c.Position;
            float yaw = c.Yaw, speed = 0;
            if (host.PreviousById.TryGetValue(c.Id, out var before))
            {
                position = Simulation.Session.Interp.Lerp(before.Position, c.Position, alpha);
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
    }

    /// <summary>
    /// Drops the memos and work layers of the characters not in this frame's snapshot.
    /// </summary>
    void ForgetStaleMemos()
    {
        if (_soundMemo.Count <= host.Session.CurrentSnapshot.Characters.Count) return;

        foreach (ulong stale in _soundMemo.Where(m => m.Value.Frame != _soundFrame).Select(m => m.Key).ToArray())
        {
            _soundMemo.Remove(stale);
            _workLayers.Remove(stale);
        }
    }

    /// <summary>
    /// A character's key for the sound objects: its slot and generation.
    /// </summary>
    static ulong SoundKey(CharacterId id) => (ulong)(uint)id.Slot << 32 | (uint)id.Generation;

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

    #endregion

    #region Clip events

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
    internal static float ClipFraction(AnimationLayer layer, bool loops)
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

    /// <summary>
    /// The work layer of the clip definition; null when there is none.
    /// </summary>
    static WorkLayer? Find(List<WorkLayer>? layers, int definition)
    {
        if (layers is null) return null;
        foreach (var layer in layers)
            if (layer.Definition == definition) return layer;
        return null;
    }

    /// <summary>
    /// Whether one of the character's layers plays the clip definition.
    /// </summary>
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

    #endregion

    #region Happenings

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

    /// <summary>
    /// Plays one of the combat's happenings: on the characters' objects, or as an interface sound.
    /// </summary>
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
                if (e.Item is not null) host.Sound.Ui.Sounds?.Play(new UiSoundEvent("Item_Drop", Item: e.Item));
                break;
            case SoundKind.BountyPaid:
                host.Sound.Ui.Sounds?.Play(new UiSoundEvent("Notifications", "Notifications", "Bounty_Fulfilled"));
                break;
            case SoundKind.DismantleDone:
                host.Sound.Ui.Sounds?.Play(new UiSoundEvent("Notifications", "Notifications", "Thing_Complete"));
                break;
        }
    }

    #endregion
}
