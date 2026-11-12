using System.Diagnostics;

namespace Meitou.Game.Ui;

/// <summary>
/// The squad's banter on arriving in a BIOME_GROUP (<c>PlayerInterface::triggerAreaArrivalDialogue</c>, docs/game/ui-screens.md "Biome banner"):
/// tried when the <see cref="BiomeBanner"/> hides, and again while the group stays under the pivot until the world says to forget it.
/// </summary>
sealed class ArrivalBanter(PlayerInterface player)
{
    /// <summary>
    /// Between banter tries: the real time between them (<c>DAT_14167fad4</c>).
    /// </summary>
    const double ArrivalRetrySeconds = 10;

    /// <summary>
    /// The group whose arrival banter is still being tried (<c>DAT_142132c70</c>); null for none.
    /// </summary>
    string? _arrivalGroup;

    /// <summary>
    /// The last try's serial, by which the world answers it.
    /// </summary>
    int _arrivalSerial;

    /// <summary>
    /// When the last try was made (wall clock).
    /// </summary>
    long _arrivalAt;

    /// <summary>
    /// Stores the group with the time and makes the first try (<c>FUN_1408015d0</c>).
    /// </summary>
    internal void TryArrival(string group)
    {
        _arrivalGroup = group;
        _arrivalAt = Stopwatch.GetTimestamp();
        player.Say(new Simulation.Dialogue.AreaArrival(group, ++_arrivalSerial) { Tick = player.Session.World.Tick });
    }

    /// <summary>
    /// Every frame (<c>FUN_140801540</c>): the stored group is dropped when the group under the pivot is another, or when the last try's result says
    /// to forget it; else after more than 10 s of real time since the last try the banter is tried again.
    /// </summary>
    internal void RetryArrival(string here)
    {
        if (_arrivalGroup is not { } group) return;

        bool? forget = player.Session.World.ArrivalForget(_arrivalSerial);
        if (here != group || forget == true) _arrivalGroup = null;
        else if (forget == false && Stopwatch.GetElapsedTime(_arrivalAt).TotalSeconds > ArrivalRetrySeconds) TryArrival(group);
    }
}
