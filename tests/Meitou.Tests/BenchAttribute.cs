using Xunit.v3;

namespace Meitou.Tests;

/// <summary>
/// A test that asserts a timing: it fails on a loaded machine for reasons that are not bugs. Run the rest without it with
/// <c>--filter "Category!=Bench"</c>; the benchmarks on their own with <c>--filter "Category=Bench"</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class BenchAttribute : Attribute, ITraitAttribute
{
    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() => [new("Category", "Bench")];
}
