"""Task 11 acceptance: sharing from any app through the private "Share with <name>" playlists. Run reset.py first.

Every check is an assert; the script exits non-zero on the first failure. Jellyfin 12.4 facts this relies on are in
docs/api-notes.md ("Task 11: playlists" and "Known Jellyfin 12.4 behaviour: private playlists").
"""
import re
import time
import uuid

from jf import SHARE_ROOT, assert_settled, call, expected_swm, login, movies, plugin_tags, run_remote, scan, series, tag_owners, under

PLUGIN = "4069397e-cf0d-460a-9a00-e5175c137516"
admin, admin_id = login("admin", "spike-admin")
tok, ids = {"admin": admin}, {"admin": admin_id}
for n in ("kid1", "kid2", "adult", "newbie"):
    tok[n], ids[n] = login(n, "pw-" + n)
RANDOM_FOLDER = re.compile(r"/swm-[0-9a-f]{32}$")


def step(msg):
    print("-", msg)


def wait(pred, what, secs=15, every=0.5):
    end = time.time() + secs
    while time.time() < end:
        if pred():
            return
        time.sleep(every)
    raise AssertionError("timed out waiting for " + what)


def sync():
    s, d = call("POST", "/SharedWithMe/Audit/Sync", admin)
    assert s == 200, ("sync", s, d)
    return d


def playlists(n):
    s, d = call("GET", "/Items", tok[n], userId=ids[n], includeItemTypes="Playlist", recursive="true", fields="Path")
    assert s == 200, (n, s, d)
    return d["Items"]


def plugin_playlists(n):
    """The plugin's playlists of user n, by name: those in a random swm-<hex> folder."""
    return {p["Name"]: p["Id"] for p in playlists(n) if RANDOM_FOLDER.search(p.get("Path") or "")}


def pl(n, recipient):
    return plugin_playlists(n).get("Share with " + recipient)


def entries(n, pid):
    s, d = call("GET", f"/Playlists/{pid}/Items", tok[n], userId=ids[n])
    assert s == 200, (n, pid, s, d)
    return [i["Id"] for i in d["Items"]]


def add(n, pid, item):
    s, d = call("POST", f"/Playlists/{pid}/Items", tok[n], ids=item, userId=ids[n])
    assert s in (200, 204), ("add", n, pid, item, s, d)


def take_out(n, pid, item):
    s, d = call("DELETE", f"/Playlists/{pid}/Items", tok[n], entryIds=item)
    assert s in (200, 204), ("remove entry", n, pid, item, s, d)


def records(sharer=None, recipient=None, state="Active"):
    s, d = call("GET", "/SharedWithMe/Shares", admin)
    assert s == 200, (s, d)
    return [r for r in d if (sharer is None or r["SharerUserId"] == ids[sharer])
            and (recipient is None or r["RecipientUserId"] == ids[recipient]) and (state is None or r["State"] == state)]


def notes(n):
    s, d = call("GET", "/SharedWithMe/Notes", tok[n])
    assert s == 200, (n, s, d)
    return d


def copies(n, kind="Movie"):
    s, d = call("GET", "/Items", tok[n], userId=ids[n], recursive="true", includeItemTypes=kind, fields="Path")
    assert s == 200, (n, s, d)
    return {i["Name"]: i["Id"] for i in d["Items"] if under(i.get("Path") or "", SHARE_ROOT)}


def views(n):
    s, d = call("GET", "/UserViews", tok[n], userId=ids[n])
    assert s == 200, (n, s, d)
    return d["Items"]


def libs():
    s, d = call("GET", "/Library/VirtualFolders", admin)
    assert s == 200, (s, d)
    return d


def plays(n, item):
    s, d = call("POST", f"/Items/{item}/PlaybackInfo", tok[n], body={}, userId=ids[n])
    return s == 200 and bool(d.get("MediaSources"))


def blind(n, lib_id, copy_id, view_name):
    """User n learns nothing about another recipient's library or copy through any call a client makes (as test_share_movie)."""
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
    for q in ({"userId": ids[n]}, {}, {"userId": ids[n], "recursive": "true", "includeItemTypes": "Movie", "parentId": lib_id}):
        s, d = call("GET", "/Items", tok[n], ids=copy_id, **q)
        assert s != 200 or copy_id not in [i["Id"] for i in d["Items"]], (n, "Items?ids=", q, s, d)


def policy(n):
    s, d = call("GET", f"/Users/{ids[n]}", admin)
    assert s == 200, (n, s, d)
    return d["Policy"]


def set_policy(n, **changes):
    p = policy(n)
    p.update(changes)
    s, d = call("POST", f"/Users/{ids[n]}/Policy", admin, body=p)
    assert s in (200, 204), (n, s, d)


def check_tag_rule(label):
    """Ruling B2: every user blocks exactly the swm- tags of every other user with plugin items, never its own."""
    owners = tag_owners(admin, {ids[n]: tok[n] for n in tok})
    for n in tok:
        got = plugin_tags(policy(n).get("BlockedTags"))
        assert got == expected_swm(owners, ids[n]), (label, n, got, expected_swm(owners, ids[n]))
    return owners


def hints(n, term="Share with"):
    s, d = call("GET", "/Search/Hints", tok[n], userId=ids[n], searchTerm=term)
    assert s == 200, (n, s, d)
    return [h.get("Id") for h in d["SearchHints"]]


def private_to_owner(pid, owner, who):
    """Nobody but the owner finds or opens playlist pid through any listing, search or playlist route (Ruling D)."""
    for n in who:
        assert pid not in [p["Id"] for p in playlists(n)], (n, "playlist listing")
        s, d = call("GET", f"/Users/{ids[n]}/Items", tok[n], includeItemTypes="Playlist", recursive="true")
        assert s == 200 and pid not in [i["Id"] for i in d["Items"]], (n, "Users/{id}/Items Playlist", s, d)
        s, d = call("GET", "/Items", tok[n], userId=ids[n], recursive="true", searchTerm="Share with")
        assert s == 200 and pid not in [i["Id"] for i in d["Items"]], (n, "search", s, d)
        s, d = call("GET", "/Search/Hints", tok[n], userId=ids[n], searchTerm="Share with")
        assert s == 200 and pid not in [h.get("Id") for h in d["SearchHints"]], (n, "search hints", s, d)
        for path in (f"/Playlists/{pid}", f"/Items/{pid}", f"/Items/{pid}/Images", f"/Items/{pid}/Images/Primary"):
            s, d = call("GET", path, tok[n], userId=ids[n])
            assert s == 404, (n, path, s, d)
        s, d = call("GET", f"/Playlists/{pid}/Items", tok[n], userId=ids[n])
        assert s == 404, (n, "playlist items", s, d)
        s, d = call("GET", "/Items", tok[n], userId=ids[n], parentId=pid)
        assert s != 200 or not d["Items"], (n, "Items parentId=playlist", s, d)
        for q in ({"userId": ids[n]}, {}):
            s, d = call("GET", "/Items", tok[n], ids=pid, **q)
            assert s == 200 and pid not in [i["Id"] for i in d["Items"]], (n, "Items?ids=playlist", q, s, d)
    # the owner, as control, finds and opens it on the same routes (Images/Primary: no control, the spike media has no
    # posters, so the owner gets 404 there too)
    assert pid in [p["Id"] for p in playlists(owner)], (owner, "own listing")
    assert call("GET", f"/Playlists/{pid}", tok[owner])[0] == 200, (owner, "own playlist")
    assert call("GET", f"/Playlists/{pid}/Items", tok[owner], userId=ids[owner])[0] == 200, (owner, "own playlist items")
    assert call("GET", f"/Items/{pid}", tok[owner], userId=ids[owner])[0] == 200, (owner, "own item")
    assert call("GET", f"/Items/{pid}/Images", tok[owner])[0] == 200, (owner, "own images")
    s, d = call("GET", "/Items", tok[owner], userId=ids[owner], recursive="true", searchTerm="Share with")
    assert s == 200 and pid in [i["Id"] for i in d["Items"]], (owner, "own search")
    assert pid in hints(owner), (owner, "own search hints")
    s, d = call("GET", "/Items", tok[owner], ids=pid, userId=ids[owner])
    assert s == 200 and pid in [i["Id"] for i in d["Items"]], (owner, "own Items?ids=")


originals = {m["Name"]: m["Id"] for m in movies(admin, admin_id)}
E18, F15, PG = originals["Spike Eighteen"], originals["Spike Fifteen"], originals["Spike PG"]
show = {s["Name"]: s["Id"] for s in series(admin, admin_id)}["Spike Show"]
s, eps = call("GET", f"/Shows/{show}/Episodes", tok["adult"], userId=ids["adult"])
assert s == 200, (s, eps)
EP = {e["IndexNumber"]: e["Id"] for e in eps["Items"]}
assert sorted(EP) == [1, 2, 4, 5], EP  # E04 (own GB-18) and E05 (own tag gore): make_series.sh, C1

step("the TV setting is at its default (on)")
s, cfg = call("GET", f"/Plugins/{PLUGIN}/Configuration", admin)
assert s == 200 and cfg.get("TvSharingPlaylists") is True, cfg

step("a playlist newbie makes itself, named like a plugin one, before the plugin runs")
s, d = call("POST", "/Playlists", tok["newbie"], body={"Name": "Share with kid1", "UserId": ids["newbie"], "IsPublic": False, "MediaType": "Video"})
assert s == 200, (s, d)
NEWBIE_OWN = d["Id"]
add("newbie", NEWBIE_OWN, PG)

step("first sync: who gets which playlist")
sync()
wait(lambda: pl("adult", "kid1") and pl("adult", "kid2") and pl("kid1", "kid2") and pl("kid2", "kid1"), "the plugin's playlists")
for n, want in (("adult", {"Share with kid1", "Share with kid2"}), ("kid1", {"Share with kid2"}), ("kid2", {"Share with kid1"}),
                ("newbie", {"Share with kid1", "Share with kid2"}), ("admin", {"Share with kid1", "Share with kid2"})):
    assert set(plugin_playlists(n)) == want, (n, plugin_playlists(n))
for n in ("adult", "kid1", "kid2", "newbie", "admin"):
    names = [p["Name"] for p in playlists(n)]
    assert not [x for x in names if x in ("Share with newbie", "Share with admin", "Share with adult")], (n, names)
    s, d = call("GET", "/Items", tok[n], userId=ids[n], includeItemTypes="Playlist", recursive="true", fields="MediaType")
    assert all(i.get("MediaType") == "Video" for i in d["Items"] if i["Id"] in plugin_playlists(n).values()), (n, d)
A1, A2 = pl("adult", "kid1"), pl("adult", "kid2")
assert entries("newbie", NEWBIE_OWN) == [PG], "newbie's own playlist touched"

step("Ruling A: the plugin's id is not the id a user-made playlist of that name gets")
s, d = call("POST", "/Playlists", tok["adult"], body={"Name": "Share with kid1", "UserId": ids["adult"], "IsPublic": False, "MediaType": "Video"})
assert s == 200, (s, d)
assert d["Id"] not in (A1, A2, NEWBIE_OWN), (d, A1)
assert call("DELETE", f"/Items/{d['Id']}", tok["adult"])[0] in (200, 204)

step("Ruling B2: every plugin playlist carries its owner's tag; every user blocks exactly the other owners' tags")
s, d = call("GET", "/Items", tok["adult"], userId=ids["adult"], ids=f"{A1},{A2}", fields="Tags")
assert s == 200 and all(i.get("Tags") == ["swm-" + uuid.UUID(ids["adult"]).hex] for i in d["Items"]) and len(d["Items"]) == 2, d
owners = check_tag_rule("after the first sync")
# written independently of the server's answers: all five may share and each has someone restricted to share with
assert owners == {uuid.UUID(ids[n]).hex for n in ("admin", "adult", "kid1", "kid2", "newbie")}, owners
for n in tok:
    assert policy(n)["EnableAllFolders"], (n, "switched off all libraries for playlists alone")

step("Ruling D: nobody but adult finds or opens adult's playlists (sibling, other adult, recipient, admin as a user)")
for pid in (A1, A2):
    private_to_owner(pid, "adult", ("kid1", "kid2", "newbie", "admin"))

step("control: lift kid2's block of adult's tag -> Search/Hints shows adult's playlist -> the plugin restores the block")
ADULT_TAG = "swm-" + uuid.UUID(ids["adult"]).hex
kid2_tags = policy("kid2").get("BlockedTags") or []
leaked = False
for _ in range(5):
    set_policy("kid2", BlockedTags=[t for t in kid2_tags if t.lower() != ADULT_TAG])
    if A1 in hints("kid2"):
        leaked = True
        break
    wait(lambda: ADULT_TAG in [t.lower() for t in policy("kid2").get("BlockedTags") or []], "the plugin to restore kid2's block", 10, 0.2)
assert leaked, "Search/Hints never showed adult's playlist to kid2 without the block (control)"
wait(lambda: ADULT_TAG in [t.lower() for t in policy("kid2").get("BlockedTags") or []], "the plugin to restore kid2's block", 10, 0.2)
assert A1 not in hints("kid2"), "the block is back but the playlist still shows"
check_tag_rule("after the control")

step("the owner keeps full use: listing, the Playlists view, opening, searching, adding and removing")
assert {A1, A2} <= {p["Id"] for p in playlists("adult")}
assert "Playlists" in [v["Name"] for v in views("adult")], [v["Name"] for v in views("adult")]
assert call("GET", f"/Playlists/{A1}", tok["adult"])[0] == 200 and call("GET", f"/Items/{A1}", tok["adult"], userId=ids["adult"])[0] == 200
assert A1 in hints("adult"), "the owner cannot find its own playlist"
s, d = call("GET", "/Items", tok["adult"], userId=ids["adult"], parentId=next(v["Id"] for v in views("adult") if v["Name"] == "Playlists"))
assert s == 200 and {A1, A2} <= {i["Id"] for i in d["Items"]}, (s, d)

step("adult adds Spike Eighteen to 'Share with kid1': kid1 gets it within 15 s")
add("adult", A1, E18)
wait(lambda: "Spike Eighteen" in copies("kid1"), "kid1 to list the shared film")
copy = copies("kid1")["Spike Eighteen"]
wait(lambda: plays("kid1", copy), "kid1 PlaybackInfo on the copy")
lib = next(v for v in libs() if v["Name"] == "Shared with kid1")["ItemId"]
blind("kid2", lib, copy, "Shared with kid1")
blind("newbie", lib, copy, "Shared with kid1")
rec = records("adult", "kid1")
assert len(rec) == 1 and rec[0]["OriginalItemId"] == E18 and rec[0]["ViaPlaylist"] is True, rec
assert entries("adult", A1) == [E18]
check_tag_rule("with kid1's share library")

step("the tag on the playlist does not reach the titles in it")
assert call("GET", f"/Items/{E18}", tok["newbie"], userId=ids["newbie"])[0] == 200, "newbie lost Spike Eighteen"
assert "Spike Eighteen" in [m["Name"] for m in movies(tok["newbie"], ids["newbie"])]
s, d = call("GET", "/Items", tok["adult"], userId=ids["adult"], ids=E18, fields="Tags")
assert s == 200 and d["Items"][0].get("Tags") == [], ("the original took a tag", d)
set_policy("kid2", MaxParentalRating=18, MaxParentalSubRating=1)  # kid2 now sees Spike Eighteen natively
wait(lambda: "Spike Eighteen" in [m["Name"] for m in movies(tok["kid2"], ids["kid2"]) if not under(m.get("Path") or "", SHARE_ROOT)], "kid2 to see the original natively")
assert call("GET", f"/Items/{E18}", tok["kid2"], userId=ids["kid2"])[0] == 200
set_policy("kid2", MaxParentalRating=12, MaxParentalSubRating=1)
wait(lambda: "Spike Eighteen" not in [m["Name"] for m in movies(tok["kid2"], ids["kid2"])], "kid2's cap back")

step("recorded, not asserted: /Items/Latest?parentId=<playlist id> with the block in place (needs the id)")
for n in ("newbie", "kid2"):
    s, d = call("GET", "/Items/Latest", tok[n], userId=ids[n], parentId=A1)
    s0, d0 = call("GET", "/Items/Latest", tok[n], userId=ids[n], parentId=uuid.uuid4().hex)
    print(f"    {n}: Latest?parentId=<A1> -> {[i['Name'] for i in d] if s == 200 else s}; random parentId -> {[i['Name'] for i in d0] if s0 == 200 else s0}")

step("taking it out of the playlist un-shares it and the library goes")
take_out("adult", A1, E18)
wait(lambda: not records("adult", "kid1"), "the share to go", 20)
wait(lambda: "Spike Eighteen" not in copies("kid1") and not any(v["Name"] == "Shared with kid1" for v in libs()), "kid1's copy and library to go", 30)

step("kid1 adds Spike PG to 'Share with kid2' (kid2 sees it): taken out, no share, no note")
K12 = pl("kid1", "kid2")
add("kid1", K12, PG)
assert PG in entries("kid1", K12), "the server stored kid1's PG entry"
wait(lambda: PG not in entries("kid1", K12), "PG to be taken out")
assert not records("kid1", "kid2", None), records("kid1", "kid2", None)
assert notes("kid1")["Notes"] == [], notes("kid1")

step("a web share by adult appears in adult's playlist; kid1 cannot pass its copy on")
s, d = call("POST", "/SharedWithMe/Shares", tok["adult"], body={"ItemId": E18, "RecipientUserId": ids["kid1"]})
assert s == 200, (s, d)
WEB = d["Id"]
wait(lambda: E18 in entries("adult", A1), "the web share mirrored into the playlist")
wait(lambda: records("adult", "kid1")[0]["ViaPlaylist"] is True, "the web share flagged ViaPlaylist")
wait(lambda: "Spike Eighteen" in copies("kid1"), "kid1's copy")
copy = copies("kid1")["Spike Eighteen"]
kid2_before = sorted(m["Name"] for m in movies(tok["kid2"], ids["kid2"]))
add("kid1", K12, copy)
assert copy in entries("kid1", K12), "the server stored kid1's copy entry"
wait(lambda: copy not in entries("kid1", K12), "the copy to be taken out")
n1 = notes("kid1")["Notes"]
assert len(n1) == 1 and n1[0]["Message"] == "Spike Eighteen was shared with you, so you can't pass it on." and n1[0]["RecipientName"] == "kid2", n1
assert not records("kid1", "kid2", None)
time.sleep(2)
assert sorted(m["Name"] for m in movies(tok["kid2"], ids["kid2"])) == kid2_before, "kid2 gained something"
assert notes("kid2")["Notes"] == [] and notes("adult")["Notes"] == [], "a note leaked to another user"
assert notes("kid1")["TvRecipients"] == [ids["kid2"]], notes("kid1")  # the page may only name a real playlist
assert sorted(notes("adult")["TvRecipients"]) == sorted([ids["kid1"], ids["kid2"]]), notes("adult")
s, d = call("GET", "/SharedWithMe/Notes", tok["kid1"], all="true")
assert s == 403, ("non-admin all notes", s, d)
assert call("DELETE", "/SharedWithMe/Notes", tok["kid1"])[0] == 204 and notes("kid1")["Notes"] == []

step("removing the web share on the web page takes it out of the playlist")
assert call("DELETE", f"/SharedWithMe/Shares/{WEB}", tok["adult"])[0] == 204
assert E18 not in entries("adult", A1)
time.sleep(3)
assert not records("adult", "kid1"), "shared again from a stale entry"

step("Ruling E: one episode of Spike Show shares the whole series; none left un-shares it")
add("adult", A1, EP[2])
wait(lambda: "Spike Show" in copies("kid1", "Series"), "kid1 to get the series", 20)
rec = records("adult", "kid1")
assert len(rec) == 1 and rec[0]["Kind"] == "Series" and rec[0]["OriginalItemId"] == show and rec[0]["ViaPlaylist"] is True, rec
assert entries("adult", A1) == [EP[2]], "the plugin churned the playlist"
take_out("adult", A1, EP[2])
wait(lambda: not records("adult", "kid1"), "the series share to go", 20)

step("adding the series itself (stored as its episodes) shares it")
add("adult", A1, show)
assert sorted(entries("adult", A1)) == sorted(EP.values()), entries("adult", A1)
wait(lambda: [r["Kind"] for r in records("adult", "kid1")] == ["Series"], "the series share", 20)
for e in EP.values():
    take_out("adult", A1, e)
wait(lambda: not records("adult", "kid1"), "the series share to go", 20)

step("renamed, public and deleted plugin playlists are healed")
assert call("POST", f"/Playlists/{A1}", tok["adult"], body={"Name": "my list"})[0] in (200, 204)
wait(lambda: pl("adult", "kid1") == A1, "the name back")
assert call("POST", f"/Playlists/{A1}", tok["adult"], body={"IsPublic": True})[0] in (200, 204)
wait(lambda: call("GET", f"/Playlists/{A1}", tok["adult"])[1].get("OpenAccess") is False, "private again")
private_to_owner(A1, "adult", ("kid2",))
assert call("DELETE", f"/Items/{A1}", tok["adult"])[0] in (200, 204)
wait(lambda: pl("adult", "kid1") not in (None, A1), "a new 'Share with kid1'")
assert pl("adult", "kid1") != A1, "Ruling A: a recreated playlist of the same name gets a new id"
A1 = pl("adult", "kid1")

step("renaming kid1 renames the playlists pointing at kid1")
s, dto = call("GET", f"/Users/{ids['kid1']}", admin)
dto["Name"] = "kid1-renamed"
assert call("POST", f"/Users/{ids['kid1']}", admin, body=dto)[0] in (200, 204)
wait(lambda: pl("adult", "kid1-renamed") == A1 and pl("kid2", "kid1-renamed"), "the playlists renamed")
dto["Name"] = "kid1"
assert call("POST", f"/Users/{ids['kid1']}", admin, body=dto)[0] in (200, 204)
wait(lambda: pl("adult", "kid1") == A1, "the name back")

step("setting off: every plugin playlist goes, shares stay; on: back, with the shares mirrored")
s, d = call("POST", "/SharedWithMe/Shares", tok["adult"], body={"ItemId": E18, "RecipientUserId": ids["kid1"]})
assert s == 200, (s, d)
WEB = d["Id"]
wait(lambda: E18 in entries("adult", A1), "mirrored")
s, cfg = call("GET", f"/Plugins/{PLUGIN}/Configuration", admin)
assert call("POST", f"/Plugins/{PLUGIN}/Configuration", admin, body={**cfg, "TvSharingPlaylists": False})[0] in (200, 204)
wait(lambda: all(not plugin_playlists(n) for n in ("adult", "kid1", "kid2", "newbie", "admin")), "every plugin playlist gone")
assert [r["Id"] for r in records("adult", "kid1")] == [WEB], "the share went with the playlist"
wait(lambda: all(plugin_tags(policy(n).get("BlockedTags")) == expected_swm({uuid.UUID(ids["kid1"]).hex}, ids[n]) for n in tok),
     "only kid1's tag (share library) left blocked")
assert entries("newbie", NEWBIE_OWN) == [PG]
assert call("POST", f"/Plugins/{PLUGIN}/Configuration", admin, body={**cfg, "TvSharingPlaylists": True})[0] in (200, 204)
wait(lambda: pl("adult", "kid1") and pl("adult", "kid2") and pl("kid1", "kid2") and pl("kid2", "kid1"), "the playlists back")
A1 = pl("adult", "kid1")
wait(lambda: entries("adult", A1) == [E18], "the existing share mirrored into the new playlist")
assert [r["Id"] for r in records("adult", "kid1")] == [WEB]

step("Important 1: S01E02's file is renamed while one of its entries shares the series; the share stays, the playlist is repaired")
add("adult", A1, EP[2])
wait(lambda: [r["Kind"] for r in records("adult", "kid1") if r["Kind"] == "Series"] == ["Series"], "the series share", 20)
SERIES = next(r["Id"] for r in records("adult", "kid1") if r["Kind"] == "Series")
SEASON = "/opt/jf-share-spike/media/shows/Spike Show (2010)/Season 01"
run_remote(f"cd '{SEASON}'\nmv 'Spike Show S01E02.mp4' 'Spike Show S01E02 Remastered.mp4'\nmv 'Spike Show S01E02.nfo' 'Spike Show S01E02 Remastered.nfo'\n", "swm-t11-rename.sh")
scan(admin)
xml_path = next(p for p in playlists("adult") if p["Id"] == A1)["Path"].replace("/config/", "/opt/jf-share-spike/config/") + "/playlist.xml"


def playlist_xml():
    return run_remote(f"cat '{xml_path}'\n", "swm-t11-xml.sh")


wait(lambda: "S01E02.mp4" not in playlist_xml() and EP[1] in entries("adult", A1), "the dangling entry replaced by a current episode", 30)
assert SERIES in [r["Id"] for r in records("adult", "kid1")], ("the series share was removed", records("adult", "kid1", None))
assert "Spike Show" in copies("kid1", "Series"), "kid1 lost the series"
assert entries("adult", A1)[-1:] == [EP[1]] and EP[2] not in entries("adult", A1), entries("adult", A1)

step("settled: no playlist or policy writes over 6 s of quiet, and the tag rule holds")
d = assert_settled(admin, list(ids.values()), "tv playlists")
check_tag_rule("at the end")
print("    diagnostics:", {k: d[k] for k in ("PolicyWrites", "PlaylistWrites", "SyncRuns")})

step("newbie's own 'Share with kid1' was never touched")
mine = [p for p in playlists("newbie") if p["Id"] == NEWBIE_OWN]
assert len(mine) == 1 and mine[0]["Name"] == "Share with kid1" and entries("newbie", NEWBIE_OWN) == [PG], mine

step("clean up")
assert call("DELETE", f"/SharedWithMe/Shares/{WEB}", tok["adult"])[0] == 204
assert call("DELETE", f"/Items/{NEWBIE_OWN}", tok["newbie"])[0] in (200, 204)
print("test_tv_playlists: PASS")
