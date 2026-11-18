using Meitou.Modding;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Engine;

namespace ProvingGrounds;

public sealed class ProvingGroundsMod : IMod
{
    internal const string Version = "0.2.0";
    internal static ModContext? Ctx;
    internal static Meitou.Simulation.Population.PopulationData? Data;

    public void Initialize(ModContext c)
    {
        Ctx = c;
        // C++: the PGLog sink is built at DLL load, before startPlugin logs its two lines and calls PGConfig::Load (PGLog.cpp:9-30,
        // Plugin.cpp:15-21): first, once per process (task 2.7).
        PgLogSink.InstallOnce(c);
        var missing = PgRecords.Required.Where(id => c.Data.Find(id) is null).ToList();
        if (missing.Count > 0)
        {
            c.LogError($"the Proving Grounds data is not loaded ({missing.Count} records missing, first {missing[0]}): enable the Workshop mod "
                + "'Proving Grounds' (3774745195) ABOVE ProvingGroundsMeitou in the mod list. Proving Grounds is off.");
            return;
        }
        Ui.PGSettingsUI.Start(c);   // task 2.6: pg_config.json (once per process) and the settings page, before any save is read
        c.AddSystem("proving-grounds", build => new ProvingGroundsSystem(build.Data), SystemAnchor.End);
        PgConsole.Install(c);
        Game.SaveSafetyHooks.Install(c);   // task 2.5 (spike S5 kept for real)
        Game.CombatHookPatches.Install(c);   // task 3.2b (spike S2's ApplyBlow pair kept for real)
        Game.ArenaMedicalPatches.Install(c);   // task 4.7 (C++ Plugin.cpp:26-27, after CombatHooks)
        Ui.ScreenHost.Install(c);   // task 4.4: the window framework (spikes S6 / S7 kept for real)
        Game.ContextMenuHooks.Install(c);   // task 4.2: the Registry / Banner / Leaderboard right-click (spike S3 kept for real; C++ Plugin.cpp:35)
        Ui.DebugMenuKey.Install(c);   // task 4.6b: the "Debug menu key" (a PlayerInterface.UiKey postfix, CAP 7.10)
        Ui.MarksHudOverlay.Install(c);   // task 4.6b: the Marks HUD's overlay label (CAP 7.9)
        // F10 rows (task 2.7; the original had its F8 debug menu, task 4.x): frame thread, the published snapshot only.
        c.AddDebugCommand("Proving Grounds: status", _ => ProvingGroundsSystem.Current?.StatusRow() ?? "Proving Grounds: no world yet");
        c.AddDebugCommand("Proving Grounds: census", _ => PgConsole.QueueCensus());
        c.Log($"ready ({Version}): data ok ({PgRecords.Required.Count} records)");
    }
}
