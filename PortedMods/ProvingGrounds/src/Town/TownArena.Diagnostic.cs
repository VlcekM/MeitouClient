namespace ProvingGrounds.Town;

// Task 8.7a: this world's controlled diagnostic run (src/Town/TownDiagnosticRun.cs), owned by TownArena. In its own file, not in TownArena.cs:
// the parallel M8 work (branch m8-services) edits TownArena.cs and TownArena.Bouts.cs, so this branch changes only their diagnostic placeholder
// lines and adds the member here.

sealed partial class TownArena
{
    TownDiagnosticRun? diagnostic;

    /// <summary>This world's diagnostic run (C++ namespace TownDiagnosticRun): TownArena's host, the debug menu, the combat log host and the load
    /// path all call it. Built at the first use from TownArena's store, clock and combat log host (the store and the host are built after this
    /// object, ProvingGroundsSystem); it keeps no state of its own (the state is the store's).</summary>
    internal TownDiagnosticRun Diagnostic => diagnostic ??= new TownDiagnosticRun(store, clock, combatLog, this);
}
