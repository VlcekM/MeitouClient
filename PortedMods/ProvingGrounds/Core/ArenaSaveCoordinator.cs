// Port of upstream/ProvingGrounds/plugin/src/ArenaSaveCoordinator.h (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

public static partial class ArenaPersistence
{
    /// <summary>C++: ArenaSaveCoordinator.h:6-26 (header-only). Engine independent transaction. Caller serializes access across
    /// Begin/Finish/Abandon; neither gameplay nor save request/dispatch writes any storage. PA §2.20.
    /// <para>Holds the storage by reference (<c>Storage&amp; files</c>) and its own copy of the snapshot: <c>captured=snapshot</c> is a
    /// deep <see cref="Snapshot.AssignFrom"/> into the held object (as the C++ assigns into the member), so later edits to the caller's snapshot do not reach the publish.</para></summary>
    public sealed class SaveCoordinator
    {
        readonly IStorage files;
        Snapshot captured;
        string target, error;
        bool pending;

        // C++: ArenaSaveCoordinator.h:12 (captured, target and error default-constructed)
        public SaveCoordinator(IStorage storage) { files = storage; captured = new Snapshot(); target = ""; error = ""; pending = false; }

        // C++: ArenaSaveCoordinator.h:13-16
        public void BeginSave(string destination, string generation, Snapshot snapshot)
        {
            captured.AssignFrom(snapshot); captured.Generation = generation; target = destination;
            error = ""; pending = target.Length != 0 && generation.Length != 0;
        }

        // C++: ArenaSaveCoordinator.h:17
        public void SetSaveKey(string key) { captured.SaveKey = key; }

        // C++: ArenaSaveCoordinator.h:18-23
        public bool FinishSave(bool nativeSucceeded)
        {
            if (!pending) return false;
            pending = false;
            if (!nativeSucceeded) { error = "Native save did not complete successfully"; return false; }
            return PublishSnapshot(files, target, captured, ref error);
        }

        // C++: ArenaSaveCoordinator.h:24 (error is kept, as in the C++)
        public void AbandonWorld() { pending = false; target = ""; captured.AssignFrom(new Snapshot()); }

        // C++: ArenaSaveCoordinator.h:25
        public string Error() { return error; }

        // ---- test seams (private in the C++; the golden reads them with #define class struct + private public) ----
        internal bool PendingForTest => pending;
        internal string TargetForTest => target;
        /// <summary>Read only: the held capture itself (tests must not write through it).</summary>
        internal Snapshot CapturedForTest => captured;
    }
}
