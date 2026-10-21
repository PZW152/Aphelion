using Aphelion.App.Platform;
using Aphelion.Core;
using Aphelion.Core.Launch;
using Aphelion.Core.Logging;
using Aphelion.Core.Storage;

namespace Aphelion.App.Bridge;

/// <summary>Switching between standard and portable mode (Settings › Data and storage).</summary>
internal sealed partial class Api
{
    private int _switching;

    private void RegisterMode()
    {
        _rpc.Add<ModeArgs>("app.modePlan", (a, ct) => Task.Run<object?>(() =>
        {
            var target = ModeSwitch.Resolve(_s.Paths, a.To, a.Folder);
            var plan = DataMigration.Plan(_s.Paths.Root, target.DataRoot);
            return new
            {
                to = target.To,
                programFolder = target.ProgramFolder,
                dataRoot = target.DataRoot,
                copiesProgram = target.CopiesProgram,
                installed = Installation.IsInstalledCopy(),
                sameVolume = plan.SameVolume,
                items = plan.Items.Select(i => new { key = i.Key, bytes = i.Bytes, required = i.Required, conflict = i.Conflict }),
                blocker = Busy() ?? ModeSwitch.Blocker(target) ?? plan.Blocker,
            };
        }, ct));

        _rpc.Add<ModeArgs>("app.switchMode", async (a, ct) =>
        {
            if (Interlocked.Exchange(ref _switching, 1) == 1) throw AppException.Of("mode.busy", "A switch is already running.");
            var target = ModeSwitch.Resolve(_s.Paths, a.To, a.Folder);
            try
            {
                if ((Busy() ?? ModeSwitch.Blocker(target)) is { } b) throw AppException.Of("mode." + b, b);
                var progress = new Progress<MigrationProgress>(p => Emit("mode.progress", new { item = p.Item, done = p.Done, total = p.Total }));
                await Task.Run(() =>
                {
                    ModeSwitch.Prepare(target);
                    try
                    {
                        DataMigration.Execute(_s.Paths.Root, target.DataRoot, a.Items ?? [], progress, ct);
                    }
                    catch when (target.CopiesProgram)
                    {
                        // Nothing moved: take the program copy away again.
                        try { Directory.Delete(target.ProgramFolder, recursive: true); }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn("Could not remove the program copy", e); }
                        throw;
                    }
                    ModeSwitch.Finish(target, _s.Paths);
                }, ct).ConfigureAwait(false);
            }
            catch
            {
                Interlocked.Exchange(ref _switching, 0);
                throw;
            }
            // The new copy is starting; this one leaves once the reply is on its way.
            _ = Task.Delay(400, CancellationToken.None).ContinueWith(_ => _window.OnUiThread(() => { Application.Exit(); return 0; }), TaskScheduler.Default);
            return new { programFolder = target.ProgramFolder, dataRoot = target.DataRoot };
        });
    }

    /// <summary>Data cannot move while a game or a download is using it.</summary>
    private string? Busy() =>
        _s.Launcher.Sessions.Any(s => s.State != SessionState.Exited) ? "gameRunning"
        : _s.Tasks.All.Any(t => t.Status == Core.Tasks.TaskStatus.Running) ? "tasksRunning"
        : null;

    private sealed record ModeArgs(string To, string? Folder, List<string>? Items);
}
