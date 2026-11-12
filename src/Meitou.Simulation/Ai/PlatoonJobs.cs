using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The editor's <c>UnloadedPlatoonJob</c> (docs/game/ai.md "Enums"): what an unloaded squad does, from its package's <c>unloaded func</c>.
/// </summary>
public enum PlatoonJob : byte
{
    /// <summary>
    /// Mode 0 and the modes without a branch in the original (1 PATROL_TOWN, 3 PATROL_LONGRANGE): the engine's travel stand-in, town to town and
    /// home.
    /// </summary>
    None = 0,
    /// <summary>
    /// Mode 2: walks between points near its home town, a new one when within 500 units of the last (docs/game/ai.md "Unloaded squads", Observed).
    /// </summary>
    PatrolShortRange = 2,
    /// <summary>
    /// Mode 4: goes home and stays.
    /// </summary>
    GoHome = 4,
    /// <summary>
    /// Modes 5 and 6: travels to its target (here the travel stand-in's towns; the fast one runs).
    /// </summary>
    TravelTarget = 5,
    /// <summary>
    /// Mode 6: travels to its target, running (see <see cref="TravelTarget"/>).
    /// </summary>
    TravelTargetFast = 6,
}

/// <summary>
/// The package a roaming platoon runs and the off-screen job it gives (docs/simulation.md "AI", Off screen). The platoon's package is chosen by the
/// base-class slot rule (<see cref="Blackboard.Update"/> without rules: the plain packages, else a lone slot-0 one), unless a save named it
/// (<c>currentPackage</c>).
/// </summary>
public static class PlatoonJobs
{
    /// <summary>
    /// The engine's range for a short-range patrol point round the home town, in units (the original's is <b>Unknown</b>).
    /// </summary>
    public const float PatrolRange = 3000;

    /// <summary>
    /// A short-range patrol point counts as reached within this distance (Observed).
    /// </summary>
    public const float PatrolArrive = 500;

    /// <summary>
    /// The AI package and unloaded-squad job of a new or loaded platoon: the template's package named <paramref name="savedPackage"/>, else the one a
    /// fresh blackboard of the template's packages starts with.
    /// </summary>
    public static (AiPackage? Package, PlatoonJob Job) For(AiDatabase ai, SquadTemplate template, string? savedPackage = null)
    {
        var packages = ai.Packages(template);
        AiPackage? package = null;
        foreach (var p in packages)
        {
            if (p.Package.Id == savedPackage)
            {
                package = p.Package;
                break;
            }
        }

        if (package is null)
        {
            var board = new Blackboard(packages);
            board.Update();
            package = board.Current;
        }

        return (package, JobOf(package?.Unloaded ?? 0));
    }

    /// <summary>
    /// The unloaded job of a package's <c>unloaded</c> mode (none for an unknown mode).
    /// </summary>
    public static PlatoonJob JobOf(int unloaded) => unloaded switch
    {
        2 => PlatoonJob.PatrolShortRange,
        4 => PlatoonJob.GoHome,
        5 => PlatoonJob.TravelTarget,
        6 => PlatoonJob.TravelTargetFast,
        _ => PlatoonJob.None,
    };
}