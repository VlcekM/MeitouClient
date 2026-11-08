using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data.Audio;

namespace Meitou.Audio;

/// <summary>
/// Plays Wwise banks the way the game's events ask (docs/audio.md): a good-enough interpreter of the bank hierarchy, not a copy of Wwise.
/// Events run their actions (play, stop, set state, volume offsets, with delays and fades); played nodes become instances (sounds, random/
/// sequence, layer and switch containers) whose volume, pitch and low-pass add up along the node's parents (properties, random ranges and
/// RTPC curves of game parameters); 3D sounds are placed by their game object (WwisePlayer.Objects.cs) with the attenuation's distance curves and
/// stereo panning, the random one-shots of the ambience move along paths around the listener, sends feed the reverb of the auxiliary buses
/// (WwisePlayer.Buses.cs), and actions fade bus volumes (the pause ducking). Music is in WwisePlayer.Music.cs.
/// <para>
/// Thread use: <see cref="Post(string)"/>, <see cref="SetParameter(string, float)"/>, <see cref="SetState(string, string)"/> and
/// <see cref="LoadBank"/> may be called from any thread; they queue commands that <see cref="Mix"/> (the audio thread) runs first.
/// </para>
/// </summary>
public sealed partial class WwisePlayer : IDisposable
{
    readonly WwiseCodebookLibrary library;
    readonly string bankDirectory;
    readonly int outputRate;
    readonly ConcurrentQueue<Action> commands = new();
    readonly ConcurrentDictionary<string, Task> loading = new(StringComparer.OrdinalIgnoreCase);
    readonly Random random = new();

    // Owned by the audio thread.
    readonly Dictionary<uint, WwiseObject> objects = [];
    readonly Dictionary<uint, MediaClip> media = [];
    readonly PcmCache pcm = new();
    readonly Dictionary<uint, float> parameters = [];
    readonly Dictionary<uint, uint> states = [];
    readonly Dictionary<uint, List<uint>> history = [];
    readonly List<Instance> roots = [];
    readonly List<SoundInstance> sounds = [];
    readonly List<(double At, uint EventId, ulong Object, double Expires)> pending = [];
    readonly List<(double At, WwiseAction Action, ulong Object)> delayed = [];
    readonly Dictionary<string, (WwiseObject[] Objects, uint[] Media)> bankContents = new(StringComparer.OrdinalIgnoreCase);
    // The block being mixed covers [blockStart, time); runTime is when the action being run takes effect (music starts at that point).
    double time, blockStart, runTime;

    /// <summary>Overall gain, linear (the ambience slider's).</summary>
    public float Gain { get; set; } = 1;

    /// <summary>Instances and voices last mixed, banks loaded (for the overlay).</summary>
    public (int Instances, int Voices, int Banks, int Clips) Stats { get; private set; }

    /// <summary>The voices of music clips last mixed (part of <see cref="Stats"/>' voices).</summary>
    public int MusicVoices { get; private set; }

    /// <summary>
    /// Meitou's voice cap (docs/audio.md "Voice cap", the Audio settings' Sound voices slider, <see cref="AudioSettings.VoiceCap"/>): at most this many sounds besides the music are mixed in a block, the loudest at the listener; the others run on
    /// silently (no decoding) and come back in place when they are among the loudest again. 0: every sound is mixed. Set from any thread.
    /// </summary>
    public int VoiceCap { get; set; } = AudioSettings.MeitouVoices;

    /// <summary>
    /// Meitou's hearing range (docs/audio.md "Hearing range", the Audio settings' Sound range slider): every attenuation curve and every game-side distance limit is read at the
    /// distance divided by this, so 2 hears everything twice as far. 1 is the original. Set from any thread.
    /// </summary>
    public float Reach { get; set; } = 1;

    /// <summary>The squared distance from <paramref name="eye"/> as the game's distance limits see it under <see cref="Reach"/>.</summary>
    public float HeardDistanceSquared(Vector3 at, Vector3 eye) => Vector3.DistanceSquared(at, eye) / (Reach * Reach);

    /// <summary>The sounds the cap left silent in the last block.</summary>
    public int Silenced { get; private set; }

    /// <summary>How much louder a sound mixed in the last block counts for the cap (a factor on its level).</summary>
    const float CapHysteresis = 1.5f;

    float[] loudness = new float[64];
    int[] order = new int[64];
    readonly HashSet<int> silenced = [];

    /// <summary>True once the bank has been read and handed to the audio thread (and not unloaded since).</summary>
    public bool IsBankLoaded(string name) => loadedBanks.ContainsKey(Normalize(name));

    readonly ConcurrentDictionary<string, bool> loadedBanks = new(StringComparer.OrdinalIgnoreCase);

    static string Normalize(string name) => name.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    /// <summary>
    /// <paramref name="bankDirectory"/>: the install's <c>data/audio</c>. Reads <c>Init.bnk</c> at once for the game parameter defaults and
    /// state transition times.
    /// </summary>
    public WwisePlayer(WwiseCodebookLibrary library, string bankDirectory, int outputRate)
    {
        this.library = library;
        this.bankDirectory = bankDirectory;
        this.outputRate = outputRate;
        var init = Path.Combine(bankDirectory, "Init.bnk");
        if (File.Exists(init))
        {
            var bank = WwiseBank.ReadFile(init);
            foreach (var (id, value) in bank.GameParameterDefaults) parameters[id] = value;
            foreach (var o in bank.Objects) objects.TryAdd(o.Id, o);
        }
    }

    /// <summary>
    /// Reads a bank and converts its WEMs on a worker thread, then hands the objects and clips to the audio thread. Loading a bank that is
    /// loaded or loading returns the same task.
    /// </summary>
    public Task LoadBank(string name) => loading.GetOrAdd(Normalize(name), n => Task.Run(() =>
    {
        var bank = WwiseBank.ReadFile(Path.Combine(bankDirectory, n + ".bnk"));
        var clips = new ConcurrentBag<MediaClip>();
        Parallel.ForEach(bank.Media, m =>
        {
            var wem = WwiseVorbis.Read(bank.MediaBytes(m));
            clips.Add(new MediaClip(m.Id, wem.ToOgg(library), wem.Channels, wem.SampleRate, wem.SampleCount));
        });
        commands.Enqueue(() =>
        {
            foreach (var o in bank.Objects) objects.TryAdd(o.Id, o);
            foreach (var c in clips) media.TryAdd(c.Id, c);
            bankContents[n] = ([.. bank.Objects], [.. clips.Select(c => c.Id)]);
            loadedBanks[n] = true;
            RetryPending();
        });
    }));

    /// <summary>
    /// Unloads a loaded bank (nothing for one that is not loaded or still loading): what is playing from it is cut, its recordings are
    /// dropped and a later <see cref="LoadBank"/> reads it again. Objects of the same id in another loaded bank (the music banks' shared
    /// upper switches) stay in use.
    /// </summary>
    public void UnloadBank(string name)
    {
        var n = Normalize(name);
        // At once, so that a load or a check right after sees the bank gone; the audio thread then drops what it holds, before any new load adds.
        if (!loadedBanks.TryRemove(n, out _)) return;
        loading.TryRemove(n, out _);
        commands.Enqueue(() =>
        {
            if (!bankContents.Remove(n, out var contents)) return;
            var own = new HashSet<WwiseObject>(contents.Objects, ReferenceEqualityComparer.Instance);
            foreach (var r in roots)
                if (own.Contains(r.Node)) r.End();
            foreach (var o in contents.Objects)
                if (objects.TryGetValue(o.Id, out var held) && own.Contains(held)) objects.Remove(o.Id);
            foreach (var other in bankContents.Values)
                foreach (var o in other.Objects) objects.TryAdd(o.Id, o);
            foreach (var id in contents.Media) media.Remove(id);
        });
    }

    /// <summary>Posts an event on the default game object (0); see the overloads with a game object.</summary>
    public void Post(string eventName) => Post(WwiseHash.Id(eventName), 0);
    public void Post(uint eventId) => Post(eventId, 0);
    public void SetParameter(string name, float value) => SetParameter(WwiseHash.Id(name), value);
    public void SetParameter(uint id, float value) => commands.Enqueue(() => parameters[id] = value);
    public void SetState(string group, string state) => SetState(WwiseHash.Id(group), WwiseHash.Id(state));
    public void SetState(uint group, uint state) => commands.Enqueue(() => ChangeState(group, state));

    /// <summary>Sets a switch group (<c>Biomes</c>, <c>General</c>): switches and states share the ids of their groups, so it is a state change.</summary>
    public void SetSwitch(string group, string value) => SetState(group, value);

    /// <summary>Called by the output: runs the queued commands, advances every instance and adds the voices to <paramref name="mix"/>.</summary>
    public void Mix(float[] mix, int frames)
    {
        blockStart = runTime = time;
        while (commands.TryDequeue(out var command)) command();
        double dt = (double)frames / outputRate;
        time += dt;
        volumeOffsets.Advance(dt);
        pitchOffsets.Advance(dt);
        lowPassOffsets.Advance(dt);
        busVolumes.Advance(dt);
        for (int i = delayed.Count - 1; i >= 0; i--)
            if (delayed[i].At <= time)
            {
                var (_, action, gameObject) = delayed[i];
                runTime = Math.Max(delayed[i].At, blockStart);
                delayed.RemoveAt(i);
                Run(action, gameObject);
            }
        runTime = blockStart;
        for (int i = roots.Count - 1; i >= 0; i--)
        {
            roots[i].Advance(dt);
            if (roots[i].Done) roots.RemoveAt(i);
        }
        int voices = 0, music = 0;
        for (int i = sounds.Count - 1; i >= 0; i--)
            if (sounds[i].Done)
            {
                sounds[i].Dispose();
                sounds.RemoveAt(i);
            }
        // The voice cap (Meitou's rule, docs/audio.md "Voice cap"): past VoiceCap sounds, only the loudest at the listener are mixed; the rest keep their place silently.
        int cap = VoiceCap, cappable = 0;
        if (loudness.Length < sounds.Count) (loudness, order) = (new float[sounds.Count * 2], new int[sounds.Count * 2]);
        for (int i = 0; i < sounds.Count; i++)
        {
            // A sound heard last block counts half as loud again: one at the cap's edge must get clearly quieter to give way, not flip every block.
            float heard = sounds[i].Prepare(Gain) * (sounds[i].Heard ? CapHysteresis : 1);
            if (cap > 0 && sounds[i].Cappable) { loudness[cappable] = -heard; order[cappable++] = i; }
        }
        if (cap > 0 && cappable > cap)
        {
            Array.Sort(loudness, order, 0, cappable);
            for (int k = cap; k < cappable; k++) silenced.Add(order[k]);
        }
        for (int i = 0; i < sounds.Count; i++)
        {
            var s = sounds[i];
            if (silenced.Contains(i))
            {
                // Heard last block: one more block fading to silence (the voice's gain ramp), then silent.
                if (s.Heard) s.FadeOut(mix, frames);
                else s.Skip(frames);
                s.Heard = false;
                continue;
            }
            s.Mix(mix, frames);
            s.Heard = true;
            voices++;
            if (s.IsMusic) music++;
        }
        Silenced = silenced.Count;
        silenced.Clear();
        MixAuxBuses(mix, frames);
        Stats = (roots.Count, voices, loading.Count, media.Count);
        MusicVoices = music;
    }

    void RetryPending()
    {
        var waiting = pending.ToList();
        pending.Clear();
        foreach (var p in waiting) PostNow(p.EventId, p.Object, p.Expires);
    }

    void PostNow(uint eventId, ulong gameObject, double expires)
    {
        if (!objects.TryGetValue(eventId, out var o) || o is not WwiseEvent e)
        {
            // Its bank is still loading: try again when a bank arrives, for a while.
            if (time < expires) pending.Add((time, eventId, gameObject, expires));
            return;
        }
        foreach (var actionId in e.Actions)
        {
            if (!objects.TryGetValue(actionId, out var a) || a is not WwiseAction action) continue;
            int delay = action.Properties.Int(WwisePropertyId.Delay);
            if (delay > 0) delayed.Add((time + delay / 1000.0, action, gameObject));
            else Run(action, gameObject);
        }
    }

    /// <summary>
    /// Whether an action is limited to the game object the event was posted on. The scope (the action type's low byte) follows the Wwise action
    /// numbering: 2 is global (the "master" forms), 3 is the posting object only (the "object" forms; Observed: <c>Stop_Emitter</c> uses it, a global
    /// stop would silence every emitter), 5 and 7 are the object forms of "all" and "all except"; 4 and 6 are global. Play always plays on the object.
    /// </summary>
    static bool ObjectScoped(WwiseAction action) => action.Scope is 3 or 5 or 7;

    void Run(WwiseAction action, ulong gameObject)
    {
        float fade = action.Properties.Int(WwisePropertyId.FadeTime) / 1000f;
        ulong scopeObject = ObjectScoped(action) ? gameObject : 0;
        switch (action.Kind)
        {
            case WwiseActionKind.Play:
                postingObject = gameObject;
                try
                {
                    if (objects.TryGetValue(action.Target, out var node) && Create(node, null) is { } instance)
                    {
                        instance.FadeIn(fade);
                        roots.Add(instance);
                        instance.Start();
                    }
                }
                finally { postingObject = 0; }
                break;
            case WwiseActionKind.Stop:
                foreach (var r in roots) r.StopMatching(action.Target, fade, ObjectScoped(action) ? gameObject : null);
                break;
            case WwiseActionKind.SetState:
                ChangeState(action.GroupId, action.StateId);
                break;
            case WwiseActionKind.SetVolume: volumeOffsets.Set(action.Target, scopeObject, action.Value, fade); break;
            case WwiseActionKind.ResetVolume: volumeOffsets.Reset(action.Target, scopeObject, fade); break;
            case WwiseActionKind.SetPitch: pitchOffsets.Set(action.Target, scopeObject, action.Value, fade); break;
            case WwiseActionKind.ResetPitch: pitchOffsets.Reset(action.Target, scopeObject, fade); break;
            case WwiseActionKind.SetLowPass: lowPassOffsets.Set(action.Target, scopeObject, action.Value, fade); break;
            case WwiseActionKind.ResetLowPass: lowPassOffsets.Reset(action.Target, scopeObject, fade); break;
            case WwiseActionKind.SetBusVolume: busVolumes.Set(action.Target, scopeObject, action.Value, fade); break;
            case WwiseActionKind.ResetBusVolume: busVolumes.Reset(action.Target, scopeObject, fade); break;
            case WwiseActionKind.BypassFx:
                if (action.TargetIsBus) ApplyBypass(action.Target, action.Bypass, action.BypassMask);
                break;
        }
    }

    void ChangeState(uint group, uint state)
    {
        if (states.TryGetValue(group, out var old) && old == state) return;
        states[group] = state;
        foreach (var r in roots) r.StateChanged(group);
    }

    Instance? Create(WwiseObject node, Instance? parent) => node switch
    {
        WwiseSound s => new SoundInstance(this, s, parent),
        WwiseRandomSequence r => new RandomSequenceInstance(this, r, parent),
        WwiseLayerContainer l => new GroupInstance(this, l, l.Children, parent),
        WwiseActorMixer m => new GroupInstance(this, m, m.Children, parent),
        WwiseSwitchContainer w => new SwitchInstance(this, w, parent),
        WwiseMusicSwitch ms => new MusicSwitchInstance(this, ms, parent),
        WwiseMusicPlaylist mp => new MusicPlaylistInstance(this, mp, parent),
        WwiseMusicSegment seg => new MusicSegmentInstance(this, seg, parent),
        _ => null,
    };

    static WwiseNodeBase? NodeOf(WwiseObject o) => o switch
    {
        WwiseSound s => s.Node,
        WwiseActorMixer m => m.Node,
        WwiseRandomSequence r => r.Node,
        WwiseSwitchContainer s => s.Node,
        WwiseLayerContainer l => l.Node,
        WwiseMusicTrack t => t.Node,
        WwiseMusicSegment s => s.Music.Node,
        WwiseMusicSwitch s => s.Music.Node,
        WwiseMusicPlaylist p => p.Music.Node,
        _ => null,
    };

    /// <summary>The node and its parents up the hierarchy (as far as the loaded banks reach).</summary>
    IEnumerable<WwiseObject> Chain(WwiseObject node)
    {
        for (var o = node; o is not null;)
        {
            yield return o;
            uint parent = NodeOf(o)?.ParentId ?? 0;
            o = parent != 0 && objects.TryGetValue(parent, out var p) ? p : null;
        }
    }

    float Parameter(uint id) => parameters.TryGetValue(id, out var v) ? v : 0;

    public void Dispose()
    {
        foreach (var s in sounds) s.Dispose();
        sounds.Clear();
    }

    /// <summary>A played node. Gains multiply down the instance tree (fades); a stopped instance fades out and ends.</summary>
    abstract class Instance(WwisePlayer player, WwiseObject node, Instance? parent)
    {
        protected readonly WwisePlayer Player = player;
        public readonly WwiseObject Node = node;
        public readonly Instance? Parent = parent;

        /// <summary>The game object this plays on: the one the event was posted on (inherited from the parent).</summary>
        public readonly ulong GameObject = parent?.GameObject ?? player.postingObject;
        protected readonly List<Instance> Children = [];
        float fade = 1, fadeTarget = 1, fadeRate;
        bool stopping;

        public bool Done { get; protected set; }

        /// <summary>When this instance starts (player time, seconds): the point in the block being mixed that music aligns to.</summary>
        public double StartTime = player.runTime;

        protected bool Stopping => stopping;

        /// <summary>This instance's fade times its parents'.</summary>
        public float Fade => fade * (Parent?.Fade ?? 1);

        public void FadeIn(float seconds)
        {
            if (seconds <= 0) return;
            fade = 0;
            fadeTarget = 1;
            fadeRate = 1 / seconds;
        }

        public abstract void Start();

        public virtual void Advance(double dt)
        {
            if (fade != fadeTarget)
            {
                float step = fadeRate <= 0 ? 1 : (float)(fadeRate * dt);
                fade = fade < fadeTarget ? Math.Min(fadeTarget, fade + step) : Math.Max(fadeTarget, fade - step);
            }
            if (stopping && fade <= 0)
            {
                End();
                return;
            }
            for (int i = Children.Count - 1; i >= 0; i--)
            {
                Children[i].Advance(dt);
                if (Children[i].Done)
                {
                    var child = Children[i];
                    Children.RemoveAt(i);
                    ChildEnded(child);
                }
            }
        }

        protected virtual void ChildEnded(Instance child)
        {
            if (Children.Count == 0) End();
        }

        public virtual void End()
        {
            if (Done) return;
            Done = true;
            foreach (var c in Children) c.End();
            Children.Clear();
        }

        public void Stop(float seconds)
        {
            if (seconds <= 0)
            {
                End();
                return;
            }
            stopping = true;
            fadeTarget = 0;
            fadeRate = Math.Max(fadeRate, fade / seconds);
        }

        /// <summary>
        /// Stops this instance if its node or a parent node is <paramref name="target"/>, otherwise looks among the children; with
        /// <paramref name="onlyObject"/> only instances playing on that game object.
        /// </summary>
        public void StopMatching(uint target, float seconds, ulong? onlyObject = null)
        {
            if (Done || onlyObject is { } only && only != GameObject) return;
            if (Player.Chain(Node).Any(n => n.Id == target))
            {
                Stop(seconds);
                return;
            }
            foreach (var c in Children) c.StopMatching(target, seconds, onlyObject);
        }

        public virtual void StateChanged(uint group)
        {
            foreach (var c in Children) c.StateChanged(group);
        }

        protected void Play(uint childId)
        {
            if (Player.objects.TryGetValue(childId, out var o) && Player.Create(o, this) is { } child)
            {
                Children.Add(child);
                child.Start();
            }
        }
    }

    /// <summary>Plays all children at once (layer containers; an actor-mixer played directly).</summary>
    sealed class GroupInstance(WwisePlayer player, WwiseObject node, uint[] children, Instance? parent) : Instance(player, node, parent)
    {
        public override void Start()
        {
            foreach (var c in children) Play(c);
            if (Children.Count == 0) End();
        }
    }

    /// <summary>
    /// A random or sequence container. Continuous (flags bit 3) plays one child after another until the loop count runs out (0 = forever);
    /// otherwise (step) plays one child per play. Random picks by weight, skipping the last <c>avoid repeat</c> picks.
    /// </summary>
    sealed class RandomSequenceInstance(WwisePlayer player, WwiseRandomSequence node, Instance? parent) : Instance(player, node, parent)
    {
        int index, played;
        double wait = -1;

        bool Continuous => (node.Flags & 0x08) != 0;

        public override void Start() => Next();

        void Next()
        {
            var list = node.Playlist.Length > 0 ? node.Playlist : node.Children.Select(c => (c, 50000)).ToArray();
            if (list.Length == 0)
            {
                End();
                return;
            }
            int total = Continuous && node.LoopCount > 0 ? node.LoopCount * list.Length : 1;
            if (Continuous ? node.LoopCount > 0 && played >= total : played >= 1)
            {
                End();
                return;
            }
            played++;
            uint pick;
            if (node.Mode == 1) pick = list[index++ % list.Length].Item1;
            else
            {
                if (!Player.history.TryGetValue(node.Id, out var recent)) Player.history[node.Id] = recent = [];
                var allowed = list.Where(e => !recent.Contains(e.Item1)).ToArray();
                if (allowed.Length == 0) allowed = list;
                long sum = allowed.Sum(e => (long)Math.Max(1, e.Item2));
                long roll = (long)(Player.random.NextDouble() * sum);
                pick = allowed[^1].Item1;
                foreach (var e in allowed)
                {
                    roll -= Math.Max(1, e.Item2);
                    if (roll < 0)
                    {
                        pick = e.Item1;
                        break;
                    }
                }
                recent.Add(pick);
                while (recent.Count > Math.Min(node.AvoidRepeatCount, list.Length - 1)) recent.RemoveAt(0);
            }
            Play(pick);
            if (Children.Count == 0 && !Done) wait = 0.1; // a child that cannot play (its media is in a bank not loaded): try the next soon
        }

        public override void Advance(double dt)
        {
            base.Advance(dt);
            if (Done || wait < 0) return;
            wait -= dt;
            if (wait < 0) Next();
        }

        protected override void ChildEnded(Instance child)
        {
            if (Done || Children.Count > 0) return;
            if (!Continuous)
            {
                End();
                return;
            }
            // Transition mode 3 waits the transition time between children; the others (0 none, 1/2 crossfades) start the next at once.
            if (node.TransitionMode == 3) wait = node.TransitionTime / 1000.0;
            else Next();
        }
    }

    /// <summary>A switch container on a state group: plays the children of the current state, swapping them (with the fades) when it changes.</summary>
    sealed class SwitchInstance(WwisePlayer player, WwiseSwitchContainer node, Instance? parent) : Instance(player, node, parent)
    {
        uint current;

        public override void Start()
        {
            Switch();
            if (!node.ContinuousValidation && Children.Count == 0) End();
        }

        void Switch()
        {
            current = Player.TryGetGroupValue(node.GroupId, GameObject, out var s) ? s : node.DefaultSwitch;
            var list = node.Switches.FirstOrDefault(w => w.SwitchId == current).Nodes ?? [];
            foreach (var c in list)
            {
                Play(c);
                var fadeIn = node.NodeParameters.FirstOrDefault(p => p.NodeId == c).FadeIn;
                if (fadeIn > 0 && Children.Count > 0) Children[^1].FadeIn(fadeIn / 1000f);
            }
        }

        public override void StateChanged(uint group)
        {
            if (Done || Stopping) return;
            base.StateChanged(group);
            if (!node.ContinuousValidation || group != node.GroupId) return;
            uint state = Player.TryGetGroupValue(group, GameObject, out var s) ? s : node.DefaultSwitch;
            if (state == current) return;
            foreach (var c in Children) c.Stop(Math.Max(0, node.NodeParameters.FirstOrDefault(p => p.NodeId == c.Node.Id).FadeOut) / 1000f);
            Switch();
        }

        // Continuous switches wait for future states; step switches finish with their last child.
        protected override void ChildEnded(Instance child)
        {
            if (!node.ContinuousValidation) base.ChildEnded(child);
        }
    }

    /// <summary>
    /// A sound: one voice, its gains from the node chain, its place (on a 3D path when a parent positions it so, else at its game object with the
    /// attenuation's curves) and its sends to auxiliary buses. Also a clip of a music track (<see cref="IsMusic"/>), which starts at
    /// <see cref="Instance.StartTime"/>, inside a block when that falls in it.
    /// </summary>
    sealed class SoundInstance : Instance, IDisposable
    {
        readonly Voice? voice;
        readonly WwiseObject[] chain;
        readonly float rolledVolume, rolledPitch, rolledLowPass;
        readonly WwisePositioning? path;
        readonly int pathIndex;
        // 3D by the game object's place (no paths): the attenuation, if the positioning names one.
        readonly bool placed3D;
        readonly WwiseAttenuation? attenuation;
        readonly GameObjectState? objectState;
        readonly uint outputBus;
        readonly AuxBusState[] sends = [];
        readonly VoiceSend[] targets = new VoiceSend[Voice.MaxSends];
        double age;
        double delayFrames;

        public bool IsMusic { get; }

        public SoundInstance(WwisePlayer player, WwiseSound sound, Instance? parent) : this(player, sound, parent, sound.Source,
            sound.Node.Properties.Values.ContainsKey(WwisePropertyId.Loop) ? sound.Node.Properties.Int(WwisePropertyId.Loop) : 1, null, 0)
        {
        }

        /// <summary>A clip of <paramref name="track"/> beginning at <paramref name="startTime"/> (player seconds).</summary>
        public SoundInstance(WwisePlayer player, WwiseMusicTrack track, WwiseMusicClip clip, WwiseSource source, double startTime, Instance parent)
            : this(player, track, parent, source, 1, clip, startTime)
        {
        }

        SoundInstance(WwisePlayer player, WwiseObject node, Instance? parent, WwiseSource source, int plays, WwiseMusicClip? musicClip, double startTime)
            : base(player, node, parent)
        {
            IsMusic = musicClip is not null;
            if (musicClip is not null) StartTime = startTime;
            chain = [.. player.Chain(node)];
            outputBus = chain.Select(n => NodeOf(n)!.OverrideBusId).FirstOrDefault(b => b != 0);
            foreach (var n in chain)
            {
                var props = NodeOf(n)!.Properties;
                rolledVolume += Roll(props, WwisePropertyId.Volume);
                rolledPitch += Roll(props, WwisePropertyId.Pitch);
                rolledLowPass += Roll(props, WwisePropertyId.LowPass);
            }
            var positioning = chain.Select(n => NodeOf(n)!.Positioning).FirstOrDefault(p => p.OverridesParent);
            if (positioning is { HasPaths: true })
            {
                path = positioning;
                pathIndex = player.random.Next(positioning.Paths.Length);
            }
            else if (positioning is { IsGameDefined: true })
            {
                placed3D = true;
                if (positioning.AttenuationId != 0 && player.objects.TryGetValue(positioning.AttenuationId, out var a)) attenuation = a as WwiseAttenuation;
                player.gameObjects.TryGetValue(GameObject, out objectState);
            }
            // The sends: the nearest node that sets user-defined sends (bit 2 overrides, bit 3 lists the buses) decides; a node that overrides with no buses sends nowhere.
            var sender = chain.Select(n => NodeOf(n)!).FirstOrDefault(n => (n.AuxBits & 0x0C) != 0);
            if (sender is not null)
                sends = [.. sender.AuxBuses.Where(b => b != 0).Take(Voice.MaxSends).Select(b => player.AuxBus(b)).OfType<AuxBusState>()];
            if (source.IsVorbis && player.media.TryGetValue(source.SourceId, out var clip))
            {
                // Plays: 0 = forever, n = n times; absent = once. A music clip plays from its begin trim to its end trim.
                long skip = 0, limit = -1;
                double phase = 0;
                if (musicClip is not null)
                {
                    skip = (long)Math.Round(Math.Max(0, musicClip.BeginTrim) / 1000 * clip.SampleRate);
                    if (musicClip.BeginTrim > 0.5 || musicClip.EndTrim < -0.5)
                        limit = (long)Math.Round(Math.Max(0, musicClip.SourceDuration + musicClip.EndTrim - musicClip.BeginTrim) / 1000 * clip.SampleRate);
                    // Starts at the first output frame at or after the start time; the clip's position there is the part of a frame the start is early.
                    double at = Math.Max(0, (StartTime - player.blockStart) * player.outputRate);
                    delayFrames = Math.Ceiling(at - 1e-6);
                    phase = Math.Max(0, delayFrames - at) * clip.SampleRate / player.outputRate;
                }
                // A sound plays from the shared decoded samples (PcmCache) when its clip is short; a music clip (trimmed, long) streams.
                voice = new Voice(clip, plays, skip, limit, phase, musicClip is null ? player.pcm.Get(clip) : null);
            }
        }

        float Roll((IReadOnlyDictionary<byte, uint> Values, IReadOnlyDictionary<byte, (float Min, float Max)> Ranges) props, byte id) =>
            props.Ranges.TryGetValue(id, out var r) ? r.Min + (float)Player.random.NextDouble() * (r.Max - r.Min) : 0;

        float Roll(WwiseProperties props, byte id) => Roll((props.Values, props.Ranges), id);

        public override void Start()
        {
            if (voice is null) End();
            else Player.sounds.Add(this);
        }

        public override void Advance(double dt)
        {
            base.Advance(dt);
            age += dt;
            if (voice is { Ended: true }) End();
        }

        // This block's gains, from Prepare: the level, the dry and send attenuations, the pan, the low-pass coefficient and the resampling rate.
        float level, dry, send, panLeft, panRight, lowPassCoefficient;
        double rate;

        /// <summary>Whether the player may leave this sound silent for a block (<see cref="VoiceCap"/>): any but a music clip, while its voice can keep its place.</summary>
        public bool Cappable => !IsMusic && voice is { CanSkip: true };

        /// <summary>The frames of this block before the sound starts (a music clip starting inside it), taken off the pending delay.</summary>
        bool Start(int frames, out int first)
        {
            first = 0;
            if (delayFrames <= 0) return true;
            if (delayFrames >= frames)
            {
                delayFrames -= frames;
                return false;
            }
            first = (int)delayFrames;
            delayFrames = 0;
            return true;
        }

        /// <summary>Mixes the block with the gains <see cref="Prepare"/> worked out.</summary>
        public void Mix(float[] mix, int frames)
        {
            if (voice is null || Done || !Start(frames, out int first)) return;
            for (int i = 0; i < sends.Length; i++)
                targets[i] = new VoiceSend(sends[i].Prepare(frames), level * send * panLeft, level * send * panRight);
            voice.Mix(mix, frames, rate, level * dry * panLeft, level * dry * panRight, lowPassCoefficient, first, targets.AsSpan(0, sends.Length));
        }

        /// <summary>Mixed in the last block (the voice cap's hysteresis and fade-out).</summary>
        public bool Heard;

        /// <summary>Mixes the block ramping from the last gains to silence: a sound the cap silences fades out over one block instead of cutting.</summary>
        public void FadeOut(float[] mix, int frames)
        {
            (level, dry, send) = (0, 0, 0);
            Mix(mix, frames);
        }

        /// <summary>Leaves the block silent: the voice's clock runs on (<see cref="Voice.Skip"/>).</summary>
        public void Skip(int frames)
        {
            if (voice is null || Done || !Start(frames, out int first)) return;
            voice.Skip(frames, rate, first);
        }

        /// <summary>Works out the block's gains (the node chain, the buses, the place); returns how loud the sound is at the listener, dry or sent, for the voice cap.</summary>
        public float Prepare(float masterGain)
        {
            if (voice is null || Done) return 0;
            float db = rolledVolume, cents = rolledPitch, lowPass = rolledLowPass, linear = 1;
            WwiseObject? child = null;
            foreach (var n in chain)
            {
                var node = NodeOf(n)!;
                db += node.Properties.Float(WwisePropertyId.Volume);
                cents += node.Properties.Float(WwisePropertyId.Pitch);
                lowPass += node.Properties.Float(WwisePropertyId.LowPass);
                db += Player.volumeOffsets.Get(n.Id, GameObject);
                cents += Player.pitchOffsets.Get(n.Id, GameObject);
                lowPass += Player.lowPassOffsets.Get(n.Id, GameObject);
                foreach (var rtpc in node.Rtpcs) Apply(rtpc, ref db, ref linear, ref cents, ref lowPass);
                if (n is WwiseLayerContainer layers && child is not null)
                    foreach (var layer in layers.Layers)
                        foreach (var (childId, curve) in layer.Associations)
                        {
                            if (childId != child.Id) continue;
                            if (layer.CrossfadeParameterId != 0 && curve.Length > 0) linear *= Math.Max(0, WwiseCurves.Evaluate(curve, Player.Parameter(layer.CrossfadeParameterId, GameObject)));
                            foreach (var rtpc in layer.Rtpcs) Apply(rtpc, ref db, ref linear, ref cents, ref lowPass);
                        }
                child = n;
            }
            // The voice's level before the output bus and the distance: what the sends start from.
            float voiceGain = WwiseCurves.DbToGain(db) * linear * Fade * masterGain;
            // The output bus: the nearest node that overrides it, then the bus's parents (bus volume, the pause ducking, the user volume curves).
            float busGain = Player.BusGain(outputBus, GameObject);

            float dry = 1, send = 1, pan = 0;
            if (path is not null)
            {
                var (x, z) = PathPosition();
                pan = MathF.Sqrt(x * x + z * z) < 1e-3f ? 0 : x / MathF.Sqrt(x * x + z * z);
            }
            else if (placed3D && objectState is not null && objectState.TryGetPlace(out var position, out var front))
            {
                // Game-defined: at the object, attenuated by the distance. An object never placed is heard at the listener.
                var ears = Player.CurrentListener();
                float distance;
                (distance, pan) = Spatial.Locate(position, ears);
                if (attenuation is not null)
                {
                    distance /= Player.Reach;
                    dry = Spatial.VolumeGain(attenuation.Curve(WwiseAttenuation.VolumeDry), distance);
                    send = Spatial.VolumeGain(attenuation.Curve(WwiseAttenuation.AuxUserDefined), distance);
                    if (attenuation.Curve(WwiseAttenuation.LowPass) is { } lp && lp.Points.Length > 0) lowPass += WwiseCurves.Evaluate(lp.Points, distance);
                    var (coneGain, coneLowPass) = Spatial.Cone(attenuation.Cone, front, position, ears.Position);
                    dry *= coneGain;
                    send *= coneGain;
                    lowPass += coneLowPass;
                }
            }
            (panLeft, panRight) = Spatial.PanGains(pan);
            level = voiceGain * busGain;
            (this.dry, this.send) = (dry, send);
            lowPassCoefficient = LowPassCoefficient(lowPass);
            rate = (double)voice.Clip.SampleRate / Player.outputRate * Math.Pow(2, cents / 1200.0);
            return level * Math.Max(dry, sends.Length > 0 ? send : 0);
        }

        /// <summary>
        /// Adds an RTPC curve's value. Volume curves with dB scaling (2) store y as linear gain - 1 (-1 silent, 0 unchanged; docs/audio.md), so
        /// they multiply <paramref name="linear"/> by 1 + y; pitch (cents) and low-pass curves add.
        /// </summary>
        void Apply(WwiseRtpc rtpc, ref float db, ref float linear, ref float cents, ref float lowPass)
        {
            float y = WwiseCurves.Evaluate(rtpc.Points, Player.Parameter(rtpc.GameParameterId, GameObject));
            switch (rtpc.ParameterId)
            {
                case WwisePropertyId.Volume when rtpc.Scaling == 2: linear *= Math.Max(0, 1 + y); break;
                case WwisePropertyId.Volume: db += y; break;
                case WwisePropertyId.Pitch: cents += y; break;
                case WwisePropertyId.LowPass: lowPass += y; break;
            }
        }

        /// <summary>
        /// The Wwise low-pass value (0 none .. 100) as a one-pole coefficient. Wwise's own mapping to a cutoff is not known; this halves the
        /// cutoff every 10 points from 20 kHz (Meitou's approximation).
        /// </summary>
        float LowPassCoefficient(float value)
        {
            if (value <= 0.5f) return 1;
            float cutoff = 20000 * MathF.Pow(2, -Math.Clamp(value, 0, 100) / 10);
            return 1 - MathF.Exp(-2 * MathF.PI * cutoff / Player.outputRate);
        }

        /// <summary>Where the sound is on its path now: the vertices in order, each reached after its duration; listener-relative x (right) and z.</summary>
        (float X, float Z) PathPosition()
        {
            var (first, count) = path!.Paths[pathIndex];
            var vertices = path.Vertices.AsSpan((int)first, (int)Math.Min(count, (uint)path.Vertices.Length - first));
            if (vertices.Length == 0) return (0, 0);
            double t = age * 1000;
            for (int i = 0; i < vertices.Length - 1; i++)
            {
                double d = Math.Max(1, vertices[i].Duration);
                if (t < d)
                {
                    float k = (float)(t / d);
                    return (vertices[i].X + (vertices[i + 1].X - vertices[i].X) * k, vertices[i].Z + (vertices[i + 1].Z - vertices[i].Z) * k);
                }
                t -= d;
            }
            return (vertices[^1].X, vertices[^1].Z);
        }

        public void Dispose() => voice?.Dispose();
    }
}
