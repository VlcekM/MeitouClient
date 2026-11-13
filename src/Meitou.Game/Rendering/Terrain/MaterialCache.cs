using System.Numerics;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Terrain;

/// <summary>
/// The terrain material cache's switch (docs/render-terrain.md "Material cache"): the A/B <c>terrain-material-cache</c> (B: every pixel
/// shades the layers);
/// <c>MEITOU_DIAG_NO_MATERIAL_CACHE=1</c> starts it off.
/// </summary>
public static class TerrainMaterialCache
{
    /// <summary>
    /// The cache is used (false: every pixel shades the layers; the levels keep what they hold).
    /// </summary>
    public static bool Enabled { get; set; } = !GpuContext.DiagNoMaterialCache;
}

/// <summary>
/// The material cache (docs/render-terrain.md "Material cache"): the terrain's layered material (the biomes' layers blended, tinted, with their
/// normal maps) baked into world-space levels round the eye, so a patch pixel near the eye reads two textures instead of shading up to five biomes of
/// up to 14 layer samples. <see cref="CacheLevels"/> levels of <see cref="CacheSide"/>² texels, level k's texel <see cref="CacheTexel"/> × 2^k, each
/// addressed toroidally: as the eye moves a level keeps its texels and bakes the strips it gains (every frame, as needed; it is built again only when
/// it moved a whole side). Besides those, a level's first bake gets <see cref="CacheBuildRows"/> rows a frame (until it is whole its pixels shade the
/// layers; fewer and a moving eye outran it), and a refresh after the material's inputs changed (<see cref="CacheKey"/>: a biome loaded, a pair's
/// near slot, the overlay window, the arrays) <see cref="CacheRows"/> rows, the finest level first, drawn from meanwhile.
/// </summary>
sealed class MaterialCache : IDisposable
{
    /// <summary>
    /// The levels, their side in texels, the snap step of a level's origin in texels, the rows a refresh bakes a frame and the rows a first bake
    /// bakes a frame.
    /// </summary>
    const int CacheLevels = 4, CacheSide = 2048, CacheStep = 64, CacheRows = 64, CacheBuildRows = 256;

    /// <summary>
    /// Level 0's texel (world units); level k's is this × 2^k.
    /// </summary>
    const float CacheTexel = 0.25f;

    /// <summary>
    /// The levels' albedo and normal (2D arrays of RGBA8, one layer per level; null until the first <see cref="Record"/> with the biomes loaded).
    /// </summary>
    GpuTexture? _cacheAlbedo, _cacheNormal;

    /// <summary>
    /// Their bindless indices (<see cref="uint.MaxValue"/>: none).
    /// </summary>
    uint _cacheAlbedoIndex = uint.MaxValue, _cacheNormalIndex = uint.MaxValue;

    /// <summary>
    /// The bake program.
    /// </summary>
    TerrainProgram? _cacheProgram;

    /// <summary>
    /// Its pipeline (made on the first bake).
    /// </summary>
    GpuPipeline? _cachePipeline;

    /// <summary>
    /// The memory budget refused the levels (they are never made).
    /// </summary>
    bool _cacheRefused;

    /// <summary>
    /// Per level: the origin drawn from (x, z, texel, side; w 0: not valid).
    /// </summary>
    readonly Vector4[] _cacheOrigin = new Vector4[CacheLevels];

    /// <summary>
    /// Per level: the origin a first bake draws to.
    /// </summary>
    readonly Vector2[] _cacheTarget = new Vector2[CacheLevels];

    /// <summary>
    /// Per level: a first bake's rows done (-1: none running).
    /// </summary>
    readonly int[] _cacheBuilt = [-1, -1, -1, -1];

    /// <summary>
    /// Per level: a refresh's next row, and its rows left.
    /// </summary>
    readonly int[] _cacheCursor = new int[CacheLevels], _cacheStale = new int[CacheLevels];

    /// <summary>
    /// The <see cref="CacheKey"/> of the inputs the levels were baked from.
    /// </summary>
    int _cacheKey;

    /// <summary>
    /// Within this distance of the eye (0.3 × the least biome fade distance) the biomes' distance fade has not begun; the bake has none.
    /// </summary>
    float _cacheReach;

    /// <summary>
    /// The renderer whose material is baked: its textures, options and constants.
    /// </summary>
    readonly TerrainRenderer _terrain;

    /// <summary>
    /// The context the levels and their program are made on.
    /// </summary>
    readonly IGpuContext _gpu;

    /// <summary>
    /// The terrain's frame set, which the bake binds as set 0.
    /// </summary>
    readonly NativeFrame _nativeFrame;

    /// <summary>
    /// The terrain set's layout (set 2), for the bake program.
    /// </summary>
    readonly GpuSetLayout _constantsLayout;

    /// <summary>
    /// The alignment of a constants slice.
    /// </summary>
    readonly ulong _uniformAlign;

    /// <summary>
    /// A cache for <paramref name="terrain"/>'s material; the levels are made on the first <see cref="Record"/> that needs them.
    /// </summary>
    public MaterialCache(TerrainRenderer terrain, IGpuContext gpu, NativeFrame nativeFrame, GpuSetLayout constantsLayout, ulong uniformAlign)
    {
        _terrain = terrain;
        _gpu = gpu;
        _nativeFrame = nativeFrame;
        _constantsLayout = constantsLayout;
        _uniformAlign = uniformAlign;
    }

    /// <summary>
    /// The levels' bytes: albedo and normal, RGBA8, <see cref="CacheLevels"/> × <see cref="CacheSide"/>² each (128 MB).
    /// </summary>
    static long CacheBytes => 2L * CacheLevels * CacheSide * CacheSide * 4;

    /// <summary>
    /// Bakes what the cache levels need this frame round <paramref name="eye"/> (before the scene, outside a pass).
    /// </summary>
    public void Record(Vector3 eye)
    {
        // Switched off, the levels keep what they hold (an A/B alternation finds them whole); the strips catch up when it is back on.
        if (!TerrainMaterialCache.Enabled) return;
        if (_cacheRefused || _terrain.TextureSet is not { HasBiomes: true, MapState: 2 } t || _terrain.FrameOptions is not { Textures: true })
        {
            Array.Clear(_cacheOrigin);
            Array.Fill(_cacheBuilt, -1);
            return;
        }
        if (_cacheAlbedo is null && !CreateCache(t)) return;

        var c = _terrain.MaterialConstants();
        // The bake has no march (its program is built without it); uParallax gives the relief height's depth.
        (c.Displace, c.FarStart, c.FarEnd, c.Debug, c.Grid) = (0, 1e9f, 2e9f, 0, 0);
        (c.InteriorCut, c.BloodCount, c.FieldCount, c.BakeLevels) = (0, 0, 0, 0);
        int key = CacheKey(in c, t.MaterialVersion);
        if (key != _cacheKey) ResetCacheInputs(key, t);

        IGpuCommandList? cmd = null;
        Span<(int X, int Y, int W, int H)> rects = stackalloc (int, int, int, int)[9];
        int budget = CacheRows, build = CacheBuildRows;
        for (int k = 0; k < CacheLevels; k++)
            BakeLevel(k, eye, ref cmd, ref c, rects, ref budget, ref build);
        if (cmd is not null)
        {
            cmd.Order();
            _gpu.EndNative(cmd);
        }
    }

    /// <summary>
    /// The inputs changed: a level being built starts again, a whole one is refreshed row by row while it is drawn from.
    /// </summary>
    void ResetCacheInputs(int key, TerrainTextures t)
    {
        _cacheKey = key;
        for (int k = 0; k < CacheLevels; k++)
        {
            if (_cacheBuilt[k] >= 0) _cacheBuilt[k] = 0;
            _cacheStale[k] = CacheSide;
        }
        // Within this the biomes' distance fade (far = distance / fade distance − 0.3) has not begun: the bake has none.
        _cacheReach = 0.3f * t.Resident.Select(b => b.FadeDistance).Where(f => f > 0).DefaultIfEmpty(float.MaxValue).Min();
    }

    /// <summary>
    /// Bakes what level <paramref name="k"/> needs this frame: its strips when it moved, else its first bake's next chunk.
    /// </summary>
    void BakeLevel(int k, Vector3 eye, ref IGpuCommandList? cmd, ref TerrainConstants c, Span<(int X, int Y, int W, int H)> rects,
        ref int budget, ref int build)
    {
        float texel = CacheTexel * (1 << k), side = texel * CacheSide, step = texel * CacheStep;
        var want = new Vector2(MathF.Floor((eye.X - side / 2) / step) * step, MathF.Floor((eye.Z - side / 2) / step) * step);
        var current = _cacheOrigin[k];
        if (current.W > 0)
        {
            var have = new Vector2(current.X, current.Y);
            if (MathF.Abs(want.X - have.X) >= side || MathF.Abs(want.Y - have.Y) >= side)
            {
                // Nothing of it is still in place (a jump): built again.
                _cacheOrigin[k] = default;
                _cacheBuilt[k] = -1;
            }
            else
            {
                BakeStrips(k, want, have, texel, rects, ref cmd, ref c, ref budget);
                return;
            }
        }
        if (build <= 0) return;
        BakeFirstChunk(k, want, texel, rects, ref cmd, ref c, ref build);
    }

    /// <summary>
    /// The strips a level gained as it moved from <paramref name="have"/> to <paramref name="want"/>, and its share of the refresh rows.
    /// </summary>
    void BakeStrips(int k, Vector2 want, Vector2 have, float texel, Span<(int X, int Y, int W, int H)> rects, ref IGpuCommandList? cmd,
        ref TerrainConstants c, ref int budget)
    {
        float side = texel * CacheSide;
        int n = 0;
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
        _cacheOrigin[k] = new Vector4(want, texel, CacheSide);
        if (budget > 0 && _cacheStale[k] > 0)
        {
            int rows = Math.Min(Math.Min(budget, _cacheStale[k]), CacheSide - _cacheCursor[k]);
            rects[n++] = (0, _cacheCursor[k], CacheSide, rows);
            _cacheCursor[k] = (_cacheCursor[k] + rows) % CacheSide;
            _cacheStale[k] -= rows;
            budget -= rows;
        }
        if (n > 0) Bake(ref cmd, k, want, texel, ref c, rects[..n]);
    }

    /// <summary>
    /// The next chunk of a level's first bake (its rows done so far in <see cref="_cacheBuilt"/>); whole, the level is ready.
    /// </summary>
    void BakeFirstChunk(int k, Vector2 want, float texel, Span<(int X, int Y, int W, int H)> rects, ref IGpuCommandList? cmd,
        ref TerrainConstants c, ref int build)
    {
        // A first bake, towards the origin it started with; the strips catch up once it is whole.
        if (_cacheBuilt[k] < 0)
        {
            _cacheBuilt[k] = 0;
            _cacheTarget[k] = want;
        }
        int chunk = Math.Min(build, CacheSide - _cacheBuilt[k]);
        rects[0] = (0, _cacheBuilt[k], CacheSide, chunk);
        _cacheBuilt[k] += chunk;
        build -= chunk;
        Bake(ref cmd, k, _cacheTarget[k], texel, ref c, rects[..1]);
        if (_cacheBuilt[k] < CacheSide) return;
        _cacheBuilt[k] = -1;
        _cacheStale[k] = 0;
        _cacheOrigin[k] = new Vector4(_cacheTarget[k], texel, CacheSide);
        Vector2 target = _cacheTarget[k];
        float side = texel * CacheSide;
        string round = $"{target.X + side / 2:0}, {target.Y + side / 2:0}";
        Console.WriteLine($"terrain   material cache level {k} baked ({texel * CacheSide / 10:0} m square round {round})");

    }

    /// <summary>
    /// Makes the levels, their bindless entries and the bake program, unless the memory budget refuses them (then never again).
    /// </summary>
    bool CreateCache(TerrainTextures t)
    {
        if (t.Guard is { } guard && !guard.Allows((ulong)CacheBytes))
        {
            _cacheRefused = true;
            Console.WriteLine($"terrain   material cache not made: memory budget (VramGuard, {CacheBytes >> 20} MB)");
            return false;
        }
        var desc = new TextureDesc(InternalFormat.Rgba8, CacheSide, CacheSide, Layers: CacheLevels, Kind: TextureKind.Texture2DArray,
            Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "terrain material cache albedo");
        _cacheAlbedo = _gpu.CreateTexture(desc);
        _cacheNormal = _gpu.CreateTexture(desc with { Name = "terrain material cache normal" });
        var sampler = _gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.Repeat,
            TextureWrapMode.Repeat, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 8, false, 0));
        _cacheAlbedoIndex = _gpu.Bindless.Register(BindlessKind.Texture2DArray, new SampledTexture(sampler, _cacheAlbedo.View()));
        _cacheNormalIndex = _gpu.Bindless.Register(BindlessKind.Texture2DArray, new SampledTexture(sampler, _cacheNormal.View()));
        GpuSetLayout[] sets = [_nativeFrame.SetLayout, GpuSetLayout.Bindless, _constantsLayout];
        _cacheProgram = new TerrainProgram(_gpu, sets, TerrainShaders.MaterialBakeVertexNative(), TerrainShaders.MaterialBakeFragmentNative(),
            "terrain material bake");
        return true;
    }

    /// <summary>
    /// The world rectangle [x0, x1) × [z0, z1) as texel rectangles of a level's wrapped image (up to four).
    /// </summary>
    static int Wrapped(Span<(int X, int Y, int W, int H)> rects, int n, float x0, float x1, float z0, float z1, float texel)
    {
        int tx0 = (int)MathF.Round(x0 / texel), tx1 = (int)MathF.Round(x1 / texel), tz0 = (int)MathF.Round(z0 / texel),
            tz1 = (int)MathF.Round(z1 / texel);
        Span<(int A, int L)> xs = stackalloc (int, int)[2], zs = stackalloc (int, int)[2];
        int nx = Split(xs, tx0, tx1), nz = Split(zs, tz0, tz1);
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
                rects[n++] = (xs[i].A, zs[j].A, xs[i].L, zs[j].L);
        return n;

        static int Split(Span<(int, int)> parts, int t0, int t1)
        {
            int length = Math.Min(t1 - t0, CacheSide), start = ((t0 % CacheSide) + CacheSide) % CacheSide;
            if (start + length <= CacheSide)
            {
                parts[0] = (start, length);
                return 1;
            }
            parts[0] = (start, CacheSide - start);
            parts[1] = (0, length - (CacheSide - start));
            return 2;
        }
    }

    /// <summary>
    /// Bakes <paramref name="rects"/> of level <paramref name="level"/> for its origin <paramref name="origin"/>: albedo and normal in one pass.
    /// </summary>
    void Bake(ref IGpuCommandList? cmd, int level, Vector2 origin, float texel, ref TerrainConstants c,
        ReadOnlySpan<(int X, int Y, int W, int H)> rects)
    {
        cmd ??= _gpu.BeginNative("terrain material cache");
        c.BakeOrigin[level] = new Vector4(origin, texel, CacheSide);
        var p = _cacheProgram!;
        var t = PassTargets.Of(_cacheAlbedo, null, _cacheNormal);
        t = t with
        {
            Colour = new RenderTarget(_cacheAlbedo!.Attachment(0, level), LoadOp.Load, default),
            Extra = new RenderTarget(_cacheNormal!.Attachment(0, level), LoadOp.Load, default),
        };
        cmd.BeginRendering(t.Rendering);
        var state = DrawState.For(t.Formats, _gpu.DepthClamp);
        state.Record(cmd, t);
        cmd.BindPipeline(_cachePipeline ??=
            _gpu.Pipelines.Get(state.Pipeline(p.P, new VertexLayout(), PrimitiveType.Triangles, t.Formats, "terrain material bake")));
        var view = new ViewConstants { ViewProjection = Matrix4x4.Identity, Eye = Vector3.Zero, LightDir = Vector3.UnitY, FogDistance = 0 };
        var binding = _nativeFrame.Prepare(in view);
        NativeFrame.Record(cmd, p.P, in binding);
        var slice = _gpu.Frame.WriteConstants(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<TerrainConstants>(in c)),
            _uniformAlign);
        var set = _terrain.ConstantSetOf(slice.Handle);
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

    /// <summary>
    /// What the baked material depends on besides the place: the textures and maps by their bindless indices, the overlay window, the biomes'
    /// parameter rows (<see cref="TerrainTextures.MaterialVersion"/>: residency and near slots), the switches.
    /// </summary>
    static int CacheKey(in TerrainConstants c, int version)
    {
        var h = new HashCode();
        h.Add(c.Region);
        h.Add(c.Diffuse);
        h.Add(c.Normal);
        h.Add(c.DiffuseFar);
        h.Add(c.NormalFar);
        h.Add(c.FarLod);
        h.Add(c.Params);
        h.Add(c.Cells);
        h.Add(c.BlendMap);
        h.Add(c.Overlay);
        h.Add(c.Colour);
        h.Add(c.Ground);
        h.Add(c.WorldColour);
        h.Add(c.MapState);
        h.Add(c.NormalMaps);
        h.Add(c.Textured);
        h.Add(c.CliffEps);
        h.Add(c.HasGround);
        h.Add(c.HasWorldColour);
        h.Add(version);
        // The baked relief height.
        h.Add(c.Height);
        h.Add(c.Relief);
        h.Add(c.OverlayRelief);
        h.Add(c.MaxDepth);
        h.Add(c.TargetSlope);
        h.Add(c.ScaleFollow);
        h.Add(c.Parallax);
        h.Add(c.CliffScale);
        return h.ToHashCode();
    }

    /// <summary>
    /// The cache's levels for the patches' colour programs (<see cref="TerrainRenderer.MaterialConstants"/>); none in the texture field (its tiles
    /// are not blended) or a debug view.
    /// </summary>
    public void Apply(ref TerrainConstants c)
    {
        if (!TerrainMaterialCache.Enabled || _cacheAlbedo is null || c.FieldCount > 0 || c.Debug != 0) return;
        bool any = false;
        for (int k = 0; k < CacheLevels; k++)
        {
            c.BakeOrigin[k] = _cacheOrigin[k];
            any |= _cacheOrigin[k].W > 0;
        }
        if (!any) return;
        (c.BakeAlbedo, c.BakeNormal, c.BakeLevels, c.BakeReach, c.BakeBias) =
            (_cacheAlbedoIndex, _cacheNormalIndex, CacheLevels, _cacheReach, MathF.Min(_gpu.LodBias, 0));
    }

    /// <summary>
    /// Frees the levels, their bindless entries and the program.
    /// </summary>
    public void Dispose()
    {
        if (_cacheAlbedoIndex != uint.MaxValue) _gpu.Bindless.Free(BindlessKind.Texture2DArray, _cacheAlbedoIndex);
        if (_cacheNormalIndex != uint.MaxValue) _gpu.Bindless.Free(BindlessKind.Texture2DArray, _cacheNormalIndex);
        _cacheAlbedo?.Dispose();
        _cacheNormal?.Dispose();
        _cacheProgram?.Dispose();
    }
}