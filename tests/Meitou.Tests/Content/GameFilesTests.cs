using Meitou.Content;

namespace Meitou.Tests.Content;

public sealed class GameFilesTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "meitou-gamefiles-" + Guid.NewGuid().ToString("N"));

    public GameFilesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Data", "Animal", "Meshes"));
        File.WriteAllText(Path.Combine(_root, "Data", "Animal", "Meshes", "Mammal_beak.skeleton"), "");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_file_or_folder_written_in_another_case_is_found()
    {
        var asked = Path.Combine(_root, "data", "animal", "meshes", "mammal_beak.skeleton");
        Assert.True(GameFiles.Exists(asked));
        Assert.Equal(
            Path.Combine(_root, "Data", "Animal", "Meshes", "Mammal_beak.skeleton"),
            GameFiles.Resolve(asked),
            ignoreCase: OperatingSystem.IsWindows());

        // A folder written in another case is found too.
        Assert.NotNull(GameFiles.ExistingDirectory(Path.Combine(_root, "data", "animal")));
    }

    [Fact]
    public void A_path_that_exists_nowhere_comes_back_unchanged()
    {
        var missing = Path.Combine(_root, "data", "animal", "nothing.skeleton");
        Assert.Equal(missing, GameFiles.Resolve(missing));
        Assert.False(GameFiles.Exists(missing));
        Assert.Null(GameFiles.ExistingDirectory(Path.Combine(_root, "data", "nowhere")));
    }
}
