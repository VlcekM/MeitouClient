namespace Meitou.Data.Gameplay.Bodies;

/// <summary>Reading a field that the data may hold as a float or as an int (the same tolerance <see cref="GameConstants"/> has).</summary>
internal static class RecordReading
{
    public static float Float(GameRecord r, string key, float fallback) =>
        r.Floats.TryGetValue(key, out float v) ? v : r.Ints.TryGetValue(key, out int n) ? n : fallback;

    public static int Int(GameRecord r, string key, int fallback) =>
        r.Ints.TryGetValue(key, out int v) ? v : r.Floats.TryGetValue(key, out float x) ? (int)MathF.Round(x) : fallback;

    public static bool Bool(GameRecord r, string key, bool fallback) =>
        r.Bools.TryGetValue(key, out bool v) ? v : r.Ints.TryGetValue(key, out int n) ? n != 0 : fallback;
}
