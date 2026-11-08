using Meitou.Game.Tutorials;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;

namespace Meitou.Game;

/// <summary>The tutorials (docs/game/ui-screens.md "Tutorials"): what their triggers see.</summary>
sealed partial class PlayerInterface
{
    /// <summary>The management tabs of the crafting and research triggers (overview tabs 3 and 2).</summary>
    const int CraftingTab = 3, ResearchTab = 2;

    /// <summary>The player's characters, buildings, selection and windows as the triggers ask for them.</summary>
    internal TutorialPoll TutorialPoll(bool loading)
    {
        var snap = session.CurrentSnapshot;
        int faction = session.World.Player.Faction;
        bool unsettled = false, aid = false, attack = false, ko = false, awake = false, sneak = false, hungry = false, playingDead = false, slave = false;
        int pcs = 0;
        var pcIds = new List<CharacterId>();
        foreach (var c in snap.Characters)
        {
            if (!c.IsPlayer || c.Body is { Dead: true }) continue;
            pcs++;
            pcIds.Add(c.Id);
            bool down = c.Body is { Unconscious: true };
            unsettled |= c.CombatMode || down || c.InBed;
            aid |= c.Body is { NeedsAid: true };
            attack |= c.InCombat;
            ko |= down && c.Body is not { PlayingDead: true };
            awake |= !down;
            sneak |= c.Stealth is not null;
            hungry |= c.Body is { Hunger: < 2 } && c.Hud is not { Robot: true };
            playingDead |= c.Body is { PlayingDead: true };
            slave |= c.Slave is not null;
        }
        var inspected = snap.Characters.FirstOrDefault(c => c.Inspected);
        bool production = false, full = false, built = false, crafting = false, research = false, unstarted = false;
        var sites = snap.Buildings.Where(b => b.Owner == faction && faction >= 0).ToDictionary(b => b.Id, StringComparer.Ordinal);
        foreach (var m in snap.Machines)
        {
            if (!sites.TryGetValue(m.Site, out var site) || m.Output is null && m.Farm is null && m.Craft is null) continue;
            production = true;
            full |= m.Status == 1 || m.Farm is { Status: Meitou.Simulation.Production.FarmStatus.OutputFull };
            built |= site.Complete && m.Output is not null;
            crafting |= site.Complete && m.Craft is not null;
        }
        foreach (var m in snap.Machines)
            if (m.TechLevel > 0 && sites.TryGetValue(m.Site, out var bench) && bench.Complete) research = true;
        foreach (var site in sites.Values) unstarted |= !site.Complete && site.Progress == 0;
        var facts = snap.TutorialFacts;
        return new TutorialPoll
        {
            EditorOrLoading = EditorOpen || loading,
            Unsettled = unsettled || facts.InPrison || facts.InCannibalCage,
            InTown = facts.InTown,
            NeedsFirstAid = aid,
            UnderAttack = attack,
            PcCount = pcs,
            Pcs = pcIds,
            Stolen = facts.Stolen,
            AnyUnconscious = ko,
            AnyConscious = awake,
            InPrison = facts.InPrison,
            InCannibalCage = facts.InCannibalCage,
            Sneaking = sneak,
            // The steal pointer (cursor 0x15): a selection over another faction's ground item.
            StealCursor = SelectedIds().Count > 0 && !pointerOverUi && StealsGroundItem(mouse),
            SelectedHasBounty = inspected is { IsPlayer: false, Bounties.Count: > 0 },
            Hungry = hungry,
            PlayingDead = playingDead,
            HasProductionBuilding = production,
            Slave = slave,
            Uniform = facts.Uniform,
            MachineFull = full,
            SelectedForSale = snap.InspectedBuilding?.SalePrice is not null,
            ProductionComplete = built,
            Placing = BuildModeOn && CurrentBuilding() is not null,
            CraftingComplete = crafting,
            ResearchComplete = research,
            ManagementTab = faithfulScreens.IsOpen("overview") ? faithfulScreens.GetTab("overview", "TabsMain") : -1,
            Blueprint = facts.Blueprint,
            UnstartedConstruction = unstarted,
            Prospecting = faithfulScreens.IsOpen("prospect"),
            SelectedStump = (Selected().FirstOrDefault() ?? inspected) is { } who && Enumerable.Range(0, 4).Any(k => ((who.LimbBits >> (2 * k)) & 3) == (int)LimbState.Stump),
        };
    }
}
