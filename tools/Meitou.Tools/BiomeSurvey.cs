using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;

/// <summary>
/// Every BIOMES record: its five footstep GroundTypes (ground, slope, grass, dirt, road; docs/audio.md "Surface") and the textures of those
/// layers.
/// </summary>
static class BiomeSurvey
{
    /// <summary>
    /// The GroundType names by value.
    /// </summary>
    static readonly string[] Types = ["Sand", "Grass", "Concrete", "Wood", "Metal", "Water", "Mud", "Snow", "Dirt"];

    /// <summary>
    /// Prints one line per biome: its layers' ground types and textures, then its absorbances.
    /// </summary>
    public static int Run(GameInstall install)
    {
        var db = GameDatabase.Load(LoadOrder.FromInstall(install));
        foreach (var r in db.OfType(FcsRecordType.Biomes).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            string layers = string.Join(", ",
                Layer(r, "ground", "ground type", 0, "texture base"),
                Layer(r, "slope", "slope type", 8, "texture slope"),
                Layer(r, "grass", "grass type", 8, "texture grass"),
                Layer(r, "dirt", "dirt type", 8, "texture dirt"),
                Layer(r, "road", "road type", 2, "texture road"));
            string absorbance = string.Join("/",
                Absorbance(r, "absorbance 0"),
                Absorbance(r, "absorbance 1"),
                Absorbance(r, "absorbance 2"),
                Absorbance(r, "absorbance dirt"),
                Absorbance(r, "absorbance grass"),
                Absorbance(r, "absorbance road"));
            Console.WriteLine($"{r.Name} [{r.DefinedBy}] {layers}; absorbance 0/1/2/dirt/grass/road {absorbance}");
        }

        return 0;
    }

    /// <summary>
    /// A layer as "<paramref name="name"/> type (texture)".
    /// </summary>
    static string Layer(GameRecord r, string name, string typeField, int fallback, string textureField) =>
        $"{name} {GroundType(r, typeField, fallback)} ({Texture(r, textureField)})";

    /// <summary>
    /// The GroundType a field holds, by name (its number when it names none).
    /// </summary>
    static string GroundType(GameRecord r, string field, int fallback) =>
        r.GetInt(field, fallback) is var t && t >= 0 && t < Types.Length ? Types[t] : t.ToString();

    /// <summary>
    /// The file name (no folder, no extension) of a texture field; "-" when it is empty.
    /// </summary>
    static string Texture(GameRecord r, string field) =>
        r.Filenames.TryGetValue(field, out var f) && f.Length > 0 ? Path.GetFileNameWithoutExtension(f.Replace((char)92, '/')) : "-";

    /// <summary>
    /// An absorbance field (0 when unset) to two decimals at most.
    /// </summary>
    static string Absorbance(GameRecord r, string field) => r.GetFloat(field, 0).ToString("0.##");
}
