using Meitou.Data.Fcs;

namespace Meitou.Simulation.Saving;

/// <summary>
/// A character's dialogue state in its GAMESTATE_CHARACTER record (docs/formats/save.md "Locks, cages and sentences"; docs/game/dialogue.md "Memory and
/// saves"): the dialogue locks as the original's pairs <c>lok0</c>, <c>lok1</c>, ... (string = DIALOGUE string id, bool of the same key = locked;
/// <b>Observed</b>, <c>FUN_140679900</c>), and the <c>personality</c> int, read when above 0 (the original writes it; whether it is the dialogue's
/// personality tag is <b>Unknown</b>, so Meitou does not write it).
/// </summary>
public static class DialogueSave
{
    /// <summary>
    /// The key prefix of a dialogue lock pair and the personality key.
    /// </summary>
    const string LockPrefix = "lok", PersonalityKey = "personality";

    /// <summary>
    /// Reads the personality and the dialogue locks from a character's state record (nothing when there is none).
    /// </summary>
    public static void Read(FcsRecord? state, CharacterCold cold)
    {
        if (state is null) return;

        if (state.Ints.GetValueOrDefault(PersonalityKey) is > 0 and < Dialogue.Personality.Count and var p) cold.Personality = p;

        for (int i = 0; state.Strings.TryGetValue(LockPrefix + i, out var dialogue); i++)
        {
            if (dialogue.Length > 0)
                (cold.DialogueLocks ??= new(StringComparer.Ordinal))[dialogue] = state.Bools.GetValueOrDefault(LockPrefix + i);
        }
    }

    /// <summary>
    /// Writes the dialogue locks over the record's pairs (the old ones removed first); the personality is not written.
    /// </summary>
    public static void Write(FcsRecord state, CharacterCold cold)
    {
        for (int i = 0; state.Strings.Remove(LockPrefix + i) | state.Bools.Remove(LockPrefix + i); i++)
        {
        }

        if (cold.DialogueLocks is not { Count: > 0 } locks) return;

        int k = 0;
        foreach (var (dialogue, locked) in locks)
        {
            state.Strings[LockPrefix + k] = dialogue;
            state.Bools[LockPrefix + k] = locked;
            k++;
        }
    }
}
