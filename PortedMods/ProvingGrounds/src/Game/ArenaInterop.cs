using System.Globalization;
using Meitou.Simulation;
using Meitou.Simulation.Population;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port only (no C++ counterpart; tasks 10.1 and 10.2, asked for by The Wasteland Remembers' author on the Meitou Discord, 2026-11-17):
// Proving Grounds and the other Meitou code mods.
//
// 10.1, what other mods read (src/Api/ProvingGroundsApi.cs, public): once per SlowWorld, after the combat hooks' view, every character the
// arena has in a bout goes into the API's feed (Core/Glue/ArenaApiState.cs):
//   - the running match's fighters (SparSession: walking in, fighting, benched, knocked out), whoever runs it (the squad's own arena, a town bout)
//   - the town event's fighters from the walk to the ring to the end of the aftercare (TownArena.CopyEventFighters)
//   - the match prisoners from their cage back into it (PrisonerUtil.CopyMatchPrisoners: an entry from PrepareMatch to the return)
//   - the holds of the bench, the borrowed town fighters and the aftercare's patients (the hold registry; medics, spectators, the announcer
//     and the limb shop's visits are not fighters and are left out)
// and every 5 s (when the character table changed, else every 30 s for renames) the roster of Scratch's town fighters (ArenaApiRules).
//
// 10.2, what we tell The Wasteland Remembers when it is installed (Core/Glue/WastelandLink.cs, looked up on this world's first tick):
//   - every town fighter, once per world, through LeaveAlone (never made one of its rivals, never renamed: the leaderboard shows their names)
//   - the news of a town bout's result (Core/Glue/ArenaNews.cs): Report, and for the squad's own wins also Note (History) and Moment (Recap)
// The calls run here, at the end of the SlowWorld (sim thread, serial), never inside the ported code; results are queued by the result
// observer (PgSparHost.TownOnMatchResult) and sent in the same tick. Nothing waits for The Wasteland Remembers: while it is not Ready the
// queues keep (capped), and a throw switches the link off for the world.
sealed class ArenaInterop(SparSession session, Town.TownArena town, PrisonerUtil prisoners, Towns towns, PopulationData data, Action<string>? log)
{
    internal const long RosterTicks = 5 * 30, RosterForceTicks = 30 * 30;
    internal const int LeaveAlonePerTick = 8, MaxNews = 8, MaxLeaveAlone = 2048;
    const HoldOwner FighterHolds = HoldOwner.TownFighter | HoldOwner.Bench | HoldOwner.AftercarePatient;

    readonly ArenaApiFeed<CharacterId> feed = new();
    readonly List<CharacterId> ids = [], handlers = [];
    readonly List<(CharacterId Id, string Name)> roster = [];
    long rosterRead = long.MinValue / 2, rosterForced = long.MinValue / 2;
    (long Spawns, int Count) rosterStamp = (-1, -1);

    bool linkLooked;
    WastelandLink? link;
    readonly HashSet<(string Name, string Faction)> leftAlone = [];
    readonly Queue<(string Name, string Faction)> toLeaveAlone = new();
    readonly Queue<ArenaNews.Item> news = new();
    int sentLeaveAlone, sentNews;
    volatile string phase = "starting";

    /// <summary>The published view (any thread).</summary>
    internal ArenaApiView<CharacterId> View => feed.View;

    /// <summary>The F10 / pg api line (any thread).</summary>
    internal string Phase => phase;

    /// <summary>Serial, once per SlowWorld after the combat hooks' view (ProvingGroundsSystem).</summary>
    internal void Tick(World world)
    {
        long tick = world.Tick;
        if (!linkLooked) LookForLink();
        feed.Begin(tick);
        int n = session.Running ? session.GetParticipantCount() : 0;
        for (int i = 0; i < n; i++)
            if (session.GetParticipant(i) is { } c) feed.Seen(c);
        ids.Clear();
        town.CopyEventFighters(ids);
        prisoners.CopyMatchPrisoners(world, ids, handlers);
        handlers.Clear();
        foreach (var c in ids) if (!c.IsNone) feed.Seen(c);
        var holds = town.Holds.Registry;
        for (int i = 0; i < holds.Count; i++)
        {
            var (c, owners) = holds.At(i);
            if ((owners & FighterHolds) != 0 && !c.IsNone) feed.Seen(c);
        }
        if (RosterDue(world, tick)) ReadRoster(world, tick);
        feed.End();
        Tell(world);
        phase = PhaseText();
    }

    bool RosterDue(World world, long tick)
    {
        if (tick - rosterForced >= RosterForceTicks) return true;
        if (tick - rosterRead < RosterTicks) return false;
        rosterRead = tick;
        return PgEngine.CharacterStamp(world) != rosterStamp;
    }

    void ReadRoster(World world, long tick)
    {
        rosterRead = rosterForced = tick;
        rosterStamp = PgEngine.CharacterStamp(world);
        roster.Clear();
        int faction = town.TownFactionIndex;
        PgEngine.FactionCharacters(world, faction, ArenaApiRules.IsTownFighterRecord, roster);
        feed.SetRoster(roster);
        if (link is not { CanLeaveAlone: true }) return;
        string factionName = PgEngine.FactionName(world, data, faction) ?? "";
        foreach (var (_, name) in roster)
        {
            if (ArenaApiView<CharacterId>.NameKey(name) is not { } key) continue;
            var who = (key, factionName);
            if (toLeaveAlone.Count < MaxLeaveAlone && leftAlone.Add(who)) toLeaveAlone.Enqueue(who);
        }
    }

    void LookForLink()
    {
        linkLooked = true;
        link = WastelandLink.Bind(WastelandLink.Find(), log);
        if (link is not null)
            log?.Invoke($"interop: The Wasteland Remembers found ({link.Describe()}): town fighters are left out of its rivals, arena news goes to it");
    }

    /// <summary>The result observer (PgSparHost.TownOnMatchResult, inside SparSession's result block, the session still listing its fighters):
    /// queues the news of a town bout's result for <see cref="Tell"/>.</summary>
    internal void OnMatchResult(World world, SparPodium.Snapshot snapshot)
    {
        if (link is not { } l || !(l.CanReport || l.CanNote || l.CanMoment)) return;
        bool owns = town.OwnsMatch(world);
        if (!owns) return;
        var fighters = new List<ArenaNews.Fighter>(snapshot.FighterCount);
        for (int f = 0; f < snapshot.FighterCount; f++)
        {
            ref readonly SparPodium.FighterRow row = ref snapshot.Fighters[f];
            var c = session.GetParticipant(row.Id);   // row.Id = the participant's index (SparStatsState.Freeze)
            bool player = c is { } p && PgEngine.IsPlayer(world, p);
            int unique = c is { } u ? ArenaNews.UniqueOf(PgEngine.RecordId(world, u)) : -1;
            fighters.Add(new ArenaNews.Fighter(row.Name, row.Team, row.RatingUpdated, row.RatingBefore, player, unique));
        }
        var item = ArenaNews.Decide(snapshot.Outcome, owns, fighters, TownName());
        if (item is not null && news.Count < MaxNews) news.Enqueue(item);
        // one line per town bout result while The Wasteland Remembers is installed (the live scenario interop.sh reads it)
        float? chance = ArenaNews.WinChance(Side(fighters, snapshot.Outcome, true), Side(fighters, snapshot.Outcome, false));
        log?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"interop: town bout result read for other mods ({snapshot.Outcome}, {fighters.Count} fighters, winners' chance {(chance is float w ? w.ToString("0.00", CultureInfo.InvariantCulture) : "n/a")}): {item?.Kind ?? "no news"}"));
    }

    static List<ArenaNews.Fighter> Side(List<ArenaNews.Fighter> all, SparPodium.OutcomeKind outcome, bool winners)
    {
        var team = outcome == SparPodium.OutcomeKind.TeamAWins ? MatchRules.MatchTeam.TeamA
            : outcome == SparPodium.OutcomeKind.TeamBWins ? MatchRules.MatchTeam.TeamB : MatchRules.MatchTeam.None;
        if (team == MatchRules.MatchTeam.None) return [];
        return all.Where(f => (f.Team == team) == winners && f.Team != MatchRules.MatchTeam.None).ToList();
    }

    void Tell(World world)
    {
        if (link is not { Broken: false } l || (toLeaveAlone.Count == 0 && news.Count == 0) || !l.Ready) return;
        for (int i = 0; i < LeaveAlonePerTick && l.CanLeaveAlone && toLeaveAlone.TryDequeue(out var who); i++)
            if (l.LeaveAlone(who.Name, who.Faction)) sentLeaveAlone++;
        while (news.TryDequeue(out var item))
        {
            string place = TownName();
            string faction = PgEngine.FactionName(world, data, town.TownFactionIndex) ?? "";
            bool told = l.CanReport && l.Report(item.Kind, place, faction, item.Text, item.Weight);
            if (item.SquadStory)
            {
                told |= l.CanNote && l.Note(item.Text);
                told |= l.CanMoment && l.Moment(item.Text.TrimEnd('.'), place, item.Weight);
            }
            if (told) sentNews++;
            log?.Invoke($"interop: arena news for The Wasteland Remembers ({item.Kind}, weight {item.Weight}{(told ? "" : ", not taken")}): {item.Text}");
        }
    }

    string TownName() => towns.Scratch?.Town.Name is { Length: > 0 } name ? name : "Scratch";

    string PhaseText()
    {
        var v = feed.View;
        string twr = link is null ? "The Wasteland Remembers not installed"
            : link.Broken ? "The Wasteland Remembers link off (it threw)"
            : string.Create(CultureInfo.InvariantCulture,
                $"The Wasteland Remembers {link.Describe()}: {sentLeaveAlone} left alone, {toLeaveAlone.Count} waiting, {sentNews} news");
        return string.Create(CultureInfo.InvariantCulture, $"API {(v.Ready ? "ready" : "not ready")}, {v.InBoutCount} in a bout, {v.TownFighterCount} town fighters; {twr}");
    }
}
