using System.Text.Json;

namespace Meitou.Game.Host.Photo;

/// <summary>
/// Photo mode's file (%LOCALAPPDATA%/Meitou/photo-mode.json): the settings the mode was last left with and the presets, each a set of settings by
/// their <c>photo set</c> names. A missing or unreadable file is an empty one.
/// </summary>
sealed class PhotoPresets
{
    /// <summary>
    /// A named set of settings.
    /// </summary>
    public sealed class Preset
    {
        /// <summary>
        /// The name the panel shows.
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// The settings by their <c>photo set</c> names.
        /// </summary>
        public Dictionary<string, float> Values { get; set; } = [];
    }

    /// <summary>
    /// The file's contents as written.
    /// </summary>
    sealed class Saved
    {
        /// <summary>
        /// The settings the mode was last left with.
        /// </summary>
        public Dictionary<string, float>? Last { get; set; }

        /// <summary>
        /// The kept presets.
        /// </summary>
        public List<Preset>? Presets { get; set; }
    }

    /// <summary>
    /// The file's path.
    /// </summary>
    public static string File =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou", "photo-mode.json");

    /// <summary>
    /// The settings the mode was last left with, by their <c>photo set</c> names.
    /// </summary>
    public Dictionary<string, float> Last { get; set; } = [];

    /// <summary>
    /// The kept presets, in the panel's order.
    /// </summary>
    public List<Preset> Presets { get; private set; } = [];

    /// <summary>
    /// Reads the file; a missing one leaves the settings as they are, an unreadable one is reported.
    /// </summary>
    public void Load()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return;

            var saved = JsonSerializer.Deserialize<Saved>(System.IO.File.ReadAllText(File));
            (Last, Presets) = (saved?.Last ?? [], saved?.Presets ?? []);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.WriteLine($"photo     cannot read {File}: {e.Message}");
        }
    }

    /// <summary>
    /// Writes the file (indented), making its folder; a failure is reported.
    /// </summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            var json = JsonSerializer.Serialize(new Saved { Last = Last, Presets = Presets }, new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(File, json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"photo     cannot write {File}: {e.Message}");
        }
    }
}
