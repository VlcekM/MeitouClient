// Port only (no C++ counterpart; task 10.2): which town bout results become news for other mods (The Wasteland Remembers' Report, Note and
// Moment, src/Game/ArenaInterop.cs), and the words. Pure: the glue hands over the result rows (SparPodium, after the ratings were applied)
// with what it read from the world (which fighters are the player's, which are the arena's unique fighters).
//
// The rules, in order:
//   - only a bout the town owns (TownArena.OwnsMatch: Scratch's ambient bouts and the player's challenges); a spar at the squad's own arena
//     is the squad's business, not the town's
//   - only a Team A or Team B win (a stop, a draw or a last-standing free-for-all is not news)
//   - the player's side won: against Skarn, against another of the arena's unique fighters (Senn, Torka, Ressa Vane, Bum Per, Veyr), or as
//     the underdog; each is news, a History line and a Recap moment (the squad's own story)
//   - the player's side lost: nothing
//   - Scratch's own fighters against each other: news only when the underdog won
// "The underdog" = the winners' chance by the ratings they had before the bout (MmrRating.ExpectedScore of each side's mean rating, the same
// classic Elo the ratings use) was at most UpsetChance. Fighters without a rating (RatingUpdated false) are left out of the means; a side with
// no rated fighter has no chance to judge, so the bout is no upset.
// Tests/Glue/ArenaNewsTests.cs.
namespace ProvingGrounds.Core.Glue;

public static class ArenaNews
{
    /// <summary>The winners had at most a one-in-four chance by their ratings before the bout.</summary>
    public const float UpsetChance = 0.25f;

    /// <summary>The Wasteland Remembers' weights (0 to 100; the weightiest live news is what towns talk about, 60 and up is also posted when
    /// the squad hears it).</summary>
    public const int TownUpsetWeight = 30, SquadUpsetWeight = 50, UniqueWeight = 65, SkarnWeight = 80;

    public const string TownUpsetKind = "arena-upset", SquadUpsetKind = "arena-squad-upset", UniqueKind = "arena-unique-beaten",
        SkarnKind = "arena-skarn-beaten";

    /// <summary>One fighter of the result. <paramref name="Unique"/>: the <see cref="TownChallengePolicy.Encounter"/> of one of the arena's
    /// unique fighters (<see cref="UniqueOf"/>), else -1.</summary>
    public readonly record struct Fighter(string? Name, MatchRules.MatchTeam Team, bool Rated, float RatingBefore, bool Player, int Unique = -1);

    /// <summary>What to tell: the news <paramref name="Kind"/>, its sentence, its weight; <paramref name="SquadStory"/>: also a History line and a
    /// Recap moment.</summary>
    public sealed record Item(string Kind, string Text, int Weight, bool SquadStory);

    /// <summary>The unique fighter a record id is (TownChallengePolicy.Role: Senn, Torka, Skarn, Ressa Vane, Bum Per, Veyr), else -1.</summary>
    public static int UniqueOf(string? recordId)
    {
        if (string.IsNullOrEmpty(recordId)) return -1;
        for (int e = (int)TownChallengePolicy.Encounter.Senn; e <= (int)TownChallengePolicy.Encounter.Veyr; ++e)
            if (string.Equals(recordId, TownChallengePolicy.Role(e), StringComparison.Ordinal)) return e;
        return -1;
    }

    /// <summary>The news of one result, or null. <paramref name="town"/>: the town's name as the game shows it (Scratch).</summary>
    public static Item? Decide(SparPodium.OutcomeKind outcome, bool townOwnsMatch, IReadOnlyList<Fighter> fighters, string town)
    {
        if (!townOwnsMatch) return null;
        MatchRules.MatchTeam win, lose;
        if (outcome == SparPodium.OutcomeKind.TeamAWins) { win = MatchRules.MatchTeam.TeamA; lose = MatchRules.MatchTeam.TeamB; }
        else if (outcome == SparPodium.OutcomeKind.TeamBWins) { win = MatchRules.MatchTeam.TeamB; lose = MatchRules.MatchTeam.TeamA; }
        else return null;
        var winners = fighters.Where(f => f.Team == win).ToList();
        var losers = fighters.Where(f => f.Team == lose).ToList();
        if (winners.Count == 0 || losers.Count == 0) return null;
        string where = string.IsNullOrWhiteSpace(town) ? "Scratch" : town.Trim();
        bool upset = WinChance(winners, losers) is float p && p <= UpsetChance;

        if (winners.Any(f => f.Player))
        {
            string w = Names(winners);
            // indexes, not FirstOrDefault: a default Fighter has Unique 0 (an encounter id), not -1
            int skarn = losers.FindIndex(f => f.Unique == (int)TownChallengePolicy.Encounter.Skarn);
            if (skarn >= 0)
                return new Item(SkarnKind, $"{w} defeated {NameOf(losers[skarn])}, the arena's last challenge, at {where}.", SkarnWeight, true);
            int unique = losers.FindIndex(f => f.Unique >= 0);
            if (unique >= 0)
                return new Item(UniqueKind, $"{w} defeated {NameOf(losers[unique])}, one of the arena's unique fighters, at {where}.", UniqueWeight, true);
            return upset ? new Item(SquadUpsetKind, Upset(winners, losers, where), SquadUpsetWeight, true) : null;
        }
        if (fighters.Any(f => f.Player)) return null;   // the player's side lost: no news from us
        return upset ? new Item(TownUpsetKind, Upset(winners, losers, where), TownUpsetWeight, false) : null;
    }

    /// <summary>The winners' chance by the rated fighters' ratings before the bout (each side's mean); null when a side has no rated fighter.</summary>
    public static float? WinChance(IReadOnlyList<Fighter> winners, IReadOnlyList<Fighter> losers)
    {
        var w = winners.Where(f => f.Rated && float.IsFinite(f.RatingBefore)).Select(f => f.RatingBefore).ToList();
        var l = losers.Where(f => f.Rated && float.IsFinite(f.RatingBefore)).Select(f => f.RatingBefore).ToList();
        if (w.Count == 0 || l.Count == 0) return null;
        return MmrRating.ExpectedScore(w.Average(), l.Average());
    }

    static string Upset(List<Fighter> winners, List<Fighter> losers, string where) =>
        $"{Names(winners)} beat the {(losers.Count == 1 ? "favourite" : "favourites")} {Names(losers)} in the arena at {where}.";

    /// <summary>"A", "A and B", "A, B and C", or "A, B and 3 others".</summary>
    public static string Names(IReadOnlyList<Fighter> side)
    {
        var names = side.Select(NameOf).ToList();
        if (names.Count == 1) return names[0];
        if (names.Count <= 3) return string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
        return $"{names[0]}, {names[1]} and {names.Count - 2} others";
    }

    static string NameOf(Fighter f) => string.IsNullOrWhiteSpace(f.Name) ? "a fighter" : f.Name.Trim();
}
