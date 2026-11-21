"""Task 8 acceptance: the audit and share endpoints and the two pages, on the spike server. Run reset.py first;
the test ends with a Restore, and reset.py should still be run afterwards.

Every check is an assert; the script exits non-zero on the first failure.
"""
import re
import time
import urllib.request
import uuid

from jf import B, SHARE_ROOT, assert_settled, call, login, movies, owns_plugin_playlist, run_remote, under

admin, admin_id = login("admin", "spike-admin")
tok, ids = {}, {}
for n in ("kid1", "kid2", "adult", "newbie"):
    tok[n], ids[n] = login(n, "pw-" + n)
N = {n: uuid.UUID(i).hex for n, i in ids.items()}
ALL = {**ids, "admin": admin_id}
PLUGIN = "4069397e-cf0d-460a-9a00-e5175c137516"


def step(msg):
    print("-", msg, flush=True)


def policy(uid):
    s, d = call("GET", f"/Users/{uid}", admin)
    assert s == 200, (s, d)
    return d["Policy"]


def set_policy(uid, **changes):
    p = policy(uid)
    p.update(changes)
    s, d = call("POST", f"/Users/{uid}/Policy", admin, body=p)
    assert s in (200, 204), (s, d)


def libs():
    s, d = call("GET", "/Library/VirtualFolders", admin)
    assert s == 200, (s, d)
    return d


def share_libs():
    return [v for v in libs() if any(under(p, SHARE_ROOT) for p in v.get("Locations", []))]


def audit():
    s, d = call("GET", "/SharedWithMe/Audit", admin)
    assert s == 200, ("audit", s, d)
    return d


def audit_row(share_id):
    for p in audit()["Profiles"]:
        for r in p["Shares"]:
            if r["Id"] == share_id:
                return p, r
    raise AssertionError(("share not in audit", share_id))


def search(n, term, **q):
    s, d = call("GET", "/SharedWithMe/Search", tok[n] if n != "admin" else admin, term=term, **q)
    assert s == 200, (n, term, s, d)
    return d


def wait(pred, what, secs=60, every=1.0):
    end = time.time() + secs
    while time.time() < end:
        v = pred()
        if v:
            return v
        time.sleep(every)
    raise AssertionError("timed out waiting for " + what)


originals = {m["Name"]: m for m in movies(admin, admin_id)}
assert sorted(originals) == ["Spike Eighteen", "Spike Fifteen", "Spike PG"], sorted(originals)
E18 = originals["Spike Eighteen"]["Id"]

step("pages: the share page is served to everyone (anonymous HTML shell, data calls need a token)")
s, html = call("GET", "/SharedWithMe/Page")
assert s == 200 and isinstance(html, str) and 'id="swm-share-page"' in html, (s, html)
s, js = call("GET", "/SharedWithMe/Page/share.js")
assert s == 200 and isinstance(js, str) and "jellyfin_credentials" in js, (s, str(js)[:200])
CSP = "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'"
for path in ("/SharedWithMe/Page", "/SharedWithMe/Page/share.js"):
    with urllib.request.urlopen(B + path, timeout=30) as r:
        h = r.headers
        assert (h["X-Content-Type-Options"], h["X-Frame-Options"], h["Content-Security-Policy"], h["Cache-Control"]) == \
            ("nosniff", "DENY", CSP, "no-cache"), (path, dict(h))

step("pages: the trailing-slash address ends on the canonical page, and the script it names is fetchable")
opener = urllib.request.build_opener(type("NoRedirect", (urllib.request.HTTPRedirectHandler,), {"redirect_request": lambda *a, **k: None}))
try:
    opener.open(B + "/SharedWithMe/Page/", timeout=30)
    raise AssertionError("expected a redirect for /SharedWithMe/Page/")
except urllib.error.HTTPError as e:
    assert e.code in (301, 302, 307, 308) and e.headers["Location"].endswith("/SharedWithMe/Page"), (e.code, dict(e.headers))
with urllib.request.urlopen(B + "/SharedWithMe/Page/", timeout=30) as r:  # followed
    assert r.geturl() == B + "/SharedWithMe/Page", r.geturl()
    page = r.read().decode()
src = re.search(r'<script src="([^"]+)"', page).group(1)
assert src.startswith("/"), src  # absolute from the base URL, resolves the same from any address
with urllib.request.urlopen(urllib.parse.urljoin(B + "/SharedWithMe/Page/", src), timeout=30) as r:
    assert r.status == 200 and b"jellyfin_credentials" in r.read()
assert "{{BASE}}" not in page, "placeholder left in the page"

step("pages: the audit page is the plugin's dashboard page, listed for admins only")
s, pages = call("GET", "/web/ConfigurationPages", admin)
assert s == 200 and any(p["Name"] == "SharedWithMe" and p["PluginId"] == PLUGIN.replace("-", "") for p in pages), (s, pages)
for n in ("kid1", "adult"):
    s, d = call("GET", "/web/ConfigurationPages", tok[n])
    assert s == 403, (n, "plugin page listing", s, d)
s, html = call("GET", "/web/ConfigurationPage", admin, name="SharedWithMe")
assert s == 200 and 'id="swm-audit-page"' in html and "__plugin/SharedWithMeAudit.js" in html, (s, str(html)[:300])
s, js = call("GET", "/web/ConfigurationPage", admin, name="SharedWithMeAudit.js")
assert s == 200 and "export default" in js and "innerHTML" not in js, (s, str(js)[:200])

step("admin-only endpoints answer 403 to kid1 and adult")
for n in ("kid1", "adult"):
    for method, path in (("GET", "/SharedWithMe/Audit"), ("POST", "/SharedWithMe/Audit/Restore"),
                         ("POST", "/SharedWithMe/Audit/Sync"), ("GET", "/SharedWithMe/Diagnostics")):
        s, d = call(method, path, tok[n])
        assert s == 403, (n, method, path, s, d)
s, d = call("GET", "/SharedWithMe/Audit")
assert s == 401, ("anonymous audit", s, d)

step("Search: short terms are empty; forUserId only for an admin")
assert search("adult", "S") == [] and search("adult", " ") == [], "short term"
s, d = call("GET", "/SharedWithMe/Search", tok["kid1"], term="Spike", forUserId=ids["adult"])
assert s == 403, ("kid1 searching as adult", s, d)
assert {h["Title"] for h in search("admin", "Spike", forUserId=ids["kid1"])} == {"Spike Kids", "Spike PG"}, "admin as kid1"

step("Search: kid1 (cap 12) only finds what kid1 can see; adult finds all five (three movies, two series)")
hits = search("kid1", "Spike")
assert [(h["Title"], h["Kind"]) for h in hits] == [("Spike Kids", "Series"), ("Spike PG", "Movie")], hits
assert set(hits[0]) == {"Id", "Title", "Year", "Kind", "OfficialRating"}, hits[0]  # no server paths
assert sorted(h["Title"] for h in search("adult", "Spike")) == ["Spike Eighteen", "Spike Fifteen", "Spike Kids", "Spike PG", "Spike Show"]

step("Search: library access is applied by the server too (control: newbie with Movies and Shows taken away finds nothing)")
movies_lib = next(v for v in libs() if v["Name"] == "Movies")["ItemId"]
assert len(search("newbie", "Spike")) == 5, "control: newbie on all libraries finds all five"
set_policy(ids["newbie"], EnableAllFolders=False, EnabledFolders=[])
assert search("newbie", "Spike") == [], "newbie without Movies and Shows must find nothing"
set_policy(ids["newbie"], EnableAllFolders=True, EnabledFolders=[])
assert len(search("newbie", "Spike")) == 5

step("adult shares Spike Eighteen with kid1")
s, rec = call("POST", "/SharedWithMe/Shares", tok["adult"], body={"ItemId": E18, "RecipientUserId": ids["kid1"]})
assert s == 200, (s, rec)
share_id = rec["Id"]

step("Audit: the share is listed under kid1, visible now once scanned, shared by adult")
_, row = audit_row(share_id)
assert row["State"] == "Active" and row["SharerName"] == "adult" and row["SharerExists"], row
row = wait(lambda: (lambda r: r if r["VisibleNow"] else None)(audit_row(share_id)[1]), "visibleNow true", every=0.5)
# proves: the server says kid1 can see the copy now (found, not pending, not missing)
assert (row["VisibleNow"], row["Pending"], row["NotFound"]) == (True, False, False), row
assert row["OfficialRating"] == "GB-18" and row["Title"] == "Spike Eighteen", row
prof, _ = audit_row(share_id)
assert prof["User"]["Name"] == "kid1" and prof["CapLabel"] == "12", prof
a = audit()
assert a.get("SetupProblem") is None and a["InCharge"] is True and a["ShareRootLocked"] is True, a
assert {"PolicyWrites", "SyncRuns"} <= set(a["Diagnostics"]), a["Diagnostics"]
assert sorted(u["Name"] for u in a["SwitchedUsers"]) == ["admin", "adult", "kid1", "kid2", "newbie"], a["SwitchedUsers"]
assert [p for p in a["Profiles"] if p["User"]["Name"] == "kid2"][0]["Shares"] == [], "kid2 got nothing"

step("Search: kid1 never gets the copy, nor the originals over its cap")
s, d = call("GET", "/Items", tok["kid1"], userId=ids["kid1"], recursive="true", includeItemTypes="Movie", fields="Path")
copy = next(i for i in d["Items"] if i["Name"] == "Spike Eighteen")
assert under(copy["Path"], SHARE_ROOT), copy  # control: kid1 does see the copy in its library
hits = search("kid1", "Spike")
assert [h["Title"] for h in hits] == ["Spike Kids", "Spike PG"] and copy["Id"] not in [h["Id"] for h in hits], hits
assert copy["Id"] not in [h["Id"] for h in search("adult", "Spike")]

step("Received: kid1 sees the share, kid2 nothing; Shares stays the sharer's list")
s, d = call("GET", "/SharedWithMe/Received", tok["kid1"])
assert s == 200 and [(x["Id"], x["Title"], x["SharerName"]) for x in d] == [(share_id, "Spike Eighteen", "adult")], (s, d)
s, d = call("GET", "/SharedWithMe/Received", tok["kid2"])
assert s == 200 and d == [], (s, d)
s, d = call("GET", "/SharedWithMe/Shares", tok["kid1"])
assert s == 200 and d == [], (s, d)
s, d = call("GET", "/SharedWithMe/Shares", tok["adult"])
assert s == 200 and [x["Id"] for x in d] == [share_id], (s, d)
s, d = call("GET", "/SharedWithMe/Shares", tok["adult"], withSummary="true")
assert s == 200 and [x["Id"] for x in d["Shares"]] == [share_id] and d["HiddenCount"] == 0, (s, d)
s, d = call("GET", "/SharedWithMe/Shares", tok["kid1"], withSummary="true")
assert s == 200 and d == {"Shares": [], "HiddenCount": 0}, (s, d)

step("the share folder setting is kept by the server while shares exist; other settings in the same save apply")
s, cfg = call("GET", f"/Plugins/{PLUGIN}/Configuration", admin)
assert s == 200 and cfg["ShareRoot"] == "" and cfg["WhoMayShare"] == "Everyone", (s, cfg)
before = {n: (policy(u)["EnableAllFolders"], sorted(policy(u).get("EnabledFolders") or [])) for n, u in ALL.items()}
s, d = call("POST", f"/Plugins/{PLUGIN}/Configuration", admin, body={**cfg, "ShareRoot": "/config/data/swm-elsewhere", "WhoMayShare": "UncappedOnly"})
assert s in (200, 204), (s, d)
s, cfg2 = call("GET", f"/Plugins/{PLUGIN}/Configuration", admin)
assert cfg2["ShareRoot"] == "" and cfg2["WhoMayShare"] == "UncappedOnly", cfg2
time.sleep(2)
assert {n: (policy(u)["EnableAllFolders"], sorted(policy(u).get("EnabledFolders") or [])) for n, u in ALL.items()} == before, "a library was granted"
assert audit_row(share_id)[1]["VisibleNow"] is True, "the share stopped working"
s, d = call("POST", f"/Plugins/{PLUGIN}/Configuration", admin, body={**cfg2, "WhoMayShare": "Everyone"})
assert s in (200, 204), (s, d)

# The brief's break (delete the copy's NFO, scan) never showed visibleNow false on 12.4: polled every 0.25 s for 30 s,
# the copy stayed visible (the server keeps the CustomRating it already read, and the post-scan sync rewrites the NFO).
# Removing the copy's file is a break the server must reflect: the scan drops the item, so the audit can only answer
# "not visible" (pending) until the sync rebuilds the link.
step("break it: delete the copy's file and scan -> visibleNow false, then the sync heals it -> true")
folder = rec["FolderName"]
link = f"{SHARE_ROOT}/{N['kid1']}/movies/{folder}/{folder}.mp4"
run_remote(f"docker exec jf-share-spike rm -f '{link}'\n", "swm-t8-break.sh")
s, d = call("POST", "/Library/Refresh", admin)
assert s in (200, 204), (s, d)
seen = wait(lambda: (lambda r: r if not r["VisibleNow"] else None)(audit_row(share_id)[1]), "visibleNow false after the break",
            secs=30, every=0.25)
print("    broken:", {k: seen.get(k) for k in ("State", "StateReason", "VisibleNow", "Pending", "NotFound")})
# proves: with its file gone the server has no item for the copy; a new share before its first missed check reads
# "pending", not "not found" and not "visible"
assert (seen["VisibleNow"], seen["Pending"], seen["NotFound"]) == (False, True, False), seen
wait(lambda: audit_row(share_id)[1]["VisibleNow"], "visibleNow true again after the heal", secs=90)

step("Restore is refused while a share is active")
s, d = call("POST", "/SharedWithMe/Audit/Restore", admin)
assert s == 409 and d["Message"] == "Remove every share first, then restore.", (s, d)
assert all(not policy(u)["EnableAllFolders"] for u in ALL.values()), "nobody restored"

step("Check now (Audit/Sync) runs a full sync and returns its report")
s, d = call("POST", "/SharedWithMe/Audit/Sync", admin)
assert s == 200 and {"Checked", "Closed", "Broken"} <= set(d) and d["Checked"] >= 1, (s, d)

step("I1: a parent narrows newbie's library list by hand after the plugin isolated it (Shows unticked)")
MOVIES_ID = next(v["ItemId"] for v in libs() if v["Name"] == "Movies")
set_policy(ids["newbie"], EnableAllFolders=False, EnabledFolders=[MOVIES_ID])
time.sleep(3)  # the plugin's heal runs: it must leave the narrowed list alone
assert [f.replace("-", "") for f in policy(ids["newbie"])["EnabledFolders"]] == [MOVIES_ID.replace("-", "")], policy(ids["newbie"])
assert [u["Name"] for u in audit()["NarrowedUsers"]] == ["newbie"], audit()["NarrowedUsers"]

step("remove the share, then Restore -> everyone else back on all libraries, newbie left on its list and named; TV sharing off, no tags, no share libraries")
s, d = call("DELETE", f"/SharedWithMe/Shares/{share_id}", tok["adult"])
assert s == 204, (s, d)
s, d = call("POST", "/SharedWithMe/Audit/Restore", admin)
assert s == 200, (s, d)
assert sorted(u["Name"] for u in d["RestoredUsers"]) == ["admin", "adult", "kid1", "kid2"], d
assert [u["Name"] for u in d["LeftOnOwnList"]] == ["newbie"], d
p = policy(ids["newbie"])
assert not p["EnableAllFolders"] and [f.replace("-", "") for f in p["EnabledFolders"]] == [MOVIES_ID.replace("-", "")], p
s, cfg = call("GET", "/Plugins/4069397e-cf0d-460a-9a00-e5175c137516/Configuration", admin)
assert s == 200 and cfg["TvSharingPlaylists"] is False, ("a restore turns TV sharing off", cfg)
for n, uid in ALL.items():
    p = policy(uid)
    assert p["EnableAllFolders"] or n == "newbie", (n, "not on all libraries")
    assert not [t for t in (p.get("BlockedTags") or []) if t.startswith("swm-")], (n, p.get("BlockedTags"))
for n in tok:
    assert not owns_plugin_playlist(tok[n], ids[n]), (n, "a plugin playlist survived the restore")
assert not owns_plugin_playlist(admin, admin_id), "admin's plugin playlist survived the restore"
assert share_libs() == [], share_libs()
a = audit()
assert a["InCharge"] is False and a["SwitchedUsers"] == [], a

step("a sync straight after the restore changes nothing")
before = assert_settled(admin, list(ALL.values()), "after restore")
s, d = call("POST", "/SharedWithMe/Audit/Sync", admin)
assert s == 200, (s, d)
after = assert_settled(admin, list(ALL.values()), "after the sync that followed the restore")
assert after["PolicyWrites"] == before["PolicyWrites"], (before, after)
for n, uid in ALL.items():
    assert policy(uid)["EnableAllFolders"] or n == "newbie", (n, "isolated again by the sync")
assert not policy(ids["newbie"])["EnableAllFolders"], "newbie's own list was overwritten after the restore"
assert share_libs() == []

print("PASS: audit and share endpoints and pages")
