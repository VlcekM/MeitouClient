using System.Globalization;

namespace Meitou.Data.Ui;

/// <summary>
/// An image to draw: a texture and the source rectangle in it, in texture pixels (zero size: the whole texture).
/// </summary>
public sealed record MyGuiImage(string Texture, UiRect Source);

/// <summary>
/// Resolves a widget's image properties to a texture and rectangle.
/// </summary>
public static class MyGuiImages
{
    /// <summary>
    /// The image a widget's <paramref name="properties"/> name: <c>ImageTexture</c> (with <c>ImageRect</c>), else the frame of an image set
    /// (<c>ImageResource</c>, <c>ImageGroup</c>, <c>ImageName</c>, Meitou's <c>ImageFrame</c>); null when they name none that exists.
    /// </summary>
    public static MyGuiImage? Resolve(MyGuiResources resources, IReadOnlyDictionary<string, string> properties)
    {
        if (properties.TryGetValue("ImageTexture", out string? texture))
            return new(texture, properties.TryGetValue("ImageRect", out var rect) ? UiRect.Parse(rect) : default);
        if (!properties.TryGetValue("ImageResource", out var name) || !resources.Resources.TryGetValue(name, out var resource))
            return null;

        var group = resource.Elements("Group").FirstOrDefault(g => (string?)g.Attribute("name") == properties.GetValueOrDefault("ImageGroup"));
        var index = group?.Elements("Index").FirstOrDefault(i => (string?)i.Attribute("name") == properties.GetValueOrDefault("ImageName"));

        // ImageFrame (Meitou's): the index's frame to show, as the game picks one with ImageBox::setItemFrame; the first by default.
        int at = int.TryParse(properties.GetValueOrDefault("ImageFrame"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int f) ? f : 0;
        var frame = index?.Elements("Frame").ElementAtOrDefault(at) ?? index?.Elements("Frame").FirstOrDefault();
        if (group is null || frame is null) return null;

        static float[] Pair(string? s) =>
            (s ?? "0 0").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();

        var size = Pair((string?)group.Attribute("size"));
        var point = Pair((string?)frame.Attribute("point"));
        if (size.Length != 2 || point.Length != 2)
            throw new InvalidDataException("Image set size and point need two coordinates.");

        return new((string?)group.Attribute("texture") ?? "", new(point[0], point[1], size[0], size[1]));
    }
}