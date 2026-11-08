using System.Diagnostics;
using Meitou.Data.Save;
using Meitou.Data.Ui;
using Meitou.Data.World;

namespace Meitou.Game.Ui;

/// <summary>
/// The biome banner (docs/game/ui-screens.md "Biome banner", <c>MainBarGUI::vfunc_10</c>): the HUD's <c>BiomeTextPanel</c> fades in with the name of the BIOME_GROUP under the camera pivot when it
/// changes, holds 5 s and fades out, at most once in 30 s, with the <c>New_Biome</c> sound. Real time, like the original's frame clock; it only follows the pivot and changes nothing in the game.
/// </summary>
sealed class BiomeBanner(PlayerInterface player)
{
    /// <summary>The seconds the banner holds (it fades in at 2 per second before, out at 1 per second after) and the least time between two banners (<b>Verified</b>).</summary>
    const float BiomeHoldSeconds = 5, BiomeCooldownSeconds = 30;

    /// <summary>The host's BIOME_GROUP at a world position: its string id and name; null when there is no weather or areas map.</summary>
    public Func<float, float, (string Id, string Name)?>? RegionAt { get; set; }

    object? _biomeWorld;
    string? _biomeGroup;
    string _biomeName = "";
    float _biomeHold, _biomeCooldown, _biomeAlpha;
    internal bool BiomeShown;
    long _biomeAt;

    /// <summary>Once a frame: the banner's timers, its fade and the group it tracks. A new world only takes the group under the pivot (the original does so while its loading window is up).</summary>
    internal void PollBiome()
    {
        long now = Stopwatch.GetTimestamp();
        float dt = _biomeAt == 0 ? 0 : MathF.Min((float)Stopwatch.GetElapsedTime(_biomeAt, now).TotalSeconds, 0.25f);
        _biomeAt = now;
        if (player.TitleOpen || player.CharacterEditor.EditorOpen || RegionAt is null) return;
        var pivot = player.Session.Camera.Current.Target;
        if (RegionAt(pivot.X, pivot.Z) is not { } here) return;
        if (!ReferenceEquals(_biomeWorld, player.Session.World))
        {
            _biomeWorld = player.Session.World;
            (_biomeGroup, BiomeShown, _biomeHold, _biomeCooldown, _pendingBiomeCooldown) = (here.Id, false, 0, _pendingBiomeCooldown, 0);
            return;
        }
        _biomeHold -= dt;
        _biomeCooldown -= dt;
        bool blocked = player.TownPanel.ShownTown != 0;   // the town panel is showing a town (MainBarGUI +0x128, FUN_140728d50:358): no banner starts and the group does not follow the pivot
        if (here.Id != _biomeGroup && _biomeCooldown <= 0 && !blocked && here.Name != "NONE" && here.Id != WeatherData.NoneRegionId)
        {
            (_biomeName, BiomeShown, _biomeAlpha, _biomeHold, _biomeCooldown) = (here.Name, true, 0, BiomeHoldSeconds, BiomeCooldownSeconds);
            player.InterfaceSounds.Sound("Notifications", "Notifications", "New_Biome");
        }
        else if (BiomeShown)
        {
            if (_biomeHold > 0) _biomeAlpha = MathF.Min(1, _biomeAlpha + dt * 2);
            else if ((_biomeAlpha -= dt) < 0)
            {
                BiomeShown = false;
                // On hiding the game tests the group's `arrival dialog`: squad banter (PlayerInterface::triggerAreaArrivalDialogue).
                // It reads the group under the pivot this frame (FUN_1408015d0 gets the current holder), not the stored one.
                TryArrival(here.Id);
            }
        }
        if (!blocked) _biomeGroup = here.Id;
        RetryArrival(here.Id);
    }

    /// <summary>The group whose arrival banter is still being tried (<c>DAT_142132c70</c>), the try's serial and when it was last made (wall clock).</summary>
    string? _arrivalGroup;
    int _arrivalSerial;
    long _arrivalAt;

    /// <summary>Between banter tries: the real time between them (<c>DAT_14167fad4</c>).</summary>
    const double ArrivalRetrySeconds = 10;

    /// <summary>Stores the group with the time and makes the first try (<c>FUN_1408015d0</c>).</summary>
    void TryArrival(string group)
    {
        _arrivalGroup = group;
        _arrivalAt = Stopwatch.GetTimestamp();
        player.Say(new Simulation.Dialogue.AreaArrival(group, ++_arrivalSerial) { Tick = player.Session.World.Tick });
    }

    /// <summary>
    /// Every frame (<c>FUN_140801540</c>): the stored group is dropped when the group under the pivot is another, or when the last try's result says to forget it; else after more than 10 s
    /// of real time since the last try the banter is tried again.
    /// </summary>
    void RetryArrival(string here)
    {
        if (_arrivalGroup is not { } group) return;
        bool? forget = player.Session.World.ArrivalForget(_arrivalSerial);
        if (here != group || forget == true) _arrivalGroup = null;
        else if (forget == false && Stopwatch.GetElapsedTime(_arrivalAt).TotalSeconds > ArrivalRetrySeconds) TryArrival(group);
    }

    /// <summary>The panel over the HUD while the banner shows: the layout's own panel at the fade's alpha with the name in its text box; hidden otherwise.</summary>
    internal void BindBiome(UiBindings b)
    {
        if (!BiomeShown) { b.Hidden.Add("BiomeTextPanel"); return; }
        b.Properties["BiomeTextPanel"] = new Dictionary<string, string> { ["Visible"] = "true", ["Alpha"] = Math.Clamp(_biomeAlpha, 0, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        b.Text["BiomeTextBox"] = _biomeName;
        // The layout lets the mouse through only the panel (NeedMouse is not inherited): its two lines would otherwise take clicks.
        foreach (string line in new[] { "BiomeNameTopLine", "BiomeNameBottomLine" }) b.Properties[line] = new Dictionary<string, string> { ["NeedMouse"] = "false" };
    }

    /// <summary>
    /// Into a save's CAMERA record (<c>FUN_140728af0</c>, <c>FUN_140375360</c>): the cooldown (the original writes its raw, ever falling value; here never below 0), the group tracked and the name of the
    /// group under the pivot, empty for NONE.
    /// </summary>
    public void WriteBiome(SaveCamera camera)
    {
        camera.BiomeTextTimer = MathF.Max(_biomeCooldown, 0);
        camera.BiomeTextGroup = _biomeGroup ?? "";
        var pivot = player.Session.Camera.Current.Target;
        camera.Area = RegionAt?.Invoke(pivot.X, pivot.Z) is { } here && here.Name != "NONE" ? here.Name : "";   // the name test only (FUN_140375360); the banner also tests the id
    }

    /// <summary>A loaded save's cooldown (<c>FUN_140728c10</c>); the group is the one under the pivot, as the original's loading window sets it.</summary>
    public void ReadBiome(SaveCamera camera)
    {
        _biomeWorld = null;
        _pendingBiomeCooldown = camera.BiomeTextTimer;
    }

    float _pendingBiomeCooldown;
}