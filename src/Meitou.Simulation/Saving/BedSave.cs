using Meitou.Data.Fcs;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The bed rents the player's faction paid, on the CAMERA record (docs/formats/save.md "Bed rent"): Meitou's own keys, as the original saves none
/// (its map of last payments, <c>FUN_1403576c0</c>, is referenced from nowhere else, <b>Verified</b> by references). <c>meitou rent bed&lt;i&gt;</c>
/// (string, the bed's fixture id) and <c>meitou rent hours&lt;i&gt;</c> (float, the absolute game hour of the payment); only payments less than
/// <see cref="BedSettings.RentHours"/> old are written, as an older one frees nothing.
/// </summary>
public static class BedSave
{
    /// <summary>
    /// The CAMERA key prefixes of a paid bed: the bed's fixture id and the game hour of the payment, each followed by the entry's number.
    /// </summary>
    public const string BedKey = "meitou rent bed", HoursKey = "meitou rent hours";

    /// <summary>
    /// Writes the paid bed rents still running over the camera record's (the old entries removed first).
    /// </summary>
    public static void Write(FcsRecord camera, World world)
    {
        for (int i = 0; camera.Strings.ContainsKey(BedKey + i); i++)
        {
            camera.Strings.Remove(BedKey + i);
            camera.Floats.Remove(HoursKey + i);
        }

        if (world.Systems.OfType<BedSystem>().FirstOrDefault() is not { } beds) return;

        int n = 0;
        foreach (var (bed, hours) in beds.RentPaid)
        {
            if (world.GameHours - hours >= beds.Settings.RentHours) continue;

            camera.Strings[BedKey + n] = bed;
            camera.Floats[HoursKey + n] = (float)hours;
            n++;
        }
    }

    /// <summary>
    /// Reads the paid bed rents back into the world's bed system.
    /// </summary>
    public static void Read(FcsRecord camera, World world)
    {
        if (world.Systems.OfType<BedSystem>().FirstOrDefault() is not { } beds) return;

        for (int i = 0; camera.Strings.TryGetValue(BedKey + i, out var bed); i++)
            beds.SetRentPaid(bed, camera.Floats.GetValueOrDefault(HoursKey + i));
    }
}
