using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay.Construction;

/// <summary>
/// The research gate of the build menu (docs/game/buildings-production.md "Build menu"): the completed RESEARCH records and the BUILDINGs their
/// <c>enable buildings</c> lists unlock. Pure over the database; the world keeps the completed set
/// (<c>Meitou.Simulation.Construction.ResearchState</c>).
/// </summary>
public static class ResearchGate
{
    /// <summary>
    /// <c>_Default Start</c> (level 0), the research a new game finishes when its start-off's <c>research</c> list is empty (<c>FUN_140872e00</c>):
    /// the string is a constant of the executable (<b>Verified</b>: the exe holds both <c>5359-gamedata.base</c> and <c>_Default Start</c>) and the
    /// five sample saves list it as their only <c>finished0</c> (<b>Observed</b>).
    /// </summary>
    public const string DefaultStartId = "5359-gamedata.base";

    /// <summary>
    /// The RESEARCH ids a new game starts with: the start-off's own without repeats, or the default start research when it lists none (and the
    /// database has it).
    /// </summary>
    public static List<string> StartResearch(GameDatabase db, NewGameStart? start)
    {
        var ids = new List<string>();

        void Add(string id)
        {
            if (db.Find(id) is { Type: FcsRecordType.Research } && !ids.Contains(id, StringComparer.Ordinal))
                ids.Add(id);
        }

        if (start is not null)
            foreach (var link in start.Research) Add(link.Id);

        if (start is null || start.Research.Count == 0) Add(DefaultStartId);
        return ids;
    }

    /// <summary>
    /// The BUILDING ids the completed research unlocks (ids of other records or unknown research are ignored). <paramref name="completed"/> holds a
    /// research id once per finished repetition. With <c>repeats</c> above 0 an <c>enable buildings</c> entry unlocks once repetition v0 is finished;
    /// without repeats every entry unlocks when the research is (<b>Verified</b>, <c>FUN_1408320a0</c>, <c>FUN_140831e60</c>).
    /// </summary>
    public static HashSet<string> Unlocked(GameDatabase db, IEnumerable<string> completed)
    {
        var unlocked = new HashSet<string>(StringComparer.Ordinal);
        var times = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in completed) times[id] = times.GetValueOrDefault(id) + 1;

        foreach (var (id, finished) in times)
        {
            if (db.Find(id) is not { Type: FcsRecordType.Research } research) continue;

            bool repeats = research.GetInt("repeats") > 0;
            foreach (var building in research.GetReferences("enable buildings"))
            {
                bool due = !repeats || (building.Values.Value0 >= 0 && building.Values.Value0 < finished);
                if (due && db.Find(building.TargetStringId) is { Type: FcsRecordType.Building })
                    unlocked.Add(building.TargetStringId);
            }
        }

        return unlocked;
    }
}
