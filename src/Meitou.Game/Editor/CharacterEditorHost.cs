using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Data.World;
using Meitou.Game.Rendering.Characters;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Sky;
using Meitou.Simulation;
using Meitou.Simulation.Editing;
using Meitou.Simulation.Movement;

namespace Meitou.Game.Editor;

/// <summary>
/// The character editor's host side (docs/characters.md "Character editor"): its data and preview source, opening it for a new game's squad once the world is shown,
/// and its scene, which replaces the world while the editor is open.
/// </summary>
sealed class CharacterEditorHost(GameHost host)
{
    /// <summary>The new game's squad, waiting for the loading screen to lift before the editor opens on it.</summary>
    internal List<CharacterId>? pendingCreation;

    internal void ConfigureEditor(PlayerInterface ui)
    {
        if (host.popData is not { } data) return;
        ui.characterEditor.EditorData = (data.Db, host.install.Root);
        ui.characterEditor.PreviewSource = (recordId, loadout) => data.Appearances?.Create(recordId, loadout);
        ui.characterEditor.PreviewReady = look => host.ActiveCharacters?.IsPrepared(look) ?? true;
        // The editor's looks are close-ups: their meshes without generated levels (CharacterContent.PrepareMesh), so a new hair or head shows at once, as in the original.
        ui.characterEditor.PreviewFetch = look => host.ActiveCharacters?.PrefetchCloseUp(look);
        ui.characterEditor.PreviewWarm = (look, urgent) => host.ActiveCharacters?.Warm(look, urgent, close: true);
    }

    /// <summary>Opens the editor on the new game's squad once the world shows and the squad is in the snapshot.</summary>
    internal void OpenPendingEditor()
    {
        if (pendingCreation is not { } squad || host.sceneLoad is not null || host.warming) return;
        if (!squad.All(id => host.session.CurrentSnapshot.Characters.Any(c => c.Id == id))) return;
        pendingCreation = null;
        creationWarmOwed = true;
        if (!host.player.characterEditor.OpenEditor(EditKind.Creation, squad)) host.session.World.Commands.Enqueue(new FinishCreation { Tick = host.session.World.Tick });
    }

    /// <summary>The world has not been warmed up yet: the new game's editor came first (see <see cref="GameHost.CoveredFrame"/>).</summary>
    bool creationWarmOwed;

    /// <summary>
    /// Once the new game's editor has closed: the edits are applied by a tick, then the loading screen comes back while the world warms up, as a load without an editor does.
    /// </summary>
    internal void ResumeAfterCreation()
    {
        if (!creationWarmOwed || pendingCreation is not null || host.player.characterEditor.EditorOpen || host.sceneLoad is not null) return;
        creationWarmOwed = false;
        if (host.playerSquad is { Leader.IsNone: false } squad) host.player.Follow = squad.Leader;   // a new game starts with the camera locked to the leader
        if (host.gpu is null || host.metalRenderer)
        {
            // Metal has no Vulkan loading/warm loop here: the world was loaded and drawn under the editor already. Apply the edits at once.
            host.session.World.RunTick();
            return;
        }
        if (host.loading is null) return;
        host.session.World.RunTick();
        host.loading.Restart();
        host.loadingSteps = GameHost.SceneSteps;
        host.BeginWarm();
    }

    /// <summary>
    /// While the editor is open the world camera stays on the edited character, so the world around it is there when the editor closes; what the editor shows is its own scene
    /// (<see cref="DrawEditorStage"/>).
    /// </summary>
    internal void EditorCamera()
    {
        if (host.player.characterEditor.EditorFocus is not { } focus || host.session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == focus.Character) is not { } c) return;
        host.camera.Target = host.DrawnPosition(c) + new Vector3(0, 10, 0);
    }

    /// <summary>Where the character stands on the picture, in normalised device units: the middle of the space between the editor's two panels (the left one ends at 16 % of the width, the right one starts at 70 %), not of the window.</summary>
    const float EditorStageShift = (0.5f * (0.161458f + 0.7f)) * 2 - 1;

    /// <summary>
    /// The frame of the character editor, in place of the world's: the original draws the edited character alone, lit plainly, over the plain ground texture
    /// (compositor <c>Kenshi_Character_Editor</c>, docs/characters.md). The renderer draws the character into the stage picture from the editor's orbit; the interface puts
    /// <c>SandyGround.dds</c> and the picture behind its panels (<c>PlayerInterface.EditorBackdrop</c>).
    /// <paramref name="posed"/>: the world pass has just updated the characters this frame (the Meitou bar's), so they are not updated again.
    /// </summary>
    void DrawEditorStage(int width, int height, bool posed = false)
    {
        if (host.ActiveCharacters is not { } characters) return;
        editorStaged = true;
        if (host.player.characterEditor.EditorView() is not { } view || host.session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == view.Character) is not { } c) { characters.StageRequest = null; return; }
        characters.StageRequest = new EditorStageRequest(host.DrawKey(view.Character), width, height, view.Yaw, view.Elevation, view.Distance, view.Head, host.camera.FieldOfView, EditorStageShift);
        characters.SetView(width, height, host.camera.FieldOfView);
        if (!posed) characters.Update(host.DrawnPosition(c));
        characters.DrawStage();
        DrawEditorPlatform(view.Character, characters);
        characters.ResolveStage();
        if (characters.StageTexture is { } stage) host.player.SetUiTexture(CharacterEditorPanel.StageTexture, stage.Texture, stage.Sampled);
    }

    /// <summary>The character the stage's platform was placed for (it is placed once per character, where the character stands when first shown).</summary>
    CharacterId? editorPlatformFor;

    /// <summary>
    /// The platform under the edited character (<see cref="Meitou.Data.World.WorldObjectLayout.EditorPlatformMesh"/>): its stage frame is the character's turned half round, as the
    /// original stands the characters on it turned so. The objects keep streaming meanwhile (the world's frame, which would, is not drawn), and the platform is drawn into the stage picture.
    /// </summary>
    void DrawEditorPlatform(CharacterId character, CharacterRenderer characters)
    {
        if (host.ActiveObjects is not { } objects || host.popData is not { } data || characters.StageView is not { } stage || characters.StageDrawTarget is not { } target) return;
        if (editorPlatformFor != character)
        {
            editorPlatformFor = character;
            objects.SetStageProp(Meitou.Data.World.WorldObjectLayout.EditorPlatformMesh(data.Db, Matrix4x4.CreateRotationY(MathF.PI) * stage.Feet));
        }
        objects.Update(stage.Eye);
        objects.DrawStageProp(target.Colour, target.Depth, stage.ViewProjection, stage.Eye, stage.Light, target.FogDistance);
    }

    /// <summary>Whether the editor's stage was drawn since it last closed (<see cref="ClearEditorStage"/>).</summary>
    bool editorStaged;

    /// <summary>The editor has closed: the platform goes, and the close-up looks warmed for it and never shown are dropped.</summary>
    internal void ClearEditorStage()
    {
        if (!editorStaged) return;
        editorStaged = false;
        host.ActiveCharacters?.ReleaseCloseUps();
        if (editorPlatformFor is null) return;
        editorPlatformFor = null;
        host.ActiveObjects?.SetStageProp(null);
    }

    // The Meitou UI's character editor scene (EditorBar, docs/characters.md "Character editor"): the world's renderers draw a copy of The Hub's bar at night
    // with the edited character in it and the others being edited sat behind, from the editor's orbit, on both hosts. The original's stage (DrawEditorStage) stays for the
    // faithful UI, and covers the bar until its parts are in.
    internal EditorBar? editorBar;
    /// <summary>No bar could be made for this opening of the editor (the data has none): the stage is used.</summary>
    bool editorBarMissing;
    /// <summary>The bar's parts and characters were in once: from then on the bar shows, not the stage over it.</summary>
    internal bool editorBarReady;
    /// <summary>The bar in the interior view with every floor shown, so its inner walls, furniture and roof are drawn.</summary>
    static readonly Dictionary<string, int> EditorBarView = new(StringComparer.Ordinal) { [EditorBar.Key] = 3 };
    /// <summary>The hour the bar is drawn at: night, its lamps lit.</summary>
    internal const float EditorBarHour = 23;
    /// <summary>
    /// How far the bar's frame reaches (<see cref="WorldFrame.Gpu.ViewCap"/>): the bar and its staged ground, short of the world under it (<see cref="EditorBar"/> stands it 3000
    /// above the ground), so the terrain, foliage, objects and shadow cascades of the world below are culled; the water and its reflection are off.
    /// </summary>
    const float EditorBarViewCap = 2500;

    bool EditorBarWanted => host.player.characterEditor.EditorOpen && host.player.UiMode == UiMode.Meitou && host.popData is not null;

    /// <summary>What <see cref="BeginEditorBar"/> changed for the world's draw: the camera and the weather, which <see cref="EndEditorBar"/> puts back.</summary>
    internal readonly record struct EditorBarFrame(Vector3 Target, float Yaw, float Pitch, float Distance, SkyWeather Sky, WorldWeather? Weather, bool WeatherParticles, Vector3 Fill, bool Water, bool Reflections);
    /// <summary>The neutral fill the characters take in the bar (<see cref="CharacterRenderer.Fill"/>): a little grey light all round, so faces read in the lamplight.</summary>
    static readonly Vector3 EditorBarCharacterFill = new(0.04f);
    /// <summary>The bar's own people for this opening of the editor (<see cref="EditorBar.Crowd"/>): their rolled looks, places, yaws and clips.</summary>
    readonly List<(Meitou.Data.Characters.CharacterAppearance Look, Vector3 At, float Yaw, string Clip)> editorCrowd = [];

    /// <summary>The Vulkan host's editor frame: the bar in the Meitou UI, else the original's stage.</summary>
    internal void DrawEditorScene(int width, int height)
    {
        if (BeginEditorBar(width, height) is not { } frame) return;
        try { WorldFrame.Draw(host.gpu, host.scene, host.camera, host.render, width, height, EditorBarHour, (float)(host.session.Clock.GameSeconds / 600 % 1e4), host.o.FogDistance); }
        finally { EndEditorBar(frame, width, height); }
    }

    /// <summary>
    /// Before the world's draw (both hosts): in the Meitou UI the bar is put up, the camera set on the editor's orbit in it and the sky made a clear night with no weather
    /// particles indoors and no placed fog; the world is then drawn at <see cref="EditorBarHour"/>. Null when the original's stage is drawn instead (drawn here).
    /// </summary>
    internal EditorBarFrame? BeginEditorBar(int width, int height)
    {
        if (!EditorBarWanted || EnsureEditorBar() is not { } bar)
        {
            ClearEditorBar();
            DrawEditorStage(width, height);
            return null;
        }
        if (!editorBarReady && EditorBarLoaded(bar)) editorBarReady = true;
        host.player.characterEditor.EditorInWorld = editorBarReady;
        var sky = host.gpu.Sky;
        var frame = new EditorBarFrame(host.camera.Target, host.camera.Yaw, host.camera.Pitch, host.camera.Distance, sky.Weather, host.gpu.Weather, host.gpu.WeatherParticles, host.gpu.Characters?.Fill ?? default, host.render.Water, host.render.Reflections);
        PlaceEditorCamera(bar, width, height);
        if (host.gpu.Characters is { } lit) lit.Fill = EditorBarCharacterFill;
        (host.gpu.ViewCap, host.render.Water, host.render.Reflections) = (EditorBarViewCap, false, false);
        if (host.ActiveObjects is { } objects) objects.InteriorView = EditorBarView;
        sky.Weather = SkyWeather.Default;
        host.gpu.Weather = null;
        host.gpu.WeatherParticles = false;
        if (host.gpu.FogVolumes is { } placedFog) placedFog.HidePlaced = true;
        return frame;
    }

    /// <summary>After the world's draw: what <see cref="BeginEditorBar"/> changed goes back, and the stage is drawn over the bar until its parts are in.</summary>
    internal void EndEditorBar(EditorBarFrame frame, int width, int height)
    {
        (host.camera.Target, host.camera.Yaw, host.camera.Pitch, host.camera.Distance) = (frame.Target, frame.Yaw, frame.Pitch, frame.Distance);
        (host.gpu.Sky.Weather, host.gpu.Weather, host.gpu.WeatherParticles) = (frame.Sky, frame.Weather, frame.WeatherParticles);
        if (host.gpu.FogVolumes is { } placedFog) placedFog.HidePlaced = false;
        if (host.gpu.Characters is { } lit) lit.Fill = frame.Fill;
        (host.gpu.ViewCap, host.render.Water, host.render.Reflections) = (null, frame.Water, frame.Reflections);
        if (editorBarReady) { if (host.gpu.Characters is { } characters) characters.StageRequest = null; }
        else DrawEditorStage(width, height, posed: true);
    }

    /// <summary>The bar for this opening of the editor, placed once above the character first shown: its parts join the objects and its lamps the lights.</summary>
    EditorBar? EnsureEditorBar()
    {
        if (editorBar is not null || editorBarMissing) return editorBar;
        if (host.popData is not { } data || host.scene.Database is not { } db || host.scene.Objects is not { } world || host.ActiveObjects is not { } objects ||
            host.player.characterEditor.EditorFocus is not { } focus || host.session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == focus.Character) is not { } shown) return null;
        if (EditorBar.Above(host.install, data, shown.Position, host.scene.GroundAt) is not { } bar || db.Find(bar.Placement.BuildingId) is not { } record)
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
        host.ActiveLamps?.SetRuntime(EditorBar.Key, lamps);
        objects.Staged = bar.Ground;
        editorCrowd.Clear();
        foreach (var (id, faction, at, yaw, clip, seed) in bar.Crowd)
            if (data.Appearances?.Create(id, faction, seed) is { } look)
            {
                editorCrowd.Add((look, at, yaw, clip));
                host.ActiveCharacters?.Prefetch(look);
            }
        return bar;
    }

    /// <summary>The editor has closed, or the bar is no longer wanted: its parts and lamps go.</summary>
    internal void ClearEditorBar()
    {
        host.player.characterEditor.EditorInWorld = false;
        (editorBarReady, editorBarLaidOut) = (false, null);
        if (!host.player.characterEditor.EditorOpen) editorBarMissing = false;
        if (editorBar is null) return;
        if (host.ActiveObjects is { } objects && objects.Staged == editorBar.Ground) objects.Staged = null;
        editorCrowd.Clear();
        editorBar = null;
        host.ActiveObjects?.RemoveRuntime(EditorBar.Key);
        host.ActiveLamps?.RemoveRuntime(EditorBar.Key);
    }

    /// <summary>
    /// The bar's room is laid out round the edited character and nothing is still streaming in or being prepared, or the room has stood for <see cref="EditorBarPatience"/>
    /// (the object and character counts are global: textures held back under memory pressure or looks warmed for later clicks can keep them up for good).
    /// </summary>
    bool EditorBarLoaded(EditorBar bar)
    {
        if (host.ActiveObjects?.InteriorAt(bar.Stand + new Vector3(0, 2, 0)) is not { PlacementId: EditorBar.Key }) return false;
        double now = host.realTime.Elapsed.TotalSeconds;
        editorBarLaidOut ??= now;
        return now - editorBarLaidOut >= EditorBarPatience ||
            host.gpu.Streamer is not { Idle: false } && !(host.gpu.Objects?.Pending > 0) && host.ActiveCharacters is { ContentPending: false };
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
        if (host.player.characterEditor.EditorView() is not { } view) return;
        var request = new EditorStageRequest(host.DrawKey(view.Character), width, height, view.Yaw, view.Elevation, view.Distance, view.Head, host.camera.FieldOfView, EditorStageShift);
        var (eye, pivot) = host.gpu.Characters?.EditorCamera(request.Key, in request) ?? StandCamera(bar, in request);
        var back = eye - pivot;
        float length = MathF.Max(back.Length(), 1e-3f);
        back /= length;
        host.camera.Distance = length;
        host.camera.Pitch = MathF.Asin(Math.Clamp(back.Y, -1, 1));
        host.camera.Yaw = MathF.Atan2(back.X, back.Z);
        var right = Vector3.Normalize(Vector3.Cross(-back, Vector3.UnitY));
        float across = -EditorStageShift * length * MathF.Tan(host.camera.FieldOfView / 2) * width / Math.Max(height, 1);
        host.camera.Target = pivot + right * across;
    }

    /// <summary>The stage's camera before the renderer has posed the character: the body view's pivot, 12 above where it stands.</summary>
    static (Vector3 Eye, Vector3 Pivot) StandCamera(EditorBar bar, in EditorStageRequest r)
    {
        var pivot = bar.Stand + new Vector3(0, 12, 0);
        var local = Vector3.Transform(new Vector3(0, 0, r.Distance), Quaternion.CreateFromAxisAngle(Vector3.UnitY, r.Yaw) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -r.Elevation));
        return (pivot + Vector3.Transform(local, Quaternion.CreateFromAxisAngle(Vector3.UnitY, bar.StandYaw)), pivot);
    }

    /// <summary>
    /// Where a character being edited is drawn in the bar: the shown one where it stands, the other people (up to <see cref="EditorBar.MaxSeated"/>, in the editor's order) on
    /// its seats with their sitting clip; null when the bar is not up or the character is not one of them. An animal not shown is not drawn (it has no sitting clip).
    /// </summary>
    internal (Vector3 Position, float Yaw, string? Clip)? EditorBarPlace(CharacterId id, CharacterId shown)
    {
        if (editorBar is not { } bar) return null;
        if (id == shown) return (bar.Stand, bar.StandYaw, null);
        int seat = 0;
        foreach (var other in host.player.characterEditor.EditorCharacters)
        {
            if (other == shown || host.player.characterEditor.IsEditorAnimal(other)) continue;
            if (seat >= EditorBar.MaxSeated || seat >= bar.Seats.Count) return null;
            if (other == id) return (bar.Seats[seat].Position, bar.Seats[seat].Yaw, bar.Seats[seat].Clip);
            seat++;
        }
        return null;
    }

    /// <summary>The draw keys of the bar's own people: their own range, clear of the characters' and the title's.</summary>
    const long EditorCrowdKey = long.MinValue + (1L << 40);

    /// <summary>The bar's own people in the draw list, each holding its clip from a start of its own so they do not move in step.</summary>
    internal void AddEditorCrowd(CharacterDrawList list)
    {
        if (editorBar is null) return;
        float now = (float)host.realTime.Elapsed.TotalSeconds;
        for (int i = 0; i < editorCrowd.Count; i++)
        {
            var (look, at, yaw, clip) = editorCrowd[i];
            list.Add(new CharacterInstance(EditorCrowdKey + i, look, at, yaw, [new CharacterPose(clip, EditorClipTime(clip, now + i * 3.7f), 1)]));
        }
    }

    /// <summary>
    /// The time a clip held in the bar (a seat's sitting clip, a standing patron's stance) is drawn at, <paramref name="seconds"/> after it started: at its record's play
    /// speed, a one-shot clip (no <c>loop</c>) held at its last frame as the simulation holds one (<see cref="AnimationSystem"/>), not wrapped round by the renderer. A seat
    /// whose clip sits down (mods' sitting animations) would otherwise stand up and sit down again over and over (issue #242).
    /// </summary>
    internal float EditorClipTime(string clip, float seconds)
    {
        if (host.animationLibrary?.IndexOf(clip) is not (>= 0 and int k)) return 0;
        var d = host.animationLibrary.Definitions[k];
        float time = seconds * d.PlaySpeed;
        if (d.Loop) return time;
        float length = host.animationLengths?.Of(d.Clip) ?? 1;
        return Math.Clamp(time, 0, length * 0.999f);
    }
}
