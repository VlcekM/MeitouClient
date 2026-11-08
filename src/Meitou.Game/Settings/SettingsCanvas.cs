using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;

namespace Meitou.Game.Settings;

/// <summary>The settings window's drawing: its inks, text, surfaces, buttons and switches, the setting rows, the open list and the message box.</summary>
sealed class SettingsCanvas(SettingsMenu menu)
{
    internal static readonly Vector4 MainInk = new(175 / 255f, 166 / 255f, 139 / 255f, 1);
    internal static readonly Vector4 LightInk = new(217 / 255f, 206 / 255f, 184 / 255f, 1);
    internal static readonly Vector4 MutedInk = new(0.52f, 0.49f, 0.43f, 1);
    internal static readonly Vector4 Brass = new(196 / 255f, 160 / 255f, 98 / 255f, 1);
    internal bool Meitou => menu.player.UiMode == UiMode.Meitou;
    internal int textViewportHeight;
    readonly Dictionary<(string Text, int Width, int Size, int Height, int Scale, string Language), float> textHeights = [];

    internal UiRect R(float x, float y, float w, float h) =>
        new(MathF.Round(menu.bounds.X + x * menu.scale), menu.bounds.Y + y * menu.scale, MathF.Round(w * menu.scale), MathF.Round(h * menu.scale));

    internal void Rect(UiRect at, Vector4 colour, UiRect? clip = null) => menu.controls.Add(new("settings-fill", "Panel", at, "", "", "", true, new Dictionary<string, string>(), colour, Clip: clip, Pickable: false));

    internal void Text(string caption, UiRect at, float size = 16, Vector4? colour = null, string align = "Left VCenter", UiRect? clip = null, bool wrap = false, bool translate = true)
    {
        if (translate) caption = Loc.Tr(caption);
        var properties = new Dictionary<string, string> { ["TextAlign"] = align, ["FontName"] = "Kenshi_StandardFont", ["FontPixels"] = ((int)MathF.Round(size * menu.scale)).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (wrap) properties["WordWrap"] = "true";
        menu.controls.Add(new("settings-text", "TextBox", at, caption, "", "", true, properties, Vector4.Zero, colour ?? MainInk, Clip: clip, Pickable: false, TextScale: size * menu.scale / 16));
    }

    internal void Surface(UiRect at, string kind, bool selected = false, bool hover = false, UiRect? clip = null)
    {
        if (Meitou)
        {
            string texture = kind switch { "window" => "window", "primary" or "paper" => hover ? "paper-hover" : "paper", _ => selected ? "button-selected" : hover ? "button-hover" : "button" };
            float margin = kind == "window" ? 16 * menu.scale : 0;
            bool window = kind == "window";
            var properties = new Dictionary<string, string>
            {
                ["ImageTexture"] = SettingsMenuArt.SurfacePrefix + texture,
                ["ImageBorder"] = window ? SettingsMenuArt.WindowBorder.ToString(System.Globalization.CultureInfo.InvariantCulture) : "12",
                ["ImageBorderScale"] = (window ? menu.scale / SettingsMenuArt.WindowDensity : menu.scale).ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            menu.controls.Add(new("settings-surface", "ImageBox", new(at.X - margin, at.Y - margin, at.Width + margin * 2, at.Height + margin * 2), "", "", "", true,
                properties, Vector4.Zero, Clip: clip, Pickable: false));
        }
        else
            menu.controls.Add(new("settings-surface", "Panel", at, "", "", kind == "window" ? "Kenshi_GenericWindowSkin" : kind == "paper" ? "Kenshi_GenericTextBoxSkin" : "Kenshi_Button1Skin", true,
                new Dictionary<string, string> { ["State"] = selected ? "pushed" : hover ? "highlighted" : "normal" },
                kind == "window" ? new(0.12f, 0.11f, 0.09f, 1) : Vector4.Zero, Clip: clip, Pickable: false));
    }

    internal void Button(string label, UiRect at, Action action, bool selected = false, bool primary = false, UiRect? clip = null, float textInset = 0)
    {
        bool hovered = at.Contains(menu.pointer.X, menu.pointer.Y) && (clip is null || clip.Value.Contains(menu.pointer.X, menu.pointer.Y));
        float hover = menu.motion.Hover(label, hovered);
        var drawn = at with { Y = at.Y + (hovered && menu.pointerDown ? menu.scale : 0) };
        bool pressed = hovered && menu.pointerDown;
        Surface(drawn, primary ? "primary" : "button", selected || pressed, false, clip);
        if (hover > 0)
        {
            int first = menu.controls.Count;
            Surface(drawn, primary ? "primary" : "button", selected || pressed, true, clip);
            menu.motion.Opacity(first, hover);
        }
        var ink = primary && Meitou ? new Vector4(20 / 255f, 8 / 255f, 6 / 255f, 1) : System.Numerics.Vector4.Lerp(MainInk, LightInk, selected ? 1 : hover);
        var textAt = new UiRect(drawn.X + 10 * menu.scale, drawn.Y, drawn.Width - (20 + textInset) * menu.scale, drawn.Height);
        Text(label, textAt, primary ? 15 : 13, ink, "Center", clip);
        if (clip is null || at.Intersect(clip.Value).Height >= at.Height - 1)
            menu.hits.Add(new(at, action, Label: label));
    }

    internal void DrawEntries(float top)
    {
        var entries = menu.Entries();
        menu.maxScroll = Math.Max(0, entries.Sum(EntryHeight) * menu.scale - menu.viewport.Height);
        menu.motion.AdvanceScroll();
        float entryY = top - menu.scroll / menu.scale;
        foreach (var entry in entries)
        {
            float row = EntryHeight(entry);
            menu.motion.animationScope = entry.Label;
            if (entryY + row >= top && entryY <= 666) DrawEntry(entry, entryY);
            entryY += row;
        }
    }

    /// <summary>Measures with the same font resource and wrapped width used by the settings text controls.</summary>
    internal float TextHeight(string text, float width, float size)
    {
        if (text.Length == 0) return 0;
        int px = (int)MathF.Round(size * menu.scale);
        var key = (text, (int)width, (int)size, textViewportHeight, (int)MathF.Round(menu.scale * 1000), Loc.Language.Code);
        if (textHeights.TryGetValue(key, out float cached)) return cached;
        if (menu.player.SettingsPainter is not { } painter) return size * 1.25f;
        var pixels = painter.MeasurePixelText(text, MathF.Round(width * menu.scale), Math.Clamp(px, 8, 96), size * menu.scale / 16);
        float measured = pixels.Height / menu.scale;
        textHeights[key] = measured;
        return measured;
    }

    int TextLines(string text, float width, float size)
    {
        if (text.Length == 0) return 0;
        if (menu.player.SettingsPainter is null) return Math.Max(1, (int)MathF.Ceiling(text.Length / Math.Max(1, width / size)));
        float wrapped = TextHeight(text, width, size);
        float line = TextHeight("Mg", 0, size);
        return Math.Max(1, (int)MathF.Ceiling(wrapped / Math.Max(1, line)));
    }

    /// <summary>The hint's width: the column less the indent, and less the Reset link's room on a changed row.</summary>
    static float HintWidth(SettingsMenu.Entry entry) => 456 - Indent(entry) - (entry is { Changed: true, Reset: not null } ? 56 : 0);

    /// <summary>
    /// The game's own arrow (the floor selector's: a grey chevron on a dark plate, gold when highlighted, <c>Kenshi_FloorArrowUpSkin</c> / <c>DownSkin</c>), 21 × 14 units
    /// centred on (<paramref name="x"/>, <paramref name="y"/>) in pixels: down opens (a closed group, a list), up closes (an open group).
    /// </summary>
    void Arrow(float x, float y, bool down, bool lit, UiRect? clip = null)
    {
        float w = MathF.Round(21 * menu.scale), h = MathF.Round(14 * menu.scale);
        menu.controls.Add(new("settings-arrow", "Widget", new(MathF.Round(x - w / 2), MathF.Round(y - h / 2), w, h), "", "", down ? "Kenshi_FloorArrowDownSkin" : "Kenshi_FloorArrowUpSkin", true,
            new Dictionary<string, string> { ["State"] = lit ? "highlighted" : "normal" }, Vector4.Zero, Clip: clip, Pickable: false));
    }

    /// <summary>A button that opens a list: its text, and the list's arrow against its right edge.</summary>
    internal void DropDown(string label, UiRect at, Action open, UiRect? clip = null)
    {
        Button(label, at, open, clip: clip, textInset: 26);
        Arrow(at.X + at.Width - 18 * menu.scale, at.Y + at.Height / 2, true, at.Contains(menu.pointer.X, menu.pointer.Y), clip);
    }

    /// <summary>How far a Graphics row's text sits right of the column: the group chevron's room, and one step more inside a group.</summary>
    static float Indent(SettingsMenu.Entry entry) => entry.Raw is not null || entry.GroupId is not null ? 28 + entry.Depth * 24 : 0;

    /// <summary>A picture under an entry's row: 560 units wide, the luckyren Mode image's proportions, with 8 units above it.</summary>
    const float PictureWidth = 560, PictureHeight = 72, PictureGap = 8;

    float RowHeight(SettingsMenu.Entry entry)
    {
        float indent = Indent(entry);
        float label = TextHeight(Loc.Tr(entry.Label), 456 - indent, entry.Depth > 0 ? 13 : 14);
        if (entry.Hint.Length == 0) return Math.Max(36, label + 4);
        return Math.Max(36, 2 + label + 1 + TextHeight(Loc.Tr(entry.Hint), HintWidth(entry), 11) + 2);
    }

    internal float EntryHeight(SettingsMenu.Entry entry) => RowHeight(entry) + (entry.Picture is null ? 0 : PictureGap + PictureHeight);

    internal void DrawEntry(SettingsMenu.Entry entry, float y)
    {
        float row = RowHeight(entry), buttonY = y + (row - 26) / 2;
        Rect(R(212, y + EntryHeight(entry) - 1, 888, 1), MainInk with { W = 0.075f }, menu.viewport);
        if (entry.Picture is { } picture && menu.player.SettingsPainter is { } painter)
        {
            menu.art.EnsureLuckyrenImage(painter);
            if (painter.HasTexture(picture))
                menu.controls.Add(new("settings-picture", "ImageBox", R(212, y + row - 4, PictureWidth, PictureHeight), "", "", "", true,
                    new Dictionary<string, string> { ["ImageTexture"] = picture }, Vector4.Zero, Clip: menu.viewport, Pickable: false));
        }
        float indent = Indent(entry), labelSize = entry.Depth > 0 ? 13 : 14;
        string translatedLabel = Loc.Tr(entry.Label);
        float labelHeight = TextHeight(translatedLabel, 456 - indent, labelSize);
        bool labelWrap = TextLines(translatedLabel, 456 - indent, labelSize) > 1;
        // A feature's settings sit in a darker band with a brass edge under its header; an open header is lightly lit.
        if (entry.Depth > 0)
        {
            Rect(R(212, y, 888, EntryHeight(entry)), new(0, 0, 0, 0.28f), menu.viewport);
            Rect(R(214, y, 2, EntryHeight(entry)), Brass with { W = 0.55f }, menu.viewport);
        }
        else if (entry.Open == true) Rect(R(212, y, 888, row), MainInk with { W = 0.06f }, menu.viewport);
        if (entry is { Open: { } open, GroupId: { } group })
        {
            // A feature's header: the arrow, and a click anywhere on its text opens or closes its settings.
            var header = R(212, y, 456, row);
            if (header.Intersect(menu.viewport).Height >= header.Height - 1)
                menu.hits.Add(new(header, () => menu.grouping.ToggleGroup(group), Label: entry.Label));
            var mark = R(212, y + 2, 22, entry.Hint.Length > 0 ? 20 : 32);
            Arrow(mark.X + mark.Width / 2, mark.Y + mark.Height / 2, !open, header.Contains(menu.pointer.X, menu.pointer.Y), menu.viewport);
        }
        // A setting that differs from the preset: its name in brass and a Reset link back to the preset's value (a header's resets the feature).
        var nameInk = entry.Changed ? Brass : MainInk;
        Text(entry.Label, R(212 + indent, y + 2, 456 - indent, entry.Hint.Length > 0 ? labelHeight + 1 : row - 4), labelSize, nameInk, clip: menu.viewport, wrap: labelWrap);
        if (entry.Impact is { } impact)
            Text(impact, R(212, y + 2, 456, 20), 11, impact.StartsWith("High", StringComparison.Ordinal) ? Brass : impact.StartsWith("Medium", StringComparison.Ordinal) ? MainInk : MutedInk,
                "Right VCenter", menu.viewport);
        if (entry is { Changed: true, Reset: { } reset })
        {
            var link = R(612, y + 18, 56, 16);
            bool over = link.Contains(menu.pointer.X, menu.pointer.Y) && menu.viewport.Contains(menu.pointer.X, menu.pointer.Y);
            Text("Reset", link, 11, over ? LightInk : Brass, "Right VCenter", menu.viewport);
            Rect(new(link.X + link.Width - 30 * menu.scale, link.Y + link.Height - menu.scale, 30 * menu.scale, menu.scale), (over ? LightInk : Brass) with { W = 0.6f }, menu.viewport);
            if (link.Intersect(menu.viewport).Height >= link.Height - 1) menu.hits.Add(new(link, reset, Label: "Reset " + entry.Label));
        }
        if (entry.Hint.Length > 0)
        {
            float hintHeight = TextHeight(Loc.Tr(entry.Hint), HintWidth(entry), 11);
            Text(entry.Hint, R(212 + indent, y + 2 + labelHeight + 1, HintWidth(entry), hintHeight + 1), 11, entry.Warn ? Brass : MutedInk, "Left Top", menu.viewport, wrap: true);
        }
        if (entry.Toggle is { Remaster: true, Optional: false } remaster)
        {
            // A Faithful / Meitou switch: both sides as buttons, the active one marked.
            bool meitou = remaster.Get();
            if (remaster.OnOff) Segmented(R(860, y + (row - 22) / 2, 240, 22), meitou ? 1 : 0, i => remaster.Set(i == 1), "Faithful (off)", "Meitou (on)");
            else Segmented(R(920, y + (row - 22) / 2, 180, 22), meitou ? 1 : 0, i => remaster.Set(i == 1));
        }
        // A switch with its own words (Hear from: Character / Camera): one button showing the current side.
        else if (entry.Toggle is { Text: { } words } worded)
            Button(words(), R(976, buttonY, 124, 26), () => worded.Set(!worded.Get()), worded.Get(), clip: menu.viewport);
        // An optional effect (bloom, in neither look) or a plain setting: Off | On in the same control.
        else if (entry.Toggle is { } toggle)
            Segmented(R(920, y + (row - 22) / 2, 180, 22), toggle.Get() ? 1 : 0, i => toggle.Set(i == 1), "Off", "On");
        else if (entry is { Segmented: true, Choice: { } sides })
            Segmented(R(860, y + (row - 22) / 2, 240, 22), sides.Current(), sides.Choose, "All Faithful", "All Meitou");
        else if (entry.Choice is { } choice)
        {
            var options = choice.Options();
            int index = choice.Current();
            string current = Loc.Tr(index >= 0 && index < options.Count ? options[index] : "Unavailable");
            var at = R(688, buttonY, 412, 26);
            DropDown(current, at, () =>
            {
                menu.openChoice = choice; menu.choiceSelection = Math.Clamp(choice.Current(), 0, Math.Max(0, options.Count - 1));
                menu.choiceScroll = Math.Max(0, menu.choiceSelection - 3); choiceAnchor = at; menu.focus = -1;
            }, menu.viewport);
        }
        else if (entry.Slider is { } slider)
        {
            var valueAt = R(908, y, 192, 20);
            // By label: the rows (and their sliders) are built again each frame.
            bool editing = menu.editingNumber?.Label == slider.Label;
            if (editing)
            {
                menu.numberBounds = valueAt;
                Rect(valueAt, new(0.06f, 0.05f, 0.04f, 0.95f), menu.viewport);
                var textAt = valueAt with { X = valueAt.X + 6 * menu.scale, Width = valueAt.Width - 12 * menu.scale };
                menu.controls.Add(new("settings-number", "EditBox", textAt, menu.numberText, "", "", true,
                    new Dictionary<string, string>
                    {
                        ["Focused"] = "true", ["Numeric"] = "true", ["TextAlign"] = "Right VCenter",
                        ["FontPixels"] = ((int)MathF.Round(13 * menu.scale)).ToString(CultureInfo.InvariantCulture),
                        ["SelectionStart"] = Math.Min(menu.numberCaret, menu.numberAnchor).ToString(CultureInfo.InvariantCulture),
                        ["SelectionLength"] = Math.Abs(menu.numberCaret - menu.numberAnchor).ToString(CultureInfo.InvariantCulture),
                        ["CaretIndex"] = menu.numberCaret.ToString(CultureInfo.InvariantCulture),
                    }, Vector4.Zero, LightInk, Clip: menu.viewport, Pickable: false));
            }
            else
            {
                if (valueAt.Contains(menu.pointer.X, menu.pointer.Y))
                {
                    Rect(valueAt, MainInk with { W = 0.06f }, menu.viewport);
                    Rect(valueAt with { Y = valueAt.Y + valueAt.Height - menu.scale, Height = menu.scale }, MainInk with { W = 0.4f }, menu.viewport);
                }
                Text(slider.Display, R(688, y, 412, 20), 13, entry.Locked ? MutedInk : LightInk, "Right VCenter", menu.viewport);
            }
            var track = R(696, y + row - 9, 396, 3);
            Rect(track, new(0.10f, 0.08f, 0.07f, 1), menu.viewport);
            Rect(track with { Width = track.Width * slider.Fraction }, entry.Locked ? MutedInk with { W = 0.5f } : Brass with { W = 0.75f }, menu.viewport);
            if (slider.Faithful is { } faithful) RemasterMark(slider, faithful, track, FaithfulInk);
            if (slider.Meitou is { } meitou) RemasterMark(slider, meitou, track, Brass);
            if (slider.Faithful is { } f && slider.Meitou is { } m && entry.Depth == 0)   // inside a feature its header has the switch
            {
                // The quick switch: jump to the game's value or Meitou's; the one the slider is at is marked.
                float now = slider.Get(), near = 0.002f * (slider.Max - slider.Min);
                int at = MathF.Abs(now - m) <= near ? 1 : MathF.Abs(now - f) <= near ? 0 : -1;
                Segmented(R(696, y + 2, 140, 18), at, i => slider.Set(i == 1 ? m : f));
            }
            var knob = new UiRect(track.X + slider.Fraction * track.Width - 3 * menu.scale, track.Y - 4 * menu.scale, 6 * menu.scale, 11 * menu.scale);
            Rect(knob, entry.Locked ? MutedInk : LightInk, menu.viewport);
            // A locked slider (a mode's value, SettingModes) shows its value but takes no drag, key or typed value.
            if (entry.Locked) return;
            var hit = R(688, y + row - 16, 412, 16);
            if (hit.Intersect(menu.viewport).Height >= hit.Height - 1) menu.hits.Add(new(hit, () => { }, slider, track, entry.Label));
            if (valueAt.Intersect(menu.viewport).Height >= valueAt.Height - 1) menu.hits.Add(new(valueAt, () => menu.BeginNumber(slider, valueAt)));
        }
        else if (entry.Binding is { } binding)
        {
            string label = string.Join(" / ", menu.bindings.Get(binding));
            Button(label.Length > 0 ? label : "Unbound", R(688, buttonY, 412, 26), () =>
            {
                menu.rebinding = binding;
                menu.notice = "";
            }, clip: menu.viewport);
        }
        else if (entry.Run is { } run) Button("Clear cache", R(944, buttonY, 156, 26), run, clip: menu.viewport);
    }

    static readonly Vector4 FaithfulInk = new(0.62f, 0.66f, 0.70f, 1);

    /// <summary>A notch under a slider's track at <paramref name="value"/>, in the colour of its side of the Faithful | Meitou switch.</summary>
    void RemasterMark(Meitou.Game.Rendering.Frame.Slider slider, float value, UiRect track, Vector4 ink)
    {
        float x = MathF.Round(track.X + slider.FractionOf(value) * track.Width), w = MathF.Max(2, MathF.Round(2 * menu.scale));
        Rect(new(x - w / 2, track.Y + track.Height + menu.scale, w, MathF.Round(4 * menu.scale)), ink with { W = 0.9f }, menu.viewport);
    }

    /// <summary>
    /// A compact Faithful | Meitou switch: one frame with two halves, the chosen half (<paramref name="selected"/>: 0 Faithful, 1 Meitou, -1 neither) filled in its
    /// colour (the colours of the slider notches); a click on a half calls <paramref name="choose"/>.
    /// </summary>
    internal void Segmented(UiRect at, int selected, Action<int> choose, string faithful = "Faithful", string meitou = "Meitou")
    {
        float line = MathF.Max(1, MathF.Round(menu.scale));
        Rect(at, new(0.07f, 0.06f, 0.05f, 0.9f), menu.viewport);
        string[] names = [faithful, meitou];
        Vector4[] inks = [FaithfulInk, Brass];
        float half = MathF.Round(at.Width / 2);
        for (int i = 0; i < 2; i++)
        {
            int index = i;
            var part = new UiRect(at.X + i * half, at.Y, i == 0 ? half : at.Width - half, at.Height);
            bool hovered = part.Contains(menu.pointer.X, menu.pointer.Y) && menu.viewport.Contains(menu.pointer.X, menu.pointer.Y);
            float hover = menu.motion.Hover(names[i], hovered);
            if (selected == i) Rect(part, inks[i] with { W = 0.28f }, menu.viewport);
            else if (hover > 0) Rect(part, MainInk with { W = 0.08f * hover }, menu.viewport);
            if (selected == i) Rect(part with { Y = part.Y + part.Height - 2 * line, Height = 2 * line }, inks[i] with { W = 0.9f }, menu.viewport);
            var ink = selected == i ? LightInk : Vector4.Lerp(MutedInk, MainInk, hover);
            Text(names[i], part, 12, ink, "Center", menu.viewport);
            if (part.Intersect(menu.viewport).Height >= part.Height - 1) menu.hits.Add(new(part, () => choose(index), Label: names[i]));
        }
        // The frame and the divider.
        var edge = MainInk with { W = 0.22f };
        Rect(at with { Height = line }, edge, menu.viewport);
        Rect(at with { Y = at.Y + at.Height - line, Height = line }, edge, menu.viewport);
        Rect(at with { Width = line }, edge, menu.viewport);
        Rect(at with { X = at.X + at.Width - line, Width = line }, edge, menu.viewport);
        Rect(new(at.X + half, at.Y + 3 * menu.scale, line, at.Height - 6 * menu.scale), edge, menu.viewport);
    }

    internal UiRect choiceAnchor;
    internal SettingsMenu.Choice? animatedChoice;
    float choiceProgress;

    internal void DrawChoice(SettingsMenu.Choice choice)
    {
        if (!ReferenceEquals(animatedChoice, choice)) { animatedChoice = choice; choiceProgress = 0; }
        choiceProgress = Math.Min(1, choiceProgress + menu.motion.frameDelta / 0.1f);
        int first = menu.controls.Count;
        menu.hits.Clear();
        var options = choice.Options();
        int count = Math.Min(6, options.Count);
        menu.choiceScroll = Math.Clamp(menu.choiceScroll, 0, Math.Max(0, options.Count - count));
        float h = (count * 28 + 12) * menu.scale;
        float y = Math.Min(choiceAnchor.Y + choiceAnchor.Height + 6 * menu.scale, menu.bounds.Y + menu.bounds.Height - 24 * menu.scale - h);
        var at = new UiRect(choiceAnchor.X, y, choiceAnchor.Width, h);
        Surface(at, "window");
        for (int i = 0; i < count; i++)
        {
            int index = menu.choiceScroll + i;
            Button(options[index], new(at.X + 6 * menu.scale, at.Y + (6 + i * 28) * menu.scale, at.Width - 12 * menu.scale, 24 * menu.scale),
                () => { choice.Choose(index); menu.openChoice = null; menu.focus = -1; }, index == menu.choiceSelection);
        }
        menu.motion.Opacity(first, MenuMotion.Ease(choiceProgress));
    }

    internal void DrawMessage(string title, string body, Action cancel, Action? confirm = null)
    {
        menu.hits.Clear();
        Rect(menu.bounds, new(8 / 255f, 6 / 255f, 5 / 255f, 0.8f));
        var at = R(290, 250, 540, 220);
        Surface(at, "window");
        Text(title, R(316, 264, 488, 30), 20, LightInk);
        menu.controls.Add(new("settings-message", "TextBox", R(316, 306, 488, 94), body, "", "", true,
            new Dictionary<string, string> { ["WordWrap"] = "true", ["FontPixels"] = ((int)MathF.Round(14 * menu.scale)).ToString(System.Globalization.CultureInfo.InvariantCulture) }, Vector4.Zero, MainInk, Pickable: false, TextScale: menu.scale));
        Button("Cancel", R(500, 420, 138, 32), cancel);
        if (confirm is not null) Button("Confirm", R(650, 420, 154, 32), confirm, primary: true);
    }
}
