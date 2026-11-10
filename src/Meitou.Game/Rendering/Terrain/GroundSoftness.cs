using Meitou.Content;
using Meitou.Data;
using Meitou.Game.Audio;

namespace Meitou.Game.Rendering.Terrain;

/// <summary>
/// How soft each ground texture is, for the marks feet and bodies leave (Meitou's ground trails; docs/render-terrain.md "Ground"): 0 takes none, 1 takes them in
/// full. The game's data has no such value: the footsteps' GroundType is Dirt for more than half the biomes, hardened metal and rock among them, and
/// <c>absorbance</c> is the same everywhere. So a table by diffuse texture name: Meitou's own (<c>ground-softness.cfg</c>, embedded), then the same file in
/// the folder of each loaded mod, in load order, then the one beside the executable; a later line replaces an earlier one. A texture in no table falls
/// back on its GroundType (<see cref="OfType"/>).
/// </summary>
public sealed class GroundSoftness
{
    public const string FileName = "ground-softness.cfg";

    /// <summary>How deep packed dirt takes a mark, against sand, mud and snow, where no table knows the texture.</summary>
    const float DirtMark = 0.45f;

    readonly Dictionary<string, float> _byTexture = new(StringComparer.OrdinalIgnoreCase);

    public GroundSoftness(GameInstall install, GameDatabase db)
    {
        using (var stream = typeof(GroundSoftness).Assembly.GetManifestResourceStream(FileName))
            if (stream is not null)
                using (var reader = new StreamReader(stream)) Read(reader.ReadToEnd());
        var mods = LoadOrder.FindMods(install);
        foreach (string file in db.Files)
            if (mods.TryGetValue(file, out var path) && Path.GetDirectoryName(path) is { } folder) ReadFile(Path.Combine(folder, FileName));
        ReadFile(Path.Combine(AppContext.BaseDirectory, FileName));
    }

    /// <summary>The GroundType names a line may end its texture with (<c>texture:Dirt</c>), by value.</summary>
    static readonly string[] TypeNames = ["Sand", "Grass", "Concrete", "Wood", "Metal", "Water", "Mud", "Snow", "Dirt"];

    /// <summary>
    /// The softness of a layer: its texture's for its GroundType in the tables (<c>texture:Type</c>: one texture is tar mud in one biome and hardened metal in
    /// another), else its texture's, else its GroundType's.
    /// </summary>
    public float Of(int groundType, string? texture)
    {
        if (texture is null) return OfType(groundType);
        string key = Key(texture);
        if (groundType >= 0 && groundType < TypeNames.Length && _byTexture.TryGetValue(key + ":" + TypeNames[groundType], out float typed)) return typed;
        return _byTexture.TryGetValue(key, out float soft) ? soft : OfType(groundType);
    }

    /// <summary>Sand, mud and snow take a mark in full, dirt a shallow one, the rest none.</summary>
    public static float OfType(int groundType) => groundType switch
    {
        GroundSurfaces.Sand or GroundSurfaces.Mud or GroundSurfaces.Snow => 1,
        GroundSurfaces.Dirt => DirtMark,
        _ => 0,
    };

    /// <summary>A texture's key: its file name without folder or extension.</summary>
    static string Key(string texture) => Path.GetFileNameWithoutExtension(texture.Replace((char)92, '/'));

    void ReadFile(string path)
    {
        try
        {
            if (File.Exists(path)) Read(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Lines of <c>texture = softness</c> or <c>texture:Type = softness</c> (0 to 1); <c>#</c> starts a comment; what does not parse is skipped.</summary>
    void Read(string text)
    {
        foreach (string raw in text.Split((char)10))
        {
            string line = raw;
            int comment = line.IndexOf('#');
            if (comment >= 0) line = line[..comment];
            int equals = line.LastIndexOf('=');
            if (equals <= 0) continue;
            string name = line[..equals].Trim();
            if (name.Length > 0 && float.TryParse(line[(equals + 1)..].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float soft))
            {
                int colon = name.LastIndexOf(':');
                string type = colon > 0 ? name[(colon + 1)..].Trim() : "";
                bool typed = TypeNames.Contains(type, StringComparer.OrdinalIgnoreCase);
                _byTexture[typed ? Key(name[..colon].Trim()) + ":" + TypeNames.First(t => t.Equals(type, StringComparison.OrdinalIgnoreCase)) : Key(name)] = Math.Clamp(soft, 0, 1);
            }
        }
    }
}
