// The sky through the Metal backend's seam: the engine's own SkyRenderer (IGpuContext) on a MetalContext —
// the same recording model the Vulkan path drives (a host pass, a guest segment inside it, the present).
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Content;
using Meitou.Data.World;
using Meitou.Metal;
using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;

var hour = 12f;
var frames = 300;
string? shot = null;
bool world = false;
bool tri = false;
bool table = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--hour" && i + 1 < args.Length) hour = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    else if (args[i] == "--hold") frames = 1800;
    else if (args[i] == "--shot" && i + 1 < args.Length) { shot = args[++i]; frames = 10; }
    else if (args[i] == "--world") world = true;
    else if (args[i] == "--tri") tri = true;
    else if (args[i] == "--table") { table = true; tri = true; }   // --table: the --tri quad whose colour comes out of the bindless table
}

ObjectiveC.LinkMetal();

var options = WindowOptions.Default with { Size = new Vector2D<int>(960, 600), Title = "meitou metal sky (the seam)" };
using var display = new MetalDisplay(options);
using var ctx = new MetalContext(display);
var sky = world && GameInstall.Locate() is { } install
    ? new SkyRenderer(ctx, new AssetLocator(install))
    : new SkyRenderer(ctx);
display.Activate();

// The vertex vocabulary's testbed (--tri): a native-model quad out of a vertex buffer, an index buffer, push constants and
// two sets that share a binding number — the terrain's shape (its set 0 and set 2 both have a binding-0 block, so Metal's
// one binding table per stage has to flatten them apart, docs/metal-backend.md).
GpuProgram triProgram = default;
VertexLayout triLayout = null!;
GpuSet triTintSet = default;
DeviceBuffer? triVertices = null, triIndices = null, triFrame = null, triView = null, triTint = null;
GpuPipeline triPipeline = default;
SampledImage? tableRed = null, tableBlue = null;
if (tri)
{
    const string vertex = """
        #version 450 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec4 aCol;
        layout(std140, set = 0, binding = 4) uniform ViewConstants { mat4 uViewProjection; };
        layout(push_constant) uniform Push { vec2 uOffset; } pc;
        out vec4 vCol;
        void main()
        {
            vCol = aCol;
            gl_Position = uViewProjection * vec4(aPos + pc.uOffset, 0.0, 1.0);
        }
        """;
    const string fragment = """
        #version 450 core
        layout(std140, set = 0, binding = 0) uniform FrameConstants { vec4 uTime; };
        layout(std140, set = 2, binding = 0) uniform Tint { vec4 uTint; };
        in vec4 vCol;
        layout(location = 0) out vec4 colour;
        void main()
        {
            colour = vCol * uTint * (1.0 - uTime.x);
        }
        """;
    if (table)
    {
        // The bindless set, in the separate form the Metal backend's table model needs (the Vulkan table is combined
        // samplers; Metal's argument buffer wants separate arrays): the pair constructed per use, the same index.
        const string tableVertex = """
            #version 450 core
            layout(location = 0) in vec2 aPos;
            layout(location = 1) in vec2 aUv;
            layout(location = 0) out vec2 vUv;
            void main()
            {
                vUv = aUv;
                gl_Position = vec4(aPos, 0.0, 1.0);
            }
            """;
        const string tableFragment = """
            #version 450 core
            #extension GL_EXT_nonuniform_qualifier : require
            layout(set = 1, binding = 0) uniform sampler2D textures2D[];
            layout(set = 1, binding = 1) uniform sampler2DArray textures2DArray[];
            layout(set = 1, binding = 2) uniform samplerCube texturesCube[];
            layout(set = 1, binding = 3) uniform sampler2DShadow shadowTextures[];
            layout(set = 1, binding = 4) uniform usampler2D utextures2D[];
            layout(set = 1, binding = 5) uniform isampler2D itextures2D[];
            layout(set = 1, binding = 6) uniform sampler2DArrayShadow shadowArrays[];
            layout(push_constant) uniform Push { uint uIndex; } pc;
            in vec2 vUv;
            layout(location = 0) out vec4 colour;
            void main()
            {
                vec4 a = texture(textures2D[pc.uIndex], vUv);
                vec4 b = texture(textures2D[pc.uIndex + 1u], vUv);
                colour = mix(a, b, vUv.x);
                colour *= 1.0 - texture(textures2DArray[pc.uIndex], vec3(vUv, 0.0)).a;
                colour.r += textureLod(texturesCube[pc.uIndex], vec3(vUv, 0.5), 0.0).r;
                colour.g += texelFetch(utextures2D[pc.uIndex], ivec2(0), 0).r;
                colour.b += texelFetch(itextures2D[pc.uIndex], ivec2(1, 0), 0).r;
            }
            """;
        var bindless = GpuSetLayout.Bindless;
        triProgram = ctx.Shaders.Native(tableVertex, tableFragment, "tri table", [GpuSetLayout.Fixed([]), bindless, GpuSetLayout.Fixed([])], 4);
        triLayout = new VertexLayout(
            new VertexInput(0, InternalFormat.RG32f, 16, false),
            new VertexInput(1, InternalFormat.RG32f, 16, false));
        float[] tableVerticesData = [-1, -1, 0, 0,   1, -1, 1, 0,   1, 1, 1, 1,   -1, 1, 0, 1];
        uint[] tableIndices = [0, 1, 2, 0, 2, 3];
        using var tableBatch = ctx.Uploads.Begin();
        triVertices = new DeviceBuffer(ctx, ctx.CreateBuffer(16 * 4, BufferUse.Vertex, "tri vertices"), true, "tri vertices");
        triIndices = new DeviceBuffer(ctx, ctx.CreateBuffer(6 * 4, BufferUse.Index, "tri indices"), true, "tri indices");
        tableBatch.Write(triVertices, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<float>(tableVerticesData)));
        tableBatch.Write(triIndices, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<uint>(tableIndices)));
        var red = SampledImage.Rgba8(ctx, 2, 2, [255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255], false, false, "table red");
        var blue = SampledImage.Rgba8(ctx, 2, 2, [0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255], false, false, "table blue");
        tableRed = red;
        tableBlue = blue;
        // The other kinds' index 0: black dummies, so the quad's extra terms add nothing (the rewrite's five kinds run).
        byte[] black = [0, 0, 0, 0];
        var bindlessTable = ctx.Bindless;
        var sampler = red.Sampled().Sampler;
        var array = tableBatch.Create(new TextureDesc(InternalFormat.Rgba8, 1, 1, Layers: 1, Kind: TextureKind.Texture2DArray,
            Use: TextureUse.Sampled | TextureUse.TransferDst, Name: "table array"));
        tableBatch.Write(array, 0, 0, new PixelRect(0, 0, 1, 1), black);
        var cube = tableBatch.Create(new TextureDesc(InternalFormat.Rgba8, 1, 1, Kind: TextureKind.Cube,
            Use: TextureUse.Sampled | TextureUse.TransferDst, Name: "table cube"));
        for (int face = 0; face < 6; face++) tableBatch.Write(cube, 0, face, new PixelRect(0, 0, 1, 1), black);
        var uint2D = tableBatch.Create(new TextureDesc(InternalFormat.R32ui, 1, 1, Use: TextureUse.Sampled | TextureUse.TransferDst, Name: "table uint"));
        tableBatch.Write(uint2D, 0, 0, new PixelRect(0, 0, 1, 1), black);
        var int2D = tableBatch.Create(new TextureDesc(InternalFormat.R32i, 1, 1, Use: TextureUse.Sampled | TextureUse.TransferDst, Name: "table int"));
        tableBatch.Write(int2D, 0, 0, new PixelRect(0, 0, 1, 1), black);
        bindlessTable.Register(red.Texture, sampler);                                        // index 0
        bindlessTable.Register(blue.Texture, sampler);                                       // index 1
        bindlessTable.Register(BindlessKind.Texture2DArray, new SampledTexture(sampler, array.View()));
        bindlessTable.Register(BindlessKind.Cube, new SampledTexture(sampler, cube.View()));
        bindlessTable.Register(BindlessKind.UTexture2D, new SampledTexture(sampler, uint2D.View()));
        bindlessTable.Register(BindlessKind.ITexture2D, new SampledTexture(sampler, int2D.View()));
    }
    else
    {
        var set0 = GpuSetLayout.Written(
        [
            new GpuSlot(0, GpuSlotKind.UniformBuffer, GpuStage.Fragment),
            new GpuSlot(4, GpuSlotKind.UniformBuffer, GpuStage.Vertex),
        ]);
        var set2 = GpuSetLayout.Fixed([new GpuSlot(0, GpuSlotKind.UniformBuffer, GpuStage.Fragment, OffsetAtBind: true)]);
        triProgram = ctx.Shaders.Native(vertex, fragment, "tri", [set0, GpuSetLayout.Fixed([]), set2], 8);
        triLayout = new VertexLayout(
            new VertexInput(0, InternalFormat.RG32f, 24, false),    // one interleaved buffer, each location its own binding (VkGl's)
            new VertexInput(1, InternalFormat.Rgba32f, 24, false));
        float[] vertices = [-1, -1, 1, 0, 0, 1,   1, -1, 0, 1, 0, 1,   1, 1, 0, 0, 1, 1,   -1, 1, 1, 1, 0, 1];
        uint[] indices = [0, 1, 2, 0, 2, 3];
        float[] identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
        using var batch = ctx.Uploads.Begin();
        triVertices = new DeviceBuffer(ctx, ctx.CreateBuffer(24 * 4, BufferUse.Vertex, "tri vertices"), true, "tri vertices");
        triIndices = new DeviceBuffer(ctx, ctx.CreateBuffer(6 * 4, BufferUse.Index, "tri indices"), true, "tri indices");
        triFrame = new DeviceBuffer(ctx, ctx.CreateBuffer(256, BufferUse.Uniform, "tri frame"), true, "tri frame");
        triView = new DeviceBuffer(ctx, ctx.CreateBuffer(256, BufferUse.Uniform, "tri view"), true, "tri view");
        triTint = new DeviceBuffer(ctx, ctx.CreateBuffer(256, BufferUse.Uniform, "tri tint"), true, "tri tint");
        batch.Write(triVertices, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<float>(vertices)));
        batch.Write(triIndices, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<uint>(indices)));
        batch.Write(triView, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<float>(identity)));
        batch.Write(triFrame, 0, new byte[16]);                    // uTime = 0: the colours pass through
        batch.Write(triTint, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<float>([1, 1, 1, 1])));
        triTintSet = ctx.CreateSet(set2, [new GpuBinding(0, GpuBindingKind.UniformBuffer, Buffer: new GpuBufferBinding(triTint.Handle, 0))]);
    }
}

var camera = new WorldCamera { Target = Vector3.Zero, Yaw = 0.9f, Pitch = 0.15f, Distance = 20 };
var sun = SkyClock.Fallback.SunDirection(hour);
var coloursNow = SkyColours.For(sun);
Console.WriteLine($"colours   zenith {coloursNow.Zenith} horizon {coloursNow.Horizon} sun {coloursNow.SunColour} twilight {coloursNow.Twilight}");
Console.WriteLine($"device    {display.Device.Name}");
Console.WriteLine($"sun       {sun} at {hour}h");

int drawn = 0;
display.Window.Render += _ =>
{
    if (!ctx.BeginFrame()) return;
    var backbuffer = ctx.Backbuffer;
    var targets = PassTargets.Of(backbuffer, null) with { Colour = new RenderTarget(backbuffer.Attachment(), LoadOp.Clear, new ClearColour(0, 0, 0, 1)) };
    var cmd = ctx.Frame.Commands;
    cmd.BeginRendering(targets.Rendering);
    ctx.BeginHostPass(cmd, targets, DrawState.For(targets.Formats, deviceDepthClamp: false));

    // The sky at infinity: the view's translation is dropped, as the Vulkan path does (WorldFrame.DrawLoadingSky).
    if (!tri)
    {
        var size = display.Window.FramebufferSize;
        var aspect = size.X / (float)Math.Max(size.Y, 1);
        var view = camera.View;
        var rotation = view with { M41 = 0, M42 = 0, M43 = 0 };
        var viewProjection = rotation * camera.Projection(aspect, 1, 1000);
        var (colours, _) = sky.Prepare(sun, 0, 100000);
        sky.Draw(viewProjection, colours);
    }
    else
    {
        // The quad through the native vocabulary: the set 0 blocks (the flatten's collision test), set 2's tint with its
        // dynamic offset, the push constant, the interleaved vertex buffer and the index buffer.
        if (triPipeline.IsNull)
            triPipeline = ctx.Pipelines.Get(DrawState.For(targets.Formats, deviceDepthClamp: false)
                .Pipeline(triProgram, triLayout, PrimitiveType.Triangles, targets.Formats, "tri"));
        // The swap: an entry updated in the open frame is seen by the frame's draws (the table's contract).
        if (table && drawn == 3)
        {
            ctx.Bindless.Update(BindlessKind.Texture2D, 0, tableBlue!.Sampled());
            ctx.Bindless.Update(BindlessKind.Texture2D, 1, tableRed!.Sampled());
        }
        cmd.BindPipeline(triPipeline);
        if (table)
        {
            // The table: the two 2D entries by index, the other kinds' black dummies at 0 (the rewrite's five kinds).
            cmd.PushConstants(triProgram, GpuStage.Fragment, 0u);
            cmd.BindBindlessTable(triProgram, NativeShaders.BindlessSet);
            cmd.BindVertexBuffers(0, [new GpuBufferBinding(triVertices!.Handle, 0), new GpuBufferBinding(triVertices.Handle, 8)]);
            cmd.BindIndexBuffer(new GpuBufferBinding(triIndices!.Handle, 0), IndexKind.U32);
            cmd.DrawIndexed(6);
        }
        else
        {
            cmd.BindProgramResources(triProgram, 0,
            [
                new GpuBinding(0, GpuBindingKind.UniformBuffer, Buffer: new GpuBufferBinding(triFrame!.Handle, 0)),
                new GpuBinding(4, GpuBindingKind.UniformBuffer, Buffer: new GpuBufferBinding(triView!.Handle, 0)),
            ]);
            cmd.BindSets(triProgram, 2, new ReadOnlySpan<GpuSet>(in triTintSet), [0]);
            cmd.PushConstants(triProgram, GpuStage.Vertex, Vector2.Zero);
            cmd.BindVertexBuffers(0, [new GpuBufferBinding(triVertices!.Handle, 0), new GpuBufferBinding(triVertices.Handle, 8)]);   // the colour's offset in the binding, as GL's pointer does
            cmd.BindIndexBuffer(new GpuBufferBinding(triIndices!.Handle, 0), IndexKind.U32);
            cmd.DrawIndexed(6);
        }
    }

    ctx.EndHostPass(cmd);
    cmd.EndRendering();
    ctx.EndFrame();
    if (shot is { } path)
    {
        if (table && drawn == 2) SaveBmp(ctx, path);
        else if (table && drawn == 4) SaveBmp(ctx, (Path.ChangeExtension(path, null)) + ".swap.bmp");
        else if (!table && drawn == 4) SaveBmp(ctx, path);
    }
    if (++drawn >= frames) display.Window.Close();
};

display.Window.Run();
Console.WriteLine($"ok: {drawn} frames");

/// <summary>The backbuffer's picture as a BMP (rows bottom-up, BGRX).</summary>
static void SaveBmp(MetalContext ctx, string path)
{
    var backbuffer = ctx.Backbuffer;
    int w = backbuffer.Desc.Width, h = backbuffer.Desc.Height;
    var pixels = ctx.Readback(backbuffer);
    int stride = w * 4;
    var rows = new byte[54 + stride * h];
    rows[0] = (byte)'B'; rows[1] = (byte)'M';
    WriteInt(rows, 2, 54 + stride * h); WriteInt(rows, 10, 54); WriteInt(rows, 14, 40);
    WriteInt(rows, 18, w); WriteInt(rows, 22, h); rows[26] = 1; rows[28] = 32;
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int src = (h - 1 - y) * stride + x * 4, dst = 54 + y * stride + x * 4;
            rows[dst] = pixels[src + 2]; rows[dst + 1] = pixels[src + 1]; rows[dst + 2] = pixels[src]; rows[dst + 3] = 255;
        }
    File.WriteAllBytes(path, rows);
    Console.WriteLine($"shot      {path} ({w}x{h})");
}

static void WriteInt(byte[] b, int at, int value)
{
    b[at] = (byte)value; b[at + 1] = (byte)(value >> 8); b[at + 2] = (byte)(value >> 16); b[at + 3] = (byte)(value >> 24);
}
