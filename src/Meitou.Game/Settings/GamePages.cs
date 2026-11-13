using Meitou.Data.Ui;
using Meitou.Game.Effects;
using Meitou.Game.Host;
using Meitou.Game.Host.Cameras;
using Meitou.Game.Host.Input;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Title;
using Meitou.Simulation.Session;
using AudioSettings = Meitou.Game.Audio.AudioSettings;

namespace Meitou.Game.Settings;

/// <summary>
/// The Interface, Controls, Advanced, Audio, Gameplay and Fixes pages: their rows and the sliders made for them.
/// </summary>
sealed class GamePages(SettingsMenu menu)
{
    #region Fields

    /// <summary>
    /// The Main menu transition speed slider (the sky flight's speed from the title).
    /// </summary>
    readonly Slider _mainMenuTransitionSpeed = new("Main menu transition speed", SkyArrival.MinSpeed, SkyArrival.MaxSpeed,
        () => menu.Config.MainMenuTransitionSpeed, v => menu.Config.MainMenuTransitionSpeed = v, "0.00'x'");

    /// <summary>
    /// The In-game transition speed slider (the sky flight's speed in play).
    /// </summary>
    readonly Slider _inGameTransitionSpeed = new("In-game transition speed", SkyArrival.MinSpeed, SkyArrival.MaxSpeed,
        () => menu.Config.InGameTransitionSpeed, v => menu.Config.InGameTransitionSpeed = v, "0.00'x'");

    /// <summary>
    /// The Character flight time slider, 0.5 to 6 seconds.
    /// </summary>
    readonly Slider _characterFlightTime = new("Character flight time", 0.5f, 6, () => menu.Config.CharacterFlightSeconds,
        v => menu.Config.CharacterFlightSeconds = v, "0.0' s'");

    /// <summary>
    /// The camera rotation speed slider, made on first use.
    /// </summary>
    Slider? _cameraRotateSpeed;

    /// <summary>
    /// The camera move and zoom speed sliders, made on first use.
    /// </summary>
    Slider? _cameraMoveSpeed, _cameraZoomSpeed;

    /// <summary>
    /// The Shift pan multiplier slider, made on first use.
    /// </summary>
    Slider? _shiftPanMultiplier;

    /// <summary>
    /// The audio sliders, made on first use.
    /// </summary>
    Slider[]? _audioSliders;

    /// <summary>
    /// The gameplay multiplier sliders, made on first use.
    /// </summary>
    Slider[]? _gameplaySliders;

    /// <summary>
    /// The auto save time slider, made on first use.
    /// </summary>
    Slider? _autoSaveSlider;

    /// <summary>
    /// The attackers per target slider, made on first use.
    /// </summary>
    Slider? _attackSlotsSlider;

    /// <summary>
    /// The harpoon limit slider, made on first use.
    /// </summary>
    Slider? _harpoonLimitSlider;

    /// <summary>
    /// The fast and fastest speed sliders, made on first use.
    /// </summary>
    Slider? _fastSpeedSlider, _fastestSpeedSlider;

    /// <summary>
    /// The dropped items kept slider, made on first use.
    /// </summary>
    Slider? _groundItemsSlider;

    /// <summary>
    /// The follow camera offset's height and side sliders, made on first use.
    /// </summary>
    Slider? _followRaiseSlider, _followSideSlider;

    #endregion

    /// <summary>
    /// The camera rotation speed slider, in half steps; a change goes to <see cref="SettingsMenu.CameraRotateSpeedChanged"/>.
    /// </summary>
    internal Slider CameraRotateSpeed => _cameraRotateSpeed ??= new("Camera rotation speed", UserConfig.MinCameraRotateSpeed,
        UserConfig.MaxCameraRotateSpeed, () => menu.Config.CameraRotateSpeed,
        v =>
        {
            menu.Config.CameraRotateSpeed = MathF.Round(v * 2) / 2;
            menu.CameraRotateSpeedChanged?.Invoke(menu.Config.CameraRotateSpeed);
        },
        "0.#", Faithful: Data.World.KenshiCamera.DefaultRotateSpeed);

    /// <summary>
    /// The camera move speed slider (the game's default when unset).
    /// </summary>
    Slider CameraMoveSpeed => _cameraMoveSpeed ??= CameraSpeedSlider("Camera move speed", UserConfig.MinCameraMoveSpeed,
        UserConfig.MaxCameraMoveSpeed, () => menu.Config.CameraMoveSpeed ?? Data.World.KenshiCamera.DefaultMoveSpeed,
        v => menu.Config.CameraMoveSpeed = v, Data.World.KenshiCamera.DefaultMoveSpeed);

    /// <summary>
    /// The camera zoom speed slider (the game's default when unset).
    /// </summary>
    Slider CameraZoomSpeed => _cameraZoomSpeed ??= CameraSpeedSlider("Camera zoom speed", UserConfig.MinCameraZoomSpeed,
        UserConfig.MaxCameraZoomSpeed, () => menu.Config.CameraZoomSpeed ?? Data.World.KenshiCamera.DefaultZoomSpeed,
        v => menu.Config.CameraZoomSpeed = v, Data.World.KenshiCamera.DefaultZoomSpeed);

    /// <summary>
    /// The Shift pan multiplier slider in 25% steps, marked at Meitou's 200%; a change goes to <see cref="SettingsMenu.CameraChanged"/>.
    /// </summary>
    Slider ShiftPanMultiplier => _shiftPanMultiplier ??= new("Shift pan speed", UserConfig.MinShiftPanMultiplier,
        UserConfig.MaxShiftPanMultiplier, () => menu.Config.ShiftPanMultiplier, SetShiftPanMultiplier, "0.00", Text: v => $"{v * 100:0}%",
        Meitou: UserConfig.DefaultShiftPanMultiplier, Scale: 100, Step: 0.25f);

    /// <summary>
    /// The Shift pan multiplier, rounded to 25% and clamped, into the config and the camera.
    /// </summary>
    void SetShiftPanMultiplier(float v)
    {
        float multiplier = Math.Clamp(MathF.Round(v * 4) / 4, UserConfig.MinShiftPanMultiplier, UserConfig.MaxShiftPanMultiplier);
        menu.Config.ShiftPanMultiplier = multiplier;
        menu.CameraChanged?.Invoke();
    }

    /// <summary>
    /// A camera speed slider in whole steps, marked at the game's <paramref name="original"/>; a change goes to <see
    /// cref="SettingsMenu.CameraChanged"/>.
    /// </summary>
    Slider CameraSpeedSlider(string label, float min, float max, Func<float> get, Action<float> set, float original) =>
        new(label, min, max, get,
            v =>
            {
                set(MathF.Round(v));
                menu.CameraChanged?.Invoke();
            },
            "0", Faithful: original);

    /// <summary>
    /// The audio sliders of the original's options (docs/formats/settings.md); each writes <see cref="UserConfig.Audio"/> and calls <see
    /// cref="SettingsMenu.AudioChanged"/>.
    /// </summary>
    Slider[] AudioSliders => _audioSliders ??=
    [
        Volume("Music volume", a => a.Music, (a, v) => a with { Music = v }),
        Volume("Ambient volume", a => a.Ambient, (a, v) => a with { Ambient = v }),
        Volume("Footstep volume", a => a.Footstep, (a, v) => a with { Footstep = v }),
        Volume("Sfx volume", a => a.Sfx, (a, v) => a with { Sfx = v }),
        Volume("UI volume", a => a.Ui, (a, v) => a with { Ui = v }),
        Volume("VO volume", a => a.Vo, (a, v) => a with { Vo = v }),
        new("Music frequency", AudioSettings.MinFrequency, AudioSettings.MaxFrequency,
            () => (menu.Config.Audio ??= new()).MusicFrequency,
            v =>
            {
                menu.Config.Audio = (menu.Config.Audio ?? new()) with { MusicFrequency = MathF.Round(v) };
                menu.AudioChanged?.Invoke();
            },
            "0", Text: v => $"{v:0} min", Faithful: 5),
        // Meitou's voice cap (docs/audio.md "Voice cap"): the top of the track mixes every sound.
        new("Sound voices", AudioSettings.MinVoices, AudioSettings.MaxVoices,
            () => (menu.Config.Audio ??= new()).Voices,
            v =>
            {
                menu.Config.Audio = (menu.Config.Audio ?? new()) with { Voices = (int)MathF.Round(v / 8) * 8 };
                menu.AudioChanged?.Invoke();
            },
            "0", Text: v => v >= AudioSettings.MaxVoices ? "All" : $"{v:0}", Faithful: AudioSettings.MaxVoices, Meitou: AudioSettings.MeitouVoices),
        // Meitou's hearing range (docs/audio.md "Hearing range"): a factor on every distance sounds are heard from.
        new("Sound range", AudioSettings.MinRange, AudioSettings.MaxRange,
            () => (menu.Config.Audio ??= new()).Range,
            v =>
            {
                float range = Math.Clamp(MathF.Round(v * 4) / 4, AudioSettings.MinRange, AudioSettings.MaxRange);
                menu.Config.Audio = (menu.Config.Audio ?? new()) with { Range = range };
                menu.AudioChanged?.Invoke();
            },
            "0.##", Text: v => $"{v:0.##}x", Faithful: 1, Meitou: AudioSettings.MeitouRange),
    ];

    /// <summary>
    /// The four sliders of the original's Gameplay page (docs/formats/settings.md "Gameplay"); each writes <see cref="UserConfig.Gameplay"/> and
    /// calls <see cref="SettingsMenu.GameplayChanged"/>.
    /// </summary>
    Slider[] GameplaySliders => _gameplaySliders ??=
    [
        Multiplier("Squad size multiplier", GameplaySettings.MinSquadSize, GameplaySettings.MaxSquadSize, g => g.SquadSize,
            (g, v) => g with { SquadSize = v }),
        Multiplier("Global population multiplier", GameplaySettings.MinPopulation, GameplaySettings.MaxPopulation, g => g.Population,
            (g, v) => g with { Population = v }),
        Multiplier("Town raid events: size", GameplaySettings.MinRaidSize, GameplaySettings.MaxRaidSize, g => g.RaidSize,
            (g, v) => g with { RaidSize = v }),
        Multiplier("Town raid events: frequency", GameplaySettings.MinRaidFrequency, GameplaySettings.MaxRaidFrequency, g => g.RaidFrequency,
            (g, v) => g with { RaidFrequency = v }),
    ];

    /// <summary>
    /// A gameplay multiplier slider in steps of 0.05, marked at 1 (the game's); a change saves through <see cref="SetGameplay"/>.
    /// </summary>
    Slider Multiplier(string label, float min, float max, Func<GameplaySettings, float> get, Func<GameplaySettings, float, GameplaySettings> set) =>
        new(label, min, max, () => get(menu.Config.Gameplay ??= new()),
            v => SetGameplay(set(menu.Config.Gameplay ?? new(), Math.Clamp(MathF.Round(v * 20) / 20, min, max))),
            "0.##", Faithful: 1);

    /// <summary>
    /// The auto save time slider, whole minutes, marked at the game's 10.
    /// </summary>
    Slider AutoSaveSlider => _autoSaveSlider ??= new("Auto save time (minutes)", GameplaySettings.MinAutoSaveMinutes,
        GameplaySettings.MaxAutoSaveMinutes, () => (menu.Config.Gameplay ??= new()).AutoSaveMinutes,
        v => SetGameplay((menu.Config.Gameplay ?? new()) with { AutoSaveMinutes = MathF.Round(v) }), "0", Text: v => $"{v:0} min", Faithful: 10);

    /// <summary>
    /// Sets the gameplay settings and calls <see cref="SettingsMenu.GameplayChanged"/>.
    /// </summary>
    internal void SetGameplay(GameplaySettings value)
    {
        menu.Config.Gameplay = value;
        menu.GameplayChanged?.Invoke();
    }

    /// <summary>
    /// A volume slider 0..1 shown as a percentage, marked at the game's 0.5; a change calls <see cref="SettingsMenu.AudioChanged"/>.
    /// </summary>
    Slider Volume(string label, Func<AudioSettings, float> get, Func<AudioSettings, float, AudioSettings> set) =>
        new(label, 0, 1, () => get(menu.Config.Audio ??= new()),
            v =>
            {
                menu.Config.Audio = set(menu.Config.Audio ?? new(), Math.Clamp(v, 0, 1));
                menu.AudioChanged?.Invoke();
            },
            "0.00", Text: v => $"{v * 100:0}%", Faithful: 0.5f, Scale: 100);

    /// <summary>
    /// The attackers per target slider, 1 to 5; a change goes to <see cref="SettingsMenu.AttackSlotsChanged"/>.
    /// </summary>
    internal Slider AttackSlotsSlider => _attackSlotsSlider ??= new("Attackers per target", 1, 5, () => menu.Config.AttackSlots, v =>
    {
        menu.Config.AttackSlots = Math.Clamp((int)MathF.Round(v), 1, 5);
        menu.AttackSlotsChanged?.Invoke(menu.Config.AttackSlotsOption);
    }, "0", Faithful: UserConfig.FaithfulAttackSlots, Meitou: UserConfig.MeitouAttackSlots);

    /// <summary>
    /// The harpoon limit slider (logarithmic, whole counts), marked at the game's default.
    /// </summary>
    Slider HarpoonLimitSlider => _harpoonLimitSlider ??= new("Harpoon limit", Simulation.Combat.RangedTuning.MinStuckLimit,
        Simulation.Combat.RangedTuning.MaxStuckLimit, () => (menu.Config.Gameplay ??= new()).HarpoonLimit,
        v => SetGameplay((menu.Config.Gameplay ?? new()) with { HarpoonLimit = ClampHarpoonLimit(v) }), "0", Logarithmic: true,
        Faithful: Simulation.Combat.RangedTuning.DefaultStuckLimit);

    /// <summary>
    /// A stuck-limit slider value as a whole count within the ranged tuning's bounds.
    /// </summary>
    static int ClampHarpoonLimit(float v) =>
        Math.Clamp((int)MathF.Round(v), Simulation.Combat.RangedTuning.MinStuckLimit, Simulation.Combat.RangedTuning.MaxStuckLimit);

    /// <summary>
    /// The fast speed slider, in half steps, marked at the game's default.
    /// </summary>
    Slider FastSpeedSlider => _fastSpeedSlider ??= TimeSpeed("Fast speed", SimulationClock.MinFastSpeed, SimulationClock.MaxFastSpeed, 1,
        () => menu.Config.FastSpeed, v => menu.Config.FastSpeed = v);

    /// <summary>
    /// The fastest speed slider, in half steps, marked at the game's default.
    /// </summary>
    Slider FastestSpeedSlider => _fastestSpeedSlider ??= TimeSpeed("Fastest speed", SimulationClock.MinFastestSpeed,
        SimulationClock.MaxFastestSpeed, 2, () => menu.Config.FastestSpeed, v => menu.Config.FastestSpeed = v);

    /// <summary>
    /// A speed button's game speed, in half steps, applied to the running clock at once.
    /// </summary>
    Slider TimeSpeed(string label, double min, double max, int step, Func<double> get, Action<double> set) =>
        new(label, (float)min, (float)max, () => (float)get(),
            v =>
            {
                set(Math.Clamp(Math.Round(v * 2) / 2, min, max));
                menu.TimeSpeedsChanged?.Invoke();
            },
            "0.#", Text: v => $"{v:0.#}x", Faithful: (float)SimulationClock.DefaultSpeeds[step]);

    /// <summary>
    /// The dropped items kept slider (logarithmic), marked at Meitou's default.
    /// </summary>
    Slider GroundItemsSlider => _groundItemsSlider ??= new("Dropped items kept", 16, Simulation.Items.GroundItems.MaxLimit,
        () => menu.Config.GroundItemLimit,
        v => menu.Config.GroundItemLimit = Math.Clamp((int)MathF.Round(v), 1, Simulation.Items.GroundItems.MaxLimit), "0", Logarithmic: true,
        Meitou: Simulation.Items.GroundItems.DefaultLimit);

    /// <summary>
    /// The Interface page: UI style, units, floaters, font size, interface scale, names, markers and the mouse.
    /// </summary>
    internal void InterfaceRows(List<SettingsMenu.Entry> result)
    {
        result.Add(new("UI style", "Choose original Kenshi skins or Meitou materials.",
            Choice: new(() => ["Faithful", "Meitou"], () => (int)menu.Player.UiMode, i => menu.Player.SetUiMode((UiMode)i))));
        result.Add(new("Main menu style", "Choose the original Kenshi title screen or the Meitou main menu over a live world scene, whatever the UI style.",
            Choice: new(() => ["Faithful", "Meitou"], () => menu.Player.MeitouMainMenu ? 1 : 0, i => menu.Player.MeitouMainMenu = menu.Config.MeitouMainMenu = i == 1)));
        result.Add(new("Speed units", "The unit of the run speed in the character's information panel (the game: mph).",
            Choice: new(() => ["mph", "km/h"], () => (int)menu.Config.SpeedUnit, i => menu.Player.SpeedUnit = menu.Config.SpeedUnit = (SpeedUnit)i)));
        result.Add(new("Damage floaters", "Numbers that float over a character when a blow lands, with the damage armour stopped in brackets. "
            + "Simple shows the total, Full one number per damage type and the part hit.",
            Choice: new(() => ["Off", "Simple info", "Full info"], () => (int)menu.Config.Floaters,
                i => menu.Player.Floaters = menu.Config.Floaters = (FloaterMode)i)));
        result.Add(new("Font size", "Points added to every interface font's default height (the game: 3).",
            Slider: new("Font size", UserConfig.MinFontSize, UserConfig.MaxFontSize, () => menu.Config.FontSize ?? KenshiFontSize.DefaultSetting,
                v => SetFontSize(v), "0", Faithful: KenshiFontSize.DefaultSetting)));
        result.Add(new("Interface scale", "The size of the whole interface: text, buttons and windows (100%: as the window's height makes it).",
            Slider: new("Interface scale", UserConfig.MinInterfaceScale, UserConfig.MaxInterfaceScale, () => menu.Config.InterfaceScale,
                SetInterfaceScale, "0.00", Text: v => $"{v * 100:0}%", Faithful: 1, Scale: 100, Step: 0.05f)));
        result.Add(new("Show names",
            "The name over each of your characters (the game's Show names option). Off, only the stealth and light icons stay.",
            Toggle: new("Show names", () => menu.Config.ShowNames, v => menu.Player.ShowNames = menu.Config.ShowNames = v, OnOff: true)));
        result.Add(new("Show movement marker", "The green marker where a move order sends your characters (the game: on).",
            Toggle: new("Show movement marker", () => menu.Config.MovementMarker != false,
                v => menu.Player.MovementMarker = (menu.Config.MovementMarker = v) == true, OnOff: true)));
        result.Add(new("Show rotation marker", "The green marker on the camera's pivot while you turn it (the game: on).",
            Toggle: new("Show rotation marker", () => menu.Config.RotationMarker != false,
                v => menu.Player.RotationMarker = (menu.Config.RotationMarker = v) == true, OnOff: true)));
        result.Add(new("Censorship", "Swaps a few strong words in the dialogue for milder ones (the game's censorship option).",
            Toggle: new("Censorship", () => menu.Config.Censorship, v => menu.Config.Censorship = v, OnOff: true)));
        result.Add(new("Feedback button",
            "The FEEDBACK button at the top right during play. Off, it still shows on the title screen and in the Esc menu.",
            Toggle: new("Feedback button", () => menu.Config.FeedbackButton, v => menu.Player.FeedbackButtonInPlay = menu.Config.FeedbackButton = v,
                OnOff: true)));
        result.Add(new("Hardware mouse",
            "Use the Windows cursor instead of the game's drawn pointer (no hover pointers unless Enhanced hardware mouse is on).",
            Toggle: new("Hardware mouse", () => menu.Config.HardwareMouse, v => menu.Config.HardwareMouse = v)));
        if (menu.Config.HardwareMouse)
        {
            result.Add(new("Enhanced hardware mouse",
                "The icon of what a right click would do beside the Windows cursor (none for a walk), and the hand over buttons and targets.",
                Toggle: new("Enhanced hardware mouse", () => menu.Config.EnhancedHardwareMouse, v => menu.Config.EnhancedHardwareMouse = v,
                    OnOff: true),
                Depth: 1));
        }
    }

    /// <summary>
    /// The UI font size, rounded and clamped, into the config and the player interface.
    /// </summary>
    void SetFontSize(float v)
    {
        float size = Math.Clamp(MathF.Round(v), UserConfig.MinFontSize, UserConfig.MaxFontSize);
        menu.Config.FontSize = size;
        menu.Player.FontSize = size;
    }

    /// <summary>
    /// The Interface scale, rounded to 5% and clamped, into the config and the player interface.
    /// </summary>
    void SetInterfaceScale(float v)
    {
        float scale = Math.Clamp(MathF.Round(v * 20) / 20, UserConfig.MinInterfaceScale, UserConfig.MaxInterfaceScale);
        menu.Config.InterfaceScale = scale;
        menu.Player.InterfaceScale = scale;
    }

    /// <summary>
    /// The Controls page: the camera's speeds and mouse, then one row per bindable action.
    /// </summary>
    internal void ControlsRows(List<SettingsMenu.Entry> result)
    {
        result.Add(new(CameraRotateSpeed.Label, "How fast the rotate and tilt keys and the mouse turn the camera (the game: 6).",
            Slider: CameraRotateSpeed));
        result.Add(new(CameraMoveSpeed.Label,
            "How fast the arrow keys and the screen edge pan the camera (the game: 500; faster the higher the camera).", Slider: CameraMoveSpeed));
        result.Add(new(CameraZoomSpeed.Label, "How fast the wheel and the zoom keys move the camera in and out (the game: 125).",
            Slider: CameraZoomSpeed));
        result.Add(new("Shift pan boost", "Holding Shift with the camera move keys pans the camera faster (the game has no such boost).",
            Toggle: new("Shift pan boost", () => menu.Config.ShiftPanBoost, SetShiftPanBoost, Remaster: true, OnOff: true)));
        if (menu.Config.ShiftPanBoost)
        {
            result.Add(new(ShiftPanMultiplier.Label, "The camera's pan speed while Shift is held, of its normal speed.", Slider: ShiftPanMultiplier,
                Depth: 1));
        }
        result.Add(new("Invert mouse X", "Reverses the camera's turn, for the mouse and the keys (the game: off).",
            Toggle: new("Invert mouse X", () => menu.Config.InvertX == true, v => { menu.Config.InvertX = v; menu.CameraChanged?.Invoke(); },
                OnOff: true)));
        result.Add(new("Invert mouse Y", "Reverses the camera's tilt, for the mouse and the keys (the game: on).",
            Toggle: new("Invert mouse Y", () => menu.Config.InvertY != false, v => { menu.Config.InvertY = v; menu.CameraChanged?.Invoke(); },
                OnOff: true)));
        result.Add(new("Swap mouse buttons", "Swap the left and right mouse buttons (the game: off; the game view follows, as in the original).",
            Toggle: new("Swap mouse buttons", () => menu.Config.SwapMouseButtons == true,
                v => KeyMap.SwapButtons = (menu.Config.SwapMouseButtons = v) == true, OnOff: true)));
        result.Add(new("Edge scrolling", "The camera moves when the pointer is at the edge of the screen.",
            Toggle: new("Edge scrolling", () => menu.Config.EdgeScroll != false, v => menu.Config.EdgeScroll = v)));
        result.Add(new("Camera smoothing",
            "The camera's turn and tilt ease in and out (the game: always on). Off: they follow the mouse and the keys at once.",
            Toggle: new("Camera smoothing", () => menu.Config.CameraSmoothing, SetCameraSmoothing)));
        result.Add(new("Loot into backpack",
            "Picked-up and looted items go into the character's backpack first, then its slots and inventory (the game: only while the bag "
            + "window is open).",
            Toggle: new("Loot into backpack", () => menu.Config.TakeIntoBackpack, SetTakeIntoBackpack, OnOff: true)));
        foreach (var action in Enum.GetValues<InputAction>().Where(a => a != InputAction.Quit && a != InputAction.ToggleFps))
            result.Add(new(SettingsMenu.ActionLabel(action), SettingHints.BindingHint(action), Binding: action));
    }

    /// <summary>
    /// The Shift pan boost switch, into the config and the camera.
    /// </summary>
    void SetShiftPanBoost(bool on)
    {
        menu.Config.ShiftPanBoost = on;
        menu.CameraChanged?.Invoke();
    }

    /// <summary>
    /// The Loot into backpack switch, into the config and the running world.
    /// </summary>
    void SetTakeIntoBackpack(bool on)
    {
        menu.Config.TakeIntoBackpack = on;
        menu.TakeIntoBackpackChanged?.Invoke(on);
    }

    /// <summary>
    /// The Camera smoothing toggle, into the config and the camera.
    /// </summary>
    void SetCameraSmoothing(bool on)
    {
        menu.Config.CameraSmoothing = on;
        menu.CameraChanged?.Invoke();
    }

    /// <summary>
    /// The Audio page: the volumes, music frequency, voices and range, and where the listener is.
    /// </summary>
    internal void AudioRows(List<SettingsMenu.Entry> result)
    {
        string[] hints = [
            "Volume of the biome music and the main theme.",
            "Volume of the weather and the world's ambience.",
            "Volume of footsteps.",
            "Volume of combat, building and other sound effects.",
            "Volume of the interface sounds.",
            "Volume of the voices.",
            "Average minutes of silence between pieces of biome music.",
            "The most sounds heard at once: the loudest play, quieter ones wait silently and come back when they are loud enough. Lower it if big "
            + "fights stutter; All mixes every sound.",
            "How far away sounds are heard: 1x is the original, 4x hears fights, footsteps and machines four times as far. Higher values mix more "
            + "sounds at once.",
        ];
        for (int i = 0; i < AudioSliders.Length; i++)
            result.Add(new(AudioSliders[i].Label, hints[i], Slider: AudioSliders[i]));
        result.Add(new("Hear from",
            "Camera: you hear the world where the camera is. Character: you hear it where your first selected character stands (from the camera while "
            + "nobody is selected).",
            Toggle: new("Hear from", () => menu.Config.HearFromSelected, v => menu.Config.HearFromSelected = v,
                Text: () => menu.Config.HearFromSelected ? "Character" : "Camera")));
    }

    /// <summary>
    /// The Gameplay page: the original's multipliers and options, autosave, zone hopping and tutorials.
    /// </summary>
    internal void GameplayRows(List<SettingsMenu.Entry> result)
    {
        string[] hints = [
            "Size of the squads made from now on: townspeople, roaming squads and raids (the game: 1). Affects performance.",
            "How many squads roam and live in the wild (the game: 1). Affects performance.",
            "How many fighters a raid on your towns gathers (the game: 1).",
            "How often factions start raids on your towns (the game: 1).",
        ];
        for (int i = 0; i < GameplaySliders.Length; i++)
            result.Add(new(GameplaySliders[i].Label, hints[i], Slider: GameplaySliders[i]));
        result.Add(new("Town attacks frequency",
            "How long a faction waits between attacks on your towns by squads passing by (the game: Normal). Never stops them.",
            Choice: new(() => GameplaySettings.TownAttackLabels, () => (menu.Config.Gameplay ??= new()).TownAttacks,
                i => SetGameplay((menu.Config.Gameplay ?? new()) with { TownAttacks = i }))));
        result.Add(new("Dismemberment",
            "When a destroyed limb is cut off: Rare only by a single heavy blow, Frequent also when a wound worsens (the game: Rare).",
            Choice: new(() => GameplaySettings.DismembermentLabels, () => (int)(menu.Config.Gameplay ??= new()).Dismemberment,
                i => SetGameplay((menu.Config.Gameplay ?? new()) with { Dismemberment = (Simulation.Bodies.Dismemberment)i }))));
        result.Add(new("Auto save",
            "Saves the game on its own every few minutes of unpaused play, into three slots in turn (autosave0 to autosave2).",
            Toggle: new("Auto save", () => (menu.Config.Gameplay ??= new()).AutoSave,
                v => SetGameplay((menu.Config.Gameplay ?? new()) with { AutoSave = v }), OnOff: true)));
        result.Add(new(AutoSaveSlider.Label, "Minutes of unpaused play between autosaves (the game: 10).", Slider: AutoSaveSlider));
        result.Add(new("Fast zone hopping",
            "Keeps a larger area loaded round each of your characters, so switching between distant ones skips most of the loading (the game: off; "
            + "uses more memory).",
            Toggle: new("Fast zone hopping", () => (menu.Config.Gameplay ??= new()).FastZoneHopping,
                v => SetGameplay((menu.Config.Gameplay ?? new()) with { FastZoneHopping = v }), OnOff: true)));
        result.Add(new("Tutorials", "Shows the tutorial hints as you play (the game: on).",
            Toggle: new("Tutorials", () => menu.Config.Tutorials != false, v => { menu.Config.Tutorials = v; menu.TutorialsChanged?.Invoke(v); },
                OnOff: true)));
        result.Add(new("Reset Tutorials", "Every tutorial can show again, the ones already seen included.", Run: () => menu.ResetTutorials?.Invoke()));
    }

    /// <summary>
    /// The Fixes page: the game's bugs, each fixed or as it ships, under one switch for all.
    /// </summary>
    internal void FixesRows(List<SettingsMenu.Entry> result)
    {
        // Bugs of the game as it ships; on fixes them, off plays as the game does.
        result.Add(new("Raids as written",
            "Raid and campaign data that builds on a base campaign counts the base's squads and follow-up campaigns once. "
            + "The game counts them twice, so inherited squads come up more often and follow-up raids are about twice as likely.",
            Toggle: new("Raids as written", () => menu.Config.CampaignAncestorsOnce,
                v => { menu.Config.CampaignAncestorsOnce = v; menu.OffscreenChanged?.Invoke(); }, Remaster: true, OnOff: true)));
        result.Add(new("Door break sound",
            "Doors and gates make their breaking sound when they are bashed or broken open. "
            + "The game never picks which breaking sound to play, so it plays none.",
            Toggle: new("Door break sound", () => menu.Config.FixDoorBreak, v => menu.Config.FixDoorBreak = v, Remaster: true, OnOff: true)));
        result.Add(new("Ambience after loading",
            "A biome whose sounds were not loaded yet starts its day or night ambience by the time of day. "
            + "The game plays the opposite one (night sounds by day) until the next biome or day change.",
            Toggle: new("Ambience after loading", () => menu.Config.FixAmbienceSuffix, v => menu.Config.FixAmbienceSuffix = v,
                Remaster: true, OnOff: true)));
        result.Add(new("Wind stops",
            "The wind's sound ends when the wind dies down. The game asks for a stop sound that does not exist, so once the wind has blown it never "
            + "goes quiet.",
            Toggle: new("Wind stops", () => menu.Config.FixWindStop, v => menu.Config.FixWindStop = v, Remaster: true, OnOff: true)));
        result.Add(new("Load button",
            "The load window's Load button works only once a save that can be read is selected. "
            + "The game keeps it live: with nothing selected a click does nothing, and an unreadable save fails to load.",
            Toggle: new("Load button", () => menu.Config.FixLoadButton,
                v => menu.Player.Frontend.FixLoadButton = menu.Config.FixLoadButton = v, Remaster: true, OnOff: true)));
        result.Add(new("Turret gunners turn",
            "A turret's gunner turns with the gun, facing where it aims. "
            + "In the game the gunner keeps facing the turret, so once the gun swings toward a target the gunner looks off to the side.",
            Toggle: new("Turret gunners turn", () => menu.Config.FixGunnerFacing,
                v => { menu.Config.FixGunnerFacing = v; menu.GunnerFacingChanged?.Invoke(v); }, Remaster: true, OnOff: true)));
        result.Add(new("Weak punch reach",
            "Fighters with martial arts of 20 or less throw their weak punch from a normal punching distance. "
            + "The game's data gives it a negative reach, so they throw it only when nearly touching their target.",
            Toggle: new("Weak punch reach", () => menu.Config.FixPunchReach,
                v => { menu.Config.FixPunchReach = v; menu.PunchReachChanged?.Invoke(v); }, Remaster: true, OnOff: true)));
        result.Add(new("Bench facing",
            "A character on a bench with the table behind it sits facing away from the table. "
            + "The game mirrors the bench's look point through the middle of the world map, so the sitter faces an unrelated direction.",
            Toggle: new("Bench facing", () => menu.Config.FixBenchFacing,
                v => { menu.Config.FixBenchFacing = v; menu.BenchFacingChanged?.Invoke(v); }, Remaster: true, OnOff: true)));
        result.Add(new("Walls inside buildings",
            "Seen into a building your characters are in, parts of other buildings that run through it are left out, such as the Hub's town wall "
            + "in the Shinobi Thieves tower. The game's world places them there and draws them inside the room.",
            Toggle: new("Walls inside buildings", () => menu.Config.FixPartsInBuildings, v => menu.Config.FixPartsInBuildings = v,
                Remaster: true, OnOff: true)));
        result.Insert(0, menu.PageSidesEntry(result, "All fixes", "Every bug on this page as the game ships it, or every fix.", "fix"));
    }

    /// <summary>
    /// The Advanced page: Meitou's gameplay changes and conveniences, under one switch for all, and the cache buttons.
    /// </summary>
    internal void AdvancedRows(List<SettingsMenu.Entry> result)
    {
        // A graphics switch that changes play: the stealth sight counts the lamps it lights, so no look preset or Faithful sets it.
        if (menu.Values.Toggles.FirstOrDefault(t => t.Label == "Interior lamps") is { } lamps)
            result.Add(new("Interior lamps", SettingHints.ToggleHint(lamps.Label), Toggle: lamps));

        AddFightRows(result);
        AddOffscreenRows(result);
        AddWorldRows(result);
        AddOptionalRows(result);
        AddSceneRows(result);
        result.Insert(0, menu.PageSidesEntry(result, "All gameplay changes", "Every change on this page as the game plays, or every Meitou change.",
            "gameplay change"));
    }

    /// <summary>
    /// The fight rows: attack slots, the stuck bolt and harpoon limit and late dodges and blocks.
    /// </summary>
    void AddFightRows(List<SettingsMenu.Entry> result)
    {
        result.Add(new(AttackSlotsSlider.Label,
            "How many enemies may swing at one character at once (the game: 1, plus its race's extra slots); the others wait at reach or circle.",
            Slider: AttackSlotsSlider));
        result.Add(new(HarpoonLimitSlider.Label,
            "How many bolts and harpoons stay stuck in the ground and buildings, per kind of bolt; past it the one that flew longest disappears (the "
            + "game: 60).",
            Slider: HarpoonLimitSlider));
        result.Add(new("Late dodges and blocks",
            "Balance change: a dodge or block that succeeds its roll but starts a moment late still works, "
            + "instead of failing as in the game. Makes skilled fighters much harder to wear down when several enemies attack at once.",
            Toggle: new("Late dodges and blocks", () => menu.Config.LateDefences,
                v => { menu.Config.LateDefences = v; menu.LateDefencesChanged?.Invoke(v); }, Remaster: true, OnOff: true)));
    }

    /// <summary>
    /// The rows for what goes on away from the player's squads: roaming squads, fights, healing, towns and production.
    /// </summary>
    void AddOffscreenRows(List<SettingsMenu.Entry> result)
    {
        result.Add(new("Squads keep their people",
            "Roaming squads keep their characters when they leave the area around your squads, and travel on as themselves. "
            + "In the game they are removed and new ones are made later.",
            Toggle: new("Squads keep their people", () => menu.Config.KeepRoamers,
                v => { menu.Config.KeepRoamers = v; menu.OffscreenChanged?.Invoke(); }, Remaster: true, OnOff: true)));
        result.Add(new("Fights far away",
            "Hostile squads that meet far from your squads fight it out by strength; the loser is wiped out. In the game nothing happens far away.",
            Toggle: new("Fights far away", () => menu.Config.OffscreenFights,
                v => { menu.Config.OffscreenFights = v; menu.OffscreenChanged?.Invoke(); }, Remaster: true, OnOff: true)));
        result.Add(new("Healing far away",
            "A squad coming back into the area around your squads has healed for the time it was away. In the game it comes back as it left.",
            Toggle: new("Healing far away", () => menu.Config.OffscreenHealing,
                v => { menu.Config.OffscreenHealing = v; menu.OffscreenChanged?.Invoke(); }, Remaster: true, OnOff: true)));
        result.Add(new("Wild squads stay",
            "Animals, bandits and other wild squads keep their characters, wounds and places when the area around them unloads, "
            + "and are there again when you come back. In the game they are removed and new ones are made.",
            Toggle: new("Wild squads stay", () => menu.Config.KeepWild,
                v => { menu.Config.KeepWild = v; menu.OffscreenChanged?.Invoke(); }, Remaster: true, OnOff: true)));
        result.Add(new("Towns keep their schedule",
            "Townspeople you come back to after an hour or more are where their day puts them: in bed at night, at the counter, at their posts and "
            + "seats. "
            + "In the game they are where they stood when you left.",
            Toggle: new("Towns keep their schedule", () => menu.Config.ResidentSchedule,
                v => { menu.Config.ResidentSchedule = v; menu.OffscreenChanged?.Invoke(); }, Remaster: true, OnOff: true)));
        result.Add(new("Production far away",
            "Farms, mines, wells and generators away from your squads keep working. "
            + "In the game they stop while their area is unloaded, and only farms catch up when you come back.",
            Toggle: new("Production far away", () => menu.Config.UnloadedProduction,
                v => { menu.Config.UnloadedProduction = v; menu.OffscreenChanged?.Invoke(); }, Remaster: true, OnOff: true)));
    }

    /// <summary>
    /// The world and play rows: doors, decapitation, ragdolls, the speeds, pausing, animals, outlines, selection and ground items.
    /// </summary>
    void AddWorldRows(List<SettingsMenu.Entry> result)
    {
        result.Add(new("Doors close behind",
            "A door someone opened by walking through it closes again a few seconds after the last person went through. In the game doors stay open.",
            Toggle: new("Doors close behind", () => menu.Config.DoorsCloseBehind,
                v => { menu.Config.DoorsCloseBehind = v; menu.DoorsCloseChanged?.Invoke(v); }, Remaster: true, OnOff: true)));
        result.Add(new("Decapitation",
            "A blow that destroys a character's head cuts it off, by the same rule the Dismemberment setting cuts off limbs. "
            + "In the game a destroyed head only kills.",
            Toggle: new("Decapitation", () => menu.Config.Decapitation,
                v => { menu.Config.Decapitation = v; menu.GameplayChanged?.Invoke(); }, Remaster: true, OnOff: true)));
        result.Add(new("Advanced ragdolls",
            "A character knocked down or killed by a blow falls with its own momentum and is pushed away from the hit, harder the heavier the blow "
            + "and most at the part that was struck, its arms and legs flung by it, and a cut-off head flies the same way. "
            + "Limp bodies have joints that ease to a stop and a slight muscle tone, so they settle into natural poses. "
            + "In the game a body drops where it stood, its joints stopping hard.",
            Toggle: new("Advanced ragdolls", () => menu.Config.AdvancedRagdolls,
                v => { menu.Config.AdvancedRagdolls = v; menu.GameplayChanged?.Invoke(); }, Remaster: true)));
        result.Add(new(FastSpeedSlider.Label, "The game speed of the second speed button (two arrows) and its key (the game: 2x).",
            Slider: FastSpeedSlider));
        result.Add(new(FastestSpeedSlider.Label,
            "The game speed of the third speed button (three arrows) and its key (the game: 5x). "
            + "High speeds need a fast processor; when it cannot keep up the game runs slower than chosen.",
            Slider: FastestSpeedSlider));
        result.Add(new("Pause after loading",
            "A save starts paused once it has loaded, so nothing happens before you look around. In the game it runs at once.",
            Toggle: new("Pause after loading", () => menu.Config.PauseOnLoad, v => menu.Config.PauseOnLoad = v, Remaster: true, OnOff: true)));
        result.Add(new("Animals pick up items",
            "Faithful, as in the game: your pack animals pick up an item you point them at, into the bag they wear (or wear a bag they pick up). "
            + "Meitou: only people pick items up, and an animal only a limb to carry in its mouth.",
            Toggle: new("Animals pick up items", () => !menu.Config.AnimalsPickUp,
                v => { menu.Config.AnimalsPickUp = !v; menu.AnimalsPickUpChanged?.Invoke(!v); }, Remaster: true)));
        result.Add(new("Hover outline", "Outline and tint the character under the cursor, in the colour of what a right click would do.",
            Toggle: new("Hover outline", () => menu.Config.HoverOutline, v => menu.Config.HoverOutline = v, Remaster: true, OnOff: true)));
        result.Add(new("Building outline",
            "Outline and tint the selected building, and the building under the cursor in the colour of what a right click would do, "
            + "like a character under the cursor, except while its interior is shown.",
            Toggle: new("Building outline", () => menu.Config.BuildingOutline, v => menu.Config.BuildingOutline = v, Remaster: true, OnOff: true)));
        result.Add(new("Click away deselects",
            "A left click on the ground ends the selection of the selected building and closes its panels. In the game the building stays selected.",
            Toggle: new("Click away deselects", () => menu.Config.DeselectBuildings,
                v => menu.Player.BuildingSelection.DeselectBuildings = menu.Config.DeselectBuildings = v, Remaster: true, OnOff: true)));
        result.Add(new("luckyren Mode",
            "A character that takes a fatal hit explodes, covers everything around in blood and sends bones flying everywhere. "
            + "Its gear falls to the ground around the spot.",
            Toggle: new("luckyren Mode", () => menu.Config.LuckyrenMode, v => menu.Config.LuckyrenMode = v),
            Picture: SettingsMenuArt.LuckyrenImage));
        result.Add(new(GroundItemsSlider.Label,
            "How many items dropped during play (severed limbs, luckyren Mode's gear) stay on the ground; past it the oldest disappears. "
            + "Items that were on the ground in the save always stay.",
            Slider: GroundItemsSlider));
    }

    /// <summary>
    /// The follow camera offset's height slider, in half steps, marked at the game's 0.
    /// </summary>
    Slider FollowRaiseSlider => _followRaiseSlider ??= new("Follow camera height", FollowFocus.MinRaise, FollowFocus.MaxRaise,
        () => menu.Config.FollowCameraRaise, v => menu.Config.FollowCameraRaise = HalfStep(v, FollowFocus.MinRaise, FollowFocus.MaxRaise), "0.0",
        Faithful: 0, Step: 0.5f);

    /// <summary>
    /// The follow camera offset's side slider, in half steps, marked at the game's 0.
    /// </summary>
    Slider FollowSideSlider => _followSideSlider ??= new("Follow camera side", -FollowFocus.MaxSide, FollowFocus.MaxSide,
        () => menu.Config.FollowCameraSide, v => menu.Config.FollowCameraSide = HalfStep(v, -FollowFocus.MaxSide, FollowFocus.MaxSide), "0.0",
        Faithful: 0, Step: 0.5f);

    /// <summary>
    /// <paramref name="v"/> rounded to a half and kept within <paramref name="min"/> and <paramref name="max"/>.
    /// </summary>
    static float HalfStep(float v, float min, float max) => Math.Clamp(MathF.Round(v * 2) / 2, min, max);

    /// <summary>
    /// The optional rows, off by default and in neither the all Faithful nor the all Meitou switch: the follow camera offset and item rotation
    /// (after ErasmianFolly's Camera Tweaks and GetRotatedIdiot mods) and the hidden hats.
    /// </summary>
    void AddOptionalRows(List<SettingsMenu.Entry> result)
    {
        result.Add(new("Follow camera offset",
            "The camera following a character looks higher than the game's point 10 units above its feet, and to one side if wanted, for a view "
            + "from head height or over the shoulder. The free camera and the unlocked camera are not changed. From ErasmianFolly's Camera Tweaks.",
            Toggle: new("Follow camera offset", () => menu.Config.FollowCameraOffset, v => menu.Config.FollowCameraOffset = v)));
        if (menu.Config.FollowCameraOffset) AddFollowCameraRows(result);

        result.Add(new("Item rotation",
            "The Rotate item control (the middle mouse button by default, Controls page) turns the inventory item on the mouse a quarter, or the "
            + "item under the pointer in a character's grid, so long items fit across. Worn items are never turned. In the game items "
            + "cannot be turned. From ErasmianFolly's GetRotatedIdiot.",
            Toggle: new("Item rotation", () => menu.Config.RotateItems, SetRotateItems)));
        AddHideHatsRows(result);
    }

    /// <summary>
    /// The Hide hats choice and, while it is on, its row for the other factions' characters.
    /// </summary>
    void AddHideHatsRows(List<SettingsMenu.Entry> result)
    {
        result.Add(new("Hide hats",
            "Leaves the hats and helmets of your characters out of the picture, so their faces and hair show; they stay worn and protect as before. "
            + "Portraits only: in the interface's portraits. On: in the world too. Off: hats are drawn everywhere, as in the game.",
            Choice: new(() => ["Off", "Portraits only", "On"], () => (int)menu.Config.HideHats, i => menu.Config.HideHats = (HideHats)i)));
        if (menu.Config.HideHats != HideHats.On) return;

        result.Add(new("Hide other factions' hats",
            "The hats of every other character are left out the same way, not only your own characters'.",
            Toggle: new("Hide other factions' hats", () => menu.Config.HideHatsOfOthers, v => menu.Config.HideHatsOfOthers = v, OnOff: true),
            Depth: 1));
    }

    /// <summary>
    /// The follow camera offset's own rows: the head lock, the height and the side.
    /// </summary>
    void AddFollowCameraRows(List<SettingsMenu.Entry> result)
    {
        result.Add(new("Follow camera head lock",
            "The camera's height follows the character's head as it crouches, sits or lies down. On, the height slider is not used.",
            Toggle: new("Follow camera head lock", () => menu.Config.FollowCameraHeadLock, v => menu.Config.FollowCameraHeadLock = v, OnOff: true),
            Depth: 1));
        result.Add(new(FollowRaiseSlider.Label, "How far above the game's point the camera looks: 0 is the game's height, about 17 a person's head.",
            Slider: FollowRaiseSlider, Depth: 1));
        result.Add(new(FollowSideSlider.Label,
            "Moves the point the camera looks at sideways: above 0 the character stands left of the middle (a view over the right shoulder), below 0 "
            + "right of it. It shows less the farther the camera is.",
            Slider: FollowSideSlider, Depth: 1));
    }

    /// <summary>
    /// The Item rotation switch, into the config and the inventory windows.
    /// </summary>
    void SetRotateItems(bool on) => menu.Player.InventoryWindows.RotateItems = menu.Config.RotateItems = on;

    /// <summary>
    /// Restore defaults for the optional rows: all off, the follow camera's values as shipped.
    /// </summary>
    internal void ResetOptional()
    {
        SetRotateItems(false);
        (menu.Config.FollowCameraOffset, menu.Config.FollowCameraHeadLock) = (false, false);
        (menu.Config.FollowCameraRaise, menu.Config.FollowCameraSide) = (FollowFocus.DefaultRaise, 0);
        (menu.Config.HideHats, menu.Config.HideHatsOfOthers) = (HideHats.Off, false);
    }

    /// <summary>
    /// The scene rows: splash screens, the sky flights and the texture keeping.
    /// </summary>
    void AddSceneRows(List<SettingsMenu.Entry> result)
    {
        result.Add(new("Splash screens",
            "Faithful shows the game's logos at startup (Faithful UI); a key or a click skips each one. Meitou shows the menu as soon as the game has "
            + "loaded.",
            Toggle: new("Splash screens", () => !menu.Config.SplashScreens, v => menu.Config.SplashScreens = !v, Remaster: true)));
        result.Add(new("Main menu scene transitions", "Sky flight to/from the main menu (Meitou main menu).",
            Toggle: new("Main menu scene transitions", () => menu.Config.MainMenuTransitions, v => menu.Config.MainMenuTransitions = v)));
        result.Add(new(_mainMenuTransitionSpeed.Label, "Higher values make the camera ascend and descend faster.",
            Slider: _mainMenuTransitionSpeed));
        result.Add(new("In-game scene transitions", "Sky flight for save loads/new games during play (Meitou UI).",
            Toggle: new("In-game scene transitions", () => menu.Config.InGameTransitions, v => menu.Config.InGameTransitions = v)));
        result.Add(new(_inGameTransitionSpeed.Label, "Higher values make the camera ascend and descend faster.", Slider: _inGameTransitionSpeed));
        result.Add(new("Character flights",
            "A double click on a squad member far from the view (or a double tap of their number) flies the camera there in an arc instead of "
            + "cutting.",
            Toggle: new("Character flights", () => menu.Config.CharacterFlights, v => menu.Config.CharacterFlights = v, Remaster: true, OnOff: true)));
        result.Add(new("Keep textures at characters",
            "Keeps the meshes and textures around every squad member loaded, not only around the camera, so a flight or jump to them lands on finished "
            + "detail. "
            + "Costs video memory.",
            Toggle: new("Keep textures at characters", () => menu.Config.KeepTexturesAtCharacters,
                v => menu.Config.KeepTexturesAtCharacters = v, Remaster: true, OnOff: true)));
        if (menu.Config.KeepTexturesAtCharacters)
        {
            result.Add(new("Keep textures at all squads",
                "Also keeps the surroundings of your other squads' members loaded, not only the squad on the squad bar. Costs more video memory.",
                Toggle: new("Keep textures at all squads", () => menu.Config.KeepTexturesAtAllSquads,
                    v => menu.Config.KeepTexturesAtAllSquads = v, OnOff: true)));
        }

        result.Add(new(_characterFlightTime.Label, "Seconds a character flight takes, and each hop of the squad tour (numpad /).",
            Slider: _characterFlightTime));
        foreach (var action in menu.Values.Actions)
        {
            result.Add(new(action.Label, "Delete cached billboards; they are rebuilt when needed.", Run: () =>
            {
                menu.PendingAction = action.Run;
                menu.ConfirmTitle = "Clear cached files?";
                menu.Confirmation = action.Question();
                menu.Focus = -1;
            }));
        }
    }
}