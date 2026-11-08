using System.Globalization;
using Meitou.Data.Gameplay;
using Meitou.Data.Ui;
using Meitou.Game.Rendering.Ui;

namespace Meitou.Game;

sealed partial class PlayerInterface
{
    ItemIcons? itemIcons;

    /// <summary>The source of the item icons the inventory and trader grids draw (null: only names).</summary>
    public void SetItemIcons(ItemIcons? icons) => itemIcons = icons;

    /// <summary>Item icon files the interface asked for that are not on the GPU yet (for pictures that wait for them).</summary>
    public int PendingIconFiles => uiPainter?.PendingIconFiles ?? 0;

    /// <summary>The records' names with the item icons the renderer is drawing (<see cref="UiPainter.RenderedIcons"/>): the host connects it to the character renderer.</summary>
    public void SetRenderedIcons(Func<string, (Meitou.Game.Rendering.Gpu.GpuTexture Texture, Meitou.Game.Rendering.Gpu.SampledTexture Sampled)?>? lookup) { if (uiPainter is not null) uiPainter.RenderedIcons = lookup; }

    /// <summary>
    /// An item icon filling <paramref name="area"/> with its quantity in the corner, or a name while the icon loads. <paramref name="tip"/> is what its hover tooltip lists (the name only when null).
    /// The transparent text control handles picking and carries <paramref name="property"/>; <paramref name="colour"/> is the item's <c>color sid</c>.
    /// </summary>
    internal void AddItemCell(List<UiControl> controls, string name, UiRect area, string record, string material, string itemName, int quantity, ItemTip? tip, string property, int index, UiRect? clip, bool pickable = true, float? charge = null, string colour = "")
    {
        itemTips[name] = tip ?? ItemTip.NameOnly(record, material, itemName, quantity);
        string count = quantity > 1 ? quantity.ToString(CultureInfo.InvariantCulture) : "";
        Dictionary<string, string>? image = null;
        if (itemIcons?.Choose(record, material, colour) is { } choice && uiPainter is { } painter)
        {
            if (choice is { Source: ItemIconSource.File, File: { } file } && painter.IconReady(file))
                image = new() { ["ImageTexture"] = UiPainter.IconTexture(file) };
            else if (choice.Source == ItemIconSource.Render && UiPainter.RenderedIconTexture(record, material, choice.Colour) is { } rendered && painter.RenderedIconReady(rendered))
                image = new() { ["ImageTexture"] = rendered, ["ImageFlipV"] = "true" };
        }
        bool shown = image is not null;
        controls.Add(new($"{name}-background", "Widget", area, "", "", "", true,
            new Dictionary<string, string>(), new(0.12f, 0.08f, 0.04f, 0.25f), Pickable: false, Clip: clip));
        if (shown) controls.Add(new($"{name}-icon", "ImageBox", area, "", "", "", true, image!, System.Numerics.Vector4.Zero, Pickable: false, Clip: clip));
        if (charge is { } fill)
        {
            // A white line over a black track along the cell's bottom edge, as long as what is left (the vanilla inventory and trader screenshots show it under food stacks).
            float height = Math.Max(3, InventoryScale * 3), inset = 1, width = Math.Max(0, area.Width - 2 * inset), top = area.Y + area.Height - inset - height;
            controls.Add(new($"{name}-charge-track", "Widget", new(area.X + inset, top, width, height), "", "", "", true,
                new Dictionary<string, string>(), new(0, 0, 0, 1), Pickable: false, Clip: clip));
            controls.Add(new($"{name}-charge", "Widget", new(area.X + inset, top, width * Math.Clamp(fill, 0, 1), height), "", "", "", true,
                new Dictionary<string, string>(), System.Numerics.Vector4.One, Pickable: false, Clip: clip));
        }
        string caption = shown ? count : itemName + (count.Length > 0 ? " x" + count : "");
        controls.Add(new(name, "TextBox", area, caption, "", "", true,
            new Dictionary<string, string> { ["WordWrap"] = "true", ["TextAlign"] = shown ? "Right Bottom" : "Center", ["FontName"] = "Kenshi_StandardFont_Medium", [ItemTipKey] = name, [property] = index.ToString(CultureInfo.InvariantCulture) },
            System.Numerics.Vector4.Zero, TextColour: System.Numerics.Vector4.One, Pickable: pickable, TextScale: 0.9f * InventoryScale, Clip: clip));
    }
}
