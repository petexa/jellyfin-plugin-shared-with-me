"""Version 0.2, B: expiring shares. Run reset.py first, and again afterwards (this test leaves shares and TvShareDays set;
reset.py puts both back).

Through the real API, with times taken from the test server's own clock (its HTTP Date header):
  * adult shares Spike Eighteen with kid1 from the web until about 45 s from now: Active, kid1 sees the copy, the share is
    mirrored into adult's "Share with kid1" playlist. Within a bounded time after the end time the share is Closed with the
    reason "expired", kid1 no longer has the copy, kid1's share library is gone and the playlist no longer lists the title;
    later runs do not bring it back;
  * a share with no end time (adult, Spike Fifteen to kid2) survives the same wait;
  * an end time in the past, or more than 366 days ahead, is refused with InvalidExpiry and changes nothing;
  * with TvShareDays = 1 a share made from a TV playlist (newbie's "Share with kid2") ends about one day from now, and a
    web share made meanwhile gets no end time; TvShareDays is held to 0..366.
Every check is an assert; the script exits non-zero on the first failure.
"""
import email.utils
import re
import time
import urllib.request
from datetime import datetime, timedelta, timezone

from jf import B, SHARE_ROOT, call, login, movies, series, under

PLUGIN = "4069397e-cf0d-460a-9a00-e5175c137516"
admin, admin_id = login("admin", "spike-admin")
tok, ids = {"admin": admin}, {"admin": admin_id}
for n in ("kid1", "kid2", "adult", "newbie"):
    tok[n], ids[n] = login(n, "pw-" + n)
RANDOM_FOLDER = re.compile(r"/swm-[0-9a-f]{32}$")
LEAD = 45  # seconds from now to the end time: long enough to see the share working first
CLOSE_BOUND = 60  # seconds after the end time by which it must have ended


def step(msg):
    print("-", msg, flush=True)


def wait(pred, what, secs=30, every=0.5):
    end = time.time() + secs
    while time.time() < end:
        if pred():
            return
        time.sleep(every)
    raise AssertionError("timed out waiting for " + what)


def same(a, b):
    return a.replace("-", "").lower() == b.replace("-", "").lower()


def server_now():
    """The test server's clock (whole seconds), so a clock difference with this machine cannot skew an end time."""
    with urllib.request.urlopen(B + "/System/Info/Public", timeout=30) as r:
        return email.utils.parsedate_to_datetime(r.headers["Date"]).astimezone(timezone.utc)


def iso(dt):
    return dt.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"


def parse(s):
    return datetime.fromisoformat(s.replace("Z", "+00:00")).astimezone(timezone.utc)


def config():
    s, cfg = call("GET", f"/Plugins/{PLUGIN}/Configuration", admin)
    assert s == 200, (s, cfg)
    return cfg


def save(**changes):
    s, d = call("POST", f"/Plugins/{PLUGIN}/Configuration", admin, body={**config(), **changes})
    assert s in (200, 204), ("save config", changes, s, d)


def share(n, item, recipient, expires=None):
    body = {"ItemId": item, "RecipientUserId": ids[recipient]}
    if expires is not None:
        body["ExpiresUtc"] = expires
    return call("POST", "/SharedWithMe/Shares", tok[n], body=body)


def records():
    s, d = call("GET", "/SharedWithMe/Shares", admin)
    assert s == 200, (s, d)
    return d


def record(share_id):
    return next(r for r in records() if r["Id"] == share_id)


def copies(n):
    s, d = call("GET", "/Items", tok[n], userId=ids[n], recursive="true", includeItemTypes="Movie", fields="Path")
    assert s == 200, (n, s, d)
    return {i["Name"]: i["Id"] for i in d["Items"] if under(i.get("Path") or "", SHARE_ROOT)}


def share_libraries():
    s, d = call("GET", "/Library/VirtualFolders", admin)
    assert s == 200, (s, d)
    return {v["Name"] for v in d if any(under(p, SHARE_ROOT) for p in v.get("Locations") or [])}


def playlist(n, recipient):
    s, d = call("GET", "/Items", tok[n], userId=ids[n], includeItemTypes="Playlist", recursive="true", fields="Path")
    assert s == 200, (n, s, d)
    found = [p["Id"] for p in d["Items"] if p["Name"] == "Share with " + recipient and RANDOM_FOLDER.search(p.get("Path") or "")]
    assert len(found) == 1, (n, recipient, d["Items"])
    return found[0]


def entries(n, pid):
    s, d = call("GET", f"/Playlists/{pid}/Items", tok[n], userId=ids[n])
    assert s == 200, (n, pid, s, d)
    return [i["Id"] for i in d["Items"]]


def received(n):
    s, d = call("GET", "/SharedWithMe/Received", tok[n])
    assert s == 200, (n, s, d)
    return d


def sync():
    s, d = call("POST", "/SharedWithMe/Audit/Sync", admin)
    assert s == 200, ("sync", s, d)
    return d


film = {m["Name"]: m["Id"] for m in movies(admin, admin_id)}
e18, f15 = film["Spike Eighteen"], film["Spike Fifteen"]
show = next(i["Id"] for i in series(admin, admin_id) if i["Name"] == "Spike Show")
assert config().get("TvShareDays", 0) == 0, config()

step(f"adult shares Spike Eighteen with kid1 until {LEAD} s from now (server clock), and Spike Fifteen with kid2 for good")
now = server_now()
until = now + timedelta(seconds=LEAD)
s, d = share("adult", e18, "kid1", iso(until))
assert s == 200 and d["State"] == "Active", (s, d)
expiring = d["Id"]
s, d = share("adult", f15, "kid2")
assert s == 200 and d["State"] == "Active", (s, d)
keeper = d["Id"]

step("before the end time: kid1 sees the copy, has a share library, and adult's playlist lists the title")
wait(lambda: "Spike Eighteen" in copies("kid1"), "kid1 to see the copy", secs=30)
wait(lambda: "Spike Fifteen" in copies("kid2"), "kid2 to see its copy", secs=30)
copy_id = copies("kid1")["Spike Eighteen"]
assert "Shared with kid1" in share_libraries(), share_libraries()
adult_pl = playlist("adult", "kid1")
wait(lambda: e18 in entries("adult", adult_pl), "the web share to be mirrored into adult's playlist", secs=15)
assert server_now() < until, ("the share was not seen working before its end time", server_now(), until)
assert record(expiring)["State"] == "Active", record(expiring)

step(f"after the end time: closed as expired within {CLOSE_BOUND} s, with no sync asked for")
while server_now() < until:
    time.sleep(0.5)
wait(lambda: record(expiring)["State"] == "Closed", "the expired share to close", secs=CLOSE_BOUND)
closed_after = (server_now() - until).total_seconds()
r = record(expiring)
assert r["StateReason"] == "expired", r
assert r["FolderName"] == "", r
print(f"    closed about {closed_after:.0f} s after its end time")

step("the end time came back as sent, in UTC")
assert r.get("ExpiresUtc") and abs((parse(r["ExpiresUtc"]) - until).total_seconds()) < 0.01, (r.get("ExpiresUtc"), iso(until))

step("kid1 has no copy, its share library is gone, the playlist no longer lists the title, the share page says so")
wait(lambda: copies("kid1") == {}, "kid1's copy to go", secs=30)
s, d = call("GET", f"/Items/{copy_id}", tok["kid1"], userId=ids["kid1"])
assert s == 404, ("the copy is still reachable", s, d)
wait(lambda: "Shared with kid1" not in share_libraries(), "kid1's share library to go", secs=15)
wait(lambda: e18 not in entries("adult", adult_pl), "the title to leave adult's playlist", secs=15)
assert received("kid1") == [], received("kid1")
s, d = call("GET", "/SharedWithMe/Shares", tok["adult"], withSummary="true")
assert s == 200, (s, d)
mine = {x["Id"]: x for x in d["Shares"]}
assert mine[expiring]["State"] == "Closed" and mine[expiring]["StateReason"] == "expired", mine[expiring]

step("the share with no end time survived the same wait")
k = record(keeper)
assert k["State"] == "Active" and k.get("ExpiresUtc") is None, k
assert "Spike Fifteen" in copies("kid2"), copies("kid2")
assert "Shared with kid2" in share_libraries(), share_libraries()
assert [x["Title"] for x in received("kid2")] == ["Spike Fifteen"], received("kid2")

step("later runs do not bring it back")
sync()
time.sleep(3)
sync()
assert record(expiring)["State"] == "Closed", record(expiring)
assert not [x for x in records() if x["OriginalItemId"] == e18 and same(x["RecipientUserId"], ids["kid1"]) and x["State"] == "Active"], records()
assert e18 not in entries("adult", adult_pl)
assert copies("kid1") == {}

step("an end time in the past or more than 366 days ahead is refused and changes nothing")
before = len(records())
for when in (server_now() - timedelta(minutes=1), server_now() + timedelta(days=367)):
    s, d = share("adult", e18, "kid1", iso(when))
    assert s == 409 and d["Refusal"] == "InvalidExpiry" and d["Message"], (iso(when), s, d)
assert len(records()) == before, records()
s, d = share("adult", e18, "kid1", iso(server_now() + timedelta(days=365)))  # the expired title may be shared afresh
assert s == 200 and abs((parse(d["ExpiresUtc"]) - server_now()).total_seconds() - 365 * 86400) < 120, (s, d)

step("TvShareDays is held to 0..366")
save(TvShareDays=1000)
assert config()["TvShareDays"] == 366, config()
save(TvShareDays=-4)
assert config()["TvShareDays"] == 0, config()

step("with TvShareDays = 1, a title newbie adds to its TV playlist for kid2 is shared for about one day")
save(TvShareDays=1)
assert config()["TvShareDays"] == 1, config()
newbie_pl = playlist("newbie", "kid2")
s, d = call("POST", f"/Playlists/{newbie_pl}/Items", tok["newbie"], ids=e18, userId=ids["newbie"])
assert s in (200, 204), ("add to playlist", s, d)
added_at = server_now()


def tv_share():
    return [x for x in records() if same(x["SharerUserId"], ids["newbie"]) and same(x["RecipientUserId"], ids["kid2"]) and x["State"] == "Active"]


wait(lambda: len(tv_share()) == 1, "the playlist share", secs=20)
tv = tv_share()[0]
assert tv["ViaPlaylist"] is True and tv["OriginalItemId"] == e18, tv
lead = (parse(tv["ExpiresUtc"]) - added_at).total_seconds()
assert abs(lead - 86400) < 120, ("TV share should end about a day from now", tv["ExpiresUtc"], iso(added_at))

step("a web share made while TvShareDays = 1 gets no end time, even once mirrored into the playlist")
s, d = share("adult", show, "kid2")
assert s == 200 and d.get("ExpiresUtc") is None, (s, d)
web = d["Id"]
wait(lambda: record(web)["ViaPlaylist"] is True, "the web share to be mirrored into adult's playlist", secs=15)
assert entries("adult", playlist("adult", "kid2")), "the mirrored series left no entry"
assert record(web).get("ExpiresUtc") is None, record(web)

print("PASS: test_expiring_shares")
