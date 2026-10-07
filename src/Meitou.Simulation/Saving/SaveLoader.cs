using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Save;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Saving;

/// <summary>Settings of <see cref="SaveLoader.Load"/>.</summary>
public sealed class SaveLoadOptions
{
    /// <summary>Put the save's relation table into <c>PopulationData.Relations</c> (it is one table shared by the data; a loaded game replaces its start values).</summary>
    public bool ApplyRelations { get; init; } = true;

    /// <summary>
    /// Make the save's other platoons (not the player's) roaming <see cref="Platoon"/> stand-ins in <see cref="World.Platoons"/>, at their saved positions, when they belong to a placed
    /// town. Their characters are not made: the population system makes the members again from the template when a zone near them is active.
    /// </summary>
    public bool NpcPlatoons { get; init; } = true;

    /// <summary>The look of a spawned character, rolled by this source from the character's saved serial (the saved CHARACTER_APPEARANCE is not read into a drawable look yet). Null: no look.</summary>
    public IAppearanceSource? Appearances { get; init; }
}

/// <summary>
/// Loads a <see cref="SaveGame"/> into a <see cref="World"/> (docs/simulation.md "Saves as built"): the clock, the factions' state and relations, the player's money and
/// platoons with their characters (positions, stats, medical state), the camera's selection, and the other platoons as roaming stand-ins. Everything else a save
/// holds (towns, war state, weather, research, zone buildings, items, appearance sliders) stays in <see cref="LoadedSave.Source"/> for <see cref="SaveCapture"/> to carry on.
/// Call it on a world that has not run a tick yet and holds no characters.
/// </summary>
public static class SaveLoader
{
    public static LoadedSave Load(World world, SaveGame save, PopulationData data, SaveLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(data);
        options ??= new SaveLoadOptions();
        if (world.Tick != 0 || world.Characters.Count != 0) throw new InvalidOperationException("A save is loaded into a world that has not started and has no characters.");

        var camera = save.Camera;
        var loaded = new LoadedSave { Source = save, Clock = new SaveClock(camera.Day, camera.Hour, camera.Minute) };
        var factionIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < data.Factions.Count; i++) factionIndex[data.Factions[i].Id] = i;

        LoadFactions(save, data, options, loaded, factionIndex);
        loaded.RelationBaseline = Snapshot(data.Relations);

        // The player.
        var playerFaction = save.PlayerFaction;
        int playerIndex = playerFaction is not null && factionIndex.TryGetValue(playerFaction.Id, out int pi) ? pi : data.PlayerFaction;
        world.Player.Faction = playerIndex;
        world.Player.Money = camera.PlayerMoney;

        var races = new Dictionary<string, RaceData?>(StringComparer.Ordinal);
        var parts = new Dictionary<string, BodyPartTemplate?>(StringComparer.Ordinal);
        var bySource = new Dictionary<SaveCharacter, CharacterId>();
        foreach (var platoon in save.Platoons)
        {
            bool mine = playerFaction is not null && platoon.FactionId == playerFaction.Id;
            if (mine && platoon.IsLoaded)
                LoadPlayerPlatoon(world, platoon, data, options, loaded, factionIndex, playerIndex, races, parts, bySource);
            else if (!mine && options.NpcPlatoons)
                LoadRoamingPlatoon(world, platoon, data, loaded, factionIndex);
        }
        if (loaded.PlayerPlatoons.Count > 0) world.Player.Squad = loaded.PlayerPlatoons[0].Squad.Id;

        // The selection.
        var handles = save.BuildHandles();
        var selected = new List<Hand> { camera.SelectedCharacter };
        for (int k = 0; camera.Record.Ints.ContainsKey($"selected_characters{k}I"); k++) selected.Add(Hand.Read(camera.Record, $"selected_characters{k}"));
        foreach (var h in selected)
            if (!h.IsNull && handles.Character(h) is { } c && bySource.TryGetValue(c, out var id) && !world.Player.Selection.Contains(id))
                world.Player.Selection.Add(id);
        return loaded;
    }

    internal static float[] Snapshot(FactionRelations relations)
    {
        int n = relations.Count;
        var all = new float[n * n];
        for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++) all[a * n + b] = relations.Get(a, b);
        return all;
    }

    static void LoadFactions(SaveGame save, PopulationData data, SaveLoadOptions options, LoadedSave loaded, Dictionary<string, int> factionIndex)
    {
        foreach (var f in save.Factions)
        {
            var state = new FactionState { Id = f.Id, Name = f.Name, Prosperity = f.Prosperity, PlatoonCounter = f.PlatoonCounter, IsPlayer = f.IsPlayer };
            foreach (var r in f.Relations) state.Relations[r.FactionId] = r;
            loaded.Factions.Add(state);
            if (!options.ApplyRelations || !factionIndex.TryGetValue(f.Id, out int a)) continue;
            foreach (var r in f.Relations)
                if (factionIndex.TryGetValue(r.FactionId, out int b)) data.Relations.Set(a, b, r.Relation);
        }
        foreach (var f in save.Factions)
            if (!factionIndex.ContainsKey(f.Id)) loaded.Notes.Add($"Faction {f.Id} ({f.Name}) is not in the game data; its state is kept in the save only.");
    }

    static void LoadPlayerPlatoon(World world, SavePlatoon platoon, PopulationData data, SaveLoadOptions options, LoadedSave loaded, Dictionary<string, int> factionIndex,
        int playerIndex, Dictionary<string, RaceData?> races, Dictionary<string, BodyPartTemplate?> parts, Dictionary<SaveCharacter, CharacterId> bySource)
    {
        var squad = new Squad
        {
            Id = world.Squads.NextId(),
            TemplateId = platoon.SquadTemplate,
            Name = platoon.Name,
            Faction = playerIndex,
            Position = platoon.Position,
            HomeCentre = new Vector2(platoon.Position.X, platoon.Position.Z),
            HomeRadius = 100,
        };
        var table = world.Characters;
        foreach (var sc in platoon.Characters)
        {
            var record = data.Db.Find(sc.RecordId);
            if (record is null)
            {
                loaded.Notes.Add($"{platoon.Name}: character {sc.Name} is made from {sc.RecordId}, which the game data does not have; left out.");
                continue;
            }
            RaceData? race = RaceOf(sc, record, data.Db, races);
            CharacterStats? stats = sc.Stats is null ? null : CharacterStats.ReadSave(sc.Stats.Floats);
            MedicalState? medical = null;
            if (race is not null && sc.Medical is not null)
                medical = MedicalState.ReadSave(race, sc.Medical.Floats, sc.Medical.Bools, sc.Medical.Strings, id => PartOf(id, data.Db, parts));
            if (medical is { Dead: true })
            {
                loaded.Notes.Add($"{platoon.Name}: {sc.Name} is dead in the save; not made, and kept as it is when saving.");
                continue;
            }

            float yaw = SaveRotation.YawOf(sc.Rotation);
            float athletics = stats?.Athletics ?? 20;
            float speed = race is null ? 45 : medical is not null && stats is not null ? Speed.Run(race, stats, medical, 1) : Speed.RunUnhurt(race, athletics);
            var hot = new CharacterHot
            {
                Position = sc.Position,
                Yaw = yaw,
                Health = 100,
                Task = (byte)CharacterTask.Idle,
                Mode = SpeedMode.Free,
                MaxSpeed = speed,
                WalkSpeed = race?.WalkSpeed ?? 15,
            };
            string ownerId = sc.OwnerFactionId;
            int faction = ownerId.Length > 0 && factionIndex.TryGetValue(ownerId, out int fi) ? fi : playerIndex;
            var handle = sc.Handle;
            CharacterAppearance? look = options.Appearances?.Create(sc.RecordId, ownerId.Length > 0 ? ownerId : null, handle.S & 0x7FFFFFFF);
            var cold = new CharacterCold
            {
                Name = sc.Name.Length > 0 ? sc.Name : record.Name,
                Faction = faction,
                Appearance = look,
                RecordId = sc.RecordId,
                SquadId = squad.Id,
                Role = sc.SquadMemberType,
                IsPlayer = faction == playerIndex,
                FootprintRadius = race?.PathfindFootprintRadius ?? 0,
                Save = new SavedCharacterLink
                {
                    Source = sc,
                    PlatoonName = platoon.Name,
                    Slot = handle.I,
                    Rotation = sc.Rotation,
                    Yaw = yaw,
                    Stats = stats,
                    Medical = medical,
                },
            };
            var id = table.Spawn(hot, cold, world.Tick);
            squad.Members.Add(id);
            bySource[sc] = id;
            loaded.Characters.Add(id);
            loaded.SpawnedSlots.Add((platoon.Name, handle.I));
            if (sc.IsLeader && squad.Leader.IsNone) squad.Leader = id;
        }
        if (squad.Leader.IsNone && squad.Members.Count > 0) squad.Leader = squad.Members[0];
        world.Squads.Add(squad);
        loaded.PlayerPlatoons.Add(new LoadedPlatoon(platoon, squad));
    }

    static void LoadRoamingPlatoon(World world, SavePlatoon platoon, PopulationData data, LoadedSave loaded, Dictionary<string, int> factionIndex)
    {
        string baseTown = platoon.Record.Strings.GetValueOrDefault("basetown", "");
        int origin = -1;
        for (int i = 0; i < data.Sites.Count && baseTown.Length > 0; i++)
            if (data.Sites[i].Town.Id == baseTown) { origin = i; break; }
        if (origin < 0 || platoon.SquadTemplate.Length == 0)
        {
            loaded.Notes.Add($"{platoon.Name}: no placed town for its base town '{baseTown}' or no squad template; its platoon stays in the save only.");
            return;
        }
        var p = new Platoon
        {
            Id = world.Platoons.NextId(),
            Origin = origin,
            TemplateId = platoon.SquadTemplate,
            Faction = factionIndex.GetValueOrDefault(platoon.FactionId, -1),
            Key = Rng.Mix(StableHash(platoon.Name)),
            Size = Math.Max(platoon.CharCount, platoon.Characters.Count),
            Position = new Vector2(platoon.Position.X, platoon.Position.Z),
            State = PlatoonState.Unloaded,
        };
        world.Platoons.Add(p);
        loaded.RoamingPlatoons[p.Id] = platoon.Name;
    }

    static RaceData? RaceOf(SaveCharacter sc, GameRecord record, GameDatabase db, Dictionary<string, RaceData?> cache)
    {
        string? id = sc.Appearance is { } a && a.References.TryGetValue("race", out var list) && list.Count > 0 ? list[0].TargetStringId : null;
        if (id is null)
            foreach (var r in record.GetReferences("race"))
                if (r.Values.Value0 > 0) { id = r.TargetStringId; break; }
        if (id is null) return null;
        if (cache.TryGetValue(id, out var race)) return race;
        race = db.Find(id) is { Type: FcsRecordType.RACE } rec ? RaceData.From(rec, db) : null;
        cache[id] = race;
        return race;
    }

    static BodyPartTemplate? PartOf(string id, GameDatabase db, Dictionary<string, BodyPartTemplate?> cache)
    {
        if (cache.TryGetValue(id, out var part)) return part;
        part = db.Find(id) is { } rec ? BodyPartTemplate.From(rec) : null;
        cache[id] = part;
        return part;
    }

    /// <summary>A string hash that is the same in every process (string.GetHashCode is not).</summary>
    internal static ulong StableHash(string s)
    {
        ulong h = 0xCBF29CE484222325UL;
        foreach (char c in s) h = (h ^ c) * 0x100000001B3UL;
        return h;
    }
}
