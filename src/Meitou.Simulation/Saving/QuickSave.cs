namespace Meitou.Simulation.Saving;

/// <summary>
/// Where the game host keeps its quicksave (docs/formats/save.md "Autosave and quicksave"). The original saves a quicksave to
/// <c>%LOCALAPPDATA%\kenshi\save\quicksave</c>; Meitou's own is <c>%LOCALAPPDATA%\Meitou\save\quicksave</c> (an engine choice, next to the other Meitou
/// caches), so a Meitou quicksave never replaces the user's save of the original game and the original never meets a save with Meitou's differences.
/// The slot name is the original's.
/// </summary>
public static class QuickSave
{
    /// <summary>
    /// The slot name the quicksave and quickload events use in the original.
    /// </summary>
    public const string Name = "quicksave";

    /// <summary>
    /// The root of Meitou's saves, <c>%LOCALAPPDATA%\Meitou\save</c> (a test passes its own).
    /// </summary>
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou", "save");

    /// <summary>
    /// The folder of the quicksave under <paramref name="root"/> (default <see cref="DefaultRoot"/>).
    /// </summary>
    public static string Folder(string? root = null) => Path.Combine(root ?? DefaultRoot, Name);

    /// <summary>
    /// The original's autosave slots, written in turn (<c>autosave0</c> to <c>autosave2</c>).
    /// </summary>
    public const int AutosaveSlots = 3;

    /// <summary>
    /// The folder of autosave slot <paramref name="index"/> under <paramref name="root"/> (default <see cref="DefaultRoot"/>).
    /// </summary>
    public static string AutosaveFolder(int index, string? root = null) => Path.Combine(root ?? DefaultRoot, $"autosave{index}");
}
