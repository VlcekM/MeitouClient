using System.Numerics;
using Meitou.Data.Characters;

namespace Meitou.Rendering;

/// <summary>The render thread's opaque per-actor history. Nothing here interprets producer-owned matrix bits.</summary>
internal sealed class RuntimeCharacterPalette
{
    public RuntimeCharacterShaderData? Current { get; private set; }
    public RuntimeCharacterShaderData? Previous { get; private set; }
    RuntimeCharacterShaderSet? shaders;
    public int Count => Current?.Prefix.Count ?? 0;

    public static int Reserve(ref int total, int boneCount, int prefixCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(boneCount);
        ArgumentOutOfRangeException.ThrowIfNegative(prefixCount);
        int boneBase = checked(total + prefixCount);
        total = checked(boneBase + boneCount);
        return boneBase;
    }

    public void Update(RuntimeCharacterShaderData? data, RuntimeCharacterShaderSet? shaderSet, bool snap)
    {
        Previous = !snap && ReferenceEquals(shaders, shaderSet) && Count == (data?.Prefix.Count ?? 0) ? Current : data;
        Current = data;
        shaders = shaderSet;
    }

    public void Write(Span<Matrix4x4> current, Span<Matrix4x4> previous)
    {
        Current?.CopyPrefixTo(current);
        if (!previous.IsEmpty) (Previous ?? Current)?.CopyPrefixTo(previous);
    }
}
