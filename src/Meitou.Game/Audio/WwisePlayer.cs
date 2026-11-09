using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data.Audio;

namespace Meitou.Game.Audio;

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
    #region Banks and commands

    /// <summary>
    /// The Vorbis codebooks the banks' WEMs are converted with.
    /// </summary>
    readonly WwiseCodebookLibrary _library;
    /// <summary>
    /// The install's <c>data/audio</c>, where the banks are read from.
    /// </summary>
    readonly string _bankDirectory;
    /// <summary>
    /// The output's sample rate, in Hz.
    /// </summary>
    readonly int _outputRate;
    /// <summary>
    /// Commands queued from any thread, run by <see cref="Mix"/> on the audio thread before it mixes.
    /// </summary>
    readonly ConcurrentQueue<Action> _commands = new();
    /// <summary>
    /// The bank loads asked for, by normalized name: loaded or loading.
    /// </summary>
    readonly ConcurrentDictionary<string, Task> _loading = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// The banks read and handed to the audio thread, by normalized name (<see cref="IsBankLoaded"/>).
    /// </summary>
    readonly ConcurrentDictionary<string, bool> _loadedBanks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="bankDirectory"/>: the install's <c>data/audio</c>. Reads <c>Init.bnk</c> at once for the game parameter defaults and
    /// state transition times.
    /// </summary>
    public WwisePlayer(WwiseCodebookLibrary library, string bankDirectory, int outputRate)
    {
        this._library = library;
        this._bankDirectory = bankDirectory;
        this._outputRate = outputRate;
        var init = Path.Combine(bankDirectory, "Init.bnk");
        if (File.Exists(init))
        {
            var bank = WwiseBank.ReadFile(init);
            foreach (var (id, value) in bank.GameParameterDefaults) _parameters[id] = value;
            foreach (var o in bank.Objects) _objects.TryAdd(o.Id, o);
        }
    }

    /// <summary>
    /// True once the bank has been read and handed to the audio thread (and not unloaded since).
    /// </summary>
    public bool IsBankLoaded(string name) => _loadedBanks.ContainsKey(Normalize(name));

    /// <summary>
    /// A bank name without its <c>.bnk</c> extension.
    /// </summary>
    static string Normalize(string name) => name.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    /// <summary>
    /// Reads a bank and converts its WEMs on a worker thread, then hands the objects and clips to the audio thread. Loading a bank that is
    /// loaded or loading returns the same task.
    /// </summary>
    public Task LoadBank(string name) => _loading.GetOrAdd(Normalize(name), n => Task.Run(() =>
    {
        var bank = WwiseBank.ReadFile(Path.Combine(_bankDirectory, n + ".bnk"));
        var clips = new ConcurrentBag<MediaClip>();
        Parallel.ForEach(bank.Media, m =>
        {
            var wem = WwiseVorbis.Read(bank.MediaBytes(m));
            clips.Add(new MediaClip(m.Id, wem.ToOgg(_library), wem.Channels, wem.SampleRate, wem.SampleCount));
        });
        _commands.Enqueue(() =>
        {
            foreach (var o in bank.Objects) _objects.TryAdd(o.Id, o);
            foreach (var c in clips) _media.TryAdd(c.Id, c);
            _bankContents[n] = ([.. bank.Objects], [.. clips.Select(c => c.Id)]);
            _loadedBanks[n] = true;
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
        if (!_loadedBanks.TryRemove(n, out _)) return;
        _loading.TryRemove(n, out _);
        _commands.Enqueue(() =>
        {
            if (!_bankContents.Remove(n, out var contents)) return;
            var own = new HashSet<WwiseObject>(contents.Objects, ReferenceEqualityComparer.Instance);
            foreach (var r in _roots)
                if (own.Contains(r.Node)) r.End();
            foreach (var o in contents.Objects)
                if (_objects.TryGetValue(o.Id, out var held) && own.Contains(held)) _objects.Remove(o.Id);
            foreach (var other in _bankContents.Values)
                foreach (var o in other.Objects) _objects.TryAdd(o.Id, o);
            foreach (var id in contents.Media) _media.Remove(id);
        });
    }

    /// <summary>
    /// Posts an event on the default game object (0); see the overloads with a game object.
    /// </summary>
    public void Post(string eventName) => Post(WwiseHash.Id(eventName), 0);
    /// <summary>
    /// Posts an event by id on the default game object (0).
    /// </summary>
    public void Post(uint eventId) => Post(eventId, 0);
    /// <summary>
    /// Sets a global game parameter by name (any thread; applied at the next block).
    /// </summary>
    public void SetParameter(string name, float value) => SetParameter(WwiseHash.Id(name), value);
    /// <summary>
    /// Sets a global game parameter by id (any thread; applied at the next block).
    /// </summary>
    public void SetParameter(uint id, float value) => _commands.Enqueue(() => _parameters[id] = value);
    /// <summary>
    /// Sets a state group to a state by name (any thread; applied at the next block).
    /// </summary>
    public void SetState(string group, string state) => SetState(WwiseHash.Id(group), WwiseHash.Id(state));
    /// <summary>
    /// Sets a state group to a state by id (any thread; applied at the next block).
    /// </summary>
    public void SetState(uint group, uint state) => _commands.Enqueue(() => ChangeState(group, state));

    /// <summary>
    /// Sets a switch group (<c>Biomes</c>, <c>General</c>): switches and states share the ids of their groups, so it is a state change.
    /// </summary>
    public void SetSwitch(string group, string value) => SetState(group, value);

    #endregion

    #region Mixing

    /// <summary>
    /// Random draws of the audio thread: picks, random property ranges, path choices.
    /// </summary>
    readonly Random _random = new();

    // Owned by the audio thread.
    /// <summary>
    /// The bank objects (events, actions, nodes, attenuations) by id, from <c>Init.bnk</c> and every loaded bank.
    /// </summary>
    readonly Dictionary<uint, WwiseObject> _objects = [];
    /// <summary>
    /// The loaded banks' clips, converted to Ogg Vorbis, by media id.
    /// </summary>
    readonly Dictionary<uint, MediaClip> _media = [];
    /// <summary>
    /// The short clips' decoded samples, shared by their voices.
    /// </summary>
    readonly PcmCache _pcm = new();
    /// <summary>
    /// The game parameters' values by id (<c>Init.bnk</c>'s defaults, then what is set); a missing one reads 0.
    /// </summary>
    readonly Dictionary<uint, float> _parameters = [];
    /// <summary>
    /// Each state group's current state, by group id.
    /// </summary>
    readonly Dictionary<uint, uint> _states = [];
    /// <summary>
    /// The last picks of each random container (by node id), for its <c>avoid repeat</c>.
    /// </summary>
    readonly Dictionary<uint, List<uint>> _history = [];
    /// <summary>
    /// The instances played by events, each the root of its instance tree.
    /// </summary>
    readonly List<Instance> _roots = [];
    /// <summary>
    /// Every sound instance with a voice, mixed each block.
    /// </summary>
    readonly List<SoundInstance> _sounds = [];
    /// <summary>
    /// Events whose bank is still loading: when they were posted, on which object, and until when they are retried.
    /// </summary>
    readonly List<(double At, uint EventId, ulong Object, double Expires)> _pending = [];
    /// <summary>
    /// Actions waiting for their delay: when they run, and on which object.
    /// </summary>
    readonly List<(double At, WwiseAction Action, ulong Object)> _delayed = [];
    /// <summary>
    /// Each loaded bank's objects and media ids, so that unloading drops only its own.
    /// </summary>
    readonly Dictionary<string, (WwiseObject[] Objects, uint[] Media)> _bankContents = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// The player's time in seconds: the block being mixed covers [<see cref="_blockStart"/>, <see cref="_time"/>); <see cref="_runTime"/> is when
    /// the action being run takes effect (music starts at that point).
    /// </summary>
    double _time, _blockStart, _runTime;

    /// <summary>
    /// How much louder a sound mixed in the last block counts for the cap (a factor on its level).
    /// </summary>
    const float CapHysteresis = 1.5f;

    /// <summary>
    /// Scratch for the voice cap: the cappable sounds' negated loudness, sorted with <see cref="_order"/>; grown as needed, never shrunk.
    /// </summary>
    float[] _loudness = new float[64];
    /// <summary>
    /// Scratch for the voice cap: the sound indices sorted by loudness, loudest first.
    /// </summary>
    int[] _order = new int[64];
    /// <summary>
    /// The sound indices the cap leaves silent this block.
    /// </summary>
    readonly HashSet<int> _silenced = [];

    /// <summary>
    /// Overall gain, linear (the ambience slider's).
    /// </summary>
    public float Gain { get; set; } = 1;

    /// <summary>
    /// Instances and voices last mixed, banks loaded (for the overlay).
    /// </summary>
    public (int Instances, int Voices, int Banks, int Clips) Stats { get; private set; }

    /// <summary>
    /// The voices of music clips last mixed (part of <see cref="Stats"/>' voices).
    /// </summary>
    public int MusicVoices { get; private set; }

    /// <summary>
    /// Meitou's voice cap (docs/audio.md "Voice cap", the Audio settings' Sound voices slider, <see cref="AudioSettings.VoiceCap"/>): at most this
    /// many sounds besides the music are mixed in a block, the loudest at the listener; the others run on silently (no decoding) and come back in
    /// place when they are among the loudest again. 0: every sound is mixed. Set from any thread.
    /// </summary>
    public int VoiceCap { get; set; } = AudioSettings.MeitouVoices;

    /// <summary>
    /// Meitou's hearing range (docs/audio.md "Hearing range", the Audio settings' Sound range slider): every attenuation curve and every game-side
    /// distance limit is read at the distance divided by this, so 2 hears everything twice as far. 1 is the original. Set from any thread.
    /// </summary>
    public float Reach { get; set; } = 1;

    /// <summary>
    /// The squared distance from <paramref name="eye"/> as the game's distance limits see it under <see cref="Reach"/>.
    /// </summary>
    public float HeardDistanceSquared(Vector3 at, Vector3 eye) => Vector3.DistanceSquared(at, eye) / (Reach * Reach);

    /// <summary>
    /// The sounds the cap left silent in the last block.
    /// </summary>
    public int Silenced { get; private set; }

    /// <summary>
    /// Called by the output: runs the queued commands, advances every instance and adds the voices to <paramref name="mix"/>.
    /// </summary>
    public void Mix(float[] mix, int frames)
    {
        _blockStart = _runTime = _time;
        while (_commands.TryDequeue(out var command)) command();
        double dt = (double)frames / _outputRate;
        _time += dt;
        _volumeOffsets.Advance(dt);
        _pitchOffsets.Advance(dt);
        _lowPassOffsets.Advance(dt);
        _busVolumes.Advance(dt);
        for (int i = _delayed.Count - 1; i >= 0; i--)
            if (_delayed[i].At <= _time)
            {
                var (_, action, gameObject) = _delayed[i];
                _runTime = Math.Max(_delayed[i].At, _blockStart);
                _delayed.RemoveAt(i);
                Run(action, gameObject);
            }
        _runTime = _blockStart;
        for (int i = _roots.Count - 1; i >= 0; i--)
        {
            _roots[i].Advance(dt);
            if (_roots[i].Done) _roots.RemoveAt(i);
        }
        int voices = 0, music = 0;
        for (int i = _sounds.Count - 1; i >= 0; i--)
            if (_sounds[i].Done)
            {
                _sounds[i].Dispose();
                _sounds.RemoveAt(i);
            }
        // The voice cap (Meitou's rule, docs/audio.md "Voice cap"): past VoiceCap sounds, only the loudest at the listener are mixed; the rest keep
        // their place silently.
        int cap = VoiceCap, cappable = 0;
        if (_loudness.Length < _sounds.Count) (_loudness, _order) = (new float[_sounds.Count * 2], new int[_sounds.Count * 2]);
        for (int i = 0; i < _sounds.Count; i++)
        {
            // A sound heard last block counts half as loud again: one at the cap's edge must get clearly quieter to give way, not flip every block.
            float heard = _sounds[i].Prepare(Gain) * (_sounds[i].Heard ? CapHysteresis : 1);
            if (cap > 0 && _sounds[i].Cappable)
            {
                _loudness[cappable] = -heard;
                _order[cappable++] = i;
            }
        }
        if (cap > 0 && cappable > cap)
        {
            Array.Sort(_loudness, _order, 0, cappable);
            for (int k = cap; k < cappable; k++) _silenced.Add(_order[k]);
        }
        for (int i = 0; i < _sounds.Count; i++)
        {
            var s = _sounds[i];
            if (_silenced.Contains(i))
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
        Silenced = _silenced.Count;
        _silenced.Clear();
        MixAuxBuses(mix, frames);
        Stats = (_roots.Count, voices, _loading.Count, _media.Count);
        MusicVoices = music;
    }

    /// <summary>
    /// Frees every sound's voice.
    /// </summary>
    public void Dispose()
    {
        foreach (var s in _sounds) s.Dispose();
        _sounds.Clear();
    }

    #endregion

    #region Events

    /// <summary>
    /// Posts again the events that waited for a bank (a bank has just arrived); those still missing wait on until they expire.
    /// </summary>
    void RetryPending()
    {
        var waiting = _pending.ToList();
        _pending.Clear();
        foreach (var p in waiting) PostNow(p.EventId, p.Object, p.Expires);
    }

    /// <summary>
    /// Runs an event's actions on <paramref name="gameObject"/> (a delayed one later); an event not loaded yet waits in <see cref="_pending"/> until
    /// <paramref name="expires"/>.
    /// </summary>
    void PostNow(uint eventId, ulong gameObject, double expires)
    {
        if (!_objects.TryGetValue(eventId, out var o) || o is not WwiseEvent e)
        {
            // Its bank is still loading: try again when a bank arrives, for a while.
            if (_time < expires) _pending.Add((_time, eventId, gameObject, expires));
            return;
        }
        foreach (var actionId in e.Actions)
        {
            if (!_objects.TryGetValue(actionId, out var a) || a is not WwiseAction action) continue;
            int delay = action.Properties.Int(WwisePropertyId.Delay);
            if (delay > 0) _delayed.Add((_time + delay / 1000.0, action, gameObject));
            else Run(action, gameObject);
        }
    }

    /// <summary>
    /// Whether an action is limited to the game object the event was posted on. The scope (the action type's low byte) follows the Wwise action
    /// numbering: 2 is global (the "master" forms), 3 is the posting object only (the "object" forms; Observed: <c>Stop_Emitter</c> uses it, a global
    /// stop would silence every emitter), 5 and 7 are the object forms of "all" and "all except"; 4 and 6 are global. Play always plays on the
    /// object.
    /// </summary>
    static bool ObjectScoped(WwiseAction action) => action.Scope is 3 or 5 or 7;

    /// <summary>
    /// Runs one action on <paramref name="gameObject"/>: plays a node, stops instances, sets a state, sets or resets a volume, pitch, low-pass or bus
    /// volume offset, or bypasses a bus's effects.
    /// </summary>
    void Run(WwiseAction action, ulong gameObject)
    {
        float fade = action.Properties.Int(WwisePropertyId.FadeTime) / 1000f;
        ulong scopeObject = ObjectScoped(action) ? gameObject : 0;
        switch (action.Kind)
        {
            case WwiseActionKind.Play:
                _postingObject = gameObject;
                try
                {
                    if (_objects.TryGetValue(action.Target, out var node) && Create(node, null) is { } instance)
                    {
                        instance.FadeIn(fade);
                        _roots.Add(instance);
                        instance.Start();
                    }
                }
                finally
                {
                    _postingObject = 0;
                }
                break;
            case WwiseActionKind.Stop:
                foreach (var r in _roots) r.StopMatching(action.Target, fade, ObjectScoped(action) ? gameObject : null);
                break;
            case WwiseActionKind.SetState:
                ChangeState(action.GroupId, action.StateId);
                break;
            case WwiseActionKind.SetVolume:
                _volumeOffsets.Set(action.Target, scopeObject, action.Value, fade);
                break;
            case WwiseActionKind.ResetVolume:
                _volumeOffsets.Reset(action.Target, scopeObject, fade);
                break;
            case WwiseActionKind.SetPitch:
                _pitchOffsets.Set(action.Target, scopeObject, action.Value, fade);
                break;
            case WwiseActionKind.ResetPitch:
                _pitchOffsets.Reset(action.Target, scopeObject, fade);
                break;
            case WwiseActionKind.SetLowPass:
                _lowPassOffsets.Set(action.Target, scopeObject, action.Value, fade);
                break;
            case WwiseActionKind.ResetLowPass:
                _lowPassOffsets.Reset(action.Target, scopeObject, fade);
                break;
            case WwiseActionKind.SetBusVolume:
                _busVolumes.Set(action.Target, scopeObject, action.Value, fade);
                break;
            case WwiseActionKind.ResetBusVolume:
                _busVolumes.Reset(action.Target, scopeObject, fade);
                break;
            case WwiseActionKind.BypassFx:
                if (action.TargetIsBus) ApplyBypass(action.Target, action.Bypass, action.BypassMask);
                break;
        }
    }

    /// <summary>
    /// Sets a state group's state; on a change every instance tree hears of it (switch containers swap their children).
    /// </summary>
    void ChangeState(uint group, uint state)
    {
        if (_states.TryGetValue(group, out var old) && old == state) return;
        _states[group] = state;
        foreach (var r in _roots) r.StateChanged(group);
    }

    /// <summary>
    /// The instance for a played node; null for a node that cannot play.
    /// </summary>
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

    /// <summary>
    /// The node properties of a bank object; null for an object without them.
    /// </summary>
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

    /// <summary>
    /// The node and its parents up the hierarchy (as far as the loaded banks reach).
    /// </summary>
    IEnumerable<WwiseObject> Chain(WwiseObject node)
    {
        for (var o = node; o is not null;)
        {
            yield return o;
            uint parent = NodeOf(o)?.ParentId ?? 0;
            o = parent != 0 && _objects.TryGetValue(parent, out var p) ? p : null;
        }
    }

    /// <summary>
    /// A global game parameter's value; 0 when never set.
    /// </summary>
    float Parameter(uint id) => _parameters.TryGetValue(id, out var v) ? v : 0;

    #endregion

    #region Instances

    /// <summary>
    /// A played node. Gains multiply down the instance tree (fades); a stopped instance fades out and ends.
    /// </summary>
    abstract class Instance(WwisePlayer player, WwiseObject node, Instance? parent)
    {
        /// <summary>
        /// The player this instance runs in.
        /// </summary>
        protected readonly WwisePlayer _player = player;
        /// <summary>
        /// The bank node this instance plays.
        /// </summary>
        public readonly WwiseObject Node = node;
        /// <summary>
        /// The instance that played this one; null for a root.
        /// </summary>
        public readonly Instance? Parent = parent;

        /// <summary>
        /// The game object this plays on: the one the event was posted on (inherited from the parent).
        /// </summary>
        public readonly ulong GameObject = parent?.GameObject ?? player._postingObject;
        /// <summary>
        /// The instances this one played and that still run.
        /// </summary>
        protected readonly List<Instance> _children = [];
        /// <summary>
        /// This instance's own fade (0..1), where it ramps to, and how fast (per second; 0 at once).
        /// </summary>
        float _fade = 1, _fadeTarget = 1, _fadeRate;
        /// <summary>
        /// Whether a stop is fading this instance out; it ends when the fade reaches 0.
        /// </summary>
        bool _stopping;

        /// <summary>
        /// The instance has ended; its owner drops it.
        /// </summary>
        public bool Done { get; protected set; }

        /// <summary>
        /// When this instance starts (player time, seconds): the point in the block being mixed that music aligns to.
        /// </summary>
        public double StartTime = player._runTime;

        /// <summary>
        /// Whether a stop is fading this instance out.
        /// </summary>
        protected bool Stopping => _stopping;

        /// <summary>
        /// This instance's fade times its parents'.
        /// </summary>
        public float Fade => _fade * (Parent?.Fade ?? 1);

        /// <summary>
        /// Starts this instance silent and ramps it in over <paramref name="seconds"/> (nothing at 0).
        /// </summary>
        public void FadeIn(float seconds)
        {
            if (seconds <= 0) return;
            _fade = 0;
            _fadeTarget = 1;
            _fadeRate = 1 / seconds;
        }

        /// <summary>
        /// Starts playing: a container plays its children, a sound joins the mix.
        /// </summary>
        public abstract void Start();

        /// <summary>
        /// Advances the fades and the children by <paramref name="dt"/> seconds; ends this instance when its stop has faded out.
        /// </summary>
        public virtual void Advance(double dt)
        {
            if (_fade != _fadeTarget)
            {
                float step = _fadeRate <= 0 ? 1 : (float)(_fadeRate * dt);
                _fade = _fade < _fadeTarget ? Math.Min(_fadeTarget, _fade + step) : Math.Max(_fadeTarget, _fade - step);
            }
            if (_stopping && _fade <= 0)
            {
                End();
                return;
            }
            for (int i = _children.Count - 1; i >= 0; i--)
            {
                _children[i].Advance(dt);
                if (_children[i].Done)
                {
                    var child = _children[i];
                    _children.RemoveAt(i);
                    ChildEnded(child);
                }
            }
        }

        /// <summary>
        /// A child ended: by default this instance ends with its last child.
        /// </summary>
        protected virtual void ChildEnded(Instance child)
        {
            if (_children.Count == 0) End();
        }

        /// <summary>
        /// Ends this instance and its children at once.
        /// </summary>
        public virtual void End()
        {
            if (Done) return;
            Done = true;
            foreach (var c in _children) c.End();
            _children.Clear();
        }

        /// <summary>
        /// Stops this instance, fading out over <paramref name="seconds"/> (at once at 0).
        /// </summary>
        public void Stop(float seconds)
        {
            if (seconds <= 0)
            {
                End();
                return;
            }
            _stopping = true;
            _fadeTarget = 0;
            _fadeRate = Math.Max(_fadeRate, _fade / seconds);
        }

        /// <summary>
        /// Stops this instance if its node or a parent node is <paramref name="target"/>, otherwise looks among the children; with
        /// <paramref name="onlyObject"/> only instances playing on that game object.
        /// </summary>
        public void StopMatching(uint target, float seconds, ulong? onlyObject = null)
        {
            if (Done || onlyObject is { } only && only != GameObject) return;
            if (_player.Chain(Node).Any(n => n.Id == target))
            {
                Stop(seconds);
                return;
            }
            foreach (var c in _children) c.StopMatching(target, seconds, onlyObject);
        }

        /// <summary>
        /// A state group changed: by default passed on to the children.
        /// </summary>
        public virtual void StateChanged(uint group)
        {
            foreach (var c in _children) c.StateChanged(group);
        }

        /// <summary>
        /// Plays the bank object <paramref name="childId"/> as a child of this instance.
        /// </summary>
        protected void Play(uint childId)
        {
            if (_player._objects.TryGetValue(childId, out var o) && _player.Create(o, this) is { } child)
            {
                _children.Add(child);
                child.Start();
            }
        }
    }

    /// <summary>
    /// Plays all children at once (layer containers; an actor-mixer played directly).
    /// </summary>
    sealed class GroupInstance(WwisePlayer player, WwiseObject node, uint[] children, Instance? parent) : Instance(player, node, parent)
    {
        public override void Start()
        {
            foreach (var c in children) Play(c);
            if (_children.Count == 0) End();
        }
    }

    /// <summary>
    /// A random or sequence container. Continuous (flags bit 3) plays one child after another until the loop count runs out (0 = forever);
    /// otherwise (step) plays one child per play. Random picks by weight, skipping the last <c>avoid repeat</c> picks.
    /// </summary>
    sealed class RandomSequenceInstance(WwisePlayer player, WwiseRandomSequence node, Instance? parent) : Instance(player, node, parent)
    {
        /// <summary>
        /// The next sequence index, and how many children have been played.
        /// </summary>
        int _index, _played;
        /// <summary>
        /// Seconds until the next child is tried (the transition wait, or 0.1 when a child could not play); negative when none is due.
        /// </summary>
        double _wait = -1;

        /// <summary>
        /// Whether the container plays its children one after another (flags bit 3) rather than one per play.
        /// </summary>
        bool Continuous => (node.Flags & 0x08) != 0;

        public override void Start() => Next();

        /// <summary>
        /// Plays the next child (by sequence, or by weighted random avoiding the recent picks) or ends the container when its loops ran out.
        /// </summary>
        void Next()
        {
            var list = node.Playlist.Length > 0 ? node.Playlist : node.Children.Select(c => (c, 50000)).ToArray();
            if (list.Length == 0)
            {
                End();
                return;
            }
            int total = Continuous && node.LoopCount > 0 ? node.LoopCount * list.Length : 1;
            if (Continuous ? node.LoopCount > 0 && _played >= total : _played >= 1)
            {
                End();
                return;
            }
            _played++;
            uint pick;
            if (node.Mode == 1) pick = list[_index++ % list.Length].Item1;
            else
            {
                if (!_player._history.TryGetValue(node.Id, out var recent)) _player._history[node.Id] = recent = [];
                var allowed = list.Where(e => !recent.Contains(e.Item1)).ToArray();
                if (allowed.Length == 0) allowed = list;
                long sum = allowed.Sum(e => (long)Math.Max(1, e.Item2));
                long roll = (long)(_player._random.NextDouble() * sum);
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
            if (_children.Count == 0 && !Done) _wait = 0.1; // a child that cannot play (its media is in a bank not loaded): try the next soon
        }

        public override void Advance(double dt)
        {
            base.Advance(dt);
            if (Done || _wait < 0) return;
            _wait -= dt;
            if (_wait < 0) Next();
        }

        protected override void ChildEnded(Instance child)
        {
            if (Done || _children.Count > 0) return;
            if (!Continuous)
            {
                End();
                return;
            }
            // Transition mode 3 waits the transition time between children; the others (0 none, 1/2 crossfades) start the next at once.
            if (node.TransitionMode == 3) _wait = node.TransitionTime / 1000.0;
            else Next();
        }
    }

    /// <summary>
    /// A switch container on a state group: plays the children of the current state, swapping them (with the fades) when it changes.
    /// </summary>
    sealed class SwitchInstance(WwisePlayer player, WwiseSwitchContainer node, Instance? parent) : Instance(player, node, parent)
    {
        /// <summary>
        /// The switch value whose children play.
        /// </summary>
        uint _current;

        public override void Start()
        {
            Switch();
            if (!node.ContinuousValidation && _children.Count == 0) End();
        }

        /// <summary>
        /// Plays the children of the current switch value (the object's, else the default) with their fade-ins.
        /// </summary>
        void Switch()
        {
            _current = _player.TryGetGroupValue(node.GroupId, GameObject, out var s) ? s : node.DefaultSwitch;
            var list = node.Switches.FirstOrDefault(w => w.SwitchId == _current).Nodes ?? [];
            foreach (var c in list)
            {
                Play(c);
                var fadeIn = node.NodeParameters.FirstOrDefault(p => p.NodeId == c).FadeIn;
                if (fadeIn > 0 && _children.Count > 0) _children[^1].FadeIn(fadeIn / 1000f);
            }
        }

        public override void StateChanged(uint group)
        {
            if (Done || Stopping) return;
            base.StateChanged(group);
            if (!node.ContinuousValidation || group != node.GroupId) return;
            uint state = _player.TryGetGroupValue(group, GameObject, out var s) ? s : node.DefaultSwitch;
            if (state == _current) return;
            foreach (var c in _children) c.Stop(Math.Max(0, node.NodeParameters.FirstOrDefault(p => p.NodeId == c.Node.Id).FadeOut) / 1000f);
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
        /// <summary>
        /// The voice that plays the clip; null when the clip is missing (the instance then ends at its start).
        /// </summary>
        readonly Voice? _voice;
        /// <summary>
        /// The node and its parents up the hierarchy.
        /// </summary>
        readonly WwiseObject[] _chain;
        /// <summary>
        /// The random parts of volume, pitch and low-pass, rolled once along the chain.
        /// </summary>
        readonly float _rolledVolume, _rolledPitch, _rolledLowPass;
        /// <summary>
        /// The positioning whose paths move the sound around the listener; null when the sound is not on a path.
        /// </summary>
        readonly WwisePositioning? _path;
        /// <summary>
        /// The path picked at random.
        /// </summary>
        readonly int _pathIndex;
        /// <summary>
        /// Whether the sound is 3D by its game object's place (no paths); <see cref="_attenuation"/> is the positioning's attenuation, if it names
        /// one.
        /// </summary>
        readonly bool _placed3D;
        /// <summary>
        /// The attenuation that shapes the placed sound by distance; null for none.
        /// </summary>
        readonly WwiseAttenuation? _attenuation;
        /// <summary>
        /// The game object's place and switches; null when it is not registered.
        /// </summary>
        readonly GameObjectState? _objectState;
        /// <summary>
        /// The output bus overridden by the nearest node in the chain; 0 for the default.
        /// </summary>
        readonly uint _outputBus;
        /// <summary>
        /// The auxiliary buses the sound sends to (at most <see cref="Voice.MaxSends"/>).
        /// </summary>
        readonly AuxBusState[] _sends = [];
        /// <summary>
        /// This block's sends for the voice, one per <see cref="_sends"/> entry.
        /// </summary>
        readonly VoiceSend[] _targets = new VoiceSend[Voice.MaxSends];
        /// <summary>
        /// Seconds since the sound started (where it is on its path).
        /// </summary>
        double _age;
        /// <summary>
        /// Output frames left before a music clip starts.
        /// </summary>
        double _delayFrames;

        /// <summary>
        /// Whether this is a clip of a music track rather than a sound.
        /// </summary>
        public bool IsMusic { get; }

        /// <summary>
        /// A sound node: plays its source the node's loop count (absent: once; 0 forever).
        /// </summary>
        public SoundInstance(WwisePlayer player, WwiseSound sound, Instance? parent) : this(player, sound, parent, sound.Source,
            sound.Node.Properties.Values.ContainsKey(WwisePropertyId.Loop) ? sound.Node.Properties.Int(WwisePropertyId.Loop) : 1, null, 0)
        {
        }

        /// <summary>
        /// A clip of <paramref name="track"/> beginning at <paramref name="startTime"/> (player seconds).
        /// </summary>
        public SoundInstance(WwisePlayer player, WwiseMusicTrack track, WwiseMusicClip clip, WwiseSource source, double startTime, Instance parent)
            : this(player, track, parent, source, 1, clip, startTime)
        {
        }

        /// <summary>
        /// Reads the node chain once: the output bus, the rolled random ranges, the positioning (a path, or the game object with its attenuation),
        /// the sends and the voice (a music clip trimmed and placed in the block).
        /// </summary>
        SoundInstance(
            WwisePlayer player,
            WwiseObject node,
            Instance? parent,
            WwiseSource source,
            int plays,
            WwiseMusicClip? musicClip,
            double startTime)
            : base(player, node, parent)
        {
            IsMusic = musicClip is not null;
            if (musicClip is not null) StartTime = startTime;
            _chain = [.. player.Chain(node)];
            _outputBus = _chain.Select(n => NodeOf(n)!.OverrideBusId).FirstOrDefault(b => b != 0);
            foreach (var n in _chain)
            {
                var props = NodeOf(n)!.Properties;
                _rolledVolume += Roll(props, WwisePropertyId.Volume);
                _rolledPitch += Roll(props, WwisePropertyId.Pitch);
                _rolledLowPass += Roll(props, WwisePropertyId.LowPass);
            }
            var positioning = _chain.Select(n => NodeOf(n)!.Positioning).FirstOrDefault(p => p.OverridesParent);
            if (positioning is { HasPaths: true })
            {
                _path = positioning;
                _pathIndex = player._random.Next(positioning.Paths.Length);
            }
            else if (positioning is { IsGameDefined: true })
            {
                _placed3D = true;
                if (positioning.AttenuationId != 0 && player._objects.TryGetValue(positioning.AttenuationId, out var a))
                    _attenuation = a as WwiseAttenuation;
                player._gameObjects.TryGetValue(GameObject, out _objectState);
            }
            // The sends: the nearest node that sets user-defined sends (bit 2 overrides, bit 3 lists the buses) decides; a node that overrides with
            // no buses sends nowhere.
            var sender = _chain.Select(n => NodeOf(n)!).FirstOrDefault(n => (n.AuxBits & 0x0C) != 0);
            if (sender is not null)
                _sends = [.. sender.AuxBuses.Where(b => b != 0).Take(Voice.MaxSends).Select(b => player.AuxBus(b)).OfType<AuxBusState>()];
            if (source.IsVorbis && player._media.TryGetValue(source.SourceId, out var clip))
            {
                // Plays: 0 = forever, n = n times; absent = once. A music clip plays from its begin trim to its end trim.
                long skip = 0, limit = -1;
                double phase = 0;
                if (musicClip is not null)
                {
                    skip = (long)Math.Round(Math.Max(0, musicClip.BeginTrim) / 1000 * clip.SampleRate);
                    if (musicClip.BeginTrim > 0.5 || musicClip.EndTrim < -0.5)
                    {
                        double playable = musicClip.SourceDuration + musicClip.EndTrim - musicClip.BeginTrim;
                        limit = (long)Math.Round(Math.Max(0, playable) / 1000 * clip.SampleRate);
                    }

                    // Starts at the first output frame at or after the start time; the clip's position there is the part of a frame the start is
                    // early.
                    double at = Math.Max(0, (StartTime - player._blockStart) * player._outputRate);
                    _delayFrames = Math.Ceiling(at - 1e-6);
                    phase = Math.Max(0, _delayFrames - at) * clip.SampleRate / player._outputRate;
                }
                // A sound plays from the shared decoded samples (PcmCache) when its clip is short; a music clip (trimmed, long) streams.
                _voice = new Voice(clip, plays, skip, limit, phase, musicClip is null ? player._pcm.Get(clip) : null);
            }
        }

        /// <summary>
        /// A random value in the property's range (0 without one).
        /// </summary>
        float Roll((IReadOnlyDictionary<byte, uint> Values, IReadOnlyDictionary<byte, (float Min, float Max)> Ranges) props, byte id) =>
            props.Ranges.TryGetValue(id, out var r) ? r.Min + (float)_player._random.NextDouble() * (r.Max - r.Min) : 0;

        /// <summary>
        /// A random value in the property's range (0 without one).
        /// </summary>
        float Roll(WwiseProperties props, byte id) => Roll((props.Values, props.Ranges), id);

        public override void Start()
        {
            if (_voice is null) End();
            else _player._sounds.Add(this);
        }

        public override void Advance(double dt)
        {
            base.Advance(dt);
            _age += dt;
            if (_voice is { Ended: true }) End();
        }

        /// <summary>
        /// This block's gains, from <see cref="Prepare"/>: the level, the dry and send attenuations, the pan and the low-pass coefficient.
        /// </summary>
        float _level, _dry, _send, _panLeft, _panRight, _lowPassCoefficient;
        /// <summary>
        /// This block's resampling rate: source frames per output frame.
        /// </summary>
        double _rate;

        /// <summary>
        /// Whether the player may leave this sound silent for a block (<see cref="VoiceCap"/>): any but a music clip, while its voice can keep its
        /// place.
        /// </summary>
        public bool Cappable => !IsMusic && _voice is { CanSkip: true };

        /// <summary>
        /// The frames of this block before the sound starts (a music clip starting inside it), taken off the pending delay.
        /// </summary>
        bool Start(int frames, out int first)
        {
            first = 0;
            if (_delayFrames <= 0) return true;
            if (_delayFrames >= frames)
            {
                _delayFrames -= frames;
                return false;
            }
            first = (int)_delayFrames;
            _delayFrames = 0;
            return true;
        }

        /// <summary>
        /// Mixes the block with the gains <see cref="Prepare"/> worked out.
        /// </summary>
        public void Mix(float[] mix, int frames)
        {
            if (_voice is null || Done || !Start(frames, out int first)) return;
            for (int i = 0; i < _sends.Length; i++)
                _targets[i] = new VoiceSend(_sends[i].Prepare(frames), _level * _send * _panLeft, _level * _send * _panRight);
            float left = _level * _dry * _panLeft;
            float right = _level * _dry * _panRight;
            _voice.Mix(mix, frames, _rate, left, right, _lowPassCoefficient, first, _targets.AsSpan(0, _sends.Length));
        }

        /// <summary>
        /// Mixed in the last block (the voice cap's hysteresis and fade-out).
        /// </summary>
        public bool Heard;

        /// <summary>
        /// Mixes the block ramping from the last gains to silence: a sound the cap silences fades out over one block instead of cutting.
        /// </summary>
        public void FadeOut(float[] mix, int frames)
        {
            (_level, _dry, _send) = (0, 0, 0);
            Mix(mix, frames);
        }

        /// <summary>
        /// Leaves the block silent: the voice's clock runs on (<see cref="Voice.Skip"/>).
        /// </summary>
        public void Skip(int frames)
        {
            if (_voice is null || Done || !Start(frames, out int first)) return;
            _voice.Skip(frames, _rate, first);
        }

        /// <summary>
        /// Works out the block's gains (the node chain, the buses, the place); returns how loud the sound is at the listener, dry or sent, for the
        /// voice cap.
        /// </summary>
        public float Prepare(float masterGain)
        {
            if (_voice is null || Done) return 0;
            float db = _rolledVolume, cents = _rolledPitch, lowPass = _rolledLowPass, linear = 1;
            WwiseObject? child = null;
            foreach (var n in _chain)
            {
                var node = NodeOf(n)!;
                db += node.Properties.Float(WwisePropertyId.Volume);
                cents += node.Properties.Float(WwisePropertyId.Pitch);
                lowPass += node.Properties.Float(WwisePropertyId.LowPass);
                db += _player._volumeOffsets.Get(n.Id, GameObject);
                cents += _player._pitchOffsets.Get(n.Id, GameObject);
                lowPass += _player._lowPassOffsets.Get(n.Id, GameObject);
                foreach (var rtpc in node.Rtpcs) Apply(rtpc, ref db, ref linear, ref cents, ref lowPass);
                if (n is WwiseLayerContainer layers && child is not null)
                    foreach (var layer in layers.Layers)
                        foreach (var (childId, curve) in layer.Associations)
                        {
                            if (childId != child.Id) continue;
                            if (layer.CrossfadeParameterId != 0 && curve.Length > 0)
                            {
                                float crossfade = _player.Parameter(layer.CrossfadeParameterId, GameObject);
                                linear *= Math.Max(0, WwiseCurves.Evaluate(curve, crossfade));
                            }

                            foreach (var rtpc in layer.Rtpcs) Apply(rtpc, ref db, ref linear, ref cents, ref lowPass);
                        }
                child = n;
            }
            // The voice's level before the output bus and the distance: what the sends start from.
            float voiceGain = WwiseCurves.DbToGain(db) * linear * Fade * masterGain;
            // The output bus: the nearest node that overrides it, then the bus's parents (bus volume, the pause ducking, the user volume curves).
            float busGain = _player.BusGain(_outputBus, GameObject);

            float dry = 1, send = 1, pan = 0;
            if (_path is not null)
            {
                var (x, z) = PathPosition();
                pan = MathF.Sqrt(x * x + z * z) < 1e-3f ? 0 : x / MathF.Sqrt(x * x + z * z);
            }
            else if (_placed3D && _objectState is not null && _objectState.TryGetPlace(out var position, out var front))
            {
                // Game-defined: at the object, attenuated by the distance. An object never placed is heard at the listener.
                var ears = _player.CurrentListener();
                float distance;
                (distance, pan) = Spatial.Locate(position, ears);
                if (_attenuation is not null)
                {
                    distance /= _player.Reach;
                    dry = Spatial.VolumeGain(_attenuation.Curve(WwiseAttenuation.VolumeDry), distance);
                    send = Spatial.VolumeGain(_attenuation.Curve(WwiseAttenuation.AuxUserDefined), distance);
                    if (_attenuation.Curve(WwiseAttenuation.LowPass) is { } lp && lp.Points.Length > 0)
                        lowPass += WwiseCurves.Evaluate(lp.Points, distance);
                    var (coneGain, coneLowPass) = Spatial.Cone(_attenuation.Cone, front, position, ears.Position);
                    dry *= coneGain;
                    send *= coneGain;
                    lowPass += coneLowPass;
                }
            }
            (_panLeft, _panRight) = Spatial.PanGains(pan);
            _level = voiceGain * busGain;
            (this._dry, this._send) = (dry, send);
            _lowPassCoefficient = LowPassCoefficient(lowPass);
            _rate = (double)_voice.Clip.SampleRate / _player._outputRate * Math.Pow(2, cents / 1200.0);
            return _level * Math.Max(dry, _sends.Length > 0 ? send : 0);
        }

        /// <summary>
        /// Adds an RTPC curve's value. Volume curves with dB scaling (2) store y as linear gain - 1 (-1 silent, 0 unchanged; docs/audio.md), so
        /// they multiply <paramref name="linear"/> by 1 + y; pitch (cents) and low-pass curves add.
        /// </summary>
        void Apply(WwiseRtpc rtpc, ref float db, ref float linear, ref float cents, ref float lowPass)
        {
            float y = WwiseCurves.Evaluate(rtpc.Points, _player.Parameter(rtpc.GameParameterId, GameObject));
            switch (rtpc.ParameterId)
            {
                case WwisePropertyId.Volume when rtpc.Scaling == 2:
                    linear *= Math.Max(0, 1 + y);
                    break;
                case WwisePropertyId.Volume:
                    db += y;
                    break;
                case WwisePropertyId.Pitch:
                    cents += y;
                    break;
                case WwisePropertyId.LowPass:
                    lowPass += y;
                    break;
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
            return 1 - MathF.Exp(-2 * MathF.PI * cutoff / _player._outputRate);
        }

        /// <summary>
        /// Where the sound is on its path now: the vertices in order, each reached after its duration; listener-relative x (right) and z.
        /// </summary>
        (float X, float Z) PathPosition()
        {
            var (first, count) = _path!.Paths[_pathIndex];
            var vertices = _path.Vertices.AsSpan((int)first, (int)Math.Min(count, (uint)_path.Vertices.Length - first));
            if (vertices.Length == 0) return (0, 0);
            double t = _age * 1000;
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

        /// <summary>
        /// Frees the voice.
        /// </summary>
        public void Dispose() => _voice?.Dispose();
    }

    #endregion
}