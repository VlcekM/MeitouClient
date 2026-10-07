using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Saving;

/// <summary>Settings of <see cref="SaveCapture.Capture"/>.</summary>
public sealed class SaveCaptureOptions
{
    /// <summary>The data files in load order, for the CAMERA <c>mods</c> list.</summary>
    public IReadOnlyList<string> DataFiles { get; init; } = LoadOrder.BaseFiles;

    /// <summary>The placed towns of the level data, in order. A new save (no loaded one to carry on) writes a default state for each; without them it has no town states.</summary>
    public IReadOnlyList<TownPlacement>? Placements { get; init; }

    /// <summary>Supplies the stats and medical state of a character when it has none from a loaded save (the host's body systems). Null results fall back to the saved ones, then to defaults.</summary>
    public Func<CharacterId, (CharacterStats? Stats, MedicalState? Medical)>? Bodies { get; init; }

    /// <summary>Seeds the random serials of new handles.</summary>
    public ulong Seed { get; init; }
}

/// <summary>
/// Turns a <see cref="World"/> into a <see cref="SaveGame"/> (docs/simulation.md "Saves as built"). With the <see cref="LoadedSave"/> the world was loaded from, the save it came
/// from is copied and the parts the world models are written over it: clock, money, the factions' relations and prosperity, the player's platoons and their characters (position,
/// facing, stats, medical state, new recruits, removals), the selection and the roaming platoons' positions; everything else is carried through unchanged. Without one a new save
/// is made from the game data: all factions with their relations, the player's platoons, a default state per placed town, and empty weather, research and decals.
/// </summary>
public static class SaveCapture
{
    public static SaveGame Capture(World world, PopulationData data, SaveClock clock, LoadedSave? loaded = null, SaveCaptureOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(data);
        options ??= new SaveCaptureOptions();

        SaveBuilder builder;
        if (loaded is not null)
        {
            builder = new SaveBuilder(loaded.Source.Clone(), options.Seed);
        }
        else
        {
            builder = new SaveBuilder(options.Seed);
            AddFreshFactions(builder, data);
            AddFreshTowns(builder, data, options);
            builder.AddEmptyWorldState();
        }
        var game = builder.Game;
        var camera = game.Camera;

        camera.Day = clock.Day;
        camera.Hour = clock.Hour;
        camera.Minute = clock.Minute;
        camera.PlayerMoney = (int)Math.Clamp(world.Player.Money, int.MinValue, int.MaxValue);
        camera.Version = SaveGame.GameVersion;
        camera.SetMods(options.DataFiles);
        if (data.PlayerFaction >= 0) camera.PlayerFactionName = data.Factions[data.PlayerFaction].Name;

        UpdateFactions(game, data, loaded);
        var written = WritePlayerSquads(builder, world, data, loaded, options);
        WriteSelection(game, world, written);
        if (loaded is not null) WriteRoaming(game, world, loaded);

        game.UpdateDerived();
        return game;
    }

    // ------------------------------------------------------------------ factions and towns

    static void AddFreshFactions(SaveBuilder builder, PopulationData data)
    {
        for (int a = 0; a < data.Factions.Count; a++)
        {
            var f = data.Factions[a];
            bool player = a == data.PlayerFaction;
            var table = new List<SaveRelation>();
            if (!player)
                for (int b = 0; b < data.Factions.Count; b++)
                    table.Add(new SaveRelation(data.Factions[b].Id, data.Relations.Get(a, b), 0, 0));
            builder.AddFaction(f.Id, f.Name, player, 1000, table);
        }
    }

    static void AddFreshTowns(SaveBuilder builder, PopulationData data, SaveCaptureOptions options)
    {
        if (options.Placements is null) return;
        foreach (var p in options.Placements)
        {
            if (data.Db.Find(p.TownId) is not { Type: FcsRecordType.TOWN } record) continue;
            var town = TownData.From(record);
            builder.AddTown("Town state " + record.Name, p.InstanceId, town.Faction ?? "", isNest: false, WorldLayout.ZoneOf(p.Position.X, p.Position.Z));
        }
    }

    /// <summary>Writes the prosperity and counters of the loaded factions and the current relation table into the save's factions.</summary>
    static void UpdateFactions(SaveGame game, PopulationData data, LoadedSave? loaded)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < data.Factions.Count; i++) index[data.Factions[i].Id] = i;
        foreach (var f in game.Factions)
        {
            if (loaded?.FactionOf(f.Id) is { } state)
            {
                f.Prosperity = state.Prosperity;
                f.PlatoonCounter = Math.Max(f.PlatoonCounter, state.PlatoonCounter);
            }
            if (loaded is null || !index.TryGetValue(f.Id, out int a) || f.IsPlayer || loaded.RelationBaseline.Length != index.Count * index.Count) continue;
            var table = f.Relations;
            var updated = new List<SaveRelation>(table.Count);
            bool changed = false;
            foreach (var r in table)
            {
                // A value the world changed since loading is written; one it did not touch stays as the save had it.
                float value = r.Relation;
                if (index.TryGetValue(r.FactionId, out int b) && data.Relations.Get(a, b) != loaded.RelationBaseline[a * index.Count + b])
                    value = data.Relations.Get(a, b);
                changed |= value != r.Relation;
                updated.Add(r with { Relation = value });
            }
            if (changed) f.SetRelations(updated);
        }
    }

    // ------------------------------------------------------------------ the player's platoons

    static Dictionary<CharacterId, SaveCharacter> WritePlayerSquads(SaveBuilder builder, World world, PopulationData data, LoadedSave? loaded, SaveCaptureOptions options)
    {
        var game = builder.Game;
        var table = world.Characters;
        var written = new Dictionary<CharacterId, SaveCharacter>();
        var races = new Dictionary<string, RaceData?>(StringComparer.Ordinal);
        string playerFactionId = data.PlayerFaction >= 0 ? data.Factions[data.PlayerFaction].Id : "204-gamedata.base";
        string playerFactionName = data.PlayerFaction >= 0 ? data.Factions[data.PlayerFaction].Name : "Nameless";
        var playerState = game.Factions.FirstOrDefault(f => f.Id == playerFactionId);
        int squads = 0, members = 0;

        // Platoons of the loaded save whose characters the world held: those not alive any more are removed.
        var spawned = loaded?.SpawnedSlots ?? [];
        var seen = new HashSet<(string Platoon, int Slot)>();

        foreach (var squad in world.Squads.All)
        {
            // A member of a loaded platoon is written whoever owns it (a prisoner or slave kept in the squad is not the player's but is in the file); only the
            // resident squads the population system made, which have no link, are left out by the player test.
            var alive = squad.Members.Where(m => table.IsAlive(m) && (table.Cold(m.Slot)!.IsPlayer || table.Cold(m.Slot)!.Save is not null)).ToList();
            if (alive.Count == 0) continue;
            squads++;

            string? name = alive.Select(m => table.Cold(m.Slot)!.Save?.PlatoonName).FirstOrDefault(n => n is not null);
            var platoon = name is null ? null : game.Platoons.FirstOrDefault(p => p.Name == name);
            if (platoon is null)
            {
                int n = playerState?.PlatoonCounter ?? 0;
                string fresh;
                while (game.Platoons.Any(p => p.Name == (fresh = $"{playerFactionName}_{n}"))) n++;
                fresh = $"{playerFactionName}_{n}";
                if (playerState is not null) playerState.PlatoonCounter = n + 1;
                var at = table.Previous[alive[0].Slot].Position;
                platoon = builder.AddPlatoon(fresh, playerFactionName, playerFactionId, squad.TemplateId, at);
            }
            else
            {
                platoon.Position = table.Previous[alive[0].Slot].Position;
            }

            foreach (var id in alive)
            {
                members++;
                var cold = table.Cold(id.Slot)!;
                ref readonly var hot = ref table.Previous[id.Slot];
                SaveCharacter? sc = null;
                if (cold.Save is { } link && link.PlatoonName == platoon.Name)
                {
                    sc = platoon.Characters.FirstOrDefault(c => c.Handle.I == link.Slot);
                    seen.Add((link.PlatoonName, link.Slot));
                }
                bool isNew = sc is null;
                sc ??= builder.AddCharacter(platoon, cold.RecordId, cold.Name, playerFactionId, hot.Position, SaveRotation.OfYaw(hot.Yaw),
                    isLeader: squad.Leader == id, squadMemberType: cold.Role);

                // Where and which way.
                sc.Position = hot.Position;
                var link2 = cold.Save;
                sc.Rotation = link2 is not null && MathF.Abs(SaveRotation.Difference(hot.Yaw, link2.Yaw)) < 1e-4f ? link2.Rotation : SaveRotation.OfYaw(hot.Yaw);
                if (sc.GameState is { } g)
                {
                    g.Strings["name"] = cold.Name;
                    g.Bools["is leader"] = squad.Leader == id;
                }

                // Body: stats and medical state, from the host, else the loaded ones, else (for a new character) defaults.
                var (stats, medical) = options.Bodies?.Invoke(id) ?? (null, null);
                stats ??= link2?.Stats;
                medical ??= link2?.Medical;
                if (stats is null && isNew) stats = new CharacterStats();
                if (stats is not null && sc.Stats is { } sr)
                {
                    stats.WriteSave(sr.Floats);
                    SaveBuilder.FillLegacyStats(sr);
                }
                if (medical is null && isNew && RaceOf(cold.RecordId, data.Db, races) is { } race)
                    medical = MedicalState.Create(race, stats?.Strength ?? 50);
                if (medical is not null && sc.Medical is { } mr)
                    medical.WriteSave(mr.Floats, mr.Bools, mr.Strings);
                if (isNew) WriteAppearance(sc, cold, data.Db, races);
                written[id] = sc;
            }
            platoon.CharCount = platoon.Characters.Count;
            if (platoon.Collection is { } c) c.Ints["char count"] = platoon.Characters.Count;
            platoon.Record.Ints["char count"] = platoon.Characters.Count;
        }

        // Characters of a loaded platoon that the world had and now has no more (killed, left the squad): gone from the file.
        foreach (var (platoonName, slot) in spawned)
        {
            if (seen.Contains((platoonName, slot))) continue;
            var platoon = game.Platoons.FirstOrDefault(p => p.Name == platoonName);
            var sc = platoon?.Characters.FirstOrDefault(c => c.Handle.I == slot);
            if (platoon is not null && sc is not null) builder.RemoveCharacter(platoon, sc);
        }

        game.Camera.Squads = squads;
        game.Camera.Members = members;
        return written;
    }

    static RaceData? RaceOf(string recordId, GameDatabase db, Dictionary<string, RaceData?> cache)
    {
        if (db.Find(recordId) is not { } record) return null;
        foreach (var r in record.GetReferences("race"))
        {
            if (r.Values.Value0 <= 0) continue;
            if (cache.TryGetValue(r.TargetStringId, out var cached)) return cached;
            var race = db.Find(r.TargetStringId) is { Type: FcsRecordType.RACE } rec ? RaceData.From(rec, db) : null;
            cache[r.TargetStringId] = race;
            return race;
        }
        return null;
    }

    /// <summary>
    /// The CHARACTER_APPEARANCE of a new character: the generated one when the character has a look (its sliders, head, hair, skin tone), else only the race and the
    /// editor flags. (Reading the saved look back into a drawable one is not done yet.)
    /// </summary>
    static void WriteAppearance(SaveCharacter sc, CharacterCold cold, GameDatabase db, Dictionary<string, RaceData?> races)
    {
        var target = sc.Appearance;
        if (target is null) return;
        if (cold.Appearance?.Loadout?.Appearance.Record is { } source)
        {
            foreach (var (k, v) in source.Bools) target.Bools[k] = v;
            foreach (var (k, v) in source.Floats) target.Floats[k] = v;
            foreach (var (k, v) in source.Ints) target.Ints[k] = v;
            foreach (var (k, v) in source.Vector3s) target.Vector3s[k] = v;
            foreach (var (k, v) in source.Strings) target.Strings[k] = v;
            foreach (var (k, v) in source.References) target.References[k] = [.. v];
            return;
        }
        target.Bools["sex female"] = false;
        target.Bools["in editor"] = false;
        target.Ints["body version"] = 2;
        target.Ints["Age"] = 20;
        target.Vector3s["Skin Tone"] = new Vector3(0.5f, 0.5f, 0.5f);
        foreach (var k in new[] { "head", "idle stance", "beard", "hair style" }) target.Strings[k] = "";
        if (db.Find(cold.RecordId) is { } record)
            foreach (var r in record.GetReferences("race"))
                if (r.Values.Value0 > 0) { target.References["race"] = [new FcsReference(r.TargetStringId, 0, 0, 0)]; break; }
    }

    static void WriteSelection(SaveGame game, World world, Dictionary<CharacterId, SaveCharacter> written)
    {
        var camera = game.Camera;
        var record = camera.Record;
        for (int k = 0; record.Ints.ContainsKey($"selected_characters{k}I"); k++)
            foreach (var part in new[] { "TYPE", "C", "CS", "I", "S" })
                record.Ints.Remove($"selected_characters{k}{part}");
        int n = 0;
        foreach (var id in world.Player.Selection)
        {
            if (!written.TryGetValue(id, out var sc) || sc.Handle.IsNull) continue;
            if (n == 0) camera.SelectedCharacter = sc.Handle;
            sc.Handle.Write(record, $"selected_characters{n}");
            n++;
        }
        if (n == 0) camera.SelectedCharacter = Hand.Null;
    }

    /// <summary>Puts the roaming platoons' positions into their PLATOON records.</summary>
    static void WriteRoaming(SaveGame game, World world, LoadedSave loaded)
    {
        foreach (var (id, name) in loaded.RoamingPlatoons)
        {
            if (world.Platoons.Find(id) is not { } p) continue;
            var platoon = game.Platoons.FirstOrDefault(x => x.Name == name);
            if (platoon is null) continue;
            var at = platoon.Position;
            platoon.Position = new Vector3(p.Position.X, at.Y, p.Position.Y);
        }
    }
}
