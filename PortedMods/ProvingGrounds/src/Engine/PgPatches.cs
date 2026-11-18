using System.Reflection;
using HarmonyLib;

namespace ProvingGrounds.Engine;

/// <summary>One Harmony patch of the mod: the target (a <see cref="Members"/> entry; null when it did not resolve), the patch methods, the
/// part it belongs to and the Harmony priority of its methods (<see cref="HarmonyLib.Priority"/>; <c>Priority.Last</c> for a patch that must
/// see what other mods' patches of the same method decided).</summary>
sealed record PgPatch(Part Part, string What, MethodBase? Target, MethodInfo? Prefix, MethodInfo? Postfix, int Priority = HarmonyLib.Priority.Normal);

/// <summary>
/// Every Harmony patch the mod applies (task 2.8), in one list, and the one way to apply it. Each installer (PgConsole.Install,
/// SaveSafetyHooks.Install, CombatHookPatches.Install, ArenaMedicalPatches.Install, Ui.ScreenHost.Install, Game.ContextMenuHooks.Install) builds its patches with its own <c>Patches()</c> and applies them through
/// <see cref="Apply"/>; PgCheck hooks applies <see cref="All"/> through the same <see cref="Apply"/> on a check Harmony instance and removes
/// them again, so the check tests exactly what ships. A new installer adds its <c>Patches()</c> to <see cref="All"/>.
/// </summary>
static class PgPatches
{
    /// <summary>Every patch of every installer.</summary>
    internal static IEnumerable<PgPatch> All() =>
        PgConsole.Patches().Concat(Game.SaveSafetyHooks.Patches()).Concat(Game.CombatHookPatches.Patches())
            .Concat(Game.ArenaMedicalPatches.Patches())
            .Concat(Ui.ScreenHost.Patches())   // task 4.4: the window framework
            .Concat(Game.ContextMenuHooks.Patches())   // task 4.2: the building right-click, the use pointer, the order hook
            .Concat(Ui.DebugMenuKey.Patches());   // task 4.6b: the debug menu key (the last spike patch, S4, became task 5.3a's: no spike installer left)

    /// <summary>Applies one patch. Throws when the target did not resolve or Harmony refuses the patch (a parameter name the target lacks, a
    /// patch method that does not exist); the installers catch and log, PgCheck reports.</summary>
    internal static void Apply(Harmony harmony, PgPatch p)
    {
        if (p.Target is null) throw new InvalidOperationException($"{p.What}: target not found");
        if (p.Prefix is null && p.Postfix is null) throw new InvalidOperationException($"{p.What}: no patch method");
        harmony.Patch(p.Target, prefix: p.Prefix is null ? null : new HarmonyMethod(p.Prefix) { priority = p.Priority },
            postfix: p.Postfix is null ? null : new HarmonyMethod(p.Postfix) { priority = p.Priority });
    }

    /// <summary>A patch method of <paramref name="type"/> by name (any visibility); null when absent (Apply then refuses the patch).</summary>
    /// <summary>Takes one applied patch off again (an installer whose later patch failed; PgCheck hooks unpatches the same way).</summary>
    internal static void Remove(Harmony harmony, PgPatch p)
    {
        if (p.Target is null) return;
        if (p.Prefix is not null) harmony.Unpatch(p.Target, p.Prefix);
        if (p.Postfix is not null) harmony.Unpatch(p.Target, p.Postfix);
    }

    internal static MethodInfo? Method(Type type, string name) => AccessTools.Method(type, name);
}
