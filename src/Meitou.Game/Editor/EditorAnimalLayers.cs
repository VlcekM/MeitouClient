using Meitou.Data.Gameplay;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Editor;

/// <summary>
/// One editor animal's animation layers while the world stands paused (docs/characters.md "Character editor"): the idle the editor asserts and the
/// layers fading out, stepped by real time as the original's window steps the shown character's layer managers (<c>FUN_1405b6d00</c>), by the rules
/// of the simulation's (docs/animation.md "Layer managers").
/// </summary>
sealed class EditorAnimalLayers(AnimationLibrary library, AnimationLengths lengths, float blendRate)
{
    /// <summary>
    /// A layer: its definition (<see cref="AnimationLibrary.Definitions"/>), clip time, weight and whether it is the asserted one.
    /// </summary>
    record struct Layer(int Definition, float Time, float Weight, bool Wanted);

    /// <summary>
    /// The weight step's body-half factors and the play-rate clamp (<c>AnimationSystem.BlendStep</c>).
    /// </summary>
    const float LowerBlend = 2, UpperBlend = 1.25f, OverlayBlend = 1, MinPlayBlend = 0.5f, MaxPlayBlend = 10;

    /// <summary>
    /// The layers, the asserted one among them.
    /// </summary>
    readonly List<Layer> _layers = [];

    /// <summary>
    /// The layers of the paused world's <paramref name="poses"/> whose clips are <paramref name="animal"/>'s, the heaviest asserted.
    /// </summary>
    internal void Seed(CharacterPose[] poses, AnimalClips animal)
    {
        _layers.Clear();
        int heaviest = -1;
        foreach (var pose in poses)
        {
            int index = Array.FindIndex(animal.Idles, x => library.Definitions[x.Index].Name == pose.Animation);
            if (index < 0) continue;

            _layers.Add(new Layer(animal.Idles[index].Index, pose.Time, pose.Weight, false));
            if (heaviest < 0 || pose.Weight > _layers[heaviest].Weight) heaviest = _layers.Count - 1;
        }

        if (heaviest >= 0) _layers[heaviest] = _layers[heaviest] with { Wanted = true };
    }

    /// <summary>
    /// Asserts idle <paramref name="definition"/> (<c>FUN_1405b7ac0</c> -> <c>FUN_1405b5270</c>): the layer playing it goes on, one fading out of it
    /// comes back with its time, else a new layer starts from the first frame; the others fade out.
    /// </summary>
    internal void Assert(int definition)
    {
        bool found = false;
        for (int i = 0; i < _layers.Count; i++)
        {
            bool wanted = _layers[i].Definition == definition;
            found |= wanted;
            _layers[i] = _layers[i] with { Wanted = wanted };
        }

        if (!found) _layers.Add(new Layer(definition, 0, _layers.Count == 0 ? 1 : 0, true));
    }

    /// <summary>
    /// Steps the layers by <paramref name="dt"/> seconds: weights toward their targets, times by the record's play speed (a fading one-shot stands
    /// still, a one-shot holds its last frame), the faded-out layers dropped.
    /// </summary>
    internal void Step(float dt, string? raceId)
    {
        var clips = lengths.For(raceId);
        for (int i = _layers.Count - 1; i >= 0; i--)
        {
            var layer = _layers[i];
            var d = library.Definitions[layer.Definition];
            float step = WeightStep(d, layer.Wanted) * dt;
            float weight = layer.Wanted ? MathF.Min(layer.Weight + step, 1) : layer.Weight - step;
            if (weight <= 0 && !layer.Wanted)
            {
                _layers.RemoveAt(i);
                continue;
            }

            float time = layer.Wanted || d.Loop ? layer.Time + dt * d.PlaySpeed : layer.Time;
            if (!d.Loop) time = Math.Clamp(time, 0, clips.Of(d.Clip) * 0.999f);
            _layers[i] = layer with { Time = time, Weight = weight };
        }
    }

    /// <summary>
    /// The layers as poses, the asserted one filling what the fading ones leave of 1 (<c>AnimationSystem.Publish</c>).
    /// </summary>
    internal CharacterPose[] Poses()
    {
        float fading = 0;
        foreach (var layer in _layers) if (!layer.Wanted) fading += layer.Weight;

        float rest = MathF.Max(1 - fading, 0);
        return [.. _layers.Select(l => new CharacterPose(library.Definitions[l.Definition].Name, l.Time, l.Wanted ? rest : l.Weight))];
    }

    /// <summary>
    /// How fast a layer's weight moves per second: the blend rate times its body half's factor, times its play speed clamped to 0.5..10 while it
    /// is asserted.
    /// </summary>
    float WeightStep(AnimationDefinition d, bool wanted)
    {
        float half = d.Area switch { AnimationArea.Lower => LowerBlend, AnimationArea.Overlay => OverlayBlend, _ => UpperBlend };
        if (!wanted) return blendRate * half;

        return blendRate * half * Math.Clamp(d.PlaySpeed, MinPlayBlend, MaxPlayBlend);
    }
}
