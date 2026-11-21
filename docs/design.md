# Shared With Me — a Jellyfin plugin for sharing one title with one profile

Design, 2026-11-19. Status: approved. Working name "Shared With Me".

## Intent

A Jellyfin user who can see a title can share it with another profile that can't,
without changing anything for anyone else. The typical case is a parent letting one
child watch one film above that child's age cap. It is built to be published for the
Jellyfin community, so it contains nothing specific to one server.

Why: Jellyfin has no per-user, per-title grant. The only ways to lift one title over a
rating cap today (changing the title's rating, tag rules, separate libraries) are
admin-only, hand-made, and change the title for every profile with the same cap.

## Goals

- Any signed-in user can share a title they can see with another profile.
- The grant reaches exactly one profile and exactly one title.
- It works on every client (TV apps, phones, web) because the server enforces it.
- An admin page lists every share, who made it, and whether it is really working.
- Shares survive metadata refreshes and server upgrades, or fail closed.
- Installable from a plugin repository URL like any community plugin.

## Non-goals (v1)

- Notifying or asking a parent when a share happens (parked as a future enhancement).
- Expiring shares, share links for people without an account.
- Hiding or blocking titles (Jellyfin Enhanced, Per-User Visibility and Personal Library do that).
- Screen-time limits.
- Windows servers (see Constraints).
- A "Share" button inside TV and mobile apps. (Sharing from the TV is done with playlists, see the addendum.)

## Anti-goals

- A share must never widen access beyond the one title for the one profile.
- A failure must never leave a child seeing more than before. Every failure mode ends
  with the shared title disappearing, not with extra titles appearing.
- The plugin never edits the original item, its files, or its metadata.
- Uninstalling must put every user's settings back as they were.

## The rule

- You can share a title only if you can already see it. Someone who can see an 18 can
  share an 18. A profile capped at 12 cannot share a 15.
- Shares are added on the fly and take effect straight away.
- There is an audit page, and titles can be added from it.
- Approval and notification for child-to-child sharing: later version.

## Approaches considered

1. **Rating lever.** Sharing lowers the title's `CustomRating`. Cheap, but server-wide:
   every profile with the same cap gets the title. Rejected, breaks "one profile".
2. **A "Shared with me" library per recipient (the chosen approach).** The plugin keeps a
   folder per recipient holding links to the shared files, exposes it as a library only
   that recipient can open, and gives the linked copy a rating override. Enforcement is
   Jellyfin's own library access and rating checks.
3. **Request-time interception.** The plugin rewrites query results and stream
   permissions per user. A true grant with no copies, but it hooks server internals that
   change between releases, and a bug there shows children content. Rejected as too risky
   for a child-safety feature.

Tag-based plugins (Per-User Visibility, Personal Library) were checked as a fourth route.
Tags can only narrow what a profile sees. They cannot lift a title over a rating cap.

## What the spike proved (Jellyfin 12.4.0, throwaway container, REST only)

Three clips rated PG, 15, 18. Two
children capped at 12 with unrated blocked, one uncapped adult.

| Check | Result |
|---|---|
| A symlink to the file inside a real folder becomes a separate item with its own id | yes |
| Shared copy with NFO `<mpaa>GB-18</mpaa><customrating>12</customrating>` is visible to the capped recipient, badge still reads 18 | yes |
| Recipient gets full media info and can stream the copy | yes |
| Recipient still gets 404 on the original item | yes |
| Second child (same cap, no access to the share library) gets 404 on the copy; nothing in search, latest or views | yes |
| Uncapped adult sees no duplicates | yes |
| Survives a full "replace all metadata" refresh | yes |
| Removing the folder and scanning revokes the share | yes |
| `.strm` stub instead of a symlink | works for listing, but no media info and static streaming returns the stub file. Not used. |
| Watch state on the copy vs the original | separate (a cost, see below) |
| A user left on "all libraries" sees every share library | yes, this is a problem the plugin must handle |

Also observed: `GET /Videos/{id}/stream?static=true` returned the file to any signed-in
user who supplied the item id, including for items that return 404 everywhere else. That
is existing Jellyfin behaviour and applies with or without this plugin. Item ids are not
discoverable by a restricted user. The README must say so plainly.

Not yet proven, to be tested during the build:
- TV series (real folders, a symlink per episode, new episodes picked up later).
- Creating the library and editing user policies from inside the plugin rather than REST.
- Letting normal metadata fetchers fill artwork on the copy without touching the override.

## How it works

**Storage.** A share root folder, configurable, default inside Jellyfin's data folder:
`<share root>/<recipient id>/movies/<Title (Year)>/` holding a symlink to the original
file and an NFO written by the plugin. The NFO carries the title, year, provider ids
(so artwork and overview match the original), the true rating, and a `customrating`
low enough for the recipient's cap. The plugin's own record of shares (item, sharer,
recipient, date) lives in its configuration folder and is the source of truth; the
folders are rebuilt from it.

**Libraries.** One library per recipient per content type, created on first share and
removed when the last share goes. Named `Shared with <name>` (`Shared with <name> (shows)` for series): library names
must be unique on the server, so the recipient's name is part of it (Ruling 4).

**Library access.** Jellyfin's default is "all libraries", which would show every share
library to everyone. On first use the plugin switches each user from "all libraries" to
an explicit list of the real libraries, records that it did so, and adds any library
created later to those users so their experience doesn't change. Each recipient
additionally gets their own share library. Uninstall reverses this from the record.

**The rating override.** `CustomRating` on the copy, chosen as the highest rating the
recipient's cap allows, so the badge keeps the true certificate. If a server upgrade
recomputes rating levels and drops the override (seen on 12.1 to 12.2), the copy becomes
blocked, which is the safe direction. The plugin re-applies overrides at startup and
after each library scan.

**Sharing check.** Done on the server as the sharer: the item must be visible to that
user under their own policy. The plugin does not look at the rating field to decide.
A share to a profile that can already see the title is refused as unnecessary.

**What a share can override (decided 2026-11-19).** A share
lifts the title over the recipient's rating cap and "block unrated" setting, and reaches
titles in libraries the recipient has no access to. It does not override a blocked tag:
if the recipient's profile blocks a tag the title carries, the share is refused with that
reason, because a blocked tag is a deliberate "never". If the recipient is on an
allowed-tags list, the copy is given one of those tags so it shows.

**When things change (my calls).**
- Recipient's cap lowered: the share stays and the override is lowered to match. A share
  is an explicit grant and lasts until someone removes it.
- Recipient's cap raised so they can see the original: the copy is removed and the share
  is closed as no longer needed.
- Sharer later loses access or is deleted: the share stays. The audit page shows the
  sharer as gone. (Final review, C1: for a SERIES the copy holds only the episodes the
  sharer can see now, so a sharer who loses sight of every episode ends the share, which
  the audit page shows as broken, "original missing". A deleted sharer's series shares
  stay, adding no episode with a rating of its own that differs from the series'.)
- Original file replaced or renamed (quality upgrade): the share follows the title. The
  record stores provider ids and the source library; on a missing original the plugin
  looks for exactly one item in that library with the same provider ids and relinks. No
  match or more than one match: the share is marked broken on the audit page and the
  copy is removed.

**Who may share.** One admin setting: everyone (the default), only
profiles with no rating cap, or admins only. The recipient list shown to a user is every
enabled profile except the user, hidden profiles and administrators included: Jellyfin
hides every new profile from the login screen by default, so "hidden" says nothing about
whether a profile may receive a share (Ruling 2). Changing the setting does not remove
shares already made.

**Series (my call).** The shareable unit is a whole series. Seasons and single episodes
are not shareable in v1. `POST /Shares` accepts a Movie or a Series id only.

**Re-sharing.** A recipient cannot pass on a title they only see
because it was shared with them, unless they could see the original anyway. Otherwise
one share to one child becomes a share to every sibling. Easy to relax later.

**Keeping in step.** After each library scan, and on a scheduled task, the plugin checks
every share: original still exists, link still resolves, recipient's cap unchanged,
recipient really sees the copy. Broken links are rebuilt, shares whose original is gone
are marked and removed.

## What people see

- **Any user, web interface:** a "Share" page added by the plugin: search the titles you
  can see, pick a profile, share. Lists what you have shared and lets you undo it.
  A "Share with…" entry in the item menu is a later milestone because it means injecting
  script into the web client.
- **Recipient, every client:** a `Shared with <name>` library containing the titles.
- **Admin, dashboard:** the audit page.

```
Sam (cap 12)                                               [+ Add title]
  Example Film One (15)           visible    shared by Alex   12 Nov   [remove]
  Example Film Two (15)           BLOCKED    shared by Alex   override lost, re-applying
Jo (cap 10)
  nothing shared
```

"Visible" is measured by asking the server what that profile can actually see, never by
reading settings.

## API (authenticated, under `/SharedWithMe`)

- `GET /Shares` mine; all of them for an admin
- `POST /Shares` `{ itemId, recipientUserId }`
- `DELETE /Shares/{id}` sharer, or an admin
- `GET /Recipients` profiles I can share with
- `GET /Audit` admin only, includes live visibility per share

## Constraints

- Jellyfin 12.x, built against 12.4.0, .NET 10, C#. (My call: 12 only for v1.)
- Linux, macOS and Docker servers. The share root must be on a filesystem that supports
  symlinks, and in Docker it must sit inside a mounted volume. Windows is unsupported in
  v1 because symlinks need elevated rights there.
- Licence GPL-3.0, matching the official plugin template. (My call.)
- No external services, no telemetry.

## Known costs, to be stated in the README

- Watch progress on a shared copy is separate from the original.
- Each recipient gets an extra library on their home screen.
- Users move from "all libraries" to an explicit list (maintained by the plugin).
- A shared title shows twice in an admin's own unfiltered views only if the admin is
  granted a share library, which the plugin never does.

## Scope and order

1. Movies: storage, libraries, access, sharing rule, API, audit page, user share page.
2. Series.
3. Release packaging: repository manifest, README, reproducible zip, GitHub release.

## Left to the builder

- Internal class layout, storage format of the share record, test framework.
- Whether artwork is fetched by normal providers or copied from the original item.
- Exact wording and layout of the two pages, within "plain, native Jellyfin look".
- Build and test set-up: build in a `dotnet/sdk:10.0` container (`tools/build.sh`) and run the integration tests
  against a disposable test server, never against a server you use.

## Design addendum (2026-11-19): sharing from the TV

Many families use a TV app (for example Wholphin) rather than the web app, so sharing has to be possible from a
native client. A server plugin cannot add a button to a native app, so sharing from the TV
uses something every client already has: playlists.

- Each user who may share gets one private playlist per possible recipient, named
  `Share with <Name>`. A possible recipient is any other enabled profile (hidden ones
  included) that is restricted (has a rating cap, blocks unrated items, has blocked or
  allowed tags of its own, or is not on all real libraries).
- Adding a movie or series to that playlist from any client shares it, under exactly the
  same rules as the share page (same `ShareService.Create`, no second code path).
- The playlist is the list of what you have shared with that person from the TV. Removing
  a title from it removes the share.
- A refused share cannot show a message on a TV. The title is taken back out of the
  playlist, and the refusal with its reason is listed on the share page and audit page
  under "Not shared".
- An episode or season added to the playlist stands for its whole series: adding one shares the series (Ruling E). The
  REST API still refuses an episode or season id (`NotShareableType`).
- Admin setting `TvSharingPlaylists` (default on). Off removes the playlists; shares stay.
- Playlists follow the people: created when a profile becomes a possible recipient,
  renamed when a profile is renamed, removed when the profile is deleted or stops being
  restricted. A Restore removes them and turns sharing from TV apps off; uninstalling removes them too (Task 10).

Unverified until tried on a real TV: that Wholphin's item menu offers "Add to playlist"
and lists these playlists. If it does not, favourites are the fallback trigger and this
task is re-planned.

Rulings recorded during Task 11 (2026-11-19):
- **A, unguessable ids.** 12.4 derives a playlist's id from its folder, which comes from the name at creation. So each
  plugin playlist is created under a random `swm-<32 hex>` name (cryptographic RNG) as a private Video playlist, then
  renamed to `Share with <Name>`; it is never created under the display name.
- **B2, the owner's tag.** `/Search/Hints` hands every private playlist's id to every user, and a blocked tag closes
  that. So a plugin playlist carries its OWNER's `swm-<owner id>` tag. One rule now covers copies and playlists alike:
  every user blocks the `swm-` tag of every other user who has a share library or owns a plugin playlist, and nobody
  blocks their own. Blocks only narrow access and never move anyone off "all libraries".
- **D, privacy.** No listing, search or playlist route gives another user a plugin playlist's name or id. Jellyfin's
  `/Items/Latest?parentId=<playlist id>` still lists a playlist's contents to anyone who already has the id. That is
  documented as known Jellyfin behaviour; the plugin makes the id neither derivable nor findable.
- **E, series.** A series in a playlist is stored as its episodes. The playlist is read as titles: a film is itself, and
  any episode stands for its whole series. A refused series loses all its entries, and a series share ends when none of
  its episodes is left.
- A restore turns sharing from TV apps off and removes the playlists, so they do not come straight back.


## Version 0.2 additions

Three features on top of 0.1. Defaults keep 0.1 behaviour for anyone who changes nothing.

### A. Chosen sharers

`WhoMayShare` gains a fourth value, `SelectedProfiles`. Administrators may always share. Any other profile may share only
when it is in the new setting `AllowedSharers` (a list of user ids). The audit page shows a checkbox per enabled
non-administrator profile under "Who may share" when `SelectedProfiles` is picked. Everything that already asks "may this
profile share" (the API, the playlists, the share page's list of people) goes through the one `MayShare` check, so the
`Share with <name>` playlists appear and disappear with the setting. A profile that stops being allowed keeps the shares it
already made (as with the other values); administrators remove them on the audit page.

### B. Expiring shares

A share record gains `ExpiresUtc` (null = until removed). The web share page and the audit page's "Add title" get a
"Keep it" choice: until I remove it, 1 day, 1 week, or a date. A new admin setting `TvShareDays` (0 = until removed)
is the expiry applied to shares made from a TV playlist, which has no way to choose. The check-shares run closes a share
whose `ExpiresUtc` has passed, exactly as if it had been removed: state Closed, reason "expired", copy and library entry
removed, playlist entry taken out. The audit page and the share page show the expiry and say "expired" afterwards.
Expiry never extends access. A record whose expiry cannot be read is treated as not expired only if it was never set.

### C. Parent approval

New setting `ApprovalMode`: `None` (the 0.1 behaviour, default) or `LimitedProfiles`. With `LimitedProfiles`, a share made
by a profile that has an age limit (or blocks unrated items, i.e. is "restricted" by the same test the playlists use)
is created in a new state `Pending`: no copy, no library, no change to anyone's access. Administrators' shares and shares by
unrestricted profiles are unaffected. The sharer sees "Waiting for approval" (share page, and the playlist entry is kept, not
read as taken out). The audit page lists pending shares at the top with Approve and Decline. Approve builds the share through
the normal path (all the usual rules are checked again at that moment) and makes it Active; Decline closes it with reason
"declined" and takes it out of the playlist. A pending share never appears to the recipient. Pending shares do not expire
unless the sharer withdraws them (Undo on the share page).

Notification: a new admin setting `NotifyUrl`. When set, the plugin POSTs a small JSON body
`{"event": "approval-needed"|"shared"|"approved"|"declined", "message": "<plain sentence>", ...}` to it, once per event,
never blocking the share (a short timeout, failures logged and ignored, no retry storm). A second setting
`NotifyOn` is `Nothing` (default), `ApprovalsOnly` or `EverySharing`. The message names the people and the title,
never a file path.
