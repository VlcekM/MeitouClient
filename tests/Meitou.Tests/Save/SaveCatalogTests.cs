using Meitou.Data.Save;

namespace Meitou.Tests.Save;

public class SaveCatalogTests
{
    [Theory]
    [InlineData("../escape")][InlineData("CON")][InlineData("slot.")][InlineData("a/b")]
    public void NamesCannotEscapeTheSaveRootOrUseReservedFileNames(string name) =>
        Assert.Throws<ArgumentException>(() => SaveCatalog.Folder(Path.GetTempPath(), name));

    [Fact]
    public void SlotsKeepTheirSourceAndReadHeaderDetailsWithoutReadingTheWorld()
    {
        string root = Path.Combine(Path.GetTempPath(), "meitou-catalog-" + Guid.NewGuid());
        try
        {
            string own = Path.Combine(root, "own"), original = Path.Combine(root, "original");
            void Slot(string parent, string name, int day)
            {
                var game = new SaveBuilder().Game;
                game.Camera.Day = day;
                game.Camera.PlayerMoney = 123;
                game.Camera.Members = 2;
                string folder = SaveCatalog.Folder(parent, name);
                Directory.CreateDirectory(folder);
                using var output = File.Create(Path.Combine(folder, SaveFolder.QuickSaveName));
                game.Quick.Write(output);
            }
            Slot(own, "same-name", 7);
            Slot(original, "same-name", 9);
            Directory.CreateDirectory(Path.Combine(own, "not-a-save"));
            var slots = SaveCatalog.Scan(own, original, own);

            Assert.Equal(2, slots.Count);
            Assert.Single(slots, s => s.Writable);
            Assert.Single(slots, s => !s.Writable);

            var info = SaveCatalog.ReadInfo(slots.Single(s => s.Writable));

            Assert.Equal(7, info.Day);
            Assert.Equal(123, info.Funds);
            Assert.Equal(2, info.Members);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
