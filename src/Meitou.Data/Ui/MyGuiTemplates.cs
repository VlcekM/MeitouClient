using System.Globalization;

namespace Meitou.Data.Ui;

/// <summary>
/// Resolve ResourceLayout skins and their Client regions without rewriting the source layout.
/// </summary>
public static partial class MyGuiTemplates
{
    /// <summary>
    /// <paramref name="layout"/> with every widget whose skin is a layout template replaced by the template's widgets, recursively.
    /// </summary>
    public static MyGuiLayout Expand(MyGuiLayout layout, MyGuiResources resources) => new(layout.Version,
        layout.Roots.Select(w => ExpandWidget(w, resources, [])).ToArray());

    /// <summary>
    /// <paramref name="source"/> with its children expanded and, when its skin is a <c>ResourceLayout</c>, the template's parts cloned around them
    /// (named <c>owner@part#n</c>, the children placed in its Client where the type takes one). <paramref name="chain"/> holds the templates being
    /// expanded; a cycle throws <see cref="InvalidDataException"/>.
    /// </summary>
    static MyGuiWidget ExpandWidget(MyGuiWidget source, MyGuiResources resources, HashSet<string> chain)
    {
        if (source.Properties.GetValueOrDefault("ResolvedTemplate") == "true") return source;
        var children = source.Children.Select(c => ExpandWidget(c, resources, new(chain))).ToArray();
        if (!resources.Templates.TryGetValue(source.Skin, out var resource)
            && (!resources.Resources.TryGetValue(source.Skin, out resource) || MyGuiReader.Attribute(resource, "type") != "ResourceLayout"))
            return source with { Children = children };
        if (!chain.Add(source.Skin)) throw new InvalidDataException($"Cyclic MyGUI template: {source.Skin}");
        var template = MyGuiReader.ReadResourceLayout(resource).Roots.FirstOrDefault()
            ?? throw new InvalidDataException($"Empty MyGUI template: {source.Skin}");
        bool placed = false;
        int anonymous = 0;
        MyGuiWidget Clone(MyGuiWidget node, float referenceWidth, float referenceHeight)
        {
            var properties = new Dictionary<string, string>(node.Properties, StringComparer.Ordinal)
            {
                ["TemplateReferenceWidth"] = referenceWidth.ToString(CultureInfo.InvariantCulture),
                ["TemplateReferenceHeight"] = referenceHeight.ToString(CultureInfo.InvariantCulture),
                ["TemplateOwner"] = source.Name,
            };
            string name = source.Name + "@" + (node.Name.Length > 0 ? node.Name : "part") + "#" + anonymous++;
            string originalName = node.Name;
            properties["TemplatePart"] = originalName;
            if (originalName == "Caption") properties["CaptionOwner"] = source.Name;
            if (node.UserStrings.TryGetValue("Event", out string? eventName)) properties["TemplateEvent"] = eventName;
            // MyGUI turns a part's position_real into pixels once, at the template's own size; its align then keeps those margins
            // (Kenshi_ListBoxEmpty's client sits 19 x 18 px in, whatever the list's size).
            var position = node.RealPosition
                ? new UiRect(node.Position.X * referenceWidth, node.Position.Y * referenceHeight, node.Position.Width * referenceWidth,
                    node.Position.Height * referenceHeight)
                : node.Position;
            var nested = node.Children.Select(c => Clone(c, position.Width, position.Height)).ToList();
            // The layout's children go into the template's Client only for the widgets that take one as their client (MyGUI's setWidgetClient:
            // windows, views, edit boxes, lists); a plain Widget, TextBox, ImageBox or Button keeps them itself (the title's CreditsPanel, a
            // Kenshi_EditBox-skinned Widget, holds its text box over its whole face).
            if (originalName == "Client" && !placed && source.Type is not ("TabControl" or "Widget" or "TextBox" or "ImageBox" or "Button"))
            {
                nested.AddRange(children);
                placed = true;
                if (source.Type is "EditBox" or "TextBox") properties["CaptionOwner"] = source.Name;
            }
            var expanded = node with { Name = name, Position = position, RealPosition = false, Properties = properties, Children = nested };
            return ExpandWidget(expanded, resources, new(chain));
        }
        var framed = template.Children.Select(c => Clone(c, template.Position.Width, template.Position.Height)).ToList();
        if (!placed) framed.AddRange(children);
        var rootProperties = new Dictionary<string, string>(template.Properties, StringComparer.Ordinal);
        foreach (var p in source.Properties) rootProperties[p.Key] = p.Value;
        if (framed.Any(c => c.DescendantsAndSelf().Any(n => n.Properties.ContainsKey("CaptionOwner")))) rootProperties["SkipCaption"] = "true";
        var strings = new Dictionary<string, string>(template.UserStrings, StringComparer.Ordinal);
        foreach (var p in source.UserStrings) strings[p.Key] = p.Value;
        if (source.Type == "MultiListBox") framed.AddRange(ColumnPrototypes(source.Name, strings, resources, chain));
        // A ListBox's lines are its SkinLine template (Kenshi_ListBoxItem): their skin and font; the selected line is that skin's checked state.
        if (strings.TryGetValue("SkinLine", out var line) && resources.Templates.TryGetValue(line, out var lineResource)
            && MyGuiReader.ReadResourceLayout(lineResource).Roots.FirstOrDefault() is { } lineRoot)
        {
            rootProperties["LineSkin"] = lineRoot.Skin;
            if (lineRoot.Properties.TryGetValue("FontName", out var lineFont)) rootProperties["LineFont"] = lineFont;
        }
        var expanded = source with { Skin = template.Skin, Properties = rootProperties, UserStrings = strings, Children = framed };
        // A template's root requests a native skin, even when that skin shares the template's name.
        var resolvedProperties = new Dictionary<string, string>(expanded.Properties, StringComparer.Ordinal) { ["ResolvedTemplate"] = "true" };
        return expanded with { Properties = resolvedProperties };
    }
}