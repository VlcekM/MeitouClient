using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Rendering;
using Meitou.Rendering.Characters;
using Meitou.Simulation;
using Meitou.Simulation.Editing;

namespace Meitou.Game;

/// <summary>
/// The character editor's host side (docs/characters.md "Character editor"): its data and preview source, opening it for a new game's squad once the world is shown,
/// and its scene, which replaces the world while the editor is open.
/// </summary>
sealed partial class GameHost
{
    /// <summary>The new game's squad, waiting for the loading screen to lift before the editor opens on it.</summary>
    List<CharacterId>? pendingCreation;

    void ConfigureEditor(PlayerInterface ui)
    {
        if (popData is not { } data) return;
        ui.EditorData = (data.Db, install.Root);
        ui.PreviewSource = (recordId, loadout) => data.Appearances?.Create(recordId, loadout);
        ui.PreviewReady = look => ActiveCharacters?.IsPrepared(look) ?? true;
        // The editor's looks are close-ups: their meshes without generated levels (CharacterContent.PrepareMesh), so a new hair or head shows at once, as in the original.
        ui.PreviewFetch = look => ActiveCharacters?.PrefetchCloseUp(look);
        ui.PreviewWarm = (look, urgent) => ActiveCharacters?.Warm(look, urgent, close: true);
    }

    /// <summary>Opens the editor on the new game's squad once the world shows and the squad is in the snapshot.</summary>
    void OpenPendingEditor()
    {
        if (pendingCreation is not { } squad || sceneLoad is not null || warming) return;
        if (!squad.All(id => session.CurrentSnapshot.Characters.Any(c => c.Id == id))) return;
        pendingCreation = null;
        creationWarmOwed = true;
        if (!player.OpenEditor(EditKind.Creation, squad)) session.World.Commands.Enqueue(new FinishCreation { Tick = session.World.Tick });
    }

    /// <summary>The world has not been warmed up yet: the new game's editor came first (see <see cref="CoveredFrame"/>).</summary>
    bool creationWarmOwed;

    /// <summary>
    /// Once the new game's editor has closed: the edits are applied by a tick, then the loading screen comes back while the world warms up, as a load without an editor does.
    /// </summary>
    void ResumeAfterCreation()
    {
        if (!creationWarmOwed || pendingCreation is not null || player.EditorOpen || sceneLoad is not null) return;
        creationWarmOwed = false;
        if (playerSquad is { Leader.IsNone: false } squad) player.Follow = squad.Leader;   // a new game starts with the camera locked to the leader
        if (gpu is null || metalRenderer)
        {
            // Metal has no Vulkan loading/warm loop here: the world was loaded and drawn under the editor already. Apply the edits at once.
            session.World.RunTick();
            return;
        }
        if (loading is null) return;
        session.World.RunTick();
        loading.Restart();
        loadingSteps = SceneSteps;
        BeginWarm();
    }

    /// <summary>
    /// While the editor is open the world camera stays on the edited character, so the world around it is there when the editor closes; what the editor shows is its own scene
    /// (<see cref="DrawEditorStage"/>).
    /// </summary>
    void EditorCamera()
    {
        if (player.EditorFocus is not { } focus || session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == focus.Character) is not { } c) return;
        camera.Target = DrawnPosition(c) + new Vector3(0, 10, 0);
    }

    /// <summary>Where the character stands on the picture, in normalised device units: the middle of the space between the editor's two panels (the left one ends at 16 % of the width, the right one starts at 70 %), not of the window.</summary>
    const float EditorStageShift = (0.5f * (0.161458f + 0.7f)) * 2 - 1;

    /// <summary>
    /// The frame of the character editor, in place of the world's: the original draws the edited character alone, lit plainly, over the plain ground texture
    /// (compositor <c>Kenshi_Character_Editor</c>, docs/characters.md). The renderer draws the character into the stage picture from the editor's orbit; the interface puts
    /// <c>SandyGround.dds</c> and the picture behind its panels (<c>PlayerInterface.EditorBackdrop</c>).
    /// </summary>
    void DrawEditorStage(int width, int height)
    {
        if (ActiveCharacters is not { } characters) return;
        editorStaged = true;
        if (player.EditorView() is not { } view || session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == view.Character) is not { } c) { characters.StageRequest = null; return; }
        characters.StageRequest = new EditorStageRequest(DrawKey(view.Character), width, height, view.Yaw, view.Elevation, view.Distance, view.Head, camera.FieldOfView, EditorStageShift);
        characters.SetView(width, height, camera.FieldOfView);
        characters.Update(DrawnPosition(c));
        // The bar's lamps, its other people and the far plane that reaches its ground; none of it with the platform.
        characters.StageLamps = editorBar?.Lamps;
        characters.StageCompany = EditorCompany();
        characters.StageFar = editorBar is null ? 400 : 1500;
        player.EditorNight = editorBar is not null;
        // The set casts into the studio's maps (Meitou, previewsun), as the character does.
        characters.StageCasters = ActiveObjects is { } caster ? (viewProjection, eye) => caster.RecordStagePropDepth(viewProjection, eye) : null;
        characters.DrawStage();
        DrawEditorPlatform(view.Character, characters);
        characters.ResolveStage();
        if (characters.StageTexture is { } stage) player.SetUiTexture(PlayerInterface.StageTexture, stage.Texture, stage.Sampled);
    }

    /// <summary>The character the stage's set was placed for (it is placed once per character, where the character stands when first shown), and whether that set is the bar.</summary>
    CharacterId? editorPlatformFor;
    bool editorSetIsBar;

    /// <summary>
    /// The set round the edited character: the platform (<see cref="Meitou.Data.World.WorldObjectLayout.EditorPlatformMesh"/>), its stage frame the character's turned half round, as the
    /// original stands the characters on it turned so, or the bar (the <c>editorbar</c> switch, <see cref="EditorBar"/>). The objects keep streaming meanwhile (the world's frame, which
    /// would, is not drawn), and the set is drawn into the stage picture.
    /// </summary>
    void DrawEditorPlatform(CharacterId character, CharacterRenderer characters)
    {
        if (ActiveObjects is not { } objects || popData is not { } data || characters.StageView is not { } stage || characters.StageDrawTarget is not { } target) return;
        bool bar = PreviewStudio.Bar;
        if (editorPlatformFor != character || editorSetIsBar != bar)
        {
            (editorPlatformFor, editorSetIsBar) = (character, bar);
            editorBar = bar && scene.Objects is { } world ? EditorBar.At(world, stage.Feet) : null;
            var set = editorBar?.Meshes ?? (Meitou.Data.World.WorldObjectLayout.EditorPlatformMesh(data.Db, Matrix4x4.CreateRotationY(MathF.PI) * stage.Feet) is { } platform ? [platform] : []);
            objects.SetStageProps(set, stage.Feet.Translation, cullBacks: editorBar is not null);
            editorCrowd.Clear();
            if (editorBar is not null)
                foreach (var (id, faction, at, yaw, clip, seed) in editorBar.Crowd)
                    if (data.Appearances?.Create(id, faction, seed) is { } look)
                    {
                        editorCrowd.Add((look, at, yaw, clip));
                        characters.Prefetch(look);
                    }
        }
        objects.Update(stage.Eye);
        objects.DrawStageProp(target.Colour, target.Depth, stage.ViewProjection, stage.Eye, stage.Light, target.FogDistance, characters);
    }

    /// <summary>Whether the editor's stage was drawn since it last closed (<see cref="ClearEditorStage"/>).</summary>
    bool editorStaged;

    /// <summary>The editor has closed: the platform goes, and the close-up looks warmed for it and never shown are dropped.</summary>
    void ClearEditorStage()
    {
        if (!editorStaged) return;
        editorStaged = false;
        ActiveCharacters?.ReleaseCloseUps();
        if (editorPlatformFor is null) return;
        editorPlatformFor = null;
        ActiveObjects?.SetStageProps([], default);
        editorBar = null;
        editorCrowd.Clear();
        player.EditorNight = false;
        if (ActiveCharacters is { } characters) (characters.StageLamps, characters.StageCompany, characters.StageFar) = (null, null, 400);
    }

    /// <summary>The bar when the set is the bar (the <c>editorbar</c> switch); null with the platform.</summary>
    EditorBar? editorBar;
    /// <summary>The bar's own people for this opening of the editor (<see cref="EditorBar.Crowd"/>): their rolled looks, places, yaws and clips.</summary>
    readonly List<(Meitou.Data.Characters.CharacterAppearance Look, Vector3 At, float Yaw, string Clip)> editorCrowd = [];
    /// <summary>The draw keys of the bar's own people: their own range, clear of the characters' and the title's.</summary>
    const long EditorCrowdKey = long.MinValue + (1L << 40);

    /// <summary>Where one of the others being edited sits in the bar (up to <see cref="EditorBar.MaxSeated"/>, in the editor's order); null without the bar or for the shown one. An animal has no sitting clip.</summary>
    (Vector3 Position, float Yaw, string Clip)? EditorBarSeat(CharacterId id, CharacterId shown)
    {
        if (editorBar is not { } bar || id == shown) return null;
        int seat = 0;
        foreach (var other in player.EditorCharacters)
        {
            if (other == shown || player.IsEditorAnimal(other)) continue;
            if (seat >= EditorBar.MaxSeated || seat >= bar.Seats.Count) return null;
            if (other == id) return bar.Seats[seat];
            seat++;
        }
        return null;
    }

    /// <summary>The bar's own people in the draw list, each holding its clip from a start of its own so they do not move in step.</summary>
    void AddEditorCrowd(CharacterDrawList list)
    {
        float now = (float)realTime.Elapsed.TotalSeconds;
        for (int i = 0; i < editorCrowd.Count; i++)
        {
            var (look, at, yaw, clip) = editorCrowd[i];
            list.Add(new CharacterInstance(EditorCrowdKey + i, look, at, yaw, [new CharacterPose(clip, EditorClipTime(clip, now + i * 3.7f), 1)]));
        }
    }

    /// <summary>The draw keys of everyone in the bar but the shown character; null with the platform.</summary>
    long[]? EditorCompany()
    {
        if (editorBar is null || player.EditorFocus is not { } focus) return null;
        var keys = new List<long>();
        foreach (var other in player.EditorCharacters)
            if (EditorBarSeat(other, focus.Character) is not null) keys.Add(DrawKey(other));
        for (int i = 0; i < editorCrowd.Count; i++) keys.Add(EditorCrowdKey + i);
        return [.. keys];
    }

    /// <summary>
    /// The time a clip held in the editor (the stance preview, an animal's idle) is drawn at, <paramref name="seconds"/> after it started: at its record's play
    /// speed, a one-shot clip (no <c>loop</c>) held at its last frame as the simulation holds one (<see cref="AnimationSystem"/>), not wrapped round by the renderer. A clip
    /// that sits down (mods' animations) would otherwise stand up and sit down again over and over (issue #242).
    /// </summary>
    float EditorClipTime(string clip, float seconds)
    {
        if (animationLibrary?.IndexOf(clip) is not (>= 0 and int k)) return 0;
        var d = animationLibrary.Definitions[k];
        float time = seconds * d.PlaySpeed;
        if (d.Loop) return time;
        float length = animationLengths?.Of(d.Clip) ?? 1;
        return Math.Clamp(time, 0, length * 0.999f);
    }
}
