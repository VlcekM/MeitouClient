using System.Reflection;
using System.Reflection.Emit;
using Meitou.Content;
using Meitou.Modding;

namespace Meitou.Tests.Modding;

/// <summary>Code-mod dependencies (docs/modding.md "Dependencies"): manifest keys, version ranges, the list rules, shared assemblies, refusals and the dependency accessor.</summary>
public class ModDependencyTests
{
    sealed class Framework : IMod
    {
        public void Initialize(ModContext context) { }
    }

    /// <summary>A temporary Kenshi-style mods folder: one folder per mod with its <c>.mod</c> and <c>meitou.json</c>.</summary>
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

    static string Manifest(string id, int api = 2, string? version = null, string dependencies = "[]")
    {
        string versionKey = version is null ? "" : $", \"version\": \"{version}\"";
        return $$"""{ "id": "{{id}}", "apiVersion": {{api}}, "assemblies": ["{{id}}.dll"], "entry": "X.Y"{{versionKey}}, "dependencies": {{dependencies}} }""";
    }

    static ModHost Load(params ModInfo[] mods) =>
        ModHost.Load(new ModHostOptions(mods, mods.Select(m => m.Name).ToHashSet(), null!, null!, () => null));

    static CodeModManifest Parse(string json)
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, json);
            return CodeModManifest.Read(file);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Writes a tiny assembly that is not on the game's list, as a framework DLL would be.</summary>
    static void EmitLibrary(string path, string name)
    {
        var builder = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        var module = builder.DefineDynamicModule(name);
        module.DefineType(name + ".Ledger", TypeAttributes.Public | TypeAttributes.Class).CreateType();
        builder.Save(path);
    }

    [Fact]
    public void The_manifest_reads_version_and_both_dependency_forms()
    {
        var manifest = Parse(Manifest("nemesis", version: "0.3.0-beta",
            dependencies: """["rivalry.core", { "id": "visitors", "version": ">=1.2.0 <2.0.0", "optional": true }]"""));

        Assert.Equal(new CodeModVersion(0, 3, 0) { Prerelease = "beta" }, manifest.Version);
        Assert.Equal(["rivalry.core", "visitors"], manifest.DependsOn.Select(d => d.Id));
        Assert.False(manifest.DependsOn[0].Optional);
        Assert.Null(manifest.DependsOn[0].Range);
        Assert.True(manifest.DependsOn[1].Optional);
        Assert.Equal(">=1.2.0 <2.0.0", manifest.DependsOn[1].Range?.Text);
    }

    [Fact]
    public void A_manifest_without_the_new_keys_reads_as_before()
    {
        var manifest = Parse("""{ "id": "old", "apiVersion": 1, "assemblies": ["Old.dll"], "entry": "Old.Mod" }""");

        Assert.Null(manifest.Version);
        Assert.Empty(manifest.DependsOn);
    }

    [Theory]
    [InlineData("""["me"]""", "lists itself")]
    [InlineData("""["a", "a"]""", "listed twice")]
    [InlineData("""[{ "id": "a", "version": "about 2" }]""", "not a version comparator")]
    [InlineData("""[{ "id": "a", "optional": "yes" }]""", "true or false")]
    [InlineData("""[3]""", "neither an id nor an object")]
    public void Bad_dependencies_refuse_the_manifest(string dependencies, string reason)
    {
        var e = Assert.Throws<InvalidDataException>(() => Parse(Manifest("me", dependencies: dependencies)));
        Assert.Contains(reason, e.Message);
    }

    [Theory]
    [InlineData(">=1.2.0 <2.0.0", "1.2.0", true)]
    [InlineData(">=1.2.0 <2.0.0", "1.9.9", true)]
    [InlineData(">=1.2.0 <2.0.0", "2.0.0", false)]
    [InlineData(">=1.2.0 <2.0.0", "1.1.9", false)]
    [InlineData(">1.0.0", "1.0.0", false)]
    [InlineData("<=1.0.0", "1.0.0", true)]
    [InlineData("1.4.2", "1.4.2", true)]
    [InlineData("=1.4.2", "1.4.3", false)]
    [InlineData(">=1.2.0", "1.2.0-beta.1", false)]   // SemVer: a pre-release is below its release
    public void Version_ranges_match_as_documented(string range, string version, bool expected)
    {
        Assert.True(CodeModVersion.TryParse(version, out var v));
        Assert.Equal(expected, CodeModVersionRange.Parse(range).Contains(v));
    }

    [Fact]
    public void A_range_rejects_a_mod_without_a_version_and_no_range_accepts_it()
    {
        Assert.False(new CodeModDependency("a", CodeModVersionRange.Parse(">=1.0.0")).Accepts(null));
        Assert.True(new CodeModDependency("a").Accepts(null));
    }

    static CodeModEntry Entry(string name, bool enabled = true, string? version = null, params CodeModDependency[] dependencies) =>
        new(name, new CodeModManifest(name, 2, [name + ".dll"], "X.Y", version is null ? null : Version(version), dependencies), enabled);

    static CodeModVersion Version(string text) => CodeModVersion.TryParse(text, out var v) ? v : throw new ArgumentException(text);

    [Fact]
    public void The_list_rules_name_each_problem()
    {
        var needsCore = new CodeModDependency("core");
        Assert.Contains("not installed", CodeModDependencies.Problem(needsCore, 0, [Entry("nemesis")]));
        Assert.Contains("not enabled", CodeModDependencies.Problem(needsCore, 1, [Entry("core", enabled: false), Entry("nemesis")]));
        Assert.Contains("loads after it", CodeModDependencies.Problem(needsCore, 0, [Entry("nemesis"), Entry("core")]));
        var needsTwo = new CodeModDependency("core", CodeModVersionRange.Parse(">=2.0.0"));
        Assert.Contains("1.0.0 is installed", CodeModDependencies.Problem(needsTwo, 1, [Entry("core", version: "1.0.0"), Entry("nemesis")]));
        Assert.Null(CodeModDependencies.Problem(needsCore, 1, [Entry("core"), Entry("nemesis")]));
    }

    [Fact]
    public void An_absent_optional_dependency_is_fine_but_a_later_one_is_not()
    {
        var optional = new CodeModDependency("core", Optional: true);
        Assert.Null(CodeModDependencies.Problem(optional, 0, [Entry("nemesis")]));
        Assert.Null(CodeModDependencies.Problem(optional, 1, [Entry("core", enabled: false), Entry("nemesis")]));
        Assert.Contains("loads after it", CodeModDependencies.Problem(optional, 0, [Entry("nemesis"), Entry("core")]));
    }

    [Fact]
    public void Arrange_moves_dependencies_up_and_keeps_the_rest_in_place()
    {
        var list = new List<CodeModEntry>
        {
            new("Textures", null, true),
            Entry("nemesis", dependencies: [new CodeModDependency("core"), new CodeModDependency("visitors", Optional: true)]),
            Entry("visitors"),
            Entry("core"),
        };
        Assert.Equal(["Textures", "visitors", "core", "nemesis"], CodeModDependencies.Arrange(list).Order);

        list.Add(Entry("loopA", dependencies: new CodeModDependency("loopB")));
        list.Add(Entry("loopB", dependencies: new CodeModDependency("loopA")));
        var cycle = CodeModDependencies.Arrange(list);
        Assert.Equal(list.Select(m => m.Name), cycle.Order);   // a cycle moves nothing and is reported
        Assert.Equal(["loopA", "loopB"], cycle.Cycle.Select(c => c.After).Order());
    }

    /// <summary>The core guarantee: a dependent gets the dependency's loaded assembly, not its own copy; without the import it would get a second copy.</summary>
    [Fact]
    public void A_dependent_shares_the_dependency_s_loaded_assembly()
    {
        using var mods = new ModsFolder();
        string frameworkFolder = Directory.CreateDirectory(Path.Combine(mods.Root, "Core")).FullName;
        string dependentFolder = Directory.CreateDirectory(Path.Combine(mods.Root, "Nemesis")).FullName;
        EmitLibrary(Path.Combine(frameworkFolder, "MeitouTestFramework.dll"), "MeitouTestFramework");
        File.Copy(Path.Combine(frameworkFolder, "MeitouTestFramework.dll"), Path.Combine(dependentFolder, "MeitouTestFramework.dll"));
        var framework = new ModLoadContext(frameworkFolder, "core").LoadFromAssemblyPath(Path.Combine(frameworkFolder, "MeitouTestFramework.dll"));
        var name = new AssemblyName("MeitouTestFramework");

        var shared = new ModLoadContext(dependentFolder, "nemesis", new Dictionary<string, Assembly> { ["MeitouTestFramework"] = framework });
        var isolated = new ModLoadContext(dependentFolder, "other");

        Assert.Same(framework, shared.LoadFromAssemblyName(name));
        Assert.Same(framework.GetType("MeitouTestFramework.Ledger"), shared.LoadFromAssemblyName(name).GetType("MeitouTestFramework.Ledger"));
        Assert.NotSame(framework, isolated.LoadFromAssemblyName(name));
    }

    [Fact]
    public void A_missing_dependency_refuses_the_mod_before_its_code_is_opened()
    {
        using var mods = new ModsFolder();
        var nemesis = mods.Add("Nemesis", Manifest("nemesis", dependencies: """["core"]"""));

        var host = Load(nemesis);

        Assert.Contains("needs code mod 'core', which is not installed", host.Refused["Nemesis"]);
    }

    [Fact]
    public void A_dependency_later_in_the_list_refuses_the_mod_with_the_fix()
    {
        using var mods = new ModsFolder();
        var nemesis = mods.Add("Nemesis", Manifest("nemesis", dependencies: """["core"]"""));
        var core = mods.Add("Core", Manifest("core"));

        var host = Load(nemesis, core);

        Assert.Contains("move Core above it", host.Refused["Nemesis"]);
    }

    [Fact]
    public void A_refused_dependency_refuses_its_dependents_with_its_reason()
    {
        using var mods = new ModsFolder();
        var core = mods.Add("Core", Manifest("core", api: 99));
        var nemesis = mods.Add("Nemesis", Manifest("nemesis", dependencies: """["core"]"""));

        var host = Load(core, nemesis);

        Assert.Contains("built for code-mod API 99", host.Refused["Core"]);
        Assert.Contains("which did not load: built for code-mod API 99", host.Refused["Nemesis"]);
    }

    [Fact]
    public void Dependencies_need_api_2_and_api_1_mods_still_load_their_manifest()
    {
        using var mods = new ModsFolder();
        var tooOld = mods.Add("TooOld", Manifest("too.old", api: 1, dependencies: """["core"]"""));
        var plainOld = mods.Add("PlainOld", Manifest("plain.old", api: 1));

        var host = Load(tooOld, plainOld);

        Assert.Contains("need apiVersion 2", host.Refused["TooOld"]);
        Assert.Contains("plain.old.dll is missing", host.Refused["PlainOld"]);   // past the API and dependency checks, to its (absent) code
    }

    [Fact]
    public void An_absent_optional_dependency_lets_the_mod_go_on()
    {
        using var mods = new ModsFolder();
        var nemesis = mods.Add("Nemesis", Manifest("nemesis", dependencies: """[{ "id": "visitors", "optional": true }]"""));

        var host = Load(nemesis);

        Assert.Contains("nemesis.dll is missing", host.Refused["Nemesis"]);
    }

    static (ModContext Context, Framework Instance) DependentContext(bool loaded)
    {
        var core = new LoadedMod(new ModInfo("Core", "mods/Core/Core.mod", ModSource.Local), new CodeModManifest("core", 2, ["Core.dll"], "Core.Mod"))
        {
            Instance = new Framework(),
        };
        var manifest = new CodeModManifest("nemesis", 2, ["Nemesis.dll"], "Nemesis.Mod", Version: null, Dependencies: [new CodeModDependency("core", Optional: true)]);
        var nemesis = new LoadedMod(new ModInfo("Nemesis", "mods/Nemesis/Nemesis.mod", ModSource.Local), manifest)
        {
            Dependencies = loaded ? [core] : [],
        };
        return (new ModContext(nemesis, new ModRegistry(), null!, null!, () => null), (Framework)core.Instance);
    }

    [Fact]
    public void Dependency_gives_the_framework_s_entry_or_null_when_an_optional_one_is_absent()
    {
        var (context, instance) = DependentContext(loaded: true);
        Assert.Same(instance, context.Dependency<Framework>("core"));
        Assert.Same(instance, context.Dependency<IMod>("core"));
        Assert.True(context.HasDependency("core"));

        var (absent, _) = DependentContext(loaded: false);
        Assert.Null(absent.Dependency<Framework>("core"));
        Assert.False(absent.HasDependency("core"));
    }

    [Fact]
    public void Dependency_refuses_an_undeclared_id_and_a_wrong_type()
    {
        var (context, _) = DependentContext(loaded: true);
        Assert.Throws<InvalidOperationException>(() => context.Dependency<Framework>("visitors"));
        Assert.Throws<InvalidCastException>(() => context.Dependency<ModDependencyTests>("core"));
    }
}
