namespace Meitou.Data.Gameplay.Bodies;

/// <summary>A STATS record read into the stat numbers (docs/game/character-stats.md "Building a character's stats", step 1).</summary>
public static class StatsData
{
    /// <summary>
    /// The 33 skill fields of a STATS record by stat number (index = <see cref="StatsEnumerated"/>, length <see cref="StatsInfo.Count"/>). A field the
    /// record lacks is 0. The editor describes the fields as [0-100], but the base game's records go up to 300.
    /// </summary>
    public static float[] Read(GameRecord stats)
    {
        var values = new float[StatsInfo.Count];
        foreach (var stat in StatsInfo.RecordStats)
            values[(int)stat] = RecordReading.Float(stats, StatsInfo.FieldName(stat)!, 0);
        return values;
    }
}
