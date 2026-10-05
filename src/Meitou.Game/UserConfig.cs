using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meitou.Game;

/// <summary>
/// The player's settings, kept in <c>meitou.user.json</c> (git-ignored) in the working directory, else next to the executable:
/// the frame limit, vsync, the simulation tick rate, the graphics sliders of the Tab panel (by label) and key bindings
/// (action name to comma-separated keys, as <see cref="Engine.Input.InputBindings.Apply"/> reads them). Written back on exit.
/// </summary>
sealed class UserConfig
{
    public const string FileName = "meitou.user.json";

    /// <summary>Frames per second when vsync is off; 0 = unlimited. Engine default 240.</summary>
    public int FpsLimit { get; set; } = 240;
    public bool VSync { get; set; }
    public int TickRate { get; set; } = 30;
    public Dictionary<string, float> Graphics { get; set; } = [];
    public Dictionary<string, string> Bindings { get; set; } = [];

    [JsonIgnore]
    public string Path { get; private set; } = FileName;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static UserConfig Load()
    {
        foreach (var dir in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var file = System.IO.Path.Combine(dir, FileName);
            if (!File.Exists(file)) continue;
            try
            {
                var config = JsonSerializer.Deserialize<UserConfig>(File.ReadAllText(file), Json) ?? new UserConfig();
                config.Path = file;
                return config;
            }
            catch (JsonException e)
            {
                Console.Error.WriteLine($"config    {file} is not valid ({e.Message}); using the defaults");
                return new UserConfig { Path = file };
            }
        }
        return new UserConfig { Path = System.IO.Path.Combine(Directory.GetCurrentDirectory(), FileName) };
    }

    public void Save()
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(this, Json)); }
        catch (IOException e) { Console.Error.WriteLine($"config    could not write {Path}: {e.Message}"); }
        catch (UnauthorizedAccessException e) { Console.Error.WriteLine($"config    could not write {Path}: {e.Message}"); }
    }
}
