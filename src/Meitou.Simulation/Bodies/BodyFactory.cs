using System.Collections.Concurrent;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// Makes the body of a character from its records (docs/game/character-stats.md "Building a character's stats"): the stats from the STATS record the
/// CHARACTER names, or from its five group fields when it names none, seeded by the world seed and the character's key, then a fresh medical state of
/// its race. Race data is read once per race. Safe to call from any thread.
/// </summary>
public sealed class BodyFactory(GameDatabase db)
{
    /// <summary>
    /// The race data read so far, by RACE string id.
    /// </summary>
    readonly ConcurrentDictionary<string, RaceData> _races = new();

    /// <summary>
    /// The game data the CHARACTER, STATS and RACE records come from.
    /// </summary>
    public GameDatabase Db { get; } = db;
    /// <summary>
    /// The CONSTANTS of the game data, read once.
    /// </summary>
    public GameConstants Constants { get; } = GameConstants.FromDatabase(db);
    /// <summary>
    /// The body options (the medical rules' engine choices and settings) the new medical states get.
    /// </summary>
    public BodyOptions Options { get; init; } = BodyOptions.Default;

    /// <summary>
    /// The data of a RACE record (cached).
    /// </summary>
    public RaceData Race(GameRecord race) => _races.GetOrAdd(race.StringId, _ => RaceData.From(race, Db));

    /// <summary>
    /// The stats and the medical state of a character of <paramref name="race"/>, made from the CHARACTER record <paramref name="character"/> (null:
    /// defaults). An ANIMAL_CHARACTER (docs/game/animals.md) reads its stats the same way; its stat multiplier is its <paramref name="age"/>, it has
    /// no humanoid stats object, its parts' HP multiplier is its size lerp(age, scale min, scale max) and their body scale its <c>HP mult</c>.
    /// </summary>
    public (RaceData Race, CharacterStats Stats, MedicalState Medical) Create(
        GameRecord race, GameRecord? character, ulong worldSeed, ulong characterKey, float age = 1)
    {
        var data = Race(race);
        float[] values;
        float randomise = 0;
        GameRecord? statsRecord = null;

        // The three numbers of a reference are its list slot, not a switch: most characters that name a STATS record carry (0, 100, 0) and the rest
        // (0, 0, 0), so the record is used whenever one is named, as the original does.
        if (character is not null)
            foreach (var r in character.GetReferences("stats"))
                if (Db.Find(r.TargetStringId) is { } listed)
                {
                    statsRecord = listed;
                    break;
                }
        if (statsRecord is not null) values = StatsData.Read(statsRecord);
        else if (character is not null)
            values = CharacterStats.FromGroups(character.GetInt("combat stats"), character.GetInt("unarmed stats"), character.GetInt("stealth stats"),
                character.GetInt("ranged stats"), character.GetInt("strength"));
        else values = new float[StatsInfo.Count];

        // The base game's field is an integer; retain float-only mod records as a fallback.
        if (character is not null) randomise = character.Ints.TryGetValue("stats randomise", out int amount)
            ? amount : character.GetFloat("stats randomise");
        var stats = CharacterStats.Create(values, data, randomise, worldSeed, characterKey);
        if (AnimalData.From(character) is { } animal)
        {
            stats.Scale = age;
            return (data, stats,
                MedicalState.Create(data, stats.Strength, hasStats: false, hpMultiplier: animal.Scale(age), bodyScale: animal.HpMult));
        }
        var medical = MedicalState.Create(data, stats.Strength);
        if (character is not null)
            foreach (var r in character.GetReferences("starting health")) medical.ApplyStartingHealth(r.TargetStringId, r.Values.Value0);
        return (data, stats, medical);
    }
}