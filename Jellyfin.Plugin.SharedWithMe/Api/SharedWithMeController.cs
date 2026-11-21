using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.SharedWithMe.Model;
using Jellyfin.Plugin.SharedWithMe.Server;
using Jellyfin.Plugin.SharedWithMe.Sharing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SharedWithMe.Api;

/// <summary>Body of a share request.</summary>
public class CreateShareRequest
{
    /// <summary>Gets or sets the item to share.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the profile to share it with.</summary>
    public Guid RecipientUserId { get; set; }
}

/// <summary>Why a share was refused.</summary>
/// <param name="Refusal">The refusal code.</param>
/// <param name="Message">One plain sentence for the person who tried to share.</param>
public record RefusalResponse(Refusal Refusal, string Message);

/// <summary>A profile that can be shared with.</summary>
/// <param name="Id">User id.</param>
/// <param name="Name">Display name.</param>
public record RecipientDto(Guid Id, string Name);

/// <summary>Counters an administrator (and the tests) can read to see that the plugin has settled.</summary>
/// <param name="PolicyWrites">Policy writes the plugin made since the server started.</param>
/// <param name="SyncRuns">Full synchronizer runs since the server started.</param>
/// <param name="LastSync">When the last full run finished (UTC), or null.</param>
/// <param name="LastReport">What the last full run did, or null.</param>
/// <param name="PlaylistWrites">Playlist writes the plugin made since the server started.</param>
public record DiagnosticsDto(int PolicyWrites, int SyncRuns, DateTime? LastSync, SyncReport? LastReport, int PlaylistWrites);

/// <summary>A title put in a "Share with" playlist that was not shared.</summary>
/// <param name="Title">The title; empty when it could not be named.</param>
/// <param name="RecipientName">Who the playlist is for.</param>
/// <param name="Message">Why it was not shared.</param>
/// <param name="AtUtc">When.</param>
/// <param name="SharerName">Whose playlist it was (filled in for an administrator's list of all notes).</param>
public record NoteDto(string Title, string RecipientName, string Message, DateTime AtUtc, string? SharerName);

/// <summary>The "Not shared" list and whether sharing from TV apps is on.</summary>
/// <param name="TvSharing">Whether the "Share with" playlists are on.</param>
/// <param name="Notes">The notes, newest first.</param>
/// <param name="TvRecipients">Profiles the caller has a "Share with" playlist for (only those may be named on the page).</param>
public record NotesResponse(bool TvSharing, IReadOnlyList<NoteDto> Notes, IReadOnlyList<Guid> TvRecipients);

/// <summary>A plain sentence for the person who asked.</summary>
/// <param name="Message">The sentence, shown verbatim.</param>
public record MessageResponse(string Message);

/// <summary>A search result as the pages show it (no server paths).</summary>
/// <param name="Id">Item id.</param>
/// <param name="Title">Title.</param>
/// <param name="Year">Year, if known.</param>
/// <param name="Kind">Movie or series.</param>
/// <param name="OfficialRating">The item's rating.</param>
public record SearchResultDto(Guid Id, string Title, int? Year, ShareKind Kind, string? OfficialRating);

/// <summary>The audit page's data.</summary>
/// <param name="Profiles">Every profile with the shares it received.</param>
/// <param name="SwitchedUsers">Profiles the plugin moved off "all libraries".</param>
/// <param name="InCharge">Whether the plugin currently manages library access.</param>
/// <param name="SetupProblem">The current share-root problem in plain words, or null.</param>
/// <param name="ShareRootLocked">Whether the server keeps the current share folder whatever a save sends (while in charge).</param>
/// <param name="Diagnostics">The plugin's counters.</param>
/// <param name="NarrowedUsers">Profiles the plugin moved whose list was changed since; a restore leaves them on it.</param>
public record AuditResponse(IReadOnlyList<AuditProfile> Profiles, IReadOnlyList<UserRef> SwitchedUsers, bool InCharge, string? SetupProblem, bool ShareRootLocked, DiagnosticsDto Diagnostics, IReadOnlyList<UserRef> NarrowedUsers);

/// <summary>What a successful restore did.</summary>
/// <param name="RestoredUsers">Profiles put back on "all libraries".</param>
/// <param name="LeftOnOwnList">Profiles left on their own library list because it was changed after the plugin moved them.</param>
public record RestoreResponse(IReadOnlyList<UserRef> RestoredUsers, IReadOnlyList<UserRef> LeftOnOwnList);

/// <summary>Shared With Me REST API. The acting user always comes from the authenticated token, never the request.</summary>
[ApiController]
[Authorize]
[Route("SharedWithMe")]
[Produces("application/json")]
public class SharedWithMeController : ControllerBase
{
    // Jellyfin.Api.Constants.InternalClaimTypes.UserId; Jellyfin.Api is not a referenceable package.
    private const string UserIdClaim = "Jellyfin-UserId";

    // The share page's policy: everything from this server only; the page's own <style> block; no framing.
    private const string PageCsp = "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'";

    // How long "Check now" waits for the full run it asked for.
    private static readonly TimeSpan SyncWait = TimeSpan.FromMinutes(2);

    private readonly ShareService _service;
    private readonly JellyfinServerGateway _gateway;
    private readonly ShareSynchronizer _sync;
    private readonly AuditService _audit;
    private readonly SyncWorker _worker;
    private readonly SharePlaylistManager _playlists;
    private readonly ILogger<SharedWithMeController> _logger;

    /// <summary>Initializes a new instance of the <see cref="SharedWithMeController"/> class.</summary>
    /// <param name="service">The share service.</param>
    /// <param name="gateway">The server gateway (users, policy-write counter).</param>
    /// <param name="sync">The synchronizer (run counters).</param>
    /// <param name="audit">The audit service.</param>
    /// <param name="worker">The sync queue.</param>
    /// <param name="playlists">The "Share with" playlists (refusal notes).</param>
    /// <param name="logger">Logger.</param>
    public SharedWithMeController(ShareService service, JellyfinServerGateway gateway, ShareSynchronizer sync, AuditService audit, SyncWorker worker, SharePlaylistManager playlists, ILogger<SharedWithMeController> logger)
    {
        _playlists = playlists;
        _service = service;
        _gateway = gateway;
        _sync = sync;
        _audit = audit;
        _worker = worker;
        _logger = logger;
    }

    /// <summary>The audit page's data, with live visibility per share. Administrators only.</summary>
    /// <returns>The audit, or 403.</returns>
    [HttpGet("Audit")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<AuditResponse> GetAudit()
    {
        if (!CallerIsAdmin())
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var report = _audit.Build();
        return Ok(new AuditResponse(report.Profiles, report.SwitchedUsers, report.InCharge, report.SetupProblem, report.ShareRootLocked, Diagnostics(), report.NarrowedUsers));
    }

    /// <summary>Puts every profile the plugin changed back on "all libraries". Refused while any share is active.</summary>
    /// <returns>The restored profiles, or 409 with the reason.</returns>
    [HttpPost("Audit/Restore")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<RestoreResponse> Restore()
    {
        if (!CallerIsAdmin())
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var outcome = _audit.Restore();
        return outcome.Done
            ? Ok(new RestoreResponse(outcome.RestoredUsers, outcome.LeftOnOwnList ?? Array.Empty<UserRef>()))
            : Conflict(new MessageResponse(outcome.Message ?? AuditService.NotFinishedMessage));
    }

    /// <summary>Runs a full check of every share now and waits for it ("Check now"). Administrators only.</summary>
    /// <returns>The report of the run.</returns>
    [HttpPost("Audit/Sync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult<SyncReport>> RunSync()
    {
        if (!CallerIsAdmin())
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        // Waited for here, never inside the gate: the worker takes the gate itself on a pool thread. The request carries
        // the report of the very run that covered it.
        SyncReport? report;
        try
        {
            report = await _worker.Request(SyncWork.Full).WaitAsync(SyncWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new MessageResponse("The check is still running. Look again in a minute."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shared With Me: Check now failed");
            return StatusCode(StatusCodes.Status500InternalServerError, new MessageResponse("The check failed. The server log has the details."));
        }

        return report is null
            ? StatusCode(StatusCodes.Status500InternalServerError, new MessageResponse("The check finished without a report. The server log has the details."))
            : Ok(report);
    }

    /// <summary>
    /// Searches the movies and series the caller can see, at most 30, never anything in a share library. Fewer than 2
    /// characters gives an empty list.
    /// </summary>
    /// <param name="term">Search text.</param>
    /// <param name="forUserId">Search as this profile instead; administrators only.</param>
    /// <returns>The results, or 403.</returns>
    [HttpGet("Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<IReadOnlyList<SearchResultDto>> Search([FromQuery] string? term, [FromQuery] Guid? forUserId)
    {
        if (ActingUser() is not { } me || _service.Search(me, term, forUserId) is not { } hits)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        return Ok(hits.Select(h => new SearchResultDto(h.Id, h.Title, h.Year, h.Kind, h.OfficialRating)).ToList());
    }

    /// <summary>Active shares the caller received and can see now.</summary>
    /// <returns>Title, sharer and date of each.</returns>
    [HttpGet("Received")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<IReadOnlyList<ReceivedShare>> GetReceived()
    {
        if (ActingUser() is not { } me)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        return Ok(_audit.Received(me));
    }

    /// <summary>The share page, for every signed-in user. An HTML shell: every data call it makes carries the user's token.</summary>
    /// <returns>The page.</returns>
    [HttpGet("Page")]
    [AllowAnonymous]
    [Produces("text/html")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public ActionResult GetPage()
    {
        var basePath = SafeBasePath();
        if (!string.Equals(Request.Path.Value, "/SharedWithMe/Page", StringComparison.Ordinal))
        {
            // One canonical address (no trailing slash, exact case), so relative resolution can never differ.
            return Redirect(basePath + "/SharedWithMe/Page");
        }

        var html = ReadEmbedded("share.html");
        if (html is null)
        {
            return NotFound();
        }

        PageHeaders();
        return Content(html.Replace("{{BASE}}", basePath, StringComparison.Ordinal), "text/html; charset=utf-8");
    }

    /// <summary>The share page's script.</summary>
    /// <returns>The script.</returns>
    [HttpGet("Page/share.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetPageScript()
    {
        var js = ReadEmbedded("share.js");
        if (js is null)
        {
            return NotFound();
        }

        PageHeaders();
        return Content(js, "application/javascript; charset=utf-8");
    }

    /// <summary>Counters showing whether the plugin has settled. Administrators only.</summary>
    /// <returns>The counters, or 403.</returns>
    [HttpGet("Diagnostics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<DiagnosticsDto> GetDiagnostics()
    {
        if (!CallerIsAdmin())
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        return Ok(Diagnostics());
    }

    /// <summary>
    /// Lists the shares the caller made, limited to titles the caller can still see; every share for an administrator.
    /// With <c>withSummary=true</c> the answer is <c>{Shares, HiddenCount}</c>, where HiddenCount is how many of the
    /// caller's shares were left out because the caller can no longer see the title (a number only).
    /// </summary>
    /// <param name="withSummary">Wrap the list with the hidden count.</param>
    /// <returns>The shares.</returns>
    [HttpGet("Shares")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult GetShares([FromQuery] bool withSummary = false)
    {
        if (ActingUser() is not { } me)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var result = CallerIsAdmin() ? new SharedByResult(_service.ListFor(me), 0) : _audit.SharedBy(me);
        return withSummary ? Ok(result) : Ok(result.Shares);
    }

    /// <summary>Shares an item with another profile.</summary>
    /// <param name="request">The item and the recipient.</param>
    /// <returns>The new share, or 409 with the refusal.</returns>
    [HttpPost("Shares")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<ShareRecord> CreateShare([FromBody] CreateShareRequest request)
    {
        if (ActingUser() is not { } me)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        if (request is null || request.ItemId == Guid.Empty || request.RecipientUserId == Guid.Empty)
        {
            return BadRequest();
        }

        var (decision, record) = _service.Create(me, request.ItemId, request.RecipientUserId);
        if (record is null)
        {
            return Conflict(new RefusalResponse(decision.Refusal, decision.Message));
        }

        return Ok(record);
    }

    /// <summary>Removes a share. Only its sharer or an administrator may.</summary>
    /// <param name="id">The share id.</param>
    /// <returns>204, or 404 when the share is unknown or not the caller's to remove.</returns>
    [HttpDelete("Shares/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteShare([FromRoute] Guid id)
    {
        if (ActingUser() is not { } me)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        return _service.Remove(id, me) ? NoContent() : NotFound();
    }

    /// <summary>
    /// The caller's "Not shared" list: titles they put in one of their "Share with" playlists that were not shared, and why.
    /// With <c>all=true</c> an administrator gets everyone's.
    /// </summary>
    /// <param name="all">Everyone's notes (administrators only).</param>
    /// <returns>The notes and whether sharing from TV apps is on, or 403.</returns>
    [HttpGet("Notes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<NotesResponse> GetNotes([FromQuery] bool all = false)
    {
        if (ActingUser() is not { } me || _playlists.NotesFor(me, all) is not { } notes)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        string NameOf(Guid id) => _gateway.GetUser(id)?.Name ?? "a deleted profile";
        var dtos = notes.Select(n => new NoteDto(n.Title, NameOf(n.RecipientUserId), n.Message, n.AtUtc, all ? NameOf(n.SharerUserId) : null)).ToList();
        return Ok(new NotesResponse(Plugin.Instance?.Configuration.TvSharingPlaylists ?? false, dtos, _playlists.RecipientsWithPlaylist(me)));
    }

    /// <summary>Clears the caller's own "Not shared" list.</summary>
    /// <returns>204, or 403.</returns>
    [HttpDelete("Notes")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult ClearNotes()
    {
        if (ActingUser() is not { } me)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        _playlists.ClearNotes(me);
        return NoContent();
    }

    /// <summary>Lists the profiles the caller can share with.</summary>
    /// <returns>Id and name of each profile.</returns>
    [HttpGet("Recipients")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<IReadOnlyList<RecipientDto>> GetRecipients()
    {
        if (ActingUser() is not { } me)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        return Ok(_service.RecipientsFor(me).Select(u => new RecipientDto(u.Id, u.Name)).ToList());
    }

    private DiagnosticsDto Diagnostics() => new(_gateway.PolicyWrites, _sync.Runs, _sync.LastRunUtc, _sync.LastReport, _gateway.PlaylistWrites);

    private bool CallerIsAdmin() => ActingUser() is { } me && _gateway.GetUser(me) is { IsAdmin: true, IsDisabled: false };

    private static string? ReadEmbedded(string name)
    {
        using var stream = typeof(SharedWithMeController).Assembly.GetManifestResourceStream("Jellyfin.Plugin.SharedWithMe.Web." + name);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // The server's base URL as the request saw it ("" or "/jellyfin"), only when it is plain path segments.
    private string SafeBasePath()
    {
        var value = Request.PathBase.Value ?? string.Empty;
        return Regex.IsMatch(value, "^(/[A-Za-z0-9._~-]+)*$") ? value : string.Empty;
    }

    private void PageHeaders()
    {
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.XFrameOptions = "DENY";
        Response.Headers.ContentSecurityPolicy = PageCsp;
    }

    // An API key carries no user, so it gets null and every endpoint answers 403.
    private Guid? ActingUser() =>
        Guid.TryParse(User.FindFirst(UserIdClaim)?.Value, out var id) && id != Guid.Empty ? id : null;
}
