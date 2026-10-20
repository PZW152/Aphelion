using Aphelion.Core;
using Aphelion.Core.Instances;
using Aphelion.Core.Launch;
using Aphelion.Core.Settings;

namespace Aphelion.App.Bridge;

internal sealed partial class Api
{
    private void RegisterGame()
    {
        _rpc.Add<LaunchArgs>("launch.start", a =>
        {
            var instance = InstanceStore.Load(_s.ActiveRoot(), a.InstanceId);
            var account = (a.AccountId is null ? _s.Accounts.Store.Selected : _s.Accounts.Store.Get(a.AccountId))
                          ?? throw AppException.Of("account.none", "Add an account before launching.");
            _s.Settings.Update(s => s.SelectedInstance = instance.Id);
            return _s.Tasks.Start("launch", instance.Name, async (p, ct) =>
            {
                var session = await _s.Launcher.LaunchAsync(instance, account, p, ct).ConfigureAwait(false);
                return session.Id;
            });
        });

        _rpc.Add("game.sessions", () => _s.Launcher.Sessions.Select(SessionDto).ToList());
        _rpc.Add<LogArgs>("game.log", a =>
        {
            var session = _s.Launcher.Find(a.SessionId) ?? throw AppException.Of("game.noSession", "That game session has ended.");
            return session.Snapshot(a.After).TakeLast(a.Limit is > 0 ? a.Limit : 5000).ToList();
        });
        _rpc.Add<SessionArgs>("game.kill", a =>
        {
            (_s.Launcher.Find(a.SessionId) ?? throw AppException.Of("game.noSession", "That game session has ended.")).Kill();
            return null;
        });
    }

    private static object SessionDto(GameSession s) => new
    {
        s.Id, s.InstanceId, s.InstanceName, s.ProcessId, s.State, s.StartedAt, s.Warnings, s.Errors, memoryBytes = s.MemoryBytes,
    };

    private void WireGameEvents()
    {
        var pending = new List<object>();
        var gate = new Lock();
        var scheduled = false;

        void Queue(GameSession s, GameLogLine line)
        {
            lock (gate)
            {
                pending.Add(new { sessionId = s.Id, line });
                if (scheduled) return;
                scheduled = true;
            }
            _ = Task.Delay(100).ContinueWith(_ =>
            {
                List<object> batch;
                lock (gate) { batch = [.. pending]; pending.Clear(); scheduled = false; }
                Emit("game.log", batch);
            }, TaskScheduler.Default);
        }

        _s.Launcher.SessionStarted += session =>
        {
            session.LineReceived += Queue;
            session.StateChanged += s =>
            {
                Emit("game.state", SessionDto(s));
                if (s.State == SessionState.Running) ApplyAfterLaunch();
            };
            Emit("game.started", SessionDto(session));
        };
        _s.Launcher.SessionEnded += (session, exit) =>
        {
            Emit("game.exited", new { session = SessionDto(session), exit });
            Emit("instances.changed", null);
            if (_s.Settings.Current.Game.AfterLaunch == AfterLaunchAction.Minimize && exit.Crash is not null)
                _window.OnUiThread(() => { _window.BringToFront(); return 0; });
        };
    }

    private void ApplyAfterLaunch()
    {
        switch (_s.Settings.Current.Game.AfterLaunch)
        {
            case AfterLaunchAction.Minimize:
                _window.OnUiThread(() => { _window.Minimize(); return 0; });
                break;
            case AfterLaunchAction.Close:
                // The game keeps running; it was started as an independent process.
                _window.OnUiThread(() => { _window.CloseWindow(); return 0; });
                break;
        }
    }

    private sealed record LaunchArgs(string InstanceId, string? AccountId);
    private sealed record LogArgs(string SessionId, long After, int Limit);
    private sealed record SessionArgs(string SessionId);
}
