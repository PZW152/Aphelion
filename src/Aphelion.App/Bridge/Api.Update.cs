using System.Text.Json;
using Aphelion.App.Platform;
using Aphelion.Core;
using Aphelion.Core.Logging;
using Aphelion.Core.Net;
using Aphelion.Core.Update;

namespace Aphelion.App.Bridge;

/// <summary>Updates of Aphelion itself, and the connection check for the official download hosts.</summary>
internal sealed partial class Api : IDisposable
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(6);
    private readonly Lock _updateGate = new();
    private UpdateService? _updates;
    private UpdateManifest? _available;
    private DateTimeOffset? _lastChecked;
    private string? _updateError;
    private bool _checking;
    private int _installing;
    private System.Threading.Timer? _updateTimer;

    private UpdateService Updates => _updates ??= new UpdateService(AppInfo.UpdateChannels, _s.Paths.Temp, _s.CreateDownloader);

    private void RegisterUpdates()
    {
        _rpc.Add("update.state", () => UpdateState());
        _rpc.Add("update.check", async (JsonElement _, CancellationToken ct) => { await CheckForUpdateAsync(ct).ConfigureAwait(false); return UpdateState(); });
        _rpc.Add("update.install", async (JsonElement args, CancellationToken ct) =>
        {
            var manifest = _available ?? throw AppException.Of("update.none", "No update is available.");
            if (Busy() is { } busy) throw AppException.Of("update." + busy, busy);
            if (Interlocked.Exchange(ref _installing, 1) == 1) throw AppException.Of("update.busy", "An update is already being installed.");
            try
            {
                var progress = new Progress<DownloadProgress>(p => Emit("update.progress", new { done = p.BytesDone, total = p.BytesTotal, rate = p.BytesPerSecond }));
                var exe = await Updates.DownloadAsync(manifest, progress, ct).ConfigureAwait(false);
                Updater.HandOver(exe);
            }
            catch
            {
                Interlocked.Exchange(ref _installing, 0);
                throw;
            }
            _ = Task.Delay(400, CancellationToken.None).ContinueWith(t => _window.OnUiThread(() => { Application.Exit(); return 0; }), TaskScheduler.Default);
            return new { version = manifest.Version };
        });

        _rpc.Add("network.check", async (JsonElement _, CancellationToken ct) => (object?)await NetworkCheck.RunAsync(ct).ConfigureAwait(false));

        // Quietly at start (after the window is up) and every few hours, unless turned off.
        _updateTimer = new System.Threading.Timer(state =>
        {
            if (_s.Settings.Current.CheckForUpdates) _ = CheckForUpdateAsync(CancellationToken.None);
        }, null, TimeSpan.FromSeconds(8), UpdateInterval);
    }

    public void Dispose() => _updateTimer?.Dispose();

    private async Task CheckForUpdateAsync(CancellationToken ct)
    {
        lock (_updateGate)
        {
            if (_checking) return;
            _checking = true;
        }
        Emit("update.changed", UpdateState());
        try
        {
            _available = await Updates.CheckAsync(AppInfo.Version, ct).ConfigureAwait(false);
            _updateError = null;
            if (_available is not null) Log.Info($"Update available: {_available.Version}");
        }
        catch (AppException e)
        {
            _updateError = e.Code;
            Log.Warn($"Update check failed: {e.Message}");
        }
        finally
        {
            _lastChecked = DateTimeOffset.Now;
            lock (_updateGate) _checking = false;
            Emit("update.changed", UpdateState());
        }
    }

    private object UpdateState() => new
    {
        current = AppInfo.Version,
        checking = _checking,
        lastChecked = _lastChecked,
        error = _updateError,
        available = _available is null ? null : new
        {
            version = _available.Version,
            published = _available.Published,
            size = _available.File.Size,
            notes = _available.Notes,
            page = _available.Page,
        },
    };
}
