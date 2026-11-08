using Meitou.Data.Ui;
using Meitou.Game.Effects;
using Meitou.Game.Host;
using Meitou.Game.Host.Input;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Title;
using Meitou.Simulation.Session;
using AudioSettings = Meitou.Game.Audio.AudioSettings;

namespace Meitou.Game.Settings;

/// <summary>The Interface, Controls, Advanced, Audio, Gameplay and Fixes pages: their rows and the sliders made for them.</summary>
sealed class GamePages(SettingsMenu menu)
{
    readonly Slider mainMenuTransitionSpeed = new("Main menu transition speed", SkyArrival.MinSpeed, SkyArrival.MaxSpeed,
        () => menu.config.MainMenuTransitionSpeed, v => menu.config.MainMenuTransitionSpeed = v, "0.00'x'");
    readonly Slider inGameTransitionSpeed = new("In-game transition speed", SkyArrival.MinSpeed, SkyArrival.MaxSpeed,
        () => menu.config.InGameTransitionSpeed, v => menu.config.InGameTransitionSpeed = v, "0.00'x'");
    readonly Slider characterFlightTime = new("Character flight time", 0.5f, 6, () => menu.config.CharacterFlightSeconds, v => menu.config.CharacterFlightSeconds = v, "0.0' s'");
    Slider? cameraRotateSpeed;
    internal Slider CameraRotateSpeed => cameraRotateSpeed ??= new("Camera rotation speed", UserConfig.MinCameraRotateSpeed, UserConfig.MaxCameraRotateSpeed,
        () => menu.config.CameraRotateSpeed, v => { menu.config.CameraRotateSpeed = MathF.Round(v * 2) / 2; menu.CameraRotateSpeedChanged?.Invoke(menu.config.CameraRotateSpeed); },
        "0.#", Faithful: global::Meitou.Data.World.KenshiCamera.DefaultRotateSpeed);
    Slider? cameraMoveSpeed, cameraZoomSpeed;
    Slider CameraMoveSpeed => cameraMoveSpeed ??= CameraSpeedSlider("Camera move speed", UserConfig.MinCameraMoveSpeed, UserConfig.MaxCameraMoveSpeed,
        () => menu.config.CameraMoveSpeed ?? global::Meitou.Data.World.KenshiCamera.DefaultMoveSpeed, v => menu.config.CameraMoveSpeed = v, global::Meitou.Data.World.KenshiCamera.DefaultMoveSpeed);
    Slider CameraZoomSpeed => cameraZoomSpeed ??= CameraSpeedSlider("Camera zoom speed", UserConfig.MinCameraZoomSpeed, UserConfig.MaxCameraZoomSpeed,
        () => menu.config.CameraZoomSpeed ?? global::Meitou.Data.World.KenshiCamera.DefaultZoomSpeed, v => menu.config.CameraZoomSpeed = v, global::Meitou.Data.World.KenshiCamera.DefaultZoomSpeed);
    Slider CameraSpeedSlider(string label, float min, float max, Func<float> get, Action<float> set, float original) =>
        new(label, min, max, get, v => { set(MathF.Round(v)); menu.CameraChanged?.Invoke(); }, "0", Faithful: original);
    Slider[]? audioSliders;
    /// <summary>The audio sliders of the original's options (docs/formats/settings.md); each writes <see cref="UserConfig.Audio"/> and calls <see cref="SettingsMenu.AudioChanged"/>.</summary>
    Slider[] AudioSliders => audioSliders ??= [
        Volume("Music volume", a => a.Music, (a, v) => a with { Music = v }),
        Volume("Ambient volume", a => a.Ambient, (a, v) => a with { Ambient = v }),
        Volume("Footstep volume", a => a.Footstep, (a, v) => a with { Footstep = v }),
        Volume("Sfx volume", a => a.Sfx, (a, v) => a with { Sfx = v }),
        Volume("UI volume", a => a.Ui, (a, v) => a with { Ui = v }),
        Volume("VO volume", a => a.Vo, (a, v) => a with { Vo = v }),
        new("Music frequency", AudioSettings.MinFrequency, AudioSettings.MaxFrequency,
            () => (menu.config.Audio ??= new()).MusicFrequency,
            v => { menu.config.Audio = (menu.config.Audio ?? new()) with { MusicFrequency = MathF.Round(v) }; menu.AudioChanged?.Invoke(); },
            "0", Text: v => $"{v:0} min", Faithful: 5),
        // Meitou's voice cap (docs/audio.md "Voice cap"): the top of the track mixes every sound.
        new("Sound voices", AudioSettings.MinVoices, AudioSettings.MaxVoices,
            () => (menu.config.Audio ??= new()).Voices,
            v => { menu.config.Audio = (menu.config.Audio ?? new()) with { Voices = (int)MathF.Round(v / 8) * 8 }; menu.AudioChanged?.Invoke(); },
            "0", Text: v => v >= AudioSettings.MaxVoices ? "All" : $"{v:0}", Faithful: AudioSettings.MaxVoices, Meitou: AudioSettings.MeitouVoices),
        // Meitou's hearing range (docs/audio.md "Hearing range"): a factor on every distance sounds are heard from.
        new("Sound range", AudioSettings.MinRange, AudioSettings.MaxRange,
            () => (menu.config.Audio ??= new()).Range,
            v => { menu.config.Audio = (menu.config.Audio ?? new()) with { Range = Math.Clamp(MathF.Round(v * 4) / 4, AudioSettings.MinRange, AudioSettings.MaxRange) }; menu.AudioChanged?.Invoke(); },
            "0.##", Text: v => $"{v:0.##}x", Faithful: 1, Meitou: AudioSettings.MeitouRange),
    ];

    Slider[]? gameplaySliders;
    /// <summary>The four sliders of the original's Gameplay page (docs/formats/settings.md "Gameplay"); each writes <see cref="UserConfig.Gameplay"/> and calls <see cref="SettingsMenu.GameplayChanged"/>.</summary>
    Slider[] GameplaySliders => gameplaySliders ??= [
        Multiplier("Squad size multiplier", GameplaySettings.MinSquadSize, GameplaySettings.MaxSquadSize, g => g.SquadSize, (g, v) => g with { SquadSize = v }),
        Multiplier("Global population multiplier", GameplaySettings.MinPopulation, GameplaySettings.MaxPopulation, g => g.Population, (g, v) => g with { Population = v }),
        Multiplier("Town raid events: size", GameplaySettings.MinRaidSize, GameplaySettings.MaxRaidSize, g => g.RaidSize, (g, v) => g with { RaidSize = v }),
        Multiplier("Town raid events: frequency", GameplaySettings.MinRaidFrequency, GameplaySettings.MaxRaidFrequency, g => g.RaidFrequency, (g, v) => g with { RaidFrequency = v }),
    ];

    Slider Multiplier(string label, float min, float max, Func<GameplaySettings, float> get, Func<GameplaySettings, float, GameplaySettings> set) =>
        new(label, min, max, () => get(menu.config.Gameplay ??= new()), v => SetGameplay(set(menu.config.Gameplay ?? new(), Math.Clamp(MathF.Round(v * 20) / 20, min, max))),
            "0.##", Faithful: 1);

    Slider? autoSaveSlider;
    Slider AutoSaveSlider => autoSaveSlider ??= new("Auto save time (minutes)", GameplaySettings.MinAutoSaveMinutes, GameplaySettings.MaxAutoSaveMinutes,
        () => (menu.config.Gameplay ??= new()).AutoSaveMinutes, v => SetGameplay((menu.config.Gameplay ?? new()) with { AutoSaveMinutes = MathF.Round(v) }), "0", Text: v => $"{v:0} min", Faithful: 10);

    internal void SetGameplay(GameplaySettings value) { menu.config.Gameplay = value; menu.GameplayChanged?.Invoke(); }

    Slider Volume(string label, Func<AudioSettings, float> get, Func<AudioSettings, float, AudioSettings> set) =>
        new(label, 0, 1, () => get(menu.config.Audio ??= new()), v => { menu.config.Audio = set(menu.config.Audio ?? new(), Math.Clamp(v, 0, 1)); menu.AudioChanged?.Invoke(); },
            "0.00", Text: v => $"{v * 100:0}%", Faithful: 0.5f, Scale: 100);
    Slider? attackSlotsSlider;
    internal Slider AttackSlotsSlider => attackSlotsSlider ??= new("Attackers per target", 1, 5, () => menu.config.AttackSlots, v =>
    {
        menu.config.AttackSlots = Math.Clamp((int)MathF.Round(v), 1, 5);
        menu.AttackSlotsChanged?.Invoke(menu.config.AttackSlotsOption);
    }, "0", Faithful: UserConfig.FaithfulAttackSlots, Meitou: UserConfig.MeitouAttackSlots);
    Slider? harpoonLimitSlider;
    Slider HarpoonLimitSlider => harpoonLimitSlider ??= new("Harpoon limit", global::Meitou.Simulation.Combat.RangedTuning.MinStuckLimit, global::Meitou.Simulation.Combat.RangedTuning.MaxStuckLimit, () => (menu.config.Gameplay ??= new()).HarpoonLimit,
        v => SetGameplay((menu.config.Gameplay ?? new()) with { HarpoonLimit = Math.Clamp((int)MathF.Round(v), global::Meitou.Simulation.Combat.RangedTuning.MinStuckLimit, global::Meitou.Simulation.Combat.RangedTuning.MaxStuckLimit) }), "0", Logarithmic: true,
        Faithful: global::Meitou.Simulation.Combat.RangedTuning.DefaultStuckLimit);
    Slider? fastSpeedSlider, fastestSpeedSlider;
    Slider FastSpeedSlider => fastSpeedSlider ??= TimeSpeed("Fast speed", SimulationClock.MinFastSpeed, SimulationClock.MaxFastSpeed, 1, () => menu.config.FastSpeed, v => menu.config.FastSpeed = v);
    Slider FastestSpeedSlider => fastestSpeedSlider ??= TimeSpeed("Fastest speed", SimulationClock.MinFastestSpeed, SimulationClock.MaxFastestSpeed, 2,
        () => menu.config.FastestSpeed, v => menu.config.FastestSpeed = v);

    /// <summary>A speed button's game speed, in half steps, applied to the running clock at once.</summary>
    Slider TimeSpeed(string label, double min, double max, int step, Func<double> get, Action<double> set) =>
        new(label, (float)min, (float)max, () => (float)get(), v =>
        {
            set(Math.Clamp(Math.Round(v * 2) / 2, min, max));
            menu.TimeSpeedsChanged?.Invoke();
        }, "0.#", Text: v => $"{v:0.#}x", Faithful: (float)SimulationClock.DefaultSpeeds[step]);
    Slider? groundItemsSlider;
    Slider GroundItemsSlider => groundItemsSlider ??= new("Dropped items kept", 16, global::Meitou.Simulation.Items.GroundItems.MaxLimit, () => menu.config.GroundItemLimit,
        v => menu.config.GroundItemLimit = Math.Clamp((int)MathF.Round(v), 1, global::Meitou.Simulation.Items.GroundItems.MaxLimit), "0", Logarithmic: true,
        Meitou: global::Meitou.Simulation.Items.GroundItems.DefaultLimit);

    /// <summary>The Interface page: UI style, units, floaters, font size, names, markers and the mouse.</summary>
    internal void InterfaceRows(List<SettingsMenu.Entry> result)
    {
        result.Add(new("UI style", "Choose original Kenshi skins or Meitou materials.", Choice: new(() => ["Faithful", "Meitou"],
            () => (int)menu.player.UiMode, i => menu.player.SetUiMode((UiMode)i))));
        result.Add(new("Speed units", "The unit of the run speed in the character's information panel (the game: mph).", Choice: new(() => ["mph", "km/h"],
            () => (int)menu.config.SpeedUnit, i => menu.player.SpeedUnit = menu.config.SpeedUnit = (SpeedUnit)i)));
        result.Add(new("Damage floaters", "Numbers that float over a character when a blow lands, with the damage armour stopped in brackets. Simple shows the total, Full one number per damage type "
            + "and the part hit.", Choice: new(() => ["Off", "Simple info", "Full info"],
            () => (int)menu.config.Floaters, i => menu.player.Floaters = menu.config.Floaters = (FloaterMode)i)));
        result.Add(new("Font size", "Points added to every interface font's default height (the game: 3).",
            Slider: new("Font size", UserConfig.MinFontSize, UserConfig.MaxFontSize, () => menu.config.FontSize ?? global::Meitou.Data.Ui.KenshiFontSize.DefaultSetting,
                v => { float size = Math.Clamp(MathF.Round(v), UserConfig.MinFontSize, UserConfig.MaxFontSize); menu.config.FontSize = size; menu.player.FontSize = size; }, "0", Faithful: global::Meitou.Data.Ui.KenshiFontSize.DefaultSetting)));
        result.Add(new("Show names", "The name over each of your characters (the game's Show names option). Off, only the stealth and light icons stay.",
            Toggle: new("Show names", () => menu.config.ShowNames, v => menu.player.ShowNames = menu.config.ShowNames = v, OnOff: true)));
        result.Add(new("Show movement marker", "The green marker where a move order sends your characters (the game: on).",
            Toggle: new("Show movement marker", () => menu.config.MovementMarker != false, v => menu.player.MovementMarker = (menu.config.MovementMarker = v) == true, OnOff: true)));
        result.Add(new("Show rotation marker", "The green marker on the camera's pivot while you turn it (the game: on).",
            Toggle: new("Show rotation marker", () => menu.config.RotationMarker != false, v => menu.player.RotationMarker = (menu.config.RotationMarker = v) == true, OnOff: true)));
        result.Add(new("Censorship", "Swaps a few strong words in the dialogue for milder ones (the game's censorship option).",
            Toggle: new("Censorship", () => menu.config.Censorship, v => menu.config.Censorship = v, OnOff: true)));
        result.Add(new("Feedback button", "The FEEDBACK button at the top right during play. Off, it still shows on the title screen and in the Esc menu.",
            Toggle: new("Feedback button", () => menu.config.FeedbackButton, v => menu.player.FeedbackButtonInPlay = menu.config.FeedbackButton = v,
                OnOff: true)));
        result.Add(new("Hardware mouse", "Use the Windows cursor instead of the game's drawn pointer (no hover pointers unless Enhanced hardware mouse is on).",
            Toggle: new("Hardware mouse", () => menu.config.HardwareMouse, v => menu.config.HardwareMouse = v)));
        if (menu.config.HardwareMouse)
            result.Add(new("Enhanced hardware mouse", "The icon of what a right click would do beside the Windows cursor (none for a walk), and the hand over buttons and targets.",
                Toggle: new("Enhanced hardware mouse", () => menu.config.EnhancedHardwareMouse, v => menu.config.EnhancedHardwareMouse = v, OnOff: true),
                Depth: 1));
    }

    /// <summary>The Controls page: the camera's speeds and mouse, then one row per bindable action.</summary>
    internal void ControlsRows(List<SettingsMenu.Entry> result)
    {
        result.Add(new(CameraRotateSpeed.Label, "How fast the rotate and tilt keys and the mouse turn the camera (the game: 6).", Slider: CameraRotateSpeed));
        result.Add(new(CameraMoveSpeed.Label, "How fast the arrow keys and the screen edge pan the camera (the game: 500; faster the higher the camera).", Slider: CameraMoveSpeed));
        result.Add(new(CameraZoomSpeed.Label, "How fast the wheel and the zoom keys move the camera in and out (the game: 125).", Slider: CameraZoomSpeed));
        result.Add(new("Invert mouse X", "Reverses the camera's turn, for the mouse and the keys (the game: off).",
            Toggle: new("Invert mouse X", () => menu.config.InvertX == true, v => { menu.config.InvertX = v; menu.CameraChanged?.Invoke(); },
                OnOff: true)));
        result.Add(new("Invert mouse Y", "Reverses the camera's tilt, for the mouse and the keys (the game: on).",
            Toggle: new("Invert mouse Y", () => menu.config.InvertY != false, v => { menu.config.InvertY = v; menu.CameraChanged?.Invoke(); },
                OnOff: true)));
        result.Add(new("Swap mouse buttons", "Swap the left and right mouse buttons (the game: off; the game view follows, as in the original).",
            Toggle: new("Swap mouse buttons", () => menu.config.SwapMouseButtons == true, v => KeyMap.SwapButtons = (menu.config.SwapMouseButtons = v) == true, OnOff: true)));
        result.Add(new("Edge scrolling", "The camera moves when the pointer is at the edge of the screen.",
            Toggle: new("Edge scrolling", () => menu.config.EdgeScroll != false, v => menu.config.EdgeScroll = v)));
        foreach (var action in Enum.GetValues<InputAction>().Where(a => a != InputAction.Quit && a != InputAction.ToggleFps))
            result.Add(new(SettingsMenu.ActionLabel(action), SettingHints.BindingHint(action), Binding: action));
    }

    /// <summary>The Audio page: the volumes, music frequency, voices and range, and where the listener is.</summary>
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
            "The most sounds heard at once: the loudest play, quieter ones wait silently and come back when they are loud enough. Lower it if big fights stutter; All mixes every sound.",
            "How far away sounds are heard: 1x is the original, 4x hears fights, footsteps and machines four times as far. Higher values mix more sounds at once.",
        ];
        for (int i = 0; i < AudioSliders.Length; i++)
            result.Add(new(AudioSliders[i].Label, hints[i], Slider: AudioSliders[i]));
        result.Add(new("Hear from", "Camera: you hear the world where the camera is. Character: you hear it where your first selected character stands (from the camera while nobody is selected).",
            Toggle: new("Hear from", () => menu.config.HearFromSelected, v => menu.config.HearFromSelected = v, Text: () => menu.config.HearFromSelected ? "Character" : "Camera")));
    }

    /// <summary>The Gameplay page: the original's multipliers and options, autosave, zone hopping and tutorials.</summary>
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
        result.Add(new("Town attacks frequency", "How long a faction waits between attacks on your towns by squads passing by (the game: Normal). Never stops them.",
            Choice: new(() => GameplaySettings.TownAttackLabels, () => (menu.config.Gameplay ??= new()).TownAttacks,
                i => SetGameplay((menu.config.Gameplay ?? new()) with { TownAttacks = i }))));
        result.Add(new("Dismemberment", "When a destroyed limb is cut off: Rare only by a single heavy blow, Frequent also when a wound worsens (the game: Rare).",
            Choice: new(() => GameplaySettings.DismembermentLabels, () => (int)(menu.config.Gameplay ??= new()).Dismemberment,
                i => SetGameplay((menu.config.Gameplay ?? new()) with { Dismemberment = (global::Meitou.Simulation.Bodies.Dismemberment)i }))));
        result.Add(new("Auto save", "Saves the game on its own every few minutes of unpaused play, into three slots in turn (autosave0 to autosave2).",
            Toggle: new("Auto save", () => (menu.config.Gameplay ??= new()).AutoSave, v => SetGameplay((menu.config.Gameplay ?? new()) with { AutoSave = v }), OnOff: true)));
        result.Add(new(AutoSaveSlider.Label, "Minutes of unpaused play between autosaves (the game: 10).", Slider: AutoSaveSlider));
        result.Add(new("Fast zone hopping", "Keeps a larger area loaded round each of your characters, so switching between distant ones skips most of the loading (the game: off; uses more memory).",
            Toggle: new("Fast zone hopping", () => (menu.config.Gameplay ??= new()).FastZoneHopping, v => SetGameplay((menu.config.Gameplay ?? new()) with { FastZoneHopping = v }), OnOff: true)));
        result.Add(new("Tutorials", "Shows the tutorial hints as you play (the game: on).",
            Toggle: new("Tutorials", () => menu.config.Tutorials != false, v => { menu.config.Tutorials = v; menu.TutorialsChanged?.Invoke(v); },
                OnOff: true)));
        result.Add(new("Reset Tutorials", "Every tutorial can show again, the ones already seen included.", Run: () => menu.ResetTutorials?.Invoke()));
    }

    /// <summary>The Fixes page: the game's bugs, each fixed or as it ships, under one switch for all.</summary>
    internal void FixesRows(List<SettingsMenu.Entry> result)
    {
        // Bugs of the game as it ships; on fixes them, off plays as the game does.
        result.Add(new("Raids as written", "Raid and campaign data that builds on a base campaign counts the base's squads and follow-up campaigns once. "
            + "The game counts them twice, so inherited squads come up more often and follow-up raids are about twice as likely.",
            Toggle: new("Raids as written", () => menu.config.CampaignAncestorsOnce, v => { menu.config.CampaignAncestorsOnce = v; menu.OffscreenChanged?.Invoke(); }, Remaster: true, OnOff: true)));
        result.Add(new("Door break sound", "Doors and gates make their breaking sound when they are bashed or broken open. "
            + "The game never picks which breaking sound to play, so it plays none.",
            Toggle: new("Door break sound", () => menu.config.FixDoorBreak, v => menu.config.FixDoorBreak = v, Remaster: true, OnOff: true)));
        result.Add(new("Ambience after loading", "A biome whose sounds were not loaded yet starts its day or night ambience by the time of day. "
            + "The game plays the opposite one (night sounds by day) until the next biome or day change.",
            Toggle: new("Ambience after loading", () => menu.config.FixAmbienceSuffix, v => menu.config.FixAmbienceSuffix = v,
                Remaster: true, OnOff: true)));
        result.Add(new("Wind stops", "The wind's sound ends when the wind dies down. The game asks for a stop sound that does not exist, so once the wind has blown it never goes quiet.",
            Toggle: new("Wind stops", () => menu.config.FixWindStop, v => menu.config.FixWindStop = v, Remaster: true, OnOff: true)));
        result.Add(new("Load button", "The load window's Load button works only once a save that can be read is selected. "
            + "The game keeps it live: with nothing selected a click does nothing, and an unreadable save fails to load.",
            Toggle: new("Load button", () => menu.config.FixLoadButton, v => menu.player.frontend.FixLoadButton = menu.config.FixLoadButton = v, Remaster: true, OnOff: true)));
        result.Add(new("Turret gunners turn", "A turret's gunner turns with the gun, facing where it aims. "
            + "In the game the gunner keeps facing the turret, so once the gun swings toward a target the gunner looks off to the side.",
            Toggle: new("Turret gunners turn", () => menu.config.FixGunnerFacing, v => { menu.config.FixGunnerFacing = v; menu.GunnerFacingChanged?.Invoke(v); }, Remaster: true, OnOff: true)));
        result.Add(new("Weak punch reach", "Fighters with martial arts of 20 or less throw their weak punch from a normal punching distance. "
            + "The game's data gives it a negative reach, so they throw it only when nearly touching their target.",
            Toggle: new("Weak punch reach", () => menu.config.FixPunchReach, v => { menu.config.FixPunchReach = v; menu.PunchReachChanged?.Invoke(v); }, Remaster: true, OnOff: true)));
        result.Add(new("Bench facing", "A character on a bench with the table behind it sits facing away from the table. "
            + "The game mirrors the bench's look point through the middle of the world map, so the sitter faces an unrelated direction.",
            Toggle: new("Bench facing", () => menu.config.FixBenchFacing, v => { menu.config.FixBenchFacing = v; menu.BenchFacingChanged?.Invoke(v); }, Remaster: true, OnOff: true)));
        result.Add(new("Walls inside buildings", "Seen into a building your characters are in, parts of other buildings that run through it are left out, such as the Hub's town wall in the Shinobi Thieves tower. "
            + "The game's world places them there and draws them inside the room.",
            Toggle: new("Walls inside buildings", () => menu.config.FixPartsInBuildings, v => menu.config.FixPartsInBuildings = v,
                Remaster: true, OnOff: true)));
        result.Insert(0, menu.PageSidesEntry(result, "All fixes", "Every bug on this page as the game ships it, or every fix.", "fix"));
    }

    /// <summary>The Advanced page: Meitou's gameplay changes and conveniences, under one switch for all, and the cache buttons.</summary>
    internal void AdvancedRows(List<SettingsMenu.Entry> result)
    {
        // A graphics switch that changes play: the stealth sight counts the lamps it lights, so no look preset or Faithful sets it.
        if (menu.values.Toggles.FirstOrDefault(t => t.Label == "Interior lamps") is { } lamps) result.Add(new("Interior lamps", SettingHints.ToggleHint(lamps.Label), Toggle: lamps));
        result.Add(new(AttackSlotsSlider.Label, "How many enemies may swing at one character at once (the game: 1, plus its race's extra slots); the others wait at reach or circle.", Slider: AttackSlotsSlider));
        result.Add(new(HarpoonLimitSlider.Label, "How many bolts and harpoons stay stuck in the ground and buildings, per kind of bolt; past it the one that flew longest disappears (the game: 60).", Slider: HarpoonLimitSlider));
        result.Add(new("Late dodges and blocks", "Balance change: a dodge or block that succeeds its roll but starts a moment late still works, "
            + "instead of failing as in the game. Makes skilled fighters much harder to wear down when several enemies attack at once.",
            Toggle: new("Late dodges and blocks", () => menu.config.LateDefences, v => { menu.config.LateDefences = v; menu.LateDefencesChanged?.Invoke(v); }, Remaster: true, OnOff: true)));
        result.Add(new("Squads keep their people", "Roaming squads keep their characters when they leave the area around your squads, and travel on as themselves. "
            + "In the game they are removed and new ones are made later.",
            Toggle: new("Squads keep their people", () => menu.config.KeepRoamers, v => { menu.config.KeepRoamers = v; menu.OffscreenChanged?.Invoke(); }, Remaster: true, OnOff: true)));
        result.Add(new("Fights far away", "Hostile squads that meet far from your squads fight it out by strength; the loser is wiped out. In the game nothing happens far away.",
            Toggle: new("Fights far away", () => menu.config.OffscreenFights, v => { menu.config.OffscreenFights = v; menu.OffscreenChanged?.Invoke(); }, Remaster: true, OnOff: true)));
        result.Add(new("Healing far away", "A squad coming back into the area around your squads has healed for the time it was away. In the game it comes back as it left.",
            Toggle: new("Healing far away", () => menu.config.OffscreenHealing, v => { menu.config.OffscreenHealing = v; menu.OffscreenChanged?.Invoke(); }, Remaster: true, OnOff: true)));
        result.Add(new("Wild squads stay", "Animals, bandits and other wild squads keep their characters, wounds and places when the area around them unloads, and are there again when you come back. "
            + "In the game they are removed and new ones are made.",
            Toggle: new("Wild squads stay", () => menu.config.KeepWild, v => { menu.config.KeepWild = v; menu.OffscreenChanged?.Invoke(); }, Remaster: true, OnOff: true)));
        result.Add(new("Towns keep their schedule", "Townspeople you come back to after an hour or more are where their day puts them: in bed at night, at the counter, at their posts and seats. "
            + "In the game they are where they stood when you left.",
            Toggle: new("Towns keep their schedule", () => menu.config.ResidentSchedule, v => { menu.config.ResidentSchedule = v; menu.OffscreenChanged?.Invoke(); }, Remaster: true, OnOff: true)));
        result.Add(new("Production far away", "Farms, mines, wells and generators away from your squads keep working. In the game they stop while their area is unloaded, and only farms catch up when you come back.",
            Toggle: new("Production far away", () => menu.config.UnloadedProduction, v => { menu.config.UnloadedProduction = v; menu.OffscreenChanged?.Invoke(); }, Remaster: true, OnOff: true)));
        result.Add(new("Doors close behind", "A door someone opened by walking through it closes again a few seconds after the last person went through. In the game doors stay open.",
            Toggle: new("Doors close behind", () => menu.config.DoorsCloseBehind, v => { menu.config.DoorsCloseBehind = v; menu.DoorsCloseChanged?.Invoke(v); }, Remaster: true, OnOff: true)));
        result.Add(new("Decapitation", "A blow that destroys a character's head cuts it off, by the same rule the Dismemberment setting cuts off limbs. In the game a destroyed head only kills.",
            Toggle: new("Decapitation", () => menu.config.Decapitation, v => { menu.config.Decapitation = v; menu.GameplayChanged?.Invoke(); }, Remaster: true, OnOff: true)));
        result.Add(new("Advanced ragdolls", "A character knocked down or killed by a blow falls with its own momentum and is pushed away from the hit, harder the heavier the blow and most at the part that was struck, its arms and legs flung by it, and a cut-off head flies the same way. Limp bodies have joints that ease to a stop and a slight muscle tone, so they settle into natural poses. In the game a body drops where it stood, its joints stopping hard.",
            Toggle: new("Advanced ragdolls", () => menu.config.AdvancedRagdolls, v => { menu.config.AdvancedRagdolls = v; menu.GameplayChanged?.Invoke(); }, Remaster: true)));
        result.Add(new(FastSpeedSlider.Label, "The game speed of the second speed button (two arrows) and its key (the game: 2x).", Slider: FastSpeedSlider));
        result.Add(new(FastestSpeedSlider.Label, "The game speed of the third speed button (three arrows) and its key (the game: 5x). "
            + "High speeds need a fast processor; when it cannot keep up the game runs slower than chosen.", Slider: FastestSpeedSlider));
        result.Add(new("Pause after loading", "A save starts paused once it has loaded, so nothing happens before you look around. In the game it runs at once.",
            Toggle: new("Pause after loading", () => menu.config.PauseOnLoad, v => menu.config.PauseOnLoad = v, OnOff: true)));
        result.Add(new("Animals pick up items", "Your pack animals pick up an item you point them at, into the bag they wear (or wear a bag they pick up). Off, only people pick items up.",
            Toggle: new("Animals pick up items", () => menu.config.AnimalsPickUp, v => { menu.config.AnimalsPickUp = v; menu.AnimalsPickUpChanged?.Invoke(v); }, OnOff: true)));
        result.Add(new("Hover outline", "Outline and tint the character under the cursor, in the colour of what a right click would do.",
            Toggle: new("Hover outline", () => menu.config.HoverOutline, v => menu.config.HoverOutline = v, Remaster: true, OnOff: true)));
        result.Add(new("Building outline", "Outline and tint the selected building, and the building under the cursor in the colour of what a right click would do, like a character under the cursor, "
            + "except while its interior is shown.",
            Toggle: new("Building outline", () => menu.config.BuildingOutline, v => menu.config.BuildingOutline = v, Remaster: true, OnOff: true)));
        result.Add(new("Click away deselects", "A left click on the ground ends the selection of the selected building and closes its panels. In the game the building stays selected.",
            Toggle: new("Click away deselects", () => menu.config.DeselectBuildings, v => menu.player.buildingSelection.DeselectBuildings = menu.config.DeselectBuildings = v, Remaster: true, OnOff: true)));
        result.Add(new("luckyren Mode", "A character that takes a fatal hit explodes, covers everything around in blood and sends bones flying everywhere. Its gear falls to the ground around the spot.",
            Toggle: new("luckyren Mode", () => menu.config.LuckyrenMode, v => menu.config.LuckyrenMode = v),
            Picture: SettingsMenuArt.LuckyrenImage));
        result.Add(new(GroundItemsSlider.Label, "How many items dropped during play (severed limbs, luckyren Mode's gear) stay on the ground; past it the oldest disappears. "
            + "Items that were on the ground in the save always stay.", Slider: GroundItemsSlider));
        result.Add(new("Splash screens", "Faithful shows the game's logos at startup (Faithful UI); a key or a click skips each one. Meitou shows the menu as soon as the game has loaded.",
            Toggle: new("Splash screens", () => !menu.config.SplashScreens, v => menu.config.SplashScreens = !v, Remaster: true)));
        result.Add(new("Main menu scene transitions", "Sky flight to/from the main menu (Meitou UI).",
            Toggle: new("Main menu scene transitions", () => menu.config.MainMenuTransitions, v => menu.config.MainMenuTransitions = v)));
        result.Add(new(mainMenuTransitionSpeed.Label, "Higher values make the camera ascend and descend faster.", Slider: mainMenuTransitionSpeed));
        result.Add(new("In-game scene transitions", "Sky flight for save loads/new games during play (Meitou UI).",
            Toggle: new("In-game scene transitions", () => menu.config.InGameTransitions, v => menu.config.InGameTransitions = v)));
        result.Add(new(inGameTransitionSpeed.Label, "Higher values make the camera ascend and descend faster.", Slider: inGameTransitionSpeed));
        result.Add(new("Character flights", "A double click on a squad member far from the view (or a double tap of their number) flies the camera there in an arc instead of cutting.",
            Toggle: new("Character flights", () => menu.config.CharacterFlights, v => menu.config.CharacterFlights = v, Remaster: true, OnOff: true)));
        result.Add(new("Keep textures at characters", "Keeps the meshes and textures around every squad member loaded, not only around the camera, so a flight or jump to them lands on finished detail. Costs video memory.",
            Toggle: new("Keep textures at characters", () => menu.config.KeepTexturesAtCharacters, v => menu.config.KeepTexturesAtCharacters = v, Remaster: true, OnOff: true)));
        if (menu.config.KeepTexturesAtCharacters)
            result.Add(new("Keep textures at all squads", "Also keeps the surroundings of your other squads' members loaded, not only the squad on the squad bar. Costs more video memory.",
                Toggle: new("Keep textures at all squads", () => menu.config.KeepTexturesAtAllSquads, v => menu.config.KeepTexturesAtAllSquads = v,
                    OnOff: true)));
        result.Add(new(characterFlightTime.Label, "Seconds a character flight takes, and each hop of the squad tour (numpad /).", Slider: characterFlightTime));
        foreach (var action in menu.values.Actions)
            result.Add(new(action.Label, "Delete cached billboards; they are rebuilt when needed.", Run: () =>
            {
                menu.pendingAction = action.Run; menu.confirmTitle = "Clear cached files?"; menu.confirmation = action.Question(); menu.focus = -1;
            }));
        result.Insert(0, menu.PageSidesEntry(result, "All gameplay changes", "Every change on this page as the game plays, or every Meitou change.", "gameplay change"));
    }
}
