using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Game.Rendering.Terrain;

/// <summary>
/// Uploaded fine height windows round kept points far from the eye's window (<see cref="TerrainStreamer.Kept"/>), swapped in when the eye arrives.
/// </summary>
sealed class HeightStandbys
{
    /// <summary>
    /// A kept point this near the drawn window's centre (a fraction of its width) needs no standby: a jump there keeps the window's fine heights in
    /// view while the window moves.
    /// </summary>
    const double CoverFraction = 0.3;

    /// <summary>
    /// The most standby windows, however many places are kept.
    /// </summary>
    const int Ceiling = 8;

    /// <summary>
    /// The streamer whose kept points and places the standbys follow.
    /// </summary>
    readonly TerrainStreamer _streamer;

    /// <summary>
    /// The renderer that uploads, swaps and drops the windows.
    /// </summary>
    readonly TerrainRenderer _terrain;

    /// <summary>
    /// The uploaded standbys.
    /// </summary>
    readonly List<TerrainRenderer.FineStandby> _windows = [];

    /// <summary>
    /// No standbys yet; they follow <paramref name="streamer"/>'s kept points and <paramref name="terrain"/> uploads them.
    /// </summary>
    public HeightStandbys(TerrainStreamer streamer, TerrainRenderer terrain)
    {
        _streamer = streamer;
        _terrain = terrain;
    }

    /// <summary>
    /// A standby is being uploaded (<see cref="BeginUpload"/>): no other window job starts until it is ready.
    /// </summary>
    public bool Uploading { get; private set; }

    /// <summary>
    /// Uploads a window the worker read for a standby through <paramref name="uploads"/>; kept once it is resident.
    /// </summary>
    public void BeginUpload(HeightWindow window, TerrainHeightBounds.Patch bounds, UploadQueue uploads)
    {
        Uploading = true;
        _terrain.BeginFineStandby(window, bounds, uploads, s =>
        {
            _windows.Add(s);
            Uploading = false;
        });
    }

    /// <summary>
    /// Draws the first standby <paramref name="ready"/> accepts (it is round where the eye now is), keeping the window it replaces as a standby for
    /// where the eye was; false when none is.
    /// </summary>
    public bool TrySwapIn(Predicate<TerrainRenderer.FineStandby> ready)
    {
        int index = _windows.FindIndex(ready);
        if (index < 0) return false;

        _windows[index] = _terrain.SwapFine(_windows[index]);
        return true;
    }

    /// <summary>
    /// The first kept point (<see cref="TerrainStreamer.Kept"/>) that is neither near the drawn window's centre nor inside a standby's middle, making
    /// room for its standby: in place of a standby no kept point is in the middle of (or a new one, up to <see cref="MaxStandbys"/>). False when there
    /// is no such point or no room.
    /// </summary>
    public bool TryPickPoint(out Vector3 point)
    {
        point = default;
        foreach (var candidate in _streamer.Kept)
        {
            if (TerrainStreamer.Within(_terrain.Fine, candidate, CoverFraction)) continue;
            if (_windows.Any(s => TerrainStreamer.Within(s.Window, candidate, TerrainStreamer.RecentreFraction))) continue;

            if (_windows.Count >= MaxStandbys)
            {
                int idle = _windows.FindIndex(s => !_streamer.Kept.Any(k => TerrainStreamer.Within(s.Window, k, TerrainStreamer.RecentreFraction)));
                if (idle < 0) return false;

                _terrain.DropFine(_windows[idle]);
                _windows.RemoveAt(idle);
            }

            point = candidate;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Lets every standby go.
    /// </summary>
    public void DropAll()
    {
        foreach (var s in _windows) _terrain.DropFine(s);
        _windows.Clear();
    }

    /// <summary>
    /// At most this many standby windows (about 5 MB of video memory and 5 MB of memory each at step 1): one per kept place
    /// (<see cref="TerrainStreamer.Areas"/> less the eye's), at least 3, at most <see cref="Ceiling"/>.
    /// </summary>
    int MaxStandbys => Math.Clamp(_streamer.Areas - 1, 3, Ceiling);
}
