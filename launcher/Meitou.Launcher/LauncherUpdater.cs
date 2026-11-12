using Velopack;
using Velopack.Sources;

namespace Meitou.Launcher;

/// <summary>
/// Self-update through Velopack (docs/launcher.md, "Self-update"): the feed of each <c>launcher-v*</c> release, one Velopack channel
/// per build family (<see cref="Releases.Channel"/>). Only a launcher installed by its Setup or AppImage can update itself; a build run from
/// anywhere else reports <see cref="IsInstalled"/> false.
/// </summary>
public sealed class LauncherUpdater(Releases releases, bool prereleases)
{
    /// <summary>
    /// Velopack's update manager over the launcher releases of this repository, on this runtime's channel.
    /// </summary>
    readonly UpdateManager _manager = new(new LauncherReleaseSource(releases, prereleases), new UpdateOptions { ExplicitChannel = Releases.Channel });

    /// <summary>
    /// The update found by the last check, downloaded and waiting to be applied; null when none.
    /// </summary>
    UpdateInfo? _pending;

    /// <summary>
    /// Whether the launcher was installed by its Setup or AppImage, so it can update itself.
    /// </summary>
    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>
    /// The newer launcher version on GitHub, or null (none, or not installed).
    /// </summary>
    public async Task<string?> CheckAsync()
    {
        if (!_manager.IsInstalled) return null;
        _pending = await _manager.CheckForUpdatesAsync();
        return _pending?.TargetFullRelease.Version.ToString();
    }

    /// <summary>
    /// Downloads the update <see cref="CheckAsync"/> found, then restarts into it.
    /// </summary>
    public async Task UpdateAndRestartAsync(Action<int> progress, CancellationToken ct)
    {
        if (_pending is null) return;
        await _manager.DownloadUpdatesAsync(_pending, progress, ct);
        _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }

    /// <summary>
    /// Velopack's GitHub source, given only this repository's <c>launcher-v*</c> releases from <see cref="Releases"/>' cached list:
    /// Velopack's own listing reads only the newest 10 releases, which game releases would soon crowd the launcher's out of.
    /// </summary>
    sealed class LauncherReleaseSource(Releases releases, bool prereleases)
        : GithubSource($"https://github.com/{Releases.Repository}", null, prereleases)
    {
        protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
        {
            var (list, _) = await releases.FetchAsync();
            return [.. list
                .Where(r => r.Kind == ReleaseKind.Launcher && (includePrereleases || !r.Prerelease))
                .Select(ToGithubRelease)];
        }

        /// <summary>
        /// <paramref name="r"/> as Velopack's GitHub source lists a release.
        /// </summary>
        static GithubRelease ToGithubRelease(Release r) =>
            new GithubRelease
            {
                Name = r.Title,
                Prerelease = r.Prerelease,
                PublishedAt = r.Published.UtcDateTime,
                Assets = [.. r.Assets.Select(ToGithubAsset)],
            };

        /// <summary>
        /// <paramref name="a"/> as Velopack's GitHub source lists a release's file.
        /// </summary>
        static GithubReleaseAsset ToGithubAsset(ReleaseAsset a) =>
            new GithubReleaseAsset { Name = a.Name, BrowserDownloadUrl = a.Url, Url = a.Url, ContentType = "application/octet-stream" };
    }
}
