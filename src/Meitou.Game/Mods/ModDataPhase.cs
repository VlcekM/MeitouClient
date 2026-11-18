using Meitou.Content;
using Meitou.Data;

namespace Meitou.Modding;

/// <summary>
/// The game data as a session reads it, with the code mods' data phase (docs/modding.md "Data phase"): when an enabled code mod's entry type
/// declares <see cref="IModData"/> (<see cref="ModDataScan"/>), every enabled code mod's code is loaded while the data is read, each mod with a data
/// phase changes the merged records in load order before the loader's own passes, and <see cref="GameMods.StartMods"/> later initializes the mods
/// loaded here. When none declares it, nothing is loaded here and the data and the mods load as before.
/// </summary>
sealed class ModDataPhase(GameInstall install)
{
    /// <summary>
    /// The code mods whose code was loaded for the data phase, not yet initialized; null when no enabled code mod has a data phase (or the data was
    /// not read through <see cref="LoadData"/>).
    /// </summary>
    internal ModHost? Mods { get; private set; }

    /// <summary>
    /// The enabled code mods <see cref="Mods"/> was loaded from, in order (the covered ones left out); empty when <see cref="Mods"/> is null.
    /// </summary>
    internal IReadOnlyList<ModInfo> Enabled { get; private set; } = [];

    /// <summary>
    /// Reads the install's game data (<see cref="GameDatabase.Load"/>) with the data phase between the translations and the loader's passes.
    /// </summary>
    internal GameDatabase LoadData() => GameDatabase.Load(LoadOrder.FromInstall(install), Edit);

    /// <summary>
    /// The data phase on the merged records, when an enabled code mod has one.
    /// </summary>
    void Edit(GameDatabase data)
    {
        var (enabled, disabled) = ModHost.CodeMods(install);
        if (!enabled.Any(ModDataScan.HasDataPhase)) return;

        BuiltInMods.SkipCovered(enabled, disabled);
        Console.WriteLine($"mods      data phase: the code of {enabled.Count} code mod(s) loads with the game data");
        Enabled = enabled;
        Mods = ModHost.Prepare(enabled, disabled, GameMods.RunningVersion());
        Mods.EditData(data);
    }
}
