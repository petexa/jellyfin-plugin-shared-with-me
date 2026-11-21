using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SharedWithMe.Sharing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SharedWithMe.Server;

/// <summary>What a queued run must do; requests that join one run are combined. A full sync includes the rest.</summary>
[Flags]
public enum SyncWork
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Re-apply library isolation.</summary>
    Isolation = 1,

    /// <summary>Bring the "Share with" playlists in step (create, heal, reconcile).</summary>
    Playlists = 2,

    /// <summary>The full synchronizer run, then the playlists.</summary>
    Full = Isolation | Playlists | 4,
}

/// <summary>
/// One coalescing, debounced background queue for every trigger (startup, post-scan, scheduled task, user events).
/// <see cref="Request"/> never blocks and never takes the <c>PluginGate</c>, so it is safe to call from a server event
/// raised while the gate is held on another thread. At most one run executes and at most one more is pending: requests
/// made while a run is pending join it (their <see cref="SyncWork"/> flags are combined), so an event storm cannot queue
/// unbounded work. Runs never overlap.
/// </summary>
public sealed class SyncWorker : IDisposable
{
    private readonly Func<SyncWork, IProgress<double>?, SyncReport?> _run;
    private readonly TimeSpan _isolationDebounce;
    private readonly TimeSpan _playlistsDebounce;
    private readonly TimeSpan _fullDebounce;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private SyncWork _pending;
    private bool _scheduled;
    private DateTime _due;
    private bool _disposed;
    private List<TaskCompletionSource<SyncReport?>> _waiters = new();
    private List<IProgress<double>> _listeners = new();

    /// <summary>Initializes a new instance of the <see cref="SyncWorker"/> class.</summary>
    /// <param name="run">Does the work on a thread-pool thread; takes the gate itself.</param>
    /// <param name="debounce">Delay before a queued run starts, so a burst of events becomes one run.</param>
    /// <param name="logger">Logger.</param>
    public SyncWorker(Action<SyncWork, IProgress<double>?> run, TimeSpan debounce, ILogger logger)
        : this(run, debounce, debounce, logger)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SyncWorker"/> class.</summary>
    /// <param name="run">Does the work on a thread-pool thread; takes the gate itself.</param>
    /// <param name="isolationDebounce">Delay before a run an isolation request needs (short: a new or edited user is
    /// exposed until it runs). It also pulls a pending full run forward.</param>
    /// <param name="fullDebounce">Delay before a full run.</param>
    /// <param name="logger">Logger.</param>
    public SyncWorker(Action<SyncWork, IProgress<double>?> run, TimeSpan isolationDebounce, TimeSpan fullDebounce, ILogger logger)
        : this((w, p) => { run(w, p); return null; }, isolationDebounce, fullDebounce, logger)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SyncWorker"/> class with a separate playlists debounce.</summary>
    /// <param name="run">Does the work on a thread-pool thread; takes the gate itself.</param>
    /// <param name="isolationDebounce">Delay before a run an isolation request needs.</param>
    /// <param name="playlistsDebounce">Delay before a run a playlists request needs.</param>
    /// <param name="fullDebounce">Delay before a full run.</param>
    /// <param name="logger">Logger.</param>
    public SyncWorker(Action<SyncWork, IProgress<double>?> run, TimeSpan isolationDebounce, TimeSpan playlistsDebounce, TimeSpan fullDebounce, ILogger logger)
        : this((w, p) => { run(w, p); return null; }, isolationDebounce, playlistsDebounce, fullDebounce, logger)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SyncWorker"/> class with a run that reports what it did.</summary>
    /// <param name="run">Does the work on a thread-pool thread; takes the gate itself; returns the full run's report, or null.</param>
    /// <param name="isolationDebounce">Delay before a run an isolation request needs.</param>
    /// <param name="fullDebounce">Delay before a full run.</param>
    /// <param name="logger">Logger.</param>
    public SyncWorker(Func<SyncWork, IProgress<double>?, SyncReport?> run, TimeSpan isolationDebounce, TimeSpan fullDebounce, ILogger logger)
        : this(run, isolationDebounce, isolationDebounce, fullDebounce, logger)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SyncWorker"/> class with a run that reports what it did.</summary>
    /// <param name="run">Does the work on a thread-pool thread; takes the gate itself; returns the full run's report, or null.</param>
    /// <param name="isolationDebounce">Delay before a run an isolation request needs.</param>
    /// <param name="playlistsDebounce">Delay before a run a playlists request needs.</param>
    /// <param name="fullDebounce">Delay before a full run.</param>
    /// <param name="logger">Logger.</param>
    public SyncWorker(Func<SyncWork, IProgress<double>?, SyncReport?> run, TimeSpan isolationDebounce, TimeSpan playlistsDebounce, TimeSpan fullDebounce, ILogger logger)
    {
        _run = run;
        _isolationDebounce = isolationDebounce;
        _playlistsDebounce = playlistsDebounce;
        _fullDebounce = fullDebounce;
        _logger = logger;
    }

    /// <summary>Queues work; never blocks.</summary>
    /// <param name="work">What to do.</param>
    /// <param name="progress">Optional progress (0 to 100) for the run that does it.</param>
    /// <returns>
    /// Completes with the report of the run that covered this request (null for an isolation-only run) once it has
    /// finished; faults if that run threw; cancelled on dispose.
    /// </returns>
    public Task<SyncReport?> Request(SyncWork work, IProgress<double>? progress = null)
    {
        var done = new TaskCompletionSource<SyncReport?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (_disposed)
            {
                done.SetCanceled();
                return done.Task;
            }

            _pending |= work;

            // The pending run starts at the earliest time any of its requests asked for; a request is as urgent as the
            // most urgent kind of work it names.
            var due = DateTime.UtcNow + Debounce(work);
            if (!_scheduled || due < _due)
            {
                _due = due;
            }

            _waiters.Add(done);
            if (progress is not null)
            {
                _listeners.Add(progress);
            }

            if (!_scheduled)
            {
                _scheduled = true;
                _ = Task.Run(RunPendingAsync);
            }
        }

        return done.Task;
    }

    /// <summary>Cancels a pending run and every waiter. A run already executing finishes on its own.</summary>
    public void Dispose()
    {
        List<TaskCompletionSource<SyncReport?>> waiters;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            waiters = _waiters;
            _waiters = new List<TaskCompletionSource<SyncReport?>>();
            _listeners = new List<IProgress<double>>();
        }

        _stop.Cancel();
        foreach (var w in waiters)
        {
            w.TrySetCanceled();
        }
    }

    private TimeSpan Debounce(SyncWork work)
    {
        if (work == SyncWork.Full)
        {
            return _fullDebounce;
        }

        var wait = _fullDebounce;
        if (work.HasFlag(SyncWork.Isolation) && _isolationDebounce < wait)
        {
            wait = _isolationDebounce;
        }

        if (work.HasFlag(SyncWork.Playlists) && _playlistsDebounce < wait)
        {
            wait = _playlistsDebounce;
        }

        return wait;
    }

    private async Task RunPendingAsync()
    {
        try
        {
            while (true)
            {
                TimeSpan wait;
                lock (_lock)
                {
                    wait = _due - DateTime.UtcNow;
                }

                if (wait <= TimeSpan.Zero)
                {
                    break;
                }

                // Short slices, so a later isolation request that moved the due time forward is noticed quickly.
                await Task.Delay(wait < TimeSpan.FromMilliseconds(25) ? wait : TimeSpan.FromMilliseconds(25), _stop.Token).ConfigureAwait(false);
            }

            // Wait, without holding a thread, for a run in progress. Until the snapshot below the request stays
            // pending, so everything that arrives meanwhile joins it.
            await _running.WaitAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // disposed: Dispose has cancelled the waiters
        }

        try
        {
            SyncWork work;
            List<TaskCompletionSource<SyncReport?>> waiters;
            List<IProgress<double>> listeners;
            lock (_lock)
            {
                if (_disposed)
                {
                    return; // Dispose has cancelled the waiters; never start a run after it
                }

                work = _pending;
                waiters = _waiters;
                listeners = _listeners;
                _pending = SyncWork.None;
                _waiters = new List<TaskCompletionSource<SyncReport?>>();
                _listeners = new List<IProgress<double>>();
                _scheduled = false;
            }

            if (work == SyncWork.None)
            {
                foreach (var w in waiters)
                {
                    w.TrySetCanceled();
                }

                return;
            }

            try
            {
                var report = _run(work, listeners.Count == 0 ? null : new FanOut(listeners));
                foreach (var w in waiters)
                {
                    w.TrySetResult(report);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Shared With Me: the {Work} run failed", work);
                foreach (var w in waiters)
                {
                    w.TrySetException(ex);
                }
            }
        }
        finally
        {
            _running.Release();
        }
    }

    private sealed class FanOut : IProgress<double>
    {
        private readonly List<IProgress<double>> _targets;

        public FanOut(List<IProgress<double>> targets) => _targets = targets;

        public void Report(double value)
        {
            foreach (var t in _targets)
            {
                t.Report(value);
            }
        }
    }
}

/// <summary>
/// Subscribes to the server's in-process events for the plugin's lifetime: user updated (policy edits and renames, also
/// raised by the plugin's own policy writes) queues isolation and playlists, and a full sync; a new library folder queues a full sync; a
/// playlist added, changed or removed queues playlists. Handlers only queue.
/// </summary>
public sealed class SyncHost : IHostedService
{
    private readonly SyncWorker _worker;
    private readonly IUserManager _users;
    private readonly ILibraryManager _libraries;
    private readonly ShareSynchronizer _sync;
    private readonly PluginGate _gate;
    private readonly AuditService _audit;
    private readonly ILogger<SyncHost> _logger;

    /// <summary>Initializes a new instance of the <see cref="SyncHost"/> class.</summary>
    /// <param name="worker">The sync queue.</param>
    /// <param name="users">User manager.</param>
    /// <param name="libraries">Library manager.</param>
    /// <param name="sync">The synchronizer (in-charge check for the share-root guard).</param>
    /// <param name="gate">The process-wide serialiser.</param>
    /// <param name="audit">Runs the forced teardown when the plugin is uninstalled.</param>
    /// <param name="logger">Logger.</param>
    public SyncHost(SyncWorker worker, IUserManager users, ILibraryManager libraries, ShareSynchronizer sync, PluginGate gate, AuditService audit, ILogger<SyncHost> logger)
    {
        _audit = audit;
        _worker = worker;
        _users = users;
        _libraries = libraries;
        _sync = sync;
        _gate = gate;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Configuration saves ask, under the gate, whether the plugin is in charge before a ShareRoot change is allowed.
        if (Plugin.Instance is { } plugin)
        {
            plugin.InChargeGate = save => _gate.Run(() => save(_sync.IsInCharge()));
            plugin.Logger = message => _logger.LogWarning("{Message}", message);
            plugin.ConfigurationSaved = () => _worker.Request(SyncWork.Playlists);

            // Uninstall (12.4 calls it on the request, services still running): no new run starts, then the forced
            // teardown closes the gate for good and puts everything back. A run already going finishes first (the
            // teardown waits for the gate).
            plugin.Uninstalling = () =>
            {
                Unsubscribe();
                _worker.Dispose();
                _audit.Uninstall();
            };
        }

        _users.OnUserUpdated += OnUserUpdated;
        _libraries.ItemAdded += OnItemAdded;
        _libraries.ItemUpdated += OnItemChanged;
        _libraries.ItemRemoved += OnItemChanged;
        _logger.LogInformation("Shared With Me: listening for user and library changes");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();

        // A run already executing finishes on its own (every step is ordered so a re-run completes it); a pending one
        // is dropped and redone by the startup run next time.
        _worker.Dispose();
        return Task.CompletedTask;
    }

    private void Unsubscribe()
    {
        _users.OnUserUpdated -= OnUserUpdated;
        _libraries.ItemAdded -= OnItemAdded;
        _libraries.ItemUpdated -= OnItemChanged;
        _libraries.ItemRemoved -= OnItemChanged;
    }

    private void OnUserUpdated(object? sender, GenericEventArgs<User> e)
    {
        _logger.LogDebug("Shared With Me: user {UserId} updated, queueing isolation, playlists and a full sync", e.Argument?.Id);
        UserEventConsumer.QueueUserChange(_worker);
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is CollectionFolder)
        {
            _logger.LogInformation("Shared With Me: library {Name} added, queueing a sync", e.Item.Name);
            _ = _worker.Request(SyncWork.Full);
        }

        OnItemChanged(sender, e);
    }

    // Any playlist added, changed (an entry added or removed, renamed, made public) or deleted: 12.4 raises item added /
    // updated / removed for it (api-notes, Task 11). The plugin's own playlist writes come back here too; that pass
    // changes nothing.
    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Playlist)
        {
            _logger.LogDebug("Shared With Me: playlist {Id} changed, queueing playlists", e.Item.Id);
            _ = _worker.Request(SyncWork.Playlists);
        }
    }
}

/// <summary>User created or updated (published by the API) → isolation and playlists at once, and a full sync; user deleted → full sync.</summary>
public sealed class UserEventConsumer : IEventConsumer<UserCreatedEventArgs>, IEventConsumer<UserDeletedEventArgs>, IEventConsumer<UserUpdatedEventArgs>
{
    private readonly SyncWorker _worker;
    private readonly ILogger<UserEventConsumer> _logger;

    /// <summary>Initializes a new instance of the <see cref="UserEventConsumer"/> class.</summary>
    /// <param name="worker">The sync queue.</param>
    /// <param name="logger">Logger.</param>
    public UserEventConsumer(SyncWorker worker, ILogger<UserEventConsumer> logger)
    {
        _worker = worker;
        _logger = logger;
    }

    /// <summary>
    /// What a created or edited user queues: isolation and the playlists (short debounce: a new or edited user is exposed
    /// until it runs), and a full sync, so a blocked tag added to a recipient closes the share at once (rule 3) instead of
    /// at the next scan. Requests coalesce, and the echo of the plugin's own policy writes is a run that writes nothing.
    /// </summary>
    /// <param name="worker">The sync queue.</param>
    public static void QueueUserChange(SyncWorker worker)
    {
        _ = worker.Request(SyncWork.Isolation | SyncWork.Playlists);
        _ = worker.Request(SyncWork.Full);
    }

    /// <inheritdoc />
    public Task OnEvent(UserCreatedEventArgs eventArgs)
    {
        _logger.LogInformation("Shared With Me: user {Name} created, queueing isolation, playlists and a full sync", eventArgs.Argument.Username);
        QueueUserChange(_worker);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnEvent(UserDeletedEventArgs eventArgs)
    {
        _logger.LogInformation("Shared With Me: user {Name} deleted, queueing a sync", eventArgs.Argument.Username);
        _ = _worker.Request(SyncWork.Full);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnEvent(UserUpdatedEventArgs eventArgs)
    {
        _logger.LogDebug("Shared With Me: user {Name} updated (event), queueing isolation, playlists and a full sync", eventArgs.Argument.Username);
        QueueUserChange(_worker);
        return Task.CompletedTask;
    }
}

/// <summary>Queues a full sync after every library scan. Returns at once; it never holds up the scan.</summary>
public sealed class SyncPostScanTask : ILibraryPostScanTask
{
    private readonly SyncWorker _worker;
    private readonly ILogger<SyncPostScanTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="SyncPostScanTask"/> class.</summary>
    /// <param name="worker">The sync queue.</param>
    /// <param name="logger">Logger.</param>
    public SyncPostScanTask(SyncWorker worker, ILogger<SyncPostScanTask> logger)
    {
        _worker = worker;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Shared With Me: library scan finished, queueing a sync");
        _ = _worker.Request(SyncWork.Full);
        progress.Report(100);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The scheduled task "Shared With Me: check shares": at server startup (a startup trigger, which the server fires once
/// its startup tasks are done) and every 6 hours. Waits for the coalesced run and reports its progress.
/// </summary>
public sealed class CheckSharesTask : IScheduledTask
{
    private readonly SyncWorker _worker;

    /// <summary>Initializes a new instance of the <see cref="CheckSharesTask"/> class.</summary>
    /// <param name="worker">The sync queue; its request carries the report of the run that covered it.</param>
    public CheckSharesTask(SyncWorker worker)
    {
        _worker = worker;
    }

    /// <inheritdoc />
    public string Name => "Shared With Me: check shares";

    /// <inheritdoc />
    public string Key => "SharedWithMeCheckShares";

    /// <inheritdoc />
    public string Description => "Keeps every shared title in step: follows renamed or upgraded files, applies age-limit changes, closes shares that are no longer needed and keeps other profiles away from shared copies.";

    /// <inheritdoc />
    public string Category => "Shared With Me";

    /// <summary>Waits for the coalesced run; fails (so the dashboard shows it) when that run reported a setup problem.</summary>
    /// <param name="progress">Progress, 0 to 100.</param>
    /// <param name="cancellationToken">Stops waiting; the run itself finishes.</param>
    /// <returns>The task.</returns>
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var report = await _worker.Request(SyncWork.Full, progress).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (report?.SetupProblem is { } problem)
        {
            throw new InvalidOperationException(problem);
        }
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(6).Ticks },
    };
}
