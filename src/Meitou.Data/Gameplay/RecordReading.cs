namespace Meitou.Data.Gameplay;

/// <summary>Reading a field that the data may hold as a float or as an int; a null record reads as an empty one (every field missing).</summary>
internal static class RecordReading
{
    public static float Float(GameRecord? r, string key, float fallback) =>
        r is null ? fallback : r.Floats.TryGetValue(key, out float v) ? v : r.Ints.TryGetValue(key, out int n) ? n : fallback;

    public static int Int(GameRecord? r, string key, int fallback) =>
        r is null ? fallback : r.Ints.TryGetValue(key, out int v) ? v : r.Floats.TryGetValue(key, out float x) ? (int)MathF.Round(x) : fallback;

    public static bool Bool(GameRecord? r, string key, bool fallback) =>
        r is null ? fallback : r.Bools.TryGetValue(key, out bool v) ? v : r.Ints.TryGetValue(key, out int n) ? n != 0 : fallback;
}

/// <summary>Where the game's CONSTANTS live, for the readers of <see cref="GameConstants"/> and <see cref="Combat.CombatConstants"/>.</summary>
internal static class ConstantsRecord
{
    /// <summary>The record named <c>GLOBAL CONSTANTS</c>, else the first CONSTANTS record, else null.</summary>
    public static GameRecord? Find(GameDatabase db) =>
        db.OfType(Fcs.FcsRecordType.CONSTANTS).FirstOrDefault(r => r.Name == "GLOBAL CONSTANTS") ?? db.OfType(Fcs.FcsRecordType.CONSTANTS).FirstOrDefault();
}
