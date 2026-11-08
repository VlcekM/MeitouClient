using Meitou.Data.Audio;

namespace Meitou.Audio;

/// <summary>
/// The music engine of <see cref="WwisePlayer"/> (docs/formats/wwise-bnk.md#playback-semantics): a music switch picks a playlist by the
/// current state or switch, a playlist walks its groups and starts each segment at the exit cue of the one before (the old one's tail plays
/// on), a segment places its tracks' clips. Starts are placed inside the block being mixed, so a loop or a hand-over is exact to one output
/// frame. Not done (no Kenshi bank needs them): stingers, transition segments, special track types, clip automation, fades of transition
/// rules, <c>continue playback</c>, music in 3D (the radio's attenuation).
/// </summary>
public sealed partial class WwisePlayer
{
    // Avoid-repeat history and sequence positions per playlist group, kept across plays.
    readonly Dictionary<(uint Playlist, uint Item), List<uint>> musicHistory = [];
    readonly Dictionary<(uint Playlist, uint Item), int> musicSteps = [];

    /// <summary>A music node that plays on a timeline: when its current segment ends, and whether it should stop after that one.</summary>
    abstract class MusicInstance(WwisePlayer player, WwiseObject node, Instance? parent) : Instance(player, node, parent)
    {
        /// <summary>Player time (seconds) of the exit cue of the segment playing now; minus infinity when none is.</summary>
        public abstract double CurrentExit { get; }

        /// <summary>When set, nothing follows the current segment (a music switch is about to replace this one at its exit cue).</summary>
        public virtual bool Hold { get; set; }

        protected Instance? Spawn(uint id, double at)
        {
            if (!Player.objects.TryGetValue(id, out var o) || Player.Create(o, this) is not { } child) return null;
            child.StartTime = at;
            Children.Add(child);
            child.Start();
            return child;
        }
    }

    /// <summary>A segment: its tracks' clips, each placed at its play-at time (segment time 0 is the entry cue minus its position).</summary>
    sealed class MusicSegmentInstance(WwisePlayer player, WwiseMusicSegment node, Instance? parent) : MusicInstance(player, node, parent)
    {
        public override double CurrentExit => StartTime + Math.Max(1, node.ExitCue - node.EntryCue) / 1000;

        public override void Start()
        {
            double zero = StartTime - node.EntryCue / 1000;
            foreach (var trackId in node.Music.Children)
            {
                if (!Player.objects.TryGetValue(trackId, out var o) || o is not WwiseMusicTrack track) continue;
                foreach (var clip in track.Clips)
                {
                    var source = track.Sources.FirstOrDefault(s => s.SourceId == clip.SourceId);
                    if (source is null) continue;
                    var sound = new SoundInstance(Player, track, clip, source, zero + (clip.PlayAt + clip.BeginTrim) / 1000, this);
                    Children.Add(sound);
                    sound.Start();
                }
            }
            if (Children.Count == 0) End(); // a silence: the playlist keeps the time
        }
    }

    /// <summary>
    /// A music random/sequence container. Segments come from walking the playlist tree lazily: a segment item plays its segment as often as
    /// its loop count says (0 forever), a group makes loop-count passes (0 forever), each pass one child (step) or all (continuous), in order
    /// (sequence) or picked by weight skipping the last <c>avoid repeat</c> picks (random).
    /// </summary>
    sealed class MusicPlaylistInstance(WwisePlayer player, WwiseMusicPlaylist node, Instance? parent) : MusicInstance(player, node, parent)
    {
        IEnumerator<uint>? walker;
        double exitTime = double.NegativeInfinity;
        bool finished;

        public override double CurrentExit => exitTime;

        public override void Start()
        {
            walker = Segments(node.Playlist).GetEnumerator();
            if (!StartNext(StartTime)) finished = true;
        }

        bool StartNext(double at)
        {
            for (int tries = 0; tries < 64 && walker!.MoveNext(); tries++)
            {
                // A segment of a bank that is not loaded is skipped.
                if (!Player.objects.TryGetValue(walker.Current, out var o) || o is not WwiseMusicSegment segment) continue;
                var instance = new MusicSegmentInstance(Player, segment, this) { StartTime = at };
                Children.Add(instance);
                instance.Start();
                exitTime = at + Math.Max(1, segment.ExitCue - segment.EntryCue) / 1000;
                return true;
            }
            return false;
        }

        public override void Advance(double dt)
        {
            base.Advance(dt);
            if (Done) return;
            double end = Player.time;
            if (!Stopping && !Hold)
                for (int guard = 0; !finished && exitTime < end && guard < 64; guard++)
                    if (!StartNext(Math.Max(exitTime, Player.blockStart))) finished = true;
            if ((finished || Hold) && Children.Count == 0 && exitTime <= end) End();
        }

        // Ends by its own rule above: not when a segment's clips end, which may be before the exit cue (a silence) or after it (a tail).
        protected override void ChildEnded(Instance child) { }

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

        /// <summary>The children one pass of a group plays, in order. Group types: 0 sequence continuous, 1 sequence step, 2 random continuous, 3 random step.</summary>
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
                    int step = Player.musicSteps.GetValueOrDefault(key);
                    Player.musicSteps[key] = step + 1;
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

        int WeightedIndex(WwisePlaylistItem group, IReadOnlyList<WwisePlaylistItem> candidates)
        {
            double Weight(WwisePlaylistItem c) => group.UsingWeight ? Math.Max(1, c.Weight) : 1;
            double roll = Player.random.NextDouble() * candidates.Sum(Weight);
            for (int i = 0; i < candidates.Count; i++)
            {
                roll -= Weight(candidates[i]);
                if (roll < 0) return i;
            }
            return candidates.Count - 1;
        }

        WwisePlaylistItem Pick(WwisePlaylistItem group, IReadOnlyList<WwisePlaylistItem> candidates)
        {
            var key = (node.Id, group.ItemId);
            if (!Player.musicHistory.TryGetValue(key, out var recent)) Player.musicHistory[key] = recent = [];
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
        MusicInstance? active;
        uint activeId;
        bool pending;
        uint pendingId;
        double switchAt;

        public override double CurrentExit => active is { Done: false } ? active.CurrentExit : double.NegativeInfinity;

        public override bool Hold
        {
            get => active?.Hold ?? false;
            set
            {
                if (active is not null) active.Hold = value;
            }
        }

        public override void Start()
        {
            activeId = Select();
            if (activeId != 0) active = Spawn(activeId, StartTime) as MusicInstance;
            if (active is null) End();
        }

        /// <summary>The music object for the current values, 0 for none: at each depth the child with the value as key, else the one keyed 0 (any).</summary>
        uint Select()
        {
            var current = node.Tree;
            foreach (var (group, _) in node.Arguments)
            {
                uint value = Player.states.GetValueOrDefault(group);
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
                if (active is { Done: false }) active.StateChanged(group);
                return;
            }
            uint target = Select();
            if (target == activeId)
            {
                if (pending)
                {
                    pending = false;
                    Hold = false;
                }
            }
            else if (active is { Done: false } && active.CurrentExit >= Player.blockStart)
            {
                // Else only the tail of the last segment sounds and the play is over: nothing follows it.
                Hold = true;
                pending = true;
                pendingId = target;
                switchAt = active.CurrentExit;
            }
        }

        public override void Advance(double dt)
        {
            base.Advance(dt);
            if (Done || !pending || active is { Done: false } && Player.time <= switchAt) return;
            pending = false;
            activeId = pendingId;
            active = activeId == 0 ? null : Spawn(activeId, Math.Clamp(switchAt, Player.blockStart, Player.time)) as MusicInstance;
            if (active is null && Children.Count == 0) End();
        }

        protected override void ChildEnded(Instance child)
        {
            if (child == active) active = null;
            if (Children.Count == 0 && !pending) End();
        }
    }
}
