using Meitou.Content;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Ogre;

namespace Meitou.Data.Gameplay;

/// <summary>
/// The length in seconds of every clip of the humanoid skeletons (what a synched movement clip needs to turn a phase into a time); per person, the
/// clips its body plays with its linked animation files (<see cref="ForPerson"/>); and, per animal race, the clips of that race's own skeleton
/// (<see cref="For"/>: an animal's clips are in the skeleton its body mesh links, and their names repeat across species with other lengths).
/// </summary>
public sealed class AnimationLengths
{
    /// <summary>
    /// Clip lengths in seconds by name (ignoring case); the first skeleton to name a clip wins, the base skeletons being read first.
    /// </summary>
    readonly Dictionary<string, float> _lengths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Each clip's <c>Bip01</c> track as key times and (X, Z) offsets, by clip name (ignoring case).
    /// </summary>
    readonly Dictionary<string, (float[] Times, System.Numerics.Vector2[] Offsets)> _roots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The tables of the animal races' own skeletons, by RACE string id.
    /// </summary>
    readonly Dictionary<string, AnimationLengths> _species = new(StringComparer.Ordinal);

    /// <summary>
    /// The clips of each ANIMATION_FILE skeleton read, by full path, one table per file (<see cref="ForPerson"/> layers them).
    /// </summary>
    readonly Dictionary<string, AnimationLengths> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The tables of the people's bodies, by race, CHARACTER record and sex: their linked files' clips, then this table's (<see cref="ForPerson"/>).
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<(string Race, string? Record, bool Female), AnimationLengths> _people = new();

    /// <summary>
    /// The people's tables by the found files they layer (full paths joined in link order), shared by every body linking the same ones.
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, AnimationLengths> _linkSets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The data the people's animation files are read from, and the bare file name to full path lookup; null without linked files.
    /// </summary>
    (GameDatabase Db, Func<string, string?> Find)? _linking;

    /// <summary>
    /// A table with no clips.
    /// </summary>
    public static readonly AnimationLengths Empty = new();

    /// <summary>
    /// The clips of every <c>*_skeleton.skeleton</c> under <c>data/character/meshes</c> (the first file to name a clip wins); with
    /// <paramref name="db"/> also the clips of the skeleton each ANIMAL_CHARACTER's race's body mesh links (<see cref="For"/>), read in
    /// parallel, one table per skeleton file. With <paramref name="find"/> (a bare file name to its full path, as the game's resource groups find it)
    /// the clips of the ANIMATION_FILE skeletons follow, in link order (<see cref="AnimationFiles"/>), for the clips no base skeleton has; a person
    /// takes its own linked files' clips first (<see cref="ForPerson"/>).
    /// </summary>
    public static AnimationLengths Load(string installRoot, GameDatabase? db = null, Func<string, string?>? find = null)
    {
        var result = new AnimationLengths();
        foreach (var file in BaseSkeletons(installRoot)) result.AddSkeleton(file);
        if (db is not null && find is not null) result.LoadLinked(db, find);
        if (db is not null) result.LoadAnimals(installRoot, db, find);
        return result;
    }

    /// <summary>
    /// The base game's humanoid skeleton files (<c>*_skeleton.skeleton</c> under <c>data/character/meshes</c>), by path.
    /// </summary>
    static List<string> BaseSkeletons(string installRoot)
    {
        var dir = Path.Combine(installRoot, "data", "character", "meshes");
        if (!Directory.Exists(dir)) return [];

        return [.. Directory.EnumerateFiles(dir, "*_skeleton.skeleton", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Reads every ANIMATION_FILE's skeleton (<see cref="AnimationFiles.All"/>) into a table of its own, in parallel, and adds their clips the base
    /// skeletons lack, in link order; a file not found or not readable is left out, as the game links only the ones it finds.
    /// </summary>
    void LoadLinked(GameDatabase db, Func<string, string?> find)
    {
        var found = AnimationFiles.All(db).Select(f => find(AnimationFiles.BareName(f)));
        var paths = found.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var read = new OgreSkeleton?[paths.Count];
        Parallel.For(0, paths.Count, i => read[i] = TryRead(paths[i]));

        for (int i = 0; i < paths.Count; i++)
        {
            if (read[i] is not { } skeleton) continue;

            var table = new AnimationLengths();
            table.AddSkeleton(skeleton);
            _files[paths[i]] = table;
            AddTable(table);
        }

        _linking = (db, find);
    }

    /// <summary>
    /// Adds the clips of skeleton files (full paths, in order; a file listed twice is read once), read in parallel; one that cannot be read is
    /// left out.
    /// </summary>
    void AddFiles(IReadOnlyList<string> files)
    {
        var paths = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var read = new OgreSkeleton?[paths.Count];
        Parallel.For(0, paths.Count, i => read[i] = TryRead(paths[i]));

        foreach (var skeleton in read)
            if (skeleton is not null) AddSkeleton(skeleton);
    }

    /// <summary>
    /// The skeleton in <paramref name="file"/>; null when it cannot be read.
    /// </summary>
    static OgreSkeleton? TryRead(string file)
    {
        try
        {
            return OgreSkeletonReader.ReadFile(file);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or FcsFormatException or OgreFormatException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>
    /// Adds the clips of a skeleton file (a clip already known keeps its first length).
    /// </summary>
    void AddSkeleton(string file)
    {
        if (TryRead(file) is { } skeleton) AddSkeleton(skeleton);
    }

    /// <summary>
    /// Adds the clips of <paramref name="skeleton"/> (a clip already known keeps its first length) and, for a new clip, its <c>Bip01</c> track.
    /// </summary>
    void AddSkeleton(OgreSkeleton skeleton)
    {
        var root = skeleton.Bones.FirstOrDefault(b => b.Name == "Bip01");
        foreach (var a in skeleton.Animations)
        {
            if (!_lengths.TryAdd(a.Name, a.Length) || root is null) continue;
            var track = a.Tracks.FirstOrDefault(t => t.Bone == root.Handle);
            if (track is null || track.KeyFrames.Count == 0) continue;
            var times = track.KeyFrames.Select(k => k.Time);
            var positions = track.KeyFrames.Select(k => new System.Numerics.Vector2(k.Translation.X, k.Translation.Z));
            _roots[a.Name] = ([.. times], [.. positions]);
        }
    }

    /// <summary>
    /// Adds the clips of <paramref name="other"/> this table does not know yet, with their <c>Bip01</c> tracks.
    /// </summary>
    void AddTable(AnimationLengths other)
    {
        foreach (var (clip, length) in other._lengths)
            if (_lengths.TryAdd(clip, length) && other._roots.TryGetValue(clip, out var root)) _roots[clip] = root;
    }

    /// <summary>
    /// Per race of an ANIMAL_CHARACTER: the race's <c>male mesh</c> (animal races name the same mesh for both), the skeleton that mesh links (read
    /// from the mesh file; looked for beside it, then anywhere under <c>data</c>), and that skeleton's clips, after with <paramref name="find"/> those
    /// of the race's animation files (an animal is male: <c>male animation</c>). Races sharing a skeleton and animation files share their table.
    /// </summary>
    void LoadAnimals(string installRoot, GameDatabase db, Func<string, string?>? find)
    {
        var races = db.OfType(FcsRecordType.AnimalCharacter)
            .Select(a => CharacterAppearance.AnimalRaceId(a) is { } id ? db.Find(id) : null)
            .OfType<GameRecord>().Where(r => r.Type == FcsRecordType.Race).DistinctBy(r => r.StringId).ToList();
        var keyOf = new System.Collections.Concurrent.ConcurrentDictionary<string, (string File, string[] Linked)?>(StringComparer.Ordinal);
        Parallel.ForEach(races, race =>
        {
            var file = SkeletonFile(installRoot, race.GetPath("male mesh"), find);
            string[] linked = file is null || find is null ? []
                : [.. AnimationFiles.Of(db, race, null, female: false).Select(f => find(AnimationFiles.BareName(f))).OfType<string>()];
            keyOf[race.StringId] = file is null ? null : (file, linked);
        });
        static string Key((string File, string[] Linked) k) => string.Join("|", [k.File, .. k.Linked]);
        var tables = new System.Collections.Concurrent.ConcurrentDictionary<string, AnimationLengths>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(keyOf.Values.OfType<(string File, string[] Linked)>().DistinctBy(Key, StringComparer.OrdinalIgnoreCase), k =>
        {
            var table = new AnimationLengths();
            table.AddFiles(k.Linked);   // found already; a linked file's clip replaces the skeleton's own of that name
            table.AddSkeleton(k.File);
            tables[Key(k)] = table;
        });
        foreach (var race in races)
            if (keyOf[race.StringId] is { } k && tables.TryGetValue(Key(k), out var table))
                _species[race.StringId] = table;
    }

    /// <summary>
    /// The skeleton file a mesh (an FCS path) links: beside the mesh, else the first file of that name under <c>data</c>; null when either
    /// is missing.
    /// </summary>
    static string? SkeletonFile(string installRoot, string fcsMesh, Func<string, string?>? find)
    {
        if (fcsMesh.Length == 0) return null;
        string relative = fcsMesh.Replace('\\', Path.DirectorySeparatorChar).TrimStart('.', Path.DirectorySeparatorChar);
        var mesh = GameFiles.Resolve(Path.Combine(installRoot, relative));
        try
        {
            // a mod's mesh away from its FCS path (a workshop folder)
            if (!File.Exists(mesh) && find?.Invoke(AnimationFiles.BareName(fcsMesh)) is { } found) mesh = found;
            if (!File.Exists(mesh)) return null;
            if (OgreMeshReader.ReadFile(mesh).SkeletonName is not { Length: > 0 } name) return null;
            var beside = GameFiles.Resolve(Path.Combine(Path.GetDirectoryName(mesh)!, name));
            if (File.Exists(beside)) return beside;
            return Directory.EnumerateFiles(Path.Combine(installRoot, "data"), Path.GetFileName(name), SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OgreFormatException or EndOfStreamException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The clips of an animal race's own skeleton (<paramref name="raceId"/> a RACE string id); this table for anything else (people, a race
    /// not loaded).
    /// </summary>
    public AnimationLengths For(string? raceId) => raceId is not null && _species.TryGetValue(raceId, out var table) ? table : this;

    /// <summary>
    /// The clips a person's body plays (<paramref name="look"/> its appearance): those of the animation files its race and record link, first file
    /// first, then this table's, as its rig looks a clip up (docs/animation.md, "Animation files"); this table when it links none (null: no
    /// appearance). Made once per race, record and sex; read on every worker.
    /// </summary>
    public AnimationLengths ForPerson(CharacterAppearance? look)
    {
        if (look is null || _linking is null) return this;

        var key = (look.Race.StringId, (look.Character ?? look.Animal)?.StringId, look.Female);
        return _people.GetOrAdd(key, static (_, s) => s.Self.Linking(s.Look), (Self: this, Look: look));
    }

    /// <summary>
    /// The table of a body linking <paramref name="look"/>'s animation files: their clips in link order, then this table's; this table when none of
    /// them was read.
    /// </summary>
    AnimationLengths Linking(CharacterAppearance look)
    {
        var (db, find) = _linking!.Value;
        var files = AnimationFiles.Of(db, look.Race, look.Character ?? look.Animal, look.Female);
        var paths = new List<string>();
        foreach (var file in files)
            if (find(AnimationFiles.BareName(file)) is { } path && _files.ContainsKey(path)) paths.Add(path);

        if (paths.Count == 0) return this;

        return _linkSets.GetOrAdd(string.Join("|", paths), static (_, s) => s.Self.Layered(s.Paths), (Self: this, Paths: paths));
    }

    /// <summary>
    /// A table of the clips of the files at <paramref name="paths"/> (read ones, in link order), then this table's.
    /// </summary>
    AnimationLengths Layered(List<string> paths)
    {
        var result = new AnimationLengths();
        foreach (var path in paths) result.AddTable(_files[path]);
        result.AddTable(this);
        return result;
    }

    /// <summary>
    /// Whether <see cref="For"/> has a table of its own for the race (an animal race whose skeleton was read).
    /// </summary>
    public bool HasSpecies(string raceId) => _species.ContainsKey(raceId);

    /// <summary>
    /// Whether the table knows the clip (its skeleton has an animation of that name).
    /// </summary>
    public bool Has(string clip) => _lengths.ContainsKey(clip);

    /// <summary>
    /// Sets an animal race's table (tests).
    /// </summary>
    public AnimationLengths AddSpecies(string raceId, AnimationLengths table)
    {
        _species[raceId] = table;
        return this;
    }

    /// <summary>
    /// Sets a clip's length in seconds (tests).
    /// </summary>
    public AnimationLengths Add(string clip, float seconds)
    {
        _lengths[clip] = seconds;
        return this;
    }

    /// <summary>
    /// Sets a clip's <c>Bip01</c> track as (time, X, Z) keys (tests).
    /// </summary>
    public AnimationLengths AddRoot(string clip, float[] times, System.Numerics.Vector2[] offsets)
    {
        _roots[clip] = (times, offsets);
        return this;
    }

    /// <summary>
    /// The clip's <c>Bip01</c> translation across the ground (model X and Z: Z forward, X to the character's left) at a time, relative to the binding
    /// pose, linear between keys and held past the ends; zero for a clip without one.
    /// </summary>
    public System.Numerics.Vector2 Root(string clip, float time)
    {
        if (!_roots.TryGetValue(clip, out var r)) return default;
        var (times, offsets) = r;
        if (time <= times[0]) return offsets[0];
        if (time >= times[^1]) return offsets[^1];
        int hi = Array.BinarySearch(times, time);
        if (hi >= 0) return offsets[hi];
        hi = ~hi;
        float f = (time - times[hi - 1]) / MathF.Max(times[hi] - times[hi - 1], 1e-6f);
        return System.Numerics.Vector2.Lerp(offsets[hi - 1], offsets[hi], f);
    }

    /// <summary>
    /// The clip's length; 1 s for a clip not known.
    /// </summary>
    public float Of(string clip) => _lengths.TryGetValue(clip, out float l) && l > 0 ? l : 1;

    /// <summary>
    /// How many keys the clip's <c>Bip01</c> track has (0: none or unknown): 1 or 2 equal ones make a still pose.
    /// </summary>
    public int RootKeys(string clip) => _roots.TryGetValue(clip, out var r) ? r.Times.Length : 0;
}
