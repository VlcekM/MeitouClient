using Meitou.Data.Ogre;
using Meitou.Data.Textures;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// A <see cref="WorldTextureCache"/>'s replacements: resident images reloaded with more top levels (something came near), fewer (nothing near needs
/// them) or at a new texture quality, a few at a time, the old image drawn until the new one is in; and the counts of what it replaced.
/// </summary>
sealed class MipGovernor(WorldTextureCache cache, AssetLocator assets)
{
    /// <summary>
    /// The <see cref="TextureQuality.Level"/> every resident image has been brought to (-1: not checked yet).
    /// </summary>
    int _requalifiedLevel = -1;

    /// <summary>
    /// Images replaced by one with more levels (finer, as something came near).
    /// </summary>
    public int Refined { get; private set; }

    /// <summary>
    /// Images replaced by one with fewer levels (what nothing near needed).
    /// </summary>
    public int Coarsened { get; private set; }

    /// <summary>
    /// Resident textures waiting for a finer image (loading, uploading) while their old one is drawn.
    /// </summary>
    public int Refining { get; internal set; }

    /// <summary>
    /// Seconds a texture must have been finer than needed before it is replaced by a coarser image (no sooner: a camera that turns back would have it
    /// reloaded at once); under the guard's pressure no wait.
    /// </summary>
    public double CoarsenAfterSeconds { get; set; } = 10;

    /// <summary>
    /// Finer replacements loading at once (the worker threads are few and shared with the foliage's layouts).
    /// </summary>
    public int MaxRefines { get; set; } = 6;

    /// <summary>
    /// Coarser replacements loading at once.
    /// </summary>
    public int MaxCoarsens { get; set; } = 2;

    /// <summary>
    /// What the resident images would hold without streaming's drops (each dropped level counted as four times the one below; an estimate, taken at
    /// <see cref="Rebalance"/>).
    /// </summary>
    public long UnstreamedBytes { get; private set; }

    /// <summary>
    /// The texture quality moved (the Tab slider): resident images loaded at another level are loaded again at the new one, a few at a time
    /// (<see cref="MaxRefines"/> replacements at once), the old image drawn until the new one is in. Unloaded ones read the level when they load
    /// again.
    /// </summary>
    internal void Requalify()
    {
        int level = TextureQuality.Level;
        if (level == _requalifiedLevel) return;
        int replacing = 0;
        foreach (var t in cache.Textures) if (t.Replacing) replacing++;
        bool done = true;
        foreach (var t in cache.Textures)
        {
            if (t.QualityLevel == level || t.State is WorldTexture.Residency.Missing or WorldTexture.Residency.Unloaded)
                continue;
            if (t.State != WorldTexture.Residency.Resident || t.Native is null || t.Replacing || t.Pending is not null)
            {
                done = false;
                continue;
            }

            if (TextureQuality.LevelsToDrop(Path.GetFileName(t.Path!), assets.Configured.GroupOfFile(t.Path!),
                    level) == t.QualityDrop || t.CannotDrop)
            {
                t.QualityLevel = level;   // this file drops as many levels at the new setting (an _HI or _LO name, an exempt group)
                continue;
            }
            if (replacing >= MaxRefines || !cache.MayStart(t))
            {
                done = false;
                continue;
            }

            cache.Start(t, replacing: true);
            replacing++;
        }
        if (done) _requalifiedLevel = level;
    }

    /// <summary>
    /// Mip streaming: after <see cref="WorldTextureCache.CommitNeeds"/>, a resident texture whose image has more top levels dropped than it may now
    /// (something came near) is loaded again with fewer, and the old image drawn until the new one is in (<see cref="WorldTextureCache.BeginSwap"/>);
    /// one that is finer than it needs to be for <see cref="CoarsenAfterSeconds"/> (at once under the guard's pressure) is replaced by a coarser.
    /// Only the textures this has been told the need of are touched (<see cref="WorldTexture.KeepAllMips"/> never).
    /// </summary>
    public void Rebalance()
    {
        long now = Environment.TickCount64;
        bool pressure = cache.Guard?.Pressure ?? false;
        cache.Mips.Extra = pressure ? 1 : 0;
        long unstreamed = 0;

        // replacements being loaded: finer ones are limited so they do not crowd out the other streaming work on the few worker threads
        int replacing = 0;
        foreach (var t in cache.Textures) if (t.Replacing) replacing++;
        foreach (var t in cache.Textures)
        {
            if (t.State != WorldTexture.Residency.Resident || t.Native is null) continue;
            unstreamed += t.Bytes << (2 * Math.Max(t.Dropped - t.QualityDrop, 0));
            if (t.Replacing || t.Pending is not null || t.KeepAllMips || t.CannotDrop || t.FileWidth == 0 || !float.IsFinite(t.Need) && !t.Lean)
                continue;
            // A lean image whose need is unknown is refined to the quality's levels.
            int target = Math.Max(float.IsFinite(t.Need) ? cache.Mips.Drop(t.FileWidth, t.FileHeight, t.Need) : 0, t.QualityDrop);

            // Another cache holds the file at the target levels: taken (a coarser one after the same wait as a load), nothing loaded.
            if ((target < t.Dropped || target > t.Dropped && (pressure || t.TooFineSince != 0 && now - t.TooFineSince >= CoarsenAfterSeconds * 1000))
                && cache.Shared(t, t.Need, exact: true) is { } shared)
            {
                cache.Adopt(t, shared);
                continue;
            }

            if (target < t.Dropped)
            {
                if (replacing >= MaxRefines || !cache.MayStart(t)) continue;
                Refined++;
                t.Refine = true;
                Refining++;
                cache.Start(t, replacing: true);
                replacing++;
            }
            else if (target > t.Dropped)
            {
                if (t.TooFineSince == 0) t.TooFineSince = now;
                if (!pressure && now - t.TooFineSince < CoarsenAfterSeconds * 1000) continue;
                if (replacing >= MaxCoarsens) continue;
                Coarsened++;
                cache.Start(t, replacing: true);
                replacing++;
            }
            else t.TooFineSince = 0;
        }
        UnstreamedBytes = unstreamed;
    }
}
