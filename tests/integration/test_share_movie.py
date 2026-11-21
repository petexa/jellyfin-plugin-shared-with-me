"""Task 6 acceptance: sharing a movie through the plugin API on the spike server. Run reset.py first.

Every check is an assert; the script exits non-zero on the first failure.
"""
import sys
import time
import uuid

from jf import SHARE_ROOT, SIBLING, assert_settled, expected_swm, is_plugin_tag, owns_plugin_playlist, plugin_tags, tag_owners, call, login, movies, run_remote, scan, set_tags, under

admin, admin_id = login("admin", "spike-admin")
tok, ids = {}, {}
for n in ("kid1", "kid2", "adult", "newbie"):
    tok[n], ids[n] = login(n, "pw-" + n)
N = {n: uuid.UUID(i).hex for n, i in ids.items()}  # "N" format ids, as in folder names


def step(msg):
    print("-", msg)


def lst(n, **q):
    s, d = call("GET", "/Items", tok[n], userId=ids[n], recursive="true", includeItemTypes="Movie",
                fields="Path,OfficialRating,CustomRating,ParentId,DateLastRefreshed", **q)
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


def refused(res, code):
    s, d = res
    assert s == 409, ("expected 409", code, s, d)
    assert d["Refusal"] == code, (code, d)
    assert d["Message"].strip(), d


def tag(n):
    return "swm-" + N[n]


def blocked(uid):
    return policy(uid).get("BlockedTags") or []


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
    s, d = call("POST", f"/Items/{copy_id}/PlaybackInfo", tok[n], body={}, userId=ids[n])
    assert s != 200 or not d.get("MediaSources"), (n, "PlaybackInfo", s, d)
    # /Items?ids= checks no library access (Task 9 fix round 1): only the rating cap and the blocked tag stand in the way
    for q in ({"userId": ids[n]}, {}, {"userId": ids[n], "recursive": "true", "includeItemTypes": "Movie", "parentId": lib_id}):
        s, d = call("GET", "/Items", tok[n], ids=copy_id, **q)
        assert s != 200 or copy_id not in [i["Id"] for i in d["Items"]], (n, "Items?ids=", q, s, d)


def OWNERS():
    """Users whose swm- tag everyone else must block (ruling B2): a share library or a plugin playlist."""
    return tag_owners(admin, {**{ids[n]: tok[n] for n in tok}, admin_id: admin})


def libs():
    s, d = call("GET", "/Library/VirtualFolders", admin)
    assert s == 200, (s, d)
    return d


def wait(pred, what, secs=60):
    for _ in range(secs):
        if pred():
            return
        time.sleep(1)
    raise AssertionError("timed out waiting for " + what)


originals = {m["Name"]: m for m in movies(admin, admin_id)}
assert sorted(originals) == ["Spike Eighteen", "Spike Fifteen", "Spike PG"], sorted(originals)
E18, F15, PG = (originals[k]["Id"] for k in ("Spike Eighteen", "Spike Fifteen", "Spike PG"))
movies_lib = next(v for v in libs() if v["Name"] == "Movies")["ItemId"]
shows_lib = next(v for v in libs() if v["Name"] == "Shows")["ItemId"]
kid1_movies = f"{SHARE_ROOT}/{N['kid1']}/movies"

step("kid2 has a blocked tag of its own that the plugin must never touch")
set_policy(ids["kid2"], BlockedTags=["kid2-own"])

step("orphan library under the share root, made through the normal REST API (no store record)")
# the shape a crash would leave: a share-layout folder (<root>/<32 hex>/movies) with no store record
orphan_dir = f"{SHARE_ROOT}/{uuid.uuid4().hex}/movies"
run_remote(f"docker exec jf-share-spike mkdir -p {orphan_dir}\n", "swm-t6-orphan.sh")
s, d = call("POST", "/Library/VirtualFolders", admin, name="Orphan Probe", collectionType="movies", refreshLibrary="true",
            body={"LibraryOptions": {"Enabled": True, "EnableRealtimeMonitor": False, "PathInfos": [{"Path": orphan_dir}]}})
assert s in (200, 204), (s, d)
wait(lambda: any(v["Name"] == "Orphan Probe" and v.get("ItemId") for v in libs()), "orphan library id")
orphan = next(v for v in libs() if v["Name"] == "Orphan Probe")["ItemId"]
# control: while adult is still on all libraries the orphan IS visible, so "absent later" means something
wait(lambda: any(v["Id"].replace("-", "") == orphan for v in views("adult")), "orphan in adult's views (control)")

step("sibling library on a path that only shares a prefix with the share root (a REAL library)")
run_remote(f"docker exec jf-share-spike mkdir -p {SIBLING}\n", "swm-t6-sibling.sh")
s, d = call("POST", "/Library/VirtualFolders", admin, name="Sibling Probe", collectionType="movies", refreshLibrary="true",
            body={"LibraryOptions": {"Enabled": True, "EnableRealtimeMonitor": False, "PathInfos": [{"Path": SIBLING}]}})
assert s in (200, 204), (s, d)
wait(lambda: any(v["Name"] == "Sibling Probe" and v.get("ItemId") for v in libs()), "sibling library id")
sibling = next(v for v in libs() if v["Name"] == "Sibling Probe")["ItemId"]
all_ids = {**ids, "admin": admin_id}
before_policy = {n: policy(u) for n, u in all_ids.items()}

step("GET /SharedWithMe/Recipients")
s, d = call("GET", "/SharedWithMe/Recipients", tok["adult"])
assert s == 200, (s, d)
names = [r["Name"] for r in d]
# every user here is hidden (Jellyfin's default); hidden only means "not on the login screen", so all are listed
assert sorted(names) == ["admin", "kid1", "kid2", "newbie"], names
assert set(d[0]) == {"Id", "Name"}, d[0]  # nothing about policies leaks to a non-admin

step("adult shares Spike Eighteen with kid1 -> 200")
s, rec = share("adult", E18, "kid1")
assert s == 200, (s, rec)
assert rec["OriginalItemId"].replace("-", "") == E18.replace("-", ""), rec
assert rec["RecipientUserId"].replace("-", "") == N["kid1"] and rec["SharerUserId"].replace("-", "") == N["adult"], rec
assert rec["State"] == "Active" and rec["Kind"] == "Movie" and rec["FolderName"], rec
share_id = rec["Id"]
assert_settled(admin, [*ids.values(), admin_id], "after the first share")

step("isolation kept every other policy field and treated the sibling as a real library")
for n, u in all_ids.items():
    after = policy(u)
    changed = {k for k in set(after) | set(before_policy[n])
               if k not in ("EnableAllFolders", "EnabledFolders", "BlockedTags") and after.get(k) != before_policy[n].get(k)}
    # BlockedTags: the user's own tags exactly as before, in order; the plugin's swm- tags exactly the one rule's set
    # (every other user with a share library or a plugin playlist; kid1 now has a library) - ruling B2
    own = [t for t in after["BlockedTags"] if not is_plugin_tag(t)]
    assert own == [t for t in before_policy[n]["BlockedTags"] if not is_plugin_tag(t)], (n, after["BlockedTags"], before_policy[n]["BlockedTags"])
    assert plugin_tags(after["BlockedTags"]) == expected_swm(OWNERS(), u), (n, after["BlockedTags"], expected_swm(OWNERS(), u))
    assert (tag("kid1") in after["BlockedTags"]) == (n != "kid1"), (n, after["BlockedTags"])
    assert not changed, (n, {k: (before_policy[n].get(k), after.get(k)) for k in changed})
    assert not after["EnableAllFolders"], n
    if n != "kid1":
        assert sorted(f.replace("-", "") for f in after["EnabledFolders"]) == sorted([movies_lib, shows_lib, sibling]), (n, after["EnabledFolders"])
s, d = call("DELETE", "/Library/VirtualFolders", admin, name="Sibling Probe", refreshLibrary="true")
assert s in (200, 204), (s, d)

step("GET /SharedWithMe/Shares: adult sees it, kid2 sees nothing, admin sees all")
s, d = call("GET", "/SharedWithMe/Shares", tok["adult"]); assert s == 200 and [x["Id"] for x in d] == [share_id], (s, d)
s, d = call("GET", "/SharedWithMe/Shares", tok["kid2"]); assert s == 200 and d == [], (s, d)
s, d = call("GET", "/SharedWithMe/Shares", admin); assert s == 200 and [x["Id"] for x in d] == [share_id], (s, d)

step("the plugin's own library scan makes the copy appear for kid1, no manual scan")
wait(lambda: any(m["Name"] == "Spike Eighteen" for m in lst("kid1")), "copy via the plugin's queued scan", 60)
scan(admin)

step("kid1 lists the copy: OfficialRating GB-18, CustomRating 12")
copies = [m for m in lst("kid1") if m["Name"] == "Spike Eighteen"]
assert len(copies) == 1, copies
copy = copies[0]
assert copy["Id"] != E18 and under(copy["Path"], kid1_movies), copy
assert copy.get("OfficialRating") == "GB-18" and copy.get("CustomRating") == "12", copy
CP = copy["Id"]
share_lib = next(v for v in libs() if kid1_movies in [l.rstrip("/") for l in v["Locations"]])
assert share_lib["Name"] == "Shared with kid1", share_lib
LIB = share_lib["ItemId"]

step("share library options: no metadata savers, no realtime monitor, movies, NFO reader on")
opts = share_lib["LibraryOptions"]
assert opts["MetadataSavers"] == [], opts.get("MetadataSavers")
assert opts["EnableRealtimeMonitor"] is False and opts.get("SaveLocalMetadata") is False, opts
assert share_lib["CollectionType"] == "movies", share_lib["CollectionType"]
assert "Nfo" not in (opts.get("DisabledLocalMetadataReaders") or []) and "Nfo" in (opts.get("LocalMetadataReaderOrder") or []), opts
copy_dir = f"{kid1_movies}/{rec['FolderName']}"
FOLDER_CHECK = (f"cd '{copy_dir}'\n"
                "ls -A | sort\n"
                "find . -mindepth 1 -type l | wc -l\n"
                f"sha256sum '{rec['FolderName']}.nfo' | cut -d' ' -f1\n")
folder_before = run_remote(f"docker exec jf-share-spike sh -c \"{FOLDER_CHECK.replace(chr(10), '; ')}\"\n", "swm-t6-folder.sh")
assert folder_before.split("\n")[:3] == [rec["FolderName"] + ".mp4", rec["FolderName"] + ".nfo", "1"], folder_before

step("PlaybackInfo on the copy returns 2 media streams")
s, d = call("POST", f"/Items/{CP}/PlaybackInfo", tok["kid1"], body={}, userId=ids["kid1"])
assert s == 200 and d["MediaSources"], (s, d)
assert len(d["MediaSources"][0]["MediaStreams"]) == 2, d["MediaSources"][0]["MediaStreams"]

step("kid1 gets 404 on the original")
s, d = call("GET", f"/Items/{E18}", tok["kid1"], userId=ids["kid1"]); assert s == 404, (s, d)

step("kid2: 404 on the copy, search Spike finds only Spike PG")
s, d = call("GET", f"/Items/{CP}", tok["kid2"], userId=ids["kid2"]); assert s == 404, (s, d)
found = sorted(m["Name"] for m in lst("kid2", searchTerm="Spike"))
assert found == ["Spike PG"], found

step("adult and newbie: exactly 3 movies, two views (Movies, Shows)")
for n in ("adult", "newbie"):
    assert len(lst(n)) == 3, (n, [m["Name"] for m in lst(n)])
    # the Playlists view is there exactly when n owns a plugin "Share with" playlist (Task 11)
    pl_view = ["Playlists"] if owns_plugin_playlist(tok[n] if n in tok else admin, all_ids[n]) else []
    assert sorted(v["Name"] for v in views(n)) == sorted(["Movies", "Shows"] + pl_view), (n, [v["Name"] for v in views(n)])

step("TV-client calls on the copy work for kid1 and reveal nothing to kid2")
kv = [v for v in views("kid1") if v["Name"] == "Shared with kid1"]
assert len(kv) == 1, [v["Name"] for v in views("kid1")]
VIEW = kv[0]["Id"]
s, d = call("GET", "/Items", tok["kid1"], userId=ids["kid1"], parentId=VIEW)
assert s == 200 and CP in [i["Id"] for i in d["Items"]], (s, d)
s, d = call("GET", "/Items/Latest", tok["kid1"], userId=ids["kid1"], parentId=VIEW)
assert s == 200 and CP in [i["Id"] for i in d], (s, d)
s, d = call("POST", f"/Items/{CP}/PlaybackInfo", tok["kid1"], body={}, userId=ids["kid1"])
assert s == 200 and d["MediaSources"] and d["MediaSources"][0]["MediaStreams"], (s, d)
for n in ("kid2", "adult", "newbie"):
    blind(n, VIEW, CP, "Shared with kid1")
    assert tag("kid1") in blocked(ids[n]), (n, blocked(ids[n]))
assert tag("kid1") in blocked(admin_id), blocked(admin_id)
assert tag("kid1") not in blocked(ids["kid1"]), blocked(ids["kid1"])
assert "kid2-own" in blocked(ids["kid2"]), blocked(ids["kid2"])
# control: without the block, Jellyfin's Latest really does leak the copy, so the green checks above mean something.
# The plugin (Task 7) puts a removed block back by itself about a second later, so the leak is read at once.
kid2_tags = blocked(ids["kid2"])
leaked = []
for _ in range(5):  # the heal (50 ms debounce) can win the race to the read; try again until the leak is seen
    set_policy(ids["kid2"], BlockedTags=[t for t in kid2_tags if t != tag("kid1")])
    s, d = call("GET", "/Items/Latest", tok["kid2"], userId=ids["kid2"], parentId=VIEW)
    assert s == 200, (s, d)
    if CP in [i["Id"] for i in d]:
        leaked.append(True)
        break
    wait(lambda: tag("kid1") in blocked(ids["kid2"]), "the plugin to restore kid2's block", 10)
assert leaked, "control: expected the unblocked leak"
wait(lambda: tag("kid1") in blocked(ids["kid2"]), "the plugin to restore kid2's block", 10)
leaked = []
for _ in range(5):  # the same control for /Items?ids=, which checks no library access either
    set_policy(ids["kid2"], BlockedTags=[t for t in kid2_tags if t != tag("kid1")])
    s, d = call("GET", "/Items", tok["kid2"], ids=CP)
    assert s == 200, (s, d)
    if CP in [i["Id"] for i in d["Items"]]:
        leaked.append(True)
        break
    wait(lambda: tag("kid1") in blocked(ids["kid2"]), "the plugin to restore kid2's block", 10)
assert leaked, "control: expected the unblocked /Items?ids= leak"
wait(lambda: tag("kid1") in blocked(ids["kid2"]), "the plugin to restore kid2's block", 10)
assert sorted(blocked(ids["kid2"])) == sorted(kid2_tags), blocked(ids["kid2"])
s, d = call("GET", "/Items/Latest", tok["kid2"], userId=ids["kid2"], parentId=VIEW)
assert s == 200 and d == [], (s, d)
copy_dto = call("GET", f"/Items/{CP}", tok["kid1"], userId=ids["kid1"])[1]
assert tag("kid1") in copy_dto.get("Tags", []), ("the copy does not carry kid1's tag from its NFO", copy_dto.get("Tags"))

step("orphan library under the share root is granted to nobody")
for n in ("adult", "newbie", "kid2", "kid1"):
    p = policy(ids[n])
    assert not p["EnableAllFolders"], (n, "still on all libraries")
    assert orphan not in [f.replace("-", "") for f in p["EnabledFolders"]], (n, p["EnabledFolders"])
    assert orphan not in [v["Id"].replace("-", "") for v in views(n)], (n, [v["Name"] for v in views(n)])
p = policy(ids["kid1"])
assert LIB in [f.replace("-", "") for f in p["EnabledFolders"]], p["EnabledFolders"]
for n in ("adult", "newbie", "kid2"):
    assert LIB not in [f.replace("-", "") for f in policy(ids[n])["EnabledFolders"]], n
assert LIB not in [f.replace("-", "") for f in policy(admin_id)["EnabledFolders"]], "admin got the share library"

step("kid1 sharing Spike Fifteen -> 409 NotVisibleToSharer, identical to an id that does not exist")
answers = [share("kid1", F15, "kid2"), share("kid1", uuid.uuid4().hex, "kid2"), share("adult", uuid.uuid4().hex, "kid2")]
for a in answers:
    refused(a, "NotVisibleToSharer")
bodies = [{k: v for k, v in a[1].items()} for a in answers]
assert bodies[0] == bodies[1] == bodies[2] == {"Refusal": "NotVisibleToSharer", "Message": "You can't share that title."}, bodies
assert "Fifteen" not in str(answers[0]) and "Spike" not in str(answers[0]), answers[0]

step("sharing Spike PG with kid1 -> 409 RecipientAlreadySees")
refused(share("adult", PG, "kid1"), "RecipientAlreadySees")

step("a sharer with no access to the item's library cannot see it -> 409 NotVisibleToSharer")
set_policy(ids["newbie"], EnableAllFolders=False, EnabledFolders=[])
refused(share("newbie", PG, "kid2"), "NotVisibleToSharer")
set_policy(ids["newbie"], EnableAllFolders=False, EnabledFolders=[movies_lib])
s, d = share("newbie", PG, "kid2")  # control: with access back the same request is refused for a different reason
assert s == 409 and d["Refusal"] == "RecipientAlreadySees", (s, d)

step("tag the 15 'never', kid1 blocks 'never' -> 409 BlockedTag")
set_tags(admin, admin_id, F15, ["never"])
kid1_tags = blocked(ids["kid1"])
set_policy(ids["kid1"], BlockedTags=kid1_tags + ["never"])
res = share("adult", F15, "kid1")
refused(res, "BlockedTag")
assert res[1]["Message"] == "kid1's settings block Spike Fifteen." and "never" not in res[1]["Message"], res  # M1: the tag is not named
step("same tag on the Movies library folder instead (inherited) -> 409 BlockedTag")
set_tags(admin, admin_id, F15, [])
set_tags(admin, admin_id, movies_lib, ["never"])
refused(share("adult", F15, "kid1"), "BlockedTag")
set_tags(admin, admin_id, movies_lib, [])
set_policy(ids["kid1"], BlockedTags=kid1_tags)

step("a leftover .swm-tmp-* folder is not indexed (control: the same folder without the dot is)")
src = originals["Spike PG"]["Path"]
tmp = f"{kid1_movies}/.swm-tmp-{uuid.uuid4().hex}"
ctl = f"{kid1_movies}/Leftover Control (2004)"
run_remote(f"docker exec jf-share-spike mkdir -p '{tmp}' '{ctl}'\n"
           f"docker exec jf-share-spike ln -s '{src}' '{tmp}/Leftover Probe (2004).mp4'\n"
           f"docker exec jf-share-spike ln -s '{src}' '{ctl}/Leftover Control (2004).mp4'\n", "swm-t6-leftover.sh")
set_policy(ids["kid1"], BlockUnratedItems=[])  # an NFO-less copy is unrated; let kid1 see unrated items for this check
scan(admin)
seen = lst("kid1")
assert any("Leftover Control" in (m.get("Path") or "") for m in seen), ("control not indexed", [m["Path"] for m in seen])
assert not any(".swm-tmp-" in (m.get("Path") or "") or "Leftover Probe" in m["Name"] for m in seen), [m["Path"] for m in seen]
assert any(m["Id"] == CP for m in seen), "copy vanished"
run_remote(f"docker exec jf-share-spike rm -rf '{tmp}' '{ctl}'\n", "swm-t6-leftover-rm.sh")
set_policy(ids["kid1"], BlockUnratedItems=["Movie", "Series"])
scan(admin)
assert not any("Leftover" in (m.get("Path") or "") for m in lst("kid1")), "control not removed"

step("full ReplaceAllMetadata refresh of the share library leaves the share visible")
# The refresh endpoint resolves the item as the caller, and the plugin never grants admin a share library,
# (and admin blocks kid1's tag), so grant it to admin for this step and take it away again. The copy's name is tampered first so the
# refresh is observable: only a completed replace-all refresh puts the NFO title back.
# The plugin (Task 7) takes the grant back by itself about a second after each policy write, so it is re-applied
# immediately before each admin call that needs it.
admin_folders = policy(admin_id)["EnabledFolders"]
admin_tags = blocked(admin_id)


def grant_admin():
    set_policy(admin_id, EnabledFolders=admin_folders + [LIB], BlockedTags=[t for t in admin_tags if t != tag("kid1")])


def as_granted_admin(action):
    """Grant, then act at once; the plugin's heal (50 ms debounce) may win the race, so retry a few times."""
    for _ in range(5):
        grant_admin()
        res = action()
        if res[0] in (200, 204):
            return res
        time.sleep(0.5)
    raise AssertionError(("admin action kept losing the race to the heal", res))


def tamper():
    s, dto = call("GET", f"/Items/{CP}", admin, userId=admin_id)
    if s != 200:
        return s, dto
    dto["Name"] = "Tampered Title"
    return call("POST", f"/Items/{CP}", admin, body=dto)


as_granted_admin(tamper)
wait(lambda: any(m["Id"] == CP and m["Name"] == "Tampered Title" for m in lst("kid1")), "tampered name")
as_granted_admin(lambda: call("POST", f"/Items/{LIB}/Refresh", admin, metadataRefreshMode="FullRefresh", imageRefreshMode="FullRefresh",
                              replaceAllMetadata="true", replaceAllImages="true"))
wait(lambda: any(m["Id"] == CP and m["Name"] == "Spike Eighteen" for m in lst("kid1")), "replace-all refresh to restore the NFO title", 120)
set_policy(admin_id, EnabledFolders=admin_folders, BlockedTags=admin_tags)
wait(lambda: sorted(policy(admin_id)["EnabledFolders"]) == sorted(admin_folders)
     and sorted(blocked(admin_id)) == sorted(admin_tags), "admin back to its own libraries and blocks", 10)
time.sleep(2)
copy = next(m for m in lst("kid1") if m["Id"] == CP)
assert copy.get("OfficialRating") == "GB-18" and copy.get("CustomRating") == "12", copy
s, d = call("GET", f"/Items/{CP}", tok["kid2"], userId=ids["kid2"]); assert s == 404, (s, d)
folder_after = run_remote(f"docker exec jf-share-spike sh -c \"{FOLDER_CHECK.replace(chr(10), '; ')}\"\n", "swm-t6-folder.sh")
assert folder_after == folder_before, ("Jellyfin wrote into the share folder", folder_before, folder_after)
step("after the replace-all refresh the copy still carries kid1's tag and everyone else is still blind")
copy_dto = call("GET", f"/Items/{CP}", tok["kid1"], userId=ids["kid1"])[1]
assert tag("kid1") in copy_dto.get("Tags", []), ("tag lost in the refresh", copy_dto.get("Tags"))
for n in ("kid2", "adult", "newbie"):
    blind(n, VIEW, CP, "Shared with kid1")
assert CP in [i["Id"] for i in call("GET", "/Items/Latest", tok["kid1"], userId=ids["kid1"], parentId=VIEW)[1]]
s, d = call("POST", f"/Items/{CP}/PlaybackInfo", tok["kid1"], body={}, userId=ids["kid1"])
assert s == 200 and d["MediaSources"][0]["MediaStreams"], (s, d)

step("adult cannot share kid1's copy: adult cannot see it -> the fixed NotVisibleToSharer answer")
s, d = share("adult", CP, "kid2")
assert s == 409 and d == {"Refusal": "NotVisibleToSharer", "Message": "You can't share that title."}, (s, d)

step("kid1, cap raised so it can see the original, shares its COPY with kid2 -> record and link point at the original")
set_policy(ids["kid1"], MaxParentalRating=18, MaxParentalSubRating=None)
s, rec2 = share("kid1", CP, "kid2")
assert s == 200, (s, rec2)
# Task 7: kid1 now sees the original, so the plugin's next sync (queued when kid2's library appears) closes kid1's own
# share as no longer needed. Wait for that while the cap is still raised, then restore the cap and share again.
wait(lambda: next(x for x in call("GET", "/SharedWithMe/Shares", admin)[1] if x["Id"] == share_id)["State"] == "Closed",
     "kid1's share to close as no longer needed", 30)
closed = next(x for x in call("GET", "/SharedWithMe/Shares", admin)[1] if x["Id"] == share_id)
assert closed["StateReason"] == "no longer needed" and closed["FolderName"] == "", closed
set_policy(ids["kid1"], MaxParentalRating=12, MaxParentalSubRating=1)
s, rec = share("adult", E18, "kid1")
assert s == 200, (s, rec)
share_id = rec["Id"]
wait(lambda: any(m["Name"] == "Spike Eighteen" and m["Id"] != E18 for m in lst("kid1")), "kid1's new copy", 60)
CP = next(m for m in lst("kid1") if m["Name"] == "Spike Eighteen")["Id"]
LIB = next(v for v in libs() if v["Name"] == "Shared with kid1")["ItemId"]
assert rec2["SharerUserId"] == N["kid1"], rec2
assert rec2["OriginalItemId"] == E18, (rec2["OriginalItemId"], E18)
kid2_movies = f"{SHARE_ROOT}/{N['kid2']}/movies"
link = run_remote(f"docker exec jf-share-spike readlink '{kid2_movies}/{rec2['FolderName']}/{rec2['FolderName']}.mp4'\n", "swm-t6-link.sh").strip()
assert link == originals["Spike Eighteen"]["Path"], link
wait(lambda: any(m["Name"] == "Spike Eighteen" for m in lst("kid2")), "kid2's copy", 60)
scan(admin)
CP2 = next(m for m in lst("kid2") if m["Name"] == "Spike Eighteen")["Id"]
assert CP2 not in (CP, E18), CP2
assert next(m for m in lst("kid2") if m["Id"] == CP2).get("CustomRating") == "12"
lib2 = next(v for v in libs() if kid2_movies in [l.rstrip("/") for l in v["Locations"]])
assert lib2["Name"] == "Shared with kid2", lib2["Name"]
LIB2 = lib2["ItemId"]

step("two share libraries, each visible only to its owner; each sibling is blind to the other's")
assert "Shared with kid2" in [v["Name"] for v in views("kid2")] and "Shared with kid1" in [v["Name"] for v in views("kid1")]
blind("kid1", LIB2, CP2, "Shared with kid2")
blind("kid2", LIB, CP, "Shared with kid1")
for n in ("adult", "newbie"):
    blind(n, LIB2, CP2, "Shared with kid2")
    blind(n, LIB, CP, "Shared with kid1")
assert tag("kid2") in blocked(ids["kid1"]) and tag("kid1") in blocked(ids["kid2"])
assert tag("kid2") not in blocked(ids["kid2"]) and tag("kid1") not in blocked(ids["kid1"])
assert "kid2-own" in blocked(ids["kid2"])
s, d = call("POST", f"/Items/{CP2}/PlaybackInfo", tok["kid2"], body={}, userId=ids["kid2"])
assert s == 200 and d["MediaSources"][0]["MediaStreams"], (s, d)

step("only uncapped profiles may share from here: the kids own no playlist, so kid1's tag can fully go later")
PLUGIN_ID = "4069397e-cf0d-460a-9a00-e5175c137516"
s, cfg = call("GET", f"/Plugins/{PLUGIN_ID}/Configuration", admin)
assert s == 200, (s, cfg)
assert call("POST", f"/Plugins/{PLUGIN_ID}/Configuration", admin, body={**cfg, "WhoMayShare": "UncappedOnly"})[0] in (200, 204)
wait(lambda: not owns_plugin_playlist(tok["kid1"], ids["kid1"]) and not owns_plugin_playlist(tok["kid2"], ids["kid2"]), "the kids' playlists to go", 20)

step("DELETE by someone else -> 404; by the sharer -> 204; after scan kid1 no longer lists it")
s, d = call("DELETE", f"/SharedWithMe/Shares/{share_id}", tok["kid2"]); assert s == 404, (s, d)
s, d = call("DELETE", f"/SharedWithMe/Shares/{share_id}", tok["kid1"]); assert s == 404, (s, d)
s, d = call("DELETE", f"/SharedWithMe/Shares/{share_id}", tok["adult"]); assert s == 204, (s, d)
wait(lambda: not any(m["Name"] == "Spike Eighteen" for m in lst("kid1")), "copy gone without a manual scan", 60)
scan(admin)
assert not any(m["Name"] == "Spike Eighteen" for m in lst("kid1")), [m["Name"] for m in lst("kid1")]
s, d = call("GET", f"/Items/{CP}", tok["kid1"], userId=ids["kid1"]); assert s == 404, (s, d)

step("kid1's library is gone while kid2's stays; tags follow the one rule (kid1 keeps its tag only as a playlist owner)")
assert not any(v["ItemId"] == LIB for v in libs()) and any(v["ItemId"] == LIB2 for v in libs())
wait(lambda: all(tag("kid1") not in blocked(u) for u in [*ids.values(), admin_id]), "kid1's tag to go from everyone", 20)
owners = OWNERS()
# written independently of the server: the uncapped sharers own playlists, kid2 has a share library, kid1 has neither
assert owners == {N["adult"], N["newbie"], uuid.UUID(admin_id).hex, N["kid2"]}, owners
for n, u in {**ids, "admin": admin_id}.items():
    assert plugin_tags(blocked(u)) == expected_swm(owners, u), (n, blocked(u), expected_swm(owners, u))
    assert (tag("kid2") in blocked(u)) == (n != "kid2"), (n, blocked(u))
    assert tag("kid1") not in blocked(u), ("the recipient's tag outlived its last share library", n, blocked(u))
s, d = call("DELETE", f"/SharedWithMe/Shares/{rec2['Id']}", admin); assert s == 204, (s, d)  # admin may remove anyone's
wait(lambda: not any(m["Id"] == CP2 for m in lst("kid2")), "kid2's copy gone", 60)

step("with no shares left no share library exists; only playlist owners' tags remain; kid2's own tag survived")
# Closed records stay 30 days for the audit page (Task 7); nothing else may be left.
s, d = call("GET", "/SharedWithMe/Shares", admin); assert s == 200 and [x for x in d if x["State"] != "Closed"] == [], (s, d)
assert not any(v["ItemId"] == LIB2 or kid2_movies in [l.rstrip("/") for l in v["Locations"]] for v in libs())
owners = OWNERS()
for n, u in {**ids, "admin": admin_id}.items():
    assert plugin_tags(blocked(u)) == expected_swm(owners, u), (n, blocked(u), expected_swm(owners, u))
assert [t for t in blocked(ids["kid2"]) if not is_plugin_tag(t)] == ["kid2-own"], blocked(ids["kid2"])
assert "Shared with kid2" not in [v["Name"] for v in views("kid2")]
assert not any(v["ItemId"] == LIB or kid1_movies in [l.rstrip("/") for l in v["Locations"]] for v in libs()), [v["Name"] for v in libs()]
assert "Shared with kid1" not in [v["Name"] for v in views("kid1")], [v["Name"] for v in views("kid1")]
assert LIB not in [f.replace("-", "") for f in policy(ids["kid1"])["EnabledFolders"]]
assert any(v["ItemId"] == orphan for v in libs()), "the plugin removed a library it does not own"

step("ShareRoot cannot be moved onto the Movies folder while the plugin is in charge (the server keeps the old one)")
PLUGIN = "4069397e-cf0d-460a-9a00-e5175c137516"
s, cfg = call("GET", f"/Plugins/{PLUGIN}/Configuration", admin)
assert s == 200, (s, cfg)
s, d = call("POST", f"/Plugins/{PLUGIN}/Configuration", admin, body={**cfg, "ShareRoot": "/media/movies"})
assert s in (200, 204), (s, d)
s, d = call("GET", f"/Plugins/{PLUGIN}/Configuration", admin)
assert d.get("ShareRoot", "") == "", ("the share root moved while in charge", d)

step("a library overlapping the share folder: a share is refused before anything changes")
# Since the guard (Task 8 fix round 1) the overlap can no longer be made by moving ShareRoot while in charge, so it is
# made the other way round: a library inside the share root.
stray = f"{SHARE_ROOT}/stray"
run_remote(f"docker exec jf-share-spike mkdir -p {stray}\n", "swm-t6-stray.sh")
s, d = call("POST", "/Library/VirtualFolders", admin, name="Stray Probe", collectionType="movies", refreshLibrary="false",
            body={"LibraryOptions": {"Enabled": True, "EnableRealtimeMonitor": False, "PathInfos": [{"Path": stray}]}})
assert s in (200, 204), (s, d)
try:
    wait(lambda: any(v["Name"] == "Stray Probe" for v in libs()), "stray library listed")
    time.sleep(2)  # let the full sync its creation queues run (isolation only, with the setup problem)
    lib_names = sorted(v["Name"] for v in libs())
    folders_before = {n: policy(u)["EnabledFolders"] for n, u in ids.items()}
    media_before = run_remote("docker exec jf-share-spike ls -A /media/movies\n", "swm-t6-media.sh")
    s, d = share("adult", E18, "kid1")
    assert s == 409 and d == {"Refusal": "ShareFolderUnusable", "Message":
        "Sharing isn't set up correctly on this server. Ask the person who runs it to check the Shared With Me settings."}, (s, d)
    assert sorted(v["Name"] for v in libs()) == lib_names, "a library was created or removed"
    for n, u in ids.items():
        p = policy(u)
        assert movies_lib in [f.replace("-", "") for f in p["EnabledFolders"]] and p["EnabledFolders"] == folders_before[n], (n, p["EnabledFolders"])
        assert len(lst(n)) >= 1 and any(m["Name"] == "Spike PG" for m in lst(n)), n
    assert run_remote("docker exec jf-share-spike ls -A /media/movies\n", "swm-t6-media.sh") == media_before, "something was written into /media/movies"
finally:
    s, d = call("DELETE", "/Library/VirtualFolders", admin, name="Stray Probe", refreshLibrary="false")
    assert s in (200, 204), (s, d)


step("TV sharing off: no plugin playlists, so no swm- tag anywhere; then back to the defaults")
s, cfg = call("GET", f"/Plugins/{PLUGIN_ID}/Configuration", admin)
assert s == 200, (s, cfg)
assert call("POST", f"/Plugins/{PLUGIN_ID}/Configuration", admin, body={**cfg, "TvSharingPlaylists": False})[0] in (200, 204)
wait(lambda: not OWNERS() and all(not plugin_tags(blocked(u)) for u in [*ids.values(), admin_id]), "every swm- tag to go", 20)
assert [t for t in blocked(ids["kid2"]) if not is_plugin_tag(t)] == ["kid2-own"], blocked(ids["kid2"])
assert call("POST", f"/Plugins/{PLUGIN_ID}/Configuration", admin, body={**cfg, "TvSharingPlaylists": True, "WhoMayShare": "Everyone"})[0] in (200, 204)

print("PASS: test_share_movie")
sys.exit(0)
