using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Data.World;
using Meitou.Game.Rendering.Characters;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Sky;
using Meitou.Simulation;
using Meitou.Simulation.Movement;

namespace Meitou.Game.Editor;

/// <summary>
/// The Meitou UI's character editor scene (<see cref="Editor.EditorBar"/>, docs/characters.md "Character editor"): the world's renderers draw a copy
/// of The Hub's bar at night with the edited character in it and the others being edited sat behind, from the editor's orbit, on both hosts. The
/// original's stage (<see cref="CharacterEditorHost.DrawEditorStage"/>) stays for the faithful UI, and covers the bar until its parts are in.
/// </summary>
sealed class EditorBarScene(GameHost host, CharacterEditorHost editor)
{
    #region Bar

    /// <summary>
    /// The bar for this opening of the editor; null before it is put up or without one.
    /// </summary>
    internal EditorBar? EditorBar;

    /// <summary>
    /// No bar could be made for this opening of the editor (the data has none): the stage is used.
    /// </summary>
    bool _editorBarMissing;

    /// <summary>
    /// The bar's parts and characters were in once: from then on the bar shows, not the stage over it.
    /// </summary>
    internal bool EditorBarReady;

    /// <summary>
    /// The bar in the interior view with every floor shown, so its inner walls, furniture and roof are drawn.
    /// </summary>
    static readonly Dictionary<string, int> EditorBarView = new(StringComparer.Ordinal) { [EditorBar.Key] = 3 };

    /// <summary>
    /// The hour the bar is drawn at: night, its lamps lit.
    /// </summary>
    internal const float EditorBarHour = 23;
    /// <summary>
    /// How far the bar's frame reaches (<see cref="WorldGpu.ViewCap"/>): the bar and its staged ground, short of the world under it (<see
    /// cref="Editor.EditorBar"/> stands it 3000 above the ground), so the terrain, foliage, objects and shadow cascades of the world below are
    /// culled; the water and its reflection are off.
    /// </summary>
    const float EditorBarViewCap = 2500;

    /// <summary>
    /// The editor is open in the Meitou UI with the game data loaded: the bar is wanted.
    /// </summary>
    bool EditorBarWanted => host.Player.CharacterEditor.EditorOpen && host.Player.UiMode == UiMode.Meitou && host.PopData is not null;

    /// <summary>
    /// What <see cref="BeginEditorBar"/> changed for the world's draw: the camera and the weather, which <see cref="EndEditorBar"/> puts back.
    /// </summary>
    internal readonly record struct EditorBarFrame(Vector3 Target, float Yaw, float Pitch, float Distance, SkyWeather Sky, WorldWeather? Weather,
        bool WeatherParticles, Vector3 Fill, bool Water, bool Reflections);

    /// <summary>
    /// The neutral fill the characters take in the bar (<see cref="CharacterRenderer.Fill"/>): a little grey light all round, so faces read in the
    /// lamplight.
    /// </summary>
    static readonly Vector3 EditorBarCharacterFill = new(0.04f);

    /// <summary>
    /// When the bar's room was first found laid out (real seconds); null before.
    /// </summary>
    double? _editorBarLaidOut;

    /// <summary>
    /// How long the stage covers a laid-out bar at most while its meshes and textures stream in.
    /// </summary>
    const double EditorBarPatience = 3;

    /// <summary>
    /// The Vulkan host's editor frame: the bar in the Meitou UI, else the original's stage.
    /// </summary>
    internal void DrawEditorScene(int width, int height)
    {
        if (BeginEditorBar(width, height) is not { } frame) return;
        try
        {
            WorldFrame.Draw(host.Gpu, host.Scene, host.Camera, host.Render, width, height, EditorBarHour,
                (float)(host.Session.Clock.GameSeconds / 600 % 1e4), host.WorldOptions.FogDistance);
        }
        finally
        {
            EndEditorBar(frame, width, height);
        }
    }

    /// <summary>
    /// Before the world's draw (both hosts): in the Meitou UI the bar is put up, the camera set on the editor's orbit in it and the sky made a clear
    /// night with no weather particles indoors and no placed fog; the world is then drawn at <see cref="EditorBarHour"/>. Null when the original's
    /// stage is drawn instead (drawn here).
    /// </summary>
    internal EditorBarFrame? BeginEditorBar(int width, int height)
    {
        if (!EditorBarWanted || EnsureEditorBar() is not { } bar)
        {
            ClearEditorBar();
            editor.DrawEditorStage(width, height);
            return null;
        }
        if (!EditorBarReady && EditorBarLoaded(bar)) EditorBarReady = true;
        host.Player.CharacterEditor.EditorInWorld = EditorBarReady;
        var sky = host.Gpu.Sky;
        var frame = new EditorBarFrame(host.Camera.Target, host.Camera.Yaw, host.Camera.Pitch, host.Camera.Distance, sky.Weather, host.Gpu.Weather,
            host.Gpu.WeatherParticles, host.Gpu.Characters?.Fill ?? default, host.Render.Water, host.Render.Reflections);
        PlaceEditorCamera(bar, width, height);
        if (host.Gpu.Characters is { } lit) lit.Fill = EditorBarCharacterFill;
        (host.Gpu.ViewCap, host.Render.Water, host.Render.Reflections) = (EditorBarViewCap, false, false);
        if (host.ActiveObjects is { } objects) objects.InteriorView = EditorBarView;
        sky.Weather = SkyWeather.Default;
        host.Gpu.Weather = null;
        host.Gpu.WeatherParticles = false;
        if (host.Gpu.FogVolumes is { } placedFog) placedFog.HidePlaced = true;
        return frame;
    }

    /// <summary>
    /// After the world's draw: what <see cref="BeginEditorBar"/> changed goes back, and the stage is drawn over the bar until its parts are in.
    /// </summary>
    internal void EndEditorBar(EditorBarFrame frame, int width, int height)
    {
        (host.Camera.Target, host.Camera.Yaw, host.Camera.Pitch, host.Camera.Distance) = (frame.Target, frame.Yaw, frame.Pitch, frame.Distance);
        (host.Gpu.Sky.Weather, host.Gpu.Weather, host.Gpu.WeatherParticles) = (frame.Sky, frame.Weather, frame.WeatherParticles);
        if (host.Gpu.FogVolumes is { } placedFog) placedFog.HidePlaced = false;
        if (host.Gpu.Characters is { } lit) lit.Fill = frame.Fill;
        (host.Gpu.ViewCap, host.Render.Water, host.Render.Reflections) = (null, frame.Water, frame.Reflections);
        if (EditorBarReady) { if (host.Gpu.Characters is { } characters) characters.StageRequest = null; }
        else editor.DrawEditorStage(width, height, posed: true);
    }

    /// <summary>
    /// The bar for this opening of the editor, placed once above the character first shown: its parts join the objects and its lamps the lights.
    /// </summary>
    EditorBar? EnsureEditorBar()
    {
        if (EditorBar is not null || _editorBarMissing) return EditorBar;
        if (host.PopData is not { } data || host.Scene.Database is not { } db || host.Scene.Objects is not { } world
            || host.ActiveObjects is not { } objects || host.Player.CharacterEditor.EditorFocus is not { } focus
            || host.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == focus.Character) is not { } shown) return null;
        if (EditorBar.Above(host.Install, data, shown.Position, host.Scene.GroundAt) is not { } bar
                || db.Find(bar.Placement.BuildingId) is not { } record)
        {
            _editorBarMissing = true;
            return null;
        }
        EditorBar = bar;
        objects.AddRuntime(bar.Placement);
        var lamps = WorldLights.ForBuilding(db, world.Levels, new ResolvedBuilding(bar.Placement, record, bar.Origin, false), world.Layouts);
        // The room's neutral fill: a white point light under the roof at a share of the lamps' mean power, lit day and night.
        float power = lamps.Count > 0 ? lamps.Average(l => l.Intensity) * EditorBar.FillShare : 1;
        lamps.Add(new WorldLight(bar.FillAt, Vector3.One, power, EditorBar.FillRadius, WorldLightType.Point, -Vector3.UnitY, 0, 0, 1,
            WorldLightEffect.None, WorldLightRule.WhenBuildingOn, true, 0, "meitou:editor-fill", record.StringId, EditorBar.Key));
        host.ActiveLamps?.SetRuntime(EditorBar.Key, lamps);
        objects.Staged = bar.Ground;
        _editorCrowd.Clear();
        foreach (var (id, faction, at, yaw, clip, seed) in bar.Crowd)
            if (data.Appearances?.Create(id, faction, seed) is { } look)
            {
                _editorCrowd.Add((look, at, yaw, clip));
                host.ActiveCharacters?.Prefetch(look);
            }
        return bar;
    }

    /// <summary>
    /// The editor has closed, or the bar is no longer wanted: its parts and lamps go.
    /// </summary>
    internal void ClearEditorBar()
    {
        host.Player.CharacterEditor.EditorInWorld = false;
        (EditorBarReady, _editorBarLaidOut) = (false, null);
        if (!host.Player.CharacterEditor.EditorOpen) _editorBarMissing = false;
        if (EditorBar is null) return;
        if (host.ActiveObjects is { } objects && objects.Staged == EditorBar.Ground) objects.Staged = null;
        _editorCrowd.Clear();
        EditorBar = null;
        host.ActiveObjects?.RemoveRuntime(EditorBar.Key);
        host.ActiveLamps?.RemoveRuntime(EditorBar.Key);
    }

    /// <summary>
    /// The bar's room is laid out round the edited character and nothing is still streaming in or being prepared, or the room has stood for <see
    /// cref="EditorBarPatience"/> (the object and character counts are global: textures held back under memory pressure or looks warmed for later
    /// clicks can keep them up for good).
    /// </summary>
    bool EditorBarLoaded(EditorBar bar)
    {
        if (host.ActiveObjects?.InteriorAt(bar.Stand + new Vector3(0, 2, 0)) is not { PlacementId: EditorBar.Key }) return false;
        double now = host.RealTime.Elapsed.TotalSeconds;
        _editorBarLaidOut ??= now;
        return now - _editorBarLaidOut >= EditorBarPatience ||
            host.Gpu.Streamer is not { Idle: false } && !(host.Gpu.Objects?.Pending > 0) && host.ActiveCharacters is { ContentPending: false };
    }

    /// <summary>
    /// The world camera on the editor's orbit (the stage's eye and pivot, <see cref="CharacterRenderer.EditorCamera"/>), the pivot moved sideways on
    /// the picture to the middle of the space between the editor's panels (<see cref="CharacterEditorHost.EditorStageShift"/>) by moving the camera
    /// across, not by the projection.
    /// </summary>
    void PlaceEditorCamera(EditorBar bar, int width, int height)
    {
        if (host.Player.CharacterEditor.EditorView() is not { } view) return;
        var request = new EditorStageRequest(host.DrawKey(view.Character), width, height, view.Yaw, view.Elevation, view.Distance, view.Head,
            host.Camera.FieldOfView, CharacterEditorHost.EditorStageShift);
        var (eye, pivot) = host.Gpu.Characters?.EditorCamera(request.Key, in request) ?? StandCamera(bar, in request);
        var back = eye - pivot;
        float length = MathF.Max(back.Length(), 1e-3f);
        back /= length;
        host.Camera.Distance = length;
        host.Camera.Pitch = MathF.Asin(Math.Clamp(back.Y, -1, 1));
        host.Camera.Yaw = MathF.Atan2(back.X, back.Z);
        var right = Vector3.Normalize(Vector3.Cross(-back, Vector3.UnitY));
        float across = -CharacterEditorHost.EditorStageShift * length * MathF.Tan(host.Camera.FieldOfView / 2) * width / Math.Max(height, 1);
        host.Camera.Target = pivot + right * across;
    }

    /// <summary>
    /// The stage's camera before the renderer has posed the character: the body view's pivot, 12 above where it stands.
    /// </summary>
    static (Vector3 Eye, Vector3 Pivot) StandCamera(EditorBar bar, in EditorStageRequest r)
    {
        var pivot = bar.Stand + new Vector3(0, 12, 0);
        var local = Vector3.Transform(new Vector3(0, 0, r.Distance),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, r.Yaw) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -r.Elevation));
        return (pivot + Vector3.Transform(local, Quaternion.CreateFromAxisAngle(Vector3.UnitY, bar.StandYaw)), pivot);
    }

    /// <summary>
    /// Where a character being edited is drawn in the bar: the shown one where it stands, the other people (up to <see cref="EditorBar.MaxSeated"/>,
    /// in the editor's order) on its seats with their sitting clip; null when the bar is not up or the character is not one of them. An animal not
    /// shown is not drawn (it has no sitting clip).
    /// </summary>
    internal (Vector3 Position, float Yaw, string? Clip)? EditorBarPlace(CharacterId id, CharacterId shown)
    {
        if (EditorBar is not { } bar) return null;
        if (id == shown) return (bar.Stand, bar.StandYaw, null);
        int seat = 0;
        foreach (var other in host.Player.CharacterEditor.EditorCharacters)
        {
            if (other == shown || host.Player.CharacterEditor.IsEditorAnimal(other)) continue;
            if (seat >= EditorBar.MaxSeated || seat >= bar.Seats.Count) return null;
            if (other == id) return (bar.Seats[seat].Position, bar.Seats[seat].Yaw, bar.Seats[seat].Clip);
            seat++;
        }
        return null;
    }

    #endregion

    #region Crowd

    /// <summary>
    /// The bar's own people for this opening of the editor (<see cref="EditorBar.Crowd"/>): their rolled looks, places, yaws and clips.
    /// </summary>
    readonly List<(Data.Characters.CharacterAppearance Look, Vector3 At, float Yaw, string Clip)> _editorCrowd = [];

    /// <summary>
    /// The draw keys of the bar's own people: their own range, clear of the characters' and the title's.
    /// </summary>
    const long EditorCrowdKey = long.MinValue + (1L << 40);

    /// <summary>
    /// The bar's own people in the draw list, each holding its clip from a start of its own so they do not move in step.
    /// </summary>
    internal void AddEditorCrowd(CharacterDrawList list)
    {
        if (EditorBar is null) return;
        float now = (float)host.RealTime.Elapsed.TotalSeconds;
        for (int i = 0; i < _editorCrowd.Count; i++)
        {
            var (look, at, yaw, clip) = _editorCrowd[i];
            list.Add(new CharacterInstance(EditorCrowdKey + i, look, at, yaw, [new CharacterPose(clip, EditorClipTime(clip, now + i * 3.7f), 1)]));
        }
    }

    /// <summary>
    /// The time a clip held in the bar (a seat's sitting clip, a standing patron's stance) is drawn at, <paramref name="seconds"/> after it started:
    /// at its record's play speed, a one-shot clip (no <c>loop</c>) held at its last frame as the simulation holds one (<see
    /// cref="AnimationSystem"/>), not wrapped round by the renderer. A seat whose clip sits down (mods' sitting animations) would otherwise stand up
    /// and sit down again over and over (issue #242).
    /// </summary>
    internal float EditorClipTime(string clip, float seconds)
    {
        if (host.AnimationLibrary?.IndexOf(clip) is not (>= 0 and int k)) return 0;
        var d = host.AnimationLibrary.Definitions[k];
        float time = seconds * d.PlaySpeed;
        if (d.Loop) return time;
        float length = host.AnimationLengths?.Of(d.Clip) ?? 1;
        return Math.Clamp(time, 0, length * 0.999f);
    }
    #endregion
}
