namespace Meitou.Modding;

/// <summary>
/// A code mod's entry type, named by its manifest's <c>entry</c> and made with its public parameterless constructor (docs/modding.md "Lifecycle"). The game calls
/// <see cref="Initialize"/> once at startup, after the data load and mod merge and before the first world is built, in <c>mods.cfg</c> order.
/// </summary>
public interface IMod
{
    /// <summary>Registers what the mod adds and applies its Harmony patches. A throw disables the mod for the session: its registrations are dropped and its patches removed.</summary>
    void Initialize(ModContext context);
}

/// <summary>
/// State of a mod's system that goes into a save (docs/modding.md "Registration surface"): a system made by a factory registered with <see cref="ModContext.AddSystem"/>
/// that implements this is written into the save's sidecar file for its mod under the system's name, and read back into the new world's system on load.
/// Both run between ticks on the thread that ticks the world.
/// </summary>
public interface IModSaveData
{
    void Write(Stream stream);

    /// <summary>Called only when the save holds data for this system; a fresh world (new game, import, a save made without the mod) starts with the system as made.</summary>
    void Read(Stream stream);
}

/// <summary>The code-mod API (docs/modding.md "Versioning").</summary>
public static class ModApi
{
    /// <summary>
    /// The newest <c>apiVersion</c> this game runs. 2 added <c>version</c>, <c>dependencies</c> and <see cref="ModContext.Dependency{T}"/>.
    /// </summary>
    public const int Version = Meitou.Content.CodeModApi.Newest;

    /// <summary>
    /// The oldest <c>apiVersion</c> this game still runs; a manifest outside <see cref="MinimumVersion"/>..<see cref="Version"/> is refused with a
    /// message.
    /// </summary>
    public const int MinimumVersion = Meitou.Content.CodeModApi.Oldest;

    /// <summary>
    /// The first <c>apiVersion</c> whose manifests may declare <c>dependencies</c>, <c>loadAfter</c>, <c>incompatible</c> and <c>gameVersion</c>; an
    /// older game would ignore them (and load a private copy of a framework).
    /// </summary>
    public const int DependenciesVersion = Meitou.Content.CodeModApi.Dependencies;

    public static bool Supports(int apiVersion) => apiVersion is >= MinimumVersion and <= Version;
}

/// <summary>
/// Implemented by a framework's entry type that takes registrations from its dependents (docs/modding-dependencies.md "Writing a framework"). When a
/// dependent's <see cref="IMod.Initialize"/> throws, the loader removes that mod's own registrations and Harmony patches, then calls
/// <see cref="DependentFailed"/> on every mod it depends on (directly or through others), so each framework drops what it holds for that mod id. A
/// throw here is logged and ignored.
/// </summary>
public interface IModDependents
{
    void DependentFailed(string modId);
}
