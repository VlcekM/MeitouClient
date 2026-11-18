namespace ProvingGrounds.Engine;

/// <summary>What the windows and the console show, built by the system on the sim thread every 0.20 s of <see cref="PgClock"/> time and
/// right after any command, then published through a <c>volatile</c> field. Immutable: the frame thread reads only this.
/// Each later task adds its part.</summary>
/// <param name="Failing">Empty, or "N steps failing: name (xcount), ..." for the frame-loop steps that have thrown in this world.</param>
/// <param name="LastProbe">The last <c>pg probe &lt;id&gt;</c> JSON (task 2.2), or "none".</param>
/// <param name="Arena">The ledger's row counts (task 2.4), e.g. "1 player rows, 0 town rows, 0 progression rows, 0 unlocks".</param>
/// <param name="ArenaBlocked">Why the arena ledger is blocked (unreadable saved data, task 2.4), or "" when it is ready.</param>
/// <param name="SaveSafety">The save-safety state (task 2.5): "idle", "arena busy", ... (WorldLifecycle.StatusText).</param>
/// <param name="Phases">Each subsystem and its phase (task 2.7, the F10 status row): "idle" for a placeholder, "failing" for a step that threw.</param>
/// <param name="LastCensus">The last census summary (the F10 census row, task 2.7), or "none".</param>
/// <param name="Match">The running match's status (SparSession's C++ status text, task 3.3), or "none" when no match runs.</param>
/// <param name="ArenaResetOffered">The leaderboard (task 5.x) offers "Reset arena progress": only after a WrongSnapshot load, which a Meitou save cannot produce (task 2.4).</param>
/// <param name="Ui">Each window's model by screen id (task 4.4): what the window side binds; null = none yet. Read with <see cref="Model"/>.</param>
sealed record PgSnapshot(long SlowTicks, uint NowMs, string LastCommand, string Failing, string LastProbe, string Arena, string ArenaBlocked, bool ArenaResetOffered, string SaveSafety,
    IReadOnlyList<(string Name, string Phase)> Phases, string LastCensus, string Match = "none", IReadOnlyDictionary<string, PgUiModel>? Ui = null,
    IReadOnlySet<string>? InteractRecords = null, bool InteractBusy = false,
    ProvingGrounds.Core.Glue.RewardPurchaseResults<PgPurchaseResult>? LastPurchases = null, PgPerfStats? Perf = null)
{
    // Perf (task 9.2a): the tick-cost instrument's numbers, null until it measured something in this world; while it is off the last
    // numbers stay (On false) and the F10 status row shows nothing of them.

    // LastPurchases (task 5.2b review I3, per sender since the task 5.3a quality review): the result of each sender's last RewardPurchase applied
    // in this world (none yet: null), published right after the Inputs that applied it, so the Rewards window (task 5.3) shows RewardsUI.cpp's
    // status line for its own click (match its own Seq with ==; another sender's purchase never ends its wait).

    /// <summary>The last result of <paramref name="sender"/> in this world, or null.</summary>
    internal PgPurchaseResult? LastPurchaseOf(ProvingGrounds.Core.Glue.RewardPurchaseSender sender) => LastPurchases?.Of(sender);

    internal static readonly PgSnapshot Empty = new(0, 0, "none", "", "none", "no world yet", "", false, "no world yet", [], "none", "none");

    // InteractRecords (task 4.2): the BUILDING records of the player's sites that ArenaIdentity reads as a Registry, Banner or Leaderboard
    // (ContextMenuHooks.InteractRecords): the window half decides a right press and the pointer from them without reading the world.
    // InteractBusy (task 4.2): a match, a staged fight or a walk-in runs (C++ ShouldOpenInteractUiImmediately's inputs): with nobody selected
    // a right press on a finished one of ours then still opens its window at once.

    /// <summary>Screen <paramref name="id"/>'s model, or null when the simulation published none.</summary>
    internal PgUiModel? Model(string id) => Ui is { } ui && ui.TryGetValue(id, out var m) ? m : null;
}

/// <summary>What the last RewardPurchase did (task 5.2b): its sender's <paramref name="Seq"/>, fighter and kind, whether it succeeded and the
/// status text RewardDelivery answered (C++ text; empty when the command was refused because its offer changed since it was shown:
/// <paramref name="OfferChanged"/>).</summary>
sealed record PgPurchaseResult(long Seq, Meitou.Simulation.CharacterId Fighter, ProvingGrounds.Core.Glue.RewardPurchaseKind Kind, bool Ok, string Status,
    bool OfferChanged, ProvingGrounds.Core.Glue.RewardPurchaseSender Sender = ProvingGrounds.Core.Glue.RewardPurchaseSender.Window);

// ---------- task 4.4: window models ----------

/// <summary>
/// What one window shows, published by its sim half (task 4.4; UI rules of docs/m4-brief.md: the window half reads only this, never the world).
/// Immutable; the sim half builds a NEW model only when something it shows changed (the window side compares models by reference to skip work),
/// with every player-visible text already formatted (CMath.FormatF / FormatG; no formatting per frame on the window side).
/// <para><see cref="RequestSerial"/> / <see cref="RequestOpen"/>: the sim half asks the window side to open (true) or close (false) the window by
/// bumping the serial; the window side acts once per new serial (Core/Glue/ScreenHostLogic.cs ScreenRequests). Each world starts at 0.</para>
/// </summary>
abstract record PgUiModel
{
    public int RequestSerial { get; init; }
    public bool RequestOpen { get; init; }
}
