using System.Numerics;
using Meitou.Data.World;
using Silk.NET.OpenGL;

namespace Meitou.ModelViewer;

/// <summary>Switches for drawing the world (keys in <see cref="WorldApp"/>).</summary>
public sealed class WorldRenderOptions
{
    public bool Textures { get; set; } = true;
    public bool NormalMaps { get; set; } = true;
    public bool Objects { get; set; } = true;
    /// <summary>0 solid, 1 solid + wireframe, 2 wireframe only.</summary>
    public int Wireframe { get; set; }
    /// <summary>0 normal, 1 blend-map slot weights, 2 layer weights (R cliff, G slope, B grass).</summary>
    public int Debug { get; set; }
    /// <summary>Chunks closer than this many chunk sizes use full detail; each doubling of distance halves it.</summary>
    public float LodDistance { get; set; } = 1.5f;
}

/// <summary>
/// Draws a <see cref="TerrainMesh"/>: one vertex buffer per chunk, index buffers per LOD shared by all chunks, the
/// LOD picked per chunk from its distance to the eye, chunks outside the view frustum skipped.
/// </summary>
public sealed unsafe class TerrainRenderer : IDisposable
{
    readonly GL gl;
    readonly uint program;
    readonly Dictionary<string, int> uniforms = [];
    readonly List<GpuChunk> chunks = [];
    readonly uint[] lodBuffers;
    readonly int[] lodCounts;
    readonly TerrainMesh mesh;
    TerrainTextures? textures;

    sealed class GpuChunk
    {
        public required TerrainChunk Chunk;
        public uint Vao, Vbo;
    }

    public TerrainRenderer(GL gl, TerrainMesh mesh)
    {
        this.gl = gl;
        this.mesh = mesh;
        program = WorldGl.Program(gl, TerrainShaders.Vertex, TerrainShaders.Fragment);
        lodBuffers = new uint[mesh.LodCount];
        lodCounts = new int[mesh.LodCount];
        for (int l = 0; l < mesh.LodCount; l++)
        {
            lodBuffers[l] = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, lodBuffers[l]);
            gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, mesh.Indices[l].AsSpan(), BufferUsageARB.StaticDraw);
            lodCounts[l] = mesh.Indices[l].Length;
        }
        foreach (var chunk in mesh.Chunks())
        {
            var gc = new GpuChunk { Chunk = chunk, Vao = gl.GenVertexArray(), Vbo = gl.GenBuffer() };
            gl.BindVertexArray(gc.Vao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, gc.Vbo);
            gl.BufferData<TerrainVertex>(BufferTargetARB.ArrayBuffer, chunk.Vertices.AsSpan(), BufferUsageARB.StaticDraw);
            uint stride = (uint)TerrainVertex.Size;
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)12);
            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, lodBuffers[0]);
            chunks.Add(gc);
        }
        gl.BindVertexArray(0);
    }

    public int ChunkCount => chunks.Count;
    public int DrawnChunks { get; private set; }
    public long DrawnTriangles { get; private set; }

    /// <summary>Uses biome textures and land maps from now on (null: untextured).</summary>
    public void SetTextures(TerrainTextures? t) => textures = t;

    public void Draw(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, Vector3 light, Vector3 fogColour, float fogDistance)
    {
        gl.UseProgram(program);
        WorldGl.Matrix(gl, U("uViewProjection"), viewProjection);
        WorldGl.Matrix(gl, U("uModel"), Matrix4x4.Identity);
        gl.Uniform1(U("uNoRoads"), 0);
        gl.Uniform3(U("uEye"), eye.X, eye.Y, eye.Z);
        gl.Uniform3(U("uLightDir"), light.X, light.Y, light.Z);
        gl.Uniform3(U("uFogColour"), fogColour.X, fogColour.Y, fogColour.Z);
        gl.Uniform1(U("uFogDistance"), fogDistance);
        gl.Uniform1(U("uHalfWorld"), (float)WorldLayout.HalfWorldSize);
        gl.Uniform1(U("uDebug"), options.Debug);
        var t = textures;
        bool textured = options.Textures && t is { HasBiomes: true };
        gl.Uniform1(U("uTextured"), textured ? 1 : 0);
        gl.Uniform1(U("uNormalMaps"), textured && options.NormalMaps ? 1 : 0);
        gl.Uniform1(U("uHasMaps"), options.Textures && t is { HasMaps: true } ? 1 : 0);
        string[] samplers = ["uDiffuse", "uNormal", "uParams", "uCells", "uBlendMap", "uOverlay", "uColour"];
        for (int i = 0; i < samplers.Length; i++) gl.Uniform1(U(samplers[i]), i);
        if (t is not null)
        {
            t.Bind();
            gl.Uniform4(U("uRegion"), t.Region.X, t.Region.Y, t.Region.Z, t.Region.W);
            gl.Uniform4(U("uColourRegion"), t.ColourRegion.X, t.ColourRegion.Y, t.ColourRegion.Z, t.ColourRegion.W);
            gl.Uniform2(U("uCellGrid"), (float)t.CellsX, t.CellsZ);
        }

        gl.Enable(EnableCap.DepthTest);
        gl.Enable(EnableCap.CullFace);
        gl.CullFace(TriangleFace.Back);
        gl.FrontFace(FrontFaceDirection.Ccw);
        DrawnChunks = 0;
        DrawnTriangles = 0;
        if (options.Wireframe != 2) DrawChunks(eye, frustum, options, wire: false);
        if (options.Wireframe != 0)
        {
            gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
            gl.Enable(EnableCap.PolygonOffsetLine);
            gl.PolygonOffset(-1, -1);
            DrawChunks(eye, frustum, options, wire: true);
            gl.Disable(EnableCap.PolygonOffsetLine);
            gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
        }
        gl.BindVertexArray(0);
    }

    void DrawChunks(Vector3 eye, Vector4[] frustum, WorldRenderOptions options, bool wire)
    {
        gl.Uniform1(U("uWireframe"), wire ? 1 : 0);
        float lodSize = mesh.ChunkSize * options.LodDistance;
        foreach (var gc in chunks)
        {
            var c = gc.Chunk;
            if (!WorldCamera.Intersects(frustum, c.Min, c.Max)) continue;
            float d = Vector3.Distance(Vector3.Clamp(eye, c.Min, c.Max), eye);
            int lod = Math.Clamp((int)MathF.Floor(MathF.Log2(Math.Max(d / lodSize, 1))), 0, mesh.LodCount - 1);
            gl.BindVertexArray(gc.Vao);
            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, lodBuffers[lod]);
            gl.DrawElements(PrimitiveType.Triangles, (uint)lodCounts[lod], DrawElementsType.UnsignedInt, (void*)0);
            if (!wire)
            {
                DrawnChunks++;
                DrawnTriangles += lodCounts[lod] / 3;
            }
        }
    }

    /// <summary>
    /// Draws other meshes with the terrain material (TERRAIN-mode map features), after <see cref="Draw"/> set the
    /// frame's uniforms. Each item: a vertex array with position at attribute 0 and normal at 1, its index count, its transform.
    /// </summary>
    public void DrawMeshes(IEnumerable<(uint Vao, int IndexCount, Matrix4x4 Model)> meshes)
    {
        gl.UseProgram(program);
        textures?.Bind();
        gl.Uniform1(U("uNoRoads"), 1);
        gl.Uniform1(U("uWireframe"), 0);
        gl.Disable(EnableCap.CullFace);
        foreach (var (vao, count, model) in meshes)
        {
            WorldGl.Matrix(gl, U("uModel"), model);
            gl.BindVertexArray(vao);
            gl.DrawElements(PrimitiveType.Triangles, (uint)count, DrawElementsType.UnsignedInt, (void*)0);
        }
        WorldGl.Matrix(gl, U("uModel"), Matrix4x4.Identity);
        gl.Uniform1(U("uNoRoads"), 0);
        gl.BindVertexArray(0);
    }

    int U(string name)
    {
        if (!uniforms.TryGetValue(name, out int location)) uniforms[name] = location = gl.GetUniformLocation(program, name);
        return location;
    }

    public void Dispose()
    {
        foreach (var gc in chunks)
        {
            gl.DeleteVertexArray(gc.Vao);
            gl.DeleteBuffer(gc.Vbo);
        }
        foreach (var b in lodBuffers) gl.DeleteBuffer(b);
        gl.DeleteProgram(program);
        textures?.Dispose();
    }
}
