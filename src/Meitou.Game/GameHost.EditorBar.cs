using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Data.World;
using Meitou.Rendering;
using Meitou.Rendering.Characters;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The Meitou UI's character editor scene (<see cref="EditorBar"/>, docs/characters.md "Character editor"): the world's renderers draw a copy of The Hub's bar at night
/// with the edited character in it and the others being edited sat behind, from the editor's orbit, on both hosts. The original's stage (<see cref="DrawEditorStage"/>) stays for the
/// faithful UI, and covers the bar until its parts are in.
/// </summary>
sealed partial class GameHost
{
    EditorBar? editorBar;
    /// <summary>No bar could be made for this opening of the editor (the data has none): the stage is used.</summary>
    bool editorBarMissing;
    /// <summary>The bar's parts and characters were in once: from then on the bar shows, not the stage over it.</summary>
    bool editorBarReady;
    /// <summary>The bar in the interior view with every floor shown, so its inner walls, furniture and roof are drawn.</summary>
    static readonly Dictionary<string, int> EditorBarView = new(StringComparer.Ordinal) { [EditorBar.Key] = 3 };
    /// <summary>The hour the bar is drawn at: night, its lamps lit.</summary>
    const float EditorBarHour = 23;
    /// <summary>
    /// How far the bar's frame reaches (<see cref="WorldFrame.Gpu.ViewCap"/>): the bar and its staged ground, short of the world under it (<see cref="EditorBar"/> stands it 3000
    /// above the ground), so the terrain, foliage, objects and shadow cascades of the world below are culled; the water and its reflection are off.
    /// </summary>
    const float EditorBarViewCap = 2500;

    bool EditorBarWanted => player.EditorOpen && player.UiMode == UiMode.Meitou && popData is not null;

    /// <summary>What <see cref="BeginEditorBar"/> changed for the world's draw: the camera and the weather, which <see cref="EndEditorBar"/> puts back.</summary>
    readonly record struct EditorBarFrame(Vector3 Target, float Yaw, float Pitch, float Distance, SkyWeather Sky, WorldWeather? Weather, bool WeatherParticles, Vector3 Fill, bool Water, bool Reflections);
    /// <summary>The neutral fill the characters take in the bar (<see cref="CharacterRenderer.Fill"/>): a little grey light all round, so faces read in the lamplight.</summary>
    static readonly Vector3 EditorBarCharacterFill = new(0.04f);
    /// <summary>The bar's own people for this opening of the editor (<see cref="EditorBar.Crowd"/>): their rolled looks, places, yaws and clips.</summary>
    readonly List<(Meitou.Data.Characters.CharacterAppearance Look, Vector3 At, float Yaw, string Clip)> editorCrowd = [];

    /// <summary>The Vulkan host's editor frame: the bar in the Meitou UI, else the original's stage.</summary>
    void DrawEditorScene(int width, int height)
    {
        if (BeginEditorBar(width, height) is not { } frame) return;
        try { WorldFrame.Draw(gpu, scene, camera, render, width, height, EditorBarHour, (float)(session.Clock.GameSeconds / 600 % 1e4), o.FogDistance); }
        finally { EndEditorBar(frame, width, height); }
    }

    /// <summary>
    /// Before the world's draw (both hosts): in the Meitou UI the bar is put up, the camera set on the editor's orbit in it and the sky made a clear night with no weather
    /// particles indoors and no placed fog; the world is then drawn at <see cref="EditorBarHour"/>. Null when the original's stage is drawn instead (drawn here).
    /// </summary>
    EditorBarFrame? BeginEditorBar(int width, int height)
    {
        if (!EditorBarWanted || EnsureEditorBar() is not { } bar)
        {
            ClearEditorBar();
            DrawEditorStage(width, height);
            return null;
        }
        if (!editorBarReady && EditorBarLoaded(bar)) editorBarReady = true;
        player.EditorInWorld = editorBarReady;
        var sky = gpu.Sky;
        var frame = new EditorBarFrame(camera.Target, camera.Yaw, camera.Pitch, camera.Distance, sky.Weather, gpu.Weather, gpu.WeatherParticles, gpu.Characters?.Fill ?? default, render.Water, render.Reflections);
        PlaceEditorCamera(bar, width, height);
        if (gpu.Characters is { } lit) lit.Fill = EditorBarCharacterFill;
        (gpu.ViewCap, render.Water, render.Reflections) = (EditorBarViewCap, false, false);
        if (ActiveObjects is { } objects) objects.InteriorView = EditorBarView;
        sky.Weather = SkyWeather.Default;
        gpu.Weather = null;
        gpu.WeatherParticles = false;
        if (gpu.FogVolumes is { } placedFog) placedFog.HidePlaced = true;
        return frame;
    }

    /// <summary>After the world's draw: what <see cref="BeginEditorBar"/> changed goes back, and the stage is drawn over the bar until its parts are in.</summary>
    void EndEditorBar(EditorBarFrame frame, int width, int height)
    {
        (camera.Target, camera.Yaw, camera.Pitch, camera.Distance) = (frame.Target, frame.Yaw, frame.Pitch, frame.Distance);
        (gpu.Sky.Weather, gpu.Weather, gpu.WeatherParticles) = (frame.Sky, frame.Weather, frame.WeatherParticles);
        if (gpu.FogVolumes is { } placedFog) placedFog.HidePlaced = false;
        if (gpu.Characters is { } lit) lit.Fill = frame.Fill;
        (gpu.ViewCap, render.Water, render.Reflections) = (null, frame.Water, frame.Reflections);
        if (editorBarReady) { if (gpu.Characters is { } characters) characters.StageRequest = null; }
        else DrawEditorStage(width, height, posed: true);
    }

    /// <summary>The bar for this opening of the editor, placed once above the character first shown: its parts join the objects and its lamps the lights.</summary>
    EditorBar? EnsureEditorBar()
    {
        if (editorBar is not null || editorBarMissing) return editorBar;
        if (popData is not { } data || scene.Database is not { } db || scene.Objects is not { } world || ActiveObjects is not { } objects ||
            player.EditorFocus is not { } focus || session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == focus.Character) is not { } shown) return null;
        if (EditorBar.Above(install, data, shown.Position, scene.GroundAt) is not { } bar || db.Find(bar.Placement.BuildingId) is not { } record)
        {
            editorBarMissing = true;
            return null;
        }
        editorBar = bar;
        objects.AddRuntime(bar.Placement);
        var lamps = WorldLights.ForBuilding(db, world.Levels, new ResolvedBuilding(bar.Placement, record, bar.Origin, false), world.Layouts);
        // The room's neutral fill: a white point light under the roof at a share of the lamps' mean power, lit day and night.
        float power = lamps.Count > 0 ? lamps.Average(l => l.Intensity) * EditorBar.FillShare : 1;
        lamps.Add(new WorldLight(bar.FillAt, Vector3.One, power, EditorBar.FillRadius, WorldLightType.Point, -Vector3.UnitY, 0, 0, 1, WorldLightEffect.None,
            WorldLightRule.WhenBuildingOn, true, 0, "meitou:editor-fill", record.StringId, EditorBar.Key));
        ActiveLamps?.SetRuntime(EditorBar.Key, lamps);
        objects.Staged = bar.Ground;
        editorCrowd.Clear();
        foreach (var (id, faction, at, yaw, clip, seed) in bar.Crowd)
            if (data.Appearances?.Create(id, faction, seed) is { } look)
            {
                editorCrowd.Add((look, at, yaw, clip));
                ActiveCharacters?.Prefetch(look);
            }
        return bar;
    }

    /// <summary>The editor has closed, or the bar is no longer wanted: its parts and lamps go.</summary>
    void ClearEditorBar()
    {
        player.EditorInWorld = false;
        (editorBarReady, editorBarLaidOut) = (false, null);
        if (!player.EditorOpen) editorBarMissing = false;
        if (editorBar is null) return;
        if (ActiveObjects is { } objects && objects.Staged == editorBar.Ground) objects.Staged = null;
        editorCrowd.Clear();
        editorBar = null;
        ActiveObjects?.RemoveRuntime(EditorBar.Key);
        ActiveLamps?.RemoveRuntime(EditorBar.Key);
    }

    /// <summary>
    /// The bar's room is laid out round the edited character and nothing is still streaming in or being prepared, or the room has stood for <see cref="EditorBarPatience"/>
    /// (the object and character counts are global: textures held back under memory pressure or looks warmed for later clicks can keep them up for good).
    /// </summary>
    bool EditorBarLoaded(EditorBar bar)
    {
        if (ActiveObjects?.InteriorAt(bar.Stand + new Vector3(0, 2, 0)) is not { PlacementId: EditorBar.Key }) return false;
        double now = realTime.Elapsed.TotalSeconds;
        editorBarLaidOut ??= now;
        return now - editorBarLaidOut >= EditorBarPatience ||
            gpu.Streamer is not { Idle: false } && !(gpu.Objects?.Pending > 0) && ActiveCharacters is { ContentPending: false };
    }

    /// <summary>When the bar's room was first found laid out (real seconds); null before.</summary>
    double? editorBarLaidOut;
    /// <summary>How long the stage covers a laid-out bar at most while its meshes and textures stream in.</summary>
    const double EditorBarPatience = 3;

    /// <summary>
    /// The world camera on the editor's orbit (the stage's eye and pivot, <see cref="CharacterRenderer.EditorCamera"/>), the pivot moved sideways on the picture to the middle of the
    /// space between the editor's panels (<see cref="EditorStageShift"/>) by moving the camera across, not by the projection.
    /// </summary>
    void PlaceEditorCamera(EditorBar bar, int width, int height)
    {
        if (player.EditorView() is not { } view) return;
        var request = new EditorStageRequest(DrawKey(view.Character), width, height, view.Yaw, view.Elevation, view.Distance, view.Head, camera.FieldOfView, EditorStageShift);
        var (eye, pivot) = gpu.Characters?.EditorCamera(request.Key, in request) ?? StandCamera(bar, in request);
        var back = eye - pivot;
        float length = MathF.Max(back.Length(), 1e-3f);
        back /= length;
        camera.Distance = length;
        camera.Pitch = MathF.Asin(Math.Clamp(back.Y, -1, 1));
        camera.Yaw = MathF.Atan2(back.X, back.Z);
        var right = Vector3.Normalize(Vector3.Cross(-back, Vector3.UnitY));
        float across = -EditorStageShift * length * MathF.Tan(camera.FieldOfView / 2) * width / Math.Max(height, 1);
        camera.Target = pivot + right * across;
    }

    /// <summary>The stage's camera before the renderer has posed the character: the body view's pivot, 12 above where it stands.</summary>
    static (Vector3 Eye, Vector3 Pivot) StandCamera(EditorBar bar, in EditorStageRequest r)
    {
        var pivot = bar.Stand + new Vector3(0, 12, 0);
        var local = Vector3.Transform(new Vector3(0, 0, r.Distance), Quaternion.CreateFromAxisAngle(Vector3.UnitY, r.Yaw) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -r.Elevation));
        return (pivot + Vector3.Transform(local, Quaternion.CreateFromAxisAngle(Vector3.UnitY, bar.StandYaw)), pivot);
    }

    /// <summary>
    /// Where a character being edited is drawn in the bar: the shown one where it stands, the others (up to <see cref="EditorBar.MaxSeated"/>, in the editor's order) on its
    /// seats with their sitting clip; null when the bar is not up or the character is not one of them.
    /// </summary>
    (Vector3 Position, float Yaw, string? Clip)? EditorBarPlace(CharacterId id, CharacterId shown)
    {
        if (editorBar is not { } bar) return null;
        if (id == shown) return (bar.Stand, bar.StandYaw, null);
        int seat = 0;
        foreach (var other in player.EditorCharacters)
        {
            if (other == shown) continue;
            if (seat >= EditorBar.MaxSeated || seat >= bar.Seats.Count) return null;
            if (other == id) return (bar.Seats[seat].Position, bar.Seats[seat].Yaw, bar.Seats[seat].Clip);
            seat++;
        }
        return null;
    }

    /// <summary>The draw keys of the bar's own people: their own range, clear of the characters' and the title's.</summary>
    const long EditorCrowdKey = long.MinValue + (1L << 40);

    /// <summary>The bar's own people in the draw list, each holding its clip from a start of its own so they do not move in step.</summary>
    void AddEditorCrowd(CharacterDrawList list)
    {
        if (editorBar is null) return;
        float now = (float)realTime.Elapsed.TotalSeconds;
        for (int i = 0; i < editorCrowd.Count; i++)
        {
            var (look, at, yaw, clip) = editorCrowd[i];
            float speed = animationLibrary?.IndexOf(clip) is >= 0 and int k ? animationLibrary.Definitions[k].PlaySpeed : 0;
            list.Add(new CharacterInstance(EditorCrowdKey + i, look, at, yaw, [new CharacterPose(clip, (now + i * 3.7f) * speed, 1)]));
        }
    }
}
