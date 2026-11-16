using System.Globalization;
using Meitou.Data.World;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// SkyX's cloud layer as the sky pass draws it (<c>SkyX_Clouds.hlsl</c>, docs/formats/clouds.md, and the Meitou lit layer), in one source for its two
/// readers: the sky pass (its own uniforms, the textures' screen derivatives) and a puddle's reflection (the frame globals, gradients from a pixel's
/// angle, <c>AtmosphereShaders</c> <c>atmoSkyClouds</c>). Each is <c>vec4 name(vec3 dir, vec3 skyBehind)</c>: the layer's HDR colour and alpha towards
/// <c>dir</c>, over the sky's colour <c>skyBehind</c> there.
/// </summary>
static class CloudLayerShaders
{
    /// <summary>
    /// The sky pass's layer, <c>skyCloudLayer</c>: needs the sky pass's <c>uCloud*</c> uniforms and <c>cloudsRed</c>, <c>cloudsTileRed</c>,
    /// <c>cloudsNormal</c> and <c>cloudsSmooth</c>.
    /// </summary>
    public static readonly string Dome = Layer("skyCloudLayer", "uCloud", "cloudsRed", "cloudsTileRed", "cloudsNormal", "cloudsSmooth", "");

    /// <summary>
    /// The reflection's layer, <c>atmoCloudLayer</c>, on the frame globals (<c>uAtmoLayer*</c>, the sky pass's values as <c>SkyRenderer</c> publishes
    /// them): the lookups' gradients are a pixel's angle (<c>uAtmoLayerWind.z</c>) across and along <c>dir</c>, mapped to the plane, as a flat mirror
    /// shows the sky at the eye's own footprint.
    /// </summary>
    public static readonly string Reflected = ReflectedLookups
        + Layer("atmoCloudLayer", "uAtmoLayer", "atmoLayerRed", "atmoLayerTileRed", "atmoLayerNormal", "atmoLayerRed", ReflectedGradients);

    /// <summary>
    /// The reflection's lookups: the published textures (the layer's density and tile as the sky pass reads them, the normal map) by the gradients
    /// <c>atmoLayerGx</c> and <c>atmoLayerGy</c>.
    /// </summary>
    const string ReflectedLookups = """

        vec2 atmoLayerGx = vec2(0.0), atmoLayerGy = vec2(0.0);
        float atmoLayerRed(vec2 uv) { return textureGrad(uAtmoLayerClouds, uv, atmoLayerGx, atmoLayerGy).r; }
        float atmoLayerTileRed(vec2 uv) { return textureGrad(uAtmoLayerTile, uv, atmoLayerGx, atmoLayerGy).r; }
        vec3 atmoLayerNormal(vec2 uv) { return textureGrad(uAtmoLayerNormal, uv, atmoLayerGx, atmoLayerGy).rgb; }
        """;

    /// <summary>
    /// The reflection's gradients, after the plane's first <c>uv</c>: the plane's coordinates a pixel's angle away from <c>d</c>, across it (level)
    /// and along it (up), less <c>uv</c>.
    /// </summary>
    const string ReflectedGradients = """
                vec3 level = abs(d.y) < 0.999 ? normalize(cross(d, vec3(0.0, 1.0, 0.0))) : vec3(1.0, 0.0, 0.0);
                vec3 dx = normalize(d + level * uAtmoLayerWind.z), dy = normalize(d + cross(level, d) * uAtmoLayerWind.z);
                dx.y = max(dx.y, 0.0005);
                dy.y = max(dy.y, 0.0005);
                atmoLayerGx = dx.xz * (HEIGHT / dx.y) * SCALE * uAtmoLayerMeitou.y + uAtmoLayerMeitou.zw - uv;
                atmoLayerGy = dy.xz * (HEIGHT / dy.y) * SCALE * uAtmoLayerMeitou.y + uAtmoLayerMeitou.zw - uv;
        """;

    /// <summary>
    /// A float as a GLSL literal (invariant culture, always with a decimal point).
    /// </summary>
    static string F(float v) => v.ToString("0.#########", CultureInfo.InvariantCulture) + (v == MathF.Floor(v) ? ".0" : "");

    /// <summary>
    /// The layer as <paramref name="name"/>, reading the uniforms <paramref name="u"/><c>Light</c>, <c>Sun</c>, <c>Wind</c>, <c>Meitou</c>,
    /// <c>Key</c>, <c>KeyDir</c> and <c>Ambient</c> and the lookups <paramref name="red"/>, <paramref name="tile"/>, <paramref name="normal"/> and
    /// <paramref name="smooth"/> (the smoothed density the lit layer's side taps read); <paramref name="afterUv"/> runs once the plane's first
    /// <c>uv</c> is known.
    /// </summary>
    static string Layer(string name, string u, string red, string tile, string normal, string smooth, string afterUv) => $$"""

        vec4 {{name}}(vec3 dir, vec3 skyBehind)
        {
            vec3 d = normalize(vec3(dir.x, max(dir.y, 0.0005), dir.z));
            float o = {{u}}Sun.w;
            const float MULT = {{F(CloudLayer.DensityMultiplier)}}, SCALE = {{F(CloudLayer.Scale)}}, HEIGHT = {{F(CloudLayer.Height)}};
            vec2 wind = {{u}}Wind.xy;
            const vec2 SHIFT = vec2({{F(CloudLayer.SecondLookupShift.X)}}, {{F(CloudLayer.SecondLookupShift.Y)}});
            // The plane hit: the cloud point is d · height / d.y, the texture coordinate its xz · scale.
            vec2 uv = d.xz * (HEIGHT / d.y) * SCALE * {{u}}Meitou.y + {{u}}Meitou.zw;
        {{afterUv}}
            float density = {{red}}(uv + wind);
            vec3 normal = -(2.0 * {{normal}}(uv + wind) - 1.0);
            normal = vec3(normal.x, normal.z, normal.y);   // the shader swaps y and z
            density = clamp((density + o) * MULT, 0.0, 1.0);
            // The fake volume: the direction bent along the normal map, the plane raised where the cloud is thin.
            vec3 nd = normalize(d + {{F(CloudLayer.VolumetricDisplacement)}} * d.y * vec3(normal.x, 0.0, normal.z));
            float vh = (HEIGHT + HEIGHT * (1.0 - density) * {{F(CloudLayer.HeightVolume)}} * d.y) / nd.y;
            uv = nd.xz * vh * SCALE * {{u}}Meitou.y + {{u}}Meitou.zw;
            density = ({{red}}(uv + wind + SHIFT) + o) * MULT;
            float tile = {{tile}}(uv - wind);
            density += tile * {{F(CloudLayer.TileWeight)}};
            vec3 pixel = {{u}}Light.rgb + {{u}}Sun.rgb * (1.0 - density * 0.1);
            float top = 1.0, dark = 1.0;   // the colour's ceiling (the game's 1, saturate; the Meitou clouds' brighter edges towards the sun) and the share of Darkness
            if ({{u}}Meitou.x > 0.5)
            {
                // Meitou clouds (MeitouClouds in Meitou.Data, docs/formats/clouds.md "Meitou clouds"): the same layer lit by the key light through the cloud's
                // depth towards it, the lesser of the path up through the layer (the pixel's density over the light's height) and the path sideways across it
                // (density taps along the light's direction on the plane), with a two-lobed phase, plus the sky's light as the ambient.
                vec3 L = {{u}}KeyDir.xyz;
                vec2 lxz = dot(L.xz, L.xz) > 1e-8 ? normalize(L.xz) : vec2(0.0);
                vec2 at = uv + wind + SHIFT;
                float side = 0.0;
                for (int k = 1; k <= {{MeitouClouds.LightSteps}}; k++)
                    side += clamp(({{smooth}}(at + lxz * ({{F(MeitouClouds.LightStep)}} * float(k))) + o) * MULT, 0.0, 1.0);
                float thick = max(density, 0.0);
                // A light below the layer (the sun just set, the cloud still sees it) shines on the underside the eye sees: a short path.
                float up = L.y > 0.0 ? thick * min(1.0 / L.y, {{F(MeitouClouds.MaxSlant)}}) : thick * 0.25;
                float across = (side + 0.5 * clamp(thick, 0.0, 1.0)) * {{F(MeitouClouds.SidePath)}};
                float depth = min(up, across);
                float through = {{F(1 - MeitouClouds.MultipleShare)}} * exp(-{{F(MeitouClouds.Extinction)}} * depth)
                              + {{F(MeitouClouds.MultipleShare)}} * exp(-{{F(MeitouClouds.Extinction * MeitouClouds.MultipleExtinction)}} * depth);
                float c = dot(d, L);
                float phase = {{F(MeitouClouds.ForwardShare)}} * {{F(1 - MeitouClouds.ForwardG * MeitouClouds.ForwardG)}} / pow(max({{F(1 + MeitouClouds.ForwardG * MeitouClouds.ForwardG)}} - {{F(2 * MeitouClouds.ForwardG)}} * c, 1e-6), 1.5)
                            + {{F(1 - MeitouClouds.ForwardShare)}} * {{F(1 - MeitouClouds.BackwardG * MeitouClouds.BackwardG)}} / pow(max({{F(1 + MeitouClouds.BackwardG * MeitouClouds.BackwardG)}} - {{F(2 * MeitouClouds.BackwardG)}} * c, 1e-6), 1.5);
                vec3 ambient = mix({{u}}Ambient.rgb, skyBehind / sqrt(SKYX_EXPOSURE), {{F(MeitouClouds.AmbientFromSky)}});
                ambient = mix(ambient, vec3(dot(ambient, vec3(0.2126, 0.7152, 0.0722))), {{u}}Ambient.a)
                             * (1.0 - {{F(MeitouClouds.BaseDarkening)}} * clamp(0.5 * thick, 0.0, 1.0));
                pixel = {{u}}Key.rgb * (through * phase * {{F(MeitouClouds.KeyGain)}}) + ambient * {{F(MeitouClouds.AmbientGain)}};
                top = 8.0;
                dark = {{F(MeitouClouds.DarknessShare)}};
            }
            // The horizon band: from 0.05 to 0.15 of d.y the layer gives way to the uniform alpha o + 0.5 (horizonClouds.a).
            float band = clamp(10.0 * clamp(d.y - {{F(CloudLayer.DistanceAttenuation)}}, 0.0, 1.0), 0.0, 1.0);
            density += band;
            pixel *= 1.0 - clamp(density, 0.0, 1.0) * {{u}}Light.a * dark;
            // Below d.y = 0.05 the alpha is the uniform horizon value, but the colour above still followed the texture lookups, whose uv runs to hundreds of units
            // there (height / d.y): minified to a few texels they sparkle (a row of white ticks along the horizon). The colour gives way to the plain
            // horizonClouds colour below d.y = 0.05 (from 0.01 up; above 0.05 the layer is untouched): the game's own horizon cloud colour, darkened
            // like the layer (an undarkened target drew a white band at c = 1, docs/formats/sky.md), and the colour the haze is pulled to.
            vec3 plain = clamp({{u}}Light.rgb + {{u}}Sun.rgb * (1.0 - 0.1 * (o + 0.2) * MULT), 0.0, 1.0) * (1.0 - {{u}}Light.a);
            pixel = mix(plain, pixel, clamp((d.y - 0.01) / 0.04, 0.0, 1.0));
            float alpha = density * clamp(1.0 - tile + o, 0.0, 1.0);
            alpha = mix(o + 0.5, alpha, band);
            return vec4(clamp(pixel, 0.0, top) * sqrt(SKYX_EXPOSURE), clamp(alpha, 0.0, 1.0));
        }
        """;
}
