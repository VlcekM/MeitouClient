using System.Numerics;
using Silk.NET.OpenGL;

namespace Meitou.ModelViewer;

/// <summary>
/// Draws a <see cref="CharacterScene"/>: the body with Kenshi's character layering (head at uv + (0, 1), skin tone mask,
/// hair and beard head overlays, clothing "vest" layers), channel-packed hair, worn items with their cut-out and dye,
/// and bone-attached items. Uses <see cref="Renderer"/> for clearing, the grid and the texture cache, and its own shader.
/// </summary>
public sealed unsafe class CharacterRenderer : IDisposable
{
    readonly GL gl;
    readonly Renderer basis;
    readonly uint program, lineProgram, lineVao, lineVbo;
    readonly Dictionary<string, int> uniforms = [], lineUniforms = [];
    readonly List<Gpu> parts = [];

    sealed class Gpu
    {
        public required CharacterPartModel Part;
        public required List<(ModelPart Sub, uint Vao, uint Vbo, uint Ebo)> Subs;
        public uint Diffuse, Normal, ColourMap, HeadDiffuse, HeadNormal, HeadMask, BodyMask, HairOverlay, BeardOverlay;
        public uint[] VestDiffuse = new uint[3], VestNormal = new uint[3], VestColour = new uint[3];
        public bool Swizzled;
    }

    public CharacterRenderer(GL gl, Renderer basis, CharacterScene scene)
    {
        this.gl = gl;
        this.basis = basis;
        program = Link(Shaders.MeshVertex, Fragment);
        lineProgram = Link(Shaders.LineVertex, Shaders.LineFragment);
        lineVao = gl.GenVertexArray();
        lineVbo = gl.GenBuffer();
        gl.BindVertexArray(lineVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, lineVbo);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 24, (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 24, (void*)12);
        gl.BindVertexArray(0);
        foreach (var part in scene.Parts) parts.Add(Upload(part));
        foreach (var m in basis.Messages) Console.WriteLine($"warning   {m}");
        basis.Messages.Clear();
    }

    Gpu Upload(CharacterPartModel part)
    {
        var subs = new List<(ModelPart, uint, uint, uint)>();
        foreach (var sub in part.Model.Parts)
        {
            uint vao = gl.GenVertexArray(), vbo = gl.GenBuffer(), ebo = gl.GenBuffer();
            gl.BindVertexArray(vao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            gl.BufferData<Vertex>(BufferTargetARB.ArrayBuffer, sub.Vertices.AsSpan(), BufferUsageARB.StaticDraw);
            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
            gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, sub.Indices.AsSpan(), BufferUsageARB.StaticDraw);
            uint stride = (uint)Vertex.Size;
            void Attrib(uint index, int size, int offset)
            {
                gl.EnableVertexAttribArray(index);
                gl.VertexAttribPointer(index, size, VertexAttribPointerType.Float, false, stride, (void*)offset);
            }
            Attrib(0, 3, 0); Attrib(1, 3, 12); Attrib(2, 2, 24); Attrib(3, 4, 32); Attrib(4, 4, 48);
            gl.EnableVertexAttribArray(5);
            gl.VertexAttribIPointer(5, 4, VertexAttribIType.UnsignedByte, stride, (void*)64);
            Attrib(6, 4, 68);
            subs.Add((sub, vao, vbo, ebo));
        }
        gl.BindVertexArray(0);
        var m = part.Material;
        bool body = m.Shading == CharacterShading.Body; // Kenshi's character units clamp to a transparent border
        var gpu = new Gpu
        {
            Part = part, Subs = subs,
            Diffuse = basis.LoadTexture(m.Diffuse, body), Normal = basis.LoadTexture(m.Normal, body), ColourMap = basis.LoadTexture(m.ColourMap, false),
            HeadDiffuse = basis.LoadTexture(m.HeadDiffuse, true), HeadNormal = basis.LoadTexture(m.HeadNormal, true), HeadMask = basis.LoadTexture(m.HeadMask, true),
            BodyMask = basis.LoadTexture(m.BodyMask, true), HairOverlay = basis.LoadTexture(m.HairOverlay, true), BeardOverlay = basis.LoadTexture(m.BeardOverlay, true),
        };
        for (int i = 0; i < m.Vests.Count && i < 3; i++)
        {
            gpu.VestDiffuse[i] = basis.LoadTexture(m.Vests[i].Diffuse, true);
            gpu.VestNormal[i] = basis.LoadTexture(m.Vests[i].Normal, true);
            gpu.VestColour[i] = basis.LoadTexture(m.Vests[i].ColourMap, true);
        }
        gpu.Swizzled = m.Normal is { } n && basis.IsSwizzled(n);
        return gpu;
    }

    public void Draw(Camera camera, int width, int height, RenderOptions options, CharacterScene scene)
    {
        bool skeleton = options.Skeleton;
        options.Skeleton = false;
        basis.Draw(camera, width, height, options, null, null); // clear + grid
        options.Skeleton = skeleton;

        var viewProjection = camera.View * camera.Projection(width / (float)Math.Max(height, 1));
        gl.UseProgram(program);
        Matrix("uViewProjection", viewProjection);
        var eye = camera.Eye;
        gl.Uniform3(U("uEye"), eye.X, eye.Y, eye.Z);
        var light = Vector3.Normalize(new Vector3(0.45f, 0.8f, 0.35f));
        gl.Uniform3(U("uLightDir"), light.X, light.Y, light.Z);
        string[] samplers = ["uDiffuse", "uNormal", "uColourMap", "uHeadDiffuse", "uHeadNormal", "uHeadMask", "uBodyMask", "uHairOverlay", "uBeardOverlay",
            "uVest0", "uVestN0", "uVestC0", "uVest1", "uVestN1", "uVestC1", "uVest2", "uVestN2", "uVestC2"];
        for (int i = 0; i < samplers.Length; i++) gl.Uniform1(U(samplers[i]), i);
        var bones = scene.Animator.SkinMatrices;
        fixed (Matrix4x4* p = bones)
            gl.UniformMatrix4(U("uBones"), (uint)Math.Min(bones.Length, Shaders.MaxBones), false, (float*)p);

        if (options.Wireframe != 2) DrawParts(options, scene, wire: false);
        if (options.Wireframe != 0)
        {
            gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
            gl.Enable(EnableCap.PolygonOffsetLine);
            gl.PolygonOffset(-1, -1);
            DrawParts(options, scene, wire: true);
            gl.Disable(EnableCap.PolygonOffsetLine);
            gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
        }
        if (options.Skeleton) DrawSkeleton(viewProjection, scene.Animator);
        gl.BindVertexArray(0);
    }

    void DrawParts(RenderOptions options, CharacterScene scene, bool wire)
    {
        foreach (var gp in parts)
        {
            var m = gp.Part.Material;
            bool textures = options.Textures;
            Matrix("uModel", gp.Part.Transform(scene.Animator));
            gl.Uniform1(U("uShading"), (int)m.Shading);
            gl.Uniform1(U("uWireframe"), wire ? 1 : 0);
            Bind(0, textures ? gp.Diffuse : 0); Bind(1, textures ? gp.Normal : 0); Bind(2, gp.ColourMap);
            Bind(3, gp.HeadDiffuse); Bind(4, gp.HeadNormal); Bind(5, gp.HeadMask); Bind(6, gp.BodyMask);
            Bind(7, gp.HairOverlay); Bind(8, gp.BeardOverlay);
            for (int i = 0; i < 3; i++) { Bind(9 + 3 * i, gp.VestDiffuse[i]); Bind(10 + 3 * i, gp.VestNormal[i]); Bind(11 + 3 * i, gp.VestColour[i]); }
            gl.Uniform1(U("uHasDiffuse"), textures && gp.Diffuse != 0 ? 1 : 0);
            gl.Uniform1(U("uHasNormal"), textures && options.NormalMaps && gp.Normal != 0 ? 1 : 0);
            gl.Uniform1(U("uNormalSwizzled"), gp.Swizzled ? 1 : 0);
            gl.Uniform1(U("uHasHead"), gp.HeadDiffuse != 0 ? 1 : 0);
            gl.Uniform1(U("uHasHeadNormal"), gp.HeadNormal != 0 ? 1 : 0);
            gl.Uniform1(U("uHasHeadMask"), gp.HeadMask != 0 ? 1 : 0);
            gl.Uniform1(U("uHasBodyMask"), gp.BodyMask != 0 ? 1 : 0);
            gl.Uniform1(U("uHasHair"), gp.HairOverlay != 0 ? 1 : 0);
            gl.Uniform1(U("uHasBeard"), gp.BeardOverlay != 0 ? 1 : 0);
            int vests = 0;
            for (int i = 0; i < 3; i++) if (gp.VestDiffuse[i] != 0 && gp.VestNormal[i] != 0) vests = i + 1;
            gl.Uniform1(U("uVestCount"), textures ? vests : 0);
            gl.Uniform1(U("uVestColoured0"), gp.VestColour[0] != 0 && m.HasShirtColour ? 1 : 0);
            gl.Uniform1(U("uVestColoured1"), gp.VestColour[1] != 0 && m.HasShirtColour ? 1 : 0);
            gl.Uniform1(U("uVestColoured2"), gp.VestColour[2] != 0 && m.HasShirtColour ? 1 : 0);
            V3("uShirtColour", m.ShirtColour);
            V3("uSkinTone", m.SkinTone);
            V3("uHairColour", m.HairColour);
            V4("uHairAlpha", m.HairOverlayAlpha); V4("uHairMult", m.HairOverlayMult); V4("uBeardAlpha", m.BeardOverlayAlpha);
            V4("uDiffuseChannel", m.DiffuseChannel); V4("uAlphaChannel", m.AlphaChannel);
            gl.Uniform1(U("uAlphaThreshold"), m.AlphaThreshold);
            gl.Uniform1(U("uClipNormalAlpha"), m.ClipOnNormalAlpha && gp.Normal != 0 && textures ? 1 : 0);
            gl.Uniform1(U("uDyed"), m.Dyed && gp.ColourMap != 0 && textures ? 1 : 0);
            V4("uColour1", m.Colour1); V4("uColour2", m.Colour2);
            bool doubleSided = m.DoubleSided || m.Shading == CharacterShading.Hair;
            if (options.BackfaceCulling && !doubleSided && !wire) gl.Enable(EnableCap.CullFace); else gl.Disable(EnableCap.CullFace);
            foreach (var (sub, vao, _, _) in gp.Subs)
            {
                gl.Uniform1(U("uSkinned"), sub.Skinned ? 1 : 0);
                gl.BindVertexArray(vao);
                gl.DrawElements(PrimitiveType.Triangles, (uint)sub.Indices.Length, DrawElementsType.UnsignedInt, (void*)0);
            }
        }
        gl.Disable(EnableCap.CullFace);
    }

    void DrawSkeleton(Matrix4x4 viewProjection, Animator animator)
    {
        var lines = new List<float>();
        for (int h = 0; h < animator.BoneCount; h++)
            if (animator.BoneParent(h) is { } p && p < animator.BoneCount)
            {
                var a = animator.BonePosition(p);
                var b = animator.BonePosition(h);
                lines.AddRange([a.X, a.Y, a.Z, 0.2f, 1f, 0.4f, b.X, b.Y, b.Z, 0.2f, 1f, 0.4f]);
            }
        if (lines.Count == 0) return;
        gl.Disable(EnableCap.DepthTest);
        gl.UseProgram(lineProgram);
        var identity = Matrix4x4.Identity;
        gl.UniformMatrix4(L("uViewProjection"), 1, false, (float*)&viewProjection);
        gl.UniformMatrix4(L("uModel"), 1, false, (float*)&identity);
        gl.BindVertexArray(lineVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, lineVbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, lines.ToArray().AsSpan(), BufferUsageARB.StreamDraw);
        gl.DrawArrays(PrimitiveType.Lines, 0, (uint)(lines.Count / 6));
        gl.Enable(EnableCap.DepthTest);
    }

    void Bind(int unit, uint texture)
    {
        gl.ActiveTexture(TextureUnit.Texture0 + unit);
        gl.BindTexture(TextureTarget.Texture2D, texture);
    }

    void Matrix(string name, Matrix4x4 m) => gl.UniformMatrix4(U(name), 1, false, (float*)&m);
    void V3(string name, Vector3 v) => gl.Uniform3(U(name), v.X, v.Y, v.Z);
    void V4(string name, Vector4 v) => gl.Uniform4(U(name), v.X, v.Y, v.Z, v.W);

    int U(string name)
    {
        if (!uniforms.TryGetValue(name, out int l)) uniforms[name] = l = gl.GetUniformLocation(program, name);
        return l;
    }

    int L(string name)
    {
        if (!lineUniforms.TryGetValue(name, out int l)) lineUniforms[name] = l = gl.GetUniformLocation(lineProgram, name);
        return l;
    }

    uint Link(string vertex, string fragment)
    {
        uint Compile(ShaderType type, string source)
        {
            uint s = gl.CreateShader(type);
            gl.ShaderSource(s, source);
            gl.CompileShader(s);
            gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException($"{type} compile failed: " + gl.GetShaderInfoLog(s));
            return s;
        }
        uint vs = Compile(ShaderType.VertexShader, vertex), fs = Compile(ShaderType.FragmentShader, fragment);
        uint p = gl.CreateProgram();
        gl.AttachShader(p, vs);
        gl.AttachShader(p, fs);
        gl.LinkProgram(p);
        gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0) throw new InvalidOperationException("Shader link failed: " + gl.GetProgramInfoLog(p));
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        return p;
    }

    public void Dispose()
    {
        foreach (var gp in parts)
            foreach (var (_, vao, vbo, ebo) in gp.Subs)
            {
                gl.DeleteVertexArray(vao);
                gl.DeleteBuffer(vbo);
                gl.DeleteBuffer(ebo);
            }
        gl.DeleteVertexArray(lineVao);
        gl.DeleteBuffer(lineVbo);
        gl.DeleteProgram(program);
        gl.DeleteProgram(lineProgram);
    }

    // The viewer's own GLSL. The layering order follows the facts in docs/characters.md (read from Kenshi's character
    // shaders); lighting is the viewer's simple forward model, not Kenshi's deferred one.
    const string Fragment = """
        #version 330 core
        in vec3 vWorld;
        in vec3 vNormal;
        in vec4 vTangent;
        in vec2 vUv;
        in vec4 vColour;

        uniform int uShading;          // 0 item, 1 body, 2 hair
        uniform bool uWireframe;
        uniform sampler2D uDiffuse, uNormal, uColourMap, uHeadDiffuse, uHeadNormal, uHeadMask, uBodyMask, uHairOverlay, uBeardOverlay;
        uniform sampler2D uVest0, uVestN0, uVestC0, uVest1, uVestN1, uVestC1, uVest2, uVestN2, uVestC2;
        uniform bool uHasDiffuse, uHasNormal, uNormalSwizzled, uHasHead, uHasHeadNormal, uHasHeadMask, uHasBodyMask, uHasHair, uHasBeard;
        uniform int uVestCount;
        uniform bool uVestColoured0, uVestColoured1, uVestColoured2;
        uniform vec3 uShirtColour, uSkinTone, uHairColour;
        uniform vec4 uHairAlpha, uHairMult, uBeardAlpha, uDiffuseChannel, uAlphaChannel;
        uniform float uAlphaThreshold;
        uniform bool uClipNormalAlpha, uDyed;
        uniform vec4 uColour1, uColour2;
        uniform vec3 uLightDir, uEye;

        out vec4 fragColour;

        void vest(sampler2D d, sampler2D n, sampler2D c, bool coloured, vec2 uv, inout vec3 albedo, inout vec4 nm, inout float gloss)
        {
            vec4 v = texture(d, uv);
            vec4 vn = texture(n, uv);
            if (coloured) v.rgb = mix(v.rgb, uShirtColour * dot(v.rgb, vec3(1.0 / 3.0)), texture(c, uv).r);
            albedo = mix(albedo, v.rgb, vn.a);
            nm.wy = mix(nm.wy, vn.xy, vn.a);
            gloss = mix(gloss, v.a, vn.a);
        }

        void main()
        {
            if (uWireframe) { fragColour = vec4(0.95, 0.75, 0.2, 1.0); return; }
            vec3 n = normalize(vNormal);
            if (!gl_FrontFacing) n = -n;
            vec2 uv = vUv;
            vec3 albedo = vec3(0.72);
            float gloss = 0.2;
            vec4 nm = vec4(0.5, 0.5, 1.0, 0.5);
            bool swizzled = uNormalSwizzled;

            if (uShading == 2)
            {
                vec4 t = uHasDiffuse ? texture(uDiffuse, uv) : vec4(1.0);
                if (uHasDiffuse && dot(t, uAlphaChannel) < uAlphaThreshold) discard;
                float brightness = dot(t, uDiffuseChannel);
                albedo = brightness * uHairColour;
                gloss = brightness * 0.3;
            }
            else if (uShading == 1)
            {
                vec4 body = uHasDiffuse ? texture(uDiffuse, uv) : vec4(0.7, 0.6, 0.5, 0.2);
                if (uHasNormal) nm = texture(uNormal, uv);
                float tone = uHasBodyMask ? texture(uBodyMask, uv).r : 0.0;
                vec2 huv = uv + vec2(0.0, 1.0);          // head UVs sit in v -1..0
                if (uHasHead) body += texture(uHeadDiffuse, huv);
                if (uHasHeadNormal && uHasNormal) nm += texture(uHeadNormal, huv);
                if (uHasHeadMask) tone += texture(uHeadMask, huv).r;
                body.rgb *= 1.0 - uSkinTone * tone;
                if (uHasHair)
                {
                    vec4 h = texture(uHairOverlay, huv);
                    body.rgb = mix(body.rgb, uHairColour * clamp(dot(h, uHairMult), 0.0, 1.0), clamp(dot(h, uHairAlpha), 0.0, 1.0));
                }
                if (uHasBeard)
                {
                    float b = clamp(dot(texture(uBeardOverlay, huv), uBeardAlpha), 0.0, 1.0);
                    body.rgb = mix(body.rgb, uHairColour * b, b);
                }
                albedo = body.rgb;
                gloss = body.a;
                if (uVestCount > 0) vest(uVest0, uVestN0, uVestC0, uVestColoured0, uv, albedo, nm, gloss);
                if (uVestCount > 1) vest(uVest1, uVestN1, uVestC1, uVestColoured1, uv, albedo, nm, gloss);
                if (uVestCount > 2) vest(uVest2, uVestN2, uVestC2, uVestColoured2, uv, albedo, nm, gloss);
                swizzled = true;                          // character normals: X in alpha, Y in green
            }
            else
            {
                vec4 d = uHasDiffuse ? texture(uDiffuse, uv) : vec4(0.72, 0.72, 0.72, 0.2);
                if (uHasNormal || uClipNormalAlpha) nm = texture(uNormal, uv);
                if (uClipNormalAlpha && nm.a < 0.6) discard;
                if (uDyed)
                {
                    vec3 cm = texture(uColourMap, uv).rgb;
                    float intensity = dot(d.rgb, vec3(1.0 / 3.0));
                    d.rgb = mix(d.rgb, uColour1.rgb * mix(intensity, 1.0, uColour1.a), cm.r);
                    d.rgb = mix(d.rgb, uColour2.rgb * mix(intensity, 1.0, uColour2.a), cm.g);
                }
                albedo = d.rgb;
                gloss = d.a;
            }

            if (uHasNormal && uShading != 2 && dot(vTangent.xyz, vTangent.xyz) > 1e-8)
            {
                vec3 t = normalize(vTangent.xyz - n * dot(n, vTangent.xyz));
                vec3 b = cross(n, t) * vTangent.w;
                vec3 tn;
                if (swizzled) { vec2 xy = vec2(nm.a, nm.g) * 2.0 - 1.0; tn = vec3(xy, sqrt(max(0.0, 1.0 - dot(xy, xy)))); }
                else tn = nm.xyz * 2.0 - 1.0;
                n = normalize(t * tn.x + b * tn.y + n * tn.z);
            }

            vec3 l = normalize(uLightDir);
            vec3 v = normalize(uEye - vWorld);
            float diff = max(dot(n, l), 0.0);
            vec3 ambient = mix(vec3(0.22, 0.20, 0.18), vec3(0.42, 0.45, 0.50), 0.5 + 0.5 * n.y);
            vec3 h = normalize(l + v);
            gloss = clamp(gloss, 0.0, 1.0);
            float spec = pow(max(dot(n, h), 0.0), 8.0 + 56.0 * gloss) * gloss * 0.5;
            fragColour = vec4(albedo * (ambient + diff * vec3(1.0, 0.97, 0.92)) + spec * diff, 1.0);
        }
        """;
}
