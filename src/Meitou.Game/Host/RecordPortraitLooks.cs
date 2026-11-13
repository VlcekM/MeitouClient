using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Game.Rendering.Characters;
using Meitou.Game.Rendering.Frame;
using Meitou.Simulation;

namespace Meitou.Game.Host;

/// <summary>
/// The looks of the CHARACTER records whose portraits code mods show (<see cref="Ui.RecordPortraits"/>): each rolled once from the record's id on a
/// worker, then listed as a hidden instance under a key of its own, from which the renderer draws the record's atlas cell.
/// </summary>
sealed class RecordPortraitLooks(GameHost host)
{
    /// <summary>
    /// The draw key's bit of a record's hidden instance (bits 53 to 55 mark a character's other instances); the rest is the record's number here.
    /// </summary>
    internal const long KeyBit = 1L << 52;

    /// <summary>
    /// A record asked for: its number (its draw key's low bits), the number that turns its portrait camera, and its look being made (null: none).
    /// </summary>
    sealed record Entry(int Number, uint Serial, Task<CharacterAppearance?> Look);

    /// <summary>
    /// The records asked for so far, by string id.
    /// </summary>
    readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// The game data <see cref="_entries"/>' looks were made from.
    /// </summary>
    GameDatabase? _database;

    /// <summary>
    /// Asks <paramref name="characters"/> for the portrait of every record the interface shows whose look is ready, in the record's cell.
    /// </summary>
    internal void AddRequests(CharacterRenderer characters)
    {
        foreach (var (record, cell) in host.Player.RecordPortraits.Wanted)
        {
            if (Ready(record) is { } entry) characters.PortraitRequests.Add(new PortraitRequest(KeyBit | (uint)entry.Number, cell, entry.Serial));
        }
    }

    /// <summary>
    /// Lists the shown records whose look is ready, hidden at <paramref name="eye"/>: the renderer builds and poses them for their portraits alone.
    /// </summary>
    internal void AddInstances(CharacterDrawList list, Vector3 eye)
    {
        foreach (var (record, _) in host.Player.RecordPortraits.Wanted)
        {
            if (Ready(record) is { Look.Result: { } look } entry)
                list.Add(new CharacterInstance(KeyBit | (uint)entry.Number, look, eye, 0, []) { Hidden = true });
        }
    }

    /// <summary>
    /// <paramref name="record"/>'s entry once its look is made (the first ask starts it); null while it is not, or when it has none.
    /// </summary>
    Entry? Ready(string record)
    {
        if (!ReferenceEquals(_database, host.Scene.Database))
        {
            _entries.Clear();
            _database = host.Scene.Database;
        }

        if (!_entries.TryGetValue(record, out var entry)) _entries[record] = entry = Begin(record);
        return entry.Look is { IsCompletedSuccessfully: true, Result: not null } ? entry : null;
    }

    /// <summary>
    /// A new entry for <paramref name="record"/>, its look rolled on a worker from the record's id (the same face every time); no look for a
    /// record that is not a CHARACTER or ANIMAL_CHARACTER, or without the population's looks.
    /// </summary>
    Entry Begin(string record)
    {
        ulong hash = Rng.StableHash(record);
        var looks = host.Population?.Data.Appearances;
        bool character = _database?.Find(record) is { Type: FcsRecordType.Character or FcsRecordType.AnimalCharacter };
        if (!character) Console.Error.WriteLine($"ui        portrait: '{record}' is not a CHARACTER record");

        var look = character && looks is not null
            ? Task.Run(() => looks.Create(record, null, unchecked((int)hash)))
            : Task.FromResult<CharacterAppearance?>(null);
        return new Entry(_entries.Count, unchecked((uint)(hash >> 32)), look);
    }
}
