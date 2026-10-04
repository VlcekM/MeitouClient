using System.Numerics;
using Silk.NET.OpenGL;

namespace Meitou.ModelViewer;

/// <summary>
/// The world view's sky: a colour model of its own (zenith and horizon colours from the sun's height, a warm band at
/// sunrise and sunset, the sun disc), not Kenshi's SkyX scattering. Only the sun's direction follows the game
/// (<see cref="Meitou.Data.World.SkyClock"/>). The horizon colour is also the fog colour, so distant terrain fades
/// into the sky.
/// </summary>
public readonly record struct SkyColours(Vector3 Sun, Vector3 Zenith, Vector3 Horizon, Vector3 SunColour, float Twilight)
{
    static float Smooth(float a, float b, float x) { float t = Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }

    public static SkyColours For(Vector3 sun)
    {
        float e = sun.Y;
        float day = Smooth(-0.12f, 0.2f, e);
        float twilight = 1 - Smooth(0, 0.35f, MathF.Abs(e + 0.02f));
        var zenith = Vector3.Lerp(new(0.01f, 0.015f, 0.035f), new(0.20f, 0.40f, 0.72f), day);
        var horizon = Vector3.Lerp(new(0.03f, 0.035f, 0.06f), new(0.70f, 0.76f, 0.82f), day);
        horizon = Vector3.Lerp(horizon, new(0.90f, 0.55f, 0.32f), twilight * 0.55f * Smooth(-0.15f, 0.0f, e));
        var sunColour = Vector3.Lerp(new(1.0f, 0.55f, 0.3f), new(1.0f, 0.96f, 0.9f), Smooth(0, 0.4f, e)) * Smooth(-0.04f, 0.04f, e);
        return new SkyColours(sun, zenith, horizon, sunColour, twilight);
    }

    /// <summary>Light for the world shaders; <paramref name="fogDistance"/> is where the haze is complete.</summary>
    public WorldLighting Lighting(float fogDistance)
    {
        // Below the horizon the shaders still get a direction (lit faces stay lit) but no sun colour.
        var dir = Sun.Y > 0.02f ? Sun : Vector3.Normalize(new Vector3(Sun.X, 0.02f, Sun.Z));
        var ambientSky = (Horizon * 0.7f + Zenith * 0.3f) * 0.5f + new Vector3(0.01f);
        var ambientGround = Horizon * 0.28f + new Vector3(0.01f);
        return new WorldLighting(dir, SunColour, ambientSky, ambientGround, Horizon, fogDistance);
    }
}

/// <summary>Draws the sky behind everything: a full-screen triangle that looks up the colour per view direction.</summary>
public sealed unsafe class SkyRenderer : IDisposable
{
    /// <summary>GLSL: the sky colour for a view direction (uniforms set by <see cref="SetUniforms"/>).</summary>
    public const string SkyFunctions = """
        uniform vec3 uSkySun;
        uniform vec3 uSkyZenith;
        uniform vec3 uSkyHorizon;
        uniform vec3 uSkySunColour;
        uniform float uSkyTwilight;
        vec3 skyColour(vec3 dir, bool disc)
        {
            float up = clamp(dir.y, 0.0, 1.0);
            float t = pow(up, 0.45);
            vec3 c = mix(uSkyHorizon, uSkyZenith, t);
            float toward = max(dot(dir, uSkySun), 0.0);
            c += vec3(1.0, 0.6, 0.3) * pow(toward, 6.0) * uSkyTwilight * 0.45 * (1.0 - t);
            c += uSkySunColour * pow(toward, 64.0) * 0.25;
            if (disc) c += uSkySunColour * smoothstep(0.99955, 0.9998, toward) * 3.0;
            return c;
        }
        """;

    const string Vertex = """
        #version 330 core
        out vec2 vNdc;
        void main()
        {
            vec2 p = vec2((gl_VertexID & 1) * 4.0 - 1.0, (gl_VertexID & 2) * 2.0 - 1.0);
            vNdc = p;
            gl_Position = vec4(p, 1.0, 1.0);
        }
        """;

    const string Fragment = "#version 330 core\n" + SkyFunctions + """

        in vec2 vNdc;
        out vec4 fragColour;
        uniform mat4 uInverseViewProjection;
        void main()
        {
            vec4 a = uInverseViewProjection * vec4(vNdc, 0.0, 1.0);
            vec4 b = uInverseViewProjection * vec4(vNdc, 1.0, 1.0);
            vec3 dir = normalize(b.xyz / b.w - a.xyz / a.w);
            fragColour = vec4(skyColour(dir, true), 1.0);
        }
        """;

    readonly GL gl;
    readonly uint program, vao;
    readonly Dictionary<(uint, string), int> uniforms = [];

    public SkyRenderer(GL gl)
    {
        this.gl = gl;
        program = WorldGl.Program(gl, Vertex, Fragment);
        vao = gl.GenVertexArray();
    }

    public void Draw(Matrix4x4 viewProjection, SkyColours sky)
    {
        if (!Matrix4x4.Invert(viewProjection, out var inverse)) return;
        gl.UseProgram(program);
        WorldGl.Matrix(gl, U(program, "uInverseViewProjection"), inverse);
        SetUniforms(program, sky);
        gl.Disable(EnableCap.DepthTest);
        gl.DepthMask(false);
        gl.BindVertexArray(vao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        gl.BindVertexArray(0);
        gl.DepthMask(true);
        gl.Enable(EnableCap.DepthTest);
    }

    /// <summary>Sets the <see cref="SkyFunctions"/> uniforms on any program that includes them.</summary>
    public void SetUniforms(uint target, SkyColours sky)
    {
        gl.Uniform3(U(target, "uSkySun"), sky.Sun.X, sky.Sun.Y, sky.Sun.Z);
        gl.Uniform3(U(target, "uSkyZenith"), sky.Zenith.X, sky.Zenith.Y, sky.Zenith.Z);
        gl.Uniform3(U(target, "uSkyHorizon"), sky.Horizon.X, sky.Horizon.Y, sky.Horizon.Z);
        gl.Uniform3(U(target, "uSkySunColour"), sky.SunColour.X, sky.SunColour.Y, sky.SunColour.Z);
        gl.Uniform1(U(target, "uSkyTwilight"), sky.Twilight);
    }

    int U(uint p, string name)
    {
        if (!uniforms.TryGetValue((p, name), out int location)) uniforms[(p, name)] = location = gl.GetUniformLocation(p, name);
        return location;
    }

    public void Dispose()
    {
        gl.DeleteVertexArray(vao);
        gl.DeleteProgram(program);
    }
}
