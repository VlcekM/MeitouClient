using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Simulation.Dialogue;

namespace Meitou.Simulation.Saving;

/// <summary>
/// A squad's memory of characters on its PLATOON record (docs/formats/save.md "Squad memory", docs/game/dialogue.md "Squad memory"). The original's keys
/// (<c>FUN_1406789e0</c> writes, <c>FUN_140678cb0</c> reads, <b>Observed</b>), one set per remembered character <c>i</c> from 0: the character's handle under
/// <c>tags&lt;i&gt;</c> (the five handle ints), int <c>taglong&lt;i&gt;</c> and <c>tagshort&lt;i&gt;</c> (the tag bit sets), float <c>tagtime&lt;i&gt;</c> (the last
/// note). Meitou's time is game hours. The original keeps one entry for the whole player faction under a key not seen; Meitou writes it as
/// <c>meitou tags player long</c> / <c>short</c> / <c>time</c> and the squad's TEMP_ALLY flag for the player as <c>meitou temp ally</c> (bool). A character
/// without a handle in the save (not written) is left out, so is a handle that does not resolve when reading.
/// </summary>
public static class MemorySave
{
    public const string HandleKey = "tags", LongKey = "taglong", ShortKey = "tagshort", TimeKey = "tagtime";

    public const string PlayerLongKey = "meitou tags player long", PlayerShortKey = "meitou tags player short", PlayerTimeKey = "meitou tags player time",
        TempAllyKey = "meitou temp ally";

    /// <summary>
    /// Writes <paramref name="data"/> over the platoon's keys (removing what was there; null or empty leaves none).
    /// </summary>
    public static void Write(FcsRecord platoon, SquadMemoryData? data, Func<CharacterId, Hand?> handleOf)
    {
        Clear(platoon);
        if (data is null) return;

        int n = 0;
        foreach (var item in data.Entries)
        {
            if (handleOf(item.Who) is not { } hand) continue;

            hand.Write(platoon, HandleKey + n);
            platoon.Ints[LongKey + n] = unchecked((int)item.Long);
            platoon.Ints[ShortKey + n] = unchecked((int)item.Short);
            platoon.Floats[TimeKey + n] = (float)item.Time;
            n++;
        }

        if (data.Player is { } p)
        {
            platoon.Ints[PlayerLongKey] = unchecked((int)p.Long);
            platoon.Ints[PlayerShortKey] = unchecked((int)p.Short);
            platoon.Floats[PlayerTimeKey] = (float)p.Time;
        }

        if (data.TempAlly) platoon.Bools[TempAllyKey] = true;
    }

    /// <summary>
    /// Reads the platoon's memory, resolving each remembered handle through <paramref name="idOf"/> (null: not in the world); null when it holds none.
    /// </summary>
    public static SquadMemoryData? Read(FcsRecord platoon, Func<Hand, CharacterId?> idOf)
    {
        SquadMemoryData? data = null;
        for (int i = 0; platoon.Ints.ContainsKey(LongKey + i); i++)
        {
            if (idOf(Hand.Read(platoon, HandleKey + i)) is not { } who) continue;

            (data ??= new()).Entries.Add(new SquadMemoryData.Item(
                who, platoon.Floats.GetValueOrDefault(TimeKey + i), unchecked((uint)platoon.Ints[LongKey + i]),
                unchecked((uint)platoon.Ints.GetValueOrDefault(ShortKey + i))));
        }

        if (platoon.Ints.TryGetValue(PlayerLongKey, out int playerLong))
        {
            (data ??= new()).Player = new SquadMemoryData.Item(CharacterId.None, platoon.Floats.GetValueOrDefault(PlayerTimeKey),
                unchecked((uint)playerLong), unchecked((uint)platoon.Ints.GetValueOrDefault(PlayerShortKey)));
        }

        if (platoon.Bools.GetValueOrDefault(TempAllyKey)) (data ??= new()).TempAlly = true;
        return data;
    }

    static void Clear(FcsRecord platoon)
    {
        for (int i = 0; platoon.Ints.ContainsKey(LongKey + i) || platoon.Ints.ContainsKey(HandleKey + i + "I"); i++)
        {
            foreach (string part in new[] { "TYPE", "C", "CS", "I", "S" })
                platoon.Ints.Remove(HandleKey + i + part);

            platoon.Ints.Remove(LongKey + i);
            platoon.Ints.Remove(ShortKey + i);
            platoon.Floats.Remove(TimeKey + i);
        }

        platoon.Ints.Remove(PlayerLongKey);
        platoon.Ints.Remove(PlayerShortKey);
        platoon.Floats.Remove(PlayerTimeKey);
        platoon.Bools.Remove(TempAllyKey);
    }
}
