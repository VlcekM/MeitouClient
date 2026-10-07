using Meitou.Data.Gameplay;

namespace Meitou.Simulation;

/// <summary>
/// The animations a character plays: weighted clips (a movement chain blended by speed, or a standing idle) that cross-fade at
/// the CONSTANTS <c>animation blend rate</c>. Cold state of <see cref="CharacterCold"/>, changed only by <see cref="AnimationSystem"/>
/// in the character's own partition, hashed, and published as <see cref="AnimationLayer"/>s.
/// </summary>
public sealed class CharacterAnimation
{
    public const int MaxLayers = 8;

    /// <summary>One clip in play: the library index, its own time (seconds; unsynched clips only), its weight and the weight it is fading to.</summary>
    public struct Layer
    {
        public int Definition;
        public float Time, Weight, Target;
    }

    public readonly Layer[] Layers = new Layer[MaxLayers];
    public int Count;
    /// <summary>Phase of the synched movement clips in cycles (0..1): the lower and the upper body, and a walk fading into a run, stay in step.</summary>
    public float Phase;
    /// <summary>The standing idle chosen, and the seconds it still plays before another is chosen (-1 / 0: none yet).</summary>
    public int Idle = -1;
    public float IdleLeft;

    internal void Hash(ref StateHasher h)
    {
        h.Add(Count);
        for (int i = 0; i < Count; i++)
        {
            h.Add(Layers[i].Definition);
            h.Add(Layers[i].Time);
            h.Add(Layers[i].Weight);
            h.Add(Layers[i].Target);
        }
        h.Add(Phase);
        h.Add(Idle);
        h.Add(IdleLeft);
    }
}

/// <summary>
/// Picks each character's animation layers from its movement and stance and advances their times in game time (docs/simulation.md
/// "Animation as built"). Runs after movement, in the Act phase, and writes only its own characters' cold animation state.
/// <list type="bullet">
/// <item><b>Verified</b> (fcs.def): <c>play speed</c> of a movement clip is multiplied by the movement speed ("tune until feet match ground speed"); <c>synchs</c> clips
/// of the lower and upper body share a phase; <c>normalise</c> clips total 1; weights move linearly towards their targets at the blend rate (docs/animation.md).</item>
/// <item><b>Observed / engine choice</b>: the clips of a layer form a chain by <c>move speed</c> (walk 14, jog 45, run 90 in the base data) and the character blends
/// the two around its speed; the blend rate is applied per second to every clip alike; the idle is chosen by <c>idle chance</c> and
/// kept for a time in <c>idle time min..max</c> seconds; a character counts as moving from <see cref="MovingSpeed"/> units per second.</item>
/// </list>
/// </summary>
public sealed class AnimationSystem(AnimationLibrary library, AnimationLengths lengths, float blendRate = 4) : ITickSystem, IAnimationSource
{
    /// <summary>Speed below which a character stands.</summary>
    public const float MovingSpeed = 1;

    public AnimationLibrary Library { get; } = library;
    public AnimationLengths Lengths { get; } = lengths;
    /// <summary>The CONSTANTS <c>animation blend rate</c> (fcs.def default 4, "1 is very slow"): weight per second.</summary>
    public float BlendRate { get; } = blendRate;

    public void Act(World world, Partition part, EffectBuffer effects)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        float dt = world.TickSeconds;
        var wantedLower = new List<(int Index, float Weight)>();
        var wantedUpper = new List<(int Index, float Weight)>();
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive) continue;
            var cold = table.Cold(i)!;
            var a = cold.Animation ??= new CharacterAnimation();
            var v = next[i].Velocity;
            Update(a, cold, MathF.Sqrt(v.X * v.X + v.Z * v.Z), dt, world.Seed, Rng.Key(table.IdOf(i)), world.Tick, wantedLower, wantedUpper);
        }
    }

    /// <summary>One tick of one character's animation (public for tests).</summary>
    public void Update(CharacterAnimation a, CharacterCold cold, float speed, float dt, ulong seed, ulong key, long tick,
        List<(int Index, float Weight)>? lowerScratch = null, List<(int Index, float Weight)>? upperScratch = null)
    {
        var lower = lowerScratch ?? [];
        var upper = upperScratch ?? [];
        lower.Clear();
        upper.Clear();
        var stance = new AnimationStance { Right = new HandHold(cold.DrawnWeapon) };
        bool moving = speed >= MovingSpeed;
        if (moving)
        {
            Library.Movement(AnimationArea.Lower, speed, stance, lower);
            Library.Movement(AnimationArea.Upper, speed, stance, upper);
            moving = lower.Count > 0 || upper.Count > 0;
        }
        var wanted = lower;
        if (moving) wanted.AddRange(upper);
        else
        {
            wanted.Clear();
            // Standing: keep the idle until its time is up, then choose another by chance.
            if (a.Idle < 0 || a.IdleLeft <= 0 || !Library.Definitions[a.Idle].Fits(stance))
            {
                var idles = Library.Idles(stance);
                float total = 0;
                foreach (var (_, w) in idles) total += w;
                if (idles.Count > 0)
                {
                    ulong roll = Rng.Hash(seed, key, RngPurpose.Animation, (ulong)tick);
                    float pick = Rng.Float(roll) * total;
                    int chosen = idles[^1].Index;
                    foreach (var (index, w) in idles)
                    {
                        if ((pick -= w) < 0) { chosen = index; break; }
                    }
                    var d = Library.Definitions[chosen];
                    a.Idle = chosen;
                    a.IdleLeft = d.IdleTimeMin + Rng.Float(Rng.Mix(roll)) * Math.Max(d.IdleTimeMax - d.IdleTimeMin, 0);
                    // A new idle starts from its first frame.
                    for (int k = 0; k < a.Count; k++) if (a.Layers[k].Definition == chosen) a.Layers[k].Time = 0;
                }
                else a.Idle = -1;
            }
            if (a.Idle >= 0)
            {
                wanted.Add((a.Idle, 1));
                a.IdleLeft -= dt;
            }
        }

        // Targets: what is wanted fades in, everything else out.
        for (int k = 0; k < a.Count; k++) a.Layers[k].Target = 0;
        foreach (var (index, weight) in wanted)
        {
            int k = Find(a, index);
            if (k < 0)
            {
                k = a.Count < CharacterAnimation.MaxLayers ? a.Count++ : Evict(a);
                if (k < 0) continue;
                a.Layers[k] = new CharacterAnimation.Layer { Definition = index };
            }
            a.Layers[k].Target = weight;
        }

        // The phase of the synched movement clips advances at the rate of the strongest one: the speed times its play speed (the clip's
        // own seconds per second), in cycles of its length.
        int lead = -1;
        float leadWeight = 0;
        for (int k = 0; k < a.Count; k++)
        {
            var d = Library.Definitions[a.Layers[k].Definition];
            if (d.Synchs && d.MoveSpeed > 0 && a.Layers[k].Weight + a.Layers[k].Target > leadWeight) (lead, leadWeight) = (k, a.Layers[k].Weight + a.Layers[k].Target);
        }
        if (lead >= 0)
        {
            var d = Library.Definitions[a.Layers[lead].Definition];
            a.Phase += speed * d.PlaySpeed * dt / Lengths.Of(d.Clip);
            a.Phase -= MathF.Floor(a.Phase);
        }

        float step = BlendRate * dt;
        int kept = 0;
        for (int k = 0; k < a.Count; k++)
        {
            ref var layer = ref a.Layers[k];
            var d = Library.Definitions[layer.Definition];
            layer.Weight = layer.Weight < layer.Target ? MathF.Min(layer.Weight + step, layer.Target) : MathF.Max(layer.Weight - step, layer.Target);
            if (!d.Synchs || d.MoveSpeed <= 0) layer.Time += dt * (d.PlaySpeed > 0 ? d.PlaySpeed : 1);
            if (layer.Weight <= 0 && layer.Target <= 0) continue;   // faded out: dropped
            a.Layers[kept++] = layer;
        }
        a.Count = kept;
    }

    static int Find(CharacterAnimation a, int definition)
    {
        for (int k = 0; k < a.Count; k++) if (a.Layers[k].Definition == definition) return k;
        return -1;
    }

    /// <summary>Frees the faintest fading-out layer when all are in use; -1 when every layer is wanted.</summary>
    static int Evict(CharacterAnimation a)
    {
        int worst = -1;
        for (int k = 0; k < a.Count; k++)
            if (a.Layers[k].Target <= 0 && (worst < 0 || a.Layers[k].Weight < a.Layers[worst].Weight)) worst = k;
        return worst;
    }

    /// <summary>The layers to publish for a snapshot: the record name (the renderer finds its track masks by it), the clip time in seconds and the weight.</summary>
    public IReadOnlyList<AnimationLayer> Publish(CharacterAnimation a)
    {
        var list = new List<AnimationLayer>(a.Count);
        for (int k = 0; k < a.Count; k++)
        {
            var layer = a.Layers[k];
            if (layer.Weight <= 0) continue;
            var d = Library.Definitions[layer.Definition];
            float time = d.Synchs && d.MoveSpeed > 0
                ? (a.Phase + d.SynchOffset - MathF.Floor(a.Phase + d.SynchOffset)) * Lengths.Of(d.Clip)
                : layer.Time;
            list.Add(new AnimationLayer(d.Name, time, layer.Weight));
        }
        return list;
    }
}

/// <summary>What the world asks to turn a character's animation state into the layers a snapshot carries.</summary>
public interface IAnimationSource
{
    IReadOnlyList<AnimationLayer> Publish(CharacterAnimation animation);
}
