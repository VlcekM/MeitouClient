// Character portraits in the windows (task 4.4, spike S7 VERIFIED live, CAP 7.4). C++: PortraitManager::getInstance()->setImageWidget(handle,
// image, true) (ResultsUI.cpp:96-108 BindPortrait, ArenaUI's preview / roster rows). Meitou has no "portrait of a character" API: the renderer draws
// the requested characters into a 16 x 2 atlas (`meitou:portraits`), the squad bar first; a postfix on TradeWindows.CharacterTradePortraits()
// appends (character, cell) pairs for the free cells after it, and an ImageBox shows a cell with the game's own PortraitBar.PortraitRect(cell)
// (S7 live: a computed rect showed the wrong part of the cell).
using System.Linq.Expressions;
using Meitou.Data.Ui;
using Meitou.Simulation;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Ui;

/// <summary>
/// The portrait cells of the open Proving Grounds windows (task 4.4): the characters every open screen asks for (<see cref="PgScreen.Portraits"/>)
/// get cells after the squad bar's and the character trading window's, before the conversation's two (<see cref="PortraitCells{TKey}"/>); a
/// character the trading window already lists is shown in its cell and never listed twice; closing a window releases our cells; with none left the
/// window shows the name only. Window thread (GameHost.FillPortraits and the interface run on the host's frame thread); <see cref="Bind"/> reads the
/// last assignment through a volatile immutable map. No allocation per frame in the steady state (scratch fields, caches).
/// </summary>
static class Portraits
{
    const string Texture = "meitou:portraits";   // PortraitBar.PortraitTexture, PortraitBar.cs:24 (public const on an internal class)
    const int MaxFailures = 3;

    static readonly PortraitCells<CharacterId> cells = new();
    static readonly List<CharacterId> wanted = [];
    static readonly List<(CharacterId Key, int Cell)> others = [];
    static readonly List<(CharacterId, int)> lastInput = [];   // task 4.8: compared through PortraitOutput.SameAsLast
    static IReadOnlyList<(CharacterId Character, int Cell)>? lastOutput;
    static int portraitCells = 32, dialogueNpcCell = 30;   // PortraitBar.PortraitCells / DialogueNpcCell, read once (Prepare)
    // compiled once (Prepare): no MethodInfo.Invoke, no boxing per frame. Bar(ui) = PlayerInterface.PortraitBar; the others take the bar.
    static Func<object, object>? bar;
    static Func<object, int>? squadCount, portraitSize;
    static Func<object, bool>? talking;
    static Func<object, int, string>? portraitRect;
    static readonly PortraitRectCache<string> rects = new();   // PortraitRect(cell) per cell, for the current PortraitSize
    static readonly PortraitRectCache<IReadOnlyDictionary<string, string>> props = new();   // the ImageBox properties per (widget, cell)
    // Stale while the interface is hidden: FillPortraits asks only while UiVisible (GameHost.Characters.cs:300), so `assigned` keeps the last
    // frame's cells then; nothing draws our windows meanwhile, and the first visible frame recomputes them.
    static volatile IReadOnlyDictionary<CharacterId, int> assigned = new Dictionary<CharacterId, int>();
    static int failures;

    /// <summary>Reads the atlas constants (literal ints, PortraitBar.cs:29, :39) and compiles the portrait calls, once at install; the reason when
    /// that failed (the portraits part stays off), else null.</summary>
    internal static string? Prepare()
    {
        try
        {
            if (Members.PortraitCellsConst?.GetRawConstantValue() is not int all || Members.DialogueNpcCellConst?.GetRawConstantValue() is not int npc)
                return "PortraitBar constants unreadable";
            (portraitCells, dialogueNpcCell) = (all, npc);
            var barType = Members.PortraitBarProp!.PropertyType;
            var ui = Expression.Parameter(typeof(object), "ui");
            bar = Expression.Lambda<Func<object, object>>(Expression.Convert(Expression.Property(Expression.Convert(ui, Members.PortraitBarProp.DeclaringType!),
                Members.PortraitBarProp), typeof(object)), ui).Compile();
            var b = Expression.Parameter(typeof(object), "bar");
            var typed = Expression.Convert(b, barType);
            var members = Expression.Call(typed, Members.PortraitMembers!);   // IReadOnlyList<CharacterSnapshot>
            squadCount = Expression.Lambda<Func<object, int>>(Expression.Property(Expression.Convert(members,
                typeof(IReadOnlyCollection<>).MakeGenericType(members.Type.GetGenericArguments()[0])), "Count"), b).Compile();
            talking = Expression.Lambda<Func<object, bool>>(Expression.Property(Expression.Call(typed, Members.DialoguePortraitCharacters!), "HasValue"), b).Compile();
            portraitSize = Expression.Lambda<Func<object, int>>(Expression.Property(typed, Members.PortraitSize!), b).Compile();
            var cell = Expression.Parameter(typeof(int), "cell");
            portraitRect = Expression.Lambda<Func<object, int, string>>(Expression.Call(typed, Members.PortraitRect!, cell), b, cell).Compile();
            return null;
        }
        catch (Exception e) { return $"portrait calls not compiled: {e.GetType().Name}: {e.Message}"; }
    }

    /// <summary>For PgCheck ui: the compiled PortraitSize and PortraitRect over a PortraitBar instance (visibility of the compiled calls).</summary>
    internal static (int Size, string Rect) ProbeForCheck(object portraitBar, int cell) => (portraitSize!(portraitBar), portraitRect!(portraitBar, cell));

    /// <summary>A new interface (LoadUi): caches cleared, a part that failed is tried again.</summary>
    internal static void NewHost()
    {
        rects.Clear();
        props.Clear();
        failures = 0;
        lastOutput = null;
        lastInput.Clear();
    }

    /// <summary>Postfix on <c>TradeWindows.CharacterTradePortraits()</c> (TradeWindows.cs:594), run by GameHost.FillPortraits every frame while the
    /// interface is visible (GameHost.Characters.cs:294-318) and by the trading window's bind (TradeWindows.cs:507): appends our windows'
    /// (character, cell) pairs, each character once and never one the trading window already lists.</summary>
    internal static void AfterTradePortraits(ref IReadOnlyList<(CharacterId Character, int Cell)> __result)
    {
        using var perfScope = PgPerf.Measure(PgPerf.TradePortraits);   // task 9.2a: the cost instrument (off: one volatile read)
        if (failures >= MaxFailures) return;
        try
        {
            wanted.Clear();
            if (ScreenHost.Interface is not null && ScreenHost.Host is not null)
            {
                var snap = ProvingGroundsSystem.Current?.Snapshot;
                foreach (var s in ScreenHost.Screens)
                {
                    if (!ScreenHost.IsOpen(s.Id)) continue;
                    var ids = s.Portraits(snap?.Model(s.Id));
                    for (int i = 0; i < ids.Count; i++)
                        if (!ids[i].IsNone) wanted.Add(ids[i]);
                }
            }
            if (wanted.Count == 0 && cells.Shown.Count == 0) return;   // nothing wanted, nothing held

            int squad = 0, end = portraitCells;
            if (wanted.Count > 0)
            {
                var pb = bar!(ScreenHost.Interface!);
                squad = squadCount!(pb);   // PortraitMembers(): cells 0..Count-1
                if (talking!(pb)) end = dialogueNpcCell;
            }
            others.Clear();
            for (int i = 0; i < __result.Count; i++) others.Add((__result[i].Character, __result[i].Cell));   // the trading window's pairs
            if (cells.Update(wanted, squad, end, others)) assigned = new Dictionary<CharacterId, int>(cells.Shown);
            var mine = cells.Pairs();
            if (mine.Count == 0) return;
            // the same input and the same pairs as the last call (FillPortraits and the trading window both ask each frame): the same output list
            if (PortraitOutput.SameAsLast(__result, lastInput, lastOutput, mine)) { __result = lastOutput!; return; }
            lastInput.Clear();
            for (int i = 0; i < __result.Count; i++) lastInput.Add(__result[i]);
            lastOutput = [.. __result, .. mine];
            __result = lastOutput;
        }
        catch (Exception e)
        {
            if (++failures >= MaxFailures) ProvingGroundsMod.Ctx?.Log($"window portraits off until the next interface load: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// ImageBox <paramref name="widget"/> shows <paramref name="character"/>'s portrait: <c>ImageTexture = "meitou:portraits"</c>,
    /// <c>ImageFlipV = "true"</c>, <c>ImageRect</c> = the game's <c>PortraitBar.PortraitRect(cell)</c> (as the character trading window binds its
    /// rows, TradeWindows.cs:540-546), merged with the widget's other bound properties. Without a cell (none free, portraits off, none asked) the
    /// ImageBox is hidden and false returned.
    /// </summary>
    internal static bool Bind(UiBindings b, string widget, CharacterId character)
    {
        if (failures >= MaxFailures || character.IsNone || !assigned.TryGetValue(character, out int cell) || ScreenHost.Interface is not { } ui)
        {
            b.Hidden.Add(widget);
            return false;
        }

        IReadOnlyDictionary<string, string>? p = null;
        try
        {
            var pb = bar!(ui);
            int size = portraitSize!(pb);   // the look's portrait size: a change empties both caches
            if (!props.TryGet(widget, cell, size, out p))
            {
                if (!rects.TryGet("", cell, size, out var rect) && portraitRect!(pb, cell) is { } made) rects.Set("", cell, rect = made);
                if (rect is not null)
                {
                    p = new Dictionary<string, string>(StringComparer.Ordinal) { ["ImageTexture"] = Texture, ["ImageFlipV"] = "true", ["ImageRect"] = rect };
                    props.Set(widget, cell, p);
                }
            }
        }
        catch (Exception) { p = null; }
        if (p is null)
        {
            b.Hidden.Add(widget);
            return false;
        }
        ScreenBinding.Merge(b, widget, p);
        return true;
    }
}
