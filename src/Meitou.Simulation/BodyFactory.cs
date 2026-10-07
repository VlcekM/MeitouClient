using System.Collections.Concurrent;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation;

/// <summary>
/// Makes the body of a character from its records (docs/game/character-stats.md "Building a character's stats"): the stats from the STATS record the
/// CHARACTER names, or from its five group fields when it names none, seeded by the world seed and the character's key, then a fresh medical state of
/// its race. Race data is read once per race. Safe to call from any thread.
/// </summary>
public sealed class BodyFactory(GameDatabase db)
{
    readonly ConcurrentDictionary<string, RaceData> races = new();

    public GameDatabase Db { get; } = db;
    public GameConstants Constants { get; } = GameConstants.FromDatabase(db);
    public BodyOptions Options { get; init; } = BodyOptions.Default;

    /// <summary>The data of a RACE record (cached).</summary>
    public RaceData Race(GameRecord race) => races.GetOrAdd(race.StringId, _ => RaceData.From(race, Db));

    /// <summary>The stats and the medical state of a character of <paramref name="race"/>, made from the CHARACTER record <paramref name="character"/> (null: defaults).</summary>
    public (RaceData Race, CharacterStats Stats, MedicalState Medical) Create(GameRecord race, GameRecord? character, ulong worldSeed, ulong characterKey)
    {
        var data = Race(race);
        float[] values;
        float randomise = 0;
        GameRecord? statsRecord = null;
        if (character is not null)
            foreach (var r in character.GetReferences("stats")) if (r.Values.Value0 > 0) { statsRecord = Db.Find(r.TargetStringId); break; }
        if (statsRecord is not null) values = StatsData.Read(statsRecord);
        else if (character is not null)
            values = CharacterStats.FromGroups(character.GetInt("combat stats"), character.GetInt("unarmed stats"), character.GetInt("stealth stats"),
                character.GetInt("ranged stats"), character.GetInt("strength"));
        else values = new float[StatsInfo.Count];
        if (character is not null) randomise = character.GetFloat("stats randomise");
        var stats = CharacterStats.Create(values, data, randomise, worldSeed, characterKey);
        return (data, stats, MedicalState.Create(data, stats.Strength));
    }
}
