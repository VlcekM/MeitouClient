// SPIKE CODE (task 0.7). Harmony patches that only log what the engine does. Later milestones replace it. S4 (the ConversationEffect prefix on
// Null's TRADE) became the real Rewards opening in task 5.3a (src/Game/ContextMenuHooks.cs, Members "task 5.3a"), without its arming; its pg flags
// verb is gone. S3 (the right press on a site) became the real building right-click in task 4.2 (src/Game/ContextMenuHooks.cs), with its members. The S6/S7 test window (pg win, pg portrait) became the window framework in task 4.4 (src/Ui/ScreenHost.cs, src/Ui/Portraits.cs,
// the test window pg.test of src/Ui/TestScreen.cs + src/Game/TestScreenSim.cs, pg window test) and is gone from here, with its arming. S5 (the QuickSaveNow prefix) moved to src/Game/WorldLifecycle.cs in task 2.5; S2 (the ApplyBlow
// pre/postfix and its tally) became the real per-blow stats feed in src/Game/CombatHooks.cs in task 3.2b (the tally is gone: the PG-SPAR result
// line reports the real counts).
namespace ProvingGrounds.Spikes;

static class SpikePatches
{
    /// <summary>Off by default: the TEST-ONLY verbs (and any future spike patch) do nothing until a test says `pg spikes on`. No spike patch is
    /// left (S4 became task 5.3a's Rewards opening), so there is no installer; the flag stays: every test verb reads it.</summary>
    internal static volatile bool Armed;
}
