using System.Numerics;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Player;
using Meitou.Simulation.Ragdolls;

namespace Meitou.Simulation.Perception;

/// <summary>
/// The sight ray (<c>FUN_1408533a0</c>, <b>Verified</b> literals): whether nothing solid stands between a looker and a target, and when the
/// original's can-see ends with it (<c>FUN_140857b10</c>, docs/game/perception.md "Sight").
/// </summary>
static class SightRay
{
    /// <summary>
    /// The ray starts this far over the looker's feet.
    /// </summary>
    const float From = 18;

    /// <summary>
    /// The ray ends this far over the target's feet (half of <see cref="From"/>).
    /// </summary>
    const float To = 9;

    /// <summary>
    /// A hit nearer than this to the target does not block the ray.
    /// </summary>
    const float Slack = 2;

    /// <summary>
    /// Whether can-see ends with the ray for this pair (<c>FUN_140857b10</c>, <b>Observed</b>): not when both are the player's; else when the target is
    /// the player's, the looker is, or the target sneaks. The original also casts it for a target that is down, on screen or in a few other states,
    /// and skips it for a looker whose squad has no current town while the target's has one; Meitou leaves those out.
    /// </summary>
    public static bool Applies(CharacterCold looker, CharacterCold target)
    {
        if (looker.IsPlayer && target.IsPlayer) return false;

        return target.IsPlayer || looker.IsPlayer || (target.Orders & CharacterOrders.Sneak) != 0;
    }

    /// <summary>
    /// <see cref="SightMap.Sees"/> followed by the ray where <see cref="Applies"/> asks for it: 0 when the ray is blocked.
    /// </summary>
    public static float Sees(World world, int looker, int target, Vector3 eye, Vector3 at, float outdoor, float stealth = -1)
    {
        float v = world.Sight.Sees(looker, target, eye, at, outdoor, stealth);
        if (!(v > 0)) return v;

        var table = world.Characters;
        if (table.Cold(looker) is not { } l || table.Cold(target) is not { } t || !Applies(l, t)) return v;

        return Clear(world.Walkability, world.SystemOf<CombatSystem>()?.Buildings, eye, at) ? v : 0;
    }

    /// <summary>
    /// Whether the ray from <see cref="From"/> over the looker's feet at <paramref name="eye"/> to <see cref="To"/> over the target's at
    /// <paramref name="target"/> meets nothing solid more than <see cref="Slack"/> short of the target: the terrain and the buildings' collision
    /// (walls, floors, stairs, furniture and roofs; not doors or passable parts, <see cref="SolidHits"/>). Blocked while the collision on the way is
    /// still being read.
    /// </summary>
    public static bool Clear(IWalkability walk, IRagdollEnvironment? buildings, Vector3 eye, Vector3 target)
    {
        var from = eye + new Vector3(0, From, 0);
        var seg = target + new Vector3(0, To, 0) - from;
        float hit = SolidHits.Hit(walk, buildings, from, seg, RangedTuning.TerrainStep, out _, out bool ready);
        if (!ready) return false;

        return hit < 0 || hit * seg.Length() >= Vector3.Distance(eye, target) - Slack;
    }
}
