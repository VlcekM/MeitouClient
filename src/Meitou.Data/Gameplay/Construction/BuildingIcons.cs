using System.Numerics;

namespace Meitou.Data.Gameplay.Construction;

/// <summary>
/// The build window's picture of a building (docs/game/ui-screens.md "Building icons"): the game's saved picture <c>data/icons/buildings/&lt;string
/// id&gt;.png</c> when there is one (<see cref="Find"/>), else drawn from the building's parts as <see cref="BuildingIconStyle"/> describes.
/// </summary>
public sealed class BuildingIcons
{
    /// <summary>
    /// The saved pictures by BUILDING string id (the file name without <c>.png</c>), to the file's path.
    /// </summary>
    readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    /// <summary>
    /// Indexes the PNGs of <paramref name="directory"/> (empty when it does not exist).
    /// </summary>
    public BuildingIcons(string directory)
    {
        if (!Directory.Exists(directory)) return;

        foreach (string file in Directory.EnumerateFiles(directory, "*.png"))
            _files[Path.GetFileNameWithoutExtension(file)] = file;
    }

    /// <summary>
    /// The saved picture of the BUILDING <paramref name="record"/> (its string id), null when none.
    /// </summary>
    public string? Find(string record) => _files.GetValueOrDefault(record);
}

/// <summary>
/// How the game draws a building's picture (FUN_14084af40, its call in the build window FUN_1404e2040, FUN_140845f30 and FUN_140845bf0,
/// <b>Verified</b> against the disassembly): a square of <see cref="Size"/> pixels; the building's node pitched by <see cref="Pitch"/> then yawed by
/// <see cref="Yaw"/> about its own axes, yawed once more by <see cref="Yaw"/> when its bounds are not wider than <see cref="WideRatio"/> times deep;
/// a perspective camera looking down -Z, pulled back from the bounds' centre until every corner is in view.
/// </summary>
public static class BuildingIconStyle
{
    /// <summary>
    /// The picture's width and height in pixels.
    /// </summary>
    public const int Size = 128;

    /// <summary>
    /// The camera's vertical field of view (degrees), its near and far clip distances.
    /// </summary>
    public const float FieldOfView = 45, Near = 0.1f, Far = 2000;

    /// <summary>
    /// The node's turns (degrees): pitch first, then yaw.
    /// </summary>
    public const float Pitch = 30, Yaw = -90;

    /// <summary>
    /// A building whose bounds are less than this many times as wide (X) as deep (Z) after the turns is yawed again, so its long side faces
    /// the camera.
    /// </summary>
    public const float WideRatio = 1.2f;

    /// <summary>
    /// The node's rotation and the camera for a building of the given parts: each part's mesh bounds (<paramref name="parts"/>: local box and the
    /// part's transform in the building). <c>Model</c> is the node's rotation (row-vector form: a part's world matrix is its transform times it).
    /// Null without parts.
    /// </summary>
    public static (Matrix4x4 Model, Matrix4x4 View, Matrix4x4 Projection)? Matrices(
        IReadOnlyList<(Vector3 Min, Vector3 Max, Matrix4x4 Transform)> parts)
    {
        if (parts.Count == 0) return null;

        float rad = MathF.PI / 180;

        // Ogre turns a node in its own axes, so pitch then yaw is v' = Rx(Ry v): Ry * Rx in row-vector form.
        var model = Matrix4x4.CreateRotationY(Yaw * rad) * Matrix4x4.CreateRotationX(Pitch * rad);
        var (min, max) = Bounds(parts, model);
        var half = (max - min) * 0.5f;
        if (half.X < half.Z * WideRatio)
        {
            model = Matrix4x4.CreateRotationY(Yaw * rad) * model;
            (min, max) = Bounds(parts, model);
            half = (max - min) * 0.5f;
        }

        // FUN_140845bf0: from the centre, back along the view axis by the largest distance at which a corner's ray meets a frustum plane (the far
        // plane left out): the front face's corners land on the side planes, or on the near plane.
        var centre = (min + max) * 0.5f;
        float tan = MathF.Tan(FieldOfView * rad / 2);
        float back = half.Z + MathF.Max(MathF.Max(half.X / tan, half.Y / tan), Near);
        var eye = centre + new Vector3(0, 0, back);
        var view = Matrix4x4.CreateLookAt(eye, eye - Vector3.UnitZ, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView * rad, 1, Near, Far);
        return (model, view, projection);
    }

    /// <summary>
    /// The axis-aligned box of every part's box corners turned by <paramref name="model"/> (Ogre merges the entities' world boxes).
    /// </summary>
    static (Vector3 Min, Vector3 Max) Bounds(IReadOnlyList<(Vector3 Min, Vector3 Max, Matrix4x4 Transform)> parts, Matrix4x4 model)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var (lo, hi, transform) in parts)
        {
            var m = transform * model;
            for (int c = 0; c < 8; c++)
            {
                var corner = new Vector3((c & 1) == 0 ? lo.X : hi.X, (c & 2) == 0 ? lo.Y : hi.Y, (c & 4) == 0 ? lo.Z : hi.Z);
                var p = Vector3.Transform(corner, m);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }

        return (min, max);
    }
}
