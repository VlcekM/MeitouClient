using Meitou.Data.Characters;
using Meitou.Simulation;

namespace Meitou.Modding;

/// <summary>
/// What a face controller is told about the character it drives, once per frame (docs/face-shape-keys.md).
/// </summary>
/// <param name="Character">The character's latest tick snapshot (animations, combat, health, hunger, bed...); read only.</param>
/// <param name="Seconds">The simulation clock at the frame, interpolation included: it stands still while the game is paused, runs with the game speed, and advances by tick length times the step when stepped.</param>
/// <param name="CameraDistance">The distance from the camera to the character, in world units, for a mod that limits how many faces it drives.</param>
public readonly record struct FaceFrame(CharacterSnapshot Character, double Seconds, float CameraDistance);

/// <summary>
/// Drives characters' faces: asked once a frame, on the host's thread, for each character that is drawn (or has a portrait), has a built look whose body mesh carries
/// shape keys (poses), and is within reach. It fills <paramref name="face"/> (cleared before the first controller of the frame; <see cref="FaceWeights.Available"/>
/// lists the character's shape keys) and the renderer applies the weights on top of the appearance's own, rebuilding the character's morph only when they changed.
/// Presentation only: it must not touch the world and does not take part in the state hash. A throw is logged and disables the controller for the session.
/// </summary>
public interface IFaceController
{
    void Update(in FaceFrame frame, FaceWeights face);
}
