using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Kidnap;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The rules of the prisoner and slaver package classes (docs/game/ai.md "Prisoner and slaver packages", <b>Observed</b> by decompilation of each
/// class's start test, finished test and activation; what the squads do while one runs is <see cref="CaptiveSystem"/>'s and
/// <see cref="KidnapSystem"/>'s).
/// </summary>
public sealed partial class AiSystem
{
    /// <summary>
    /// The nearest town of <paramref name="faction"/> that is not a nest marker (<c>FUN_140927bb0</c>); -1: none.
    /// </summary>
    int NearestTownOf(Vector3 at, int faction)
    {
        int best = -1;
        float bestD = float.MaxValue;
        foreach (var site in data.Sites)
        {
            if (site.Index >= data.SiteFactions.Count || data.SiteFactions[site.Index] != faction
                || site.Town.Type is TownType.NestMarker or TownType.Null)
                continue;

            float d = Vector2.DistanceSquared(Flat(at), Flat(site.Position));
            if (d < bestD) (best, bestD) = (site.Index, d);
        }

        return best;
    }

    /// <summary>
    /// The town the squad leader stands in and its faction (-1 each: none).
    /// </summary>
    (int Site, int Faction) LeaderTown(World world, Squad squad)
    {
        if (!world.Characters.TryResolveNext(squad.Leader, out int s)) return (-1, -1);

        int site = TownAt(world.Characters.Next[s].Position);
        return (site, site >= 0 && site < data.SiteFactions.Count ? data.SiteFactions[site] : -1);
    }

    static bool AnyCarrying(World world, Squad squad)
    {
        var table = world.Characters;
        foreach (var m in squad.Members)
            if (table.TryResolveNext(m, out int s) && !table.Cold(s)!.Carrying.IsNone) return true;

        return false;
    }

    /// <summary>
    /// The town whose cages a gathering squad takes prisoners from (<c>PrisonerGathering</c> slot 19, <c>FUN_140290b70</c> / <c>FUN_140295b10</c>):
    /// the leader's town, else the nearest town of its faction within 3062 (squared 9.375e6); not a slave camp or prison. It has a prisoner when a
    /// cage of the town whose owner is not the player's faction and is its own or an ally holds an awake live character (the unholy kind: a woman, or
    /// one of another race than the leader). -1: none.
    /// </summary>
    int GatherTown(World world, Squad squad, bool unholy)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(squad.Leader, out int ls)) return -1;

        var at = table.Next[ls].Position;
        int site = TownAt(at);
        if (site < 0 && NearestTownOf(at, squad.Faction) is >= 0 and var near
            && Vector2.DistanceSquared(Flat(at), Flat(data.Sites[near].Position)) <= 9.375e6f)
            site = near;
        if (site < 0 || data.Sites[site].Town.Type is TownType.SlaveCamp or TownType.Prison) return -1;

        if (!world.Fixtures.IsRegistered(site)) world.Fixtures.RegisterSite(world, data, site);
        var leaderRace = table.Cold(ls)!.Race?.StringId;
        foreach (var f in world.Fixtures.All)
        {
            if (f.Kind != FixtureKind.Cage || f.Site != site || f.Owner < 0 || f.Owner == world.Player.Faction) continue;
            if (f.Owner != squad.Faction
                && !(data.Relations.Contains(f.Owner) && data.Relations.Contains(squad.Faction) && data.Relations.IsAlly(squad.Faction, f.Owner)))
                continue;
            if (FixtureRegistry.IsFree(table, f) || !table.TryResolveNext(f.Occupant, out int s)
                || table.Cold(s) is not { Medical.Incapacitated: false, IsPlayer: false } c)
                continue;
            if (unholy && c.Appearance is not { Female: true } && c.Race?.StringId == leaderRace) continue;
            return site;
        }

        return -1;
    }

    internal bool CaptiveCanStart(World world, Squad squad, Blackboard board, AiPackage package, PackageClass kind) => kind switch
    {
        // CannibalRetreatFromRaid slot 12 (FUN_140288630): not while the leader is in a town of its faction; then when a member carries someone.
        PackageClass.CannibalRetreat => world.Characters.IsAlive(squad.Leader)
            && LeaderTown(world, squad).Faction != squad.Faction && AnyCarrying(world, squad),

        // Package_CannibalsAtHome slot 12 (FUN_140291ef0): in its home town, or in a town of a faction not hostile to it that has a bar (town vfunc
        // 0x2d0 with BD_BAR).
        PackageClass.CannibalsAtHome => LeaderTown(world, squad) is var (site, owner) && site >= 0
            && (site == board.Town
                || !(squad.Faction >= 0 && owner >= 0 && data.Relations.IsHostile(squad.Faction, owner)) && Places(site).Bars.Count > 0),

        // PrisonerGathering slot 12 (FUN_1402872a0): an intact squad, and a town with prisoners to gather.
        PackageClass.PrisonerGathering => board.Intact && GatherTown(world, squad, package.Signal == 24) >= 0,

        // SlaverPrisonerShipping slot 12 (FUN_140288aa0): the squad has slaves.
        PackageClass.SlaverShipping => CaptiveSystem.SlavesOf(world, squad).Any(),
        _ => false,
    };

    internal bool CaptiveFinished(World world, Squad squad, Blackboard board, AiPackage package, PackageClass kind) => kind switch
    {
        // Slot 13 (FUN_140288740): the leader is in a town of its faction.
        PackageClass.CannibalRetreat => LeaderTown(world, squad).Faction == squad.Faction || !world.Characters.IsAlive(squad.Leader),
        PackageClass.CannibalsAtHome => !CaptiveCanStart(world, squad, board, package, kind),

        // Slot 13 (FUN_140288a00): more slaves than 3 x the squad's members, or nothing left to gather.
        PackageClass.PrisonerGathering => CaptiveSystem.SlavesOf(world, squad).Count() > 3 * squad.Members.Count
            || GatherTown(world, squad, package.Signal == 24) < 0,

        // Slot 13 (FUN_140288af0): no slaves left.
        PackageClass.SlaverShipping => !CaptiveSystem.SlavesOf(world, squad).Any(),
        _ => false,
    };

    /// <summary>
    /// The activation: a cannibal retreat (1) goes to the squad's home town, else the nearest town of its faction (<c>FUN_140288580</c>); the
    /// nearest-nest kind (25) always to the nearest (<c>FUN_140289b50</c>). Slave shipping goes to the nearest slave camp of a faction not hostile to
    /// the squad (<c>FUN_1402873a0</c>); the anywhere kind (28) takes a nearer town (a TOWN within 0.7 x that squared distance, <c>FUN_140289f90</c>)
    /// only when its flag bit 10 is set, whose meaning is <b>Unknown</b>: Meitou takes the camp.
    /// </summary>
    internal void CaptiveStarted(World world, Squad squad, Blackboard board, AiPackage package, PackageClass kind)
    {
        if (!world.Characters.TryResolveNext(squad.Leader, out int ls)) return;

        var at = world.Characters.Next[ls].Position;
        switch (kind)
        {
            case PackageClass.CannibalRetreat:
                board.MissionTown = package.Signal == 1 && board.Town >= 0 ? board.Town : NearestTownOf(at, squad.Faction);
                break;
            case PackageClass.SlaverShipping:
                {
                    int best = -1;
                    float bestD = float.MaxValue;
                    foreach (var site in data.Sites)
                    {
                        if (site.Town.Type != TownType.SlaveCamp || site.Index >= data.SiteFactions.Count) continue;

                        int f = data.SiteFactions[site.Index];
                        if (f >= 0 && squad.Faction >= 0 && data.Relations.Contains(f) && data.Relations.Contains(squad.Faction)
                            && data.Relations.IsHostile(f, squad.Faction))
                            continue;

                        float d = Vector2.DistanceSquared(Flat(at), Flat(site.Position));
                        if (d < bestD) (best, bestD) = (site.Index, d);
                    }

                    board.MissionTown = best;
                    break;
                }
        }

        // The travel goals (TRAVEL_TO_TARGET_TOWN_FAST) walk to the running package's destination.
        if (board.MissionTown >= 0) board.Destination = CampaignTown.OfSite(board.MissionTown);
    }

    /// <summary>
    /// <c>GoOutOnPatrol_Cannibal</c> slot 12 (<c>FUN_140289d80</c>): no patrol while every cage of the squad's faction in its town is taken
    /// (CAGES_ARE_ALL_FULL).
    /// </summary>
    bool CannibalPatrolHeld(World world, Squad squad, Blackboard board, AiPackage package) =>
        package.Signal == 4 && board.Town >= 0 && world.Characters.TryResolveNext(squad.Leader, out int leader)
        && world.Characters.Cold(leader) is { } who && CaptiveSystem.CagesFull(world, data, board.Town, who);
}