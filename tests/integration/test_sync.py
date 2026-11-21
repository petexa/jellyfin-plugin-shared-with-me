"""Task 7 acceptance: keeping in step. Run reset.py first; run reset.py again afterwards (this test deletes kid1,
creates a user and a library, and renames a movie on disk).

Every check is an assert; the script exits non-zero on the first failure.
"""
import time
import uuid

from jf import SHARE_ROOT, STORE_HOST, assert_settled, call, login, movies, run_remote

MEDIA = "/media/movies"
OLD_DIR, OLD_BASE = "Spike Eighteen (2002)", "Spike Eighteen (2002)"
NEW_DIR, NEW_BASE = "Spike Eighteen Remastered (2002)", "Spike Eighteen Remastered (2002)"
TMDB = "999001"
EXTRA_LIB_DIR = "/config/data/swm-extra-lib"
NEW_USER = "swmnew"
TASK_NAME = "Shared With Me: check shares"

admin, admin_id = login("admin", "spike-admin")
tok, ids = {}, {}
for n in ("kid1", "kid2", "adult", "newbie"):
    tok[n], ids[n] = login(n, "pw-" + n)
N = {n: uuid.UUID(i).hex for n, i in ids.items()}


def step(msg):
    print("-", msg, flush=True)


def lst(n, **q):
    s, d = call("GET", "/Items", tok[n], userId=ids[n], recursive="true", includeItemTypes="Movie",
                fields="Path,OfficialRating,CustomRating,ParentId,Tags", **q)
    assert s == 200, (n, s, d)
    return d["Items"]


def views(n):
    s, d = call("GET", "/UserViews", tok[n], userId=ids[n])
    assert s == 200, (n, s, d)
    return d["Items"]


def policy(uid):
    s, d = call("GET", f"/Users/{uid}", admin)
    assert s == 200, (s, d)
    return d["Policy"]


def set_policy(uid, **changes):
    p = policy(uid)
    p.update(changes)
    s, d = call("POST", f"/Users/{uid}/Policy", admin, body=p)
    assert s in (200, 204), (s, d)


def share(n, item, recipient):
    return call("POST", "/SharedWithMe/Shares", tok[n], body={"ItemId": item, "RecipientUserId": ids[recipient]})


def shares():
    s, d = call("GET", "/SharedWithMe/Shares", admin)
    assert s == 200, (s, d)
    return d


def libs():
    s, d = call("GET", "/Library/VirtualFolders", admin)
    assert s == 200, (s, d)
    return d


def tag(n):
    return "swm-" + N[n]


def blocked(uid):
    return policy(uid).get("BlockedTags") or []


def folders(uid):
    return [f.replace("-", "") for f in policy(uid).get("EnabledFolders") or []]


def wait(pred, what, secs=60):
    start = time.time()
    while time.time() - start < secs:
        if pred():
            return time.time() - start
        time.sleep(0.5)
    raise AssertionError(f"timed out after {secs} s waiting for " + what)


def blind(n, lib_id, copy_id, view_name):
    """User n learns nothing about another recipient's library or copy through any call a client makes."""
    assert view_name not in [v["Name"] for v in views(n)] and lib_id not in [v["Id"] for v in views(n)], (n, "view")
    s, d = call("GET", "/Items", tok[n], userId=ids[n], parentId=lib_id)
    assert s != 200 or copy_id not in [i["Id"] for i in d["Items"]], (n, "Items parentId", s, d)
    s, d = call("GET", "/Items/Latest", tok[n], userId=ids[n], parentId=lib_id)
    assert s != 200 or d == [], (n, "Latest parentId", s, d)
    s, d = call("GET", "/Items/Filters", tok[n], userId=ids[n], parentId=lib_id)
    assert s != 200 or (not d.get("OfficialRatings") and not d.get("Years") and not d.get("Tags")), (n, "Filters parentId", s, d)
    s, d = call("GET", "/Items", tok[n], userId=ids[n], recursive="true", searchTerm="Spike")
    assert s == 200 and copy_id not in [i["Id"] for i in d["Items"]], (n, "search", s, d)
    s, d = call("GET", "/Search/Hints", tok[n], userId=ids[n], searchTerm="Spike")
    assert s == 200 and copy_id not in [h.get("Id") for h in d["SearchHints"]], (n, "search hints", s, d)
    s, d = call("GET", f"/Items/{copy_id}", tok[n], userId=ids[n])
    assert s == 404, (n, "item", s, d)


def responsive(label):
    """(iii) Deadlock check: a request that takes the plugin's gate, and a plain one, each answer within 5 s."""
    t = time.time()
    s, d = share("adult", PG, "kid2")  # refused inside the gate (kid2 already sees Spike PG)
    took = time.time() - t
    assert s == 409 and d["Refusal"] == "RecipientAlreadySees", (label, s, d)
    assert took < 5, (label, "gated request took", took)
    t = time.time()
    s, _ = call("GET", "/System/Info/Public")
    assert s == 200 and time.time() - t < 5, (label, "System/Info/Public")
    return took


def run_task():
    s, t = call("GET", "/ScheduledTasks", admin)
    assert s == 200, (s, t)
    task = next((x for x in t if x["Name"] == TASK_NAME), None)
    assert task, ("scheduled task missing", [x["Name"] for x in t])
    before = (task.get("LastExecutionResult") or {}).get("EndTimeUtc")
    s, d = call("POST", f"/ScheduledTasks/Running/{task['Id']}", admin)
    assert s in (200, 204), (s, d)

    def finished():
        s, x = call("GET", f"/ScheduledTasks/{task['Id']}", admin)
        r = x.get("LastExecutionResult") or {}
        return x["State"] == "Idle" and r.get("EndTimeUtc") and r.get("EndTimeUtc") != before
    wait(finished, "the check-shares task", 120)
    s, x = call("GET", f"/ScheduledTasks/{task['Id']}", admin)
    assert x["LastExecutionResult"]["Status"] == "Completed", x["LastExecutionResult"]


def scan_all():
    s, d = call("POST", "/Library/Refresh", admin)
    assert s in (200, 204), (s, d)
    time.sleep(3)
    wait(lambda: not any(x["State"] != "Idle" and "Scan" in x["Name"] for x in call("GET", "/ScheduledTasks", admin)[1]),
         "library scan", 180)


def copy_of(n, name):
    return next((m for m in lst(n) if m["Name"] == name and "shared-with-me" in (m.get("Path") or "")), None)


originals = {m["Name"]: m for m in movies(admin, admin_id)}
assert sorted(originals) == ["Spike Eighteen", "Spike Fifteen", "Spike PG"], sorted(originals)
E18, PG = originals["Spike Eighteen"]["Id"], originals["Spike PG"]["Id"]
kid1_movies = f"{SHARE_ROOT}/{N['kid1']}/movies"

step("setup: Spike Eighteen gets a tmdb id (NFO on disk, so a renamed file keeps it, and on the item)")
nfo = (f'<?xml version="1.0"?>\n<movie><title>Spike Eighteen</title><year>2002</year><mpaa>GB-18</mpaa>'
       f'<uniqueid type="tmdb" default="true">{TMDB}</uniqueid><lockdata>true</lockdata></movie>\n')
run_remote(f"cat > '/opt/jf-share-spike/media/movies/{OLD_DIR}/{OLD_BASE}.nfo' <<'NFO'\n{nfo}NFO\n", "swm-t7-nfo.sh")
s, dto = call("GET", f"/Items/{E18}", admin, userId=admin_id)
assert s == 200, (s, dto)
dto["ProviderIds"] = {**(dto.get("ProviderIds") or {}), "Tmdb": TMDB}
s, d = call("POST", f"/Items/{E18}", admin, body=dto)
assert s in (200, 204), (s, d)
s, dto = call("GET", f"/Items/{E18}", admin, userId=admin_id)
assert (dto.get("ProviderIds") or {}).get("Tmdb") == TMDB, dto.get("ProviderIds")

step("adult shares Spike Eighteen with kid1")
s, rec = share("adult", E18, "kid1")
assert s == 200, (s, rec)
assert rec["ProviderIds"].get("Tmdb") == TMDB, rec["ProviderIds"]
wait(lambda: copy_of("kid1", "Spike Eighteen"), "kid1's copy")
CP = copy_of("kid1", "Spike Eighteen")["Id"]
LIB = next(v for v in libs() if v["Name"] == "Shared with kid1")["ItemId"]
responsive("after the share")

step("(i) a brand-new user is isolated within 10 s and is blind to kid1's library")
s, u = call("POST", "/Users/New", admin, body={"Name": NEW_USER, "Password": "pw-" + NEW_USER})
assert s == 200, (s, u)
ids[NEW_USER] = u["Id"]
tok[NEW_USER], _ = login(NEW_USER, "pw-" + NEW_USER)
took = responsive("right after creating a user")
print(f"    gated request answered in {took:.2f} s")
secs = wait(lambda: not policy(ids[NEW_USER])["EnableAllFolders"] and tag("kid1") in blocked(ids[NEW_USER]),
            "the new user to be isolated", 10)
print(f"    isolated after {secs:.1f} s")
assert LIB not in folders(ids[NEW_USER]), folders(ids[NEW_USER])
assert folders(ids[NEW_USER]), "the new user lost every library"
blind(NEW_USER, LIB, CP, "Shared with kid1")
assert any(m["Name"] == "Spike PG" for m in lst(NEW_USER)), "the new user lost the real library"

step("(ii) an admin re-ticks 'all libraries' on adult and deletes its swm- block -> both restored within 10 s")
runs_before = call("GET", "/SharedWithMe/Diagnostics", admin)[1]["SyncRuns"]
adult_folders = folders(ids["adult"])
set_policy(ids["adult"], EnableAllFolders=True, EnabledFolders=[], BlockedTags=[t for t in blocked(ids["adult"]) if t != tag("kid1")])
took = responsive("right after the admin edit")
print(f"    gated request answered in {took:.2f} s")
secs = wait(lambda: (lambda p: not p["EnableAllFolders"] and tag("kid1") in (p.get("BlockedTags") or []))(policy(ids["adult"])),
            "adult to be re-isolated", 10)
print(f"    restored after {secs:.1f} s")
assert sorted(folders(ids["adult"])) == sorted(adult_folders), (folders(ids["adult"]), adult_folders)
blind("adult", LIB, CP, "Shared with kid1")
d = assert_settled(admin, [*ids.values(), admin_id], "after the admin edit healed")
print(f"    settled: no policy write in 6 s (counter {d['PolicyWrites']}, sync runs {d['SyncRuns']})")
# I3: the user-updated event now queues a full run as well; it ran, and the full runs settled with the policies
assert d["SyncRuns"] > runs_before, ("the admin edit queued no full run", runs_before, d)
p = policy(ids["adult"])
assert not p["EnableAllFolders"] and tag("kid1") in p["BlockedTags"], "the plugin's own writes did not settle"
s, d = call("GET", "/SharedWithMe/Diagnostics", tok["adult"])
assert s == 403, ("diagnostics are admin-only", s, d)
responsive("after settling")

step("(I3) a tag added to an original and then blocked for its recipient closes the share within 15 s, no scan needed")
F15 = originals["Spike Fifteen"]["Id"]
s, rec15 = share("adult", F15, "kid2")
assert s == 200, (s, rec15)
wait(lambda: copy_of("kid2", "Spike Fifteen"), "kid2's copy of Spike Fifteen")
s, dto = call("GET", f"/Items/{F15}", admin, userId=admin_id)
dto["Tags"] = ["probe-i3"]
s, d = call("POST", f"/Items/{F15}", admin, body=dto)
assert s in (200, 204), (s, d)
set_policy(ids["kid2"], BlockedTags=blocked(ids["kid2"]) + ["probe-i3"])
t0 = time.time()
secs = wait(lambda: (lambda r: r["State"] == "Closed")(next(x for x in shares() if x["Id"] == rec15["Id"])), "the share to close (rule 3)", 15)
r15 = next(x for x in shares() if x["Id"] == rec15["Id"])
assert r15["StateReason"] == "the recipient's profile blocks a tag on the title" and r15["FolderName"] == "", r15
wait(lambda: copy_of("kid2", "Spike Fifteen") is None, "kid2's copy to go", 15 - (time.time() - t0) + 1)
print(f"    closed {secs:.1f} s after the block, copy gone {time.time() - t0:.1f} s after it")
dto["Tags"] = []
s, d = call("POST", f"/Items/{F15}", admin, body=dto)
assert s in (200, 204), (s, d)
set_policy(ids["kid2"], BlockedTags=[t for t in blocked(ids["kid2"]) if t != "probe-i3"])
d = assert_settled(admin, [*ids.values(), admin_id], "after the blocked-tag closure")
print(f"    settled again (policy writes {d['PolicyWrites']}, sync runs {d['SyncRuns']})")

step("(I2) shares.json unreadable: a user created meanwhile is still isolated within 10 s and blind to kid1's library")
I2_USER = "swmi2"
run_remote(f"cp -p {STORE_HOST}/shares.json /opt/jf-share-spike/swm-i2-shares.bak\n"
           f"printf '{{ corrupt by test_sync' > {STORE_HOST}/shares.json\n", "swm-i2-corrupt.sh")
try:
    s, u = call("POST", "/Users/New", admin, body={"Name": I2_USER, "Password": "pw-" + I2_USER})
    assert s == 200, (s, u)
    ids[I2_USER] = u["Id"]
    tok[I2_USER], _ = login(I2_USER, "pw-" + I2_USER)
    secs = wait(lambda: not policy(ids[I2_USER])["EnableAllFolders"] and tag("kid1") in blocked(ids[I2_USER]),
                "the new user to be isolated with the store unreadable", 10)
    print(f"    isolated after {secs:.1f} s with no readable store")
    assert LIB not in folders(ids[I2_USER]) and folders(ids[I2_USER]), folders(ids[I2_USER])
    blind(I2_USER, LIB, CP, "Shared with kid1")
    s, d = call("GET", "/Items", tok[I2_USER], ids=CP)
    assert s != 200 or not d["Items"], ("Items?ids= gave the new user the copy", s, d)
    s, d = call("GET", "/Items", tok[I2_USER], ids=CP, userId=ids[I2_USER])
    assert s != 200 or not d["Items"], ("Items?ids= gave the new user the copy", s, d)
    # the audit page and the last full run name the problem in plain words; the scheduled task fails with it
    s, a = call("GET", "/SharedWithMe/Audit", admin)
    assert s == 200 and "cannot be read" in (a.get("SetupProblem") or ""), (s, a.get("SetupProblem"))
    wait(lambda: "cannot be read" in ((call("GET", "/SharedWithMe/Diagnostics", admin)[1].get("LastReport") or {}).get("SetupProblem") or ""),
         "a full run to report the unreadable store", 15)
    s, t = call("GET", "/ScheduledTasks", admin)
    task = next(x for x in t if x["Name"] == TASK_NAME)
    before = (task.get("LastExecutionResult") or {}).get("EndTimeUtc")
    assert call("POST", f"/ScheduledTasks/Running/{task['Id']}", admin)[0] in (200, 204)
    wait(lambda: (lambda x: x["State"] == "Idle" and (x.get("LastExecutionResult") or {}).get("EndTimeUtc") not in (None, before))(
        call("GET", f"/ScheduledTasks/{task['Id']}", admin)[1]), "the check-shares task", 60)
    res = call("GET", f"/ScheduledTasks/{task['Id']}", admin)[1]["LastExecutionResult"]
    assert res["Status"] == "Failed" and "cannot be read" in (res.get("ErrorMessage") or ""), res
    assert run_remote(f"cat {STORE_HOST}/shares.json\n", "swm-i2-cat.sh").startswith("{ corrupt"), "the plugin wrote the store"
finally:
    run_remote(f"mv -f /opt/jf-share-spike/swm-i2-shares.bak {STORE_HOST}/shares.json\n", "swm-i2-restore.sh")
step("(I2) the record back: a sync settles as before")
s, rep = call("POST", "/SharedWithMe/Audit/Sync", admin)
assert s == 200 and rep.get("SetupProblem") is None, (s, rep)
r = next(x for x in shares() if x["Id"] == rec["Id"])
assert r["State"] == "Active", r
assert call("GET", "/SharedWithMe/Audit", admin)[1].get("SetupProblem") is None
d = assert_settled(admin, [*ids.values(), admin_id], "after the store came back")
s, d = call("DELETE", f"/Users/{ids[I2_USER]}", admin)
assert s in (200, 204), (s, d)
del ids[I2_USER], tok[I2_USER]

step("relink: rename the original's folder and file on disk, scan -> kid1 still sees and can play the copy")
run_remote(f"cd /opt/jf-share-spike/media/movies\n"
           f"mv '{OLD_DIR}' '{NEW_DIR}'\n"
           f"mv '{NEW_DIR}/{OLD_BASE}.mp4' '{NEW_DIR}/{NEW_BASE}.mp4'\n"
           f"mv '{NEW_DIR}/{OLD_BASE}.nfo' '{NEW_DIR}/{NEW_BASE}.nfo'\n", "swm-t7-rename.sh")
scan_all()

def relinked():
    r = next(x for x in shares() if x["Id"] == rec["Id"])
    return r["OriginalItemId"].replace("-", "") != E18.replace("-", "") and r["State"] == "Active" and r
wait(relinked, "the share to follow the renamed file", 90)
r = next(x for x in shares() if x["Id"] == rec["Id"])
NEW_E18 = r["OriginalItemId"].replace("-", "")
s, orig = call("GET", f"/Items/{NEW_E18}", admin, userId=admin_id)
assert s == 200 and orig["Path"] == f"{MEDIA}/{NEW_DIR}/{NEW_BASE}.mp4", (s, orig.get("Path"))
link = run_remote(f"docker exec jf-share-spike readlink '{kid1_movies}/{r['FolderName']}/{r['FolderName']}.mp4'\n", "swm-t7-link.sh").strip()
assert link == f"{MEDIA}/{NEW_DIR}/{NEW_BASE}.mp4", link
wait(lambda: copy_of("kid1", "Spike Eighteen"), "kid1's relinked copy", 60)
CP = copy_of("kid1", "Spike Eighteen")["Id"]
s, d = call("POST", f"/Items/{CP}/PlaybackInfo", tok["kid1"], body={}, userId=ids["kid1"])
assert s == 200 and d["MediaSources"] and d["MediaSources"][0]["MediaStreams"], (s, d)
assert d["MediaSources"][0]["Path"].endswith(f"{r['FolderName']}.mp4"), d["MediaSources"][0]["Path"]

step("rule 7 finds the copy by its symlink path: after two task runs it is neither missing nor failing")
assert copy_of("kid1", "Spike Eighteen")["Path"] == f"{kid1_movies}/{r['FolderName']}/{r['FolderName']}.mp4"  # the link, not its target
run_task()
run_task()
r = next(x for x in shares() if x["Id"] == rec["Id"])
assert r["State"] == "Active" and r["MissingChecks"] == 0 and r["FailedChecks"] == 0, r

step("(iv) after the relink the copy still carries the swm- tag and kid2 is still blind (Latest, Filters too)")
s, dto = call("GET", f"/Items/{CP}", tok["kid1"], userId=ids["kid1"])
assert s == 200 and tag("kid1") in (dto.get("Tags") or []), (s, dto.get("Tags"))
blind("kid2", LIB, CP, "Shared with kid1")
blind("newbie", LIB, CP, "Shared with kid1")

step("lower kid1's cap to PG -> after the task runs kid1 still sees the copy, now stamped PG")
set_policy(ids["kid1"], MaxParentalRating=8, MaxParentalSubRating=0)
run_task()
wait(lambda: (lambda c: c and c.get("CustomRating") == "PG")(copy_of("kid1", "Spike Eighteen")), "kid1 to see the PG copy", 90)
assert copy_of("kid1", "Spike Eighteen")["OfficialRating"] == "GB-18"
blind("kid2", LIB, CP, "Shared with kid1")

step("raise kid1 to uncapped -> the copy is gone and kid1 sees the original")
set_policy(ids["kid1"], MaxParentalRating=None, MaxParentalSubRating=None, BlockUnratedItems=[])
run_task()
r = next(x for x in shares() if x["Id"] == rec["Id"])
assert r["State"] == "Closed" and r["StateReason"] == "no longer needed" and r["FolderName"] == "", r
wait(lambda: copy_of("kid1", "Spike Eighteen") is None, "the copy to go", 60)
assert any(m["Id"].replace("-", "") == NEW_E18 for m in lst("kid1")), "kid1 does not see the original"
assert not run_remote(f"docker exec jf-share-spike sh -c 'ls -A {kid1_movies} 2>/dev/null || true'\n", "swm-t7-ls.sh").strip()

step("re-share to kid1 (capped again), then delete user kid1 -> shares, folder, library and tag all go")
set_policy(ids["kid1"], MaxParentalRating=12, MaxParentalSubRating=1, BlockUnratedItems=["Movie", "Series"])
s, rec2 = share("adult", NEW_E18, "kid1")
assert s == 200, (s, rec2)
wait(lambda: copy_of("kid1", "Spike Eighteen"), "kid1's new copy")
assert any(v["Name"] == "Shared with kid1" for v in libs())
s, d = call("DELETE", f"/Users/{ids['kid1']}", admin)
assert s in (200, 204), (s, d)
responsive("right after deleting a user")


def kid1_gone():
    # (the Closed Spike Fifteen record of kid2 from the I3 step is kept 30 days for the audit page)
    return (not [x for x in shares() if x["RecipientUserId"].replace("-", "") == N["kid1"] or x["State"] != "Closed"]
            and not any(v["Name"].startswith("Shared with") for v in libs())
            and not any(tag("kid1") in blocked(ids[n]) for n in ("kid2", "adult", "newbie", NEW_USER))
            and tag("kid1") not in blocked(admin_id))
secs = wait(kid1_gone, "the user-deleted event to clean up kid1", 20)
print(f"    cleaned up {secs:.1f} s after the delete (user-deleted event)")
run_task()  # the brief's path: the task run leaves it clean
assert kid1_gone()
# nothing of kid1's is left, and no copy anywhere (kid2's emptied folder from the I3 step may stay)
left = run_remote(f"docker exec jf-share-spike sh -c 'cd {SHARE_ROOT} 2>/dev/null && find . -mindepth 1 | sort || true'\n", "swm-t7-root.sh").split()
assert not [p for p in left if N["kid1"] in p or "[swm-" in p or p.split("/")[-1].startswith(".swm-")], left

step("create a new library -> adult and newbie see it without anyone ticking it")
run_remote(f"docker exec jf-share-spike mkdir -p {EXTRA_LIB_DIR}\n"
           f"docker exec jf-share-spike sh -c \"mkdir -p '{EXTRA_LIB_DIR}/Extra Probe (2005)' && "
           f"cp '{MEDIA}/Spike PG (2003)/Spike PG (2003).mp4' '{EXTRA_LIB_DIR}/Extra Probe (2005)/Extra Probe (2005).mp4'\"\n",
           "swm-t7-extra.sh")
s, d = call("POST", "/Library/VirtualFolders", admin, name="Extra Probe", collectionType="movies", refreshLibrary="true",
            body={"LibraryOptions": {"Enabled": True, "EnableRealtimeMonitor": False, "PathInfos": [{"Path": EXTRA_LIB_DIR}],
                                     "MetadataSavers": [], "TypeOptions": [{"Type": "Movie", "MetadataFetchers": [], "ImageFetchers": []}]}})
assert s in (200, 204), (s, d)
wait(lambda: any(v["Name"] == "Extra Probe" and v.get("ItemId") for v in libs()), "the new library id")
EXTRA = next(v for v in libs() if v["Name"] == "Extra Probe")["ItemId"]
secs = wait(lambda: all(EXTRA in folders(ids[n]) for n in ("adult", "newbie")), "adult and newbie to get the new library", 90)
print(f"    granted after {secs:.1f} s")
for n in ("adult", "newbie"):
    assert not policy(ids[n])["EnableAllFolders"], n
    wait(lambda: any(v["Id"].replace("-", "") == EXTRA for v in views(n)), f"{n} to see the new library", 30)
responsive("at the end")
print("PASS test_sync")
