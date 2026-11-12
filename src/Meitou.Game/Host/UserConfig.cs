using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Meitou.Game.Effects;
using Meitou.Game.Settings;
using Meitou.Game.Ui;

namespace Meitou.Game.Host;

/// <summary>
/// The unit the run speed is shown in.
/// </summary>
enum SpeedUnit
{
    /// <summary>
    /// Miles per hour, as the game shows it (the default).
    /// </summary>
    Mph,
    /// <summary>
    /// Kilometres per hour.
    /// </summary>
    Kmh
}

/// <summary>
/// Whose frame generation the Frame generation setting turns on (<see cref="UserConfig.FrameGenerationVendor"/>): AMD's FSR 3 or NVIDIA's DLSS.
/// </summary>
enum FrameGenerationVendor
{
    /// <summary>
    /// AMD's FSR 3 frame-interpolation swapchain (the default).
    /// </summary>
    Fsr,
    /// <summary>
    /// NVIDIA's DLSS frame generation through Streamline.
    /// </summary>
    Dlss
}

/// <summary>
/// When the next world tick runs during the world draw (<see cref="UserConfig.TickAhead"/>): never (Faithful), while ticks are expensive (Meitou),
/// always.
/// </summary>
enum TickAheadMode
{
    /// <summary>
    /// Never: the world tick, then the draw, as the game does.
    /// </summary>
    Faithful,
    /// <summary>
    /// While a tick is expensive (Meitou, the default).
    /// </summary>
    Dynamic,
    /// <summary>
    /// Every frame.
    /// </summary>
    Always
}

/// <summary>
/// The Metal display path: native at full size, MetalFX temporal at any preset scale, MetalFX spatial as an alternative, or TAA.
/// </summary>
enum MetalUpscalerMode
{
    /// <summary>
    /// Native rendering at full size (used when MetalFX is unavailable).
    /// </summary>
    Native,
    /// <summary>
    /// MetalFX temporal upscaling at any preset scale (the default).
    /// </summary>
    Temporal,
    /// <summary>
    /// MetalFX spatial upscaling, as an alternative.
    /// </summary>
    Spatial,
    /// <summary>
    /// TAA.
    /// </summary>
    Taa
}

/// <summary>
/// The player's settings, kept in <c>meitou.user.json</c> (git-ignored) in the working directory, else next to the executable:
/// the frame limit, vsync, full screen, the simulation tick rate, the graphics sliders of the Tab panel (by label) and key bindings
/// (those that differ from the defaults, action name to comma-separated keys, as <see cref="Input.InputBindings.Apply"/> reads them). Written
/// back on exit.
/// One file is shared by every installed version (the launcher's data folder) and edited by the launcher too: docs/launcher.md "Graphics".
/// </summary>
sealed class UserConfig
{
    #region Settings

    /// <summary>
    /// The layout of this file, its <c>version</c> key; a file without one (written before it existed) is 1. Raise it when a key is renamed,
    /// retyped or read with a new meaning, and convert older files in <see cref="Migrate"/>. A Tab panel slider whose numbering changes
    /// takes a new label instead (<c>WorldFrame.UpscalerSliders</c>), so its old value is not misread.
    /// </summary>
    public const int CurrentVersion = 2;
    /// <summary>
    /// The default of <see cref="CharacterFlightSeconds"/>, and the distance from the view a squad member must be past for a double click to
    /// fly there.
    /// </summary>
    public const float DefaultFlightSeconds = 2, FlightDistance = 10000;
    /// <summary>
    /// <see cref="AttackSlots"/>'s Faithful value (the game's 1) and its Meitou value.
    /// </summary>
    public const int FaithfulAttackSlots = 1, MeitouAttackSlots = 3;
    /// <summary>
    /// The Controls slider's range of <see cref="CameraRotateSpeed"/> (the original's 2 … 50).
    /// </summary>
    public const float MinCameraRotateSpeed = 2, MaxCameraRotateSpeed = 50;
    /// <summary>
    /// The range of <see cref="FontSize"/> (the original's slider -10 … 32).
    /// </summary>
    public const float MinFontSize = -10, MaxFontSize = 32;
    /// <summary>
    /// The range of <see cref="InterfaceScale"/> (50 … 200%).
    /// </summary>
    public const float MinInterfaceScale = 0.5f, MaxInterfaceScale = 2;
    /// <summary>
    /// The ranges of <see cref="CameraMoveSpeed"/> (150 … 1000) and <see cref="CameraZoomSpeed"/> (10 … 200), the original's.
    /// </summary>
    public const float MinCameraMoveSpeed = 150, MaxCameraMoveSpeed = 1000, MinCameraZoomSpeed = 10, MaxCameraZoomSpeed = 200;
    /// <summary>
    /// The range and default of <see cref="ShiftPanMultiplier"/>.
    /// </summary>
    public const float MinShiftPanMultiplier = 1.25f, MaxShiftPanMultiplier = 5, DefaultShiftPanMultiplier = 2;

    /// <summary>
    /// The file's layout: <see cref="CurrentVersion"/>, or the newer one of a later release whose file this is (kept, so that release does not
    /// convert its own keys again).
    /// </summary>
    public int Version { get; set; } = CurrentVersion;
    /// <summary>
    /// Top-level keys this release does not know (a newer one's), written back as read.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>
    /// Frames per second when vsync is off; 0 = unlimited. Engine default 150.
    /// </summary>
    public int FpsLimit { get; set; } = 150;
    /// <summary>
    /// Vsync on: the frames follow the display and <see cref="FpsLimit"/> is not applied; <c>--vsync</c> / <c>--no-vsync</c> override it for one run.
    /// </summary>
    public bool VSync { get; set; }
    /// <summary>
    /// Frame generation: a generated frame between each two rendered ones, by <see cref="FrameGenerationVendor"/>: FSR 3's frame-interpolation
    /// swapchain (Windows, Proton and Linux, needs <c>amd_fidelityfx_vk.dll</c> / <c>libamd_fidelityfx_vk.so</c>) or DLSS's through Streamline
    /// (<c>FrameGeneration</c>). Off by default; read at start-up, so a change takes effect after a restart.
    /// </summary>
    public bool FrameGeneration { get; set; }
    /// <summary>
    /// Whose frame generation <see cref="FrameGeneration"/> turns on: FSR 3 (the default) or DLSS (Windows, RTX 40 or later, through Streamline; read
    /// at start-up).
    /// </summary>
    public FrameGenerationVendor FrameGenerationVendor { get; set; }

    /// <summary>
    /// The frame generation to start with: off, or <see cref="FrameGenerationVendor"/>'s.
    /// </summary>
    [JsonIgnore]
    public Rendering.Post.FrameGenerationMethod FrameGenerationMethod
    {
        get => !FrameGeneration ? Rendering.Post.FrameGenerationMethod.Off
            : FrameGenerationVendor == FrameGenerationVendor.Dlss ? Rendering.Post.FrameGenerationMethod.Dlss
                : Rendering.Post.FrameGenerationMethod.Fsr;
        set
        {
            FrameGeneration = value != Rendering.Post.FrameGenerationMethod.Off;
            if (FrameGeneration)
                FrameGenerationVendor = value == Rendering.Post.FrameGenerationMethod.Dlss ? FrameGenerationVendor.Dlss : FrameGenerationVendor.Fsr;
        }
    }
    /// <summary>
    /// The Metal display default: MetalFX balanced (temporal at 0.50); Native is used when MetalFX is unavailable.
    /// </summary>
    public MetalUpscalerMode MetalUpscaler { get; set; } = MetalUpscalerMode.Temporal;
    /// <summary>
    /// The Metal render scale; the default 0.50 is the balanced profile. The Metal settings panel edits it live.
    /// </summary>
    public float MetalRenderScale { get; set; } = 0.5f;
    /// <summary>
    /// The interface style: Meitou (the default) or Faithful, the game's.
    /// </summary>
    public Meitou.Data.Ui.UiMode UiMode { get; set; } = Data.Ui.UiMode.Meitou;
    /// <summary>
    /// The unit of the run speed in the information panel (Interface settings): mph as the game, or km/h.
    /// </summary>
    public SpeedUnit SpeedUnit { get; set; } = SpeedUnit.Mph;
    /// <summary>
    /// The Interface scale (Interface settings, Meitou's own; 1 = 100%): the whole UI, its text, skins and layout-sized windows, is this much
    /// larger than the window height alone makes it.
    /// </summary>
    public float InterfaceScale { get; set; } = 1;
    /// <summary>
    /// The damage floaters (Interface settings): the original's <c>floaters</c> option, Simple by default; Meitou also floats a green "+N" over a
    /// player character that heals.
    /// </summary>
    public FloaterMode Floaters { get; set; } = FloaterMode.Simple;
    /// <summary>
    /// The original's <c>censorship</c> option (Interface settings, off by default): five words swapped in the displayed dialogue.
    /// </summary>
    public bool Censorship { get; set; }
    /// <summary>
    /// The original's <c>Show names</c> option (Interface settings, on by default): the name over the player's characters; off, the tag keeps only
    /// its stealth and light icons.
    /// </summary>
    public bool ShowNames { get; set; } = true;
    /// <summary>
    /// The FEEDBACK button at the top right during play (Interface settings, default on); off, it shows only on the title and with the game menu
    /// open.
    /// </summary>
    public bool FeedbackButton { get; set; } = true;
    /// <summary>
    /// The original's splash at startup in the Faithful UI (Advanced settings: the switch's Faithful side), off by default; off, the menu shows as
    /// soon as the game has loaded.
    /// </summary>
    public bool SplashScreens { get; set; }
    /// <summary>
    /// The sky arrival's flight over a load from the title (Meitou main menu only; <see cref="Title.SkyArrival"/>), on by default.
    /// </summary>
    public bool MainMenuTransitions { get; set; } = true;
    /// <summary>
    /// The title screen (Interface settings), independent of <see cref="UiMode"/>: on, Meitou's menu over the staged world scene; off, the original's picture.
    /// </summary>
    public bool MeitouMainMenu { get; set; } = true;
    /// <summary>
    /// The sky arrival's flight over a load during play (Meitou UI only), on by default.
    /// </summary>
    public bool InGameTransitions { get; set; } = true;
    /// <summary>
    /// The speed of the sky arrival from the title (1.5 by default; <see cref="Title.SkyArrival"/> clamps it to 0.25 … 4).
    /// </summary>
    public float MainMenuTransitionSpeed { get; set; } = 1.5f;
    /// <summary>
    /// The speed of the sky arrival during play (1.5 by default; clamped as <see cref="MainMenuTransitionSpeed"/>).
    /// </summary>
    public float InGameTransitionSpeed { get; set; } = 1.5f;
    /// <summary>
    /// A double click (or double tap) on a squad member farther than <see cref="FlightDistance"/> from the view flies the camera there in an arc
    /// (Advanced settings; Meitou only, the game cuts).
    /// </summary>
    public bool CharacterFlights { get; set; } = false;
    /// <summary>
    /// Keeps the object meshes and textures and the terrain textures around every squad bar character loaded, not only around the camera (Advanced
    /// settings; Meitou only, costs video memory).
    /// </summary>
    public bool KeepTexturesAtCharacters { get; set; } = true;
    /// <summary>
    /// With <see cref="KeepTexturesAtCharacters"/>, the other player squads' members are kept too, not only the squad bar's (more video memory).
    /// </summary>
    public bool KeepTexturesAtAllSquads { get; set; }
    /// <summary>
    /// Seconds a character flight (and each squad tour hop, numpad /) takes.
    /// </summary>
    public float CharacterFlightSeconds { get; set; } = DefaultFlightSeconds;
    /// <summary>
    /// Borderless full screen (the Tab panel, Alt+Enter); <c>--windowed</c> starts in a window for one run.
    /// </summary>
    public bool FullScreen { get; set; } = true;
    /// <summary>
    /// World ticks per second, 30 by default (the session's fixed-step clock).
    /// </summary>
    public int TickRate { get; set; } = 30;
    /// <summary>
    /// The next world tick run during the world draw (Display settings, docs/simulation.md "Tick ahead"): Faithful never (the world, then the draw,
    /// as the game), Meitou (the default) while a tick is expensive, or always. Running ahead adds up to a tick (33 ms) of input delay.
    /// </summary>
    public TickAheadMode TickAhead { get; set; } = TickAheadMode.Dynamic;
    /// <summary>
    /// How many enemies may swing at one target at once (Advanced settings, 1-5; plus the race's extra slots): Faithful 1 (the game's CONSTANTS, the
    /// default), Meitou 3.
    /// </summary>
    public int AttackSlots { get; set; } = FaithfulAttackSlots;

    /// <summary>
    /// The setting for the combat system: null (the loaded CONSTANTS <c>max num attack slots</c>, 1 in the base game) at the Faithful value, else the
    /// chosen 1 to 5.
    /// </summary>
    public int? AttackSlotsOption => AttackSlots == FaithfulAttackSlots ? null : Math.Clamp(AttackSlots, 1, 5);
    /// <summary>
    /// A late but successfully rolled dodge or block still counts (Advanced settings; off by default and in both presets, so fights feel as the
    /// game's): a balance change for fights against several enemies.
    /// </summary>
    public bool LateDefences { get; set; } = false;
    /// <summary>
    /// The off-screen switches (Advanced settings; Faithful off, Meitou on; docs/simulation.md "Off-screen tier"): roaming squads keep their
    /// characters off screen, hostile squads that meet off screen fight it out, a squad coming back has healed for the time away.
    /// </summary>
    public bool KeepRoamers { get; set; } = true;
    /// <summary>
    /// Hostile squads that meet off screen fight it out (see <see cref="KeepRoamers"/>).
    /// </summary>
    public bool OffscreenFights { get; set; } = true;
    /// <summary>
    /// A squad coming back has healed for the time away (see <see cref="KeepRoamers"/>).
    /// </summary>
    public bool OffscreenHealing { get; set; } = true;
    /// <summary>
    /// Campaign data counts each <c>inherits from</c> ancestor once (Fixes settings; Faithful off: twice, as the game does, doubling inherited squad
    /// weights and follow-up chances).
    /// </summary>
    public bool CampaignAncestorsOnce { get; set; } = true;
    /// <summary>
    /// Wild animals and homeless squads keep their characters while their area is unloaded (Advanced settings; Faithful off: removed and made anew,
    /// as the game does).
    /// </summary>
    public bool KeepWild { get; set; } = true;
    /// <summary>
    /// Town residents back after an hour or more unloaded are where their daily schedule puts them (Advanced settings; Faithful off: where they
    /// stood, as the game does).
    /// </summary>
    public bool ResidentSchedule { get; set; } = true;
    /// <summary>
    /// Farms, automatic machines and generators work in unloaded zones (Advanced settings; Faithful off: they stop there and farms catch up on load,
    /// as the game does).
    /// </summary>
    public bool UnloadedProduction { get; set; } = true;
    /// <summary>
    /// Doors a walker opened close behind it after 5 s (Advanced settings; Faithful off: they stay open, as in the game; docs/simulation.md "Cages,
    /// beds and locks").
    /// </summary>
    public bool DoorsCloseBehind { get; set; } = true;
    /// <summary>
    /// A blow that destroys the head cuts it off, under the limbs' rule (Advanced settings; Faithful off: the game never does;
    /// docs/game/character-stats.md "Decapitation").
    /// </summary>
    public bool Decapitation { get; set; } = true;
    /// <summary>
    /// A new ragdoll keeps the character's motion and the downing blow's push, its joints have soft limits and a relaxed pull, and a cut-off head is
    /// thrown with it (Advanced settings; Faithful off: the game starts a ragdoll at rest; docs/game/ragdolls.md "Initial state").
    /// </summary>
    public bool AdvancedRagdolls { get; set; } = true;
    /// <summary>
    /// A loaded or imported save starts paused (Advanced settings, a Faithful / Meitou switch; on by default, Meitou: in the game it runs at once).
    /// <c>--load</c> is not paused.
    /// </summary>
    public bool PauseOnLoad { get; set; } = true;
    /// <summary>
    /// The player's animals pick up loose items into their bags (Advanced settings, a Faithful / Meitou switch whose
    /// Faithful side is on, as the game lets animals pick up; off by default, Meitou;
    /// <c>PlayerSystem.AnimalsPickUp</c>).
    /// </summary>
    public bool AnimalsPickUp { get; set; }
    /// <summary>
    /// Fixes of the game's own bugs (Fixes settings; on: fixed, off: as the game does; docs/audio.md): doors and gates sound when they break, a biome
    /// entered before its bank loads gets the right day or night ambience, and the wind bed stops when the wind does.
    /// </summary>
    public bool FixDoorBreak { get; set; } = true;
    /// <summary>
    /// A biome entered before its bank loads gets the right day or night ambience (see <see cref="FixDoorBreak"/>).
    /// </summary>
    public bool FixAmbienceSuffix { get; set; } = true;
    /// <summary>
    /// The wind bed stops when the wind does (see <see cref="FixDoorBreak"/>).
    /// </summary>
    public bool FixWindStop { get; set; } = true;
    /// <summary>
    /// The Faithful load window's Load button waits for a readable save (Fixes settings; off: always live, as in the game).
    /// </summary>
    public bool FixLoadButton { get; set; } = true;
    /// <summary>
    /// A turret's gunner faces along the barrel as the gun turns (Fixes settings; off: toward the turret, as the game does).
    /// </summary>
    public bool FixGunnerFacing { get; set; } = true;
    /// <summary>
    /// "ma badpunch"'s attack distance -4 reads as 4 (Fixes settings; off: as written, as the game does).
    /// </summary>
    public bool FixPunchReach { get; set; } = true;
    /// <summary>
    /// A bench with its table behind is sat facing away from it (Fixes settings; off: towards the world origin, as the game does;
    /// docs/game/ai-tasks.md "Town goals").
    /// </summary>
    public bool FixBenchFacing { get; set; } = true;
    /// <summary>
    /// Other buildings' parts (the Hub's wall in the Shinobi Thieves tower) are cut inside a building in the interior view (Fixes settings; off:
    /// drawn, as the game does; docs/render-interior-cut.md).
    /// </summary>
    public bool FixPartsInBuildings { get; set; } = true;
    /// <summary>
    /// The strategy camera's turn and tilt rate in degrees per second per unit of input (Controls settings): the original's <c>mouse speed pan</c> /
    /// <c>mouse speed tilt</c> (slider 2 … 50, default 6), one value for both here.
    /// </summary>
    public float CameraRotateSpeed { get; set; } = Data.World.KenshiCamera.DefaultRotateSpeed;
    /// <summary>
    /// The pointer at the screen's edge pans the strategy camera (the original's <c>Edge scrolling</c>, default on; Controls settings); null until
    /// first used, then taken from the install's settings.cfg.
    /// </summary>
    public bool? EdgeScroll { get; set; }
    /// <summary>
    /// The strategy camera's turn and tilt ease in and out as the original's always do (Controls settings; the original has no such option).
    /// </summary>
    public bool CameraSmoothing { get; set; } = true;
    /// <summary>
    /// Shift held with the pan keys multiplies the strategy camera's pan speed by <see cref="ShiftPanMultiplier"/> (Controls settings, a Faithful /
    /// Meitou switch; Faithful off: the game has no such boost).
    /// </summary>
    public bool ShiftPanBoost { get; set; } = true;
    /// <summary>
    /// The pan speed factor while Shift is held (Controls settings, <see cref="MinShiftPanMultiplier"/> … <see cref="MaxShiftPanMultiplier"/>,
    /// default 2).
    /// </summary>
    public float ShiftPanMultiplier { get; set; } = DefaultShiftPanMultiplier;
    /// <summary>
    /// The in-game tutorials (the original's <c>tutorials</c>, default on; Gameplay settings); null until first used, then taken from the install's
    /// settings.cfg.
    /// </summary>
    public bool? Tutorials { get; set; }
    /// <summary>
    /// The original's `invert X` (default off) and `invert Y` (default on); Controls settings. Null until first used, then taken from the install's
    /// settings.cfg.
    /// </summary>
    public bool? InvertX { get; set; }
    /// <summary>
    /// The original's <c>invert Y</c> (default on); see <see cref="InvertX"/>.
    /// </summary>
    public bool? InvertY { get; set; }
    /// <summary>
    /// The original's `swap mouse buttons` (default off; Controls settings): the left and right buttons trade places in the GUI and the game view.
    /// Null until first used, then taken from the install's settings.cfg.
    /// </summary>
    public bool? SwapMouseButtons { get; set; }
    /// <summary>
    /// The original's `font size2` (slider -10 … 32, default 3; Interface settings): points added to every GUI font's default height (<see
    /// cref="Meitou.Data.Ui.KenshiFontSize"/>). Null until first used, then taken from the install's settings.cfg.
    /// </summary>
    public float? FontSize { get; set; }
    /// <summary>
    /// The original's `camera speed` (150 … 1000, default 500) and `camera zoom` (10 … 200, default 125); Controls settings. Null until first used,
    /// then taken from the install's settings.cfg.
    /// </summary>
    public float? CameraMoveSpeed { get; set; }
    /// <summary>
    /// The original's <c>camera zoom</c> (10 … 200, default 125); see <see cref="CameraMoveSpeed"/>.
    /// </summary>
    public float? CameraZoomSpeed { get; set; }
    /// <summary>
    /// The green marker where a move order was given (the original's `Movement marker`, default on; Interface settings); null until first used, then
    /// taken from the install's settings.cfg.
    /// </summary>
    public bool? MovementMarker { get; set; }
    /// <summary>
    /// The marker on the camera's pivot while it is turned (the original's `Rotation marker`, default on; Interface settings); null until first used,
    /// then taken from the install's settings.cfg.
    /// </summary>
    public bool? RotationMarker { get; set; }
    /// <summary>
    /// Sounds are heard from the first selected character instead of the camera (Audio settings; off, the default: the camera, as the game does).
    /// </summary>
    public bool HearFromSelected { get; set; }
    /// <summary>
    /// The Windows cursor instead of the game's drawn pointer (the original's <c>hardware_mouse</c>, default off; Interface settings).
    /// </summary>
    public bool HardwareMouse { get; set; }
    /// <summary>
    /// With <see cref="HardwareMouse"/>: the action's pointer beside the Windows cursor and the hand over what a click acts on (Interface settings;
    /// Meitou only, default on).
    /// </summary>
    public bool EnhancedHardwareMouse { get; set; } = true;
    /// <summary>
    /// Outline and tint the character under the cursor (Advanced settings; Meitou only, the game has none).
    /// </summary>
    public bool HoverOutline { get; set; } = true;
    /// <summary>
    /// Outline and tint the selected building, and the one under the cursor, the same way, except while its interior is shown (Advanced settings;
    /// Meitou only).
    /// </summary>
    public bool BuildingOutline { get; set; } = true;
    /// <summary>
    /// A left click on the ground (nothing picked) ends the building's selection (Advanced settings; Faithful off: it stays selected, as in the
    /// game).
    /// </summary>
    public bool DeselectBuildings { get; set; } = true;
    /// <summary>
    /// A character that takes a fatal hit explodes, covers everything around in blood and sends bones flying (Advanced settings; an easter egg, off
    /// by default, not part of the all Faithful / all Meitou switches; docs/game/luckyren.md).
    /// </summary>
    public bool LuckyrenMode { get; set; }
    /// <summary>
    /// The second and third speed buttons' (and keys') game speeds (Advanced settings; the game's 2 and 5 by default; <see
    /// cref="Meitou.Simulation.Session.SimulationClock.SetFastSpeeds"/>).
    /// </summary>
    public double FastSpeed { get; set; } = Simulation.Session.SimulationClock.DefaultSpeeds[1];
    /// <summary>
    /// The third speed button's (and key's) game speed (the game's 5 by default); see <see cref="FastSpeed"/>.
    /// </summary>
    public double FastestSpeed { get; set; } = Simulation.Session.SimulationClock.DefaultSpeeds[2];
    /// <summary>
    /// How many items dropped in play stay on the ground before the oldest goes (Advanced settings; <see
    /// cref="Meitou.Simulation.Items.GroundItems.Limit"/>).
    /// </summary>
    public int GroundItemLimit { get; set; } = Simulation.Items.GroundItems.DefaultLimit;
    /// <summary>
    /// The <c>--sandbox</c> debug panel at the top right is folded to its button (a down arrow); a click on the button toggles it.
    /// </summary>
    public bool SandboxPanelCollapsed { get; set; }
    /// <summary>
    /// The audio sliders (Audio settings): the original's six volumes and the music frequency; null until first used, then taken from the install's
    /// settings.cfg.
    /// </summary>
    public Audio.AudioSettings? Audio { get; set; }
    /// <summary>
    /// The original's Gameplay options (Gameplay settings); null until first used, then taken from the install's settings.cfg.
    /// </summary>
    public GameplaySettings? Gameplay { get; set; }
    /// <summary>
    /// Worker threads of the simulation; null = half the cores.
    /// </summary>
    public int? SimThreads { get; set; }
    /// <summary>
    /// The autosave slot written last (the original's <c>settings.cfg</c> <c>autosaveindex</c>); the next autosave takes the one after.
    /// </summary>
    public int AutosaveIndex { get; set; }
    /// <summary>
    /// The code mods' settings the player changed (docs/modding.md "Settings"): mod id to setting key to the value as text; a default is not kept.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> ModSettings { get; set; } = [];
    /// <summary>
    /// The feedback form's ticks and Discord name, as last used; null until the form first opens.
    /// </summary>
    public FeedbackSettings? Feedback { get; set; }
    /// <summary>
    /// The Tab panel's graphics sliders, by label.
    /// </summary>
    public Dictionary<string, float> Graphics { get; set; } = [];
    /// <summary>
    /// The Graphics quality preset (Low, Medium, High, Ultra, Custom): the game saves the one its settings sit at; one the launcher chose is applied
    /// at start unless the settings already sit at it (<c>SettingsMenu.ApplySavedPreset</c>). Null: nothing saved yet.
    /// </summary>
    public string? GraphicsPreset { get; set; }
    /// <summary>
    /// The active look preset's name (Graphics; null: Meitou); the presets are files in the <c>presets</c> folder beside this file (docs/native-ui.md
    /// "Settings").
    /// </summary>
    public string? LookPreset { get; set; }
    /// <summary>
    /// Whether the look is Faithful (the game as it ships, a starting point the player may change) rather than a Meitou preset (Graphics); null:
    /// decided from the switches (all on the game's side: Faithful).
    /// </summary>
    public bool? FaithfulLook { get; set; }
    /// <summary>
    /// The costly look switches kept on at Low quality (their labels); at Low the others are held off.
    /// </summary>
    public List<string> KeepAtLow { get; set; } = [];
    /// <summary>
    /// The key bindings that differ from the defaults (the engine's and the install's controls.cfg): action name to comma-separated keys, as
    /// <see cref="Input.InputBindings.Apply"/> reads them; an empty value unbinds.
    /// </summary>
    public Dictionary<string, string> Bindings { get; set; } = [];
    /// <summary>
    /// The Meitou skin per race, where chosen against the default (RACE string id to on or off; Graphics > Skin): <c>CharacterRenderer.SkinRaces</c>.
    /// </summary>
    public Dictionary<string, bool> SkinRaces { get; set; } = [];

    /// <summary>
    /// Fills the camera options still unset from the install's settings.cfg (the original's missing-key defaults).
    /// </summary>
    public void SeedCamera(Content.GameInstall install)
    {
        InvertX ??= Meitou.Game.Audio.AudioSystem.Setting(install, "invert X", 0) != 0;
        InvertY ??= Meitou.Game.Audio.AudioSystem.Setting(install, "invert Y", 1) != 0;
        SwapMouseButtons ??= Meitou.Game.Audio.AudioSystem.Setting(install, "swap mouse buttons", 0) != 0;
        FontSize ??= Meitou.Game.Audio.AudioSystem.Setting(install, "font size2", Data.Ui.KenshiFontSize.DefaultSetting, MinFontSize, MaxFontSize);
        CameraMoveSpeed ??= Meitou.Game.Audio.AudioSystem.Setting(install, "camera speed", Data.World.KenshiCamera.DefaultMoveSpeed,
            MinCameraMoveSpeed, MaxCameraMoveSpeed);
        CameraZoomSpeed ??= Meitou.Game.Audio.AudioSystem.Setting(install, "camera zoom", Data.World.KenshiCamera.DefaultZoomSpeed,
            MinCameraZoomSpeed, MaxCameraZoomSpeed);
    }

    /// <summary>
    /// Hands the camera options to the strategy camera's settings (unset ones keep the camera's own defaults, which are the original's).
    /// </summary>
    public void ApplyCamera(Cameras.CameraSettings camera)
    {
        camera.InvertX = InvertX ?? camera.InvertX;
        camera.InvertY = InvertY ?? camera.InvertY;
        camera.Smoothing = CameraSmoothing;
        camera.MoveSpeed = CameraMoveSpeed ?? camera.MoveSpeed;
        camera.ZoomSpeed = CameraZoomSpeed ?? camera.ZoomSpeed;
        camera.ShiftPanFactor = ShiftPanBoost ? ShiftPanMultiplier : 1;
    }

    #endregion

    #region File

    /// <summary>
    /// The file's name, looked for in the working directory, then next to the executable.
    /// </summary>
    public const string FileName = "meitou.user.json";
    /// <summary>
    /// The file's JSON form: indented, camelCase keys.
    /// </summary>
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// The file this config was read from and is written back to; for a config made new, the working directory's.
    /// </summary>
    [JsonIgnore]
    public string Path { get; private set; } = FileName;

    /// <summary>
    /// Reads the config from the working directory, else from next to the executable; without either file, the defaults, for the working directory.
    /// </summary>
    public static UserConfig Load()
    {
        foreach (var dir in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var file = System.IO.Path.Combine(dir, FileName);
            if (File.Exists(file)) return Load(file);
        }
        return new UserConfig { Path = System.IO.Path.Combine(Directory.GetCurrentDirectory(), FileName) };
    }

    /// <summary>
    /// Reads an existing <paramref name="file"/> (see <see cref="Read"/>); <see cref="Save"/> writes back to it.
    /// </summary>
    internal static UserConfig Load(string file)
    {
        var config = Read(file) ?? new UserConfig();
        config.Path = file;
        config.Normalize();
        return config;
    }

    /// <summary>
    /// Reads the file key by key: a value this release cannot read (a newer release changed its type, a hand edit) is dropped and takes its
    /// default, the rest are kept. Before anything is dropped the file is copied aside, as the next save writes over it.
    /// </summary>
    static UserConfig? Read(string file)
    {
        JsonObject? json;
        try
        {
            json = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"config    {file} is not valid ({e.Message}); using the defaults");
            Backup(file);
            return null;
        }
        if (json is null)
        {
            Console.Error.WriteLine($"config    {file} is not a JSON object; using the defaults");
            Backup(file);
            return null;
        }
        int version = json["version"] is JsonValue v && v.TryGetValue(out int n) ? n : 1;
        if (version < CurrentVersion) Migrate(json, version);
        else if (version > CurrentVersion)
            Console.WriteLine($"config    {file} is from a newer Meitou (layout {version}, this one reads {CurrentVersion}); its new keys are kept");
        json["version"] = Math.Max(version, CurrentVersion);
        bool backedUp = false;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return json.Deserialize<UserConfig>(Json);
            }
            catch (JsonException e) when (attempt < 100)
            {
                backedUp = backedUp || Backup(file);
                if (!RemoveAt(json, e.Path))
                {
                    Console.Error.WriteLine($"config    {file} is not valid ({e.Message}); using the defaults");
                    return null;
                }
                Console.Error.WriteLine($"config    {file}: {e.Path} could not be read ({e.Message}); using its default");
            }
        }
    }

    /// <summary>
    /// Converts a file of an older <see cref="CurrentVersion"/>, one step at a time. A renamed key moves only where the new one is missing: the
    /// launcher may already have written it.
    /// </summary>
    static void Migrate(JsonObject json, int from)
    {
        if (from < 2 && json["graphics"] is JsonObject graphics)
        {
            // 2: haze, heat haze, night grading and the tone map became a switch and a Meitou strength kept while it is off. Layout 1 stored Faithful
            // as the strength at the game's value, which would leave the switch's Meitou side looking the same: such a strength goes, so Meitou's
            // default is used. The tone map slider lost its 0 (the clamp is the switch's Faithful side); a saved 0 goes, so it is not clamped to the
            // shoulder curve.
            float? Get(string key) => graphics[key] is JsonValue v && v.TryGetValue(out double d) ? (float)d : null;
            bool AtGame(string key, float game) => Get(key) is { } v && MathF.Abs(v - game) < 1e-3f;
            void Strength(string switchKey, float game, params string[] keys)
            {
                if (!keys.All(k => AtGame(k, game) || Get(k) is null) || !keys.Any(k => Get(k) is not null)) return;
                float? on = Get(switchKey);
                if (on is null) graphics[switchKey] = 0;   // layout 1 read the switch from the strength: at the game's value it was Faithful
                if (on is null or < 0.5f) foreach (var k in keys) graphics.Remove(k);
            }
            Strength("Meitou: Haze", 1, "Haze strength (1 = game)");
            Strength("Meitou: Heat haze", 1, "Heat haze strength (1 = game)");
            Strength("Meitou: Night grading", 0, "Night grading strength (0 = game)");
            Strength("Meitou: Tone map and grade", 0, "Tone map (0 = game)", "Grading (0 = game)");
            if (AtGame("Tone map (0 = game)", 0)) graphics.Remove("Tone map (0 = game)");
        }
    }

    /// <summary>
    /// Copies the file to <c>meitou.user.&lt;time&gt;.bak.json</c> beside it.
    /// </summary>
    static bool Backup(string file)
    {
        var copy = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file) ?? ".", $"meitou.user.{DateTime.Now:yyyyMMdd-HHmmss}.bak.json");
        try
        {
            File.Copy(file, copy, overwrite: true);
            Console.Error.WriteLine($"config    kept a copy of the old file as {copy}");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"config    could not copy {file}: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Removes the value a <see cref="JsonException.Path"/> names (<c>$.a.b</c>, <c>$['a b']</c>, <c>$.a[2]</c>); false when there is none.
    /// </summary>
    static bool RemoveAt(JsonObject json, string? path)
    {
        if (path is null || !path.StartsWith('$')) return false;
        var segments = new List<object>();
        for (int i = 1; i < path.Length;)
        {
            if (path[i] == '.')
            {
                int end = path.IndexOfAny(['.', '['], i + 1);
                if (end < 0) end = path.Length;
                segments.Add(path[(i + 1)..end]);
                i = end;
            }
            else if (path.AsSpan(i).StartsWith("['"))
            {
                int end = path.IndexOf("']", i + 2, StringComparison.Ordinal);
                if (end < 0) return false;
                segments.Add(path[(i + 2)..end]);
                i = end + 2;
            }
            else if (path[i] == '[')
            {
                int end = path.IndexOf(']', i);
                if (end < 0 || !int.TryParse(path.AsSpan(i + 1, end - i - 1), out int index)) return false;
                segments.Add(index);
                i = end + 1;
            }
            else return false;
        }
        if (segments.Count == 0) return false;
        JsonNode? parent = json;
        foreach (var segment in segments.SkipLast(1))
            parent = segment is int index ? parent is JsonArray a && index < a.Count ? a[index] : null
                : parent is JsonObject o ? o[(string)segment] : null;
        switch (parent, segments[^1])
        {
            case (JsonObject o, string key):
                return o.Remove(key);
            case (JsonArray a, int index) when index < a.Count:
                a.RemoveAt(index);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Keeps a hand-edited or older config inside the values the Metal path can use.
    /// </summary>
    void Normalize()
    {
        if (!float.IsFinite(MetalRenderScale)) MetalRenderScale = 0.5f;
        MetalRenderScale = Math.Clamp(MetalRenderScale, 0.33f, 1f);
        if (!Enum.IsDefined(MetalUpscaler)) MetalUpscaler = MetalUpscalerMode.Temporal;
        if (MetalUpscaler is MetalUpscalerMode.Temporal or MetalUpscalerMode.Spatial) MetalRenderScale = Math.Clamp(MetalRenderScale, 0.4f, 1f);
        ModSettings ??= [];
        foreach (var (mod, saved) in ModSettings.ToList())
            if (saved is null) ModSettings.Remove(mod);
    }

    /// <summary>
    /// Writes the config back to <see cref="Path"/>; a write that fails (I/O, access) is logged.
    /// </summary>
    public void Save()
    {
        try
        {
            File.WriteAllText(Path, JsonSerializer.Serialize(this, Json));
        }
        catch (IOException e)
        {
            Console.Error.WriteLine($"config    could not write {Path}: {e.Message}");
        }
        catch (UnauthorizedAccessException e)
        {
            Console.Error.WriteLine($"config    could not write {Path}: {e.Message}");
        }
    }

    #endregion
}