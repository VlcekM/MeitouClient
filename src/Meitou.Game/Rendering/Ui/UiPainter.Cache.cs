using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.Ui;
using Meitou.Game.Rendering.Overlays;

namespace Meitou.Game.Rendering.Ui;

/// <summary>
/// Painted controls kept across frames: what a control added to the overlay pass (its quads and glyphs, their textures and clips), its opaque area
/// and edit boxes, replayed while a control of the same content is drawn in the same hover and pushed state. Matched by content, not by object: the
/// interface arranges new controls whenever the world ticks. A control is recorded the second frame it is seen (most controls that live one frame, as
/// the floaters' moving ones, are never copied), and never while it changes on its own (a blinking caret, an icon still loading or the renderer's).
/// Every record goes when a texture or font atlas the painter draws from changes, the size or the mode changes.
/// </summary>
public sealed partial class UiPainter
{
    /// <summary>
    /// Frames a record is kept without being drawn.
    /// </summary>
    const int PaintRecordLife = 120;

    /// <summary>
    /// The controls seen, by content and state, with what they painted.
    /// </summary>
    readonly Dictionary<PaintKey, PaintRecord> _painted = new(PaintKeyComparer.Instance);

    /// <summary>
    /// What the records were painted against: the viewport size, the smallest text size, the mode, the texture version and the font atlases' changes.
    /// </summary>
    (int Width, int Height, float MinimumPixels, UiMode Mode, int Textures, int Fonts) _paintedFor;

    /// <summary>
    /// The frames <see cref="BeginPaintFrame"/> began, by which <see cref="PaintRecord.Seen"/> is told.
    /// </summary>
    long _paintFrame;

    /// <summary>
    /// Raised whenever a texture the painter draws from is loaded, set, replaced or freed.
    /// </summary>
    int _textureVersion;

    /// <summary>
    /// Set while a control is painted when its picture changes on its own (it is never recorded).
    /// </summary>
    bool _paintVolatile;

    /// <summary>
    /// The adds a control's recording collects, copied into its record.
    /// </summary>
    readonly List<DebugOverlay.OverlayPass.Recorded> _recordAdds = [];

    /// <summary>
    /// The floats a control's recording collects, copied into its record.
    /// </summary>
    readonly List<float> _recordFloats = [];

    /// <summary>
    /// A control and its state: bit 0 hovered, bit 1 pushed.
    /// </summary>
    readonly record struct PaintKey(UiControl Control, byte State);

    /// <summary>
    /// What one control painted, replayed instead of painting it again.
    /// </summary>
    sealed class PaintRecord
    {
        /// <summary>
        /// The frame (<see cref="_paintFrame"/>) the control was last seen in.
        /// </summary>
        public long Seen;

        /// <summary>
        /// Whether the record holds the control's painting (replayed), and whether the control changes on its own (painted every frame, never
        /// recorded).
        /// </summary>
        public bool Ready, Volatile;

        /// <summary>
        /// The recorded vertex floats.
        /// </summary>
        public float[] Floats = [];

        /// <summary>
        /// The recorded adds: each one's overlay, image or atlas, clip and range of <see cref="Floats"/>.
        /// </summary>
        public DebugOverlay.OverlayPass.Recorded[] Adds = [];

        /// <summary>
        /// The opaque pieces the control added.
        /// </summary>
        public UiRect[] Opaque = [];

        /// <summary>
        /// The edit boxes the control laid out.
        /// </summary>
        public (UiRect Box, DebugOverlay Font, float Scale, float X, string Text)[] Edits = [];
    }

    /// <summary>
    /// A new frame of <see cref="Draw"/>: every record dropped when what they were painted against changed; old ones dropped now and then.
    /// </summary>
    void BeginPaintFrame(int width, int height, float minimumTextPixels)
    {
        _paintFrame++;
        var now = (width, height, minimumTextPixels, Mode, textureVersion: _textureVersion, UiFont.Changes);
        if (now != _paintedFor)
        {
            _painted.Clear();
            _paintedFor = now;
        }

        if (_paintFrame % 60 != 0) return;

        foreach (var (key, record) in _painted)
        {
            if (record.Seen < _paintFrame - PaintRecordLife) _painted.Remove(key);
        }
    }

    /// <summary>
    /// Draws a control: the first frame it is seen painted, the second recorded, then replayed; one that changes on its own is painted every frame.
    /// </summary>
    void PaintCached(UiControl c, int width, int height, Vector2 mouse, string? active, float minimumTextPixels, DebugOverlay.OverlayPass pass)
    {
        bool hovered = c.Bounds.Contains(mouse.X, mouse.Y), pushed = active is not null && active == c.Action;
        var key = new PaintKey(c, (byte)((hovered ? 1 : 0) | (pushed ? 2 : 0)));
        if (!_painted.TryGetValue(key, out var record))
        {
            _painted[key] = new PaintRecord { Seen = _paintFrame };
            PaintControl(c, width, height, mouse, active, minimumTextPixels, pass);
            return;
        }

        record.Seen = _paintFrame;
        if (record.Ready) Replay(record, pass);
        else if (record.Volatile) PaintControl(c, width, height, mouse, active, minimumTextPixels, pass);
        else Record(record, c, width, height, mouse, active, minimumTextPixels, pass);
    }

    /// <summary>
    /// Replays <paramref name="record"/> into <paramref name="pass"/>, with its opaque pieces and edit boxes.
    /// </summary>
    void Replay(PaintRecord record, DebugOverlay.OverlayPass pass)
    {
        // The fonts draw into the painter's target, as painting sets them (the loading screen's and the HUD's targets differ).
        foreach (var add in record.Adds) add.Owner.Target = _overlay.Target;

        pass.Replay(record.Floats, record.Adds);
        _opaqueRects.AddRange(record.Opaque);
        _editBoxes.AddRange(record.Edits);
    }

    /// <summary>
    /// Paints the control while the pass records what it adds; kept unless the control turned out to change on its own.
    /// </summary>
    void Record(PaintRecord record, UiControl c, int width, int height, Vector2 mouse, string? active, float minimumTextPixels,
        DebugOverlay.OverlayPass pass)
    {
        int opaqueFrom = _opaqueRects.Count, editsFrom = _editBoxes.Count, fonts = UiFont.Changes, textures = _textureVersion;
        _paintVolatile = false;
        pass.BeginRecording(_recordAdds, _recordFloats);
        try
        {
            PaintControl(c, width, height, mouse, active, minimumTextPixels, pass);
        }
        finally
        {
            pass.EndRecording();
        }

        // A texture loaded or an atlas grown while it was painted: the next frame starts afresh anyway.
        if (_paintVolatile || fonts != UiFont.Changes || textures != _textureVersion)
        {
            record.Volatile = _paintVolatile;
        }
        else
        {
            record.Floats = [.. _recordFloats];
            record.Adds = [.. _recordAdds];
            record.Opaque = [.. CollectionsMarshal.AsSpan(_opaqueRects)[opaqueFrom..]];
            record.Edits = [.. CollectionsMarshal.AsSpan(_editBoxes)[editsFrom..]];
            record.Ready = true;
        }

        _recordAdds.Clear();
        _recordFloats.Clear();
    }

    /// <summary>
    /// Controls equal in everything the painter reads: their fields and their properties' contents.
    /// </summary>
    sealed class PaintKeyComparer : IEqualityComparer<PaintKey>
    {
        /// <summary>
        /// The one comparer.
        /// </summary>
        public static readonly PaintKeyComparer Instance = new();

        /// <summary>
        /// Whether two keys are the same state of controls equal in every field and property.
        /// </summary>
        public bool Equals(PaintKey x, PaintKey y)
        {
            if (x.State != y.State) return false;

            var (a, b) = (x.Control, y.Control);
            if (ReferenceEquals(a, b)) return true;

            return a.Name == b.Name && a.Type == b.Type && a.Bounds == b.Bounds && a.Caption == b.Caption && a.Action == b.Action && a.Skin == b.Skin
                && a.Enabled == b.Enabled && a.Background == b.Background && a.TextColour == b.TextColour && a.HoverColour == b.HoverColour
                && a.ActiveColour == b.ActiveColour && a.DisabledColour == b.DisabledColour && a.TextScale == b.TextScale && a.Clip == b.Clip
                && SameProperties(a.Properties, b.Properties);
        }

        /// <summary>
        /// A hash of the state and the control's main fields (name, type, bounds, caption, skin, property count).
        /// </summary>
        public int GetHashCode(PaintKey key)
        {
            var c = key.Control;
            var hash = new HashCode();
            hash.Add(key.State);
            hash.Add(c.Name);
            hash.Add(c.Type);
            hash.Add(c.Bounds);
            hash.Add(c.Caption);
            hash.Add(c.Skin);
            hash.Add(c.Properties.Count);
            return hash.ToHashCode();
        }

        /// <summary>
        /// Whether the two property sets hold the same keys with the same values.
        /// </summary>
        static bool SameProperties(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a.Count != b.Count) return false;

            if (a is Dictionary<string, string> concrete)
            {
                foreach (var (k, v) in concrete)
                {
                    if (!b.TryGetValue(k, out var other) || other != v) return false;
                }

                return true;
            }

            foreach (var (k, v) in a)
            {
                if (!b.TryGetValue(k, out var other) || other != v) return false;
            }

            return true;
        }
    }
}
