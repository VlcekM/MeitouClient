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
    /// The engine's range for a short-range patrol point round the home town, in units (engine choice, part of the walking stand-in: the original's
    /// PATROL_SHORTRANGE draws a point in a neighbouring 4608 sector with the loaded patrol's picker, <c>FUN_1407ee790</c>, <b>Verified</b>, but its
    /// mover gets no speed, so it stands still, <b>Observed</b>; docs/game/factions-squads-towns.md "Off-screen world").
    /// </summary>
    public const float PatrolRange = 3000;

    /// <summary>
    /// A short-range patrol point counts as reached within this distance (Observed).
    /// </summary>
    public const float PatrolArrive = 500;

    /// <summary>
    /// The AI package and unloaded-squad job of a new or loaded platoon of faction <paramref name="factionId"/>: its package named
    /// <paramref name="savedPackage"/>, else the one a fresh blackboard of its packages (the template's and the faction's <c>AI fallback</c>,
    /// <see cref="AiDatabase.Packages(SquadTemplate, string, bool)"/>; <paramref name="restored"/>: rebuilt from a saved record) starts with.
    /// </summary>
    public static (AiPackage? Package, PlatoonJob Job) For(AiDatabase ai, SquadTemplate template, string? factionId, bool restored = false,
        string? savedPackage = null)
    {
        var packages = ai.Packages(template, factionId, restored);
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

    /// <summary>
    /// The unloaded job a war package's start writes over its <c>unloaded func</c> (package <c>+0x78</c>, campaign slot 12 <c>FUN_1409d4cc0</c>,
    /// <b>Verified</b>): 5 TRAVEL_TARGET for the campaign's lead platoon, 6 TRAVEL_TARGET_FAST for any other; null for another package or a platoon
    /// with no campaign.
    /// </summary>
    internal static PlatoonJob? WarJob(Factions.Platoon platoon, AiPackage running) =>
        platoon.Campaign is { } campaign && PackageClasses.Of(running.Signal) is PackageClass.WarBase or PackageClass.WarAssault
            or PackageClass.WarAssaultCannibal
            ? campaign.Leader == platoon.Id ? PlatoonJob.TravelTarget : PlatoonJob.TravelTargetFast
            : null;
}