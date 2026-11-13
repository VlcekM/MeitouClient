using System.Text.Json;

namespace Meitou.Game.Host.Photo;

/// <summary>
/// Photo mode's file (%LOCALAPPDATA%/Meitou/photo-mode.json): the settings the mode was last left with and the presets, each a set of settings by
/// their <c>photo set</c> names. A missing or unreadable file is an empty one.
/// </summary>
sealed class PhotoPresets
{
    public sealed class Preset
    {
        public string Name { get; set; } = "";
        public Dictionary<string, float> Values { get; set; } = [];
    }

    sealed class Saved
    {
        public Dictionary<string, float>? Last { get; set; }
        public List<Preset>? Presets { get; set; }
    }

    public static string File => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou", "photo-mode.json");

    public Dictionary<string, float> Last { get; set; } = [];
    public List<Preset> Presets { get; private set; } = [];

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

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(new Saved { Last = Last, Presets = Presets }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"photo     cannot write {File}: {e.Message}");
        }
    }
}
