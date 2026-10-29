using Velopack;
using Velopack.Sources;

namespace WinClip;

internal enum UpdateCheckResult
{
    /// <summary>This copy wasn't installed with Setup.exe or the portable zip, so it can't update itself.</summary>
    NotSupported,
    UpToDate,
    /// <summary>A newer version is downloaded; it installs on restart.</summary>
    Ready,
}

/// <summary>
/// Updates from this repository's GitHub Releases. Pre-release builds (like 0.2.0-alpha) also receive
/// pre-releases; stable builds only receive stable releases.
/// </summary>
internal sealed class Updater
{
    private const string RepositoryUrl = "https://github.com/slimabbadi/winclip";

    private readonly UpdateManager _manager;
    private VelopackAsset? _ready;

    public Updater()
    {
        var current = new UpdateManager(new GithubSource(RepositoryUrl, null, prerelease: false));
        var isPrerelease = current.CurrentVersion?.ToString().Contains('-') ?? false;
        _manager = isPrerelease ? new UpdateManager(new GithubSource(RepositoryUrl, null, prerelease: true)) : current;
        _ready = _manager.UpdatePendingRestart;
    }

    /// <summary>Installed version, or null when running a build that wasn't installed (such as from source).</summary>
    public string? CurrentVersion => _manager.CurrentVersion?.ToString();

    /// <summary>The downloaded version waiting for a restart, if any.</summary>
    public string? ReadyVersion => _ready?.Version.ToString();

    /// <summary>Checks GitHub and downloads a newer version in the background. Network failures throw.</summary>
    public async Task<UpdateCheckResult> CheckAndDownloadAsync()
    {
        if (!_manager.IsInstalled)
            return UpdateCheckResult.NotSupported;
        if (_ready is not null)
            return UpdateCheckResult.Ready;

        var update = await _manager.CheckForUpdatesAsync();
        if (update is null)
            return UpdateCheckResult.UpToDate;

        await _manager.DownloadUpdatesAsync(update);
        _ready = update.TargetFullRelease;
        return UpdateCheckResult.Ready;
    }

    /// <summary>
    /// Hands the downloaded update to Velopack's updater, which waits for WinClip to exit, installs it and
    /// starts the new version. The caller must then shut down normally so history writes finish first.
    /// </summary>
    public bool ScheduleRestart()
    {
        if (_ready is null)
            return false;
        _manager.WaitExitThenApplyUpdates(_ready, silent: true, restart: true);
        return true;
    }
}
