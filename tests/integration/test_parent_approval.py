"""Version 0.2, C: parent approval and notifications. Run reset.py first, and again afterwards (this test edits kid1's
policy and the plugin settings, and leaves shares behind; reset.py puts them back).

With ApprovalMode = LimitedProfiles, kid1 (still restricted, but capped at 18 here so it can see the higher-rated
titles kid2 cannot) asks to share with kid2: the share is Pending, kid2 sees nothing and nobody's policy or tags change;
an administrator's Approve makes it real, Decline (with a note) ends it for good and kid1 sees the note, and kid1 can
withdraw a request. Shares by the administrator and by an unrestricted profile are made at once, and ApprovalMode = None
makes kid1's shares immediate again. A title kid1 puts in its "Share with kid2" playlist waits with its entry kept, is
shared once approved, and is taken out (and stays out) once declined.

Notifications go to a throwaway HTTP listener this script runs in a thread (stopped at the end): SWM_LISTEN_HOST (default:
this machine's address on the route to the test server) and SWM_LISTEN_PORT (default 8765).
Every check is an assert; the script exits non-zero on the first failure.
"""
import http.server
import json
import os
import socket
import threading
import time
import urllib.parse

from jf import B, SHARE_ROOT, assert_settled, call, login, movies, series, under

PLUGIN = "4069397e-cf0d-460a-9a00-e5175c137516"
admin, admin_id = login("admin", "spike-admin")
tok, ids = {"admin": admin}, {"admin": admin_id}
for n in ("kid1", "kid2", "adult", "newbie"):
    tok[n], ids[n] = login(n, "pw-" + n)


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


# ---- the listener ----

def own_address():
    host = urllib.parse.urlparse(B).hostname
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect((host, 9))  # no packet is sent: this only picks the route
        return s.getsockname()[0]
    finally:
        s.close()


LISTEN_HOST = os.environ.get("SWM_LISTEN_HOST") or own_address()
LISTEN_PORT = int(os.environ.get("SWM_LISTEN_PORT", "8765"))
received = []  # (path, content type, body) of every POST
lock = threading.Lock()


class Hook(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length") or 0))
        with lock:
            received.append((self.path, self.headers.get("Content-Type"), body))
        if self.path.startswith("/slow"):
            time.sleep(20)  # never answers in time
        if self.path.startswith("/redirect"):
            self.send_response(302)
            self.send_header("Location", f"http://{LISTEN_HOST}:{LISTEN_PORT}/redirected")
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        self.send_response(204)
        self.end_headers()

    def log_message(self, *args):
        pass


server = http.server.ThreadingHTTPServer(("0.0.0.0", LISTEN_PORT), Hook)
server.daemon_threads = True
threading.Thread(target=server.serve_forever, daemon=True).start()
HOOK = f"http://{LISTEN_HOST}:{LISTEN_PORT}/hook?token=abc"


def events(since):
    with lock:
        got = received[since:]
    return [json.loads(b) for p, _, b in got if p.startswith("/hook")]


def mark():
    with lock:
        return len(received)


# ---- the server ----

def config():
    s, cfg = call("GET", f"/Plugins/{PLUGIN}/Configuration", admin)
    assert s == 200, (s, cfg)
    return cfg


def save(**changes):
    s, d = call("POST", f"/Plugins/{PLUGIN}/Configuration", admin, body={**config(), **changes})
    assert s in (200, 204), ("save config", changes, s, d)


def set_policy(n, **changes):
    s, d = call("GET", f"/Users/{ids[n]}", admin)
    assert s == 200, (s, d)
    s, d = call("POST", f"/Users/{ids[n]}/Policy", admin, body={**d["Policy"], **changes})
    assert s in (200, 204), (n, s, d)


def share(n, item, recipient):
    return call("POST", "/SharedWithMe/Shares", tok[n], body={"ItemId": item, "RecipientUserId": ids[recipient]})


def record(share_id):
    s, d = call("GET", "/SharedWithMe/Shares", admin)
    assert s == 200, (s, d)
    return next((r for r in d if r["Id"] == share_id), None)


def records(state=None):
    s, d = call("GET", "/SharedWithMe/Shares", admin)
    assert s == 200, (s, d)
    return [r for r in d if state is None or r["State"] == state]


def sync():
    s, d = call("POST", "/SharedWithMe/Audit/Sync", admin)
    assert s == 200, ("sync", s, d)
    return d


def policies():
    out = {}
    for n in ids:
        s, u = call("GET", f"/Users/{ids[n]}", admin)
        assert s == 200, (n, s, u)
        p = u["Policy"]
        out[n] = (p["EnableAllFolders"], sorted(p.get("BlockedTags") or []), sorted(p.get("EnabledFolders") or []))
    return out


def libraries():
    s, d = call("GET", "/Library/VirtualFolders", admin)
    assert s == 200, (s, d)
    return sorted(v["Name"] for v in d)


def copies(n, kind="Movie"):
    s, d = call("GET", "/Items", tok[n], userId=ids[n], recursive="true", includeItemTypes=kind, fields="Path")
    assert s == 200, (n, s, d)
    return {i["Name"]: i["Id"] for i in d["Items"] if under(i.get("Path") or "", SHARE_ROOT)}


def plays(n, item):
    s, d = call("POST", f"/Items/{item}/PlaybackInfo", tok[n], body={}, userId=ids[n])
    return s == 200 and bool(d.get("MediaSources"))


def received_by(n):
    s, d = call("GET", "/SharedWithMe/Received", tok[n])
    assert s == 200, (n, s, d)
    return [r["Title"] for r in d]


def kid2_sees_nothing_of(title):
    assert title not in received_by("kid2"), received_by("kid2")
    assert title not in copies("kid2") and title not in copies("kid2", "Series"), copies("kid2")
    s, d = call("GET", "/Items", tok["kid2"], userId=ids["kid2"], recursive="true", searchTerm=title, includeItemTypes="Movie,Series")
    assert s == 200 and not [i for i in d["Items"] if i["Name"] == title], (s, d)
    s, d = call("GET", "/UserViews", tok["kid2"], userId=ids["kid2"])
    assert s == 200, (s, d)
    return [v["Name"] for v in d["Items"]]


def audit():
    s, d = call("GET", "/SharedWithMe/Audit", admin)
    assert s == 200, (s, d)
    return d


def approve(share_id, n="admin"):
    return call("POST", f"/SharedWithMe/Shares/{share_id}/Approve", tok[n])


def decline(share_id, reason=None, n="admin"):
    return call("POST", f"/SharedWithMe/Shares/{share_id}/Decline", tok[n], body=None if reason is None else {"Reason": reason})


def mine(n):
    s, d = call("GET", "/SharedWithMe/Shares", tok[n], withSummary="true")
    assert s == 200, (n, s, d)
    return d["Shares"]


def plugin_playlist(n, recipient):
    s, d = call("GET", "/Items", tok[n], userId=ids[n], includeItemTypes="Playlist", recursive="true", fields="Path")
    assert s == 200, (n, s, d)
    return next((p["Id"] for p in d["Items"] if p["Name"] == "Share with " + recipient and "/swm-" in (p.get("Path") or "")), None)


def entries(n, pid):
    s, d = call("GET", f"/Playlists/{pid}/Items", tok[n], userId=ids[n])
    assert s == 200, (n, pid, s, d)
    return [i["Id"] for i in d["Items"]]


def add(n, pid, item):
    s, d = call("POST", f"/Playlists/{pid}/Items", tok[n], ids=item, userId=ids[n])
    assert s in (200, 204), ("add", n, pid, item, s, d)


try:
    film = {m["Name"]: m["Id"] for m in movies(admin, admin_id)}
    show = {m["Name"]: m["Id"] for m in series(admin, admin_id)}
    F15, E18, SHOW = film["Spike Fifteen"], film["Spike Eighteen"], show["Spike Show"]

    step("the listener is reachable from the test server (the test-message endpoint)")
    save(NotifyUrl=HOOK, NotifyOn="ApprovalsOnly")
    cfg = config()
    assert cfg["NotifyUrl"] == HOOK and cfg["NotifyOn"] == "ApprovalsOnly" and cfg.get("ApprovalMode") == "None", cfg
    m0 = mark()
    s, d = call("POST", "/SharedWithMe/Audit/NotifyTest", admin)
    assert s == 200 and d["Sent"], ("test message", s, d)
    got = events(m0)
    assert got == [{"event": "test", "message": "Shared With Me test message.", "sharer": "", "recipient": "", "title": ""}], got
    with lock:
        assert received[-1][1].startswith("application/json"), received[-1]
    for n in ("kid1", "adult"):
        s, d = call("POST", "/SharedWithMe/Audit/NotifyTest", tok[n])
        assert s == 403, ("test message as", n, s, d)

    step("a notification address that is not http(s) is refused on save: the previous one stays")
    for bad in ("ftp://example.org/x", "file:///etc/passwd", "not a url", "javascript:alert(1)"):
        save(NotifyUrl=bad)
        assert config()["NotifyUrl"] == HOOK, (bad, config()["NotifyUrl"])

    step("approval on; kid1 capped at 18 (still restricted) so it sees the titles kid2 cannot")
    save(ApprovalMode="LimitedProfiles")
    assert config()["ApprovalMode"] == "LimitedProfiles", config()
    set_policy("kid1", MaxParentalRating=18, MaxParentalSubRating=1)
    sync()
    kid1_sees = {m["Name"] for m in movies(tok["kid1"], ids["kid1"])}
    assert {"Spike Fifteen", "Spike Eighteen"} <= kid1_sees, kid1_sees
    assert "Spike Show" in {m["Name"] for m in series(tok["kid1"], ids["kid1"])}
    assert "Spike Fifteen" not in {m["Name"] for m in movies(tok["kid2"], ids["kid2"])}
    assert_settled(admin, list(ids.values()), "before the first request")
    before_policies, before_libs, before_views = policies(), libraries(), kid2_sees_nothing_of("Spike Fifteen")
    assert all(p[0] for p in before_policies.values()), ("everyone starts on all libraries", before_policies)

    step("kid1 shares Spike Fifteen with kid2: Pending, and nothing changes for anyone")
    m1 = mark()
    s, pending = share("kid1", F15, "kid2")
    assert s == 200 and pending["State"] == "Pending" and not pending["FolderName"], (s, pending)
    assert record(pending["Id"])["State"] == "Pending"
    assert [r["Id"] for r in mine("kid1") if r["State"] == "Pending"] == [pending["Id"]], mine("kid1")
    time.sleep(3)
    assert policies() == before_policies, ("a pending share changed a policy", before_policies, policies())
    assert all(p[0] for p in policies().values()), policies()
    assert libraries() == before_libs, libraries()
    assert kid2_sees_nothing_of("Spike Fifteen") == before_views
    assert "Shared with kid2" not in libraries()
    sync()
    assert record(pending["Id"])["State"] == "Pending", record(pending["Id"])
    assert policies() == before_policies and libraries() == before_libs
    wait(lambda: events(m1), "the approval-needed message")
    assert events(m1) == [{"event": "approval-needed", "message": "kid1 is waiting for approval to share Spike Fifteen with kid2.",
                           "sharer": "kid1", "recipient": "kid2", "title": "Spike Fifteen"}], events(m1)

    step("asking again is refused; only an administrator may approve or decline")
    s, d = share("kid1", F15, "kid2")
    assert s == 409 and d["Refusal"] == "AlreadyShared", (s, d)
    for n in ("kid1", "kid2", "adult"):
        assert approve(pending["Id"], n)[0] == 403 and decline(pending["Id"], "x", n)[0] == 403, n
    assert record(pending["Id"])["State"] == "Pending"

    step("the audit page lists it under Waiting for approval, and not under kid2's profile")
    waiting = audit()["AwaitingApproval"]
    assert [(w["Id"], w["SharerName"], w["RecipientName"], w["Title"]) for w in waiting] == [(pending["Id"], "kid1", "kid2", "Spike Fifteen")], waiting
    kid2_rows = next(p for p in audit()["Profiles"] if p["User"]["Name"] == "kid2")["Shares"]
    assert all(r["Id"] != pending["Id"] for r in kid2_rows), kid2_rows

    step("admin approves: the share is built, kid2 sees and can play the copy")
    m2 = mark()
    s, active = approve(pending["Id"])
    assert s == 200 and active["State"] == "Active" and active["Id"] == pending["Id"] and active["FolderName"], (s, active)
    assert active["CreatedUtc"] == pending["CreatedUtc"], (active, pending)
    wait(lambda: "Spike Fifteen" in copies("kid2"), "kid2's copy of Spike Fifteen", secs=60)
    wait(lambda: plays("kid2", copies("kid2")["Spike Fifteen"]), "kid2 can play the copy", secs=60)
    wait(lambda: "Spike Fifteen" in received_by("kid2"), "Spike Fifteen in kid2's Received", secs=60)
    assert "Shared with kid2" in libraries()
    assert audit()["AwaitingApproval"] == []
    wait(lambda: events(m2), "the approved message")
    assert [e["event"] for e in events(m2)] == ["approved"], events(m2)
    assert events(m2)[0]["message"] == "kid1 may now share Spike Fifteen with kid2: it was approved.", events(m2)
    assert approve(pending["Id"])[0] == 404

    step("a second request is declined with a note: it is gone for good and kid1 sees why")
    m3 = mark()
    s, second = share("kid1", E18, "kid2")
    assert s == 200 and second["State"] == "Pending", (s, second)
    s, d = decline(second["Id"], "Not this one,\nask me at the weekend. <b>ok</b>")
    assert s == 200 and d["State"] == "Closed" and d["StateReason"] == "declined", (s, d)
    assert d["DeclineNote"] == "Not this one, ask me at the weekend. <b>ok</b>", d
    row = next(r for r in mine("kid1") if r["Id"] == second["Id"])
    assert row["State"] == "Closed" and row["StateReason"] == "declined" and row["DeclineNote"].startswith("Not this one"), row
    sync()
    assert record(second["Id"])["State"] == "Closed"
    kid2_sees_nothing_of("Spike Eighteen")
    assert approve(second["Id"])[0] == 404 and decline(second["Id"])[0] == 404
    wait(lambda: len(events(m3)) >= 2, "the approval-needed and declined messages")
    assert [e["event"] for e in events(m3)] == ["approval-needed", "declined"], events(m3)
    assert "weekend" not in json.dumps(events(m3)), events(m3)

    step("kid1 withdraws a request (Undo)")
    s, third = share("kid1", SHOW, "kid2")
    assert s == 200 and third["State"] == "Pending", (s, third)
    s, d = call("DELETE", f"/SharedWithMe/Shares/{third['Id']}", tok["kid1"])
    assert s == 204, (s, d)
    assert record(third["Id"]) is None
    assert audit()["AwaitingApproval"] == []

    step("an unrestricted profile and the administrator share at once; no 'shared' message under ApprovalsOnly")
    m4 = mark()
    s, d = share("adult", E18, "kid2")
    assert s == 200 and d["State"] == "Active", (s, d)
    assert call("DELETE", f"/SharedWithMe/Shares/{d['Id']}", tok["adult"])[0] == 204
    save(NotifyOn="EverySharing")
    s, d = share("admin", E18, "kid2")
    assert s == 200 and d["State"] == "Active", (s, d)
    wait(lambda: events(m4), "the shared message")
    assert events(m4) == [{"event": "shared", "message": "admin shared Spike Eighteen with kid2.", "sharer": "admin",
                           "recipient": "kid2", "title": "Spike Eighteen"}], events(m4)
    assert call("DELETE", f"/SharedWithMe/Shares/{d['Id']}", admin)[0] == 204

    step("NotifyOn = Nothing sends nothing; ApprovalMode = None makes kid1's shares immediate again")
    save(NotifyOn="Nothing", ApprovalMode="None")
    m5 = mark()
    s, d = share("kid1", E18, "kid2")
    assert s == 200 and d["State"] == "Active", (s, d)
    assert call("DELETE", f"/SharedWithMe/Shares/{d['Id']}", tok["kid1"])[0] == 204
    time.sleep(3)
    assert events(m5) == [], events(m5)

    step("a dead notification address does not stop or hold up a share")
    save(ApprovalMode="LimitedProfiles", NotifyOn="EverySharing")
    for dead in (f"http://{LISTEN_HOST}:{LISTEN_PORT + 1}/nothing-listens", f"http://{LISTEN_HOST}:{LISTEN_PORT}/slow"):
        save(NotifyUrl=dead)
        for who in ("admin", "kid1"):
            t0 = time.time()
            s, d = share(who, E18, "kid2")
            took = time.time() - t0
            assert s == 200 and d["State"] == ("Active" if who == "admin" else "Pending"), (dead, who, s, d)
            assert took < 2.5, (dead, who, "the share took", took)
            if who == "admin":
                assert call("DELETE", f"/SharedWithMe/Shares/{d['Id']}", admin)[0] == 204
            else:
                t0 = time.time()
                assert decline(d["Id"])[0] == 200
                assert time.time() - t0 < 2.5, (dead, "decline took", time.time() - t0)

    step("the test message reports a dead address and a redirect in plain words, and no redirect is followed")
    save(NotifyUrl=f"http://{LISTEN_HOST}:{LISTEN_PORT + 1}/nothing-listens")
    s, d = call("POST", "/SharedWithMe/Audit/NotifyTest", admin)
    assert s == 200 and not d["Sent"] and "could not be sent" in d["Message"], (s, d)
    m6 = mark()
    save(NotifyUrl=f"http://{LISTEN_HOST}:{LISTEN_PORT}/redirect")
    s, d = call("POST", "/SharedWithMe/Audit/NotifyTest", admin)
    assert s == 200 and not d["Sent"] and "302" in d["Message"], (s, d)
    time.sleep(2)
    with lock:
        paths = [p for p, _, _ in received[m6:]]
    assert paths == ["/redirect"], ("a redirect was followed", paths)

    step("TV: kid1 puts Spike Show in its 'Share with kid2' playlist: Pending, the entry is kept, no second request")
    save(NotifyUrl=HOOK, NotifyOn="ApprovalsOnly")
    pid = plugin_playlist("kid1", "kid2")
    assert pid, "kid1 has no 'Share with kid2' playlist"
    before = policies()
    m7 = mark()
    s, eps = call("GET", f"/Shows/{SHOW}/Episodes", tok["kid1"], userId=ids["kid1"])
    assert s == 200 and eps["Items"], (s, eps)
    add("kid1", pid, eps["Items"][0]["Id"])
    wait(lambda: [r for r in records("Pending") if r["OriginalItemId"].replace("-", "") == SHOW.replace("-", "")], "the TV request")
    for _ in range(2):
        sync()
        time.sleep(2)
    tv = [r for r in records() if same(r["OriginalItemId"], SHOW) and same(r["SharerUserId"], ids["kid1"]) and r["State"] != "Closed"]
    assert len(tv) == 1 and tv[0]["State"] == "Pending" and tv[0]["ViaPlaylist"], tv
    # the approved web share of Spike Fifteen was mirrored in as well; the episode is the one entry of the series
    assert sorted(entries("kid1", pid)) == sorted([eps["Items"][0]["Id"], F15.replace("-", "")]), (entries("kid1", pid), F15)
    assert "Spike Show" not in copies("kid2", "Series")
    assert policies() == before, ("the TV request changed a policy", before, policies())
    assert [e["event"] for e in events(m7)] == ["approval-needed"], events(m7)
    s, notes = call("GET", "/SharedWithMe/Notes", tok["kid1"])
    assert s == 200 and notes["Notes"] == [], notes
    assert [w["ViaPlaylist"] for w in audit()["AwaitingApproval"]] == [True], audit()["AwaitingApproval"]

    step("TV: approved, the series reaches kid2 and the entry stays")
    s, d = approve(tv[0]["Id"])
    assert s == 200 and d["State"] == "Active", (s, d)
    wait(lambda: "Spike Show" in copies("kid2", "Series"), "kid2's copy of Spike Show", secs=60)
    sync()
    time.sleep(2)
    # the approved web share of Spike Fifteen was mirrored in as well; the episode is the one entry of the series
    assert sorted(entries("kid1", pid)) == sorted([eps["Items"][0]["Id"], F15.replace("-", "")]), (entries("kid1", pid), F15)
    assert record(tv[0]["Id"])["State"] == "Active"
    assert len([r for r in records() if same(r["OriginalItemId"], SHOW) and r["State"] != "Closed"]) == 1

    step("TV: Spike Eighteen put in the playlist and declined: the entry goes and does not come back")
    add("kid1", pid, E18)
    wait(lambda: [r for r in records("Pending") if same(r["OriginalItemId"], E18)], "the second TV request")
    tv2 = next(r for r in records("Pending") if same(r["OriginalItemId"], E18))
    assert E18 in entries("kid1", pid)
    s, d = decline(tv2["Id"], "Not on a school night")
    assert s == 200 and d["StateReason"] == "declined", (s, d)
    wait(lambda: E18 not in entries("kid1", pid), "the declined entry to leave the playlist")
    for _ in range(3):
        sync()
        time.sleep(2)
    assert E18 not in entries("kid1", pid), entries("kid1", pid)
    assert [r["State"] for r in records() if same(r["OriginalItemId"], E18) and same(r["SharerUserId"], ids["kid1"])
            and r["State"] != "Closed"] == [], records()
    assert not record(tv2["Id"])["ViaPlaylist"], record(tv2["Id"])
    kid2_sees_nothing_of("Spike Eighteen")

    step("restore is not blocked by a pending share (only active ones count)")
    s, waiting_one = share("kid1", E18, "kid2")
    assert s == 200 and waiting_one["State"] == "Pending", (s, waiting_one)
    s, d = call("POST", "/SharedWithMe/Audit/Restore", admin)
    assert s == 409, ("restore must wait for the two active shares", s, d)
    for r in records("Active"):
        assert call("DELETE", f"/SharedWithMe/Shares/{r['Id']}", admin)[0] == 204
    s, d = call("POST", "/SharedWithMe/Audit/Restore", admin)
    assert s == 200, ("restore with only a pending share", s, d)
    after = record(waiting_one["Id"])
    assert after["State"] == "Closed" and after["StateReason"] == "sharing was switched off", after
    assert all(p[0] for p in policies().values()), policies()
finally:
    server.shutdown()
    server.server_close()

print("PASS: test_parent_approval")
