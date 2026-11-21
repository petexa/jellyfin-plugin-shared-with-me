using System;
using System.Linq;
using Jellyfin.Plugin.SharedWithMe.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SharedWithMe.Sharing;

/// <summary>
/// Makes expiry prompt. The check-shares run ends an expired share, but it otherwise runs only after a library scan, every
/// 6 hours and when users or settings change. A timer asks <see cref="Due"/> every <see cref="Interval"/>; it reads the
/// record of shares and nothing else, and answers true (the caller then queues one full run) only when a share's time has
/// come up since the last time it answered true. A share whose run could not end it (a copy that could not be removed) is
/// therefore not asked for again on every tick: every later full run retries it.
/// </summary>
public sealed class ExpiryWatch
{
    /// <summary>How often the timer asks.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly ShareStore _store;
    private readonly Func<DateTime> _utcNow;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private DateTime? _seenUpTo;

    /// <summary>Initializes a new instance of the <see cref="ExpiryWatch"/> class.</summary>
    /// <param name="store">The share store.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="utcNow">Clock; defaults to <see cref="DateTime.UtcNow"/>.</param>
    public ExpiryWatch(ShareStore store, ILogger logger, Func<DateTime>? utcNow = null)
    {
        _store = store;
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Whether a full run is needed now: some share that is still Active or Broken has an end time after the moment this
    /// last answered true (the first call: ever) and at or before now. An unreadable store answers false (the full run
    /// reports that problem itself). Never throws.
    /// </summary>
    /// <returns>True when the caller should queue a full run.</returns>
    public bool Due()
    {
        lock (_lock)
        {
            var now = _utcNow();
            try
            {
                var since = _seenUpTo;
                if (!_store.All().Any(r => r.IsExpired(now) && (since is null || r.ExpiresUtc > since)))
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Shared With Me: the expiry check could not read the share record");
                return false;
            }

            _seenUpTo = now;
            return true;
        }
    }
}
