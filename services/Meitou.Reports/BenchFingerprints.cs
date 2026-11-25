namespace Meitou.Reports;

/// <summary>
/// Benchmark fingerprints that builds reported for a workload whose fingerprint later changed without the workload changing, each with the
/// fingerprint that replaced it (docs/bench.md "Version and fingerprint"). A submission's fingerprint and the stored sessions' are mapped through
/// it, so one workload has one fingerprint whatever build ran it.
/// </summary>
static class BenchFingerprints
{
    /// <summary>
    /// The current <c>bases</c> fingerprint of benchmark version 1.
    /// </summary>
    const string BasesV1 = "d20b018ddde8aec5b048c12e2e1487dd43cab387be777d0eb7a2e65a8ff01d54";

    /// <summary>
    /// The current <c>shark-flyby</c> fingerprint of benchmark version 1.
    /// </summary>
    const string SharkFlybyV1 = "f678239ac9b7af5b886f3d834e9b0325db0381afb563eee4434eac9f9dddbc8b";

    /// <summary>
    /// Retired fingerprint to its replacement. Version 1 builds up to 0.23.1 hashed the whole default user config, so a new unrelated setting
    /// changed the fingerprint: the 0.22.2 nightlies, 0.23.0 (settings opacity, mod setting presets) and 0.23.1 (monitor). Their texts differ from
    /// the current one only in those settings and in the benchmark-neutral ones (Verified: each recomputed from the 0.23.1 canonical text with
    /// those keys removed gives the fingerprint stored for that build).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Retired = new Dictionary<string, string>
    {
        ["d6e535b01ebc151e00b3d32f4576fcbbb72dae5f8bbb2e4fd3be789d7eb30e5e"] = BasesV1,
        ["02721fe03af7ae3ffee0fb68599087f9e05649b5fd5ec2a220c008b0c30304cc"] = BasesV1,
        ["67d659a21879505da04de137f67dc4bdb6be725b3b5a96db185b5a3a5d8a34a2"] = BasesV1,
        ["14acf11ceb9b3cd95a66dee0216aad9e7d8bd8065683fb3441cebd26e2784bfc"] = SharkFlybyV1,
        ["1100a617a81a6ecdb2a77284fa23f324aa0e7374947aa9d29aec049992a26328"] = SharkFlybyV1,
        ["72033f87a788415709188ca4ec922d448dd94bc0a11cc520caf997e054c7ad5a"] = SharkFlybyV1,
    };

    /// <summary>
    /// <paramref name="fingerprint"/>'s replacement when it is retired, else itself.
    /// </summary>
    public static string Current(string fingerprint) => Retired.GetValueOrDefault(fingerprint, fingerprint);
}
