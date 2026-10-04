using Meitou.Content;
using Meitou.Data.Ogre;

/// <summary>Reads every .skeleton under data/ and reports failures and what the files contain.</summary>
static class SkeletonSurvey
{
    public static int Run(GameInstall install)
    {
        var files = Directory.EnumerateFiles(install.DataDirectory, "*.skeleton", SearchOption.AllDirectories).ToList();
        var failures = new List<(string File, string Error)>();
        var versions = new Dictionary<string, int>();
        var blendModes = new Dictionary<string, int>();
        var linkTargets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int bones = 0, scaledBones = 0, maxBones = 0, roots = 0, multiRoot = 0, animations = 0, withBase = 0, emptyAnimations = 0;
        int tracks = 0, links = 0, withAnimations = 0, lengthMismatches = 0, mismatchFiles = 0, nonUnitBoneScale = 0;
        long keyFrames = 0, scaledKeyFrames = 0, nonUnitKeyScale = 0;
        float maxLength = 0;
        foreach (var file in files)
        {
            try
            {
                var skeleton = OgreSkeletonReader.ReadFile(file);
                versions[skeleton.Version] = versions.GetValueOrDefault(skeleton.Version) + 1;
                var mode = $"{skeleton.Version} {skeleton.StoredBlendMode?.ToString() ?? "(none)"}";
                blendModes[mode] = blendModes.GetValueOrDefault(mode) + 1;
                bones += skeleton.Bones.Count;
                maxBones = Math.Max(maxBones, skeleton.Bones.Count);
                scaledBones += skeleton.Bones.Count(b => b.HasScale);
                nonUnitBoneScale += skeleton.Bones.Count(b => b.HasScale && b.Scale != System.Numerics.Vector3.One);
                int r = skeleton.Bones.Count(b => b.Parent is null);
                roots += r;
                if (r > 1) multiRoot++;
                if (skeleton.Animations.Count > 0) withAnimations++;
                animations += skeleton.Animations.Count;
                foreach (var a in skeleton.Animations)
                {
                    if (a.Base is not null) withBase++;
                    if (a.Tracks.Count == 0) emptyAnimations++;
                    maxLength = Math.Max(maxLength, a.Length);
                    tracks += a.Tracks.Count;
                    foreach (var t in a.Tracks)
                    {
                        keyFrames += t.KeyFrames.Count;
                        scaledKeyFrames += t.KeyFrames.Count(k => k.HasScale);
                        nonUnitKeyScale += t.KeyFrames.Count(k => k.HasScale && k.Scale != System.Numerics.Vector3.One);
                    }
                }
                links += skeleton.AnimationLinks.Count;
                foreach (var l in skeleton.AnimationLinks) linkTargets[l.SkeletonName] = linkTargets.GetValueOrDefault(l.SkeletonName) + 1;
                lengthMismatches += skeleton.ChunkLengthMismatches;
                if (skeleton.ChunkLengthMismatches > 0) mismatchFiles++;
            }
            catch (Exception e)
            {
                failures.Add((Path.GetRelativePath(install.DataDirectory, file), e.Message));
            }
        }

        Console.WriteLine($"{files.Count} skeletons, {failures.Count} failed");
        foreach (var (k, v) in versions) Console.WriteLine($"  {k}: {v}");
        Console.WriteLine("  blend modes: " + string.Join(", ", blendModes.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} x{kv.Value}")));
        Console.WriteLine($"  {bones} bones (max {maxBones} per skeleton), {scaledBones} with a stored scale ({nonUnitBoneScale} not 1,1,1); {roots} roots, {multiRoot} skeletons with several roots");
        Console.WriteLine($"  {animations} animations in {withAnimations} skeletons ({emptyAnimations} without tracks, {withBase} with base info, longest {maxLength:0.###} s)");
        Console.WriteLine($"  {tracks} tracks, {keyFrames} keyframes, {scaledKeyFrames} with a stored scale ({nonUnitKeyScale} not 1,1,1)");
        var names = files.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"  {links} animation links to {linkTargets.Count} skeletons, {linkTargets.Keys.Count(names.Contains)} of them under data/");
        Console.WriteLine($"  chunk length mismatches (animation/track/keyframe): {lengthMismatches} in {mismatchFiles} files");
        foreach (var (file, error) in failures.Take(20)) Console.WriteLine($"  FAIL {file}: {error}");
        return failures.Count == 0 ? 0 : 1;
    }
}
