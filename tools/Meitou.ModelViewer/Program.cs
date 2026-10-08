using System.Diagnostics;
using System.Globalization;
using Meitou.ModelViewer;
using Silk.NET.Windowing;

System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
args = SmokeTest.Strip(args);
// Started without options (a double-click on the release's exe): the world at The Hub.
if (args.Length == 0) args = ["--world", "--town", "The Hub"];
if (args.Contains("--world") || args.Contains("--view") || args.Contains("--bench-compare")) return WorldApp.Run(args);
if (args.Contains("--impostor-preview") || args.Contains("--impostor-bake-all")) return ImpostorApp.Run(args);
// The mesh and character viewers were removed in phase 8 (DECISIONS 23; docs/character-viewer.md, the tag model-viewer-last has them).
Console.WriteLine("meitou-viewer --world [options]   (meitou-viewer --world --help lists the options)");
Console.WriteLine(ImpostorApp.Usage);
Console.WriteLine("The mesh and character viewers were removed (DECISIONS 23, docs/character-viewer.md).");
return 2;

/// <summary><c>--quit-after &lt;s&gt;</c> (any interactive mode): the window closes itself after that many seconds and the frames drawn are printed (an unattended smoke test).</summary>
static class SmokeTest
{
    static double? seconds;
    static long frames;
    static readonly Stopwatch clock = new();

    /// <summary>Takes <c>--quit-after</c> out of <paramref name="args"/>.</summary>
    public static string[] Strip(string[] args)
    {
        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--quit-after" && i + 1 < args.Length) seconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
            else rest.Add(args[i]);
        }
        return rest.ToArray();
    }

    /// <summary>Counts a drawn frame.</summary>
    public static void Frame()
    {
        if (frames++ == 0) clock.Start();   // the time starts at the first frame, after loading
    }

    /// <summary>Closes <paramref name="window"/> once the time is up (call from the update).</summary>
    public static void Check(IWindow window)
    {
        if (seconds is not { } s || frames == 0 || clock.Elapsed.TotalSeconds < s) return;
        double t = clock.Elapsed.TotalSeconds;
        Console.WriteLine($"smoke     {frames} frames in {t:0.0} s: {frames / t:0} fps on average");
        seconds = null;
        window.Close();
    }
}
