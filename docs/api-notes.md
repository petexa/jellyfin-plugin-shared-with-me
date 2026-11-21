# API and environment notes

Facts verified against Jellyfin 12.4.0 (spike server `jf-share-spike`). Add to this file as later tasks confirm more.

## Build
- `Jellyfin.Controller` and `Jellyfin.Model` 12.4.0 restore from nuget.org; target `net10.0`.
- Plugin csproj: `ExcludeAssets=runtime` on both packages (server supplies them). The xUnit project references the same two packages WITHOUT the exclude, otherwise the test host throws `FileNotFoundException: MediaBrowser.Model`.
- Plugin `OutputPath` is `../artifacts`, no TFM subfolder, so the DLL is `artifacts/Jellyfin.Plugin.SharedWithMe.dll`.
- `BasePlugin<T>` ctor takes `(IApplicationPaths, IXmlSerializer)`; `BasePluginConfiguration` lives in `MediaBrowser.Model.Plugins`.
- Warnings-as-errors with `GenerateDocumentationFile` means public members need XML docs (CS1591).
- Build pipeline: `tools/build.sh build|test` runs `mcr.microsoft.com/dotnet/sdk:10.0` in Docker with a NuGet cache in `~/.cache/swm-nuget`. First build ~1 min (image pull + restore), later ~15 s.
- Solution file is `.slnx` (the .NET 10 default); the scripts build the csproj files directly.

## Plugin folder and meta.json
- Folder: `<config>/plugins/SharedWithMe_0.1.0.0/` holding `Jellyfin.Plugin.SharedWithMe.dll` and `meta.json`.
- `meta.json` is OPTIONAL: if deleted, Jellyfin regenerates it on the next start (empty category/overview, targetAbi `12.4.0.0` taken from the assembly) and the plugin still loads `Active`.
- If present, Jellyfin rewrites it on load (adds `"assemblies": []`, normalises timestamp, may blank `description`). `deploy-spike.sh` supplies it anyway.
- `GET /Plugins` as admin lists `{Name, Version, Status, Id}`; `Status` is `Active` once loaded.
- Restart takes roughly 10 s; `deploy-spike.sh` polls `/System/Info/Public`.

## REST (admin, via `tests/integration/jf.py`)
- `GET /Library/VirtualFolders` returns `Name`, `Locations`, `ItemId`; `DELETE /Library/VirtualFolders?name=..&refreshLibrary=false` removes one.
- `GET /Users/{id}` returns `Policy`; null policy fields (`MaxParentalRating`, `MaxParentalSubRating`) are OMITTED from the JSON, use `.get`. `POST /Users/{id}/Policy` with the edited policy sets it; sending `null` for them clears the cap.

## Spike environment
- `/shares` is mounted into `jf-share-spike` from `/opt/jf-share-spike/shares` (root-owned). `reset.py` empties it with `find /shares -mindepth 1 -delete` inside the container, and removes `/opt/jf-share-spike/config/data/shared-with-me` (the default share root is `<data path>/shared-with-me`).
- Default plugin GUID: `4069397e-cf0d-460a-9a00-e5175c137516` (fixed forever).

## Task 6: server API facts (Jellyfin 12.4.0)

How they were found: XML docs of the server DLLs (`/jellyfin/*.xml` in `jf-share-spike`), and a small reflection dump of the 12.4.0 server assemblies run in the SDK container (signatures, enum values, constant values). The NuGet packages ship no XML docs.

### Services and DI
- `MediaBrowser.Controller.Plugins.IPluginServiceRegistrator.RegisterServices(IServiceCollection, IServerApplicationHost)`. The class needs a parameterless constructor. Ours is `Jellyfin.Plugin.SharedWithMe.ServiceRegistrator`.
- The plugin csproj needs `<FrameworkReference Include="Microsoft.AspNetCore.App" />` for controllers. Jellyfin picks up plugin controllers on its own.
- `IApplicationPaths` resolves from DI. `DataPath` = `/config/data` in the spike, so the default share root is `/config/data/shared-with-me`. `PluginConfigurationsPath` = `/config/plugins/configurations`.
- The store lives at `<PluginConfigurationsPath>/SharedWithMe/shares.json`, outside the versioned `SharedWithMe_<version>` folder, so records and policy snapshots survive an upgrade. `reset.py` deletes that file.

### Users and policies
- `IUserManager.GetUserById(Guid)`, `GetUsers()`, `GetUserDto(User, string? remoteEndPoint = null)`. `.Policy` is the full `UserPolicy`.
- The `User` entity has no `Policy` property. We read the policy through `GetUserDto(user).Policy`, which is what the dashboard round-trips.
- Writes use `IUserManager.UpdatePolicyAsync(Guid, UserPolicy)` with that full policy, changing only `EnableAllFolders` and `EnabledFolders`, so every other field is kept. Its XML doc calls it a "stopgap until the new API" but it is the documented `IUserManager` path.
- `UserPolicy.MaxParentalRating` / `MaxParentalSubRating` are `int?`. `BlockedTags`, `AllowedTags`, `IsHidden`, `IsDisabled`, `IsAdministrator`.
- **Every new user is hidden by default** (`IsHidden = true`), and hidden only means "not on the login screen". Ruling 2: `RecipientsFor` no longer filters on it. `reset.py` puts every user back to hidden, the default, and the test checks that hidden users are listed.

### Items and visibility
- `ILibraryManager.GetItemById(Guid)` returns `BaseItem?`. `Movie` is in `MediaBrowser.Controller.Entities.Movies`, `Series` and `Episode` in `...Entities.TV`.
- **`BaseItem.IsVisible(User, bool skipAllowedTagsCheck = false)` is the parental check only** ("Default is just parental allowed"). It does not check library access.
- `BaseItem.IsVisibleStandalone(User)` also walks the parents and the user's library access. `CanSee` uses `IsVisibleStandalone`, not `IsVisible` as the brief said.
- The integration test proves it: newbie (uncapped, no Movies access) sharing Spike PG gets `NotVisibleToSharer`; with access back the same request gets `RecipientAlreadySees`.
- `BaseItem.GetInheritedTags()` returns `List<string>`. It includes a tag set on the library (collection) folder: proved by `BlockedTag` with the tag only on the Movies folder. We also union `item.Tags`.
- `ILibraryManager.GetCollectionFolders(BaseItem)` returns `List<Folder>`, the libraries that contain the item. Their `Id` is the same id as `VirtualFolderInfo.ItemId` and `UserPolicy.EnabledFolders`.
- Sidecar subtitles: `BaseItem.GetMediaStreams()` where `IsExternal && Type == MediaStreamType.Subtitle`, path in `MediaStream.Path`.

### Ratings
- `ILocalizationManager.GetParentalRatings()` returns `IReadOnlyList<ParentalRating>` (`Name`, `RatingScore{Score, SubScore?}`). `Unrated` has a null score and `10` has a null sub score; we read a null sub score as 0.
- With country GB the score 12 / sub 1 entries are `12` and `12+`; `RatingChooser` picks `12` (first of equal options).

### Libraries
- `ILibraryManager.AddVirtualFolder(string name, CollectionTypeOptions? type, LibraryOptions options, bool refreshLibrary)` returns `Task`. `RemoveVirtualFolder(string name, bool refreshLibrary)` returns `Task`. `GetVirtualFolders()` returns `List<VirtualFolderInfo>` (`Name`, `Locations`, `ItemId` as an N string).
- With `refreshLibrary: false` the library has no item id until `ILibraryManager.ValidateTopLibraryFolders(CancellationToken, bool removeRoot = false)` runs. `CreateLibrary` and `RemoveLibrary` both call it, then read the id or the removal back by path.
- Library names are unique (each library is a folder under `root/default`). Ruling 4: share libraries are named `Shared with <user name>` (series: `... (shows)`), which is unique because user names are. No suffix logic; a name clash would make `AddVirtualFolder` throw.
- `CollectionTypeOptions` values are lowercase: `movies`, `tvshows`. `LibraryOptions`: `EnableRealtimeMonitor`, `MetadataSavers`, `SaveLocalMetadata`, `LocalMetadataReaderOrder` (`"Nfo"`), `DisabledLocalMetadataReaders`, `PathInfos` (`new MediaPathInfo(path)`). `TypeOptions` is left empty, so the server's default fetchers apply.
- One-library scan: `IProviderManager.QueueRefresh(Guid, MetadataRefreshOptions, RefreshPriority.High)` with `new MetadataRefreshOptions(new DirectoryService(IFileSystem))` and both modes `Default`. This is the dashboard's per-library Scan. Measured on the spike: the copy appears for the recipient about 2 s after `POST`, and is gone about 1 s after `DELETE`, with no manual scan.
- `POST /Items/{id}/Refresh` resolves the item as the caller. Admin gets 404 on a share library it was not granted, so the test grants it for that step.
- Jellyfin does NOT index a dot-prefixed folder (`.swm-tmp-*`). The control check is that the same NFO-less folder without the dot IS indexed and shown to kid1 (unrated allowed).

### Controller and auth
- The acting user is the `Jellyfin-UserId` claim on `ControllerBase.User`. The constant is `Jellyfin.Api.Constants.InternalClaimTypes.UserId`, but `Jellyfin.Api` is not a package, so the plugin hard-codes the string. An API key has no user claim; we answer 403.
- `[Authorize]` alone uses Jellyfin's default policy. A call with no token gets 401.
- JSON: PascalCase property names; enums as strings (`"Refusal": "RecipientAlreadySees"`, `"State": "Active"`); Guids in N format; null properties are omitted (`StateReason`). Request bodies bind case-insensitively (`itemId` and `ItemId` both work). An empty body gives 400.

### Jellyfin behaviour that breaks a design assumption (child safety)
- `GET /Items/Latest?parentId=<library id>` does **not** check library access: it returns items from a library the caller cannot open, filtered only by the rating cap.
- `GET /Items/Filters?parentId=<library id>` leaks the ratings and years in the same way. Other endpoints answer 401 "not permitted to access Library": `/Items?parentId=`, and `/Items/{id}`, `PlaybackInfo` and images on the copy return 404.
- It is ordinary 12.4.0 behaviour. Control: kid2 with Movies removed from its list still gets Spike PG from `/Items/Latest?parentId=<Movies>`.
- For this plugin it matters. The copy's `customrating` is set to the recipient's cap, so any other profile with the same cap passes the rating filter. Given kid1's share library id, kid2 gets the 18 listed (name, id, `GB-18`).
- With the id, `/Videos/{id}/stream?static=true` streams it (the known behaviour in design.md). The library id is a hash of the library's path, so it can be derived rather than discovered.
- **Closed (Ruling 1) with a per-recipient blocked tag.**
  - Every copy's NFO carries `<tag>swm-<recipient id, N format></tag>`. Every other user, including admin, disabled and hidden users, has that tag in `BlockedTags`.
  - **An item-level tag is enough.** On the spike, with only the copy's own tag blocked (library folder untagged), kid2's `/Items/Latest?parentId=` and `/Items/Filters?parentId=` came back empty. The library folder is therefore not tagged.
  - The NFO `<tag>` lands in the item's `Tags`: the test reads it back on the copy.
  - The integration test also runs a control. With the block taken off kid2, Latest leaks the copy again. With it put back, Latest returns `[]`.
  - Blocked tags are written by `IServerGateway.SetBlockedTags`, which uses the same full-policy round-trip as `SetLibraryAccess`.
  - Admin blocks the tags too, so a test that needs admin to act on a copy must lift the block for that step.

## Fix round 1 (review of Task 6)

### Writes and blocking
- Policy writes change only `EnableAllFolders` and `EnabledFolders` (`SetLibraryAccess`) or `BlockedTags` (`SetBlockedTags`). Each reads the full policy with `GetUserDto(user).Policy` and writes it back whole with `UpdatePolicyAsync`, so every other field is kept.
- These gateway calls block on async server APIs (`.GetAwaiter().GetResult()`):
  - `CreateLibrary`: `AddVirtualFolder` and `ValidateTopLibraryFolders`.
  - `RemoveLibrary`: `RemoveVirtualFolder` and `ValidateTopLibraryFolders`.
  - `SetLibraryAccess` and `SetBlockedTags`: `UpdatePolicyAsync`, which raises `IUserManager.OnUserUpdated`.
- `QueueScan` only enqueues; it does not wait for the scan.
- `ShareService` holds the shared `PluginGate` (one singleton, a re-entrant monitor) across all of them.
  - An event handler that runs on the same thread may re-enter the gate.
  - A handler that runs on another thread must not be waited on while the gate is held, or it deadlocks.
  - This matters for Task 7, whose user-updated handler will take the gate.

### Paths
- Paths are compared ordinally after `Path.GetFullPath`, whole segments only, with no symlink resolution (`ShareLayout`).
- A share root configured through a symlinked path is compared as written. A library reached through a different spelling of the same folder is not detected as overlapping.
- A share library is only a library whose folders have the exact layout `<share root>/<32 hex>/movies|shows`.
- Any other library with a folder under the share root is a misplaced library:
  - it is not real (`GetRealLibraryIds` excludes it, so it is never granted);
  - it is not a share library: `CreateLibrary` and `RemoveLibrary` refuse it, and `FindLibraryByPath` throws rather than offer it for adoption.
- `Create` refuses with `ShareFolderUnusable` and the fixed setup message before anything else when either of these holds:
  - the share root is inside, or equal to, any library folder;
  - any library folder other than the share layout is inside the share root.
- The detail goes to the server log.
- Proved on the spike: with ShareRoot set to `/media/movies`, the share gets 409, no library is created or removed, every user keeps Movies, and nothing is written to `/media/movies`.

### Plugin configuration API
- `GET`/`POST /Plugins/4069397e-cf0d-460a-9a00-e5175c137516/Configuration` (admin) reads and writes `{ShareRoot, WhoMayShare}`. WhoMayShare is a string enum.
- `reset.py` puts both back to their defaults.
- 0.2 adds `WhoMayShare: "SelectedProfiles"` and `AllowedSharers` (an array of user ids), see "Version 0.2, A" below.
- 0.2 adds `TvShareDays` (an integer, 0 to 366, default 0), see "Version 0.2, B" below.

### Refusal messages
- An item that does not exist and an item the sharer cannot see get one identical body: `{"Refusal":"NotVisibleToSharer","Message":"You can't share that title."}`. Proved for Spike Fifteen as kid1 and for random GUIDs.
- A re-share of a copy names only the copy: "`<copy title>` was shared with you, so you can't pass it on."
- The blocked `swm-<kid1>` tag survives a ReplaceAllMetadata refresh. It comes from the plugin's NFO, which Jellyfin re-reads.

## Task 7: keeping in step (Jellyfin 12.4.0)

Found from the XML docs of the server DLLs and from the server log on the spike. A Serilog override
`{"Serilog":{"MinimumLevel":{"Override":{"Jellyfin.Plugin.SharedWithMe":"Debug"}}}}` in `/config/config/logging.json`
turned on the plugin's Debug lines for one run and was removed afterwards.

### Events and hooks
- **Hosted services work from a plugin.** `serviceCollection.AddHostedService<T>()` in `IPluginServiceRegistrator`. `StartAsync` runs before "Running startup tasks".
- **User created.** `IEventConsumer<Jellyfin.Data.Events.Users.UserCreatedEventArgs>`, registered with `AddScoped`, fires on `POST /Users/New`. `Argument` is the `User` entity.
- **User deleted.** `IEventConsumer<UserDeletedEventArgs>` fires on `DELETE /Users/{id}`. Measured: the full sync it queues had removed kid1's shares, library, folder and tag 1.6 s after the DELETE returned.
- **Policy update: two events, both raised on the writer's thread.**
  - Every `IUserManager.UpdatePolicyAsync` raises `IUserManager.OnUserUpdated` (`EventHandler<GenericEventArgs<User>>`).
  - It also publishes `UserUpdatedEventArgs` to `IEventConsumer`s.
  - Both fire whether the write comes from `POST /Users/{id}/Policy` or from the plugin's own `SetLibraryAccess` / `SetBlockedTags`.
  - Both run synchronously on the thread doing the write. For the plugin that thread holds the `PluginGate`. So handlers only queue work, never wait.
- **Logging in does not raise a user-updated event** (checked against the log).
- **Libraries: there is no virtual-folder event.** `ILibraryManager.ItemAdded` fires for the new `CollectionFolder` when a library is added.
  - It fires for a library added with `refreshLibrary=true`, and for the plugin's own `CreateLibrary`, which uses `refreshLibrary=false` plus `ValidateTopLibraryFolders`.
  - The plugin filters on `e.Item is CollectionFolder` and queues a full sync.
  - A full library scan queues the same sync again through the post-scan task.
  - Measured: a new library reached adult and newbie 1.5 s after the create returned.
- **Post-scan task.** A plugin class implementing `ILibraryPostScanTask` is found and built from DI by the server. It runs as "post-scan task 1/10" after `POST /Library/Refresh`. A per-library `QueueRefresh` (the plugin's `QueueScan`) does NOT run post-scan tasks, so the plugin's own scans cannot loop the sync.
- **Scheduled task.** An `IScheduledTask` class is found the same way and is listed as "Shared With Me: check shares".
  - `TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger | StartupTrigger, IntervalTicks }`.
  - `POST /ScheduledTasks/Running/{id}` runs it. `GET /ScheduledTasks/{id}` → `LastExecutionResult.Status`/`EndTimeUtc` shows when it has finished.
- **Startup hook = the task's `StartupTrigger`.**
  - The server fires it about 3 s after "Core startup complete", once its startup tasks are done. Logged: sync finished 11:03:57.7, startup complete 11:03:54.9.
  - It does not block startup.
  - An admin can remove the trigger in the dashboard. The 6-hour interval, post-scan and event triggers remain.

### Deadlock check
- Every handler only calls `SyncWorker.Request`. That call takes a private lock for microseconds, never the gate.
- The worker waits on a `SemaphoreSlim` asynchronously, then takes the gate on a pool thread.
- Measured on the spike: a gated request (`POST /SharedWithMe/Shares`, refused inside the gate) answered in 0.02 s each time:
  - right after `POST /Users/New`;
  - right after an admin policy edit;
  - right after `DELETE /Users/{id}`.
- The plugin's own policy writes re-raise user-updated. The echo run is a no-op, so it settles: two runs, then quiet (unit test `The_plugins_own_policy_writes_re_trigger_isolation_once_and_then_settle`).

### Items
- **A copy's item `Path` is the symlink path** (`<share root>/<N>/movies/<folder>/<folder>.mp4`), not the link target. So `ILibraryManager.FindByPath(<symlink path>, null)` finds the copy, which is how the gateway's `FindItemByPath` works. The integration test asserts that the record's `MissingChecks` and `FailedChecks` stay 0 after two task runs.
- **Relink.** A renamed file plus a full scan removes the old item and creates a new one with a new id.
  - The NFO's `<uniqueid type="tmdb">` lands in `ProviderIds["Tmdb"]`, which `FindByProviderIds` (an `InternalItemsQuery` with `HasAnyProviderId`) finds.
  - The Spike NFOs have no uniqueid, so `test_sync.py` adds one, and `reset.py` restores the original NFO and file names.
- **Rule 6 rewrite.** `QueueItemRefresh` (FullRefresh, ReplaceAllMetadata) on the copy, plus a per-library scan: kid1 saw the copy restamped `PG` within the test's wait after the task ran.
- **`POST /Items/{id}` with `ProviderIds` sets provider ids on a locked (`lockdata`) item.**

### Live self-healing and the older tests
- Isolation is now re-applied about a second after any policy write. A test that takes a block away or grants admin a share library must therefore act at once and expect the plugin to undo it.
- `test_share_movie.py` was adapted:
  - the Latest leak control reads the leak at once, then asserts the plugin restored the block;
  - the admin grant for the replace-all refresh is re-applied right before each admin call.
  - when kid1's cap is raised so kid1 can share its copy, the test now waits for the plugin to close kid1's own share
    ("no longer needed", rule 4, raised by the sync that kid2's new library queues), then lowers the cap and re-shares.
    This was found by a real failure: the sync ran between the share and the test lowering the cap again.
  - "no shares left" ignores Closed records, which are kept 30 days for the audit page.
- With no store the plugin is not in charge and writes nothing, so `reset.py` deletes the store FIRST, and again at the end.

## Task 7, fix round 1

- **Restore contract.** `LibraryAccessManager.RestoreAll()` removes share libraries, plugin tags and snapshots. It leaves records and share folders alone.
  - Every caller must hold `PluginGate` for the whole restore and remove the share folders FIRST.
  - A folder left behind would be shown again, with its rating-lifting NFO, by the next share library created at the same path.
  - Safety net: when the plugin is not in charge, the sync removes every copy still on disk. Those shares become Closed "sharing was switched off"; a copy it cannot remove is retried.
- **"In charge"** means one of these holds:
  - the store has a snapshot or a library entry;
  - OR the server has a library at a share-layout path. A lost `shares.json` therefore never switches isolation off while share libraries exist.
- **Isolation ignores the setup problem.** It runs even when the share-root check fails, because it only narrows access. A full run with a setup problem does isolation and nothing else, and the scheduled task then ends as Failed with the plain message.
- **Policy writes are read-modify-write within one call (checked).** `SetLibraryAccess` and `SetBlockedTags` read `GetUserById` and `GetUserDto(user).Policy` immediately before `UpdatePolicyAsync`; no policy object is cached across calls.
  - The decision to write is made from a `UserInfo` read a little earlier in the same gated run.
  - An admin edit landing in that window can therefore be overwritten for the fields the plugin writes: `EnableAllFolders`/`EnabledFolders`, or `BlockedTags`. The admin's own non-plugin blocked tags are re-read and kept, except during that window.
- **"Changed?" is decided on sets** (tags case-insensitively), so a server that reorders or re-cases `BlockedTags`/`EnabledFolders` cannot cause endless rewrites.
  - Proved on the spike with the new admin-only `GET /SharedWithMe/Diagnostics` → `{PolicyWrites, SyncRuns, LastSync, LastReport}`.
  - After the admin edit healed (test_sync) and after the first share (test_share_movie), the policy-write counter and every user's BlockedTags/EnabledFolders stayed still for 6 s.
  - Non-admins get 403.
- **Debounces.** Isolation requests wait 50 ms and pull a pending full run forward; full runs wait 1 s.
  - Measured: a new user was isolated before the first 0.5 s poll, and an admin's "all libraries" + unblock edit was undone within 0.5 s.
  - The test controls that take a block away (Latest leak) or grant admin a share library now retry when the heal wins the race to the read.

## Task 8: pages (Jellyfin 12.4.0)

### Plugin pages are for administrators only
- `GET /web/ConfigurationPages` (with or without `enableInMainMenu=true`) answers **403 to a non-admin** (kid1), 401 anonymous, 200 to admin.
- The web client's `configurationpage` route sits under an admin-level guard. Proved in a real browser as adult: `#/configurationpage?name=SharedWithMe` logs `[ConnectionRequired] normal user attempted to access admin route` and shows nothing.
- `GET /web/ConfigurationPage?name=<page>` serves the page body to anyone, including anonymous callers (TMDb's page: 200 anon). So the audit page's HTML/JS is public. "Admin only" is enforced by the endpoints it calls (`/SharedWithMe/Audit*`, `/Diagnostics` → 403), not by the HTML.
- Audit page = `IHasWebPages` page `SharedWithMe` with `EnableInMainMenu = true`. It appears under Dashboard → Plugins in the drawer. Its script is a second page `SharedWithMeAudit.js`, loaded by `data-controller="__plugin/SharedWithMeAudit.js"`. The web client resolves that to `/web/configurationpage?name=SharedWithMeAudit.js` and imports it as an ES module whose default export is `function (view)`. No inline script is needed. No CSP header or meta tag is set on `/web/index.html`.
- `ApiClient.fetch` without `dataType` resolves with the `Response`; on a non-2xx status it **rejects with the Response**. To show refusals verbatim, read the body in the catch.

### Share page for every user: controller-served, standalone
- `GET /SharedWithMe/Page` and `GET /SharedWithMe/Page/share.js` are `[AllowAnonymous]`. They return an HTML shell and its script, served from embedded resources. Every data call needs a token.
- The web client keeps its session in `localStorage["jellyfin_credentials"]` = `{"Servers":[{"Id","AccessToken","UserId","DateLastAccessed","ManualAddress","LocalAddress",...}]}`. It is the same origin as `/SharedWithMe/Page`, so the page can read it. Verified in the browser after a real login.
- The page picks the stored server whose `Id` matches `/System/Info/Public`'s `Id`, else the most recent one. It sends `Authorization: MediaBrowser Token="<token>"` with **no** Client/Device fields. Checked by HTTP that the token-only header authenticates. The server then uses the token's own device, so the web client's device entry is not renamed. (`X-Emby-Token` alone answers 401 on 12.4.)
- If nothing is stored, or `/Users/Me` answers 401, the page says "sign in first" and links to `/web/`.
- There is no menu entry for non-admins. People reach the page by its URL only. A link in the item menu would need script injected into the web client, which is a later milestone in design.md.

### Search
- `InternalItemsQuery(user)` with no `ParentId`/`AncestorIds`/`TopParentIds` is scoped by the server to the user's own views, plus rating cap and blocked tags. Setting any of those ids would skip the library-access part, so none is set.
- Control on the spike: newbie (uncapped) with Movies taken off its explicit list finds nothing for "Spike". On all libraries, newbie finds all three.
- The caller's own copies are dropped with `ExcludeTags = ["swm-<caller N>"]`, and anything under the share root is dropped by path. `SortOrder` is `Jellyfin.Database.Implementations.Enums.SortOrder`; `ItemSortBy` is in `Jellyfin.Data.Enums`.

### Breaking a copy for the audit test
- Deleting the copy's NFO and running a full scan never made `visibleNow` false. It was polled every 0.25 s for 30 s. The server keeps the CustomRating it has already read, and the post-scan sync rewrites the NFO.
- Removing the copy's media symlink and scanning does show it: the item goes, so the audit says `VisibleNow:false, Pending:true`. The sync then rebuilds the link (rule 5) and it is visible again.
- Seen in the browser: straight after a share, "visible now" can read **no** (not pending) for a moment. The server has indexed the item but has not yet applied the NFO's customrating. On reload, about 5 s later, it read yes. That is the honest live answer.

## Task 8, fix round 1

### The share-root guard runs in the configuration save hook
- `BasePlugin<T>.UpdateConfiguration(BasePluginConfiguration)` is virtual in 12.4. The plugin overrides it.
- `POST /Plugins/{id}/Configuration` reaches the override. The dashboard and `ApiClient.updatePluginConfiguration` both call that endpoint.
- Proof on the spike, with a share active:
  - A POST with `ShareRoot: "/config/data/swm-elsewhere"` and `WhoMayShare: "UncappedOnly"` read back `ShareRoot: ""` and `WhoMayShare: "UncappedOnly"`.
  - The server log has the warning, written through `SyncHost`: `Shared With Me: the share folder was not changed because shares exist…`.
- How the guard works:
  - The decision is `Configuration.ShareRootGuard.KeepWhileInCharge`, unit-tested. It compares effective, normalised roots, so an empty setting and `<data>/shared-with-me/` are the same folder.
  - It runs under `PluginGate`, with `ShareSynchronizer.IsInCharge()`.
  - The gate is handed to the plugin by `SyncHost.StartAsync` (`Plugin.InChargeGate`). Before that, a save is treated as in charge, the strictest choice.
- Loading the XML at startup does not go through this hook.

### Share page address, headers and sessions
- `GET /SharedWithMe/Page/` (trailing slash) is routed to the same action. It now answers 302 to `<PathBase>/SharedWithMe/Page`; any other spelling is redirected the same way.
- Asset URLs in the HTML are absolute from `Request.PathBase`, which is accepted only when it matches `^(/[A-Za-z0-9._~-]+)*$`.
- `GET //evil.example/SharedWithMe/Page` answers 404 on 12.4.
- The page and its script carry these headers:
  - `X-Content-Type-Options: nosniff`
  - `X-Frame-Options: DENY`
  - `Cache-Control: no-cache`
  - `Content-Security-Policy: default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'`
- About the CSP:
  - `img-src data:` is only for the empty `data:,` favicon.
  - `'unsafe-inline'` is only for the page's own `<style>` block.
  - In the browser it raised no console violations, and the web client's `theme.css` loads.
- The page uses only the stored session whose `Id` equals this server's `/System/Info/Public` Id. Every request is built as `new URL(location.origin + basePath + path)` and is sent only if its origin is the page's.
- Browser check: with the stored server Id changed to another value, the page showed the signed-out state. Its only requests were the page, `theme.css`, `share.js` and the anonymous `/System/Info/Public`, so no request carried a token.

## Task 9: series (Jellyfin 12.4.0)

Found by sharing `Spike Show (2010)` (GB-15, S01E01-E02, episode NFOs without a rating) with kid1 through the Task 8
plugin, whose series copy is `tvshow.nfo` (`mpaa` GB-15, `customrating` 12, `<tag>swm-<kid1></tag>`, `lockedfields`)
plus `Season 01/<original episode file name>` symlinks and nothing else. Spike media: `tests/integration/make_series.sh`,
library `Shows` on `/media/shows` (TypeOptions Series/Season/Episode with no fetchers), made by `reset.py`.

### Baseline: episodes inherit the series rating
- kid1/kid2 (cap 12, `BlockUnratedItems` Movie+Series) see `Spike Kids` (GB-PG) with its season and its UNRATED
  episode, and see nothing of `Spike Show` (GB-15). An episode with no rating of its own is judged by its series.
- GB seasons are named `Series 1`.

### 2(a) the recipient: `tvshow.nfo` alone is enough (superseded by the fix-round ruling: every level now carries its own protection)
- kid1 sees `Shared with kid1 (shows)` in `/UserViews`; `/Items?parentId=<view>` lists the series (OfficialRating
  GB-15, CustomRating 12); `/Shows/{copy}/Seasons` lists `Series 1`; `/Shows/{copy}/Episodes` lists S01E01-E02;
  `/Items/Latest?parentId=` lists the series; `PlaybackInfo` on the copy's E01 returns 2 media streams.
- The copy's episodes have NO rating, NO customrating and NO tags of their own (`Tags: []`). They are visible to kid1
  because they inherit the series' customrating. No episode or season NFO is needed for visibility. (Fix round 1 made
  every level carry its own NFO anyway; see the end of this section.)
- Copy episodes are named from their file (`Spike Show S01E01`): the original's episode NFO sits beside the original
  file, not beside the link, so it is never read.
- **That was a hole (final review, C1), now closed.** Because the copy never reads the original episode's own metadata,
  an episode the admin rated higher than its series (or tagged) used to reach the recipient with the SERIES' rating and
  the lowered customrating: a sharer capped at 15 could pass on an 18-rated episode it could not see itself. The plugin
  now reads each episode's own rating and tags and builds the copy from them (see "C1: an episode's own rating and tags"
  below).
- A `ReplaceAllMetadata` refresh of the share library reaches the series AND its episodes. Proved with markers: the
  series' tampered name came back from `tvshow.nfo`, and a junk tag set on a copy episode was dropped within 1 s.
  An episode's tampered NAME is kept (nothing provides another one). Afterwards the series keeps GB-15 / 12 / its tag
  and kid1 still lists the season and the episodes and gets PlaybackInfo.
- **But a replace-all strips a copy episode's numbers** (`IndexNumber` became null; `ParentIndexNumber` stayed). A new
  NFO-less episode is numbered from its file name when it is created; a replace-all clears that and, with no NFO and
  no fetcher, nothing puts it back. So the builder writes one NFO per episode link carrying the ORIGINAL item's
  season and episode numbers (and, since fix round 1, its own rating, override, tags and lock).
- **Observed once, cause inferred:** an episode re-made under a series whose `tvshow.nfo` had `<lockdata>true</lockdata>`
  came back with `LockData: true` and never read its own NFO (no numbers, name from the file), even after a replace-all
  refresh of that episode; an NFO-less E03 added the same way also had no numbers. Episodes that existed when the series
  was first scanned were not locked. The inference is that 12.4 locks a new episode under a locked series. The spike's `tvshow.nfo`
  therefore has no lockdata (a test-data limit), and `reset.py` remakes the Shows library if Spike Show's episodes are
  not exactly E01/E02 read from their NFOs.
- **What the server did in the final green run:** under the unlocked series, the original's NFO-less S01E03 was
  numbered (1, 3) by the server itself (from its file name); the gateway logged no "no season or episode number"
  warning, and the copy's E03 NFO carries those numbers.
- **An original episode with no season or episode number is not shared:** the gateway leaves it out and logs a warning
  ("... has no season or episode number, so it is not shared"); numbers are never guessed. The builder also refuses
  any entry with season below 0 or episode below 1 (unit test). The earlier fallback (season from the season item,
  episode 0 = unknown) is gone.

### 2(b) everyone else: the series' tag hides seasons and episodes too, and it is load-bearing
- Probed as kid2 (same cap) and newbie (uncapped), each WITH `swm-<kid1>` blocked, then each endpoint again with the
  block lifted (one probe right after each policy write; the plugin heals ~50 ms later).
- With the block, nothing of the copy at any level: `/Items/Latest?parentId=` the library, the series or the season
  (grouped and `groupItems=false`), `/Items/Filters?parentId=` (library, series, season, `includeItemTypes=Episode`),
  `/Items/Filters2`, `/Years`, `/Genres`, `/Shows/NextUp` (plain, `seriesId=`, `parentId=`), `/UserItems/Resume`,
  search, `/Search/Hints`. 401: `/Items?parentId=` the library, the series or the season. 404: `/Shows/{copy}/Seasons`,
  `/Shows/{copy}/Episodes`, `/Items/{id}` for the series, the season and each episode, `Similar`, `Ancestors`,
  `Intros`, `Images`, `ThemeMedia`.
- Without the block (control) these LEAK, for kid2 and newbie alike:
  - `/Items/Latest?parentId=` the library, the series or the season: the series; with `groupItems=false` the EPISODES.
  - `/Items/Filters?parentId=<library>`: GB-15, 2010 and the tag.
  - **`/Items?parentId=<copy series id>` lists its season and `/Items?parentId=<copy season id>` its episodes.** Library
    access is only checked when the parent is a library; below that only the rating cap and tags apply.
  - **`/Shows/NextUp?seriesId=<copy series id>` returns S01E01 to a user who has never played anything.**
- So the per-recipient tag on the SERIES alone hides the series, its seasons and its episodes from every other user on
  every endpoint found; the server applies a series' tags to its seasons and episodes (inherited), though they carry
  none themselves. (Fix round 1: every season and episode now carries the tag itself too; see below.)

### 2(c) the copy and the original stay apart
- The copy series carries the original's provider id (`tvdb` 990010) in another library. Nothing groups or merges them:
  - kid1 marks the copy's E01 played: kid1's Next Up shows the COPY's E02; the adult's original E01 stays unplayed and
    the adult's Next Up stays empty.
  - The adult marks the original E01 played: the adult's Next Up shows the ORIGINAL's E02; kid1's still the copy's.
  - `/Items/{copy}/Similar` for kid1 is empty; for the adult on the original it lists only `Spike Kids`.
  - `/Items/Latest` (no parent) for kid1 lists the copy's episode, for the adult the original's; the adult's series list
    holds the original only.
- Watch state on the copy is separate from the original, per user, as for movies (documented cost).

### What the plugin does for a series (Task 9)
- `GetSourceMedia(series)` lists the episodes whose file exists on disk now (one deleted before the next scan is not
  linked again), each with its external subtitle streams.
- Sync: when the copy is not exactly what `Build` would make now (an episode added or removed, or the CONTENT of any
  NFO differs: series, season or episode, e.g. a lowered cap), the copy is rebuilt in place (same share id, same folder, through the dot-prefixed temp folder) and a
  scan is queued. Measured: S01E03 reached kid1 after one full scan (post-scan sync + its queued share-library scan);
  a removed S01E01 left kid1's copy after one scan. Copy item ids stay the same (same paths), so watch state survives.
- An original with no episode file left is rule 2 (gone) on the SECOND consecutive run only (`EmptyChecks` on the
  record; the first sighting may be a scan in progress and changes nothing); a replacement must have episode files
  too, so a series never relinks to itself.
- Rule 7 and the audit look up the copy's episode items by their link paths (`FindItemByPath` finds an episode by the
  symlink path, as for a movie). Rule 7 uses the first episode the server has, regular seasons before Season 00; none
  scanned yet counts as "copy not found yet", never as a failed check. The audit stops at the first visible episode.
- Cost per check: `GetSourceMedia(series)` is one item query plus one media-stream read per episode (for its
  subtitles), so it is read once per share per sync check and once per `Create`; the store's library map is read once
  per check and once per audit build.
- Search returns Movie and Series only: `Spike Show One` (an episode's title) finds nothing.
- Sharing a season or an episode id answers 409 `NotShareableType` to someone who sees it, and the fixed
  `NotVisibleToSharer` body to someone who does not (the gateway maps Season and Episode to Kind null).

### Fix round 1: `/Items?ids=`, and protection at every level (Jellyfin 12.4.0)
- **`GET /Items?ids=<copy ids>` checks no library access.** With kid1's tag blocked, kid2 and newbie got nothing for
  `ids=<series,season,episodes>` (with and without `userId`) or `ids=<episode>`; adding
  `recursive=true&includeItemTypes=Episode&parentId=<share library>` answered 401. Control, block lifted: the first two
  return the series, the season and the episodes (an `ids=<episode>` answer also names the series and season ids). So
  the swm- tag is load-bearing here as well. Asserted in both `blind()` helpers (series and movie) with lifted-block
  controls in both tests.
- **Every level now carries its own protection.** The builder writes `tvshow.nfo`, a `season.nfo` per `Season NN`
  folder (`<season>` with `seasonnumber`) and one `<episodedetails>` per episode link, each with `mpaa`,
  `customrating` (omitted when empty), the tags (`swm-<recipient>` plus any CopyTags); season and episode NFOs add
  `<lockedfields>OfficialRating|Tags</lockedfields>`.
  - 12.4 reads `season.nfo`: the copy's season item reports kid1's tag, GB-15 and 12.
  - The episode items report the tag, GB-15, 12 and `LockedFields: [OfficialRating, Tags]`; kid1 still lists and plays
    them (integration test, every level, after every step).
  - `MetadataField` in 12.4 has `Cast, Genres, Name, OfficialRating, Overview, ProductionLocations, Runtime, Studios,
    Tags` only: there is **no CustomRating field to lock**.
  - **A locked `Tags` field freezes the tags the item first read.** Changing the tag in a locked episode NFO and running
    a default refresh or a replace-all left the item's tags unchanged; the same change on a fresh copy without the
    Tags lock reached the items on the next replace-all (control). LockedFields read from an NFO also stay on the item.
    The swm- tag never changes for a recipient, so this only matters for a CopyTags (allowed-tags) change: such a copy
    cannot pick up the new tag at season/episode level and rule 7 ends it Broken (fails closed).
  - **`customrating` still updates under the lock**: 12 -> PG in the NFO reached season and episodes on a replace-all.
    So a changed cap (rule 6) still works; the sync rebuilds the copy (every NFO compared by content) and queues an
    item refresh of the copy series.
  - `tvshow.nfo` and the movie NFO keep `lockedfields` = OfficialRating only, so their tag can still be rewritten.
- **The episode's own tag blocks by itself.** With the series item's tag removed by hand through the admin item API
  (simulating lost inheritance) and kid2's block in place, `Latest?groupItems=false`, `/Items?parentId=<season>`,
  `/Items?ids=<episodes>` and `NextUp?seriesId=` showed no episode; with kid2's block lifted all four leaked the
  episodes (control); with it back, none. The series tag is then put back through the same API.
- Replace-all marker: tampered series and episode NAMES (restored from `tvshow.nfo` and the episode NFO `title`); a
  junk tag no longer works as a marker because episode tags are locked.

## C1: an episode's own rating and tags (Jellyfin 12.4.0, final-review fix wave)

Found on the spike with `Spike Show` S01E04 (episode NFO `<mpaa>GB-18</mpaa>`) and S01E05 (episode NFO `<tag>gore</tag>`)
added by `make_series.sh`; probe output in the fix report's evidence folder.
- **Own vs inherited.** `Episode.OfficialRating` and `Episode.Tags` hold only what the episode's own metadata gave it. As
  admin, `/Shows/{series}/Episodes?fields=OfficialRating,Tags` reads E01/E02 `OfficialRating` absent and `Tags: []`, E04
  `GB-18`, E05 `["gore"]`. With `<tag>nfoinh</tag>` added to `tvshow.nfo` and a replace-all refresh of the series, the series
  reads `["nfoinh"]` while every episode still reads its own tags only (E01 `[]`, E05 `["gore"]`); a user blocking
  `nfoinh` gets 404 on E01. So a series' tags (and rating) reach its episodes at query time, not by being copied.
- **Exception: the item-update API copies.** Setting tags on the SERIES through `POST /Items/{series}` also wrote the
  added tag into each episode's own `Tags` (and removing it there removed it from them), except where `Tags` is locked.
  An episode's "own" tags can therefore include tags an admin set on the series by hand; the plugin then treats them as the
  episode's own, which only makes it stricter (the series itself is refused or closed for such a tag anyway).
- **An episode's own rating wins.** With a user capped at `15` (score 15 / sub 3, `/Localization/ParentalRatings`), the
  series, E01, E02 and E05 answer 200 and E04 answers 404; `/Shows/{series}/Episodes` lists 1, 2, 5. The highest rating the
  spike's server knows is `R18` (score 1000).
- **What the plugin does.** `GetSourceMedia` returns, per episode, its item id, own `OfficialRating` (empty when none) and
  own `Tags`. A series share holds only episodes with a file that the sharer can see now (`CanSee(sharer, episode)`) and
  whose own non-plugin tags the recipient does not block; with the sharer deleted, only episodes without an own rating
  different from the series' and without a blocked own tag. That one set (`ShareEligibility.ShareableEpisodes`) drives
  Create, Build, Matches (so an excluded episode never causes a rebuild loop), the sync's rules 2, 6 and 7, the audit's
  episode check and the playlist repair. Each copy episode's NFO carries the episode's own rating as `mpaa` (else the
  series'), the lowered `customrating`, the tags and the lock. No episode left at Create: refused `NotShareableType`
  ("There are no episodes of <title> you can share yet."); none left at a sync: the existing empty-series rule.
- Proved by `test_share_series.py`: `teen` (cap 15) shares Spike Show with kid1 (who blocks `gore`) and kid1 gets E01-E02
  only, on every episode route and `/Items?ids=`, and only those two links are on disk; adult's share gives kid1 E01, E02
  and E04 (GB-18, override 12) but never E05; adult's share with kid2 gives E04 (GB-18) and E05.

## Task 11: playlists (Jellyfin 12.4.0) - facts found before any code

Found on the spike over REST as the real users (`adult`, `kid1`, `kid2`, `newbie`, `admin`), plus one throwaway build of
the plugin that only logged library events for playlists (reverted, not committed). Decompiling the server was not
allowed, so everything here is observed behaviour. The probe scripts are not part of the repository.

### Storage and identity
- `POST /Playlists {Name, UserId, IsPublic:false}` as the owner gives a private playlist: `GET /Playlists/{id}` →
  `{OpenAccess:false, Shares:[], ItemIds:[]}`. It lives in ONE shared folder, `/config/data/playlists/<name>/playlist.xml`
  (`OwnerUserId`, `<PlaylistItems><PlaylistItem><Path>` per entry, `<Shares/>`, `PlaylistMediaType`, `ContentRating`).
- **Without `MediaType` the playlist becomes `Audio`** even when the first item is a movie. `MediaType: "Video"` gives
  `Video`. A TV client may filter "Add to playlist" by media type, so plugin playlists must be created as `Video`.
- **The folder name comes from the name at creation and never changes**: a rename (`POST /Playlists/{id} {Name}`) keeps
  `Path` and the id. A second playlist with a taken name gets a numeric suffix (`Share with kid11`). Unsafe characters
  become spaces (`a/b:c?*` → `a b c  `).
- **The id is a function of that folder path.** Deleting `a/b:c?*` and creating it again gave the same id
  (`b7f91ed9…` both times). So the id of `Share with kid1` can be worked out by anyone who knows the naming scheme and
  the data path. It also means a playlist that is deleted and re-made under the same name - by ANY user - gets the old
  id back, so a stored id can come to belong to another owner.
- **Entries are keyed by library item id.** `/Playlists/{id}/Items` returns `PlaylistItemId` equal to the item `Id`.
  The same item can be added twice (two entries). `DELETE /Playlists/{id}/Items?entryIds=<item id>` removed BOTH
  entries. (Decision 10's "entry ids distinct from item ids" does not hold on 12.4.)
- **The server only stores items the caller can see.** kid1 adding Spike Eighteen (over its cap) or a random GUID to its
  own playlist answered 204 and stored nothing (`ItemIds` empty, nothing in `playlist.xml`). A non-owner adding to
  someone else's playlist gets 404 "Playlist not found".
- **A Series added to a playlist is stored as its episodes** (adult added Spike Show → `Spike Show One`, `Spike Show
  Two`; ItemIds are the episode ids). An episode is stored as itself. A recipient's own shared COPY can be added by the
  recipient (it is visible to them).
- `POST /Playlists/{id}/Users/{userId}` (add a share) answers **500** (NullReferenceException in
  `PlaylistManager.AddUserToShares`) on 12.4.0. `IsPublic:true` through `POST /Playlists/{id}` works and makes the
  playlist and its items readable by every user (`kid2` got `GET /Playlists/{id}` 200 and its items).

### Isolation does not hide a user's own playlists
- With adult and kid1 on explicit library lists (a live share), both still had the `Playlists` view, listed their own
  playlist with `/Items?includeItemTypes=Playlist&recursive=true`, read `/Playlists/{id}/Items` and added items.

### Change events (throwaway build, server log)
- `ILibraryManager.ItemUpdated` fires for the playlist on every entry add and entry remove (`MetadataEdit`, followed by
  a second update from the refresh), on rename and on `IsPublic` changes, on the request thread.
- `ILibraryManager.ItemAdded` fires on create, `ItemRemoved` on delete. So no 30 s polling is needed.

### Privacy: what other users can reach, given the playlist id
- Not reachable by others (adult's private playlist, probed as kid2, newbie and admin): listing
  (`/Items?includeItemTypes=Playlist`, `/Users/{id}/Items`), search, `/Search/Hints`, `/Items/{id}` (404),
  `/Playlists/{id}` (404), `/Playlists/{id}/Items` (404), `/Playlists/{id}/Users/{uid}`, `/Items?parentId=<id>` (401),
  `InstantMix`, `Similar`, `Ancestors`, `Images` and `Images/Primary` (404, also anonymous), `PlaybackInfo`, `UserData`.
  Admin is NOT special: 404 on all of these too.
- **`GET /Items?ids=<playlist id>` returns the playlist** to newbie and admin: name ("Share with kid1"), `Path`,
  `ChildCount`, `OfficialRating` (it is the highest rating inside, e.g. GB-18), run time. kid1/kid2 got nothing only
  because the playlist read GB-18, over their cap; with only PG titles inside they get it too.
  - **A blocked tag on the playlist closes this one.** With `<Tags><Tag>swm-<adult></Tag></Tags>` in `playlist.xml`
    and a scan, newbie (blocking that tag) got nothing; admin (not blocking it) still got the playlist; the owner kept
    full use. The tag was written by hand into the XML; writing it in-process was not tested.
- **`GET /Items/Latest?parentId=<playlist id>` returns the playlist's CONTENTS to any signed-in user**, filtered only by
  that user's own cap and blocked tags. newbie got exactly the titles in adult's playlist (`Spike Show`, `Spike Kids`,
  `Spike PG`, `Spike Fifteen`; Spike Eighteen, removed earlier, absent) - control: with no parentId or a random
  parentId newbie gets its normal Latest including Spike Eighteen. kid2 got `Spike PG` from kid1's playlist (kid1's copy
  inside, tagged `swm-<kid1>`, stayed hidden). **The blocked tag on the playlist does not stop this**: newbie was
  blocking the playlist's tag. The children are original library items, which the plugin cannot tag.
- `GET /Items/Filters?parentId=<Playlists folder id>` gives every user the ratings of all playlists in the shared folder
  (`OfficialRatings: [GB-PG]` for kid2, who owns none). The folder id is the same for everyone.
- What this means: Jellyfin 12.4 does not keep a private playlist's contents private from someone who has its id, and
  the id follows from the name. A child can never learn an over-cap title this way (Latest applies the prober's own
  cap), but another uncapped profile can learn which titles one user put in "Share with <name>".
- A possible mitigation, NOT implemented: create each plugin playlist under a random 32-hex name (so its folder, and
  therefore its id, cannot be guessed) and rename it to `Share with <name>` straight away; tag it `swm-<owner>` so
  `/Items?ids=` is closed too. The contents would then be protected only by the id staying secret.

## Known Jellyfin 12.4 behaviour: private playlists

Observed on the spike with the Task 11 build (plugin playlists created under a random `swm-<32 hex>` folder and renamed,
Ruling A). This is how 12.4 treats EVERY private playlist on the server, not something the plugin adds.

- **`GET /Search/Hints?searchTerm=<words of the name>` returns every user's private playlists, with name and id, to every
  signed-in user.** kid1, kid2 and newbie all got adult's `Share with kid1` (id `fc1f54f4…`, folder
  `/config/data/playlists/swm-33452c12…`) for `searchTerm=Share with`, also with `includeItemTypes=Playlist` and with
  `mediaTypes=Video`. `GET /Items?searchTerm=Share with&recursive=true` does NOT return them. Searching `swm` finds
  nothing (the folder name is not searched). So the random folder makes the id underivable, but Search/Hints hands it out.
- **A blocked tag on the playlist closes Search/Hints.** With `swm-<adult>` hand-written into that playlist's
  `playlist.xml` (plus a scan), kid1 blocking that tag no longer got it from Search/Hints; kid2 (not blocking it) still did.
- `GET /Items/Latest?parentId=<playlist id>` lists the playlist's CONTENTS to anyone with the id, filtered by the caller's
  own cap and blocked tags (see "Task 11: playlists"). A tag on the playlist does not stop this. With Search/Hints giving
  out the id, any user can read which titles another user put in any of their playlists in two calls.
- `GET /Items?ids=<playlist id>` returns the playlist's name, item count and highest rating; a blocked tag closes it.
- Admin is not special on the closed routes (404), and sees playlists through Search/Hints and `/Items?ids=` like
  everyone else.
- Observed once: after the owner made a plugin playlist public and the plugin made it private again, kid2's Search/Hints
  no longer listed it. Not investigated.

**What the plugin does about it (Rulings A and B2):**
- Every plugin playlist is created under a random `swm-<32 hex>` name, so its folder and id cannot be derived. It is
  renamed to `Share with <name>` afterwards.
- Before the playlist exists, every other user blocks the owner's tag. The playlist is then tagged `swm-<owner id>`.
  The tag is written in-process: the item's own `Tags` are saved to the repository, and `IPlaylistManager.SavePlaylistFile`
  writes `<Tags>` into `playlist.xml`.
- With the block in place, Search/Hints, `/Items?ids=`, listing and search show nothing of the playlist to any other
  user, the recipient and admin included (admin blocks the tag too).
- **What remains:** `/Items/Latest?parentId=<playlist id>` still lists the contents to anyone who has the id. The owner
  tag does not change that, but the id can no longer be derived or found.

Verified on the spike by `test_tv_playlists.py` with the Task 11 build (ruling B2):
- **(i) Others see nothing.** kid1 (the recipient), kid2 (a sibling), newbie (another adult) and admin as a user were
  each asked for adult's plugin playlists. Every one got nothing from:
  - listing (`/Items?includeItemTypes=Playlist`, `/Users/{id}/Items`);
  - search, and `/Search/Hints` for "Share with";
  - `/Items?ids=<playlist>`, with and without `userId`.
  - `/Playlists/{id}`, `/Playlists/{id}/Items`, `/Items/{id}`, and Images (plain and Primary) answered 404.
  - Admin blocks the tag too, so admin is covered like everyone else.
  - Control: with kid2's block of `swm-<adult>` lifted, Search/Hints showed the playlist to kid2. The plugin put the
    block back (user-updated event) and the playlist was gone again.
- **(ii) The owner keeps full use.** adult lists its playlists, has the `Playlists` view and `/Items?parentId=<that
  view>`, opens `/Playlists/{id}` and `/Items/{id}`, finds them in Search/Hints, and adds and removes entries; the
  whole sharing flow runs on that. The tag survived the owner's adds, removes, rename and public toggle: no heal writes
  over the 6 s settling window.
- **(iii) The tag stays on the playlist.** With Spike Eighteen in adult's `Share with kid1`:
  - newbie still lists it and opens `/Items/{id}`;
  - the original's own `Tags` stay `[]`;
  - kid2, given cap 18 for the step, saw it natively.
- **(iv) Latest by id, with the block in place.** newbie's `Latest?parentId=<adult's playlist>` returned
  `['Spike Eighteen']`, exactly the contents; a random parentId returned its normal Latest. kid2 got `[]`: Spike
  Eighteen is over its cap. So the block does not change this route, which still needs the id.

## Task 11, fix round 1: dangling playlist entries (Jellyfin 12.4.0)

- **A renamed file leaves a dangling entry in `playlist.xml`, but not on the playlist item.** When S01E02's file was
  renamed and the library scanned, the server made a new episode item and deleted the old one. Two things happened:
  - The playlist's `LinkedChildren` (and so `/Playlists/{id}/Items`) lost the entry at once.
  - `playlist.xml` still held `<Path>/media/shows/Spike Show (2010)/Season 01/Spike Show S01E02.mp4</Path>` until the
    playlist was saved next.
  - So a reader of `LinkedChildren` alone sees the title as "taken out". The first build did exactly that and removed
    the series share; it was observed in the server log at 16:17:05.
- **What the plugin does now:**
  - `GetPlaylistEntries` reports the paths that no longer resolve, from `LinkedChildren` and from `playlist.xml`.
  - Reconcile never removes a share that a dangling entry could still be. That means an entry with no path, one inside
    the title's folder (a series' folder, or a film's file folder), or any dangling entry when the title's folder is
    unknown.
  - When the dangling entry is inside the title's folder, the playlist is repaired. One current entry is added (the
    film, or the series' first episode with a file), then the stale paths in that folder are removed. Adding already
    makes the server save the file from its live entries, which drops the stale path.
  - Proved by `test_tv_playlists.py`: after the rename and a scan, the series share stays, kid1 still has the series, the
    playlist holds S01E01, and `playlist.xml` no longer names the old file. The settling check then saw no writes.
- `LinkedChild.Path` is marked obsolete in 12.4 ("Use ItemId instead"). It is still the only record of where a dangling
  entry pointed, so the gateway reads it with a local warning suppression.

## Task 10: uninstall (Jellyfin 12.4.0)

Found with a throwaway probe build (not committed): `OnUninstalling` wrote a timestamped marker file, called a hook set
by the plugin's hosted service (which took the `PluginGate`, read the users through `IUserManager` and asked the
synchronizer whether it was in charge), then slept 3 s.

- **The dashboard's Uninstall calls `DELETE /Plugins/{id}/{version}`** (the web client's `uninstallPluginByVersion`, in
  `plugins-plugin.*.chunk.js`).
- **`BasePlugin.OnUninstalling()` runs on that request, before it answers.** The DELETE took 3.05 s with the 3 s sleep
  and answered 204 after the marker's "end" line. `DELETE /Plugins/{id}` (no version) does the same (3.08 s, 204).
- **Every service is still running when it is called.** The hook was set (so the hosted service had started), took the
  gate, read 5 users and the in-charge state without error.
- **What 12.4 does around it.**
  - It deletes the plugin folder `plugins/SharedWithMe_<version>` at once.
  - `/Plugins` no longer lists the plugin, and `/System/Info` reports `HasPendingRestart: true`.
  - **The loaded plugin keeps running until the restart**: `/SharedWithMe/Diagnostics` and `POST /SharedWithMe/Audit/Sync`
    still answered 200 after the DELETE. So the teardown closes the `PluginGate` for good. From then on no share, sync,
    isolation, playlist pass or restore changes anything. The hosted service also unsubscribes from server events and
    disposes the sync worker.
  - After the restart the plugin is gone (`/SharedWithMe/*` 404).
  - The plugin's configuration XML (`plugins/configurations/Jellyfin.Plugin.SharedWithMe.xml`) and the store folder
    `plugins/configurations/SharedWithMe/` are left in place by Jellyfin.
- **Not tested:** what 12.4 does if `OnUninstalling` throws. The plugin catches everything, so it never does.

### An update from a plugin repository does not run the uninstall (Jellyfin 12.4.0)
Tested once on the spike with a throwaway local repository: a temporary `python3 -m http.server` on the test host served a
`manifest.json` and a 0.1.0.1 zip, both made by `tools/package.py --source-url ...` from a scratch commit. The server,
the files and the repository entry were all removed afterwards.
- `GET /Packages` listed the plugin from the manifest as written (`guid`, `versions[].checksum`, `sourceUrl`, `targetAbi`),
  so the manifest shape is one 12.4 reads.
- `POST /Packages/Installed/Shared%20With%20Me?assemblyGuid=...&version=0.1.0.1&repositoryUrl=...` answered 204.
  - The server logged `Plugin installed: Shared With Me 0.1.0.1` and **no** "being uninstalled" line.
  - It unpacked the zip into `plugins/Shared With Me_0.1.0.1/`, named after the package rather than our
    `SharedWithMe_<version>`. It marked the old `SharedWithMe_0.1.0.0` `Superseded`; `/Plugins` showed 0.1.0.0 Superseded
    and 0.1.0.1 `Restart`.
- The Active share (Spike Eighteen to kid1) and `shares.json` were untouched. kid1 still saw the copy before and after the
  restart.
- After the restart only 0.1.0.1 was loaded (`Active`), the old folder was gone, and the share was still Active and
  visible.
- `tools/deploy-spike.sh` therefore also removes `Shared With Me_*` folders, so a later manual install does not leave the
  repository-installed version loaded beside it.

What the uninstall does (`AuditService.Uninstall`, one forced run of the same teardown as Restore, inside one gate):
- **Order.** Close the gate. Remove every plugin playlist, mapped or left over, one by one. Remove every copy and every
  folder the plugin made under the share root (marker folders, `.swm-*` build and probe folders), never following a
  link. Remove every share library: those at share-layout paths, then any the store knows. Lift plugin tags. Put snapshot
  users back on all libraries. Delete empty recipient folders, and the share root when it is the default location and
  empty. Rename `shares.json` to `shares.uninstalled-<UTC>.json`.
- **A failed step is logged and the rest go on**, with two exceptions:
  - While any share library is left (one that cannot be removed, one that also reads other folders, or one with no id),
    nobody is put back on all libraries, and the log names the profiles left on their list.
  - A user who still has a share library or a plugin playlist keeps their `swm-` tag blocked by everyone else.
- `test_uninstall.py` proves the clean case on the spike, with an Active movie share and an Active series share made
  through a playlist:
  - the DELETE answered 204 in about 2.4 s, with everything already put back before the restart;
  - after the restart every policy equals its pre-share state apart from `swm-` tags, and an unrelated blocked tag survives;
  - the user-made playlist and the media hashes are unchanged.

### Packaging (Task 10, fix round 1)
- `tools/package.py` stores the zip's two entries uncompressed (`ZIP_STORED`), so the zip's bytes depend only on the DLL,
  `meta.json` and the fixed dates and modes, never on the host's zlib version.
- What was run (fix round 2): `dist/SharedWithMe_0.1.0.0.zip` (MD5 `e92f8a800f94a546e388e340d31c78dc`, stored entries) was
  copied unchanged to the test host and extracted there with `python3 -m zipfile -e` into
  `/opt/jf-share-spike/config/plugins/SharedWithMe_0.1.0.0/`, as a manual install would, after the old plugin folder and
  its configuration were deleted; the server was then restarted and listed the plugin Active. A repository install of a
  stored zip was not tried (the repository test used an earlier, deflated zip).
- Two `tools/build.sh build` runs of the same source give the same DLL bytes (`Deterministic`, fixed `/src` path in the SDK
  container), so the manifest checksum holds for a rebuild of the committed tree.

## Final-review fix wave (Jellyfin 12.4.0)

- **I1, lists narrowed by hand.** Each snapshot now also records the real libraries the plugin gave the user
  (`PolicySnapshot.GivenLibraries`). A user whose explicit list no longer holds every one of those that still exists is
  left on it by Restore, the uninstall and new-library grants; the audit (`NarrowedUsers`), the Restore answer
  (`LeftOnOwnList`) and the uninstall summary name them. Snapshots written before this compare with every real library.
  Proved by `test_pages.py`: newbie, narrowed to Movies after isolation, is listed in `NarrowedUsers`, stays on Movies after
  Restore and is named; the other four go back to all libraries.
- **I2, unreadable store.** With `shares.json` corrupt (`{ corrupt by test_sync`), a user created meanwhile was moved off
  all libraries and blocked kid1's tag before the first 0.5 s poll, saw nothing of kid1's library by views,
  `/Items?parentId=`, Latest, Filters, search, hints, `/Items/{id}` or `/Items?ids=`; the audit's `SetupProblem` and the
  last report named the problem; the scheduled task ended `Failed` with it; the file was not written. With the file put
  back, a sync reported no problem and the counters stayed flat (`test_sync.py`). Users moved this way have no snapshot.
- **I3, user edits run a full sync.** A tag added to an original (`POST /Items/{id}` Tags) and then blocked for its
  recipient closed the share 0.5 s after the block, copy gone 0.5 s after it, no scan (`test_sync.py`). After an admin
  edit the SyncRuns counter rose and policy and playlist writes stayed flat over 6 s.
  - **Found on the way:** with full runs this frequent, rule 7 counted a second failed check 0.3 s after the first and
    broke a fresh share before the server had applied its customrating (server log: `"kid1" still cannot see the copy`
    0.3 s after the first failure; `/SharedWithMe/Received` briefly empty in `test_pages.py`). A counted check (failed or
    missing) now waits 30 s after the previous one (`ShareRecord.CountedUtc`, `ShareSynchronizer.MinCheckInterval`).
- **I4, deletes.** Every delete of a copy or a plugin folder renames it first to a dot-prefixed `.swm-del-<guid>` sibling
  (Jellyfin never indexes a dot folder, see Task 6), then deletes it; links are removed, never followed. Sweeps finish
  `.swm-del-*` and `.swm-tmp-*` left-overs.
- **I5, copies no record names.** Any ` [swm-<8 hex>]` folder in a kind root that no record of that recipient and kind
  names is removed before a share library is created or adopted, in every full sync and in Restore. The `test_sync.py`
  run leaves kid2's emptied `movies` folder (from the I3 step) but no copy.
- **I7.** The highest-scored rating the spike knows is `R18` (score 1000, `/Localization/ParentalRatings`); an uncapped
  recipient who blocks unrated items of the kind gets it as the copy's `customrating` for an unrated original. Not run on
  the spike (no unrated original in the test media); unit tests cover movie and series.
- **M1.** `BlockedTag` now answers `"<recipient>'s settings block <title>."` (`test_share_movie.py` asserts it), and a
  share by a different sharer answers `RecipientAlreadySees`.

## Version 0.2, A: chosen sharers

- `WhoMayShare: "SelectedProfiles"` with `AllowedSharers: ["<user id>", ...]`: administrators may always share, any other
  profile only when listed. The list is ignored for the other three values.
- The save tidies the list (no empty ids, no repeats) before it is stored. A JSON `null` is stored as an empty list.
  `GET` echoes ids in Jellyfin's dash-less form (`00f7cd89e2af407eb771b6230a1f0d6a`); compare without dashes and case.
- A settings file without `AllowedSharers` (0.1) loads with an empty list and its own `WhoMayShare` (unit test).
- 0.1 answers a `POST` with `"SelectedProfiles"` with `500 Error processing request.` (the enum value does not exist), so
  `test_selected_sharers.py` fails there at its first save: the control run.
- Every save already queues the playlists pass (`Plugin.ConfigurationSaved`), and `EnsurePlaylists` asks `MayShare` for
  each user, so a list change needs no extra trigger. Proved on the spike by `test_selected_sharers.py`: with
  `AllowedSharers = [kid1]` only kid1 and admin kept their `Share with` playlists (kid2, adult, newbie lost theirs), kid1
  (capped at 15) shared Spike Fifteen with kid2, adult, kid2 and newbie got `409 SharingNotAllowed` and an empty
  `/SharedWithMe/Recipients`, admin shared while unlisted; taking kid1 off removed its playlist and kept its share;
  `Everyone` brought every playlist back.
- The audit page lists the tick boxes from `ApiClient.getUsers()` (enabled, not administrator) and writes exactly the
  ticked ones on every save, whichever value is picked: the list is kept while another value is in force, and an id of a
  profile not shown (disabled, or now an administrator) is dropped at the next save.

## Version 0.2, B: expiring shares

- `POST /SharedWithMe/Shares` takes an optional `ExpiresUtc` (ISO 8601). ASP.NET binds `"...Z"` as UTC, an offset as local
  time (converted back to UTC by the plugin) and a timestamp without zone as unspecified (taken as UTC); a value that is not
  a timestamp is refused by the model binder with `400` before the plugin sees it. Every share record in
  `GET /SharedWithMe/Shares`, `/Received` and `/Audit` carries `ExpiresUtc` (UTC, `...Z`) or `null`.
- A past end time and one more than 366 days ahead each answer `409 {"Refusal": "InvalidExpiry", "Message": ...}` and
  change nothing (no record); 365 days ahead was accepted (`test_expiring_shares.py`).
- The test takes "now" from the server's HTTP `Date` header, not the test machine's clock.
- What was observed on the spike (`test_expiring_shares.py`, adult shares Spike Eighteen with kid1 until 45 s ahead):
  kid1 saw the copy and adult's "Share with kid1" playlist listed the mirrored title before the end time; with no sync
  asked for, the server log showed `a share's time is up, queueing a sync` and the share `Closed (expired)` about 1 s
  later, and the test saw it Closed about 5 s after the end time. Then kid1's copy answered 404, kid1's movie list held no
  copy, the `Shared with kid1` library was gone, the playlist no longer listed the title and `/Received` was empty. Two
  more full runs left it Closed and created nothing. A share with no end time (Spike Fifteen to kid2) was untouched.
- `TvShareDays`: a save of 1000 reads back 366, of -4 reads back 0 (the setting clamps itself, so the XML file is held too).
  With 1, Spike Eighteen added to newbie's "Share with kid2" playlist became a share ending 1 day (within 2 min) after it
  was added; a web share made meanwhile (Spike Show, adult to kid2) had `ExpiresUtc: null`, also after being mirrored into
  adult's playlist.
- Control: the same test against the feature-A build (HEAD before this change) failed at "timed out waiting for the
  expired share to close": that build ignores `ExpiresUtc` and the share stayed Active.
- Browser (Chrome via Playwright): the share page's "Keep it" offers Until I remove it / 1 day / 1 week / Until a date; the
  date field appears only for the last, limited to today..+365 days; an empty date is refused on the page; a date share was
  sent as the end of that day (browser in UTC: `Nov 23, 12:00 AM` for 22 Nov). "You shared" and "Shared with you" show an
  Until column; an expired share stays in "You shared" as "expired" with no Undo. The audit page shows Until per share,
  "Closed: expired" afterwards, "Keep it" under Add title, and the TV days setting, which saved and read back. No page
  errors.
