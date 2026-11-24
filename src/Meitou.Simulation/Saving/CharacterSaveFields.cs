using System.Text.RegularExpressions;
using Meitou.Data.Fcs;

namespace Meitou.Simulation.Saving;

/// <summary>
/// Which of a character's records in the vanilla save a mod field lives in (<see cref="CharacterSaveFields"/>); room for more records.
/// </summary>
public enum CharacterSaveRecord : byte
{
    /// <summary>
    /// The orders record: the character's GAMESTATE_AI record <c>ai</c> (the original's <c>OrdersReceiver</c>), which holds its job list and
    /// order queue.
    /// </summary>
    Orders,
    /// <summary>
    /// The stats record: the character's STATS record <c>stats</c>, which holds its skill levels as floats.
    /// </summary>
    Stats,
}

/// <summary>
/// Per-character fields a code mod keeps in the vanilla save under keys of its choosing, such as an original plugin's own keys
/// (docs/modding.md "Character save fields"): an int, float, bool or string by record and key. A value set (or removed) here is written into the
/// character's record with every save after the game's own keys; one not set reads what the loaded save held there (while the character keeps its
/// link to the save it was loaded from), so a mod reads its keys once the world is ready and sets those it keeps. The game's own keys of a record
/// cannot be set (<see cref="IsEngineKey"/>). Set and removed in the serial phases only (a tick system's step or a command, not during a save);
/// the values set enter the state hash. Engine API, not in the original.
/// </summary>
public sealed partial class CharacterSaveFields
{
    /// <summary>
    /// The world whose characters' fields these are.
    /// </summary>
    readonly World _world;

    /// <summary>
    /// How many characters have a field set.
    /// </summary>
    int _characters;

    /// <summary>
    /// The game's own keys of the stats record: the floats every STATS record of the vanilla saves surveyed holds (docs/formats/save.md
    /// "Character save fields"), Meitou's stat fields and legacy keys among them.
    /// </summary>
    static readonly HashSet<string> StatsKeys =
    [
        "armour smith", "arrow defence", "assassin", "athletics", "attack", "bluff", "blunt", "bow", "bow smith", "climbing", "cooking", "defence",
        "dexterity", "doctor", "dodge", "endurance", "engineer", "farming", "ff", "free attribute points", "hackers", "heavy weapons", "katana",
        "labouring", "lockpicking", "mass combat", "medic", "perception", "poles", "robotics", "sabres", "science", "stealth", "strength",
        "survival", "swimming", "thievery", "toughness2", "tracking", "turrets", "unarmed", "warrior spirit", "weapon smith", "xp",
    ];

    /// <summary>
    /// The fields of <paramref name="world"/>'s characters.
    /// </summary>
    internal CharacterSaveFields(World world) => _world = world;

    /// <summary>
    /// Whether any character has a field set; nothing is hashed or written without one.
    /// </summary>
    internal bool Any => _characters > 0;

    /// <summary>
    /// The int <paramref name="key"/> of <paramref name="character"/>'s <paramref name="record"/>; null when it has none.
    /// </summary>
    public int? Int(CharacterId character, CharacterSaveRecord record, string key) =>
        Read(character, record, key, out var value) ? value as int?
            : Loaded(character, record) is { } saved && saved.Ints.TryGetValue(key, out int i) ? i : null;

    /// <summary>
    /// The float <paramref name="key"/> of <paramref name="character"/>'s <paramref name="record"/>; null when it has none.
    /// </summary>
    public float? Float(CharacterId character, CharacterSaveRecord record, string key) =>
        Read(character, record, key, out var value) ? value as float?
            : Loaded(character, record) is { } saved && saved.Floats.TryGetValue(key, out float f) ? f : null;

    /// <summary>
    /// The bool <paramref name="key"/> of <paramref name="character"/>'s <paramref name="record"/>; null when it has none.
    /// </summary>
    public bool? Bool(CharacterId character, CharacterSaveRecord record, string key) =>
        Read(character, record, key, out var value) ? value as bool?
            : Loaded(character, record) is { } saved && saved.Bools.TryGetValue(key, out bool b) ? b : null;

    /// <summary>
    /// The string <paramref name="key"/> of <paramref name="character"/>'s <paramref name="record"/>; null when it has none.
    /// </summary>
    public string? String(CharacterId character, CharacterSaveRecord record, string key) =>
        Read(character, record, key, out var value) ? value as string
            : Loaded(character, record) is { } saved && saved.Strings.TryGetValue(key, out var s) ? s : null;

    /// <summary>
    /// Sets the int <paramref name="key"/> of <paramref name="character"/>'s <paramref name="record"/>.
    /// </summary>
    public void SetInt(CharacterId character, CharacterSaveRecord record, string key, int value) => Set(character, record, key, value);

    /// <summary>
    /// Sets the float <paramref name="key"/> of <paramref name="character"/>'s <paramref name="record"/> (a finite number).
    /// </summary>
    public void SetFloat(CharacterId character, CharacterSaveRecord record, string key, float value)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value), "A save field's float must be a finite number.");

        Set(character, record, key, value);
    }

    /// <summary>
    /// Sets the bool <paramref name="key"/> of <paramref name="character"/>'s <paramref name="record"/>.
    /// </summary>
    public void SetBool(CharacterId character, CharacterSaveRecord record, string key, bool value) => Set(character, record, key, value);

    /// <summary>
    /// Sets the string <paramref name="key"/> of <paramref name="character"/>'s <paramref name="record"/>.
    /// </summary>
    public void SetString(CharacterId character, CharacterSaveRecord record, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        Set(character, record, key, value);
    }

    /// <summary>
    /// Removes <paramref name="key"/> (of every type) from <paramref name="character"/>'s <paramref name="record"/>, also what the loaded save held.
    /// </summary>
    public void Remove(CharacterId character, CharacterSaveRecord record, string key) => Set(character, record, key, null);

    /// <summary>
    /// Whether <paramref name="key"/> is one of the game's own keys of <paramref name="record"/>, which no mod may set: of the orders record
    /// <c>jobs</c>, the job list's <c>pjob&lt;i&gt;</c> and <c>pjobT&lt;i&gt;</c> handle parts, the order queue's <c>tjob&lt;i&gt;</c>,
    /// <c>tjobP&lt;i&gt;</c> and <c>tjobT&lt;i&gt;</c>; of the stats record the 44 floats every saved STATS record has (<see cref="StatsKeys"/>);
    /// of both, every key starting <c>meitou </c>.
    /// </summary>
    public static bool IsEngineKey(CharacterSaveRecord record, string key) => record switch
    {
        _ when key.StartsWith("meitou ", StringComparison.Ordinal) => true,
        CharacterSaveRecord.Orders => key == "jobs" || OrdersKey().IsMatch(key),
        CharacterSaveRecord.Stats => StatsKeys.Contains(key),
        _ => true,
    };

    /// <summary>
    /// The job list's and order queue's numbered keys of the orders record.
    /// </summary>
    [GeneratedRegex(@"^(pjobT?|tjob[PT]?)\d+(TYPE|C|CS|I|S)?$", RegexOptions.CultureInvariant)]
    private static partial Regex OrdersKey();

    /// <summary>
    /// A field set or removed (null value) for the character; false when the character has none of that key.
    /// </summary>
    bool Read(CharacterId character, CharacterSaveRecord record, string key, out object? value)
    {
        value = null;
        return Cold(character)?.SaveFields is { } fields && fields.TryGetValue((record, key), out value);
    }

    /// <summary>
    /// The record the character's loaded save held; null without one.
    /// </summary>
    FcsRecord? Loaded(CharacterId character, CharacterSaveRecord record) => record switch
    {
        CharacterSaveRecord.Orders => Cold(character)?.Save?.Source.Ai,
        CharacterSaveRecord.Stats => Cold(character)?.Save?.Source.Stats,
        _ => null,
    };

    /// <summary>
    /// The character's cold state; null for none.
    /// </summary>
    CharacterCold? Cold(CharacterId character)
    {
        var table = _world.Characters;
        return table.TryResolveNext(character, out int slot) ? table.Cold(slot) : null;
    }

    /// <summary>
    /// Sets (or with a null <paramref name="value"/> removes) a field, after the checks.
    /// </summary>
    void Set(CharacterId character, CharacterSaveRecord record, string key, object? value)
    {
        Check(record, key);
        var cold = Cold(character) ?? throw new ArgumentException($"No character {character} in the world.", nameof(character));
        if (cold.SaveFields is null)
        {
            cold.SaveFields = [];
            _characters++;
        }

        cold.SaveFields[(record, key)] = value;
    }

    /// <summary>
    /// Throws when a field may not be set now (a parallel phase, a save under way) or under <paramref name="key"/>.
    /// </summary>
    void Check(CharacterSaveRecord record, string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var table = _world.Characters;
        if (table.Phase == TablePhase.Parallel)
            throw new InvalidOperationException("Character save fields are set in the serial phases only.");
        if (table.SaveHold) throw new InvalidOperationException("Character save fields cannot be set during a save.");
        if (!Enum.IsDefined(record) || IsEngineKey(record, key))
            throw new ArgumentException($"'{key}' is the game's own key of the {record} record.", nameof(key));
    }

    /// <summary>
    /// Writes <paramref name="cold"/>'s fields of <paramref name="which"/> into its saved <paramref name="target"/>, after the game's own keys (a
    /// save).
    /// </summary>
    internal static void Write(FcsRecord target, CharacterCold cold, CharacterSaveRecord which)
    {
        if (cold.SaveFields is not { } fields) return;

        foreach (var ((record, key), value) in fields)
        {
            if (record != which) continue;

            target.Ints.Remove(key);
            target.Floats.Remove(key);
            target.Bools.Remove(key);
            target.Strings.Remove(key);
            Put(target, key, value);
        }
    }

    /// <summary>
    /// Puts one value into the record under its type.
    /// </summary>
    static void Put(FcsRecord ai, string key, object? value)
    {
        switch (value)
        {
            case int i:
                ai.Ints[key] = i;
                break;
            case float f:
                ai.Floats[key] = f;
                break;
            case bool b:
                ai.Bools[key] = b;
                break;
            case string s:
                ai.Strings[key] = s;
                break;
        }
    }

    /// <summary>
    /// A value's type and invariant text, as hashed ("" for a removed one).
    /// </summary>
    static string Text(object? value) => value switch
    {
        null => "",
        IFormattable f => value.GetType().Name + ":" + f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => "String:" + value,
    };

    /// <summary>
    /// Adds every set field to the state hash: per character slot, its fields in key order.
    /// </summary>
    internal void Hash(ref StateHasher hash)
    {
        var table = _world.Characters;
        hash.Add(0x53465344);
        for (int slot = 0; slot < table.HighWater; slot++)
        {
            if (!table.Previous[slot].Alive || table.Cold(slot)?.SaveFields is not { Count: > 0 } fields) continue;

            hash.Add(slot);
            foreach (var ((record, key), value) in fields.OrderBy(f => f.Key.Item1).ThenBy(f => f.Key.Item2, StringComparer.Ordinal))
            {
                hash.Add((int)record);
                hash.Add(Rng.StableHash(key));
                hash.Add(Rng.StableHash(Text(value)));
            }
        }
    }
}
