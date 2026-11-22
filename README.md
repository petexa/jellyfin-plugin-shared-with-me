# Shared With Me

A Jellyfin server plugin. A user who can see a film or series can share it with one other profile that cannot. That
profile then sees the title in a library of its own; nobody else gains anything.

It is for households where profiles have age limits or other restrictions, and an adult wants to let one child watch
one title without changing that title's rating or restrictions for everyone else.

The plugin changes every user's library access setting and blocked tags, listed in the next section. Uninstalling it
puts those back; the cases where it cannot are listed under "How to undo it".

## Where to go

Once it is installed (see "Install"):

- **Sharing from a TV or phone app:** open a film or series, choose "Add to playlist", and pick `Share with <name>`.
  Taking a title back out of that playlist ends the share. Nothing else to open or remember.
- **Sharing from a browser:** the share page, `http://<your server>/SharedWithMe/Page` (sign in to Jellyfin first).
  Jellyfin shows plugin pages to administrators only, so there is no menu entry for anyone else. The administrator's
  page shows the exact address with a Copy button (Dashboard, Plugins, Shared With Me): send it to the family or
  bookmark it on their phones.
- **Administrators:** Dashboard, Plugins, Shared With Me lists every profile and what it has been given, and is where
  you remove a share, change who may share, or press Restore everyone.

## What the plugin changes on your server

- **Library access.** Once the first share exists, every user on "Enable access to all libraries" is moved to an explicit
  list of all your libraries, so nobody can open another person's share library. The plugin records who it moved and
  which libraries it gave them. It adds libraries you create later to those users, and it isolates users you create
  later straight away. In the dashboard those users no longer show "all libraries" ticked. If you tick "Enable access to
  all libraries" for a profile while shares exist, the plugin undoes it within about a second.
- **Lists you narrow are left alone.** If you later take a library away from one of those users (untick it), the plugin
  stops managing that user's list: it adds no new library to it, and Restore everyone or an uninstall leaves the user on
  that list instead of putting them back on "all libraries". Such users are named on the audit page, in the Restore
  result and in the uninstall's last log line ("left on their own library list because it was changed after the
  plugin moved them").
- **Blocked tags.** Every shared copy carries the tag `swm-<recipient id>`, and every other user, administrators
  included, gets that tag in their blocked tags. Each plugin playlist carries its owner's `swm-<owner id>` tag, blocked
  the same way. With sharing from TV apps on (the default), playlists exist from install, so these tags are added at
  install, before any share. If you remove one of these tags by hand, the plugin puts it back within about a second.
- **Libraries.** For each profile that receives a share, a library `Shared with <name>` (and `Shared with <name> (shows)`
  for series), which only that profile can open.
- **Playlists.** `Share with <name>` playlists, private to their owner (only while sharing from TV apps is on).
- **Files.** The plugin writes files directly only inside its share folder (links to your media files plus small NFO files
  it writes) and in its own configuration folder. Through Jellyfin it creates and removes libraries (`Shared with
  <name>`, `Shared with <name> (shows)`), creates, renames, tags and removes playlists (`Share with <name>`), and changes
  profiles' library access and blocked tags. It never changes your media files, their metadata, or the original items.
- **Another tool that manages the same settings.** Another plugin or script that keeps rewriting users' library access
  or blocked tags will fight with this one.

**Who may share, and the default.** By default any profile, including a child with an age limit, can share a title
within their own limit with a sibling whose limit is lower, with no approval and no notification (both can be turned on,
see "Approval and notifications" below). The setting "Who may
share" (plugin settings) has four values: everyone (the default), only profiles with no age limit, only
administrators, or administrators and the profiles you tick. With the last one the audit page shows a tick box for each
enabled profile that is not an administrator: for example tick Sam and leave Jo and Alex unticked, and Sam may share
while Jo and Alex may not. Administrators may always share and are not listed. The setting decides everything about
sharing at once: who may share on the share page and through the API, who is offered a list of people there, and who
gets `Share with <name>` playlists (they appear and disappear within a few seconds of saving). Changing it, or unticking
a profile, does not remove shares already made: remove those on the audit page. Anyone who may share sees the names of
all enabled profiles, hidden profiles and administrators included, in the share page's list of people to share with.

**Before you disable the plugin or upgrade Jellyfin.** Press Restore everyone (or uninstall the plugin) before disabling
it or before upgrading Jellyfin to a version this plugin has not been tested on. While the plugin is not running, new or
edited profiles can open share libraries.

**If the plugin's record of shares cannot be read.** If `shares.json` (in `plugins/configurations/SharedWithMe/`) is
damaged or was written by a newer version, the plugin pauses sharing: it only keeps every profile out of other profiles'
share libraries (moving anyone on "all libraries" to an explicit list and blocking the share tags) and changes nothing
else. The audit page and the scheduled task say so. Profiles it moves while the record cannot be read are not recorded,
so a later Restore does not put them back on "all libraries": tick that by hand for them if you stop using sharing.

### How to undo it

- **Restore everyone** (audit page) works only after every share is removed. It removes the share libraries and
  playlists, any shared copy still on disk, the plugin's tags, puts the users it moved back on "all libraries" (except
  those whose list you narrowed since, see above), and turns sharing from TV apps off. Shares still waiting for approval
  do not count (they changed nothing): they do not stop a restore, and a restore that finishes closes them ("sharing was
  switched off"), so a later approval cannot bring the plugin back.
- **Uninstall** (Dashboard, Plugins) does not wait for shares to be removed:
  - It removes every playlist, copy, share library and plugin tag, and puts the users it moved back on "all libraries"
    (except those whose list you narrowed since, see above). Shares waiting for approval are discarded (closed in the
    renamed record of shares).
  - It does this before the restart; the restart then unloads the plugin.
  - It does not turn the "Sharing from TV apps" setting off. If you install the plugin again later, it makes the
    playlists and adds their tags again at once.
- An uninstall leaves two files in `plugins/configurations/`: the plugin's settings file
  (`Jellyfin.Plugin.SharedWithMe.xml`), and its renamed record of shares
  (`SharedWithMe/shares.uninstalled-<date>.json`). The plugin never reads the record again; it is there so you can see
  what was shared.
- The clean-up runs on uninstall only. Disabling the plugin does not run it.
- Updating the plugin from a repository did not run the uninstall clean-up in a test with one shared film; the share was
  still there afterwards.

**What an uninstall can leave behind.** Each of these is named in the server log, which ends with one line listing
everything left behind. Once the plugin is gone nothing repairs these for you. New profiles get "all libraries" by
default.

- A shared copy folder that could not be deleted stays on disk. If you install the plugin again, it removes that folder
  when the next share is made after the reinstall, before any share library reads it. Until then you can delete it by
  hand.
- A share library that could not be removed leaves everyone on their explicit library lists, and leaves the `swm-`
  blocked tags on every other profile. Remove the library under Libraries. Then, for each profile, tick "Enable access
  to all libraries" and delete its `swm-` blocked tags.
- An unreadable `shares.json` at uninstall means nobody is put back on "all libraries" and no tag is lifted. Fix it by
  hand the same way.
- A plugin playlist that could not be removed keeps its owner's `swm-` tag blocked for everyone else. Delete the
  playlist, then delete that tag from each profile.

## Requirements

- Jellyfin 12.4.0 (the only version tested; later versions not tested).
- A Linux server or the official Docker image (tested). macOS: not tested (paths are compared case-sensitively; a
  case-insensitive filesystem is not handled). Windows is not supported.
- A share folder on a filesystem that supports symbolic links. By default it is `<Jellyfin data folder>/shared-with-me`,
  which you can change in the plugin settings before the first share.
- In Docker:
  - the share folder must be inside a mounted volume, so it survives the container being re-created. The default
    location is inside the data folder, which is normally in the `/config` volume;
  - the media must be reachable from inside the container at the same paths Jellyfin uses for your libraries. The plugin
    links to those paths.

## Install

From a plugin repository:

1. Dashboard, Plugins, Repositories: add `https://raw.githubusercontent.com/petexa/jellyfin-plugin-shared-with-me/main/manifest.json`.
2. Install "Shared With Me" from the catalogue, then restart Jellyfin.

By hand:

1. Download `SharedWithMe_<version>.zip` from https://github.com/petexa/jellyfin-plugin-shared-with-me/releases.
2. Create a folder `SharedWithMe_<version>` in Jellyfin's `plugins` folder (for example
   `/config/plugins/SharedWithMe_0.2.0.0`) and unzip the file into it. It holds `Jellyfin.Plugin.SharedWithMe.dll` and
   `meta.json`.
3. Restart Jellyfin. Dashboard, Plugins should list "Shared With Me" as Active.

## First use

- **Share page:** `http://<your server>/SharedWithMe/Page` (the administrator's page shows the exact address with a Copy
  button), after signing in to the Jellyfin web app in the same browser.
  Search the titles you can see, pick a profile, share. The page lists what you shared (with an undo), what was shared
  with you, and anything a TV playlist could not share. Jellyfin shows plugin pages only to administrators, so other
  users need the address: there is no menu entry for them.
- **Audit page (administrators):** Dashboard, Plugins, Shared With Me. It shows every profile and the shares it received:
  - who shared each title, and whether the profile can see it right now (asked of the server, not read from settings);
  - at the top, when there are any, the shares waiting for approval, with Approve and Decline;
  - the settings: who may share, the share folder, sharing from TV apps and how many days a share made from a TV app lasts,
    approval, and the notification address with a "Send a test message" button;
  - when each share ends ("until removed" or a date), and "expired" afterwards;
  - "Add title", "Check now", and "Restore everyone".
- **TV apps:** each user who may share gets a private playlist `Share with <name>` for each profile with restrictions.
  Adding a film, a series, or any episode of a series to it shares that title with that profile; taking it out stops
  sharing it. TV apps have no "Share" button: the playlist is the way to share from a TV.

## How sharing behaves

- **You can share what you can see.** If you can see a title, you can share it with a profile that cannot. A profile
  that can already see it gets nothing new, and the share is refused as unnecessary.
- **Who may share** is a setting: everyone (the default), only profiles with no age limit, only administrators, or
  administrators and the profiles you tick. See "Who may share, and the default" above: by default a child can share
  with a younger sibling.
- **Blocked tags win.** If the recipient's profile blocks a tag the title carries, the share is refused (the refusal does
  not name the tag). A share lifts a title over an age limit, over "block unrated items" and into a profile without
  access to its library. It never lifts a blocked tag. If the recipient starts blocking a tag the title carries, the
  share is closed within about a second.
- **Whole series only.** A series is shared as a whole; seasons and single episodes cannot be shared. From a playlist,
  adding any one episode shares the whole series, and the share ends when no episode of that series is left in the
  playlist.
- **Only the episodes the sharer can see.** A series share holds the episodes the sharer can see now, and never an
  episode carrying a tag (of its own) that the recipient blocks. An episode rated higher than its series is left out for
  a sharer whose limit is below that rating, and each shared episode keeps its own rating on the copy. If the sharer
  later loses sight of an episode, or the recipient starts blocking its tag, it leaves the copy. If the sharer can see
  none of the episodes any more (for example their own age limit was lowered), the share ends and the audit page shows it
  as broken with the reason "original missing", although the original is still there. If the sharer's profile is
  deleted, the share stays, but episodes with a rating of their own that differs from the series' are no longer added.
- **Shares can end by themselves.** On the share page and under "Add title" on the audit page, "Keep it" offers: until I
  remove it (the default), 1 day, 1 week, or until a date (the end of that day, in the browser's time zone). Through the
  API, send `ExpiresUtc` (an ISO time, UTC) with the share; any time in the future up to 366 days ahead is accepted. A TV
  app has no way to choose, so the setting "Days a share made from a TV app lasts" (0, the default, keeps it until it is
  removed; at most 366) gives every share made from a `Share with <name>` playlist that end date. Shares made on the
  share page keep their own choice. When the time is up the share ends as if it had been removed, within about 15
  seconds: the copy and, if it was the last one, the share library go, the title is taken out of the playlist, and the
  pages show the share as expired (kept on the audit page for 30 days). An end date never gives anyone more: sharing the
  title again is a new share.
- **No passing on.** A profile cannot share a title it can see only because it was shared with it, unless it could see
  the original anyway.
- **When files are replaced** (renamed, or a new version): after a library scan the share follows the title, if the
  original's library holds exactly one title with the same provider ids and, while the sharer's profile exists, the
  sharer can see it. Otherwise the share is marked broken on the audit page and the copy is removed. A share marked
  broken because its original went missing comes back by itself if a matching title reappears.
- **New episodes** of a shared series reach the recipient after the next library scan. Removed episodes leave it.
- **When age limits change:** if the recipient's limit is lowered, the share stays and the copy's rating override is
  lowered to match. If it is raised so that the recipient can see the original, the share is closed as no longer needed.
- **When profiles are deleted:** if the recipient is deleted, its shares, its share library, its files and its tag go. If
  the sharer is deleted, the shares stay, and the audit page shows the sharer as gone.
- **When something fails,** the plugin is built so that the shared title disappears for the recipient rather than anyone
  seeing more than before. The failure cases in the test suites check this.

## Approval and notifications

**Approval.** The setting "Approval" (plugin settings) is off by default. With "Shares by profiles with limits wait for an
administrator", a share made by a profile that is not an administrator and has an age limit or other restrictions (it
blocks unrated items, blocks or allows tags of its own, or cannot open every library: the same test that decides who gets
a `Share with <name>` playlist) is not made at once. It waits:

- Every rule is checked when the profile shares, so a share that could never happen is refused at once, as before.
- While it waits nothing exists for it: no copy, no share library, and nobody's library access or tags change. The
  recipient sees nothing of it anywhere (pages, libraries, search).
- The share page shows it to the person who shared as "Waiting for approval", with Undo to withdraw it. From a TV app,
  the title stays in the `Share with <name>` playlist while it waits; taking it out withdraws it.
- The audit page lists every waiting share at the top, with who wants to share what with whom and when, and Approve and
  Decline buttons. Approve checks every rule again at that moment and then makes the share exactly as an immediate one is
  made. If a rule now refuses it (for example the recipient can see the title by now, or the sharer may no longer share),
  it is closed with the reason "no longer possible: ..." instead. Decline closes it as "declined", with an optional note
  (plain text, at most 200 characters) that the person who shared sees on the share page; a title declined from a TV
  playlist is taken out of the playlist and does not come back by itself (putting it in again asks again).
- A waiting share does not expire. Its end time, if one was chosen (or set by "Days a share made from a TV app lasts"),
  counts from when it was asked for: approved in time, it ends then; approved after that time, it is closed as expired
  instead.
- Shares by administrators and by profiles without restrictions are never held. Turning approval off does not make
  waiting shares happen: approve or decline them.

**Notifications.** Set "Notification address" to an `http://` or `https://` address, and "Notify on" to "Approvals only"
(a share is waiting, approved, declined) or "Every share" (those, and every share made at once, from a TV app too). The
server then POSTs a small JSON message to that address, for example:

```json
{"event": "approval-needed", "message": "Sam is waiting for approval to share Example Film with Jo.", "sharer": "Sam", "recipient": "Jo", "title": "Example Film"}
```

- `event` is `approval-needed`, `shared`, `approved` or `declined` (and `test` for the button on the audit page). The
  message names the people and the title, nothing else: no file path, no decline note.
- It is sent by the Jellyfin server, so the address must be reachable from the server, and it can be any address the
  server can reach (only an administrator can set it). Redirects are not followed.
- It never holds up or fails a share: it is sent in the background, gets one attempt with at most 5 seconds, and a failure
  is written to the server log as a warning naming only the address's scheme, host and port.
- An address that is not `http://` or `https://` is refused when the settings are saved (the previous one stays).
- "Send a test message" sends `{"event": "test", "message": "Shared With Me test message.", ...}` to the saved address
  and says whether it was accepted.

## Known limits and costs

- Watch progress on a shared copy is separate from the original's.
- Each recipient has an extra library on their home screen.
- Episodes of a shared series copy are titled by their file name, not by the episode's title.
- Films stored as a disc folder (Blu-ray `BDMV` or DVD `VIDEO_TS`) cannot be shared. A film in several parts, or with
  alternate versions, is shared as its primary file only.
- Shows numbered by date or by absolute episode number, and episodes with no season or episode number, may be shared
  only in part; an episode without numbers is left out.
- An episode whose file name is extremely long (an NFO name over 255 bytes) is left out of the share.
- If the recipient's allowed-tags list changes, an existing series share breaks. Remove it and share the series again.
- A brand-new user, or a user an administrator has just edited, can reach share libraries for a moment before the
  plugin isolates them: under half a second in testing.
- Straight after a share, the audit page can say "visible now: no" for a few seconds, until the server has read the
  copy's rating override.
- Non-administrators reach the share page by its address only (Jellyfin shows plugin pages to administrators only).
- TV apps have no "Share" button; sharing from a TV is done with the `Share with <name>` playlists.
- The share folder cannot be changed while shares exist.
- Windows is not supported.
- Not tested behind a base URL (a reverse proxy path such as `/jellyfin`).
- Not tested on a real TV client at the time of writing. Sharing through playlists was tested through the server's API.
- Do not downgrade the plugin: a share record written by a newer version may be refused, or partly ignored, by an older
  one. Set "Who may share" to a value 0.1 knows before going back to 0.1: it does not know "administrators and the
  profiles you tick", and may then fail to read its settings and fall back to the default, everyone. Versions before 0.2's
  expiring shares do not know end dates: they ignore them and write the record back without them, so every share with an
  end date then lasts until it is removed. Remove those shares first. Versions before 0.2's parent approval do not know the
  state "Pending": if the record of shares holds a share waiting for approval, they cannot read the record at all and
  pause sharing (see "If the plugin's record of shares cannot be read"): existing copies stay, but no share can be made or
  removed and nothing is kept in step until the newer version is back or the record is fixed. They also ignore the
  approval and notification settings, so every allowed share is made at once and nothing is sent, and their next settings
  save drops those settings. A decline note on a closed share is dropped when they write the record. Before going back, approve or decline every waiting share and set "Approval" to off.

## Jellyfin behaviour you should know about

These are how Jellyfin 12.4 itself behaves. They apply with or without this plugin, but they shaped how it works.

- **Streaming by id.** Any signed-in user who supplies an item's id can stream it with
  `GET /Videos/{id}/stream?static=true`, even an item that answers 404 everywhere else. The plugin does nothing about
  this route: anyone who holds a shared copy's item id can stream that copy. In testing, a profile was not given the ids
  of items it cannot see.
- **Routes that skip library access.** `GET /Items/Latest?parentId=`, `GET /Items/Filters?parentId=` and
  `GET /Items?ids=` apply the caller's age limit and blocked tags but not library access. `/Items?parentId=<series or
  season id>` and `/Shows/NextUp?seriesId=` behave the same below the library level.
  - A shared copy is rated within the recipient's limit, so another profile with the same limit would pass that check.
  - This is why every copy carries the recipient's tag, which everyone else blocks: the tag is what keeps copies off
    those routes.
  - If an administrator removes one of those blocked tags by hand, that user can, until the plugin puts it back (about a
    second), list another profile's shared copies through those routes, given the share library's id or the item ids.
- **Private playlists.**
  - `GET /Search/Hints` returns every user's private playlists, name and id, to every signed-in user.
  - `GET /Items/Latest?parentId=<playlist id>` lists a playlist's contents to anyone who has its id. It filters by the
    caller's own age limit and blocked tags.
  - What the plugin does: each `Share with` playlist gets a random folder name, so its id cannot be worked out from its
    name, and it carries its owner's tag, which every other user blocks. That keeps it out of everyone else's search,
    Search/Hints and `/Items?ids=`.
  - What remains: anyone who obtains a plugin playlist's id can still list its contents through `/Items/Latest`. Your
    users' own private playlists (not made by the plugin) still appear in everyone's `/Search/Hints`.
- Websocket and session messages were not examined.

## How this was built and tested

- Built against Jellyfin 12.4.0 (.NET 10) and tested on a Jellyfin 12.4.0 server in Docker, with test media, four test
  profiles and an administrator.
- The unit tests run against an in-memory stand-in for the server.
- Integration scripts in `tests/integration/` drive the real server over its HTTP API as each profile:
  - sharing films and series;
  - what other profiles can and cannot reach;
  - keeping shares in step (renamed files, changed limits, deleted profiles, new libraries and users);
  - the two pages;
  - sharing through playlists;
  - the uninstall;
  - a clean install from the release zip.
- Also tested, once: an update through a plugin repository, with one shared film, kept the share.
- The plugin was written with AI assistance (Claude, by Anthropic) under the maintainer's direction. Each behaviour
  described here is covered by a unit test, an integration script, or an observation recorded in
  [docs/api-notes.md](docs/api-notes.md).
- There is no warranty; see the licence.

## Licence

GPL-3.0. See [LICENSE](LICENSE).

## Building and testing

- `tools/build.sh build|test` builds or runs the unit tests inside the .NET 10 SDK container (Docker is the only
  requirement). `python3 tools/package.py` then writes `dist/SharedWithMe_<version>.zip`, `manifest.json` and `build.yaml`.
- `tests/integration/` drives a real Jellyfin 12.4 server through its API. It is meant for a **disposable** test server (a
  Docker container named `jf-share-spike` whose config folder is `/opt/jf-share-spike/config`; see `reset.py`). Never point
  it at a server you use. Set `SWM_JF_URL` to the test server's address and, if its Docker host is another machine,
  `SWM_RUN` to a command that runs a script from stdin there (for example `ssh me@docker-host bash -s`).
  `tools/deploy-spike.sh` installs the packaged zip into that container.
