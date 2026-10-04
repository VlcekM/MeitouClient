using Meitou.Content;
using Meitou.Data.Fcs;

namespace Meitou.Tests.Fcs;

public class FcsSchemaTests
{
    [Fact]
    public void Parses_fields_references_owned_lists_and_loops()
    {
        var schema = FcsSchema.Parse("""
            [DIALOGUE,DIALOGUE_LINE]
            OWNED:                conditions
            conditions:           DIALOG_ACTION (0)
            category:
            chance:               100.0 "percent"
            text0:                "" multiline looped "the dialog text(s)"
            mesh:                 "Ogre mesh|*.mesh" "the mesh"
            CONDITIONS:
            condition "mesh" if "chance" is 1
            [AI_TASK]
            enum:                 taskType.IDLE
            Leader AI Goals:      AI_TASK (0, 24,0) "goals"
            TRANSLATE: ALL
            """.Split('\n'));

        Assert.Equal(["DIALOGUE", "DIALOGUE_LINE", "AI_TASK"], schema.TypeNames);
        Assert.True(schema.IsOwned("DIALOGUE_LINE", "conditions"));
        Assert.False(schema.IsOwned("AI_TASK", "conditions"));

        var conditions = schema.FindField("DIALOGUE", "conditions")!;
        Assert.Equal(FcsFieldKind.Reference, conditions.Kind);
        Assert.Equal("DIALOG_ACTION", conditions.ReferenceType);
        Assert.Equal(FcsFieldKind.Float, schema.FindField("DIALOGUE", "chance")!.Kind);
        Assert.Equal(FcsFieldKind.Filename, schema.FindField("DIALOGUE", "mesh")!.Kind);
        Assert.Null(schema.FindField("DIALOGUE", "category"));

        var text = schema.FindField("DIALOGUE_LINE", "text3")!;
        Assert.Equal("text0", text.Name);
        Assert.True(text.IsLooped);
        Assert.Null(schema.FindField("DIALOGUE_LINE", "chance3"));

        Assert.Equal(FcsFieldKind.Enum, schema.FindField("AI_TASK", "enum")!.Kind);
        var goals = schema.FindField("AI_TASK", "Leader AI Goals")!;
        Assert.Equal(["0, 24,0"], goals.Groups);
        Assert.Equal("goals", goals.Description);
    }

    /// <summary>
    /// Every <see cref="FcsRecordType"/> name that has an fcs.def section must be (one of) the best-matching
    /// sections for the fields its records actually use in the base game.
    /// </summary>
    [Fact]
    public void Record_type_names_match_fcs_def_against_base_game()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");

        var schema = FcsSchema.Load(Path.Combine(install!.Root, "fcs.def"));
        var keysByType = new Dictionary<int, HashSet<string>>();
        foreach (var file in new[] { "gamedata.base", "Newwworld.mod", "Dialogue.mod", "rebirth.mod" })
            foreach (var r in FcsReader.ReadFile(Path.Combine(install.DataDirectory, file)).Records)
            {
                if (!keysByType.TryGetValue(r.Type, out var keys)) keysByType[r.Type] = keys = [];
                keys.UnionWith(r.Bools.Keys.Concat(r.Floats.Keys).Concat(r.Ints.Keys).Concat(r.Vector3s.Keys)
                    .Concat(r.Vector4s.Keys).Concat(r.Strings.Keys).Concat(r.Filenames.Keys).Concat(r.References.Keys));
            }

        foreach (var type in Enum.GetValues<FcsRecordType>())
        {
            var name = type.ToString();
            if (!schema.TypeNames.Contains(name) || !keysByType.TryGetValue((int)type, out var keys)) continue;

            int Hits(string t) => keys.Count(k => schema.FindField(t, k) is not null);
            int best = schema.TypeNames.Max(Hits);
            Assert.True(Hits(name) == best, $"{name} ({(int)type}): {Hits(name)} fields found, best section has {best}.");
        }
    }
}
