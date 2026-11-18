namespace ProvingGrounds.Core.Glue;

/// <summary>
/// The part of the C++ <c>WorldLifecycle::AbandonWorldState</c> (WorldLifecycle.cpp:250-280, on GameWorld clear) the port still runs when a new
/// world takes over (ProvingGroundsSystem.TakeOver; the rest is replaced by rebuilding the system for every world). Task 7.1 quality review:
/// <c>TownArena::AbandonWorldState</c> puts back the process-wide medical protocol, and <c>ArenaMedical::SetProtocol</c> refuses while a spar
/// match is active, so the old world's SparSession must be abandoned FIRST, as the C++ order has it (SparSession :265 before TownArena :267).
/// Each step under its own guard: a throw in one is reported and the next still runs.
/// </summary>
public static class OldWorldAbandon
{
    /// <summary>The step names <see cref="Run"/> passes to its failure callback.</summary>
    public const string SparStep = "SparSession", TownStep = "TownArena";

    /// <summary>C++ order: <paramref name="sparAbandon"/> (SparSession::AbandonWorldState), then <paramref name="townAbandon"/>
    /// (TownArena::AbandonWorldState). <paramref name="failed"/> gets the step's name and its exception.</summary>
    public static void Run(Action sparAbandon, Action townAbandon, Action<string, Exception> failed)
    {
        try { sparAbandon(); }
        catch (Exception e) { failed(SparStep, e); }
        try { townAbandon(); }
        catch (Exception e) { failed(TownStep, e); }
    }
}
