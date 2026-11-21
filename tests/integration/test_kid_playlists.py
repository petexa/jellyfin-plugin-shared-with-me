"""A profile that blocks unrated items of type Other (Jellyfin counts a playlist as one) must still list its own
"Share with <name>" playlists, as a TV app does when it offers "Add to playlist". Run reset.py first, and again afterwards
(this test edits kid1's policy; reset.py puts it back).

Before the fix kid1's own playlist had no rating, so Jellyfin hid it from kid1's playlist list although it opened by id.
The playlist now carries a rating kid1 may see. It must stay that way after a library refresh and a server restart, and
nobody else may see kid1's playlist.
Every check is an assert; the script exits non-zero on the first failure.
"""
import time

from jf import call, login, run_remote, B

admin, admin_id = login("admin", "spike-admin")
tok, ids = {"admin": admin}, {"admin": admin_id}
for n in ("kid1", "kid2", "adult"):
    tok[n], ids[n] = login(n, "pw-" + n)


def step(msg):
    print("-", msg, flush=True)


def wait(pred, what, secs=30, every=0.5):
    end = time.time() + secs
    while time.time() < end:
        v = pred()
        if v:
            return v
        time.sleep(every)
    raise AssertionError("timed out waiting for " + what)


def wholphin_list(n):
    """The request Wholphin's "Add to playlist" menu makes, then its per-playlist edit check."""
    s, d = call("GET", "/Items", tok[n], userId=ids[n], includeItemTypes="Playlist", recursive="true", limit=25,
                sortBy="DateLastContentAdded", sortOrder="Descending")
    assert s == 200, (n, s, d)
    out = []
    for p in d["Items"]:
        s2, d2 = call("GET", f"/Playlists/{p['Id']}/Users/{ids[n]}", tok[n])
        if s2 == 200 and d2.get("CanEdit"):
            out.append(p["Name"])
    return sorted(out)


def policy(uid):
    s, d = call("GET", f"/Users/{uid}", admin)
    assert s == 200, (s, d)
    return d["Policy"]


def set_unrated(uid, kinds):
    p = policy(uid)
    p["BlockUnratedItems"] = kinds
    s, d = call("POST", f"/Users/{uid}/Policy", admin, body=p)
    assert s in (200, 204), (s, d)


def sync():
    s, d = call("POST", "/SharedWithMe/Audit/Sync", admin)
    assert s == 200, ("sync", s, d)


step("kid1 blocks unrated Movie, Series and Other (like a real child profile): the plugin still gives kid1 its playlist")
set_unrated(ids["kid1"], ["Movie", "Series", "Other"])
sync()
wait(lambda: wholphin_list("kid1") == ["Share with kid2"], "kid1 to list its own playlist")

step("nobody else sees it: kid2 lists only its own, the adult only its own")
assert wholphin_list("kid2") == ["Share with kid1"], wholphin_list("kid2")
assert wholphin_list("adult") == ["Share with kid1", "Share with kid2"], wholphin_list("adult")

step("it survives a library refresh")
s, d = call("POST", "/Library/Refresh", admin)
assert s in (200, 204), (s, d)
time.sleep(8)
assert wholphin_list("kid1") == ["Share with kid2"], wholphin_list("kid1")

step("it survives a server restart")
run_remote("docker restart jf-share-spike\n", "swm-restart.sh")
import urllib.request
def up():
    try:
        return urllib.request.urlopen(B + "/System/Info/Public", timeout=3).status == 200
    except Exception:
        return False
wait(up, "the server to come back", secs=120, every=2)
time.sleep(10)
tok["kid1"], ids["kid1"] = login("kid1", "pw-kid1")
assert wholphin_list("kid1") == ["Share with kid2"], wholphin_list("kid1")

step("kid1 stops blocking unrated Other items: the playlist still lists (it simply carries no rating again)")
set_unrated(ids["kid1"], ["Movie", "Series"])
sync()
time.sleep(3)
assert wholphin_list("kid1") == ["Share with kid2"], wholphin_list("kid1")
print("PASS: test_kid_playlists")
