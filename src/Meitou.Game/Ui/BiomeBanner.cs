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

    object? biomeWorld;
    string? biomeGroup;
    string biomeName = "";
    float biomeHold, biomeCooldown, biomeAlpha;
    internal bool biomeShown;
    long biomeAt;

    /// <summary>Once a frame: the banner's timers, its fade and the group it tracks. A new world only takes the group under the pivot (the original does so while its loading window is up).</summary>
    internal void PollBiome()
    {
        long now = Stopwatch.GetTimestamp();
        float dt = biomeAt == 0 ? 0 : MathF.Min((float)Stopwatch.GetElapsedTime(biomeAt, now).TotalSeconds, 0.25f);
        biomeAt = now;
        if (player.TitleOpen || player.characterEditor.EditorOpen || RegionAt is null) return;
        var pivot = player.session.Camera.Current.Target;
        if (RegionAt(pivot.X, pivot.Z) is not { } here) return;
        if (!ReferenceEquals(biomeWorld, player.session.World))
        {
            biomeWorld = player.session.World;
            (biomeGroup, biomeShown, biomeHold, biomeCooldown, pendingBiomeCooldown) = (here.Id, false, 0, pendingBiomeCooldown, 0);
            return;
        }
        biomeHold -= dt;
        biomeCooldown -= dt;
        bool blocked = player.townPanel.shownTown != 0;   // the town panel is showing a town (MainBarGUI +0x128, FUN_140728d50:358): no banner starts and the group does not follow the pivot
        if (here.Id != biomeGroup && biomeCooldown <= 0 && !blocked && here.Name != "NONE" && here.Id != WeatherData.NoneRegionId)
        {
            (biomeName, biomeShown, biomeAlpha, biomeHold, biomeCooldown) = (here.Name, true, 0, BiomeHoldSeconds, BiomeCooldownSeconds);
            player.interfaceSounds.Sound("Notifications", "Notifications", "New_Biome");
        }
        else if (biomeShown)
        {
            if (biomeHold > 0) biomeAlpha = MathF.Min(1, biomeAlpha + dt * 2);
            else if ((biomeAlpha -= dt) < 0)
            {
                biomeShown = false;
                // On hiding the game tests the group's `arrival dialog`: squad banter (PlayerInterface::triggerAreaArrivalDialogue).
                // It reads the group under the pivot this frame (FUN_1408015d0 gets the current holder), not the stored one.
                TryArrival(here.Id);
            }
        }
        if (!blocked) biomeGroup = here.Id;
        RetryArrival(here.Id);
    }

    /// <summary>The group whose arrival banter is still being tried (<c>DAT_142132c70</c>), the try's serial and when it was last made (wall clock).</summary>
    string? arrivalGroup;
    int arrivalSerial;
    long arrivalAt;

    /// <summary>Between banter tries: the real time between them (<c>DAT_14167fad4</c>).</summary>
    const double ArrivalRetrySeconds = 10;

    /// <summary>Stores the group with the time and makes the first try (<c>FUN_1408015d0</c>).</summary>
    void TryArrival(string group)
    {
        arrivalGroup = group;
        arrivalAt = Stopwatch.GetTimestamp();
        player.Say(new Meitou.Simulation.Dialogue.AreaArrival(group, ++arrivalSerial) { Tick = player.session.World.Tick });
    }

    /// <summary>
    /// Every frame (<c>FUN_140801540</c>): the stored group is dropped when the group under the pivot is another, or when the last try's result says to forget it; else after more than 10 s
    /// of real time since the last try the banter is tried again.
    /// </summary>
    void RetryArrival(string here)
    {
        if (arrivalGroup is not { } group) return;
        bool? forget = player.session.World.ArrivalForget(arrivalSerial);
        if (here != group || forget == true) arrivalGroup = null;
        else if (forget == false && Stopwatch.GetElapsedTime(arrivalAt).TotalSeconds > ArrivalRetrySeconds) TryArrival(group);
    }

    /// <summary>The panel over the HUD while the banner shows: the layout's own panel at the fade's alpha with the name in its text box; hidden otherwise.</summary>
    internal void BindBiome(UiBindings b)
    {
        if (!biomeShown) { b.Hidden.Add("BiomeTextPanel"); return; }
        b.Properties["BiomeTextPanel"] = new Dictionary<string, string> { ["Visible"] = "true", ["Alpha"] = Math.Clamp(biomeAlpha, 0, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        b.Text["BiomeTextBox"] = biomeName;
        // The layout lets the mouse through only the panel (NeedMouse is not inherited): its two lines would otherwise take clicks.
        foreach (string line in new[] { "BiomeNameTopLine", "BiomeNameBottomLine" }) b.Properties[line] = new Dictionary<string, string> { ["NeedMouse"] = "false" };
    }

    /// <summary>
    /// Into a save's CAMERA record (<c>FUN_140728af0</c>, <c>FUN_140375360</c>): the cooldown (the original writes its raw, ever falling value; here never below 0), the group tracked and the name of the
    /// group under the pivot, empty for NONE.
    /// </summary>
    public void WriteBiome(SaveCamera camera)
    {
        camera.BiomeTextTimer = MathF.Max(biomeCooldown, 0);
        camera.BiomeTextGroup = biomeGroup ?? "";
        var pivot = player.session.Camera.Current.Target;
        camera.Area = RegionAt?.Invoke(pivot.X, pivot.Z) is { } here && here.Name != "NONE" ? here.Name : "";   // the name test only (FUN_140375360); the banner also tests the id
    }

    /// <summary>A loaded save's cooldown (<c>FUN_140728c10</c>); the group is the one under the pivot, as the original's loading window sets it.</summary>
    public void ReadBiome(SaveCamera camera)
    {
        biomeWorld = null;
        pendingBiomeCooldown = camera.BiomeTextTimer;
    }

    float pendingBiomeCooldown;
}
