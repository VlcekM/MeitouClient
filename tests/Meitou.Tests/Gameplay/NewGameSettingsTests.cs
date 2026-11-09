using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Game;
using Meitou.Game.Title;
using static Meitou.Tests.Save.SaveSamples;

namespace Meitou.Tests.Gameplay;

public class NewGameSettingsTests
{
    [Fact]
    public void SaveRoundTripKeepsAllOptionsAndOldSavesUseNeutralValues()
    {
        var options = new NewGameSettings();
        float[] values = [3.25f, 2, 1.5f, .75f, 1.25f, 2, 3.5f];
        for (int i = 0; i < values.Length; i++) options = options.Set(i, values[i]);
        var game = new SaveBuilder(17).Game;
        options.Write(game.Camera);
        string folder = Temp("difficulty");
        try
        {
            game.Write(folder);
            var loaded = NewGameSettings.Read(SaveGame.Load(folder).Camera);
            for (int i = 0; i < values.Length; i++) Assert.Equal(values[i], loaded[i]);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        var old = NewGameSettings.Read(new SaveCamera(new FcsRecord()));
        for (int i = 0; i < values.Length; i++) Assert.Equal(1, old[i]);
    }

    [Fact]
    public void ReadsTheOriginalsKeysAsItsLoaderDoes()
    {
        var camera = new SaveCamera(new FcsRecord());
        camera.Record.Floats["ht"] = 8;
        camera.Record.Bools["bl"] = false;
        var withoutCod = NewGameSettings.Read(camera);
        Assert.Equal(1, withoutCod[0]);
        Assert.True(withoutCod.BanditsLoot);

        camera.Record.Floats["cod"] = 2;
        var loaded = NewGameSettings.Read(camera);
        Assert.Equal(8, loaded[0]);
        Assert.Equal(2, loaded[1]);
        Assert.Equal(0, loaded[2]);
        Assert.Equal(1, loaded[6]);
        Assert.False(loaded.BanditsLoot);
    }
}
