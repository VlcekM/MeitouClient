using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Ui;

namespace Meitou.Game;

/// <summary>
/// Loose items under the pointer (docs/simulation.md "Ground items"): a right click on one sends the nearest selected character to pick it up (pointer <c>pick_item</c>).
/// While the Highlight key is held each one near the focus shows its name label (docs/game/ui-input.md "Ground item labels"), which picks like the item.
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>What the ray meets of a ground item where it is drawn: its triangles in its own space, its place and turn (the host sets it; null: no item is picked but by its label).</summary>
    public Func<Meitou.Simulation.Items.GroundItemSnapshot, (IReadOnlyList<(Vector3[] Vertices, int[] Indices)> Mesh, Vector3 At, Quaternion Rotation)?>? GroundItemShape { get; set; }

    /// <summary>Whether a ground item is not drawn, being above the shown floor of its building (the host sets it; null: none): it has no label and is not picked.</summary>
    public Func<int, bool>? GroundItemHidden { get; set; }

    /// <summary>The camera's focus while the Highlight key is held (the host sets it; null otherwise): the item labels show round it.</summary>
    public Vector3? HighlightFocus;

    /// <summary>The labels drawn last frame, front to back, for <see cref="PickGroundItem"/>.</summary>
    readonly List<(int Id, UiRect Rect)> itemLabels = [];

    /// <summary>
    /// The ground item whose label is under the pixel, else the nearest the ray meets on its collision (<see cref="GroundItemShape"/>), as the original's pick hits an item's
    /// shapes; one inside or behind a building seen from outside does not count.
    /// </summary>
    int? PickGroundItem(Vector2 pixel) => PickGroundItemAt(pixel)?.Id;

    /// <summary>The ground item whose name label (shown while the Highlight key is held) is under the pixel, else null.</summary>
    int? LabelAt(Vector2 pixel)
    {
        if (HighlightFocus is null) return null;
        foreach (var (id, rect) in itemLabels)
            if (rect.Contains(pixel.X, pixel.Y)) return id;
        return null;
    }

    /// <summary><see cref="PickGroundItem"/> with the ray distance (0 for a label: it is drawn over everything).</summary>
    (int Id, float T)? PickGroundItemAt(Vector2 pixel)
    {
        if (LabelAt(pixel) is { } labelled) return (labelled, 0);
        if (Ray(pixel) is not { } ray || GroundItemShape is not { } shapeOf) return null;
        (int, float)? best = null;
        float bestT = float.MaxValue;
        foreach (var g in session.CurrentSnapshot.GroundItems)
            if (GroundItemHidden?.Invoke(g.Id) != true && shapeOf(g) is { } shape && RayNears(ray, shape.Mesh, shape.At, shape.Rotation) && RayMesh(ray, shape.Mesh, shape.At, shape.Rotation) is { } t && t < bestT && !Hidden(ray, t))
                (best, bestT) = ((g.Id, t), t);
        return best;
    }

    /// <summary>Each ground item shape's bounding sphere in its own space, by the shape (the host keeps one per record): the cheap test before its triangles.</summary>
    readonly Dictionary<IReadOnlyList<(Vector3[] Vertices, int[] Indices)>, (Vector3 Centre, float Radius)> shapeSpheres = new(ReferenceEqualityComparer.Instance);

    /// <summary>Whether the ray passes through the bounding sphere of the shape put at <paramref name="at"/> turned by <paramref name="rotation"/> (a hit on its triangles is inside it).</summary>
    bool RayNears((Vector3 Origin, Vector3 Direction) ray, IReadOnlyList<(Vector3[] Vertices, int[] Indices)> mesh, Vector3 at, Quaternion rotation)
    {
        if (!shapeSpheres.TryGetValue(mesh, out var sphere))
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var (vertices, _) in mesh) foreach (var v in vertices) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
            float reach = 0;
            foreach (var (vertices, _) in mesh) foreach (var v in vertices) reach = MathF.Max(reach, Vector3.Distance(v, (min + max) / 2));
            shapeSpheres[mesh] = sphere = ((min + max) / 2, reach * 1.001f + 1e-3f);   // widened a little for rounding
        }
        var q = rotation.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(rotation);
        var toCentre = at + Vector3.Transform(sphere.Centre, q) - ray.Origin;
        float along = MathF.Max(Vector3.Dot(toCentre, ray.Direction) / ray.Direction.LengthSquared(), 0);   // the ray's nearest point to the centre, not behind its origin
        return (toCentre - ray.Direction * along).LengthSquared() <= sphere.Radius * sphere.Radius;
    }

    /// <summary>
    /// A right click on a ground item, with a selection (true): the pick-up order at once. A plain item has no inventory, so the original adds no verb and a held press shows no menu
    /// (<c>FUN_1407a6bc0</c> adds Pick Up / Steal and Loot only for an item with an inventory; docs/game/ui-input.md "Ground item labels").
    /// </summary>
    bool GroundItemClick(Vector2 p)
    {
        if (SelectedIds().Count == 0 || PickGroundItem(p) is not { } item) return false;
        Say(new Meitou.Simulation.Items.PickUpItemOrder([], item) { Tick = session.World.Tick, Alt = AltHeld });
        return true;
    }

    /// <summary>The ground item whose label took the left press, until the release.</summary>
    int? labelPress;

    /// <summary>
    /// A left click released on an item's name label (<c>FUN_1401638f0</c>, the label's eventMouseButtonReleased handler, button Left only): the pick-up order for the selection, as the right
    /// click on the item. The original shows the "Hold down the ALT key to steal items" hint instead of stealing when Alt is not held; the label exists only while Highlight is held, so that cannot arise here.
    /// </summary>
    void LabelPickUp(int item)
    {
        if (SelectedIds().Count == 0) return;
        Say(new Meitou.Simulation.Items.PickUpItemOrder([], item) { Tick = session.World.Tick });
    }

    /// <summary>
    /// The name label over each ground item within <see cref="HighlightRadius"/> of the focus while the Highlight key is held: the original's <c>Kenshi_ItemDropLabel</c>
    /// (<b>Verified</b>), a <c>Kenshi_GenericTextBoxFlatSmallSkin</c> 0.0259 of the screen high and fitted to the name, centred in Kenshi_StandardFont_Medium; plate and text
    /// in the item's <see cref="ItemLabelColour"/>, both green (<c>...Selected</c>, #66ff66) for the one the pointer picks, the plate red for a stolen one. Labels that would overlap the nearer
    /// one's are lifted above it, and hidden ones are skipped (engine choices).
    /// </summary>
    void AddItemLabels(List<UiControl> controls)
    {
        itemLabels.Clear();
        if (HighlightFocus is not { } focus || uiPainter is not { } painter || EditorOpen) return;
        float scale = Height / 1080f, height = Height * 0.0259259f, em = 20 * 50f / 72f * scale;
        var near = new List<(Meitou.Simulation.Items.GroundItemSnapshot Item, float Distance, Vector2 At)>();
        foreach (var g in session.CurrentSnapshot.GroundItems)
        {
            if (g.Name.Length == 0 || GroundItemHidden?.Invoke(g.Id) == true || Vector3.DistanceSquared(focus, g.Position) > HighlightRadius * HighlightRadius) continue;
            var middle = g.Position + new Vector3(0, 0.5f, 0);
            if (Project(middle) is not { } at || Ray(at) is not { } ray) continue;
            float distance = Vector3.Distance(ray.Origin, middle);
            if (!Hidden(ray, distance)) near.Add((g, distance, at));
        }
        near.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var placed = new List<(int Id, UiRect Rect)>();
        foreach (var (g, _, at) in near)
        {
            float width = painter.MeasureStandard(g.Name, em) + em;
            var rect = new UiRect(at.X - width / 2, at.Y - 6 * scale - height, width, height);
            for (bool moved = true; moved;)
            {
                moved = false;
                foreach (var (_, other) in placed)
                    if (rect.Intersect(other) is { Width: > 0, Height: > 0 }) { rect = rect with { Y = other.Y - height - 1 }; moved = true; }
            }
            placed.Add((g.Id, rect));
        }
        itemLabels.AddRange(placed);
        int? hovered = PickGroundItem(mouse);
        // Drawn far to near so the nearer labels lie on top.
        for (int i = placed.Count - 1; i >= 0; i--)
        {
            var (id, rect) = placed[i];
            bool stealing = Stealing(near[i].Item);
            string item = ItemLabelColour(near[i].Item), selected = stealing ? "1 0.4 0.4" : "0.4 1 0.4";
            string back = stealing ? "1 0.4 0.4" : id == hovered ? selected : item, text = id == hovered ? selected : item;
            controls.Add(new($"itemlabel-{i}", "Widget", rect, "", "", "Kenshi_GenericTextBoxFlatSmallSkin", true,
                new Dictionary<string, string> { ["Colour"] = back }, Vector4.Zero, Pickable: false));
            controls.Add(new($"itemlabel-text-{i}", "TextBox", rect, near[i].Item.Name, "", "", true,
                new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["TextAlign"] = "Center", ["TextColour"] = text }, Vector4.Zero, Pickable: false, TextScale: 1));
        }
    }

    /// <summary>Picking the item up would be stealing it: its owner's faction (<see cref="Meitou.Simulation.Items.GroundItem.OwnedBy"/>) is not the player's.</summary>
    /// <summary>The ground item under <paramref name="pixel"/> belongs to another faction: its pointer is <c>steal</c> (<c>FUN_1407fc440</c>), and a right click without Alt only walks to it.</summary>
    internal bool StealsGroundItem(Vector2 pixel) => PickGroundItem(pixel) is { } id && session.CurrentSnapshot.GroundItems.FirstOrDefault(g => g.Id == id) is { } g && Stealing(g);

    bool Stealing(Meitou.Simulation.Items.GroundItemSnapshot g) => g.OwnedBy >= 0 && g.OwnedBy != session.World.Player.Faction;

    /// <summary>Whether an ITEM record is an artifact (its <c>artifact</c> field); the host sets it.</summary>
    public Func<string, bool>? ArtifactOf { get; set; }

    /// <summary>
    /// A label's own colour (docs/game/ui-input.md "Ground item labels"): a plain ITEM's <c>ItemArtifact</c> or <c>ItemStandard</c>; a weapon, armour, backpack, nest item, map item,
    /// crossbow or limb its <c>ItemGrade</c> by its <see cref="Meitou.Simulation.Items.ItemInstance.GearLevel"/> below 16/36/51/66/81 (grades 4 and 5 share the yellow); anything else <c>DropItemLabelBackDefault</c>.
    /// </summary>
    string ItemLabelColour(Meitou.Simulation.Items.GroundItemSnapshot g)
    {
        int level = Meitou.Simulation.Items.ItemInstance.GearLevelOf(g.Type, g.Level);
        return g.Type switch
        {
            FcsRecordType.ITEM => TipHex(ArtifactOf?.Invoke(g.Record) == true ? "0dfceb" : "e0cccc"),
            FcsRecordType.WEAPON or FcsRecordType.ARMOUR or FcsRecordType.CONTAINER or FcsRecordType.NEST_ITEM or FcsRecordType.MAP_ITEM or FcsRecordType.CROSSBOW
                or FcsRecordType.LIMB_REPLACEMENT => TipHex(level < 16 ? "666666" : level < 36 ? "d9ccbf" : level < 51 ? "4099b3" : level < 66 ? "66fc80" : "fceb0d"),
            _ => "1 1 1",
        };
    }
}
