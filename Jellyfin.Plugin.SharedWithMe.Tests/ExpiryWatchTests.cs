using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.SharedWithMe.Model;
using Jellyfin.Plugin.SharedWithMe.Sharing;
using Jellyfin.Plugin.SharedWithMe.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SharedWithMe.Tests;

// 0.2, B: what makes expiry prompt. The timer only asks; Due decides when a full run is worth queueing.
public sealed class ExpiryWatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "swm-expiry-" + Guid.NewGuid().ToString("N"));
    private readonly ShareStore _store;
    private DateTime _now = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

    public ExpiryWatchTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new ShareStore(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ExpiryWatch Sut() => new(_store, NullLogger.Instance, () => _now);

    private ShareRecord Add(DateTime? expires, ShareState state = ShareState.Active)
    {
        var r = new ShareRecord(Guid.NewGuid(), Guid.NewGuid(), ShareKind.Movie, "Heat", 1995, new Dictionary<string, string>(), Guid.NewGuid(), Guid.NewGuid(), "Sam", Guid.NewGuid(), _now, state, null, "Heat (1995) [swm-1]", ExpiresUtc: expires);
        _store.Upsert(r);
        return r;
    }

    [Fact]
    public void Nothing_to_do_without_shares_or_with_shares_that_never_end()
    {
        var watch = Sut();
        Assert.False(watch.Due());

        Add(null);
        _now = _now.AddYears(5);
        Assert.False(watch.Due());
    }

    [Fact]
    public void Due_once_when_a_share_comes_up_exactly_at_its_end_time_and_not_before()
    {
        var watch = Sut();
        var r = Add(_now.AddSeconds(30));

        _now = r.ExpiresUtc!.Value.AddTicks(-1);
        Assert.False(watch.Due());

        _now = r.ExpiresUtc.Value;
        Assert.True(watch.Due());

        // The run could not end it (still Active here): it is not asked for again on every tick.
        _now = _now.AddSeconds(10);
        Assert.False(watch.Due());
        _now = _now.AddMinutes(10);
        Assert.False(watch.Due());
    }

    [Fact]
    public void A_later_end_time_is_due_again()
    {
        var watch = Sut();
        Add(_now.AddSeconds(5));
        var second = Add(_now.AddMinutes(5));

        _now = _now.AddSeconds(6);
        Assert.True(watch.Due());
        _now = second.ExpiresUtc!.Value;
        Assert.True(watch.Due());
        Assert.False(watch.Due());
    }

    [Fact]
    public void A_share_already_past_its_end_time_at_startup_is_due_on_the_first_tick()
    {
        Add(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), ShareState.Broken);
        Add(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)); // an end time that could not be read

        Assert.True(Sut().Due());
    }

    [Fact]
    public void A_closed_share_is_never_due()
    {
        Add(_now.AddSeconds(-5), ShareState.Closed);

        Assert.False(Sut().Due());
    }

    [Fact]
    public void An_unreadable_record_is_not_due_and_does_not_throw()
    {
        File.WriteAllText(Path.Combine(_dir, "shares.json"), "{ corrupt");

        Assert.False(Sut().Due());
    }
}
