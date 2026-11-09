using System.Globalization;

namespace Meitou.Data.Ui;

/// <summary>
/// A mouse pointer (<c>ResourceImageSetPointer</c>, data/gui/pointer/kenshi_pointers.xml): its image set's frame drawn <c>Size</c> pixels large
/// (<see cref="Width"/> × <see cref="Height"/>) with <c>Point</c> (<see cref="PointX"/>, <see cref="PointY"/>: the hot spot, pointer pixels from the
/// top left) on the mouse position.
/// </summary>
public sealed record MyGuiPointer(string Name, float PointX, float PointY, float Width, float Height, MyGuiImage Image)
{
    /// <summary>
    /// Where the pointer is drawn for the mouse at (<paramref name="x"/>, <paramref name="y"/>).
    /// </summary>
    public UiRect At(float x, float y) => new(x - PointX, y - PointY, Width, Height);
}

/// <summary>
/// Reads the mouse pointers of the loaded MyGUI resources.
/// </summary>
public static class MyGuiPointers
{
    /// <summary>
    /// The pointer named <paramref name="name"/> with its image resolved, or null when it or its image set is missing (the original's empty-named
    /// entry has neither).
    /// </summary>
    public static MyGuiPointer? Resolve(MyGuiResources resources, string name)
    {
        if (name.Length == 0 || !resources.Resources.TryGetValue(name, out var resource)
            || MyGuiReader.Attribute(resource, "type") != "ResourceImageSetPointer")
            return null;
        string? Property(string key) =>
            resource.Elements("Property").FirstOrDefault(p => (string?)p.Attribute("key") == key)?.Attribute("value")?.Value;
        var point = Pair(Property("Point"));
        var size = Pair(Property("Size"));
        if (point is null || size is null || Property("Resource") is not { Length: > 0 } imageSet) return null;
        // The image sets of the pointers keep their one frame under group and index "Pointer".
        var image = MyGuiImages.Resolve(resources,
            new Dictionary<string, string> { ["ImageResource"] = imageSet, ["ImageGroup"] = "Pointer", ["ImageName"] = "Pointer" });
        return image is null ? null : new(name, point.Value.X, point.Value.Y, size.Value.X, size.Value.Y, image);
    }

    /// <summary>
    /// Two whitespace-separated numbers; null when <paramref name="text"/> is not exactly two.
    /// </summary>
    static (float X, float Y)? Pair(string? text)
    {
        var parts = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
            float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ? (x, y) : null;
    }
}