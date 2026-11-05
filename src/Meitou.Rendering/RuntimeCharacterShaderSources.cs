using Meitou.Data.Characters;
using Meitou.Rendering.Characters;

namespace Meitou.Rendering;

/// <summary>Current host shader ABI fragments for complete runtime character programs; these do not contain mod-specific behavior.</summary>
public static class RuntimeCharacterShaderSources
{
    public static string Prelude(string pushMembers) => NativeShaders.Prelude(pushMembers);
    public static string AtmosphereFunctions => NativeShaders.AtmosphereFunctions;
    public static string DepthFragment => NativeShaders.DepthFragment;
    public static string DepthConventionGlsl => DepthConvention.Glsl;
    public const int SkinWidth = SkinScattering.Width, SkinHeight = SkinScattering.Height;
    public const float SkinMaxInverseRadiusMm = SkinScattering.MaxInverseRadiusMm;
    public static RuntimeCharacterShaderSet Default(bool lateDepth = false) => new(CharacterShaders.Vertex(), CharacterShaders.Fragment(lateDepth),
        CharacterShaders.DepthVertex(), CharacterShaders.DepthFragment(), CharacterShaders.MotionVertex(), CharacterShaders.MotionFragment());
}
