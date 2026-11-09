using System.Diagnostics;
using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Rendering.Terrain;
using Silk.NET.Vulkan;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;

namespace Meitou.Game.Rendering.Shadows;

/// <summary>
/// The Meitou shadows (the <c>shadows</c> enhancement, docs/formats/shadows.md "Meitou shadows"): the same cascade array and caster draws as the
/// game's CSM, but five cascades (<see cref="MeitouCascades"/>) fitted over the visible depths (<see cref="MeitouShadowFit"/>), drawn on a staggered
/// schedule (<see cref="Cadence"/>: 1, 2, 4, 16, 32 frames with five cascades, each kept with the matrices it was drawn with), a soft receiver with
/// cascade blending and a fade at the range's end, contact-hardening penumbrae from a half-resolution blocker map, and the terrain's own shadow over
/// the whole world beyond the range (<see cref="TerrainShadowMap"/>), and the landmarks' beyond it however far they are (the landmark map).
/// </summary>
public sealed unsafe partial class ShadowPass
{
    /// <summary>
    /// The landmark shadow map's side (texels). Over the landmarks of the default reach (150000 around the camera) a texel is about 75 units; a
    /// landmark is 2000 or more in radius.
    /// </summary>
    public const int LandmarkMapSize = 4096;

    /// <summary>
    /// The watched point's distance from the anchor before a turning sun moves it: a first-cascade grid then slides at most about (this + its radius)
    /// × the turn per frame.
    /// </summary>
    public const double AnchorReach = 200;

    /// <summary>
    /// How far the shadow range may drift (share of the drawn one) before every cascade is drawn again.
    /// </summary>
    const float RangeTolerance = 0.1f;

    /// <summary>
    /// Foliage meshes smaller than this many texels of a cascade (radius × largest scale) cast nothing into it (Meitou): their shadow would be a blur
    /// of a few texels under the soft filter, at the price of their triangles and cut-out fragments.
    /// </summary>
    public const float MinFoliageCasterTexels = 2;

    /// <summary>
    /// A character part (hair, a weapon, a hat) narrower than this many texels casts nothing into a coarse cascade
    /// (<c>CharacterRenderer.DrawDepth</c>).
    /// </summary>
    public const float MinCharacterCasterTexels = 1;

    /// <summary>
    /// The <c>shadows</c> switch: Meitou (true, the default) or the game's CSM.
    /// </summary>
    public bool Meitou { get; set; } = true;

    /// <summary>
    /// A temporal anti-aliasing or upscaling pass runs: the filter's noise changes every frame (it is static otherwise).
    /// </summary>
    public bool Temporal { get; set; }

    /// <summary>
    /// Contact-hardening penumbrae (the blocker search); off: a fixed small filter.
    /// </summary>
    public bool ContactHardening { get; set; } = true;

    /// <summary>
    /// The sun's angular diameter the penumbrae are made for (radians): a little more than the real sun's 0.53°.
    /// </summary>
    public float SunAngle { get; set; } = 0.7f * MathF.PI / 180;

    /// <summary>
    /// Filter radius without a blocker search, and the least with one, in texels of the cascade.
    /// </summary>
    public float FilterTexels { get; set; } = 1.5f;

    /// <summary>
    /// The widest penumbra radius in world units (a tall caster far above the ground); never less than <see cref="FilterTexels"/>.
    /// </summary>
    public float MaxPenumbra { get; set; } = 3;

    /// <summary>
    /// A sudden turn of the sun larger than this (radians) redraws every cascade at once.
    /// </summary>
    public float SunJump { get; set; } = 2 * MathF.PI / 180;

    /// <summary>
    /// How often each cascade was drawn (Meitou), for the log.
    /// </summary>
    public readonly int[] CascadeDraws = new int[ShadowSettings.MaxCascades];

    /// <summary>
    /// Per cascade why it was drawn (Meitou): the camera moved so it no longer covers the view, the sun turned, its cadence came, the splits or a sun
    /// jump redrew all.
    /// </summary>
    public readonly int[,] RedrawReasons = new int[ShadowSettings.MaxCascades, 4];

    /// <summary>
    /// How often every cascade was redrawn at once because the splits changed (index 0) or the sun jumped (1).
    /// </summary>
    public readonly int[] AllReasons = new int[2];

    /// <summary>
    /// The frames the Meitou shadows were drawn for (the log).
    /// </summary>
    public int MeitouFrames { get; private set; }

    /// <summary>
    /// The Meitou shadows' number of cascades (1 to <see cref="ShadowSettings.MaxCascades"/>; <see cref="MeitouShadowFit.DefaultCascades"/>). The
    /// Faithful CSM keeps the game's <see cref="ShadowSettings.Cascades"/>.
    /// </summary>
    public int MeitouCascades
    {
        get => _meitouCascades;
        set => _meitouCascades = Math.Clamp(value, 1, ShadowSettings.MaxCascades);
    }

    /// <summary>
    /// How many cascades, nearest first, filter in full (blocker search, 16 taps) at the <see cref="FilterQuality"/>.
    /// </summary>
    int FullFilterCascades => _filterQuality switch { 0 => 0, 1 => Math.Max(1, MeitouCascades - 2), _ => ShadowSettings.MaxCascades };

    /// <summary>
    /// The receiver's filter tier (<c>--shadow-filter</c>, Tab): 2 every cascade with the blocker search and 16 taps; 1 the two nearest so, the far
    /// ones 8 taps at the least radius; 0 every cascade 8 taps at the least radius, no blocker map (low-end GPUs).
    /// </summary>
    public int FilterQuality
    {
        get => _filterQuality;
        set => _filterQuality = Math.Clamp(value, 0, 2);
    }

    /// <summary>
    /// The tier behind <see cref="FilterQuality"/> (0 to 2).
    /// </summary>
    int _filterQuality = 2;

    /// <summary>
    /// The count behind <see cref="MeitouCascades"/>.
    /// </summary>
    int _meitouCascades = MeitouShadowFit.DefaultCascades;

    #region Cascade schedule

    /// <summary>
    /// Each cascade as it was last drawn (null: never), kept between its redraws; the receivers read these.
    /// </summary>
    readonly ShadowCascade?[] _stored = new ShadowCascade?[ShadowSettings.MaxCascades];

    /// <summary>
    /// The sun's direction each cascade was last drawn with.
    /// </summary>
    readonly Vector3[] _storedSunDir = new Vector3[ShadowSettings.MaxCascades];

    /// <summary>
    /// The cascades that wanted drawing but waited for the far budget (drawn first next time).
    /// </summary>
    readonly bool[] _owed = new bool[ShadowSettings.MaxCascades];

    /// <summary>
    /// The frames each deferred cascade has waited (<see cref="ShadowSchedule.Pick"/>).
    /// </summary>
    readonly int[] _waited = new int[ShadowSettings.MaxCascades];

    /// <summary>
    /// The cascades drawn before the camera's flight (docs/render-shadows.md "Flights"): stale until drawn again; the published cascades stop before
    /// the first stale one.
    /// </summary>
    readonly bool[] _stale = new bool[ShadowSettings.MaxCascades];

    /// <summary>
    /// The splits all the cascades were last drawn with at once (null: never).
    /// </summary>
    float[]? _storedSplits;

    /// <summary>
    /// The sun's direction at the last redraw of every cascade.
    /// </summary>
    Vector3 _storedSun;

    /// <summary>
    /// The map size at the last redraw of every cascade, and the Meitou frame counter the cadences run on.
    /// </summary>
    int _storedMapSize, _meitouFrame;

    /// <summary>
    /// The Meitou cascades and block hold a frame's shadows (false after a frame of the game's CSM, or shadows off).
    /// </summary>
    bool _meitouValid;

    /// <summary>
    /// Each cascade's redraw phase (<see cref="Phases"/>), made again when the count changes.
    /// </summary>
    int[] _phases = [];

    /// <summary>
    /// Frames between the redraws of cascade <paramref name="i"/> of <paramref name="count"/> when nothing makes it stale (Meitou): the nearest every
    /// frame, the last three every 4th, 16th and 32nd frame (a far cascade's casters hardly move and its texels are big; 1,2,4,4 was the schedule for
    /// four before 2026-10-08), any between every 2nd (with five: 1, 2, 4, 16, 32). <see cref="Phase"/> puts the redraws on different frames.
    /// </summary>
    public static int Cadence(int i, int count) => i == 0 ? 1 : (count - 1 - i) switch { 0 => 32, 1 => 16, 2 => 4, _ => 2 };

    /// <summary>
    /// The frame (modulo its <see cref="Cadence"/>) on which each of <paramref name="count"/> cascades is redrawn: nearest first, each takes the
    /// phase that collides least with the nearer far cascades' over 32 frames (they share <see cref="FarBudget"/>), the old <c>i · 5 mod cadence</c>
    /// where that is as good (so four cascades keep the phases 0, 1, 10, 15 they had; five get 0, 1, 2, 0, 20).
    /// </summary>
    public static int[] Phases(int count)
    {
        var phase = new int[count];
        for (int i = 1; i < count; i++)
        {
            int cadence = Cadence(i, count), preferred = i * 5 % cadence, fewest = int.MaxValue;
            for (int k = 0; k < cadence; k++)
            {
                int p = (preferred + k) % cadence, hits = 0;
                for (int f = p; f < 32; f += cadence)
                {
                    for (int j = 1; j < i; j++)
                    {
                        if (f % Cadence(j, count) == phase[j])
                        {
                            hits++;
                            break;
                        }
                    }
                }

                if (hits < fewest) (phase[i], fewest) = (p, hits);
            }
        }

        return phase;
    }

    /// <summary>
    /// How far (radians) the sun may turn before a cascade is redrawn: a caster 150 units tall shifts its shadow by half a texel; at most 2°, at
    /// least 0.25°.
    /// </summary>
    static float SunTolerance(ShadowCascade c) => (float)Math.Clamp(0.5 * c.Texel / 150, 0.25 * Math.PI / 180, 2 * Math.PI / 180);

    /// <summary>
    /// How many far cascades (1 and up, 1.1 to 1.8 ms each on the swamp) one frame may draw; the other redraws wait, the longest first, and are
    /// forced after
    /// <see cref="MaxWait"/> frames. 0: no limit (the schedule before 2026-10-09, where up to three coincided in one frame); the <c>shadow-spread</c>
    /// A/B switch.
    /// </summary>
    public int FarBudget { get; set; } = ShadowSchedule.DefaultBudget;

    /// <summary>
    /// The most frames a deferred redraw waits.
    /// </summary>
    public int MaxWait { get; set; } = ShadowSchedule.DefaultMaxWait;

    /// <summary>
    /// The host flies the camera this frame (a character flight or squad tour hop, set by the frame before <see cref="Render"/>): the far cascades
    /// are not drawn.
    /// </summary>
    public bool CameraFlying { get; set; }

    /// <summary>
    /// Frames the far cascades were skipped for the camera's flight (the log).
    /// </summary>
    public int FlightFrames { get; private set; }

    /// <summary>
    /// The smallest foliage mesh <paramref name="cascade"/> draws (world units; 0 with the faithful shadows: every mesh).
    /// </summary>
    public float MinFoliageCaster(ShadowCascade cascade) => Meitou ? (float)cascade.Texel * MinFoliageCasterTexels : 0;

    /// <summary>
    /// The <c>char-casters</c> A/B switch: on by default; the cull of <see cref="MinCharacterCasterTexels"/>.
    /// </summary>
    public static bool CharacterCasterCull { get; set; } = true;

    /// <summary>
    /// One line on the schedule: how often each cascade was drawn, and the terrain map's rebuilds.
    /// </summary>
    public string DescribeMeitou()
    {
        string draws = string.Join("/", CascadeDraws.Take(MeitouCascades));
        var reasonText = Enumerable.Range(0, MeitouCascades)
            .Select(i => $"c{i} {RedrawReasons[i, 0]}/{RedrawReasons[i, 1]}/{RedrawReasons[i, 2]}/{RedrawReasons[i, 3]}");
        string reasons = string.Join(" ", reasonText);
        string terrain = $"terrain shadow rebuilt {_terrainMap?.Builds ?? 0} times (cpu {_terrainMap?.LastBuildCpuMs ?? 0:0.00} ms)";
        string landmarks = $"landmark shadow map drawn {_landmarks.Draws} times ({_landmarks.Count} landmarks, {_landmarks.Texel()})";
        string frames = $"drawn {draws} times in {MeitouFrames} frames " +
            $"(because the view left the box / the sun turned / the cadence / all at once: {reasons}); ";
        return frames +
            $"all at once: splits changed {AllReasons[0]}, sun jumped {AllReasons[1]}; " +
            $"far cascades skipped for the camera's flight in {FlightFrames} frames; " +
            $"{terrain}; {landmarks}";
    }

    #endregion

    #region Anchor

    /// <summary>
    /// Where the texel grids are anchored (null: not yet).
    /// </summary>
    (double X, double Y, double Z)? _anchor;

    /// <summary>
    /// The sun's direction of the last frame (a different one means it is turning).
    /// </summary>
    Vector3 _lastFrameSun;

    /// <summary>
    /// Where the cascades' texel grids are anchored (<see cref="MeitouShadowFit.Fit"/>): at the point the camera looks at (the eye without a focus),
    /// so a turning sun slides the first cascade's grid under what is watched only by the distance from here times the angle (anchored at the eye,
    /// 300 to 1000 units back, every shadow edge round the squad crawled by a fifth of a texel a frame while time ran). It moves only while the sun
    /// turns and the point is more than <see cref="AnchorReach"/> away (a move shifts the grids by a fraction of a texel, once); with the sun still
    /// the grids do not slide at all, so it stays.
    /// </summary>
    (double X, double Y, double Z) Anchor(Vector3 point, Vector3 toSun)
    {
        bool turning = toSun != _lastFrameSun;
        _lastFrameSun = toSun;
        if (_anchor is not { } a)
            return (_anchor = (Math.Round(point.X), Math.Round(point.Y), Math.Round(point.Z))).Value;

        double dx = point.X - a.X, dy = point.Y - a.Y, dz = point.Z - a.Z;
        if (turning && dx * dx + dy * dy + dz * dz > AnchorReach * AnchorReach)
        {
            _anchor = (Math.Round(point.X), Math.Round(point.Y), Math.Round(point.Z));
            AnchorMoves++;
        }

        return _anchor.Value;
    }

    /// <summary>
    /// How often the grid anchor moved (Meitou), for the log.
    /// </summary>
    public int AnchorMoves { get; private set; }

    #endregion

    #region Landmarks

    /// <summary>
    /// Collects the landmarks to cast into the landmark map: their bounding spheres (xyz centre, w radius) into the list, and a key that changes when
    /// that set does (WorldObjectRenderer.LandmarkCasters).
    /// </summary>
    public delegate long LandmarkCollect(List<Vector4> spheres);

    /// <summary>
    /// The landmarks for <see cref="Render"/>: how to collect them and how to draw their depth.
    /// </summary>
    public readonly record struct LandmarkCasters(LandmarkCollect Collect, CasterDraw Draw);

    /// <summary>
    /// The landmark shadow map: every landmark along the sun, fitted round them all.
    /// </summary>
    readonly LandmarkShadowMap _landmarks;

    /// <summary>
    /// The landmark shadow map (docs/formats/shadows.md, "Landmark shadows"): every landmark drawn (resolved, within the landmark reach) into one
    /// depth map along the sun, fitted around them all (<see cref="MeitouShadowFit.FitLandmarks"/>), so a landmark casts its shadow however far it
    /// is; the receivers read it beyond the cascades. Drawn again only when the set of landmarks changes or the sun has turned (as the terrain map).
    /// </summary>
    void UpdateLandmarks(ShadowView view, Vector3 toSun, LandmarkCasters? landmarks)
    {
        if (_landmarks.Plan(toSun, landmarks, out long key) is not { } box) return;

        var map = _landmarks.EnsureMap();
        var host = BeginHost("landmark shadows", clear: true, map);
        SetTile(0, 0, LandmarkMapSize);
        SetCasterBias(new Vector4(box.FixedBias, KenshiShadows.SlopeBias, KenshiShadows.MaxSlopeBias, 1f));
        landmarks!.Value.Draw(box, box.WorldToClip(), box.CullPlanes(), view.Eye);
        EndHost(host);
        _landmarks.Drawn(box, key, toSun);
    }

    #endregion

    #region Cascade drawing

    /// <summary>
    /// One frame of the Meitou shadows: the splits, which cascades are drawn now (wanted, the far budget, a flight), the drawing, the blocker,
    /// terrain and landmark maps, then the receivers' blocks.
    /// </summary>
    void RenderMeitou(ShadowView view, Vector3 toSun, CasterDraw draw, LandmarkCasters? landmarks, CasterPrepare? prepare)
    {
        var watch = Stopwatch.StartNew();
        Array.Clear(PhaseMs);
        int count = MeitouCascades;
        Resize(Settings.TileSize, count);
        if (_phases.Length != count) _phases = Phases(count);
        float near = MeitouShadowFit.QuantizedNear(view.Near);
        var splits = MeitouShadowFit.Splits(near, Math.Max(EffectiveRange, near * 4), count, view.Focus);
        bool all = RedrawAll(splits, toSun);

        var gridAnchor = Anchor(view.Focus > 0 ? view.Eye + Vector3.Normalize(view.Forward) * view.Focus : view.Eye, toSun);
        int frame = _meitouFrame++;
        MeitouFrames++;
        Span<bool> drawNow = stackalloc bool[ShadowSettings.MaxCascades];
        Span<bool> want = stackalloc bool[ShadowSettings.MaxCascades];
        Span<bool> mustDraw = stackalloc bool[ShadowSettings.MaxCascades];
        MarkWanted(view, toSun, splits, count, frame, all, want, mustDraw);

        // At most FarBudget far cascades are drawn per frame; the others wait (longest first) and are owed, forced after MaxWait frames. The far
        // cascades are the last three (the costly ones); the nearer ones are never deferred (ShadowSchedule.NearCascades).
        int nearCount = ShadowSchedule.NearCascades(count);
        ShadowSchedule.Pick(want[..count], mustDraw[..count], _waited, FarBudget, MaxWait, drawNow, nearCount);

        // While the host flies the camera (a character flight, a squad tour hop) the far cascades are not drawn: a map drawn now covers the wrong
        // ground the next frame, and the ones drawn before the flight shadow the ground kilometres back, so they are stale and not published until
        // drawn again, once the camera lands (uncovered, over the budget's frames).
        bool flying = CameraFlying;
        if (flying) HoldFarCascades(drawNow, mustDraw, nearCount, count);

        for (int i = 0; i < count; i++) _owed[i] = want[i] && !drawNow[i] && !flying;

        _timer.Begin();
        int drawn = FitCascades(view, toSun, splits, gridAnchor, drawNow, count);
        prepare?.Invoke(_casterViews.AsSpan(0, drawn));
        DrawCascades(drawn, draw, toSun);
        Array.Clear(_casterViews);
        bool blockers = ContactHardening && _filterQuality > 0 && _blockers.Update(drawNow, count, _atlasSize, _atlasPlain);
        if (_coarse is not null)
        {
            _terrainMap ??= new TerrainShadowMap(Gpu, _coarse, _coarseSize);
        }

        // The terrain map (rebuilt when the sun has moved).
        _terrainMap?.Update(toSun);
        UpdateLandmarks(view, toSun, landmarks);
        _timer.End();
        if (all)
        {
            _storedSun = toSun;
            _storedSplits = splits;
            _storedMapSize = Settings.MapSize;
        }

        _meitouValid = true;
        PublishFrame(view, toSun, blockers, count);
        CpuMs = watch.Elapsed.TotalMilliseconds;
        _cpuSamples.Add(CpuMs);
    }

    /// <summary>
    /// Whether every cascade is drawn again this frame (the first frame, other splits or map size, a sun that jumped); counts the reason.
    /// </summary>
    bool RedrawAll(float[] splits, Vector3 toSun)
    {
        // (A range that varies from frame to frame, as the memory guard's or a fog clamp makes it, redraws them all only past RangeTolerance.)
        // The splits move with the camera's near plane; a cascade drawn with other splits keeps its own slice (the receiver reads each one's) and is
        // drawn again when the new slice no longer fits it (Covers below), so only a change of the range or of the number of cascades redraws them
        // all.
        bool splitsChanged = _storedSplits is null || _storedSplits.Length != splits.Length ||
            Math.Abs(splits[^1] - _storedSplits[^1]) > RangeTolerance * _storedSplits[^1];
        bool sunJumped = Vector3.Dot(toSun, _storedSun) < MathF.Cos(SunJump);
        bool all = !_meitouValid || splitsChanged || _storedMapSize != Settings.MapSize || sunJumped;
        if (_meitouValid && all)
        {
            if (splitsChanged) AllReasons[0]++;
            if (sunJumped) AllReasons[1]++;
        }
        return all;
    }

    /// <summary>
    /// Marks the cascades that want drawing this frame and the ones that must be (never drawn yet), and counts each redraw's reason.
    /// </summary>
    void MarkWanted(ShadowView view, Vector3 toSun, float[] splits, int count, int frame, bool all, Span<bool> want, Span<bool> mustDraw)
    {
        for (int i = 0; i < count; i++)
        {
            // A cascade is drawn again when its slice no longer fits its box (the camera moved or turned), when the sun has turned further than
            // its texels allow, and at the latest after its cadence (casters stream in and move; docs/formats/shadows.md "Cached far cascades").
            bool due = frame % Cadence(i, count) == _phases[i];
            bool stale = _stored[i] is not null && !all && !due;
            bool uncovered = _stored[i] is not null
                && !MeitouShadowFit.Covers(_stored[i]!, view, splits, SearchRadius(_stored[i]!) + 2 * _stored[i]!.Texel);
            bool sunTurned = _stored[i] is not null && Vector3.Dot(toSun, _storedSunDir[i]) < MathF.Cos(SunTolerance(_stored[i]!));
            // Stale: drawn before a flight, wherever its box is now.
            want[i] = all || _stored[i] is null || due || uncovered || sunTurned || _owed[i] || this._stale[i];
            mustDraw[i] = _stored[i] is null;
            if (stale && uncovered) RedrawReasons[i, 0]++;
            if (stale && !uncovered && sunTurned) RedrawReasons[i, 1]++;
            if (due && !all) RedrawReasons[i, 2]++;
            if (all) RedrawReasons[i, 3]++;
        }
    }

    /// <summary>
    /// During a camera flight: the far cascades marked stale and not drawn (unless never drawn at all), their wait reset.
    /// </summary>
    void HoldFarCascades(Span<bool> drawNow, Span<bool> mustDraw, int nearCount, int count)
    {
        FlightFrames++;
        for (int i = nearCount; i < count; i++)
        {
            _stale[i] = true;
            if (mustDraw[i]) continue;

            drawNow[i] = false;
            _waited[i] = 0;
        }
    }

    /// <summary>
    /// Fits each cascade drawn this frame into <see cref="_casterViews"/>; returns how many.
    /// </summary>
    int FitCascades(ShadowView view, Vector3 toSun, float[] splits, (double X, double Y, double Z) gridAnchor, Span<bool> drawNow, int count)
    {
        int drawn = 0;
        for (int i = 0; i < count; i++)
        {
            if (drawNow[i] && MeitouShadowFit.Fit(view, toSun, Effective, splits, i, gridAnchor) is var fitted)
                _casterViews[drawn++] = new CasterView(fitted, fitted.WorldToClip(), fitted.CullPlanes(), view.Eye);
        }
        return drawn;
    }

    /// <summary>
    /// Draws the <paramref name="drawn"/> cascades collected in <see cref="_casterViews"/>, each into its own layer of the array.
    /// </summary>
    void DrawCascades(int drawn, CasterDraw draw, Vector3 toSun)
    {
        // Each cascade drawn is a rendering of its own layer, cleared by the load op; the other layers keep what they hold.
        for (int k = 0; k < drawn; k++)
        {
            var (c, worldToClip, planes, lodEye) = _casterViews[k];
            int i = c.Index;
            var host = BeginHost("shadow cascade", clear: true, layer: i);
            SetTile(0, 0, _atlasSize);
            SetCasterBias(new Vector4(c.FixedBias, MeitouShadowFit.Slope, c.MaxSlopeBias, 1f));
            draw(c, worldToClip, planes, lodEye);
            EndHost(host);
            _stored[i] = c;
            _storedSunDir[i] = toSun;
            _stale[i] = false;
            CascadeDraws[i]++;
        }
    }

    /// <summary>
    /// Publishes the cascades before the first stale one (the receiver's range ends with the last published; the terrain and landmark maps go on).
    /// </summary>
    void PublishFrame(ShadowView view, Vector3 toSun, bool blockers, int count)
    {
        // The receivers read the cascades before the first stale one (the receiver's range ends with the last published; the terrain and landmark
        // maps go on).
        int published = count;

        for (int i = 0; i < count; i++)
        {
            if (_stale[i])
            {
                published = i;
                break;
            }
        }

        var cascades = new ShadowCascade[published];
        for (int i = 0; i < published; i++) cascades[i] = _stored[i]!;
        Cascades = cascades;
        PublishMeitou(view, cascades, toSun, blockers);
    }

    #endregion

    #region Publish

    /// <summary>
    /// The blocker search's and the filter's widest radius in a cascade (world units).
    /// </summary>
    double SearchRadius(ShadowCascade c) => Math.Max(MaxPenumbra, FilterTexels * 1.5 * c.Texel);

    /// <summary>
    /// The whole-world height grid (WorldScene.Coarse) for the terrain shadow beyond the range; uploaded when first needed.
    /// </summary>
    public void SetTerrain(ushort[] heights, int size) => (_coarse, _coarseSize) = (heights, size);

    /// <summary>
    /// The whole-world height grid for the terrain shadow (<see cref="SetTerrain"/>; null: none).
    /// </summary>
    ushort[]? _coarse;

    /// <summary>
    /// The grid's side in samples.
    /// </summary>
    int _coarseSize;

    /// <summary>
    /// The terrain shadow map (made once the grid is set).
    /// </summary>
    TerrainShadowMap? _terrainMap;

    /// <summary>
    /// The terrain shadow map the receivers sample, as the GL code bound it to its unit (kept while no new one is).
    /// </summary>
    SampledTexture? _boundTerrain;   // the terrain shadow map the receivers sample, as the GL code bound it to its unit (kept while no new one is)

    /// <summary>
    /// The Meitou receiver block (<see cref="MeitouShadowShaders.Block"/>).
    /// </summary>
    readonly FrameBlock _meitouBlock;

    /// <summary>
    /// The blocker map of the contact-hardening search.
    /// </summary>
    readonly ShadowBlockerMap _blockers;

    /// <summary>
    /// The half-resolution blocker map (native; null until the first frame with contact hardening).
    /// </summary>
    public GpuTexture? BlockerMap => _blockers.Map;

    /// <summary>
    /// Fills the receiver block and the Meitou block from the published cascades (slices, filter and search radii, penumbra scale), the fade and
    /// noise, the terrain and landmark maps, and publishes the maps.
    /// </summary>
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
            Put(data, i * 16, ReceiverTile(c, origin));
            Put(data, RectAt + i * 4, c.Tile);
            Put(data, CascadeAt + i * 4, new Vector4(c.FarDepth, c.FilterRadius, c.Size.X, c.Size.Y));
            Put(data, ExtentAt + i * 4, new Vector4(c.Size.Z, 1f / tile, 0, 0));
            Put(ms, i * 4, new Vector4(c.NearDepth, c.FarDepth, FilterTexels / tile, (float)c.Texel));
            Put(ms, ShadowSettings.MaxCascades * 4 + i * 4, new Vector4(world, c.Size.Z, (float)(SearchRadius(c) / world), sunTan / world));
        }

        Put(data, LightAt, cascades[0].Rotation);
        Put(data, OriginAt, new Vector4(origin, 1));
        Put(data, ForwardAt, new Vector4(Vector3.Normalize(view.Forward), cascades.Length));

        // x, y: one layer (no virtual atlas: cascade i is layer i); w: the Meitou receiver.
        Put(data, AtlasAt, new Vector4(_atlasSize, _atlasSize, 0, 1));
        const int p = MeitouShadowShaders.ParamsOffset;
        float noise = Temporal ? _meitouFrame % 64 * 5.588238f : 0;
        Put(ms, p, new Vector4(noise, range, range * 0.85f, range * 0.55f));
        bool terrain = _terrainMap?.Sampled is not null;
        float half = WorldLayout.HalfWorldSize;
        if (_terrainMap is { } map)
        {
            float horizontal = MathF.Sqrt(toSun.X * toSun.X + toSun.Z * toSun.Z);
            float cosElevation = Math.Max(horizontal, 0.05f);
            Put(ms, p + 4, new Vector4(-half, -half, 1 / map.Spacing, terrain ? 1 : 0));

            // The penumbra's height across: occluder distance × the sun's angular radius over the cosine of its height.
            Put(ms, p + 8, new Vector4(map.Size, 20, 6, sunTan / cosElevation));
        }

        Put(ms, p + 12, new Vector4(blockers ? 1 : 0, range * 0.9f, FullFilterCascades, 0));
        if (_landmarks.Box is { } box)
        {
            // The landmark map: (p - origin) -> (u, v, depth); its filter radius (UV) and normal offset (world: one and a half texels).
            Put(ms, p + 16, ReceiverTile(box, origin));
            Put(ms, p + 32, new Vector4(box.FilterRadius, (float)(box.Texel * 1.5), 0, 1));
            _landmarks.MarkPublished();
        }

        Upload(data);
        _meitouBlock.Set(ms);

        // The maps the receivers sample from now on (the GL code bound them to their units here; a unit kept what it held).
        if (terrain) _boundTerrain = _terrainMap!.Sampled;
        if (blockers) _blockers.MarkPublished();
    }

    /// <summary>
    /// Frees the terrain, blocker and landmark maps.
    /// </summary>
    void DisposeMeitou()
    {
        _terrainMap?.Dispose();
        _boundTerrain = null;
        _blockers.Dispose();
        _landmarks.Dispose();
    }

    #endregion
}
