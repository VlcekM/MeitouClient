namespace ProvingGrounds.Core.Glue;

/// <summary>
/// The arena state of one world and its save decisions (task 2.4), without game types: the Core ledger with LeaderboardStore's
/// activate / block (LeaderboardStore.cpp:164-182), the load outcomes of ArenaNativePersistence.cpp's <c>ActivateLoad</c> (:325-395) for the
/// data Meitou kept in the save, the save capture of <c>Save</c> (:197-234), the reset of <c>ResetUnmatchedSidecar</c> (:421-433), and the
/// debug setters <c>SetRating</c> / <c>SetMarks</c> (LeaderboardStore.cpp:369-395, 506-529). The glue (src/Game/ArenaNativePersistence.cs)
/// feeds it bytes, the world's "loaded from a save" fact and identity tokens, and does the I/O.
/// <para>Every ledger change Read decides is kept as pending and applied by the first <see cref="Resolve"/> (a serial step) or by
/// <see cref="TryWrite"/>, so Read changes no live state (CAP 9.1).</para>
/// <para>Every arena write and read checks <see cref="Ready"/> first (the store, task 5.1: Core/Glue/LeaderboardStoreLogic.cs; M7's town schedule must too) (C++ <c>g_ledger.Ready()</c> /
/// <c>LeaderboardStore::HasActiveSave</c>): a blocked world (unreadable saved data) refuses ratings, marks, rewards and the town schedule,
/// as the C++ does ("rating skip - arena ledger unavailable", LeaderboardStore.cpp:218-222).</para>
/// </summary>
public sealed class ArenaWorldState
{
    /// <summary>What the debug setters did.</summary>
    public enum SetResult
    {
        /// <summary>The row was written.</summary>
        Updated,
        /// <summary>The ledger is not ready (blocked); nothing written, no identity created.</summary>
        NotReady,
        /// <summary>No fighter identity for the character; nothing written.</summary>
        NoIdentity,
    }

    readonly ArenaLedger.Ledger ledger = new();
    readonly Action<string> log;
    readonly Action<string> ownLog;   // the PG: lines (port only)
    readonly Func<byte[], string?> recover;
    readonly Func<byte[], ArenaSaveData.ReadResult> decode;
    // C++: LeaderboardStore.cpp:35 (g_persistenceError's initial text; ActivateSnapshot clears it, BlockPersistence sets it)
    string persistenceError = "Arena world state has not been activated.";
    bool resetAllowed;
    bool resolved;
    Action? pending;
    string notice = "";
    byte[]? legacyOriginal;

    /// <param name="log">Receives whole log lines (the C++ <c>PGLog::Debug</c> lines and the task's <c>PG:</c> line).</param>
    /// <param name="recover">Keeps bytes in the mod's <c>recovered/</c> folder; returns the file's path, or null when it could not (a throw
    /// counts as null).</param>
    /// <param name="decode">The decoder (a test seam; <see cref="ArenaSaveData.Read"/>).</param>
    /// <param name="ownLog">Receives the port's own <c>PG:</c> lines (no C++ counterpart; task 2.7: Meitou's log); null = <paramref name="log"/>.</param>
    public ArenaWorldState(Action<string> log, Func<byte[], string?> recover, Func<byte[], ArenaSaveData.ReadResult>? decode = null,
        Action<string>? ownLog = null)
    {
        this.log = log;
        this.ownLog = ownLog ?? log;
        this.recover = recover;
        this.decode = decode ?? (b => ArenaSaveData.Read(b));
        // The state of a world loaded from a save without arena data (ActivateLoad's Missing branch, ArenaNativePersistence.cpp:381-384:
        // a default Snapshot, card seed 1). Resolve turns it into the new-game state when the world did not come from a save.
        ActivateSnapshot(new ArenaPersistence.Snapshot());
    }

    /// <summary>The world's ledger (C++ <c>g_ledger</c>, LeaderboardStore.cpp:30).</summary>
    public ArenaLedger.Ledger Ledger => ledger;

    /// <summary>C++ <c>TownArena::CapturePlannedNpcBout(snapshot.plannedNpc)</c> (ArenaNativePersistence.cpp:206, task 7.3): fills the save's
    /// planned bout; false = the arena part of the save is not written (the same report as a ledger that cannot be captured). Null: the ledger's.</summary>
    public Func<ArenaPersistence.PlannedNpcBout, bool>? CapturePlannedNpc { get; set; }

    /// <summary>C++ <c>TownArena::RestorePlannedNpcBout(snapshot.plannedNpc)</c> (ArenaNativePersistence.cpp:360-361, task 7.3): called with a loaded
    /// snapshot's planned bout right after it is activated (the world's first serial step).</summary>
    public Action<ArenaPersistence.PlannedNpcBout>? RestorePlannedNpc { get; set; }

    /// <summary>C++ <c>TownDiagnosticRun::RecoverPendingAfterLoad()</c> (ArenaNativePersistence.cpp:363, task 8.7a): called once with a loaded
    /// snapshot that has no active announced bout, right after the bout's restore. Its argument is the sink for the run's balance-log event
    /// ("diagnostic_assignment_cancelled"): the recover writes the run's state at once (a save then holds it) and gives its event to that sink,
    /// which writes it as <see cref="DiagnosticEvent"/> says. Null: nothing.</summary>
    public Action<Action<string, string>>? RecoverDiagnosticAfterLoad { get; set; }

    /// <summary>C++ <c>CombatBalanceLog::DiagnosticEvent(name, fields)</c> of the load (ArenaNativePersistence.cpp:370, and the recover's, task 8.7a).
    /// The line reads the world's game hours and save key when it is written (the glue's combat log host is bound to the world at the start of
    /// each serial step), so it is written only inside <see cref="Resolve"/>. A loaded snapshot applied before the world's first serial step (by a
    /// save, <see cref="TryWrite"/>) keeps its events for the next Resolve: written then, in their order, after the held bout's restore, once.
    /// Null: nothing.</summary>
    public Action<string, string>? DiagnosticEvent { get; set; }

    // C++: ArenaNativePersistence.cpp:369-370 (the event's name and its fields text)
    const string PersistenceErrorEvent = "diagnostic_persistence_error";
    const string PersistenceErrorFields = ",\"error\":\"invalid diagnostic state; see arena persistence log\"";

    /// <summary>The load's diagnostic events held for the next <see cref="Resolve"/> (see <see cref="DiagnosticEvent"/>).</summary>
    readonly List<(string Name, string Fields)> heldDiagnosticEvents = [];

    /// <summary>The sink of the load's diagnostic events: written now inside Resolve, else held for the next Resolve.</summary>
    void DiagnosticEventNowOrHeld(string name, string fields)
    {
        if (inResolve) DiagnosticEvent?.Invoke(name, fields);
        else heldDiagnosticEvents.Add((name, fields));
    }

    /// <summary>The standings and progression tables (C++ <c>g_data</c>, LeaderboardStore.cpp:31): one object for the whole world (Activate
    /// and Block copy into it).</summary>
    public LeaderboardData.Collections Data => ledger.State().Fighters;

    /// <summary>C++ <c>LeaderboardStore::HasActiveSave</c> (:675-679) = <c>g_ledger.Ready()</c>.</summary>
    public bool Ready => ledger.Ready();

    /// <summary>C++ <c>LeaderboardStore::GetPersistenceError</c> (:172): why the ledger is blocked, "" when it is active.</summary>
    public string PersistenceError => persistenceError;

    /// <summary>C++ <c>CanResetUnmatchedSidecar</c> (:420): only for a WrongSnapshot load (:389-393), which a Meitou save cannot produce
    /// (nothing is matched); kept as the C++ has it.</summary>
    public bool ResetOffered => resetAllowed;

    // C++: ArenaNativePersistence.cpp:65-68
    void Log(string text) => log("Proving Grounds persistence: " + text);

    // C++: ArenaNativePersistence.cpp:69 (log, and keep the text for the next Tick)
    void Report(string text) { Log(text); notice = text; }

    /// <summary>The report the C++ <c>Tick</c> shows as a player message (ArenaNativePersistence.cpp:434), once.</summary>
    public string TakeNotice() { string n = notice; notice = ""; return n; }

    /// <summary>C++: LeaderboardStore.cpp:175-182 <c>ActivateSnapshot</c>: the ledger takes the snapshot (a deep copy), the buy-in account starts
    /// over with the snapshot's credit, the once-per-character identity log starts over (<see cref="LedgerSwitches"/>), the error is cleared.</summary>
    public void ActivateSnapshot(ArenaPersistence.Snapshot snapshot)
    {
        ledger.Activate(snapshot);
        challengeAccount = new TownChallengeBuyInPolicy.Account();
        challengeAccount.Credit = snapshot.ChallengeCredit;
        ++ledgerSwitches;   // g_missingPersistentIdentityLogged.clear()
        persistenceError = "";
    }

    /// <summary>C++: LeaderboardStore.cpp:164-170 <c>BlockPersistence</c>: the ledger blocked and emptied, the buy-in account reset, the
    /// identity log started over, <paramref name="reason"/> kept as the error. (<c>Unload</c>, :173, is this with "Arena world state is
    /// unloaded."; Meitou builds a new system per world, so nothing calls it.)</summary>
    public void BlockPersistence(string reason)
    {
        ledger.Block();
        challengeAccount = new TownChallengeBuyInPolicy.Account();
        ++ledgerSwitches;   // g_missingPersistentIdentityLogged.clear()
        persistenceError = reason;
    }

    // C++: LeaderboardStore.cpp:33 (g_challengeAccount; held beside the ledger, as the C++ keeps it outside the snapshot)
    TownChallengeBuyInPolicy.Account challengeAccount;
    TownChallengeBuyInPolicy.Account unavailableAccount;
    int ledgerSwitches;

    /// <summary>How many times the ledger was activated or blocked: the C++ clears its once-per-character identity log
    /// (<c>g_missingPersistentIdentityLogged</c>) at both (LeaderboardStore.cpp:168, :180); the store compares this count.</summary>
    public int LedgerSwitches => ledgerSwitches;

    /// <summary>C++: LeaderboardStore.cpp:150-156 <c>GetChallengeAccount</c>: the held account while Ready, else a scratch account reset to
    /// the default at each call (writes to it are lost, as writes to the C++ static).</summary>
    public ref TownChallengeBuyInPolicy.Account ChallengeAccount()
    {
        if (ledger.Ready()) return ref challengeAccount;
        unavailableAccount = new TownChallengeBuyInPolicy.Account();
        return ref unavailableAccount;
    }

    /// <summary>C++: LeaderboardStore.cpp:184-192 <c>CaptureSnapshot</c>: the ledger's copy into <paramref name="output"/> (false, untouched,
    /// when not Ready or a purchase is reserved), the challenge credit from the buy-in account, every booked offer cancelled.</summary>
    public bool CaptureSnapshot(ArenaPersistence.Snapshot output) =>
        ArenaSaveData.CaptureInto(ledger, output, TownChallengeBuyInPolicy.SaveCredit(challengeAccount));

    /// <summary>
    /// C++: ArenaNativePersistence.cpp:325-395 <c>ActivateLoad</c> for the bytes Meitou kept in the save; decided now, applied by the first
    /// <see cref="Resolve"/> / <see cref="TryWrite"/>. Returns the decoded result (the glue restores its identity tokens).
    /// <list type="bullet">
    /// <item>Ready: the snapshot is activated, its announced bout restored, a pending diagnostic assignment cancelled when it has no announced bout
    /// (task 8.7a), the "Loaded arena snapshot" line written (:351-367), then for a diagnostic error its balance-log event and report (:368-372).</item>
    /// <item>LegacyReset: a default snapshot (seed 1) and the C++ "Legacy arena progress reset" report (:381-385); the original bytes go
    /// to <c>recovered/</c> at the next successful save, which is what the report says.</item>
    /// <item>A throw anywhere (fail closed, <see cref="ReadFailed"/>): the same as anything else, reason <c>read failed: Type: message</c>.</item>
    /// <item>Anything else: the ledger is BLOCKED (:387-395, LeaderboardStore.cpp:164-170) with <c>&lt;recovered file&gt; field: message</c>
    /// (the C++ uses the sidecar's path), the C++ "Arena progress blocked" report, and the task's <c>PG: saved arena data unreadable</c>
    /// line; the bytes are kept in <c>recovered/</c> now. The reset is offered only for WrongSnapshot (:390-393).</item>
    /// </list>
    /// </summary>
    public ArenaSaveData.ReadResult Read(byte[] bytes)
    {
        try { return Decide(bytes); }
        catch (Exception e)
        {
            ReadFailed(bytes, e);
            return new ArenaSaveData.ReadResult { Kind = ArenaSaveData.ReadKind.Unreadable, Reason = FailedReason(e) };
        }
    }

    static string FailedReason(Exception e) => "read failed: " + e.GetType().Name + ": " + e.Message;

    /// <summary>
    /// Fail CLOSED: anything unexpected while reading the saved data (a throw from the stream, the decoder, the identity restore or the
    /// recover callback) takes the unreadable branch, so a world whose data could not be read never stays Ready with a default ledger that the
    /// next save would write over the real data. <paramref name="bytes"/> = what was read before the throw (may be empty).
    /// </summary>
    public void ReadFailed(byte[] bytes, Exception e)
    {
        string reason = FailedReason(e);
        legacyOriginal = null;
        resetAllowed = false;
        Unreadable(bytes, reason);
    }

    string? TryRecover(byte[] bytes)
    {
        try { return recover(bytes); }
        catch (Exception e) { ownLog("PG: recovered data not kept: " + e.GetType().Name + ": " + e.Message); return null; }
    }

    // C++: ArenaNativePersistence.cpp:387-395 (ActivateLoad's last branch: block and report)
    void Unreadable(byte[] bytes, string reason)
    {
        ownLog("PG: saved arena data unreadable: " + reason);
        string where = (bytes.Length != 0 ? TryRecover(bytes) : null) ?? "the saved arena data";
        string error = where + " " + reason;   // C++ :387 path+" "+loaded.field+": "+loaded.message
        pending = () => BlockPersistence(error);
        Report("Arena progress blocked: " + error + ". Preserve the file/backups or use the leaderboard's explicit reset to start arena progress from zero for this slot.");
    }

    ArenaSaveData.ReadResult Decide(byte[] bytes)
    {
        ArenaSaveData.ReadResult result = decode(bytes);
        switch (result.Kind)
        {
            case ArenaSaveData.ReadKind.Loaded:
            {
                ArenaPersistence.Snapshot snapshot = result.Snapshot;
                pending = () =>
                {
                    ActivateSnapshot(snapshot);   // :360
                    RestoreOrHold(snapshot.PlannedNpc);   // :361-362 TownArena::RestorePlannedNpcBout (task 7.3; Meitou has no import)
                    if (!snapshot.PlannedNpc.Active) RecoverDiagnosticAfterLoad?.Invoke(DiagnosticEventNowOrHeld);   // :363 (task 8.7a)
                    Log("Loaded arena snapshot from the save generation=" + snapshot.Generation);   // :365-367
                    // :368-372 (task 8.7a): the event and the Report in the applied step, after the "Loaded" line, as the C++ orders them
                    if (snapshot.DiagnosticError.Length != 0)
                    {
                        DiagnosticEventNowOrHeld(PersistenceErrorEvent, PersistenceErrorFields);
                        Report("Diagnostic selection disabled: " + snapshot.DiagnosticError + ". Ordinary arena matchmaking remains active.");
                    }
                };
                break;
            }
            case ArenaSaveData.ReadKind.LegacyReset:
                legacyOriginal = bytes;
                pending = () => ActivateSnapshot(new ArenaPersistence.Snapshot());
                Report("Legacy arena progress reset. Original JSON will be backed up when this slot is next saved.");
                break;
            default:
                if (result.Code == ArenaPersistence.LoadCode.WrongSnapshot) resetAllowed = true;   // :389-393 (never in a Meitou save)
                Unreadable(bytes, result.Reason);
                break;
        }
        return result;
    }

    /// <summary>The first serial step of the world (and every later one): applies what Read decided; with no Read (a new game, or a save
    /// made without the mod), a new game gets the C++ new-game state (<c>CompleteWorldTransition(true)</c>, LeaderboardStore.cpp:194-200:
    /// card seed <c>GetTickCount() | 1u</c>), a world loaded from a save keeps the default snapshot of the Missing branch (seed 1).</summary>
    /// <param name="loadedFromSave">The world came from a save (Meitou: <c>World.Saved</c> is set).</param>
    /// <param name="tickCount">GetTickCount() at this moment.</param>
    public void Resolve(bool loadedFromSave, uint tickCount)
    {
        inResolve = true;
        try
        {
            if (ApplyPending()) { resolved = true; return; }
            if (resolved) return;
            resolved = true;
            if (!loadedFromSave) ActivateSnapshot(ArenaSaveData.FreshSnapshot(tickCount));
        }
        finally
        {
            inResolve = false;
            if (lateRestore is { } held && RestorePlannedNpc is { } restore)
            {
                lateRestore = null;
                ownLog("PG: the loaded announced town bout is restored now, at the world's first serial step (its snapshot was applied earlier, by a save)");
                restore(held);
            }
            if (heldDiagnosticEvents.Count != 0)   // task 8.7a: the load's diagnostic events of a snapshot a save applied earlier, once, in order
            {
                var events = heldDiagnosticEvents.ToArray();
                heldDiagnosticEvents.Clear();
                foreach (var (name, fields) in events) DiagnosticEvent?.Invoke(name, fields);
            }
        }
    }

    bool inResolve;
    /// <summary>A loaded snapshot's announced bout whose snapshot was applied outside <see cref="Resolve"/> (a save before the world's first serial
    /// step: TryWrite applies what Read decided). Held for the next Resolve (TownArena needs a world), and written back by a save in between, as the
    /// C++ captures a restored bout still pending (TownArena.cpp:597).</summary>
    ArenaPersistence.PlannedNpcBout? lateRestore;

    /// <summary>Test seam: the held bout, if any.</summary>
    public bool HoldsLateRestore => lateRestore is not null;

    void RestoreOrHold(ArenaPersistence.PlannedNpcBout planned)
    {
        if (inResolve || RestorePlannedNpc is null) { RestorePlannedNpc?.Invoke(planned); return; }
        lateRestore = planned.Clone();
        ownLog("PG: a loaded snapshot was applied before the world's first serial step (by a save); its announced town bout waits for that step");
    }

    bool ApplyPending()
    {
        if (pending is null) return false;
        var apply = pending;
        pending = null;
        apply();
        return true;
    }

    /// <summary>
    /// C++: ArenaNativePersistence.cpp:197-234 (Save) with LeaderboardStore.cpp:184-192 (CaptureSnapshot) and :173-196 (FinishIfReady): the
    /// payload for this save, or false with a report when the arena part cannot be written. A blocked ledger cannot be captured, so a
    /// blocked world never writes a fresh ledger over the data the save had: the caller then leaves that data in place.
    /// After a successful write, the original bytes of a legacy snapshot are kept in <c>recovered/</c> (the LegacyReset report's promise).
    /// </summary>
    public bool TryWrite(string generation, IEnumerable<string> tokens, out byte[] payload, out string error)
    {
        ApplyPending();
        payload = [];
        ArenaPersistence.Snapshot? snapshot = ArenaSaveData.Capture(ledger, generation, TownChallengeBuyInPolicy.SaveCredit(challengeAccount));
        // :206 TownArena::CapturePlannedNpcBout(snapshot.plannedNpc) (task 7.3): the announced bout, or the arena part is not written
        if (snapshot is not null && lateRestore is { } held) snapshot.PlannedNpc.AssignFrom(held);   // the held bout, as the C++'s RestoredNpcPending branch (TownArena.cpp:597)
        else if (snapshot is not null && CapturePlannedNpc is { } capturePlanned && !capturePlanned(snapshot.PlannedNpc)) snapshot = null;
        if (snapshot is null)
        {
            // C++: ArenaNativePersistence.cpp:206-209
            error = persistenceError.Length != 0 ? persistenceError
                : "the arena ledger, announced lineup identity, or snapshot generation could not be captured";
            Report("Arena portion of save unavailable: " + error + ". Wait for any reward delivery to finish, then retry saving. Native game save will continue; arena data will not be published.");
            return false;
        }
        if (!ArenaSaveData.TryWrite(snapshot, tokens, out payload, out error))
        {
            // C++: ArenaNativePersistence.cpp:193-195 (without the C++ " at <sidecar path>": there is no sidecar)
            Report("Arena portion of save FAILED: " + error + ". Live arena progress is retained. Preserve this slot and its backups; retry saving before leaving this world.");
            return false;
        }
        if (legacyOriginal is { } original)
        {
            // The LegacyReset report promises the original is backed up by this save: a failed copy fails the arena part of the save (the
            // C++ publish fails when its backup does, ArenaSnapshotRepository.cpp:175-176), so Meitou keeps the old bytes and the next save retries.
            if (TryRecover(original) is null)
            {
                payload = [];
                error = "Could not back up the legacy arena snapshot";
                Report("Arena portion of save FAILED: " + error + ". Live arena progress is retained. Preserve this slot and its backups; retry saving before leaving this world.");
                return false;
            }
            legacyOriginal = null;
        }
        return true;
    }

    /// <summary>C++: LeaderboardStore.cpp:369-395 <c>SetRating</c> (the debug menu's rating setter, DebugMenu.cpp:398-430): refused when the
    /// ledger is not ready (before any identity is made); <paramref name="key"/> is asked only then (C++ <c>CharacterKey(character, true)</c>,
    /// null = no identity); the rating is clamped to 0..9999 and written into the character's player-board row.</summary>
    public SetResult SetRating(Func<string?> key, string name, float rating)
    {
        if (!ledger.Ready()) return SetResult.NotReady;
        string? id = key();
        if (string.IsNullOrEmpty(id)) return SetResult.NoIdentity;
        if (rating < 0.0f) rating = 0.0f;
        if (rating > 9999.0f) rating = 9999.0f;
        LeaderboardData.EnsureStanding(Data, LeaderboardData.Kind.Player, id, name).Mmr = rating;
        log("Proving Grounds: debug rating updated");
        return SetResult.Updated;
    }

    /// <summary>C++: LeaderboardStore.cpp:506-529 <c>SetMarks</c> (the debug menu's marks setter, DebugMenu.cpp:432-445): as
    /// <see cref="SetRating"/>, clamped to 0..9999, into the character's progression row.</summary>
    public SetResult SetMarks(Func<string?> key, string name, int marks)
    {
        if (!ledger.Ready()) return SetResult.NotReady;
        string? id = key();
        if (string.IsNullOrEmpty(id)) return SetResult.NoIdentity;
        if (marks < 0) marks = 0;
        if (marks > 9999) marks = 9999;
        LeaderboardData.EnsureProgression(Data, id, name).Marks = marks;
        log("Proving Grounds: debug Arena Marks updated");
        return SetResult.Updated;
    }

    /// <summary>C++: ArenaNativePersistence.cpp:421-433 <c>ResetUnmatchedSidecar</c>: only while offered (a WrongSnapshot load); a fresh
    /// ledger, the choice withdrawn. (The C++ re-reads the sidecar first to confirm it is still a wrong snapshot; inside a save it cannot
    /// change.)</summary>
    public bool ResetUnmatched()
    {
        if (!resetAllowed) return false;
        ApplyPending();
        resetAllowed = false;
        ActivateSnapshot(new ArenaPersistence.Snapshot());
        Log("Player explicitly reset unmatched arena progress; save this slot now to publish a matching sidecar.");
        return true;
    }

    // ---- test seams ----
    internal void SetWrongSnapshotForTest() { resetAllowed = true; }
}
