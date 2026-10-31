using System.Numerics;
using System.Globalization;
using Meitou.Data.Ui;
using Meitou.Rendering;

namespace Meitou.Game;

sealed partial class SettingsMenu
{
    static readonly Vector4 MainInk = new(175 / 255f, 166 / 255f, 139 / 255f, 1);
    static readonly Vector4 LightInk = new(217 / 255f, 206 / 255f, 184 / 255f, 1);
    static readonly Vector4 MutedInk = new(0.52f, 0.49f, 0.43f, 1);
    static readonly Vector4 Brass = new(196 / 255f, 160 / 255f, 98 / 255f, 1);
    bool Meitou => player.UiMode == UiMode.Meitou;
    int textViewportHeight;
    readonly Dictionary<(string Text, int Width, int Size, int Height, int Scale, string Language), float> textHeights = [];

    UiRect R(float x, float y, float w, float h) => new(MathF.Round(bounds.X + x * scale), bounds.Y + y * scale, MathF.Round(w * scale), MathF.Round(h * scale));

    void Rect(UiRect at, Vector4 colour, UiRect? clip = null) => controls.Add(new("settings-fill", "Panel", at, "", "", "", true, new Dictionary<string, string>(), colour, Clip: clip, Pickable: false));

    void Text(string caption, UiRect at, float size = 16, Vector4? colour = null, string align = "Left VCenter", UiRect? clip = null, bool wrap = false)
    {
        caption = Loc.Tr(caption);
        var properties = new Dictionary<string, string> { ["TextAlign"] = align, ["FontName"] = "Kenshi_StandardFont", ["FontPixels"] = ((int)MathF.Round(size * scale)).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (wrap) properties["WordWrap"] = "true";
        controls.Add(new("settings-text", "TextBox", at, caption, "", "", true, properties, Vector4.Zero, colour ?? MainInk, Clip: clip, Pickable: false, TextScale: size * scale / 16));
    }


    void Surface(UiRect at, string kind, bool selected = false, bool hover = false, UiRect? clip = null)
    {
        if (Meitou)
        {
            string texture = kind switch { "window" => "window", "primary" or "paper" => hover ? "paper-hover" : "paper", _ => selected ? "button-selected" : hover ? "button-hover" : "button" };
            float margin = kind == "window" ? 16 * scale : 0;
            bool window = kind == "window";
            var properties = new Dictionary<string, string>
            {
                ["ImageTexture"] = SettingsMenuArt.SurfacePrefix + texture,
                ["ImageBorder"] = window ? SettingsMenuArt.WindowBorder.ToString(System.Globalization.CultureInfo.InvariantCulture) : "12",
                ["ImageBorderScale"] = (window ? scale / SettingsMenuArt.WindowDensity : scale).ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            controls.Add(new("settings-surface", "ImageBox", new(at.X - margin, at.Y - margin, at.Width + margin * 2, at.Height + margin * 2), "", "", "", true,
                properties, Vector4.Zero, Clip: clip, Pickable: false));
        }
        else
            controls.Add(new("settings-surface", "Panel", at, "", "", kind == "window" ? "Kenshi_GenericWindowSkin" : kind == "paper" ? "Kenshi_GenericTextBoxSkin" : "Kenshi_Button1Skin", true,
                new Dictionary<string, string> { ["State"] = selected ? "pushed" : hover ? "highlighted" : "normal" },
                kind == "window" ? new(0.12f, 0.11f, 0.09f, 1) : Vector4.Zero, Clip: clip, Pickable: false));
    }

    void Button(string label, UiRect at, Action action, bool selected = false, bool primary = false, UiRect? clip = null, float textInset = 0)
    {
        bool hovered = at.Contains(pointer.X, pointer.Y) && (clip is null || clip.Value.Contains(pointer.X, pointer.Y));
        float hover = Hover(label, hovered);
        var drawn = at with { Y = at.Y + (hovered && pointerDown ? scale : 0) };
        bool pressed = hovered && pointerDown;
        Surface(drawn, primary ? "primary" : "button", selected || pressed, false, clip);
        if (hover > 0)
        {
            int first = controls.Count;
            Surface(drawn, primary ? "primary" : "button", selected || pressed, true, clip);
            Opacity(first, hover);
        }
        var ink = primary && Meitou ? new Vector4(20 / 255f, 8 / 255f, 6 / 255f, 1) : System.Numerics.Vector4.Lerp(MainInk, LightInk, selected ? 1 : hover);
        Text(label, new(drawn.X + 10 * scale, drawn.Y, drawn.Width - (20 + textInset) * scale, drawn.Height), primary ? 15 : 13, ink, "Center", clip);
        if (clip is null || at.Intersect(clip.Value).Height >= at.Height - 1)
            hits.Add(new(at, action));
    }

    void Navigate(int next)
    {
        page = next; scroll = scrollTarget = 0; contentProgress = 0; focus = -1; openChoice = null; rebinding = null; notice = "";
    }

    public void Draw(int width, int height, DebugOverlay.OverlayPass? pass = null)
    {
        if (!Visible || player.SettingsPainter is not { } painter) return;
        AdvanceMotion();
        art.Ensure(painter, Meitou, player.UiAtlas);
        scale = Math.Max(0.35f, Math.Min(1.25f, Math.Min(height / 900f, width / 1280f)));
        textViewportHeight = height;
        bounds = new(MathF.Round((width - 1120 * scale) / 2), MathF.Round((height - 720 * scale) / 2), MathF.Round(1120 * scale), MathF.Round(720 * scale));
        bounds = bounds with { Y = bounds.Y + (1 - Ease(menuProgress)) * 8 * scale };
        controls.Clear(); hits.Clear();
        animationScope = "chrome";
        Rect(new(0, 0, width, height), new(8 / 255f, 6 / 255f, 5 / 255f, 0.64f));
        Surface(bounds, "window");
        if (!Meitou)
            controls.Add(new("settings-header", "Panel", R(6, 4, 1108, 36), "", "", "Kenshi_GenericWindowHeaderSkin", true, new Dictionary<string, string>(), Vector4.Zero, Pickable: false));
        Text("Settings", R(20, 4, 650, 32), 22, LightInk);
        var close = R(1069, 6, 31, 32);
        controls.Add(new("settings-close", "Button", close, "", "settings:close", "Kenshi_CloseButtonSkin", true,
            new Dictionary<string, string>(), Vector4.Zero));
        hits.Add(new(close, () => Visible = false));
        Rect(R(20, 44, 1080, 1), MainInk with { W = 0.12f });
        Rect(R(192, 56, 1, 610), MainInk with { W = 0.12f });

        for (int i = 0; i < Pages.Length; i++)
        {
            int index = i;
            Button(Pages[i], R(20, 58 + i * 38, 156, 32), () => Navigate(index), page == i);
        }
        Text(Meitou ? "MEITOU" : "FAITHFUL", R(28, 610, 148, 18), 11, MutedInk);
        Text(BuildVersion.Tag, R(28, 630, 148, 16), 11, MutedInk);
        Text(BuildVersion.Commit, R(28, 646, 148, 16), 11, MutedInk);
        int contentStart = controls.Count;
        Surface(R(212, 56, 888, 34), "paper");
        Text(Pages[page], R(224, 56, 864, 34), 18, new(20 / 255f, 8 / 255f, 6 / 255f, 1));
        float top = 98;
        if (page == 1)
        {
            // The Look and Quality rows (SettingsMenu.Looks.cs), then the tabs, one per subject, across the page.
            DrawLookBar();
            const float tabGap = 6;
            float tabWidth = (888 - tabGap * (GraphicsPages.Length - 1)) / GraphicsPages.Length;
            for (int i = 0; i < GraphicsPages.Length; i++)
            {
                int index = i;
                Button(GraphicsPages[i], R(212 + i * (tabWidth + tabGap), 174, tabWidth, 30), () => { graphicsPage = index; scroll = scrollTarget = 0; contentProgress = 0; focus = -1; }, graphicsPage == i);
            }
            top = 212;
        }
        viewport = R(212, top, 888, 666 - top);
        var entries = Entries();
        maxScroll = Math.Max(0, entries.Sum(EntryHeight) * scale - viewport.Height);
        AdvanceScroll();
        float entryY = top - scroll / scale;
        foreach (var entry in entries)
        {
            float row = EntryHeight(entry);
            animationScope = entry.Label;
            if (entryY + row >= top && entryY <= 666) DrawEntry(entry, entryY);
            entryY += row;
        }
        animationScope = "chrome";
        if (maxScroll > 0)
        {
            float track = viewport.Height, thumb = Math.Max(24 * scale, track * track / (track + maxScroll));
            Rect(new(viewport.X + viewport.Width + 12 * scale, viewport.Y, 3 * scale, track), MainInk with { W = 0.1f });
            Rect(new(viewport.X + viewport.Width + 12 * scale, viewport.Y + (track - thumb) * scroll / maxScroll, 3 * scale, thumb), Brass with { W = 0.75f });
        }
        Opacity(contentStart, Ease(contentProgress));
        Rect(R(20, 676, 1080, 1), MainInk with { W = 0.12f });
        Button("Restore defaults", R(20, 686, 150, 28), () => { resetPending = true; openChoice = null; focus = -1; });
        if (notice.Length > 0) Text(notice, R(188, 686, 744, 28), 11, Brass);
        Button("Save", R(960, 684, 140, 32), () => Visible = false, primary: true);
        if (openChoice is { } choice) DrawChoice(choice);
        else animatedChoice = null;
        if (naming) DrawNameDialog();
        if (rebinding is { } binding) DrawMessage("Assign a control", notice.Length > 0 ? notice : $"{ActionLabel(binding)}: press a key or mouse button.\nDelete clears the binding. Esc cancels.", () => rebinding = null);
        if (resetPending || pendingAction is not null)
            DrawMessage(resetPending ? "Restore defaults?" : confirmTitle, resetPending ? "Restore all preferences, controls and the look to their defaults?" : confirmation,
                () => { resetPending = false; pendingAction = null; }, () =>
                {
                    if (resetPending) Reset();
                    else if (pendingAction is { } action) { notice = action(); pendingAction = null; }
                });
        Opacity(0, Ease(menuProgress));
        painter.Draw(controls, width, height, pointer, null, pass: pass);
    }

    /// <summary>Measures with the same font resource and wrapped width used by the settings text controls.</summary>
    float TextHeight(string text, float width, float size)
    {
        if (text.Length == 0) return 0;
        int px = (int)MathF.Round(size * scale);
        var key = (text, (int)width, (int)size, textViewportHeight, (int)MathF.Round(scale * 1000), Loc.Language.Code);
        if (textHeights.TryGetValue(key, out float cached)) return cached;
        if (player.SettingsPainter is not { } painter) return size * 1.25f;
        float measured = painter.MeasurePixelText(text, MathF.Round(width * scale), Math.Clamp(px, 8, 96), size * scale / 16).Height / scale;
        textHeights[key] = measured;
        return measured;
    }

    int TextLines(string text, float width, float size)
    {
        if (text.Length == 0) return 0;
        if (player.SettingsPainter is null) return Math.Max(1, (int)MathF.Ceiling(text.Length / Math.Max(1, width / size)));
        float wrapped = TextHeight(text, width, size);
        float line = TextHeight("Mg", 0, size);
        return Math.Max(1, (int)MathF.Ceiling(wrapped / Math.Max(1, line)));
    }

    /// <summary>The hint's width: the column less the indent, and less the Reset link's room on a changed row.</summary>
    static float HintWidth(Entry entry) => 456 - Indent(entry) - (entry is { Changed: true, Reset: not null } ? 56 : 0);

    /// <summary>
    /// The game's own arrow (the floor selector's: a grey chevron on a dark plate, gold when highlighted, <c>Kenshi_FloorArrowUpSkin</c> / <c>DownSkin</c>), 21 × 14 units
    /// centred on (<paramref name="x"/>, <paramref name="y"/>) in pixels: down opens (a closed group, a list), up closes (an open group).
    /// </summary>
    void Arrow(float x, float y, bool down, bool lit, UiRect? clip = null)
    {
        float w = MathF.Round(21 * scale), h = MathF.Round(14 * scale);
        controls.Add(new("settings-arrow", "Widget", new(MathF.Round(x - w / 2), MathF.Round(y - h / 2), w, h), "", "", down ? "Kenshi_FloorArrowDownSkin" : "Kenshi_FloorArrowUpSkin", true,
            new Dictionary<string, string> { ["State"] = lit ? "highlighted" : "normal" }, Vector4.Zero, Clip: clip, Pickable: false));
    }

    /// <summary>A button that opens a list: its text, and the list's arrow against its right edge.</summary>
    void DropDown(string label, UiRect at, Action open, UiRect? clip = null)
    {
        Button(label, at, open, clip: clip, textInset: 26);
        Arrow(at.X + at.Width - 18 * scale, at.Y + at.Height / 2, true, at.Contains(pointer.X, pointer.Y), clip);
    }

    /// <summary>How far a Graphics row's text sits right of the column: the group chevron's room, and one step more inside a group.</summary>
    static float Indent(Entry entry) => entry.Raw is not null || entry.GroupId is not null ? 28 + entry.Depth * 24 : 0;

    /// <summary>A picture under an entry's row: 560 units wide, the luckyren Mode image's proportions, with 8 units above it.</summary>
    const float PictureWidth = 560, PictureHeight = 72, PictureGap = 8;

    float RowHeight(Entry entry)
    {
        float indent = Indent(entry);
        float label = TextHeight(Loc.Tr(entry.Label), 456 - indent, entry.Depth > 0 ? 13 : 14);
        if (entry.Hint.Length == 0) return Math.Max(36, label + 4);
        return Math.Max(36, 2 + label + 1 + TextHeight(Loc.Tr(entry.Hint), HintWidth(entry), 11) + 2);
    }

    float EntryHeight(Entry entry) => RowHeight(entry) + (entry.Picture is null ? 0 : PictureGap + PictureHeight);

    void DrawEntry(Entry entry, float y)
    {
        float row = RowHeight(entry), buttonY = y + (row - 26) / 2;
        Rect(R(212, y + EntryHeight(entry) - 1, 888, 1), MainInk with { W = 0.075f }, viewport);
        if (entry.Picture is { } picture && player.SettingsPainter is { } painter)
        {
            art.EnsureLuckyrenImage(painter);
            if (painter.HasTexture(picture))
                controls.Add(new("settings-picture", "ImageBox", R(212, y + row - 4, PictureWidth, PictureHeight), "", "", "", true,
                    new Dictionary<string, string> { ["ImageTexture"] = picture }, Vector4.Zero, Clip: viewport, Pickable: false));
        }
        float indent = Indent(entry), labelSize = entry.Depth > 0 ? 13 : 14;
        string translatedLabel = Loc.Tr(entry.Label);
        float labelHeight = TextHeight(translatedLabel, 456 - indent, labelSize);
        bool labelWrap = TextLines(translatedLabel, 456 - indent, labelSize) > 1;
        // A feature's settings sit in a darker band with a brass edge under its header; an open header is lightly lit.
        if (entry.Depth > 0)
        {
            Rect(R(212, y, 888, EntryHeight(entry)), new(0, 0, 0, 0.28f), viewport);
            Rect(R(214, y, 2, EntryHeight(entry)), Brass with { W = 0.55f }, viewport);
        }
        else if (entry.Open == true) Rect(R(212, y, 888, row), MainInk with { W = 0.06f }, viewport);
        if (entry is { Open: { } open, GroupId: { } group })
        {
            // A feature's header: the arrow, and a click anywhere on its text opens or closes its settings.
            var header = R(212, y, 456, row);
            if (header.Intersect(viewport).Height >= header.Height - 1) hits.Add(new(header, () => ToggleGroup(group)));
            var mark = R(212, y + 2, 22, entry.Hint.Length > 0 ? 20 : 32);
            Arrow(mark.X + mark.Width / 2, mark.Y + mark.Height / 2, !open, header.Contains(pointer.X, pointer.Y), viewport);
        }
        // A setting that differs from the preset: its name in brass and a Reset link back to the preset's value (a header's resets the feature).
        var nameInk = entry.Changed ? Brass : MainInk;
        Text(entry.Label, R(212 + indent, y + 2, 456 - indent, entry.Hint.Length > 0 ? labelHeight + 1 : row - 4), labelSize, nameInk, clip: viewport, wrap: labelWrap);
        if (entry.Impact is { } impact)
            Text(impact, R(212, y + 2, 456, 20), 11, impact.StartsWith("High", StringComparison.Ordinal) ? Brass : impact.StartsWith("Medium", StringComparison.Ordinal) ? MainInk : MutedInk,
                "Right VCenter", viewport);
        if (entry is { Changed: true, Reset: { } reset })
        {
            var link = R(612, y + 18, 56, 16);
            bool over = link.Contains(pointer.X, pointer.Y) && viewport.Contains(pointer.X, pointer.Y);
            Text("Reset", link, 11, over ? LightInk : Brass, "Right VCenter", viewport);
            Rect(new(link.X + link.Width - 30 * scale, link.Y + link.Height - scale, 30 * scale, scale), (over ? LightInk : Brass) with { W = 0.6f }, viewport);
            if (link.Intersect(viewport).Height >= link.Height - 1) hits.Add(new(link, reset));
        }
        if (entry.Hint.Length > 0)
        {
            float hintHeight = TextHeight(Loc.Tr(entry.Hint), HintWidth(entry), 11);
            Text(entry.Hint, R(212 + indent, y + 2 + labelHeight + 1, HintWidth(entry), hintHeight + 1), 11, entry.Warn ? Brass : MutedInk, "Left Top", viewport, wrap: true);
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
            Button(words(), R(976, buttonY, 124, 26), () => worded.Set(!worded.Get()), worded.Get(), clip: viewport);
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
                openChoice = choice; choiceSelection = Math.Clamp(choice.Current(), 0, Math.Max(0, options.Count - 1));
                choiceScroll = Math.Max(0, choiceSelection - 3); choiceAnchor = at; focus = -1;
            }, viewport);
        }
        else if (entry.Slider is { } slider)
        {
            var valueAt = R(908, y, 192, 20);
            bool editing = ReferenceEquals(editingNumber, slider);
            if (editing)
            {
                numberBounds = valueAt;
                Rect(valueAt, new(0.06f, 0.05f, 0.04f, 0.95f), viewport);
                var textAt = valueAt with { X = valueAt.X + 6 * scale, Width = valueAt.Width - 12 * scale };
                controls.Add(new("settings-number", "EditBox", textAt, numberText, "", "", true,
                    new Dictionary<string, string>
                    {
                        ["Focused"] = "true", ["Numeric"] = "true", ["TextAlign"] = "Right VCenter",
                        ["FontPixels"] = ((int)MathF.Round(13 * scale)).ToString(CultureInfo.InvariantCulture),
                        ["SelectionStart"] = Math.Min(numberCaret, numberAnchor).ToString(CultureInfo.InvariantCulture),
                        ["SelectionLength"] = Math.Abs(numberCaret - numberAnchor).ToString(CultureInfo.InvariantCulture),
                        ["CaretIndex"] = numberCaret.ToString(CultureInfo.InvariantCulture),
                    }, Vector4.Zero, LightInk, Clip: viewport, Pickable: false));
            }
            else
            {
                if (valueAt.Contains(pointer.X, pointer.Y))
                {
                    Rect(valueAt, MainInk with { W = 0.06f }, viewport);
                    Rect(valueAt with { Y = valueAt.Y + valueAt.Height - scale, Height = scale }, MainInk with { W = 0.4f }, viewport);
                }
                Text(slider.Display, R(688, y, 412, 20), 13, LightInk, "Right VCenter", viewport);
            }
            var track = R(696, y + row - 9, 396, 3);
            Rect(track, new(0.10f, 0.08f, 0.07f, 1), viewport);
            Rect(track with { Width = track.Width * slider.Fraction }, Brass with { W = 0.75f }, viewport);
            if (slider.Faithful is { } faithful) RemasterMark(slider, faithful, track, FaithfulInk);
            if (slider.Meitou is { } meitou) RemasterMark(slider, meitou, track, Brass);
            if (slider.Faithful is { } f && slider.Meitou is { } m && entry.Depth == 0)   // inside a feature its header has the switch
            {
                // The quick switch: jump to the game's value or Meitou's; the one the slider is at is marked.
                float now = slider.Get(), near = 0.002f * (slider.Max - slider.Min);
                int at = MathF.Abs(now - m) <= near ? 1 : MathF.Abs(now - f) <= near ? 0 : -1;
                Segmented(R(696, y + 2, 140, 18), at, i => slider.Set(i == 1 ? m : f));
            }
            var knob = new UiRect(track.X + slider.Fraction * track.Width - 3 * scale, track.Y - 4 * scale, 6 * scale, 11 * scale);
            Rect(knob, LightInk, viewport);
            var hit = R(688, y + row - 16, 412, 16);
            if (hit.Intersect(viewport).Height >= hit.Height - 1) hits.Add(new(hit, () => { }, slider, track));
            if (valueAt.Intersect(viewport).Height >= valueAt.Height - 1) hits.Add(new(valueAt, () => BeginNumber(slider, valueAt)));
        }
        else if (entry.Binding is { } binding)
        {
            string label = string.Join(" / ", bindings.Get(binding));
            Button(label.Length > 0 ? label : "Unbound", R(688, buttonY, 412, 26), () => { rebinding = binding; notice = ""; }, clip: viewport);
        }
        else if (entry.Run is { } run) Button("Clear cache", R(944, buttonY, 156, 26), run, clip: viewport);
    }

    static readonly Vector4 FaithfulInk = new(0.62f, 0.66f, 0.70f, 1);

    /// <summary>A notch under a slider's track at <paramref name="value"/>, in the colour of its side of the Faithful | Meitou switch.</summary>
    void RemasterMark(Meitou.Rendering.Slider slider, float value, UiRect track, Vector4 ink)
    {
        float x = MathF.Round(track.X + slider.FractionOf(value) * track.Width), w = MathF.Max(2, MathF.Round(2 * scale));
        Rect(new(x - w / 2, track.Y + track.Height + scale, w, MathF.Round(4 * scale)), ink with { W = 0.9f }, viewport);
    }

    /// <summary>
    /// A compact Faithful | Meitou switch: one frame with two halves, the chosen half (<paramref name="selected"/>: 0 Faithful, 1 Meitou, -1 neither) filled in its
    /// colour (the colours of the slider notches); a click on a half calls <paramref name="choose"/>.
    /// </summary>
    void Segmented(UiRect at, int selected, Action<int> choose, string faithful = "Faithful", string meitou = "Meitou")
    {
        float line = MathF.Max(1, MathF.Round(scale));
        Rect(at, new(0.07f, 0.06f, 0.05f, 0.9f), viewport);
        string[] names = [faithful, meitou];
        Vector4[] inks = [FaithfulInk, Brass];
        float half = MathF.Round(at.Width / 2);
        for (int i = 0; i < 2; i++)
        {
            int index = i;
            var part = new UiRect(at.X + i * half, at.Y, i == 0 ? half : at.Width - half, at.Height);
            bool hovered = part.Contains(pointer.X, pointer.Y) && viewport.Contains(pointer.X, pointer.Y);
            float hover = Hover(names[i], hovered);
            if (selected == i) Rect(part, inks[i] with { W = 0.28f }, viewport);
            else if (hover > 0) Rect(part, MainInk with { W = 0.08f * hover }, viewport);
            if (selected == i) Rect(part with { Y = part.Y + part.Height - 2 * line, Height = 2 * line }, inks[i] with { W = 0.9f }, viewport);
            var ink = selected == i ? LightInk : Vector4.Lerp(MutedInk, MainInk, hover);
            Text(names[i], part, 12, ink, "Center", viewport);
            if (part.Intersect(viewport).Height >= part.Height - 1) hits.Add(new(part, () => choose(index)));
        }
        // The frame and the divider.
        var edge = MainInk with { W = 0.22f };
        Rect(at with { Height = line }, edge, viewport);
        Rect(at with { Y = at.Y + at.Height - line, Height = line }, edge, viewport);
        Rect(at with { Width = line }, edge, viewport);
        Rect(at with { X = at.X + at.Width - line, Width = line }, edge, viewport);
        Rect(new(at.X + half, at.Y + 3 * scale, line, at.Height - 6 * scale), edge, viewport);
    }

    UiRect choiceAnchor;
    void DrawChoice(Choice choice)
    {
        if (!ReferenceEquals(animatedChoice, choice)) { animatedChoice = choice; choiceProgress = 0; }
        choiceProgress = Math.Min(1, choiceProgress + frameDelta / 0.1f);
        int first = controls.Count;
        hits.Clear();
        var options = choice.Options();
        int count = Math.Min(6, options.Count);
        choiceScroll = Math.Clamp(choiceScroll, 0, Math.Max(0, options.Count - count));
        float h = (count * 28 + 12) * scale;
        float y = Math.Min(choiceAnchor.Y + choiceAnchor.Height + 6 * scale, bounds.Y + bounds.Height - 24 * scale - h);
        var at = new UiRect(choiceAnchor.X, y, choiceAnchor.Width, h);
        Surface(at, "window");
        for (int i = 0; i < count; i++)
        {
            int index = choiceScroll + i;
            Button(options[index], new(at.X + 6 * scale, at.Y + (6 + i * 28) * scale, at.Width - 12 * scale, 24 * scale),
                () => { choice.Choose(index); openChoice = null; focus = -1; }, index == choiceSelection);
        }
        Opacity(first, Ease(choiceProgress));
    }

    void DrawMessage(string title, string body, Action cancel, Action? confirm = null)
    {
        hits.Clear();
        Rect(bounds, new(8 / 255f, 6 / 255f, 5 / 255f, 0.8f));
        var at = R(290, 250, 540, 220);
        Surface(at, "window");
        Text(title, R(316, 264, 488, 30), 20, LightInk);
        controls.Add(new("settings-message", "TextBox", R(316, 306, 488, 94), body, "", "", true,
            new Dictionary<string, string> { ["WordWrap"] = "true", ["FontPixels"] = ((int)MathF.Round(14 * scale)).ToString(System.Globalization.CultureInfo.InvariantCulture) }, Vector4.Zero, MainInk, Pickable: false, TextScale: scale));
        Button("Cancel", R(500, 420, 138, 32), cancel);
        if (confirm is not null) Button("Confirm", R(650, 420, 154, 32), confirm, primary: true);
    }
}
