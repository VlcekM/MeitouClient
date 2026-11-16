using Meitou.Data.Gameplay;
using Meitou.Game.Rendering.Frame;
using Meitou.Simulation;

namespace Meitou.Game.Editor;

/// <summary>
/// The animals in the character editor (docs/characters.md "Character editor"), as the original's: the world stands paused behind the modal editor;
/// each time an animal is shown, one of its idles is drawn and asserted (<c>FUN_1405efd60</c>), and while it is shown its layers are stepped by real
/// time (<c>FUN_1405f16a0</c>, <see cref="EditorAnimalLayers"/>). An animal not shown keeps its layers as they stand.
/// </summary>
sealed class EditorAnimalIdles(GameHost host)
{
    /// <summary>
    /// The frame time the original's visual update takes while the world is paused (<c>FUN_140788a00</c>: <c>DAT_141682ad8</c>, 0.01 s). A
    /// character in the camera's view puts no time by for it (<c>FUN_1405c9f60</c>), and the editor's characters stand together in view.
    /// </summary>
    const float PausedFrame = 0.01f;

    /// <summary>
    /// The editor's opening the layers belong to.
    /// </summary>
    CharacterEditorPanel.EditorSession? _opening;

    /// <summary>
    /// The animal shown in the frames before; null while a person or nothing is shown.
    /// </summary>
    CharacterId? _shown;

    /// <summary>
    /// When the shown animal's layers were last stepped (real seconds).
    /// </summary>
    double _steppedAt;

    /// <summary>
    /// The layers of each animal shown in this opening of the editor.
    /// </summary>
    readonly Dictionary<CharacterId, EditorAnimalLayers> _layers = [];

    /// <summary>
    /// Editor animal <paramref name="id"/>'s poses; <paramref name="poses"/> (the paused world's) before it was first shown.
    /// </summary>
    internal CharacterPose[] Poses(CharacterId id, string recordId, CharacterPose[] poses)
    {
        var editor = host.Player.CharacterEditor;
        if (editor.Editor is not { } session || host.AnimationLibrary is not { } library || library.Animal(recordId) is not { } animal)
            return poses;
        if (session != _opening) Open(session);

        if (editor.EditorFocus?.Character != id)
        {
            if (!_layers.TryGetValue(id, out var standing)) return poses;

            // Another character shown: the one shown before has its whole update once (FUN_1405efd60), by the paused frame time.
            if (_shown == id)
            {
                _shown = null;
                standing.Step(PausedFrame, animal.RaceId);
            }

            return standing.Poses();
        }

        double now = host.RealTime.Elapsed.TotalSeconds;
        var layers = _shown == id ? _layers[id] : Show(id, animal, poses, now);
        layers.Step((float)(now - _steppedAt), animal.RaceId);
        _steppedAt = now;

        return layers.Poses();
    }

    /// <summary>
    /// A new opening of the editor: no animal's layers yet.
    /// </summary>
    void Open(CharacterEditorPanel.EditorSession session)
    {
        _opening = session;
        _shown = null;
        _layers.Clear();
    }

    /// <summary>
    /// The animal has just been shown: its layers (the paused world's the first time) have their whole update once by the paused frame time
    /// (<c>FUN_1405efd60</c> -> Character slot 28), then a drawn idle is asserted.
    /// </summary>
    EditorAnimalLayers Show(CharacterId id, AnimalClips animal, CharacterPose[] poses, double now)
    {
        if (!_layers.TryGetValue(id, out var layers))
        {
            float blendRate = host.PopData?.Bodies.Constants.AnimationBlendRate ?? 4;
            layers = new EditorAnimalLayers(host.AnimationLibrary!, host.AnimationLengths ?? AnimationLengths.Empty, blendRate);
            layers.Seed(poses, animal);
            _layers[id] = layers;
        }
        layers.Step(PausedFrame, animal.RaceId);

        int drawn = DrawIdle(animal);
        if (drawn >= 0) layers.Assert(drawn);

        (_shown, _steppedAt) = (id, now);
        return layers;
    }

    /// <summary>
    /// One of <paramref name="animal"/>'s idles whose clip its skeleton has, drawn by its <c>idle chance</c> (the idle draw, <c>FUN_14051ec80</c>);
    /// -1 when it has none.
    /// </summary>
    int DrawIdle(AnimalClips animal)
    {
        var idles = animal.Idles.Where(x => x.Weight > 0 && HasClip(animal, x.Index)).ToList();
        if (idles.Count == 0) return -1;

        float pick = Random.Shared.NextSingle() * idles.Sum(x => x.Weight);
        foreach (var (index, weight) in idles)
        {
            if ((pick -= weight) < 0) return index;
        }

        return idles[^1].Index;
    }

    /// <summary>
    /// Whether <paramref name="animal"/>'s skeleton has the clip of definition <paramref name="index"/>, as the simulation's plan keeps its clips
    /// (<c>AnimalPlans</c>: a race whose skeleton was not read keeps them all).
    /// </summary>
    bool HasClip(AnimalClips animal, int index)
    {
        var lengths = host.AnimationLengths;
        bool known = animal.RaceId is { } race && lengths?.HasSpecies(race) == true;

        return !known || lengths!.For(animal.RaceId).Has(host.AnimationLibrary!.Definitions[index].Clip);
    }
}
