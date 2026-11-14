using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Construction;
using Meitou.Game.Building;
using Meitou.Simulation;
using Meitou.Simulation.Navigation;
using Meitou.Simulation.Production;

namespace Meitou.Game.Ui;

/// <summary>
/// What a right click does with a town building that is a machine its workers operate (a farm, a mine, a refinery; docs/game/ui-input.md "Context
/// menu verbs", the Building row): with characters selected the pointer over one is the class's (<c>mine</c> for function 1 or 27, else
/// <c>use</c>); a quick right click operates it (87, <see cref="OperateOrder"/> on its <c>townwork:</c> site, with Shift the Operating machine job);
/// held, the menu offers Use and, once its machine exists, Loot (26): the selection walks to it and its window opens beside the character's
/// inventory, where taking from it is a theft from the town's faction.
/// </summary>
sealed class TownMachineUse(PlayerInterface player)
{
    /// <summary>
    /// The nearest town machine under the pixel, before the ground: its placement, what the selection panel shows of it, its placed hull, base
    /// height and ray distance. A farm (no collision) is picked by its plot, any other on its collision, as a level building is
    /// (<see cref="BuildingSelection.PickBuildingAt"/>).
    /// </summary>
    internal (PlacedBuilding Building, WorldBuildingInfo Info, Vector2[] Hull, float Y, float T)? PickAt(Vector2 pixel)
    {
        if (player.Ray(pixel) is not { } ray || player.Build.Services is not { } host) return null;

        float limit = player.Picking.GroundHit(ray.Origin, ray.Direction) is { } ground ? Vector3.Distance(ray.Origin, ground) : 6000;
        float bestT = limit;
        (PlacedBuilding, WorldBuildingInfo, Vector2[], float, float)? best = null;
        foreach (var candidate in host.Placement.StaticAlong(ray.Origin, ray.Direction, limit))
        {
            var b = candidate.Placement;
            if (player.LevelEditorPanel.Services?.IsCleared(b.Key) == true) continue;

            float y = b.WorldY ?? player.Ground(b.Position.X, b.Position.Z) + b.Position.Y;
            var hull = candidate.Footprint.Place(b.Position, b.Rotation);
            float? hit = candidate.Plot
                ? WorldPicking.PlotT(ray, hull, limit < 6000 ? limit : null)
                : player.Picking.BuildingRayT(ray, hull, candidate.Footprint.YMin, candidate.Footprint.YMax, b.BuildingId, b.Position, y, b.Rotation);
            if (hit is not { } t || t >= bestT || player.Hidden(ray, t)) continue;
            if (host.WorldInfo(b) is not { Machine: true, Destroyed: false } info) continue;

            (best, bestT) = ((candidate, info, hull, y, t), t);
        }

        return best;
    }

    /// <summary>
    /// A right press on a town machine with characters selected: released before the hold time it operates it, held longer it opens the menu (true:
    /// taken).
    /// </summary>
    internal bool Press(Vector2 p)
    {
        if (player.SelectedIds().Count == 0 || PickAt(p) is not { } hit) return false;

        string site = SiteOf(hit.Building);
        ContextMenuWindow.ContextMenu? Menu(Vector2 at) => player.ContextMenuWindow.Menu(hit.Info.Name, at, Verbs(site, hit));

        if (!player.ContextMenuWindow.ContextPressOn(p, Menu, () => Use(site))) Use(site);
        return true;
    }

    /// <summary>
    /// The pointer over the town machine under the pixel: <c>mine</c> over a machine of function 1 or 27 (a farm, a mine), else <c>use</c>; null over
    /// none.
    /// </summary>
    internal string? Pointer(Vector2 p)
    {
        if (PickAt(p) is not { } hit) return null;

        return player.Production.Machine(SiteOf(hit.Building))?.Function is MachineData.MineFunction or MachineData.NaturalMineFunction
            ? "mine"
            : "use";
    }

    /// <summary>
    /// The site id of a town building's machine (<see cref="ProductionSystem.TownWorkId"/>).
    /// </summary>
    static string SiteOf(PlacedBuilding building) => ProductionSystem.TownWorkPrefix + building.Placement.Key;

    /// <summary>
    /// The menu's verbs: Use (87), then Loot (26) once the machine exists.
    /// </summary>
    List<(string Caption, Action Give)> Verbs(string site, (PlacedBuilding Building, WorldBuildingInfo Info, Vector2[] Hull, float Y, float T) hit)
    {
        var verbs = new List<(string, Action)> { ("Use", () => Use(site)) };
        if (player.Production.Machine(site) is not null) verbs.Add(("Loot", () => Loot(site, hit)));
        return verbs;
    }

    /// <summary>
    /// The selection operates the machine (with Shift as the Operating machine job).
    /// </summary>
    void Use(string site) => player.Say(player.SlowModifier
        ? new AddJob(player.SelectedIds(), TaskType.OperateMachinery, site) { Tick = player.Session.World.Tick }
        : new OperateOrder(player.SelectedIds(), site) { Tick = player.Session.World.Tick });

    /// <summary>
    /// The selection walks to the machine, whose window then opens beside the character's inventory, the town building selected as its machine.
    /// </summary>
    void Loot(string site, (PlacedBuilding Building, WorldBuildingInfo Info, Vector2[] Hull, float Y, float T) hit)
    {
        var b = hit.Building.Placement;
        var info = hit.Info;
        var position = new Vector3(b.Position.X, hit.Y, b.Position.Z);
        var selection = new BuildingSelection.SelectedBuilding(info.Name, info.Type, info.Owner, info.Town, site, b.Key, position, hit.Hull,
            info.PublicDay, info.PublicNight, b.BuildingId, info.Destroyed);
        player.Build.OpenMachineWindow(site, position, selection);
    }
}
