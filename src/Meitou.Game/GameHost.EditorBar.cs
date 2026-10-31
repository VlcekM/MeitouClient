using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Data.World;
using Meitou.Rendering;
using Meitou.Rendering.Characters;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The Meitou UI's character editor scene (<see cref="EditorBar"/>, docs/characters.md "Character editor"): the world's renderers draw a copy of The Hub's bar at night
/// with the edited character in it and the others being edited sat behind, from the editor's orbit. The original's stage (<see cref="DrawEditorStage"/>) stays for the
/// faithful UI and the Metal renderer, and covers the bar until its parts are in.
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

    bool EditorBarWanted => player.EditorOpen && player.UiMode == UiMode.Meitou && !metalRenderer && popData is not null;

    /// <summary>The editor's frame: the bar in the Meitou UI, else the original's stage.</summary>
    void DrawEditorScene(int width, int height)
    {
        if (!EditorBarWanted || EnsureEditorBar() is not { } bar)
        {
            ClearEditorBar();
            DrawEditorStage(width, height);
            return;
        }
        if (!editorBarReady && EditorBarLoaded(bar)) editorBarReady = true;
        player.EditorInWorld = editorBarReady;
        var (target, yaw, pitch, distance) = (camera.Target, camera.Yaw, camera.Pitch, camera.Distance);
        try
        {
            PlaceEditorCamera(bar, width, height);
            DrawEditorBar(width, height);
        }
        finally { (camera.Target, camera.Yaw, camera.Pitch, camera.Distance) = (target, yaw, pitch, distance); }
        if (editorBarReady) { if (gpu.Characters is { } characters) characters.StageRequest = null; }
        else DrawEditorStage(width, height);   // the stage over the bar until its parts are in
    }

    /// <summary>The bar for this opening of the editor, placed once above the character first shown: its parts join the objects and its lamps the lights.</summary>
    EditorBar? EnsureEditorBar()
    {
        if (editorBar is not null || editorBarMissing) return editorBar;
        if (popData is not { } data || scene.Database is not { } db || scene.Objects is not { } world || ActiveObjects is not { } objects ||
            player.EditorFocus is not { } focus || session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == focus.Character) is not { } shown) return null;
        if (EditorBar.Above(data, shown.Position, scene.GroundAt) is not { } bar || db.Find(bar.Placement.BuildingId) is not { } record)
        {
            editorBarMissing = true;
            return null;
        }
        editorBar = bar;
        objects.AddRuntime(bar.Placement);
        ActiveLamps?.SetRuntime(EditorBar.Key, WorldLights.ForBuilding(db, world.Levels, new ResolvedBuilding(bar.Placement, record, bar.Origin, false), world.Layouts));
        return bar;
    }

    /// <summary>The editor has closed, or the bar is no longer wanted: its parts and lamps go.</summary>
    void ClearEditorBar()
    {
        player.EditorInWorld = false;
        editorBarReady = false;
        if (!player.EditorOpen) editorBarMissing = false;
        if (editorBar is null) return;
        editorBar = null;
        ActiveObjects?.RemoveRuntime(EditorBar.Key);
        ActiveLamps?.RemoveRuntime(EditorBar.Key);
    }

    /// <summary>The bar's room is laid out round the edited character, nothing is still streaming in and every character in it is prepared.</summary>
    bool EditorBarLoaded(EditorBar bar) =>
        ActiveObjects?.InteriorAt(bar.Stand + new Vector3(0, 2, 0)) is { PlacementId: EditorBar.Key } && gpu.Streamer is not { Idle: false } && !(gpu.Objects?.Pending > 0) &&
        ActiveCharacters is { ContentPending: false };

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

    /// <summary>The world's frame at night under a clear sky, the bar open: no weather particles fall indoors and no placed fog drifts in.</summary>
    void DrawEditorBar(int width, int height)
    {
        if (ActiveObjects is { } objects) objects.InteriorView = EditorBarView;
        var sky = gpu.Sky;
        var (weather, regions, particles) = (sky.Weather, gpu.Weather, gpu.WeatherParticles);
        sky.Weather = SkyWeather.Default;
        gpu.Weather = null;
        gpu.WeatherParticles = false;
        if (gpu.FogVolumes is { } placedFog) placedFog.HidePlaced = true;
        try { WorldFrame.Draw(gpu, scene, camera, render, width, height, EditorBarHour, (float)(session.Clock.GameSeconds / 600 % 1e4), o.FogDistance); }
        finally
        {
            (sky.Weather, gpu.Weather, gpu.WeatherParticles) = (weather, regions, particles);
            if (gpu.FogVolumes is { } restoreFog) restoreFog.HidePlaced = false;
        }
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
}
