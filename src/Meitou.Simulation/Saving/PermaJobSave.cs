using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Player;
using Meitou.Simulation.Production;

namespace Meitou.Simulation.Saving;

/// <summary>
/// A character's job list and jobs switch in its GAMESTATE_AI record <c>ai</c> (<b>Verified</b>, <c>OrdersReceiver</c> vfuncs 1 and 2 and 4122
/// sampled records; docs/game/ai-tasks.md "Perma-jobs"): bool <c>jobs</c>, and per job i from 0 the int <c>pjob&lt;i&gt;</c> (task type) and the
/// handle <c>pjobT&lt;i&gt;</c> (target). Meitou's own string <c>meitou pjob&lt;i&gt;</c> (the <see cref="BuildSite.Id"/>) names a building the
/// handle cannot (one without a saved state, or a rock's natural site) and wins on load. A mod's own job type
/// (<see cref="SimulationExtensions.JobTypes"/>) stays out of that list: Meitou's int <c>meitou mjob&lt;k&gt;</c> (task type), int
/// <c>meitou mjobAt&lt;k&gt;</c> (its place: how many game jobs come before it) and string <c>meitou mjob&lt;k&gt;</c> (its building id) keep it.
/// </summary>
public static class PermaJobSave
{
    /// <summary>
    /// The GAMESTATE_AI key prefixes of a job (task type, target handle, Meitou's site id) and the jobs switch key.
    /// </summary>
    const string Type = "pjob", Target = "pjobT", MeitouSite = "meitou pjob", Switch = "jobs";

    /// <summary>
    /// The GAMESTATE_AI key prefixes of a mod's job: its task type and building id, and its place in the list.
    /// </summary>
    const string ModJob = "meitou mjob", ModJobAt = "meitou mjobAt";

    /// <summary>
    /// The handle key of job <paramref name="i"/>'s target.
    /// </summary>
    public static string TargetKey(int i) => Target + i;

    /// <summary>
    /// The jobs the original's list holds, with their numbers in it: every job but the mods' own (<paramref name="modJobs"/>), numbered in order.
    /// </summary>
    public static IEnumerable<(int Key, PermaJob Job)> GameJobs(CharacterCold cold, IReadOnlyDictionary<TaskType, string>? modJobs)
    {
        int key = 0;
        foreach (var job in cold.Jobs)
            if (modJobs?.ContainsKey(job.Type) != true) yield return (key++, job);
    }

    /// <summary>
    /// Writes the list (the old entries are cleared first), the mods' own jobs under Meitou keys (<paramref name="modJobs"/>) and the switch.
    /// </summary>
    public static void Write(
        FcsRecord ai, CharacterCold cold, BuildingRegistry sites, IReadOnlyDictionary<string, FcsRecord> buildings,
        Func<CharacterId, Hand>? handleOf = null, IReadOnlyDictionary<TaskType, string>? modJobs = null)
    {
        // Only the list's own numbered keys: other keys starting "pjob" (an original plugin's) stay as read.
        var oldInts = ai.Ints.Keys
            .Where(k => IsJobKey(k) || Starts(k, ModJob))
            .ToList();
        foreach (var key in oldInts)
            ai.Ints.Remove(key);

        var oldStrings = ai.Strings.Keys
            .Where(k => Starts(k, MeitouSite) || Starts(k, ModJob))
            .ToList();
        foreach (var key in oldStrings)
            ai.Strings.Remove(key);

        WriteModJobs(ai, cold, modJobs);
        foreach (var (i, job) in GameJobs(cold, modJobs))
        {
            var hand = !job.Character.IsNone && handleOf?.Invoke(job.Character) is { IsNull: false } target ? target : job.Saved;
            if (job.Site is { } id)
            {
                hand = sites.Find(id) is { } site
                    && buildings.TryGetValue(site.LevelPlacement is null ? BuildingKey.Of(site.Zone, id) : id, out var state)
                    && Hand.IsComplete(state, "handle")
                    ? Hand.Read(state, "handle")
                    : Hand.Null;
                ai.Strings[MeitouSite + i] = id;
            }

            ai.Ints[Type + i] = (int)job.Type;
            hand.Write(ai, Target + i);
        }

        ai.Bools[Switch] = (cold.Orders & CharacterOrders.Jobs) != 0;
    }

    /// <summary>
    /// Whether key <paramref name="key"/> starts with <paramref name="prefix"/>.
    /// </summary>
    static bool Starts(string key, string prefix) => key.StartsWith(prefix, StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="key"/> is one of the job list's numbered keys (<c>pjob&lt;i&gt;</c>, a <c>pjobT&lt;i&gt;</c> handle part).
    /// </summary>
    static bool IsJobKey(string key) =>
        (key.StartsWith(Target, StringComparison.Ordinal) || key.StartsWith(Type, StringComparison.Ordinal))
        && CharacterSaveFields.IsEngineKey(CharacterSaveRecord.Orders, key);

    /// <summary>
    /// The mods' own jobs (<paramref name="modJobs"/>), each with its type, its place (how many game jobs come before it) and its building id; a
    /// character target is not kept.
    /// </summary>
    static void WriteModJobs(FcsRecord ai, CharacterCold cold, IReadOnlyDictionary<TaskType, string>? modJobs)
    {
        if (modJobs is not { Count: > 0 }) return;

        int k = 0, gameJobs = 0;
        foreach (var job in cold.Jobs)
        {
            if (!modJobs.ContainsKey(job.Type))
            {
                gameJobs++;
                continue;
            }

            ai.Ints[ModJob + k] = (int)job.Type;
            ai.Ints[ModJobAt + k] = gameJobs;
            if (job.Site is { } site) ai.Strings[ModJob + k] = site;
            k++;
        }
    }

    /// <summary>
    /// Reads them back as the original does: each job is added again through <see cref="PermaJobs.Add"/> (which turns the switch on), then the mods'
    /// own jobs go back in their places while their type is registered (<paramref name="modJobs"/>; a job of a mod no longer loaded is dropped), then
    /// the switch is read. Meitou's <see cref="Orders.JobsKey"/> in <paramref name="state"/> wins when present (older Meitou saves have only it,
    /// with a stale <c>jobs</c>).
    /// </summary>
    public static void Read(
        FcsRecord ai, FcsRecord? state, CharacterCold cold, BuildingRegistry sites, SaveHandles handles,
        IReadOnlyDictionary<FcsRecord, string> instanceOf,
        IReadOnlySet<string> instances, IReadOnlyDictionary<TaskType, string>? modJobs = null)
    {
        // The list's length after each saved job, so a mod's job goes back after the game jobs that came before it, whether or not each was
        // added again.
        var lengths = new List<int>();
        for (int i = 0; ai.Ints.TryGetValue(Type + i, out int type); i++)
        {
            var hand = Hand.Read(ai, Target + i);
            string? site = ai.Strings.TryGetValue(MeitouSite + i, out var id) && IsKnownSite(id, sites, instances)
                ? id
                : handles.Building(hand) is { } b && instanceOf.TryGetValue(b, out var key) && key.IndexOf(':') is var colon and >= 0
                    && sites.Find(key[(colon + 1)..]) is not null
                    ? key[(colon + 1)..]
                    : null;
            PermaJobs.Add(cold, (TaskType)type, new PermaJob((TaskType)type, site, site is null ? hand : Hand.Null));
            lengths.Add(cold.Jobs.Count);
        }

        ReadModJobs(ai, cold, sites, instances, lengths, modJobs);

        bool? on = state is not null && state.Bools.TryGetValue(Orders.JobsKey, out bool old)
            ? old
            : ai.Bools.TryGetValue(Switch, out bool saved)
                ? saved
                : null;
        if (on is { } jobs) cold.Orders = jobs ? cold.Orders | CharacterOrders.Jobs : cold.Orders & ~CharacterOrders.Jobs;
    }

    /// <summary>
    /// The mods' own jobs back in their places, those of a registered type only: each after the jobs read back from the saved game jobs before it
    /// (<paramref name="lengths"/>, the list's length after each) and the mods' jobs already put back. A building id that names no site is dropped.
    /// </summary>
    static void ReadModJobs(
        FcsRecord ai, CharacterCold cold, BuildingRegistry sites, IReadOnlySet<string> instances, List<int> lengths,
        IReadOnlyDictionary<TaskType, string>? modJobs)
    {
        int inserted = 0;
        for (int k = 0; ai.Ints.TryGetValue(ModJob + k, out int type); k++)
        {
            if (modJobs?.ContainsKey((TaskType)type) != true) continue;

            int before = ai.Ints.TryGetValue(ModJobAt + k, out int saved) ? Math.Clamp(saved, 0, lengths.Count) : lengths.Count;
            int at = (before == 0 ? 0 : lengths[before - 1]) + inserted;
            string? site = ai.Strings.TryGetValue(ModJob + k, out var id) && IsKnownSite(id, sites, instances) ? id : null;
            cold.Jobs.Insert(Math.Min(at, cold.Jobs.Count), new PermaJob((TaskType)type, site, Hand.Null));
            inserted++;
        }
    }

    /// <summary>
    /// Whether a saved building id <paramref name="id"/> is kept: a registered site, or a level instance's, or a rock's natural id, a town dummy's
    /// or a town turret's, which are kept as they are: the site is found from the id once its town has registered (ProductionSystem.Natural,
    /// TownDummy) or its machine is read back (TownMachineSave).
    /// </summary>
    static bool IsKnownSite(string id, BuildingRegistry sites, IReadOnlySet<string> instances) =>
        sites.Find(id) is not null
        || instances.Contains(id)
        || ProductionSystem.IsNatural(id)
        || Locks.FixtureRegistry.IsDummy(id)
        || Locks.FixtureRegistry.IsSeat(id)
        || ProductionSystem.IsTownWork(id);
}
