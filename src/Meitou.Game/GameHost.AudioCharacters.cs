using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using Meitou.Audio;
using Meitou.Data.Characters;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Combat;
using Meitou.Data.Fcs;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The characters' sounds (docs/audio.md "Characters"): every character near the camera is a Wwise game object kept at its place with the animation events of its clips
/// (footsteps, swings) fired from the snapshot, and the combat's happenings (<see cref="World.Sounds"/>) played as impacts and voices. Driven each frame from <see cref="UpdateAudio"/>;
/// it only reads the snapshots and empties the sound queue, so the simulation is not affected.
/// </summary>
sealed partial class GameHost
{
    AudioSystem? characterAudioFor;
    CharacterSounds? characterSounds;
    GroundSurfaces? groundSurfaces;
    AnimationSoundEvents? animationSounds;
    AnimationSoundEvent[]?[]? definitionEvents;
    string?[]? definitionWeapons;
    bool[]? definitionLoops;
    /// <summary>A clip's events with a functionality's added, by functionality and clip definition (one array each, so a cursor keeps its place).</summary>
    readonly Dictionary<(string Functionality, int Definition), AnimationSoundEvent[]?> workEvents = [];
    /// <summary>Each working character's clip that carries its machine's events, kept while that clip plays (the exe adds them to the clip's instance, which lives until it is gone).</summary>
    readonly Dictionary<ulong, (int Definition, AnimationSoundEvent[] Events)> workLayers = [];
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
        if (scene.Database is not { } db || animationLibrary is not { } library) return;
        if (characterSounds is null)
        {
            animationSounds ??= AnimationSoundEvents.FromDatabase(db);
            groundSurfaces ??= new GroundSurfaces(install, db);
            var events = new AnimationSoundEvent[]?[library.Definitions.Count];
            for (int i = 0; i < events.Length; i++) events[i] = animationSounds.Of(library.Definitions[i].Id);
            definitionEvents = events;
            var weapons = new string?[events.Length];
            var techniques = db.OfType(FcsRecordType.COMBAT_TECHNIQUE).ToDictionary(r => r.StringId, CombatTechnique.From, StringComparer.Ordinal);
            for (int i = 0; i < weapons.Length; i++)
                if (techniques.TryGetValue(library.Definitions[i].Id, out var t) && t is { Kinds: WeaponKinds.Unarmed, IsBlock: false, IsDodge: false, Animal: 0 })
                    weapons[i] = (t.Blows >= 2 ? t.Limb2 : t.Limb1) is 0 or 1 ? "HtH_Punch" : "HtH_Kick";   // the last blow's limb: the arms are 0 and 1 (FUN_1402b4570)
            definitionWeapons = weapons;
            definitionLoops = [.. library.Definitions.Select(d => d.Loop)];
            workEvents.Clear();
            characterSounds = new CharacterSounds(sound.Player, groundSurfaces) { Floor = FloorSurface };
        }
        var sounds = characterSounds;
        var world = session.World;
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
        IndexPrevious();
        float alpha = session.SimulationAlpha;
        float tick = world.TickSeconds;
        foreach (var c in session.CurrentSnapshot.Characters)
        {
            if (c.Appearance is not { } look) continue;
            var position = c.Position;
            float yaw = c.Yaw, speed = 0;
            if (previousById.TryGetValue(c.Id, out var before))
            {
                position = Vector3.Lerp(before.Position, c.Position, alpha);
                yaw = Meitou.Engine.Time.Interp.LerpAngle(before.Yaw, c.Yaw, alpha);
                speed = new Vector2(c.Position.X - before.Position.X, c.Position.Z - before.Position.Z).Length() / tick;
            }
            position += CarriedOffset(c, yaw);
            if (!soundProfiles.TryGetValue(look, out var profile))
                soundProfiles.Add(look, profile = CharacterSoundProfile.From(look, db));
            ulong key = SoundKey(c.Id);
            FillSoundLayers(c, key);
            var loop = c.Task == CharacterTask.Build ? CharacterLoop.Build : c.Task == CharacterTask.Treat ? CharacterLoop.Medic : (c.PickingLock || c.WorkingShackles) && c.Task != CharacterTask.GoTo ? CharacterLoop.Unlock : CharacterLoop.None;
            sounds.Update(key, profile, position, yaw, speed, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(soundLayers), loop);
            JobSounds(sounds, key, c);
        }
        sounds.EndFrame();
        if (soundMemo.Count > session.CurrentSnapshot.Characters.Count)
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
        var work = WorkLayer(c, key);
        for (int k = 0; k < c.Animations.Count; k++)   // indexed: a foreach over the interface boxes its enumerator, per character per frame
        {
            var layer = c.Animations[k];
            bool known = (uint)layer.Definition < (uint)definitionEvents!.Length;
            var events = !known ? null : layer.Definition == work.Definition ? work.Events : definitionEvents[layer.Definition];
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
    /// The clip a working character's machine puts its <c>animation events</c> on and the clip's events with those added (<c>Task_OperateMachine</c> and <c>Task_UseTrainingDummy</c> add
    /// them to the playing clip each update, <c>FUN_140065280</c>; docs/audio.md "Character tasks"), kept until that clip stops playing; (-1, null) for none.
    /// </summary>
    (int Definition, AnimationSoundEvent[]? Events) WorkLayer(CharacterSnapshot c, ulong key)
    {
        if (c.WorkEvents is { } work && LayerOf(c, work.Clip) is var definition && WorkEventsOf(work.Functionality, definition) is { } events)
            workLayers[key] = (definition, events);
        if (!workLayers.TryGetValue(key, out var kept)) return (-1, null);
        for (int k = 0; k < c.Animations.Count; k++)
            if (c.Animations[k].Definition == kept.Definition) return kept;
        workLayers.Remove(key);
        return (-1, null);
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
        var all = session.CurrentSnapshot.Characters;
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
            case SoundKind.WallBroken: sounds.WallBreak(e.Position, config.FixDoorBreak); break;
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
    string CharacterAudioStats() => characterSounds?.StatsLine() ?? "";
}
