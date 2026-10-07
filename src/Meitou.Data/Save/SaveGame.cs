using System.Numerics;
using System.Text.RegularExpressions;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Data.Save;

/// <summary>
/// A whole save: <c>quick.save</c>, the platoon files and the zone files of a save folder, as typed views over their records (docs/formats/save.md).
/// The views hold the records themselves, so changing one changes what <see cref="Write"/> writes; reading and writing again without a change gives the
/// same bytes. <see cref="UpdateDerived"/> brings the parts that follow from the rest (slot lists, counters, CAMERA zone list) up to date after changes.
/// </summary>
public sealed partial class SaveGame
{
    /// <summary>The game version the saves are written as (<see cref="SaveCamera.Version"/>); the original refuses nothing at or above 0.99.99.</summary>
    public const string GameVersion = "1.0.68";

    /// <summary>The first slot lists 2 and 3 of a new <c>quick.save</c>: every zone container 1..4096 (all saves seen hold at least these, docs/formats/save.md).</summary>
    public const int ZoneContainerCount = WorldLayout.ZoneCount * WorldLayout.ZoneCount;

    public SaveFile Quick { get; }
    public SaveCamera Camera { get; private set; } = null!;
    public List<SaveFaction> Factions { get; } = [];
    public List<SaveTown> Towns { get; } = [];
    public List<SavePlatoon> Platoons { get; } = [];
    public SortedDictionary<ZoneCoordinate, SaveZone> Zones { get; } = new(Comparer<ZoneCoordinate>.Create((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y)));

    /// <summary>The two INSTANCE_COLLECTIONs of <c>quick.save</c>: the player's faction, then every other faction (each instance targets a GAMESTATE_FACTION).</summary>
    public List<FcsRecord> FactionCollections { get; } = [];
    public FcsRecord? TownInstanceList { get; private set; }
    public FcsRecord? Biomes { get; private set; }
    public FcsRecord? Research { get; private set; }
    public FcsRecord? Decals { get; private set; }

    /// <summary>The <c>5-redirect</c> SAVED_STATE (old and new character handles), when the save has one.</summary>
    public FcsRecord? Redirect { get; private set; }

    /// <summary>The bytes of <c>portraits_texture.png</c>, if the save has one.</summary>
    public byte[]? Portraits { get; set; }

    /// <summary>What does not fit the format's own rules (a platoon file that is missing, a zone file the CAMERA list does not name...); loading goes on.</summary>
    public List<string> Problems { get; } = [];

    public SaveGame(SaveFile quick)
    {
        Quick = quick;
        Reindex();
    }

    /// <summary>Opens a save folder.</summary>
    public static SaveGame Load(string folder) => Load(SaveFolder.Open(folder));

    public static SaveGame Load(SaveFolder folder)
    {
        if (!folder.HasQuickSave) throw new FileNotFoundException($"'{folder.TopLayer}' has no {SaveFolder.QuickSaveName}.");
        var game = new SaveGame(folder.ReadQuickSave());
        foreach (var platoon in game.Platoons)
        {
            if (platoon.FileName is not { } name) continue;
            if (!folder.Exists(name))
            {
                game.Problems.Add($"Platoon file missing: {name}");
                continue;
            }
            platoon.File = folder.ReadFile(name);
        }
        foreach (var zone in folder.ZoneFiles)
            game.Zones[zone] = new SaveZone(zone, folder.ReadFile(SaveFolder.ZoneName(zone)));
        if (folder.Exists(SaveFolder.PortraitName)) game.Portraits = folder.ReadAllBytes(SaveFolder.PortraitName);
        game.Reindex();
        game.CheckZoneFiles();
        return game;
    }

    /// <summary>Sorts the records of <c>quick.save</c> into the views, and builds the platoons' character lists.</summary>
    public void Reindex()
    {
        Factions.Clear();
        Towns.Clear();
        FactionCollections.Clear();
        var files = Platoons.ToDictionary(p => p.Record, p => p.File);
        Platoons.Clear();
        var wars = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
        foreach (var r in Quick.Records)
            if (r.RecordType == FcsRecordType.WAR_SAVESTATE && r.Strings.TryGetValue("faction", out var f)) wars.TryAdd(f, r);
        foreach (var r in Quick.Records)
        {
            switch (r.RecordType)
            {
                case FcsRecordType.CAMERA: Camera = new SaveCamera(r); break;
                case FcsRecordType.GAMESTATE_FACTION:
                    var faction = new SaveFaction(r, null);
                    faction.War = wars.GetValueOrDefault(faction.Id);
                    Factions.Add(faction);
                    break;
                case FcsRecordType.GAMESTATE_TOWN: Towns.Add(new SaveTown(r)); break;
                case FcsRecordType.PLATOON: Platoons.Add(new SavePlatoon(r, files.GetValueOrDefault(r))); break;
                case FcsRecordType.INSTANCE_COLLECTION: FactionCollections.Add(r); break;
                case FcsRecordType.GAMESTATE_TOWN_INSTANCE_LIST: TownInstanceList = r; break;
                case FcsRecordType.BIOMES: Biomes = r; break;
                case FcsRecordType.RESEARCH: Research = r; break;
                case FcsRecordType.TERRAIN_DECALS: Decals = r; break;
                case FcsRecordType.SAVED_STATE: if (r.StringId.EndsWith("5-redirect", StringComparison.Ordinal) || r.Name == "5-redirect") Redirect = r; break;
            }
        }
        if (Camera is null) throw new FcsFormatException("quick.save has no CAMERA record.", 0);
        foreach (var p in Platoons) BuildCharacters(p);
    }

    /// <summary>Reads the characters of a platoon file: one instance per character in the file's collection, each pointing at its state records by id.</summary>
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
                    case FcsRecordType.GAMESTATE_CHARACTER: c.GameState = s; break;
                    case FcsRecordType.GAMESTATE_AI: c.Ai = s; break;
                    case FcsRecordType.INVENTORY_STATE: c.Inventory = s; break;
                    case FcsRecordType.MEDICAL_STATE: c.Medical = s; break;
                    case FcsRecordType.STATS: c.Stats = s; break;
                    case FcsRecordType.CHARACTER_APPEARANCE: c.Appearance = s; break;
                }
            }
            platoon.Characters.Add(c);
        }
    }

    /// <summary>All characters of all loaded platoons.</summary>
    public IEnumerable<SaveCharacter> Characters => Platoons.SelectMany(p => p.Characters);

    /// <summary>The player's faction: the one with a <c>known</c> list, else the one whose id is <c>204-gamedata.base</c>.</summary>
    public SaveFaction? PlayerFaction =>
        Factions.FirstOrDefault(f => f.Record.References.ContainsKey("known")) ?? Factions.FirstOrDefault(f => f.Id == "204-gamedata.base");

    /// <summary>
    /// The zone-file check the game runs at load and at save (docs/formats/save.md, <c>FUN_140a0bef0</c>): the CAMERA <c>zones</c> list against the files.
    /// Adds "Zone file missing" and "Extra zone file" problems.
    /// </summary>
    public void CheckZoneFiles()
    {
        var listed = Camera.Zones.ToHashSet();
        foreach (var z in listed.Where(z => !Zones.ContainsKey(z))) Problems.Add($"Zone file missing: zone.{z.X}.{z.Y}.zone");
        foreach (var z in Zones.Keys.Where(z => !listed.Contains(z))) Problems.Add($"Extra zone file: zone.{z.X}.{z.Y}.zone");
    }

    // ------------------------------------------------------------------ handles

    /// <summary>An index for resolving handles against this save's platoons, characters, towns and zone buildings (rebuild it after changing them).</summary>
    public SaveHandles BuildHandles() => new(this);

    // ------------------------------------------------------------------ derived data

    /// <summary>
    /// Brings the parts that follow from the rest up to date: the CAMERA <c>zones</c> list from the zone files, every platoon's character count and file name,
    /// slot lists 0 (platoon containers) and 1 (town containers) of <c>quick.save</c> (lists 2 and 3 are kept, or start as 1..4096: their meaning is Unknown), a zone file's
    /// slot list when it has none, each file's <c>nextId</c> and the <c>byteSize</c> of the records that carry a child count.
    /// </summary>
    public void UpdateDerived()
    {
        Camera.SetZones(Zones.Keys);
        foreach (var p in Platoons)
        {
            p.CharCount = p.Characters.Count;
            p.Collection?.Ints.Remove("char count");
            if (p.Collection is { } c) c.Ints["char count"] = p.Characters.Count;
            if (p.File is not null && p.FileName is null) p.ContentFile = SaveFolder.PlatoonName(p.Record.StringId);
        }

        var platoonContainers = new SortedSet<int>(Platoons.Select(p => p.Handle).Where(h => !h.IsNull).Select(h => h.C));
        var townContainers = new SortedSet<int>(Towns.Select(t => t.Handle).Where(h => !h.IsNull).Select(h => h.C));
        foreach (var file in AllFiles().Select(f => f.File))
            foreach (var r in file.Records)
                foreach (var prefix in Hand.Prefixes(r))
                    if (Hand.Read(r, prefix) is { IsNull: false, Type: (int)FcsRecordType.TOWN or 85 } h)
                        townContainers.Add(h.C);
        var lists = Quick.SlotLists;
        while (lists.Count < 4) lists.Add(lists.Count >= 2 ? Enumerable.Range(1, ZoneContainerCount).ToArray() : []);
        lists[0] = [.. platoonContainers];
        lists[1] = [.. townContainers];

        foreach (var zone in Zones.Values)
            if (zone.File.SlotLists.Count == 0)
            {
                var slots = new SortedSet<int>();
                foreach (var s in zone.BuildingStates)
                    foreach (var prefix in Hand.Prefixes(s))
                        if (Hand.Read(s, prefix) is { IsNull: false } h && h.C == zone.ContainerId) slots.Add(h.I);
                zone.File.SlotLists.Add([.. slots]);
            }

        foreach (var (name, file) in AllFiles().Select(f => (f.Name, f.File)))
        {
            file.Data.NextId = file.Records.Count == 0 ? 0 : file.Records.Max(r => r.Id);
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
            case FcsRecordType.INVENTORY_STATE or FcsRecordType.INVENTORY_ITEM_STATE or FcsRecordType.ITEM_PLACEMENT_GROUP:
                return (uint)r.Instances.Count;
            case FcsRecordType.INSTANCE_COLLECTION:
                return fileName == SaveFolder.QuickSaveName || r.StringId.EndsWith("-itemlist", StringComparison.Ordinal) ? (uint)r.Instances.Count : 0;
            default:
                return 0;
        }
    }

    // ------------------------------------------------------------------ writing

    /// <summary>Every file of the save as (relative name, file): <c>quick.save</c>, then the platoon files, then the zone files.</summary>
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
    /// Writes the save into <paramref name="folder"/> the way the game does (docs/formats/save.md "The save file system"): first into a working folder
    /// <c>_current&lt;N&gt;</c> beside it (the first N whose folder does not exist), then the finished folder replaces the old one in two renames, so a crash
    /// leaves either the old save or the new one, never half of one. The old folder's contents are replaced as a whole: files the new save no longer has are gone.
    /// </summary>
    public void Write(string folder)
    {
        folder = Path.GetFullPath(folder);
        var parent = Path.GetDirectoryName(folder) ?? throw new ArgumentException("A save folder needs a parent folder.", nameof(folder));
        Directory.CreateDirectory(parent);
        int n = 1;
        string work;
        while (Directory.Exists(work = Path.Combine(parent, "_current" + n))) n++;
        Directory.CreateDirectory(work);
        try
        {
            Directory.CreateDirectory(Path.Combine(work, SaveFolder.PlatoonDirectory));
            Directory.CreateDirectory(Path.Combine(work, SaveFolder.ZoneDirectory));
            foreach (var (name, file) in AllFiles())
                file.WriteFile(Path.Combine(work, name.Replace('/', Path.DirectorySeparatorChar)));
            if (Portraits is not null) File.WriteAllBytes(Path.Combine(work, SaveFolder.PortraitName), Portraits);

            string? old = null;
            if (Directory.Exists(folder))
            {
                old = folder + ".old";
                if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
                Directory.Move(folder, old);
            }
            Directory.Move(work, folder);
            if (old is not null) Directory.Delete(old, recursive: true);
        }
        catch
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            throw;
        }
    }

    [GeneratedRegex(@"^(\d+)-")]
    private static partial Regex NumberPrefix();

    /// <summary>The number in the front of a record's string id (<c>12-quick.save-INGAME</c> is 12), or -1 when it has none.</summary>
    public static int NumberOf(FcsRecord r) => NumberPrefix().Match(r.StringId) is { Success: true } m && int.TryParse(m.Groups[1].Value, out int v) ? v : -1;
}

/// <summary>
/// Resolves <see cref="Hand"/>s against a save: platoon handles by the platoon's container, character handles by (container, slot) within the platoon,
/// town and nest handles by the town's container, building handles by (zone container, slot) with the slot taken from the buildings' own handles.
/// </summary>
public sealed class SaveHandles
{
    readonly Dictionary<int, SavePlatoon> platoons = [];
    readonly Dictionary<(int C, int I), SaveCharacter> characters = [];
    readonly Dictionary<int, SaveTown> towns = [];
    readonly Dictionary<(int C, int I), FcsRecord> buildings = [];

    internal SaveHandles(SaveGame game)
    {
        foreach (var p in game.Platoons)
        {
            if (!p.Handle.IsNull) platoons[p.Handle.C] = p;
            foreach (var c in p.Characters)
                if (!c.Handle.IsNull) characters[(c.Handle.C, c.Handle.I)] = c;
        }
        foreach (var t in game.Towns)
            if (!t.Handle.IsNull) towns[t.Handle.C] = t;
        foreach (var z in game.Zones.Values)
            foreach (var s in z.BuildingStates)
                if (Hand.Read(s, "handle") is { IsNull: false } h) buildings[(h.C, h.I)] = s;
    }

    public SavePlatoon? Platoon(Hand h) => h.Type == (int)FcsRecordType.PLATOON ? platoons.GetValueOrDefault(h.C) : null;
    public SaveCharacter? Character(Hand h) => h.Type == (int)FcsRecordType.CHARACTER ? characters.GetValueOrDefault((h.C, h.I)) : null;
    public SaveTown? Town(Hand h) => h.Type is (int)FcsRecordType.TOWN or 85 ? towns.GetValueOrDefault(h.C) : null;
    public FcsRecord? Building(Hand h) => h.Type == (int)FcsRecordType.BUILDING ? buildings.GetValueOrDefault((h.C, h.I)) : null;

    /// <summary>The object a non-null handle names, or null when this save does not hold it.</summary>
    public object? Resolve(Hand h) =>
        h.IsNull ? null : h.Type switch
        {
            (int)FcsRecordType.PLATOON => Platoon(h),
            (int)FcsRecordType.CHARACTER => Character(h),
            (int)FcsRecordType.TOWN or 85 => Town(h),
            (int)FcsRecordType.BUILDING => Building(h),
            _ => null,
        };
}
