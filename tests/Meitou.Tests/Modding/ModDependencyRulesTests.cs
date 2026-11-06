using Meitou.Content;
using Meitou.Data.Save;
using Meitou.Modding;

namespace Meitou.Tests.Modding;

/// <summary>
/// The code-mod rules added after review (docs/modding-dependencies.md): manifest field kinds, SemVer precedence, Arrange over code and data constraints, API 1
/// binary surface, loadAfter / incompatible / gameVersion, the launcher's preliminary check, failed-initialization cleanup and the save's mod record.
/// </summary>
public class ModDependencyRulesTests
{
    sealed class ModsFolder : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("meitou-rules-").FullName;

        public ModInfo Add(string name, string manifestJson)
        {
            var folder = Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
            File.WriteAllText(Path.Combine(folder, ModList.CodeManifestFile), manifestJson);
            return new ModInfo(name, Path.Combine(folder, name + ".mod"), ModSource.Local);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    static string Manifest(string id, string extra = "", int api = 2, string version = "1.0.0") =>
        $$"""{ "id": "{{id}}", "apiVersion": {{api}}, "version": "{{version}}", "assemblies": ["{{id}}.dll"], "entry": "X.Y"{{extra}} }""";

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

    static CodeModVersion V(string text) => CodeModVersion.TryParse(text, out var v) ? v : throw new ArgumentException(text);

    static ModHost Load(CodeModVersion? game, params ModInfo[] mods) =>
        ModHost.Load(new ModHostOptions(mods, mods.Select(m => m.Name).ToHashSet(), null!, null!, () => null, null, game));

    // ---------------------------------------------------------------- manifest field kinds (review P1)

    /// <summary>A wrong JSON kind used to throw InvalidOperationException out of the loader's pre-scan, aborting every mod's load.</summary>
    [Theory]
    [InlineData(""""{ "id": "a", "apiVersion": "2", "assemblies": ["a.dll"], "entry": "X.Y" }"""", "apiVersion")]
    [InlineData(""""{ "id": "a", "apiVersion": 2.5, "assemblies": ["a.dll"], "entry": "X.Y" }"""", "apiVersion")]
    [InlineData(""""{ "id": "a", "apiVersion": 2, "assemblies": ["a.dll"], "entry": "X.Y", "version": 3 }"""", "version")]
    [InlineData(""""{ "id": "a", "apiVersion": 2, "assemblies": ["a.dll"], "entry": "X.Y", "loadAfter": "b" }"""", "loadAfter")]
    [InlineData(""""{ "id": "a", "apiVersion": 2, "assemblies": ["a.dll"], "entry": "X.Y", "incompatible": [{ "id": "b", "optional": true }] }"""", "incompatible")]
    [InlineData(""""{ "id": "a", "apiVersion": 2, "assemblies": ["a.dll"], "entry": "X.Y", "gameVersion": 16 }"""", "gameVersion")]
    public void A_field_of_the_wrong_kind_is_a_malformed_manifest(string json, string field)
    {
        var e = Assert.Throws<InvalidDataException>(() => Parse(json));
        Assert.StartsWith(field, e.Message);
    }

    [Fact]
    public void A_malformed_manifest_refuses_only_its_own_mod()
    {
        using var mods = new ModsFolder();
        var bad = mods.Add("Bad", """{ "id": "bad", "apiVersion": "2", "assemblies": ["B.dll"], "entry": "X.Y" }""");
        var good = mods.Add("Good", Manifest("good"));

        var host = Load(null, bad, good);

        Assert.Null(CodeModDependencies.TryReadManifest(bad));
        Assert.Contains("apiVersion: a whole number is needed", host.Refused["Bad"]);
        Assert.Contains("good.dll is missing", host.Refused["Good"]);   // the pass went on to the next mod
    }

    // ---------------------------------------------------------------- SemVer (review P2)

    [Fact]
    public void Versions_order_as_the_semver_specification_example()
    {
        string[] ascending = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0"];
        var versions = ascending.Select(V).ToList();
        Assert.All(versions.Zip(versions.Skip(1)), pair => Assert.True(pair.First.CompareTo(pair.Second) < 0, $"{pair.First} < {pair.Second}"));
        Assert.Equal(V("1.2.0+build.5"), V("1.2.0+other"));
    }

    [Theory]
    [InlineData(">=1.2.0", "1.2.0-alpha", false)]
    [InlineData("=1.2.0", "1.2.0-beta", false)]
    [InlineData("=1.2.0-beta", "1.2.0-beta", true)]
    [InlineData(">=1.2.0 <2.0.0", "2.0.0-alpha", false)]
    [InlineData(">=1.2.0-beta", "1.2.0-beta.2", true)]
    [InlineData(">=1.2.0-beta", "1.3.0-beta", false)]
    [InlineData(">=1.2.0-beta", "1.3.0", true)]
    [InlineData("=1.2.0", "1.2.0+build5", true)]
    public void A_range_takes_a_prerelease_only_when_it_names_one(string range, string version, bool expected) =>
        Assert.Equal(expected, CodeModVersionRange.Parse(range).Contains(V(version)));

    [Theory]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3-a..b")]
    [InlineData("1.2")]
    public void Versions_outside_semver_are_refused(string text) => Assert.False(CodeModVersion.TryParse(text, out _));

    // ---------------------------------------------------------------- API 1 binary surface (review P2)

    /// <summary>A mod compiled against API 1 binds to these exact signatures; optional parameters on a longer constructor do not satisfy it.</summary>
    [Fact]
    public void The_api_1_constructor_and_deconstruction_still_exist()
    {
        Type list = typeof(IReadOnlyList<string>);
        Assert.NotNull(typeof(CodeModManifest).GetConstructor([typeof(string), typeof(int), list, typeof(string)]));
        var deconstruct = typeof(CodeModManifest).GetMethod("Deconstruct",
            [typeof(string).MakeByRefType(), typeof(int).MakeByRefType(), list.MakeByRefType(), typeof(string).MakeByRefType()]);
        Assert.NotNull(deconstruct);

        var (id, api, assemblies, entry) = new CodeModManifest("old", 1, ["Old.dll"], "Old.Mod");
        Assert.Equal(("old", 1, "Old.dll", "Old.Mod"), (id, api, assemblies.Single(), entry));
    }

    // ---------------------------------------------------------------- Arrange (review P1)

    static CodeModEntry Entry(string name, bool enabled = true, string[]? code = null, string[]? data = null, string[]? after = null) =>
        new(name, code is null && after is null ? null : new CodeModManifest(name, 2, [name + ".dll"], "X.Y", null,
            (code ?? []).Select(c => new CodeModDependency(c)).ToList()) { LoadAfter = after ?? [] }, enabled, data);

    [Fact]
    public void Arrange_keeps_data_dependencies_while_fixing_code_ones()
    {
        // The review's case: A needs Framework's code, B's .mod header needs A. Code-only sorting gave [B, Framework, A].
        var result = CodeModDependencies.Arrange([Entry("A", code: ["Framework"]), Entry("B", data: ["A.mod"]), Entry("Framework", code: [])]);

        Assert.False(result.HasCycle);
        Assert.Equal(["Framework", "A", "B"], result.Order);
    }

    [Fact]
    public void Arrange_reports_a_code_constraint_contradicting_a_data_one_and_moves_nothing()
    {
        var list = new List<CodeModEntry> { Entry("Framework", code: [], data: ["A.mod"]), Entry("A", code: ["Framework"]) };

        var result = CodeModDependencies.Arrange(list);

        Assert.Equal(["Framework", "A"], result.Order);
        Assert.Equal(2, result.Cycle.Count);
        Assert.Contains(result.Cycle, c => c.Why.StartsWith("code:") && c.After == "A");
        Assert.Contains(result.Cycle, c => c.Why.StartsWith("data:") && c.After == "Framework");
    }

    [Fact]
    public void Arrange_ignores_disabled_mods_constraints_and_honours_load_after()
    {
        Assert.Equal(["X", "Y"], CodeModDependencies.Arrange([Entry("X", enabled: false, code: ["Y"]), Entry("Y", code: [])]).Order);
        Assert.Equal(["Y", "X"], CodeModDependencies.Arrange([Entry("X", after: ["Y"]), Entry("Y", code: [])]).Order);
    }

    // ---------------------------------------------------------------- loadAfter, incompatible, gameVersion

    [Fact]
    public void The_new_keys_read_and_need_api_2()
    {
        var manifest = Parse(Manifest("a", """, "loadAfter": ["b"], "incompatible": ["c", { "id": "d", "version": "<2.0.0" }], "gameVersion": ">=0.16.0" """));
        Assert.Equal(["b"], manifest.LoadAfter);
        Assert.Equal(["c", "d <2.0.0"], manifest.Incompatible.Select(c => c.ToString()));
        Assert.Equal(">=0.16.0", manifest.GameVersion?.Text);

        using var mods = new ModsFolder();
        var old = mods.Add("Old", Manifest("old", """, "loadAfter": ["b"]""", api: 1));
        Assert.Contains("uses loadAfter, which need apiVersion 2", Load(null, old).Refused["Old"]);
    }

    [Fact]
    public void Load_after_a_later_mod_refuses_with_the_fix_and_an_absent_one_is_fine()
    {
        using var mods = new ModsFolder();
        var a = mods.Add("A", Manifest("a", """, "loadAfter": ["b", "absent"]"""));
        var b = mods.Add("B", Manifest("b"));

        Assert.Contains("move B above it", Load(null, a, b).Refused["A"]);
        Assert.Contains("a.dll is missing", Load(null, b, a).Refused["A"]);
    }

    /// <summary>The shared rule (the loader applies it against mods that actually loaded, the launcher against enabled ones).</summary>
    [Fact]
    public void An_incompatible_pair_refuses_the_second_whichever_side_declares_it()
    {
        var b = new CodeModManifest("b", 2, ["b.dll"], "X.Y", V("1.0.0"), null);
        var c = new CodeModManifest("c", 2, ["c.dll"], "X.Y", null, null) { Incompatible = [new("b", CodeModVersionRange.Parse("<2.0.0"))] };
        CodeModEntry E(CodeModManifest m) => new(m.Id.ToUpperInvariant(), m, true);

        Assert.Contains("incompatible with B", CodeModChecks.Check([E(b), E(c)])["C"].Single());
        Assert.Contains("incompatible with C", CodeModChecks.Check([E(c), E(b)])["B"].Single());
        Assert.Empty(CodeModChecks.Check([E(b with { Version = V("2.0.0") }), E(c)]));   // only the named versions conflict
    }

    [Fact]
    public void Game_version_refuses_other_releases_and_takes_a_nightly_in_range()
    {
        using var mods = new ModsFolder();
        var later = mods.Add("Later", Manifest("later", """, "gameVersion": ">=0.17.0" """));
        var current = mods.Add("Current", Manifest("current", """, "gameVersion": ">=0.16.0 <0.17.0" """));
        var nightly = V("0.16.1-nightly.20261106");

        var host = Load(nightly, later, current);

        Assert.Contains("built for Meitou >=0.17.0", host.Refused["Later"]);
        Assert.Contains("current.dll is missing", host.Refused["Current"]);
        Assert.Contains("current.dll is missing", Load(null, current).Refused["Current"]);   // an untagged build passes
    }

    // ---------------------------------------------------------------- the launcher's preliminary check

    [Fact]
    public void The_preliminary_check_cascades_through_mods_that_will_not_load()
    {
        CodeModEntry Mod(string name, string id, params string[] needs) =>
            new(name, new CodeModManifest(id, 2, [id + ".dll"], "X.Y", null, needs.Select(n => new CodeModDependency(n)).ToList()), true);
        var list = new List<CodeModEntry>
        {
            new("Broken", null, true) { ManifestError = "apiVersion: a whole number is needed" },
            Mod("Core", "core"),
            Mod("Twin", "core"),
            Mod("Nemesis", "nemesis", "core"),
            Mod("Hunter", "hunter", "nemesis"),
        };

        var problems = CodeModChecks.Check(list, trusted: new HashSet<string> { "Broken", "Twin", "Nemesis", "Hunter" });

        Assert.Contains("bad meitou.json", problems["Broken"].Single());
        Assert.Contains("id 'core' is taken by mod Core", problems["Twin"].Single());
        Assert.Contains("which will not load: it is not allowed to run code", problems["Nemesis"].Single());
        Assert.Contains("which will not load: needs Core", problems["Hunter"].Single());
        Assert.False(problems.ContainsKey("Core"));
    }

    // ---------------------------------------------------------------- failed initialization

    sealed class Framework : IMod, IModDependents
    {
        public List<string> Dropped { get; } = [];
        public void Initialize(ModContext context) { }
        public void DependentFailed(string modId) => Dropped.Add(modId);
    }

    sealed class Throwing : IMod, IModDependents
    {
        public void Initialize(ModContext context) { }
        public void DependentFailed(string modId) => throw new InvalidOperationException("cleanup bug");
    }

    static LoadedMod Loaded(string id, IMod? instance, params LoadedMod[] dependencies) =>
        new(new ModInfo(id, $"mods/{id}/{id}.mod", ModSource.Local), new CodeModManifest(id, 2, [id + ".dll"], "X.Y"))
        {
            Instance = instance,
            Dependencies = dependencies,
        };

    [Fact]
    public void A_failed_dependent_is_dropped_by_every_framework_it_reaches_once()
    {
        var core = new Framework();
        var middle = new Framework();
        var coreMod = Loaded("core", core);
        var middleMod = Loaded("middle", middle, coreMod);
        var leaf = Loaded("leaf", null, middleMod, coreMod, Loaded("broken", new Throwing()));

        ModHost.TellDependenciesItFailed(leaf);

        Assert.Equal(["leaf"], core.Dropped);
        Assert.Equal(["leaf"], middle.Dropped);
    }

    // ---------------------------------------------------------------- the save's mod record

    [Fact]
    public void The_mod_record_round_trips_and_names_each_change()
    {
        var saved = new CodeModEnvironment("v0.16.0", [new("core", "1.2.0", "Core"), new("nemesis", "0.3.0", "Nemesis"), new("gone", null, "Gone")]);
        var now = new CodeModEnvironment("v0.16.1", [new("nemesis", "0.3.0", "Nemesis"), new("core", "2.0.0", "Core"), new("fresh", "1.0.0", "Fresh")]);

        var back = CodeModEnvironment.TryRead(saved.ToJson())!;
        var changes = back.ChangesTo(now);

        Assert.Equal(saved.Mods, back.Mods);
        Assert.Contains(changes, l => l.Contains("1.2.0 when this was saved and is 2.0.0 now (a major update"));
        Assert.Contains(changes, l => l.StartsWith("Gone ('gone') was running"));
        Assert.Contains(changes, l => l.Contains("different order"));
        Assert.Contains(changes, l => l.StartsWith("Fresh ('fresh') is new"));
        Assert.Empty(back.ChangesTo(saved));
        Assert.Null(CodeModEnvironment.TryRead("""{ "format": 2, "gameVersion": "x", "mods": [] }"""u8.ToArray()));
    }

    [Fact]
    public void The_mod_record_is_not_taken_for_a_mod_s_sidecar()
    {
        var folder = Directory.CreateTempSubdirectory("meitou-save-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, SaveFolder.ModDataDirectory));
            File.WriteAllText(Path.Combine(folder, SaveFolder.ModEnvironmentName), "{}");
            File.WriteAllText(Path.Combine(folder, SaveFolder.ModDataName("core")), "x");

            var save = SaveFolder.Open(folder);

            Assert.Equal(["core"], save.ModDataFiles.Select(f => f.Id));
            Assert.True(save.Exists(SaveFolder.ModEnvironmentName));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
