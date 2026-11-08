using Meitou.Data.Fcs;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The crime markers of a character in its GAMESTATE_CHARACTER record (docs/formats/save.md "Locks, cages and sentences"): Meitou's own keys where the original's are not known.
/// <c>meitou escaped</c> (bool, <see cref="CharacterCold.Escaped"/>), <c>meitou kidnapped</c> (bool, <see cref="CharacterCold.Kidnapped"/>), the original's <c>disguiseblown</c> (float game seconds left, status <c>+0x110</c>, <see cref="CharacterCold.RecentlyWanted"/>; <c>FUN_140626580</c>, <b>Verified</b>; written always, as the original does, and read with the older <c>meitou recently wanted</c> as fallback), the original's short-keyed <c>tn</c> (bool, status <c>+0xea</c>, <see cref="CharacterCold.Freed"/>; <c>FUN_140626580</c> writes and <c>FUN_140626830</c> reads it, <b>Verified</b>; written always) and
/// <c>meitou recognition</c> (float 0..1, the bounty recognition meter <see cref="CharacterCold.Recognition"/>). The Meitou keys are written only while set and removed otherwise, so a save without
/// them is written as it was read. The bounty ledger, the held state and the sentence have their own savers (<see cref="BountySave"/>, <see cref="HeldSave"/>).
/// </summary>
public static class CrimeSave
{
    public const string EscapedKey = "meitou escaped", RecentlyWantedKey = "disguiseblown", OldRecentlyWantedKey = "meitou recently wanted", RecognitionKey = "meitou recognition", KidnappedKey = "meitou kidnapped", FreedKey = "tn";

    public static void Read(FcsRecord? state, CharacterCold cold)
    {
        if (state is null) return;
        cold.Escaped = state.Bools.GetValueOrDefault(EscapedKey);
        cold.Kidnapped = state.Bools.GetValueOrDefault(KidnappedKey);
        cold.Freed = state.Bools.GetValueOrDefault(FreedKey);
        cold.RecentlyWanted = MathF.Max(0, state.Floats.TryGetValue(RecentlyWantedKey, out float blown) ? blown : state.Floats.GetValueOrDefault(OldRecentlyWantedKey));
        cold.Recognition = Math.Clamp(state.Floats.GetValueOrDefault(RecognitionKey), 0, 1);
    }

    public static void Write(FcsRecord state, CharacterCold cold)
    {
        if (cold.Escaped) state.Bools[EscapedKey] = true; else state.Bools.Remove(EscapedKey);
        if (cold.Kidnapped) state.Bools[KidnappedKey] = true; else state.Bools.Remove(KidnappedKey);
        state.Bools[FreedKey] = cold.Freed;
        state.Floats[RecentlyWantedKey] = MathF.Max(0, cold.RecentlyWanted);
        state.Floats.Remove(OldRecentlyWantedKey);
        if (cold.Recognition > 0) state.Floats[RecognitionKey] = cold.Recognition; else state.Floats.Remove(RecognitionKey);
    }
}