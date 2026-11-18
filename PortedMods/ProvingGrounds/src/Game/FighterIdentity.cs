using Meitou.Simulation;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port of upstream/ProvingGrounds/plugin/src/FighterIdentity.h/.cpp (Proving Grounds by Shutori, GPLv3), task 2.3 (PA §2.16, CAP 9.1, 9.2).
//
// Engine calls of the C++ and what replaces them (E1):
//   UuidCreate + UuidToStringA (NewId, FighterIdentity.cpp:51-63) ... no UUID: the token is derived, FighterIdentityMap.Token(
//       PgEngine.PortraitSerial(id), PgEngine.RecordId(id)) = "<serial:x8>-<record id>" (divergences.md)
//   GameSaveState::getState(GAMESTATE_CHARACTER), GameData::sdata / addString (InstanceState, ReadId, Serialise :69-123) ... none: Meitou
//       saves PortraitSerial (SaveCapture.cs:805) and RecordId (SaveCapture.cs:735, read back SavedPopulation.cs:494) for every character it
//       writes, so the token comes back by itself; the list of created tokens is the world's (FighterIdentityMap.Registered, saved by task 2.4)
//   hook Character::_NV_loadFromSerialise (Load :125-144) ... the lazy scan in Refresh: a character first seen begins a lifetime and binds
//       a created token (FighterIdentityMap.Rebuild), PgEngine.AllCharacters + PgEngine.CharacterStamp
//   hook Character::_DESTRUCTOR (Destroy :146-155) ... the same scan: a CharacterId no longer live ends its lifetime
//   Character* (the registry key, compared by address) ... CharacterId (slot + generation): a new generation is a new key = a new lifetime
//   Character::isValid() (in the callers, LeaderboardStore.cpp:41) ... PgEngine.Exists, checked here before every map call
//   KenshiLib::AddHook x3 (InstallHooks :159-184) ... none (no hook); CanPersist always true
//   PGLog::Debug("Proving Grounds fighter identity: ...") (Log :45-49) ... PGLog.Debug (pg_debug.log; the system passes it, task 2.7)
//
// C++ function -> port:
//   InstallHooks (:159-184)     nothing to install: the "hooks ready" / "hook installation failed" / "native adapter disabled" lines have no
//                               counterpart; ready and healthy are always true
//   CanPersist (:186-190)       CanPersist (true)
//   GetOrCreate (:192-218)      GetOrCreate: "create" = register the derived token in the world map and bind it to every live character that
//                               carries it; the duplicate case logs "duplicate generated fighter ID <token>; careers blocked"
//   Find (:220-225)             Find: only a created (or restored), unblocked token, as the C++ (a character never rated has none)
//   AbandonWorldState (:227-231) AbandonWorldState: bindings and blocks dropped; kept for fidelity; unused: one map per world
//   Load hook                   a scan that finds a character with a created token binds it; a second live holder logs "duplicate live fighter
//                               ID <token>; both careers blocked until corrected reload"
//   Destroy hook                the scan ends the lifetime of a CharacterId no longer in the table
//   Serialise hook              none: the token is the character's own saved data (task 2.4 saves FighterIdentityMap.Registered)
// No identity: a character with no PortraitSerial (or 0) gets "" and false from GetOrCreate and Find, as the C++ for a failed UUID or an
// unhealthy adapter; callers skip such a fighter (LeaderboardStore.cpp:254-259, "unavailable or conflicting fighter identity").
// Town NPC fighters (TW §2.2 step 2, TownMatchmakingRuntime.cpp:79): the same calls, nothing special.
//
// Lazy rebuild (CAP 9.1 trap: no CharacterId lookups in IModSaveData.Read, where world.Tick is 0 and some platoons spawn on the first
// tick): Refresh runs in serial steps only (SlowWorld before the frame loop, and before the pg probe in Inputs); it scans world.Characters
// only when CharacterStamp moved since the last scan (a spawn or a removal), so a quiet tick costs two integer reads. A character spawned
// later in the same tick is bound on first use (FighterIdentityMap.Find / GetOrCreate take any live key).

/// <summary>The fighter identity of one world (task 2.3): the career key of a character. One object per world, owned by
/// <see cref="ProvingGroundsSystem"/>. Sim thread, serial steps only (<c>Inputs</c>, <c>SlowWorld</c>), like <see cref="PgEngine"/>.</summary>
sealed class FighterIdentity
{
    readonly FighterIdentityMap<CharacterId> map;

    internal FighterIdentity(Action<string> log) => map = new FighterIdentityMap<CharacterId>(id => id.IsNone, log);

    /// <summary>The world map's created tokens (task 2.4 saves them and gives them back through <see cref="Restore"/>).</summary>
    internal IReadOnlyCollection<string> Registered => map.Registered;

    /// <summary>The created tokens in ordinal order, for the save (task 2.4).</summary>
    internal List<string> RegisteredSorted() => map.RegisteredSorted();

    /// <summary>Task 2.4: the created tokens read from the save. Safe to call in <c>IModSaveData.Read</c>: it looks up no character.</summary>
    internal void Restore(IEnumerable<string> tokens) => map.Restore(tokens);

    /// <summary>The scan, when the table changed since the last one (header). Serial steps only.</summary>
    internal void Refresh(World world)
    {
        var (spawns, count) = PgEngine.CharacterStamp(world);
        if (!map.IsStale(spawns, count)) return;
        map.Rebuild(PgEngine.AllCharacters(world), id => TokenOf(world, id), spawns, count);
    }

    /// <summary>The character's token, or null (no identity: no PortraitSerial, or a stale id).</summary>
    internal static string? TokenOf(World world, CharacterId id) =>
        FighterIdentityMap<CharacterId>.Token(PgEngine.PortraitSerial(world, id), PgEngine.RecordId(world, id));

    // C++: FighterIdentity.cpp:186-190
    internal bool CanPersist => map.CanPersist;

    // C++: FighterIdentity.cpp:192-218 (and LeaderboardStore.cpp:38-45 CharacterKey(c, true): no live character -> "")
    internal bool GetOrCreate(World world, CharacterId character, out string id)
    {
        id = string.Empty;
        if (!PgEngine.Exists(world, character)) return false;
        Refresh(world);
        return map.GetOrCreate(character, TokenOf(world, character), out id);
    }

    // C++: FighterIdentity.cpp:220-225 (and LeaderboardStore.cpp:38-45 CharacterKey(c): no live character -> "")
    internal bool Find(World world, CharacterId character, out string id)
    {
        id = string.Empty;
        if (!PgEngine.Exists(world, character)) return false;
        Refresh(world);
        return map.Find(character, TokenOf(world, character), out id);
    }

    /// <summary>The duplicate guard blocks this character's career (two live characters with its token; sticky for the world).</summary>
    internal bool IsBlocked(World world, CharacterId character)
    {
        if (!PgEngine.Exists(world, character)) return false;
        Refresh(world);
        return map.IsBlocked(character);
    }

    // C++: FighterIdentity.cpp:227-231 (kept for fidelity; unused: one map per world, a new system per load)
    internal void AbandonWorldState() => map.AbandonWorldState();

    /// <summary>TEST ONLY (<c>pg identity-dupes</c>, armed by <c>pg spikes on</c>): every character's token grouped, as one JSON object:
    /// counts, the tokens held by more than one character (with ids, names, record ids, spawn ticks and whether each came from the
    /// loaded save, <c>CharacterCold.Save</c> non-null, CharacterTable.cs:526-531), and every token with its from-save flag (the scenario
    /// compares the lists before and after a load). Measures how often derived tokens repeat (quality review of task 2.3).</summary>
    internal static string DupesJson(World world)
    {
        var ids = PgEngine.AllCharacters(world);
        var byToken = new SortedDictionary<string, List<CharacterId>>(StringComparer.Ordinal);
        int noToken = 0, fromSave = 0;
        foreach (var id in ids)
        {
            if (PgEngine.FromSave(world, id)) fromSave++;
            if (TokenOf(world, id) is not { } t) { noToken++; continue; }
            if (!byToken.TryGetValue(t, out var list)) byToken[t] = list = [];
            list.Add(id);
        }
        var j = new System.Text.StringBuilder(4096);
        int dupTokens = 0, dupCharacters = 0, mixed = 0;
        var ex = new System.Text.StringBuilder();
        foreach (var (t, list) in byToken)
        {
            if (list.Count < 2) continue;
            dupTokens++;
            dupCharacters += list.Count;
            bool anySaved = list.Any(id => PgEngine.FromSave(world, id)), anyNew = list.Any(id => !PgEngine.FromSave(world, id));
            if (anySaved && anyNew) mixed++;
            if (dupTokens > 20) continue;
            ex.Append(ex.Length > 0 ? "," : "").Append("{\"token\":").Append(Q(t)).Append(",\"holders\":[");
            for (int i = 0; i < list.Count; i++)
                ex.Append(i > 0 ? "," : "").Append("{\"id\":").Append(Q(CharacterIdText.Format(list[i]))).Append(",\"name\":").Append(Q(PgEngine.Name(world, list[i])))
                  .Append(",\"spawnedTick\":").Append(PgEngine.SpawnedTick(world, list[i]).ToString(System.Globalization.CultureInfo.InvariantCulture))
                  .Append(",\"fromSave\":").Append(PgEngine.FromSave(world, list[i]) ? "true" : "false").Append('}');
            ex.Append("]}");
        }
        j.Append("{\"verb\":\"identity-dupes\",\"tick\":").Append(world.Tick.ToString(System.Globalization.CultureInfo.InvariantCulture))
         .Append(",\"characters\":").Append(ids.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
         .Append(",\"fromSave\":").Append(fromSave.ToString(System.Globalization.CultureInfo.InvariantCulture))
         .Append(",\"noToken\":").Append(noToken.ToString(System.Globalization.CultureInfo.InvariantCulture))
         .Append(",\"tokens\":").Append(byToken.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
         .Append(",\"dupTokens\":").Append(dupTokens.ToString(System.Globalization.CultureInfo.InvariantCulture))
         .Append(",\"dupCharacters\":").Append(dupCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture))
         .Append(",\"dupSavedAndNew\":").Append(mixed.ToString(System.Globalization.CultureInfo.InvariantCulture))
         .Append(",\"examples\":[").Append(ex).Append("],\"all\":[");
        bool first = true;
        foreach (var (t, list) in byToken)
            foreach (var id in list)
            {
                j.Append(first ? "" : ",").Append(Q(t + (PgEngine.FromSave(world, id) ? "|s" : "|n")));
                first = false;
            }
        return j.Append("]}").ToString();
    }

    static string Q(string? s) => s is null ? "null" : System.Text.Json.JsonSerializer.Serialize(s);
}
