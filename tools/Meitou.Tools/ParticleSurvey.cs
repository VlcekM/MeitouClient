using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Particles;

/// <summary>
/// <c>meitou-tools particles</c>: reads every ParticleUniverse script and particle material of the install and counts what they use
/// (the survey behind docs/formats/particle-universe.md). <c>particles effects</c> lists the EFFECT records with their systems.
/// </summary>
static class ParticleSurvey
{
    public static int Run(GameInstall install, bool effects)
    {
        var library = ParticleLibrary.Load(install);
        if (effects) return Effects(install, library);
        Console.WriteLine($"{library.ScriptFiles} scripts, {library.AllSystems.Count} systems ({library.Systems.Count} names), {library.Materials.Count} materials");
        foreach (var e in library.Errors) Console.WriteLine($"error: {e}");
        foreach (var d in library.Duplicates) Console.WriteLine($"duplicate: {d}");

        var techniques = library.AllSystems.SelectMany(s => s.Techniques).ToList();
        Count("renderers", techniques.Select(t => t.Renderer.TypeName));
        Count("billboard types", techniques.Where(t => t.Renderer.IsBillboard).Select(t => t.Renderer.BillboardType));
        Count("billboard origins", techniques.Where(t => t.Renderer.IsBillboard).Select(t => t.Renderer.Origin));
        Count("rotation types", techniques.Where(t => t.Renderer.IsBillboard).Select(t => t.Renderer.RotationType));
        Count("emitters", techniques.SelectMany(t => t.Emitters).Select(e => e.TypeName));
        Count("affectors", techniques.SelectMany(t => t.Affectors).Select(a => a.TypeName));
        Count("observers", techniques.SelectMany(t => t.Observers).Select(o => o.TypeName));
        Count("handlers", techniques.SelectMany(t => t.Observers).SelectMany(o => o.Handlers));
        Count("emits", techniques.SelectMany(t => t.Emitters).Where(e => e.EmitsKind is not null).Select(e => e.EmitsKind!));
        Count("blends", library.Materials.Values.Select(m => m.Blend.ToString()));
        Count("depth write", library.Materials.Values.Select(m => m.DepthWrite.ToString()));
        Count("ambient vertex program", library.Materials.Values.Select(m => m.Ambient.ToString()));
        Count("colour scale", library.Materials.Values.Select(m => m.ColourScale.ToString()));
        var missing = techniques.Select(t => t.Material).Where(m => m.Length > 0 && library.FindMaterial(m) is null).Distinct().ToList();
        Console.WriteLine($"materials named by techniques but not found: {string.Join(", ", missing)}");
        var unused = library.Materials.Keys.Except(techniques.Select(t => t.Material), StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"materials no technique names: {string.Join(", ", unused)}");
        var texture = library.Materials.Values.Select(m => m.Texture).Where(t => t is not null && !File.Exists(Path.Combine(install.DataDirectory, "particles", "textures", t))).ToList();
        Console.WriteLine($"textures not found under particles/textures: {string.Join(", ", texture)}");
        return library.Errors.Count == 0 ? 0 : 1;
    }

    static void Count(string title, IEnumerable<string> values)
    {
        Console.WriteLine($"{title}: " + string.Join(", ", values.GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
    }

    static int Effects(GameInstall install, ParticleLibrary library)
    {
        var db = GameDatabase.Load(LoadOrder.BaseGame(install));
        foreach (var e in db.OfType(FcsRecordType.EFFECT).OrderBy(e => e.GetInt("type")).ThenBy(e => e.Name))
        {
            var system = e.GetString("particle system");
            var def = library.FindSystem(system);
            Console.WriteLine($"{e.Name,-40} type {e.GetInt("type"),2} system {system,-34} {(def is null ? "MISSING" : $"extent {def.LargestEmitterExtent:0.#} scale {def.Scale}")}  colour {e.GetInt("colour multiplier"):X6} wind {e.GetBool("wind affected")} x{e.GetFloat("wind speed mult"):0.##} span {e.GetFloat("min wind span rate"):0.#}-{e.GetFloat("max wind span rate"):0.#} view {e.GetFloat("maximum view distance"):0} life {e.GetFloat("min time to live"):0}-{e.GetFloat("max time to live"):0} alt {e.GetFloat("min altitude"):0}-{e.GetFloat("max altitude"):0} slope {e.GetFloat("min slope"):0.##}-{e.GetFloat("max slope"):0.##} fade-out {e.GetFloat("particle fade out delay"):0.#} dir {e.GetBool("wind direction emission")} ground {e.GetBool("ground colour")} sky {e.GetFloat("sky colour multiplier"):0.##}");
        }
        return 0;
    }
}
