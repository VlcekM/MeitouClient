using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;

namespace Meitou.Game.Settings;

/// <summary>
/// The settings window's drawing: its inks, text, surfaces, buttons and switches, the setting rows, the open list and the message box.
/// </summary>
sealed class SettingsCanvas(SettingsMenu menu)
{
    /// <summary>
    /// The kit's main ink.
    /// </summary>
    internal static readonly Vector4 MainInk = new(175 / 255f, 166 / 255f, 139 / 255f, 1);

    /// <summary>
    /// The kit's light ink (selected and hovered text, titles).
    /// </summary>
    internal static readonly Vector4 LightInk = new(217 / 255f, 206 / 255f, 184 / 255f, 1);

    /// <summary>
    /// The muted ink.
    /// </summary>
    internal static readonly Vector4 MutedInk = new(0.52f, 0.49f, 0.43f, 1);

    /// <summary>
    /// The brass ink (the Meitou mark).
    /// </summary>
    internal static readonly Vector4 Brass = new(196 / 255f, 160 / 255f, 98 / 255f, 1);

    /// <summary>
    /// The Faithful mark's ink.
    /// </summary>
    static readonly Vector4 FaithfulInk = new(0.62f, 0.66f, 0.70f, 1);

    /// <summary>
    /// Measured text heights by text, width, size, viewport height, scale and language.
    /// </summary>
    readonly Dictionary<(string Text, int Width, int Size, int Height, int Scale, string Language), float> _textHeights = [];

    /// <summary>
    /// The window's pixel height (part of the text height cache's key).
    /// </summary>
    internal int TextViewportHeight;

    /// <summary>
    /// The open list's anchor (the row's button).
    /// </summary>
    internal UiRect ChoiceAnchor;

    /// <summary>
    /// The list whose fade-in is running.
    /// </summary>
    internal SettingsMenu.Choice? AnimatedChoice;

    /// <summary>
    /// The list's fade-in progress (0..1).
    /// </summary>
    float _choiceProgress;

    /// <summary>
    /// A picture under an entry's row: 560 units wide, the luckyren Mode image's proportions, with 8 units above it.
    /// </summary>
    const float PictureWidth = 560, PictureHeight = 72, PictureGap = 8;

    /// <summary>
    /// The Meitou UI mode is on.
    /// </summary>
    internal bool Meitou => menu.Player.UiMode == UiMode.Meitou;

    /// <summary>
    /// A rectangle in the window's 1120-unit space, in pixels.
    /// </summary>
    internal UiRect R(float x, float y, float w, float h) =>
        new(MathF.Round(menu.Bounds.X + x * menu.Scale), menu.Bounds.Y + y * menu.Scale, MathF.Round(w * menu.Scale), MathF.Round(h * menu.Scale));

    /// <summary>
    /// A filled rectangle.
    /// </summary>
    internal void Rect(UiRect at, Vector4 colour, UiRect? clip = null) =>
        menu.Controls.Add(new("settings-fill", "Panel", at, "", "", "", true, new Dictionary<string, string>(), colour, Clip: clip, Pickable: false));

    /// <summary>
    /// A text box in the standard font at <paramref name="size"/> units (translated unless <paramref name="translate"/> is false).
    /// </summary>
    internal void Text(string caption, UiRect at, float size = 16, Vector4? colour = null, string align = "Left VCenter", UiRect? clip = null,
        bool wrap = false, bool translate = true)
    {
        if (translate) caption = Loc.Tr(caption);
        var properties = new Dictionary<string, string>
        {
            ["TextAlign"] = align,
            ["FontName"] = "Kenshi_StandardFont",
            ["FontPixels"] = ((int)MathF.Round(size * menu.Scale)).ToString(CultureInfo.InvariantCulture),
        };
        if (wrap) properties["WordWrap"] = "true";
        menu.Controls.Add(new("settings-text", "TextBox", at, caption, "", "", true, properties, Vector4.Zero, colour ?? MainInk, Clip: clip,
            Pickable: false, TextScale: size * menu.Scale / 16));
    }

    /// <summary>
    /// A surface: a window, paper or button, from the Meitou kit's textures, else the game's skins.
    /// </summary>
    internal void Surface(UiRect at, string kind, bool selected = false, bool hover = false, UiRect? clip = null)
    {
        if (Meitou)
        {
            string texture = kind switch
            {
                "window" => "window",
                "primary" or "paper" => hover ? "paper-hover" : "paper",
                _ => selected ? "button-selected" : hover ? "button-hover" : "button",
            };
            float margin = kind == "window" ? 16 * menu.Scale : 0;
            bool window = kind == "window";
            var properties = new Dictionary<string, string>
            {
                ["ImageTexture"] = SettingsMenuArt.SurfacePrefix + texture,
                ["ImageBorder"] = window ? SettingsMenuArt.WindowBorder.ToString(CultureInfo.InvariantCulture) : "12",
                ["ImageBorderScale"] = (window ? menu.Scale / SettingsMenuArt.WindowDensity : menu.Scale).ToString(CultureInfo.InvariantCulture),
            };
            menu.Controls.Add(new("settings-surface", "ImageBox",
                new(at.X - margin, at.Y - margin, at.Width + margin * 2, at.Height + margin * 2), "", "", "", true,
                properties, Vector4.Zero, Clip: clip, Pickable: false));
        }
        else
        {
            menu.Controls.Add(new("settings-surface", "Panel", at, "", "", SurfaceSkin(kind), true,
                new Dictionary<string, string> { ["State"] = selected ? "pushed" : hover ? "highlighted" : "normal" },
                kind == "window" ? new(0.12f, 0.11f, 0.09f, 1) : Vector4.Zero, Clip: clip, Pickable: false));
        }
    }

    /// <summary>
    /// The MyGUI skin of a surface kind: a window, paper or a button.
    /// </summary>
    static string SurfaceSkin(string kind) =>
        kind == "window" ? "Kenshi_GenericWindowSkin" : kind == "paper" ? "Kenshi_GenericTextBoxSkin" : "Kenshi_Button1Skin";

    /// <summary>
    /// A button with its hover fade and press offset; a hit is added when it is not clipped.
    /// </summary>
    internal void Button(string label, UiRect at, Action action, bool selected = false, bool primary = false, UiRect? clip = null, float textInset = 0)
    {
        bool hovered = at.Contains(menu.Pointer.X, menu.Pointer.Y) && (clip is null || clip.Value.Contains(menu.Pointer.X, menu.Pointer.Y));
        float hover = menu.Motion.Hover(label, hovered);
        var drawn = at with { Y = at.Y + (hovered && menu.PointerDown ? menu.Scale : 0) };
        bool pressed = hovered && menu.PointerDown;
        Surface(drawn, primary ? "primary" : "button", selected || pressed, false, clip);
        if (hover > 0)
        {
            int first = menu.Controls.Count;
            Surface(drawn, primary ? "primary" : "button", selected || pressed, true, clip);
            menu.Motion.Opacity(first, hover);
        }
        var ink = primary && Meitou ? new Vector4(20 / 255f, 8 / 255f, 6 / 255f, 1) : Vector4.Lerp(MainInk, LightInk, selected ? 1 : hover);
        var textAt = new UiRect(drawn.X + 10 * menu.Scale, drawn.Y, drawn.Width - (20 + textInset) * menu.Scale, drawn.Height);
        Text(label, textAt, primary ? 15 : 13, ink, "Center", clip);
        if (clip is null || at.Intersect(clip.Value).Height >= at.Height - 1)
            menu.Hits.Add(new(at, action, Label: label));
    }

    /// <summary>
    /// Draws the rows from <paramref name="top"/>, scrolled, and sets the scroll's maximum.
    /// </summary>
    internal void DrawEntries(float top)
    {
        var entries = menu.Entries();
        menu.MaxScroll = Math.Max(0, entries.Sum(EntryHeight) * menu.Scale - menu.Viewport.Height);
        menu.Motion.AdvanceScroll();
        float entryY = top - menu.ScrollOffset / menu.Scale;
        foreach (var entry in entries)
        {
            float row = EntryHeight(entry);
            menu.Motion.AnimationScope = entry.Label;
            if (entryY + row >= top && entryY <= 666) DrawEntry(entry, entryY);
            entryY += row;
        }
    }

    /// <summary>
    /// Measures with the same font resource and wrapped width used by the settings text controls.
    /// </summary>
    internal float TextHeight(string text, float width, float size)
    {
        if (text.Length == 0) return 0;
        int px = (int)MathF.Round(size * menu.Scale);
        var key = (text, (int)width, (int)size, textViewportHeight: TextViewportHeight, (int)MathF.Round(menu.Scale * 1000), Loc.Language.Code);
        if (_textHeights.TryGetValue(key, out float cached)) return cached;
        if (menu.Player.SettingsPainter is not { } painter) return size * 1.25f;
        var pixels = painter.MeasurePixelText(text, MathF.Round(width * menu.Scale), Math.Clamp(px, 8, 96), size * menu.Scale / 16);
        float measured = pixels.Height / menu.Scale;
        _textHeights[key] = measured;
        return measured;
    }

    /// <summary>
    /// How many lines <paramref name="text"/> wraps to at <paramref name="width"/>.
    /// </summary>
    int TextLines(string text, float width, float size)
    {
        if (text.Length == 0) return 0;
        if (menu.Player.SettingsPainter is null) return Math.Max(1, (int)MathF.Ceiling(text.Length / Math.Max(1, width / size)));
        float wrapped = TextHeight(text, width, size);
        float line = TextHeight("Mg", 0, size);
        return Math.Max(1, (int)MathF.Ceiling(wrapped / Math.Max(1, line)));
    }

    /// <summary>
    /// The hint's width: the column less the indent, and less the Reset link's room on a changed row.
    /// </summary>
    static float HintWidth(SettingsMenu.Entry entry) => 456 - Indent(entry) - (entry is { Changed: true, Reset: not null } ? 56 : 0);

    /// <summary>
    /// The game's own arrow (the floor selector's: a grey chevron on a dark plate, gold when highlighted, <c>Kenshi_FloorArrowUpSkin</c> /
    /// <c>DownSkin</c>), 21 × 14 units centred on (<paramref name="x"/>, <paramref name="y"/>) in pixels: down opens (a closed group, a list), up
    /// closes (an open group).
    /// </summary>
    void Arrow(float x, float y, bool down, bool lit, UiRect? clip = null)
    {
        float w = MathF.Round(21 * menu.Scale), h = MathF.Round(14 * menu.Scale);
        string skin = down ? "Kenshi_FloorArrowDownSkin" : "Kenshi_FloorArrowUpSkin";
        menu.Controls.Add(new("settings-arrow", "Widget", new(MathF.Round(x - w / 2), MathF.Round(y - h / 2), w, h), "", "", skin, true,
            new Dictionary<string, string> { ["State"] = lit ? "highlighted" : "normal" }, Vector4.Zero, Clip: clip, Pickable: false));
    }

    /// <summary>
    /// A button that opens a list: its text, and the list's arrow against its right edge.
    /// </summary>
    internal void DropDown(string label, UiRect at, Action open, UiRect? clip = null)
    {
        Button(label, at, open, clip: clip, textInset: 26);
        Arrow(at.X + at.Width - 18 * menu.Scale, at.Y + at.Height / 2, true, at.Contains(menu.Pointer.X, menu.Pointer.Y), clip);
    }

    /// <summary>
    /// How far a Graphics row's text sits right of the column: the group chevron's room, and one step more inside a group.
    /// </summary>
    static float Indent(SettingsMenu.Entry entry) => entry.Raw is not null || entry.GroupId is not null ? 28 + entry.Depth * 24 : 0;

    /// <summary>
    /// A row's height: its label and hint, at least 36 units.
    /// </summary>
    float RowHeight(SettingsMenu.Entry entry)
    {
        float indent = Indent(entry);
        float label = TextHeight(Loc.Tr(entry.Label), 456 - indent, entry.Depth > 0 ? 13 : 14);
        if (entry.Hint.Length == 0) return Math.Max(36, label + 4);
        return Math.Max(36, 2 + label + 1 + TextHeight(Loc.Tr(entry.Hint), HintWidth(entry), 11) + 2);
    }

    /// <summary>
    /// A row's height with its picture.
    /// </summary>
    internal float EntryHeight(SettingsMenu.Entry entry) => RowHeight(entry) + (entry.Picture is null ? 0 : PictureGap + PictureHeight);

    /// <summary>
    /// Draws a row at <paramref name="y"/>: its line, picture, band, group header, name, reset link, hint and control.
    /// </summary>
    internal void DrawEntry(SettingsMenu.Entry entry, float y)
    {
        float row = RowHeight(entry), buttonY = y + (row - 26) / 2;
        Rect(R(212, y + EntryHeight(entry) - 1, 888, 1), MainInk with { W = 0.075f }, menu.Viewport);
        DrawPicture(entry, y, row);
        float indent = Indent(entry), labelSize = entry.Depth > 0 ? 13 : 14;
        string translatedLabel = Loc.Tr(entry.Label);
        float labelHeight = TextHeight(translatedLabel, 456 - indent, labelSize);
        bool labelWrap = TextLines(translatedLabel, 456 - indent, labelSize) > 1;
        DrawFeatureBand(entry, y, row);
        DrawGroupHeader(entry, y, row);
        DrawName(entry, y, row, indent, labelSize, labelHeight, labelWrap);
        DrawResetLink(entry, y);
        DrawHint(entry, y, indent, labelHeight);
        DrawControl(entry, y, row, buttonY);
    }

    /// <summary>
    /// The entry's picture, when it has one and its texture is loaded.
    /// </summary>
    void DrawPicture(SettingsMenu.Entry entry, float y, float row)
    {
        if (entry.Picture is { } picture && menu.Player.SettingsPainter is { } painter)
        {
            menu.Art.EnsureLuckyrenImage(painter);
            if (painter.HasTexture(picture))
            {
                menu.Controls.Add(new("settings-picture", "ImageBox", R(212, y + row - 4, PictureWidth, PictureHeight), "", "", "", true,
                    new Dictionary<string, string> { ["ImageTexture"] = picture }, Vector4.Zero, Clip: menu.Viewport, Pickable: false));
            }
        }
    }

    /// <summary>
    /// The band behind a feature's settings and the light on an open header.
    /// </summary>
    void DrawFeatureBand(SettingsMenu.Entry entry, float y, float row)
    {
        // A feature's settings sit in a darker band with a brass edge under its header; an open header is lightly lit.
        if (entry.Depth > 0)
        {
            Rect(R(212, y, 888, EntryHeight(entry)), new(0, 0, 0, 0.28f), menu.Viewport);
            Rect(R(214, y, 2, EntryHeight(entry)), Brass with { W = 0.55f }, menu.Viewport);
        }
        else if (entry.Open == true)
        {
            Rect(R(212, y, 888, row), MainInk with { W = 0.06f }, menu.Viewport);
        }
    }

    /// <summary>
    /// A feature header's arrow and the click that opens or closes it.
    /// </summary>
    void DrawGroupHeader(SettingsMenu.Entry entry, float y, float row)
    {
        if (entry is { Open: { } open, GroupId: { } group })
        {
            // A feature's header: the arrow, and a click anywhere on its text opens or closes its settings.
            var header = R(212, y, 456, row);
            if (header.Intersect(menu.Viewport).Height >= header.Height - 1)
                menu.Hits.Add(new(header, () => menu.Grouping.ToggleGroup(group), Label: entry.Label));

            var mark = R(212, y + 2, 22, entry.Hint.Length > 0 ? 20 : 32);
            Arrow(mark.X + mark.Width / 2, mark.Y + mark.Height / 2, !open, header.Contains(menu.Pointer.X, menu.Pointer.Y), menu.Viewport);
        }
    }

    /// <summary>
    /// The entry's name, in brass when it differs from the preset, and its impact.
    /// </summary>
    void DrawName(SettingsMenu.Entry entry, float y, float row, float indent, float labelSize, float labelHeight, bool labelWrap)
    {
        // A setting that differs from the preset: its name in brass and a Reset link back to the preset's value (a header's resets the feature).
        var nameInk = entry.Changed ? Brass : MainInk;
        Text(entry.Label, R(212 + indent, y + 2, 456 - indent, entry.Hint.Length > 0 ? labelHeight + 1 : row - 4), labelSize, nameInk,
            clip: menu.Viewport, wrap: labelWrap);
        if (entry.Impact is { } impact)
            Text(impact, R(212, y + 2, 456, 20), 11, ImpactInk(impact), "Right VCenter", menu.Viewport);
    }

    /// <summary>
    /// The ink of an impact note: brass for high, the main ink for medium, muted otherwise.
    /// </summary>
    static Vector4 ImpactInk(string impact)
    {
        if (impact.StartsWith("High", StringComparison.Ordinal)) return Brass;

        return impact.StartsWith("Medium", StringComparison.Ordinal) ? MainInk : MutedInk;
    }

    /// <summary>
    /// The Reset link of a changed entry, underlined, back to the preset's value.
    /// </summary>
    void DrawResetLink(SettingsMenu.Entry entry, float y)
    {
        if (entry is { Changed: true, Reset: { } reset })
        {
            var link = R(612, y + 18, 56, 16);
            bool over = link.Contains(menu.Pointer.X, menu.Pointer.Y) && menu.Viewport.Contains(menu.Pointer.X, menu.Pointer.Y);
            Text("Reset", link, 11, over ? LightInk : Brass, "Right VCenter", menu.Viewport);
            Rect(new(link.X + link.Width - 30 * menu.Scale, link.Y + link.Height - menu.Scale, 30 * menu.Scale, menu.Scale),
                (over ? LightInk : Brass) with { W = 0.6f }, menu.Viewport);
            if (link.Intersect(menu.Viewport).Height >= link.Height - 1)
                menu.Hits.Add(new(link, reset, Label: "Reset " + entry.Label));
        }
    }

    /// <summary>
    /// The entry's hint under its name, wrapped.
    /// </summary>
    void DrawHint(SettingsMenu.Entry entry, float y, float indent, float labelHeight)
    {
        if (entry.Hint.Length > 0)
        {
            float hintHeight = TextHeight(Loc.Tr(entry.Hint), HintWidth(entry), 11);
            Text(entry.Hint, R(212 + indent, y + 2 + labelHeight + 1, HintWidth(entry), hintHeight + 1), 11, entry.Warn ? Brass : MutedInk,
                "Left Top", menu.Viewport, wrap: true);
        }
    }

    /// <summary>
    /// The entry's control: a Faithful / Meitou switch, a worded or plain toggle, a choice, a slider, a key binding or a run button.
    /// </summary>
    void DrawControl(SettingsMenu.Entry entry, float y, float row, float buttonY)
    {
        if (entry.Toggle is { Remaster: true, Optional: false } remaster)
        {
            // A Faithful / Meitou switch: both sides as buttons, the active one marked.
            bool meitou = remaster.Get();
            if (remaster.OnOff)
            {
                Segmented(R(860, y + (row - 22) / 2, 240, 22), meitou ? 1 : 0, i => remaster.Set(i == 1), "Faithful (off)", "Meitou (on)");
            }
            else
            {
                Segmented(R(920, y + (row - 22) / 2, 180, 22), meitou ? 1 : 0, i => remaster.Set(i == 1));
            }
        }
        // A switch with its own words (Hear from: Character / Camera): one button showing the current side.
        else if (entry.Toggle is { Text: { } words } worded)
        {
            Button(words(), R(976, buttonY, 124, 26), () => worded.Set(!worded.Get()), worded.Get(), clip: menu.Viewport);
        }
        // An optional effect (bloom, in neither look) or a plain setting: Off | On in the same control.
        else if (entry.Toggle is { } toggle)
        {
            Segmented(R(920, y + (row - 22) / 2, 180, 22), toggle.Get() ? 1 : 0, i => toggle.Set(i == 1), "Off", "On");
        }
        else if (entry is { Segmented: true, Choice: { } sides })
        {
            Segmented(R(860, y + (row - 22) / 2, 240, 22), sides.Current(), sides.Choose, "All Faithful", "All Meitou");
        }
        else if (entry.Choice is { } choice)
        {
            DrawChoiceButton(choice, buttonY);
        }
        else if (entry.Slider is { } slider)
        {
            DrawSliderRow(entry, slider, y, row);
        }
        else if (entry.Binding is { } binding)
        {
            string label = string.Join(" / ", menu.Bindings.Get(binding));
            Button(label.Length > 0 ? label : "Unbound", R(688, buttonY, 412, 26), () =>
            {
                menu.Rebinding = binding;
                menu.Notice = "";
            }, clip: menu.Viewport);
        }
        else if (entry.Run is { } run)
        {
            Button("Clear cache", R(944, buttonY, 156, 26), run, clip: menu.Viewport);
        }
    }

    /// <summary>
    /// A choice's drop-down showing the current option.
    /// </summary>
    void DrawChoiceButton(SettingsMenu.Choice choice, float buttonY)
    {
        var options = choice.Options();
        int index = choice.Current();
        string current = Loc.Tr(index >= 0 && index < options.Count ? options[index] : "Unavailable");
        var at = R(688, buttonY, 412, 26);
        DropDown(current, at, () =>
        {
            menu.ActiveChoice = choice;
            menu.ChoiceSelection = Math.Clamp(choice.Current(), 0, Math.Max(0, options.Count - 1));
            menu.ChoiceScroll = Math.Max(0, menu.ChoiceSelection - 3);
            ChoiceAnchor = at;
            menu.Focus = -1;
        }, menu.Viewport);
    }

    /// <summary>
    /// A slider row: the bar, and its value or the number being typed into it.
    /// </summary>
    void DrawSliderRow(SettingsMenu.Entry entry, Rendering.Frame.Slider slider, float y, float row)
    {
        var valueAt = R(908, y, 192, 20);
        // By label: the rows (and their sliders) are built again each frame.
        bool editing = menu.EditingNumber?.Label == slider.Label;
        if (editing)
        {
            DrawNumberEdit(valueAt);
        }
        else
        {
            DrawSliderValue(entry, slider, y, valueAt);
        }

        var track = R(696, y + row - 9, 396, 3);
        Rect(track, new(0.10f, 0.08f, 0.07f, 1), menu.Viewport);
        Rect(track with { Width = track.Width * slider.Fraction }, entry.Locked ? MutedInk with { W = 0.5f } : Brass with { W = 0.75f }, menu.Viewport);
        if (slider.Faithful is { } faithful) RemasterMark(slider, faithful, track, FaithfulInk);
        if (slider.Meitou is { } meitou) RemasterMark(slider, meitou, track, Brass);
        if (slider.Faithful is { } f && slider.Meitou is { } m && entry.Depth == 0)   // inside a feature its header has the switch
        {
            DrawQuickSwitch(slider, f, m, y);
        }

        var knob = new UiRect(track.X + slider.Fraction * track.Width - 3 * menu.Scale, track.Y - 4 * menu.Scale, 6 * menu.Scale, 11 * menu.Scale);
        Rect(knob, entry.Locked ? MutedInk : LightInk, menu.Viewport);
        // A locked slider (a mode's value, SettingModes) shows its value but takes no drag, key or typed value.
        if (entry.Locked) return;

        var hit = R(688, y + row - 16, 412, 16);
        if (hit.Intersect(menu.Viewport).Height >= hit.Height - 1) menu.Hits.Add(new(hit, () => { }, slider, track, entry.Label));
        if (valueAt.Intersect(menu.Viewport).Height >= valueAt.Height - 1) menu.Hits.Add(new(valueAt, () => menu.BeginNumber(slider, valueAt)));
    }

    /// <summary>
    /// The box a slider's value is typed into.
    /// </summary>
    void DrawNumberEdit(UiRect valueAt)
    {
        menu.NumberBounds = valueAt;
        Rect(valueAt, new(0.06f, 0.05f, 0.04f, 0.95f), menu.Viewport);
        var textAt = valueAt with { X = valueAt.X + 6 * menu.Scale, Width = valueAt.Width - 12 * menu.Scale };
        menu.Controls.Add(new("settings-number", "EditBox", textAt, menu.NumberText, "", "", true,
            new Dictionary<string, string>
            {
                ["Focused"] = "true",
                ["Numeric"] = "true",
                ["TextAlign"] = "Right VCenter",
                ["FontPixels"] = ((int)MathF.Round(13 * menu.Scale)).ToString(CultureInfo.InvariantCulture),
                ["SelectionStart"] = Math.Min(menu.NumberCaret, menu.NumberAnchor).ToString(CultureInfo.InvariantCulture),
                ["SelectionLength"] = Math.Abs(menu.NumberCaret - menu.NumberAnchor).ToString(CultureInfo.InvariantCulture),
                ["CaretIndex"] = menu.NumberCaret.ToString(CultureInfo.InvariantCulture),
            }, Vector4.Zero, LightInk, Clip: menu.Viewport, Pickable: false));
    }

    /// <summary>
    /// A slider's value at the row's right, lit under the pointer.
    /// </summary>
    void DrawSliderValue(SettingsMenu.Entry entry, Rendering.Frame.Slider slider, float y, UiRect valueAt)
    {
        if (valueAt.Contains(menu.Pointer.X, menu.Pointer.Y))
        {
            Rect(valueAt, MainInk with { W = 0.06f }, menu.Viewport);
            Rect(valueAt with { Y = valueAt.Y + valueAt.Height - menu.Scale, Height = menu.Scale }, MainInk with { W = 0.4f }, menu.Viewport);
        }

        Text(slider.Display, R(688, y, 412, 20), 13, entry.Locked ? MutedInk : LightInk, "Right VCenter", menu.Viewport);
    }

    /// <summary>
    /// The switch between the game's value and Meitou's for a slider, the one it is at marked.
    /// </summary>
    void DrawQuickSwitch(Rendering.Frame.Slider slider, float f, float m, float y)
    {
        // The quick switch: jump to the game's value or Meitou's; the one the slider is at is marked.
        float now = slider.Get(), near = 0.002f * (slider.Max - slider.Min);
        int at = MathF.Abs(now - m) <= near ? 1 : MathF.Abs(now - f) <= near ? 0 : -1;
        Segmented(R(696, y + 2, 140, 18), at, i => slider.Set(i == 1 ? m : f));
    }

    /// <summary>
    /// A notch under a slider's track at <paramref name="value"/>, in the colour of its side of the Faithful | Meitou switch.
    /// </summary>
    void RemasterMark(Rendering.Frame.Slider slider, float value, UiRect track, Vector4 ink)
    {
        float x = MathF.Round(track.X + slider.FractionOf(value) * track.Width), w = MathF.Max(2, MathF.Round(2 * menu.Scale));
        Rect(new(x - w / 2, track.Y + track.Height + menu.Scale, w, MathF.Round(4 * menu.Scale)), ink with { W = 0.9f }, menu.Viewport);
    }

    /// <summary>
    /// A compact Faithful | Meitou switch: one frame with two halves, the chosen half (<paramref name="selected"/>: 0 Faithful, 1 Meitou, -1 neither)
    /// filled in its colour (the colours of the slider notches); a click on a half calls <paramref name="choose"/>.
    /// </summary>
    internal void Segmented(UiRect at, int selected, Action<int> choose, string faithful = "Faithful", string meitou = "Meitou")
    {
        float line = MathF.Max(1, MathF.Round(menu.Scale));
        Rect(at, new(0.07f, 0.06f, 0.05f, 0.9f), menu.Viewport);
        string[] names = [faithful, meitou];
        Vector4[] inks = [FaithfulInk, Brass];
        float half = MathF.Round(at.Width / 2);
        for (int i = 0; i < 2; i++)
        {
            int index = i;
            var part = new UiRect(at.X + i * half, at.Y, i == 0 ? half : at.Width - half, at.Height);
            bool hovered = part.Contains(menu.Pointer.X, menu.Pointer.Y) && menu.Viewport.Contains(menu.Pointer.X, menu.Pointer.Y);
            float hover = menu.Motion.Hover(names[i], hovered);
            if (selected == i) Rect(part, inks[i] with { W = 0.28f }, menu.Viewport);
            else if (hover > 0) Rect(part, MainInk with { W = 0.08f * hover }, menu.Viewport);
            if (selected == i) Rect(part with { Y = part.Y + part.Height - 2 * line, Height = 2 * line }, inks[i] with { W = 0.9f }, menu.Viewport);
            var ink = selected == i ? LightInk : Vector4.Lerp(MutedInk, MainInk, hover);
            Text(names[i], part, 12, ink, "Center", menu.Viewport);
            if (part.Intersect(menu.Viewport).Height >= part.Height - 1) menu.Hits.Add(new(part, () => choose(index), Label: names[i]));
        }
        // The frame and the divider.
        var edge = MainInk with { W = 0.22f };
        Rect(at with { Height = line }, edge, menu.Viewport);
        Rect(at with { Y = at.Y + at.Height - line, Height = line }, edge, menu.Viewport);
        Rect(at with { Width = line }, edge, menu.Viewport);
        Rect(at with { X = at.X + at.Width - line, Width = line }, edge, menu.Viewport);
        Rect(new(at.X + half, at.Y + 3 * menu.Scale, line, at.Height - 6 * menu.Scale), edge, menu.Viewport);
    }

    /// <summary>
    /// Draws the open list under its anchor: up to six options, scrolled, fading in; choosing one closes it.
    /// </summary>
    internal void DrawChoice(SettingsMenu.Choice choice)
    {
        if (!ReferenceEquals(AnimatedChoice, choice))
        {
            AnimatedChoice = choice;
            _choiceProgress = 0;
        }

        _choiceProgress = Math.Min(1, _choiceProgress + menu.Motion.FrameDelta / 0.1f);
        int first = menu.Controls.Count;
        menu.Hits.Clear();
        var options = choice.Options();
        int count = Math.Min(6, options.Count);
        menu.ChoiceScroll = Math.Clamp(menu.ChoiceScroll, 0, Math.Max(0, options.Count - count));
        float h = (count * 28 + 12) * menu.Scale;
        float y = Math.Min(ChoiceAnchor.Y + ChoiceAnchor.Height + 6 * menu.Scale, menu.Bounds.Y + menu.Bounds.Height - 24 * menu.Scale - h);
        var at = new UiRect(ChoiceAnchor.X, y, ChoiceAnchor.Width, h);
        Surface(at, "window");
        for (int i = 0; i < count; i++)
        {
            int index = menu.ChoiceScroll + i;
            Button(options[index], new(at.X + 6 * menu.Scale, at.Y + (6 + i * 28) * menu.Scale, at.Width - 12 * menu.Scale, 24 * menu.Scale),
                () =>
                {
                    choice.Choose(index);
                    menu.ActiveChoice = null;
                    menu.Focus = -1;
                }, index == menu.ChoiceSelection);
        }

        menu.Motion.Opacity(first, MenuMotion.Ease(_choiceProgress));
    }

    /// <summary>
    /// Draws a message box over the window: title, body, Cancel and an optional Confirm.
    /// </summary>
    internal void DrawMessage(string title, string body, Action cancel, Action? confirm = null)
    {
        menu.Hits.Clear();
        Rect(menu.Bounds, new(8 / 255f, 6 / 255f, 5 / 255f, 0.8f));
        var at = R(290, 250, 540, 220);
        Surface(at, "window");
        Text(title, R(316, 264, 488, 30), 20, LightInk);
        menu.Controls.Add(new("settings-message", "TextBox", R(316, 306, 488, 94), body, "", "", true,
            new Dictionary<string, string>
            {
                ["WordWrap"] = "true",
                ["FontPixels"] = ((int)MathF.Round(14 * menu.Scale)).ToString(CultureInfo.InvariantCulture),
            }, Vector4.Zero, MainInk, Pickable: false, TextScale: menu.Scale));
        Button("Cancel", R(500, 420, 138, 32), cancel);
        if (confirm is not null) Button("Confirm", R(650, 420, 154, 32), confirm, primary: true);
    }
}