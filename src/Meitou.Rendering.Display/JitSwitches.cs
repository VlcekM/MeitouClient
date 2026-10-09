using System.Diagnostics;

namespace Meitou.Rendering.Display;

/// <summary>
/// <c>--tiered-jit</c> and <c>--pgo</c> (game and viewer): the runtime's JIT mode is fixed when the process starts (the projects ship with tiered
/// compilation and tiered PGO off, DECISIONS 11 and 19), so asking for another one starts the same executable again with the runtime's
/// <c>DOTNET_TieredCompilation</c> / <c>DOTNET_TieredPGO</c> variables set, which take precedence over the runtimeconfig (Observed 2026-10-09 with a
/// test program: methods tier up with the variables set). The first process waits and returns the second's exit code. <c>--pgo</c> implies
/// <c>--tiered-jit</c> (PGO profiles in tier 0). <c>--no-tiered-jit</c> / <c>--no-pgo</c> force the shipped mode against inherited variables.
/// </summary>
public static class JitSwitches
{
    const string Relaunched = "MEITOU_JIT_RELAUNCHED";

    /// <summary>Takes the switches out of <paramref name="args"/>; a non-null <paramref name="exitCode"/> means the run happened in a relaunched
    /// process and the caller returns it.</summary>
    public static string[] Apply(string[] args, out int? exitCode)
    {
        exitCode = null;
        bool? tiered = null, pgo = null;
        var rest = new List<string>(args.Length);
        foreach (var a in args)
            switch (a)
            {
                case "--tiered-jit": tiered = true; break;
                case "--no-tiered-jit": tiered = false; pgo = false; break;
                case "--pgo": pgo = true; tiered = true; break;
                case "--no-pgo": pgo = false; break;
                default: rest.Add(a); break;
            }
        bool relaunched = Environment.GetEnvironmentVariable(Relaunched) == "1";
        if (tiered is null && pgo is null && !relaunched) return [.. rest];

        bool wantTiered = tiered ?? Current("DOTNET_TieredCompilation"), wantPgo = (pgo ?? Current("DOTNET_TieredPGO")) && wantTiered;
        if (relaunched || (Current("DOTNET_TieredCompilation") == wantTiered && Current("DOTNET_TieredPGO") == wantPgo))
        {
            Console.WriteLine($"jit       tiered compilation {(wantTiered ? "on" : "off")}, PGO {(wantPgo ? "on" : "off")}{(relaunched ? " (relaunched)" : "")}");
            return [.. rest];
        }

        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("no process path to relaunch")) { UseShellExecute = false };
        foreach (var a in rest) start.ArgumentList.Add(a);
        start.Environment["DOTNET_TieredCompilation"] = wantTiered ? "1" : "0";
        start.Environment["DOTNET_TieredPGO"] = wantPgo ? "1" : "0";
        start.Environment[Relaunched] = "1";
        using var child = Process.Start(start)!;
        child.WaitForExit();
        exitCode = child.ExitCode;
        return [.. rest];
    }

    /// <summary>The mode this process runs in as far as the variables tell (unset: the shipped off).</summary>
    static bool Current(string name) => Environment.GetEnvironmentVariable(name) is "1" or "true";
}
