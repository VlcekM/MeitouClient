using Xunit.v3;

namespace Meitou.Tests;

/// <summary>
/// Marks a test that needs the Kenshi install or a Vulkan device, or takes long. The quick run leaves these out:
/// <c>dotnet test --filter "Category!=Slow"</c>; a plain <c>dotnet test</c> runs everything.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class SlowAttribute : Attribute, ITraitAttribute
{
    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() => [new("Category", "Slow")];
}
