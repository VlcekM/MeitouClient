using System.Numerics;
using Meitou.Data.Characters;

namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// The code mods' colour tints on a person's skin (<see cref="CharacterAppearance.Tints"/>, docs/modding.md "Look extras"). A body material with
/// tints has the <see cref="Flag"/> and a companion record in the material table (<see cref="CharacterMaterialTable"/>), whose slot rides in the
/// body record's <c>alphaChannel.x</c> (a body never reads that field). The companion keeps the record's layout: <c>vestCount</c> the number of
/// tints, tint i's centre, radius and softness in <see cref="Shape"/>'s field, its colour and strength in <see cref="Colour"/>'s, its area and side
/// in <c>tex[2i]</c> and its lightness limit's bits in <c>tex[2i + 1]</c>. Without an extras source (<see cref="ModLooks.HasExtras"/>) the shaders
/// get none of this code, so they are as they were.
/// </summary>
internal static class CharacterLookTints
{
    /// <summary>
    /// The material flag of a body with tints (<see cref="CharacterMaterialRecord.Flags"/>; the other bits: <see cref="CharacterShaders"/>).
    /// </summary>
    public const uint Flag = 1u << 31;

    /// <summary>
    /// The companion's shading mode: never drawn.
    /// </summary>
    const uint ShadeTints = 3;

    /// <summary>
    /// The shader's functions, before <c>main</c>: <c>lookTints</c> lays a companion's tints over a body texel. A tint takes the texel's
    /// brightness (its largest channel) with the tint's hue and saturation, by its strength, fading out over the disk's outer softness and above
    /// its lightness limit. The head's texels sit at v -1..0 and are measured on the head texture (v + 1).
    /// </summary>
    public static string Functions => !ModLooks.HasExtras ? "" : """
        vec4 tintShape(CharMaterial t, uint i) { return i == 0u ? t.shirtColour : i == 1u ? t.hairColour : i == 2u ? t.hairMult : t.diffuseChannel; }
        vec4 tintColour(CharMaterial t, uint i) { return i == 0u ? t.skinTone : i == 1u ? t.hairAlpha : i == 2u ? t.beardAlpha : t.alphaChannel; }

        // The code mods' tints (CharacterLookTints): bind is the vertex in the mesh's own space, whose x tells the character's left from its right.
        void lookTints(CharMaterial t, vec2 uv, vec3 bind, inout vec3 c)
        {
            bool head = uv.y < 0.0;
            vec2 at = head ? uv + vec2(0.0, 1.0) : uv;
            for (uint i = 0u; i < min(t.vestCount, 4u); i++)
            {
                uint kind = t.tex[2u * i];
                if (((kind & 15u) == 1u) != head) continue;
                uint side = kind >> 4u;
                if ((side == 1u && bind.x < 0.0) || (side == 2u && bind.x > 0.0)) continue;
                vec4 shape = tintShape(t, i), colour = tintColour(t, i);
                float w = colour.a * (1.0 - smoothstep(1.0 - max(shape.w, 1e-3), 1.0, distance(at, shape.xy) / shape.z));
                float v = max(c.r, max(c.g, c.b));
                float limit = uintBitsToFloat(t.tex[2u * i + 1u]);
                if (limit < 1.0) w *= 1.0 - smoothstep(limit - 0.05, limit, v);
                c = mix(c, colour.rgb / max(max(colour.r, max(colour.g, colour.b)), 1e-4) * v, w);
            }
        }
        """;

    /// <summary>
    /// The shader's call, after the skin tone in the body's branch of <c>main</c>.
    /// </summary>
    public static string Call => !ModLooks.HasExtras ? "" : "if (flag(f, " + Flag + "u)) lookTints(materials.items[uint(m.alphaChannel.x)], uv, vBind, body.rgb);";

    /// <summary>
    /// The companion record of <paramref name="tints"/> (at most <see cref="ModLooks.MaxTints"/>).
    /// </summary>
    public static CharacterMaterialRecord Record(IReadOnlyList<LookTint> tints)
    {
        var r = new CharacterMaterialRecord { Shading = ShadeTints, VestCount = (uint)Math.Min(tints.Count, ModLooks.MaxTints) };
        for (int i = 0; i < (int)r.VestCount; i++)
        {
            var t = tints[i];
            var shape = new Vector4(t.Centre, t.Radius, t.Softness);
            var colour = new Vector4(t.Colour, t.Strength);
            switch (i)
            {
                case 0: (r.ShirtColour, r.SkinTone) = (shape, colour); break;
                case 1: (r.HairColour, r.HairAlpha) = (shape, colour); break;
                case 2: (r.HairMult, r.BeardAlpha) = (shape, colour); break;
                default: (r.DiffuseChannel, r.AlphaChannel) = (shape, colour); break;
            }
            r.Tex[2 * i] = (uint)t.Area | ((uint)t.Side << 4);
            r.Tex[2 * i + 1] = BitConverter.SingleToUInt32Bits(t.MaxLightness);
        }
        return r;
    }
}
