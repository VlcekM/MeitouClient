using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;

namespace Meitou.Tests.Simulation;

/// <summary>
/// The campaign half of the original's platoon keep test (FUN_1407ec570): held by a campaign record that is not in phase 2, and not in spawn mode 3.
/// </summary>
public class PlatoonKeepTests
{
    static Platoon Of(CampaignInstance? campaign, bool replacedHome = false) =>
        new() { Id = 1, Key = 1, Origin = 0, TemplateId = "t", Campaign = campaign, ReplacedHome = replacedHome };

    static CampaignInstance Instance(int phase) => new() { Faction = 0, Id = 0, CampaignId = "c", Phase = phase };

    [Fact]
    public void A_campaign_force_is_kept_until_its_record_is_in_phase_2()
    {
        Assert.False(PopulationSystem.CampaignKept(Of(null)));
        Assert.True(PopulationSystem.CampaignKept(Of(Instance(0))));
        Assert.True(PopulationSystem.CampaignKept(Of(Instance(1))));
        Assert.False(PopulationSystem.CampaignKept(Of(Instance(2))));
    }
}
