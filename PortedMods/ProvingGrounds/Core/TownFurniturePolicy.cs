namespace ProvingGrounds.Core;

/// <summary>C++: TownFurniturePolicy.h (header-only, namespace <c>TownFurniturePolicy</c>).</summary>
public static class TownFurniturePolicy
{
    /// <summary><paramref name="handleValid"/> is unused in the C++ too (kept for the same signature).</summary>
    // C++: TownFurniturePolicy.h:3
    public static bool Occupied(bool handleValid, bool characterResolved, bool characterValid) { return characterResolved && characterValid; }
}
