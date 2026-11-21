"""Version 0.2, A: chosen sharers. Run reset.py first, and again afterwards (this test edits kid1's policy and leaves
shares behind; reset.py puts both back).

With "Who may share" set to SelectedProfiles and AllowedSharers = [kid1]: kid1 may share and owns "Share with" playlists;
kid2 and the (non-administrator) adult may not share, are offered nobody on the share page and own no plugin playlist;
the administrator always may. Taking kid1 off the list removes its playlists but keeps the share it made. Switching
back to Everyone gives everyone their playlists again.
Every check is an assert; the script exits non-zero on the first failure.
"""
import re
import time

from jf import call, login, movies

PLUGIN = "4069397e-cf0d-460a-9a00-e5175c137516"
admin, admin_id = login("admin", "spike-admin")
tok, ids = {"admin": admin}, {"admin": admin_id}
for n in ("kid1", "kid2", "adult", "newbie"):
    tok[n], ids[n] = login(n, "pw-" + n)
RANDOM_FOLDER = re.compile(r"/swm-[0-9a-f]{32}$")


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
    """Guids compared whatever their written form (dashes, case)."""
    return a.replace("-", "").lower() == b.replace("-", "").lower()


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
    p = {**d["Policy"], **changes}
    s, d = call("POST", f"/Users/{ids[n]}/Policy", admin, body=p)
    assert s in (200, 204), (n, s, d)


def plugin_playlists(n):
    """Names of the plugin's playlists user n owns (those in a random swm-<hex> folder)."""
    s, d = call("GET", "/Items", tok[n], userId=ids[n], includeItemTypes="Playlist", recursive="true", fields="Path")
    assert s == 200, (n, s, d)
    return sorted(p["Name"] for p in d["Items"] if RANDOM_FOLDER.search(p.get("Path") or ""))


def recipients(n):
    s, d = call("GET", "/SharedWithMe/Recipients", tok[n])
    assert s == 200, (n, s, d)
    return sorted(r["Name"] for r in d)


def share(n, item, recipient):
    return call("POST", "/SharedWithMe/Shares", tok[n], body={"ItemId": item, "RecipientUserId": ids[recipient]})


def refused(res, code):
    s, d = res
    assert s == 409, ("expected 409", code, s, d)
    assert d["Refusal"] == code, (code, d)


def active(sharer):
    s, d = call("GET", "/SharedWithMe/Shares", admin)
    assert s == 200, (s, d)
    return [r for r in d if same(r["SharerUserId"], ids[sharer]) and r["State"] == "Active"]


def sync():
    s, d = call("POST", "/SharedWithMe/Audit/Sync", admin)
    assert s == 200, ("sync", s, d)


film = {m["Name"]: m["Id"] for m in movies(admin, admin_id)}
EVERYONE = {
    "admin": ["Share with kid1", "Share with kid2"],
    "adult": ["Share with kid1", "Share with kid2"],
    "kid1": ["Share with kid2"],
    "kid2": ["Share with kid1"],
    "newbie": ["Share with kid1", "Share with kid2"],
}

step("the reset state: everyone may share and owns the playlists for the two capped kids")
cfg = config()
assert cfg["WhoMayShare"] == "Everyone" and not cfg.get("AllowedSharers"), cfg
for n, want in EVERYONE.items():
    wait(lambda n=n, want=want: plugin_playlists(n) == want, f"{n}'s playlists {want}")

step("SelectedProfiles with AllowedSharers = [kid1] is saved and read back as given")
save(WhoMayShare="SelectedProfiles", AllowedSharers=[ids["kid1"]])
cfg = config()
assert cfg["WhoMayShare"] == "SelectedProfiles", cfg
assert len(cfg["AllowedSharers"]) == 1 and same(cfg["AllowedSharers"][0], ids["kid1"]), cfg

step("the playlists follow: kid1 and the administrator keep theirs, kid2, adult and newbie lose theirs")
wait(lambda: all(plugin_playlists(n) == [] for n in ("kid2", "adult", "newbie")), "the unlisted profiles' playlists to go")
assert plugin_playlists("kid1") == EVERYONE["kid1"], plugin_playlists("kid1")
assert plugin_playlists("admin") == EVERYONE["admin"], plugin_playlists("admin")

step("the share page offers people to kid1 and the administrator only")
assert recipients("kid1") == ["admin", "adult", "kid2", "newbie"], recipients("kid1")
assert "kid1" in recipients("admin"), recipients("admin")
for n in ("kid2", "adult", "newbie"):
    assert recipients(n) == [], (n, recipients(n))

step("kid1 (now capped at 15, so it sees Spike Fifteen and kid2 does not) shares it with kid2")
set_policy("kid1", MaxParentalRating=15, MaxParentalSubRating=3)
sync()
s, d = share("kid1", film["Spike Fifteen"], "kid2")
assert s == 200 and d["State"] == "Active", (s, d)
kid1_share = d["Id"]

step("adult, kid2 and newbie are refused as not allowed, for shares that Everyone would allow")
refused(share("adult", film["Spike Eighteen"], "kid2"), "SharingNotAllowed")
refused(share("newbie", film["Spike Eighteen"], "kid1"), "SharingNotAllowed")
refused(share("kid2", film["Spike PG"], "kid1"), "SharingNotAllowed")  # checked before anything about the title
assert active("adult") == [] and active("kid2") == [] and active("newbie") == []

step("the administrator always may share, though not on the list")
s, d = share("admin", film["Spike Eighteen"], "kid2")
assert s == 200 and d["State"] == "Active", (s, d)

step("kid1 taken off the list: its playlist goes, the share it made stays")
save(WhoMayShare="SelectedProfiles", AllowedSharers=[])
assert config()["AllowedSharers"] == [], config()
wait(lambda: plugin_playlists("kid1") == [], "kid1's playlist to go")
refused(share("kid1", film["Spike Fifteen"], "kid2"), "SharingNotAllowed")
assert [r["Id"] for r in active("kid1")] == [kid1_share], active("kid1")
assert plugin_playlists("admin") == EVERYONE["admin"], plugin_playlists("admin")

step("adult put on the list: adult gets its playlists, the others still have none")
save(WhoMayShare="SelectedProfiles", AllowedSharers=[ids["adult"]])
wait(lambda: plugin_playlists("adult") == EVERYONE["adult"], "adult's playlists")
assert all(plugin_playlists(n) == [] for n in ("kid1", "kid2", "newbie"))

step("back to Everyone: every profile has its playlists again and may share")
save(WhoMayShare="Everyone", AllowedSharers=[])
for n, want in EVERYONE.items():
    wait(lambda n=n, want=want: plugin_playlists(n) == want, f"{n}'s playlists {want} again")
assert recipients("kid2") != [] and recipients("adult") != []
assert [r["Id"] for r in active("kid1")] == [kid1_share], active("kid1")

print("PASS: test_selected_sharers")
