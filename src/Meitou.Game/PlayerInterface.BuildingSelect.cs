using System.Globalization;
using System.Text;
using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// Clicking a building (docs/game/ui-input.md "Building picking and selection", docs/game/ui-screens.md "Selected building"): a left click on the player's sites and on the level data's
/// buildings selects one, which fills the HUD's name, state, stats and medical-header panels in place of the character's and leaves the character selection alone. Picking uses the buildings'
/// collision footprints (a vertical prism over the convex hull of the record's shapes), not the original's per-part collision rays; characters under the cursor win over a building.
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>A building selected by a click: what it is, where it stands (hull in the world, for the outline) and what the panels say of it.</summary>
    internal sealed record SelectedBuilding(string Name, string? Type, string? Owner, string? Town, string? SiteId, string Instance, Vector3 Position, Vector2[]? Hull,
        bool PublicDay = false, bool PublicNight = false, string? Record = null, bool Destroyed = false);

    internal SelectedBuilding? selectedBuilding;

    /// <summary>A left click on the ground ends the building's selection (Advanced settings "Click away deselects"); false: it stays, as in the game.</summary>
    public bool DeselectBuildings { get; set; } = true;

    /// <summary>The selected building's instance (a level building's placement key, a site's id), null when none: the building the interior editor shows.</summary>
    public string? SelectedBuildingKey => selectedBuilding?.Instance;

    /// <summary>Makes <paramref name="building"/> the selected one (null: none) and tells the simulation, which then publishes its doors.</summary>
    internal void SetBuilding(SelectedBuilding? building)
    {
        if (ReferenceEquals(selectedBuilding, building)) return;
        selectedBuilding = building;
        closedMachine = null;   // clicking a building asks for its window again, also when the player closed it before (the mark only held until another was selected)
        // Selecting calls the building's select sound (FUN_1405504a0: Buildings_Click = its `select sound`, none for 0); a click on the same building picks it anew and sounds again.
        if (building?.Record is { } record) Sound("Building_Click", building: record);
        Say(new InspectBuildingCommand(building?.Instance) { Tick = session.World.Tick });
    }

    /// <summary>The selected building while it still exists (a site that was cancelled or dismantled drops the selection).</summary>
    internal SelectedBuilding? BuildingFocus()
    {
        if (selectedBuilding is { SiteId: { } id } && !Meitou.Simulation.Production.ProductionSystem.IsNatural(id) && !session.CurrentSnapshot.Buildings.Any(s => s.Id == id)) SetBuilding(null);
        return selectedBuilding;
    }

    /// <summary>
    /// The buildings to outline (docs/game/ui-input.md "Hover outline"): the selected one in white, then, with <paramref name="hover"/>, the one under the cursor
    /// (<see cref="HoverBuilding"/>) unless it is the selected one; each with its placement key and its floor (the height it stands at, where the outline stops).
    /// </summary>
    public List<(string Key, float Floor, Vector4 Colour)> OutlinedBuildings(bool hover)
    {
        var list = new List<(string Key, float Floor, Vector4 Colour)>(2);
        if (selectedBuilding is { } b) list.Add((b.Instance, b.Position.Y, WhiteOutline));
        if (hover && HoverBuilding is { } h && h.Key != selectedBuilding?.Instance) list.Add(h);
        return list;
    }

    /// <summary>A site of the player's as a selected building (what a left click on it selects).</summary>
    SelectedBuilding SiteBuilding(Meitou.Simulation.Construction.BuildingSnapshot site) =>
        new(SiteName(site), null, FactionName?.Invoke(session.World.Player.Faction), null, site.Id, site.Id, site.Position,
            (buildHost?.Placement.Footprint(site.Record) ?? buildHost?.Placement.Plot(site.Record)) is { } f ? f.Place(site.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, Meitou.Game.Building.PlacementCheck.YawOf(site.Rotation))) : null,
            Record: site.Record);

    /// <summary>The nearest building under the pixel, a site of the player's or a level-data one, when the ray meets it before the ground.</summary>
    internal SelectedBuilding? PickBuilding(Vector2 pixel) => PickBuildingAt(pixel)?.Building;

    /// <summary><see cref="PickBuilding"/> with the distance along the ray.</summary>
    (SelectedBuilding Building, float T)? PickBuildingAt(Vector2 pixel)
    {
        if (Ray(pixel) is not { } ray) return null;
        // A visible bed takes the click over the house around it, as a plain building is no right-click candidate (PickAt); a nearer object site or rock still wins.
        if (PickBedAt(pixel) is { } bedHit && !(PickSiteAt(pixel) is { } before && before.Site.Id + "@bed0" != bedHit.Bed.Id && SiteIsObject(before.Site) && before.T < bedHit.T)
            && !(PickRockAt(pixel) is { } stone && stone.T < bedHit.T))
        {
            var bed = bedHit.Bed;
            if (session.CurrentSnapshot.Buildings.FirstOrDefault(s => bed.Id == s.Id + "@bed0") is { } bedSite)
                return (SiteBuilding(bedSite), bedHit.T);
            var spot = new SelectedBuilding(buildHost?.ItemName(bed.Record) ?? bed.Record, null, FactionName?.Invoke(bed.Owner), null,
                null, bed.Id, bed.Position, null, Record: bed.Record);
            return (spot, bedHit.T);
        }
        float limit = GroundHit(ray.Origin, ray.Direction) is { } groundPoint ? Vector3.Distance(ray.Origin, groundPoint) : 6000;
        float bestT = limit;
        SelectedBuilding? best = null;
        var site = PickSiteAt(pixel);
        if (site is { } nearest && nearest.T < bestT)
        {
            bestT = nearest.T;
            best = SiteBuilding(nearest.Site);
        }
        Meitou.Simulation.Navigation.PlacedBuilding? level = null;
        if (buildHost is { } host)
            foreach (var candidate in host.Placement.StaticAlong(ray.Origin, ray.Direction, limit))
            {
                var b = candidate.Placement;
                if (levelEditorPanel.levelHost?.IsCleared(b.Key) == true) continue;   // the level editor cleared it
                float y = b.WorldY ?? ground(b.Position.X, b.Position.Z) + b.Position.Y;
                var hull = candidate.Footprint.Place(b.Position, b.Rotation);
                float? hit = candidate.Plot ? PlotT(ray, hull, limit < 6000 ? limit : null)
                    : BuildingRayT(ray, hull, candidate.Footprint.YMin, candidate.Footprint.YMax, b.BuildingId, b.Position, y, b.Rotation);
                if (hit is not { } t || t >= bestT) continue;
                bestT = t;
                level = candidate;
                var info = host.WorldInfo(b);
                best = new(info.Name, info.Type, info.Owner, info.Town, null, b.Key, new Vector3(b.Position.X, y, b.Position.Z), hull, info.PublicDay, info.PublicNight, b.BuildingId, info.Destroyed);
            }
        if (level is { } shown && site is { } inner && inner.T > bestT && SiteShownInside(ray, shown, inner))
        {
            bestT = inner.T;
            best = SiteBuilding(inner.Site);
        }
        if (PickRockAt(pixel) is { } rock && rock.T < bestT) (best, bestT) = (RockBuilding(rock.Rock), rock.T);
        return best is { } found ? (found, bestT) : null;
    }

    /// <summary>
    /// Whether a site met behind a level building's hit stands inside that building and the view shows it there (furniture in a bought house): the level building is tested against its
    /// whole collision, roof included, while the original passes a building's hidden roof and upper floors in the interior view (docs/game/ui-input.md "Building picking and selection").
    /// </summary>
    bool SiteShownInside((Vector3 Origin, Vector3 Direction) ray, Meitou.Simulation.Navigation.PlacedBuilding level, (Meitou.Simulation.Construction.BuildingSnapshot Site, float T) site) =>
        !level.Plot && !Hidden(ray, site.T) && AboveViewedFloor?.Invoke(site.Site.Position) != true
        && level.Footprint.Gap(level.Placement.Position, level.Placement.Rotation, new Vector2(site.Site.Position.X, site.Site.Position.Z)) == 0;

    /// <summary>The colour of the [PAUSED] caption (the exe's static <c>#777777</c>).</summary>
    const string PausedColour = "777777";

    /// <summary>The BUILDING record's construction target, the sum of its materials' required amounts (docs/game/buildings-production.md); null when unknown. The host sets it.</summary>
    public Func<string, float?>? BuildTarget { get; set; }

    /// <summary>
    /// The building panels' lines (docs/game/ui-screens.md "Selected building"): the name bar (name, owner), the state lines (Type, Town and, for the player's own site, the
    /// Dismantle and PAUSE CONSTRUCTION buttons), the stats lines (the first door's Open/Close and Security, buttons on the player's own building; Open to public for another
    /// faction's) and the status word of the medical header. A house speaks through its first door as the original selects it as that door: the door's lines come first.
    /// </summary>
    internal (HudLine Name, HudLine[] State, HudLine[] Stats, string Status, bool Bad, HudLine[] Extended) BuildingLines(SelectedBuilding focus)
    {
        var state = new List<HudLine>();
        var stats = new List<HudLine>();
        var extended = new List<HudLine>();
        string status = "Normal";
        bool bad = false;
        bool own = focus.SiteId is not null;
        bool player = focus.SiteId is { } siteId && !Meitou.Simulation.Production.ProductionSystem.IsNatural(siteId);
        var site = session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == focus.Instance);
        // A town building the player owns (bought, or the player's in the save) gets the buttons of a building of the player's (the original's slot 79: the owner is the player's faction).
        var detail = session.CurrentSnapshot.InspectedBuilding is { } d && d.Instance == focus.Instance ? d : null;
        bool mine = player || detail is { Owned: true };
        if (focus.Type is { Length: > 0 } type) state.Add(new("Type:", type));
        var inspected = session.CurrentSnapshot.InspectedRichness is { } ir && ir.Instance == focus.Instance ? ir : (Meitou.Simulation.Production.RichnessView?)null;
        if ((focus.Town ?? site?.TownName ?? detail?.Bed?.Town ?? inspected?.TownName) is { Length: > 0 } town) state.Add(new("Town:", town));
        if (own && session.CurrentSnapshot.InspectedTurret is { } turret && turret.Instance == focus.Instance) AddHeightLines(turret, state);
        if (detail?.Bed is { } bed)
        {
            if (bed.Cost != 0)
                state.Add(new("Cost to use:", bed.Cost < 0 ? Loc.Tr("Private") : $"({Loc.Tr("Pay c.{1,num}", bed.Cost)})"));
            stats.Add(new("Healing bonus:", Loc.Tr("x{1,num}", bed.HealingBonus), MainColour));
        }
        if (player && site is { LevelPlacement: null }) AddUpgradeLines(site, state);
        // The construction buttons (Building slot 117, FUN_1402fea10): Dismantle in BadBright, and while unfinished PAUSE CONSTRUCTION, captioned [PAUSED] in #777777 while paused.
        if (player && site is { LevelPlacement: null })
        {
            state.Add(new("", Loc.Tr("Dismantle"), ValueColour: BadBrightColour, Action: "meitou:building-dismantle"));
            if (!site.Complete) state.Add(site.Paused ? new("", Loc.Tr("[PAUSED]"), ValueColour: PausedColour, Action: "meitou:building-pause")
                : new("", Loc.Tr("PAUSE CONSTRUCTION"), ValueColour: MainColour, Action: "meitou:building-pause"));
        }
        // Slot 114 asks slot 97 (broken, +0x3b6) before completion: a machine being dismantled reads Broken, not Under construction. A generator's own word still comes after.
        var unfinished = site is { Complete: false } && own ? Machine(focus.SiteId) : null;
        if (site is { Destroyed: true } || unfinished is { Broken: true }) { status = "Broken"; bad = true; }
        else if (site is { Complete: false })
        {
            status = site.Dismantling ? "Being dismantled" : site.Paused ? "Paused" : "Under construction";
            bad = true;
        }
        if (unfinished is not null && GeneratorWord(unfinished) is { } word) (status, bad) = word;
        else
        {
            if (FirstDoor(focus) is { } door)
            {
                // DoorStuff slot 40 (FUN_140304120): the value in the exe's green #a8b774, BadBright for a broken door, a closed locked one and Locked; on the player's own door
                // full-row buttons (width 1.0: the key is not shown, as the owner's screenshots show).
                if (door.Broken) { status = "Broken"; bad = true; }
                stats.Add(new(mine ? "" : "Open/Close:", door.Broken ? "The door is broken" : door.Open ? "The door is open" : "The door is closed", MainColour,
                    door.Broken || !door.Open && door.Locked ? BadBrightColour : GoodColour, door.Broken ? "It will no longer close and needs to be repaired." : null,
                    mine ? "meitou:building-doors" : null));
                // Meitou: an open door that wants its lock (the lock button's setting; it locks when it closes) reads Locked, else the button changed nothing visible.
                bool locked = door.Locked || door.WantsLock && door.Open;
                stats.Add(new(mine ? "" : "Security:", $"{Loc.Tr(locked ? "Locked" : "Unlocked")} ({Loc.Tr("lvl {1,num}", door.Level)})", MainColour, locked ? BadBrightColour : GoodColour,
                    door.Locked ? "The door is locked and will keep out intruders." : locked ? "The door will lock when it closes." : "The door is unlocked.", mine ? "meitou:building-locks" : null));
            }
            var rich = session.CurrentSnapshot.InspectedRichness is { } r && r.Instance == focus.Instance ? r : (Meitou.Simulation.Production.RichnessView?)null;
            if (rich is { } richness) AddRichnessLine(richness, stats);
            if (detail?.Bed is null && own && Machine(focus.SiteId) is { } machine) AddMachineLines(machine, stats, extended, ref status, ref bad, rich,
                session.CurrentSnapshot.InspectedTurret is { } tp && tp.Instance == focus.Instance ? tp : null, mine);
            else if (rich is { Efficiency: { } e } unmined)
            {
                // A rock nobody has mined yet has no machine; the original shows its lines all the same.
                (status, bad) = ProductionStatus(0, e);
                stats.Add(WorkersLine(e.Operators, e.MaxOperators));
                AddEfficiencyLines(e, unmined.Resource, stats);
            }
        }
        // Not on a player-built site under construction; an owned town house has it while being repaired too.
        if (mine && site is not { LevelPlacement: null, Complete: false } && (player ? site?.TownName : focus.Town) is { Length: > 0 } && (site?.Record ?? focus.Record) is { } record && DataOf(record) is { HasInterior: true })
        {
            // The player's building with an interior in a town (the original's +0x1f0): a Kenshi_Button2 toggle of width 0.3, Public in Good, Private in BadBright (slots 82 / 83).
            bool open = PublicOf(focus, site, detail);
            stats.Add(new("Open to public:", open ? "Public" : "Private", MainColour, open ? GoodColour : BadBrightColour, Action: "meitou:building-public", ButtonWidth: 0.3f));
        }
        else if (!mine && (focus.PublicDay || focus.PublicNight))
        {
            bool open = session.Clock.IsDaytime ? focus.PublicDay : focus.PublicNight;
            stats.Add(new("Open to public:", open ? "Public" : "Private", MainColour, open ? GoodColour : BadColour));
        }
        if (detail?.SalePrice is { } price)
            stats.Add(new("For sale:", Loc.Tr("c.{1,num}", price), GoodColour, GoodColour, Action: "meitou:building-buy", ButtonWidth: 0.4f));
        // A rock (slot 113): the player's faction once a player character has worked it, else its owner nofac's name ("No Faction"; its town NO_TOWN is explored, not secret).
        string? owner = focus.Owner;
        if (own && !player)
            owner = inspected is { Owner: >= 0 and var by } ? FactionName?.Invoke(by) : RecordName?.Invoke(Meitou.Simulation.Factions.SquadManagement.DismissFallbackFaction);
        return (new(detail?.Bed?.Name ?? focus.Name, mine ? FactionName?.Invoke(session.World.Player.Faction) ?? "" : owner ?? "", TitleColour, TitleColour), [.. state], [.. stats], status, bad, [.. extended]);
    }

    /// <summary>The upgrade chain of a BUILDING record and its build data (the panel's [UPGRADE] / [DOWNGRADE] and their tooltips); the host sets them.</summary>
    public Func<string, string?>? UpgradeOf { get; set; }
    public Func<string, string?>? DowngradeOf { get; set; }
    public Func<string, Meitou.Data.Gameplay.Construction.BuildingBuildData?>? BuildingData { get; set; }
    readonly Dictionary<string, Meitou.Data.Gameplay.Construction.BuildingBuildData?> buildingData = new(StringComparer.Ordinal);

    Meitou.Data.Gameplay.Construction.BuildingBuildData? DataOf(string record) =>
        buildingData.TryGetValue(record, out var data) ? data : buildingData[record] = BuildingData?.Invoke(record);

    /// <summary>
    /// The upgrade lines of a site of the player's (Building slot 116, docs/game/ui-screens.md "Selected building"): <c>[UPGRADE]</c> in Good when its record upgrades to a BUILDING research
    /// has unlocked, <c>[DOWNGRADE]</c> in Main while it is unfinished and some BUILDING upgrades to it. Each button's tooltip names the record it becomes; the upgrade's also lists the
    /// materials still owed (the new record's, less what is delivered of each item) and the power output's change. Engine scope as the simulation's: no walls.
    /// </summary>
    void AddUpgradeLines(Meitou.Simulation.Construction.BuildingSnapshot site, List<HudLine> state)
    {
        var snapshot = session.CurrentSnapshot;
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
                    tip.Append(CultureInfo.InvariantCulture, $"\n      {buildHost?.ItemName(cost.Item) ?? cost.Item} +{(cost.Required - delivered).ToString("0.##", CultureInfo.InvariantCulture)}");
            }
            if (PowerOutput?.Invoke(up) - PowerOutput?.Invoke(site.Record) is { } power and not 0) tip.Append(CultureInfo.InvariantCulture, $"\n{Loc.Tr("Power")} {power}");
            state.Add(new("", $"[{Loc.Tr("Upgrade").ToUpper(CultureInfo.CurrentCulture)}]", ValueColour: GoodColour, Tooltip: tip.ToString(), Action: "meitou:building-upgrade"));
        }
        if (!site.Complete && DowngradeOf?.Invoke(site.Record) is { } down && DataOf(down) is { } lower)
            state.Add(new("", $"[{Loc.Tr("Downgrade").ToUpper(CultureInfo.CurrentCulture)}]", ValueColour: MainColour, Tooltip: $"{Loc.Tr("Downgrade To:")} {lower.Name}",
                Action: "meitou:building-downgrade"));
    }

    /// <summary>A BUILDING record's <c>power output</c> (the upgrade tooltip's Power change); the host sets it.</summary>
    public Func<string, int?>? PowerOutput { get; set; }

    /// <summary>
    /// A turret's section 3 rows (<c>FUN_14030c2a0</c>, <b>Observed</b> by disassembly): <c>Height:</c> int((building y - last aim y) x 0.1) m, "The turrets height above the last target we aimed
    /// at."; <c>Height bonus:</c> "+" int((h - 1) x 100)% with the bonus text naming the height in the Title tag (the penalty text only for a negative h, which h never is). The original
    /// leaves out Height for some buildings (slots 0x268 / 0x270 / 0x228, not decoded) and reads the ground under the building before any aim; Meitou shows it once the turret has aimed.
    /// </summary>
    static void AddHeightLines(Meitou.Simulation.Combat.TurretPanelView turret, List<HudLine> state)
    {
        string metres = Loc.Tr("{1,num} m", turret.HeightMetres ?? 0);
        if (turret.HeightMetres is not null) state.Add(new("Height:", metres, Tooltip: Loc.Tr("The turrets height above the last target we aimed at.")));
        string tip = turret.Height >= 0
            ? Loc.Tr("The additional bonus to range, speed and power that resulted from having a height difference of {1} above the last target.", TitleTag + metres + InkTag)
            : Loc.Tr("The additional penalty to range, speed and power that resulted from having a height difference of {1} below the last target.", TitleTag + metres + InkTag);
        state.Add(new("Height bonus:", "+" + Loc.Tr("{1,num}%", (int)((turret.Height - 1) * 100)), ValueColour: turret.Height >= 0 ? GoodColour : BadColour, Tooltip: tip));
    }

    /// <summary>
    /// The gun's <c>Loaded</c> bar (<c>GunClass</c> slot 14 <c>FUN_14030ce20</c> section 4, <b>Observed</b>): "{loaded}/{shots}", filled (loaded + 1 - the reload still to go) / shots; its rows
    /// Base Reload Speed (the reload times, 2 decimals), User Skill and, with a gunner, User Reload Speed from the gunner's Crossbows level (stat 0x23, as the base class reads it).
    /// </summary>
    static MedicalRow LoadedRow(Meitou.Simulation.Combat.TurretPanelView g)
    {
        static string F2(float v) => v.ToString("F2", CultureInfo.InvariantCulture);
        float fill = g.Shots > 0 ? Math.Clamp((g.Loaded + 1 - g.ReloadLeft) / g.Shots, 0, 1) : 0;
        var tip = new List<string> { Loc.Tr("Base Reload Speed:") + " " + Loc.Tr("{1}-{2} seconds", F2(g.ReloadMin), F2(g.ReloadMax)),
            Loc.Tr("User Skill:") + " " + ((int)(g.Crossbows ?? 0)).ToString(CultureInfo.InvariantCulture) };
        if (g.Crossbows is { } level)
        {
            float time = Meitou.Simulation.Combat.RangedFormulas.ReloadTime(level * 0.01f, g.ReloadMin, g.ReloadMax);
            tip.Add(Loc.Tr("User Reload Speed:") + " " + Loc.Tr((int)time == 1 ? "{1} second" : "{1} seconds", F2(time)));
        }
        return new(Loc.Tr("Loaded"), 0, fill, 0, $"{g.Loaded}/{g.Shots}", "", false, string.Join("\n", tip));
    }

    /// <summary>The selected building's first door as the simulation publishes it, or null.</summary>
    internal Meitou.Simulation.Locks.DoorDetail? FirstDoor(SelectedBuilding focus) =>
        session.CurrentSnapshot.InspectedBuilding is { } detail && detail.Instance == focus.Instance && detail.Doors.Count > 0 ? detail.Doors[0] : null;

    /// <summary>
    /// The medical panel's rows of a selected building (its section 4, docs/game/ui-screens.md "Selected building"): the first door's <c>Door Condition</c>, then <c>Building Condition</c>
    /// (<c>Condition</c> without doors), each the construction progress as the number and its fraction of the target as the bar, then while unfinished a row per material, delivered
    /// over required. Meitou models no building damage, so a finished building's progress is its target (the original's damaged state lowers it).
    /// </summary>
    internal List<MedicalRow> BuildingRows(SelectedBuilding focus)
    {
        var rows = new List<MedicalRow>();
        // A rock has no site in the registry: its Condition is its record's target, complete (an Iron Resource's build materials 2).
        var door = FirstDoor(focus);
        if (door is { Record.Length: > 0 } && BuildTarget?.Invoke(door.Value.Record) is { } doorTarget)
            rows.Add(Bar(Loc.Tr("Door Condition"), 1, ((int)doorTarget).ToString(CultureInfo.InvariantCulture), "The door is in good condition."));
        var site = session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == focus.Instance);
        float? target = site?.Target ?? (focus.Record is { } record ? BuildTarget?.Invoke(record) : null);
        if (target is not { } total) return rows;
        float progress = (site?.Destroyed ?? focus.Destroyed) ? 0 : site?.Progress ?? total;
        bool complete = site?.Complete ?? true;
        float fraction = total > 0 ? Math.Min(progress / total, 1) : 1;
        string tip = !complete ? Loc.Tr("The building is under construction and at {1,num}% completion.", (int)(fraction * 100))
            : fraction >= 1 ? Loc.Tr("The building is in good condition.") : Loc.Tr("The building is damaged.");
        rows.Add(Bar(Loc.Tr(door is null ? "Condition" : "Building Condition"), fraction, ((int)progress).ToString(CultureInfo.InvariantCulture), tip));
        // A wind generator's rows (WindGeneratorBuilding::getGUIPower section 4): Wind, its share as a whole percent and the bar; Power Output, the output as an int over the rated.
        if (site is { Complete: true } && Machine(site.Id) is { Wind: { Rated: > 0 } wind, Power: { } power })
        {
            string windTip = Loc.Tr("The current amount of wind.");
            if (wind.Range is { } range)
                windTip += "\n" + Loc.Tr("Operating wind speed is between {1,num=fix,p=2} mph and {2,num=fix,p=2} mph.", range.Min * MphPerUnit, range.Max * MphPerUnit);
            rows.Add(new(Loc.Tr("Wind"), 0, wind.Share, 0, ((int)(wind.Share * 100)).ToString(CultureInfo.InvariantCulture) + "%", "", false, windTip));
            float output = power.Demand;
            string outputTip = !wind.PowerOn ? "Power is switched off."
                : output <= wind.Rated - 1 ? wind.Share > 0 ? "Power output is running at reduced efficiency due to damage." : "Currently not enough wind to generate any power"
                : "Power output is at maximum.";
            rows.Add(Bar(Loc.Tr("Power Output"), output / wind.Rated, ((int)output).ToString(CultureInfo.InvariantCulture), outputTip));
        }
        if (site is { Complete: true } && session.CurrentSnapshot.InspectedTurret is { } gun && gun.Instance == focus.Instance) rows.Add(LoadedRow(gun));
        if (site is { Complete: false })
            foreach (var m in site.Materials)
            {
                float share = m.Required > 0 ? Math.Min(m.Delivered / m.Required, 1) : 1;
                string need = m.Delivered >= m.Required ? Loc.Tr("All necessary materials are ready.")
                    : Loc.Tr("{1} more 'Building Materials' will be needed to complete this construction.", (m.Required - m.Delivered).ToString("0.##", CultureInfo.InvariantCulture));
                rows.Add(Bar(buildHost?.ItemName(m.Item) ?? m.Item, share, $"{(int)m.Delivered}/{(int)m.Required}", need));
            }
        return rows;

        // The rows a DataPanel adds with a fraction (FUN_140729f90): the bar in the healed (yellow) layer, as a base-game screenshot of a town house shows it.
        static MedicalRow Bar(string name, float fraction, string value, string tooltip) => new(name, 0, fraction, 0, value, "", false, Loc.Tr(tooltip));
    }

    /// <summary>
    /// Whether a building of the player's is open to the public: a site's flag; a town building's as the player set it, else as its state reads (<c>is public day</c>, Building+0xc8).
    /// </summary>
    static bool PublicOf(SelectedBuilding focus, Meitou.Simulation.Construction.BuildingSnapshot? site, Meitou.Simulation.Locks.BuildingDetail? detail) =>
        site?.Public ?? detail?.Public ?? focus.PublicDay;

    /// <summary>The panel's own buttons on a selected building of the player's: Dismantle (after the original's "Are you sure?"), pause, upgrade, downgrade, the doors, their locks and Open to public.</summary>
    internal void BuildingAction(string action)
    {
        if (BuildingFocus() is not { } focus) return;
        var detail = session.CurrentSnapshot.InspectedBuilding is { } d && d.Instance == focus.Instance ? d : null;
        if (action == "building-buy" && detail?.SalePrice is { } price)
        {
            string instance = focus.Instance;
            Sound("General_Click");
            Confirm(focus.Name, Loc.Tr("Do you want to buy this building for c.{1,num}?", price),
                () => Say(new Meitou.Simulation.Locks.BuyBuilding(instance, price) { Tick = session.World.Tick }), yes: "Yes", cancel: "No");
            return;
        }
        if (focus.SiteId is null && detail is { Owned: true })
        {
            // A town building the player owns: its doors and the public toggle.
            if (action == "building-doors") Say(new Meitou.Simulation.Locks.ToggleDoorsOrder(focus.Instance) { Tick = session.World.Tick });
            else if (action == "building-locks") Say(new Meitou.Simulation.Locks.ToggleDoorLocksOrder(focus.Instance) { Tick = session.World.Tick });
            else if (action == "building-public")
            {
                Sound("General_Click");
                Say(new Meitou.Simulation.Construction.SetBuildingPublic(focus.Instance, !PublicOf(focus, null, detail)) { Tick = session.World.Tick });
            }
            return;
        }
        if (focus.SiteId is not { } id || Meitou.Simulation.Production.ProductionSystem.IsNatural(id)) return;
        var site = session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == id);
        if (action == "building-upgrade") Say(new Meitou.Simulation.Construction.UpgradeSite(id) { Tick = session.World.Tick });
        else if (action == "building-downgrade") Say(new Meitou.Simulation.Construction.DowngradeSite(id) { Tick = session.World.Tick });
        else if (action == "building-public" && site is not null)
        {
            Sound("General_Click");
            Say(new Meitou.Simulation.Construction.SetBuildingPublic(id, !site.Public) { Tick = session.World.Tick });
        }
        if (action == "building-dismantle" && site?.LevelPlacement is null)
        {
            // Slot 84 (FUN_14054db30): with furniture in it the furniture warning, else "Are you sure?"; Yes dismantles it at once (Building::confirmDismantle).
            bool furnished = session.CurrentSnapshot.Buildings.Any(b => b.Parent == id);
            Confirm("Dismantle", furnished ? "Dismantling this building will destroy all furniture.\nDo you want to continue?" : "Are you sure?",
                () => Say(new Meitou.Simulation.Construction.ConfirmDismantle(id) { Tick = session.World.Tick }));
        }
        else if (action is "machine-power" or "machine-battery" && Machine(id) is { Power: { } pw } machine)
        {
            // A click flips the toggle, sets the Generator switch and posts UI_Generator (vfunc 161, FUN_140297a60).
            bool on = action == "machine-power" ? !(machine.Wind?.PowerOn ?? pw.PowerOn) : !pw.GetsBattery;
            Sound("UI_Generator", "Generator", on ? "On" : "Off");
            Say(action == "machine-power" ? new Meitou.Simulation.Production.SetMachinePower(id, PowerOn: on) { Tick = session.World.Tick } : new Meitou.Simulation.Production.SetMachinePower(id, GetsBattery: on) { Tick = session.World.Tick });
        }
        else if (action == "machine-incinerate" && Machine(id) is { Furnace: not null })
        {
            Sound("General_Click");   // engine choice: the button's own sound is not traced
            Say(new Meitou.Simulation.Production.IncinerateFurnace(id) { Tick = session.World.Tick });
        }
        else if (action == "building-pause" && site is { Complete: false }) Say(new Meitou.Simulation.Construction.PauseSite(id, !site.Paused) { Tick = session.World.Tick });
        else if (action == "building-doors") Say(new Meitou.Simulation.Locks.ToggleDoorsOrder(id) { Tick = session.World.Tick });
        else if (action == "building-locks") Say(new Meitou.Simulation.Locks.ToggleDoorLocksOrder(id) { Tick = session.World.Tick });
    }
}
