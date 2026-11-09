using Meitou.Game.Rendering.Frame;
using Meitou.Simulation;

namespace Meitou.Game.Editor;

/// <summary>
/// The character editor's idle stances on the edited character (docs/characters.md "Character editor"): the chosen one faded in, the ones chosen
/// before faded out.
/// </summary>
sealed class EditorStances(GameHost host)
{
    /// <summary>
    /// The stances and their weights, of the character last shown.
    /// </summary>
    readonly List<(string Stance, float Weight)> _editorStances = [];

    /// <summary>
    /// The character the stances are of.
    /// </summary>
    CharacterId _editorStancesOf;

    /// <summary>
    /// When the stances were last stepped (real seconds).
    /// </summary>
    double _editorStancesAt;

    /// <summary>
    /// The edited character's poses: the chosen idle stance at its record's play speed, as the simulation plays it (0 for the stances: a held pose of
    /// a 1-frame clip; 0.01 for squat; a mod's one-shot stance held at its last frame, <see cref="EditorBarScene.EditorClipTime"/>), cross-faded
    /// from the stances chosen before at the simulation's blend rate (<c>animation blend rate</c>).
    /// </summary>
    internal CharacterPose[] EditorStancePoses(CharacterId id, string stance)
    {
        double now = host.RealTime.Elapsed.TotalSeconds;
        if (id != _editorStancesOf || _editorStances.Count == 0)
        {
            _editorStances.Clear();
            _editorStances.Add((stance, 1));
            _editorStancesOf = id;
        }
        else if (!_editorStances.Exists(s => s.Stance == stance)) _editorStances.Add((stance, 0));
        float step = (float)(now - _editorStancesAt) * (host.PopData?.Bodies.Constants.AnimationBlendRate ?? 4);
        _editorStancesAt = now;
        for (int i = _editorStances.Count - 1; i >= 0; i--)
        {
            var (name, weight) = _editorStances[i];
            weight = name == stance ? MathF.Min(weight + step, 1) : weight - step;
            if (weight <= 0) _editorStances.RemoveAt(i);
            else _editorStances[i] = (name, weight);
        }
        return [.. _editorStances.Select(s => new CharacterPose(s.Stance, host.CharacterEditor.Bar.EditorClipTime(s.Stance, (float)now), s.Weight))];
    }
}
