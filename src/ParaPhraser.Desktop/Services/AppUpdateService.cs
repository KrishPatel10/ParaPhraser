using Velopack;
using Velopack.Sources;

namespace ParaPhraser.Desktop.Services;

internal sealed class AppUpdateService
{
    private readonly UpdateManager _manager = new(
        new GithubSource("https://github.com/KrishPatel10/ParaPhraser", null, false));

    private UpdateInfo? _downloaded;

    // False in a developer build and in the MSIX, which updates through the Store.
    internal bool IsAvailable => _manager.IsInstalled;

    // Returns the new version, or null when this one is the latest.
    internal async Task<string?> DownloadLatestAsync()
    {
        var update = await _manager.CheckForUpdatesAsync();
        if (update is null)
        {
            return null;
        }

        await _manager.DownloadUpdatesAsync(update);
        _downloaded = update;
        return update.TargetFullRelease.Version.ToString();
    }

    internal void RestartIntoUpdate()
    {
        if (_downloaded is not null)
        {
            _manager.ApplyUpdatesAndRestart(_downloaded.TargetFullRelease);
        }
    }
}
