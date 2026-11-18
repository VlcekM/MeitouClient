using System.Globalization;
using System.Numerics;
using System.Text;

namespace ProvingGrounds.Core.Glue;

// Port of upstream/ProvingGrounds/plugin/src/TownMatchmakingRuntime.h/.cpp (Proving Grounds by Shutori, GPLv3), task 7.2 (TW §2.2 steps 2-3, §2.3;
// CAP 2, 6, 11; census docs/census.md): the town's roster snapshot (Collect) and its helpers, over ITownRosterHost (the character reads). The glue
// (src/Town/TownMatchmakingRuntime.cs, src/Town/TownArena.Matchmaking.cs) implements the host over PgEngine, FighterIdentity and the leaderboard
// store; the test fake is in Tests/Glue/TownMatchmakingRuntimeLogicTests.cs. Characters are a TId (the C++ Character*), the registry a
// PlacedBuilding (its position and town). Readiness (TownArena.cpp:297-346) is TownArenaLogic.Ready, passed in as the C++ passes its ReadyCheck.
//
// C++ engine calls -> host:
//   Character::isValid ... Valid; getCurrentTownLocation ... CurrentTown (a town key); getGameData()->stringID ... Role; getName ... Name
//   ou->getObjectsWithinSphere(objects, registry pos, 2500, CHARACTER, 512, NULL) ... CharactersInSphere(pos, 2500, 512)
//   FighterIdentity::GetOrCreate / Find ... GetOrCreateIdentity / FindIdentity; LeaderboardStore::RemoveTownNpcProgression ... RemoveTownNpcProgression
//   LeaderboardStore::GetRating(c, Town) ... TownRating; ArenaCombatProfile::Evaluate(Read(c)) ... Ability
//   hand(c) + TownArenaRuntimePolicy::PlayerSelectionKey ... SelectionIdentity
//   TownAftercare::IsManaged / TownLimbShop::IsManaged / TownArena::IsFighter ... AftercareManaged / LimbShopManaged / TownArenaFighter
//   isCarryingSomething / isBeingCarried ... IsCarrying / IsCarried
//   port only (census): IsTownFaction (faction Bloodstamp 91): a character of another faction is not discovered (docs/divergences.md)

/// <summary>The character reads of <c>TownMatchmakingRuntime::Collect</c> (and of TownArena.cpp, which extends this interface as ITownHost).</summary>
public interface ITownRosterHost<TId>
{
    /// <summary><c>ou</c> is not NULL (a world is bound).</summary>
    bool HasWorld { get; }
    /// <summary><c>!c</c>: no character at all.</summary>
    bool IsNull(TId c);
    /// <summary>The C++ NULL <c>Character*</c> as a TId (<c>default</c> may name a real character: CharacterId's default is slot 0; task 7.3).</summary>
    TId None { get; }
    /// <summary><c>c-&gt;isValid()</c>.</summary>
    bool Valid(TId c);
    /// <summary><c>c-&gt;getCurrentTownLocation()</c> as a town key (<see cref="TownKey.None"/>: outside every town).</summary>
    long CurrentTown(TId c);
    /// <summary><c>ou-&gt;getObjectsWithinSphere(objects, origin, radius, CHARACTER, max, NULL)</c>; null with no world.</summary>
    IReadOnlyList<TId>? CharactersInSphere(Vector3 origin, float radius, int max);
    /// <summary>Port only (census, docs/census.md "What this means" 6): the character belongs to the town faction (Bloodstamp,
    /// <c>91-Proving Grounds.mod</c>).</summary>
    bool IsTownFaction(TId c);
    /// <summary><c>TownMatchmakingRuntime::Role</c>: <c>getGameData()-&gt;stringID</c> (<c>CharacterCold.RecordId</c>), "" when invalid.</summary>
    string Role(TId c);
    /// <summary><c>c-&gt;getName()</c>.</summary>
    string Name(TId c);
    /// <summary><c>FighterIdentity::GetOrCreate(c, id)</c>.</summary>
    bool GetOrCreateIdentity(TId c, out string id);
    /// <summary><c>FighterIdentity::Find(c, id)</c>.</summary>
    bool FindIdentity(TId c, out string id);
    /// <summary><c>LeaderboardStore::RemoveTownNpcProgression(c)</c> (the result is not read).</summary>
    void RemoveTownNpcProgression(TId c);
    /// <summary><c>TownMatchmakingRuntime::SelectionIdentity</c>: the session-only player key, "" when the live handle is unavailable.</summary>
    string SelectionIdentity(TId c);
    /// <summary><c>TownMatchmakingRuntime::Ability</c>: <c>ArenaCombatProfile::Evaluate(ArenaCombatProfile::Read(c))</c> (-1 when unusable).</summary>
    double Ability(TId c);
    /// <summary><c>LeaderboardStore::GetRating(c, LeaderboardData::Town)</c>.</summary>
    double TownRating(TId c);
    /// <summary><c>TownAftercare::IsManaged(c)</c>.</summary>
    bool AftercareManaged(TId c);
    /// <summary><c>TownLimbShop::IsManaged(c)</c>.</summary>
    bool LimbShopManaged(TId c);
    /// <summary><c>TownArena::IsFighter(c)</c>.</summary>
    bool TownArenaFighter(TId c);
    /// <summary><c>c-&gt;isCarryingSomething</c>.</summary>
    bool IsCarrying(TId c);
    /// <summary><c>c-&gt;isBeingCarried()</c>.</summary>
    bool IsCarried(TId c);
}

/// <summary>C++ <c>TownMatchmakingRuntime::ReadyCheck</c> (<c>bool (*)(Character*, bool doctor, bool bookedPlayer, std::string* reason)</c>):
/// <paramref name="reason"/> is what the C++ writes through its pointer.</summary>
public delegate bool TownReadyCheck<TId>(TId c, bool doctor, bool bookedPlayer, out string reason);

/// <summary>C++: TownMatchmakingRuntime.h:8-16 <c>struct Snapshot</c> (copied by value in the C++: <see cref="Clone"/>). <see cref="Characters"/> is
/// <c>std::map&lt;std::string, Character*&gt;</c>: a key whose value is the C++ NULL (two characters with that identity) has
/// <see cref="Entry.Unique"/> false. A new snapshot has every string empty, as the C++ default constructor; <c>Collect</c> fills them.
/// <para>Port only: <see cref="Roles"/>, <see cref="Blockers"/> and the counts are Collect's locals (which the C++ prints only into
/// <see cref="Diagnostics"/>), kept so the test verb <c>pg town roster</c> can show them.</para></summary>
public sealed class TownRosterSnapshot<TId>
{
    /// <summary>A <see cref="Characters"/> value: the character, or (<see cref="Unique"/> false) the C++ NULL of an ambiguous key.</summary>
    public readonly record struct Entry(bool Unique, TId Character);

    public List<TownMatchmakingPolicy.Fighter> Players = new(), Npcs = new();
    public SortedDictionary<string, Entry> Characters = new(StringComparer.Ordinal);
    public List<TId> Medics = new();
    public string Context = "";
    public string Availability = "", Diagnostics = "", PlayerBlocker = "";
    public bool PlayersReady;

    // ---- port only: Collect's locals (pg town roster) ----
    public SortedDictionary<string, int> Roles = new(StringComparer.Ordinal);
    public SortedDictionary<string, int> Blockers = new(StringComparer.Ordinal);
    public int Scanned, InTown, MissingIdentity, Ambiguous;
    /// <summary>Port only: the characters of the registry's town the census faction filter left out, by record (pg town roster).</summary>
    public SortedDictionary<string, int> OtherFaction = new(StringComparer.Ordinal);
    /// <summary>Port only: the valid characters in the sphere outside the registry's town, by record (pg town roster).</summary>
    public SortedDictionary<string, int> OtherTown = new(StringComparer.Ordinal);

    /// <summary>The C++ by-value copy (fighters are structs; the lists and maps copied).</summary>
    public TownRosterSnapshot<TId> Clone() => new()
    {
        Players = new(Players), Npcs = new(Npcs), Characters = new(Characters, StringComparer.Ordinal), Medics = new(Medics),
        Context = Context, Availability = Availability, Diagnostics = Diagnostics, PlayerBlocker = PlayerBlocker, PlayersReady = PlayersReady,
        Roles = new(Roles, StringComparer.Ordinal), Blockers = new(Blockers, StringComparer.Ordinal),
        Scanned = Scanned, InTown = InTown, MissingIdentity = MissingIdentity, Ambiguous = Ambiguous,
        OtherFaction = new(OtherFaction, StringComparer.Ordinal), OtherTown = new(OtherTown, StringComparer.Ordinal),
    };
}

/// <summary>C++ <c>namespace TownMatchmakingRuntime</c>. Sim thread only: every function reads the world through the host.</summary>
public static class TownMatchmakingRuntimeLogic
{
    /// <summary><c>getObjectsWithinSphere(..., 2500.0f, CHARACTER, 512, NULL)</c> (TownMatchmakingRuntime.cpp:66): the radius ...</summary>
    public const float SearchRadius = 2500.0f;
    /// <summary>... and the cap.</summary>
    public const int SearchMax = 512;

    // C++: TownMatchmakingRuntime.cpp:21-25
    public static string Identity<TId>(ITownRosterHost<TId> h, TId c)
    {
        if (h.IsNull(c) || !h.Valid(c)) return "";
        return h.FindIdentity(c, out string id) ? id : "";
    }

    // C++: TownMatchmakingRuntime.cpp:26-29
    public static string Role<TId>(ITownRosterHost<TId> h, TId c) => !h.IsNull(c) && h.Valid(c) ? h.Role(c) : "";

    // C++: TownMatchmakingRuntime.cpp:30-32
    public static double Ability<TId>(ITownRosterHost<TId> h, TId c) => h.Ability(c);

    // C++: TownMatchmakingRuntime.cpp:33-38
    public static string Label<TId>(ITownRosterHost<TId> h, TId c)
    {
        if (h.IsNull(c) || !h.Valid(c)) return "Unavailable";
        TownFighterCatalog.Entry? entry = TownFighterCatalog.Find(Role(h, c));
        return h.Name(c) + (entry != null ? " (" + TownChallengePolicy.DivisionName(entry.Tier) + " " + entry.Type + ")" : "");
    }

    /// <summary>C++: TownMatchmakingRuntime.cpp:39-43 <c>Resolve</c>: the character holding <paramref name="id"/> in the snapshot, when that key
    /// is unique and the character still has that identity; false (with <paramref name="character"/> = the host's None) for the C++ NULL.</summary>
    public static bool Resolve<TId>(ITownRosterHost<TId> h, TownRosterSnapshot<TId> s, string id, out TId character)
    {
        character = h.None;
        if (!s.Characters.TryGetValue(id, out var found) || !found.Unique || h.IsNull(found.Character) || Identity(h, found.Character) != id)
            return false;
        character = found.Character;
        return true;
    }

    // C++: TownMatchmakingRuntime.cpp:44-46 (std::sort with this comparator: std::string's operator<, a byte order)
    static int ById(TownMatchmakingPolicy.Fighter a, TownMatchmakingPolicy.Fighter b) => string.CompareOrdinal(a.Id, b.Id);

    // C++: TownMatchmakingRuntime.cpp:47-52
    static TownMatchmakingPolicy.Fighter Fighter<TId>(ITownRosterHost<TId> h, TId c, double ability)
    {
        var f = new TownMatchmakingPolicy.Fighter();
        f.Id = Identity(h, c); f.Role = Role(h, c); f.Ability = ability;
        f.Mmr = h.TownRating(c);
        return f;
    }

    // C++: TownMatchmakingRuntime.cpp:53-59 (hand(c) and the key: the host's SelectionIdentity)
    public static string SelectionIdentity<TId>(ITownRosterHost<TId> h, TId c) => h.IsNull(c) || !h.Valid(c) ? "" : h.SelectionIdentity(c);

    /// <summary>A Character* compared by address in DiscoveryIndex: one object per character.</summary>
    sealed class Actor<TId>(TId id) { public readonly TId Id = id; }

    static void Inc(SortedDictionary<string, int> map, string key) => map[key] = map.TryGetValue(key, out int n) ? n + 1 : 1;

    /// <summary>C++: TownMatchmakingRuntime.cpp:60-176 <c>Collect(registry, players, ready, includeNamed)</c>. <paramref name="registry"/> null = the
    /// C++ NULL / invalid registry; <paramref name="ready"/> null = no readiness callback. <paramref name="players"/> may hold none ids (the C++ NULL).
    /// Port only (census): a character not of the town faction is left out before anything else (docs/divergences.md "Matchmaking snapshot").
    /// Ability is read once per NPC (the C++ reads it twice in one tick, :137-138: the same value).</summary>
    public static TownRosterSnapshot<TId> Collect<TId>(ITownRosterHost<TId> h, PlacedBuilding? registry, IReadOnlyList<TId> players,
        TownReadyCheck<TId>? ready, bool includeNamed)
    {
        var eq = EqualityComparer<TId>.Default;
        var s = new TownRosterSnapshot<TId>();
        s.Availability = "Town registry unavailable";
        s.Diagnostics = "registry/world/town/readiness callback unavailable";
        if (!h.HasWorld || registry is not { } reg || reg.Town == TownKey.None || ready == null) return s;
        IReadOnlyList<TId> objects = h.CharactersInSphere(reg.Building.Position, SearchRadius, SearchMax) ?? [];
        int inTown = 0, missingIdentity = 0, ambiguous = 0;
        var roles = s.Roles;
        var blockers = s.Blockers;
        var discovery = new TownArenaRuntimePolicy.DiscoveryIndex<Actor<TId>>();
        // Resolve ambiguity before readiness filtering: an injured namesake still collides.
        for (int i = 0; i < objects.Count; ++i)
        {
            TId c = objects[i];
            if (h.IsNull(c) || !h.Valid(c)) continue;
            if (!TownKey.Same(h.CurrentTown(c), reg.Town)) { Inc(s.OtherTown, h.Role(c)); continue; }   // (port only: counted for pg town roster)
            if (!h.IsTownFaction(c)) { Inc(s.OtherFaction, h.Role(c)); continue; }   // port only (census): Shek ranchers, hunters and animals walk the arena circle
            string role = Role(h, c);
            bool named = TownChallengePolicy.NamedRoleDivision(role) >= 0;
            bool opponent = TownFighterCatalog.Find(role) != null ||
                (includeNamed && named);
            string id = "";
            if (opponent) h.GetOrCreateIdentity(c, out id);
            // Remove old unused Marks rows as known town fighters are discovered.
            // Never infer NPC ownership from an absent/unloaded player character.
            if (opponent || named)
                h.RemoveTownNpcProgression(c);
            // one object per character (the C++ compares Character* pointers): the one already discovered, if any
            Actor<TId> actor = discovery.Actors.Find(a => eq.Equals(a.Id, c)) ?? new Actor<TId>(c);
            if (!discovery.Add(actor, id)) continue;
            ++inTown;
            Inc(roles, Role(h, c));
            if (id.Length == 0) ++missingIdentity;
        }
        foreach (var kv in discovery.ByIdentity)
            s.Characters[kv.Key] = kv.Value is { } a ? new TownRosterSnapshot<TId>.Entry(true, a.Id) : new TownRosterSnapshot<TId>.Entry(false, h.None);
        foreach (var kv in s.Characters)
            if (!kv.Value.Unique) ++ambiguous;
        s.PlayersReady = players.Count != 0 && players.Count <= 3;
        for (int i = 0; i < players.Count; ++i)
        {
            TId c = players[i];
            string playerReason = "";
            string selectionId = SelectionIdentity(h, c);
            if (h.IsNull(c) || !h.Valid(c)) playerReason = "selected character is unavailable";
            else if (!TownKey.Same(h.CurrentTown(c), reg.Town)) playerReason = "return to the registry town";
            else if (!ready(c, false, true, out playerReason))
            {
                if (playerReason.Length == 0) playerReason = "not ready to fight";
            }
            else if (h.AftercareManaged(c)) playerReason = "still in aftercare";
            else if (selectionId.Length == 0) playerReason = "live character handle unavailable";
            else if (!(Ability(h, c) > 0)) playerReason = "combat stats unavailable";
            for (int j = 0; j < i; ++j) if (eq.Equals(players[j], c)) playerReason = "same fighter selected more than once";
            if (playerReason.Length != 0)
            {
                Inc(blockers, "selected player: " + playerReason);
                if (s.PlayerBlocker.Length == 0) s.PlayerBlocker = !h.IsNull(c) && h.Valid(c) ?
                    h.Name(c) + ": " + playerReason : playerReason;
                s.PlayersReady = false; continue;
            }
            TownMatchmakingPolicy.Fighter fighter = Fighter(h, c, Ability(h, c));
            fighter.Id = selectionId;
            s.Players.Add(fighter);
        }
        for (int i = 0; i < discovery.Actors.Count; ++i)
        {
            TId c = discovery.Actors[i].Id;
            string role = Role(h, c);
            bool medic = TownArenaRuntimePolicy.IsMedic(role);
            bool named = TownChallengePolicy.NamedRoleDivision(role) >= 0;
            if (!medic && TownFighterCatalog.Find(role) == null && !(includeNamed && named)) continue;
            if (h.AftercareManaged(c) || h.LimbShopManaged(c) || h.TownArenaFighter(c))
            {
                Inc(blockers, role + ": active bout/recovery ownership"); continue;
            }
            string reason = "";
            if (medic)
            {
                if (h.IsCarrying(c) || h.IsCarried(c))
                    Inc(blockers, role + ": carrying/being carried");
                else if (ready(c, true, false, out reason)) s.Medics.Add(c);
                else Inc(blockers, role + ": " + reason);
                continue;
            }
            if (!Resolve(h, s, Identity(h, c), out TId resolved) || !eq.Equals(resolved, c))
            {
                Inc(blockers, role + ": missing/ambiguous opponent identity"); continue;
            }
            if (!ready(c, false, false, out reason)) { Inc(blockers, role + ": " + reason); continue; }
            double ability = Ability(h, c);
            if (!(ability > 0)) { Inc(blockers, role + ": invalid ability stats"); continue; }
            s.Npcs.Add(Fighter(h, c, ability));
        }
        // Preserve stable selector/context order even if spatial query order changes.
        s.Npcs.Sort(ById);
        s.Availability = N(s.Npcs.Count) + " eligible opponents, " + N(s.Medics.Count) + " available medics";
        var diagnostic = new StringBuilder();
        diagnostic.Append(s.Availability).Append("; scanned=").Append(N(objects.Count)).Append(" inTown=").Append(N(inTown))
            .Append(" missingIdentity=").Append(N(missingIdentity)).Append(" ambiguousKeys=").Append(N(ambiguous))
            .Append(" selected=").Append(N(players.Count)).Append(" eligiblePlayers=").Append(N(s.Players.Count))
            .Append(" playersReady=").Append(s.PlayersReady ? '1' : '0').Append("; roles=");
        int diagnosticRows = 0;
        foreach (var kv in roles)
        {
            if (diagnosticRows >= 64) break;
            diagnostic.Append('[').Append(kv.Key).Append('=').Append(N(kv.Value)).Append(']');
            ++diagnosticRows;
        }
        diagnostic.Append("; rejected=");
        diagnosticRows = 0;
        foreach (var kv in blockers)
        {
            if (diagnosticRows >= 64) break;
            diagnostic.Append('[').Append(kv.Key).Append('=').Append(N(kv.Value)).Append(']');
            ++diagnosticRows;
        }
        s.Diagnostics = diagnostic.ToString();
        s.Players.Sort(ById);
        s.Context = ContextDigest(s);
        s.Scanned = objects.Count; s.InTown = inTown; s.MissingIdentity = missingIdentity; s.Ambiguous = ambiguous;
        return s;
    }

    static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>The raw context of TownMatchmakingRuntime.cpp:158-165: <c>std::ostringstream</c> with <c>setprecision(17)</c> (msvcp100 -&gt; msvcr100
    /// <c>%.17g</c>, <see cref="CMath.FormatG(double, int)"/>), a bool as 0/1, <c>size()</c> = the UTF-8 byte count of the std::string.</summary>
    // C++: TownMatchmakingRuntime.cpp:158-165
    public static string RawContext<TId>(TownRosterSnapshot<TId> s)
    {
        var context = new StringBuilder();
        context.Append(s.PlayersReady ? '1' : '0').Append(':').Append(N(s.Medics.Count));
        foreach (var p in s.Players)
            context.Append("|P").Append(N(Encoding.UTF8.GetByteCount(p.Id))).Append(':').Append(p.Id).Append(':')
                .Append(CMath.FormatG(p.Ability, 17)).Append(':').Append(CMath.FormatG(p.Mmr, 17));
        foreach (var n in s.Npcs)
            context.Append("|N").Append(N(Encoding.UTF8.GetByteCount(n.Id))).Append(':').Append(n.Id).Append(':').Append(n.Role).Append(':')
                .Append(CMath.FormatG(n.Ability, 17)).Append(':').Append(CMath.FormatG(n.Mmr, 17));
        return context.ToString();
    }

    /// <summary>C++: TownMatchmakingRuntime.cpp:166-174: two 32-bit hashes (FNV-1a and Bernstein's xor variant) over the raw context's bytes,
    /// printed <c>first:second</c>.</summary>
    public static string ContextDigest<TId>(TownRosterSnapshot<TId> s)
    {
        // Two independent 32-bit hashes keep persisted preview context bounded.
        byte[] raw = Encoding.UTF8.GetBytes(RawContext(s));
        uint first = 2166136261u, second = 5381u;
        unchecked
        {
            for (int i = 0; i < raw.Length; ++i)
            {
                first = (first ^ raw[i]) * 16777619u;
                second = (second * 33u) ^ raw[i];
            }
        }
        return first.ToString(CultureInfo.InvariantCulture) + ":" + second.ToString(CultureInfo.InvariantCulture);
    }
}
