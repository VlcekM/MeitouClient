using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Data.Save;

/// <summary>
/// A whole save: <c>quick.save</c>, the platoon files and the zone files of a save folder, as typed views over their records
/// (docs/formats/save.md). The views hold the records themselves, so changing one changes what <see cref="Write"/> writes; reading and writing
/// again without a change gives the same bytes. <see cref="UpdateDerived"/> brings the parts that follow from the rest (slot lists, counters,
/// CAMERA zone list) up to date after changes.
/// </summary>
public sealed class SaveGame
{
    /// <summary>
    /// The game version the saves are written as (<see cref="SaveCamera.Version"/>); the original refuses nothing at or above 0.99.99.
    /// </summary>
    public const string GameVersion = "1.0.68";

    /// <summary>
    /// The first slot lists 2 and 3 of a new <c>quick.save</c>: every zone container 1..4096 (all saves seen hold at least
    /// these, docs/formats/save.md).
    /// </summary>
    public const int ZoneContainerCount = WorldLayout.ZoneCount * WorldLayout.ZoneCount;

    #region Views and loading

    /// <summary>
    /// The <c>quick.save</c> file.
    /// </summary>
    public SaveFile Quick { get; }

    /// <summary>
    /// The CAMERA record of <c>quick.save</c>.
    /// </summary>
    public SaveCamera Camera { get; private set; } = null!;

    /// <summary>
    /// The GAMESTATE_FACTIONs of <c>quick.save</c>, each with its WAR_SAVESTATE.
    /// </summary>
    public List<SaveFaction> Factions { get; } = [];

    /// <summary>
    /// The GAMESTATE_TOWNs of <c>quick.save</c>.
    /// </summary>
    public List<SaveTown> Towns { get; } = [];

    /// <summary>
    /// The PLATOON records of <c>quick.save</c>, with their files when loaded.
    /// </summary>
    public List<SavePlatoon> Platoons { get; } = [];

    /// <summary>
    /// The zone files, by coordinate (X, then Y).
    /// </summary>
    public SortedDictionary<ZoneCoordinate, SaveZone> Zones { get; } = new(Comparer<ZoneCoordinate>.Create(CompareZones));

    /// <summary>
    /// The two INSTANCE_COLLECTIONs of <c>quick.save</c>: the player's faction, then every other faction (each instance targets a GAMESTATE_FACTION).
    /// </summary>
    public List<FcsRecord> FactionCollections { get; } = [];

    /// <summary>
    /// The GAMESTATE_TOWN_INSTANCE_LIST of <c>quick.save</c>: the towns made in play (nests, player outposts), each with its GAMESTATE_TOWN state.
    /// Set by a writer that adds one.
    /// </summary>
    public FcsRecord? TownInstanceList { get; set; }

    /// <summary>
    /// The BIOMES record of <c>quick.save</c> (the regions' weather), when the save has one.
    /// </summary>
    public FcsRecord? Biomes { get; private set; }

    /// <summary>
    /// The RESEARCH record of <c>quick.save</c>, when the save has one.
    /// </summary>
    public FcsRecord? Research { get; private set; }

    /// <summary>
    /// The TERRAIN_DECALS record of <c>quick.save</c> (<see cref="SaveDecals"/>), when the save has one.
    /// </summary>
    public FcsRecord? Decals { get; private set; }

    /// <summary>
    /// The <c>5-redirect</c> SAVED_STATE (old and new character handles), when the save has one.
    /// </summary>
    public FcsRecord? Redirect { get; private set; }

    /// <summary>
    /// The bytes of <c>portraits_texture.png</c>, if the save has one.
    /// </summary>
    public byte[]? Portraits { get; set; }

    /// <summary>
    /// Meitou's own sidecar files of code mods (<c>meitou-mods/&lt;mod id&gt;.dat</c>, docs/formats/save.md "Mod data"), by mod id. Kept for mods not
    /// loaded now, so a save made from this one carries them on.
    /// </summary>
    public SortedDictionary<string, byte[]> ModData { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The level editor's edits of the session (Meitou's own, in <see cref="SaveFolder.LevelEditsDirectory"/>): per zone the bytes of a zone file in
    /// the form of the editor's saved mod (placed buildings with their state, cleared ones as entries with no target). A save without the folder
    /// (every older save, the original's) has none.
    /// </summary>
    public SortedDictionary<ZoneCoordinate, byte[]> LevelEdits { get; } = new(Comparer<ZoneCoordinate>.Create(CompareZones));

    /// <summary>
    /// The order of <see cref="Zones"/> and <see cref="LevelEdits"/>: by X, then by Y.
    /// </summary>
    static int CompareZones(ZoneCoordinate a, ZoneCoordinate b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y);

    /// <summary>
    /// The bytes of <c>meitou-mods/environment.json</c> (the code mods the save was made with, <c>CodeModEnvironment</c>); null when it has none.
    /// </summary>
    public byte[]? ModEnvironment { get; set; }

    /// <summary>
    /// The bytes of <c>meitou-weapon-blood.json</c> (the blood on the characters' weapons, <see cref="SaveWeaponBlood"/>); null when the save has
    /// none.
    /// </summary>
    public byte[]? WeaponBlood { get; set; }

    /// <summary>
    /// The bytes of <c>meitou-mods/character-ids.json</c> (the lasting ids code mods gave characters, <see cref="SaveLastingIds"/>); null when the
    /// save has none (no mod asked for one).
    /// </summary>
    public byte[]? LastingIds { get; set; }

    /// <summary>
    /// What does not fit the format's own rules (a platoon file that is missing, a zone file the CAMERA list does not name...); loading goes on.
    /// </summary>
    public List<string> Problems { get; } = [];

    /// <summary>
    /// The platoon and zone files the original's loader refuses (<see cref="RejectedType"/>), by name, as read: loaded as no file, as the original
    /// does, and written back unchanged while nothing replaces them (<see cref="CarriedRejects"/>), as the original's save copies a file it did not
    /// rewrite (docs/formats/save.md "Rejected files").
    /// </summary>
    public SortedDictionary<string, byte[]> RejectedFiles { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// A save of <paramref name="quick"/> alone, its views indexed (<see cref="Reindex"/>); throws <see cref="FcsFormatException"/> without a CAMERA.
    /// </summary>
    public SaveGame(SaveFile quick)
    {
        Quick = quick;
        Reindex();
    }

    /// <summary>
    /// Opens a save folder.
    /// </summary>
    public static SaveGame Load(string folder) => Load(SaveFolder.Open(folder));

    /// <summary>
    /// Opens a save folder; <paramref name="cameraRead"/> is told the CAMERA as soon as <c>quick.save</c> is read, before the platoon and zone files.
    /// </summary>
    public static SaveGame Load(SaveFolder folder, Action<SaveCamera>? cameraRead = null)
    {
        if (!folder.HasQuickSave) throw new FileNotFoundException($"'{folder.TopLayer}' has no {SaveFolder.QuickSaveName}.");
        var game = new SaveGame(folder.ReadQuickSave());
        cameraRead?.Invoke(game.Camera);
        game.ReadPlatoonFiles(folder);
        game.ReadZoneFiles(folder);
        game.ReadSidecars(folder);
        game.Reindex();
        game.CheckZoneFiles();
        return game;
    }

    /// <summary>
    /// The file of every platoon that names one; a missing or rejected one is a problem and leaves the platoon without a file.
    /// </summary>
    void ReadPlatoonFiles(SaveFolder folder)
    {
        foreach (var platoon in Platoons)
        {
            if (platoon.FileName is not { } name) continue;
            if (!folder.Exists(name))
            {
                Problems.Add($"Platoon file missing: {name}");
                continue;
            }
            platoon.File = ReadOrReject(folder, name);
        }
    }

    /// <summary>
    /// The zone files of <c>zone/</c>; a rejected one is a problem and the zone is left without a file.
    /// </summary>
    void ReadZoneFiles(SaveFolder folder)
    {
        foreach (var zone in folder.ZoneFiles)
            if (ReadOrReject(folder, SaveFolder.ZoneName(zone)) is { } file)
                Zones[zone] = new SaveZone(zone, file);
    }

    /// <summary>
    /// The files beside the record files: the portraits and Meitou's own (mod data, mod environment, level edits, weapon blood, lasting ids).
    /// </summary>
    void ReadSidecars(SaveFolder folder)
    {
        if (folder.Exists(SaveFolder.PortraitName)) Portraits = folder.ReadAllBytes(SaveFolder.PortraitName);
        foreach (var (id, name) in folder.ModDataFiles) ModData[id] = folder.ReadAllBytes(name);
        if (folder.Exists(SaveFolder.ModEnvironmentName)) ModEnvironment = folder.ReadAllBytes(SaveFolder.ModEnvironmentName);
        foreach (var (zone, name) in folder.LevelEditFiles) LevelEdits[zone] = folder.ReadAllBytes(name);
        if (folder.Exists(SaveFolder.WeaponBloodName)) WeaponBlood = folder.ReadAllBytes(SaveFolder.WeaponBloodName);
        if (folder.Exists(SaveFolder.LastingIdsName)) LastingIds = folder.ReadAllBytes(SaveFolder.LastingIdsName);
    }

    /// <summary>
    /// Reads the platoon or zone file <paramref name="name"/>. One the original's loader refuses (<see cref="RejectedType"/>) is kept in
    /// <see cref="RejectedFiles"/> with a problem, and null is returned; one of an accepted type that does not parse throws an
    /// <see cref="FcsFormatException"/> that names the file.
    /// </summary>
    SaveFile? ReadOrReject(SaveFolder folder, string name)
    {
        var bytes = folder.ReadAllBytes(name);
        if (RejectedType(bytes) is { } reason)
        {
            RejectedFiles[name] = bytes;
            Problems.Add($"File rejected, loaded as none ({reason}): {name}");
            return null;
        }

        try
        {
            return SaveFile.Read(bytes);
        }
        catch (FcsFormatException e)
        {
            throw Named(e, name);
        }
    }

    /// <summary>
    /// Why the original's game-data loader refuses a file of these bytes: shorter than its type, or a type outside 8..17 (docs/formats/fcs-mod.md
    /// "Files that fail to load"); null when it takes it.
    /// </summary>
    static string? RejectedType(byte[] bytes)
    {
        if (bytes.Length < 4) return $"{bytes.Length} bytes";

        int type = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        return type is >= 8 and <= 17 ? null : $"file type {type}";
    }

    /// <summary>
    /// <paramref name="e"/> again with the file name in front of its message.
    /// </summary>
    static FcsFormatException Named(FcsFormatException e, string name)
    {
        string suffix = $" (at byte {e.Offset})";
        string message = e.Message.EndsWith(suffix, StringComparison.Ordinal) ? e.Message[..^suffix.Length] : e.Message;
        return new FcsFormatException($"{name}: {message}", e.Offset, e) { Partial = e.Partial };
    }

    /// <summary>
    /// Sorts the records of <c>quick.save</c> into the views, and builds the platoons' character lists.
    /// </summary>
    public void Reindex()
    {
        Factions.Clear();
        Towns.Clear();
        FactionCollections.Clear();
        var files = Platoons.ToDictionary(p => p.Record, p => p.File);
        Platoons.Clear();
        var wars = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
        foreach (var r in Quick.Records)
            if (r.RecordType == FcsRecordType.WarSavestate && r.Strings.TryGetValue("faction", out var f)) wars.TryAdd(f, r);
        foreach (var r in Quick.Records)
        {
            switch (r.RecordType)
            {
                case FcsRecordType.Camera:
                    Camera = new SaveCamera(r);
                    break;
                case FcsRecordType.GamestateFaction:
                    var faction = new SaveFaction(r, null);
                    faction.War = wars.GetValueOrDefault(faction.Id);
                    Factions.Add(faction);
                    break;
                case FcsRecordType.GamestateTown:
                    Towns.Add(new SaveTown(r));
                    break;
                case FcsRecordType.Platoon:
                    Platoons.Add(new SavePlatoon(r, files.GetValueOrDefault(r)));
                    break;
                case FcsRecordType.InstanceCollection:
                    FactionCollections.Add(r);
                    break;
                case FcsRecordType.GamestateTownInstanceList:
                    TownInstanceList = r;
                    break;
                case FcsRecordType.Biomes:
                    Biomes = r;
                    break;
                case FcsRecordType.Research:
                    Research = r;
                    break;
                case FcsRecordType.TerrainDecals:
                    Decals = r;
                    break;
                case FcsRecordType.SavedState:
                    if (r.StringId.EndsWith("5-redirect", StringComparison.Ordinal) || r.Name == "5-redirect") Redirect = r;
                    break;
            }
        }
        if (Camera is null) throw new FcsFormatException("quick.save has no CAMERA record.", 0);
        foreach (var p in Platoons) BuildCharacters(p);
    }

    /// <summary>
    /// Reads the characters of a platoon file: one instance per character in the file's collection, each pointing at its state records by id.
    /// </summary>
    public static void BuildCharacters(SavePlatoon platoon)
    {
        platoon.Characters.Clear();
        if (platoon.File is not { } file || platoon.Collection is not { } collection) return;
        var byId = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
        foreach (var r in file.Records) byId[r.StringId] = r;
        foreach (var instance in collection.Instances)
        {
            var c = new SaveCharacter(platoon, instance);
            foreach (var id in instance.States)
            {
                if (!byId.TryGetValue(id, out var s)) continue;
                switch (s.RecordType)
                {
                    case FcsRecordType.GamestateCharacter: c.GameState = s; break;
                    case FcsRecordType.GamestateAi: c.Ai = s; break;
                    case FcsRecordType.InventoryState: c.Inventory = s; break;
                    case FcsRecordType.MedicalState: c.Medical = s; break;
                    case FcsRecordType.Stats: c.Stats = s; break;
                    case FcsRecordType.CharacterAppearance: c.Appearance = s; break;
                }
            }
            platoon.Characters.Add(c);
        }
    }

    /// <summary>
    /// All characters of all loaded platoons.
    /// </summary>
    public IEnumerable<SaveCharacter> Characters => Platoons.SelectMany(p => p.Characters);

    /// <summary>
    /// The player's faction: the one with a <c>known</c> list, else the one whose id is <c>204-gamedata.base</c>.
    /// </summary>
    public SaveFaction? PlayerFaction =>
        Factions.FirstOrDefault(f => f.Record.References.ContainsKey("known")) ?? Factions.FirstOrDefault(f => f.Id == Gameplay.FactionData.PlayerId);

    /// <summary>
    /// The zone-file check the game runs at load and at save (docs/formats/save.md, <c>FUN_140a0bef0</c>): the CAMERA <c>zones</c> list against the
    /// files. Adds "Zone file missing" and "Extra zone file" problems; a rejected zone file has its own problem.
    /// </summary>
    public void CheckZoneFiles()
    {
        var listed = Camera.Zones.ToHashSet();
        var rejected = RejectedZones().ToHashSet();
        foreach (var z in listed.Where(z => !Zones.ContainsKey(z) && !rejected.Contains(z))) Problems.Add($"Zone file missing: zone.{z.X}.{z.Y}.zone");
        foreach (var z in Zones.Keys.Where(z => !listed.Contains(z))) Problems.Add($"Extra zone file: zone.{z.X}.{z.Y}.zone");
    }

    /// <summary>
    /// The zones of the rejected files in <see cref="RejectedFiles"/>.
    /// </summary>
    IEnumerable<ZoneCoordinate> RejectedZones()
    {
        foreach (var name in RejectedFiles.Keys)
            if (SaveFolder.TryParseZoneName(name, out var zone))
                yield return zone;
    }

    /// <summary>
    /// A deep copy made by writing every file to memory and reading it back: nothing is shared with the original.
    /// </summary>
    public SaveGame Clone()
    {
        var copy = new SaveGame(SaveFile.Read(Quick.ToBytes()));
        foreach (var p in Platoons)
        {
            var q = copy.Platoons.First(x => x.Record.StringId == p.Record.StringId);
            if (p.File is not null) q.File = SaveFile.Read(p.File.ToBytes());
        }
        foreach (var z in Zones.Values) copy.Zones[z.Coordinate] = new SaveZone(z.Coordinate, SaveFile.Read(z.File.ToBytes()));
        copy.Portraits = Portraits?.ToArray();
        foreach (var (id, bytes) in ModData) copy.ModData[id] = bytes.ToArray();
        copy.ModEnvironment = ModEnvironment?.ToArray();
        foreach (var (zone, bytes) in LevelEdits) copy.LevelEdits[zone] = bytes.ToArray();
        copy.WeaponBlood = WeaponBlood?.ToArray();
        copy.LastingIds = LastingIds?.ToArray();
        foreach (var (name, bytes) in RejectedFiles) copy.RejectedFiles[name] = bytes.ToArray();
        copy.Reindex();
        copy.Problems.AddRange(Problems);
        return copy;
    }

    #endregion

    #region Handles

    /// <summary>
    /// An index for resolving handles against this save's platoons, characters, towns and zone buildings (rebuild it after changing them).
    /// </summary>
    public SaveHandles BuildHandles() => new(this);

    #endregion

    #region Derived data

    /// <summary>
    /// Brings the parts that follow from the rest up to date: the CAMERA <c>zones</c> list from the zone files (and the carried rejected ones it
    /// listed, <see cref="ListedCarriedZones"/>), the character count of every platoon
    /// with a file and the file name, slot lists 0 (platoon containers) and 1 (town containers) of <c>quick.save</c> (lists 2 and 3 are kept, or
    /// start as 1..4096: their meaning is Unknown), a zone file's slot list when it has none, each file's <c>nextId</c> (raised to its highest record
    /// id, never lowered) and the <c>byteSize</c> of the records that carry a child count.
    /// </summary>
    public void UpdateDerived()
    {
        Camera.SetZones(Zones.Keys.Concat(ListedCarriedZones()));
        UpdatePlatoonCounts();
        UpdateSlotLists();
        FillZoneSlotLists();
        UpdateFileCounters();
    }

    /// <summary>
    /// The zones of the rejected files a write carries on (<see cref="CarriedRejects"/>) that the CAMERA <c>zones</c> list names: they stay in it,
    /// as the original keeps a zone's has-file flag until it loads the zone again.
    /// </summary>
    IEnumerable<ZoneCoordinate> ListedCarriedZones()
    {
        var listed = Camera.Zones.ToHashSet();
        return RejectedZones().Where(z => !Zones.ContainsKey(z) && listed.Contains(z)).ToList();
    }

    /// <summary>
    /// The character count of every platoon with a file, its collection's <c>char count</c> and the file name.
    /// </summary>
    void UpdatePlatoonCounts()
    {
        foreach (var p in Platoons)
        {
            // A platoon with no file has no characters to count: its count is the save's (the town pool counts it while the platoon is not active).
            if (p.File is not null) p.CharCount = p.Characters.Count;
            if (p.Collection is { } c) c.Ints["char count"] = p.Characters.Count;
            if (p.File is not null && p.FileName is null) p.ContentFile = SaveFolder.PlatoonName(p.Record.StringId);
        }
    }

    /// <summary>
    /// Slot lists 0 (platoon containers) and 1 (town containers, also those any handle of any file names) of <c>quick.save</c>; lists 2 and 3 are kept,
    /// or start as 1..4096.
    /// </summary>
    void UpdateSlotLists()
    {
        var platoonContainers = new SortedSet<int>(Platoons.Select(p => p.Handle).Where(h => !h.IsNull).Select(h => h.C));
        var townContainers = new SortedSet<int>(Towns.Select(t => t.Handle).Where(h => !h.IsNull).Select(h => h.C));
        foreach (var (_, file) in AllFiles())
            foreach (var record in file.Records)
                AddTownContainers(record, townContainers);

        var lists = Quick.SlotLists;
        while (lists.Count < 4) lists.Add(lists.Count >= 2 ? Enumerable.Range(1, ZoneContainerCount).ToArray() : []);
        lists[0] = [.. platoonContainers];
        lists[1] = [.. townContainers];
    }

    /// <summary>
    /// Adds the containers of town handles without reading the parts of unrelated handles. As in <see cref="Hand.Read"/>, a handle must have a
    /// slot key and a missing container key reads as zero.
    /// </summary>
    static void AddTownContainers(FcsRecord record, SortedSet<int> containers)
    {
        foreach (var (key, type) in record.Ints)
        {
            if (type is not ((int)FcsRecordType.Town or (int)FcsRecordType.Nest) || !key.EndsWith("TYPE", StringComparison.Ordinal)) continue;

            var prefix = key.AsSpan(0, key.Length - 4);
            if (record.Ints.ContainsKey(string.Concat(prefix, "I")))
                containers.Add(record.Ints.GetValueOrDefault(string.Concat(prefix, "C")));
        }
    }

    /// <summary>
    /// A zone file without a slot list gets one: the slots of its building states' handles in its own container.
    /// </summary>
    void FillZoneSlotLists()
    {
        foreach (var zone in Zones.Values)
            if (zone.File.SlotLists.Count == 0)
            {
                var slots = new SortedSet<int>();
                foreach (var s in zone.BuildingStates)
                    foreach (var prefix in Hand.Prefixes(s))
                        if (Hand.Read(s, prefix) is { IsNull: false } h && h.C == zone.ContainerId) slots.Add(h.I);
                zone.File.SlotLists.Add([.. slots]);
            }
    }

    /// <summary>
    /// Each file's <c>nextId</c> (raised to its highest record id, never lowered) and its records' <c>byteSize</c>.
    /// </summary>
    void UpdateFileCounters()
    {
        foreach (var (name, file) in AllFiles().Select(f => (f.Name, f.File)))
        {
            // A file the mod editor saved (type 17, numeric ids 0, real byte sizes) is left as it is.
            if (file.Data.FileType != FcsFileType.V15) continue;
            // The id counter only grows (Observed: the original's nextId can be above the file's highest record id).
            file.Data.NextId = Math.Max(file.Data.NextId, file.Records.Count == 0 ? 0 : file.Records.Max(r => r.Id));
            foreach (var r in file.Records) r.ByteSize = ChildCountSize(r, name);
        }
    }

    /// <summary>
    /// The <c>byteSize</c> the game writes (docs/formats/save.md, counted over all three saves): the instance count for the records that keep one
    /// (the collections of <c>quick.save</c>, a zone's <c>1-itemlist</c>, INVENTORY_STATE, INVENTORY_ITEM_STATE, ITEM_PLACEMENT_GROUP), else 0.
    /// </summary>
    public static uint ChildCountSize(FcsRecord r, string fileName)
    {
        if (r.Instances.Count == 0) return 0;
        switch (r.RecordType)
        {
            case FcsRecordType.InventoryState or FcsRecordType.InventoryItemState or FcsRecordType.ItemPlacementGroup:
                return (uint)r.Instances.Count;
            case FcsRecordType.InstanceCollection:
                return fileName == SaveFolder.QuickSaveName || r.StringId.EndsWith("-itemlist", StringComparison.Ordinal) ? (uint)r.Instances.Count : 0;
            default:
                return 0;
        }
    }

    #endregion

    #region Writing

    /// <summary>
    /// Every file of the save as (relative name, file): <c>quick.save</c>, then the platoon files, then the zone files.
    /// </summary>
    public IEnumerable<(string Name, SaveFile File)> AllFiles()
    {
        yield return (SaveFolder.QuickSaveName, Quick);
        foreach (var p in Platoons)
            if (p.File is not null && p.FileName is { } name)
                yield return (name, p.File);
        foreach (var z in Zones.Values)
            yield return (SaveFolder.ZoneName(z.Coordinate), z.File);
    }

    /// <summary>
    /// The rejected files (<see cref="RejectedFiles"/>) a write carries on byte for byte: a zone's while the zone has no file now, a platoon's while
    /// a PLATOON record without a file still names it. The original never rewrites a file it did not load (an unloaded platoon's is written only
    /// with its loaded flag set, <c>FUN_14036bad0</c>), and its save copies the loaded save's files under the ones it wrote (<c>FUN_140472ac0</c>).
    /// </summary>
    public IEnumerable<(string Name, byte[] Bytes)> CarriedRejects()
    {
        var named = Platoons.Where(p => p.File is null).Select(p => p.FileName).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var (name, bytes) in RejectedFiles)
        {
            bool carried = SaveFolder.TryParseZoneName(name, out var zone) ? !Zones.ContainsKey(zone) : named.Contains(name);
            if (carried) yield return (name, bytes);
        }
    }

    /// <summary>
    /// Writes the save into <paramref name="folder"/> through a <see cref="SaveWriter"/>: into a working folder beside it, then in place of the old
    /// one in two renames. <paramref name="beforeInstall"/> runs between the two renames.
    /// </summary>
    public void Write(string folder, Action? beforeInstall = null) => new SaveWriter(this).Write(folder, beforeInstall);

    #endregion

    #region Record numbers

    /// <summary>
    /// The number in the front of a record's string id (<c>12-quick.save-INGAME</c> is 12), or -1 when it has none.
    /// </summary>
    public static int NumberOf(FcsRecord r)
    {
        ReadOnlySpan<char> id = r.StringId;
        int digits = 0;
        while (digits < id.Length && char.IsAsciiDigit(id[digits])) digits++;

        if (digits == 0 || digits == id.Length || id[digits] != '-') return -1;
        return int.TryParse(id[..digits], NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : -1;
    }

    #endregion
}
