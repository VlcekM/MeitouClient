using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port of upstream/ProvingGrounds/plugin/src/ArenaNativePersistence.h/.cpp (Proving Grounds by Shutori, GPLv3), task 2.4 (PA §2.20, CAP 9.1),
// with the world hooks of LeaderboardStore.cpp it calls (BlockPersistence :164-170, ActivateSnapshot :175-182, CaptureSnapshot :184-192,
// CompleteWorldTransition :194-200). The decisions are Core/Glue/ArenaWorldState.cs (tested); this file does the I/O.
// Meitou 0.17.3 (tag v0.17.3, fa34a1b).
//
// Engine calls of the C++ and what replaces them (E1):
//   hooks SaveManager::saveGame / loadGame / importGame, GameDataContainer::save / load, SaveFileSystem::saveGame / loadGame / sync
//       (InstallHooks :436-450) ... none: IModSaveData on ProvingGroundsSystem. Meitou calls Write on every save from GameSaves.QuickSaveNow
//       -> GameMods.CaptureModData -> ModHost.CaptureSaveData (src/Meitou.Game/Host/GameSaves.cs:223, src/Meitou.Game/Mods/GameMods.cs:118-123,
//       src/Meitou.Game/Mods/ModHost.cs:405-418), and Read after SaveLoader.Load, before the first tick, only when the save holds our data
//       (GameSaves.cs:456-457, GameMods.cs:162, ModHost.cs:423-441). Both "run between ticks on the thread that ticks the world"
//       (src/Meitou.Game/Mods/IMod.cs:17-21; HandleSaves runs once per frame between ticks, GameSaves.cs:50-54), so no SlowWorld runs at the
//       same time; Write still encodes a deep copy (ArenaSaveData.Capture) and Meitou writes the bytes to disk on a worker (GameSaves.cs:234).
//   GameData::sdata / addString of "proving_grounds.generation" in the CAMERA record (WorldMetadata, ReadGeneration, ContainerSave
//       :142-160, :243-256) ... none: the data is inside the save, so nothing has to be matched; the snapshot's generation is still a new
//       UUID per save (Guid.NewGuid, "D" = UuidToStringA's lowercase 8-4-4-4-12) for the format
//   UuidCreate / UuidToStringA (NewGeneration :137-141) ... Guid.NewGuid().ToString("D")
//   SaveFileSystem::state / isRunning / currentSave / failedToCopyError, NativeSaveEvidence, NativeSyncBoundary (Sync :262-305) ... none:
//       Meitou hands Write a stream and stores the bytes as they are (IMod.cs:24-27); a Write that throws keeps the file the save had
//       (ModHost.cs:403-404, 413-416)
//   ArenaSnapshotFiles (sidecar, backups, emergency_save_* search :84-136) ... none (port map: dropped)
//   ou->showPlayerAMessage(text, true) (Tick :434) ... PgEngine.Message (world.Post)
//   PGLog::Debug("Proving Grounds persistence: ...") (Log :65-68) ... the mod log (task 2.7 routes it to PGLog.Debug)
//   GetTickCount() (new-game card seed, LeaderboardStore.cpp:198) ... Environment.TickCount (the same millisecond counter, GetTickCount64)
//   "is this a new game" (WorldLifecycle.cpp:216-225: SaveManager::execute dispatched NEWGAME -> CompleteWorldTransition(true)) ... World.Saved,
//       "null: a world that started new" (src/Meitou.Simulation/World.cs:321-324), set only by SaveLoader.Load (src/Meitou.Simulation/Saving/
//       SaveLoader.cs:241; the one caller, GameSaves.cs:456) and never cleared: PgEngine.LoadedFromSave, read at the world's first serial step
//
// C++ function -> port:
//   Save (:197-234) + FinishIfReady (:173-196)  Write: capture (LeaderboardStore::CaptureSnapshot), a new generation, encode, write. A capture
//                                               that fails (a BLOCKED ledger, or a purchase reserved) reports the C++ "Arena portion of save
//                                               unavailable" text and throws ArenaDataNotWritten ON PURPOSE: Meitou then keeps the arena data the
//                                               save had (ModHost.cs:410-416 leaves files[mod] as it was; the capture base is a deep copy of the
//                                               loaded save, SaveCapture.cs:86, which copies ModData, src/Meitou.Data/Save/SaveGame.cs:285), as
//                                               the C++ leaves the old sidecar in place. Meitou logs "save data not written" to stderr
//   Load (:396-401) + ActivateLoad (:325-395)   Read: the bytes decoded now, applied in the first serial step (ApplyPending; CAP 9.1 trap).
//                                               Ready -> ActivateSnapshot, the announced bout's restore (WireTownBout, task 7.3), the
//                                               diagnostic run's RecoverPendingAfterLoad when no bout was announced, the "Loaded arena
//                                               snapshot" line, then for a diagnostic error its balance-log event and report
//                                               (WireDiagnosticRun, task 8.7a); LegacyReset -> a default
//                                               snapshot + the C++ "Legacy arena progress reset" report, the original kept in recovered/ at
//                                               the next save; anything else (bad frame, InvalidJson, InvalidData, UnsupportedVersion) ->
//                                               "PG: saved arena data unreadable: <reason>", the bytes kept in <mod folder>/recovered/<utc>.dat now,
//                                               the ledger BLOCKED (BlockPersistence) and the C++ "Arena progress blocked" report (the
//                                               recovered file's path where the C++ names the sidecar). No reset offered: the C++ offers it
//                                               only for WrongSnapshot (:389-393), which a Meitou save cannot produce
//   Import (:402-414) + PrepareImportedSnapshot  none: Meitou's import does not carry mod data (CAP 9.1); a fresh ledger. Importing an
//                                               RE_Kenshi sidecar is optional task 9.7
//   no Read (a save made before the mod)        the default snapshot of ActivateLoad's Missing branch (:381-384, card seed 1) (CAP 9.1)
//   new game (CompleteWorldTransition(true))     the first serial step of a world with no World.Saved: card seed GetTickCount() | 1u.
//                                               A save made before that first step writes the seed-1 default (no world reaches Write)
//   CanResetUnmatchedSidecar / ResetUnmatchedSidecar (:420-433)  ResetOffered / ResetArenaProgress (task 5.x's leaderboard calls them;
//                                               only ever offered for WrongSnapshot, kept as the C++ has it)
//   Tick (:434)                                 Tick: the pending report as a player message
//   AbandonWorld / Disable / IsLoading / IsReady  none: one object per world, nothing to abandon; always ready
//   ContainerSave / FilesystemSave / ContainerLoad / FilesystemLoad  none (no native save pipeline to watch)
//
// Rule copied from the original (ArenaSaveCoordinator.h:7-8, PA §2.20): gameplay never writes anything to disk; only a save does (through
// Meitou). The only files this class writes itself are the recovered copies: unreadable data during a load, a legacy snapshot during the
// next save; never in gameplay.
//
// Every arena read and write checks Ready first (C++ g_ledger.Ready() / LeaderboardStore::HasActiveSave): a blocked world refuses ratings
// ("Proving Grounds: rating skip - arena ledger unavailable", LeaderboardStore.cpp:218-222), marks, rewards and the town schedule. The store
// (src/Game/LeaderboardStore.cs, task 5.1) reads and writes this ledger through State; the town schedule checks it too (PlanNpcBout, HasActiveSave: task 7.3).
//
// Fighter identity: Write saves FighterIdentity.RegisteredSorted() after the snapshot JSON (ArenaSaveData's layout); Read gives them back
// with FighterIdentity.Restore at once (it looks up no character).

/// <summary>The arena data of one world inside the Meitou save (task 2.4): owns the <see cref="ArenaWorldState"/> (the Core ledger: standings,
/// progression, unlocks, town card, credits and planned bout; LeaderboardStore.cpp:30-35 keeps references into it, task 5.1's src/Game/LeaderboardStore.cs wraps it in the
/// full store) and does its I/O. One object per world, owned by <see cref="ProvingGroundsSystem"/>. Sim thread only (serial steps, and
/// Write/Read between ticks).</summary>
sealed class ArenaNativePersistence
{
    /// <summary>C++ LeaderboardUI.cpp:566 (and :82, :677, :718): the caption of the leaderboard's reset button, shown while
    /// <see cref="ResetOffered"/>. The leaderboard window is task 5.x.</summary>
    internal const string ResetChoiceCaption = "Reset arena progress";

    /// <summary>C++ LeaderboardUI.cpp:75: the button's caption after the first click (the second click resets).</summary>
    internal const string ResetConfirmCaption = "Confirm reset to zero";

    /// <summary>Thrown by <see cref="Write"/> on purpose when the arena part cannot be written: Meitou then keeps the arena data the save had
    /// (header).</summary>
    internal sealed class ArenaDataNotWritten(string message) : Exception(message)
    {
        // Meitou prints the whole exception to stderr on every refused save (ModHost.cs:415); a blocked world refuses every autosave, so
        // print only the message, no stack.
        public override string? StackTrace => null;
        public override string ToString() => GetType().Name + ": " + Message;
    }

    readonly ArenaWorldState state;
    readonly FighterIdentity identity;
    readonly Action<string> log;   // the port's own PG: lines (no C++ counterpart): Meitou's log (task 2.7)
    readonly string? modFolder;

    /// <param name="modFolder">The mod's folder (<c>ProvingGroundsMod.Ctx.Folder</c>), where <c>recovered/</c> goes; null in no world.</param>
    /// <param name="cppLog">The C++ ArenaNativePersistence / LeaderboardStore lines (PGLog::Debug, ArenaNativePersistence.cpp:57-60).</param>
    /// <param name="ownLog">The port's own <c>PG:</c> lines (no C++ counterpart).</param>
    internal ArenaNativePersistence(FighterIdentity identity, Action<string> cppLog, Action<string> ownLog, string? modFolder)
    {
        this.identity = identity;
        log = ownLog;
        this.modFolder = modFolder;
        state = new ArenaWorldState(cppLog, Recover, ownLog: ownLog);
    }

    static uint TickCount() => unchecked((uint)Environment.TickCount);

    /// <summary>The world's state (ledger, Ready, the debug setters).</summary>
    internal ArenaWorldState State => state;

    /// <summary>The world's ledger (C++ <c>g_ledger</c>, LeaderboardStore.cpp:30).</summary>
    internal ArenaLedger.Ledger Ledger => state.Ledger;

    /// <summary>The standings and progression tables (C++ <c>g_data</c>, LeaderboardStore.cpp:31): one object for the whole world.</summary>
    internal LeaderboardData.Collections Data => state.Data;

    /// <summary>C++ <c>HasActiveSave</c>: false while the saved arena data was unreadable (blocked).</summary>
    internal bool Ready => state.Ready;

    /// <summary>Why the arena is blocked ("" when it is active).</summary>
    internal string PersistenceError => state.PersistenceError;

    /// <summary>C++ <c>CanResetUnmatchedSidecar</c> (:420): only for a WrongSnapshot load, which a Meitou save cannot produce.</summary>
    internal bool ResetOffered => state.ResetOffered;

    /// <summary>The first serial step decides the world's state (ArenaWorldState.Resolve); every step after applies nothing new.</summary>
    internal void Resolve(World world)
    {
        resolving = world;
        try { state.Resolve(PgEngine.LoadedFromSave(world), TickCount()); }
        finally { resolving = null; }
    }

    World? resolving;   // the world of the Resolve that may apply a loaded snapshot (the restore hook's world)

    /// <summary>Task 7.3 (ArenaNativePersistence.cpp:205-206, :360-361): TownArena's announced bout, captured into every save and restored with a
    /// loaded snapshot. <paramref name="capture"/> gets the world of the last serial step (null before the first).</summary>
    internal void WireTownBout(Func<ArenaPersistence.PlannedNpcBout, bool> capture, Action<World, ArenaPersistence.PlannedNpcBout> restore)
    {
        state.CapturePlannedNpc = capture;
        // ArenaWorldState calls this only inside Resolve (a snapshot applied earlier, by a save, holds its bout until then and a save in between
        // writes that bout back: ArenaWorldState.RestoreOrHold / TryWrite), so the world is the Resolve's.
        state.RestorePlannedNpc = p => { if (resolving is { } w) restore(w, p); };
    }

    /// <summary>Task 8.7a (ArenaNativePersistence.cpp:363, :368-371): the loaded snapshot's diagnostic run. <paramref name="recover"/> =
    /// <c>TownDiagnosticRun::RecoverPendingAfterLoad</c> with the load's event sink (the world's run, src/Town/TownDiagnosticRun.cs);
    /// <paramref name="diagnosticEvent"/> = <c>CombatBalanceLog::DiagnosticEvent</c> over the world's combat log host. ArenaWorldState decides
    /// when each is called (written inside Resolve, after the system bound the combat log host to the world; held from a save before the
    /// world's first serial step until then).</summary>
    internal void WireDiagnosticRun(Action<Action<string, string>> recover, Action<string, string> diagnosticEvent)
    {
        state.RecoverDiagnosticAfterLoad = recover;
        state.DiagnosticEvent = diagnosticEvent;
    }

    /// <summary>C++: ArenaNativePersistence.cpp:434 <c>Tick</c>: the pending report as a player message (WorldLifecycle.cpp:284 calls it each frame).</summary>
    internal void Tick(World world)
    {
        Resolve(world);
        string text = state.TakeNotice();
        if (text.Length != 0) PgEngine.Message(world, text);
    }

    /// <summary>IModSaveData.Write (header): the <see cref="ArenaSaveData"/> payload, or <see cref="ArenaDataNotWritten"/> on purpose.</summary>
    internal void Write(Stream stream)
    {
        if (!state.TryWrite(Guid.NewGuid().ToString("D"), identity.RegisteredSorted(), out byte[] payload, out string error))
        {
            log("PG: arena data not written into this save (the save keeps the arena data it had): " + error);
            throw new ArenaDataNotWritten("Proving Grounds arena data not written: " + error);
        }
        stream.Write(payload, 0, payload.Length);
    }

    /// <summary>IModSaveData.Read (header): decodes now (ArenaWorldState.Read), touches no character (CAP 9.1); the identity tokens go back at once.</summary>
    internal void Read(Stream stream)
    {
        // 2.6: PGConfig.Load must run in Initialize before any world is read: the codec reads PGConfig.BookieCreditCeiling(). Initialize runs once
        // at startup, before the first world is built (IMod.cs:4-6; GameMods.cs:68 -> ModHost.Load -> ModHost.cs:342), so it always comes
        // before a Read. Until 2.6, the shipped defaults (PGConfig's initial state) are what the codec reads.
        // Fail closed (quality review): any throw here (the stream, the identity restore) blocks the arena like unreadable data.
        // Timing: the tokens are restored now, the ledger is activated by the first serial step. Restore only registers tokens (no character,
        // no ledger), so a token known before its rows are live changes nothing; on a later failure the tokens stay registered and the
        // ledger is blocked, which refuses every write anyway.
        byte[] bytes = [];
        try
        {
            bytes = ArenaSaveData.ReadBounded(stream, ArenaSaveData.MaxPayloadBytes);
            ArenaSaveData.ReadResult result = state.Read(bytes);
            if (result.Kind == ArenaSaveData.ReadKind.Loaded) identity.Restore(result.Tokens);
        }
        catch (Exception e) { state.ReadFailed(bytes, e); }
    }

    /// <summary>Keeps saved bytes in <c>&lt;mod folder&gt;/recovered/&lt;utc yyyyMMdd-HHmmss&gt;.dat</c> (the whole payload as read): unreadable data
    /// during a load, a legacy snapshot during the next save. Written to a <c>.tmp</c> file, then moved into place, so a half file never has the
    /// final name. The only disk writes of this class, never in gameplay. A failure is logged and returns null, never thrown.</summary>
    string? Recover(byte[] bytes)
    {
        if (modFolder is null) { log("PG: recovered data not kept: no mod folder"); return null; }
        try
        {
            string dir = Path.Combine(modFolder, "recovered");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, ArenaSaveData.RecoveredFileName(DateTime.UtcNow, name => File.Exists(Path.Combine(dir, name))));
            string tmp = path + ".tmp";
            using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                file.Write(bytes, 0, bytes.Length);
                file.Flush(true);
            }
            File.Move(tmp, path, overwrite: false);
            log("PG: saved arena data kept in " + path);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            log("PG: recovered data not kept: " + e.GetType().Name + ": " + e.Message);
            return null;
        }
    }

    /// <summary>C++: ArenaNativePersistence.cpp:421-433 <c>ResetUnmatchedSidecar</c> (task 5.x's leaderboard). Serial steps only.</summary>
    internal bool ResetArenaProgress() => state.ResetUnmatched();
}
