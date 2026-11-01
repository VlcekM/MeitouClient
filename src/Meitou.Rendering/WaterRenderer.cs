using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Textures;
using Meitou.Data.World;

using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Shaders;

namespace Meitou.Rendering;

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
    /// <summary>Set 2: the water's own values and the bindless indices of its maps (the native frame is set 0 and the table set 1).</summary>
    const int ConstantsSet = 2;

    /// <summary>The block <see cref="WaterConstants"/> mirrors. It is already placed, so <see cref="NativeShaders.Port"/> leaves it alone.</summary>
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
            vec4 depthPlanes;     // this slice's near and far planes, 1 with a scene depth copy (CaptureScene), 1 with 0..1 clip depth
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
            uint spare1;
        } water;

        """;

    /// <summary>The C# side of <see cref="ConstantsBlock"/> (std140).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 512)]
    struct WaterConstants
    {
        [FieldOffset(0)] public Vector4 Params;
        [FieldOffset(16)] public Vector4 CentreExtent;
        [FieldOffset(32)] public Vector4 SeaA;
        [FieldOffset(48)] public Vector4 SeaB;
        [FieldOffset(64)] public Vector4 SeaColour;
        [FieldOffset(80)] public Vector4 SunDir;
        [FieldOffset(96)] public Vector4 SunColour;
        [FieldOffset(112)] public Vector4 GlintDir;
        [FieldOffset(128)] public Vector4 GlintColour;
        [FieldOffset(144)] public Vector4 Ocean;
        [FieldOffset(160)] public Vector4 ShoreRect;
        [FieldOffset(176)] public Vector4 Shore;
        [FieldOffset(192)] public Vector4 WaveFade;
        [FieldOffset(208)] public Vector4 Clarity;
        [FieldOffset(224)] public Vector4 ScreenReflect;
        [FieldOffset(240)] public Matrix4x4 ReflectionViewProjection;
        [FieldOffset(304)] public Vector4 SkySun;
        [FieldOffset(320)] public Vector4 SkyZenith;
        [FieldOffset(336)] public Vector4 SkyHorizon;
        [FieldOffset(352)] public Vector4 SkySunColour;
        [FieldOffset(368)] public Vector4 CoarseRect;
        [FieldOffset(384)] public Vector4 FineRect;
        [FieldOffset(400)] public Vector4 CoarseCellsFineBand;
        [FieldOffset(416)] public Vector4 FineCells;
        [FieldOffset(432)] public Vector4 DepthPlanes;
        [FieldOffset(448)] public uint ColourMap;
        [FieldOffset(452)] public uint FlowMap;
        [FieldOffset(456)] public uint FoamMap;
        [FieldOffset(460)] public uint NormalMap;
        [FieldOffset(464)] public uint RainMap;
        [FieldOffset(468)] public uint ParamsA;
        [FieldOffset(472)] public uint ParamsB;
        [FieldOffset(476)] public uint ReflectionMap;
        [FieldOffset(480)] public uint RefractionMap;
        [FieldOffset(484)] public uint OceanMap;
        [FieldOffset(488)] public uint ShoreMap;
        [FieldOffset(492)] public uint RiverMap;
        [FieldOffset(496)] public uint HeightCoarse;
        [FieldOffset(500)] public uint HeightFine;
        [FieldOffset(504)] public uint SceneDepthMap;
        [FieldOffset(508)] public uint Spare1;
    }

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
    /// takes its own off (forward/water.hlsl); here the slice's depth, copied before the water (<see cref="CaptureScene"/>), whatever stands in
    /// the water included. -1 without a copy (Metal, or a slice whose water is opaque).
    /// </summary>
    const string ThicknessFunctions = """

        uniform sampler2D uSceneDepth;   // the slice's depth before the water (CaptureScene), read where uDepthPlanes.z is 1
        uniform vec4 uDepthPlanes;       // the slice's near and far planes, 1 with a depth copy, the depth's convention (1: the clip depth itself; 0: half of it plus a half)

        // The view depth of a stored depth.
        float waterViewZ(float d)
        {
            float zd = uDepthPlanes.w > 0.5 ? d : 2.0 * d - 1.0;
            return uDepthPlanes.x * uDepthPlanes.y / (uDepthPlanes.y - zd * (uDepthPlanes.y - uDepthPlanes.x));
        }

        // From this fragment (dist from the eye) to the surface behind it along the view ray, or -1 without a depth copy.
        float sceneThickness(float dist)
        {
            if (uDepthPlanes.z < 0.5) return -1.0;
            float behind = waterViewZ(texelFetch(uSceneDepth, ivec2(gl_FragCoord.xy), 0).r);
            return max(dist * (behind / waterViewZ(gl_FragCoord.z) - 1.0), 0.0);
        }

        // The error of sceneThickness from the two stored depths' precision (a float's step near 1 is about 6e-8, times z^2 / near in view depth,
        // doubled with the 2d - 1 convention, for each of the two): the edge fades over at least this, so distant shores do not shimmer.
        float thicknessError(float dist)
        {
            return (uDepthPlanes.w > 0.5 ? 1.2e-7 : 2.4e-7) * dist * dist / max(uDepthPlanes.x, 1e-3);
        }
        """;

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
        struct Shore { float depth; float dist; float g; float wave; vec2 dir; float open; float size; float breakAt; float river; vec2 flow; float speed; float sizeWet; float brk; };

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
        }

        vec2 shoreField(vec2 uv) { return textureLod(uShoreField, uv, 0.0).rg; }

        // depth: still water over the ground (negative on land); dist: the distance to the waterline (negative inland); g: the breaker phase
        // in [0, 1), the crest at 0.5 with its steep front towards the shore; dir: the unit direction towards the shore; open: how exposed the
        // shore is (0 a pond or a sheltered bay, 1 open water): only exposed shores get surf. size: this breaker's height factor (they come in
        // sets, 0.6 to 1.4, varying smoothly from one to the next); breakAt: the distance from the waterline where it breaks.
        Shore shoreAt(vec2 p)
        {
            Shore r;
            float trust = 0.0;   // how well the field gives the direction to the shore (it fades to nil on the axis between two shores, where the nearest shore jumps)
            r.depth = uWaterHeight - terrainHeight(p);
            r.dist = 1.0e6; r.dir = vec2(0.0); r.open = 0.0; r.flow = vec2(0.0); r.speed = 0.0;
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
                vec2 f = shoreField(uv);
                vec2 e = vec2(12.0) / extent;
                vec2 grad = vec2(shoreField(uv + vec2(e.x, 0.0)).r - shoreField(uv - vec2(e.x, 0.0)).r,
                                 shoreField(uv + vec2(0.0, e.y)).r - shoreField(uv - vec2(0.0, e.y)).r);
                r.dist = f.r;
                trust = smoothstep(10.0, 20.0, length(grad));
                r.dir = length(grad) > 1e-6 ? -normalize(grad) : vec2(0.0);
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
            // The breakers: phase from the distance to the shore and the wave count; in the surf zone the pieces of the shore (above) offset it
            // (so the crests slant against the shore and the break peels along them), and give each wave its size and whether it breaks.
            float x = r.dist / uShore.z + uShore.x;
            r.size = 1.0; r.sizeWet = 1.0; r.brk = 1.0;
            float base = 70.0 + 35.0 * sin(p.x * 0.0031 + p.y * 0.0023);
            if (r.open > 0.0 && r.dist > -150.0 && r.dist < 700.0)
            {
                vec2 q = p + r.dir * r.dist + 260.0 * vec2(sin(uShore.x * (23.0 * 6.2831853 / 4096.0) + 1.0), cos(uShore.x * (17.0 * 6.2831853 / 4096.0)));
                x += trust * (0.6 * (sin(q.x * 0.0021 + sin(q.y * 0.0013) * 2.0) + sin(q.y * 0.0017 + q.x * 0.0009))
                   + 1.4 * (wNoise(q / 480.0 + 11.3) - 0.5) + 0.7 * (wNoise(q / 210.0 + 5.1) - 0.5));
                float k = floor(x);
                float s0, b0, s1, b1;
                waveAt(k, q, s0, b0);
                waveAt(k - 1.0, q, s1, b1);
                // Continuous across the troughs between waves (the breakers' profile is nil there): the new wave's values take over at its front.
                float t = smoothstep(0.0, 0.25, fract(x));
                r.size = mix(1.0, mix(s1, s0, t), trust);
                r.brk = mix(1.0, mix(b1, b0, t), trust);
                r.sizeWet = mix(1.0, fract(x) >= 0.5 ? s0 : s1, trust);   // the wet sand remembers the last crest that passed
            }
            r.g = fract(x);
            r.wave = mod(floor(x), 4096.0);
            // A wave that does not break here runs on to the shore (its break point is at the waterline).
            r.breakAt = mix(-40.0, base * (0.7 + 0.5 * r.size), r.brk);
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
            return uShore.w * s.open * s.size * build * collapse * smoothstep(-5.0, 20.0, s.dist) * smoothstep(250.0, 120.0, s.depth);
        }

        // The steep profile the fragment shades with.
        float breakerHeight(Shore s) { return breakerScale(s) * breakerProfile(s.g, smoothstep(s.breakAt + 160.0, s.breakAt, s.dist)); }
        """;

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
                float hump = breakerScale(s) * pow(0.5 - 0.5 * cos(6.2831853 * s.g), 1.5) * smoothstep(0.45, 0.2, spacing / uShore.z);
                float lift = uShore.y * s.open * max(s.size, s.sizeWet) * (1.0 - smoothstep(10.0, 60.0, s.dist)) * smoothstep(-140.0, -60.0, s.dist);
                float scale = oceanScale(s) * fade;
                vec3 d = vec3(0.0);
                if (uLean < 0.5 || scale > 0.0) d = oceanDisplace(p, spacing).xyz * scale;
                w += vec3(d.x, d.y + max(hump, lift) * shoreFade, d.z);
            }
            vBase = p;
            vWorld = w;
            gl_Position = uViewProjection * vec4(w, 1.0);
        }
        """;

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
        uniform sampler2D uFoamMap;     // WaterFoam: a lace of bubble rims in R
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

        // The foam's two blotch scales (R: lace, G: noise) stretched along the flow into streaks and carried with it, cross-faded the same way.
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
                a1 += textureGrad(uFoamMap, q * s1, frame * dpx * s1, frame * dpy * s1).rg * w;
                a2 += textureGrad(uFoamMap, q * s2 + 0.37, frame * dpx * s2, frame * dpy * s2).rg * w;
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

        // The surf's cells: a cellular pattern (the 3 by 3 neighbouring cells) whose points wander slowly with the clock (cycles of 10 and
        // 20 s). x: the distance to the cell wall (F2 - F1, 0 on a wall, up to about 0.5 in a cell's middle); y: the nearest cell's lifetime
        // (0.25 to 1: the foam age at which it bursts).
        vec2 popCells(vec2 x)
        {
            vec2 i = floor(x), f = fract(x);
            vec2 a = 6.2831853 * vec2(fract(uTime * 60.0), fract(uTime * 30.0));
            float d1 = 8.0, d2 = 8.0, life = 0.0;
            for (int j = -1; j <= 1; j++)
                for (int k = -1; k <= 1; k++)
                {
                    vec2 c = vec2(float(k), float(j)), h = i + c;
                    vec2 hv = vec2(wHash(h), wHash(h + 17.3));
                    vec2 pt = c + 0.15 + 0.7 * hv + 0.15 * sin(a + 6.2831853 * hv.yx);
                    float d = distance(pt, f);
                    if (d < d1) { d2 = d1; d1 = d; life = wHash(h + 71.9); }
                    else d2 = min(d2, d);
                }
            return vec2(d2 - d1, mix(0.25, 1.0, life));
        }

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
            s.depth = 1.0e4; s.dist = 1.0e6; s.g = 0.0; s.wave = 0.0; s.dir = vec2(0.0); s.open = 0.0; s.size = 1.0; s.breakAt = 0.0; s.river = 0.0; s.flow = vec2(1.0, 0.0); s.speed = 0.0; s.sizeWet = 1.0; s.brk = 1.0;
            if (fade > 0.0) s = shoreAt(p);
            else if (outside <= 0.0) s.depth = uWaterHeight - terrainHeight(p);

            // The swash: a breaker that reached the shore runs up the beach (to where the ground is `level` above the still water) and back,
            // leaving the sand wet behind it; above both the water is not there.
            float runup = uShore.y * s.open * s.size * fade * smoothstep(-120.0, -40.0, s.dist);   // at most about 10 m up the beach: low flats further in stay dry
            float above = -s.depth;
            float sinceCrest = fract(s.g - 0.5);
            float level = runup * breakerProfile(s.g);
            float wetLevel = uShore.y * s.open * s.sizeWet * fade * smoothstep(-120.0, -40.0, s.dist) * (1.0 - 0.8 * sinceCrest);
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
            ahead.g = fract(s.g - 0.01); ahead.dist -= 0.01 * uShore.z;
            behind.g = fract(s.g + 0.01); behind.dist += 0.01 * uShore.z;
            float breaker = breakerHeight(s) * fade;
            // The breaker's shading fades with the distance: a few pixels across its steep front only read as a dark streak.
            float near = 1.0 - smoothstep(700.0, 2500.0, dist);
            slope += s.dir * ((breakerHeight(behind) - breakerHeight(ahead)) * fade * near * 0.7 / (0.02 * uShore.z));
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
            float sparkle = smoothstep(0.55, 0.95, textureLod(uFoamMap, p * 0.0023 + n.xz * 0.35 + vec2(uTime * 1.7, uTime * 1.1), 0.0).r);
            spec += pow(nh, power * 0.12) * (power * 0.12 + 8.0) / 25.0 * gloss * sparkle * far * 2.5;
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
                float hump = breakerScale(s) * pow(0.5 - 0.5 * cos(6.2831853 * s.g), 1.5) * smoothstep(0.45, 0.2, spacing / uShore.z);
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
                under = texture(uRefraction, gl_FragCoord.xy * uScreen + bend).rgb;
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
            colour += min(spec, 4.0) * uGlintColour * 0.25 * atmoCloudShadow(vWorld) + pb.y * waterColour;

            // A breaker's face: light comes through its thin lip, so it is brighter than the water (in its colour), and less see-through.
            float lip = smoothstep(0.22, 0.45, s.g) * (1.0 - smoothstep(0.46, 0.51, s.g)) * clamp(breaker / max(uShore.w, 0.1), 0.0, 1.0) * smoothstep(4.0, 24.0, s.depth);
            vec3 lipColour = (waterColour * 1.8 + vec3(0.01, 0.04, 0.035) * clean) * (uSunColour * 0.7 * overcast + uSkyZenith * 0.6);
            colour = mix(colour, max(colour, lipColour), lip * 0.6 * near);   // it only brightens: dark water has no light to show through its lip
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
            // Sheltered ponds have no surf; keep river, open-water and waterline foam independent below.
            if (uLean < 0.5 || surfZone > 0.0 || swash)
            {
                float travel = clamp((s.breakAt - (s.dist - uShore.z * (s.g - 0.5))) / max(s.breakAt + 10.0, 40.0), 0.0, 1.0);
                ageS = clamp(0.6 * travel + 0.5 * smoothstep(0.5, 0.95, s.g), 0.0, 1.0);
                float squeeze = 1.0 - 1.0 / (1.0 + 1.1 * ageS * ageS);
                vec2 q0 = p + s.dir * (s.dist - uShore.z * s.g) + vec2(wHash(vec2(s.wave, 21.7)), wHash(vec2(s.wave, 5.3))) * 997.0;
                q = q0 + s.dir * (uShore.z * (s.g - 0.5) * squeeze);
                vec2 gx = dpx - s.dir * (dot(s.dir, dpx) * squeeze), gy = dpy - s.dir * (dot(s.dir, dpy) * squeeze);
                clump = textureGrad(uFoamMap, q0 * 0.0037 + vec2(0.21, 0.63), dpx * 0.0037, dpy * 0.0037).g;
                if (fade > 0.0) { b1 = textureGrad(uFoamMap, q * 0.009, gx * 0.009, gy * 0.009).rg; b2 = textureGrad(uFoamMap, q * 0.027 + 0.37, gx * 0.027, gy * 0.027).rg; }
                float gw = s.g + (clump - 0.5) * 0.22, dw = s.dist + (clump - 0.5) * 36.0;
                float crestAt = dw - uShore.z * (gw - 0.5);   // the distance of the crest this point trails (or leads)
                float broken = smoothstep(s.breakAt + 15.0, s.breakAt - 15.0, crestAt);   // the wave has broken: its foam follows the crest back out to the break point
                float burst = (1.0 - smoothstep(0.0, 45.0, abs(dw - s.breakAt + 10.0))) * smoothstep(0.42, 0.5, gw) * (1.0 - smoothstep(0.62, 0.8, gw));
                float bore = broken * smoothstep(0.43, 0.5, gw) * (1.0 - smoothstep(0.54, 0.68, gw));
                float trail = broken * smoothstep(0.56, 0.66, gw) * (1.0 - smoothstep(0.66, 0.95, gw)) * 0.8;
                float feather = (1.0 - broken) * smoothstep(0.47, 0.5, gw) * (1.0 - smoothstep(0.5, 0.53, gw)) * smoothstep(s.breakAt + 120.0, s.breakAt + 20.0, dw);
                // Along the shore the breakers break harder in some stretches than others, and not at all in a few.
                float stretch = smoothstep(0.2, 0.75, textureGrad(uFoamMap, p * 0.0011, dpx * 0.0011, dpy * 0.0011).g);
                float surfFoam = max(max(burst * 1.3, bore * 0.9), max(trail * 1.1, feather * 0.6)) * min(s.size * 1.1, 1.3) * mix(0.275, 1.0, s.brk);
                surfAmount = min(surfFoam * surfZone * (0.525 + 0.725 * stretch) * (0.825 + 0.4 * clump) * 1.5, 0.95);   // capped: the lace shows through even the thickest whitewater
            }
            amount = max(amount, (1.0 - smoothstep(0.0, 4.0, abs(s.dist))) * (0.15 + 0.25 * s.open) * fade);
            // The swash: its front is dense, the sheet behind it thins out.
            if (swash && sheet > 0.0) surfAmount = max(surfAmount, max(0.85 * (1.0 - smoothstep(0.0, runup * 0.2, sheet + (b1.g - 0.5) * runup * 0.3)), 0.35 * (1.0 - sinceCrest)));
            // Foam wears away as it ages: fresh at the break, and the further the wave has run in since (travel, from the break point to the
            // waterline) and the further behind its crest, the more of it is lace with holes (on the swash too: the sheet thins as it recedes).
            float age = clamp(ageS + (0.5 - clump) * 0.5, 0.0, 1.0);
            vec2 drift = direction * speed * uTime * 0.2;
            vec2 driftDx = dFdx(drift), driftDy = dFdy(drift);
            // Blotches (the multi-octave noise at two scales) with bubble rims in them; the more foam, the lower the threshold. Up close the
            // foam shows its bubbles (B: fine cell walls), lit on their walls and darker inside.
            vec2 a1 = texture(uFoamMap, p * 0.009 + drift).rg, a2 = texture(uFoamMap, p * 0.027 - drift * 1.3).rg;
            if (rw > 0.01)
            {
                // A river's foam: streaks along the current, a thin trace everywhere in it and a white rush in its rapids.
                vec2 r1, r2;
                riverFoam(p, s.flow, s.speed, dpx, dpy, r1, r2);
                a1 = mix(a1, r1, rw); a2 = mix(a2, r2, rw);
                amount = max(amount, rw * (0.02 + 0.55 * rapid));
            }
            float bubbles = 0.0;
            if (uLean < 0.5 || dist < 600.0)
                bubbles = textureGrad(uFoamMap, p * 0.06 - drift * 2.0, dpx * 0.06 - driftDx * 2.0, dpy * 0.06 - driftDy * 2.0).b * (1.0 - smoothstep(150.0, 600.0, dist));
            float pattern = (a1.g * 0.6 + a2.g * 0.4) * 0.7 + max(a1.r, a2.r) * 0.45 + bubbles * 0.08;
            // Dark and coloured water foams less (a thinner, more broken lace), and the foam takes its colour: white only on clear water, a dirty
            // grey-brown on black water, rust on red.
            amount *= mix(0.7, 1.0, clean);
            float foam = smoothstep(0.95 - amount, 1.25 - amount, pattern);
            if (surfAmount > 0.0)
            {
                // The same lace in three scales (clumps, blotches, rims) in the wave's frame, its threshold rising with the foam's age.
                float lace = (smoothstep(0.15, 0.9, clump) * 0.4 + b1.g * 0.3 + b2.g * 0.3) * 0.7 + max(b1.r, b2.r) * 0.45 + bubbles * 0.08;
                // It wears cell by cell (cells of about 22 units, in the same stretched frame): each bursts at its own age, a hole opening
                // from its middle while the walls between the cells stay as lace; the threshold itself rises only a little with the age.
                vec2 cell = popCells(q * 0.045);
                float popped = smoothstep(cell.y, cell.y + 0.12, age);
                lace -= popped * 0.45 * smoothstep(0.04, 0.3, cell.x);
                float wear = 0.075 + age * 0.3;
                float surf = surfAmount * mix(0.7, 1.0, clean);
                foam = max(foam, smoothstep(0.95 - surf + wear, 1.3 - surf + wear + 0.2 * age, lace));
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
            float edge = thick >= 0.0 ? clamp(thick / max(swash ? 0.5 : 2.0, thicknessError(dist)), 0.0, 1.0) : 1.0;
            // Wet sand, behind the swash and under its front: darker and glossy, drying out until the next wave, faded out over the edge of its band.
            float wet = swash ? (1.0 - sinceCrest) * 0.6 * smoothstep(0.0, 0.3, max(level, wetLevel) - above) : 0.0;
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
                    float f = foam * smoothstep(0.0, 0.15, sheet), front = smoothstep(0.0, 0.6, sheet);
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

    static readonly string MeitouFragment = "#version 330 core\n" + TerrainShaders.HeightFunctions + SkyRenderer.SkyFunctions + ThicknessFunctions + MeitouMotion + MeitouBody;

    /// <summary>The water's own uniforms in the native model: <see cref="ConstantsBlock"/> and the bindless arrays.</summary>
    static readonly Dictionary<string, string> NativeOwn = new()
    {
        ["uWaterHeight"] = "water.params.x", ["uHalfWorld"] = "water.params.y", ["uGridStep"] = "water.params.z", ["uTime"] = "water.params.w",
        ["uCentre"] = "water.centreExtent.xy", ["uExtent"] = "water.centreExtent.z", ["uLean"] = "water.centreExtent.w",
        ["uSeaA"] = "water.seaA", ["uSeaB"] = "water.seaB", ["uSeaColour"] = "water.seaColour.xyz", ["uWaterFar"] = "water.seaColour.w",
        ["uSunDir"] = "water.sunDir.xyz", ["uSunColour"] = "water.sunColour.xyz", ["uGlintDir"] = "water.glintDir.xyz", ["uGlintColour"] = "water.glintColour.xyz",
        ["uOcean"] = "water.ocean", ["uShoreRect"] = "water.shoreRect", ["uShore"] = "water.shore", ["uWaveFade"] = "water.waveFade",
        ["uClarity"] = "water.clarity.xyz", ["uScreen"] = "water.screenReflect.xy", ["uReflect"] = "water.screenReflect.z", ["uRefract"] = "water.screenReflect.w",
        ["uReflectionViewProjection"] = "water.reflectionViewProjection",
        ["uSkySun"] = "water.skySun.xyz", ["uSkyZenith"] = "water.skyZenith.xyz", ["uSkyHorizon"] = "water.skyHorizon.xyz",
        ["uSkySunColour"] = "water.skySunColour.xyz", ["uSkyTwilight"] = "water.skySunColour.w",
        ["uCoarseRect"] = "water.coarseRect", ["uFineRect"] = "water.fineRect", ["uCoarseCells"] = "water.coarseCellsFineBand.xy",
        ["uFineBand"] = "water.coarseCellsFineBand.z", ["uHasFine"] = "(water.coarseCellsFineBand.w > 0.5)", ["uFineCells"] = "water.fineCells.xy",
        ["uHeightCoarse"] = "textures2D[water.heightCoarse]", ["uHeightFine"] = "textures2D[water.heightFine]",
        ["uColourMap"] = "textures2D[water.colourMap]", ["uFlowMap"] = "textures2D[water.flowMap]", ["uFoamMap"] = "textures2D[water.foamMap]",
        ["uNormalMap"] = "textures2D[water.normalMap]", ["uRainMap"] = "textures2D[water.rainMap]",
        ["uParamsA"] = "textures2D[water.paramsA]", ["uParamsB"] = "textures2D[water.paramsB]",
        ["uReflection"] = "textures2D[water.reflectionMap]", ["uRefraction"] = "textures2D[water.refractionMap]",
        ["uOceanMaps"] = "textures2DArray[water.oceanMap]", ["uShoreField"] = "textures2D[water.shoreMap]", ["uRiverMap"] = "textures2D[water.riverMap]",
        ["uSceneDepth"] = "textures2D[water.sceneDepthMap]", ["uDepthPlanes"] = "water.depthPlanes",
    };

    static readonly Dictionary<string, string> NativeMap = NativeShaders.Map(NativeOwn);

    /// <summary>Puts the water constants block after the version line, before the shared text.</summary>
    static string WithConstants(string source)
    {
        int line = source.IndexOf('\n');
        if (line < 0 || !source.StartsWith("#version", StringComparison.Ordinal))
            throw new InvalidOperationException("WaterRenderer: a native water shader has no #version line");
        return source[..(line + 1)] + ConstantsBlock + source[(line + 1)..];
    }

    static string NativeVertex(string source) => NativeShaders.Port(WithConstants(source), NativeMap);
    static string NativeFragment(string source) => Gi.GiResolveShaders.WithAlbedo(NativeShaders.Port(WithConstants(source), NativeMap), none: true);

    /// <summary>The native GPU API (phase 8 stage 2: the water makes no GL call; docs/renderer-native.md 8.6).</summary>
    public IGpuContext Gpu { get; }
    readonly DeviceBuffer quad;
    readonly DeviceBuffer? grid, gridIndices;
    readonly uint gridIndexCount;
    readonly SampledImage[] maps;   // colour, flow, normal, parameters A and B, rain ripples, foam (Meitou), river, in the order of WaterProgram.MapNames
    readonly Vector4 seaA, seaB;
    readonly Vector3 seaColour;
    readonly NativeFrame frame;
    readonly WaterProgram faithful;
    readonly WaterProgram? meitou;
    readonly BindlessTexture[] mapTextures;
    readonly BindlessTexture oceanMap, shoreMap, reflectionMap, refractionMap, heightCoarse, heightFine, sceneDepthMap;
    readonly GpuTexture standInArrayTexture;
    readonly uint standInArray;
    readonly GpuSetLayout constantsLayout;
    readonly Dictionary<ulong, GpuSet> constantSets = [];
    ulong constantSetKey;
    GpuSet constantSet;
    readonly WaveSet waves = new();
    readonly OceanWaves? ocean;
    readonly ShoreField? shore;
    readonly float gridStep;
    readonly ulong uniformAlign;
    GpuTexture? refraction;
    long refractionFrame = -1;
    GpuTexture? sceneDepthCopy;
    long sceneDepthFrame = -1;
    Vector2 sceneDepthPlanes;

    /// <summary>The Enhancements switch <c>water</c>: the game's flat water (Faithful) or Meitou's waves, breakers and foam (the default).</summary>
    public bool Meitou { get; set; } = true;

    /// <summary>The Meitou polar grid: segments round the eye (128 low, 256 default, 512 high), rings follow from it.</summary>
    public const int DefaultGridSegments = 256;

    /// <summary>One native water program: the vertex layout and buffers its one draw needs.</summary>
    sealed class WaterProgram : IDisposable
    {
        public static readonly string[] MapNames = ["uColourMap", "uFlowMap", "uNormalMap", "uParamsA", "uParamsB", "uRainMap", "uFoamMap", "uRiverMap"];
        public readonly NativeProg Program;
        public readonly VertexLayout Layout;
        public readonly GpuBufferBinding[] Vertices;
        public readonly PrimitiveType Topology;
        public GpuProgram Gpu => Program.P;

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

        public void Dispose() => Program.Dispose();
    }

    /// <summary>A stable bindless entry for one water texture; its sampler may change with the upscaler's LOD bias.</summary>
    sealed class BindlessTexture : IDisposable
    {
        readonly IGpuContext gpu;
        readonly BindlessKind kind;
        BindlessHandle handle;
        SampledTexture registered;
        bool has;

        public BindlessTexture(IGpuContext gpu, BindlessKind kind) => (this.gpu, this.kind) = (gpu, kind);

        public uint Index(in SampledTexture texture)
        {
            if (!has)
            {
                handle = new BindlessHandle(kind, gpu.Bindless.Register(kind, texture));
                has = true;
            }
            else if (!texture.Equals(registered)) gpu.Bindless.Update(handle, texture);
            registered = texture;
            return handle.Index;
        }

        public uint IndexOr(in SampledTexture texture, uint fallback) => texture.IsNull ? fallback : Index(texture);

        public void Dispose()
        {
            if (has) gpu.Bindless.Free(handle);
        }
    }

    WaterRenderer(IGpuContext gpu, SampledImage[] maps, Vector4 seaA, Vector4 seaB, Vector3 seaColour, int gridSegments, int oceanSize,
        bool meitouWaves, bool shoreField)
    {
        // The ocean FFT and the shore field are seam-neutral; the faithful water (the quad) simply does not read them.
        ocean = meitouWaves ? new OceanWaves(gpu, oceanSize) : null;
        shore = shoreField ? new ShoreField(gpu) : null;
        Meitou = meitouWaves;
        gridStep = (float)(2 * Math.PI / gridSegments);
        (this.seaA, this.seaB, this.seaColour) = (seaA, seaB, seaColour);
        Gpu = gpu;
        this.maps = maps;
        frame = new NativeFrame(gpu);
        uniformAlign = Math.Max(gpu.UniformBufferAlign, 16);
        constantsLayout = GpuSetLayout.Fixed([new GpuSlot(0, GpuSlotKind.UniformBuffer, GpuStage.All, 1, OffsetAtBind: true)]);
        mapTextures = [.. WaterProgram.MapNames.Select(_ => new BindlessTexture(gpu, BindlessKind.Texture2D))];
        oceanMap = new BindlessTexture(gpu, BindlessKind.Texture2DArray);
        shoreMap = new BindlessTexture(gpu, BindlessKind.Texture2D);
        reflectionMap = new BindlessTexture(gpu, BindlessKind.Texture2D);
        refractionMap = new BindlessTexture(gpu, BindlessKind.Texture2D);
        sceneDepthMap = new BindlessTexture(gpu, BindlessKind.Texture2D);
        heightCoarse = new BindlessTexture(gpu, BindlessKind.Texture2D);
        heightFine = new BindlessTexture(gpu, BindlessKind.Texture2D);
        // A valid one-texel array view for the frames before the ocean's first FFT; the shader gates every read on uOcean.w.
        standInArrayTexture = gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba8, 1, 1, Layers: 1, Kind: TextureKind.Texture2DArray,
            Use: TextureUse.Sampled | TextureUse.TransferDst, Name: "water array stand-in"));
        standInArray = gpu.Bindless.Register(BindlessKind.Texture2DArray, new SampledTexture(gpu.Samplers.Get(ClampLinear), standInArrayTexture.View()));

        float[] corners = [-1, -1, -1, 1, 1, -1, 1, 1]; // triangle strip, counter-clockwise from above
        quad = DeviceBuffer.Create(gpu, sizeof(float) * (ulong)corners.Length, BufferUse.Vertex, "water quad");
        using (var batch = gpu.Uploads.Begin()) batch.Write(quad, 0, MemoryMarshal.AsBytes(corners.AsSpan()));
        LegacyProgram.Attribute?[] quadAttributes = [QuadAttribute(quad)];
        faithful = new WaterProgram(gpu, frame, NativeVertex(Vertex), NativeFragment(Fragment), "water", PrimitiveType.TriangleStrip, quadAttributes, constantsLayout);

        if (meitouWaves)
        {
            var (positions, indices) = PolarGrid(gridSegments);
            grid = DeviceBuffer.Create(gpu, sizeof(float) * (ulong)positions.Length, BufferUse.Vertex, "water grid");
            gridIndices = DeviceBuffer.Create(gpu, sizeof(uint) * (ulong)indices.Length, BufferUse.Index, "water grid indices");
            using (var batch = gpu.Uploads.Begin())
            {
                batch.Write(grid, 0, MemoryMarshal.AsBytes(positions.AsSpan()));
                batch.Write(gridIndices, 0, MemoryMarshal.AsBytes(indices.AsSpan()));
            }
            LegacyProgram.Attribute?[] gridAttributes = [QuadAttribute(grid)];
            meitou = new WaterProgram(gpu, frame, NativeVertex(MeitouVertex), NativeFragment(MeitouFragment), "water meitou",
                PrimitiveType.Triangles, gridAttributes, constantsLayout);
            gridIndexCount = (uint)indices.Length;
        }
    }

    /// <summary>The quad's only vertex input, location 0: two floats, 8 bytes apart (the GL vertex array's
    /// <c>VertexAttribPointer(0, 2, FLOAT, false, 8, 0)</c>); no element buffer.</summary>
    internal static LegacyProgram.Attribute QuadAttribute(DeviceBuffer quad) =>
        new(new GpuBufferBinding(quad.Handle, 0), GlConventions.VertexFormat(GLEnum.Float, 2, false, false), 8, false);

    /// <summary>The Meitou water's mesh: a centre vertex and rings round it whose spacing grows with the radius (as fine across as along, from
    /// 1 unit out to <see cref="GridDetailRadius"/>), then one ring marked to reach the plane's extent. Offsets from the eye, triangle list,
    /// counter-clockwise from above.</summary>
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
        for (int s = 0; s < segments; s++) indices.AddRange([0u, V(0, s + 1), V(0, s)]);
        for (int ring = 0; ring + 1 < radii.Count; ring++)
            for (int s = 0; s < segments; s++)
                indices.AddRange([V(ring, s), V(ring, s + 1), V(ring + 1, s + 1), V(ring, s), V(ring + 1, s + 1), V(ring + 1, s)]);
        return (positions, indices.ToArray());
    }

    /// <summary>Out to this radius the Meitou grid is fine enough for the waves; beyond it the water is flat.</summary>
    public const float GridDetailRadius = 6000;

    /// <summary>
    /// What <see cref="Create"/> reads and works out on the CPU (about 1.5 s at a cold start): the colour, flow, normal and rain maps, the biome water parameters
    /// baked per blend-map pixel, the open sea, the river flow map baked from the world's heights and the foam. Made by <see cref="Prepare"/> on any thread.
    /// </summary>
    public sealed class Source
    {
        internal RgbaImage? Colour, Flow, Normal, Rain;
        internal Vector4[] A = [], B = [];
        internal int BlendWidth, BlendHeight, RiverSize;
        internal (Vector4 A, Vector4 B, Vector3 Colour) Sea;
        internal byte[]? River;
        internal byte[] Foam = [];
        internal readonly List<string> Messages = [];
    }

    /// <summary>The CPU part of <see cref="Create"/>, on any thread (it makes no GPU resource).</summary>
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
        var fallback = BiomeWater.FromRecord(db.OfType(global::Meitou.Data.Fcs.FcsRecordType.BIOMES).First());
        Vector4 A(BiomeWater w) => new(w.Scale.X, w.Scale.Y, w.InvStrength, w.InvOpacity);
        Vector4 B(BiomeWater w) => new(w.Gloss, w.Glow, w.Distortion.X, 0);
        (s.BlendWidth, s.BlendHeight) = (blend.Width, blend.Height);
        s.A = BiomeField.Bake(info, blend.Width, blend.Height, blend.Pixels, c => water.TryGetValue(c, out var w) ? A(w) : null, A(fallback));
        s.B = BiomeField.Bake(info, blend.Width, blend.Height, blend.Pixels, c => water.TryGetValue(c, out var w) ? B(w) : null, B(fallback));

        s.Sea = OpenSea(s.A, s.B, blend.Width, blend.Height, s.Colour);
        Console.WriteLine($"sea       open-sea water: scale {s.Sea.A.X * 5000:0.#}, {s.Sea.A.Y * 5000:0.#}, gloss {s.Sea.B.X:0.##}, colour {s.Sea.Colour.X:0.##} {s.Sea.Colour.Y:0.##} {s.Sea.Colour.Z:0.##}");
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
    public static WaterRenderer Create(IGpuContext gpu, GameInstall install, GameDatabase db, AssetLocator assets, SkyRenderer? sky, List<string> messages, ushort[]? coarseHeights = null,
        int gridSegments = DefaultGridSegments, int oceanSize = OceanWaves.DefaultSize, Source? source = null, bool meitouWaves = true, bool shoreField = true)
    {
        _ = sky;
        var s = source ?? Prepare(install, db, assets, coarseHeights);
        messages.AddRange(s.Messages);
        // As the GL textures were made (WorldGl.Texture2D): a found map has mips only where it repeats; the 1 × 1 stand-in for a missing one
        // always had its (single-level) chain and a trilinear filter.
        SampledImage Rgba(RgbaImage? img, bool repeat, byte[] flat, string name) =>
            img is null ? SampledImage.Rgba8(gpu, 1, 1, flat, repeat, mipmaps: true, name) : SampledImage.Rgba8(gpu, img, repeat, mipmaps: repeat, name);
        SampledImage riverMap = s.River is { } river
            ? SampledImage.Rgba8(gpu, s.RiverSize, s.RiverSize, river, repeat: false, mipmaps: false, "water river map")
            : SampledImage.Rgba8(gpu, 1, 1, [128, 128, 0, 0], repeat: false, mipmaps: false, "water river map");
        return new WaterRenderer(gpu,
            [Rgba(s.Colour, false, [0, 32, 64, 255], "water colour map"), Rgba(s.Flow, false, [128, 128, 0, 255], "water flow map"),
             Rgba(s.Normal, true, [128, 255, 128, 255], "water normal map"),
             SampledImage.Rgba32F(gpu, s.A, s.BlendWidth, s.BlendHeight, "water parameters a"), SampledImage.Rgba32F(gpu, s.B, s.BlendWidth, s.BlendHeight, "water parameters b"),
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
                if (x >= ring && x < width - ring && y >= ring && y < height - ring) { x = width - ring - 1; continue; }
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

    static RgbaImage? Load(GameInstall install, string relative)
    {
        var path = GameFiles.Resolve(Path.Combine(install.DataDirectory, relative));
        return File.Exists(path) ? TextureLoader.LoadImage(File.ReadAllBytes(path)) : null;
    }

    /// <summary>
    /// Steps the Meitou water's motion once per frame: <paramref name="gameHours"/> is the game clock (hours since the load; it stands still while
    /// paused), the wind is the weather's at the camera. Each wave's phase is integrated, so a change of wind changes the waves smoothly.
    /// </summary>
    public void Animate(double gameHours, Vector2 windDirection, float windSpeed)
    {
        waves.Step(gameHours, windDirection, windSpeed);
        if (Meitou) ocean?.Update(gameHours, windDirection, windSpeed);
    }

    /// <summary>Keeps the Meitou water's shore field (<see cref="ShoreField"/>) round the eye: once a frame on the render thread, with the
    /// terrain's heights of this frame. Nothing while the water is Faithful.</summary>
    internal void Track(Vector3 eye, HeightSnapshot heights)
    {
        if (Meitou) shore?.Update(eye, heights);
    }

    /// <summary>
    /// Records the Meitou ocean's FFT before the scene host opens (Metal records compute into the same command buffer and
    /// cannot nest it in a render encoder). The reflection pass has already set <see cref="NearestWater"/>.
    /// </summary>
    internal void PrepareFrame()
    {
        if (Meitou && OceanInView) ocean?.Record();   // the FFT only while water near enough to show its detail is in view
    }

    /// <summary>The Meitou water refracts what lies under it (<see cref="CaptureScene"/> before each of its draws); off: it blends over it as
    /// the game's water does (`--no-water-refraction`, for integrated GPUs).</summary>
    public bool Refraction { get; set; } = true;

    /// <summary>Skip zero-contribution work (<c>--ab water-lean</c>).</summary>
    public bool Lean { get; set; } = true;

    /// <summary>
    /// The distance to the nearest water in view this frame, from the reflection's rect (<see cref="ReflectionPass.NearestWater"/>), set by the frame
    /// loop: infinity with none, NaN when unknown (no reflection pass ran). The ocean's FFT and the refraction copy are
    /// skipped while no water within their reach is in view; unknown gates nothing.
    /// </summary>
    public float NearestWater { get; set; } = float.NaN;

    /// <summary>The eye distance past which the fragment stage reads no ocean texture: the open waves' detail fades from 9000 to 14000 units
    /// (<c>open</c> in <see cref="MeitouFragment"/>; the vertex displacement is gone by <see cref="WaveFadeEnd"/>). The FFT serves nothing beyond it.</summary>
    public const float OceanFadeEnd = 14000;

    /// <summary>Whether the ocean's FFT has anything to feed this frame: water within <see cref="OceanFadeEnd"/> is in view, or that is unknown.</summary>
    bool OceanInView => float.IsNaN(NearestWater) || NearestWater <= OceanFadeEnd;

    /// <summary>The slice near plane bounds every fragment's eye distance from below. Beyond both cutoffs neither
    /// transmitted floor colour nor wet sand contributes, so the blended path is opaque and needs no copy. The same
    /// bound holds for the nearest water in view: with none nearer, the slice shows no transmitting water at all.</summary>
    internal bool NeedsRefraction(float near) => Meitou && Refraction &&
        (!Lean || SliceCanTransmit(near, ClearDistance)) &&
        (float.IsNaN(NearestWater) || SliceCanTransmit(NearestWater, ClearDistance));

    internal static bool SliceCanTransmit(float near, float clearDistance) =>
        near < Math.Max(Math.Max(clearDistance, 100f) * 1.5f, ShoreFadeEnd);

    /// <summary>The Tab panel's "Water clarity x" (Meitou water; not in the game): the biome's absorption per unit of depth is divided by it, so
    /// above 1 the floor shows through deeper water. 1 is the game's opacity; the default 0.5 is murkier.</summary>
    public float Clarity { get; set; } = 0.5f;

    /// <summary>The Tab panel's "Far water clarity" (Meitou water; not in the game): 0 absorbs along the true slant path through the water (it is
    /// up to 20 times the depth at grazing angles, so distant shallows read opaque), 1 only along the depth, as if looked at from straight above. Default 0.25.</summary>
    public float FarClarity { get; set; } = 0.25f;

    /// <summary>The Tab panel's "Water see-through distance" (Meitou water): the eye distance where the floor starts to fade out under the water,
    /// gone by 1.5 times it (refracted; by 1.25 times when blended). The game's water turns opaque at 4000 units, which from high above leaves
    /// only a disc of clear water under the camera. Default 10000.</summary>
    public float ClearDistance { get; set; } = 10000;

    /// <summary>How overcast the sky is this frame (<c>WorldFrame.Overcast</c>: the cloud density, or the rain's amount where that is more), which the
    /// Meitou water's sun glint fades under (<see cref="GlintCloudFade"/>).</summary>
    public float Overcast { get; set; }

    /// <summary>How much of the Meitou water's sun glint a full overcast takes away (docs/render-water.md "Glint under clouds"; not in the game, whose
    /// specular ignores the weather). Default 0.95.</summary>
    public float GlintCloudShade { get; set; } = 0.95f;

    /// <summary>What is left of the glint at cloud density <paramref name="c"/>: 1 up to c 0.3, easing (smoothstep) to 1 − shade at full cover.</summary>
    internal static float GlintCloudFade(float shade, float c)
    {
        float t = Math.Clamp((c - 0.3f) / 0.7f, 0, 1);
        return 1 - Math.Clamp(shade, 0, 1) * t * t * (3 - 2 * t);
    }

    /// <summary>The eye distance from which the Faithful water is opaque (the game's alpha: 1 from 4000 units), so needs no depth.</summary>
    public const float FaithfulOpaqueDistance = 4000;

    /// <summary>Whether this slice's water reads the scene's depth (<see cref="CaptureScene"/>): Vulkan only (Metal keeps the terrain's height), and
    /// only while water in it can be see-through: Meitou with <see cref="Refraction"/> on, as <see cref="NeedsRefraction"/>; Faithful nearer than
    /// <see cref="FaithfulOpaqueDistance"/>. With <see cref="NearestWater"/> unknown (reflections off) it is copied whether or not water is in view.</summary>
    internal bool NeedsDepth(float near)
    {
        if (Gpu is not GpuContext) return false;
        if (!Meitou) return near < FaithfulOpaqueDistance && (float.IsNaN(NearestWater) || NearestWater < FaithfulOpaqueDistance);
        return NeedsRefraction(near);   // without refraction (`--no-water-refraction`) the Meitou water keeps the slant path: no pass restart or copy
    }

    /// <summary>Whether this slice needs <see cref="CaptureScene"/> before its water (<paramref name="depthSlice"/>: its depth can be copied).</summary>
    internal bool NeedsCapture(float near, bool depthSlice) => NeedsRefraction(near) || depthSlice && NeedsDepth(near);

    /// <summary>
    /// Copies the scene drawn so far (the slice's opaque geometry, with no pass open) for the water: at half the size the colour the Meitou water
    /// refracts (the floor and whatever stands in the water, seen through the waves), and the depth, from which both waters measure the distance
    /// through them (<see cref="ThicknessFunctions"/>). One native segment (a full barrier on each side), a blit for the colour and a copy for the depth.
    /// </summary>
    public void CaptureScene(GpuTexture sceneColour, GpuTexture? sceneDepth, float near, float far)
    {
        bool colour = NeedsRefraction(near), depth = sceneDepth is not null && NeedsDepth(near);
        if (!colour && !depth) return;
        var cmd = Gpu.BeginNative("water scene copy");
        if (colour)
        {
            int w = Math.Max(sceneColour.Desc.Width / 2, 1), h = Math.Max(sceneColour.Desc.Height / 2, 1);
            if (refraction is not { } r || r.Desc.Width != w || r.Desc.Height != h || r.Desc.Format != sceneColour.Desc.Format)
            {
                refraction?.Dispose();
                refraction = Gpu is GpuContext vk
                    ? vk.CreateTexture(new TextureDesc(sceneColour.Desc.Format, w, h, Name: "water refraction"), ((CommandList)cmd).Handle)
                    : Gpu.CreateTexture(new TextureDesc(sceneColour.Desc.Format, w, h,
                        Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "water refraction"));
            }
            cmd.Blit(sceneColour, refraction, TextureMinFilter.Linear);
            refractionFrame = Gpu.Frame.Number;
        }
        if (depth && Gpu is GpuContext vulkan)
        {
            var d = sceneDepth!.Desc;
            if (sceneDepthCopy is not { } c || c.Desc.Width != d.Width || c.Desc.Height != d.Height || c.Desc.Format != d.Format)
            {
                sceneDepthCopy?.Dispose();
                sceneDepthCopy = vulkan.CreateTexture(new TextureDesc(d.Format, d.Width, d.Height,
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
                vulkan.Device.NativeImage(sceneDepthCopy!), Silk.NET.Vulkan.ImageLayout.General, 1, &region);
            (sceneDepthFrame, sceneDepthPlanes) = (Gpu.Frame.Number, new Vector2(near, far));
        }
        Gpu.EndNative(cmd);
    }

    /// <summary>The open waves (for the viewer's statistics and tests).</summary>
    public OceanWaves? Ocean => ocean;

    /// <summary>
    /// The water surface: one draw, blended, into the pass VkGl has open. Prepare sets the uniforms and the textures the shader reads (the height
    /// textures, the atmosphere and the heights' uniforms come through the frame globals); Record is one native segment with the pass's state
    /// and the water's own: no culling, depth test without write, alpha blending. No GL state is changed (phase 8 stage 2): the GL code set
    /// these and put back the depth write and blending after the draw; what it left differently (the cull face off, the blend factors) is read by
    /// no later draw (docs/renderer-native.md 8.6). Faithful draws the game's quad; Meitou the polar grid with <see cref="Animate"/>'s motion.
    /// </summary>
    /// <param name="time">Animation time; the game's unit for it is Unknown (the viewer uses real hours). Meitou uses the game clock instead.</param>
    public void Draw(Matrix4x4 viewProjection, Vector3 eye, WorldLighting light, SkyColours colours, float time, float extent, ReflectionPass? reflection = null)
    {
        var targets = Gpu.CurrentTargets();
        bool meitouWater = Meitou && meitou is not null;
        var p = meitouWater ? meitou! : faithful;
        var c = PrepareConstants(meitouWater, eye, light, colours, meitouWater ? waves.NormalTime : time, extent, reflection, targets);
        var slice = Gpu.Frame.WriteConstants(MemoryMarshal.AsBytes(new ReadOnlySpan<WaterConstants>(in c)), uniformAlign);
        var set = ConstantSet(slice.Handle);
        var cmd = Gpu.BeginGuest(meitouWater ? "water meitou" : "water");
        // What the GL code's Enable(DepthTest), DepthMask(false), Disable(CullFace), Enable(Blend) and BlendFunc made of the pass's state (the
        // depth test and blending only with the attachment they need, as VkGl's CurrentState).
        bool hasDepth = targets.Formats.Depth != InternalFormat.None, hasColour = targets.Formats.Colour != InternalFormat.None;
        var state = Gpu.CurrentState() with
        {
            Cull = CullMode.None, DepthTest = hasDepth, DepthWrite = false, Blend = hasColour ? AlphaBlend : BlendState.Off,
        };
        cmd.SetViewport(targets.Viewport);
        cmd.SetScissor(targets.Scissor);
        cmd.SetRaster(state.Cull, state.Front);
        cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
        cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
        var view = new ViewConstants
        {
            ViewProjection = viewProjection, Eye = eye, Time = time,
            LightDir = light.SunDirection, FogColour = light.FogColour, FogDistance = light.FogDistance,
        };
        frame.Bind(cmd, p.Gpu, in view);
        uint offset = (uint)slice.Offset;
        cmd.BindSets(p.Gpu, ConstantsSet, new ReadOnlySpan<GpuSet>(in set), new ReadOnlySpan<uint>(in offset));
        cmd.BindPipeline(Gpu.Pipelines.Get(state.Pipeline(p.Gpu, p.Layout, p.Topology, targets.Formats, meitouWater ? "water meitou" : "water")));
        cmd.BindVertexBuffers(0, p.Vertices);
        if (meitouWater)
        {
            cmd.BindIndexBuffer(new GpuBufferBinding(gridIndices!.Handle, 0, gridIndices.Size), IndexKind.U32);
            cmd.DrawIndexed(gridIndexCount);
        }
        else cmd.Draw(4);
        Gpu.EndGuest(cmd);
    }

    WaterConstants PrepareConstants(bool meitouWater, Vector3 eye, WorldLighting light, SkyColours colours, float time, float extent,
        ReflectionPass? reflection, PassTargets targets)
    {
        var (glintDirection, glintColour) = light.Glint;
        if (meitouWater) glintColour *= GlintCloudFade(GlintCloudShade, Overcast);
        bool reflect = reflection is { Valid: true };
        bool refract = meitouWater && Refraction && refraction is not null && refractionFrame == Gpu.Frame.Number;
        var c = new WaterConstants
        {
            Params = new Vector4(WorldWater.Height, (float)WorldLayout.HalfWorldSize, gridStep, time),
            CentreExtent = new Vector4(eye.X, eye.Z, extent, Lean ? 1f : 0f),
            SeaA = seaA, SeaB = seaB, SeaColour = new Vector4(seaColour, extent / 1.5f),
            SunDir = new Vector4(light.SunDirection, 0f), SunColour = new Vector4(light.SunColour, 0f),
            GlintDir = new Vector4(glintDirection, 0f), GlintColour = new Vector4(glintColour, 0f),
            Ocean = new Vector4(ocean?.Lengths.X ?? 0, ocean?.Lengths.Y ?? 0, ocean?.Lengths.Z ?? 0, ocean is { Ready: true } o ? o.Size : 0),
            ShoreRect = shore is { Ready: true } s ? s.Rect : Vector4.Zero,
            Shore = waves.Shore,
            WaveFade = new Vector4(WaveFadeStart, WaveFadeEnd, ShoreFadeStart, ShoreFadeEnd),
            Clarity = new Vector4(1f / Math.Max(Clarity, 0.01f), Math.Clamp(FarClarity, 0f, 1f), Math.Max(ClearDistance, 100f), 0f),
            ScreenReflect = new Vector4(1f / Math.Max(targets.Width, 1), 1f / Math.Max(targets.Height, 1), reflect ? 1f : 0f, refract ? 1f : 0f),
            ReflectionViewProjection = reflect ? reflection!.ViewProjection : Matrix4x4.Identity,
            SkySun = new Vector4(colours.Sun, 0f), SkyZenith = new Vector4(colours.Zenith, 0f),
            SkyHorizon = new Vector4(colours.Horizon, 0f), SkySunColour = new Vector4(colours.SunColour, colours.Twilight),
        };
        var g = Gpu.Globals;
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

        c.ColourMap = mapTextures[0].Index(maps[0].Sampled());
        c.FlowMap = mapTextures[1].Index(maps[1].Sampled());
        c.NormalMap = mapTextures[2].Index(maps[2].Sampled());
        c.ParamsA = mapTextures[3].Index(maps[3].Sampled());
        c.ParamsB = mapTextures[4].Index(maps[4].Sampled());
        c.RainMap = mapTextures[5].Index(maps[5].Sampled());
        c.FoamMap = mapTextures[6].Index(maps[6].Sampled());
        c.RiverMap = mapTextures[7].Index(maps[7].Sampled());
        c.OceanMap = ocean is { Ready: true } readyOcean ? oceanMap.Index(readyOcean.OceanMaps) : standInArray;
        c.ShoreMap = shore is { Ready: true } readyShore ? shoreMap.Index(readyShore.Sampled()) : Gpu.StandIn2D;
        c.HeightCoarse = heightCoarse.IndexOr(g.Texture("uHeightCoarse")?.Invoke() ?? default, Gpu.StandIn2D);
        c.HeightFine = heightFine.IndexOr(g.Texture("uHeightFine")?.Invoke() ?? default, Gpu.StandIn2D);
        c.ReflectionMap = reflect ? reflectionMap.Index(reflection!.Sampled) : Gpu.StandIn2D;
        c.RefractionMap = refract
            ? refractionMap.Index(new SampledTexture(Gpu.Samplers.Get(ClampLinear), refraction!.View()))
            : Gpu.StandIn2D;
        bool depth = sceneDepthCopy is not null && sceneDepthFrame == Gpu.Frame.Number;
        c.DepthPlanes = new Vector4(sceneDepthPlanes, depth ? 1f : 0f, Gpu.ClipDepthZeroToOne ? 1f : 0f);
        c.SceneDepthMap = depth ? sceneDepthMap.Index(new SampledTexture(Gpu.Samplers.Get(ClampNearest), sceneDepthCopy!.View())) : Gpu.StandIn2D;
        return c;
    }

    static T ReadGlobal<T>(FrameGlobals globals, string name, T fallback) where T : unmanaged
    {
        var value = fallback;
        var bytes = MemoryMarshal.AsBytes(new Span<T>(ref value));
        return globals.UniformValue(name)?.TryRead(bytes) == true ? value : fallback;
    }

    GpuSet ConstantSet(GpuBuffer buffer)
    {
        ulong key = (ulong)buffer.Index;
        if (!constantSet.IsNull && key == constantSetKey) return constantSet;
        if (!constantSets.TryGetValue(key, out var made))
            constantSets[key] = made = Gpu.AllocateConstantSet(constantsLayout, buffer, (ulong)sizeof(WaterConstants));
        (constantSetKey, constantSet) = (key, made);
        return made;
    }

    /// <summary>The eye distances over which the Meitou open waves fade into the flat water (the grid coarsens with the distance), and over which
    /// the shore's breakers and foam fade (they read from much further).</summary>
    public const float WaveFadeStart = 3000, WaveFadeEnd = 5500, ShoreFadeStart = 7000, ShoreFadeEnd = 10000;

    static readonly SamplerDesc ClampLinear = new(TextureMinFilter.Linear, TextureMagFilter.Linear, false,
        TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false,
        DepthFunction.Always, false, false, 1, 0);

    static readonly SamplerDesc ClampNearest = ClampLinear with { Min = TextureMinFilter.Nearest, Mag = TextureMagFilter.Nearest };

    static readonly BlendState AlphaBlend = new(true, BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

    public void Dispose()
    {
        if (ocean is { Dispatched: > 0 } waves)
            Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"water     ocean: {waves.Dispatched} passes ({waves.Size}²), gpu us per pass {(waves.GpuTimed > 0 ? waves.GpuMicroseconds / waves.GpuTimed : 0):F1} ({waves.GpuTimed} timed)"));
        if (shore is { } field)
            Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"water     shore bake {field.LastBakeMs:F0} ms, {field.UploadedFields} fields uploaded and {field.UniformFields} uniform (one texel)"));
        faithful.Dispose();
        meitou?.Dispose();
        frame.Dispose();
        quad.Dispose();
        grid?.Dispose();
        gridIndices?.Dispose();
        foreach (var t in mapTextures) t.Dispose();
        oceanMap.Dispose();
        shoreMap.Dispose();
        reflectionMap.Dispose();
        refractionMap.Dispose();
        sceneDepthMap.Dispose();
        heightCoarse.Dispose();
        heightFine.Dispose();
        Gpu.Bindless.Free(BindlessKind.Texture2DArray, standInArray);
        standInArrayTexture.Dispose();
        foreach (var t in maps) t.Dispose();
        ocean?.Dispose();
        shore?.Dispose();
        refraction?.Dispose();
        sceneDepthCopy?.Dispose();
    }
}
