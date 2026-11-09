using System.Numerics;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// The part of the full view the world frame draws: a tiled screenshot (<c>screens.cfg</c> <c>multiplier</c> n, docs/game/ui-input.md "Screenshots")
/// draws n x n tiles, each with the camera frustum offset to its sub-window, as the original does (here with a guard band of pixels round each tile,
/// so what samples neighbours has them at the tile's border). In the NDC of a frame drawn with the whole view's field of view and the drawn size's
/// aspect, a tile's NDC is <c>ndc' = Scale * ndc - Offset</c>; <see cref="Current"/> is the tile being drawn (<see cref="Whole"/> otherwise).
/// Everything that builds a projection (<see cref="WorldCamera.Projection"/>) or rebuilds a view ray from <c>right</c>, <c>up</c>, <c>back</c> and
/// the tangents of half the field of view goes through here: <see cref="Tan"/> and <see cref="Back"/> keep
/// <c>ray = right * ndc.x * tan.x + up * ndc.y * tan.y - back</c> true in the tile's NDC; <see cref="ToTile"/> is the other way.
/// </summary>
public readonly record struct ViewWindow(float Scale, float OffsetX, float OffsetY)
{
    /// <summary>
    /// The whole view: scale 1, no offset (no tiled screenshot).
    /// </summary>
    public static readonly ViewWindow Whole = new(1, 0, 0);

    /// <summary>
    /// The tile being drawn; set by the host for each tile of a tiled screenshot and put back to <see cref="Whole"/> after. Drawing is
    /// single-threaded.
    /// </summary>
    public static ViewWindow Current { get; set; } = Whole;

    /// <summary>
    /// Tile (<paramref name="column"/>, <paramref name="row"/>, row 0 at the top) of an <paramref name="n"/> x n grid of <paramref name="width"/> x
    /// <paramref name="height"/> tiles, drawn <paramref name="guard"/> pixels larger on each side.
    /// </summary>
    public static ViewWindow Tile(int n, int column, int row, int width, int height, int guard)
    {
        float sx = n * width / (float)(width + 2 * guard), sy = n * height / (float)(height + 2 * guard);
        return new(sy, sx * ((2f * column + 1) / n - 1), sy * (1 - (2f * row + 1) / n));
    }

    /// <summary>
    /// Whether this is <see cref="Whole"/>, so projections pass through unchanged.
    /// </summary>
    public bool IsWhole => Scale == 1 && OffsetX == 0 && OffsetY == 0;

    /// <summary>
    /// The projection of the tile for the whole view's <paramref name="projection"/>.
    /// </summary>
    public Matrix4x4 Apply(Matrix4x4 projection)
    {
        if (IsWhole) return projection;
        var crop = new Matrix4x4(Scale, 0, 0, 0, 0, Scale, 0, 0, 0, 0, 1, 0, -OffsetX, -OffsetY, 0, 1);
        return projection * crop;
    }

    /// <summary>
    /// The whole view's NDC as the tile's.
    /// </summary>
    public Vector2 ToTile(Vector2 ndc) => new(Scale * ndc.X - OffsetX, Scale * ndc.Y - OffsetY);

    /// <summary>
    /// The tangents of half the field of view to give a shader for the tile.
    /// </summary>
    public static Vector2 Tan(float tanX, float tanY) => new(tanX / Current.Scale, tanY / Current.Scale);

    /// <summary>
    /// The view's <c>back</c> axis shifted for the tile (the sub-window's centre looks off the axis), for the tangents of the whole view.
    /// </summary>
    public static Vector3 Back(Matrix4x4 rotation, float tanX, float tanY)
    {
        var back = new Vector3(rotation.M13, rotation.M23, rotation.M33);
        var w = Current;
        if (w.IsWhole) return back;
        var right = new Vector3(rotation.M11, rotation.M21, rotation.M31);
        var up = new Vector3(rotation.M12, rotation.M22, rotation.M32);
        return back - right * (w.OffsetX / w.Scale * tanX) - up * (w.OffsetY / w.Scale * tanY);
    }

    /// <summary>
    /// The vertical field of view of the tile (the vendor upscalers' camera).
    /// </summary>
    public static float FieldOfView(float fieldOfView) => 2 * MathF.Atan(MathF.Tan(fieldOfView * 0.5f) / Current.Scale);

    /// <summary>
    /// <see cref="Back"/> for a vertical field of view and aspect.
    /// </summary>
    public static Vector3 BackFor(Matrix4x4 rotation, float fieldOfView, float aspect)
    {
        float tanY = MathF.Tan(fieldOfView * 0.5f);
        return Back(rotation, tanY * aspect, tanY);
    }
}