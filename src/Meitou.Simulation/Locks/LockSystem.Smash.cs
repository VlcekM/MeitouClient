using System.Numerics;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Locks;

/// <summary>
/// Smashing a wall (Task_BashDoor serving 272 Smashing, docs/game/locks.md "Smash a wall"): an NPC attacker whose target lies behind fortifications
/// no gate leads through plans 270 -> 272 on the nearest wall of its own region (<see cref="GatePlan.PlansSmash"/>), walks to it and strikes it until
/// it is destroyed. <see cref="CharacterCold.Bashing"/> names the wall site, <see cref="CharacterCold.BashWall"/> is set.
/// </summary>
public sealed partial class LockSystem
{
    /// <summary>
    /// 270's wall scan (<c>FUN_1405a3f60</c>, <b>Verified</b>): the wall buildings (function BF_WALL, the sites that link together) sorted by
    /// distance from the looker, the first whose gate code is the looker's (<c>FUN_1405b0eb0</c>); a wall line's attached pieces are the line's own.
    /// Null: none.
    /// </summary>
    BuildSite? NearestWall(World world, Vector3 from, int code)
    {
        if (Regions is null || Combat?.Construction is not { } construction) return null;
        BuildSite? best = null;
        float bestD = float.MaxValue;
        foreach (var site in world.Buildings.All)
        {
            if (site.Destroyed || site.WallParent is not null || site.Progress <= 0 || construction.Data(site.Record) is not { LinksTogether: true })
                continue;

            float d = Vector2.DistanceSquared(Flat(from), new Vector2(site.Position.X, site.Position.Z));
            if (d >= bestD || Regions.CodeAt(site.Position) != code) continue;
            (best, bestD) = (site, d);
        }

        return best;
    }

    /// <summary>
    /// The fallback of <see cref="FindGateBasher"/>: no gate leads to the target, so the planner's 270 picks a wall of the attacker's region to
    /// smash; true when the plan does.
    /// </summary>
    bool FindWallBasher(World world, int s, int t, CharacterCold c, int code)
    {
        if (NearestWall(world, world.Characters.Next[s].Position, code) is not { } wall || !_gatePlan.PlansSmash(world, Combat, this, s, t, wall))
            return false;

        c.Bashing = wall.Id;
        c.BashWall = true;
        c.BashTime = 0;
        return true;
    }

    /// <summary>
    /// The wall site <paramref name="id"/> as the swing and the end test see it (its line's root, which takes the blows).
    /// </summary>
    static BuildSite? LineOf(World world, string id) =>
        world.Buildings.Find(id) is { } site ? (site.WallParent is { } parent && world.Buildings.Find(parent) is { } line ? line : site) : null;

    /// <summary>
    /// One update of a wall basher (<c>FUN_140348c10</c> for 272): it ends when the wall is destroyed, the attacker is down or no longer attacking;
    /// it walks to the wall's reach (<see cref="GatePlan.AtWall"/>), faces it and, at 0.6 of every <see cref="BashClipSeconds"/>, strikes it with its
    /// blow's damage (<see cref="CombatSystem.StrikeWall"/>, the wall line's formula). No crime is booked: only doors book Burglary.
    /// </summary>
    void Smash(World world, int s, CharacterCold c, string wallId)
    {
        var table = world.Characters;
        ref var n = ref table.Next[s];
        if (LineOf(world, wallId) is not { Destroyed: false } line || world.Buildings.Find(wallId) is not { } wall
                || c.Medical is { Incapacitated: true } || n.Task != (byte)CharacterTask.Attack)
        {
            StopBashing(table, s, c);
            return;
        }

        if (!GatePlan.AtWall(world, this, wall, n))
        {
            if ((n.Flags & (ushort)MoveFlags.AnyPath) == 0)
            {
                if (c.BashTime < 0)
                {
                    StopBashing(table, s, c);
                    return;
                }

                n.Goal = new Vector2(wall.Position.X, wall.Position.Z);
                n.Mode = SpeedMode.Free;
                n.Flags |= (ushort)MoveFlags.NeedPath;
                c.BashTime = -1;
            }

            return;
        }

        if ((n.Flags & (ushort)MoveFlags.AnyPath) != 0)
        {
            n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.AnyPath);
            n.PathCursor = 0;
            c.PathRequest = 0;
        }

        var face = new Vector2(wall.Position.X, wall.Position.Z) - Flat(n.Position);
        if (face.LengthSquared() > 1e-4f) n.Yaw = MathF.Atan2(face.X, face.Y);
        if (c.BashTime < 0) c.BashTime = 0;
        if (!BashBlowLands(c, world.TickSeconds)) return;
        float force = Packet(c) is var (cut, blunt, animal) ? (2 * blunt + 0.5f * cut) * (animal ? 0.25f : 1) : 0;
        if (CombatSystem.StrikeWall(world, wall, force, s)) StopBashing(table, s, c);
    }
}