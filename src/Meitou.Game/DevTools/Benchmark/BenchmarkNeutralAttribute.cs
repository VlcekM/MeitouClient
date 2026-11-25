namespace Meitou.Game.DevTools.Benchmark;

/// <summary>
/// A user setting that cannot change what the launcher benchmark measures: the benchmark overrides it (frame limit, vsync, HDR, windowed),
/// another renderer reads it (Metal), or nothing in a run reads it (file bookkeeping, menus the script never opens, key bindings). Left out of
/// the benchmark's fingerprint (BenchmarkSpec.CanonicalText, docs/bench.md "Version and fingerprint"), so adding or changing one splits no
/// results; a setting that can change a frame, the simulation or a memory peak never carries it.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
sealed class BenchmarkNeutralAttribute : Attribute;
