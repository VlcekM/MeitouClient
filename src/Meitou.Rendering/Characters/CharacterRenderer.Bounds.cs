using System.Numerics;

namespace Meitou.Rendering.Characters;

internal sealed unsafe partial class CharacterRenderer
{
    internal static (Vector3 Centre, float Radius) BoundsOf(in CharacterInstance instance, float height, float radius, float scale) =>
        instance.WorldBounds is { IsValid: true } bounds ? (bounds.Centre, bounds.Radius)
            : (instance.Position + new Vector3(0, height * .5f * scale, 0), radius * scale);

    internal static (Vector3 Centre, float Radius) LoadingBounds(in CharacterInstance instance) =>
        instance.WorldBounds is { IsValid: true } bounds ? (bounds.Centre, bounds.Radius) : (instance.Position, 0);

    // A posed actor sphere locates shared geometry; each mesh retains its own detail and shadow-size budget.
    internal static (Vector3 Centre, float Radius, float FileRadius) PartBounds(AssetPart part, in Matrix4x4 model,
        float scale, Vector3 actorCentre, bool customBounds) =>
        (customBounds && part.Shared ? actorCentre : Vector3.Transform(part.BoundsCentre, model),
            part.BoundsRadius * scale, part.FileRadius * scale);
}
