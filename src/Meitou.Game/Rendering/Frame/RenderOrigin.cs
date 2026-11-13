using System.Text.RegularExpressions;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// Rendering relative to the eye (docs/renderer-native.md "Render origin"). Far from the world's origin a float steps by 0.004 units at 50000; a
/// world-space position projected by a view-projection that carries the eye's world position cancels those large numbers in float, a rounding
/// that changes with the camera, so close-up geometry shook against the view. Every scene vertex stage projects <c>position − uEye</c> instead
/// (one subtraction of near values, exact) with a view-projection that has no translation: the frame's precise <c>WorldFrame</c> relative one for
/// the main view, translation(eye) × the view-projection for passes that have only that (shadow casters, reflections, bakes).
/// </summary>
public static partial class RenderOrigin
{
    /// <summary>
    /// <paramref name="glsl"/> with every <c>uViewProjection * vec4(x, 1.0)</c> projecting <c>(x) - uEye</c>, and <c>uniform vec3 uEye;</c>
    /// declared when the stage projects and does not declare it. A stage that projects no <c>vec4(…, 1.0)</c> through <c>uViewProjection</c> is
    /// returned as is.
    /// </summary>
    public static string Relative(string glsl)
    {
        if (!Projection().IsMatch(glsl)) return glsl;

        string text = Projection().Replace(glsl, m => $"uViewProjection * vec4(({m.Groups[1].Value}) - uEye, 1.0)");
        if (!EyeDeclared().IsMatch(text)) text = ViewProjectionDeclared().Replace(text, m => m.Value + "\nuniform vec3 uEye;", 1);
        return text;
    }

    /// <summary>
    /// The view-projection for positions less <paramref name="eye"/>: <paramref name="relative"/> when given, else translation(eye) ×
    /// <paramref name="viewProjection"/>.
    /// </summary>
    public static System.Numerics.Matrix4x4 ViewProjection(System.Numerics.Matrix4x4 viewProjection, System.Numerics.Vector3 eye,
        System.Numerics.Matrix4x4? relative = null) =>
        relative ?? System.Numerics.Matrix4x4.CreateTranslation(eye) * viewProjection;

    /// <summary>
    /// A projection of a position through <c>uViewProjection</c>: <c>uViewProjection * vec4(x, 1.0)</c>, x in group 1.
    /// </summary>
    [GeneratedRegex(@"uViewProjection\s*\*\s*vec4\s*\(\s*((?:[^(),]|\([^()]*\))+)\s*,\s*1\.0\s*\)")]
    private static partial Regex Projection();

    /// <summary>
    /// The declaration of <c>uEye</c>.
    /// </summary>
    [GeneratedRegex(@"uniform\s+vec3\s+uEye\s*;")]
    private static partial Regex EyeDeclared();

    /// <summary>
    /// The declaration of <c>uViewProjection</c>, which <c>uEye</c>'s follows.
    /// </summary>
    [GeneratedRegex(@"uniform\s+mat4\s+uViewProjection\s*;")]
    private static partial Regex ViewProjectionDeclared();
}
