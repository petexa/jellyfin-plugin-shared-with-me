"""Put the spike server (the container jf-share-spike) into the known test state. Idempotent.

Known state:
  * library "Movies" on /media/movies and library "Shows" on /media/shows (make_series.sh: Spike Show (2010) GB-15
    S01E01, E02, E04 (own rating GB-18) and E05 (own tag gore), Spike Kids (2011) GB-PG S01E01); no library under /shares
    or under the plugin's share root
  * users kid1, kid2 (rating cap 12, sub 1, block unrated Movie+Series), adult, newbie (uncapped)
  * every user, admin included, on "all libraries", no blocked or allowed tags, hidden (Jellyfin's default)
  * no tags on the Spike movies or the Movies library folder
  * /shares and the share root empty, no plugin shares.json (nor a shares.uninstalled-*.json left by test_uninstall.py)
  * no library other than Movies and Shows, no user other than admin and the four above (test_sync.py deletes kid1, adds a
    user and a library), Spike Eighteen back under its own name with its original NFO, no provider ids on the Spikes
  * no playlist of any user (the plugin's "Share with" playlists and any a test made), every user under its own name
    (test_tv_playlists.py renames kid1), TvSharingPlaylists back at its default (on). Reset keeps the setting off while it
    runs, then turns it on and runs one full sync: the plugin makes fresh "Share with" playlists and every user blocks
    the swm- tag of every other playlist owner (ruling B2); then quiet.
  * plugin settings otherwise at their defaults: ShareRoot empty, WhoMayShare Everyone, no AllowedSharers, TvShareDays 0
The plugin's store is removed FIRST: with no store the plugin is not in charge, so its background sync (user events,
post-scan) changes nothing while this script runs. It is removed again at the end in case a run was in flight.
The plugin keeps no share state in memory (ShareStore reads shares.json on every call), so no restart is needed.
Only touches jf-share-spike. Never the live `jellyfin` container.
Exits non-zero if the known state is not reached.
"""
import os
import sys

from jf import assert_settled, SHARE_ROOT, SHARE_ROOT_HOST, SIBLING, SIBLING_HOST, STORE_HOST, call, login, movies, run_remote, scan, series, set_tags, under

ADMIN = ("admin", "spike-admin")
REAL_LIBS = {"Movies": ("/media/movies", "movies", ["Movie"]), "Shows": ("/media/shows", "tvshows", ["Series", "Season", "Episode"])}
USERS = {"kid1": True, "kid2": True, "adult": False, "newbie": False}  # name -> capped


def ok(label, res, good=(200, 204)):
    assert res[0] in good, (label, res)


def plugin_libs(folders):
    return [vf for vf in folders
            if any(under(p, "/shares") or under(p, SHARE_ROOT) or under(p, SIBLING) for p in vf.get("Locations", []))]


EXTRA_HOST = "/opt/jf-share-spike/config/data/swm-extra-lib"
MOVIES_HOST = "/opt/jf-share-spike/media/movies"
E18_NFO = ('<?xml version="1.0"?>\n<movie><title>Spike Eighteen</title><year>2002</year><mpaa>GB-18</mpaa>'
           '<lockdata>true</lockdata></movie>\n')

tok, admin_id = login(*ADMIN)
PLUGIN = "4069397e-cf0d-460a-9a00-e5175c137516"
RENAMED = {"kid1-renamed": "kid1"}  # names a test gives a user -> the user's own name


def set_tv_sharing(on):
    s, cfg = call("GET", f"/Plugins/{PLUGIN}/Configuration", tok)
    assert s == 200, (s, cfg)
    if cfg.get("TvSharingPlaylists", True) != on:
        ok("tv sharing " + str(on), call("POST", f"/Plugins/{PLUGIN}/Configuration", tok, body={**cfg, "TvSharingPlaylists": on}))


def delete_all_playlists():
    """Every user's playlists, deleted as their owner (a private playlist is invisible even to admin)."""
    s, users = call("GET", "/Users", tok)
    left = []
    for u in users:
        if u["Name"] not in USERS and u["Name"] != ADMIN[0] and u["Name"] not in RENAMED:
            continue
        name = RENAMED.get(u["Name"], u["Name"])
        # a second login with the same device would end this script's own admin session
        utok, uid = (tok, admin_id) if name == ADMIN[0] else login(u["Name"], "pw-" + name)
        s, d = call("GET", "/Items", utok, userId=uid, includeItemTypes="Playlist", recursive="true")
        assert s == 200, (s, d)
        for pl in d["Items"]:
            # 404: the plugin removed it meanwhile (the setting was just turned off); the check below still requires none
            ok("delete playlist " + pl["Name"], call("DELETE", f"/Items/{pl['Id']}", utok), good=(200, 204, 404))
        s, d = call("GET", "/Items", utok, userId=uid, includeItemTypes="Playlist", recursive="true")
        left += [(u["Name"], pl["Name"]) for pl in d["Items"]]
    return left


# 0. the plugin's store first, so the plugin is not in charge while the rest runs; no playlists made while reset runs
run_remote(f"rm -f {STORE_HOST}/shares.json {STORE_HOST}/shares.json.tmp\n", "swm-reset-store.sh")
set_tv_sharing(False)

# 0b. users a test renamed get their own names back
s, users = call("GET", "/Users", tok)
for u in users:
    if u["Name"] in RENAMED and not any(x["Name"] == RENAMED[u["Name"]] for x in users):
        s, dto = call("GET", f"/Users/{u['Id']}", tok)
        dto["Name"] = RENAMED[u["Name"]]
        ok("rename back " + u["Name"], call("POST", f"/Users/{u['Id']}", tok, body=dto))
        print("renamed", u["Name"], "back to", RENAMED[u["Name"]])
print("deleted playlists; left:", delete_all_playlists())

# 1. delete every library except Movies and Shows (this is the spike server only)
s, folders = call("GET", "/Library/VirtualFolders", tok)
assert s == 200, (s, folders)
for vf in folders:
    if vf["Name"] not in REAL_LIBS or plugin_libs([vf]):
        ok("delete " + vf["Name"], call("DELETE", "/Library/VirtualFolders", tok, name=vf["Name"], refreshLibrary="true"))
        print("deleted library", vf["Name"])

# 2. empty /shares, the share root and the plugin's store
run_remote("docker exec jf-share-spike sh -c 'find /shares -mindepth 1 -delete'\n"
           f"rm -rf {SHARE_ROOT_HOST} {SIBLING_HOST} {EXTRA_HOST}\n"
           f"rm -f {STORE_HOST}/shares.json {STORE_HOST}/shares.json.tmp {STORE_HOST}/shares.uninstalled-*.json\n"
           # test_sync.py renames Spike Eighteen and gives its NFO a tmdb id
           f"cd {MOVIES_HOST}\n"
           "if [ -d 'Spike Eighteen Remastered (2002)' ]; then\n"
           "  mv 'Spike Eighteen Remastered (2002)' 'Spike Eighteen (2002)'\n"
           "  mv 'Spike Eighteen (2002)/Spike Eighteen Remastered (2002).mp4' 'Spike Eighteen (2002)/Spike Eighteen (2002).mp4'\n"
           "  rm -f 'Spike Eighteen (2002)/Spike Eighteen Remastered (2002).nfo'\n"
           "fi\n"
           f"cat > 'Spike Eighteen (2002)/Spike Eighteen (2002).nfo' <<'NFO'\n{E18_NFO}NFO\n", "swm-reset.sh")

# 3. plugin configuration back to defaults (empty ShareRoot = <data path>/shared-with-me)
s, cfg = call("GET", f"/Plugins/{PLUGIN}/Configuration", tok)
assert s == 200, (s, cfg)
if cfg.get("ShareRoot") or cfg.get("WhoMayShare") != "Everyone" or cfg.get("AllowedSharers") or cfg.get("TvShareDays"):
    ok("plugin config", call("POST", f"/Plugins/{PLUGIN}/Configuration", tok, body={**cfg, "ShareRoot": "", "WhoMayShare": "Everyone", "AllowedSharers": [], "TvShareDays": 0}))
    print("plugin configuration reset")

# 3b. series media (Spike Show, Spike Kids; a stray S01E03 removed, a removed S01E01 made again)
print(run_remote(open(os.path.join(os.path.dirname(__file__), "make_series.sh")).read(), "swm-make-series.sh").strip())

# 3c. Movies and Shows libraries
s, folders = call("GET", "/Library/VirtualFolders", tok)
for name, (path, ctype, types) in REAL_LIBS.items():
    if not any(v["Name"] == name for v in folders):
        opts = {"LibraryOptions": {"Enabled": True, "EnableRealtimeMonitor": False, "PathInfos": [{"Path": path}],
                "MetadataSavers": [], "LocalMetadataReaderOrder": ["Nfo"],
                "TypeOptions": [{"Type": t, "MetadataFetchers": [], "ImageFetchers": []} for t in types]}}
        ok("create " + name, call("POST", "/Library/VirtualFolders", tok, body=opts, name=name, collectionType=ctype, refreshLibrary="false"))
        print("created library", name)

# 4. users and policies (admin included: the plugin isolates every user); users a test added are deleted
s, users = call("GET", "/Users", tok)
for u in users:
    if u["Name"] not in USERS and u["Name"] != ADMIN[0]:
        ok("delete user " + u["Name"], call("DELETE", f"/Users/{u['Id']}", tok))
        print("deleted user", u["Name"])
s, users = call("GET", "/Users", tok)
by_name = {u["Name"]: u["Id"] for u in users}
for name, capped in USERS.items():
    if name not in by_name:
        s, u = call("POST", "/Users/New", tok, body={"Name": name, "Password": "pw-" + name})
        ok("new user " + name, (s, u))
        by_name[name] = u["Id"]
        print("created user", name)
for name, uid in by_name.items():
    capped = USERS.get(name, False)
    s, d = call("GET", f"/Users/{uid}", tok)
    assert s == 200, (s, d)
    p = d["Policy"]
    p["EnableAllFolders"] = True
    p["EnabledFolders"] = []
    p["BlockedTags"] = []
    p["AllowedTags"] = []
    p["IsHidden"] = True
    if name in USERS:
        p["MaxParentalRating"] = 12 if capped else None
        p["MaxParentalSubRating"] = 1 if capped else None
        p["BlockUnratedItems"] = ["Movie", "Series"] if capped else []
    ok("policy " + name, call("POST", f"/Users/{uid}/Policy", tok, body=p))

scan(tok)

# 4b. Spike Show must hold exactly S01E01, E02, E04 and E05 read from their NFOs. An episode item left locked, or stale after a test
# deleted and re-made its file, is cleared the only sure way: the Shows library is made again from the files.
def spike_show_episodes():
    s, d = call("GET", "/Items", tok, userId=by_name[ADMIN[0]], recursive="true", includeItemTypes="Episode",
                parentId=next(v["ItemId"] for v in call("GET", "/Library/VirtualFolders", tok)[1] if v["Name"] == "Shows"),
                fields="Path,LockData,OfficialRating,Tags")
    assert s == 200, (s, d)
    return sorted((e.get("ParentIndexNumber") or 0, e.get("IndexNumber") or 0, e["Name"], e.get("OfficialRating") or "",
                   tuple(e.get("Tags") or [])) for e in d["Items"] if "Spike Show" in e["Path"])


SPIKE_SHOW_EPISODES = [(1, 1, "Spike Show One", "", ()), (1, 2, "Spike Show Two", "", ()),
                       (1, 4, "Spike Show Four", "GB-18", ()), (1, 5, "Spike Show Five", "", ("gore",))]
if spike_show_episodes() != SPIKE_SHOW_EPISODES:
    print("Spike Show episodes wrong, remaking the Shows library:", spike_show_episodes())
    ok("delete Shows", call("DELETE", "/Library/VirtualFolders", tok, name="Shows", refreshLibrary="true"))
    path, ctype, types = REAL_LIBS["Shows"]
    opts = {"LibraryOptions": {"Enabled": True, "EnableRealtimeMonitor": False, "PathInfos": [{"Path": path}],
            "MetadataSavers": [], "LocalMetadataReaderOrder": ["Nfo"],
            "TypeOptions": [{"Type": t, "MetadataFetchers": [], "ImageFetchers": []} for t in types]}}
    ok("create Shows", call("POST", "/Library/VirtualFolders", tok, body=opts, name="Shows", collectionType=ctype, refreshLibrary="false"))
    scan(tok)

# 5. tags off the Spike movies, the Spike series and the Movies/Shows library folders
s, folders = call("GET", "/Library/VirtualFolders", tok)
movies_lib = next(v for v in folders if v["Name"] == "Movies")
shows_lib = next(v for v in folders if v["Name"] == "Shows")
for item in movies(tok, admin_id):
    set_tags(tok, admin_id, item["Id"], [])
    s, dto = call("GET", f"/Items/{item['Id']}", tok, userId=admin_id)
    if dto.get("ProviderIds"):
        dto["ProviderIds"] = {}
        ok("clear provider ids " + item["Name"], call("POST", f"/Items/{item['Id']}", tok, body=dto))
set_tags(tok, admin_id, movies_lib["ItemId"], [])
set_tags(tok, admin_id, shows_lib["ItemId"], [])
for item in series(tok, admin_id):
    set_tags(tok, admin_id, item["Id"], [])

# 5b. a sync that was already running when the store went could have written it back
run_remote(f"rm -rf {SHARE_ROOT_HOST}\nrm -f {STORE_HOST}/shares.json {STORE_HOST}/shares.json.tmp\n", "swm-reset-store.sh")

# 5c. no playlist left (made while the setting was still on, or by a test); then the setting back to its default
problems = []
left = delete_all_playlists()
if left:
    problems.append(f"playlists left: {left}")
run_remote(f"rm -f {STORE_HOST}/shares.json {STORE_HOST}/shares.json.tmp\n", "swm-reset-store.sh")

# 6. assert the known state
s, folders = call("GET", "/Library/VirtualFolders", tok)
if plugin_libs(folders):
    problems.append(f"plugin libraries remain: {[v['Name'] for v in plugin_libs(folders)]}")
for name in REAL_LIBS:
    if not any(v["Name"] == name and v.get("ItemId") for v in folders):
        problems.append(f"no {name} library")
if sorted(v["Name"] for v in folders) != sorted(REAL_LIBS):
    problems.append(f"extra libraries: {[v['Name'] for v in folders]}")
s, users = call("GET", "/Users", tok)
if sorted(u["Name"] for u in users) != sorted([ADMIN[0], *USERS]):
    problems.append(f"users: {sorted(u['Name'] for u in users)}")
for name, uid in by_name.items():
    s, d = call("GET", f"/Users/{uid}", tok)
    p = d["Policy"]
    if not p["EnableAllFolders"] or p.get("BlockedTags") or p.get("AllowedTags") or not p["IsHidden"]:
        problems.append(f"{name} policy not reset: {p['EnableAllFolders']} {p.get('BlockedTags')} {p.get('AllowedTags')}")
names = sorted(m["Name"] for m in movies(tok, admin_id))
if names != ["Spike Eighteen", "Spike Fifteen", "Spike PG"]:
    problems.append(f"admin movies: {names}")
for item in movies(tok, admin_id):
    s, dto = call("GET", f"/Items/{item['Id']}", tok, userId=admin_id)
    if dto.get("Tags"):
        problems.append(f"{item['Name']} still tagged {dto['Tags']}")
    if dto.get("ProviderIds"):
        problems.append(f"{item['Name']} still has provider ids {dto['ProviderIds']}")
    if item["Name"] == "Spike Eighteen" and item.get("Path") != "/media/movies/Spike Eighteen (2002)/Spike Eighteen (2002).mp4":
        problems.append(f"Spike Eighteen is at {item.get('Path')}")
shows = {i["Name"]: i for i in series(tok, admin_id)}
if sorted(shows) != ["Spike Kids", "Spike Show"]:
    problems.append(f"admin series: {sorted(shows)}")
else:
    if spike_show_episodes() != SPIKE_SHOW_EPISODES:
        problems.append(f"Spike Show episodes (number, name, own rating, own tags): {spike_show_episodes()}")
    for name, rating, want in (("Spike Show", "GB-15", [(1, 1), (1, 2), (1, 4), (1, 5)]), ("Spike Kids", "GB-PG", [(1, 1)])):
        if shows[name].get("OfficialRating") != rating:
            problems.append(f"{name} rated {shows[name].get('OfficialRating')}")
        s, eps = call("GET", f"/Shows/{shows[name]['Id']}/Episodes", tok, userId=admin_id)
        got = sorted((e.get("ParentIndexNumber"), e.get("IndexNumber")) for e in eps.get("Items", [])) if s == 200 else (s, eps)
        if got != want:
            problems.append(f"{name} episodes: {got}")
        s, dto = call("GET", f"/Items/{shows[name]['Id']}", tok, userId=admin_id)
        if dto.get("Tags"):
            problems.append(f"{name} still tagged {dto['Tags']}")
run_remote(f"if [ -e {SHARE_ROOT_HOST} ] || [ -e {SIBLING_HOST} ] || [ -e {STORE_HOST}/shares.json ] || [ -n \"$(ls -A /opt/jf-share-spike/shares)\" ]; then\n"
           "  echo 'share state left behind' >&2; exit 1\nfi\n", "swm-reset-check.sh")
# last: the setting back to its default, and one full sync so the plugin's fresh playlists and their owner-tag blocks
# exist before any test starts (deterministic), then quiet
set_tv_sharing(True)
ok("sync after reset", call("POST", "/SharedWithMe/Audit/Sync", tok))
s, users = call("GET", "/Users", tok)
assert_settled(tok, [u["Id"] for u in users], "after reset")
if problems:
    print("RESET FAILED:")
    for p in problems:
        print("   ", p)
    sys.exit(1)
print("reset done: Movies + Shows + users", ", ".join(by_name), "on all libraries; share root, /shares and store empty")
