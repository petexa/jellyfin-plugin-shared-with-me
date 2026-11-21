using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SharedWithMe.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SharedWithMe.Sharing;

/// <summary>The JSON body POSTed to the notification address.</summary>
/// <param name="Event">What happened: <c>approval-needed</c>, <c>shared</c>, <c>approved</c>, <c>declined</c> or <c>test</c>.</param>
/// <param name="Message">One plain sentence naming the sharer, the recipient and the title, and nothing else.</param>
/// <param name="Sharer">The sharer's name.</param>
/// <param name="Recipient">The recipient's name.</param>
/// <param name="Title">The title.</param>
public record NotifyBody(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("sharer")] string Sharer,
    [property: JsonPropertyName("recipient")] string Recipient,
    [property: JsonPropertyName("title")] string Title);

/// <summary>
/// Tells someone outside Jellyfin that something was shared, by POSTing a small JSON body (<see cref="NotifyBody"/>) to the
/// administrator's notification address (<see cref="PluginConfiguration.NotifyUrl"/>). It never blocks or fails a share:
/// <see cref="Notify"/> returns at once and never throws; the POST runs on a background task, gets one attempt with a hard
/// <see cref="Timeout"/>, follows no redirect (the production sender is built that way), and a failure is logged at Warning
/// with the address cut to its scheme, host and port (no credentials, path or query, which may hold a secret).
/// The request is made by the server, so the address is reached from the server's network.
/// </summary>
public sealed class ShareNotifier
{
    /// <summary>A share waiting for approval.</summary>
    public const string ApprovalNeededEvent = "approval-needed";

    /// <summary>A share made at once (no approval needed).</summary>
    public const string SharedEvent = "shared";

    /// <summary>A pending share an administrator approved.</summary>
    public const string ApprovedEvent = "approved";

    /// <summary>A pending share an administrator declined.</summary>
    public const string DeclinedEvent = "declined";

    /// <summary>The audit page's "Send a test message".</summary>
    public const string TestEvent = "test";

    /// <summary>The test message's sentence.</summary>
    public const string TestMessage = "Shared With Me test message.";

    /// <summary>The name of the HTTP client the server builds for the plugin (no redirects followed).</summary>
    public const string HttpClientName = "SharedWithMe.Notify";

    /// <summary>The longest a POST may take, connecting included.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly Func<string?> _url;
    private readonly Func<NotifyOn> _on;
    private readonly Func<Uri, string, CancellationToken, Task<int>> _post;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly List<Task> _inFlight = new();

    /// <summary>Initializes a new instance of the <see cref="ShareNotifier"/> class.</summary>
    /// <param name="url">Reads the notification address (empty or null: off).</param>
    /// <param name="on">Reads which events are sent.</param>
    /// <param name="post">Sends one POST of a JSON body and returns the HTTP status code; it must not follow redirects.</param>
    /// <param name="logger">Logger.</param>
    public ShareNotifier(Func<string?> url, Func<NotifyOn> on, Func<Uri, string, CancellationToken, Task<int>> post, ILogger logger)
    {
        _url = url;
        _on = on;
        _post = post;
        _logger = logger;
    }

    /// <summary>Whether an event is sent under a setting: nothing for <see cref="NotifyOn.Nothing"/>; the three approval
    /// events for <see cref="NotifyOn.ApprovalsOnly"/>; those and "shared" for <see cref="NotifyOn.EverySharing"/>.</summary>
    /// <param name="on">The setting.</param>
    /// <param name="eventName">The event.</param>
    /// <returns>True when it is sent.</returns>
    public static bool Wanted(NotifyOn on, string eventName) => eventName switch
    {
        SharedEvent => on == NotifyOn.EverySharing,
        ApprovalNeededEvent or ApprovedEvent or DeclinedEvent => on is NotifyOn.ApprovalsOnly or NotifyOn.EverySharing,
        _ => false,
    };

    /// <summary>The one plain sentence for an event. It holds the sharer's name, the recipient's name and the title, and
    /// nothing else (no path, no year, no reason).</summary>
    /// <param name="eventName">The event.</param>
    /// <param name="sharer">The sharer's name.</param>
    /// <param name="recipient">The recipient's name.</param>
    /// <param name="title">The title.</param>
    /// <returns>The sentence.</returns>
    public static string MessageFor(string eventName, string sharer, string recipient, string title) => eventName switch
    {
        ApprovalNeededEvent => $"{sharer} is waiting for approval to share {title} with {recipient}.",
        ApprovedEvent => $"{sharer} may now share {title} with {recipient}: it was approved.",
        DeclinedEvent => $"{sharer} may not share {title} with {recipient}: it was declined.",
        _ => $"{sharer} shared {title} with {recipient}.",
    };

    /// <summary>The address cut to scheme, host and port, for the log.</summary>
    /// <param name="uri">The address.</param>
    /// <returns>For example <c>https://example.org:8443</c>.</returns>
    public static string SafeAddress(Uri uri) => uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);

    /// <summary>
    /// Sends an event in the background when the setting wants it and an address is set. Returns at once; never throws.
    /// </summary>
    /// <param name="eventName">The event.</param>
    /// <param name="sharer">The sharer's name.</param>
    /// <param name="recipient">The recipient's name.</param>
    /// <param name="title">The title.</param>
    public void Notify(string eventName, string sharer, string recipient, string title)
    {
        try
        {
            if (!Wanted(_on(), eventName) || Target() is not { } target)
            {
                return;
            }

            var body = JsonSerializer.Serialize(new NotifyBody(eventName, MessageFor(eventName, sharer, recipient, title), sharer, recipient, title));
            var task = Task.Run(() => SendAsync(target, body, eventName));
            lock (_lock)
            {
                _inFlight.RemoveAll(t => t.IsCompleted);
                _inFlight.Add(task);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Shared With Me: a notification could not be started ({Error})", ex.GetType().Name);
        }
    }

    /// <summary>Sends the test message now, waiting at most <see cref="Timeout"/>. Never throws.</summary>
    /// <returns>Whether it was sent and answered with a 2xx status, and one plain sentence saying what happened.</returns>
    public async Task<(bool Sent, string Message)> SendTestAsync()
    {
        Uri? target;
        try
        {
            target = Target();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Shared With Me: the notification address could not be read ({Error})", ex.GetType().Name);
            target = null;
        }

        if (target is null)
        {
            return (false, "No notification address is set. Enter one and save the settings first.");
        }

        var body = JsonSerializer.Serialize(new NotifyBody(TestEvent, TestMessage, string.Empty, string.Empty, string.Empty));
        var (status, error) = await SendAsync(target, body, TestEvent).ConfigureAwait(false);
        return status is >= 200 and < 300
            ? (true, $"The test message was sent. The address answered {status}.")
            : status is { } code
                ? (false, $"The test message was not accepted: the address answered {code}.")
                : (false, $"The test message could not be sent: {error}.");
    }

    /// <summary>Waits until every notification started so far has finished (for tests).</summary>
    /// <returns>A task.</returns>
    public Task WhenIdle()
    {
        lock (_lock)
        {
            return Task.WhenAll(_inFlight.ToArray());
        }
    }

    // The address to send to, only when it is set and valid (a hand-edited settings file is checked here too).
    private Uri? Target()
    {
        var url = _url()?.Trim();
        return !string.IsNullOrEmpty(url) && PluginConfiguration.IsValidNotifyUrl(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
    }

    // One attempt, at most Timeout. The status, or null and why not in plain words; never throws.
    private async Task<(int? Status, string Error)> SendAsync(Uri target, string body, string eventName)
    {
        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            var status = await _post(target, body, cts.Token).ConfigureAwait(false);
            if (status is < 200 or >= 300)
            {
                _logger.LogWarning("Shared With Me: the {Event} notification to {Address} was answered {Status}; it is not sent again", eventName, SafeAddress(target), status);
            }
            else
            {
                _logger.LogDebug("Shared With Me: the {Event} notification was sent to {Address}", eventName, SafeAddress(target));
            }

            return (status, string.Empty);
        }
        catch (Exception ex)
        {
            var why = ex switch
            {
                OperationCanceledException => "no answer within 5 seconds",
                HttpRequestException h => "the connection failed (" + h.HttpRequestError + ")",
                _ => "it failed (" + ex.GetType().Name + ")",
            };
            _logger.LogWarning("Shared With Me: the {Event} notification to {Address} could not be sent: {Why}; it is not sent again", eventName, SafeAddress(target), why);
            return (null, why);
        }
    }
}
