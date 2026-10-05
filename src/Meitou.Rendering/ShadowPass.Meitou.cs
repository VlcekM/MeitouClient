using System.Diagnostics;
using System.Numerics;
using Meitou.Data.World;

using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The Meitou shadows (the <c>shadows</c> enhancement, docs/formats/shadows.md "Meitou shadows"): the same atlas and caster draws as the
/// game's CSM, but cascades fitted over the visible depths (<see cref="MeitouShadowFit"/>), drawn on a staggered schedule (the first every
/// frame, the second every other frame, the last two every fourth, each kept with the matrices it was drawn with), a soft receiver with
/// cascade blending and a fade at the range's end, contact-hardening penumbrae from a half-resolution blocker map, and the terrain's own
/// shadow over the whole world beyond the range (<see cref="TerrainShadowMap"/>).
/// </summary>
public sealed unsafe partial class ShadowPass
{
    /// <summary>The <c>shadows</c> switch: Meitou (true, the default) or the game's CSM.</summary>
    public bool Meitou { get; set; } = true;
    /// <summary>A temporal anti-aliasing or upscaling pass runs: the filter's noise changes every frame (it is static otherwise).</summary>
    public bool Temporal { get; set; }
    /// <summary>Contact-hardening penumbrae (the blocker search); off: a fixed small filter.</summary>
    public bool ContactHardening { get; set; } = true;
    /// <summary>The sun's angular diameter the penumbrae are made for (radians): a little more than the real sun's 0.53°.</summary>
    public float SunAngle { get; set; } = 0.7f * MathF.PI / 180;
    /// <summary>Filter radius without a blocker search, and the least with one, in texels of the cascade.</summary>
    public float FilterTexels { get; set; } = 1.0f;
    /// <summary>The widest penumbra radius in world units (a tall caster far above the ground); never less than <see cref="FilterTexels"/>.</summary>
    public float MaxPenumbra { get; set; } = 3;
    /// <summary>A sudden turn of the sun larger than this (radians) redraws every cascade at once.</summary>
    public float SunJump { get; set; } = 2 * MathF.PI / 180;

    /// <summary>How often each cascade was drawn (Meitou), for the log.</summary>
    public readonly int[] CascadeDraws = new int[4];
    public int MeitouFrames { get; private set; }

    readonly ShadowCascade?[] stored = new ShadowCascade?[4];
    float[]? storedSplits;
    Vector3 storedSun;
    int storedMapSize, meitouFrame;
    bool meitouValid;
    uint meitouUbo;
    ushort[]? coarse;
    int coarseSize;
    TerrainShadowMap? terrainMap;
    uint blockerTexture, blockerFbo, blockerProgram, blockerVao;
    int blockerSize, blockerAtlasU = -1, blockerSizeU = -1;

    /// <summary>The whole-world height grid (WorldScene.Coarse) for the terrain shadow beyond the range; uploaded when first needed.</summary>
    public void SetTerrain(ushort[] heights, int size) => (coarse, coarseSize) = (heights, size);

    /// <summary>One line on the schedule: how often each cascade was drawn, and the terrain map's rebuilds.</summary>
    public string DescribeMeitou() =>
        $"drawn {string.Join("/", CascadeDraws)} times in {MeitouFrames} frames; terrain shadow rebuilt {terrainMap?.Builds ?? 0} times (cpu {terrainMap?.LastBuildCpuMs ?? 0:0.00} ms)";

    void RenderMeitou(ShadowView view, Vector3 toSun, uint restoreFramebuffer, int restoreWidth, int restoreHeight, CasterDraw draw)
    {
        var watch = Stopwatch.StartNew();
        Array.Clear(PhaseMs);
        Resize(Settings.MapSize);
        int count = Math.Min(Settings.Cascades, 4);
        float near = MeitouShadowFit.QuantizedNear(view.Near);
        var splits = MeitouShadowFit.Splits(near, Math.Max(Settings.Range, near * 4), count);
        bool all = !meitouValid || storedSplits is null || !splits.AsSpan().SequenceEqual(storedSplits) || storedMapSize != Settings.MapSize
            || Vector3.Dot(toSun, storedSun) < MathF.Cos(SunJump);
        int frame = meitouFrame++;
        MeitouFrames++;
        Span<bool> drawNow = stackalloc bool[4];
        int drawing = 0;
        for (int i = 0; i < count; i++)
        {
            // The first cascade every frame, the second every other, the third and fourth every fourth frame on alternate frames.
            bool due = i == 0 || (i == 1 ? frame % 2 == 0 : frame % 4 == (i == 2 ? 1 : 3));
            drawNow[i] = all || stored[i] is null || due
                || !MeitouShadowFit.Covers(stored[i]!, view, splits, SearchRadius(stored[i]!) + 2 * stored[i]!.Texel);
            if (drawNow[i]) drawing++;
        }

        int timed = -1;
        if (!pending[slot]) { timed = slot; gl.QueryCounter(queries[timed * 2], QueryCounterTarget.Timestamp); }
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        gl.Viewport(0, 0, (uint)atlasSize, (uint)atlasSize);
        gl.DepthMask(true);
        gl.ColorMask(false, false, false, false);
        gl.ClearDepth(1.0);
        gl.Disable(EnableCap.Blend);
        if (drawing == count)
        {
            gl.Disable(EnableCap.ScissorTest);
            gl.Clear(ClearBufferMask.DepthBufferBit);
        }
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Less);
        gl.Enable(EnableCap.DepthClamp);
        gl.Enable(EnableCap.ScissorTest);
        gl.BindBufferBase(BufferTargetARB.UniformBuffer, ShadowShaders.CasterBinding, casterUbo);
        for (int i = 0; i < count; i++)
        {
            if (!drawNow[i]) continue;
            var c = MeitouShadowFit.Fit(view, toSun, Settings, splits, i);
            int x = (int)MathF.Round(c.Tile.X * atlasSize), y = (int)MathF.Round(c.Tile.Y * atlasSize), s = Settings.TileSize;
            gl.Viewport(x, y, (uint)s, (uint)s);
            gl.Scissor(x, y, (uint)s, (uint)s);
            if (drawing != count) gl.Clear(ClearBufferMask.DepthBufferBit);   // only this tile: the others keep what they hold
            var bias = new Vector4(c.FixedBias, KenshiShadows.SlopeBias, KenshiShadows.MaxSlopeBias, 0);
            gl.BindBuffer(BufferTargetARB.UniformBuffer, casterUbo);
            gl.BufferSubData(BufferTargetARB.UniformBuffer, 0, 16, &bias);
            draw(c, c.WorldToClip(), c.CullPlanes(), view.Eye);
            stored[i] = c;
            CascadeDraws[i]++;
        }
        gl.BindBuffer(BufferTargetARB.UniformBuffer, 0);
        gl.Disable(EnableCap.ScissorTest);
        gl.Disable(EnableCap.DepthClamp);
        gl.ColorMask(true, true, true, true);
        gl.DepthFunc(DepthFunction.Lequal);
        bool blockers = ContactHardening && UpdateBlockers(drawNow, count);
        if (coarse is not null)
        {
            terrainMap ??= new TerrainShadowMap(gl, coarse, coarseSize);
            terrainMap.Update(toSun);
        }
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, restoreFramebuffer);
        gl.Viewport(0, 0, (uint)restoreWidth, (uint)restoreHeight);
        if (timed >= 0)
        {
            gl.QueryCounter(queries[timed * 2 + 1], QueryCounterTarget.Timestamp);
            pending[timed] = true;
            slot = (slot + 1) % 2;
        }
        if (all) { storedSun = toSun; storedSplits = splits; storedMapSize = Settings.MapSize; }
        meitouValid = true;
        var cascades = new ShadowCascade[count];
        for (int i = 0; i < count; i++) cascades[i] = stored[i]!;
        Cascades = cascades;
        PublishMeitou(view, cascades, toSun, blockers);
        CpuMs = watch.Elapsed.TotalMilliseconds;
        cpuSamples.Add(CpuMs);
    }

    /// <summary>Rebuilds the blocker map's tiles of the cascades drawn this frame (the atlas read raw: its comparison off meanwhile).</summary>
    bool UpdateBlockers(ReadOnlySpan<bool> drawn, int count)
    {
        int size = atlasSize / 2;
        bool fresh = false;   // a new map: every tile
        if (blockerTexture == 0 || blockerSize != size)
        {
            if (blockerTexture != 0) { gl.DeleteFramebuffer(blockerFbo); gl.DeleteTexture(blockerTexture); }
            blockerSize = size;
            blockerTexture = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, blockerTexture);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R32f, (uint)size, (uint)size, 0, PixelFormat.Red, PixelType.Float, null);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            blockerFbo = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, blockerFbo);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, blockerTexture, 0);
            if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
                throw new InvalidOperationException("Shadow blocker framebuffer incomplete.");
            fresh = true;
        }
        if (blockerProgram == 0)
        {
            blockerProgram = WorldGl.Program(gl, ShadowShaders.FullscreenVertex, MeitouShadowShaders.BlockerFragment);
            blockerVao = gl.GenVertexArray();
            blockerAtlasU = gl.GetUniformLocation(blockerProgram, "uAtlas");
            blockerSizeU = gl.GetUniformLocation(blockerProgram, "uAtlasSize");
        }
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, blockerFbo);
        gl.Disable(EnableCap.DepthTest);
        gl.DepthMask(false);
        gl.Disable(EnableCap.CullFace);
        gl.UseProgram(blockerProgram);
        gl.BindVertexArray(blockerVao);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, atlas);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.None);
        gl.Uniform1(blockerAtlasU, 0);
        gl.Uniform1(blockerSizeU, (float)atlasSize);
        int tile = Settings.TileSize / 2, grid = Settings.Grid;
        for (int i = 0; i < count; i++)
        {
            if (!drawn[i] && !fresh) continue;
            gl.Viewport(i % grid * tile, i / grid * tile, (uint)tile, (uint)tile);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        gl.BindVertexArray(0);
        gl.DepthMask(true);
        gl.Enable(EnableCap.DepthTest);
        return true;
    }

    /// <summary>The blocker search's and the filter's widest radius in a cascade (world units).</summary>
    double SearchRadius(ShadowCascade c) => Math.Max(MaxPenumbra, FilterTexels * 1.5 * c.Texel);

    void PublishMeitou(ShadowView view, ShadowCascade[] cascades, Vector3 toSun, bool blockers)
    {
        var data = new float[ReceiverBytes / 4];
        var origin = view.Eye;
        var ms = new float[MeitouShadowShaders.BlockBytes / 4];
        int tile = Settings.TileSize;
        float range = cascades[^1].FarDepth;
        float sunTan = MathF.Tan(SunAngle * 0.5f);
        for (int i = 0; i < cascades.Length; i++)
        {
            var c = cascades[i];
            float world = c.Size.X;   // world units per unit of tile UV
            Put(data, i * 16, c.OriginToTile(origin));
            Put(data, 64 + i * 4, c.Tile);
            Put(data, 80 + i * 4, new Vector4(c.FarDepth, c.FilterRadius, c.Size.X, c.Size.Y));
            Put(data, 96 + i * 4, new Vector4(c.Size.Z, 1f / tile, 0, 0));
            Put(ms, i * 4, new Vector4(c.NearDepth, c.FarDepth, FilterTexels / tile, (float)c.Texel));
            Put(ms, 16 + i * 4, new Vector4(world, c.Size.Z, (float)(SearchRadius(c) / world), sunTan / world));
        }
        Put(data, 112, cascades[0].Rotation);
        Put(data, 128, new Vector4(origin, 1));
        Put(data, 132, new Vector4(Vector3.Normalize(view.Forward), cascades.Length));
        Put(data, 136, new Vector4(atlasSize, 0, 0, 1));   // w: the Meitou receiver
        float noise = Temporal ? meitouFrame % 64 * 5.588238f : 0;
        Put(ms, 32, new Vector4(noise, range, range * 0.85f, range * 0.55f));
        bool terrain = terrainMap?.Texture is > 0;
        float half = WorldLayout.HalfWorldSize;
        if (terrainMap is { } map)
        {
            float horizontal = MathF.Sqrt(toSun.X * toSun.X + toSun.Z * toSun.Z);
            float cosElevation = Math.Max(horizontal, 0.05f);
            Put(ms, 36, new Vector4(-half, -half, 1 / map.Spacing, terrain ? 1 : 0));
            // The penumbra's height across: occluder distance × the sun's angular radius over the cosine of its height.
            Put(ms, 40, new Vector4(map.Size, 20, 6, sunTan / cosElevation));
        }
        Put(ms, 44, new Vector4(blockers ? 1 : 0, range * 0.9f, 0, 0));
        Upload(data);
        if (meitouUbo == 0)
        {
            meitouUbo = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.UniformBuffer, meitouUbo);
            gl.BufferData(BufferTargetARB.UniformBuffer, MeitouShadowShaders.BlockBytes, null, BufferUsageARB.DynamicDraw);
        }
        gl.BindBuffer(BufferTargetARB.UniformBuffer, meitouUbo);
        fixed (float* p = ms) gl.BufferSubData(BufferTargetARB.UniformBuffer, 0, (nuint)(ms.Length * 4), p);
        gl.BindBuffer(BufferTargetARB.UniformBuffer, 0);
        gl.BindBufferBase(BufferTargetARB.UniformBuffer, MeitouShadowShaders.Binding, meitouUbo);
        if (terrain)
        {
            gl.ActiveTexture(TextureUnit.Texture0 + MeitouShadowShaders.TerrainUnit);
            gl.BindTexture(TextureTarget.Texture2D, terrainMap!.Texture);
        }
        if (blockers)
        {
            gl.ActiveTexture(TextureUnit.Texture0 + MeitouShadowShaders.BlockerUnit);
            gl.BindTexture(TextureTarget.Texture2D, blockerTexture);
        }
        gl.ActiveTexture(TextureUnit.Texture0);
    }

    void DisposeMeitou()
    {
        terrainMap?.Dispose();
        if (blockerTexture != 0) { gl.DeleteFramebuffer(blockerFbo); gl.DeleteTexture(blockerTexture); }
        if (blockerProgram != 0) { gl.DeleteProgram(blockerProgram); gl.DeleteVertexArray(blockerVao); }
        if (meitouUbo != 0) gl.DeleteBuffer(meitouUbo);
    }
}
