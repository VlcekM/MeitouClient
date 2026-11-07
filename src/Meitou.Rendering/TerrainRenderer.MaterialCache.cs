using System.Numerics;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>The terrain material cache's switch (docs/render-terrain.md "Material cache"): the A/B <c>terrain-material-cache</c> (B: every pixel shades the layers);
/// <c>MEITOU_DIAG_NO_MATERIAL_CACHE=1</c> starts it off.</summary>
public static class TerrainMaterialCache
{
    public static bool Enabled { get; set; } = !GpuContext.DiagNoMaterialCache;
}

/// <summary>
/// The material cache (docs/render-terrain.md "Material cache"): the terrain's layered material (the biomes' layers blended, tinted, with their normal maps)
/// baked into world-space levels round the eye, so a patch pixel near the eye reads two textures instead of shading up to five biomes of up to 14 layer
/// samples. <see cref="CacheLevels"/> levels of <see cref="CacheSide"/>² texels, level k's texel <see cref="CacheTexel"/> × 2^k, each addressed
/// toroidally: as the eye moves a level keeps its texels and bakes the strips it gains (every frame, as needed; it is built again only when it moved a
/// whole side). Besides those, a level's first bake gets <see cref="CacheBuildRows"/> rows a frame (until it is whole its pixels shade the layers;
/// fewer and a moving eye outran it), and a refresh after the material's inputs changed (<see cref="CacheKey"/>: a biome loaded, a pair's near slot,
/// the overlay window, the arrays) <see cref="CacheRows"/> rows, the finest level first, drawn from meanwhile.
/// </summary>
public sealed unsafe partial class TerrainRenderer
{
    const int CacheLevels = 4, CacheSide = 2048, CacheStep = 64, CacheRows = 64, CacheBuildRows = 256;
    const float CacheTexel = 0.25f;

    GpuTexture? cacheAlbedo, cacheNormal;
    uint cacheAlbedoIndex = uint.MaxValue, cacheNormalIndex = uint.MaxValue;
    TerrainProgram? cacheProgram;
    GpuPipeline? cachePipeline;
    bool cacheRefused;
    // Per level: the origin drawn from (w 0: not valid); a first bake's origin and rows done (-1: none running); a refresh's next row and rows left.
    readonly Vector4[] cacheOrigin = new Vector4[CacheLevels];
    readonly Vector2[] cacheTarget = new Vector2[CacheLevels];
    readonly int[] cacheBuilt = [-1, -1, -1, -1];
    readonly int[] cacheCursor = new int[CacheLevels], cacheStale = new int[CacheLevels];
    int cacheKey;
    float cacheReach;

    static long CacheBytes => 2L * CacheLevels * CacheSide * CacheSide * 4;

    /// <summary>Bakes what the cache levels need this frame round <paramref name="eye"/> (before the scene, outside a pass).</summary>
    public void RecordMaterialCache(Vector3 eye)
    {
        // Switched off, the levels keep what they hold (an A/B alternation finds them whole); the strips catch up when it is back on.
        if (!TerrainMaterialCache.Enabled) return;
        if (cacheRefused || textures is not { HasBiomes: true, MapState: 2 } t || frame.Options is not { Textures: true })
        {
            Array.Clear(cacheOrigin);
            Array.Fill(cacheBuilt, -1);
            return;
        }
        if (cacheAlbedo is null && !CreateCache(t)) return;

        PrepareConstants(material: true, patches: true);
        var c = constants;
        // The bake has no march (its program is built without it); uParallax gives the relief height's depth.
        (c.Displace, c.FarStart, c.FarEnd, c.Debug, c.Grid, c.InteriorCut, c.BloodCount, c.FieldCount, c.BakeLevels) = (0, 1e9f, 2e9f, 0, 0, 0, 0, 0, 0);
        int key = CacheKey(in c, t.MaterialVersion);
        if (key != cacheKey)
        {
            // The inputs changed: a level being built starts again, a whole one is refreshed row by row while it is drawn from.
            cacheKey = key;
            for (int k = 0; k < CacheLevels; k++)
            {
                if (cacheBuilt[k] >= 0) cacheBuilt[k] = 0;
                cacheStale[k] = CacheSide;
            }
            // Within this the biomes' distance fade (far = distance / fade distance − 0.3) has not begun: the bake has none.
            cacheReach = 0.3f * t.Resident.Select(b => b.FadeDistance).Where(f => f > 0).DefaultIfEmpty(float.MaxValue).Min();
        }

        IGpuCommandList? cmd = null;
        Span<(int X, int Y, int W, int H)> rects = stackalloc (int, int, int, int)[9];
        int budget = CacheRows, build = CacheBuildRows;
        for (int k = 0; k < CacheLevels; k++)
        {
            float texel = CacheTexel * (1 << k), side = texel * CacheSide, step = texel * CacheStep;
            var want = new Vector2(MathF.Floor((eye.X - side / 2) / step) * step, MathF.Floor((eye.Z - side / 2) / step) * step);
            int n = 0;
            var current = cacheOrigin[k];
            if (current.W > 0)
            {
                var have = new Vector2(current.X, current.Y);
                if (MathF.Abs(want.X - have.X) >= side || MathF.Abs(want.Y - have.Y) >= side)
                {
                    // Nothing of it is still in place (a jump): built again.
                    cacheOrigin[k] = default;
                    cacheBuilt[k] = -1;
                }
                else
                {
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
                    cacheOrigin[k] = new Vector4(want, texel, CacheSide);
                    if (budget > 0 && cacheStale[k] > 0)
                    {
                        int rows = Math.Min(Math.Min(budget, cacheStale[k]), CacheSide - cacheCursor[k]);
                        rects[n++] = (0, cacheCursor[k], CacheSide, rows);
                        cacheCursor[k] = (cacheCursor[k] + rows) % CacheSide;
                        cacheStale[k] -= rows;
                        budget -= rows;
                    }
                    if (n > 0) Bake(ref cmd, k, want, texel, ref c, rects[..n]);
                    continue;
                }
            }
            if (build <= 0) continue;
            // A first bake, towards the origin it started with; the strips catch up once it is whole.
            if (cacheBuilt[k] < 0) { cacheBuilt[k] = 0; cacheTarget[k] = want; }
            int chunk = Math.Min(build, CacheSide - cacheBuilt[k]);
            rects[0] = (0, cacheBuilt[k], CacheSide, chunk);
            cacheBuilt[k] += chunk;
            build -= chunk;
            Bake(ref cmd, k, cacheTarget[k], texel, ref c, rects[..1]);
            if (cacheBuilt[k] >= CacheSide)
            {
                cacheBuilt[k] = -1;
                cacheStale[k] = 0;
                cacheOrigin[k] = new Vector4(cacheTarget[k], texel, CacheSide);
                Console.WriteLine($"terrain   material cache level {k} baked ({texel * CacheSide / 10:0} m square round {cacheTarget[k].X + side / 2:0}, {cacheTarget[k].Y + side / 2:0})");
            }
        }
        if (cmd is not null)
        {
            cmd.Order();
            gpu.EndNative(cmd);
        }
    }

    bool CreateCache(TerrainTextures t)
    {
        if (t.Guard is { } guard && !guard.Allows((ulong)CacheBytes))
        {
            cacheRefused = true;
            Console.WriteLine($"terrain   material cache not made: memory budget (VramGuard, {CacheBytes >> 20} MB)");
            return false;
        }
        var desc = new TextureDesc(InternalFormat.Rgba8, CacheSide, CacheSide, Layers: CacheLevels, Kind: TextureKind.Texture2DArray,
            Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "terrain material cache albedo");
        cacheAlbedo = gpu.CreateTexture(desc);
        cacheNormal = gpu.CreateTexture(desc with { Name = "terrain material cache normal" });
        var sampler = gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.Repeat,
            TextureWrapMode.Repeat, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 8, false, 0));
        cacheAlbedoIndex = gpu.Bindless.Register(BindlessKind.Texture2DArray, new SampledTexture(sampler, cacheAlbedo.View()));
        cacheNormalIndex = gpu.Bindless.Register(BindlessKind.Texture2DArray, new SampledTexture(sampler, cacheNormal.View()));
        GpuSetLayout[] sets = [nativeFrame.SetLayout, GpuSetLayout.Bindless, constantsLayout];
        cacheProgram = new TerrainProgram(gpu, sets, TerrainShaders.MaterialBakeVertexNative(), TerrainShaders.MaterialBakeFragmentNative(), "terrain material bake");
        return true;
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

    /// <summary>Bakes <paramref name="rects"/> of level <paramref name="level"/> for its origin <paramref name="origin"/>: albedo and normal in one pass.</summary>
    void Bake(ref IGpuCommandList? cmd, int level, Vector2 origin, float texel, ref TerrainConstants c, ReadOnlySpan<(int X, int Y, int W, int H)> rects)
    {
        cmd ??= gpu.BeginNative("terrain material cache");
        c.BakeOrigin[level] = new Vector4(origin, texel, CacheSide);
        var p = cacheProgram!;
        var t = PassTargets.Of(cacheAlbedo, null, cacheNormal);
        t = t with
        {
            Colour = new RenderTarget(cacheAlbedo!.Attachment(0, level), LoadOp.Load, default),
            Extra = new RenderTarget(cacheNormal!.Attachment(0, level), LoadOp.Load, default),
        };
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
        var push = new TerrainBakePush { Pass = level };
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

    /// <summary>What the baked material depends on besides the place: the textures and maps by their bindless indices, the overlay window, the
    /// biomes' parameter rows (<see cref="TerrainTextures.MaterialVersion"/>: residency and near slots), the switches.</summary>
    static int CacheKey(in TerrainConstants c, int version)
    {
        var h = new HashCode();
        h.Add(c.Region); h.Add(c.Diffuse); h.Add(c.Normal); h.Add(c.DiffuseFar); h.Add(c.NormalFar); h.Add(c.FarLod); h.Add(c.Params); h.Add(c.Cells);
        h.Add(c.BlendMap); h.Add(c.Overlay); h.Add(c.Colour); h.Add(c.Ground); h.Add(c.WorldColour); h.Add(c.MapState); h.Add(c.NormalMaps);
        h.Add(c.Textured); h.Add(c.CliffEps); h.Add(c.HasGround); h.Add(c.HasWorldColour); h.Add(version);
        h.Add(c.Height); h.Add(c.Relief); h.Add(c.OverlayRelief); h.Add(c.MaxDepth); h.Add(c.TargetSlope); h.Add(c.ScaleFollow); h.Add(c.Parallax); h.Add(c.CliffScale);   // the baked relief height
        return h.ToHashCode();
    }

    /// <summary>The cache's levels for the patches' colour programs (<see cref="PrepareConstants"/>); none in the texture field (its tiles are not blended) or a debug view.</summary>
    void ApplyMaterialCache(ref TerrainConstants c)
    {
        if (!TerrainMaterialCache.Enabled || cacheAlbedo is null || c.FieldCount > 0 || c.Debug != 0) return;
        bool any = false;
        for (int k = 0; k < CacheLevels; k++) { c.BakeOrigin[k] = cacheOrigin[k]; any |= cacheOrigin[k].W > 0; }
        if (!any) return;
        (c.BakeAlbedo, c.BakeNormal, c.BakeLevels, c.BakeReach) = (cacheAlbedoIndex, cacheNormalIndex, CacheLevels, cacheReach);
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
