using Meitou.Simulation;
using Meitou.Simulation.Movement;

/// <summary>
/// The <c>anim</c> mode of <c>meitou-tools probe</c>: the animation layers of the seated characters round the leader, tick by tick, and how fast
/// each clip ran (#634: bar sitters whose hands moved too fast under a mod that animates the seat clip).
/// </summary>
static class AnimationProbe
{
    /// <summary>
    /// Prints the layers of the seated characters within <paramref name="radius"/> of the leader (those named like <paramref name="name"/> when
    /// given, seated or not) for <paramref name="ticks"/> ticks, then each clip's mean rate in clip seconds per second and how often a
    /// character's set of clips changed.
    /// </summary>
    public static void Run(ProbeWorld probe, float radius, int ticks, string? name)
    {
        var animation = probe.World.Systems.OfType<AnimationSystem>().First();
        var slots = probe.Near(probe.LeaderPosition, radius).Where(i => Watched(probe, i, name)).ToList();
        var runs = slots.ToDictionary(i => i, _ => new Seen());
        var layers = new List<AnimationLayer>();
        for (int tick = 0; tick < ticks; tick++)
        {
            foreach (int i in slots)
            {
                if (probe.Characters.Cold(i)?.Animation is not { } state) continue;

                layers.Clear();
                animation.Publish(state, layers);
                runs[i].Add(layers);
                Console.WriteLine($"tick {probe.World.Tick} {i} " + string.Join("  ", layers.Select(l => $"{l.Name} t={l.Time:F3} w={l.Weight:F2}")));
            }

            probe.Tick(1);
        }

        foreach (int i in slots)
        {
            string? seat = probe.Characters.Cold(i)?.Ai?.SitClip;
            Console.WriteLine($"{probe.Who(i)} sit={seat}: {runs[i].Summary(probe.World.TickSeconds)}");
        }
    }

    /// <summary>
    /// Whether slot <paramref name="i"/> is one to print: named like <paramref name="name"/>, or seated when no name is given.
    /// </summary>
    static bool Watched(ProbeWorld probe, int i, string? name)
    {
        var cold = probe.Characters.Cold(i)!;
        return name is null ? cold.Ai?.SitClip is not null : cold.Name.Contains(name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What one character's layers did over the watched ticks.
    /// </summary>
    sealed class Seen
    {
        /// <summary>
        /// Per clip: the ticks it played after its first, the clip time it covered in them (steps back, a loop's wrap, left out) and those steps.
        /// </summary>
        readonly Dictionary<string, (int Ticks, float Seconds, int Wraps)> _clips = new(StringComparer.Ordinal);

        /// <summary>
        /// The last tick's clip times by name.
        /// </summary>
        Dictionary<string, float> _last = new(StringComparer.Ordinal);

        /// <summary>
        /// How often the set of clips differed from the tick before.
        /// </summary>
        int _changes;

        /// <summary>
        /// Takes one tick's layers.
        /// </summary>
        public void Add(List<AnimationLayer> layers)
        {
            var now = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var layer in layers) now[layer.Name] = layer.Time;
            if (_last.Count > 0 && !now.Keys.ToHashSet().SetEquals(_last.Keys)) _changes++;

            foreach (var (clip, time) in now)
            {
                if (!_last.TryGetValue(clip, out float before)) continue;

                var (ticks, seconds, wraps) = _clips.GetValueOrDefault(clip);
                _clips[clip] = time >= before ? (ticks + 1, seconds + time - before, wraps) : (ticks, seconds, wraps + 1);
            }

            _last = now;
        }

        /// <summary>
        /// The clips with their mean rate (clip seconds per second of <paramref name="tickSeconds"/> ticks) and wraps, and the changes of set.
        /// </summary>
        public string Summary(float tickSeconds)
        {
            var clips = _clips.Select(c =>
            {
                float rate = c.Value.Ticks > 0 ? c.Value.Seconds / (c.Value.Ticks * tickSeconds) : 0;
                return $"{c.Key} rate {rate:F3} wraps {c.Value.Wraps}";
            });
            return string.Join(", ", clips) + $"; clip set changed {_changes}x";
        }
    }
}
