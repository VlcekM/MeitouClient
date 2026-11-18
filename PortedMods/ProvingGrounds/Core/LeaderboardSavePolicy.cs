// Port of upstream/ProvingGrounds/plugin/src/LeaderboardSavePolicy.h (Proving Grounds by Shutori, GPLv3).
// LeaderboardSavePolicy.h - pure save/sidecar synchronization policy
//
// Ported whole. The sidecar half (ShouldWriteSidecar, ActiveSaveKey, NormaliseSaveKey, SameSaveKey, ShouldAcceptSidecar, IsForkSave,
// WorldContext.CanReadSidecar) decides about the original's proving_grounds_mmr.json beside the save folder; this port stores the
// snapshot inside the save as Meitou mod data (docs/divergences.md, "Sidecar files"), so the glue has no caller for it. The world
// context, the prisoner hold and the manual-save gate apply unchanged.
namespace ProvingGrounds.Core;

/// <summary>When arena data is written and read with Kenshi's world save, and how a manual save waits for prisoners.
/// C++: LeaderboardSavePolicy.h (PA §2.20).</summary>
public static class LeaderboardSavePolicy
{
    /// <summary>C++: LeaderboardSavePolicy.h:9-21</summary>
    public sealed class WorldContext
    {
        enum Mode { Blocked, AwaitingLoad, Fresh, Loaded }
        Mode mode;
        public WorldContext() { mode = Mode.AwaitingLoad; }
        public void Block() { mode = Mode.Blocked; }
        public void CompleteTransition(bool newGame) { mode = newGame ? Mode.Fresh : Mode.AwaitingLoad; }
        public void MarkLoaded() { mode = Mode.Loaded; }
        public bool IsFresh() { return mode == Mode.Fresh; }
        public bool CanReadSidecar() { return mode == Mode.AwaitingLoad; }
        public bool CanUseMemory() { return mode == Mode.Fresh || mode == Mode.Loaded; }
    }

    /// <summary>C++: LeaderboardSavePolicy.h:22-26</summary>
    public enum Event
    {
        GameplayMutation = 0,
        NativeSaveCompleted
    }

    // Arena data must advance only with Kenshi's world snapshot. Writing on a
    // purchase or match would make an older world save inherit newer data.
    // C++: LeaderboardSavePolicy.h:30-33
    public static bool ShouldWriteSidecar(Event e)
    {
        return e == Event.NativeSaveCompleted;
    }

    // SaveManager::name is a pending save/load target and may remain stale.
    // The loaded snapshot is identified only by the active save directory.
    // C++: LeaderboardSavePolicy.h:37-42
    public static string ActiveSaveKey(
        string currentGame,
        string pendingName)
    {
        _ = pendingName;
        return currentGame;
    }

    // Kenshi save-folder names are case-insensitive on Windows. Keep the
    // normalization here, away from filesystem and game-runtime code, so the
    // sidecar owner check is directly unit-testable.
    // C++: LeaderboardSavePolicy.h:47-56 (std::tolower per byte in the "C" locale: only A-Z change; every other char is kept)
    public static string NormaliseSaveKey(string key)
    {
        char[] result = key.ToCharArray();
        for (int i = 0; i < result.Length; ++i)
        {
            char c = result[i];
            if (c >= 'A' && c <= 'Z')
                result[i] = (char)(c + ('a' - 'A'));
        }
        return new string(result);
    }

    // C++: LeaderboardSavePolicy.h:58-62
    public static bool SameSaveKey(string a, string b)
    {
        return a.Length != 0 && b.Length != 0 &&
            string.Equals(NormaliseSaveKey(a), NormaliseSaveKey(b), StringComparison.Ordinal);
    }

    // Versions before 6 did not stamp an owner key. They are accepted only
    // from the requested folder and upgraded on its next native save. A v6+
    // sidecar with a different owner is foreign data and must be ignored.
    // C++: LeaderboardSavePolicy.h:67-77
    public static bool ShouldAcceptSidecar(
        string requestedSaveKey,
        string declaredSaveKey,
        int version)
    {
        if (requestedSaveKey.Length == 0)
            return false;
        if (version < 6)
            return true;
        return SameSaveKey(requestedSaveKey, declaredSaveKey);
    }

    // Saving into another folder is a deliberate native world fork (Save As,
    // quicksave, or autosave), never a context switch. The active context
    // remains the source until Kenshi actually loads the target folder.
    // C++: LeaderboardSavePolicy.h:82-88
    public static bool IsForkSave(
        string activeSaveKey,
        string targetSaveKey)
    {
        return activeSaveKey.Length != 0 && targetSaveKey.Length != 0 &&
            !SameSaveKey(activeSaveKey, targetSaveKey);
    }

    // C++: LeaderboardSavePolicy.h:90
    public static bool HoldForPrisoners(bool activity, bool pending, bool failed) { return pending || (activity && !failed); }

    /// <summary>C++: LeaderboardSavePolicy.h:91-92</summary>
    public enum ManualSaveDecision
    {
        AllowManualSave, WarnUnsecuredSave, DeferManualSave,
        NoDeferredSave, CancelManualSave, ResumeManualSave
    }

    /// <summary>C++: LeaderboardSavePolicy.h:93-107</summary>
    public sealed class ManualSaveGate
    {
        bool waiting;
        public ManualSaveGate() { waiting = false; }
        public void Defer() { waiting = true; }
        public void Abandon() { waiting = false; }
        public bool Waiting() { return waiting; }
        public ManualSaveDecision Request(bool failed) { return waiting ? ManualSaveDecision.DeferManualSave : (failed ? ManualSaveDecision.WarnUnsecuredSave : ManualSaveDecision.AllowManualSave); }
        public ManualSaveDecision Poll(bool pending, bool failed)
        {
            if (!waiting) return ManualSaveDecision.NoDeferredSave;
            if (pending) return ManualSaveDecision.DeferManualSave;
            waiting = false;
            return failed ? ManualSaveDecision.CancelManualSave : ManualSaveDecision.ResumeManualSave;
        }
    }

    // C++: LeaderboardSavePolicy.h:108
    public static bool NativeDispatchReady(int delay, bool filesystemIdle) { return delay <= 0 && filesystemIdle; }
}
