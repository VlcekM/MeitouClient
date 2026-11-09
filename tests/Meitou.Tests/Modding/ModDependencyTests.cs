using System.Reflection;
using System.Reflection.Emit;
using Meitou.Content;
using Meitou.Modding;

namespace Meitou.Tests.Modding;

/// <summary>
/// Code-mod dependencies (docs/modding-dependencies.md): shared assemblies, the loader's refusals, Arrange, gameVersion and launcher/loader
/// agreement.
/// </summary>
public class ModDependencyTests
{
    /// <summary>
    /// A temporary Kenshi-style mods folder: one folder per mod with its <c>meitou.json</c>; no DLL in it is ever loaded, so it deletes.
    /// </summary>
    sealed class ModsFolder : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("meitou-deps-").FullName;

        public ModInfo Add(string name, string manifestJson)
        {
            var folder = Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
            File.WriteAllText(Path.Combine(folder, ModList.CodeManifestFile), manifestJson);
            return new ModInfo(name, Path.Combine(folder, name + ".mod"), ModSource.Local);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    static string Manifest(string id, int api = 2, string extra = "") =>
        $$"""{ "id": "{{id}}", "apiVersion": {{api}}, "version": "1.0.0", "assemblies": ["{{id}}.dll"], "entry": "X.Y"{{extra}} }""";

    static string Needs(string id) => $$""", "dependencies": ["{{id}}"]""";

    static ModHost Load(IReadOnlyList<ModInfo> enabled, IReadOnlyList<ModInfo>? disabled = null)
    {
        var options = new ModHostOptions(enabled, null!, null!, () => null, Disabled: disabled);
        return ModHost.Load(options);
    }

    /// <summary>
    /// A tiny assembly that is not on the game's list, as a framework DLL would be.
    /// </summary>
    static byte[] EmitLibrary(string name)
    {
        var builder = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        var module = builder.DefineDynamicModule(name);
        module.DefineType(name + ".Ledger", TypeAttributes.Public | TypeAttributes.Class).CreateType();
        using var stream = new MemoryStream();
        builder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// The core guarantee: a dependent resolves the dependency's loaded assembly, even with a stray copy in its own folder. The framework loads from
    /// a stream and the copy is never opened, so the folder deletes on Windows too.
    /// </summary>
    [Fact]
    public void A_dependent_shares_the_dependency_s_loaded_assembly()
    {
        using var mods = new ModsFolder();
        string dependentFolder = Directory.CreateDirectory(Path.Combine(mods.Root, "Nemesis")).FullName;
        var bytes = EmitLibrary("MeitouTestFramework");
        File.WriteAllBytes(Path.Combine(dependentFolder, "MeitouTestFramework.dll"), bytes);
        var framework = new ModLoadContext(mods.Root, "core").LoadFromStream(new MemoryStream(bytes));
        var imports = new Dictionary<string, Assembly> { ["MeitouTestFramework"] = framework };

        var shared = new ModLoadContext(dependentFolder, "nemesis", imports).LoadFromAssemblyName(new AssemblyName("MeitouTestFramework"));

        Assert.Same(framework, shared);
        Assert.Same(framework.GetType("MeitouTestFramework.Ledger"), shared.GetType("MeitouTestFramework.Ledger"));
    }

    /// <summary>
    /// Each refusal before code is opened names its fix; a refused dependency cascades with its reason; a disabled one says so.
    /// </summary>
    [Fact]
    public void The_loader_refuses_missing_disabled_later_and_refused_dependencies()
    {
        using var mods = new ModsFolder();
        var core = mods.Add("Core", Manifest("core", api: 99));
        var nemesis = mods.Add("Nemesis", Manifest("nemesis", extra: Needs("core")));
        var lonely = mods.Add("Lonely", Manifest("lonely", extra: Needs("absent")));
        var early = mods.Add("Early", Manifest("early", extra: Needs("late")));
        var late = mods.Add("Late", Manifest("late"));
        var waiting = mods.Add("Waiting", Manifest("waiting", extra: Needs("idle")));
        var idle = mods.Add("Idle", Manifest("idle"));

        var host = Load([core, nemesis, lonely, early, late, waiting], disabled: [idle]);

        Assert.Contains("needs Core ('core'), which is refused: built for code-mod API 99", host.Refused["Nemesis"]);
        Assert.Contains("needs code mod 'absent', which is not installed", host.Refused["Lonely"]);
        Assert.Contains("move Late above it", host.Refused["Early"]);
        Assert.Contains("needs Idle ('idle'), which is not enabled", host.Refused["Waiting"]);
    }

    /// <summary>
    /// A dependency moves up to its dependent, not the dependent down (data overrides keep their order); a cycle moves nothing and is shown.
    /// </summary>
    [Fact]
    public void Arrange_moves_a_dependency_up_and_reports_a_cycle()
    {
        static CodeModEntry Entry(string name, params string[] needs) =>
            new(name, new CodeModManifest(name, 2, [name + ".dll"], "X.Y", null, [.. needs.Select(n => new CodeModDependency(n))]), true);

        Assert.Equal(["C", "A", "B"], CodeModDependencies.Arrange([Entry("A", "C"), Entry("B"), Entry("C")]).Order);

        List<CodeModEntry> loop = [Entry("Textures"), Entry("loopA", "loopB"), Entry("loopB", "loopA")];
        var cycle = CodeModDependencies.Arrange(loop);

        Assert.Equal(loop.Select(m => m.Name), cycle.Order);
        Assert.Equal(["loopA", "loopB"], cycle.Cycle.Select(c => c.After).Order());
    }

    /// <summary>
    /// gameVersion compares the build's release, so a nightly counts as the release it leads to; a range naming a pre-release is refused.
    /// </summary>
    [Fact]
    public void Game_version_takes_a_nightly_as_its_release()
    {
        static CodeModManifest Built(string range) =>
            new("m", 2, ["m.dll"], "X.Y", null, null) { GameVersion = CodeModVersionRange.Parse(range) };

        static CodeModVersion V(string text) => CodeModVersion.TryParse(text, out var v) ? v : throw new ArgumentException(text);

        Assert.Null(CodeModChecks.GameVersionProblem(Built(">=0.16.1"), V("0.16.1-nightly.20261106")));
        Assert.Null(CodeModChecks.GameVersionProblem(Built(">=0.16.0 <0.17.0"), V("0.16.1-nightly.20261106.2")));
        Assert.Contains("built for Meitou <0.17.0", CodeModChecks.GameVersionProblem(Built("<0.17.0"), V("0.17.0-nightly.20261201")));

        using var mods = new ModsFolder();
        var nightly = mods.Add("Nightly", Manifest("nightly", extra: """, "gameVersion": ">=0.16.1-nightly.1" """));
        Assert.Contains("gameVersion: name releases only", Load([nightly]).Refused["Nightly"]);
    }

    /// <summary>
    /// The launcher and the loader judge against what loads, not what is merely enabled: a refused (here missing a dependency) mod neither takes its
    /// id nor conflicts, and a dependent names the mod that would have served.
    /// </summary>
    [Fact]
    public void A_refused_mod_blocks_no_later_one_in_the_launcher_or_the_loader()
    {
        using var mods = new ModsFolder();
        ModInfo[] list =
        [
            mods.Add("Old", Manifest("old", extra: Needs("absent"))),
            mods.Add("Rival", Manifest("rival", extra: """, "incompatible": ["x"], "dependencies": ["absent"]""")),
            mods.Add("Twin", Manifest("old")),
            mods.Add("Needy", Manifest("needy", extra: Needs("old"))),
            mods.Add("X", Manifest("x")),
        ];

        var launcher = CodeModChecks.Check([.. list.Select(m => CodeModDependencies.ReadEntry(m, enabled: true))]);
        var host = Load(list);

        Assert.Equal(["Old", "Rival"], launcher.Keys.Order());
        Assert.Contains("old.dll is missing", host.Refused["Twin"]);   // past the id check, to its (absent) code
        Assert.Contains("x.dll is missing", host.Refused["X"]);
        Assert.Contains("needs Twin ('old'), which is refused: old.dll is missing", host.Refused["Needy"]);
    }
}
