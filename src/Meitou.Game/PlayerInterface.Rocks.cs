using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Construction;
using Meitou.Simulation;
using Meitou.Simulation.Production;

namespace Meitou.Game;

/// <summary>
/// Mining rocks by hand (docs/simulation.md "Production", natural sites): a right click on a resource rock with characters selected has them operate it, with Shift it becomes a job
/// (Operating machine). A left click selects it; its machine window holds the mined ore, taken out by hand. Rocks are the foliage system's (<see cref="Rocks"/>); a rock is hit by its mesh's triangles, as drawn (engine choice: the original picks the rock
/// building's collision; without the mesh, a sphere of its radius).
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>The world's resource rocks (null: none, e.g. headless or before the world is loaded).</summary>
    public Func<IResourceNodes?>? Rocks { get; set; }

    /// <summary>How far (X/Z) from the ground under the cursor a rock is looked for: a rock in front of the cursor's ground point is still hit.</summary>
    const float RockSearch = 200;

    /// <summary>The nearest rock the pixel's ray meets before the ground.</summary>
    ResourceNode? PickRock(Vector2 pixel) => PickRockAt(pixel)?.Rock;

    /// <summary>The nearest rock the pixel's ray meets before the ground, with the ray distance.</summary>
    internal (ResourceNode Rock, float T)? PickRockAt(Vector2 pixel)
    {
        if (Rocks?.Invoke() is not { } rocks || Ray(pixel) is not { } ray || GroundHit(ray.Origin, ray.Direction) is not { } ground) return null;
        float bestT = Vector3.Distance(ray.Origin, ground) + 1;
        ResourceNode? best = null;
        foreach (var rock in rocks.Near(0, ground.X, ground.Z, RockSearch))
        {
            if (rock.Record.Length == 0) continue;
            if (rock.RayHit(ray.Origin, ray.Direction) is { } t && t < bestT) (bestT, best) = (t, rock);
        }
        return best is { } found ? (found, bestT) : null;
    }

    /// <summary>A rock as a selected building: its site is the natural site, so the machine window shows its output once it has been mined.</summary>
    SelectedBuilding RockBuilding(ResourceNode rock) =>
        new(buildHost?.ItemName(rock.Record) ?? rock.Record, null, null, null, ProductionSystem.NaturalId(rock.Record, rock.Position), ProductionSystem.NaturalId(rock.Record, rock.Position),
            rock.Position, null, Record: rock.Record);

    void MineRock(IReadOnlyList<CharacterId> selection, ResourceNode rock, bool asJob)
    {
        string site = ProductionSystem.NaturalId(rock.Record, rock.Position);
        if (asJob) Say(new AddJob(selection, TaskType.OPERATE_MACHINERY, site) { Tick = session.World.Tick });
        else Say(new OperateOrder(selection, site) { Tick = session.World.Tick });
    }

    (ResourceNode Rock, Vector2 At, long Since)? rockPending;

    /// <summary>
    /// A right press on a rock with characters selected: released before the menu opens it mines (with Shift as a job), held longer it opens the menu (Mine, Add to jobs) as a site's does
    /// (true: taken).
    /// </summary>
    bool RockPress(Vector2 p)
    {
        if (SelectedIds().Count == 0 || PickRock(p) is not { } rock) return false;
        rockPending = (rock, p, System.Diagnostics.Stopwatch.GetTimestamp());
        return true;
    }

    /// <summary>The right button let go: a press on a rock released before the menu opened mines it.</summary>
    void RockRelease()
    {
        if (rockPending is not { } pending) return;
        rockPending = null;
        if (SelectedIds() is { Count: > 0 } selection) MineRock(selection, pending.Rock, SlowModifier);
    }

    /// <summary>Once a frame: a rock press held long enough opens its menu.</summary>
    void UpdateRock()
    {
        if (rockPending is not { } pending || System.Diagnostics.Stopwatch.GetElapsedTime(pending.Since).TotalSeconds < ContextHoldSeconds) return;
        rockPending = null;
        if (SelectedIds() is not { Count: > 0 } selection) return;
        string name = buildHost?.ItemName(pending.Rock.Record) ?? pending.Rock.Record;
        contextMenu = new(name, pending.At,
        [
            ("Mine", () => MineRock(selection, pending.Rock, false)),
            ("Add to jobs", () => MineRock(selection, pending.Rock, true)),
        ]);
    }
}
