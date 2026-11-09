namespace Meitou.Tests.Save;

/// <summary>
/// Scratch folders for the save tests (they never read the user's own saves).
/// </summary>
public static class SaveSamples
{
    public static string Temp(string name)
    {
        string path = Path.Combine(Path.GetTempPath(), "meitou-save-tests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}
