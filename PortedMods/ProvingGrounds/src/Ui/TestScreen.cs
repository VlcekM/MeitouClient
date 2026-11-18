using System.Globalization;
using Meitou.Simulation;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;
using ProvingGrounds.Game;

namespace ProvingGrounds.Ui;

/// <summary>
/// The window half of the TEST-ONLY screen <c>pg.test</c> (task 4.4; sim half src/Game/TestScreenSim.cs, opened by <c>pg window test open</c>, refused
/// until <c>pg spikes on</c>). Not an original window: it exercises every part of the framework the real windows use (registered non-modal, texts
/// bound from the model, a button click (on the release over it, task 4.4b) becoming a <see cref="UiClick"/> with the Ctrl modifier, a shape change through <c>Replace</c> keeping the
/// edit box's focus, a portrait ImageBox). It replaces spike S6/S7's test window (src/Spikes, gone with its <c>pg win</c> / <c>pg portrait</c> verbs).
/// </summary>
static class TestScreen
{
    internal const string Root = "PgTestWindow", Status = "PgTestStatus", Clicks = "PgTestClicks", Edit = "PgTestEdit", Face = "PgTestFace";
    const int Pad = 12, Width = 360, FaceSize = 128;

    static TestScreenModel? Of(PgUiModel? m) => m as TestScreenModel;

    internal static readonly PgScreen Declaration = new()
    {
        Id = TestScreenSim.ScreenId,
        ShapeKey = m => "rows " + (Of(m)?.Rows ?? 0).ToString(CultureInfo.InvariantCulture),
        Layout = Build,
        Bind = (m, b) =>
        {
            if (Of(m) is not { } t) { b.Text(Status, "Test window: no world yet"); b.Disable(TestScreenSim.ButtonWidget); b.Hide(Face); return; }
            b.Text(Status, t.Status);
            b.Text(Clicks, t.Clicks);
            b.Portrait(Face, t.Portrait);
        },
        Click = (_, c) => c.Widget == TestScreenSim.ButtonWidget && c.Kind is UiEventKind.Click or UiEventKind.RightPress ? new UiClick(c) { Tick = 0 } : null,
        Portraits = m => Of(m) is { Portrait.IsNone: false } t ? [t.Portrait] : [],
        CheckModels = () =>
        [
            null,
            new TestScreenModel(1, "s", "c", CharacterId.None),
            new TestScreenModel(3, "s", "c", new CharacterId(0, 0)),
            new TestScreenModel(TestScreenSim.MaxRows, "s", "c", CharacterId.None),
        ],
    };

    /// <summary>The layout for a model: a window, the status line, one row per model row, the button, the click count, an edit box (its name never
    /// changes, so a typed text and the focus survive a Replace, S6) and the portrait ImageBox.</summary>
    static LayoutBuilder Build(PgUiModel? m, int fontHeight)
    {
        int rows = Of(m)?.Rows ?? 0;
        int row = NativeUI.RowHeight(fontHeight, fontHeight, 0);
        var b = new LayoutBuilder()
            .OpenReal("Window", "Kenshi_WindowCX", .3f, .05f, .4f, .9f, Root, "Center", "Window").Prop("Caption", "Proving Grounds - Test");
        NativeUI.Label(b, Pad, Pad, Width, row, Status, "Test window").Close();
        int y = Pad + row;
        for (int i = 0; i < rows; i++, y += row)
            NativeUI.Label(b, Pad, y, Width, row, "PgTestRow" + i.ToString(CultureInfo.InvariantCulture),
                string.Create(CultureInfo.InvariantCulture, $"row {i + 1} of {rows}")).Close();
        NativeUI.Button(b, Pad, y + NativeUI.Spacing(fontHeight), 200, 32, TestScreenSim.ButtonWidget, "Click me").Close();
        y += 32 + 2 * NativeUI.Spacing(fontHeight);
        NativeUI.WrappedLabel(b, Pad, y, Width, row, Clicks, "Clicks: 0").Close();
        y += row;
        b.Open("EditBox", "Kenshi_EditBox", Pad, y, 200, 32, Edit).Prop("MaxTextLength", "40").Close();
        b.Open("ImageBox", "ImageBox", Pad + Width + Pad, Pad, FaceSize, FaceSize, Face).Close();
        return b.Close();
    }
}
