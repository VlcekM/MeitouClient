using System.Diagnostics;
using System.Numerics;
using Meitou.Content;
using Meitou.Game.Rendering.Gpu;
using Meitou.Modding;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// What a post chain hands the mods' passes of one frame: the scene's depth and surface targets (null: none) and its camera.
/// </summary>
readonly record struct PostPassView(GpuTexture? Depth, GpuTexture? Surface, Vector3 Eye, Matrix4x4 View, float FieldOfView, float Aspect,
    float Near, float Far, bool HasScene, bool Hdr);

/// <summary>
/// One full-screen draw of a mod's pass into <paramref name="target"/> (null: the frame's target), its uniforms and samplers set, timed under
/// <paramref name="stamp"/>.
/// </summary>
delegate void PostPassDraw(LegacyProgram program, GpuTexture? target, string stamp);

/// <summary>
/// Runs the code mods' post passes (docs/modding.md "Post passes") for a post chain, on either backend: compiles each pass's fragment against
/// the chain's full-screen vertex, makes the two pictures a stage's passes draw between, binds what a fragment declares and calls its
/// <see cref="PostPass.Prepare"/>. A stage without a pass that is on makes nothing and costs nothing. A pass whose shader does not compile, or
/// whose callback throws, is turned off for the session with one log line.
/// </summary>
sealed class PostPassRunner(IGpuContext ctx, string vertex, bool topDown) : IDisposable
{
    #region Passes of the frame

    /// <summary>
    /// A compiled pass: its program and the samplers and uniforms the game sets by name.
    /// </summary>
    sealed class Compiled(LegacyProgram program)
    {
        /// <summary>
        /// The pass's program.
        /// </summary>
        public readonly LegacyProgram P = program;
        /// <summary>
        /// <c>uImage</c>, <c>uDepth</c> and <c>uSurface</c>.
        /// </summary>
        public readonly SamplerSlot Image = program.Sampler("uImage"), Depth = program.Sampler("uDepth"), Surface = program.Sampler("uSurface");
        /// <summary>
        /// <c>uViewport</c>, <c>uPlanes</c> and <c>uTime</c>.
        /// </summary>
        public readonly UniformHandle Viewport = program.Uniform("uViewport"), Planes = program.Uniform("uPlanes"), Time = program.Uniform("uTime");
    }

    /// <summary>
    /// <c>uTime</c> wraps at this many seconds, so a float keeps its precision.
    /// </summary>
    const double TimeWrap = 3600;

    /// <summary>
    /// The programs made so far, by pass.
    /// </summary>
    readonly Dictionary<PostPass, Compiled> _compiled = [];

    /// <summary>
    /// The mods' passes the programs were made for (<see cref="Forget"/>).
    /// </summary>
    ModPostPasses? _passes;

    /// <summary>
    /// The passes that draw this frame, per stage, in running order (<see cref="Begin"/>).
    /// </summary>
    readonly List<Compiled>[] _frame = [[], []];

    /// <summary>
    /// The names the frame's passes are timed under, beside <see cref="_frame"/>.
    /// </summary>
    readonly List<string>[] _stamps = [[], []];

    /// <summary>
    /// What <see cref="PostPass.Prepare"/> gets, reused.
    /// </summary>
    readonly PostPassFrame _info = new();

    /// <summary>
    /// The real clock <see cref="PostPassFrame.Seconds"/> reads.
    /// </summary>
    readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>
    /// Readies <paramref name="stage"/>'s passes for a picture of <paramref name="width"/> x <paramref name="height"/> in
    /// <paramref name="format"/>: compiles the new ones, calls each <see cref="PostPass.Prepare"/> and makes the pictures they draw between.
    /// Returns how many draw this frame (0: the chain runs as without mods).
    /// </summary>
    public int Begin(PostStage stage, ModPostPasses? passes, in PostPassView view, int width, int height, InternalFormat format)
    {
        var frame = _frame[(int)stage];
        var stamps = _stamps[(int)stage];
        frame.Clear();
        stamps.Clear();
        if (passes is null || width <= 0 || height <= 0) return 0;
        if (!ReferenceEquals(passes, _passes)) Forget(passes);

        foreach (var pass in passes.All)
        {
            if (pass.Stage != stage || !pass.Enabled || pass.Fault is not null) continue;
            if (Ready(pass, stage, view, width, height) is not { } compiled) continue;

            frame.Add(compiled);
            stamps.Add("mod " + pass.Name);
        }
        if (frame.Count > 0 && !EnsurePictures(stage, width, height, format, frame.Count > 1 ? 2 : 1)) frame.Clear();

        return frame.Count;
    }

    /// <summary>
    /// Takes another mod host's passes (a new world started the mods again): the programs of passes it does not hold are released.
    /// </summary>
    void Forget(ModPostPasses passes)
    {
        _passes = passes;
        foreach (var (pass, compiled) in _compiled.Where(c => !passes.All.Contains(c.Key)).ToList())
        {
            compiled.P.Dispose();
            _compiled.Remove(pass);
        }
        foreach (var list in _frame) list.Clear();
    }

    /// <summary>
    /// The pass compiled and prepared for this frame; null when it is left out (it failed, or its callback skipped the frame).
    /// </summary>
    Compiled? Ready(PostPass pass, PostStage stage, in PostPassView view, int width, int height)
    {
        if (!_compiled.TryGetValue(pass, out var compiled))
        {
            if (Compile(pass) is not { } made) return null;

            _compiled[pass] = compiled = made;
        }
        compiled.P.Set(compiled.Viewport, (float)width, (float)height);
        compiled.P.Set(compiled.Planes, view.Near, view.Far);
        compiled.P.Set(compiled.Time, (float)(_clock.Elapsed.TotalSeconds % TimeWrap));

        return pass.Prepare is null || Prepare(pass, compiled, stage, view, width, height) ? compiled : null;
    }

    /// <summary>
    /// The pass's program; a fragment that does not compile turns the pass off and is logged.
    /// </summary>
    Compiled? Compile(PostPass pass)
    {
        try
        {
            return new Compiled(LegacyProgram.Create(ctx, vertex, pass.Fragment, $"mod {pass.Owner} {pass.Name}"));
        }
        catch (Exception e)
        {
            Fail(pass, "its shader did not compile", e);
            return null;
        }
    }

    /// <summary>
    /// Calls the pass's <see cref="PostPass.Prepare"/>; false when it skipped the frame or threw (which turns the pass off).
    /// </summary>
    bool Prepare(PostPass pass, Compiled compiled, PostStage stage, in PostPassView view, int width, int height)
    {
        var info = _info;
        (info.Stage, info.Width, info.Height) = (stage, width, height);
        (info.FieldOfView, info.Aspect, info.Near, info.Far) = (view.FieldOfView, view.Aspect, view.Near, view.Far);
        (info.View, info.Eye, info.Seconds) = (view.View, view.Eye, _clock.Elapsed.TotalSeconds);
        (info.HasScene, info.HasSurface, info.Hdr, info.TopDown) = (view.HasScene, view.Surface is not null, view.Hdr, topDown);
        info.Open(compiled.P);
        try
        {
            pass.Prepare!(info);
            return !info.Skip;
        }
        catch (Exception e)
        {
            Fail(pass, "its Prepare threw", e);
            return false;
        }
        finally
        {
            info.Close();
        }
    }

    /// <summary>
    /// Turns a pass off for the session and says why, once.
    /// </summary>
    static void Fail(PostPass pass, string what, Exception e)
    {
        pass.Fault = $"{what}: {e.Message}";
        Console.Error.WriteLine($"mod       {pass.Owner}: post pass '{pass.Name}' is off, {what}: {e.GetType().Name}: {e.Message}");
        CodeModFaults.Tell(pass.Owner, e);
    }

    #endregion

    #region Pictures and draws

    /// <summary>
    /// The two pictures a stage's passes draw between (the second only with two passes or more), per stage; null until made.
    /// </summary>
    readonly GpuTexture?[,] _pictures = new GpuTexture?[2, 2];

    /// <summary>
    /// Whether a failure to make the pictures was logged (it is said once).
    /// </summary>
    bool _picturesFailed;

    /// <summary>
    /// The linear and the nearest clamped samplers.
    /// </summary>
    readonly SampledTexture _linear = Sampler(ctx, TextureMinFilter.Linear, TextureMagFilter.Linear),
        _nearest = Sampler(ctx, TextureMinFilter.Nearest, TextureMagFilter.Nearest);

    /// <summary>
    /// The picture the chain draws its own last pass into in place of the frame's target, for the <see cref="PostStage.Picture"/> passes to read
    /// (after a <see cref="Begin"/> that returned above 0).
    /// </summary>
    public GpuTexture PictureInput => _pictures[(int)PostStage.Picture, 0]!;

    /// <summary>
    /// Draws the stage's passes of this frame: the first reads <paramref name="source"/>, each next the one before, and the last draws into the
    /// frame's target when <paramref name="toTarget"/>, else into a picture of the stage. Returns the picture last written (null: the frame's
    /// target), or <paramref name="source"/> when no pass draws.
    /// </summary>
    public GpuTexture? Run(PostStage stage, GpuTexture source, in PostPassView view, bool toTarget, PostPassDraw draw)
    {
        var frame = _frame[(int)stage];
        var stamps = _stamps[(int)stage];
        GpuTexture? read = source;
        for (int i = 0; i < frame.Count; i++)
        {
            var pass = frame[i];
            Bind(pass, read!, view);
            var write = toTarget && i == frame.Count - 1 ? null : Other(stage, read!);
            draw(pass.P, write, stamps[i]);
            read = write;
        }

        return read;
    }

    /// <summary>
    /// Binds a pass's picture so far, and the scene's depth and surfaces where it declares them (the stand-in where the frame has none).
    /// </summary>
    void Bind(Compiled pass, GpuTexture image, in PostPassView view)
    {
        var p = pass.P;
        if (pass.Image.IsValid) p.Bind(pass.Image, _linear with { View = image.View() });
        if (pass.Depth.IsValid)
            p.Bind(pass.Depth, view.Depth is { } depth ? _nearest with { View = depth.View() } : ctx.Dummy(p.SamplerInfo(pass.Depth)));
        if (pass.Surface.IsValid)
            p.Bind(pass.Surface, view.Surface is { } surface ? _nearest with { View = surface.View() } : ctx.Dummy(p.SamplerInfo(pass.Surface)));
    }

    /// <summary>
    /// The stage's picture a pass reading <paramref name="read"/> draws into: the first, or the second when it reads the first.
    /// </summary>
    GpuTexture Other(PostStage stage, GpuTexture read) =>
        ReferenceEquals(read, _pictures[(int)stage, 0]) ? _pictures[(int)stage, 1]! : _pictures[(int)stage, 0]!;

    /// <summary>
    /// Makes (or makes again, for another size or format) <paramref name="count"/> pictures of a stage; false, logged once, when the device
    /// cannot (out of video memory): the stage's passes are then left out of the frame.
    /// </summary>
    bool EnsurePictures(PostStage stage, int width, int height, InternalFormat format, int count)
    {
        try
        {
            for (int i = 0; i < count; i++) EnsurePicture(stage, i, width, height, format);
            return true;
        }
        catch (Exception e)
        {
            if (!_picturesFailed) Console.Error.WriteLine($"mods      post passes left out: their pictures could not be made ({e.Message})");
            _picturesFailed = true;
            return false;
        }
    }

    /// <summary>
    /// One picture of a stage at the size and format, kept while they stay the same.
    /// </summary>
    void EnsurePicture(PostStage stage, int index, int width, int height, InternalFormat format)
    {
        var have = _pictures[(int)stage, index];
        if (have is not null && have.Desc.Width == width && have.Desc.Height == height && have.Desc.Format == format) return;

        have?.Dispose();   // released after the frames in flight
        _pictures[(int)stage, index] = null;
        _pictures[(int)stage, index] = ctx.CreateTexture(new TextureDesc(format, width, height,
            Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: $"mod post {stage} {index}"));
    }

    /// <summary>
    /// A clamped sampler without a view.
    /// </summary>
    static SampledTexture Sampler(IGpuContext ctx, TextureMinFilter min, TextureMagFilter mag) =>
        new(ctx.Samplers.Get(SamplerDesc.FromGl(min, mag, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.Repeat,
            false, DepthFunction.Lequal, false, 1, false, 0)), default);

    /// <summary>
    /// Releases the pictures and the programs.
    /// </summary>
    public void Dispose()
    {
        foreach (var picture in _pictures) picture?.Dispose();
        foreach (var compiled in _compiled.Values) compiled.P.Dispose();
        _compiled.Clear();
    }

    #endregion
}
