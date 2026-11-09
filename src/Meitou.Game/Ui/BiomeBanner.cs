using System.Diagnostics;
using Meitou.Data.Save;
using Meitou.Data.Ui;
using Meitou.Data.World;

namespace Meitou.Game.Ui;

/// <summary>
/// The biome banner (docs/game/ui-screens.md "Biome banner", <c>MainBarGUI::vfunc_10</c>): the HUD's <c>BiomeTextPanel</c> fades in with the name of
/// the BIOME_GROUP under the camera pivot when it changes, holds 5 s and fades out, at most once in 30 s, with the <c>New_Biome</c> sound. Real time,
/// like the original's frame clock; it only follows the pivot and changes nothing in the game.
/// </summary>
sealed class BiomeBanner(PlayerInterface player)
{
    #region Banner

    /// <summary>
    /// The seconds the banner holds (it fades in at 2 per second before, out at 1 per second after) and the least time between two banners
    /// (<b>Verified</b>).
    /// </summary>
    const float BiomeHoldSeconds = 5, BiomeCooldownSeconds = 30;

    /// <summary>
    /// The host's BIOME_GROUP at a world position: its string id and name; null when there is no weather or areas map.
    /// </summary>
    public Func<float, float, (string Id, string Name)?>? RegionAt { get; set; }

    /// <summary>
    /// The world the banner follows; another one only takes the group under the pivot.
    /// </summary>
    object? _biomeWorld;

    /// <summary>
    /// The string id of the BIOME_GROUP tracked; null before the first.
    /// </summary>
    string? _biomeGroup;

    /// <summary>
    /// The name the banner shows.
    /// </summary>
    string _biomeName = "";

    /// <summary>
    /// The seconds the banner still holds, the seconds until another may start, and its alpha.
    /// </summary>
    float _biomeHold, _biomeCooldown, _biomeAlpha;

    /// <summary>
    /// Whether the banner shows.
    /// </summary>
    internal bool BiomeShown;

    /// <summary>
    /// The timestamp of the last poll; 0 before the first.
    /// </summary>
    long _biomeAt;

    /// <summary>
    /// A loaded save's cooldown, taken when the new world is first polled.
    /// </summary>
    float _pendingBiomeCooldown;

    /// <summary>
    /// The squad's banter on arriving in the group whose banner hid.
    /// </summary>
    readonly ArrivalBanter _arrival = new(player);

    /// <summary>
    /// Once a frame: the banner's timers, its fade and the group it tracks. A new world only takes the group under the pivot (the original does so
    /// while its loading window is up).
    /// </summary>
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

        // the town panel is showing a town (MainBarGUI +0x128, FUN_140728d50:358): no banner starts and the group does not follow the pivot
        bool blocked = player.TownPanel.ShownTown != 0;
        if (here.Id != _biomeGroup && _biomeCooldown <= 0 && !blocked
            && here.Name != "NONE" && here.Id != WeatherData.NoneRegionId)
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
                _arrival.TryArrival(here.Id);
            }
        }

        if (!blocked) _biomeGroup = here.Id;
        _arrival.RetryArrival(here.Id);
    }

    /// <summary>
    /// The panel over the HUD while the banner shows: the layout's own panel at the fade's alpha with the name in its text box; hidden otherwise.
    /// </summary>
    internal void BindBiome(UiBindings b)
    {
        if (!BiomeShown)
        {
            b.Hidden.Add("BiomeTextPanel");
            return;
        }

        string alpha = Math.Clamp(_biomeAlpha, 0, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        b.Properties["BiomeTextPanel"] = new Dictionary<string, string> { ["Visible"] = "true", ["Alpha"] = alpha };
        b.Text["BiomeTextBox"] = _biomeName;

        // The layout lets the mouse through only the panel (NeedMouse is not inherited): its two lines would otherwise take clicks.
        foreach (string line in new[] { "BiomeNameTopLine", "BiomeNameBottomLine" })
            b.Properties[line] = new Dictionary<string, string> { ["NeedMouse"] = "false" };
    }

    #endregion

    #region Save

    /// <summary>
    /// Into a save's CAMERA record (<c>FUN_140728af0</c>, <c>FUN_140375360</c>): the cooldown (the original writes its raw, ever falling value; here
    /// never below 0), the group tracked and the name of the group under the pivot, empty for NONE.
    /// </summary>
    public void WriteBiome(SaveCamera camera)
    {
        camera.BiomeTextTimer = MathF.Max(_biomeCooldown, 0);
        camera.BiomeTextGroup = _biomeGroup ?? "";
        var pivot = player.Session.Camera.Current.Target;

        // the name test only (FUN_140375360); the banner also tests the id
        camera.Area = RegionAt?.Invoke(pivot.X, pivot.Z) is { } here && here.Name != "NONE" ? here.Name : "";
    }

    /// <summary>
    /// A loaded save's cooldown (<c>FUN_140728c10</c>); the group is the one under the pivot, as the original's loading window sets it.
    /// </summary>
    public void ReadBiome(SaveCamera camera)
    {
        _biomeWorld = null;
        _pendingBiomeCooldown = camera.BiomeTextTimer;
    }

    #endregion
}