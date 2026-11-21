using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SharedWithMe.Model;
using Jellyfin.Plugin.SharedWithMe.Server;
using Jellyfin.Plugin.SharedWithMe.Sharing;
using Jellyfin.Plugin.SharedWithMe.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SharedWithMe.Tests;

public sealed class SyncWorkerTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(50);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "swm-worker-" + Guid.NewGuid().ToString("N"));

    public SyncWorkerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static async Task<bool> Within(Task t, int ms = 5000) => await Task.WhenAny(t, Task.Delay(ms)) == t;

    private static void WaitUntil(Func<bool> condition, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.ElapsedMilliseconds < ms, "timed out");
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void The_plugins_own_policy_writes_re_trigger_isolation_once_and_then_settle()
    {
        var gw = new FakeServerGateway();
        var store = new ShareStore(_dir);
        var gate = new PluginGate();
        var lib = Guid.NewGuid();
        var kid1 = Guid.NewGuid();
        var kid2 = Guid.NewGuid();
        gw.RealLibraryIds.Add(lib);
        foreach (var u in new[] { kid1, kid2 })
        {
            gw.Users[u] = new UserInfo(u, "u", false, false, false, null, null, true, new List<Guid>(), Array.Empty<string>(), Array.Empty<string>());
        }

        store.SetLibrary(kid1, ShareKind.Movie, Guid.NewGuid()); // the plugin is in charge
        var sync = new ShareSynchronizer(gw, store, new LibraryAccessManager(gw, store), new ShareFolderBuilder(), () => Path.Combine(_dir, "shares"), gate, NullLogger.Instance);
        var runs = 0;
        var writesPerRun = new List<int>();
        using var worker = new SyncWorker(
            (work, _) =>
            {
                Interlocked.Increment(ref runs);
                var before = gw.LibraryAccessCalls.Count + gw.BlockedTagCalls.Count;
                sync.EnsureIsolation();
                writesPerRun.Add(gw.LibraryAccessCalls.Count + gw.BlockedTagCalls.Count - before);
            },
            Debounce,
            NullLogger.Instance);
        gw.OnPolicyWritten = _ => worker.Request(SyncWork.Isolation); // the server's user-updated event

        worker.Request(SyncWork.Isolation);
        WaitUntil(() => Volatile.Read(ref runs) >= 2);
        Thread.Sleep(Debounce * 10);

        Assert.Equal(2, Volatile.Read(ref runs));
        Assert.True(writesPerRun[0] > 0);
        Assert.Equal(0, writesPerRun[1]); // the echo run is a no-op, so nothing re-triggers
        Assert.All(gw.Users.Values, u => Assert.False(u.AllLibraries));
    }

    // I3: a user created or edited (a blocked tag added, for example) queues a full run too, so rule 3 closes a share at once.
    [Fact]
    public async Task A_user_created_or_updated_queues_a_full_run_as_well_as_fast_isolation()
    {
        var works = new List<SyncWork>();
        using var worker = new SyncWorker(
            (work, _) =>
            {
                lock (works)
                {
                    works.Add(work);
                }
            },
            Debounce,
            TimeSpan.FromMilliseconds(200),
            NullLogger.Instance);
        var consumer = new UserEventConsumer(worker, NullLogger<UserEventConsumer>.Instance);
        var user = new Jellyfin.Database.Implementations.Entities.User("kid", "auth", "reset");

        await consumer.OnEvent(new Jellyfin.Data.Events.Users.UserUpdatedEventArgs(user));
        WaitUntil(() => { lock (works) { return works.Count >= 1; } });
        await consumer.OnEvent(new Jellyfin.Data.Events.Users.UserCreatedEventArgs(user));
        WaitUntil(() => { lock (works) { return works.Count >= 2; } });
        await Task.Delay(300);

        lock (works)
        {
            Assert.Equal(new[] { SyncWork.Full, SyncWork.Full }, works);
        }
    }

    // I3: full runs re-triggered by the plugin's own policy writes settle: the echo run writes nothing, so nothing follows it.
    [Fact]
    public void Full_runs_triggered_by_the_plugins_own_policy_writes_settle()
    {
        var gw = new FakeServerGateway();
        var store = new ShareStore(_dir);
        var gate = new PluginGate();
        var lib = Guid.NewGuid();
        var kid1 = Guid.NewGuid();
        var kid2 = Guid.NewGuid();
        gw.RealLibraryIds.Add(lib);
        gw.ShareRoot = Path.Combine(_dir, "shares");
        foreach (var u in new[] { kid1, kid2 })
        {
            gw.Users[u] = new UserInfo(u, "u", false, false, false, null, null, true, new List<Guid>(), Array.Empty<string>(), Array.Empty<string>());
        }

        store.SetLibrary(kid1, ShareKind.Movie, gw.SeedLibrary(Path.Combine(_dir, "shares", kid1.ToString("N"), "movies")));
        var sync = new ShareSynchronizer(gw, store, new LibraryAccessManager(gw, store), new ShareFolderBuilder(), () => Path.Combine(_dir, "shares"), gate, NullLogger.Instance);
        var runs = 0;
        var writesPerRun = new List<int>();
        using var worker = new SyncWorker(
            (work, _) =>
            {
                Interlocked.Increment(ref runs);
                var before = gw.LibraryAccessCalls.Count + gw.BlockedTagCalls.Count;
                if (work == SyncWork.Full)
                {
                    sync.Run();
                }
                else
                {
                    sync.EnsureIsolation();
                }

                writesPerRun.Add(gw.LibraryAccessCalls.Count + gw.BlockedTagCalls.Count - before);
            },
            Debounce,
            Debounce,
            NullLogger.Instance);
        var consumer = new UserEventConsumer(worker, NullLogger<UserEventConsumer>.Instance);
        var user = new Jellyfin.Database.Implementations.Entities.User("kid", "auth", "reset");
        gw.OnPolicyWritten = _ => consumer.OnEvent(new Jellyfin.Data.Events.Users.UserUpdatedEventArgs(user)); // the server's echo

        worker.Request(SyncWork.Full);
        WaitUntil(() => Volatile.Read(ref runs) >= 2);
        Thread.Sleep(Debounce * 10);

        Assert.Equal(2, Volatile.Read(ref runs));
        Assert.True(writesPerRun[0] > 0);
        Assert.Equal(0, writesPerRun[1]);
    }

    [Fact]
    public async Task An_event_storm_during_a_run_queues_one_more_run_not_one_per_event()
    {
        var runs = 0;
        using var release = new ManualResetEventSlim();
        using var worker = new SyncWorker(
            (_, _) =>
            {
                if (Interlocked.Increment(ref runs) == 1)
                {
                    release.Wait(5000);
                }
            },
            Debounce,
            NullLogger.Instance);

        _ = worker.Request(SyncWork.Full);
        WaitUntil(() => Volatile.Read(ref runs) == 1);
        var waits = Enumerable.Range(0, 1000).Select(i => worker.Request(i % 2 == 0 ? SyncWork.Isolation : SyncWork.Full)).ToList();
        release.Set();

        Assert.True(await Within(Task.WhenAll(waits)));
        await Task.Delay(Debounce * 5);
        Assert.Equal(2, Volatile.Read(ref runs));
    }

    [Fact]
    public async Task Runs_never_overlap()
    {
        var current = 0;
        var max = 0;
        var runs = 0;
        using var worker = new SyncWorker(
            (_, _) =>
            {
                var now = Interlocked.Increment(ref current);
                InterlockedMax(ref max, now);
                Thread.Sleep(30);
                Interlocked.Decrement(ref current);
                Interlocked.Increment(ref runs);
            },
            TimeSpan.FromMilliseconds(5),
            NullLogger.Instance);

        var waits = new List<Task>();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 500)
        {
            waits.Add(worker.Request(SyncWork.Full));
            Thread.Sleep(3);
        }

        Assert.True(await Within(Task.WhenAll(waits)));
        Assert.True(Volatile.Read(ref runs) >= 2);
        Assert.Equal(1, Volatile.Read(ref max));
    }

    [Fact]
    public async Task Request_never_blocks_while_another_thread_holds_the_gate()
    {
        var gate = new PluginGate();
        var ran = 0;
        using var worker = new SyncWorker((_, _) => gate.Run(() => Interlocked.Increment(ref ran)), Debounce, NullLogger.Instance);
        using var held = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        var holder = Task.Run(() => gate.Run(() =>
        {
            held.Set();
            done.Wait(5000);
        }));
        held.Wait();

        // The server raises user-updated on its own thread; the handler only queues.
        var sw = Stopwatch.StartNew();
        // StartNew (not Task.Run) so the outer task is only the call to Request, not the run it queues.
        var request = Task.Factory.StartNew(() => worker.Request(SyncWork.Isolation), CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
        Assert.True(await Within(request, 1000));
        var pending = await request;
        Assert.True(sw.ElapsedMilliseconds < 1000);
        await Task.Delay(Debounce * 4);
        Assert.Equal(0, Volatile.Read(ref ran));
        Assert.False(pending.IsCompleted);

        done.Set();
        Assert.True(await Within(pending));
        Assert.Equal(1, Volatile.Read(ref ran));
        await holder;
    }

    [Fact]
    public async Task A_full_request_subsumes_a_pending_isolation_request()
    {
        var seen = new List<SyncWork>();
        using var worker = new SyncWorker((w, _) => { lock (seen) { seen.Add(w); } }, Debounce, NullLogger.Instance);

        var a = worker.Request(SyncWork.Isolation);
        var b = worker.Request(SyncWork.Full);
        var c = worker.Request(SyncWork.Isolation);

        Assert.True(await Within(Task.WhenAll(a, b, c)));
        Assert.Equal(new[] { SyncWork.Full }, seen);
    }

    [Fact]
    public async Task A_run_that_throws_does_not_stop_later_runs()
    {
        var runs = 0;
        using var worker = new SyncWorker(
            (_, _) =>
            {
                if (Interlocked.Increment(ref runs) == 1)
                {
                    throw new InvalidOperationException("boom");
                }
            },
            Debounce,
            NullLogger.Instance);

        var first = worker.Request(SyncWork.Full);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        Assert.True(await Within(worker.Request(SyncWork.Full)));
        Assert.Equal(2, Volatile.Read(ref runs));
    }

    [Fact]
    public async Task Dispose_cancels_a_pending_run_and_later_requests_do_nothing()
    {
        var runs = 0;
        var worker = new SyncWorker((_, _) => Interlocked.Increment(ref runs), TimeSpan.FromMilliseconds(300), NullLogger.Instance);

        var pending = worker.Request(SyncWork.Full);
        worker.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(worker.Request(SyncWork.Full).IsCanceled);
        await Task.Delay(500);
        Assert.Equal(0, Volatile.Read(ref runs));
    }

    [Fact]
    public async Task Progress_reaches_the_requester()
    {
        var reported = new List<double>();
        using var worker = new SyncWorker((_, p) => { p?.Report(50); p?.Report(100); }, Debounce, NullLogger.Instance);

        var progress = new SyncProgress(v => { lock (reported) { reported.Add(v); } });
        Assert.True(await Within(worker.Request(SyncWork.Full, progress)));

        Assert.Equal(new[] { 50.0, 100.0 }, reported);
    }

    [Fact]
    public async Task Playlist_and_isolation_requests_join_into_one_run_that_carries_both_and_full_covers_playlists()
    {
        Assert.True(SyncWork.Full.HasFlag(SyncWork.Playlists));
        Assert.True(SyncWork.Full.HasFlag(SyncWork.Isolation));
        var seen = new List<SyncWork>();
        using var worker = new SyncWorker((w, _) => { lock (seen) { seen.Add(w); } }, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(3), NullLogger.Instance);

        var sw = Stopwatch.StartNew();
        var a = worker.Request(SyncWork.Playlists);
        var b = worker.Request(SyncWork.Isolation);
        Assert.True(await Within(Task.WhenAll(a, b), 1000));
        Assert.True(sw.ElapsedMilliseconds < 1000); // the playlists debounce, not the full one
        Assert.Equal(new[] { SyncWork.Isolation | SyncWork.Playlists }, seen);

        var full = worker.Request(SyncWork.Full);
        var pl = worker.Request(SyncWork.Playlists); // pulls the full run forward and joins it
        Assert.True(await Within(Task.WhenAll(full, pl), 1500));
        Assert.Equal(SyncWork.Full, seen[^1]);
    }

    [Fact]
    public async Task Isolation_uses_the_short_debounce_and_pulls_a_pending_full_run_forward()
    {
        var seen = new List<SyncWork>();
        using var worker = new SyncWorker((w, _) => { lock (seen) { seen.Add(w); } }, TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(3), NullLogger.Instance);

        var sw = Stopwatch.StartNew();
        Assert.True(await Within(worker.Request(SyncWork.Isolation), 1000));
        Assert.True(sw.ElapsedMilliseconds < 1000);

        var full = worker.Request(SyncWork.Full);
        await Task.Delay(100);
        Assert.False(full.IsCompleted); // a full run waits its own (long) debounce...
        sw.Restart();
        var iso = worker.Request(SyncWork.Isolation); // ...until an isolation request needs a run now
        Assert.True(await Within(Task.WhenAll(full, iso), 1000));
        Assert.True(sw.ElapsedMilliseconds < 1000);
        Assert.Equal(new[] { SyncWork.Isolation, SyncWork.Full }, seen);
    }

    [Fact]
    public async Task No_run_starts_after_dispose_even_if_one_was_waiting_for_the_running_one()
    {
        var runs = 0;
        using var release = new ManualResetEventSlim();
        var worker = new SyncWorker(
            (_, _) =>
            {
                if (Interlocked.Increment(ref runs) == 1)
                {
                    release.Wait(5000);
                }
            },
            Debounce,
            NullLogger.Instance);

        _ = worker.Request(SyncWork.Full);
        WaitUntil(() => Volatile.Read(ref runs) == 1);
        var second = worker.Request(SyncWork.Full);
        await Task.Delay(Debounce * 4); // the second is now waiting for the first to finish
        worker.Dispose();
        release.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await Task.Delay(Debounce * 4);
        Assert.Equal(1, Volatile.Read(ref runs));
    }

    [Fact]
    public async Task The_scheduled_task_fails_with_the_plain_message_when_the_share_root_is_misconfigured()
    {
        var gw = new FakeServerGateway();
        var store = new ShareStore(_dir);
        var root = Path.Combine(_dir, "shares");
        gw.AddRealLibrary(Path.Combine(root, "stray")); // a library inside the share root
        var sync = new ShareSynchronizer(gw, store, new LibraryAccessManager(gw, store), new ShareFolderBuilder(), () => root, new PluginGate(), NullLogger.Instance);
        using var worker = new SyncWorker((_, p) => sync.Run(p), Debounce, Debounce, NullLogger.Instance);
        var task = new CheckSharesTask(worker);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => task.ExecuteAsync(new Progress<double>(), CancellationToken.None));

        Assert.Contains("overlaps", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ex.Message, sync.LastReport!.SetupProblem);
    }

    [Fact]
    public async Task A_request_completes_with_the_report_of_the_run_that_covered_it()
    {
        var runs = 0;
        using var worker = new SyncWorker(
            (w, _) => w == SyncWork.Full ? new SyncReport(Interlocked.Increment(ref runs), 0, 0, 0, 0) : null,
            Debounce,
            Debounce,
            NullLogger.Instance);

        var first = await worker.Request(SyncWork.Full);
        var a = worker.Request(SyncWork.Full);
        var b = worker.Request(SyncWork.Isolation); // joins the pending full run
        var second = await a;

        Assert.Equal(1, first!.Checked);
        Assert.Equal(2, second!.Checked);
        Assert.Same(second, await b);
        Assert.Null(await worker.Request(SyncWork.Isolation));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    // Synchronous IProgress (Progress<T> posts to the thread pool, which would make the assertion racy).
    private sealed class SyncProgress : IProgress<double>
    {
        private readonly Action<double> _report;

        public SyncProgress(Action<double> report) => _report = report;

        public void Report(double value) => _report(value);
    }
}
