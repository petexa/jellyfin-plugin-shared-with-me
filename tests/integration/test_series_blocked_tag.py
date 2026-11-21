"""R1 acceptance: a series share closes at once when the recipient starts blocking a tag EVERY episode carries.
Run reset.py first, and again afterwards (this test tags every Spike Show episode and edits kid2's blocked tags;
reset.py puts both back).

Before the fix the share stayed in the scan-in-progress grace, ended Broken "original missing" on a later run and
the copy lingered until the next scan, user edit or 6 h run. Spike Show itself carries no tag, so this exercises the
every-episode branch. Control: a tag that only SOME episodes carry keeps the share Active.
Every check is an assert; the script exits non-zero on the first failure.
"""
import time

from jf import call, login, series, set_tags

admin, admin_id = login("admin", "spike-admin")
tok, ids = {"admin": admin}, {"admin": admin_id}
for n in ("kid2", "adult"):
    tok[n], ids[n] = login(n, "pw-" + n)


def step(msg):
    print("-", msg, flush=True)


def wait(pred, what, secs=20, every=0.5):
    end = time.time() + secs
    while time.time() < end:
        if pred():
            return
        time.sleep(every)
    raise AssertionError("timed out waiting for " + what)


def record(rid):
    s, d = call("GET", "/SharedWithMe/Shares", admin)
    assert s == 200, (s, d)
    return next(r for r in d if r["Id"] == rid)


def policy(uid):
    s, d = call("GET", f"/Users/{uid}", admin)
    assert s == 200, (s, d)
    return d["Policy"]


def set_blocked(uid, tags):
    p = policy(uid)
    p["BlockedTags"] = tags
    s, d = call("POST", f"/Users/{uid}/Policy", admin, body=p)
    assert s in (200, 204), (s, d)


def copy_visible_to_kid2():
    s, d = call("GET", "/Items", tok["kid2"], userId=ids["kid2"], recursive="true", includeItemTypes="Episode",
                fields="Path")
    assert s == 200, (s, d)
    return [i["Name"] for i in d["Items"] if "/shared-with-me/" in (i.get("Path") or "")]


SS = {s["Name"]: s["Id"] for s in series(admin, admin_id)}["Spike Show"]
s, eps = call("GET", f"/Shows/{SS}/Episodes", admin, userId=admin_id)
assert s == 200 and len(eps["Items"]) >= 3, (s, eps)
EP = [e["Id"] for e in eps["Items"]]

step("adult shares Spike Show with kid2 (kid2 blocks no tag yet): the copy is there")
s, d = call("POST", "/SharedWithMe/Shares", tok["adult"], body={"ItemId": SS, "RecipientUserId": ids["kid2"]})
assert s in (200, 201), (s, d)
RID = d["Id"]
wait(lambda: copy_visible_to_kid2(), "kid2 to see the copy")
assert record(RID)["State"] == "Active"

step("control: a tag on only ONE episode, blocked by kid2, keeps the share Active")
set_tags(admin, admin_id, EP[0], ["r1-some"])
set_blocked(ids["kid2"], ["r1-some"])
time.sleep(8)
assert record(RID)["State"] == "Active", record(RID)
assert copy_visible_to_kid2(), "the other episodes must stay visible"

step("every episode carries r1-block and kid2 starts blocking it: the share closes within seconds")
for e in EP:
    set_tags(admin, admin_id, e, ["r1-block"])
set_blocked(ids["kid2"], ["r1-block"])
t0 = time.time()
wait(lambda: record(RID)["State"] == "Closed", "the share to close", secs=30)
r = record(RID)
print(f"    closed after {time.time() - t0:.1f} s: {r['State']} / {r['StateReason']}")
assert "blocks a tag" in r["StateReason"], r
assert not copy_visible_to_kid2(), "the copy must be gone for kid2"
print("PASS: test_series_blocked_tag")
