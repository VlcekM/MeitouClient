using Meitou.Content;
using Meitou.Data;
using Meitou.Game.Audio;

namespace Meitou.Game.Rendering.Terrain;

/// <summary>
/// How soft each ground texture is, for the marks feet and bodies leave (Meitou's ground trails; docs/render-terrain.md "Ground"): 0 takes none, 1
/// takes them in full. The game's data has no such value: the footsteps' GroundType is Dirt for more than half the biomes, hardened metal and rock
/// among them, and <c>absorbance</c> is the same everywhere. So a table by diffuse texture name: Meitou's own (<c>ground-softness.cfg</c>,
/// embedded), then the same file in the folder of each loaded mod, in load order, then the one beside the executable; a later line replaces an
/// earlier one. A texture in no table falls back on its GroundType (<see cref="OfType"/>).
/// </summary>
public sealed class GroundSoftness
{
    /// <summary>
    /// The tables' file name: the embedded resource's, and the one looked for in each mod's folder and beside the executable.
    /// </summary>
    public const string FileName = "ground-softness.cfg";

    /// <summary>
    /// How deep packed dirt takes a mark, against sand, mud and snow, where no table knows the texture.
    /// </summary>
    const float DirtMark = 0.45f;

    /// <summary>
    /// The GroundType names a line may end its texture with (<c>texture:Dirt</c>), by value.
    /// </summary>
    static readonly string[] TypeNames = ["Sand", "Grass", "Concrete", "Wood", "Metal", "Water", "Mud", "Snow", "Dirt"];

    /// <summary>
    /// The softness by texture key, or by key and GroundType name (<c>key:Type</c>).
    /// </summary>
    readonly Dictionary<string, float> _byTexture = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The share of its relief depth a texture keeps, by key, for the textures a line gives one.
    /// </summary>
    readonly Dictionary<string, float> _relief = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The tables for the database's data files.
    /// </summary>
    public GroundSoftness(GameInstall install, GameDatabase db) : this(install, db.Files) { }

    /// <summary>
    /// The tables for the data files <paramref name="files"/> (their names, in load order).
    /// </summary>
    public GroundSoftness(GameInstall install, IEnumerable<string> files)
    {
        ReadEmbedded();
        ReadModTables(install, files);
        ReadFile(Path.Combine(AppContext.BaseDirectory, FileName));
    }

    /// <summary>
    /// The softness of a layer: its texture's for its GroundType in the tables (<c>texture:Type</c>: one texture is tar mud in one biome and
    /// hardened metal in another), else its texture's, else its GroundType's.
    /// </summary>
    public float Of(int groundType, string? texture)
    {
        if (texture is null) return OfType(groundType);

        string key = Key(texture);
        if (groundType >= 0 && groundType < TypeNames.Length && _byTexture.TryGetValue(key + ":" + TypeNames[groundType], out float typed))
            return typed;

        return _byTexture.TryGetValue(key, out float soft) ? soft : OfType(groundType);
    }

    /// <summary>
    /// The share of its relief depth (<see cref="GroundRelief"/>, parallax and displacement) a layer keeps: 1 unless a line gives the texture a
    /// second value. For the textures the depth rule turns into spikes: a normal map of fine noise is raised to the slope of rock.
    /// </summary>
    public float Relief(string? texture) => texture is not null && _relief.TryGetValue(Key(texture), out float share) ? share : 1;

    /// <summary>
    /// Sand, mud and snow take a mark in full, dirt a shallow one, the rest none.
    /// </summary>
    public static float OfType(int groundType) => groundType switch
    {
        GroundSurfaces.Sand or GroundSurfaces.Mud or GroundSurfaces.Snow => 1,
        GroundSurfaces.Dirt => DirtMark,
        _ => 0,
    };

    /// <summary>
    /// Reads Meitou's own table, embedded in the assembly.
    /// </summary>
    void ReadEmbedded()
    {
        using var stream = typeof(GroundSoftness).Assembly.GetManifestResourceStream(FileName);
        if (stream is null) return;

        using var reader = new StreamReader(stream);
        Read(reader.ReadToEnd());
    }

    /// <summary>
    /// Reads the table in the folder of each loaded mod, in load order.
    /// </summary>
    void ReadModTables(GameInstall install, IEnumerable<string> files)
    {
        var mods = LoadOrder.FindMods(install);
        foreach (string file in files)
        {
            if (mods.TryGetValue(Path.GetFileNameWithoutExtension(file), out var path) && Path.GetDirectoryName(path) is { } folder)
                ReadFile(Path.Combine(folder, FileName));
        }
    }

    /// <summary>
    /// A texture's key: its file name without folder or extension.
    /// </summary>
    static string Key(string texture) => Path.GetFileNameWithoutExtension(texture.Replace((char)92, '/'));

    /// <summary>
    /// Reads the table at <paramref name="path"/>, if there is one and it can be read.
    /// </summary>
    void ReadFile(string path)
    {
        try
        {
            if (File.Exists(path)) Read(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Lines of <c>texture = softness</c> or <c>texture:Type = softness</c> (0 to 1), with an optional second value after a comma, the texture's
    /// share of its relief depth (<c>texture = softness, relief</c>; <see cref="Relief"/>); <c>#</c> starts a comment; what does not parse is
    /// skipped.
    /// </summary>
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
            var values = line[(equals + 1)..].Split(',');
            if (name.Length == 0 || !Number(values[0], out float soft)) continue;

            int colon = name.LastIndexOf(':');
            string type = colon > 0 ? name[(colon + 1)..].Trim() : "";
            bool typed = TypeNames.Contains(type, StringComparer.OrdinalIgnoreCase);
            string key = Key(typed ? name[..colon].Trim() : name);
            string entry = typed ? key + ":" + TypeNames.First(t => t.Equals(type, StringComparison.OrdinalIgnoreCase)) : key;
            _byTexture[entry] = Math.Clamp(soft, 0, 1);
            if (values.Length > 1 && Number(values[1], out float relief)) _relief[key] = Math.Clamp(relief, 0, 1);
        }
    }

    /// <summary>
    /// Parses a table value (invariant culture, surrounding blanks ignored).
    /// </summary>
    static bool Number(string text, out float value) =>
        float.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
}
