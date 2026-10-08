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
    public static int Run(GameInstall install, bool effects, bool weathers = false)
    {
        var library = ParticleLibrary.Load(install);
        if (weathers) return Weathers(install, library);
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
            Console.WriteLine($"{e.Name,-40} type {e.GetInt("type"),2} system {system,-34} {(def is null ? "MISSING" : $"extent {def.LargestEmitterExtent:0.#} radius {def.BoundingRadius:0} quota {def.Techniques.Where(t => t.Enabled && t.Emitters.Count > 0).Sum(t => t.VisualQuota)} scale {def.Scale}")}  colour {e.GetInt("colour multiplier"):X6} wind {e.GetBool("wind affected")} x{e.GetFloat("wind speed mult"):0.##} span {e.GetFloat("min wind span rate"):0.#}-{e.GetFloat("max wind span rate"):0.#} view {e.GetFloat("maximum view distance"):0} life {e.GetFloat("min time to live"):0}-{e.GetFloat("max time to live"):0} alt {e.GetFloat("min altitude"):0}-{e.GetFloat("max altitude"):0} slope {e.GetFloat("min slope"):0.##}-{e.GetFloat("max slope"):0.##} fade-out {e.GetFloat("particle fade out delay"):0.#} dir {e.GetBool("wind direction emission")} ground {e.GetBool("ground colour")} sky {e.GetFloat("sky colour multiplier"):0.##}");
        }
        return 0;
    }

    /// <summary>Every WEATHER with its effect entries: the effect, its type, how many may exist at once and the respawn times, its fog volumes and lights.</summary>
    static int Weathers(GameInstall install, ParticleLibrary library)
    {
        var db = GameDatabase.Load(LoadOrder.BaseGame(install));
        foreach (var f in db.OfType(FcsRecordType.EFFECT_FOG_VOLUME))
            Console.WriteLine($"fog volume {f.Name}: type {f.Ints.GetValueOrDefault("type", -1)} radius {f.GetFloat("radius")} distance {f.GetFloat("distance")} alpha {f.GetFloat("alpha")} colour {f.GetInt("colour"):X6} additive {f.GetBool("additive colour")} ground {f.GetBool("ground movement")} pos ({f.GetFloat("position x")}, {f.GetFloat("position y")}, {f.GetFloat("position z")}) pos2 ({f.GetFloat("position 2 x")}, {f.GetFloat("position 2 y")}, {f.GetFloat("position 2 z")})");
        foreach (var w in db.OfType(FcsRecordType.WEATHER).OrderBy(w => w.Name))
        {
            var refs = w.GetReferences("effects");
            if (refs.Count == 0) continue;
            Console.WriteLine($"{w.Name}  (wind {w.GetFloat("wind speed min"):0.#}-{w.GetFloat("wind speed max"):0.#})");
            foreach (var r in refs)
            {
                if (db.Find(r.TargetStringId) is not { Type: FcsRecordType.EFFECT } e) continue;
                var fogs = e.GetReferences("fog volumes").Select(f => db.Find(f.TargetStringId)).Where(f => f is not null).Select(f => $"{f!.Name} type{f.GetInt("type")} r{f.GetFloat("radius"):0} d{f.GetFloat("distance"):0} a{f.GetFloat("alpha"):0.##}");
                var lights = e.GetReferences("lights").Select(l => db.Find(l.TargetStringId)?.Name ?? "?");
                Console.WriteLine($"    {e.Name,-34} type {e.GetInt("type"),2} count {r.Values.Value0} respawn {r.Values.Value1}-{r.Values.Value2}  fogs [{string.Join("; ", fogs)}] lights [{string.Join("; ", lights)}]");
            }
        }
        return 0;
    }
}
