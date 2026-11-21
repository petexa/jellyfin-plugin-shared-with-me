"""Task 10 acceptance: uninstalling the plugin puts the server back. Run reset.py first, and run this LAST: it removes the
plugin. Afterwards run tools/deploy-spike.sh, then reset.py, to put the spike back in its known state.

With an Active movie share (made on the share page) and an Active series share (made through a "Share with" playlist)
in place, the plugin is uninstalled the way the dashboard does it (DELETE /Plugins/{id}/{version}) and the server is
restarted. Every check is an assert; the script exits non-zero on the first failure.
"""
import re
import time

from jf import SHARE_ROOT, SHARE_ROOT_HOST, STORE_HOST, assert_settled, call, is_plugin_tag, login, movies, run_remote, series, under

PLUGIN = "4069397e-cf0d-460a-9a00-e5175c137516"
UNRELATED = "nightmares"  # an admin's own blocked tag, which must survive
MEDIA_HOST = "/opt/jf-share-spike/media"
admin, admin_id = login("admin", "spike-admin")
tok, ids = {"admin": admin}, {"admin": admin_id}
for n in ("kid1", "kid2", "adult", "newbie"):
    tok[n], ids[n] = login(n, "pw-" + n)
RANDOM_FOLDER = re.compile(r"/swm-[0-9a-f]{32}$")


def step(msg):
    print("-", msg)


def wait(pred, what, secs=60, every=0.5):
    """Polls pred until true; returns the seconds it took."""
    start = time.time()
    end = start + secs
    while time.time() < end:
        if pred():
            return time.time() - start
        time.sleep(every)
    raise AssertionError("timed out waiting for " + what)


def policy(n):
    s, d = call("GET", f"/Users/{ids[n]}", admin)
    assert s == 200, (n, s, d)
    return d["Policy"]


def without_plugin_tags(p):
    q = dict(p)
    q["BlockedTags"] = sorted(t for t in (p.get("BlockedTags") or []) if not is_plugin_tag(t))
    q["EnabledFolders"] = sorted(p.get("EnabledFolders") or [])
    return q


def playlists(n):
    s, d = call("GET", "/Items", tok[n], userId=ids[n], includeItemTypes="Playlist", recursive="true", fields="Path")
    assert s == 200, (n, s, d)
    return d["Items"]


def plugin_playlist(n, recipient):
    return next((p["Id"] for p in playlists(n) if p["Name"] == "Share with " + recipient and RANDOM_FOLDER.search(p.get("Path") or "")), None)


def copies(n, kind):
    s, d = call("GET", "/Items", tok[n], userId=ids[n], recursive="true", includeItemTypes=kind, fields="Path")
    assert s == 200, (n, s, d)
    return {i["Name"]: i["Id"] for i in d["Items"] if under(i.get("Path") or "", SHARE_ROOT)}


def records(state="Active"):
    s, d = call("GET", "/SharedWithMe/Shares", admin)
    assert s == 200, (s, d)
    return [r for r in d if state is None or r["State"] == state]


def media_fingerprint():
    """Every file under the test media: path, size and sha256 (symlinks listed as links, never followed)."""
    return run_remote(f"cd {MEDIA_HOST} && find . -type f -print0 | sort -z | xargs -0 sha256sum; find . -type l | sort\n", "swm-uninstall-hash.sh")


def share_layout_libraries():
    s, folders = call("GET", "/Library/VirtualFolders", admin)
    assert s == 200, (s, folders)
    return [v for v in folders if v["Name"].startswith("Shared with ")
            or any(under(p, SHARE_ROOT) for p in v.get("Locations") or [])]


def wait_up():
    end = time.time() + 120
    while time.time() < end:
        try:
            s, _ = call("GET", "/System/Info/Public")
            if s == 200:
                return
        except Exception:
            pass
        time.sleep(2)
    raise AssertionError("the spike did not come back after the restart")


t_start = time.time()
originals = {m["Name"]: m["Id"] for m in movies(admin, admin_id)}
E18, PG = originals["Spike Eighteen"], originals["Spike PG"]
show = {s["Name"]: s["Id"] for s in series(admin, admin_id)}["Spike Show"]
s, eps = call("GET", f"/Shows/{show}/Episodes", tok["adult"], userId=ids["adult"])
assert s == 200 and eps["Items"], (s, eps)
EP1 = sorted(eps["Items"], key=lambda e: e["IndexNumber"])[0]["Id"]

step("before: an unrelated blocked tag on newbie, then every policy as the admin left it (the plugin's swm- tags aside)")
p = policy("newbie")
p["BlockedTags"] = sorted(set((p.get("BlockedTags") or []) + [UNRELATED]))
s, d = call("POST", f"/Users/{ids['newbie']}/Policy", admin, body=p)
assert s in (200, 204), (s, d)
assert_settled(admin, list(ids.values()), "after the unrelated tag")
before = {n: without_plugin_tags(policy(n)) for n in ids}
assert all(b["EnableAllFolders"] for b in before.values()), before  # reset puts everyone on all libraries
assert not records(None), "run reset.py first: shares exist"
hashes_before = media_fingerprint()

step("a user-made playlist that must survive")
s, d = call("POST", "/Playlists", tok["adult"], body={"Name": "Adult picks", "UserId": ids["adult"], "Ids": [PG], "MediaType": "Video", "IsPublic": False})
assert s in (200, 201), (s, d)
mine = d["Id"]

step("an Active movie share from the share page, and an Active series share through adult's 'Share with kid1' playlist")
s, d = call("POST", "/SharedWithMe/Shares", tok["adult"], body={"ItemId": E18, "RecipientUserId": ids["kid1"]})
assert s == 200, ("share movie", s, d)
pid = plugin_playlist("adult", "kid1")
assert pid, [p["Name"] for p in playlists("adult")]
s, d = call("POST", f"/Playlists/{pid}/Items", tok["adult"], ids=EP1, userId=ids["adult"])
assert s in (200, 204), (s, d)
wait(lambda: any(r["OriginalItemId"].replace("-", "") == show.replace("-", "") and r.get("ViaPlaylist") for r in records()), "the series share")
wait(lambda: "Spike Eighteen" in copies("kid1", "Movie") and "Spike Show" in copies("kid1", "Series"), "kid1 to see both copies")
active = records()
assert len(active) == 2 and {r["Kind"] for r in active} == {"Movie", "Series"}, active
copy_movie = copies("kid1", "Movie")["Spike Eighteen"]
copy_series = copies("kid1", "Series")["Spike Show"]
share_libs = {v["Name"]: v["ItemId"] for v in share_layout_libraries()}
assert share_libs, "no share library while shares are Active"
s, ceps = call("GET", f"/Shows/{copy_series}/Episodes", tok["kid1"], userId=ids["kid1"])
assert s == 200 and ceps["Items"], (s, ceps)
copy_episode = ceps["Items"][0]["Id"]
copy_episodes = [e["Id"] for e in ceps["Items"]]
s, cseasons = call("GET", f"/Shows/{copy_series}/Seasons", tok["kid1"], userId=ids["kid1"])
assert s == 200 and cseasons["Items"], (s, cseasons)
copy_season = cseasons["Items"][0]["Id"]
all_copies = [copy_movie, copy_series, copy_season, *copy_episodes]


def ids_answer(n):
    """What /Items?ids=<every copy id> gives user n, with and without userId."""
    got = []
    for q in ({"userId": ids[n]}, {}):
        s, d = call("GET", "/Items", tok[n], ids=",".join(all_copies), **q)
        got += [i["Id"] for i in d["Items"]] if s == 200 else []
    return got


def streams(n, item):
    s, _ = call("GET", f"/Videos/{item}/stream", tok[n], raw=True, static="true")
    return s == 200


assert set(ids_answer("kid1")) >= {copy_movie, copy_series}, "control: kid1 gets its copies from Items?ids="


def filters(n, parent):
    s, d = call("GET", "/Items/Filters", tok[n], userId=ids[n], parentId=parent)
    return (set(d.get("OfficialRatings") or []), set(d.get("Years") or []), set(d.get("Tags") or [])) if s == 200 else (set(), set(), set())


# control: Filters on each share library reports the copies' own data to the recipient (rating, year, the swm- tag)
filters_before = {lib: filters("kid1", lib) for lib in share_libs.values()}
for lib, (r, y, t) in filters_before.items():
    assert r and y, ("control: Filters on the share library reports the copies' data", lib, filters_before[lib])

# CONTROL for the latch proof below. While a share exists the plugin's policy-updated handler is live and writes:
# (1) re-ticking "all libraries" on newbie (not a recipient) is healed back to an explicit list, and (2) removing a swm-
# blocked tag from newbie is healed by putting it back. Each within 10 s, each moving Diagnostics PolicyWrites. heal_s is
# the slower of the two, so the post-uninstall leg can wait well past the time a live handler needs.
def heal(label, change, healed):
    before = call("GET", "/SharedWithMe/Diagnostics", admin)[1]["PolicyWrites"]
    p = policy("newbie")
    change(p)
    s, d = call("POST", f"/Users/{ids['newbie']}/Policy", admin, body=p)
    assert s in (200, 204), (label, s, d)
    took = wait(lambda: healed(policy("newbie")), "control: the plugin heals " + label, secs=10)
    assert call("GET", "/SharedWithMe/Diagnostics", admin)[1]["PolicyWrites"] > before, ("control: the heal is counted", label)
    assert_settled(admin, list(ids.values()), "after the control: " + label)
    print(f"   control: {label} healed in {took:.2f} s")
    return took


def retick(p):
    p["EnableAllFolders"] = True
    p["EnabledFolders"] = []


swm_on_newbie = sorted(t for t in policy("newbie")["BlockedTags"] if is_plugin_tag(t))
assert swm_on_newbie, "control: newbie blocks a swm- tag while shares exist"


def drop_swm(p):
    p["BlockedTags"] = [t for t in p["BlockedTags"] if t != swm_on_newbie[0]]


heal_s = max(
    heal("re-ticked 'all libraries'", retick, lambda q: not q["EnableAllFolders"]),
    heal("a removed swm- tag", drop_swm, lambda q: swm_on_newbie[0] in q["BlockedTags"]))
assert streams("kid1", copy_movie), "control: kid1 streams its copy by id"
assert not policy("kid2")["EnableAllFolders"], "control: the plugin moved users to an explicit list"
assert any(is_plugin_tag(t) for t in policy("kid2").get("BlockedTags") or []), "control: kid2 blocks a plugin tag"
s, hits = call("GET", "/Search/Hints", tok["kid1"], userId=ids["kid1"], searchTerm="Spike Eighteen")
assert s == 200 and hits["SearchHints"], ("control: kid1 finds the shared film", s, hits)

step("uninstall from the dashboard's call: DELETE /Plugins/{id}/{version}")
s, plugins = call("GET", "/Plugins", admin)
me = next(p for p in plugins if p["Id"].replace("-", "") == PLUGIN.replace("-", ""))
t0 = time.time()
s, d = call("DELETE", f"/Plugins/{me['Id']}/{me['Version']}", admin)
assert s == 204, ("uninstall", s, d)
print(f"   uninstall answered 204 after {time.time() - t0:.1f} s")

step("before the restart: everything is already put back")
assert not share_layout_libraries(), share_layout_libraries()
for n in ids:
    assert policy(n)["EnableAllFolders"], (n, "not back on all libraries before the restart")

step("before the restart: the copies are gone from the server's index (Items?ids= and the static stream)")
for n in ("kid1", "kid2"):
    assert not ids_answer(n), (n, "copies still indexed after the uninstall", ids_answer(n))
    assert not streams(n, copy_movie), (n, "the copy still streams after the uninstall")

step("before the restart: the loaded plugin is latched - no share, no sync, no policy write")
d0 = call("GET", "/SharedWithMe/Diagnostics", admin)
assert d0[0] == 200, ("diagnostics before the restart", d0)
s, d = call("POST", "/SharedWithMe/Shares", tok["adult"], body={"ItemId": E18, "RecipientUserId": ids["kid2"]})
assert s != 200, ("a share after the uninstall", s, d)
call("POST", "/SharedWithMe/Audit/Sync", admin)  # answers an error once the worker is gone; it must change nothing
# POST-UNINSTALL LEG (before the restart, while the loaded plugin still runs and its handler still receives events).
# Two policy-updated events on newbie, a user the plugin managed: an explicit library list that omits one real library,
# and an extra unrelated blocked tag. Proves: for max(10, 3 * heal_s) seconds the plugin makes no write - newbie's policy
# stays exactly as set (no list "repaired", no tag added), no other user's policy changes, and PolicyWrites does not move.
# With the refused share and the no-op sync above, this is the evidence for the latch.
others = {n: policy(n) for n in ids if n != "newbie"}
newbie_before = policy("newbie")
s, folders = call("GET", "/Library/VirtualFolders", admin)
real = sorted(v["ItemId"] for v in folders if v["Name"] in ("Movies", "Shows"))
assert len(real) == 2, folders
p = dict(newbie_before)
p["EnableAllFolders"] = False
p["EnabledFolders"] = real[:1]
p["BlockedTags"] = sorted(set((p.get("BlockedTags") or []) + ["latch-check"]))
s, d = call("POST", f"/Users/{ids['newbie']}/Policy", admin, body=p)
assert s in (200, 204), (s, d)
as_set = policy("newbie")
assert not as_set["EnableAllFolders"] and as_set["EnabledFolders"] == real[:1] and "latch-check" in as_set["BlockedTags"], as_set
quiet = max(10, 3 * heal_s)
time.sleep(quiet)
assert policy("newbie") == as_set, ("the plugin changed newbie's policy after the uninstall", as_set, policy("newbie"))
assert {n: policy(n) for n in others} == others, "another user's policy changed after the uninstall"
d1 = call("GET", "/SharedWithMe/Diagnostics", admin)
if d1[0] == 200:
    assert (d1[1]["PolicyWrites"], d1[1]["SyncRuns"], d1[1]["PlaylistWrites"]) == (d0[1]["PolicyWrites"], d0[1]["SyncRuns"], d0[1]["PlaylistWrites"]), (d0, d1)
print(f"   no plugin write for {quiet:.1f} s after two policy edits on newbie (control heal took {heal_s:.2f} s)")
assert not [r for r in call("GET", "/SharedWithMe/Shares", admin)[1] or []], "a share record exists after the uninstall"
s, d = call("POST", f"/Users/{ids['newbie']}/Policy", admin, body=newbie_before)  # back as before, for the comparison below
assert s in (200, 204), (s, d)

step("restart")
run_remote("docker restart jf-share-spike >/dev/null\n", "swm-uninstall-restart.sh")
time.sleep(5)
wait_up()
admin, admin_id = login("admin", "spike-admin")
tok["admin"] = admin
for n in ("kid1", "kid2", "adult", "newbie"):
    tok[n], ids[n] = login(n, "pw-" + n)

step("the plugin is gone")
s, plugins = call("GET", "/Plugins", admin)
assert s == 200 and not any(p["Name"] == "Shared With Me" for p in plugins), [p["Name"] for p in plugins]
assert call("GET", "/SharedWithMe/Diagnostics", admin)[0] == 404

step("no share library, by path or by name")
assert not share_layout_libraries(), share_layout_libraries()

step("every user back on all libraries, every other policy field as before, no swm- tag, the unrelated tag kept")
for n in ids:
    after = policy(n)
    assert not any(is_plugin_tag(t) for t in after.get("BlockedTags") or []), (n, after.get("BlockedTags"))
    assert without_plugin_tags(after) == before[n], (n, {k: (before[n].get(k), v) for k, v in without_plugin_tags(after).items() if before[n].get(k) != v})
assert UNRELATED in policy("newbie")["BlockedTags"], policy("newbie")["BlockedTags"]

step("no plugin playlist for anyone, as each owner and as admin; the user-made playlist survives")
for n in ids:
    left = [(p["Name"], p.get("Path")) for p in playlists(n) if p["Name"].startswith("Share with ") or RANDOM_FOLDER.search(p.get("Path") or "")]
    assert not left, (n, left)
assert mine in [p["Id"] for p in playlists("adult")], "the user-made playlist is gone"
s, items = call("GET", f"/Playlists/{mine}/Items", tok["adult"], userId=ids["adult"])
assert s == 200 and [i["Id"] for i in items["Items"]] == [PG], (s, items)
s, d = call("GET", "/Search/Hints", admin, userId=admin_id, searchTerm="Share with")
assert s == 200 and not d["SearchHints"], d

step("the share root holds no recipient folder (it is the default location, so it is gone)")
out = run_remote(f"if [ -e {SHARE_ROOT_HOST} ]; then find {SHARE_ROOT_HOST} -mindepth 1 | head; else echo GONE; fi\n", "swm-uninstall-root.sh").strip()
assert out == "GONE", out

step("the originals are untouched")
assert media_fingerprint() == hashes_before, "the test media changed"

step("kid1 can no longer reach the shared film or series by any route the other tests use")
s, d = call("GET", "/Items", tok["kid1"], userId=ids["kid1"], recursive="true", includeItemTypes="Movie,Series")
assert s == 200, (s, d)
names = [i["Name"] for i in d["Items"]]
assert "Spike Eighteen" not in names and "Spike Show" not in names, names
s, v = call("GET", "/UserViews", tok["kid1"], userId=ids["kid1"])
assert s == 200 and not [x["Name"] for x in v["Items"] if x["Name"].startswith("Shared with")], v
for term in ("Spike Eighteen", "Spike Show"):
    s, d = call("GET", "/Search/Hints", tok["kid1"], userId=ids["kid1"], searchTerm=term)
    assert s == 200 and not d["SearchHints"], (term, d)
    s, d = call("GET", "/Items", tok["kid1"], userId=ids["kid1"], recursive="true", searchTerm=term)
    assert s == 200 and not d["Items"], (term, d)
for cid in (copy_movie, copy_series, copy_episode, E18, show):
    s, d = call("GET", f"/Items/{cid}", tok["kid1"], userId=ids["kid1"])
    assert s == 404, ("item", cid, s, d)
    s, d = call("POST", f"/Items/{cid}/PlaybackInfo", tok["kid1"], body={}, userId=ids["kid1"])
    assert s != 200 or not d.get("MediaSources"), ("PlaybackInfo", cid, s, d)
    for q in ({"userId": ids["kid1"]}, {}):
        s, d = call("GET", "/Items", tok["kid1"], ids=cid, **q)
        assert s != 200 or not d["Items"], ("Items?ids=", cid, q, s, d)
s, d = call("GET", f"/Shows/{copy_series}/Episodes", tok["kid1"], userId=ids["kid1"])
assert s != 200 or not d["Items"], ("copy episodes", s, d)
def gone_from(items):
    return not [i["Name"] for i in items if i["Name"] in ("Spike Eighteen", "Spike Show") or i.get("SeriesName") == "Spike Show"]


for lib in share_libs.values():
    # an unknown parentId is ignored by 12.4 (kid1 gets its normal Latest): only the shared titles must be absent
    s, d = call("GET", "/Items/Latest", tok["kid1"], userId=ids["kid1"], parentId=lib, groupItems="false")
    assert s != 200 or gone_from(d), ("Latest parentId", s, d)
s, d = call("GET", "/Items/Latest", tok["kid1"], userId=ids["kid1"], groupItems="false", limit=100)
assert s == 200 and gone_from(d), d
# Filters on a former share library: none of the values recorded before the uninstall (rating, tag) may be reported;
# a recorded year only if kid1's ordinary Filters (no parentId) has it from titles kid1 can see anyway.
s, own = call("GET", "/Items/Filters", tok["kid1"], userId=ids["kid1"])
assert s == 200, (s, own)
own_years = set(own.get("Years") or [])
for lib, (r0, y0, t0) in filters_before.items():
    r1, y1, t1 = filters("kid1", lib)
    assert not (r0 & r1) and not (t0 & t1) and not ((y0 - own_years) & y1), ("Filters parentId still reports the copies", lib, (r0, y0, t0), (r1, y1, t1))
for q in ({}, {"seriesId": copy_series}):
    s, d = call("GET", "/Shows/NextUp", tok["kid1"], userId=ids["kid1"], **q)
    assert s != 200 or gone_from(d["Items"]), ("NextUp", q, s, d)
s, d = call("GET", "/UserItems/Resume", tok["kid1"], userId=ids["kid1"])
assert s != 200 or gone_from(d["Items"]), ("Resume", s, d)
for parent in (copy_series, copy_season):
    s, d = call("GET", "/Items", tok["kid1"], userId=ids["kid1"], parentId=parent)
    assert s != 200 or not d["Items"], ("Items parentId", parent, s, d)
assert not ids_answer("kid1"), ("Items?ids= all copies", ids_answer("kid1"))
assert not streams("kid1", copy_movie), "the copy still streams after the restart"

step("the share records were kept, renamed, for the admin")
out = run_remote(f"cd {STORE_HOST}\nif [ -e shares.json ]; then echo shares.json; fi\n"
                 "for f in shares.uninstalled-*.json; do if [ -e \"$f\" ]; then echo \"$f $(stat -c %Y \"$f\")\"; fi; done\n",
                 "swm-uninstall-store.sh")
lines = [line.split() for line in out.strip().splitlines()]
assert ["shares.json"] not in lines, out
fresh = [name for name, mtime in (x for x in lines if len(x) == 2) if int(mtime) >= int(t_start) - 1]
assert len(fresh) == 1 and re.match(r"^shares\.uninstalled-\d{8}T\d{6}Z\.json$", fresh[0]), out
print("   kept as", fresh[0])

print("PASS: uninstall removed every share, library, playlist and tag, and put every user back")
