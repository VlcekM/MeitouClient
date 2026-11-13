using System.Globalization;
using System.Numerics;
using System.Text;
using Meitou.Game.Ui;
using Meitou.Simulation;

namespace Meitou.Game.Building;

/// <summary>
/// Clicking a building (docs/game/ui-input.md "Building picking and selection", docs/game/ui-screens.md "Selected building"): a left click on the
/// player's sites and on the level data's buildings selects one, which fills the HUD's name, state, stats and medical-header panels in place of the
/// character's and leaves the character selection alone. Picking uses the buildings' collision footprints (a vertical prism over the convex hull of
/// the record's shapes), not the original's per-part collision rays; characters under the cursor win over a building.
/// </summary>
sealed class BuildingSelection(PlayerInterface player)
{
    #region Selection

    /// <summary>
    /// A building selected by a click: what it is, where it stands (hull in the world, for the outline) and what the panels say of it.
    /// </summary>
    internal sealed record SelectedBuilding(string Name, string? Type, string? Owner, string? Town, string? SiteId, string Instance,
        Vector3 Position, Vector2[]? Hull, bool PublicDay = false, bool PublicNight = false, string? Record = null, bool Destroyed = false);

    /// <summary>
    /// The building selected by a click; null when none is.
    /// </summary>
    internal SelectedBuilding? Selected;

    /// <summary>
    /// A left click on the ground ends the building's selection (Advanced settings "Click away deselects"); false: it stays, as in the game.
    /// </summary>
    public bool DeselectBuildings { get; set; } = true;

    /// <summary>
    /// The selected building's instance (a level building's placement key, a site's id), null when none: the building the interior editor shows.
    /// </summary>
    public string? SelectedBuildingKey => Selected?.Instance;

    /// <summary>
    /// Makes <paramref name="building"/> the selected one (null: none) and tells the simulation, which then publishes its doors.
    /// </summary>
    internal void SetBuilding(SelectedBuilding? building)
    {
        if (ReferenceEquals(Selected, building)) return;

        Selected = building;

        // clicking a building asks for its window again, also when the player closed it before (the mark only held until another was selected)
        player.Production.ClosedMachine = null;

        // Selecting calls the building's select sound (FUN_1405504a0: Buildings_Click = its `select sound`, none for 0); a click on the same
        // building picks it anew and sounds again.
        if (building?.Record is { } record) player.InterfaceSounds.Sound("Building_Click", building: record);

        player.Say(new InspectBuildingCommand(building?.Instance) { Tick = player.Session.World.Tick });
    }

    /// <summary>
    /// The selected building while it still exists (a site that was cancelled or dismantled drops the selection; a rock's or a town building's
    /// machine has no site).
    /// </summary>
    internal SelectedBuilding? BuildingFocus()
    {
        if (Selected is { SiteId: { } id } && !Simulation.Production.ProductionSystem.IsNatural(id)
            && !Simulation.Production.ProductionSystem.IsTownWork(id)
            && !player.Session.CurrentSnapshot.Buildings.Any(s => s.Id == id))
            SetBuilding(null);

        return Selected;
    }

    /// <summary>
    /// The buildings to outline (docs/game/ui-input.md "Hover outline"): the selected one in white, then, with <paramref name="hover"/>, the one
    /// under the cursor (<see cref="PointerView.HoverBuilding"/>) unless it is the selected one; each with its placement key and its floor (the
    /// height it stands at, where the outline stops).
    /// </summary>
    public List<(string Key, float Floor, Vector4 Colour)> OutlinedBuildings(bool hover)
    {
        var list = new List<(string Key, float Floor, Vector4 Colour)>(2);
        if (Selected is { } b) list.Add((b.Instance, b.Position.Y, PointerView.WhiteOutline));
        if (hover && player.PointerPanel.HoverBuilding is { } h && h.Key != Selected?.Instance) list.Add(h);
        return list;
    }

    /// <summary>
    /// A site of the player's as a selected building (what a left click on it selects).
    /// </summary>
    internal SelectedBuilding SiteBuilding(Simulation.Construction.BuildingSnapshot site)
    {
        var name = player.Build.SiteName(site);
        var owner = player.FactionName?.Invoke(player.Session.World.Player.Faction);
        var footprint = player.Build.Services?.Placement.Footprint(site.Record) ?? player.Build.Services?.Placement.Plot(site.Record);
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, PlacementCheck.YawOf(site.Rotation));
        var hull = footprint is { } f ? f.Place(site.Position, rotation) : null;
        return new(name, null, owner, null, site.Id, site.Id, site.Position, hull, Record: site.Record);
    }

    #endregion

    #region Picking

    /// <summary>
    /// How far from a piece of furniture its house's placement point is looked for (world units): twice the 200 that BuildingFootprints.StaticAround
    /// takes as a building's own extent beyond its placement point.
    /// </summary>
    const float HouseReach = 400;

    /// <summary>
    /// The nearest building under the pixel, a site of the player's or a level-data one, when the ray meets it before the ground.
    /// </summary>
    internal SelectedBuilding? PickBuilding(Vector2 pixel) => PickBuildingAt(pixel)?.Building;

    /// <summary>
    /// <see cref="PickBuilding"/> with the distance along the ray.
    /// </summary>
    internal (SelectedBuilding Building, float T)? PickBuildingAt(Vector2 pixel)
    {
        if (player.Ray(pixel) is not { } ray) return null;
        if (PickFurnitureAt(pixel) is { } furniture) return furniture;

        float limit = player.Picking.GroundHit(ray.Origin, ray.Direction) is { } groundPoint ? Vector3.Distance(ray.Origin, groundPoint) : 6000;
        float bestT = limit;
        SelectedBuilding? best = null;
        var site = player.Build.PickSiteAt(pixel);
        if (site is { } nearest && nearest.T < bestT)
        {
            bestT = nearest.T;
            best = SiteBuilding(nearest.Site);
        }
        Simulation.Navigation.PlacedBuilding? level = null;
        if (player.Build.Services is { } host)
        {
            foreach (var candidate in host.Placement.StaticAlong(ray.Origin, ray.Direction, limit))
            {
                var b = candidate.Placement;
                // the level editor cleared it
                if (player.LevelEditorPanel.Services?.IsCleared(b.Key) == true) continue;

                float y = b.WorldY ?? player.Ground(b.Position.X, b.Position.Z) + b.Position.Y;
                var hull = candidate.Footprint.Place(b.Position, b.Rotation);
                float? hit = candidate.Plot
                    ? WorldPicking.PlotT(ray, hull, limit < 6000 ? limit : null)
                    : player.Picking.BuildingRayT(ray, hull, candidate.Footprint.YMin, candidate.Footprint.YMax,
                        b.BuildingId, b.Position, y, b.Rotation);
                if (hit is not { } t || t >= bestT) continue;

                bestT = t;
                level = candidate;
                var info = host.WorldInfo(b);
                var position = new Vector3(b.Position.X, y, b.Position.Z);
                best = new(info.Name, info.Type, info.Owner, info.Town, null, b.Key, position, hull,
                    info.PublicDay, info.PublicNight, b.BuildingId, info.Destroyed);
            }
        }

        if (level is { } shown && site is { } inner && inner.T > bestT && SiteShownInside(ray, shown, inner))
        {
            bestT = inner.T;
            best = SiteBuilding(inner.Site);
        }

        if (PickCampBuildingAt(pixel) is { } camp && camp.T < bestT)
            (best, bestT) = camp;

        if (player.Furniture.PickRockAt(pixel) is { } rock && rock.T < bestT)
            (best, bestT) = (player.Furniture.RockBuilding(rock.Rock), rock.T);

        return best is { } found ? (found, bestT) : null;
    }

    /// <summary>
    /// The visible furniture nearest under the pixel (a bed, container, cage or seat, else a piece of a house's layout), which takes the click over
    /// the house around it, as the original picks furniture as a building by its own collision (docs/game/ui-input.md "Building picking and
    /// selection"); a plain building is no right-click candidate (PickAt). A nearer object site or rock still wins. One that belongs to a site of
    /// the player's selects that site; any other names the town of the house it stands in.
    /// </summary>
    (SelectedBuilding Building, float T)? PickFurnitureAt(Vector2 pixel)
    {
        var fixture = NearestFurnitureAt(pixel);
        if (PickPieceAt(pixel) is { } piece && !(fixture is { } nearer && nearer.T <= piece.T))
            return Blocked(pixel, piece.Building.Instance, piece.T) ? null : piece;

        if (fixture is not { } hit || Blocked(pixel, hit.Id, hit.T)) return null;

        if (player.Session.CurrentSnapshot.Buildings.FirstOrDefault(s => IsSiteOf(s, hit.Id)) is { } site)
            return (SiteBuilding(site), hit.T);

        var name = player.Build.Services?.ItemName(hit.Record) ?? hit.Record;
        var owner = player.FactionName?.Invoke(hit.Owner);
        var town = TownOf(hit.House, hit.Position);
        return (new SelectedBuilding(name, null, owner, town, null, hit.Id, hit.Position, null, Record: hit.Record), hit.T);
    }

    /// <summary>
    /// Whether a nearer object site of another, or a nearer rock, takes the click from the furniture <paramref name="id"/> met at
    /// <paramref name="t"/>.
    /// </summary>
    bool Blocked(Vector2 pixel, string id, float t) =>
        player.Build.PickSiteAt(pixel) is { } front && !IsSiteOf(front.Site, id) && player.Build.SiteIsObject(front.Site) && front.T < t
        || player.Furniture.PickRockAt(pixel) is { } stone && stone.T < t;

    /// <summary>
    /// The town piece nearest under the pixel before the ground (a table, shelf, lamp, torch or dummy of a house the ray passes,
    /// Data.World.TownBuilding.Pieces) as the selected building: named by its record, with its house's owner and town. Left out of a level
    /// building the level editor cleared.
    /// </summary>
    (SelectedBuilding Building, float T)? PickPieceAt(Vector2 pixel)
    {
        if (player.Ray(pixel) is not { } ray || player.Build.Services is not { } host) return null;

        float limit = player.Picking.GroundHit(ray.Origin, ray.Direction) is { } ground ? Vector3.Distance(ray.Origin, ground) : 6000;
        (Data.World.TownPiece Piece, Data.World.BuildingPlacement House, float T)? best = null;
        foreach (var candidate in host.Placement.StaticAlong(ray.Origin, ray.Direction, limit))
        {
            var house = candidate.Placement;
            if (player.LevelEditorPanel.Services?.IsCleared(house.Key) == true) continue;

            foreach (var piece in host.WorldInfo(house).Pieces ?? [])
            {
                if (PieceRayT(ray, house.Key, piece, best?.T ?? limit) is { } t)
                    best = (piece, house, t);
            }
        }

        if (best is not { } hit) return null;

        var info = host.WorldInfo(hit.House);
        var (id, record) = (hit.Piece.Id, hit.Piece.RecordId);
        return (new SelectedBuilding(host.ItemName(record), null, info.Owner, info.Town, null, id, hit.Piece.Position, null, Record: record), hit.T);
    }

    /// <summary>
    /// The camp building nearest under the pixel before the ground (a camp fire or tent of a nest, WorldSnapshot.NestBuildings) as the selected
    /// building, with the camp's owner: by its collision, else the hull of its parts' bounds as a site without collision is picked (a camp fire
    /// has none, yet the original selects it, docs/game/ui-screens.md "Selected building"). A camp bed is left to the beds.
    /// </summary>
    (SelectedBuilding Building, float T)? PickCampBuildingAt(Vector2 pixel)
    {
        var camps = player.Session.CurrentSnapshot.NestBuildings;
        if (camps.Count == 0 || player.Ray(pixel) is not { } ray || player.Build.Services is not { } host) return null;

        float ground = player.Picking.GroundHit(ray.Origin, ray.Direction) is { } at ? Vector3.Distance(ray.Origin, at) : float.MaxValue;
        (Simulation.Construction.BuildingSnapshot Camp, float T)? best = null;
        foreach (var camp in camps)
            if (CampRayT(ray, camp, ground) is { } t && (best is not { } b || t < b.T)) best = (camp, t);

        if (best is not { } hit) return null;

        var c = hit.Camp;
        var name = c.Name.Length > 0 ? c.Name : host.ItemName(c.Record);
        return (new SelectedBuilding(name, null, player.FactionName?.Invoke(c.Owner), null, null, c.Id, c.Position, null, Record: c.Record), hit.T);
    }

    /// <summary>
    /// Where the ray meets the camp building <paramref name="camp"/> before <paramref name="ground"/>; null when it misses, it is a bed, or a
    /// building seen from outside stands before it.
    /// </summary>
    float? CampRayT((Vector3 Origin, Vector3 Direction) ray, Simulation.Construction.BuildingSnapshot camp, float ground)
    {
        if (player.Build.Services is not { } host) return null;

        var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, PlacementCheck.YawOf(camp.Rotation));
        float? t = host.Placement.Footprint(camp.Record) is { } footprint
            ? player.Picking.BuildingRayT(ray, footprint.Place(camp.Position, yaw), footprint.YMin, footprint.YMax, camp.Record, camp.Position,
                camp.Position.Y, yaw)
            : host.Placement.Plot(camp.Record) is { } plot
                ? WorldPicking.PlotT(ray, plot.Place(camp.Position, yaw), ground < float.MaxValue ? ground : null)
                : null;
        if (t is not { } hit || hit >= ground || player.Hidden(ray, hit)) return null;

        return player.Session.CurrentSnapshot.Beds.Any(b => b.Id == camp.Id + "@bed0") ? null : hit;
    }

    /// <summary>
    /// Where the ray meets <paramref name="piece"/>'s own collision before <paramref name="before"/> (the ground, or a nearer piece), as a building
    /// is picked; null when it misses, the piece has no collision (or it is still being read), it is not drawn
    /// (<see cref="FurnitureUse.FloorHidden"/> of its <paramref name="house"/>) or a wall stands before it.
    /// </summary>
    float? PieceRayT((Vector3 Origin, Vector3 Direction) ray, string house, Data.World.TownPiece piece, float before)
    {
        if (player.Furniture.FloorHidden?.Invoke(house, piece.Floor, piece.Outside) == true) return null;
        if (player.Build.Services?.Placement.Footprint(piece.RecordId) is not { } footprint) return null;

        var placed = footprint.Place(piece.Position, piece.Rotation);
        var rayT = player.Picking.BuildingRayT(ray, placed, footprint.YMin, footprint.YMax, piece.RecordId, piece.Position, piece.Position.Y,
            piece.Rotation);
        return rayT is { } t && t < before && !player.Hidden(ray, t) ? t : null;
    }

    /// <summary>
    /// The nearest of the bed, container, cage and seat under the pixel: its fixture id, BUILDING record, place, owner faction, the house it stands
    /// in and ray distance. A container, cage or seat that is a level building of its own (a town's standalone cage, safe, bench or throne) is left
    /// to the level buildings' pick, which gives its Type, Town and outline. A bed names no house: its panel takes the town from the bed itself.
    /// </summary>
    (string Id, string Record, Vector3 Position, int Owner, string? House, float T)? NearestFurnitureAt(Vector2 pixel)
    {
        (string Id, string Record, Vector3 Position, int Owner, string? House, float T)? best = null;
        void Offer(string id, string record, Vector3 position, int owner, string? house, float t)
        {
            if (best is not { } b || t < b.T) best = (id, record, position, owner, house, t);
        }

        if (player.Furniture.PickBedAt(pixel) is ({ } bed, var bedT))
            Offer(bed.Id, bed.Record, bed.Position, bed.Owner, null, bedT);
        if (player.Furniture.PickContainerAt(pixel) is ({ } box, var boxT) && !IsLevelBuilding(box.Id, box.Position))
            Offer(box.Id, box.Record, box.Position, box.Owner, HouseOf(box.Id), boxT);
        if (player.Furniture.PickCageAt(pixel) is ({ } cage, var cageT) && !IsLevelBuilding(cage.Id, cage.Position))
            Offer(cage.Id, cage.Record, cage.Position, cage.Owner, HouseOf(cage.Id), cageT);
        if (player.Furniture.PickSeatAt(pixel) is ({ } seat, var seatT) && !StandsAlone(seat))
            Offer(seat.Id, seat.Record, seat.Position, seat.Owner, seat.Building, seatT);
        return best;
    }

    /// <summary>
    /// The level building a selected instance belongs to: the house of an interior fixture or piece (<c>&lt;house key&gt;/&lt;index&gt;</c>), seat
    /// (<c>@seat</c>) or bed (<c>@bed</c>); any other instance itself.
    /// </summary>
    internal static string HouseKeyOf(string instance) => instance.IndexOfAny(['/', '@']) is > 0 and var end ? instance[..end] : instance;

    /// <summary>
    /// The house key of an interior fixture (<c>&lt;house key&gt;/&lt;index&gt;</c>); null for any other.
    /// </summary>
    static string? HouseOf(string fixture) => fixture.LastIndexOf('/') is > 0 and var end ? fixture[..end] : null;

    /// <summary>
    /// The town of the level building <paramref name="house"/> that furniture at <paramref name="at"/> stands in, which the furniture's panel shows
    /// (docs/game/ui-screens.md "Selected building"). Null when it stands in none or the house is in no town.
    /// </summary>
    string? TownOf(string? house, Vector3 at)
    {
        if (house is null || player.Build.Services is not { } host) return null;

        var placement = host.Placement.StaticPlacementsAround(new Vector2(at.X, at.Z), HouseReach).FirstOrDefault(p => p.Key == house);
        return placement is null ? null : host.WorldInfo(placement).Town;
    }

    /// <summary>
    /// Whether the fixture <paramref name="id"/> at <paramref name="at"/> is a level building's own lock: the placement standing there has its id
    /// as key (an interior's fixture is <c>&lt;house key&gt;/&lt;index&gt;</c>).
    /// </summary>
    bool IsLevelBuilding(string id, Vector3 at) =>
        player.Build.Services?.Placement.StaticPlacementsAround(new Vector2(at.X, at.Z), 1).Any(p => p.Key == id) == true;

    /// <summary>
    /// Whether <paramref name="seat"/> is a level building of its own (a bench or throne standing alone in a town, registered as
    /// <c>&lt;its key&gt;@seat0</c>): its building's placement stands where the seat does, with the seat's own record.
    /// </summary>
    bool StandsAlone(Simulation.Locks.SeatSpot seat) =>
        player.Build.Services?.Placement.StaticPlacementsAround(new Vector2(seat.Position.X, seat.Position.Z), 1)
            .Any(p => p.Key == seat.Building && p.BuildingId == seat.Record) == true;

    /// <summary>
    /// Whether the fixture <paramref name="fixture"/> belongs to the player's <paramref name="site"/>: its bed (<c>&lt;site&gt;@bed0</c>) or its own
    /// lock (a cage or safe, the site's id).
    /// </summary>
    static bool IsSiteOf(Simulation.Construction.BuildingSnapshot site, string fixture) => fixture == site.Id + "@bed0" || fixture == site.Id;

    /// <summary>
    /// Whether a site met behind a level building's hit stands inside that building and the view shows it there (furniture in a bought house): the
    /// level building is tested against its whole collision, roof included, while the original passes a building's hidden roof and upper floors in
    /// the interior view (docs/game/ui-input.md "Building picking and selection").
    /// </summary>
    bool SiteShownInside((Vector3 Origin, Vector3 Direction) ray, Simulation.Navigation.PlacedBuilding level,
        (Simulation.Construction.BuildingSnapshot Site, float T) site) =>
        !level.Plot && !player.Hidden(ray, site.T) && player.Furniture.AboveViewedFloor?.Invoke(site.Site.Position) != true
        && level.Footprint.Gap(level.Placement.Position, level.Placement.Rotation, new Vector2(site.Site.Position.X, site.Site.Position.Z)) == 0;

    #endregion

    #region Panel lines

    /// <summary>
    /// The colour of the [PAUSED] caption (the exe's static <c>#777777</c>).
    /// </summary>
    const string PausedColour = "777777";

    /// <summary>
    /// Each BUILDING record's build data by record id (<see cref="BuildingData"/>, asked once per record).
    /// </summary>
    readonly Dictionary<string, Data.Gameplay.Construction.BuildingBuildData?> _buildingData = new(StringComparer.Ordinal);

    /// <summary>
    /// The BUILDING record's construction target, the sum of its materials' required amounts (docs/game/buildings-production.md); null when unknown.
    /// The host sets it.
    /// </summary>
    public Func<string, float?>? BuildTarget { get; set; }

    /// <summary>
    /// The building panels' lines (docs/game/ui-screens.md "Selected building"): the name bar (name, owner), the state lines (Type, Town and, for the
    /// player's own site, the Dismantle and PAUSE CONSTRUCTION buttons), the stats lines (the first door's Open/Close and Security, buttons on the
    /// player's own building; Open to public for another faction's) and the status word of the medical header. A house speaks through its first door
    /// as the original selects it as that door: the door's lines come first.
    /// </summary>
    internal (PlayerInterface.HudLine Name, PlayerInterface.HudLine[] State, PlayerInterface.HudLine[] Stats, string Status, bool Bad,
        PlayerInterface.HudLine[] Extended) BuildingLines(SelectedBuilding focus)
    {
        var state = new List<PlayerInterface.HudLine>();
        var stats = new List<PlayerInterface.HudLine>();
        var extended = new List<PlayerInterface.HudLine>();
        bool own = focus.SiteId is not null;
        bool playerSite = focus.SiteId is { } siteId && !Simulation.Production.ProductionSystem.IsNatural(siteId);
        var site = player.Session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == focus.Instance);
        // A town building the player owns (bought, or the player's in the save) gets the buttons of a building of the player's
        // (the original's slot 79: the owner is the player's faction).
        var detail = player.Session.CurrentSnapshot.InspectedBuilding is { } d && d.Instance == focus.Instance ? d : null;
        bool mine = playerSite || detail is { Owned: true };
        var inspected = AddStateLines(focus, site, detail, own, playerSite, state, stats);

        // Slot 114 asks slot 97 (broken, +0x3b6) before completion: a machine being dismantled reads Broken, not Under construction.
        // A generator's own word still comes after.
        var unfinished = site is { Complete: false } && own ? player.Production.Machine(focus.SiteId) : null;
        var (status, bad) = SiteStatus(site, unfinished);
        if (unfinished is not null && ProductionWindows.GeneratorWord(unfinished) is { } word)
        {
            (status, bad) = word;
        }
        else
        {
            AddWorkLines(focus, detail, own, mine, stats, extended, ref status, ref bad);
        }

        AddPublicLines(focus, site, detail, mine, playerSite, stats);
        var header = HeaderLine(focus, detail, own, playerSite, mine, inspected);
        return (header, [.. state], [.. stats], status, bad, [.. extended]);
    }

    /// <summary>
    /// The building's state lines (Type, Town, a turret's heights, a bed's cost, the upgrades and construction buttons of the player's site) and a
    /// bed's healing line; returns the inspected richness of the building, if any.
    /// </summary>
    Simulation.Production.RichnessView? AddStateLines(SelectedBuilding focus, Simulation.Construction.BuildingSnapshot? site,
        Simulation.Locks.BuildingDetail? detail, bool own, bool playerSite, List<PlayerInterface.HudLine> state, List<PlayerInterface.HudLine> stats)
    {
        if (focus.Type is { Length: > 0 } type) state.Add(new("Type:", type));
        var inspected = player.Session.CurrentSnapshot.InspectedRichness is { } ir && ir.Instance == focus.Instance
            ? ir : (Simulation.Production.RichnessView?)null;
        if ((focus.Town ?? site?.TownName ?? detail?.Bed?.Town ?? inspected?.TownName) is { Length: > 0 } town) state.Add(new("Town:", town));
        if (own && player.Session.CurrentSnapshot.InspectedTurret is { } turret && turret.Instance == focus.Instance)
            AddHeightLines(turret, state);

        if (detail?.Bed is { } bed)
        {
            if (bed.Cost != 0)
                state.Add(new("Cost to use:", bed.Cost < 0 ? Loc.Tr("Private") : $"({Loc.Tr("Pay c.{1,num}", bed.Cost)})"));

            stats.Add(new("Healing bonus:", Loc.Tr("x{1,num}", bed.HealingBonus), PlayerInterface.MainColour));
        }

        if (playerSite && site is { LevelPlacement: null }) AddUpgradeLines(site, state);

        // The construction buttons (Building slot 117, FUN_1402fea10): Dismantle in BadBright, and while unfinished PAUSE CONSTRUCTION,
        // captioned [PAUSED] in #777777 while paused.
        if (playerSite && site is { LevelPlacement: null }) AddConstructionButtons(site, state);
        return inspected;
    }

    /// <summary>
    /// The medical header's status word of a site: Broken (destroyed, or an unfinished machine that is broken), the construction state while
    /// unfinished, else Normal.
    /// </summary>
    static (string Status, bool Bad) SiteStatus(Simulation.Construction.BuildingSnapshot? site, Simulation.Production.MachineSnapshot? unfinished)
    {
        string status = "Normal";
        bool bad = false;
        if (site is { Destroyed: true } || unfinished is { Broken: true })
        {
            status = "Broken";
            bad = true;
        }
        else if (site is { Complete: false })
        {
            status = site.Dismantling ? "Being dismantled" : site.Paused ? "Paused" : "Under construction";
            bad = true;
        }
        return (status, bad);
    }

    /// <summary>
    /// The stats lines of what the building does: its first door, a rock's richness, a machine's lines (or an unmined rock's), which may change
    /// the status word.
    /// </summary>
    void AddWorkLines(SelectedBuilding focus, Simulation.Locks.BuildingDetail? detail, bool own, bool mine, List<PlayerInterface.HudLine> stats,
        List<PlayerInterface.HudLine> extended, ref string status, ref bool bad)
    {
        if (FirstDoor(focus) is { } door)
        {
            // DoorStuff slot 40 (FUN_140304120): the value in the exe's green #a8b774, BadBright for a broken door, a closed locked one and
            // Locked; on the player's own door full-row buttons (width 1.0: the key is not shown, as the owner's screenshots show).
            if (door.Broken)
            {
                status = "Broken";
                bad = true;
            }

            AddDoorLines(door, mine, stats);
        }

        var rich = player.Session.CurrentSnapshot.InspectedRichness is { } r && r.Instance == focus.Instance
            ? r : (Simulation.Production.RichnessView?)null;
        if (rich is { } richness) ProductionWindows.AddRichnessLine(richness, stats);

        if (detail?.Bed is null && own && player.Production.Machine(focus.SiteId) is { } machine)
        {
            Simulation.Combat.TurretPanelView? turretPanel =
                player.Session.CurrentSnapshot.InspectedTurret is { } tp && tp.Instance == focus.Instance ? tp : null;
            player.Production.AddMachineLines(machine, stats, extended, ref status, ref bad, rich, turretPanel, mine);
        }
        else if (rich is { Efficiency: { } e } unmined)
        {
            // A rock nobody has mined yet has no machine; the original shows its lines all the same.
            (status, bad) = ProductionWindows.ProductionStatus(0, e);
            stats.Add(ProductionWindows.WorkersLine(e.Operators, e.MaxOperators));
            ProductionWindows.AddEfficiencyLines(e, unmined.Resource, stats);
        }
    }

    /// <summary>
    /// The Open to public line (a toggle on the player's town building with an interior, a caption on another faction's public one) and the
    /// For sale button.
    /// </summary>
    void AddPublicLines(SelectedBuilding focus, Simulation.Construction.BuildingSnapshot? site, Simulation.Locks.BuildingDetail? detail, bool mine,
        bool playerSite, List<PlayerInterface.HudLine> stats)
    {
        // Not on a player-built site under construction; an owned town house has it while being repaired too.
        if (mine && site is not { LevelPlacement: null, Complete: false }
            && (playerSite ? site?.TownName : focus.Town) is { Length: > 0 }
            && (site?.Record ?? focus.Record) is { } record && DataOf(record) is { HasInterior: true })
        {
            // The player's building with an interior in a town (the original's +0x1f0): a Kenshi_Button2 toggle of width 0.3, Public in Good,
            // Private in BadBright (slots 82 / 83).
            bool open = PublicOf(focus, site, detail);
            stats.Add(new("Open to public:", open ? "Public" : "Private", PlayerInterface.MainColour,
                open ? PlayerInterface.GoodColour : PlayerInterface.BadBrightColour, Action: "meitou:building-public", ButtonWidth: 0.3f));
        }
        else if (!mine && (PublicDayOf(focus, detail) || focus.PublicNight))
        {
            bool open = player.Session.Clock.IsDaytime ? PublicDayOf(focus, detail) : focus.PublicNight;
            stats.Add(new("Open to public:", open ? "Public" : "Private", PlayerInterface.MainColour,
                open ? PlayerInterface.GoodColour : PlayerInterface.BadColour));
        }

        if (detail?.SalePrice is { } price)
        {
            stats.Add(new("For sale:", Loc.Tr("c.{1,num}", price), PlayerInterface.GoodColour, PlayerInterface.GoodColour,
                Action: "meitou:building-buy", ButtonWidth: 0.4f));
        }
    }

    /// <summary>
    /// The name bar: the building's (or bed's) name and its owner, the player's faction for the player's own building.
    /// </summary>
    PlayerInterface.HudLine HeaderLine(SelectedBuilding focus, Simulation.Locks.BuildingDetail? detail, bool own, bool playerSite, bool mine,
        Simulation.Production.RichnessView? inspected)
    {
        // A rock (slot 113): the player's faction once a player character has worked it, else its owner nofac's name ("No Faction"; its town
        // NO_TOWN is explored, not secret).
        string? owner = focus.Owner;
        if (own && !playerSite)
        {
            owner = inspected is { Owner: >= 0 and var by } ? player.FactionName?.Invoke(by)
                : player.RecordName?.Invoke(Simulation.Factions.SquadManagement.DismissFallbackFaction);
        }

        var name = detail?.Bed?.Name ?? focus.Name;
        var value = mine ? player.FactionName?.Invoke(player.Session.World.Player.Faction) ?? "" : owner ?? "";
        return new PlayerInterface.HudLine(name, value, PlayerInterface.TitleColour, PlayerInterface.TitleColour);
    }

    /// <summary>
    /// The construction buttons of a player's site without a level placement: Dismantle, and while unfinished PAUSE CONSTRUCTION
    /// (or its [PAUSED] caption).
    /// </summary>
    static void AddConstructionButtons(Simulation.Construction.BuildingSnapshot site, List<PlayerInterface.HudLine> state)
    {
        state.Add(new("", Loc.Tr("Dismantle"), ValueColour: PlayerInterface.BadBrightColour, Action: "meitou:building-dismantle"));
        if (site.Complete) return;

        state.Add(site.Paused
            ? new("", Loc.Tr("[PAUSED]"), ValueColour: PausedColour, Action: "meitou:building-pause")
            : new("", Loc.Tr("PAUSE CONSTRUCTION"), ValueColour: PlayerInterface.MainColour, Action: "meitou:building-pause"));
    }

    /// <summary>
    /// The first door's Open/Close and Security rows: buttons on the player's own door, plain values on another faction's.
    /// </summary>
    static void AddDoorLines(Simulation.Locks.DoorDetail door, bool mine, List<PlayerInterface.HudLine> stats)
    {
        var doorState = door.Broken ? "The door is broken" : door.Open ? "The door is open" : "The door is closed";
        var doorColour = door.Broken || !door.Open && door.Locked ? PlayerInterface.BadBrightColour : PlayerInterface.GoodColour;
        var doorTip = door.Broken ? "It will no longer close and needs to be repaired." : null;
        stats.Add(new(mine ? "" : "Open/Close:", doorState, PlayerInterface.MainColour, doorColour, doorTip, mine ? "meitou:building-doors" : null));

        // Meitou: an open door that wants its lock (the lock button's setting; it locks when it closes) reads Locked, else the button changed
        // nothing visible.
        bool locked = door.Locked || door.WantsLock && door.Open;
        var security = $"{Loc.Tr(locked ? "Locked" : "Unlocked")} ({Loc.Tr("lvl {1,num}", door.Level)})";
        var securityColour = locked ? PlayerInterface.BadBrightColour : PlayerInterface.GoodColour;
        var securityTip = door.Locked ? "The door is locked and will keep out intruders."
            : locked ? "The door will lock when it closes." : "The door is unlocked.";
        stats.Add(new(mine ? "" : "Security:", security, PlayerInterface.MainColour, securityColour, securityTip,
            mine ? "meitou:building-locks" : null));
    }

    /// <summary>
    /// The upgrade chain of a BUILDING record and its build data (the panel's [UPGRADE] / [DOWNGRADE] and their tooltips); the host sets them.
    /// </summary>
    public Func<string, string?>? UpgradeOf { get; set; }
    /// <summary>
    /// The BUILDING record that upgrades to this one; null when none does (the host sets it).
    /// </summary>
    public Func<string, string?>? DowngradeOf { get; set; }
    /// <summary>
    /// A BUILDING record's build data; null for one without (the host sets it).
    /// </summary>
    public Func<string, Data.Gameplay.Construction.BuildingBuildData?>? BuildingData { get; set; }

    /// <summary>
    /// A record's build data, asked of <see cref="BuildingData"/> once and kept.
    /// </summary>
    Data.Gameplay.Construction.BuildingBuildData? DataOf(string record) =>
        _buildingData.TryGetValue(record, out var data) ? data : _buildingData[record] = BuildingData?.Invoke(record);

    /// <summary>
    /// The upgrade lines of a site of the player's (Building slot 116, docs/game/ui-screens.md "Selected building"): <c>[UPGRADE]</c> in Good when
    /// its record upgrades to a BUILDING research has unlocked, <c>[DOWNGRADE]</c> in Main while it is unfinished and some BUILDING upgrades to it.
    /// Each button's tooltip names the record it becomes; the upgrade's also lists the materials still owed (the new record's, less what is delivered
    /// of each item) and the power output's change. Engine scope as the simulation's: no walls.
    /// </summary>
    void AddUpgradeLines(Simulation.Construction.BuildingSnapshot site, List<PlayerInterface.HudLine> state)
    {
        var snapshot = player.Session.CurrentSnapshot;
        if (site.WallParent is not null || DataOf(site.Record) is not ({ LinksTogether: false } or { IsWallGate: true })) return;
        if (UpgradeOf?.Invoke(site.Record) is { } up && snapshot.UnlockedBuildings.Contains(up) && DataOf(up) is { } higher)
        {
            var had = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var m in site.Materials) had[m.Item] = had.GetValueOrDefault(m.Item) + m.Delivered;
            var tip = new StringBuilder($"{Loc.Tr("Upgrades To:")} {higher.Name}");
            foreach (var cost in higher.Materials)
            {
                float delivered = Math.Min(had.GetValueOrDefault(cost.Item), cost.Required);
                had[cost.Item] = had.GetValueOrDefault(cost.Item) - delivered;
                if (cost.Required - delivered > 0)
                {
                    var itemName = player.Build.Services?.ItemName(cost.Item) ?? cost.Item;
                    var owed = (cost.Required - delivered).ToString("0.##", CultureInfo.InvariantCulture);
                    tip.Append(CultureInfo.InvariantCulture, $"\n      {itemName} +{owed}");
                }
            }

            if (PowerOutput?.Invoke(up) - PowerOutput?.Invoke(site.Record) is { } power and not 0)
                tip.Append(CultureInfo.InvariantCulture, $"\n{Loc.Tr("Power")} {power}");

            var upgradeCaption = $"[{Loc.Tr("Upgrade").ToUpper(CultureInfo.CurrentCulture)}]";
            state.Add(new("", upgradeCaption, ValueColour: PlayerInterface.GoodColour, Tooltip: tip.ToString(), Action: "meitou:building-upgrade"));
        }

        if (!site.Complete && DowngradeOf?.Invoke(site.Record) is { } down && DataOf(down) is { } lower)
        {
            var downgradeCaption = $"[{Loc.Tr("Downgrade").ToUpper(CultureInfo.CurrentCulture)}]";
            state.Add(new("", downgradeCaption, ValueColour: PlayerInterface.MainColour, Tooltip: $"{Loc.Tr("Downgrade To:")} {lower.Name}",
                Action: "meitou:building-downgrade"));
        }
    }

    /// <summary>
    /// A BUILDING record's <c>power output</c> (the upgrade tooltip's Power change); the host sets it.
    /// </summary>
    public Func<string, int?>? PowerOutput { get; set; }

    /// <summary>
    /// A turret's section 3 rows (<c>FUN_14030c2a0</c>, <b>Observed</b> by disassembly): <c>Height:</c> int((building y - last aim y) x 0.1) m, "The
    /// turrets height above the last target we aimed at."; <c>Height bonus:</c> "+" int((h - 1) x 100)% with the bonus text naming the height in the
    /// Title tag (the penalty text only for a negative h, which h never is). The original leaves out Height for some buildings (slots 0x268 / 0x270 /
    /// 0x228, not decoded) and reads the ground under the building before any aim; Meitou shows it once the turret has aimed.
    /// </summary>
    static void AddHeightLines(Simulation.Combat.TurretPanelView turret, List<PlayerInterface.HudLine> state)
    {
        string metres = Loc.Tr("{1,num} m", turret.HeightMetres ?? 0);
        if (turret.HeightMetres is not null)
            state.Add(new("Height:", metres, Tooltip: Loc.Tr("The turrets height above the last target we aimed at.")));
        string tip = turret.Height >= 0
            ? Loc.Tr("The additional bonus to range, speed and power that resulted from having a height difference of {1} above the last target.",
                TooltipRuntime.TitleTag + metres + ProductionWindows.InkTag)
            : Loc.Tr("The additional penalty to range, speed and power that resulted from having a height difference of {1} below the last target.",
                TooltipRuntime.TitleTag + metres + ProductionWindows.InkTag);
        string bonus = "+" + Loc.Tr("{1,num}%", (int)((turret.Height - 1) * 100));
        var bonusColour = turret.Height >= 0 ? PlayerInterface.GoodColour : PlayerInterface.BadColour;
        state.Add(new("Height bonus:", bonus, ValueColour: bonusColour, Tooltip: tip));
    }

    /// <summary>
    /// The gun's <c>Loaded</c> bar (<c>GunClass</c> slot 14 <c>FUN_14030ce20</c> section 4, <b>Observed</b>): "{loaded}/{shots}", filled (loaded + 1
    /// - gun +0xe0) / shots, so it drains from (loaded + 1) / shots while a reload runs; its rows Base Reload Speed (the reload times, 2 decimals),
    /// User Skill and, with a gunner, User Reload Speed from the gunner's Crossbows level (stat 0x23, as the base class reads it).
    /// </summary>
    static PlayerInterface.MedicalRow LoadedRow(Simulation.Combat.TurretPanelView g)
    {
        static string F2(float v) => v.ToString("F2", CultureInfo.InvariantCulture);
        float fill = g.Shots > 0 ? Math.Clamp((g.Loaded + g.ReloadLeft) / g.Shots, 0, 1) : 0;
        var tip = new List<string> { Loc.Tr("Base Reload Speed:") + " " + Loc.Tr("{1}-{2} seconds", F2(g.ReloadMin), F2(g.ReloadMax)),
            Loc.Tr("User Skill:") + " " + ((int)(g.Crossbows ?? 0)).ToString(CultureInfo.InvariantCulture) };
        if (g.Crossbows is { } level)
        {
            float time = Simulation.Combat.RangedFormulas.ReloadTime(level * 0.01f, g.ReloadMin, g.ReloadMax);
            tip.Add(Loc.Tr("User Reload Speed:") + " " + Loc.Tr((int)time == 1 ? "{1} second" : "{1} seconds", F2(time)));
        }
        return new(Loc.Tr("Loaded"), 0, fill, 0, $"{g.Loaded}/{g.Shots}", "", false, string.Join("\n", tip));
    }

    #endregion

    #region Rows and actions

    /// <summary>
    /// The selected building's first door as the simulation publishes it, or null.
    /// </summary>
    internal Simulation.Locks.DoorDetail? FirstDoor(SelectedBuilding focus) =>
        player.Session.CurrentSnapshot.InspectedBuilding is { } detail && detail.Instance == focus.Instance && detail.Doors.Count > 0
            ? detail.Doors[0]
            : null;

    /// <summary>
    /// The medical panel's rows of a selected building (its section 4, docs/game/ui-screens.md "Selected building"): the first door's <c>Door
    /// Condition</c>, then <c>Building Condition</c> (<c>Condition</c> without doors), each the construction progress as the number and its fraction
    /// of the target as the bar, then while unfinished a row per material, delivered over required. Meitou models no building damage, so a finished
    /// building's progress is its target (the original's damaged state lowers it).
    /// </summary>
    internal List<PlayerInterface.MedicalRow> BuildingRows(SelectedBuilding focus)
    {
        var rows = new List<PlayerInterface.MedicalRow>();
        // A rock has no site in the registry: its Condition is its record's target, complete (an Iron Resource's build materials 2).
        var door = FirstDoor(focus);
        if (door is { Record.Length: > 0 } && BuildTarget?.Invoke(door.Value.Record) is { } doorTarget)
        {
            var doorValue = ((int)doorTarget).ToString(CultureInfo.InvariantCulture);
            rows.Add(Bar(Loc.Tr("Door Condition"), 1, doorValue, "The door is in good condition."));
        }

        var site = player.Session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == focus.Instance);
        float? target = site?.Target ?? (focus.Record is { } record ? BuildTarget?.Invoke(record) : null);
        if (target is not { } total) return rows;
        float progress = (site?.Destroyed ?? focus.Destroyed) ? 0 : site?.Progress ?? total;
        bool complete = site?.Complete ?? true;
        float fraction = total > 0 ? Math.Min(progress / total, 1) : 1;
        string tip = !complete ? Loc.Tr("The building is under construction and at {1,num}% completion.", (int)(fraction * 100))
            : fraction >= 1 ? Loc.Tr("The building is in good condition.") : Loc.Tr("The building is damaged.");
        var progressValue = ((int)progress).ToString(CultureInfo.InvariantCulture);
        rows.Add(Bar(Loc.Tr(door is null ? "Condition" : "Building Condition"), fraction, progressValue, tip));

        // A wind generator's rows (WindGeneratorBuilding::getGUIPower section 4): Wind, its share as a whole percent and the bar; Power Output,
        // the output as an int over the rated.
        if (site is { Complete: true } && player.Production.Machine(site.Id) is { Wind: { Rated: > 0 } wind, Power: { } power })
        {
            string windTip = Loc.Tr("The current amount of wind.");
            if (wind.Range is { } range)
            {
                windTip += "\n" + Loc.Tr("Operating wind speed is between {1,num=fix,p=2} mph and {2,num=fix,p=2} mph.",
                    range.Min * ProductionWindows.MphPerUnit, range.Max * ProductionWindows.MphPerUnit);
            }

            var windShare = ((int)(wind.Share * 100)).ToString(CultureInfo.InvariantCulture) + "%";
            rows.Add(new(Loc.Tr("Wind"), 0, wind.Share, 0, windShare, "", false, windTip));
            float output = power.Demand;
            string outputTip = !wind.PowerOn ? "Power is switched off."
                : output <= wind.Rated - 1
                    ? wind.Share > 0 ? "Power output is running at reduced efficiency due to damage."
                        : "Currently not enough wind to generate any power"
                    : "Power output is at maximum.";
            rows.Add(Bar(Loc.Tr("Power Output"), output / wind.Rated, ((int)output).ToString(CultureInfo.InvariantCulture), outputTip));
        }

        if (site is { Complete: true } && player.Session.CurrentSnapshot.InspectedTurret is { } gun && gun.Instance == focus.Instance)
            rows.Add(LoadedRow(gun));

        if (site is { Complete: false })
        {
            foreach (var m in site.Materials)
            {
                float share = m.Required > 0 ? Math.Min(m.Delivered / m.Required, 1) : 1;
                string need = m.Delivered >= m.Required ? Loc.Tr("All necessary materials are ready.")
                    : Loc.Tr("{1} more 'Building Materials' will be needed to complete this construction.",
                        (m.Required - m.Delivered).ToString("0.##", CultureInfo.InvariantCulture));
                var itemName = player.Build.Services?.ItemName(m.Item) ?? m.Item;
                rows.Add(Bar(itemName, share, $"{(int)m.Delivered}/{(int)m.Required}", need));
            }
        }

        return rows;

        // The rows a DataPanel adds with a fraction (FUN_140729f90): the bar in the healed (yellow) layer, as a base-game screenshot of a town
        // house shows it.
        static PlayerInterface.MedicalRow Bar(string name, float fraction, string value, string tooltip) =>
            new(name, 0, fraction, 0, value, "", false, Loc.Tr(tooltip));
    }

    /// <summary>
    /// Whether a building of the player's is open to the public: a site's flag; a town building's as the player set it, else as its state reads
    /// (<c>is public day</c>, Building+0xc8).
    /// </summary>
    static bool PublicOf(SelectedBuilding focus, Simulation.Construction.BuildingSnapshot? site, Simulation.Locks.BuildingDetail? detail) =>
        site?.Public ?? detail?.Public ?? focus.PublicDay;

    /// <summary>
    /// Whether someone else's building is open to the public by day: as its residents' template set it (the original writes the template's
    /// <c>public day</c> over the building's flag when the squad is housed, which is what the residents' AI reads), else as its level state reads.
    /// </summary>
    static bool PublicDayOf(SelectedBuilding focus, Simulation.Locks.BuildingDetail? detail) => detail?.ResidentsPublic ?? focus.PublicDay;

    /// <summary>
    /// The panel's own buttons on a selected building of the player's: Dismantle (after the original's "Are you sure?"), pause, upgrade, downgrade,
    /// the doors, their locks and Open to public.
    /// </summary>
    internal void BuildingAction(string action)
    {
        if (BuildingFocus() is not { } focus) return;
        var detail = player.Session.CurrentSnapshot.InspectedBuilding is { } d && d.Instance == focus.Instance ? d : null;
        if (action == "building-buy" && detail?.SalePrice is { } price)
        {
            string instance = focus.Instance;
            player.InterfaceSounds.Sound("General_Click");
            player.Confirm(focus.Name, Loc.Tr("Do you want to buy this building for c.{1,num}?", price),
                () => player.Say(new Simulation.Locks.BuyBuilding(instance, price) { Tick = player.Session.World.Tick }), yes: "Yes", cancel: "No");
            return;
        }

        if (focus.SiteId is null && detail is { Owned: true })
        {
            // A town building the player owns: its doors and the public toggle.
            TownBuildingAction(focus, detail, action);
            return;
        }

        if (focus.SiteId is not { } id || Simulation.Production.ProductionSystem.IsNatural(id)) return;

        SiteAction(id, action);
    }

    /// <summary>
    /// The doors, their locks and the public toggle of a town building the player owns.
    /// </summary>
    void TownBuildingAction(SelectedBuilding focus, Simulation.Locks.BuildingDetail detail, string action)
    {
        if (action == "building-doors")
        {
            player.Say(new Simulation.Locks.ToggleDoorsOrder(focus.Instance) { Tick = player.Session.World.Tick });
        }
        else if (action == "building-locks")
        {
            player.Say(new Simulation.Locks.ToggleDoorLocksOrder(focus.Instance) { Tick = player.Session.World.Tick });
        }
        else if (action == "building-public")
        {
            player.InterfaceSounds.Sound("General_Click");
            var nowPublic = !PublicOf(focus, null, detail);
            player.Say(new Simulation.Construction.SetBuildingPublic(focus.Instance, nowPublic) { Tick = player.Session.World.Tick });
        }
    }

    /// <summary>
    /// The buttons of a site of the player's: upgrade, downgrade, public, dismantle, the machines, pause and doors.
    /// </summary>
    void SiteAction(string id, string action)
    {
        var site = player.Session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == id);
        if (action == "building-upgrade") player.Say(new Simulation.Construction.UpgradeSite(id) { Tick = player.Session.World.Tick });
        else if (action == "building-downgrade") player.Say(new Simulation.Construction.DowngradeSite(id) { Tick = player.Session.World.Tick });
        else if (action == "building-public" && site is not null)
        {
            player.InterfaceSounds.Sound("General_Click");
            player.Say(new Simulation.Construction.SetBuildingPublic(id, !site.Public) { Tick = player.Session.World.Tick });
        }

        if (action == "building-dismantle" && site?.LevelPlacement is null)
        {
            // Slot 84 (FUN_14054db30): with furniture in it the furniture warning, else "Are you sure?"; Yes dismantles it at once
            // (Building::confirmDismantle).
            bool furnished = player.Session.CurrentSnapshot.Buildings.Any(b => b.Parent == id);
            var warning = furnished ? "Dismantling this building will destroy all furniture.\nDo you want to continue?" : "Are you sure?";
            player.Confirm("Dismantle", warning,
                () => player.Say(new Simulation.Construction.ConfirmDismantle(id) { Tick = player.Session.World.Tick }));
        }
        else if (action is "machine-power" or "machine-battery" && player.Production.Machine(id) is { Power: { } pw } machine)
        {
            // A click flips the toggle, sets the Generator switch and posts UI_Generator (vfunc 161, FUN_140297a60).
            bool on = action == "machine-power" ? !(machine.Wind?.PowerOn ?? pw.PowerOn) : !pw.GetsBattery;
            player.InterfaceSounds.Sound("UI_Generator", "Generator", on ? "On" : "Off");
            player.Say(action == "machine-power"
                ? new Simulation.Production.SetMachinePower(id, PowerOn: on) { Tick = player.Session.World.Tick }
                : new Simulation.Production.SetMachinePower(id, GetsBattery: on) { Tick = player.Session.World.Tick });
        }
        else if (action == "machine-incinerate" && player.Production.Machine(id) is { Furnace: not null })
        {
            player.InterfaceSounds.Sound("General_Click");   // engine choice: the button's own sound is not traced
            player.Say(new Simulation.Production.IncinerateFurnace(id) { Tick = player.Session.World.Tick });
        }
        else if (action == "building-pause" && site is { Complete: false })
            player.Say(new Simulation.Construction.PauseSite(id, !site.Paused) { Tick = player.Session.World.Tick });
        else if (action == "building-doors") player.Say(new Simulation.Locks.ToggleDoorsOrder(id) { Tick = player.Session.World.Tick });
        else if (action == "building-locks") player.Say(new Simulation.Locks.ToggleDoorLocksOrder(id) { Tick = player.Session.World.Tick });
    }

    #endregion
}