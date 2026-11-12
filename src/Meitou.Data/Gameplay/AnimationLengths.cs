using Meitou.Content;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Ogre;

namespace Meitou.Data.Gameplay;

/// <summary>
/// The length in seconds of every clip of the humanoid skeletons (what a synched movement clip needs to turn a phase into a time), and, per animal
/// race, the clips of that race's own skeleton (<see cref="For"/>: an animal's clips are in the skeleton its body mesh links, and their names repeat
/// across species with other lengths).
/// </summary>
public sealed class AnimationLengths
{
    /// <summary>
    /// Clip lengths in seconds by name (ignoring case); the first skeleton to name a clip wins.
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
    /// A table with no clips.
    /// </summary>
    public static readonly AnimationLengths Empty = new();

    /// <summary>
    /// The clips of every <c>*_skeleton.skeleton</c> under <c>data/character/meshes</c> (the first file to name a clip wins); with
    /// <paramref name="db"/> also the clips of the skeleton each ANIMAL_CHARACTER's race's body mesh links (<see cref="For"/>), read in parallel, one
    /// table per skeleton file. With <paramref name="find"/> (a bare file name to its full path, as the game's resource groups find it) the clips of
    /// the ANIMATION_FILE skeletons follow, in link order (<see cref="AnimationFiles"/>).
    /// </summary>
    public static AnimationLengths Load(string installRoot, GameDatabase? db = null, Func<string, string?>? find = null)
    {
        var result = new AnimationLengths();
        var dir = Path.Combine(installRoot, "data", "character", "meshes");
        if (Directory.Exists(dir))
            foreach (var file in Directory.EnumerateFiles(dir, "*_skeleton.skeleton", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                result.AddSkeleton(file);
        if (db is not null && find is not null) result.AddLinked(AnimationFiles.All(db), find);
        if (db is not null) result.LoadAnimals(installRoot, db, find);
        return result;
    }

    /// <summary>
    /// Adds the clips of linked skeleton files (FCS paths, in link order), read in parallel; a file not found is left out, as the game links only the
    /// ones it finds.
    /// </summary>
    void AddLinked(IReadOnlyList<string> files, Func<string, string?> find)
    {
        var paths = files.Select(f => find(AnimationFiles.BareName(f))).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var read = new OgreSkeleton?[paths.Count];
        Parallel.For(0, paths.Count, i => read[i] = TryRead(paths[i]));
        foreach (var skeleton in read) if (skeleton is not null) AddSkeleton(skeleton);
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
    /// Per race of an ANIMAL_CHARACTER: the race's <c>male mesh</c> (animal races name the same mesh for both), the skeleton that mesh links (read
    /// from the mesh file; looked for beside it, then anywhere under <c>data</c>), and that skeleton's clips, then with <paramref name="find"/> those
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
            table.AddSkeleton(k.File);
            table.AddLinked(k.Linked, f => f);
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
}
