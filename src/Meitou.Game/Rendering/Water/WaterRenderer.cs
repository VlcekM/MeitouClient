using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Data.Textures;
using Meitou.Data.World;

using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Shaders;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Terrain;

namespace Meitou.Game.Rendering.Water;

/// <summary>
/// The <c>contactfoam</c> switch (docs/render-water.md "Contact foam"): off, none of its code runs or is in the water's program.
/// </summary>
public static class WaterContact
{
    /// <summary>
    /// Whether the contact foam and the interactive waves are on.
    /// </summary>
    public static bool Enabled { get; set; }
}

/// <summary>
/// Water as the game places it (docs/formats/terrain.md, "Water"): one surface at <see cref="WorldWater.Height"/>
/// over the whole world, hidden wherever the terrain is higher. Shading follows the facts of Kenshi's water material:
/// colour from <c>watercolourmap.png</c>, flow from <c>flowmap.png</c>, three phase-shifted scrolling samples of the
/// normal map, the biomes' water parameters, alpha from the water depth near the camera and opaque beyond 4000
/// units. With a <see cref="ReflectionPass"/> the water reflects the mirrored scene, else the sky colour. Not reproduced: scum, turbulence,
/// rain ripples and per-biome normal maps (all use <c>water.png</c>).
/// </summary>
public sealed unsafe class WaterRenderer : IDisposable
{
    #region Shaders

    /// <summary>
    /// Set 2: the water's own values and the bindless indices of its maps (the native frame is set 0 and the table set 1).
    /// </summary>
    const int ConstantsSet = 2;

    /// <summary>
    /// The block <see cref="WaterConstants"/> mirrors. It is already placed, so <see cref="NativeShaders.Port"/> leaves it alone.
    /// </summary>
    const string ConstantsBlock = """
        layout(std140, set = 2, binding = 0) uniform WaterConstants
        {
            vec4 params;          // water height, half world, grid step, animation time
            vec4 centreExtent;    // eye xz, plane extent, lean
            vec4 seaA;
            vec4 seaB;
            vec4 seaColour;
            vec4 sunDir;
            vec4 sunColour;
            vec4 glintDir;
            vec4 glintColour;
            vec4 ocean;
            vec4 shoreRect;
            vec4 shore;
            vec4 waveFade;
            vec4 clarity;
            vec4 screenReflect;   // 1 / target xy, reflect, refract
            mat4 reflectionViewProjection;
            vec4 skySun;
            vec4 skyZenith;
            vec4 skyHorizon;
            vec4 skySunColour;    // rgb, twilight
            vec4 coarseRect;
            vec4 fineRect;
            vec4 coarseCellsFineBand; // coarse cells xy, fine band, has fine
            vec4 fineCells;
            vec4 depthPlanes;     // the scene's near and far planes, 1 with a scene depth copy (CaptureScene); w unused
            uint colourMap;
            uint flowMap;
            uint foamMap;
            uint normalMap;
            uint rainMap;
            uint paramsA;
            uint paramsB;
            uint reflectionMap;
            uint refractionMap;
            uint oceanMap;
            uint shoreMap;
            uint riverMap;
            uint heightCoarse;
            uint heightFine;
            uint sceneDepthMap;
            uint rippleMap;       // the contactfoam switch's waves (WaterRipples)
            vec4 rippleWindow;
            vec4 rippleShape;
        } water;

        """;

    /// <summary>
    /// The C# side of <see cref="ConstantsBlock"/> (std140).
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 544)]
    struct WaterConstants
    {
        /// <summary>
        /// The water height, the half world, the grid step and the animation time.
        /// </summary>
        [FieldOffset(0)] public Vector4 Params;
        /// <summary>
        /// The eye's x, z, the plane's extent and the lean switch.
        /// </summary>
        [FieldOffset(16)] public Vector4 CentreExtent;
        /// <summary>
        /// The open sea's first parameters (as the parameters A map: scale X, scale Y, invStrength, invOpacity).
        /// </summary>
        [FieldOffset(32)] public Vector4 SeaA;
        /// <summary>
        /// The open sea's second parameters (as the parameters B map: gloss, glow, distortion).
        /// </summary>
        [FieldOffset(48)] public Vector4 SeaB;
        /// <summary>
        /// The open sea's colour, and the water's far distance in w (<c>uWaterFar</c>: the plane's extent over 1.5).
        /// </summary>
        [FieldOffset(64)] public Vector4 SeaColour;
        /// <summary>
        /// The sun's direction.
        /// </summary>
        [FieldOffset(80)] public Vector4 SunDir;
        /// <summary>
        /// The sun's colour.
        /// </summary>
        [FieldOffset(96)] public Vector4 SunColour;
        /// <summary>
        /// The specular glint's light direction (the sun's, or by night the planet's; <see cref="WorldLighting.Glint"/>).
        /// </summary>
        [FieldOffset(112)] public Vector4 GlintDir;
        /// <summary>
        /// The glint's colour (faded by the clouds' shade on the Meitou water).
        /// </summary>
        [FieldOffset(128)] public Vector4 GlintColour;
        /// <summary>
        /// The ocean's three wave cascade lengths and its FFT size (0: not ready).
        /// </summary>
        [FieldOffset(144)] public Vector4 Ocean;
        /// <summary>
        /// The shore field's world rectangle (0: none yet).
        /// </summary>
        [FieldOffset(160)] public Vector4 ShoreRect;
        /// <summary>
        /// The breaking waves' phase and sizes (<see cref="WaveSet.Shore"/>).
        /// </summary>
        [FieldOffset(176)] public Vector4 Shore;
        /// <summary>
        /// Where the open-water waves and the shore waves fade (start, end each).
        /// </summary>
        [FieldOffset(192)] public Vector4 WaveFade;
        /// <summary>
        /// The clarity: the inverse clarity, the far clarity and the clear distance.
        /// </summary>
        [FieldOffset(208)] public Vector4 Clarity;
        /// <summary>
        /// One over the target's size, and whether the reflection and the refraction are read this frame.
        /// </summary>
        [FieldOffset(224)] public Vector4 ScreenReflect;
        /// <summary>
        /// The reflection's view-projection (identity without one).
        /// </summary>
        [FieldOffset(240)] public Matrix4x4 ReflectionViewProjection;
        /// <summary>
        /// The simple sky's sun direction.
        /// </summary>
        [FieldOffset(304)] public Vector4 SkySun;
        /// <summary>
        /// The simple sky's zenith colour.
        /// </summary>
        [FieldOffset(320)] public Vector4 SkyZenith;
        /// <summary>
        /// The simple sky's horizon colour.
        /// </summary>
        [FieldOffset(336)] public Vector4 SkyHorizon;
        /// <summary>
        /// The simple sky's sun colour and twilight.
        /// </summary>
        [FieldOffset(352)] public Vector4 SkySunColour;
        /// <summary>
        /// The coarse heightmap's world rectangle (<see cref="TerrainShaders.HeightFunctions"/>).
        /// </summary>
        [FieldOffset(368)] public Vector4 CoarseRect;
        /// <summary>
        /// The fine heightmap's world rectangle.
        /// </summary>
        [FieldOffset(384)] public Vector4 FineRect;
        /// <summary>
        /// The coarse heightmap's cells, the fine band, and whether there is a fine heightmap.
        /// </summary>
        [FieldOffset(400)] public Vector4 CoarseCellsFineBand;
        /// <summary>
        /// The fine heightmap's cells.
        /// </summary>
        [FieldOffset(416)] public Vector4 FineCells;
        /// <summary>
        /// The scene's near and far planes, and 1 with a scene depth copy this frame (<see cref="CaptureScene"/>).
        /// </summary>
        [FieldOffset(432)] public Vector4 DepthPlanes;
        /// <summary>
        /// The colour map's bindless index (<c>watercolourmap.png</c>).
        /// </summary>
        [FieldOffset(448)] public uint ColourMap;
        /// <summary>
        /// The flow map's bindless index (<c>flowmap.png</c>).
        /// </summary>
        [FieldOffset(452)] public uint FlowMap;
        /// <summary>
        /// The foam lace's bindless index (Meitou).
        /// </summary>
        [FieldOffset(456)] public uint FoamMap;
        /// <summary>
        /// The normal map's bindless index (<c>water.png</c>).
        /// </summary>
        [FieldOffset(460)] public uint NormalMap;
        /// <summary>
        /// The rain ripples' bindless index (<c>rain-ripples.png</c>).
        /// </summary>
        [FieldOffset(464)] public uint RainMap;
        /// <summary>
        /// The baked parameters A map's bindless index.
        /// </summary>
        [FieldOffset(468)] public uint ParamsA;
        /// <summary>
        /// The baked parameters B map's bindless index.
        /// </summary>
        [FieldOffset(472)] public uint ParamsB;
        /// <summary>
        /// The reflection's bindless index (the stand-in without one).
        /// </summary>
        [FieldOffset(476)] public uint ReflectionMap;
        /// <summary>
        /// The refraction's bindless index (the stand-in without one).
        /// </summary>
        [FieldOffset(480)] public uint RefractionMap;
        /// <summary>
        /// The ocean's wave maps (a 2D array), bindless index.
        /// </summary>
        [FieldOffset(484)] public uint OceanMap;
        /// <summary>
        /// The shore field's bindless index.
        /// </summary>
        [FieldOffset(488)] public uint ShoreMap;
        /// <summary>
        /// The river flow map's bindless index (<see cref="RiverFlowBake"/>).
        /// </summary>
        [FieldOffset(492)] public uint RiverMap;
        /// <summary>
        /// The coarse heightmap's bindless index.
        /// </summary>
        [FieldOffset(496)] public uint HeightCoarse;
        /// <summary>
        /// The fine heightmap's bindless index.
        /// </summary>
        [FieldOffset(500)] public uint HeightFine;
        /// <summary>
        /// The scene depth copy's bindless index (the stand-in without one).
        /// </summary>
        [FieldOffset(504)] public uint SceneDepthMap;
        /// <summary>
        /// The contact ripples' surface's bindless index (the stand-in without them).
        /// </summary>
        [FieldOffset(508)] public uint RippleMap;
        /// <summary>
        /// The ripples' window: its centre and size in the world.
        /// </summary>
        [FieldOffset(512)] public Vector4 RippleWindow;
        /// <summary>
        /// The ripples' half side, one over their side in world units, their texel, and 1 while they are drawn.
        /// </summary>
        [FieldOffset(528)] public Vector4 RippleShape;
    }

    /// <summary>
    /// The game's water vertex stage: a quad at the water height that follows the eye, half its side past the far plane.
    /// </summary>
    const string Vertex = """
        #version 330 core
        layout(location = 0) in vec2 aXZ;
        uniform mat4 uViewProjection;
        uniform float uWaterHeight;
        uniform vec2 uCentre;          // the plane follows the eye, so the sea has no edge
        uniform float uExtent;         // half its side, past the far plane
        out vec3 vWorld;
        void main()
        {
            vWorld = vec3(uCentre.x + aXZ.x * uExtent, uWaterHeight, uCentre.y + aXZ.y * uExtent);
            gl_Position = uViewProjection * vec4(vWorld, 1.0);
        }
        """;

    /// <summary>
    /// The water's thickness along the view ray (docs/render-water.md "Edges"). The game's water reads its G-buffer's distance at the pixel and
    /// takes its own off (forward/water.hlsl); here the scene's depth, copied before the water (<see cref="CaptureScene"/>), whatever stands in
    /// the water included. -1 without a copy (Metal, or a view whose water is opaque).
    /// </summary>
    const string ThicknessFunctions = DepthConvention.Glsl + """

        uniform sampler2D uSceneDepth;   // the scene's depth before the water (CaptureScene), read where uDepthPlanes.z is 1
        uniform vec4 uDepthPlanes;       // the scene's near and far planes, 1 with a depth copy; w unused

        // From this fragment (dist from the eye) to the surface behind it along the view ray, or -1 without a depth copy.
        float sceneThickness(float dist)
        {
            if (uDepthPlanes.z < 0.5) return -1.0;
            float behind = depthViewZ(texelFetch(uSceneDepth, ivec2(gl_FragCoord.xy), 0).r, uDepthPlanes.xy);
            return max(dist * (behind / depthViewZ(gl_FragCoord.z, uDepthPlanes.xy) - 1.0), 0.0);
        }

        // The error of sceneThickness from the two stored depths' precision (one float step each, depthStep): the edge fades over at
        // least this, so distant shores do not shimmer.
        float thicknessError(float dist)
        {
            return 2.0 * depthStep(dist, uDepthPlanes.xy);
        }
        """;

    /// <summary>
    /// The game's water fragment stage: the biomes' colour and parameters blended per pixel, flow, rain ripples, the reflection, the sun's glint and
    /// the haze.
    /// </summary>
    static readonly string Fragment = "#version 330 core\n" + TerrainShaders.HeightFunctions + SkyRenderer.SkyFunctions + ThicknessFunctions + """

        in vec3 vWorld;
        out vec4 fragColour;
        uniform vec3 uEye;
        uniform float uWaterFar;
        uniform float uWaterHeight;
        uniform float uHalfWorld;
        uniform float uTime;
        uniform vec3 uSunDir;
        uniform vec3 uSunColour;
        uniform vec3 uGlintDir;         // the specular glint's light: the sun's direction and colour, or by night the planet's own radiance (WorldLighting.Glint)
        uniform vec3 uGlintColour;
        uniform vec3 uFogColour;
        uniform float uFogDistance;
        uniform sampler2D uColourMap;   // watercolourmap.png: the biomes' water colour, whole world
        uniform sampler2D uFlowMap;     // flowmap.png: RG flow direction, B scum amount
        uniform sampler2D uNormalMap;   // water.png
        uniform sampler2D uRainMap;     // rain-ripples.png: R the ring's size, G B its normal, A the drop's phase
        uniform sampler2D uParamsA;     // per pixel, blended over biomes: scale X, scale Y (repeats per unit), invStrength, invOpacity
        uniform sampler2D uParamsB;     // gloss, glow, distortion, -
        uniform vec4 uSeaA;             // the open sea past the world's edge: parameters as in the two maps above, and colour
        uniform vec4 uSeaB;
        uniform vec3 uSeaColour;
        uniform sampler2D uReflection;  // the scene mirrored about the water (ReflectionPass), looked up with uReflectionViewProjection
        uniform mat4 uReflectionViewProjection;
        uniform float uReflect;         // 1 with a reflection this frame, else the sky colour is reflected

        vec3 sampleNormal(vec2 coord, vec2 direction, float speed, float time)
        {
            float t = fract(time);
            vec3 n = texture(uNormalMap, coord + direction * speed * t).rgb * 2.0 - 1.0;
            return n.xzy * (1.0 - abs(t * 2.0 - 1.0));
        }

        // forward/water.hlsl rainRipple: a ring per texel, its phase scrolling with the time, the normal's xz scaled by the ring's profile.
        vec3 rainRipple(vec2 coord, float time, float weight)
        {
            vec4 ripple = texture(uRainMap, coord);
            float dropTime = fract(ripple.a + time * 0.5);
            float dropFrac = dropTime - 1.0 + ripple.r * 0.5 + 0.5;
            float s = -sin(clamp(dropFrac * 16.0, 0.0, 2.0) * 3.14159);
            vec3 rainNormal = ripple.grb;
            rainNormal.xz *= s;
            return rainNormal * weight * ripple.r;
        }

        void main()
        {
            vec2 map = (vWorld.xz + uHalfWorld) / (2.0 * uHalfWorld);
            vec4 pa = texture(uParamsA, map);
            vec4 pb = texture(uParamsB, map);
            vec3 waterColour = texture(uColourMap, map).rgb;
            // Past the world's edge the data's last pixels would stretch outwards: fade to the open sea instead.
            vec2 beyond = abs(vWorld.xz) - uHalfWorld;
            float outside = max(max(beyond.x, beyond.y), 0.0);
            // The data is shallow-coast colours right up to its edge: fade into the open sea across the edge, from inside it, so there is no straight seam.
            float sea = smoothstep(-30000.0, 12000.0, max(beyond.x, beyond.y));
            pa = mix(pa, uSeaA, sea);
            pb = mix(pb, uSeaB, sea);
            waterColour = mix(waterColour, uSeaColour, sea);
            vec3 toEye = uEye - vWorld;
            float dist = length(toEye);
            vec3 toEyeDir = toEye / dist;

            // Flow: direction from the flow map, speed from the texture scale and the biome's distortion.
            vec2 tex = vWorld.xz * pa.xy;
            vec2 direction = (texture(uFlowMap, map).rg * 2.0 - 1.0) * (pa.xy * 5000.0);
            float speed = length(direction);
            direction /= max(speed, 1e-5);
            float distortion = max(pb.z, 1.0);
            speed /= distortion;
            float time = uTime * distortion;
            vec3 n = vec3(0.0, 1.0, 0.0);
            n += sampleNormal(tex, direction, speed, time);
            n += sampleNormal(tex + vec2(0.1, 0.3), direction, speed, time + 0.33);
            n += sampleNormal(tex + vec2(0.4, 0.7), direction, speed, time + 0.66);
            n.y *= pa.z;
            // Rain ripples (docs/formats/weather.md): three layers weighted by rainAmount, faded out at grazing view and with distance.
            float rain = uWeatherWet.y * clamp(toEyeDir.y * 2.0 - dist * 0.0001, 0.0, 1.0);
            if (rain > 0.0)
            {
                vec2 rainCoord = vWorld.xz * 0.01;
                n += rainRipple(rainCoord * 6.0, time * 1.5, rain * 2.0);
                n += rainRipple(rainCoord.yx * 6.0, time * 1.5 + 0.5, rain * 2.0);
                n += rainRipple(rainCoord * 2.0, time + 0.3, rain * 0.8);
            }
            n = normalize(n);

            // Lighting: sun specular, sky reflection, a little diffuse for the water colour.
            float gloss = clamp(pb.x, 0.0, 1.0);
            vec3 l = normalize(uSunDir);
            vec3 glintL = normalize(uGlintDir);
            vec3 h = normalize(glintL + toEyeDir);
            // The ripples are finer than a pixel far away, so the glint widens and dims with distance instead of staying a hot point.
            float power = max(exp2(gloss * 11.0 + 1.0) / (1.0 + dist / 6000.0), 12.0);
            float spec = pow(max(dot(n, h), 0.0), power) * (power + 8.0) / 25.0 * gloss;
            // Above the game's camera heights (uAtmoAltitude.z, a viewer choice) every water pixel is far away and its widened glint covers
            // a large patch of sea: weigh it by the Fresnel term the game's own sun specular has (F0 0.04), so it stays a soft sheen.
            float lh = clamp(dot(glintL, h), 0.0, 1.0);
            spec *= mix(1.0, 0.04 + 0.96 * exp2((-5.55473 * lh - 6.98316) * lh), uAtmoAltitude.z);
            float cosv = max(dot(toEyeDir, n), 0.0);
            float schlick = 0.02 + 0.98 * pow(1.0 - cosv, 5.0);
            vec3 reflected = skyColour(reflect(-toEyeDir, n), false);
            if (uReflect > 0.5)
            {
                // As the game's water: the normal pushes the lookup sideways by 60 / depth, less the further away.
                vec4 rc = uReflectionViewProjection * vec4(vWorld, 1.0);
                vec2 offset = n.xz * (60.0 / rc.w);
                offset *= min(1.0, 0.04 / max(length(offset), 1e-5));
                vec2 uv = rc.xy / rc.w * 0.5 + 0.5 + offset;
                // Past the picture's border there is nothing to reflect: fade to the sky colour.
                vec2 inside = smoothstep(vec2(0.0), vec2(0.04), uv) * smoothstep(vec2(0.0), vec2(0.04), 1.0 - uv);
                reflected = mix(reflected, min(texture(uReflection, uv).rgb, vec3(3.0)), inside.x * inside.y);
            }
            vec3 diffuse = waterColour * (max(dot(n, l), 0.0) * uSunColour * 0.6 + uSkyZenith * 0.5 + 0.03);
            vec3 colour = mix(diffuse, reflected, schlick * gloss) + min(spec, 4.0) * uGlintColour * 0.25 + pb.y * waterColour;

            // Alpha as the game's water: see-through near the camera where shallow, opaque beyond 4000 units. Its depth is the game's, the
            // distance through the water to what is behind it; without a depth copy, the slant path down to the terrain's height.
            float depth = sceneThickness(dist);
            float edgeWidth = depth >= 0.0 ? max(2.0, thicknessError(dist)) : 2.0;   // the game's 2 units, wider where the copy's precision is coarser
            if (depth < 0.0) depth = max(0.0, uWaterHeight - terrainHeight(vWorld.xz)) / max(toEyeDir.y, 0.05);
            if (outside > 0.0) depth = 1e4;   // past the edge of the world there is only open sea
            float fresnel = 1.0 - pow(1.0 - cosv, 2.0);
            float a = clamp(1.0 - (dist - 4000.0) / 1000.0, 0.0, 1.0);
            a *= mix(1.0, clamp(depth * pa.w, 0.0, 1.0), fresnel);
            a *= clamp(depth / edgeWidth, 0.0, 1.0);
            a = mix(1.0, a, clamp((4000.0 - dist) / 400.0, 0.0, 1.0));

            fragColour = vec4(atmoApply(colour, uEye, vWorld), a * atmoWaterWeight(dist, uWaterFar));
        }
        """;

    /// <summary>
    /// The Meitou water's motion, shared by both stages (docs/render-water.md): the open water's FFT ocean (<see cref="OceanWaves"/>: three
    /// cascades of displacement and slopes on the game clock) and the shore's breakers. A breaker's phase runs along the signed distance to the
    /// waterline from the <see cref="ShoreField"/>, so its crests follow the shore as real waves turn to do; the field's exposure keeps surf
    /// off ponds and sheltered bays.
    /// </summary>
    const string MeitouMotion = """

        uniform float uWaterHeight;
        uniform float uHalfWorld;
        uniform sampler2DArray uOceanMaps;  // OceanWaves: layers 0..2 displacement x, y, z and dDx/dz; 3..5 dDy/dx, dDy/dz, dDx/dx, dDz/dz
        uniform vec4 uOcean;                // xyz the cascades' tile sizes, w texels per side (0: no ocean yet)
        uniform sampler2D uShoreField;      // ShoreField: R the signed distance to the waterline (positive over water), G the exposure
        uniform vec4 uShoreRect;            // the field's x0, z0, x1, z1 (x1 <= x0: none yet)
        uniform vec4 uShore;                // x the breakers' wave count (cycles since the start, wrapping at 4096), y run-up height, z breaker wavelength, w breaker height
        uniform vec4 uWaveFade;             // eye distances over which the open waves' heights (x to y) and the shore's breakers and foam (z to w) fade out
        uniform float uLean;                // skip work with zero contribution
        uniform sampler2D uRiverMap;        // RiverFlowBake: RG the flow direction (x, z), B the half-width over 8 texels, A the river weight; world bounds

        // The ocean's displacement at p, each cascade read at the mip whose texels are as far apart as the vertices sampling it (finer
        // waves would alias into the grid's facets; the fragment's normals carry them instead).
        vec4 oceanDisplace(vec2 p, float spacing)
        {
            vec4 d = vec4(0.0);
            if (uOcean.w <= 0.0) return d;
            for (int c = 0; c < 3; c++)
            {
                float l = uOcean[c];
                d += textureLod(uOceanMaps, vec3(p / l, float(c)), max(log2(spacing * uOcean.w / l) + 0.5, 0.0));
            }
            return d;
        }

        // river: how much p is in a river (0 none, 1 a river's channel), flow: its unit direction (x, z), speed: how fast it runs (units a second).
        // wave: the number of the breaker whose crest is in p's cycle (at g 0.5; it changes in the trough at g 0/1), wrapping at 4096.
        // len: the local wavelength of the shore waves (units); prop: the way they run (unit, towards the shore); swell: what is left of them here (0 to 1).
        struct Shore { float depth; float dist; float g; float wave; vec2 dir; float open; float size; float breakAt; float river; vec2 flow; float speed; float sizeWet; float brk; float len; vec2 prop; float swell; };

        // Waves arrive in pieces, not as one line round the coast. Each wave has a number (the wave count at its place, which it keeps as it
        // travels in), and slow, coarse noise on the shore point it is heading for (constant across the wave, varying along the shore, drifting
        // with the clock) decides its height, whether it breaks at all, and (as a phase offset) where along its crest it breaks first.
        float wHash(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
        float wNoise(vec2 p)
        {
            vec2 i = floor(p), f = fract(p);
            f = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
            return mix(mix(wHash(i), wHash(i + vec2(1.0, 0.0)), f.x), mix(wHash(i + vec2(0.0, 1.0)), wHash(i + vec2(1.0, 1.0)), f.x), f.y);
        }

        // Wave number k at the shore point q: size, its height factor (0.2 to 1.7: they come in sets of about seven, the sets reaching
        // different stretches of the shore at different times), and brk, 0 where it rolls in without breaking and 1 where it breaks (a gap
        // is a few hundred units long, the more so for small waves). The numbers wrap at 4096 with the clock, so every term is periodic in it.
        void waveAt(float k, vec2 q, out float size, out float brk)
        {
            float id = mod(k, 4096.0);
            vec2 o = vec2(wHash(vec2(id, 3.7)), wHash(vec2(id, 9.1))) * 173.0;
            float arg = id * (606.0 * 6.2831853 / 4096.0) + 1.1 * sin(id * (261.0 * 6.2831853 / 4096.0) + 0.7) + 3.0 * (wNoise(q / 1500.0 + 3.3) - 0.5);
            float group = smoothstep(0.2, 0.85, 0.5 + 0.5 * sin(arg));
            float n = (wNoise(q / 620.0 + o) - 0.5) * 1.6 + 0.71 + 0.3 * (group - 0.5);
            brk = smoothstep(0.2, 0.6, n);
            size = mix(0.55, 1.6, group) * (0.8 + 0.4 * wNoise(q / 800.0 + o + 40.0)) * mix(0.7, 1.0, brk);
            // A crest is not one line round the shore: along it, it swells and fades over a few hundred units, each wave in its own places.
            size *= mix(0.5, 1.0, smoothstep(0.15, 0.65, wNoise(q / 240.0 + o * 1.7 + 70.0)));
        }

        vec4 shoreField(vec2 uv) { return textureLod(uShoreField, uv, 0.0); }

        // The swell's wave count at time tau (seconds; docs/render-water.md "Shore waves"): a wave every uShore.z seconds on average, in sets of seven over which the period
        // swings 20% either way, so the crests never come evenly spaced. period: the period at tau.
        float waveCount(float tau, out float period)
        {
            float n = floor(tau / uShore.z), r = tau - n * uShore.z;
            float a = 6.2831853 * (mod(n, 7.0) + r / uShore.z) / 7.0;
            period = uShore.z / (1.0 + 0.2 * cos(a));
            return n + r / uShore.z + 0.2 * 7.0 / 6.2831853 * sin(a);
        }

        // depth: still water over the ground (negative on land); dist: the distance to the waterline (negative inland); g: the breaker phase
        // in [0, 1), the crest at 0.5 with its steep front towards the shore; dir: the unit direction towards the shore; open: how exposed the
        // shore is (0 a pond or a sheltered bay, 1 open water): only exposed shores get surf. size: this breaker's height factor (they come in
        // sets, 0.6 to 1.4, varying smoothly from one to the next); breakAt: the distance from the waterline where it breaks.
        Shore shoreAt(vec2 p)
        {
            Shore r;
            float travel = 0.0;   // the swell's arrival time here (seconds)
            r.depth = uWaterHeight - terrainHeight(p);
            r.dist = 1.0e6; r.dir = vec2(0.0); r.open = 0.0; r.flow = vec2(0.0); r.speed = 0.0; r.prop = vec2(1.0, 0.0); r.swell = 0.0;
            // Rivers (the baked map, 144 units a texel): the weight, the coarse direction downstream and the width.
            vec4 rv = textureLod(uRiverMap, (p + uHalfWorld) / (2.0 * uHalfWorld), 0.0);
            vec2 coarse = rv.rg * 2.0 - 1.0;
            // Between texels whose directions differ the filtered vector shortens: the river fades there instead of snapping round.
            r.river = rv.a * smoothstep(0.2, 0.6, length(coarse));
            coarse = length(coarse) > 0.05 ? normalize(coarse) : vec2(1.0, 0.0);
            r.flow = coarse;
            vec2 extent = uShoreRect.zw - uShoreRect.xy;
            if (extent.x > 0.0)
            {
                vec2 uv = (p - uShoreRect.xy) / extent;
                vec4 f = shoreField(uv);
                vec2 e = vec2(12.0) / extent;
                vec4 fx1 = shoreField(uv + vec2(e.x, 0.0)), fx0 = shoreField(uv - vec2(e.x, 0.0));
                vec4 fz1 = shoreField(uv + vec2(0.0, e.y)), fz0 = shoreField(uv - vec2(0.0, e.y));
                vec2 grad = vec2(fx1.r - fx0.r, fz1.r - fz0.r);
                vec2 gradT = vec2(fx1.b - fx0.b, fz1.b - fz0.b);
                r.dist = f.r;
                r.dir = length(grad) > 1e-6 ? -normalize(grad) : vec2(0.0);
                // The way the swell runs: up its arrival time (smooth; it bends round headlands and the ends of walls, it has no seam where two shores meet).
                r.prop = length(gradT) > 1e-7 ? normalize(gradT) : r.dir;
                travel = f.b;
                r.swell = f.a;
                // Fade out towards the field's edge (it follows the eye; beyond it nothing is known).
                vec2 inside = min(uv, 1.0 - uv) * extent;
                r.open = f.g * smoothstep(0.0, 600.0, min(inside.x, inside.y));
                // In a river the bank's isolines give the channel's axis at the shore field's resolution; the map says which way is down.
                if (r.river > 0.0)
                {
                    // Over 40 units each way: the 12-unit difference follows every kink of the bank and showed as facets in the flow.
                    vec2 w = vec2(40.0) / extent;
                    vec2 wide = vec2(shoreField(uv + vec2(w.x, 0.0)).r - shoreField(uv - vec2(w.x, 0.0)).r,
                                     shoreField(uv + vec2(0.0, w.y)).r - shoreField(uv - vec2(0.0, w.y)).r);
                    vec2 toShore = length(wide) > 1e-6 ? -normalize(wide) : vec2(0.0);
                    vec2 axis = vec2(-toShore.y, toShore.x);
                    float along = dot(axis, coarse);
                    // Only a small correction (within about 25 degrees of the map), and only in a narrow channel: round a pond the isolines circle,
                    // and following them made the water spin in a pinwheel.
                    float trust = smoothstep(12.0, 48.0, length(wide)) * smoothstep(0.85, 0.95, abs(along)) * (1.0 - smoothstep(2.0, 4.0, rv.b * 8.0));
                    r.flow = normalize(mix(coarse, axis * (along < 0.0 ? -1.0 : 1.0), trust));
                }
            }
            // Rivers have no open water: no surf, and (oceanScale) no waves. They run faster where narrow and shallow.
            r.open *= 1.0 - r.river;
            r.speed = mix(30.0, 14.0, smoothstep(1.0, 5.0, rv.b * 8.0)) * mix(1.5, 0.8, smoothstep(5.0, 60.0, r.depth));
            // The breakers: the swell's crests, by its arrival time (travel; ShoreTravel), so they come in from the open water, slow and close up in the shallows and turn
            // towards the depth contours; a little slow noise bends them. Each wave its own size along its crest (waveAt, in the wave's own frame q, which runs with it), and
            // what is left of the swell here (in a narrow inlet, behind a wall or round a headland, less).
            float period;
            float x = waveCount(uShore.x - travel, period) + 0.3 * (wNoise(p / 420.0 + 11.3) - 0.5);
            r.len = (r.depth > 0.0 ? 22.0 * sqrt(clamp(r.depth / 60.0, 1.0 / 60.0, 1.0)) : 14.0) * period;
            r.size = 1.0; r.sizeWet = 1.0; r.brk = 1.0;
            if (r.open > 0.0 && r.dist > -150.0 && r.dist < 700.0)
            {
                float k = floor(x);
                vec2 q = p - r.prop * r.len * fract(x);
                float s0, b0, s1, b1;
                waveAt(k, q, s0, b0);
                waveAt(k - 1.0, q - r.prop * r.len, s1, b1);
                // Continuous across the troughs between waves (the breakers' profile is nil there): the new wave's values take over at its front.
                float t = smoothstep(0.0, 0.25, fract(x));
                r.size = mix(s1, s0, t) * r.swell;
                r.brk = mix(b1, b0, t);
                r.sizeWet = (fract(x) >= 0.5 ? s0 : s1) * r.swell;   // the wet sand remembers the last crest that passed
            }
            r.g = fract(x);
            r.wave = mod(floor(x), 4096.0);
            // A wave breaks where the water is about 1.3 times as deep as it is high (depth 0.78 of the height); its distance from the waterline from the local slope
            // of the floor. One that does not break runs on to the shore (its break point is at the waterline).
            float breakDepth = uShore.w * r.size / 0.78;
            r.breakAt = mix(-40.0, clamp(breakDepth * r.dist / max(r.depth, 0.3), 10.0, 400.0), r.brk);
            return r;
        }

        // How much of the open sea's waves reach p: none in the shallows and on the shore (the breakers take over), fewer in sheltered water.
        float oceanScale(Shore s)
        {
            return smoothstep(0.5, 20.0, s.depth) * smoothstep(-10.0, 150.0, s.dist) * mix(0.3, 1.0, max(s.open, smoothstep(400.0, 1500.0, s.dist))) * (1.0 - s.river);
        }

        // A breaker's height over its phase: a front (g below 0.5, the shore side) that steepens as it nears the break, and a long back.
        float breakerProfile(float g, float steep) { return g < 0.5 ? smoothstep(0.5 - mix(0.22, 0.1, steep), 0.5, g) : 1.0 - smoothstep(0.5, 0.97, g); }
        float breakerProfile(float g) { return breakerProfile(g, 0.0); }

        // The breaker's size (units) before its profile: it builds as the water shoals, peaks just before its break point, collapses into a
        // low bore of whitewater after it, and is gone at the waterline (the run-up takes over).
        float breakerScale(Shore s)
        {
            float build = smoothstep(460.0, 160.0, s.dist) * (1.0 + 0.9 * s.brk * smoothstep(s.breakAt + 140.0, s.breakAt + 10.0, s.dist));
            float collapse = mix(0.3, 1.0, smoothstep(s.breakAt - 30.0, s.breakAt + 5.0, s.dist));
            // No higher than the water is deep: past the break the bore shrinks with the depth, and a film over the sand carries none.
            float limit = clamp(0.78 * max(s.depth, 0.0) / max(uShore.w * s.size, 0.1), 0.0, 1.0);
            return uShore.w * s.open * s.size * build * collapse * limit * smoothstep(-5.0, 20.0, s.dist) * smoothstep(250.0, 120.0, s.depth);
        }

        // The steep profile the fragment shades with.
        float breakerHeight(Shore s) { return breakerScale(s) * breakerProfile(s.g, smoothstep(s.breakAt + 160.0, s.breakAt, s.dist)); }

        #ifdef WATER_CONTACT
        uniform sampler2D uRippleMap;       // WaterRipples: height, its slopes along x and z, foam; world-anchored, wrapping
        uniform vec4 uRippleWindow;         // the window's centre (world xz) and that centre's place in the image
        uniform vec4 uRippleShape;          // x the half side the waves show over, y 1 / the image's side in units, z a texel (units), w 1 with a field

        // Where p is in the ripples' image, and how much of the field shows there (fading out over the window's last 12 units).
        vec2 rippleUv(vec2 p) { return (p - uRippleWindow.xy) * uRippleShape.y + uRippleWindow.zw; }
        // The field's texel, bounded: a value not finite reads as calm water (never a hole or a raised sheet over the land).
        vec4 rippleAt(vec2 p, float lod)
        {
            vec4 r = textureLod(uRippleMap, rippleUv(p), lod);
            if (isnan(r.x) || isnan(r.y) || isnan(r.z) || isnan(r.w) || isinf(r.x) || isinf(r.y) || isinf(r.z) || isinf(r.w)) return vec4(0.0);
            return clamp(r, vec4(-4.0, -2.0, -2.0, 0.0), vec4(4.0, 2.0, 2.0, 1.0));
        }

        float rippleWeight(vec2 p)
        {
            vec2 d = abs(p - uRippleWindow.xy);
            return uRippleShape.w * (1.0 - smoothstep(uRippleShape.x - 12.0, uRippleShape.x, max(d.x, d.y)));
        }
        #endif
        """;

    /// <summary>
    /// The Meitou water's vertex stage: the polar grid around the eye (its outer ring out to the extent), displaced near the eye by the ocean waves
    /// and the breakers' smooth hump; the undisplaced position goes on for the fragment.
    /// </summary>
    const string MeitouVertex = "#version 330 core\n" + TerrainShaders.HeightFunctions + MeitouMotion + """

        layout(location = 0) in vec2 aXZ;   // the polar grid around the eye; the outer ring is marked by a length over 1e5
        uniform mat4 uViewProjection;
        uniform vec2 uCentre;
        uniform float uExtent;
        uniform vec3 uEye;
        uniform float uGridStep;            // the grid's spacing over its radius
        out vec3 vWorld;
        out vec2 vBase;                     // the undisplaced position: the fragment's waves and shore are evaluated there
        void main()
        {
            float r = length(aXZ);
            vec2 p = uCentre + (r > 1.0e5 ? aXZ / r * uExtent : aXZ);
            vec3 w = vec3(p.x, uWaterHeight, p.y);
            float dist = distance(w, uEye);
            bool inside = max(abs(p.x), abs(p.y)) < uHalfWorld;
            if (dist < uWaveFade.w && inside && r < 1.0e5)
            {
                float spacing = max(r, 1.0) * uGridStep;
                float fade = 1.0 - smoothstep(uWaveFade.x, uWaveFade.y, dist), shoreFade = 1.0 - smoothstep(uWaveFade.z, uWaveFade.w, dist);
                Shore s = shoreAt(p);
                // The geometry gets a smooth hump only (the steep front a few units wide would fall between the vertices and facet; the
                // fragment shades it), and none where the vertices are too far apart for it.
                float hump = breakerScale(s) * pow(0.5 - 0.5 * cos(6.2831853 * s.g), 1.5) * smoothstep(0.45, 0.2, spacing / s.len);
                float lift = uShore.y * s.open * max(s.size, s.sizeWet) * (1.0 - smoothstep(10.0, 60.0, s.dist)) * smoothstep(-140.0, -60.0, s.dist);
                float scale = oceanScale(s) * fade;
                vec3 d = vec3(0.0);
                if (uLean < 0.5 || scale > 0.0) d = oceanDisplace(p, spacing).xyz * scale;
                w += vec3(d.x, d.y + max(hump, lift) * shoreFade, d.z);
            }
            #ifdef WATER_CONTACT
            // The interactive waves' height, only where the vertices are close enough to carry it (further out the fragment's normals do).
            float rw = r < 1.0e5 ? rippleWeight(p) * (1.0 - smoothstep(1.0, 3.0, max(r, 1.0) * uGridStep / uRippleShape.z)) : 0.0;
            if (rw > 0.0) w.y += rippleAt(p, 0.0).r * rw;
            #endif
            vBase = p;
            vWorld = w;
            gl_Position = uViewProjection * vec4(w, 1.0);
        }
        """;

    /// <summary>
    /// The Meitou water's fragment body (after the shared functions), shading the waves and the shore at the undisplaced position.
    /// </summary>
    const string MeitouBody = """

        in vec3 vWorld;
        in vec2 vBase;
        out vec4 fragColour;
        uniform vec3 uEye;
        uniform float uWaterFar;
        uniform float uTime;
        uniform vec3 uSunDir;
        uniform vec3 uSunColour;
        uniform vec3 uGlintDir;         // the specular glint's light: the sun's direction and colour, or by night the planet's own radiance (WorldLighting.Glint)
        uniform vec3 uGlintColour;
        uniform sampler2D uColourMap;
        uniform sampler2D uFlowMap;
        uniform sampler2D uFoamMap;     // WaterFoam: the caustics' lace in R, patches in G, bubbles in B, the froth in A
        uniform sampler2D uNormalMap;
        uniform sampler2D uRainMap;
        uniform sampler2D uParamsA;
        uniform sampler2D uParamsB;
        uniform vec4 uSeaA;
        uniform vec4 uSeaB;
        uniform vec3 uSeaColour;
        uniform sampler2D uReflection;
        uniform mat4 uReflectionViewProjection;
        uniform float uReflect;
        uniform sampler2D uRefraction;  // the scene under the water, copied at half size before it (CaptureScene)

        // The scene under the water at a screen position, from the half-size copy: its four texels weighed by how near the depths they were made of are to this pixel's
        // own (zHere, 0 without a depth copy: plain filtering). A leg or a blade in the water otherwise took some of the bright floor beside it, which it shows through
        // its own thin layer of water: a light rim along its edge.
        vec3 sceneUnder(vec2 uv, float zHere)
        {
            if (zHere <= 0.0) return texture(uRefraction, uv).rgb;
            ivec2 size = textureSize(uRefraction, 0);
            vec2 full = vec2(textureSize(uSceneDepth, 0));
            vec2 f = uv * vec2(size) - 0.5;
            ivec2 i0 = ivec2(floor(f));
            vec2 w = f - vec2(i0);
            vec3 sum = vec3(0.0);
            float total = 0.0;
            for (int k = 0; k < 4; k++)
            {
                ivec2 o = ivec2(k & 1, k >> 1);
                ivec2 q = clamp(i0 + o, ivec2(0), size - 1);
                vec4 d = textureGather(uSceneDepth, (vec2(q) * 2.0 + 1.0) / full);   // the four pixels the texel averages
                float rel = 0.0;
                for (int j = 0; j < 4; j++) rel = max(rel, abs(depthViewZ(d[j], uDepthPlanes.xy) - zHere));
                float weight = max((o.x == 1 ? w.x : 1.0 - w.x) * (o.y == 1 ? w.y : 1.0 - w.y), 1e-3) / (rel / zHere + 0.005);
                sum += texelFetch(uRefraction, q, 0).rgb * weight;
                total += weight;
            }
            return sum / total;
        }
        uniform float uRefract;         // 1: refract it and composite here; 0: blend over the scene as the game's water
        uniform vec2 uScreen;           // 1 / the target's size: gl_FragCoord to the copy's coordinates
        uniform vec2 uCentre;           // the grid's centre and its spacing over its radius (the vertex stage's), for the hump's vertex spacing
        uniform float uGridStep;
        uniform vec3 uClarity;          // x: the absorption per unit of depth times this (1 / the Tab panel's clarity); y: the far water's clarity (0 the true slant path, 1 none); z: the see-through distance (the game: 4000)

        vec3 sampleNormal(vec2 coord, vec2 direction, float speed, float time)
        {
            float t = fract(time);
            vec3 n = texture(uNormalMap, coord + direction * speed * t).rgb * 2.0 - 1.0;
            return n.xzy * (1.0 - abs(t * 2.0 - 1.0));
        }

        vec3 rainRipple(vec2 coord, float time, float weight)
        {
            vec4 ripple = texture(uRainMap, coord);
            float dropTime = fract(ripple.a + time * 0.5);
            float dropFrac = dropTime - 1.0 + ripple.r * 0.5 + 0.5;
            float s = -sin(clamp(dropFrac * 16.0, 0.0, 2.0) * 3.14159);
            vec3 rainNormal = ripple.grb;
            rainNormal.xz *= s;
            return rainNormal * weight * ripple.r;
        }

        vec3 rotatedNormal(vec2 coord, vec2 direction, float speed, float time, mat2 r)
        {
            vec3 n = sampleNormal(r * coord, r * direction, speed, time);
            n.xz = transpose(r) * n.xz;
            return n;
        }

        // A river's moving normals: the game's normal map carried along the flow at its speed, as two samples half a cycle apart cross-faded
        // (each is renewed where its weight is zero, so the pattern does not stretch where the flow turns). The map's coordinates are rotated and
        // rescaled per layer so the layers do not line up; dp: the pixel's footprint (the samples are in a branch, so they take explicit gradients).
        vec3 riverLayer(vec2 p, vec2 flow, float speed, vec2 scale, mat2 r, float phase, vec2 dpx, vec2 dpy)
        {
            vec3 sum = vec3(0.0);
            for (int i = 0; i < 2; i++)
            {
                float t = fract(uTime * 240.0 + phase + 0.5 * float(i));   // a cycle of 2.5 s
                vec2 q = p - flow * speed * 2.5 * (t - 0.5);
                vec3 n = textureGrad(uNormalMap, r * q * scale, r * dpx * scale, r * dpy * scale).rgb * 2.0 - 1.0;
                n = n.xzy;
                n.xz = transpose(r) * n.xz;
                sum += n * (1.0 - abs(t * 2.0 - 1.0));
            }
            return sum;
        }

        // The foam's two blotch scales (x: froth, y: noise) stretched along the flow into streaks and carried with it, cross-faded the same way.
        void riverFoam(vec2 p, vec2 flow, float speed, vec2 dpx, vec2 dpy, out vec2 a1, out vec2 a2)
        {
            a1 = vec2(0.0); a2 = vec2(0.0);
            mat2 frame = mat2(flow.x, -flow.y, flow.y, flow.x);   // world to (along, across)
            vec2 s1 = vec2(0.0024, 0.009), s2 = vec2(0.0072, 0.027);
            for (int i = 0; i < 2; i++)
            {
                float t = fract(uTime * 240.0 + 0.5 * float(i));
                float w = 1.0 - abs(t * 2.0 - 1.0);
                vec2 q = frame * (p - flow * speed * 2.5 * (t - 0.5));
                a1 += textureGrad(uFoamMap, q * s1, frame * dpx * s1, frame * dpy * s1).ag * w;
                a2 += textureGrad(uFoamMap, q * s2 + 0.37, frame * dpx * s2, frame * dpy * s2).ag * w;
            }
        }

        // The foam lit as the land is lit (kenshiLight's diffuse terms, the sun by the biome's ambient map and the sun shadow, and the irradiance cube
        // by the ambient map's colour and the environment factor; no specular), with the ambient map read at level 0 as the shader runs in a branch.
        vec3 foamLight(vec3 albedo, vec3 n, vec3 world, float shadow)
        {
            vec4 am = uAtmoMaps.z < 0.5 ? vec4(1.0, 1.0, 1.0, 0.5) : atmoAmbientFloor(textureLod(uAtmoAmbientMap, (world.xz + uAtmoMaps.w) / (2.0 * uAtmoMaps.w), 0.0), uAtmoMaps);
            vec3 sun = uAtmoSunLight * am.a * 2.0 * atmoCloudShadow(world) * shadow;
            vec3 diffuse = ATMO_PI * clamp(dot(n, uAtmoLight.xyz), 0.0, 1.0) * sun * 0.96;
            return albedo * (diffuse + atmoIrradiance(n) * 0.96 * am.rgb * uAtmoLight.w);
        }

        #ifdef WATER_CONTACT
        // How far from the eye the contact foam and the wakes reach, and the distance over which they fade out there.
        const float ContactFar = 1200.0, ContactFade = 400.0;

        // Foam where something stands in the water (docs/render-water.md "Contact foam"): the view ray ends close behind the surface, on something that stands above
        // the terrain under this point (the shore has foam of its own). thick: the way through the water to it; depth: the terrain's depth here; up: the view's
        // height; blotch: the band's edge wanders with the foam's noise.
        float contactFoam(float thick, float depth, float up, float blotch)
        {
            if (thick < 0.0) return 0.0;
            float standing = smoothstep(2.5, 4.5, depth - thick * up);   // the ground's relief (ripples, displacement) stands less than this
            return 0.85 * standing * (1.0 - smoothstep(0.0, mix(1.5, 4.0, blotch), thick));
        }

        #endif

        void main()
        {
            vec2 p = vBase;
            vec2 dpx = dFdx(p), dpy = dFdy(p);
            vec2 map = (p + uHalfWorld) / (2.0 * uHalfWorld);
            vec4 pa = texture(uParamsA, map);
            vec4 pb = texture(uParamsB, map);
            vec3 waterColour = texture(uColourMap, map).rgb;
            vec2 beyond = abs(p) - uHalfWorld;
            float outside = max(max(beyond.x, beyond.y), 0.0);
            float sea = smoothstep(-30000.0, 12000.0, max(beyond.x, beyond.y));
            pa = mix(pa, uSeaA, sea);
            pb = mix(pb, uSeaB, sea);
            waterColour = mix(waterColour, uSeaColour, sea);
            vec3 toEye = uEye - vWorld;
            float dist = length(toEye);
            vec3 toEyeDir = toEye / dist;

            // The motion fades with the eye distance; beyond it (and past the world's edge) this is the game's flat water.
            float fade = outside <= 0.0 ? 1.0 - smoothstep(uWaveFade.z, uWaveFade.w, dist) : 0.0;
            Shore s;
            s.depth = 1.0e4; s.dist = 1.0e6; s.g = 0.0; s.wave = 0.0; s.dir = vec2(0.0); s.open = 0.0; s.size = 1.0; s.breakAt = 0.0; s.river = 0.0; s.flow = vec2(1.0, 0.0); s.speed = 0.0; s.sizeWet = 1.0; s.brk = 1.0; s.len = 100.0; s.prop = vec2(1.0, 0.0); s.swell = 0.0;
            if (fade > 0.0) s = shoreAt(p);
            else if (outside <= 0.0) s.depth = uWaterHeight - terrainHeight(p);

            // The swash: a breaker that reached the shore runs up the beach (to where the ground is `level` above the still water) and back,
            // leaving the sand wet behind it; above both the water is not there.
            float runup = uShore.y * s.open * s.size * fade * smoothstep(-120.0, -40.0, s.dist);   // at most about 10 m up the beach: low flats further in stay dry
            float above = -s.depth;
            float sinceCrest = fract(s.g - 0.5);
            float level = runup * breakerProfile(s.g);
            float wetLevel = uShore.y * s.open * s.sizeWet * fade * smoothstep(-120.0, -40.0, s.dist) * (1.0 - 0.8 * sinceCrest);
            // Each wave runs further up the beach in some places than in others (tongues some 70 units across), and the wet sand it leaves is as ragged,
            // so the waterline is not one smooth copy of the shore.
            if (runup > 0.0)
            {
                level *= mix(0.55, 1.0, wNoise(p / 70.0 + vec2(wHash(vec2(s.wave, 3.1)), wHash(vec2(s.wave, 8.7))) * 97.0));
                wetLevel *= mix(0.7, 1.0, wNoise(p / 55.0 + 13.7));
            }
            if (above > 0.0 && above >= max(level, wetLevel)) discard;
            bool swash = above > 0.0;
            float sheet = swash ? level - above : 0.0;   // the swash's thickness (negative: wet sand only)

            // Strength first: explicit gradients captured before discard keep mip selection defined in the branch.
            float open = oceanScale(s) * (1.0 - smoothstep(9000.0, 14000.0, dist));
            vec4 oceanSlope = vec4(0.0);
            float oceanHeight = 0.0, oceanFoam = 0.0;
            if (uOcean.w > 0.0 && (uLean < 0.5 || open > 0.0))
                for (int c = 0; c < 3; c++)
                {
                    float tile = uOcean[c];
                    oceanSlope += textureGrad(uOceanMaps, vec3(p / tile, 3.0 + float(c)), dpx / tile, dpy / tile);
                    vec4 d = textureGrad(uOceanMaps, vec3(p / tile, float(c)), dpx / tile, dpy / tile);
                    oceanHeight += d.y;
                    oceanFoam += d.w * (1.0 - 0.3 * float(c));
                }

            // The normal: the game's scrolled normal map along the flow, the rain's ripples, then the open waves' slopes added.
            vec2 tex = p * pa.xy;
            vec2 direction = (texture(uFlowMap, map).rg * 2.0 - 1.0) * (pa.xy * 5000.0);
            float speed = length(direction);
            direction /= max(speed, 1e-5);
            float distortion = max(pb.z, 1.0);
            speed /= distortion;
            float time = uTime * distortion;
            vec3 nm = vec3(0.0, 1.0, 0.0);
            // The game's three phase-shifted samples, but the second and third rotated and rescaled (and their normals turned back), so the
            // map's 250-unit tile no longer lines up into a grid; a slow noise makes some patches rougher than others.
            nm += sampleNormal(tex, direction, speed, time);
            nm += rotatedNormal(tex * 0.71 + vec2(0.1, 0.3), direction, speed, time + 0.33, mat2(0.36, 0.93, -0.93, 0.36));
            nm += rotatedNormal(tex * 1.37 + vec2(0.4, 0.7), direction, speed, time + 0.66, mat2(-0.74, 0.67, -0.67, -0.74));
            // Rivers: the normals run with the current instead (faster where it is narrow and shallow), rougher where it rushes.
            float rw = s.river * fade;
            float blotch = texture(uFoamMap, p * 0.0013 + vec2(0.31, 0.17)).g;
            float rapid = rw * smoothstep(32.0, 46.0, s.speed) * smoothstep(0.35, 0.7, blotch);   // fast, shallow stretches, in patches
            if (rw > 0.01)
            {
                vec3 rn = vec3(0.0, 1.0, 0.0);
                rn += riverLayer(p, s.flow, s.speed, pa.xy, mat2(1.0, 0.0, 0.0, 1.0), 0.0, dpx, dpy) * 0.5;
                rn += riverLayer(p, s.flow, s.speed, pa.xy * 0.71, mat2(0.36, 0.93, -0.93, 0.36), 0.33, dpx, dpy) * 0.5;
                rn += riverLayer(p, s.flow, s.speed, pa.xy * 1.37, mat2(-0.74, 0.67, -0.67, -0.74), 0.66, dpx, dpy) * 0.5;
                rn.xz *= 1.0 + 1.3 * rapid;
                nm = mix(nm, rn, rw);
            }
            float rough = texture(uFoamMap, p * 0.00035 + vec2(uTime * 0.02, 0.0)).g;
            // Near the eye the ocean's own slopes carry the detail; the game's map takes over with the distance.
            nm.xz *= (0.55 + 0.9 * rough) * mix(0.35, 1.0, smoothstep(1500.0, 6000.0, dist));
            nm.y *= pa.z;
            float rain = uWeatherWet.y * clamp(toEyeDir.y * 2.0 - dist * 0.0001, 0.0, 1.0);
            if (rain > 0.0)
            {
                vec2 rainCoord = p * 0.01;
                nm += rainRipple(rainCoord * 6.0, time * 1.5, rain * 2.0);
                nm += rainRipple(rainCoord.yx * 6.0, time * 1.5 + 0.5, rain * 2.0);
                nm += rainRipple(rainCoord * 2.0, time + 0.3, rain * 0.8);
            }
            nm = normalize(nm);
            // The ocean: its height's slopes over the stretch of its horizontal displacement, and the Jacobian (below about 0.7 the crests
            // fold enough to foam).
            vec4 os = oceanSlope * open;
            vec2 spread = max(vec2(1.0) + os.zw, vec2(0.25));
            float jacobian = spread.x * spread.y;
            vec2 slope = nm.xz / max(nm.y, 0.05) - os.xy / spread;
            // The breaker's hump tilts the surface: its height falls towards the shore on the front and rises on the back.
            Shore ahead = s, behind = s;
            ahead.g = fract(s.g - 0.03); ahead.dist -= 0.03 * s.len;
            behind.g = fract(s.g + 0.03); behind.dist += 0.03 * s.len;
            float breaker = breakerHeight(s) * fade;
            // The breaker's shading fades with the distance: a few pixels across its steep front only read as a dark streak.
            float near = 1.0 - smoothstep(700.0, 2500.0, dist);
            // Over 6% of a wavelength and at most 0.35: the 2% difference made a thin, steep strip ahead of every crest, which reflected as a bright
            // greenish line all along it.
            slope += s.prop * clamp((breakerHeight(behind) - breakerHeight(ahead)) * fade * near * 0.7 / (0.06 * s.len), -0.35, 0.35);
            #ifdef WATER_CONTACT
            // The interactive waves (docs/render-water.md "Wakes and ripples"): their slopes tilt the surface; their foam joins the rest below.
            vec4 ripple = vec4(0.0);
            float rippleW = outside <= 0.0 ? rippleWeight(p) : 0.0;
            if (rippleW > 0.0)
            {
                // The field has one level: where a pixel spans several of its texels the waves are smaller than it, and fade out (the foam stays).
                ripple = rippleAt(p, 0.0) * rippleW;
                ripple.xyz *= 1.0 - smoothstep(1.5, 4.0, max(length(dpx), length(dpy)) / uRippleShape.z);
            }
            slope -= ripple.yz;
            #endif
            vec3 n = normalize(vec3(slope.x, 1.0, slope.y));

            float gloss = clamp(pb.x, 0.0, 1.0);
            vec3 l = normalize(uSunDir);
            vec3 glintL = normalize(uGlintDir);
            vec3 h = normalize(glintL + toEyeDir);
            float power = max(exp2(gloss * 11.0 + 1.0) / (1.0 + dist / 6000.0), 12.0);
            float nh = max(dot(n, h), 0.0);
            float spec = pow(nh, power) * (power + 8.0) / 25.0 * gloss;
            // Glitter: far away the waves are smaller than a pixel, so the sun's glint spreads into a band of sparkles, a broader lobe let
            // through where a fine, moving pattern peaks.
            float far = smoothstep(800.0, 4000.0, dist);
            // The glitter's mean only (0.15 of a broader lobe): a mask from the foam texture showed its cells, standing still, in a band of distances.
            spec += pow(nh, power * 0.12) * (power * 0.12 + 8.0) / 25.0 * gloss * 0.15 * far * 2.5;
            float lh = clamp(dot(glintL, h), 0.0, 1.0);
            spec *= mix(1.0, 0.04 + 0.96 * exp2((-5.55473 * lh - 6.98316) * lh), uAtmoAltitude.z);
            float cosv = max(dot(toEyeDir, n), 0.0);
            float schlick = 0.02 + 0.98 * pow(1.0 - cosv, 5.0);
            vec3 reflected = skyColour(reflect(-toEyeDir, n), false);
            if (uReflect > 0.5)
            {
                vec4 rc = uReflectionViewProjection * vec4(vWorld, 1.0);
                vec2 offset = n.xz * (60.0 / rc.w);
                offset *= min(1.0, 0.04 / max(length(offset), 1e-5));
                vec2 uv = rc.xy / rc.w * 0.5 + 0.5 + offset;
                vec2 inside = smoothstep(vec2(0.0), vec2(0.04), uv) * smoothstep(vec2(0.0), vec2(0.04), 1.0 - uv);
                vec3 mirrored = min(texture(uReflection, uv).rgb, vec3(3.0));
                // The sun's glint only where the mirror shows sky: where it shows land (darker than the sky the same way), the land hides the
                // sun, else its streak runs through the hills' reflection. The game multiplies its specular by the reflection's brightness.
                const vec3 lum = vec3(0.2126, 0.7152, 0.0722);
                float open = smoothstep(0.5, 0.85, dot(mirrored, lum) / max(dot(reflected, lum), 1e-4));
                spec *= mix(1.0, open, inside.x * inside.y);
                reflected = mix(reflected, mirrored, inside.x * inside.y);
            }
            vec3 light = max(dot(n, l), 0.0) * uSunColour * 0.6 + uSkyZenith * 0.5 + 0.03;
            // What the biome's water colour says: its brightness, hue (the strongest channel 1), how coloured it is, and `clean`: bright,
            // blue-green water, the only kind that glows through its crests and foams white (black, rusty or olive water does neither).
            float wLuma = dot(waterColour, vec3(0.299, 0.587, 0.114));
            vec3 hue = waterColour / max(max(waterColour.r, waterColour.g), max(waterColour.b, 1e-3));
            float saturation = 1.0 - min(hue.r, min(hue.g, hue.b));
            float warm = clamp((waterColour.r - waterColour.b) / (wLuma + 0.02), 0.0, 1.0);
            float clean = smoothstep(0.06, 0.3, wLuma) * (1.0 - 0.75 * warm);
            float overcast = 1.0 - 0.8 * uWeatherWet.y;   // rain: no sun to shine through crests or to focus on the floor
            float deepWater = smoothstep(6.0, 40.0, s.depth);   // over sand, a few units deep, there is nothing to glow
            // The light the water scatters back out of its body, and the light through the crests: with the sun behind a wave its thin top
            // glows in the water's own colour (a faint blue-green only in clear water; stronger the higher the crest and the more the eye looks
            // towards the sun), and not at all in the shallows.
            vec3 body = waterColour * light;
            float crest = smoothstep(1.0, 6.0, oceanHeight * open);
            vec2 towardsSun = normalize(l.xz + vec2(1e-5)), looking = normalize(-toEyeDir.xz + vec2(1e-5));
            float back = pow(clamp(dot(looking, towardsSun), 0.0, 1.0), 3.0) * smoothstep(-0.05, 0.2, l.y);
            body += (waterColour * 1.2 + vec3(0.02, 0.1, 0.09) * clean) * uSunColour * crest * deepWater * overcast * (back * 0.7 + 0.04) * (1.0 - 0.6 * toEyeDir.y) * (1.0 - far);

            // The water's depth along the view: the distance through it to what is behind it (the floor, a rock, a leg), from the depth copy;
            // without one, the slant path down to the terrain's height. The swash: its own thin sheet (the surface is raised over the whole beach
            // and cut back to the run-up here, so its height over the sand is not the water's). The slant path grows to 20 times the depth at
            // grazing angles, so far water reads opaque; the far clarity (uClarity.y) shortens it towards the plain depth.
            float floorDepth = max(0.0, vWorld.y - (uLean > 0.5 && outside <= 0.0 ? uWaterHeight - s.depth : terrainHeight(p)));
            float thick = sceneThickness(dist);
            // The surface near the shore is raised to the swash's highest reach (the vertex stage's lift) and cut back to this moment's run-up
            // on the beach (above); in the water beside it the raise over the run-up is not water either, else the water stood a few units
            // deep at the waterline while the sand beside it was dry.
            if (thick >= 0.0 && !swash && fade > 0.0)
            {
                float lift = uShore.y * s.open * max(s.size, s.sizeWet) * (1.0 - smoothstep(10.0, 60.0, s.dist)) * smoothstep(-140.0, -60.0, s.dist);
                float spacing = max(length(vBase - uCentre), 1.0) * uGridStep;
                float hump = breakerScale(s) * pow(0.5 - 0.5 * cos(6.2831853 * s.g), 1.5) * smoothstep(0.45, 0.2, spacing / s.len);
                thick = max(thick - max(lift - max(hump, level), 0.0) * fade / max(toEyeDir.y, 0.05), 0.0);
            }
            float depth = thick >= 0.0 && !swash ? thick * pow(max(toEyeDir.y, 0.05), uClarity.y)
                : (swash ? max(sheet, 0.0) : floorDepth) / pow(max(toEyeDir.y, 0.05), 1.0 - uClarity.y);
            if (outside > 0.0) depth = 1e4;
            bool refract = uRefract > 0.5;
            vec3 under = vec3(0.0);
            vec3 colour;
            if (refract)
            {
                // What lies under the water, seen through the waves: the scene copied before the water, its lookup bent by the surface's slope
                // (less in thin water and far away).
                vec2 bend = n.xz * 0.06 * clamp(depth / 30.0, 0.0, 1.0) / (1.0 + dist / 600.0);
                float zHere = uDepthPlanes.z > 0.5 ? depthViewZ(texelFetch(uSceneDepth, ivec2(gl_FragCoord.xy), 0).r, uDepthPlanes.xy) : 0.0;
                if (zHere > 0.0)
                {
                    // The bend stays on what this pixel shows: across a silhouette it brought the floor's colour under a leg's thin water, or the leg's under the floor's deep water.
                    ivec2 bent = clamp(ivec2(gl_FragCoord.xy + bend / uScreen), ivec2(0), textureSize(uSceneDepth, 0) - 1);
                    bend *= 1.0 - smoothstep(0.03, 0.1, abs(depthViewZ(texelFetch(uSceneDepth, bent, 0).r, uDepthPlanes.xy) - zHere) / zHere);
                }
                under = sceneUnder(gl_FragCoord.xy * uScreen + bend, zHere);
                // Caustics: the waves focus the sun into a moving web of light on the floor of shallow water (two drifting webs of the foam
                // texture's cell rims, multiplied).
                if (uLean < 0.5 || (dist < 2500.0 && floorDepth > 1.5 && l.y > 0.0))
                {
                    vec2 floorP = p - toEyeDir.xz / max(toEyeDir.y, 0.05) * floorDepth * 0.75;
                    float tc = uTime * 30.0;
                    // Softer with the depth (the web blurs a few units down, as the real one does), weaker than a bright sand would show, and gone in
                    // the shallows, far away and in rain.
                    float blur = 1.0 + clamp(floorDepth * 0.15, 0.0, 3.0);
                    float c1 = textureLod(uFoamMap, floorP * 0.011 + vec2(tc * 0.011, tc * 0.006), blur).r;
                    float c2 = textureLod(uFoamMap, mat2(0.8, 0.6, -0.6, 0.8) * floorP * 0.0147 - vec2(tc * 0.008, -tc * 0.01), blur).r;
                    float caustic = (c1 * c2 * 0.8 + (c1 + c2) * 0.04) * 0.5 * max(l.y, 0.0) * smoothstep(1.5, 7.0, floorDepth) * exp(-floorDepth / 18.0) * (1.0 - smoothstep(500.0, 2500.0, dist)) * overcast;
                    under *= 1.0 + caustic * uSunColour;
                }
                // Absorption: in clear water red goes first, then green, so the shallows over sand are turquoise and the deep water dark; a
                // strongly coloured biome water (a swamp's olive, a red lake) filters towards its own colour instead. The biome's opacity (the
                // game's alpha per unit of depth) sets how fast. The floor fades out from the see-through distance (the game: opaque at 4000) to 1.5 times it.
                // The clear-water spectrum only for bright water: dark grey water (Shark's ForestLand, 20 20 20) is a murky film in the game, and
                // red going first turned the mud under it teal; it filters by its own hue instead.
                vec3 sigma = mix(1.0 + 3.0 * (1.0 - hue), vec3(4.5, 1.6, 1.1), (1.0 - smoothstep(0.15, 0.5, saturation)) * smoothstep(0.06, 0.3, wLuma));
                vec3 transmit = exp(-depth * max(pa.w, 0.002) * uClarity.x * sigma) * (1.0 - smoothstep(uClarity.z, uClarity.z * 1.5, dist));
                colour = mix(under * transmit + body * (1.0 - transmit), reflected, schlick * gloss);
            }
            else colour = mix(body, reflected, schlick * gloss);
            colour += min(spec, 4.0) * uGlintColour * 0.25 * atmoCloudShadow(vWorld) * (swash ? 0.25 : 1.0) + pb.y * waterColour;

            // A breaker's face: light comes through its thin lip, so it is brighter than the water (in its colour), and less see-through.
            // Only as the wave steepens just before it breaks (along the whole swell it drew a yellow-green band ahead of every crest).
            float lip = smoothstep(0.22, 0.45, s.g) * (1.0 - smoothstep(0.46, 0.51, s.g)) * clamp(breaker / max(uShore.w, 0.1), 0.0, 1.0) * smoothstep(4.0, 24.0, s.depth)
                * smoothstep(s.breakAt + 80.0, s.breakAt + 10.0, s.dist);
            vec3 lipColour = (waterColour * 1.8 + vec3(0.01, 0.04, 0.035) * clean) * (uSunColour * 0.7 * overcast + uSkyZenith * 0.6);
            colour = mix(colour, max(colour, lipColour), lip * 0.35 * near);   // it only brightens: dark water has no light to show through its lip
            // and the trough in front of it is darker.
            float trough = smoothstep(0.05, 0.2, s.g) * (1.0 - smoothstep(0.2, 0.3, s.g)) * clamp(breakerHeight(behind) / max(uShore.w, 0.1), 0.0, 1.0) * (1.0 - smoothstep(s.breakAt, s.breakAt - 30.0, s.dist));
            colour *= 1.0 - 0.25 * trough * near;

            // Foam (WaterFoam's lace, let through the more the more foam there is): sharp open-water crests in a wind and the foam they leave
            // behind (OceanWaves keeps it a few seconds); the breaking crest as a line and the foam it leaves behind; a thin edge at the
            // waterline; the swash's front and the lace in its sheet.
            // Outside the break point the swell only feathers at its crest; at the break it bursts white; inside it a bore of whitewater runs
            // to the shore with lace trailing behind it.
            float surfZone = smoothstep(-2.0, 10.0, s.dist) * smoothstep(250.0, 120.0, s.depth) * fade * s.open * min(uShore.w, 1.0);
            // The surf's foam is read from the lace in the wave's own frame: q0 is the point's nearest shore point (where the wave front it
            // sits on crosses the shore) shifted along the normal by the phase g, so a point carried by the wave keeps its q0 and the foam
            // drifts in with it instead of swimming through a fixed pattern. A slow clump noise ragged-edges the band's front and back.
            // Every wave reads its own part of the lace: q0 is offset by a hash of the wave's number (which changes in the trough, g 0/1,
            // where there is no foam), so the next wave is not a copy of this one.
            // As the foam ages (ageS below: the way run in since the break, and the distance behind the crest) the lace is stretched along
            // the direction of travel about the crest, up to 2.1 times (the lookup's along-shore-normal scale shrinks): the bore's round
            // blobs run out into streaks in its wake. The clump (the bands' envelope) is not stretched.
            float amount = max(clamp((0.7 - jacobian) * 3.75, 0.0, 1.0), clamp(oceanFoam * open * 1.5, 0.0, 1.0) * 0.75);
            float surfAmount = 0.0, clump = 0.5, ageS = 0.0;
            vec2 q = vec2(0.0), b1 = vec2(0.5), b2 = vec2(0.5);
            float fine = 0.5;
            // Sheltered ponds have no surf; keep river, open-water and waterline foam independent below.
            if (uLean < 0.5 || surfZone > 0.0 || swash)
            {
                float travel = clamp((s.breakAt - (s.dist - s.len * (s.g - 0.5))) / max(s.breakAt + 10.0, 40.0), 0.0, 1.0);
                ageS = clamp(0.6 * travel + 0.5 * smoothstep(0.5, 0.95, s.g), 0.0, 1.0);
                float squeeze = 1.0 - 1.0 / (1.0 + 1.1 * ageS * ageS);
                // The foam's frame runs with the wave (the phase goes back as the crest comes in), and is a new one for every wave (its number's hash).
                vec2 q0 = p - s.prop * (s.len * s.g) + vec2(wHash(vec2(s.wave, 21.7)), wHash(vec2(s.wave, 5.3))) * 997.0;
                q = q0 + s.prop * (s.len * (s.g - 0.5) * squeeze);
                vec2 gx = dpx + s.prop * (dot(s.prop, dpx) * (1.0 - squeeze)), gy = dpy + s.prop * (dot(s.prop, dpy) * (1.0 - squeeze));
                // On the swash the foam rides the sheet: up the beach with the uprush and back down with the backwash (the sheet's edge moves level / slope
                // across the sand; no per-wave hash, whose change would draw a line on the sand).
                if (swash)
                {
                    float beach = clamp(s.depth / min(s.dist, -1.0), 0.02, 0.5);
                    q = p - s.prop * (level / beach);
                    gx = dpx;
                    gy = dpy;
                }
                clump = textureGrad(uFoamMap, q0 * 0.0037 + vec2(0.21, 0.63), dpx * 0.0037, dpy * 0.0037).g;
                if (fade > 0.0)
                {
                    b1 = textureGrad(uFoamMap, q * 0.009, gx * 0.009, gy * 0.009).ag;
                    b2 = textureGrad(uFoamMap, q * 0.027 + 0.37, gx * 0.027, gy * 0.027).ag;
                    mat2 turnA = mat2(0.8, 0.6, -0.6, 0.8), turnB = mat2(-0.28, 0.96, -0.96, -0.28);
                    fine = textureGrad(uFoamMap, turnA * q * 0.027 + 0.61, turnA * gx * 0.027, turnA * gy * 0.027).a * 0.5
                         + textureGrad(uFoamMap, turnB * q * 0.045 + 0.23, turnB * gx * 0.045, turnB * gy * 0.045).a * 0.5;
                }
                float gw = s.g + (clump - 0.5) * 0.22, dw = s.dist + (clump - 0.5) * 36.0;
                float crestAt = dw - s.len * (gw - 0.5);   // the distance of the crest this point trails (or leads)
                float broken = smoothstep(s.breakAt + 15.0, s.breakAt - 15.0, crestAt);   // the wave has broken: its foam follows the crest back out to the break point
                float burst = (1.0 - smoothstep(0.0, 45.0, abs(dw - s.breakAt + 10.0))) * smoothstep(0.42, 0.5, gw) * (1.0 - smoothstep(0.62, 0.8, gw));
                float bore = broken * smoothstep(0.43, 0.5, gw) * (1.0 - smoothstep(0.54, 0.68, gw));
                float trail = broken * smoothstep(0.56, 0.66, gw) * (1.0 - smoothstep(0.66, 0.95, gw)) * 0.8;
                float feather = (1.0 - broken) * smoothstep(0.47, 0.5, gw) * (1.0 - smoothstep(0.5, 0.53, gw)) * smoothstep(s.breakAt + 120.0, s.breakAt + 20.0, dw);
                // Along the shore the breakers break harder in some stretches than others, and not at all in a few.
                float stretch = smoothstep(0.2, 0.75, textureGrad(uFoamMap, p * 0.0011, dpx * 0.0011, dpy * 0.0011).g);
                float surfFoam = max(max(burst * 1.3, bore * 0.9), max(trail * 1.1, feather * 0.6)) * min(s.size * 1.1, 1.3) * mix(0.275, 1.0, s.brk);
                surfAmount = min(surfFoam * surfZone * (0.525 + 0.725 * stretch) * (0.825 + 0.4 * clump) * 1.65, 0.88);   // capped: the lace shows through even the thickest whitewater
            }
            // At the waterline of an exposed shore a little foam comes in with each wave and wears off before the next (a pond's edge has none: it stood still).
            amount = max(amount, (1.0 - smoothstep(0.0, 4.0, abs(s.dist))) * 0.3 * s.open * (1.0 - sinceCrest) * fade);
            // The swash: its front is dense, the sheet behind it thins out.
            // The swash's front: a line of foam whose width wanders along the shore (a thread here, a broad band there), thinning out at its very
            // edge and broken by the lace, and a thin lace in the sheet behind it.
            if (swash && sheet > 0.0)
            {
                float wander = wNoise(p / 40.0 + 7.3);
                float band = runup * mix(0.04, 0.3, wander * wander);
                float frontFoam = 0.8 * (1.0 - smoothstep(0.0, band, sheet + (b1.g - 0.5) * band)) * smoothstep(0.0, 0.03 + 0.12 * b2.g, sheet);
                surfAmount = max(surfAmount, max(frontFoam, 0.3 * (1.0 - sinceCrest) * smoothstep(0.0, 0.08, sinceCrest) * smoothstep(0.35, 0.75, b1.g)));
            }
            // Foam wears away as it ages: fresh at the break, and the further the wave has run in since (travel, from the break point to the
            // waterline) and the further behind its crest, the more of it is lace with holes (on the swash too: the sheet thins as it recedes).
            float age = clamp(ageS + (0.5 - clump) * 0.5, 0.0, 1.0);
            // Carried by the current; on still water it still wanders a little (half a unit, over about a minute), so standing foam is never frozen.
            vec2 wander = fract(uTime * vec2(10.5, 7.93)) * 6.2832;   // phases of 57 and 76 s (uTime counts 600 s), kept small so they stay precise
            vec2 drift = direction * speed * uTime * 0.2 + vec2(sin(wander.x), cos(wander.y)) * (0.5 * 0.009);
            vec2 driftDx = dFdx(drift), driftDy = dFdy(drift);
            // Blotches (the multi-octave noise at two scales) with the froth's holes in them; the more foam, the lower the threshold. Up close the
            // foam shows its bubbles (B: rings of 0.13 to 0.4 units), lit on their walls and darker inside.
            vec2 a1 = texture(uFoamMap, p * 0.009 + drift).ag, a2 = texture(uFoamMap, p * 0.027 - drift * 1.3).ag;
            if (rw > 0.01)
            {
                // A river's foam: streaks along the current, a thin trace everywhere in it and a white rush in its rapids.
                vec2 r1, r2;
                riverFoam(p, s.flow, s.speed, dpx, dpy, r1, r2);
                a1 = mix(a1, r1, rw); a2 = mix(a2, r2, rw);
                amount = max(amount, rw * (0.02 + 0.55 * rapid));
            }
            float touched = 0.0;
            #ifdef WATER_CONTACT
            float contact = 0.0;
            if (!swash && outside <= 0.0 && dist < ContactFar)
            {
                contact = contactFoam(thick, floorDepth, toEyeDir.y, a1.g);
                touched = max(contact, ripple.a) * (1.0 - smoothstep(ContactFar - ContactFade, ContactFar, dist));
            }
            #endif
            // Every foam pattern is in the world at fixed scales, so zooming never changes its size or shape. Detail finer than a pixel is filtered (the
            // texture's mips), and the finest octave, the bubbles' walls, is faded out by the pixel's footprint rather than the eye's distance.
            float footprint = max(length(dpx), length(dpy));
            float bubbleShow = 1.0 - smoothstep(0.12, 0.3, footprint);
            float bubbles = 0.0;
            if (bubbleShow > 0.0)
                bubbles = textureGrad(uFoamMap, p * 0.06 - drift * 2.0, dpx * 0.06 - driftDx * 2.0, dpy * 0.06 - driftDy * 2.0).b * bubbleShow;
            // The blotches and the froth at two scales (holes of 0.4 to 9 units): organic clusters, holes and dots as the threshold rises, never a line network.
            float pattern = (a1.g * 0.6 + a2.g * 0.4) * 0.8 + (a1.r * 0.4 + a2.r * 0.6) * 0.3 + bubbles * 0.08;
            // Dark and coloured water foams less (a thinner, more broken lace), and the foam takes its colour: white only on clear water, a dirty
            // grey-brown on black water, rust on red.
            amount *= mix(0.7, 1.0, clean);
            // Foam churned up by what moves in the water (a splash, a wake, a rock's surge) is as thick on dark water as on clear: only its colour changes.
            amount = max(amount, touched);
            float foam = smoothstep(0.95 - amount, 1.25 - amount, pattern);
            if (surfAmount > 0.0)
            {
                // Where the foam lies: clumps and blotches of noise in the wave's frame, the holes opening as it ages (its threshold rises). Its texture: the froth at two
                // finer scales, turned against each other, which only shades it, so even the densest whitewater is not a flat white sheet.
                // Streaks along the waves' way: the blotch noise stretched five to one along it.
                vec2 along = s.prop, across = vec2(-s.prop.y, s.prop.x);
                vec2 sk = vec2(0.006, 0.03);
                float streak = textureGrad(uFoamMap, vec2(dot(q, along), dot(q, across)) * sk + 0.13,
                    vec2(dot(dpx, along), dot(dpx, across)) * sk, vec2(dot(dpy, along), dot(dpy, across)) * sk).g;
                float lace = smoothstep(0.15, 0.9, clump) * 0.35 + b1.g * 0.25 + b2.g * 0.15 + streak * 0.25;
                float wear = 0.2 + age * 0.4;   // even fresh whitewater has holes
                float surf = surfAmount * mix(0.7, 1.0, clean);
                // The rims also fray its edges, which stay crisp (a narrow threshold) rather than soft blobs.
                // The froth at fixed world scales (holes of 0.3 to 3.5 units, two scales turned against each other); far away the mips average it.
                float rims = fine;
                float cover = smoothstep(0.95 - surf + wear, 1.03 - surf + wear + 0.05 * age, lace + (rims - 0.3) * 0.35);
                // Inside, the foam's density follows its texture (bubbles, streaks), so even dense whitewater is never a flat white sheet.
                float texture_ = rims * 0.5 + b2.g * 0.25 + streak * 0.25;
                foam = max(foam, min(cover * mix(0.3, 1.2, clamp(texture_, 0.0, 1.0)), 1.0));   // linear, so the filtered (distant) foam keeps the same mean
            }
            foam *= mix(0.8, 0.92, clean);
            // The shadow receiver takes derivatives, so it runs here and not inside the foam's branch, and only within 8000 units (where it is
            // about uniform per quad: the cascades end well before).

            vec3 foamN = normalize(mix(n, vec3(0.0, 1.0, 0.0), 0.6));   // up, a little tilted by the waves
            float foamShadow = dist < 8000.0 ? kenshiShadow(vWorld, foamN) : 1.0;
            vec3 surface = colour, foamColour = vec3(0.0);   // the water before its foam
            if (foam > 0.004)
            {
                vec3 dirt = mix(vec3(0.8), hue, 0.5) * mix(0.42, 0.6, smoothstep(0.02, 0.25, wLuma));
                vec3 foamAlbedo = mix(dirt, vec3(0.72) * mix(vec3(1.0), hue, 0.12), clean);
                // Lit as the land is (the sun through the sun shadows and the biome's ambient map, and the environment light), so it falls
                // into shadow and dims at night and in dark biomes; in the simple sky, by its old formula times the shadow.
                vec3 foamLit = uAtmoParams.x > 0.5 ? foamLight(foamAlbedo, foamN, vWorld, foamShadow)
                    : foamAlbedo * (max(dot(foamN, l), 0.0) * uSunColour * 0.9 * foamShadow + uSkyZenith * 0.8 + 0.04);
                foamColour = foamLit * (0.84 + 0.2 * bubbles);
                colour = mix(colour, foamColour, foam);
            }

            // The last two units of water fade into what is behind it (the game's alpha term saturate(depth / 2), on the distance through the
            // water): no line where the surface meets the ground (displaced or not), a rock or a leg. The swash's raised surface lies nearly
            // along the beach, so it fades over half a unit where it meets the sand.
            // Over 2 units along the ray (0.5 for the swash) and 0.6 units of depth (0.2), whichever is longer, so a grazing view of ripples standing out of a thin
            // film does not cut them with a line; smoothstep, so no band shows at either end.
            float edge = thick >= 0.0 ? smoothstep(0.0, 1.0, min(thick / max(swash ? 0.5 : 2.0, thicknessError(dist)), thick * max(toEyeDir.y, 0.02) / (swash ? 0.2 : 0.6))) : 1.0;
            #ifdef WATER_CONTACT
            edge = max(edge, max(contact, ripple.a) * foam);   // the foam stays up to the line it clings to, where the water itself has faded out
            #endif
            // Wet sand, behind the swash and under its front: darker and glossy, drying out until the next wave, faded out over the edge of its band.
            // The sand dries in patches after the backwash (a slow noise sets which patch dries first), and the wet line fades out over a soft band.
            float drying = smoothstep(sinceCrest * 1.3 - 0.3, sinceCrest * 1.3, wNoise(p / 9.0 + 3.7) * 0.6 + wNoise(p / 23.0) * 0.4);
            float wet = swash ? drying * 0.6 * smoothstep(0.0, 0.5, max(level, wetLevel) - above) : 0.0;
            vec3 wetGloss = reflected * schlick * gloss + min(spec, 4.0) * uGlintColour * 0.15;
            float a;
            if (refract)
            {
                // Composited here (the scene behind was copied at half size), over the scene itself where the edge fades.
                a = 1.0;
                if (swash)
                {
                    // The sheet thins into the wet sand at its front, and its foam's leading edge is a few units wide. Wet sand darkens and glosses
                    // the scene under it, scene * (1 - 0.35 wet) + gloss * wet: a blend over the scene at full size (the half-size copy blurred the sand).
                    float f = foam * smoothstep(0.0, 0.05 + 0.25 * b2.g, sheet), front = smoothstep(0.0, 0.3 + 0.6 * b1.g, sheet);
                    a = f + (1.0 - f) * (front + (1.0 - front) * 0.35 * wet);
                    colour = (foamColour * f + (1.0 - f) * (surface * front + (1.0 - front) * wetGloss * wet)) / max(a, 1e-4);
                }
                a *= edge;
            }
            else
            {
                // Alpha as the game's water from its depth (the swash: its own thin sheet), then the foam over it.
                float fresnel = 1.0 - pow(1.0 - cosv, 2.0);
                a = clamp(1.0 - (dist - uClarity.z) / (uClarity.z * 0.25), 0.0, 1.0);
                a *= mix(1.0, clamp(depth * pa.w * uClarity.x, 0.0, 1.0), fresnel);
                a *= clamp(depth / 2.0, 0.0, 1.0);
                // The lip (only where the water is deep enough for it, else an opaque stroke) is less see-through.
                a = max(a, lip * 0.6 * near);
                // The swash thins out to its edge instead of ending in a line.
                if (swash) a = max(a, 0.3 * smoothstep(0.0, 0.8, sheet));
                a = max(a, foam);
                if (swash && sheet <= 0.0)
                {
                    // Wet sand: darker and glossy, drying out until the next wave.
                    colour = wetGloss;
                    a = wet * 0.45;
                }
                if (swash) a *= edge;
                a = mix(1.0, a, 1.0 - smoothstep(uClarity.z * 0.9, uClarity.z * 1.25, dist));
            }

            fragColour = vec4(atmoApply(colour, uEye, vWorld), a * atmoWaterWeight(dist, uWaterFar));
        }
        """;

    /// <summary>
    /// The Meitou water's fragment stage: the shared functions and <see cref="MeitouBody"/>.
    /// </summary>
    static readonly string MeitouFragment = "#version 330 core\n" + TerrainShaders.HeightFunctions + SkyRenderer.SkyFunctions + ThicknessFunctions
        + MeitouMotion + MeitouBody;

    /// <summary>
    /// The Meitou water with the <c>contactfoam</c> switch's foam (<see cref="WaterContact"/>); the plain program has none of its code.
    /// </summary>
    static string MeitouContactNative() => NativeFragment(WithContact(MeitouFragment));

    /// <summary>
    /// The Meitou water's vertex stage with the <c>contactfoam</c> switch's waves.
    /// </summary>
    static string MeitouContactVertex() => NativeVertex(WithContact(MeitouVertex));

    /// <summary>
    /// <paramref name="source"/> with <c>WATER_CONTACT</c> defined.
    /// </summary>
    static string WithContact(string source) => source.Insert(source.IndexOf('\n') + 1, "#define WATER_CONTACT\n");

    /// <summary>
    /// The water's own uniforms in the native model: <see cref="ConstantsBlock"/> and the bindless arrays.
    /// </summary>
    static readonly Dictionary<string, string> NativeOwn = new()
    {
        ["uWaterHeight"] = "water.params.x",
        ["uHalfWorld"] = "water.params.y",
        ["uGridStep"] = "water.params.z",
        ["uTime"] = "water.params.w",
        ["uCentre"] = "water.centreExtent.xy",
        ["uExtent"] = "water.centreExtent.z",
        ["uLean"] = "water.centreExtent.w",
        ["uSeaA"] = "water.seaA",
        ["uSeaB"] = "water.seaB",
        ["uSeaColour"] = "water.seaColour.xyz",
        ["uWaterFar"] = "water.seaColour.w",
        ["uSunDir"] = "water.sunDir.xyz",
        ["uSunColour"] = "water.sunColour.xyz",
        ["uGlintDir"] = "water.glintDir.xyz",
        ["uGlintColour"] = "water.glintColour.xyz",
        ["uOcean"] = "water.ocean",
        ["uShoreRect"] = "water.shoreRect",
        ["uShore"] = "water.shore",
        ["uWaveFade"] = "water.waveFade",
        ["uClarity"] = "water.clarity.xyz",
        ["uScreen"] = "water.screenReflect.xy",
        ["uReflect"] = "water.screenReflect.z",
        ["uRefract"] = "water.screenReflect.w",
        ["uReflectionViewProjection"] = "water.reflectionViewProjection",
        ["uSkySun"] = "water.skySun.xyz",
        ["uSkyZenith"] = "water.skyZenith.xyz",
        ["uSkyHorizon"] = "water.skyHorizon.xyz",
        ["uSkySunColour"] = "water.skySunColour.xyz",
        ["uSkyTwilight"] = "water.skySunColour.w",
        ["uCoarseRect"] = "water.coarseRect",
        ["uFineRect"] = "water.fineRect",
        ["uCoarseCells"] = "water.coarseCellsFineBand.xy",
        ["uFineBand"] = "water.coarseCellsFineBand.z",
        ["uHasFine"] = "(water.coarseCellsFineBand.w > 0.5)",
        ["uFineCells"] = "water.fineCells.xy",
        ["uHeightCoarse"] = "textures2D[water.heightCoarse]",
        ["uHeightFine"] = "textures2D[water.heightFine]",
        ["uColourMap"] = "textures2D[water.colourMap]",
        ["uFlowMap"] = "textures2D[water.flowMap]",
        ["uFoamMap"] = "textures2D[water.foamMap]",
        ["uNormalMap"] = "textures2D[water.normalMap]",
        ["uRainMap"] = "textures2D[water.rainMap]",
        ["uParamsA"] = "textures2D[water.paramsA]",
        ["uParamsB"] = "textures2D[water.paramsB]",
        ["uReflection"] = "textures2D[water.reflectionMap]",
        ["uRefraction"] = "textures2D[water.refractionMap]",
        ["uOceanMaps"] = "textures2DArray[water.oceanMap]",
        ["uShoreField"] = "textures2D[water.shoreMap]",
        ["uRiverMap"] = "textures2D[water.riverMap]",
        ["uSceneDepth"] = "textures2D[water.sceneDepthMap]",
        ["uDepthPlanes"] = "water.depthPlanes",
        ["uRippleMap"] = "textures2D[water.rippleMap]",
        ["uRippleWindow"] = "water.rippleWindow",
        ["uRippleShape"] = "water.rippleShape",
    };

    /// <summary>
    /// The uniforms' native forms: the common ones and <see cref="NativeOwn"/>.
    /// </summary>
    static readonly Dictionary<string, string> NativeMap = NativeShaders.Map(NativeOwn);

    /// <summary>
    /// Puts the water constants block after the version line, before the shared text.
    /// </summary>
    static string WithConstants(string source)
    {
        int line = source.IndexOf('\n');
        if (line < 0 || !source.StartsWith("#version", StringComparison.Ordinal))
            throw new InvalidOperationException("WaterRenderer: a native water shader has no #version line");
        return source[..(line + 1)] + ConstantsBlock + source[(line + 1)..];
    }

    /// <summary>
    /// A water vertex shader in the native model (the constants block put in, the uniforms mapped).
    /// </summary>
    static string NativeVertex(string source) => NativeShaders.Port(WithConstants(RenderOrigin.Relative(source)), NativeMap);

    /// <summary>
    /// A water fragment shader in the native model, its albedo for the GI resolve (none).
    /// </summary>
    static string NativeFragment(string source) => Gi.GiResolveShaders.WithAlbedo(NativeShaders.Port(WithConstants(source), NativeMap), none: true);

    #endregion

    #region Construction

    /// <summary>
    /// The native GPU API (phase 8 stage 2: the water makes no GL call; docs/renderer-native.md 8.6).
    /// </summary>
    public IGpuContext Gpu { get; }

    /// <summary>
    /// The game's water quad (four corners, a triangle strip).
    /// </summary>
    readonly DeviceBuffer _quad;

    /// <summary>
    /// The Meitou polar grid's vertices and indices (null without the Meitou waves).
    /// </summary>
    readonly DeviceBuffer? _grid, _gridIndices;

    /// <summary>
    /// The grid's index count.
    /// </summary>
    readonly uint _gridIndexCount;

    /// <summary>
    /// The water's maps: colour, flow, normal, parameters A and B, rain ripples, foam (Meitou), river, in the order
    /// of <see cref="WaterProgram.MapNames"/>.
    /// </summary>
    readonly SampledImage[] _maps;

    /// <summary>
    /// The open sea's two parameter vectors (as the parameter maps').
    /// </summary>
    readonly Vector4 _seaA, _seaB;

    /// <summary>
    /// The open sea's colour.
    /// </summary>
    readonly Vector3 _seaColour;

    /// <summary>
    /// The frame and view constants of both programs.
    /// </summary>
    readonly NativeFrame _frame;

    /// <summary>
    /// The game's water program (the quad).
    /// </summary>
    readonly WaterProgram _faithful;

    /// <summary>
    /// The Meitou water program (the grid; null without the Meitou waves).
    /// </summary>
    readonly WaterProgram? _meitou;

    /// <summary>
    /// The Meitou water with the contact foam (<see cref="WaterContact"/>), made at its first use.
    /// </summary>
    WaterProgram? _meitouContact;

    /// <summary>
    /// The grid's inputs, for <see cref="_meitouContact"/>.
    /// </summary>
    LegacyProgram.Attribute?[] _gridInputs = [];

    /// <summary>
    /// The contact ripples (made while the contact foam is on).
    /// </summary>
    WaterRipples? _ripples;

    /// <summary>
    /// The game hours the ripples last stepped at.
    /// </summary>
    double _rippleHours;

    /// <summary>
    /// The heights the ripples read.
    /// </summary>
    HeightSnapshot? _rippleHeights;

    /// <summary>
    /// The bindless entries of <see cref="_maps"/>.
    /// </summary>
    readonly BindlessTexture[] _mapTextures;

    /// <summary>
    /// The bindless entries of the ocean's maps, the shore field, the reflection, the refraction, the heightmaps and the scene depth copy.
    /// </summary>
    readonly BindlessTexture _oceanMap, _shoreMap, _reflectionMap, _refractionMap, _heightCoarse, _heightFine, _sceneDepthMap, _rippleMap;

    /// <summary>
    /// The one-texel array the ocean's entry reads before its first FFT (every read is gated on the ocean's size).
    /// </summary>
    readonly GpuTexture _standInArrayTexture;

    /// <summary>
    /// The stand-in array's bindless index.
    /// </summary>
    readonly uint _standInArray;

    /// <summary>
    /// The constants set's layout: one uniform buffer, its offset given at bind.
    /// </summary>
    readonly GpuSetLayout _constantsLayout;

    /// <summary>
    /// The breaking waves' phase and the normals' time, stepped by the game time and the wind.
    /// </summary>
    readonly WaveSet _waves = new();

    /// <summary>
    /// The ocean's FFT waves (null without the Meitou waves).
    /// </summary>
    readonly OceanWaves? _ocean;

    /// <summary>
    /// The shore field (null when off).
    /// </summary>
    readonly ShoreField? _shore;

    /// <summary>
    /// The polar grid's spacing over its radius (2π over the segments).
    /// </summary>
    readonly float _gridStep;

    /// <summary>
    /// The constants' alignment in the frame's buffer (the device's, at least 16).
    /// </summary>
    readonly ulong _uniformAlign;

    /// <summary>
    /// The walls the game set (<see cref="Walls"/>).
    /// </summary>
    Func<Vector2, float, IReadOnlyList<(Vector2 A, Vector2 B)>>? _walls;

    /// <summary>
    /// The <c>contactfoam</c> switch's interactive waves, made the first time they are asked for (Vulkan, Meitou water); null otherwise.
    /// </summary>
    public WaterRipples? Ripples =>
        WaterContact.Enabled && Meitou && _meitou is not null && Gpu is GpuContext
            ? _ripples ??= new WaterRipples(Gpu) { Walls = _walls }
            : null;

    /// <summary>
    /// The walls and posts standing in the water round a point (centre, radius: X/Z segments where the buildings' collision cuts the water's
    /// height, read on a worker), which neither the shore waves (<see cref="ShoreTravel"/>) nor the characters' wakes pass. Set by the game, which
    /// knows the buildings.
    /// </summary>
    public Func<Vector2, float, IReadOnlyList<(Vector2 A, Vector2 B)>>? Walls
    {
        get => _walls;
        set
        {
            _walls = value;
            if (_shore is not null) _shore.Walls = value;
            if (_ripples is not null) _ripples.Walls = value;
        }
    }

    /// <summary>
    /// The Meitou program to draw with: with the contact foam (Vulkan only: it needs the scene depth), made the first time it is asked for.
    /// </summary>
    WaterProgram MeitouProgram() =>
        !WaterContact.Enabled || Gpu is not GpuContext
            ? _meitou!
            : _meitouContact ??= MeitouContactProgram();

    /// <summary>
    /// Makes the Meitou program with the contact foam, on the grid's inputs.
    /// </summary>
    WaterProgram MeitouContactProgram()
    {
        var vertex = MeitouContactVertex();
        var fragment = MeitouContactNative();

        return new WaterProgram(Gpu, _frame, vertex, fragment, "water meitou contact", PrimitiveType.Triangles, _gridInputs, _constantsLayout);
    }

    /// <summary>
    /// The Meitou polar grid: segments round the eye (256 low, 1024 default), rings follow from it. A vertex's spacing is its distance from the eye
    /// times 2 pi over this, and the waves it carries are those that spacing can hold: at 256 (30 pixels a cell at 1080p) the crests a few hundred
    /// units out changed shape as the camera moved.
    /// </summary>
    public const int DefaultGridSegments = 1024;

    /// <summary>
    /// Out to this radius the Meitou grid is fine enough for the waves; beyond it the water is flat.
    /// </summary>
    public const float GridDetailRadius = 6000;

    /// <summary>
    /// One native water program: the vertex layout and buffers its one draw needs.
    /// </summary>
    sealed class WaterProgram : IDisposable
    {
        /// <summary>
        /// The water maps' sampler names, in the order of <see cref="_maps"/>.
        /// </summary>
        public static readonly string[] MapNames = ["uColourMap", "uFlowMap", "uNormalMap", "uParamsA", "uParamsB", "uRainMap", "uFoamMap",
            "uRiverMap"];

        /// <summary>
        /// The native program.
        /// </summary>
        public readonly NativeProg Program;

        /// <summary>
        /// The vertex layout of its one stream.
        /// </summary>
        public readonly VertexLayout Layout;

        /// <summary>
        /// The vertex buffers by location.
        /// </summary>
        public readonly GpuBufferBinding[] Vertices;

        /// <summary>
        /// The topology (the quad's strip, the grid's triangles).
        /// </summary>
        public readonly PrimitiveType Topology;

        /// <summary>
        /// The program's GPU handle.
        /// </summary>
        public GpuProgram Gpu => Program.P;

        /// <summary>
        /// Makes the native program with the water constants set, its vertex layout and buffers, and checks its samplers against Metal's budget.
        /// </summary>
        public WaterProgram(IGpuContext gpu, NativeFrame frame, string vertex, string fragment, string name, PrimitiveType topology,
            ReadOnlySpan<LegacyProgram.Attribute?> attributes, GpuSetLayout constants)
        {
            Program = new NativeProg(gpu, frame, vertex, fragment, name, constants);
            Layout = Program.Layout(attributes);
            Vertices = Program.Buffers(attributes, attributes.Length);
            Topology = topology;
            // Every water texture is in the bindless table: the full atmosphere no longer competes for Metal's 16 direct slots.
            if (Program.P.Info.FragmentReflection is { } reflection)
                SamplerBudget.Require(name, reflection.Samplers, NativeShaders.BindlessSet);
        }

        /// <summary>
        /// Frees the program.
        /// </summary>
        public void Dispose() => Program.Dispose();
    }

    /// <summary>
    /// A stable bindless entry for one water texture; its sampler may change with the upscaler's LOD bias.
    /// </summary>
    sealed class BindlessTexture : IDisposable
    {
        /// <summary>
        /// The context whose bindless table holds the entry.
        /// </summary>
        readonly IGpuContext _gpu;

        /// <summary>
        /// The kind of the entry (2D or 2D array).
        /// </summary>
        readonly BindlessKind _kind;

        /// <summary>
        /// The entry (made on first use).
        /// </summary>
        BindlessHandle _handle;

        /// <summary>
        /// The view the entry last held, to update it only on a change.
        /// </summary>
        SampledTexture _registered;

        /// <summary>
        /// The entry is registered.
        /// </summary>
        bool _has;

        /// <summary>
        /// An entry of <paramref name="kind"/>, registered on first use.
        /// </summary>
        public BindlessTexture(IGpuContext gpu, BindlessKind kind) => (this._gpu, this._kind) = (gpu, kind);

        /// <summary>
        /// The entry's index for <paramref name="texture"/>: registered the first time, updated when the view or sampler changed.
        /// </summary>
        public uint Index(in SampledTexture texture)
        {
            if (!_has)
            {
                _handle = new BindlessHandle(_kind, _gpu.Bindless.Register(_kind, texture));
                _has = true;
            }
            else if (!texture.Equals(_registered)) _gpu.Bindless.Update(_handle, texture);
            _registered = texture;
            return _handle.Index;
        }

        /// <summary>
        /// <see cref="Index"/>, or <paramref name="fallback"/> for a null texture.
        /// </summary>
        public uint IndexOr(in SampledTexture texture, uint fallback) => texture.IsNull ? fallback : Index(texture);

        /// <summary>
        /// Frees the entry.
        /// </summary>
        public void Dispose()
        {
            if (_has) _gpu.Bindless.Free(_handle);
        }
    }

    /// <summary>
    /// Makes the ocean and shore field when asked, both programs (the grid with the Meitou waves), the bindless entries and the array stand-in.
    /// </summary>
    WaterRenderer(IGpuContext gpu, SampledImage[] maps, Vector4 seaA, Vector4 seaB, Vector3 seaColour, int gridSegments, int oceanSize,
        bool meitouWaves, bool shoreField)
    {
        // The ocean FFT and the shore field are seam-neutral; the faithful water (the quad) simply does not read them.
        _ocean = meitouWaves ? new OceanWaves(gpu, oceanSize) : null;
        _shore = shoreField ? new ShoreField(gpu) : null;
        Meitou = meitouWaves;
        _gridStep = (float)(2 * Math.PI / gridSegments);
        (this._seaA, this._seaB, this._seaColour) = (seaA, seaB, seaColour);
        Gpu = gpu;
        this._maps = maps;
        _frame = new NativeFrame(gpu);
        _uniformAlign = Math.Max(gpu.UniformBufferAlign, 16);
        _constantsLayout = GpuSetLayout.Fixed([new GpuSlot(0, GpuSlotKind.UniformBuffer, GpuStage.All, 1, OffsetAtBind: true)]);
        _mapTextures = [.. WaterProgram.MapNames.Select(_ => new BindlessTexture(gpu, BindlessKind.Texture2D))];
        _oceanMap = new BindlessTexture(gpu, BindlessKind.Texture2DArray);
        _shoreMap = new BindlessTexture(gpu, BindlessKind.Texture2D);
        _reflectionMap = new BindlessTexture(gpu, BindlessKind.Texture2D);
        _refractionMap = new BindlessTexture(gpu, BindlessKind.Texture2D);
        _sceneDepthMap = new BindlessTexture(gpu, BindlessKind.Texture2D);
        _rippleMap = new BindlessTexture(gpu, BindlessKind.Texture2D);
        _heightCoarse = new BindlessTexture(gpu, BindlessKind.Texture2D);
        _heightFine = new BindlessTexture(gpu, BindlessKind.Texture2D);
        // A valid one-texel array view for the frames before the ocean's first FFT; the shader gates every read on uOcean.w.
        _standInArrayTexture = gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba8, 1, 1, Layers: 1, Kind: TextureKind.Texture2DArray,
            Use: TextureUse.Sampled | TextureUse.TransferDst, Name: "water array stand-in"));
        _standInArray = gpu.Bindless.Register(BindlessKind.Texture2DArray, new SampledTexture(gpu.Samplers.Get(ClampLinear),
            _standInArrayTexture.View()));

        (_quad, _faithful) = FaithfulWater(gpu);
        if (meitouWaves) (_grid, _gridIndices, _meitou, _gridIndexCount) = MeitouWater(gpu, gridSegments);
    }

    /// <summary>
    /// The game's water: its quad's vertex buffer and its program.
    /// </summary>
    (DeviceBuffer Quad, WaterProgram Program) FaithfulWater(IGpuContext gpu)
    {
        float[] corners = [-1, -1, -1, 1, 1, -1, 1, 1]; // triangle strip, counter-clockwise from above
        var quad = DeviceBuffer.Create(gpu, sizeof(float) * (ulong)corners.Length, BufferUse.Vertex, "water quad");
        using (var batch = gpu.Uploads.Begin()) batch.Write(quad, 0, MemoryMarshal.AsBytes(corners.AsSpan()));
        LegacyProgram.Attribute?[] quadAttributes = [QuadAttribute(quad)];
        var program = new WaterProgram(gpu, _frame, NativeVertex(Vertex), NativeFragment(Fragment), "water", PrimitiveType.TriangleStrip,
            quadAttributes, _constantsLayout);
        return (quad, program);
    }

    /// <summary>
    /// The Meitou water: the polar grid of <paramref name="gridSegments"/> (<see cref="PolarGrid"/>) as vertex and index buffers, its program and
    /// its index count.
    /// </summary>
    (DeviceBuffer Grid, DeviceBuffer Indices, WaterProgram Program, uint IndexCount) MeitouWater(IGpuContext gpu, int gridSegments)
    {
        var (positions, indices) = PolarGrid(gridSegments);
        var grid = DeviceBuffer.Create(gpu, sizeof(float) * (ulong)positions.Length, BufferUse.Vertex, "water grid");
        var gridIndices = DeviceBuffer.Create(gpu, sizeof(uint) * (ulong)indices.Length, BufferUse.Index, "water grid indices");
        using (var batch = gpu.Uploads.Begin())
        {
            batch.Write(grid, 0, MemoryMarshal.AsBytes(positions.AsSpan()));
            batch.Write(gridIndices, 0, MemoryMarshal.AsBytes(indices.AsSpan()));
        }
        LegacyProgram.Attribute?[] gridAttributes = [QuadAttribute(grid)];
        var program = new WaterProgram(gpu, _frame, NativeVertex(MeitouVertex), NativeFragment(MeitouFragment), "water meitou",
            PrimitiveType.Triangles, gridAttributes, _constantsLayout);
        _gridInputs = gridAttributes;
        return (grid, gridIndices, program, (uint)indices.Length);
    }

    /// <summary>
    /// Logs the ocean's and the shore's costs and frees the programs, buffers, entries, maps and captures.
    /// </summary>
    public void Dispose()
    {
        if (_ocean is { Dispatched: > 0 } waves)
        {
            Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"water     ocean: {waves.Dispatched} passes ({waves.Size}²), " +
                $"gpu us per pass {(waves.GpuTimed > 0 ? waves.GpuMicroseconds / waves.GpuTimed : 0):F1} ({waves.GpuTimed} timed)"));
        }
        if (_shore is { } field)
        {
            Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"water     shore bake {field.LastBakeMs:F0} ms, {field.UploadedFields} fields uploaded and " +
                $"{field.UniformFields} uniform (one texel)"));
        }
        _faithful.Dispose();
        _meitou?.Dispose();
        _meitouContact?.Dispose();
        _frame.Dispose();
        _quad.Dispose();
        _grid?.Dispose();
        _gridIndices?.Dispose();
        foreach (var t in _mapTextures) t.Dispose();
        _oceanMap.Dispose();
        _shoreMap.Dispose();
        _reflectionMap.Dispose();
        _refractionMap.Dispose();
        _sceneDepthMap.Dispose();
        _rippleMap.Dispose();
        _ripples?.Dispose();
        _heightCoarse.Dispose();
        _heightFine.Dispose();
        Gpu.Bindless.Free(BindlessKind.Texture2DArray, _standInArray);
        _standInArrayTexture.Dispose();
        foreach (var t in _maps) t.Dispose();
        _ocean?.Dispose();
        _shore?.Dispose();
        _refraction?.Dispose();
        _sceneDepthCopy?.Dispose();
    }

    /// <summary>
    /// The quad's only vertex input, location 0: two floats, 8 bytes apart (the GL vertex array's
    /// <c>VertexAttribPointer(0, 2, FLOAT, false, 8, 0)</c>); no element buffer.
    /// </summary>
    internal static LegacyProgram.Attribute QuadAttribute(DeviceBuffer quad) =>
        new(new GpuBufferBinding(quad.Handle, 0), GlConventions.VertexFormat(GlEnum.Float, 2, false, false), 8, false);

    /// <summary>
    /// The Meitou water's mesh: a centre vertex and rings round it whose spacing grows with the radius (as fine across as along, from
    /// 1 unit out to <see cref="GridDetailRadius"/>), then one ring marked to reach the plane's extent. Offsets from the eye, triangle list,
    /// counter-clockwise from above.
    /// </summary>
    internal static (float[] Positions, uint[] Indices) PolarGrid(int segments)
    {
        double q = 1 + 2 * Math.PI / segments;
        var radii = new List<double>();
        for (double r = 1; r < GridDetailRadius; r *= q) radii.Add(r);
        radii.Add(GridDetailRadius);
        radii.Add(1e6);   // the outer ring: scaled to the extent in the vertex shader
        var positions = new float[(1 + radii.Count * segments) * 2];
        for (int ring = 0; ring < radii.Count; ring++)
            for (int s = 0; s < segments; s++)
            {
                double a = 2 * Math.PI * s / segments;
                int i = 1 + ring * segments + s;
                positions[i * 2] = (float)(Math.Cos(a) * radii[ring]);
                positions[i * 2 + 1] = (float)(Math.Sin(a) * radii[ring]);
            }
        var indices = new List<uint>(segments * 3 + (radii.Count - 1) * segments * 6);
        uint V(int ring, int s) => (uint)(1 + ring * segments + (s % segments));
        // Far rings first, the eye's fan last: the water writes no depth, so where a displaced crest stands in front of water further out the nearer
        // triangle must come later to cover it (drawn near first, the water behind a wave showed through it).
        for (int ring = radii.Count - 2; ring >= 0; ring--)
            for (int s = 0; s < segments; s++)
                indices.AddRange([V(ring, s), V(ring, s + 1), V(ring + 1, s + 1), V(ring, s), V(ring + 1, s + 1), V(ring + 1, s)]);
        for (int s = 0; s < segments; s++) indices.AddRange([0u, V(0, s + 1), V(0, s)]);
        return (positions, indices.ToArray());
    }

    #endregion

    #region Sources

    /// <summary>
    /// What <see cref="Create"/> reads and works out on the CPU (about 1.5 s at a cold start): the colour, flow, normal and rain maps, the biome
    /// water parameters baked per blend-map pixel, the open sea, the river flow map baked from the world's heights and the foam. Made by <see
    /// cref="Prepare"/> on any thread.
    /// </summary>
    public sealed class Source
    {
        /// <summary>
        /// The colour, flow, normal and rain ripple images (null when not found).
        /// </summary>
        internal RgbaImage? Colour, Flow, Normal, Rain;

        /// <summary>
        /// The biome water parameters baked per blend-map pixel (A and B).
        /// </summary>
        internal Vector4[] A = [], B = [];

        /// <summary>
        /// The blend map's size, and the river map's side.
        /// </summary>
        internal int BlendWidth, BlendHeight, RiverSize;

        /// <summary>
        /// The open sea's parameters and colour.
        /// </summary>
        internal (Vector4 A, Vector4 B, Vector3 Colour) Sea;

        /// <summary>
        /// The river flow map baked from the world's heights (null: none).
        /// </summary>
        internal byte[]? River;

        /// <summary>
        /// The foam lace (<see cref="WaterFoam"/>).
        /// </summary>
        internal byte[] Foam = [];

        /// <summary>
        /// Warnings for the log (missing files).
        /// </summary>
        internal readonly List<string> Messages = [];
    }

    /// <summary>
    /// The CPU part of <see cref="Create"/>, on any thread (it makes no GPU resource).
    /// </summary>
    public static Source Prepare(GameInstall install, GameDatabase db, AssetLocator assets, ushort[]? coarseHeights = null)
    {
        var s = new Source { Colour = Load(install, WorldWater.ColourMap), Flow = Load(install, WorldWater.FlowMap) };
        var normalPath = assets.Find("water.png");
        s.Normal = normalPath is not null ? TextureLoader.LoadFile(normalPath, allMips: false).Levels[0] : null;
        var rainPath = assets.Find("rain-ripples.png");
        s.Rain = rainPath is not null ? TextureLoader.LoadFile(rainPath, allMips: false).Levels[0] : null;
        if (s.Normal is null) s.Messages.Add("water.png not found: flat water");
        if (s.Rain is null) s.Messages.Add("rain-ripples.png not found: no rain ripples");

        // Biome water parameters blended per blend-map pixel.
        var info = BlendInfoFile.Open(install);
        var water = BiomeWater.ByIndex(db);
        var blend = TextureLoader.LoadImage(File.ReadAllBytes(Path.Combine(install.DataDirectory, TerrainMaps.BlendMap)));
        var fallback = BiomeWater.FromRecord(db.OfType(Data.Fcs.FcsRecordType.Biomes).First());
        Vector4 A(BiomeWater w) => new(w.Scale.X, w.Scale.Y, w.InvStrength, w.InvOpacity);
        Vector4 B(BiomeWater w) => new(w.Gloss, w.Glow, w.Distortion.X, 0);
        (s.BlendWidth, s.BlendHeight) = (blend.Width, blend.Height);
        s.A = BiomeField.Bake(info, blend.Width, blend.Height, blend.Pixels, c => water.TryGetValue(c, out var w) ? A(w) : null, A(fallback));
        s.B = BiomeField.Bake(info, blend.Width, blend.Height, blend.Pixels, c => water.TryGetValue(c, out var w) ? B(w) : null, B(fallback));

        s.Sea = OpenSea(s.A, s.B, blend.Width, blend.Height, s.Colour);
        Console.WriteLine($"sea       open-sea water: scale {s.Sea.A.X * 5000:0.#}, {s.Sea.A.Y * 5000:0.#}, gloss {s.Sea.B.X:0.##}, " +
            $"colour {s.Sea.Colour.X:0.##} {s.Sea.Colour.Y:0.##} {s.Sea.Colour.Z:0.##}");
        // The river map: baked from the whole-world heights at the flow map's size (the game's flow map does not follow the rivers); none without them.
        var riverWatch = System.Diagnostics.Stopwatch.StartNew();
        int riverSize = s.Flow?.Width ?? 0;
        if (coarseHeights is not null && riverSize > 0 && coarseHeights.Length == (riverSize + 1) * (riverSize + 1))
        {
            s.River = RiverFlowBake.Bake(coarseHeights, riverSize, (ushort)Math.Ceiling(WorldWater.Height * ushort.MaxValue / WorldLayout.MaxHeight));
            s.RiverSize = riverSize;
            Console.WriteLine($"rivers    flow map baked at {riverSize}² ({riverWatch.ElapsedMilliseconds} ms)");
        }
        s.Foam = WaterFoam.Bake();
        return s;
    }

    /// <param name="sky">Unused since phase 8 stage 2: the atmosphere comes through the frame globals.</param>
    /// <param name="source">What <see cref="Prepare"/> made (made here when null).</param>
    public static WaterRenderer Create(IGpuContext gpu, GameInstall install, GameDatabase db, AssetLocator assets, SkyRenderer? sky,
        List<string> messages, ushort[]? coarseHeights = null, int gridSegments = DefaultGridSegments, int oceanSize = OceanWaves.DefaultSize,
        Source? source = null, bool meitouWaves = true, bool shoreField = true)
    {
        _ = sky;
        var s = source ?? Prepare(install, db, assets, coarseHeights);
        messages.AddRange(s.Messages);
        // As the GL textures were made (WorldGl.Texture2D): a found map has mips only where it repeats; the 1 × 1 stand-in for a missing one
        // always had its (single-level) chain and a trilinear filter.
        SampledImage Rgba(RgbaImage? img, bool repeat, byte[] flat, string name) =>
            img is null ? SampledImage.Rgba8(gpu, 1, 1, flat, repeat, mipmaps: true, name) : SampledImage.Rgba8(gpu, img, repeat, mipmaps: repeat,
                name);
        SampledImage riverMap = s.River is { } river
            ? SampledImage.Rgba8(gpu, s.RiverSize, s.RiverSize, river, repeat: false, mipmaps: false, "water river map")
            : SampledImage.Rgba8(gpu, 1, 1, [128, 128, 0, 0], repeat: false, mipmaps: false, "water river map");
        return new WaterRenderer(gpu,
            [Rgba(s.Colour, false, [0, 32, 64, 255], "water colour map"), Rgba(s.Flow, false, [128, 128, 0, 255], "water flow map"),
             Rgba(s.Normal, true, [128, 255, 128, 255], "water normal map"),
             SampledImage.Rgba32F(gpu, s.A, s.BlendWidth, s.BlendHeight, "water parameters a"), SampledImage.Rgba32F(gpu, s.B, s.BlendWidth,
                 s.BlendHeight, "water parameters b"),
             Rgba(s.Rain, true, [0, 0, 0, 0], "water rain ripples"),
             SampledImage.Rgba8(gpu, WaterFoam.Size, WaterFoam.Size, s.Foam, repeat: true, mipmaps: true, "water foam"), riverMap],
            s.Sea.A, s.Sea.B, s.Sea.Colour, gridSegments, oceanSize, meitouWaves, shoreField);
    }

    /// <summary>
    /// The water of the open sea: the most common parameters among the pixels of the maps' outer ring (the world's edge is
    /// sea nearly all round), with the watercolourmap colour at one such pixel. The water plane uses it beyond the world.
    /// </summary>
    static (Vector4 A, Vector4 B, Vector3 Colour) OpenSea(Vector4[] a, Vector4[] b, int width, int height, RgbaImage? colour)
    {
        const int ring = 8;
        var counts = new Dictionary<(Vector4, Vector4), (int Count, int Pixel)>();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (x >= ring && x < width - ring && y >= ring && y < height - ring)
                {
                    x = width - ring - 1;
                    continue;
                }
                int i = y * width + x;
                var key = (a[i], b[i]);
                counts[key] = counts.TryGetValue(key, out var c) ? (c.Count + 1, c.Pixel) : (1, i);
            }
        var best = counts.MaxBy(p => p.Value.Count);
        var rgb = new Vector3(0, 32, 64) / 255;
        if (colour is not null)
        {
            int px = best.Value.Pixel % width * colour.Width / width, py = best.Value.Pixel / width * colour.Height / height;
            int o = (py * colour.Width + px) * 4;
            rgb = new Vector3(colour.Pixels[o], colour.Pixels[o + 1], colour.Pixels[o + 2]) / 255;
        }
        return (best.Key.Item1, best.Key.Item2, rgb);
    }

    /// <summary>
    /// An image of the install's data directory, or null when the file is missing.
    /// </summary>
    static RgbaImage? Load(GameInstall install, string relative)
    {
        var path = GameFiles.Resolve(Path.Combine(install.DataDirectory, relative));
        return File.Exists(path) ? TextureLoader.LoadImage(File.ReadAllBytes(path)) : null;
    }

    #endregion

    #region Settings

    /// <summary>
    /// The Enhancements switch <c>water</c>: the game's flat water (Faithful) or Meitou's waves, breakers and foam (the default).
    /// </summary>
    public bool Meitou { get; set; } = true;

    /// <summary>
    /// The Meitou water refracts what lies under it (<see cref="CaptureScene"/> before each of its draws); off: it blends over it as
    /// the game's water does (`--no-water-refraction`, for integrated GPUs).
    /// </summary>
    public bool Refraction { get; set; } = true;

    /// <summary>
    /// Skip zero-contribution work (<c>--ab water-lean</c>).
    /// </summary>
    public bool Lean { get; set; } = true;

    /// <summary>
    /// The distance to the nearest water in view this frame, from the reflection's rect (<see cref="ReflectionPass.NearestWater"/>), set by the frame
    /// loop: infinity with none, NaN when unknown (no reflection pass ran). The ocean's FFT and the refraction copy are
    /// skipped while no water within their reach is in view; unknown gates nothing.
    /// </summary>
    public float NearestWater { get; set; } = float.NaN;

    /// <summary>
    /// The Tab panel's "Water clarity x" (Meitou water; not in the game): the biome's absorption per unit of depth is divided by it, so
    /// above 1 the floor shows through deeper water. 1 is the game's opacity; the default 0.5 is murkier.
    /// </summary>
    public float Clarity { get; set; } = 0.5f;

    /// <summary>
    /// The Tab panel's "Far water clarity" (Meitou water; not in the game): 0 absorbs along the true slant path through the water (it is up to 20
    /// times the depth at grazing angles, so distant shallows read opaque), 1 only along the depth, as if looked at from straight above. Default
    /// 0.25.
    /// </summary>
    public float FarClarity { get; set; } = 0.25f;

    /// <summary>
    /// The Tab panel's "Water see-through distance" (Meitou water): the eye distance where the floor starts to fade out under the water,
    /// gone by 1.5 times it (refracted; by 1.25 times when blended). The game's water turns opaque at 4000 units, which from high above leaves
    /// only a disc of clear water under the camera. Default 10000.
    /// </summary>
    public float ClearDistance { get; set; } = 10000;

    /// <summary>
    /// How overcast the sky is this frame (<c>WorldFrame.Overcast</c>: the cloud density, or the rain's amount where that is more), which the
    /// Meitou water's sun glint fades under (<see cref="GlintCloudFade"/>).
    /// </summary>
    public float Overcast { get; set; }

    /// <summary>
    /// How much of the Meitou water's sun glint a full overcast takes away (docs/render-water.md "Glint under clouds"; not in the game, whose
    /// specular ignores the weather). Default 0.95.
    /// </summary>
    public float GlintCloudShade { get; set; } = 0.95f;

    /// <summary>
    /// The eye distance from which the Faithful water is opaque (the game's alpha: 1 from 4000 units), so needs no depth.
    /// </summary>
    public const float FaithfulOpaqueDistance = 4000;

    /// <summary>
    /// The eye distances over which the Meitou open waves fade into the flat water (the grid coarsens with the distance), and over which
    /// the shore's breakers and foam fade (they read from much further).
    /// </summary>
    public const float WaveFadeStart = 3000, WaveFadeEnd = 5500, ShoreFadeStart = 7000, ShoreFadeEnd = 10000;

    /// <summary>
    /// What is left of the glint at cloud density <paramref name="c"/>: 1 up to c 0.3, easing (smoothstep) to 1 − shade at full cover.
    /// </summary>
    internal static float GlintCloudFade(float shade, float c)
    {
        float t = Math.Clamp((c - 0.3f) / 0.7f, 0, 1);
        return 1 - Math.Clamp(shade, 0, 1) * t * t * (3 - 2 * t);
    }

    #endregion

    #region Motion

    /// <summary>
    /// The eye distance past which the fragment stage reads no ocean texture: the open waves' detail fades from 9000 to 14000 units (<c>open</c> in
    /// <see cref="MeitouFragment"/>; the vertex displacement is gone by <see cref="WaveFadeEnd"/>). The FFT serves nothing beyond it.
    /// </summary>
    public const float OceanFadeEnd = 14000;

    /// <summary>
    /// Steps the Meitou water's motion once per frame: <paramref name="gameHours"/> is the game clock (hours since the load; it stands still while
    /// paused), the wind is the weather's at the camera. Each wave's phase is integrated, so a change of wind changes the waves smoothly.
    /// </summary>
    public void Animate(double gameHours, Vector2 windDirection, float windSpeed)
    {
        _waves.Step(gameHours, windDirection, windSpeed);
        if (Meitou) _ocean?.Update(gameHours, windDirection, windSpeed);
        _rippleHours = gameHours;
    }

    /// <summary>
    /// Keeps the Meitou water's shore field (<see cref="ShoreField"/>) round the eye: once a frame on the render thread, with the
    /// terrain's heights of this frame. Nothing while the water is Faithful.
    /// </summary>
    internal void Track(Vector3 eye, HeightSnapshot heights)
    {
        if (Meitou) _shore?.Update(eye, heights);
        _rippleHeights = heights;
    }

    /// <summary>
    /// Records the Meitou ocean's FFT before the scene host opens (Metal records compute into the same command buffer and
    /// cannot nest it in a render encoder). The reflection pass has already set <see cref="NearestWater"/>.
    /// </summary>
    internal void PrepareFrame()
    {
        if (Meitou && OceanInView) _ocean?.Record();   // the FFT only while water near enough to show its detail is in view

        // The interactive waves only while their switch is on and water within their reach can be in view.
        if (Meitou && WaterContact.Enabled && _ripples is { } r && _rippleHeights is { } heights
            && (float.IsNaN(NearestWater) || NearestWater < ContactFar))
        {
            r.Record(_rippleHours, heights);
        }
    }


    /// <summary>
    /// The open waves (for the viewer's statistics and tests).
    /// </summary>
    public OceanWaves? Ocean => _ocean;

    /// <summary>
    /// Whether the ocean's FFT has anything to feed this frame: water within <see cref="OceanFadeEnd"/> is in view, or that is unknown.
    /// </summary>
    bool OceanInView => float.IsNaN(NearestWater) || NearestWater <= OceanFadeEnd;

    #endregion

    #region Scene capture

    /// <summary>
    /// The refraction: this frame's copy of the scene's colour (Meitou).
    /// </summary>
    GpuTexture? _refraction;

    /// <summary>
    /// The frame the refraction was copied in (-1: never).
    /// </summary>
    long _refractionFrame = -1;

    /// <summary>
    /// This frame's copy of the scene's depth.
    /// </summary>
    GpuTexture? _sceneDepthCopy;

    /// <summary>
    /// The frame the depth was copied in (-1: never).
    /// </summary>
    long _sceneDepthFrame = -1;

    /// <summary>
    /// The near and far planes of the copied depth.
    /// </summary>
    Vector2 _sceneDepthPlanes;

    /// <summary>
    /// Copies the scene drawn so far (the opaque geometry, with no pass open) for the water: at half the size the colour the Meitou water refracts
    /// (the floor and whatever stands in the water, seen through the waves), and the depth, from which both waters measure the distance through them
    /// (<see cref="ThicknessFunctions"/>). One native segment (a full barrier on each side), a blit for the colour and a copy for the depth.
    /// </summary>
    public void CaptureScene(GpuTexture sceneColour, GpuTexture? sceneDepth, float near, float far)
    {
        bool colour = NeedsRefraction(near), depth = sceneDepth is not null && NeedsDepth(near);
        if (!colour && !depth) return;
        var cmd = Gpu.BeginNative("water scene copy");
        if (colour) CopyRefraction(cmd, sceneColour);
        if (depth && Gpu is GpuContext vulkan) CopyDepth(vulkan, cmd, sceneDepth!, near, far);
        Gpu.EndNative(cmd);
    }

    /// <summary>
    /// The scene's colour blitted at half its size into the refraction texture (made again when the size or format changed).
    /// </summary>
    void CopyRefraction(IGpuCommandList cmd, GpuTexture sceneColour)
    {
        int w = Math.Max(sceneColour.Desc.Width / 2, 1), h = Math.Max(sceneColour.Desc.Height / 2, 1);
        if (_refraction is not { } r || r.Desc.Width != w || r.Desc.Height != h || r.Desc.Format != sceneColour.Desc.Format)
        {
            _refraction?.Dispose();
            _refraction = Gpu is GpuContext vk
                ? vk.CreateTexture(new TextureDesc(sceneColour.Desc.Format, w, h, Name: "water refraction"), ((CommandList)cmd).Handle)
                : Gpu.CreateTexture(new TextureDesc(sceneColour.Desc.Format, w, h,
                    Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "water refraction"));
        }
        cmd.Blit(sceneColour, _refraction, TextureMinFilter.Linear);
        _refractionFrame = Gpu.Frame.Number;
    }

    /// <summary>
    /// The scene's depth copied into the water's own (made again when the size or format changed), with the camera's
    /// <paramref name="near"/> and <paramref name="far"/> planes it was drawn with.
    /// </summary>
    void CopyDepth(GpuContext vulkan, IGpuCommandList cmd, GpuTexture sceneDepth, float near, float far)
    {
        var d = sceneDepth.Desc;
        if (_sceneDepthCopy is not { } c || c.Desc.Width != d.Width || c.Desc.Height != d.Height || c.Desc.Format != d.Format)
        {
            _sceneDepthCopy?.Dispose();
            _sceneDepthCopy = vulkan.CreateTexture(new TextureDesc(d.Format, d.Width, d.Height,
                Use: TextureUse.Sampled | TextureUse.TransferDst | TextureUse.DepthTarget, Name: "water scene depth"), ((CommandList)cmd).Handle);
        }
        // Same size and format: a copy (blitting into a depth format is not guaranteed, BLIT_DST), as ShadowPass.CaptureDepth.
        var region = new Silk.NET.Vulkan.ImageCopy
        {
            SrcSubresource = new Silk.NET.Vulkan.ImageSubresourceLayers(Silk.NET.Vulkan.ImageAspectFlags.DepthBit, 0, 0, 1),
            DstSubresource = new Silk.NET.Vulkan.ImageSubresourceLayers(Silk.NET.Vulkan.ImageAspectFlags.DepthBit, 0, 0, 1),
            Extent = new Silk.NET.Vulkan.Extent3D((uint)d.Width, (uint)d.Height, 1),
        };
        vulkan.Device.Vk.CmdCopyImage(((CommandList)cmd).Handle, vulkan.Device.NativeImage(sceneDepth), Silk.NET.Vulkan.ImageLayout.General,
            vulkan.Device.NativeImage(_sceneDepthCopy!), Silk.NET.Vulkan.ImageLayout.General, 1, &region);
        (_sceneDepthFrame, _sceneDepthPlanes) = (Gpu.Frame.Number, new Vector2(near, far));
    }

    /// <summary>
    /// Whether the scene needs <see cref="CaptureScene"/> before its water.
    /// </summary>
    internal bool NeedsCapture(float near) => NeedsRefraction(near) || NeedsDepth(near);

    /// <summary>
    /// The camera's near plane bounds every fragment's eye distance from below. Beyond both cutoffs neither
    /// transmitted floor colour nor wet sand contributes, so the blended path is opaque and needs no copy. The same
    /// bound holds for the nearest water in view: with none nearer, the view shows no transmitting water at all.
    /// </summary>
    internal bool NeedsRefraction(float near) => Meitou && Refraction &&
        (!Lean || SliceCanTransmit(near, ClearDistance)) &&
        (float.IsNaN(NearestWater) || SliceCanTransmit(NearestWater, ClearDistance));

    /// <summary>
    /// Whether the scene's water reads its depth (<see cref="CaptureScene"/>): Vulkan only (Metal keeps the terrain's height), and
    /// only while water in it can be see-through: Meitou with <see cref="Refraction"/> on, as <see cref="NeedsRefraction"/>; Faithful nearer than
    /// <see cref="FaithfulOpaqueDistance"/>. With <see cref="NearestWater"/> unknown (reflections off) it is copied whether or not water is in view.
    /// </summary>
    internal bool NeedsDepth(float near)
    {
        if (Gpu is not GpuContext) return false;
        if (!Meitou) return near < FaithfulOpaqueDistance && (float.IsNaN(NearestWater) || NearestWater < FaithfulOpaqueDistance);
        // Without refraction (`--no-water-refraction`) the Meitou water keeps the slant path: no pass restart or copy, unless the contact foam
        // reads the depth.
        return NeedsRefraction(near)
            || WaterContact.Enabled && near < ContactFar && (float.IsNaN(NearestWater) || NearestWater < ContactFar);
    }

    /// <summary>
    /// How far from the eye the contact foam reaches (the shader's ContactFar).
    /// </summary>
    const float ContactFar = 1200;

    /// <summary>
    /// Whether water at <paramref name="near"/> can still be see-through: nearer than 1.5 times the clear distance (at least 100) or the shore
    /// fade's end.
    /// </summary>
    internal static bool SliceCanTransmit(float near, float clearDistance) =>
        near < Math.Max(Math.Max(clearDistance, 100f) * 1.5f, ShoreFadeEnd);

    #endregion

    #region Drawing

    /// <summary>
    /// The constants sets by frame buffer.
    /// </summary>
    readonly Dictionary<ulong, GpuSet> _constantSets = [];

    /// <summary>
    /// The buffer of the last constants set.
    /// </summary>
    ulong _constantSetKey;

    /// <summary>
    /// The last constants set.
    /// </summary>
    GpuSet _constantSet;

    /// <summary>
    /// Linear, edge-clamped, no mipmaps (the array stand-in).
    /// </summary>
    static readonly SamplerDesc ClampLinear = new(TextureMinFilter.Linear, TextureMagFilter.Linear, false,
        TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false,
        DepthFunction.Always, false, false, 1, 0);

    /// <summary>
    /// Nearest, edge-clamped (the scene depth copy).
    /// </summary>
    static readonly SamplerDesc ClampNearest = ClampLinear with { Min = TextureMinFilter.Nearest, Mag = TextureMagFilter.Nearest };

    /// <summary>
    /// Straight alpha blending (the water over the scene, when the target has colour).
    /// </summary>
    static readonly BlendState AlphaBlend = new(true, BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

    /// <summary>
    /// The water surface: one draw, blended, into the pass VkGl has open. Prepare sets the uniforms and the textures the shader reads (the height
    /// textures, the atmosphere and the heights' uniforms come through the frame globals); Record is one native segment with the pass's state
    /// and the water's own: no culling, depth test without write, alpha blending. No GL state is changed (phase 8 stage 2): the GL code set
    /// these and put back the depth write and blending after the draw; what it left differently (the cull face off, the blend factors) is read by
    /// no later draw (docs/renderer-native.md 8.6). Faithful draws the game's quad; Meitou the polar grid with <see cref="Animate"/>'s motion.
    /// </summary>
    /// <param name="time">Animation time; the game's unit for it is Unknown (the viewer uses real hours). Meitou uses the game clock instead.</param>
    public void Draw(Matrix4x4 viewProjection, Vector3 eye, WorldLighting light, SkyColours colours, float time, float extent,
        ReflectionPass? reflection = null, Matrix4x4? relative = null)
    {
        var targets = Gpu.CurrentTargets();
        bool meitouWater = Meitou && _meitou is not null;
        var p = meitouWater ? MeitouProgram() : _faithful;
        var c = PrepareConstants(meitouWater, eye, light, colours, meitouWater ? _waves.NormalTime : time, extent, reflection, targets);
        var slice = Gpu.Frame.WriteConstants(MemoryMarshal.AsBytes(new ReadOnlySpan<WaterConstants>(in c)), _uniformAlign);
        var set = ConstantSet(slice.Handle);
        var cmd = Gpu.BeginGuest(meitouWater ? "water meitou" : "water");
        var state = WaterState(targets);
        SetDynamicState(cmd, targets, state);
        var view = View(viewProjection, eye, time, light, relative);
        _frame.Bind(cmd, p.Gpu, in view);
        uint offset = (uint)slice.Offset;
        cmd.BindSets(p.Gpu, ConstantsSet, new ReadOnlySpan<GpuSet>(in set), new ReadOnlySpan<uint>(in offset));
        cmd.BindPipeline(Gpu.Pipelines.Get(state.Pipeline(p.Gpu, p.Layout, p.Topology, targets.Formats, meitouWater ? "water meitou" : "water")));
        cmd.BindVertexBuffers(0, p.Vertices);
        if (meitouWater)
        {
            cmd.BindIndexBuffer(new GpuBufferBinding(_gridIndices!.Handle, 0, _gridIndices.Size), IndexKind.U32);
            cmd.DrawIndexed(_gridIndexCount);
        }
        else cmd.Draw(4);
        Gpu.EndGuest(cmd);
    }

    /// <summary>
    /// What the GL code's Enable(DepthTest), DepthMask(false), Disable(CullFace), Enable(Blend) and BlendFunc made of the pass's state (the
    /// depth test and blending only with the attachment they need, as VkGl's CurrentState).
    /// </summary>
    DrawState WaterState(PassTargets targets)
    {
        bool hasDepth = targets.Formats.Depth != InternalFormat.None, hasColour = targets.Formats.Colour != InternalFormat.None;
        return Gpu.CurrentState() with
        {
            Cull = CullMode.None,
            DepthTest = hasDepth,
            DepthWrite = false,
            Blend = hasColour ? AlphaBlend : BlendState.Off,
        };
    }

    /// <summary>
    /// The pass's viewport and scissor and the draw's raster, depth and bias state into <paramref name="cmd"/>.
    /// </summary>
    static void SetDynamicState(IGpuCommandList cmd, PassTargets targets, DrawState state)
    {
        cmd.SetViewport(targets.Viewport);
        cmd.SetScissor(targets.Scissor);
        cmd.SetRaster(state.Cull, state.Front);
        cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
        cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
    }

    /// <summary>
    /// The camera (eye-relative: <paramref name="relative"/>, else from <paramref name="viewProjection"/>), the animation <paramref name="time"/> and
    /// the light's direction and fog as <see cref="ViewConstants"/>.
    /// </summary>
    static ViewConstants View(Matrix4x4 viewProjection, Vector3 eye, float time, WorldLighting light, Matrix4x4? relative)
    {
        return new ViewConstants
        {
            ViewProjection = RenderOrigin.ViewProjection(viewProjection, eye, relative),
            Eye = eye,
            Time = time,
            LightDir = light.SunDirection,
            FogColour = light.FogColour,
            FogDistance = light.FogDistance,
        };
    }

    /// <summary>
    /// The water's constants for a draw: its own values, the light and the sky colours, then the height uniforms (<see cref="SetHeights"/>) and
    /// the maps' bindless indices (<see cref="SetMaps"/>).
    /// </summary>
    WaterConstants PrepareConstants(bool meitouWater, Vector3 eye, WorldLighting light, SkyColours colours, float time, float extent,
        ReflectionPass? reflection, PassTargets targets)
    {
        var (glintDirection, glintColour) = light.Glint;
        if (meitouWater) glintColour *= GlintCloudFade(GlintCloudShade, Overcast);
        bool reflect = reflection is { Valid: true };
        bool refract = meitouWater && Refraction && _refraction is not null && _refractionFrame == Gpu.Frame.Number;
        var c = new WaterConstants
        {
            Params = new Vector4(WorldWater.Height, (float)WorldLayout.HalfWorldSize, _gridStep, time),
            CentreExtent = new Vector4(eye.X, eye.Z, extent, Lean ? 1f : 0f),
            SeaA = _seaA,
            SeaB = _seaB,
            SeaColour = new Vector4(_seaColour, extent / 1.5f),
            SunDir = new Vector4(light.SunDirection, 0f),
            SunColour = new Vector4(light.SunColour, 0f),
            GlintDir = new Vector4(glintDirection, 0f),
            GlintColour = new Vector4(glintColour, 0f),
            Ocean = new Vector4(_ocean?.Lengths.X ?? 0, _ocean?.Lengths.Y ?? 0, _ocean?.Lengths.Z ?? 0, _ocean is { Ready: true } o ? o.Size : 0),
            ShoreRect = _shore is { Ready: true } s ? s.Rect : Vector4.Zero,
            Shore = _waves.Shore,
            WaveFade = new Vector4(WaveFadeStart, WaveFadeEnd, ShoreFadeStart, ShoreFadeEnd),
            Clarity = new Vector4(1f / Math.Max(Clarity, 0.01f), Math.Clamp(FarClarity, 0f, 1f), Math.Max(ClearDistance, 100f), 0f),
            ScreenReflect = new Vector4(1f / Math.Max(targets.Width, 1), 1f / Math.Max(targets.Height, 1), reflect ? 1f : 0f, refract ? 1f : 0f),
            ReflectionViewProjection = reflect ? reflection!.ViewProjection : Matrix4x4.Identity,
            SkySun = new Vector4(colours.Sun, 0f),
            SkyZenith = new Vector4(colours.Zenith, 0f),
            SkyHorizon = new Vector4(colours.Horizon, 0f),
            SkySunColour = new Vector4(colours.SunColour, colours.Twilight),
        };
        var g = Gpu.Globals;
        SetHeights(ref c, g);
        SetMaps(ref c, g, reflection, reflect, refract);
        return c;
    }

    /// <summary>
    /// The heights' uniforms from the frame globals (<see cref="TerrainShaders.HeightFunctions"/>), the whole world when the terrain published
    /// none.
    /// </summary>
    static void SetHeights(ref WaterConstants c, FrameGlobals g)
    {
        float half = (float)WorldLayout.HalfWorldSize;
        var world = new Vector4(-half, -half, half, half);
        c.CoarseRect = ReadGlobal(g, "uCoarseRect", world);
        c.FineRect = ReadGlobal(g, "uFineRect", world);
        var coarseCells = ReadGlobal(g, "uCoarseCells", Vector2.One);
        var fineCells = ReadGlobal(g, "uFineCells", Vector2.One);
        float fineBand = ReadGlobal(g, "uFineBand", 1f);
        int hasFine = ReadGlobal(g, "uHasFine", 0);
        c.CoarseCellsFineBand = new Vector4(coarseCells, fineBand, hasFine != 0 ? 1f : 0f);
        c.FineCells = new Vector4(fineCells, 0f, 0f);
    }

    /// <summary>
    /// The bindless indices of the water's maps, the ocean's and the shore's, the heights, the reflection (<paramref name="reflect"/>), the
    /// refraction (<paramref name="refract"/>) and the scene's depth; a stand-in for each one absent this frame.
    /// </summary>
    void SetMaps(ref WaterConstants c, FrameGlobals g, ReflectionPass? reflection, bool reflect, bool refract)
    {
        c.ColourMap = _mapTextures[0].Index(_maps[0].Sampled());
        c.FlowMap = _mapTextures[1].Index(_maps[1].Sampled());
        c.NormalMap = _mapTextures[2].Index(_maps[2].Sampled());
        c.ParamsA = _mapTextures[3].Index(_maps[3].Sampled());
        c.ParamsB = _mapTextures[4].Index(_maps[4].Sampled());
        c.RainMap = _mapTextures[5].Index(_maps[5].Sampled());
        c.FoamMap = _mapTextures[6].Index(_maps[6].Sampled());
        c.RiverMap = _mapTextures[7].Index(_maps[7].Sampled());
        c.OceanMap = _ocean is { Ready: true } readyOcean ? _oceanMap.Index(readyOcean.OceanMaps) : _standInArray;
        c.ShoreMap = _shore is { Ready: true } readyShore ? _shoreMap.Index(readyShore.Sampled()) : Gpu.StandIn2D;
        c.HeightCoarse = _heightCoarse.IndexOr(g.Texture("uHeightCoarse")?.Invoke() ?? default, Gpu.StandIn2D);
        c.HeightFine = _heightFine.IndexOr(g.Texture("uHeightFine")?.Invoke() ?? default, Gpu.StandIn2D);
        c.ReflectionMap = reflect ? _reflectionMap.Index(reflection!.Sampled) : Gpu.StandIn2D;
        c.RefractionMap = refract
            ? _refractionMap.Index(new SampledTexture(Gpu.Samplers.Get(ClampLinear), _refraction!.View()))
            : Gpu.StandIn2D;
        bool depth = _sceneDepthCopy is not null && _sceneDepthFrame == Gpu.Frame.Number;
        c.DepthPlanes = new Vector4(_sceneDepthPlanes, depth ? 1f : 0f, 0f);
        c.SceneDepthMap = depth ? _sceneDepthMap.Index(new SampledTexture(Gpu.Samplers.Get(ClampNearest), _sceneDepthCopy!.View())) : Gpu.StandIn2D;
        SetRippleMap(ref c);
    }

    /// <summary>
    /// The contact ripples' field, window and shape (the stand-in and 0 in w while there is no field to draw).
    /// </summary>
    void SetRippleMap(ref WaterConstants c)
    {
        bool ripple = WaterContact.Enabled && _ripples is { Ready: true };
        c.RippleMap = ripple ? _rippleMap.Index(_ripples!.Surface) : Gpu.StandIn2D;
        var (window, rippleHalf) = _ripples?.Window ?? default;
        c.RippleWindow = window;
        c.RippleShape = new Vector4(rippleHalf, 1f / (WaterRipples.Side * WaterRipples.Texel), WaterRipples.Texel, ripple ? 1f : 0f);
    }

    /// <summary>
    /// A published uniform's value from the frame globals (<paramref name="fallback"/> when not published).
    /// </summary>
    static T ReadGlobal<T>(FrameGlobals globals, string name, T fallback) where T : unmanaged
    {
        var value = fallback;
        var bytes = MemoryMarshal.AsBytes(new Span<T>(ref value));
        return globals.UniformValue(name)?.TryRead(bytes) == true ? value : fallback;
    }

    /// <summary>
    /// The constants set for this frame's buffer, made once per buffer.
    /// </summary>
    GpuSet ConstantSet(GpuBuffer buffer)
    {
        ulong key = (ulong)buffer.Index;
        if (!_constantSet.IsNull && key == _constantSetKey) return _constantSet;
        if (!_constantSets.TryGetValue(key, out var made))
            _constantSets[key] = made = Gpu.AllocateConstantSet(_constantsLayout, buffer, (ulong)sizeof(WaterConstants));
        (_constantSetKey, _constantSet) = (key, made);
        return made;
    }

    #endregion
}