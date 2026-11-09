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
/// The character editor's host side (docs/characters.md "Character editor"): its data and preview source, opening it for a new game's squad once the
/// world is shown, and its scene, which replaces the world while the editor is open.
/// </summary>
sealed class CharacterEditorHost(GameHost host)
{
    #region Session

    /// <summary>
    /// The Meitou UI's editor scene, The Hub's bar.
    /// </summary>
    internal EditorBarScene Bar => field ??= new(host, this);

    /// <summary>
    /// The new game's squad, waiting for the loading screen to lift before the editor opens on it.
    /// </summary>
    internal List<CharacterId>? PendingCreation;

    /// <summary>
    /// The world has not been warmed up yet: the new game's editor came first (see <see cref="GameHost.CoveredFrame"/>).
    /// </summary>
    bool _creationWarmOwed;

    /// <summary>
    /// Gives a new interface's editor the game data, the install root and the preview hooks (the looks, their readiness, close-up prefetch
    /// and warming).
    /// </summary>
    internal void ConfigureEditor(PlayerInterface ui)
    {
        if (host.PopData is not { } data) return;
        ui.CharacterEditor.EditorData = (data.Db, host.Install.Root);
        ui.CharacterEditor.Previews.PreviewSource = (recordId, loadout) => data.Appearances?.Create(recordId, loadout);
        ui.CharacterEditor.Previews.PreviewReady = look => host.ActiveCharacters?.IsPrepared(look) ?? true;
        // The editor's looks are close-ups: their meshes without generated levels (CharacterContent.PrepareMesh), so a new hair or head shows at
        // once, as in the original.
        ui.CharacterEditor.Previews.PreviewFetch = look => host.ActiveCharacters?.PrefetchCloseUp(look);
        ui.CharacterEditor.Previews.PreviewWarm = (look, urgent) => host.ActiveCharacters?.Warm(look, urgent, close: true);
    }

    /// <summary>
    /// Opens the editor on the new game's squad once the world shows and the squad is in the snapshot.
    /// </summary>
    internal void OpenPendingEditor()
    {
        if (PendingCreation is not { } squad || host.ActiveSceneLoad is not null || host.Warming) return;
        if (!squad.All(id => host.Session.CurrentSnapshot.Characters.Any(c => c.Id == id))) return;
        PendingCreation = null;
        _creationWarmOwed = true;
        if (!host.Player.CharacterEditor.OpenEditor(EditKind.Creation, squad))
            host.Session.World.Commands.Enqueue(new FinishCreation { Tick = host.Session.World.Tick });
    }

    /// <summary>
    /// Once the new game's editor has closed: the edits are applied by a tick, then the loading screen comes back while the world warms up, as a load
    /// without an editor does.
    /// </summary>
    internal void ResumeAfterCreation()
    {
        if (!_creationWarmOwed || PendingCreation is not null || host.Player.CharacterEditor.EditorOpen || host.ActiveSceneLoad is not null) return;
        _creationWarmOwed = false;
        if (host.PlayerSquad is { Leader.IsNone: false } squad)
            host.Player.Follow = squad.Leader;   // a new game starts with the camera locked to the leader
        if (host.Gpu is null || host.MetalRenderer)
        {
            // Metal has no Vulkan loading/warm loop here: the world was loaded and drawn under the editor already. Apply the edits at once.
            host.Session.World.RunTick();
            return;
        }
        if (host.Loading is null) return;
        host.Session.World.RunTick();
        host.Loading.Restart();
        host.LoadingSteps = GameHost.SceneSteps;
        host.BeginWarm();
    }

    /// <summary>
    /// While the editor is open the world camera stays on the edited character, so the world around it is there when the editor closes; what the
    /// editor shows is its own scene (<see cref="DrawEditorStage"/>).
    /// </summary>
    internal void EditorCamera()
    {
        if (host.Player.CharacterEditor.EditorFocus is not { } focus
                || host.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == focus.Character) is not { } c)
            return;
        host.Camera.Target = host.DrawnPosition(c) + new Vector3(0, 10, 0);
    }

    #endregion

    #region Editor stage

    /// <summary>
    /// Where the character stands on the picture, in normalised device units: the middle of the space between the editor's two panels (the left one
    /// ends at 16 % of the width, the right one starts at 70 %), not of the window.
    /// </summary>
    internal const float EditorStageShift = (0.5f * (0.161458f + 0.7f)) * 2 - 1;

    /// <summary>
    /// The character the stage's platform was placed for (it is placed once per character, where the character stands when first shown).
    /// </summary>
    CharacterId? _editorPlatformFor;

    /// <summary>
    /// Whether the editor's stage was drawn since it last closed (<see cref="ClearEditorStage"/>).
    /// </summary>
    bool _editorStaged;

    /// <summary>
    /// The frame of the character editor, in place of the world's: the original draws the edited character alone, lit plainly, over the plain ground
    /// texture (compositor <c>Kenshi_Character_Editor</c>, docs/characters.md). The renderer draws the character into the stage picture from the
    /// editor's orbit; the interface puts <c>SandyGround.dds</c> and the picture behind its panels (<c>PlayerInterface.EditorBackdrop</c>). <paramref
    /// name="posed"/>: the world pass has just updated the characters this frame (the Meitou bar's), so they are not updated again.
    /// </summary>
    internal void DrawEditorStage(int width, int height, bool posed = false)
    {
        if (host.ActiveCharacters is not { } characters) return;
        _editorStaged = true;
        if (host.Player.CharacterEditor.EditorView() is not { } view
                || host.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == view.Character) is not { } c)
        {
            characters.StageRequest = null;
            return;
        }
        characters.StageRequest = new EditorStageRequest(host.DrawKey(view.Character), width, height, view.Yaw, view.Elevation, view.Distance,
            view.Head, host.Camera.FieldOfView, EditorStageShift);
        characters.SetView(width, height, host.Camera.FieldOfView);
        if (!posed) characters.Update(host.DrawnPosition(c));
        characters.DrawStage();
        DrawEditorPlatform(view.Character, characters);
        characters.ResolveStage();
        if (characters.StageTexture is { } stage) host.Player.SetUiTexture(CharacterEditorPanel.StageTexture, stage.Texture, stage.Sampled);
    }

    /// <summary>
    /// The platform under the edited character (<see cref="Meitou.Data.World.WorldObjectLayout.EditorPlatformMesh"/>): its stage frame is the
    /// character's turned half round, as the original stands the characters on it turned so. The objects keep streaming meanwhile (the world's frame,
    /// which would, is not drawn), and the platform is drawn into the stage picture.
    /// </summary>
    void DrawEditorPlatform(CharacterId character, CharacterRenderer characters)
    {
        if (host.ActiveObjects is not { } objects || host.PopData is not { } data || characters.StageView is not { } stage
                || characters.StageDrawTarget is not { } target)
            return;
        if (_editorPlatformFor != character)
        {
            _editorPlatformFor = character;
            objects.SetStageProp(WorldObjectLayout.EditorPlatformMesh(data.Db, Matrix4x4.CreateRotationY(MathF.PI) * stage.Feet));
        }
        objects.Update(stage.Eye);
        objects.DrawStageProp(target.Colour, target.Depth, stage.ViewProjection, stage.Eye, stage.Light, target.FogDistance);
    }

    /// <summary>
    /// The editor has closed: the platform goes, and the close-up looks warmed for it and never shown are dropped.
    /// </summary>
    internal void ClearEditorStage()
    {
        if (!_editorStaged) return;
        _editorStaged = false;
        host.ActiveCharacters?.ReleaseCloseUps();
        if (_editorPlatformFor is null) return;
        _editorPlatformFor = null;
        host.ActiveObjects?.SetStageProp(null);
    }

    #endregion
}
