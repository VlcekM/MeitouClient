using Meitou.Content;
using Meitou.Data.Fcs;

namespace Meitou.Tests.Fcs;

public class FcsSchemaTests
{
    /// <summary>
    /// Every <see cref="FcsRecordType"/> name that has an fcs.def section must be (one of) the best-matching
    /// sections for the fields its records actually use in the base game.
    /// </summary>
    [Fact]
    [Slow]
    public void Record_type_names_match_fcs_def_against_base_game()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");

        var schema = FcsSchema.Load(Path.Combine(install!.Root, "fcs.def"));
        var keysByType = new Dictionary<int, HashSet<string>>();
        foreach (var file in new[] { "gamedata.base", "Newwworld.mod", "Dialogue.mod", "rebirth.mod" })
        {
            foreach (var r in FcsReader.ReadFile(Path.Combine(install.DataDirectory, file)).Records)
            {
                if (!keysByType.TryGetValue(r.Type, out var keys)) keysByType[r.Type] = keys = [];
                keys.UnionWith(r.Bools.Keys.Concat(r.Floats.Keys).Concat(r.Ints.Keys).Concat(r.Vector3S.Keys)
                    .Concat(r.Vector4S.Keys).Concat(r.Strings.Keys).Concat(r.Filenames.Keys).Concat(r.References.Keys));
            }
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
