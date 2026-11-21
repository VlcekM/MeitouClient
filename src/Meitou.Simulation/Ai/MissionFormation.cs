using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Ai;

/// <summary>
/// 215 Mission formation under a war package (<c>Task_TravelToMissionTarget_ArmyFormation</c>, update <c>FUN_140349e70</c>, <b>Verified</b>;
/// docs/game/factions-squads-towns.md "Campaigns"): where a campaign platoon's member walks and at what pace. Pure reads of the platoons, the
/// campaign and the last tick's characters, which only serial steps write, so the parallel think may ask it.
/// </summary>
static class MissionFormation
{
    #region Destination

    /// <summary>
    /// Whether 215 aims by the war classes' rules: the running package is a war package (19, 20, 32) of a campaign's platoon.
    /// </summary>
    internal static bool Runs(Blackboard board, Platoon? platoon) =>
        platoon?.Campaign is not null && board.CurrentClass is PackageClass.WarBase or PackageClass.WarAssault or PackageClass.WarAssaultCannibal;

    /// <summary>
    /// 215's destination (the package target <c>FUN_140796120</c> as a position, <c>FUN_14079d390</c>): the campaign target's point
    /// (<see cref="Blackboard.WarPoint"/>) while the package aims at it; the copied lead platoon's place (Platoon <c>+0x48</c>,
    /// <see cref="Platoon.Position"/>, written by the serial step one update before) while that platoon is in the world; else the campaign's
    /// stored point (<see cref="Fallback"/>). Null: nowhere to go.
    /// </summary>
    internal static Vector2? Destination(World world, Platoon platoon, Blackboard board)
    {
        if (board.WarLead < 0) return board.WarPoint;
        if (world.Platoons.Find(board.WarLead) is { } lead) return lead.Position;

        return Fallback(world, platoon, board);
    }

    /// <summary>
    /// The point beside an unresolved lead (campaign slot 21 <c>FUN_1409d5990</c>, <b>Verified</b>), which resolves the campaign's current lead,
    /// not the package's copy: none, the asking platoon's own place (it stands); the asker itself, the campaign target's point
    /// (<c>FUN_1409b65d0</c>); another, its point on the lead's trail (<see cref="PopulationSystem.TrailAim"/>), the asker's own place without a trail.
    /// </summary>
    static Vector2? Fallback(World world, Platoon platoon, Blackboard board)
    {
        if (platoon.Campaign is not { } campaign || world.Platoons.Find(campaign.Leader) is null) return platoon.Position;
        if (campaign.Leader == platoon.Id) return board.WarPoint;

        return PopulationSystem.TrailAim(campaign, platoon.Id) ?? platoon.Position;
    }

    #endregion

    #region Pace

    /// <summary>
    /// The campaign leader keeps its loaded speed while every intact platoon is within this of its platoon (slot 15 <c>FUN_1409c3910</c>,
    /// <b>Verified</b>), else it stops within <see cref="WaitNearTarget"/> of its destination, else walks.
    /// </summary>
    const float ColumnRange = 6000, WaitNearTarget = 8294.4f;

    /// <summary>
    /// Another member jogs while this far or more from its destination; nearer it goes straight at the campaign leader's speed (<b>Verified</b>).
    /// </summary>
    const float JogBeyond = 100;

    /// <summary>
    /// 215's pace for the member at <paramref name="slot"/>, <paramref name="distance"/> from its destination: its movement mode, a cap on its speed
    /// (null: none), and whether it stands instead (the campaign leader waiting for the column near its target).
    /// </summary>
    internal static (byte Mode, float? Cap, bool Stands) Pace(World world, int slot, Platoon platoon, Blackboard board, float distance)
    {
        var campaign = platoon.Campaign!;
        if (LeaderOf(world, campaign) is not { } head) return (SpeedMode.Run, null, false);
        if (head == slot) return LeaderPace(world, slot, platoon, board, distance);
        if (distance >= JogBeyond) return (SpeedMode.Run, null, false);

        var v = world.Characters.Previous[head].Velocity;
        float near = distance < PopulationSystem.CampaignPaceDistance ? PopulationSystem.CampaignPaceFactor : 1;
        float cap = new Vector2(v.X, v.Z).Length() * near;
        return (world.Characters.Previous[head].Mode, cap, false);
    }

    /// <summary>
    /// The campaign leader's pace: its campaign's <c>travel speed loaded</c> while the column holds together, else a stand within
    /// <see cref="WaitNearTarget"/> of its destination, else a walk.
    /// </summary>
    static (byte Mode, float? Cap, bool Stands) LeaderPace(World world, int slot, Platoon platoon, Blackboard board, float distance)
    {
        if (ColumnTogether(world, platoon)) return (LoadedMode(board.WarSpeed, world.Characters.Previous[slot].Mode), null, false);

        return distance <= WaitNearTarget ? (SpeedMode.Walk, null, true) : (SpeedMode.Walk, null, false);
    }

    /// <summary>
    /// Whether every other intact platoon of the campaign is within <see cref="ColumnRange"/> of the leader platoon (the intact flag as the serial
    /// campaign step last stored it, <see cref="Platoon.IntactFlag"/>; a dead platoon is not intact).
    /// </summary>
    static bool ColumnTogether(World world, Platoon lead)
    {
        foreach (int id in lead.Campaign!.Platoons)
        {
            if (id == lead.Id || world.Platoons.Find(id) is not { } other || other.Dead || !other.IntactFlag) continue;
            if (Vector2.Distance(other.Position, lead.Position) > ColumnRange) return false;
        }

        return true;
    }

    /// <summary>
    /// The slot of the campaign's leader character (the lead platoon's squad leader, slot 5 <c>FUN_140285820</c>); null without one in the world.
    /// </summary>
    static int? LeaderOf(World world, CampaignInstance campaign)
    {
        if (world.Platoons.Find(campaign.Leader) is not { SquadId: >= 0 } lead || world.Squads.Find(lead.SquadId) is not { } squad) return null;

        return world.Characters.TryResolveNext(squad.Leader, out int s) ? s : null;
    }

    /// <summary>
    /// A <c>travel speed loaded</c> as a movement mode (<b>Verified</b>: WALK the default walk, JOG 55, RUN and GROUPED uncapped, NO_SPEED_CHANGE as
    /// it was).
    /// </summary>
    internal static byte LoadedMode(MoveSpeed speed, byte current) => speed switch
    {
        MoveSpeed.Walk => SpeedMode.Walk,
        MoveSpeed.Jog => SpeedMode.Run,
        MoveSpeed.Run or MoveSpeed.Grouped => SpeedMode.Free,
        _ => current,
    };

    #endregion
}
