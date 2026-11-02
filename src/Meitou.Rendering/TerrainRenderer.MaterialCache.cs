using System.Numerics;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>The terrain material cache's switch (docs/render-terrain.md "Material cache"): the A/B <c>terrain-material-cache</c>.</summary>
public static class TerrainMaterialCache
{
    public static bool Enabled { get; set; } = true;
}

/// <summary>
/// The material cache (docs/render-terrain.md "Material cache"): the terrain's layered material (the biomes' layers blended, tinted, with their normal maps)
/// baked into world-space levels round the eye, so a patch pixel near the eye reads two textures instead of shading up to five biomes of up to 14 layer
/// samples. <see cref="CacheLevels"/> levels of <see cref="CacheSide"/>² texels, level k's texel <see cref="CacheTexel"/> × 2^k, each addressed
/// toroidally: as the eye moves a level keeps its texels and bakes the strips it gains. A level is baked whole in chunks of <see cref="CacheRows"/> rows a
/// frame when first needed and whenever the material's inputs change (<see cref="CacheKey"/>); until it is whole the pixels shade the layers.
/// </summary>
public sealed unsafe partial class TerrainRenderer
{
    const int CacheLevels = 4, CacheSide = 2048, CacheStep = 64, CacheRows = 256;
    const float CacheTexel = 0.25f;

    GpuTexture? cacheAlbedo, cacheNormal;
    uint cacheAlbedoIndex = uint.MaxValue, cacheNormalIndex = uint.MaxValue;
    TerrainProgram? cacheProgram;
    GpuPipeline? cachePipeline;
    // Per level: the origin drawn from (w 0: not valid), the origin a whole bake works towards and the rows it has done (-1: none running).
    readonly Vector4[] cacheOrigin = new Vector4[CacheLevels];
    readonly Vector2[] cacheTarget = new Vector2[CacheLevels];
    readonly int[] cacheRows = [-1, -1, -1, -1];
    int cacheKey;

    /// <summary>Bakes what the cache levels need this frame round <paramref name="eye"/> (before the scene, outside a pass).</summary>
    public void RecordMaterialCache(Vector3 eye)
    {
        if (!TerrainMaterialCache.Enabled || textures is not { HasBiomes: true, MapState: 2 } || frame.Options is not { Textures: true })
        {
            Array.Clear(cacheOrigin);
            return;
        }
        if (cacheAlbedo is null)
        {
            var desc = new TextureDesc(InternalFormat.Rgba8, CacheSide, CacheSide, Layers: CacheLevels, Kind: TextureKind.Texture2DArray,
                Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "terrain material cache albedo");
            cacheAlbedo = gpu.CreateTexture(desc);
            cacheNormal = gpu.CreateTexture(desc with { Name = "terrain material cache normal" });
            var sampler = gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.Repeat,
                TextureWrapMode.Repeat, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 8, false, 0));
            cacheAlbedoIndex = gpu.Bindless.Register(BindlessKind.Texture2DArray, new SampledTexture(sampler, cacheAlbedo.View()));
            cacheNormalIndex = gpu.Bindless.Register(BindlessKind.Texture2DArray, new SampledTexture(sampler, cacheNormal!.View()));
            GpuSetLayout[] sets = [nativeFrame.SetLayout, GpuSetLayout.Bindless, constantsLayout];
            cacheProgram = new TerrainProgram(gpu, sets, TerrainShaders.MaterialBakeVertexNative(), TerrainShaders.MaterialBakeFragmentNative(), "terrain material bake");
        }

        PrepareConstants(material: true, patches: true);
        var c = constants;
        c.Parallax = 0;
        c.Displace = 0;
        c.FarStart = 1e9f;
        c.FarEnd = 2e9f;
        c.Debug = 0;
        c.Grid = 0;
        c.InteriorCut = 0;
        c.BloodCount = 0;
        c.FieldCount = 0;
        c.BakeLevels = 0;
        int key = CacheKey(in c);
        if (key != cacheKey)
        {
            cacheKey = key;
            Array.Clear(cacheOrigin);
            Array.Fill(cacheRows, -1);
        }

        IGpuCommandList? cmd = null;
        Span<(int X, int Y, int W, int H)> rects = stackalloc (int, int, int, int)[8];
        for (int k = 0; k < CacheLevels; k++)
        {
            float texel = CacheTexel * (1 << k), side = texel * CacheSide, step = texel * CacheStep;
            var want = new Vector2(MathF.Floor((eye.X - side / 2) / step) * step, MathF.Floor((eye.Z - side / 2) / step) * step);
            int n = 0;
            Vector2 bakeOrigin;
            var current = cacheOrigin[k];
            if (current.W > 0)
            {
                var have = new Vector2(current.X, current.Y);
                if (have == want) continue;
                if (MathF.Abs(want.X - have.X) >= side / 2 || MathF.Abs(want.Y - have.Y) >= side / 2)
                {
                    cacheOrigin[k] = default;
                    cacheRows[k] = -1;
                    continue;
                }
                // The strips the level gains: along x, then along z, each as texel ranges of the wrapped image.
                if (want.X != have.X)
                {
                    float x0 = want.X > have.X ? have.X + side : want.X, x1 = want.X > have.X ? want.X + side : have.X;
                    n = Wrapped(rects, n, x0, x1, want.Y, want.Y + side, texel);
                }
                if (want.Y != have.Y)
                {
                    float z0 = want.Y > have.Y ? have.Y + side : want.Y, z1 = want.Y > have.Y ? want.Y + side : have.Y;
                    n = Wrapped(rects, n, want.X, want.X + side, z0, z1, texel);
                }
                bakeOrigin = want;
                cacheOrigin[k] = new Vector4(want, texel, CacheSide);
            }
            else
            {
                // A whole bake, CacheRows rows a frame, towards the origin it started with; the strips catch up once it is whole.
                if (cacheRows[k] < 0) { cacheRows[k] = 0; cacheTarget[k] = want; }
                rects[n++] = (0, cacheRows[k], CacheSide, CacheRows);
                cacheRows[k] += CacheRows;
                bakeOrigin = cacheTarget[k];
                if (cacheRows[k] >= CacheSide)
                {
                    cacheRows[k] = -1;
                    cacheOrigin[k] = new Vector4(bakeOrigin, texel, CacheSide);
                }
            }
            cmd ??= gpu.BeginNative("terrain material cache");
            c.BakeOrigin[k] = new Vector4(bakeOrigin, texel, CacheSide);
            for (int pass = 0; pass < 2; pass++) BakeRects(cmd, pass == 0 ? cacheAlbedo! : cacheNormal!, k, pass, in c, rects[..n]);
        }
        if (cmd is not null)
        {
            cmd.Order();
            gpu.EndNative(cmd);
        }
    }

    /// <summary>The world rectangle [x0, x1) × [z0, z1) as texel rectangles of a level's wrapped image (up to four).</summary>
    static int Wrapped(Span<(int X, int Y, int W, int H)> rects, int n, float x0, float x1, float z0, float z1, float texel)
    {
        int tx0 = (int)MathF.Round(x0 / texel), tx1 = (int)MathF.Round(x1 / texel), tz0 = (int)MathF.Round(z0 / texel), tz1 = (int)MathF.Round(z1 / texel);
        Span<(int A, int L)> xs = stackalloc (int, int)[2], zs = stackalloc (int, int)[2];
        int nx = Split(xs, tx0, tx1), nz = Split(zs, tz0, tz1);
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
                rects[n++] = (xs[i].A, zs[j].A, xs[i].L, zs[j].L);
        return n;

        static int Split(Span<(int, int)> parts, int t0, int t1)
        {
            int length = Math.Min(t1 - t0, CacheSide), start = ((t0 % CacheSide) + CacheSide) % CacheSide;
            if (start + length <= CacheSide) { parts[0] = (start, length); return 1; }
            parts[0] = (start, CacheSide - start);
            parts[1] = (0, length - (CacheSide - start));
            return 2;
        }
    }

    void BakeRects(IGpuCommandList cmd, GpuTexture target, int level, int pass, in TerrainConstants c, ReadOnlySpan<(int X, int Y, int W, int H)> rects)
    {
        var p = cacheProgram!;
        var t = PassTargets.Of(target, null);
        t = t with { Colour = new RenderTarget(target.Attachment(0, level), LoadOp.Load, default) };
        cmd.BeginRendering(t.Rendering);
        var state = DrawState.For(t.Formats, gpu.DepthClamp);
        state.Record(cmd, t);
        cmd.BindPipeline(cachePipeline ??= gpu.Pipelines.Get(state.Pipeline(p.P, new VertexLayout(), PrimitiveType.Triangles, t.Formats, "terrain material bake")));
        var view = new ViewConstants { ViewProjection = Matrix4x4.Identity, Eye = Vector3.Zero, LightDir = Vector3.UnitY, FogDistance = 0 };
        var binding = nativeFrame.Prepare(in view);
        NativeFrame.Record(cmd, p.P, in binding);
        var slice = gpu.Frame.WriteConstants(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<TerrainConstants>(in c)), uniformAlign);
        ulong key = (ulong)slice.Handle.Index;
        if (!constantSets.TryGetValue(key, out var set)) constantSets[key] = set = ConstantSet(slice.Handle);
        uint offset = (uint)slice.Offset;
        cmd.BindSets(p.P, TerrainShaders.ConstantsSet, new ReadOnlySpan<GpuSet>(in set), new ReadOnlySpan<uint>(in offset));
        var push = new TerrainBakePush { Pass = pass | level << 1 };
        cmd.PushConstants(p.P, GpuStage.Vertex | GpuStage.Fragment, in push);
        cmd.SetViewport(new PixelViewport(0, 0, CacheSide, CacheSide));
        foreach (var r in rects)
        {
            if (r.W <= 0 || r.H <= 0) continue;
            cmd.SetScissor(new PixelRect(r.X, r.Y, (uint)r.W, (uint)r.H));
            cmd.Draw(3, 1);
        }
        cmd.EndRendering();
    }

    /// <summary>What the baked material depends on besides the place: the textures and maps by their bindless indices, the overlay window, the switches.</summary>
    static int CacheKey(in TerrainConstants c)
    {
        var h = new HashCode();
        h.Add(c.Region); h.Add(c.Diffuse); h.Add(c.Normal); h.Add(c.DiffuseFar); h.Add(c.NormalFar); h.Add(c.FarLod); h.Add(c.Params); h.Add(c.Cells);
        h.Add(c.BlendMap); h.Add(c.Overlay); h.Add(c.Colour); h.Add(c.Ground); h.Add(c.WorldColour); h.Add(c.MapState); h.Add(c.NormalMaps);
        h.Add(c.Textured); h.Add(c.CliffEps); h.Add(c.HasGround); h.Add(c.HasWorldColour);
        return h.ToHashCode();
    }

    /// <summary>The cache's levels for the patches' colour programs (<see cref="PrepareConstants"/>).</summary>
    void ApplyMaterialCache(ref TerrainConstants c)
    {
        if (!TerrainMaterialCache.Enabled || cacheAlbedo is null) return;
        bool any = false;
        for (int k = 0; k < CacheLevels; k++) { c.BakeOrigin[k] = cacheOrigin[k]; any |= cacheOrigin[k].W > 0; }
        if (!any) return;
        (c.BakeAlbedo, c.BakeNormal, c.BakeLevels) = (cacheAlbedoIndex, cacheNormalIndex, CacheLevels);
    }

    void DisposeMaterialCache()
    {
        if (cacheAlbedoIndex != uint.MaxValue) gpu.Bindless.Free(BindlessKind.Texture2DArray, cacheAlbedoIndex);
        if (cacheNormalIndex != uint.MaxValue) gpu.Bindless.Free(BindlessKind.Texture2DArray, cacheNormalIndex);
        cacheAlbedo?.Dispose();
        cacheNormal?.Dispose();
        cacheProgram?.Dispose();
    }
}
