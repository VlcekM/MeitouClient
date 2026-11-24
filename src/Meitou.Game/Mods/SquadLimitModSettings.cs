using Meitou.Content;
using Meitou.Data.Fcs;
using Meitou.Game.Host;

namespace Meitou.Modding;

/// <summary>
/// The player squad and faction limits of a covered squad-size or recruitment-limit mod as Meitou's Advanced settings take them in
/// (<see cref="BuiltInMods"/>, docs/formats/rekenshi.md).
/// </summary>
static class SquadLimitModSettings
{
    /// <summary>
    /// Takes in the player caps (<c>max faction size</c>, <c>max squad size</c>, <c>max squads</c>) set in <paramref name="mod"/>'s
    /// <c>GLOBAL CONSTANTS</c> record into <paramref name="config"/>; leaves any cap the mod does not set as it is.
    /// </summary>
    internal static void Adopt(UserConfig config, ModInfo mod)
    {
        try
        {
            var file = FcsReader.ReadFile(mod.Path);
            var constants = file.Records.FirstOrDefault(r => r.RecordType == FcsRecordType.Constants && r.Name == "GLOBAL CONSTANTS")
                ?? file.Records.FirstOrDefault(r => r.RecordType == FcsRecordType.Constants);
            if (constants is null) return;

            if (IntValue(constants, "max faction size") is { } faction)
                config.MaxFactionSize = Math.Clamp(faction, UserConfig.MinSquadLimit, UserConfig.MaxSquadLimit);
            if (IntValue(constants, "max squad size") is { } squad)
                config.MaxSquadSize = Math.Clamp(squad, UserConfig.MinSquadLimit, UserConfig.MaxSquadLimit);
            if (IntValue(constants, "max squads") is { } squads)
                config.MaxSquads = Math.Clamp(squads, UserConfig.MinSquadLimit, UserConfig.MaxSquadLimit);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FcsFormatException)
        {
            Console.Error.WriteLine($"mods      {mod.Path} could not be read: {e.Message}");
        }
    }

    /// <summary>
    /// The int (or rounded float) value of <paramref name="key"/> in <paramref name="record"/>; null when the record does not set it.
    /// </summary>
    static int? IntValue(FcsRecord record, string key) =>
        record.Ints.TryGetValue(key, out int v) ? v
        : record.Floats.TryGetValue(key, out float f) ? (int)MathF.Round(f)
        : null;
}
