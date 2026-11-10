using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;

/// <summary>Every BIOMES record: its five footstep GroundTypes (ground, slope, grass, dirt, road; docs/audio.md "Surface") and the textures of those layers.</summary>
static class BiomeSurvey
{
    static readonly string[] Types = ["Sand", "Grass", "Concrete", "Wood", "Metal", "Water", "Mud", "Snow", "Dirt"];

    public static int Run(GameInstall install)
    {
        var db = GameDatabase.Load(LoadOrder.FromInstall(install));
        foreach (var r in db.OfType(FcsRecordType.Biomes).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            string Type(string field, int fallback) => r.GetInt(field, fallback) is var t && t >= 0 && t < Types.Length ? Types[t] : t.ToString();
            string File(string field) => r.Filenames.TryGetValue(field, out var f) && f.Length > 0 ? Path.GetFileNameWithoutExtension(f.Replace((char)92, '/')) : "-";
            Console.WriteLine($"{r.Name} [{r.DefinedBy}] ground {Type("ground type", 0)} ({File("texture base")}), slope {Type("slope type", 8)} ({File("texture slope")}), grass {Type("grass type", 8)} ({File("texture grass")}), dirt {Type("dirt type", 8)} ({File("texture dirt")}), road {Type("road type", 2)} ({File("texture road")}); absorbance 0/1/2/dirt/grass/road {r.GetFloat("absorbance 0", 0):0.##}/{r.GetFloat("absorbance 1", 0):0.##}/{r.GetFloat("absorbance 2", 0):0.##}/{r.GetFloat("absorbance dirt", 0):0.##}/{r.GetFloat("absorbance grass", 0):0.##}/{r.GetFloat("absorbance road", 0):0.##}");
        }
        return 0;
    }
}
