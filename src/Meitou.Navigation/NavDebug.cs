namespace Meitou.Navigation;

/// <summary>
/// Debug switches of the navmesh tooling, read from the environment once. Both only add output to <c>meitou-tools navmesh</c>; neither changes a mesh
/// (docs/game/pathfinding.md, "Tool").
/// </summary>
internal static class NavDebug
{
    /// <summary><c>NAV_VERBOSE</c>: painters, seeds, interiors, door joins and path points are listed.</summary>
    public static readonly bool Verbose = Environment.GetEnvironmentVariable("NAV_VERBOSE") is not null;

    /// <summary><c>NAV_DUMP</c>: <c>--near</c> also lists every field of the building records.</summary>
    public static readonly bool Dump = Environment.GetEnvironmentVariable("NAV_DUMP") is not null;
}
