using Meitou.Content;

namespace Meitou.Tests.Content;

/// <summary>
/// The launcher's check for stub patches that load below the mods they name (docs/launcher.md "Mod list").
/// </summary>
public sealed class ModBlankingTests
{
    static readonly string[] Meshes = ["male mesh", "female mesh"];

    static ModRecordScan Full(string id) => new([new ScannedRecord(id, IsNew: true, Removed: false, Filled: Meshes, Blank: [])]);

    static ModRecordScan Stub(string id) => new([new ScannedRecord(id, IsNew: true, Removed: false, Filled: [], Blank: Meshes)]);

    static ModRecordScan Removal(string id) => new([new ScannedRecord(id, IsNew: false, Removed: true, Filled: [], Blank: [])]);

    static Dictionary<string, IReadOnlyList<BlankedFile>> Check(params (string Name, ModRecordScan? Scan)[] mods) => ModBlanking.Check([], mods);

    [Fact]
    public void A_stub_below_the_mod_it_names_blanks_it()
    {
        var blanked = Check(("Race", Full("10-Race.mod")), ("Patch", Stub("10-Race.mod")));
        Assert.Equal("Patch", Assert.Single(blanked).Key);
        Assert.Equal([new BlankedFile("Race", 1)], blanked["Patch"]);
    }

    [Fact]
    public void A_stub_above_the_mod_it_names_is_overwritten() =>
        Assert.Empty(Check(("Patch", Stub("10-Race.mod")), ("Race", Full("10-Race.mod"))));

    [Fact]
    public void A_record_removed_in_between_has_no_paths_left_to_blank() =>
        Assert.Empty(Check(("Race", Full("10-Race.mod")), ("Remover", Removal("10-Race.mod")), ("Patch", Stub("10-Race.mod"))));

    [Fact]
    public void A_stub_for_a_mod_that_is_not_in_the_list_blanks_nobody() =>
        Assert.Empty(Check(("Copies", Full("10-Absent.mod")), ("Patch", Stub("10-Absent.mod"))));
}
