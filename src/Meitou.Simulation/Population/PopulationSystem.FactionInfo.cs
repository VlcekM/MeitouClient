namespace Meitou.Simulation.Population;

public sealed partial class PopulationSystem
{
    /// <summary>
    /// Upcoming Events (<c>FUN_1407f2ef0</c> per faction's <c>FactionWarMgr</c>, <c>FUN_1407ac9c0</c>): for a faction with running or queued campaigns a "-{name}-" heading; per running
    /// campaign against the player's faction that is not ending, its name, "Target:" with the target's faction and name (??? when gone) and a spacer; per queued one (no player
    /// filter) its display name and the time until it starts.
    /// </summary>
    public List<FactionInfoLine> UpcomingEvents(World world, Func<int, string?> factionName)
    {
        var lines = new List<FactionInfoLine>();
        int player = PlayerFactionOf(world);
        foreach (var war in Wars)
        {
            if (war.Actives.Count == 0 && war.Requests.Count == 0) continue;
            lines.Add(new(FactionInfoKind.Heading, "-" + (factionName(war.Faction) ?? "") + "-"));
            foreach (var inst in war.Actives)
            {
                // Phase 2 is Meitou's ending phase (the original's RETREATING and VICTORY are left out).
                if (inst.Phase == 2 || player < 0 || inst.Enemy != player) continue;
                lines.Add(new(FactionInfoKind.Name, CampaignOf(inst.CampaignId)?.Name ?? inst.CampaignId));
                string where = Exists(world, inst.Target) ? NameOf(world, inst.Target) : "???";
                lines.Add(new(FactionInfoKind.Detail, "Target:", (factionName(inst.Enemy) ?? "") + "- " + where));
                lines.Add(new(FactionInfoKind.Spacer, ""));
            }
            foreach (var request in war.Requests)
                lines.Add(new(FactionInfoKind.Queued, CampaignOf(request.CampaignId)?.DisplayName ?? request.CampaignId, "", request.Due - world.GameHours));
        }
        return lines;
    }
}