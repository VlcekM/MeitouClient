using System.Diagnostics;

namespace Meitou.Game.Ui;

/// <summary>
/// The code mods' map marker image files on the map (docs/modding.md "Map markers"): whether each can be shown (<see cref="MarkerImageFile"/>, and
/// whether the painter could decode it), checked again a second after a refusal so a file a mod writes while the game runs is seen; and their
/// textures, freed once no marker of the frame names them any more.
/// </summary>
sealed class ModMarkerImages(PlayerInterface player)
{
    /// <summary>
    /// How long a refused file's verdict stands before the file is looked at again.
    /// </summary>
    static readonly TimeSpan RecheckAfter = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Each file's last verdict (null: fine) and when it was reached.
    /// </summary>
    readonly Dictionary<string, (string? Problem, long At)> _checked = new(StringComparer.Ordinal);

    /// <summary>
    /// The files the markers of this frame name.
    /// </summary>
    HashSet<string> _shown = new(StringComparer.Ordinal);

    /// <summary>
    /// The files the markers of the last frame named.
    /// </summary>
    HashSet<string> _shownBefore = new(StringComparer.Ordinal);

    /// <summary>
    /// Why the image file <paramref name="path"/> (full) cannot be shown, or null when it can; asking counts it as named this frame.
    /// </summary>
    internal string? Problem(string path)
    {
        long now = Stopwatch.GetTimestamp();
        bool known = _checked.TryGetValue(path, out var last);
        if (known && last.Problem is { } refused && Stopwatch.GetElapsedTime(last.At, now) < RecheckAfter) return refused;

        // A file the painter could not decode is refused for a while: its texture is then forgotten (EndFrame), which would decode it again.
        string? problem = player.UiPainter?.IconFailed(path) == true ? "the file could not be read as an image"
            : known && last.Problem is null ? null
            : MarkerImageFile.Problem(path);
        bool stillFine = known && problem is null && last.Problem is null;
        if (!stillFine) _checked[path] = (problem, now);

        if (problem is null) _shown.Add(path);
        return problem;
    }

    /// <summary>
    /// After a frame's markers: frees the textures of the files the last frame named and this one did not, and starts the next frame.
    /// </summary>
    internal void EndFrame()
    {
        if (player.UiPainter is { } painter)
        {
            foreach (string path in _shownBefore)
                if (!_shown.Contains(path)) painter.ForgetIcon(path);
        }

        (_shownBefore, _shown) = (_shown, _shownBefore);
        _shown.Clear();
    }
}
