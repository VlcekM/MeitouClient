using System.Text;

namespace ProvingGrounds.Core.Glue;

// Task 8.1a, port only: the published views of the bookie's two windows (TownBookieUI / TownBookieResultUI's View, the window half is task 8.1b)
// and how TownBookieLogic builds them. A view is immutable once built (init-only, read-only lists; the Core View / ResultView it carries is a fresh
// object per view, never written after); a new object is published only when its Key (every field as text) changed.

/// <summary>One fighter card of a published view: the C++ <c>FighterView</c> with its <c>hand actor</c> (Core's FighterView has none) and the portrait
/// key the window binds (<c>SelectionIdentity(actor)</c> while the actor is valid, TownBookieUI.cpp:362-363; read once per lineup).</summary>
public sealed record BookieFighterCard<TId>(TId Actor, string PortraitKey, TownBookie.FighterView View);

/// <summary>The betting window's published state (<c>TownBookieUI::View</c> + the actors, the six stake buttons' enabled state, the window's request
/// numbers). The window half reports its close with <see cref="RequestSerial"/> (ScreenHost's Closed report carries the serial of the model the
/// window closed under), and numbers its clicks above <see cref="LastSeq"/>.</summary>
public sealed record TownBookieUiView<TId>(bool Open, int RequestSerial, long LastSeq, TownBookie.View View,
    IReadOnlyList<IReadOnlyList<BookieFighterCard<TId>>> Cards, IReadOnlyList<bool> AdjustmentEnabled, string Key)
{
    /// <summary>Two empty sides.</summary>
    public static readonly IReadOnlyList<IReadOnlyList<BookieFighterCard<TId>>> NoCards = [Array.Empty<BookieFighterCard<TId>>(), Array.Empty<BookieFighterCard<TId>>()];
    /// <summary>A closed window before anything was published.</summary>
    public static readonly TownBookieUiView<TId> Empty = new(false, 0, 0, new TownBookie.View(), NoCards, new bool[6], "");
}

/// <summary>The result window's published state (<c>TownBookieResultUI::View</c> + the actors and the request numbers).</summary>
public sealed record TownBookieResultUiView<TId>(bool Open, int RequestSerial, long LastSeq, TownBookie.ResultView View,
    IReadOnlyList<IReadOnlyList<BookieFighterCard<TId>>> Cards, string Key)
{
    public static readonly TownBookieResultUiView<TId> Empty = new(false, 0, 0, new TownBookie.ResultView(), TownBookieUiView<TId>.NoCards, "");
}

public sealed partial class TownBookieLogic<TId>
{
    // ---------- publishing (port only) ----------

    void Publish(ITownBookieHost<TId> h, TownBookie.View v)
    {
        var cards = Cards(h, v.Fighters, fighterActors);
        var adjust = new bool[Adjustments.Length];
        for (int i = 0; i < Adjustments.Length; ++i)
            adjust[i] = v.CanStake && TownBettingPolicy.AdjustStake(v.Stake, Adjustments[i]) != v.Stake;
        string key = KeyOf(betVisible, v, cards, adjust);
        if (key != view.Key) view = new TownBookieUiView<TId>(betVisible, requestSerial, lastSeq, v, cards, adjust, key);
    }

    void PublishResult(ITownBookieHost<TId> h, TownBookie.ResultView v)
    {
        var cards = Cards(h, v.Fighters, resultFighterActors);
        string key = ResultKeyOf(resultVisible, v, cards);
        if (key != resultView.Key) resultView = new TownBookieResultUiView<TId>(resultVisible, requestSerial, lastSeq, v, cards, key);
    }

    // the cards of both sides: each view's fighter with its actor and cached key ("" once the actor is no longer valid)
    static IReadOnlyList<IReadOnlyList<BookieFighterCard<TId>>> Cards(ITownBookieHost<TId> h, List<TownBookie.FighterView>[] views,
        List<(TId Actor, string Key)>[] actors)
    {
        var sides = new IReadOnlyList<BookieFighterCard<TId>>[2];
        for (int side = 0; side < 2; ++side)
        {
            int n = Math.Min(views[side].Count, actors[side].Count);
            var list = new BookieFighterCard<TId>[n];
            for (int i = 0; i < n; ++i)
            {
                var (actor, key) = actors[side][i];
                list[i] = new BookieFighterCard<TId>(actor, !h.IsNull(actor) && h.Valid(actor) ? key : "", views[side][i]);
            }
            sides[side] = list;
        }
        return sides;
    }

    // TownBookieUI.cpp:362-363: the key of a valid actor, else none (the window shows the placeholder)
    static string PortraitKey(ITownBookieHost<TId> h, TId c) => !h.IsNull(c) && h.Valid(c) ? h.SelectionIdentity(c) : "";

    string KeyOf(bool open, TownBookie.View v, IReadOnlyList<IReadOnlyList<BookieFighterCard<TId>>> cards, bool[] adjust)
    {
        var k = keyBuilder.Clear();
        k.Append(open).Append('|').Append(requestSerial).Append('|').Append(lastSeq).Append('|').Append(v.Header).Append('|').Append(v.Timer)
         .Append('|').Append(v.Status).Append('|').Append(v.Slip).Append('|').Append(v.Odds[0]).Append('|').Append(v.Odds[1]).Append('|')
         .Append(v.SelectedSide).Append('|').Append(v.Stake).Append('|').Append(v.CanSelect).Append(v.CanStake).Append(v.CanBet).Append(v.CanArrange);
        for (int i = 0; i < adjust.Length; ++i) k.Append(adjust[i] ? '1' : '0');
        AppendCards(k, cards);
        return k.ToString();
    }

    string ResultKeyOf(bool open, TownBookie.ResultView v, IReadOnlyList<IReadOnlyList<BookieFighterCard<TId>>> cards)
    {
        var k = keyBuilder.Clear();
        k.Append(open).Append('|').Append(requestSerial).Append('|').Append(lastSeq).Append('|').Append(v.Title).Append('|').Append(v.Context)
         .Append('|').Append(v.Payout).Append('|').Append(v.WinningSide).Append('|').Append(v.SelectedSide).Append('|').Append(v.Tone);
        AppendCards(k, cards);
        return k.ToString();
    }

    static void AppendCards(StringBuilder k, IReadOnlyList<IReadOnlyList<BookieFighterCard<TId>>> cards)
    {
        for (int side = 0; side < 2; ++side)
            foreach (var c in cards[side])
                k.Append('|').Append(side).Append(':').Append(c.Actor).Append(':').Append(c.PortraitKey).Append(':').Append(c.View.Name).Append(':')
                 .Append(c.View.Style).Append(':').Append(c.View.Record).Append(':').Append(c.View.Stats);
    }
}
