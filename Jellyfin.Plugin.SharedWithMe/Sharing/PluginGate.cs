using System;

namespace Jellyfin.Plugin.SharedWithMe.Sharing;

/// <summary>
/// The one serialiser for every change the plugin makes (shares, library access, tags, sync). Register one instance
/// per process. Re-entrant on the same thread (it is a monitor). Do not wait inside it for work that itself needs the
/// gate on another thread.
/// </summary>
public sealed class PluginGate
{
    private readonly object _lock = new();
    private volatile bool _closed;

    /// <summary>
    /// Gets a value indicating whether the plugin is being uninstalled. Once set it stays set for the life of the process:
    /// every entry point that changes anything checks it inside the gate and does nothing.
    /// </summary>
    public bool IsClosed => _closed;

    /// <summary>Marks the plugin as being uninstalled (see <see cref="IsClosed"/>). Call it while holding the gate.</summary>
    public void Close() => _closed = true;

    /// <summary>Runs a function under the gate.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="work">The work.</param>
    /// <returns>The work's result.</returns>
    public T Run<T>(Func<T> work)
    {
        lock (_lock)
        {
            return work();
        }
    }

    /// <summary>Runs an action under the gate.</summary>
    /// <param name="work">The work.</param>
    public void Run(Action work)
    {
        lock (_lock)
        {
            work();
        }
    }
}
