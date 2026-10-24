using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Production;

namespace Meitou.Simulation.Saving;

/// <summary>
/// A character's job list and jobs switch in its GAMESTATE_AI record <c>ai</c> (<b>Verified</b>, <c>OrdersReceiver</c> vfuncs 1 and 2 and 4122 sampled records; docs/game/ai-tasks.md
/// "Perma-jobs"): bool <c>jobs</c>, and per job i from 0 the int <c>pjob&lt;i&gt;</c> (task type) and the handle <c>pjobT&lt;i&gt;</c> (target). Meitou's own string <c>meitou pjob&lt;i&gt;</c>
/// (the <see cref="BuildSite.Id"/>) names a building the handle cannot (one without a saved state, or a rock's natural site) and wins on load.
/// </summary>
public static class PermaJobSave
{
    const string Type = "pjob", Target = "pjobT", MeitouSite = "meitou pjob", Switch = "jobs";

    /// <summary>Writes the list (the old entries are cleared first) and the switch.</summary>
    public static void Write(FcsRecord ai, CharacterCold cold, BuildingRegistry sites, IReadOnlyDictionary<string, FcsRecord> buildings, Func<CharacterId, Hand>? handleOf = null)
    {
        foreach (var key in ai.Ints.Keys.Where(k => k.StartsWith(Type, StringComparison.Ordinal)).ToList()) ai.Ints.Remove(key);
        foreach (var key in ai.Strings.Keys.Where(k => k.StartsWith(MeitouSite, StringComparison.Ordinal)).ToList()) ai.Strings.Remove(key);
        for (int i = 0; i < cold.Jobs.Count; i++)
        {
            var job = cold.Jobs[i];
            var hand = !job.Character.IsNone && handleOf?.Invoke(job.Character) is { IsNull: false } target ? target : job.Saved;
            if (job.Site is { } id)
            {
                hand = sites.Find(id) is { } site && buildings.TryGetValue(site.LevelPlacement is null ? BuildingKey.Of(site.Zone, id) : id, out var state) && Hand.IsComplete(state, "handle") ? Hand.Read(state, "handle") : Hand.Null;
                ai.Strings[MeitouSite + i] = id;
            }
            ai.Ints[Type + i] = (int)job.Type;
            hand.Write(ai, Target + i);
        }
        ai.Bools[Switch] = (cold.Orders & CharacterOrders.Jobs) != 0;
    }

    /// <summary>
    /// Reads them back as the original does: each job is added again through <see cref="PermaJobs.Add"/> (which turns the switch on), then the switch is read. Meitou's
    /// <see cref="Orders.JobsKey"/> in <paramref name="state"/> wins when present (older Meitou saves have only it, with a stale <c>jobs</c>).
    /// </summary>
    public static void Read(FcsRecord ai, FcsRecord? state, CharacterCold cold, BuildingRegistry sites, SaveHandles handles, IReadOnlyDictionary<FcsRecord, string> instanceOf)
    {
        for (int i = 0; ai.Ints.TryGetValue(Type + i, out int type); i++)
        {
            var hand = Hand.Read(ai, Target + i);
            // A rock's natural id is kept as it is: its site is made from the id (ProductionSystem.Natural).
            string? site = ai.Strings.TryGetValue(MeitouSite + i, out var id) && (sites.Find(id) is not null || instanceOf.Values.Contains(id) || ProductionSystem.IsNatural(id)) ? id
                : handles.Building(hand) is { } b && instanceOf.TryGetValue(b, out var key) && key.IndexOf(':') is var colon and >= 0 && sites.Find(key[(colon + 1)..]) is not null ? key[(colon + 1)..]
                : null;
            PermaJobs.Add(cold, (TaskType)type, new PermaJob((TaskType)type, site, site is null ? hand : Hand.Null));
        }
        bool? on = state is not null && state.Bools.TryGetValue(Orders.JobsKey, out bool old) ? old : ai.Bools.TryGetValue(Switch, out bool saved) ? saved : null;
        if (on is { } jobs) cold.Orders = jobs ? cold.Orders | CharacterOrders.Jobs : cold.Orders & ~CharacterOrders.Jobs;
    }
}
