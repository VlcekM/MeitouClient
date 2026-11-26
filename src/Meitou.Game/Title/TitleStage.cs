using System.Numerics;

namespace Meitou.Game.Title;

/// <summary>
/// The title fight's places for the Beak Thing easter egg (<see cref="TitleAmbush"/>), as drawn, and the one scale between the fight's world and its
/// drawing: a drawn point is <see cref="Origin"/> plus its world offset from it times <see cref="Scale"/> across the ground; heights over the ground
/// grow by the scale too.
/// </summary>
/// <param name="Origin">The point that stays put between the world and the drawing (the middle fight's centre).</param>
/// <param name="Scale">How much larger than life the fight is drawn.</param>
/// <param name="Entry">Where the Beak Thing waits, off the right of the frame.</param>
/// <param name="Landing">Where its first blow throws the far pair: the left of the frame.</param>
/// <param name="Wanderer">Where the wanderer stands on the plateau.</param>
/// <param name="Eye">The title camera's eye.</param>
readonly record struct TitleStage(Vector3 Origin, float Scale, Vector3 Entry, Vector3 Landing, Vector3 Wanderer, Vector3 Eye)
{
    /// <summary>
    /// The drawn point over the world point <paramref name="world"/>, across the ground only (the height is the drawing's business).
    /// </summary>
    public Vector2 Drawn(Vector3 world) => new(Origin.X + (world.X - Origin.X) * Scale, Origin.Z + (world.Z - Origin.Z) * Scale);

    /// <summary>
    /// The world point under the drawn point <paramref name="drawn"/>, across the ground only.
    /// </summary>
    public Vector2 World(Vector3 drawn) => new(Origin.X + (drawn.X - Origin.X) / Scale, Origin.Z + (drawn.Z - Origin.Z) / Scale);
}
