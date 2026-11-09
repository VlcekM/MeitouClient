using Meitou.Data.Audio;

namespace Meitou.Game.Audio;

/// <summary>
/// The music engine of <see cref="WwisePlayer"/> (docs/formats/wwise-bnk.md#playback-semantics): a music switch picks a playlist by the
/// current state or switch, a playlist walks its groups and starts each segment at the exit cue of the one before (the old one's tail plays
/// on), a segment places its tracks' clips. Starts are placed inside the block being mixed, so a loop or a hand-over is exact to one output
/// frame. Not done (no Kenshi bank needs them): stingers, transition segments, special track types, clip automation, fades of transition
/// rules, <c>continue playback</c>, music in 3D (the radio's attenuation).
/// </summary>
public sealed partial class WwisePlayer
{
    /// <summary>
    /// The avoid-repeat history per playlist group (by playlist and item id), kept across plays.
    /// </summary>
    readonly Dictionary<(uint Playlist, uint Item), List<uint>> _musicHistory = [];
    /// <summary>
    /// The sequence step positions per playlist group (by playlist and item id), kept across plays.
    /// </summary>
    readonly Dictionary<(uint Playlist, uint Item), int> _musicSteps = [];

    /// <summary>
    /// A music node that plays on a timeline: when its current segment ends, and whether it should stop after that one.
    /// </summary>
    abstract class MusicInstance(WwisePlayer player, WwiseObject node, Instance? parent) : Instance(player, node, parent)
    {
        /// <summary>
        /// Player time (seconds) of the exit cue of the segment playing now; minus infinity when none is.
        /// </summary>
        public abstract double CurrentExit { get; }

        /// <summary>
        /// When set, nothing follows the current segment (a music switch is about to replace this one at its exit cue).
        /// </summary>
        public virtual bool Hold { get; set; }

        /// <summary>
        /// Plays the bank object <paramref name="id"/> as a child starting at player time <paramref name="at"/>; null when it is not loaded or
        /// cannot play.
        /// </summary>
        protected Instance? Spawn(uint id, double at)
        {
            if (!_player._objects.TryGetValue(id, out var o) || _player.Create(o, this) is not { } child) return null;
            child.StartTime = at;
            _children.Add(child);
            child.Start();
            return child;
        }
    }

    /// <summary>
    /// A segment: its tracks' clips, each placed at its play-at time (segment time 0 is the entry cue minus its position).
    /// </summary>
    sealed class MusicSegmentInstance(WwisePlayer player, WwiseMusicSegment node, Instance? parent) : MusicInstance(player, node, parent)
    {
        /// <inheritdoc/>
        public override double CurrentExit => StartTime + Math.Max(1, node.ExitCue - node.EntryCue) / 1000;

        public override void Start()
        {
            double zero = StartTime - node.EntryCue / 1000;
            foreach (var trackId in node.Music.Children)
            {
                if (!_player._objects.TryGetValue(trackId, out var o) || o is not WwiseMusicTrack track) continue;
                foreach (var clip in track.Clips)
                {
                    var source = track.Sources.FirstOrDefault(s => s.SourceId == clip.SourceId);
                    if (source is null) continue;
                    var sound = new SoundInstance(_player, track, clip, source, zero + (clip.PlayAt + clip.BeginTrim) / 1000, this);
                    _children.Add(sound);
                    sound.Start();
                }
            }
            if (_children.Count == 0) End(); // a silence: the playlist keeps the time
        }
    }

    /// <summary>
    /// A music random/sequence container. Segments come from walking the playlist tree lazily: a segment item plays its segment as often as
    /// its loop count says (0 forever), a group makes loop-count passes (0 forever), each pass one child (step) or all (continuous), in order
    /// (sequence) or picked by weight skipping the last <c>avoid repeat</c> picks (random).
    /// </summary>
    sealed class MusicPlaylistInstance(WwisePlayer player, WwiseMusicPlaylist node, Instance? parent) : MusicInstance(player, node, parent)
    {
        /// <summary>
        /// Walks the playlist tree: the ids of the segments to play, in order.
        /// </summary>
        IEnumerator<uint>? _walker;
        /// <summary>
        /// Player time of the exit cue of the segment started last; minus infinity before the first.
        /// </summary>
        double _exitTime = double.NegativeInfinity;
        /// <summary>
        /// The walk has run out: nothing follows the current segment.
        /// </summary>
        bool _finished;

        /// <inheritdoc/>
        public override double CurrentExit => _exitTime;

        public override void Start()
        {
            _walker = Segments(node.Playlist).GetEnumerator();
            if (!StartNext(StartTime)) _finished = true;
        }

        /// <summary>
        /// Starts the next segment of the walk at <paramref name="at"/> (skipping those of banks not loaded, at most 64); false when the walk has
        /// run out.
        /// </summary>
        bool StartNext(double at)
        {
            for (int tries = 0; tries < 64 && _walker!.MoveNext(); tries++)
            {
                // A segment of a bank that is not loaded is skipped.
                if (!_player._objects.TryGetValue(_walker.Current, out var o) || o is not WwiseMusicSegment segment) continue;
                var instance = new MusicSegmentInstance(_player, segment, this) { StartTime = at };
                _children.Add(instance);
                instance.Start();
                _exitTime = at + Math.Max(1, segment.ExitCue - segment.EntryCue) / 1000;
                return true;
            }
            return false;
        }

        public override void Advance(double dt)
        {
            base.Advance(dt);
            if (Done) return;
            double end = _player._time;
            if (!Stopping && !Hold)
                for (int guard = 0; !_finished && _exitTime < end && guard < 64; guard++)
                    if (!StartNext(Math.Max(_exitTime, _player._blockStart))) _finished = true;
            if ((_finished || Hold) && _children.Count == 0 && _exitTime <= end) End();
        }

        // Ends by its own rule above: not when a segment's clips end, which may be before the exit cue (a silence) or after it (a tail).
        protected override void ChildEnded(Instance child) { }

        /// <summary>
        /// The segment ids an item plays: a segment its loop count of times (0 forever), a group its passes (stopping after 100 empty ones).
        /// </summary>
        IEnumerable<uint> Segments(WwisePlaylistItem item)
        {
            bool forever = item.LoopCount == 0;
            int loops = Math.Max(1, (int)item.LoopCount);
            if (!item.IsGroup)
            {
                for (int n = 0; forever || n < loops; n++) yield return item.SegmentId;
                yield break;
            }
            int emptyPasses = 0;
            for (int pass = 0; (forever || pass < loops) && emptyPasses < 100; pass++)
            {
                bool any = false;
                foreach (var child in Pass(item))
                    foreach (var id in Segments(child))
                    {
                        any = true;
                        yield return id;
                    }
                emptyPasses = any ? 0 : emptyPasses + 1;
            }
        }

        /// <summary>
        /// The children one pass of a group plays, in order. Group types: 0 sequence continuous, 1 sequence step, 2 random continuous, 3 random step.
        /// </summary>
        List<WwisePlaylistItem> Pass(WwisePlaylistItem group)
        {
            var children = group.Children;
            if (children.Length == 0) return [];
            var key = (node.Id, group.ItemId);
            switch (group.PlaylistType)
            {
                case 0:
                    return [.. children];
                case 1:
                    int step = _player._musicSteps.GetValueOrDefault(key);
                    _player._musicSteps[key] = step + 1;
                    return [children[step % children.Length]];
                case 3:
                    return [Pick(group, children)];
                default:
                    if (group.Shuffle)
                    {
                        var left = children.ToList();
                        var order = new List<WwisePlaylistItem>();
                        while (left.Count > 0)
                        {
                            int i = WeightedIndex(group, left);
                            order.Add(left[i]);
                            left.RemoveAt(i);
                        }
                        return order;
                    }
                    return [.. children.Select(_ => Pick(group, children))];
            }
        }

        /// <summary>
        /// A random index into <paramref name="candidates"/> by their weights (all 1 when the group does not use weights).
        /// </summary>
        int WeightedIndex(WwisePlaylistItem group, IReadOnlyList<WwisePlaylistItem> candidates)
        {
            double Weight(WwisePlaylistItem c) => group.UsingWeight ? Math.Max(1, c.Weight) : 1;
            double roll = _player._random.NextDouble() * candidates.Sum(Weight);
            for (int i = 0; i < candidates.Count; i++)
            {
                roll -= Weight(candidates[i]);
                if (roll < 0) return i;
            }
            return candidates.Count - 1;
        }

        /// <summary>
        /// A weighted pick among <paramref name="candidates"/> that skips the group's last <c>avoid repeat</c> picks (all of them when none is left).
        /// </summary>
        WwisePlaylistItem Pick(WwisePlaylistItem group, IReadOnlyList<WwisePlaylistItem> candidates)
        {
            var key = (node.Id, group.ItemId);
            if (!_player._musicHistory.TryGetValue(key, out var recent)) _player._musicHistory[key] = recent = [];
            var allowed = candidates.Where(c => !recent.Contains(c.ItemId)).ToList();
            if (allowed.Count == 0) allowed = [.. candidates];
            var pick = allowed[WeightedIndex(group, allowed)];
            recent.Add(pick.ItemId);
            while (recent.Count > Math.Min((int)group.AvoidRepeatCount, candidates.Count - 1)) recent.RemoveAt(0);
            return pick;
        }
    }

    /// <summary>
    /// A music switch: plays what its decision tree picks for the current state or switch values. When they change during playback it lets
    /// the playlist playing finish its current segment and starts the new pick at that segment's exit cue (Unknown whether the game does
    /// this or crossfades at once with the second rule; docs/formats/wwise-bnk.md#playback-semantics).
    /// </summary>
    sealed class MusicSwitchInstance(WwisePlayer player, WwiseMusicSwitch node, Instance? parent) : MusicInstance(player, node, parent)
    {
        /// <summary>
        /// The pick playing now; null when nothing does.
        /// </summary>
        MusicInstance? _active;
        /// <summary>
        /// The id of the pick playing now; 0 for none.
        /// </summary>
        uint _activeId;
        /// <summary>
        /// Whether a new pick waits for the current segment's exit cue.
        /// </summary>
        bool _pending;
        /// <summary>
        /// The id of the waiting pick; 0 for none.
        /// </summary>
        uint _pendingId;
        /// <summary>
        /// The player time the waiting pick starts at (the current segment's exit cue).
        /// </summary>
        double _switchAt;

        /// <inheritdoc/>
        public override double CurrentExit => _active is { Done: false } ? _active.CurrentExit : double.NegativeInfinity;

        /// <inheritdoc/>
        public override bool Hold
        {
            get => _active?.Hold ?? false;
            set
            {
                if (_active is not null) _active.Hold = value;
            }
        }

        public override void Start()
        {
            _activeId = Select();
            if (_activeId != 0) _active = Spawn(_activeId, StartTime) as MusicInstance;
            if (_active is null) End();
        }

        /// <summary>
        /// The music object for the current values, 0 for none: at each depth the child with the value as key, else the one keyed 0 (any).
        /// </summary>
        uint Select()
        {
            var current = node.Tree;
            foreach (var (group, _) in node.Arguments)
            {
                uint value = _player._states.GetValueOrDefault(group);
                var next = value == 0 ? null : current.Children.FirstOrDefault(c => c.Key == value);
                next ??= current.Children.FirstOrDefault(c => c.Key == 0);
                if (next is null) return 0;
                current = next;
            }
            return current.AudioNodeId;
        }

        public override void StateChanged(uint group)
        {
            if (Done) return;
            if (!node.Arguments.Any(a => a.GroupId == group))
            {
                if (_active is { Done: false }) _active.StateChanged(group);
                return;
            }
            uint target = Select();
            if (target == _activeId)
            {
                if (_pending)
                {
                    _pending = false;
                    Hold = false;
                }
            }
            else if (_active is { Done: false } && _active.CurrentExit >= _player._blockStart)
            {
                // Else only the tail of the last segment sounds and the play is over: nothing follows it.
                Hold = true;
                _pending = true;
                _pendingId = target;
                _switchAt = _active.CurrentExit;
            }
        }

        public override void Advance(double dt)
        {
            base.Advance(dt);
            if (Done || !_pending || _active is { Done: false } && _player._time <= _switchAt) return;
            _pending = false;
            _activeId = _pendingId;
            _active = _activeId == 0 ? null : Spawn(_activeId, Math.Clamp(_switchAt, _player._blockStart, _player._time)) as MusicInstance;
            if (_active is null && _children.Count == 0) End();
        }

        protected override void ChildEnded(Instance child)
        {
            if (child == _active) _active = null;
            if (_children.Count == 0 && !_pending) End();
        }
    }
}