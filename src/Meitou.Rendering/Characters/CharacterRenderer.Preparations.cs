using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.Characters;

namespace Meitou.Rendering.Characters;

internal sealed unsafe partial class CharacterRenderer
{
    internal static bool PreparationBudgetAvailable(int limit, int built, double milliseconds, bool covered) =>
        limit <= 0 || ((covered || built < limit) && milliseconds < (covered ? CoveredBudgetMs : BuildBudgetMs));

    // Called after real actors and the distant list. These requests never enter live/state, palette, material or draw gathers.
    void PrepareAssets(Vector3 eye, float reach, ref int built, ref double buildMs)
    {
        // Actual draws can still compile lazily if their budget is spent, but speculative shaders must never go first.
        for (int i = 0; i < liveCount; i++)
        {
            if (live[i].Shaders is not { } shaders || runtimePrograms?.Contains(shaders) == true) continue;
            if (!PreparationBudgetAvailable(LoadBudget, built, buildMs, Covered)) return;
            PrepareProgram(shaders, ref built, ref buildMs);
        }
        foreach (ref readonly var instance in CollectionsMarshal.AsSpan(Draws.Preparations))
        {
            if (instance.Geometry?.IsRetired == true) continue;
            if (AssetFor(in instance, eye, reach, ref built, ref buildMs, preparation: true) is null) continue;
            if (instance.Geometry?.Shaders is not { } shaders || runtimePrograms?.Contains(shaders) == true) continue;
            if (!PreparationBudgetAvailable(LoadBudget, built, buildMs, Covered)) continue;
            PrepareProgram(shaders, ref built, ref buildMs);
        }
    }

    void PrepareProgram(RuntimeCharacterShaderSet shaders, ref int built, ref double buildMs)
    {
        long start = Stopwatch.GetTimestamp();
        Gpu.EnsureFrame();
        _ = Programs(shaders);
        buildMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        built++;
    }
}
