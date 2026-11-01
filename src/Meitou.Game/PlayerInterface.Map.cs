using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Items;
using Meitou.Simulation.Production;
using Meitou.Simulation.Trading;
using Meitou.Ui;

namespace Meitou.Game;

/// <summary>How a town's faction stands to the player, for its map colour.</summary>
public enum MapStanding { Neutral, Ally, Enemy, Player }

/// <summary>A town's faction as the map shows it: its name, the player's relation to it, the standing and whether it is <c>not real</c> (wildlife: a nest, not a camp).</summary>
public readonly record struct MapFaction(string Name, float Relation, MapStanding Standing, bool Wildlife);

/// <summary>The world map (Overview's MAP tab), after the original's MapScreen (docs/game/ui-screens.md "Map").</summary>
sealed partial class PlayerInterface
{
    readonly UiMapView mapView = new() { FitWhole = true };
    UiRect mapViewport;
    Vector2? mapDrag;
    bool mapRightPressed, mapShown;
    Vector3 movementMarker;
    long movementMarkerUntil;
    readonly Dictionary<string, Vector2> markerSizes = new(StringComparer.Ordinal);

    /// <summary>The placed towns (the population's sites, in the order of <see cref="WorldSnapshot.Towns"/>); the host sets it.</summary>
    public Func<IReadOnlyList<TownSite>>? MapTowns { get; set; }
    /// <summary>A faction (by string id) as the map shows it, null when unknown; the host sets it.</summary>
    public Func<string?, MapFaction?>? MapFactionOf { get; set; }
    /// <summary>A faction's string id by its index (an outpost's owner), null when unknown; the host sets it.</summary>
    public Func<int, string?>? MapFactionId { get; set; }
    /// <summary>The names of the towns a map item (by record id) would reveal now, null for an item that is not a map; the host sets it.</summary>
    public Func<string, IReadOnlyList<string>?>? MapItemTowns { get; set; }

    const float MapExtent = 147456, MapCanvas = 4096, MovementMarkerSeconds = 3, NestShowRange = 3258;
    // MarkerAlly, MarkerNeutral, MarkerEnemy, MarkerPlayer and MarkerMovement of data/gui/colours/kenshi_colours.xml; PlayerSelected is the game's built-in one (not in the file).
    static readonly Vector4 AllyColour = Rgb(0x5cb473), NeutralColour = Rgb(0xdfdfdf), EnemyColour = Rgb(0xe65139), PlayerColour = Rgb(0x8dcdff),
        MovementColour = Rgb(0x79b44b), SelectedColour = new(0.652f, 0.903f, 1, 1);
    static Vector4 Rgb(int rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1);

    // The map image spans the world's 294912 units with world (0, 0) at its centre, +X right and +Z down (Verified: GUI_Map.dds against the placed towns).
    static Vector2 MapPoint(Vector3 world) => new((world.X + MapExtent) / (2 * MapExtent), (world.Z + MapExtent) / (2 * MapExtent));
    static Vector3 MapWorld(Vector2 normalized) => new(normalized.X * 2 * MapExtent - MapExtent, 0, normalized.Y * 2 * MapExtent - MapExtent);

    float UiScale => Height / 1080f;
    bool MapTabOpen => UiVisible && faithfulScreens.IsOpen("overview") && faithfulScreens.GetTab("overview", "TabsMain") == 0;
    bool OverMap(Vector2 p) => MapTabOpen && mapViewport.Contains(p.X, p.Y) && !faithfulScreens.HasModal;
    /// <summary>The original's map scale: on-screen pixels per pixel of the 4096 canvas. The least fits the whole map, the most is 1.5 x the UI scale.</summary>
    float MapScale => mapView.Size(mapViewport) / MapCanvas;
    float MapMinScale => Math.Min(mapViewport.Width, mapViewport.Height) / MapCanvas;
    /// <summary>0 below 0.4 x the UI scale (no characters or labels, small towns), 1 to 0.75 x, else 2.</summary>
    int MapZoomLevel => MapScale < 0.4f * UiScale ? 0 : MapScale <= 0.75f * UiScale ? 1 : 2;

    /// <summary>Left or middle button: pans.</summary>
    bool MapPointerDown(Vector2 p)
    {
        if (!OverMap(p)) return false;
        mapDrag = p; return true;
    }
    bool MapPointerMove(Vector2 p)
    {
        if (mapDrag is not { } previous) return false;
        mapView.Pan(mapViewport, p - previous); mapDrag = p; return true;
    }
    /// <summary>The right button does nothing until it comes up (<see cref="MapRightUp"/>).</summary>
    bool MapRightDown(Vector2 p)
    {
        if (!OverMap(p)) return false;
        mapRightPressed = true; return true;
    }
    /// <summary>
    /// A move order for the selection to the world point under the cursor (clamped to the map), its height the ground's but at least 100. Shift does not queue, as in the
    /// original; the movement marker shows for 3 s.
    /// </summary>
    bool MapRightUp(Vector2 p)
    {
        if (!mapRightPressed) return false;
        mapRightPressed = false;
        if (!OverMap(p)) return true;
        if (SelectedIds().Count > 0)
        {
            var at = MapWorld(Vector2.Clamp(mapView.At(mapViewport, p), Vector2.Zero, Vector2.One));
            at.Y = MathF.Max(ground(at.X, at.Z), 100);
            Move(at, queued: false);
            movementMarker = at;
            movementMarkerUntil = Stopwatch.GetTimestamp() + (long)(MovementMarkerSeconds * Stopwatch.Frequency);
        }
        return true;
    }
    /// <summary>The wheel adds 0.075 to the scale per notch, about the cursor.</summary>
    bool MapWheel(Vector2 p, float steps)
    {
        if (!OverMap(p)) return false;
        MapScaleAt(p, MapScale + 0.075f * steps); return true;
    }
    void MapScaleAt(Vector2 p, float scale)
    {
        if (MapScale <= 0) return;
        mapView.ZoomAt(mapViewport, p, Math.Max(scale, MapMinScale) / MapScale);
    }
    void MapAction(string widget)
    {
        if (widget == "MapCenterButton") mapView.CentreOn(MapPoint(session.Camera.Current.Target));
        else if (widget is "MapZoomInButton" or "MapZoomOutButton")
            MapScaleAt(new(mapViewport.X + mapViewport.Width / 2, mapViewport.Y + mapViewport.Height / 2), MapScale + (widget == "MapZoomInButton" ? MapMinScale : -MapMinScale));
    }

    /// <summary>Once a frame: opening the map centres it on the camera and hides the movement marker; closing the overview on it marks the new discoveries seen.</summary>
    void UpdateMap()
    {
        bool shown = MapTabOpen;
        if (shown && !mapShown)
        {
            mapView.CentreOn(MapPoint(session.Camera.Current.Target));
            movementMarkerUntil = 0;
        }
        if (!shown && mapShown && !faithfulScreens.IsOpen("overview") && session.CurrentSnapshot.Towns.Any(t => (t & TownKnowledge.Recent) != 0))
            Say(new SeenDiscoveriesCommand { Tick = session.World.Tick });
        if (!shown) { mapDrag = null; mapRightPressed = false; }
        mapShown = shown;
    }

    void AddMapContent(string id, List<UiControl> controls)
    {
        if (id != "overview") return;
        int imageIndex = controls.FindIndex(c => c.Name == "MapImage");
        if (imageIndex < 0) { mapViewport = default; return; }
        var image = controls[imageIndex]; mapViewport = image.Bounds.Intersect(image.Clip ?? image.Bounds);
        if (mapViewport.Width <= 0 || mapViewport.Height <= 0) return;
        float ui = UiScale;
        mapView.MaxZoom = 1.5f * ui / MapMinScale;
        var bounds = mapView.ImageBounds(mapViewport);
        controls[imageIndex] = image with { Bounds = bounds, Clip = mapViewport, Pickable = true };
        int level = MapZoomLevel;
        Vector2 Screen(Vector3 world) { var n = MapPoint(world); return new(bounds.X + n.X * bounds.Width, bounds.Y + n.Y * bounds.Height); }
        Vector2 GroupSize(string group)
        {
            if (markerSizes.TryGetValue(group, out var known)) return known;
            var props = new Dictionary<string, string> { ["ImageResource"] = "Kenshi_MapMarkers", ["ImageGroup"] = group, ["ImageName"] = group };
            return markerSizes[group] = MyGuiImages.Resolve(uiResources, props) is { } i ? new(i.Source.Width, i.Source.Height) : new(32);
        }
        void Marker(string name, string group, UiRect box, Vector4 colour, float angle = 0, string? tooltip = null)
        {
            if (box.Intersect(mapViewport).Width <= 0 || box.Intersect(mapViewport).Height <= 0) return;
            var props = new Dictionary<string, string> { ["ImageResource"] = "Kenshi_MapMarkers", ["ImageGroup"] = group, ["ImageName"] = group,
                ["Colour"] = FormattableString.Invariant($"{colour.X} {colour.Y} {colour.Z} {colour.W}") };
            if (angle != 0) props["ImageAngle"] = angle.ToString(CultureInfo.InvariantCulture);
            // Type 4, a map place (the original's "hand" payload, FUN_140922e60): rows of the data, a tab between a row's label and value.
            if (tooltip is not null) { props[UiTooltips.Type] = "4"; props[UiTooltips.Data] = tooltip; }
            controls.Insert(++imageIndex, new(name, "ImageBox", box, "", "", "", true, props, default, Pickable: tooltip is not null, Clip: mapViewport));
        }
        void Label(string name, string text, Vector2 topCentre, Vector4 colour)
        {
            float width = 400 * ui, height = 24 * ui;
            var props = new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["TextAlign"] = "HCenter Top" };
            // MyGUI's text shadow: the text again in black, a pixel down and right, under it.
            controls.Insert(++imageIndex, new(name + "-shadow", "TextBox", new(topCentre.X - width / 2 + 1, topCentre.Y + 1, width, height), text, "", "", true, props, default,
                new Vector4(0, 0, 0, colour.W), Pickable: false, TextScale: ui, Clip: mapViewport));
            controls.Insert(++imageIndex, new(name, "TextBox", new(topCentre.X - width / 2, topCentre.Y, width, height), text, "", "", true, props, default,
                colour, Pickable: false, TextScale: ui, Clip: mapViewport));
        }

        // Towns, camps and nests: discovered ones only; which zoom levels show each kind, its size, label and tooltip (docs/game/ui-screens.md "Map").
        var snapshot = session.CurrentSnapshot;
        var players = snapshot.Characters.Where(c => c.IsPlayer).ToList();
        bool PlayerNear(Vector3 at) => players.Any(c => Vector2.DistanceSquared(new(c.Position.X, c.Position.Z), new(at.X, at.Z)) <= NestShowRange * NestShowRange);
        void Place(string name, TownType type, string townName, string unexploredName, string? factionId, Vector3 position, TownKnowledge known)
        {
            bool nest = type == TownType.Nest, recent = (known & TownKnowledge.Recent) != 0, explored = (known & TownKnowledge.Explored) != 0;
            if (nest && !PlayerNear(position)) return;
            int least = nest ? 2 : recent || type == TownType.Town ? 0 : type == TownType.PointOfInterest ? 2 : 1;
            if (level < least) return;
            var faction = MapFactionOf?.Invoke(factionId);
            string group = type switch
            {
                TownType.Nest => faction?.Wildlife == true ? "Nest" : "Camp", TownType.Outpost or TownType.Military or TownType.Prison => "Outpost", TownType.Town => "Town",
                TownType.Village => "Village", TownType.Ruins => "Ruin", TownType.SlaveCamp => "Workcamp", _ => "SmallPlace",
            };
            float factor = level == 0 ? 0.5f : type == TownType.Town ? (level == 1 ? 0.75f : 1) : 0.85f;
            var size = GroupSize(group) * ui * factor;
            var centre = Screen(position);
            var box = new UiRect(centre.X - size.X / 2, centre.Y - size.Y / 2, size.X, size.Y);
            var colour = !explored ? NeutralColour : (faction?.Standing ?? MapStanding.Neutral) switch
            {
                MapStanding.Player => PlayerColour, MapStanding.Ally => AllyColour, MapStanding.Enemy => EnemyColour, _ => NeutralColour,
            };
            string shownName = explored || unexploredName.Length == 0 ? townName : unexploredName;
            // An explored place's tooltip: its name, its faction and the player's relation to it.
            string? tooltip = explored ? $"{shownName}\nFaction:\t{faction?.Name ?? "-"}\nRelations:\t{(faction is { } f ? MathF.Round(f.Relation).ToString(CultureInfo.InvariantCulture) : "-")}" : null;
            Marker(name, group, box, colour, tooltip: tooltip);
            if (recent)
            {
                var mark = GroupSize("NewDiscovered") * ui * 0.5f;
                Marker(name + "-new", "NewDiscovered", new(box.X + box.Width - mark.X / 2, box.Y - mark.Y / 2, mark.X, mark.Y), Vector4.One);
            }
            if (level > 0 && !nest) Label(name + "-label", shownName, new(centre.X, box.Y + box.Height + MathF.Round(3.2f * ui)), colour);
        }
        if (MapTowns?.Invoke() is { } towns)
            for (int i = 0; i < towns.Count && i < snapshot.Towns.Count; i++)
                if ((snapshot.Towns[i] & TownKnowledge.Discovered) != 0 && towns[i].Town.Type is not (TownType.NestMarker or TownType.Null))
                    Place("map-town-" + i, towns[i].Town.Type, towns[i].Town.Name, towns[i].Town.UnexploredName, towns[i].Town.Faction, towns[i].Position, snapshot.Towns[i]);
        foreach (var camp in snapshot.Camps)
            if ((camp.Known & TownKnowledge.Discovered) != 0 && camp.Position is { } at)
                Place("map-camp-" + camp.Site, TownType.Nest, camp.Name, "", camp.FactionId, at, camp.Known);
        // The player's outposts: TOWN player_town_template (type 1), in the same town list, made discovered and explored (FUN_140934e10).
        foreach (var outpost in snapshot.Outposts)
            Place("map-outpost-" + outpost.Number, TownType.Outpost, outpost.Name, "", MapFactionId?.Invoke(outpost.Owner), outpost.Position, TownKnowledge.Discovered | TownKnowledge.Explored);

        // The player's characters, hidden at the least zoom level.
        if (level > 0)
        {
            var size = GroupSize("Character") * ui;
            foreach (var character in players)
            {
                var at = Screen(where(character));
                Marker("map-character-" + character.Id, "Character", new(at.X - size.X / 2, at.Y - size.Y / 2, size.X, size.Y), character.Selected ? SelectedColour : PlayerColour);
            }
        }

        // The movement marker stands on the ordered point (its bottom centre) for 3 s.
        if (Stopwatch.GetTimestamp() < movementMarkerUntil)
        {
            var size = GroupSize("Movement") * ui;
            var at = Screen(movementMarker);
            Marker("map-movement", "Movement", new(at.X - size.X / 2, at.Y - size.Y, size.X, size.Y), MovementColour);
        }

        // The camera, turned to its view: the angle between the level view direction and -Z, negative towards -X.
        {
            var camera = session.Camera.Current;
            var look = new Vector2(camera.Forward.X, camera.Forward.Z);
            float angle = look.LengthSquared() > 1e-8f ? MathF.Acos(Math.Clamp(-Vector2.Normalize(look).Y, -1, 1)) * (look.X < 0 ? -1 : 1) : 0;
            var size = GroupSize("Camera") * ui;
            var at = Screen(camera.Target);
            Marker("map-camera", "Camera", new(at.X - size.X / 2, at.Y - size.Y / 2, size.X, size.Y), PlayerColour, angle);
        }
    }
}

sealed partial class PlayerInterface
{
    static string? JoinCaptions(string? a, string? b) => a is null ? b : b is null ? a : a + "\n" + b;

    /// <summary>A map item's tooltip lines: what a right click would add to the map, or that it is all known (docs/game/ui-screens.md "Map").</summary>
    string? MapItemCaption(string record)
    {
        if (MapItemTowns?.Invoke(record) is not { } towns) return null;
        if (towns.Count == 0) return "[Location already known]";
        return "[Right-click to learn new map locations]\nTowns:\n" + string.Join("\n", towns.GroupBy(t => t).Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key));
    }

    /// <summary>
    /// A right click on an item of the shown inventory (the original's release handler <c>FUN_140714a20</c>, <b>Observed</b> by decompile): while a trade is open it moves one unit to the other
    /// side, buying a stock item or selling the customer's own; with Shift all the units (the original repeats the move until one fails, here the sim's affordable and fits clamp ends it).
    /// With another window open it moves to it (<see cref="WindowRightClick"/>). With only the character's own windows open a map item is read (<see cref="ReadMapItem"/>); anything else is
    /// equipped, taken off, fitted or moved between the grid and the open bag, money turned into cats (<see cref="QuickMoveItem"/>); with another character's window open it moves there.
    /// </summary>
    bool InventoryRightClick(Vector2 p, bool shift)
    {
        if (!UiVisible) return false;
        if (confirmation is not null) return true;   // an open question (the fencing prompt) swallows the click, as the original's pending state does
        if (faithfulScreens.HitTest(p) is not { } picked || !picked.Control.Enabled) return false;
        var trade = session.CurrentSnapshot.Trade;
        if (trade is not null && picked.Control.Properties.TryGetValue("TradeItem", out var stockIndex))
        {
            if (trade.Stock.FirstOrDefault(i => i.Index == int.Parse(stockIndex, CultureInfo.InvariantCulture)) is not { } stock) return false;
            Say(new BuyItem(stock.Token, shift ? stock.Quantity : 1) { Tick = session.World.Tick, Equip = true });
            return true;
        }
        if (trade is null && WindowRightClick(picked.Screen, picked.Control, shift)) return true;
        if (IsLootScreen(picked.Screen) || !picked.Control.Properties.TryGetValue("InventoryItem", out var index) || WindowCharacter(picked.Screen) is not { ItemGrid: { } grid } lead) return false;
        var item = grid.Items.FirstOrDefault(i => i.Index == int.Parse(index, CultureInfo.InvariantCulture));
        if (item is null) return false;
        var token = new InventoryItemToken(item.Index, item.Record, item.Section, item.X, item.Y, item.Quantity, item.Quality);
        if (trade is not null && trade.Customer == lead.Id && InventoryCommands.IsMovableSection(item.Section))
        {
            Sell(trade, token, shift ? item.Quantity : 1, null, 0);
            return true;
        }
        if (trade is null && OtherCharacterWindow(picked.Screen, lead.Id) is { } other)
        {
            if (!InventoryCommands.IsMovableSection(item.Section)) return false;
            bool otherBag = other == InventoryOwner()?.Id && faithfulScreens.IsOpen("bag");
            Say(new QuickMoveItem(lead.Id, token, shift, false) { Tick = session.World.Tick, Into = other, IntoBag = otherBag });
            return true;
        }
        if (MapItemTowns?.Invoke(item.Record) is not null) Say(new ReadMapItem(lead.Id, token) { Tick = session.World.Tick });
        else Say(new QuickMoveItem(lead.Id, token, shift, faithfulScreens.IsOpen("bag") && !IsPinnedInventory(picked.Screen)) { Tick = session.World.Tick });
        return true;
    }

    /// <summary>
    /// The other player character whose window a right click in <paramref name="screen"/> moves to (the inventory or a pinned one, docs/game/ui-screens.md section 6): of several, the one on
    /// top (engine choice); null when only <paramref name="own"/>'s windows are open.
    /// </summary>
    CharacterId? OtherCharacterWindow(string screen, CharacterId own)
    {
        if (screen is not ("inventory" or "bag") && !IsPinnedInventory(screen)) return null;
        var open = faithfulScreens.OpenScreens;
        for (int i = open.Count - 1; i >= 0; i--)
            if ((open[i] == "inventory" || IsPinnedInventory(open[i])) && WindowCharacter(open[i]) is { } other && other.Id != own) return other.Id;
        return null;
    }

    /// <summary>
    /// A right click on an item while the character's inventory and another item window are open (the original's release handler <c>FUN_140714a20</c>, move <c>FUN_140713d20</c>):
    /// one unit goes to the other window, with Shift the whole stack (the original repeats the one-unit move until it fails); into a character, its open bag window first, then its slots, then
    /// its grid (<see cref="InventoryCommands.MoveInto"/>). Between the lead's inventory or bag and the shown machine, storage or container window, or the open loot (true: taken).
    /// </summary>
    bool WindowRightClick(string screen, UiControl control, bool shift)
    {
        int count = shift ? 0 : 1;
        if (!faithfulScreens.IsOpen("inventory") || InventoryOwner() is not { ItemGrid: { } grid } lead) return false;
        var machine = Machine(shownMachine) is { } shown && faithfulScreens.IsOpen(MachineScreen(shown)) ? shown : null;
        bool loot = faithfulScreens.IsOpen("loot") && lead.Id == session.CurrentSnapshot.LootOpener;
        if (machine is not null && control.Properties.TryGetValue("MachineItem", out var raw))
        {
            int code = int.Parse(raw, CultureInfo.InvariantCulture);
            Say(new TakeFromMachine(lead.Id, machine.Site, code >= 1000 ? -1 : code - 10, Math.Max(0, code - 1000), Count: count) { Tick = session.World.Tick, BagFirst = faithfulScreens.IsOpen("bag") });
            return true;
        }
        if (!control.Properties.TryGetValue("InventoryItem", out var index)) return false;
        int at = int.Parse(index, CultureInfo.InvariantCulture);
        if (IsLootScreen(screen))
        {
            if (!loot || LootBody()?.ItemGrid?.Items.FirstOrDefault(i => i.Index == at) is not { } looted) return false;
            Say(new TakeLootItem(Token(looted)) { Tick = session.World.Tick, Count = count, BagFirst = faithfulScreens.IsOpen("bag") });
            return true;
        }
        if (screen is not ("inventory" or "bag") || grid.Items.FirstOrDefault(i => i.Index == at) is not { } item || !InventoryCommands.IsMovableSection(item.Section)) return false;
        if (loot)
        {
            Say(new PutLootItem(lead.Id, Token(item)) { Tick = session.World.Tick, Count = count, BagFirst = faithfulScreens.IsOpen("loot-bag") });
            return true;
        }
        if (machine is null) return false;
        bool inputs = machine.Inputs.Count > 0 || machine.Storage is null;
        int slot = inputs ? machine.Inputs.ToList().FindIndex(i => i.Item == item.Record) : -1;
        if (inputs && slot < 0) return true;   // no input takes it
        Say(new PutInMachine(lead.Id, Token(item), machine.Site, slot, -1, 0, count) { Tick = session.World.Tick });
        return true;

        static InventoryItemToken Token(InventoryItemView i) => new(i.Index, i.Record, i.Section, i.X, i.Y, i.Quantity, i.Quality);
    }
}
