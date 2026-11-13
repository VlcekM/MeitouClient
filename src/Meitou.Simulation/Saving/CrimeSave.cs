using Meitou.Data.Fcs;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The crime markers of a character in its GAMESTATE_CHARACTER record (docs/formats/save.md "Locks, cages and sentences"), under the original's
/// keys (<c>FUN_140626580</c> writes and <c>FUN_140626830</c> reads them, <b>Verified</b>; written always, as the original does): <c>escap</c>
/// (bool, status <c>+0xe8</c>, <see cref="CharacterCold.Escaped"/>), the short-keyed <c>kidn</c> (bool, status <c>+0xe9</c>,
/// <see cref="CharacterCold.Kidnapped"/>) and <c>tn</c> (bool, status <c>+0xea</c>, <see cref="CharacterCold.Freed"/>), and
/// <c>disguiseblown</c> (float game seconds left, status <c>+0x110</c>, <see cref="CharacterCold.RecentlyWanted"/>). Older Meitou saves wrote
/// <c>meitou escaped</c>, <c>meitou kidnapped</c> and <c>meitou recently wanted</c>: they are read when the original's key is missing and dropped
/// on save. Meitou's own <c>meitou recognition</c> (float 0..1, the bounty recognition meter <see cref="CharacterCold.Recognition"/>) is written
/// only while set and removed otherwise. The bounty ledger, the held state and the sentence have their own savers (<see cref="BountySave"/>,
/// <see cref="HeldSave"/>).
/// </summary>
public static class CrimeSave
{
    /// <summary>
    /// The GAMESTATE_CHARACTER keys of the crime markers (see the class remarks; <c>tn</c> is the freed flag), and the keys older Meitou saves
    /// used.
    /// </summary>
    public const string EscapedKey = "escap", KidnappedKey = "kidn", FreedKey = "tn", RecentlyWantedKey = "disguiseblown",
        RecognitionKey = "meitou recognition", OldEscapedKey = "meitou escaped", OldKidnappedKey = "meitou kidnapped",
        OldRecentlyWantedKey = "meitou recently wanted";

    /// <summary>
    /// Reads the crime markers from a character's state record (nothing when there is none).
    /// </summary>
    public static void Read(FcsRecord? state, CharacterCold cold)
    {
        if (state is null) return;

        cold.Escaped = Flag(state, EscapedKey, OldEscapedKey);
        cold.Kidnapped = Flag(state, KidnappedKey, OldKidnappedKey);
        cold.Freed = state.Bools.GetValueOrDefault(FreedKey);
        float recently = state.Floats.TryGetValue(RecentlyWantedKey, out float blown)
            ? blown
            : state.Floats.GetValueOrDefault(OldRecentlyWantedKey);
        cold.RecentlyWanted = MathF.Max(0, recently);
        cold.Recognition = Math.Clamp(state.Floats.GetValueOrDefault(RecognitionKey), 0, 1);
    }

    /// <summary>
    /// Writes the crime markers into a character's state record: the original's keys always, Meitou's only while set, the older Meitou keys
    /// never.
    /// </summary>
    public static void Write(FcsRecord state, CharacterCold cold)
    {
        state.Bools[EscapedKey] = cold.Escaped;
        state.Bools[KidnappedKey] = cold.Kidnapped;
        state.Bools[FreedKey] = cold.Freed;
        state.Floats[RecentlyWantedKey] = MathF.Max(0, cold.RecentlyWanted);

        state.Bools.Remove(OldEscapedKey);
        state.Bools.Remove(OldKidnappedKey);
        state.Floats.Remove(OldRecentlyWantedKey);

        if (cold.Recognition > 0)
            state.Floats[RecognitionKey] = cold.Recognition;
        else
            state.Floats.Remove(RecognitionKey);
    }

    /// <summary>
    /// A flag under the original's key, or under the key older Meitou saves used when the original's is missing.
    /// </summary>
    static bool Flag(FcsRecord state, string key, string oldKey) =>
        state.Bools.TryGetValue(key, out bool set) ? set : state.Bools.GetValueOrDefault(oldKey);
}
