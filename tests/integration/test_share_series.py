"""Task 9 acceptance: sharing a whole series on the spike server. Run reset.py first, and again afterwards (this test
adds Spike Show S01E03 on disk and deletes S01E01's file, and adds a user `teen`; reset.py puts all three back).

Spike Show at rest (make_series.sh): S01E01, E02 (no rating of their own: GB-15 from the series), S01E04 rated GB-18 by its
own NFO, S01E05 tagged `gore` by its own NFO. kid1 blocks `gore` throughout this test.

C1 (an episode's own rating and tags): a series share holds only the episodes the SHARER can see and whose own tags the
recipient does not block, and each copy episode carries its own rating:
  * `teen` (cap 15) sees the series but not E04 -> teen's share gives kid1 E01-E02 only (E04: teen cannot see it; E05: kid1
    blocks its tag), on every episode route.
  * adult (uncapped) shares -> kid1 gets E04 (rated GB-18, override 12) but never E05; kid2 (no gore block) gets E04 and E05.

What every level proves (series, season, episode):
  * kid1 (the recipient) lists the series under "Shared with kid1 (shows)", its season and its episodes, and plays E01.
  * kid2 (same cap), newbie (uncapped), adult and admin learn nothing of the copy at any level, on every endpoint that
    Task 9's probe found leaking without the per-recipient tag (controls included), plus direct ids and search.
Every check is an assert; the script exits non-zero on the first failure.
"""
import json
import sys
import time
import uuid

from jf import SHARE_ROOT, call, expected_swm, login, movies, owns_plugin_playlist, plugin_tags, run_remote, scan, series, tag_owners, under

admin, admin_id = login("admin", "spike-admin")
tok, ids = {"admin": admin}, {"admin": admin_id}
for n in ("kid1", "kid2", "adult", "newbie"):
    tok[n], ids[n] = login(n, "pw-" + n)
N = {n: uuid.UUID(i).hex for n, i in ids.items()}
TAG = "swm-" + N["kid1"]
OTHERS = ("kid2", "newbie", "adult", "admin")
SHOW_DIR = "/media/shows/Spike Show (2010)/Season 01"
LIB_NAME = "Shared with kid1 (shows)"


def step(msg):
    print("-", msg, flush=True)


def get(n, path, **q):
    return call("GET", path, tok[n], userId=ids[n], **q)


def ok(n, path, **q):
    s, d = get(n, path, **q)
    assert s == 200, (n, path, q, s, d)
    return d


def views(n):
    return ok(n, "/UserViews")["Items"]


def policy(uid):
    s, d = call("GET", f"/Users/{uid}", admin)
    assert s == 200, (s, d)
    return d["Policy"]


def set_policy(uid, **changes):
    p = policy(uid)
    p.update(changes)
    s, d = call("POST", f"/Users/{uid}/Policy", admin, body=p)
    assert s in (200, 204), (s, d)


def blocked(uid):
    return policy(uid).get("BlockedTags") or []


def share(n, item, recipient):
    return call("POST", "/SharedWithMe/Shares", tok[n], body={"ItemId": item, "RecipientUserId": ids[recipient]})


def libs():
    s, d = call("GET", "/Library/VirtualFolders", admin)
    assert s == 200, (s, d)
    return d


def shares():
    s, d = call("GET", "/SharedWithMe/Shares", admin)
    assert s == 200, (s, d)
    return d


def wait(pred, what, secs=60, every=1.0):
    end = time.time() + secs
    while time.time() < end:
        v = pred()
        if v:
            return v
        time.sleep(every)
    raise AssertionError("timed out waiting for " + what)


def numbers(items):
    return sorted((e.get("ParentIndexNumber"), e.get("IndexNumber")) for e in items)


class Copy:
    """kid1's view of the copy, read through the calls a client makes: views -> library -> series -> seasons/episodes."""

    def __init__(self):
        view = [v for v in views("kid1") if v["Name"] == LIB_NAME]
        assert len(view) == 1, [v["Name"] for v in views("kid1")]
        self.view = view[0]["Id"]
        items = ok("kid1", "/Items", parentId=self.view, fields="Path,OfficialRating,CustomRating,Tags")["Items"]
        assert [(i["Type"], i["Name"]) for i in items] == [("Series", "Spike Show")], items
        self.series = items[0]
        self.seasons = ok("kid1", f"/Shows/{self.series['Id']}/Seasons", fields="Tags,OfficialRating,CustomRating")["Items"]
        self.episodes = ok("kid1", f"/Shows/{self.series['Id']}/Episodes", fields="Path,Tags,OfficialRating,CustomRating")["Items"]

    @property
    def ids(self):
        return {self.view, self.series["Id"], *(s["Id"] for s in self.seasons), *(e["Id"] for e in self.episodes)}


def kid1_episodes():
    """Episode numbers kid1 lists in its share library, or None while the copy is not (fully) there."""
    try:
        return numbers(Copy().episodes)
    except AssertionError:
        return None


def leaks(d, copy_ids):
    text = json.dumps(d)
    return [i for i in copy_ids if i in text]


def blind(n, c, lib):
    """User n learns nothing about kid1's series copy at any level through any call a client makes."""
    every = c.ids | {lib}
    sid, season, e1 = c.series["Id"], c.seasons[0]["Id"], c.episodes[0]["Id"]
    assert not leaks(views(n), every) and LIB_NAME not in [v["Name"] for v in views(n)], (n, "views")
    for parent in (lib, sid, season):
        s, d = get(n, "/Items", parentId=parent)
        assert s != 200 or not leaks(d, every), (n, "Items parentId", parent, s, d)
        for group in ("true", "false"):
            s, d = get(n, "/Items/Latest", parentId=parent, groupItems=group)
            assert s != 200 or (d == [] or not leaks(d, every)), (n, "Latest", parent, group, s, d)
        s, d = get(n, "/Items/Filters", parentId=parent)
        assert s != 200 or (not d.get("OfficialRatings") and not d.get("Years") and not d.get("Tags")), (n, "Filters", parent, s, d)
    s, d = get(n, "/Items/Latest", parentId=lib, groupItems="false", includeItemTypes="Episode")
    assert s != 200 or not leaks(d, every), (n, "Latest episodes", s, d)
    for q in ({}, {"seriesId": sid}, {"parentId": lib}):
        s, d = get(n, "/Shows/NextUp", **q)
        assert s != 200 or not leaks(d, every), (n, "NextUp", q, s, d)
    for path in (f"/Shows/{sid}/Seasons", f"/Shows/{sid}/Episodes"):
        s, d = get(n, path)
        assert s != 200 or not leaks(d, every), (n, path, s, d)
    for i in c.ids - {c.view}:
        s, d = get(n, f"/Items/{i}")
        assert s == 404, (n, "item", i, s, d)
    d = ok(n, "/Items", recursive="true", includeItemTypes="Series,Season,Episode")
    assert not leaks(d, every), (n, "all series/seasons/episodes", d)
    for term in ("Spike Show", "Spike"):
        assert not leaks(ok(n, "/Items", recursive="true", searchTerm=term), every), (n, "search", term)
        assert not leaks(ok(n, "/Search/Hints", searchTerm=term), every), (n, "hints", term)
    assert not leaks(ok(n, "/UserItems/Resume"), every), (n, "resume")
    s, d = call("POST", f"/Items/{e1}/PlaybackInfo", tok[n], body={}, userId=ids[n])
    assert s != 200 or not d.get("MediaSources"), (n, "PlaybackInfo", s, d)
    # /Items?ids= checks no library access (Task 9 fix round 1): only the rating cap and the blocked tags stand between
    # a sibling who learns the ids and the copy.
    all_ids = ",".join(sorted(c.ids - {c.view}))
    for q in ({"userId": ids[n]}, {}, {"userId": ids[n], "recursive": "true", "includeItemTypes": "Episode", "parentId": lib}):
        s, d = call("GET", "/Items", tok[n], ids=all_ids, **q)
        assert s != 200 or not leaks(d, every), (n, "Items?ids=", q, s, d)


def own_rating(e):
    """The rating a copy episode must carry: its original's own (E04: GB-18), else the series' GB-15 (C1)."""
    return "GB-18" if e.get("IndexNumber") == 4 else "GB-15"


def recipient_sees(c, want):
    """kid1 sees the copy at every level, the badge still reads GB-15 (E04: GB-18) and E01 plays."""
    assert c.series.get("OfficialRating") == "GB-15" and c.series.get("CustomRating") == "12", c.series
    assert under(c.series["Path"], f"{SHARE_ROOT}/{N['kid1']}/shows"), c.series["Path"]
    assert TAG in (c.series.get("Tags") or []), ("the copy series does not carry kid1's tag", c.series.get("Tags"))
    assert [s["IndexNumber"] for s in c.seasons] == [1], c.seasons
    assert numbers(c.episodes) == want, numbers(c.episodes)
    assert all(under(e["Path"], f"{SHARE_ROOT}/{N['kid1']}/shows") for e in c.episodes), [e["Path"] for e in c.episodes]
    assert numbers(ok("kid1", "/Items", parentId=c.seasons[0]["Id"])["Items"]) == want
    # every level carries its own protection, as the server reports it: true rating, override and kid1's tag
    for item in (*c.seasons, *c.episodes):
        assert TAG in (item.get("Tags") or []), ("no swm- tag of its own", item["Type"], item["Name"], item.get("Tags"))
        rating = own_rating(item) if item["Type"] == "Episode" else "GB-15"
        assert (item.get("OfficialRating"), item.get("CustomRating")) == (rating, "12"), (item["Type"], item["Name"], item)
        assert "gore" not in [t.lower() for t in item.get("Tags") or []], ("kid1 blocks gore", item)
    s, d = call("POST", f"/Items/{c.episodes[0]['Id']}/PlaybackInfo", tok["kid1"], body={}, userId=ids["kid1"])
    assert s == 200 and d["MediaSources"] and len(d["MediaSources"][0]["MediaStreams"]) == 2, (s, d)


def lifted_block_leaks(n, label, read):
    """Control: with n's block on kid1's tag lifted, `read` really does leak. The plugin heals ~50 ms after the policy
    write, so the read follows the write at once and is retried when the heal wins the race."""
    mine = blocked(ids[n])
    for _ in range(6):
        set_policy(ids[n], BlockedTags=[t for t in mine if t != TAG])
        if read():
            break
        wait(lambda: TAG in blocked(ids[n]), "the plugin to restore the block", 10, 0.2)
    else:
        raise AssertionError(("control: expected a leak without the block", n, label))
    wait(lambda: TAG in blocked(ids[n]), "the plugin to restore the block", 10, 0.2)
    assert sorted(blocked(ids[n])) == sorted(mine), blocked(ids[n])
    assert not read(), ("still leaking with the block back", n, label)


def episode_file(e):
    return f"{SHOW_DIR}/Spike Show S01E0{e}.mp4"


orig_series = {s["Name"]: s for s in series(admin, admin_id)}
assert sorted(orig_series) == ["Spike Kids", "Spike Show"], sorted(orig_series)
SS = orig_series["Spike Show"]["Id"]
orig_eps = ok("adult", f"/Shows/{SS}/Episodes", fields="Path,OfficialRating,Tags")["Items"]
assert numbers(orig_eps) == [(1, 1), (1, 2), (1, 4), (1, 5)], numbers(orig_eps)
ORIG_EP = {e["IndexNumber"]: e for e in orig_eps}
assert ORIG_EP[4].get("OfficialRating") == "GB-18" and ORIG_EP[5].get("Tags") == ["gore"], (ORIG_EP[4], ORIG_EP[5])
orig_season = ok("adult", f"/Shows/{SS}/Seasons")["Items"][0]["Id"]

step("kid1 blocks the tag gore (Spike Show S01E05 carries it on its own episode NFO)")
set_policy(ids["kid1"], BlockedTags=sorted(set(blocked(ids["kid1"]) + ["gore"])))
wait(lambda: "gore" in blocked(ids["kid1"]), "kid1's gore block to stay")

step("before any share: kid1 sees Spike Kids (GB-PG) and nothing of Spike Show")
assert [s["Name"] for s in series(tok["kid1"], ids["kid1"])] == ["Spike Kids"], series(tok["kid1"], ids["kid1"])
for i in (SS, orig_season, *(e["Id"] for e in orig_eps)):
    s, d = get("kid1", f"/Items/{i}")
    assert s == 404, ("kid1 sees the original", i, s, d)

step("the share page's search offers the series, never its seasons or episodes")
s, d = call("GET", "/SharedWithMe/Search", tok["adult"], term="Spike Show")
assert s == 200 and [(h["Title"], h["Kind"]) for h in d] == [("Spike Show", "Series")], (s, d)
s, d = call("GET", "/SharedWithMe/Search", tok["adult"], term="Spike Show One")  # an episode's title
assert s == 200 and d == [], (s, d)

step("a season or an episode: 409 NotShareableType for adult (who sees it), the fixed answer for kid1 (who does not)")
for item in (orig_eps[0]["Id"], orig_season):
    s, d = share("adult", item, "kid1")
    assert s == 409 and d["Refusal"] == "NotShareableType" and d["Message"].strip(), (s, d)
    s, d = share("kid1", item, "kid2")
    assert s == 409 and d == {"Refusal": "NotVisibleToSharer", "Message": "You can't share that title."}, (s, d)
assert [x for x in shares() if x["State"] != "Closed"] == [], shares()


def kid1_has_no_episode(numbers_absent, copy):
    """C1: no route kid1 has gives an episode with these numbers: the copy holds no item for it at all."""
    lib_view, season = copy.view, copy.seasons[0]["Id"]
    reads = {
        "Shows/Episodes": ok("kid1", f"/Shows/{copy.series['Id']}/Episodes")["Items"],
        "Items?parentId=<season>": ok("kid1", "/Items", parentId=season)["Items"],
        "Items recursive Episode": ok("kid1", "/Items", recursive="true", includeItemTypes="Episode")["Items"],
        "Latest ungrouped": ok("kid1", "/Items/Latest", parentId=lib_view, groupItems="false") or [],
        "NextUp?seriesId": ok("kid1", "/Shows/NextUp", seriesId=copy.series["Id"])["Items"],
        "search": ok("kid1", "/Items", recursive="true", searchTerm="Spike Show")["Items"],
    }
    for route, items in reads.items():
        got = [(i.get("SeriesName"), i.get("IndexNumber")) for i in items if i.get("Type") == "Episode"]
        assert not [g for g in got if g[1] in numbers_absent], ("kid1 reached an excluded episode", route, got)
    for n in numbers_absent:  # the original's id: nothing for kid1 (over its cap / blocked), with and without userId
        for q in ({"userId": ids["kid1"]}, {}):
            s, d = call("GET", "/Items", tok["kid1"], ids=ORIG_EP[n]["Id"], **q)
            assert s != 200 or not d["Items"], ("Items?ids= gave kid1 an excluded original episode", n, q, d)


step("C1: teen (cap 15) sees Spike Show but not E04 (own GB-18); teen shares it with kid1 -> kid1 gets E01-E02 only")
s, u = call("POST", "/Users/New", admin, body={"Name": "teen", "Password": "pw-teen"})
assert s == 200, (s, u)
ids["teen"] = u["Id"]
set_policy(ids["teen"], MaxParentalRating=15, MaxParentalSubRating=3)
tok["teen"], _ = login("teen", "pw-teen")
assert numbers(ok("teen", f"/Shows/{SS}/Episodes")["Items"]) == [(1, 1), (1, 2), (1, 5)], "teen's own view of Spike Show"
s, d = get("teen", f"/Items/{ORIG_EP[4]['Id']}")
assert s == 404, ("teen sees E04", s, d)
s, trec = share("teen", SS, "kid1")
assert s == 200 and trec["Kind"] == "Series", (s, trec)
wait(lambda: kid1_episodes() == [(1, 1), (1, 2)], "kid1 to see teen's copy with E01-E02", 90)
time.sleep(3)  # a later scan must not bring E04 or E05 in
c = Copy()
recipient_sees(c, [(1, 1), (1, 2)])
kid1_has_no_episode({4, 5}, c)
tlinks = run_remote(f"docker exec jf-share-spike sh -c \"cd '{SHARE_ROOT}/{N['kid1']}/shows/{trec['FolderName']}' && "
                    "find . -type l | sort\"\n", "swm-c1-teen-links.sh").splitlines()
assert tlinks == ["./Season 01/Spike Show S01E01.mp4", "./Season 01/Spike Show S01E02.mp4"], tlinks
s, d = call("DELETE", f"/SharedWithMe/Shares/{trec['Id']}", tok["teen"])
assert s == 204, (s, d)
wait(lambda: LIB_NAME not in [v["Name"] for v in views("kid1")], "teen's share library to go", 60)
s, d = call("DELETE", f"/Users/{ids['teen']}", admin)
assert s in (200, 204), (s, d)
TEEN_TAG = "swm-" + uuid.UUID(ids["teen"]).hex
del tok["teen"], ids["teen"]
wait(lambda: all(TEEN_TAG not in blocked(ids[n]) for n in ids), "teen's playlist tag to be lifted from everyone", 30)

step("adult shares Spike Show with kid1 -> 200")
s, rec = share("adult", SS, "kid1")
assert s == 200 and rec["Kind"] == "Series" and rec["State"] == "Active" and rec["FolderName"], (s, rec)
assert rec["OriginalItemId"] == uuid.UUID(SS).hex, (rec["OriginalItemId"], SS)
SERIES_SHARE = rec["Id"]

step("kid1 lists the series under 'Shared with kid1 (shows)', with Series 1 and S01E01, E02 and E04 (never E05), and plays E01")
wait(lambda: kid1_episodes() == [(1, 1), (1, 2), (1, 4)], "kid1 to see the copy and its three episodes", 90)
c = Copy()
recipient_sees(c, [(1, 1), (1, 2), (1, 4)])
kid1_has_no_episode({5}, c)
LIB = next(v for v in libs() if v["Name"] == LIB_NAME)
assert LIB["CollectionType"] == "tvshows" and LIB["LibraryOptions"]["MetadataSavers"] == [], LIB
LIB = LIB["ItemId"]
assert c.view.replace("-", "") == LIB, (c.view, LIB)
for item in (*c.seasons, *c.episodes):
    assert TAG in (item.get("Tags") or []) and item.get("CustomRating") == "12", ("each level carries its own tag", item)

# Admin acting on the copy: the refresh and item-update endpoints resolve the item as the caller, and the plugin never
# grants admin a share library (and admin blocks kid1's tag), so admin is granted both for each call and the plugin
# takes them back about a second later.
admin_folders = policy(admin_id)["EnabledFolders"]
admin_tags = blocked(admin_id)


def as_granted_admin(action):
    for _ in range(8):
        set_policy(admin_id, EnabledFolders=admin_folders + [LIB], BlockedTags=[t for t in admin_tags if t != TAG])
        res = action()
        if res[0] in (200, 204):
            return res
        time.sleep(0.5)
    raise AssertionError(("admin action kept losing the race to the heal", res))


def edit(item_id, **changes):
    def act():
        s, dto = call("GET", f"/Items/{item_id}", admin, userId=admin_id)
        if s != 200:
            return s, dto
        dto.update(changes)
        return call("POST", f"/Items/{item_id}", admin, body=dto)
    return act


def admin_settled():
    wait(lambda: sorted(policy(admin_id)["EnabledFolders"]) == sorted(admin_folders) and sorted(blocked(admin_id)) == sorted(admin_tags),
         "admin back to its own libraries and blocks", 15)

step("kid1 still gets 404 on the original series, season and episodes")
for i in (SS, orig_season, *(e["Id"] for e in orig_eps)):
    s, d = get("kid1", f"/Items/{i}")
    assert s == 404, ("kid1 sees the original", i, s, d)
s, d = get("kid1", f"/Shows/{SS}/Episodes")
assert s != 200 or not d["Items"], (s, d)

step("the share settles Active and the audit says visible now (series and an episode)")
s, d = call("POST", "/SharedWithMe/Audit/Sync", admin)
assert s in (200, 204), (s, d)


def settled():
    r = next(x for x in shares() if x["Id"] == SERIES_SHARE)
    return r["State"] == "Active" and r["FailedChecks"] == 0 and r["MissingChecks"] == 0 and r


wait(settled, "the series share to settle Active with no failed check", 60)
row = next(r for p in call("GET", "/SharedWithMe/Audit", admin)[1]["Profiles"] for r in p["Shares"] if r["Id"] == SERIES_SHARE)
assert row["VisibleNow"] and (row["Title"], row["Year"], row["OfficialRating"]) == ("Spike Show", 2010, "GB-15"), row

step("kid2, newbie, adult and admin are blind to the copy at series, season and episode level")
for n in OTHERS:
    blind(n, c, LIB)
    assert TAG in blocked(ids[n]), (n, blocked(ids[n]))

step("control: without kid2's block the series' episodes leak through Latest, Items?parentId=<season> and NextUp")
E1 = c.episodes[0]["Id"]
lifted_block_leaks("kid2", "Latest ungrouped",
                   lambda: E1 in [i["Id"] for i in get("kid2", "/Items/Latest", parentId=LIB, groupItems="false")[1] or []])
lifted_block_leaks("kid2", "Items?parentId=<season>",
                   lambda: (lambda s, d: s == 200 and E1 in [i["Id"] for i in d["Items"]])(*get("kid2", "/Items", parentId=c.seasons[0]["Id"])))
lifted_block_leaks("newbie", "NextUp?seriesId",
                   lambda: E1 in [i["Id"] for i in ok("newbie", "/Shows/NextUp", seriesId=c.series["Id"])["Items"]])
lifted_block_leaks("kid2", "Items?ids=<episode>",
                   lambda: E1 in [i["Id"] for i in ok("kid2", "/Items", ids=E1)["Items"]])
lifted_block_leaks("newbie", "Items?ids=<series,season,episodes> with userId",
                   lambda: E1 in [i["Id"] for i in ok("newbie", "/Items", ids=",".join(sorted(c.ids - {c.view})))["Items"]])
for n in OTHERS:
    blind(n, c, LIB)

step("the EPISODE's own tag blocks by itself: with the series' and the season's tags removed by hand, kid2 gets no episode")
# Simulates lost inheritance. The tags of the series item AND the season item are removed through the admin item API
# (the NFOs are untouched), so only the episodes' own tags remain. The series and season are then reachable for kid2
# (same cap, override 12), which is the simulated failure; the checks below are about episodes only.
EPS = [e["Id"] for e in c.episodes]
SEASON = c.seasons[0]["Id"]
READS = {
    "Latest ungrouped": lambda n: get(n, "/Items/Latest", parentId=LIB, groupItems="false"),
    "Items?parentId=<season>": lambda n: get(n, "/Items", parentId=SEASON),
    "Items?ids=<episodes>": lambda n: get(n, "/Items", ids=",".join(EPS)),
    "NextUp?seriesId": lambda n: get(n, "/Shows/NextUp", seriesId=c.series["Id"]),
}


def episode_leak(n, name):
    s, d = READS[name](n)
    return leaks(d, EPS) if s == 200 else []


def own_tag(item_id):
    return TAG in (ok("kid1", f"/Items/{item_id}").get("Tags") or [])


as_granted_admin(edit(c.series["Id"], Tags=[]))
as_granted_admin(edit(SEASON, Tags=[]))
admin_settled()
wait(lambda: not own_tag(c.series["Id"]) and not own_tag(SEASON), "the series and the season to lose their tag")
assert all(own_tag(e) for e in EPS), "the episodes kept their own tag"
mine = blocked(ids["kid2"])
for name in READS:
    assert not episode_leak("kid2", name), ("an episode leaked with only its own tag blocking", name, episode_leak("kid2", name))
    leaked = False
    for _ in range(8):  # control: lift kid2's block; this read then really does leak an episode
        set_policy(ids["kid2"], BlockedTags=[t for t in mine if t != TAG])
        if episode_leak("kid2", name):
            leaked = True
            break
        wait(lambda: TAG in blocked(ids["kid2"]), "the plugin to restore kid2's block", 10, 0.2)
    assert leaked, ("control: expected this read to leak an episode with kid2's block lifted", name)
    wait(lambda: TAG in blocked(ids["kid2"]), "the plugin to restore kid2's block", 10, 0.2)
    assert sorted(blocked(ids["kid2"])) == sorted(mine), blocked(ids["kid2"])
    assert not episode_leak("kid2", name), ("an episode leaked once kid2's block was back", name)
    # the hand edits must still hold, or the read above did not test the episode's own tag alone
    assert not own_tag(c.series["Id"]) and not own_tag(SEASON), ("a hand-removed tag came back during the control", name)
    print(f"    {name}: empty with the block, leaks without it, empty again")
as_granted_admin(edit(c.series["Id"], Tags=[TAG]))
as_granted_admin(edit(SEASON, Tags=[TAG]))
admin_settled()
wait(lambda: own_tag(c.series["Id"]) and own_tag(SEASON), "the series and season tags to be back")
c = Copy()
recipient_sees(c, [(1, 1), (1, 2), (1, 4)])
for n in OTHERS:
    blind(n, c, LIB)

step("kid1 watches E01 on the copy: the adult's original E01 stays unwatched (watch state is separate, a documented cost)")
s, d = call("POST", f"/UserPlayedItems/{E1}", tok["kid1"], userId=ids["kid1"])
assert s == 200, (s, d)
assert ok("kid1", f"/Items/{E1}")["UserData"]["Played"] is True
assert ok("adult", f"/Items/{orig_eps[0]['Id']}")["UserData"]["Played"] is False
nu = ok("kid1", "/Shows/NextUp")["Items"]
assert [(i["SeriesId"], i["IndexNumber"]) for i in nu] == [(c.series["Id"], 2)], nu  # kid1's Next Up is the copy's E02
assert ok("adult", "/Shows/NextUp")["Items"] == [], "the adult's Next Up picked up kid1's watching"
for n in ("kid2", "newbie"):
    assert not leaks(ok(n, "/Shows/NextUp"), c.ids), n

step("S01E03 appears in the original: after a scan the sync links it and kid1 sees three episodes; the others stay blind")
run_remote("docker exec jf-share-spike /usr/lib/jellyfin-ffmpeg/ffmpeg -loglevel error -y "
           "-f lavfi -i testsrc=duration=3:size=320x240:rate=25 -f lavfi -i sine=frequency=770:duration=3 "
           f"-c:v libx264 -pix_fmt yuv420p -c:a aac -shortest '{episode_file(3)}'\n", "swm-t9-e03.sh")
scan(admin)
wait(lambda: kid1_episodes() == [(1, 1), (1, 2), (1, 3), (1, 4)], "kid1 to see S01E03 on the copy", 120)
c = Copy()
recipient_sees(c, [(1, 1), (1, 2), (1, 3), (1, 4)])
# the copy was rebuilt in place (same paths): E01 keeps its id and kid1's watch state
assert c.episodes[0]["Id"] == E1 and ok("kid1", f"/Items/{E1}")["UserData"]["Played"] is True, c.episodes[0]
for n in OTHERS:
    blind(n, c, LIB)

step("S01E01's file is removed from the original: after a scan kid1 sees E02-E04 only")
run_remote(f"docker exec jf-share-spike rm -f '{episode_file(1)}' '{SHOW_DIR}/Spike Show S01E01.nfo'\n", "swm-t9-rm-e01.sh")
scan(admin)
wait(lambda: kid1_episodes() == [(1, 2), (1, 3), (1, 4)], "kid1 to lose S01E01 on the copy", 120)
c = Copy()
recipient_sees(c, [(1, 2), (1, 3), (1, 4)])
# (compared by file name: the original's E03 has no NFO; on the final runs the server numbered it from its file name)
assert sorted(e["Path"].rsplit("/", 1)[1] for e in ok("adult", f"/Shows/{SS}/Episodes", fields="Path")["Items"]) == [
    "Spike Show S01E02.mp4", "Spike Show S01E03.mp4", "Spike Show S01E04.mp4", "Spike Show S01E05.mp4"]
link_list = run_remote(f"docker exec jf-share-spike sh -c \"cd '{SHARE_ROOT}/{N['kid1']}/shows/{rec['FolderName']}' && "
                       "find . -type l | sort\"\n", "swm-t9-links.sh").splitlines()
assert link_list == ["./Season 01/Spike Show S01E02.mp4", "./Season 01/Spike Show S01E03.mp4", "./Season 01/Spike Show S01E04.mp4"], link_list
for n in OTHERS:
    blind(n, c, LIB)

step("ReplaceAllMetadata refresh of the share library: visibility, rating, tags and numbers intact at every level")
# Markers prove the refresh reached the series and an episode: both names are tampered through the admin API and only a
# completed replace-all puts them back from tvshow.nfo and the episode's NFO <title>. (The episode's tags are locked, so
# a junk tag could not serve as the marker.)
E2 = c.episodes[0]["Id"]
as_granted_admin(edit(c.series["Id"], Name="Tampered Show"))
as_granted_admin(edit(E2, Name="Tampered Episode"))
wait(lambda: ok("kid1", f"/Items/{c.series['Id']}")["Name"] == "Tampered Show"
     and ok("kid1", f"/Items/{E2}")["Name"] == "Tampered Episode", "both markers set")
as_granted_admin(lambda: call("POST", f"/Items/{LIB}/Refresh", admin, metadataRefreshMode="FullRefresh",
                              imageRefreshMode="FullRefresh", replaceAllMetadata="true", replaceAllImages="true"))
wait(lambda: ok("kid1", f"/Items/{c.series['Id']}")["Name"] == "Spike Show"
     and ok("kid1", f"/Items/{E2}")["Name"] == "Spike Show S01E02", "the replace-all refresh to reach series and episode", 120)
admin_settled()
time.sleep(2)
c = Copy()
recipient_sees(c, [(1, 2), (1, 3), (1, 4)])
for n in OTHERS:
    blind(n, c, LIB)

step("a movie share and a series share for kid1 coexist in two libraries; removing the movie share leaves the series")
E18 = next(m for m in movies(admin, admin_id) if m["Name"] == "Spike Eighteen")["Id"]
s, mrec = share("adult", E18, "kid1")
assert s == 200 and mrec["Kind"] == "Movie", (s, mrec)
wait(lambda: any(m["Name"] == "Spike Eighteen" for m in movies(tok["kid1"], ids["kid1"])), "kid1's movie copy", 90)
# the Playlists view is there exactly when kid1 owns a plugin "Share with" playlist (Task 11)
pl_view = ["Playlists"] if owns_plugin_playlist(tok["kid1"], ids["kid1"]) else []
assert sorted(v["Name"] for v in views("kid1")) == sorted(["Movies", "Shared with kid1", LIB_NAME, "Shows"] + pl_view), [v["Name"] for v in views("kid1")]
s, d = call("DELETE", f"/SharedWithMe/Shares/{mrec['Id']}", tok["adult"])
assert s == 204, (s, d)
wait(lambda: "Shared with kid1" not in [v["Name"] for v in views("kid1")], "the movie share library to go", 60)
assert not any(v["Name"] == "Shared with kid1" for v in libs())
assert not any(m["Name"] == "Spike Eighteen" for m in movies(tok["kid1"], ids["kid1"]))
c = Copy()
recipient_sees(c, [(1, 2), (1, 3), (1, 4)])
for n in OTHERS:
    assert TAG in blocked(ids[n]), ("kid1's tag dropped while its series share remains", n, blocked(ids[n]))
    blind(n, c, LIB)

step("C1: adult (uncapped) shares Spike Show with kid2 (no gore block) -> kid2 gets E04 rated GB-18 (override 12) and E05")
s, k2rec = share("adult", SS, "kid2")
assert s == 200, (s, k2rec)
K2_LIB = "Shared with kid2 (shows)"


def kid2_copy_episodes():
    view = [v for v in views("kid2") if v["Name"] == K2_LIB]
    if not view:
        return None
    items = ok("kid2", "/Items", parentId=view[0]["Id"])["Items"]
    if not items:
        return None
    return ok("kid2", f"/Shows/{items[0]['Id']}/Episodes", fields="OfficialRating,CustomRating,Tags")["Items"]


wait(lambda: (kid2_copy_episodes() or None) and numbers(kid2_copy_episodes()) == [(1, 2), (1, 3), (1, 4), (1, 5)],
     "kid2 to see E02-E05 on its copy", 90)
k2eps = {e["IndexNumber"]: e for e in kid2_copy_episodes()}
assert (k2eps[4].get("OfficialRating"), k2eps[4].get("CustomRating")) == ("GB-18", "12"), k2eps[4]
assert (k2eps[5].get("OfficialRating"), k2eps[5].get("CustomRating")) == ("GB-15", "12"), k2eps[5]
assert ("swm-" + N["kid2"]) in (k2eps[4].get("Tags") or []), k2eps[4]
s, d = call("POST", f"/Items/{k2eps[4]['Id']}/PlaybackInfo", tok["kid2"], body={}, userId=ids["kid2"])
assert s == 200 and d["MediaSources"], ("kid2 plays its E04", s, d)
s, d = call("DELETE", f"/SharedWithMe/Shares/{k2rec['Id']}", tok["adult"])
assert s == 204, (s, d)
wait(lambda: K2_LIB not in [v["Name"] for v in views("kid2")], "kid2's series library to go", 60)

step("removing the series share leaves kid1 nothing: library gone, folder gone, no swm- tag anywhere")
s, d = call("DELETE", f"/SharedWithMe/Shares/{SERIES_SHARE}", tok["adult"])
assert s == 204, (s, d)
wait(lambda: LIB_NAME not in [v["Name"] for v in views("kid1")], "kid1's series library to go", 60)
assert not any(v["Name"] == LIB_NAME or v["ItemId"] == LIB for v in libs())
for i in c.ids:
    s, d = get("kid1", f"/Items/{i}")
    assert s == 404, ("kid1 still reaches the copy", i, s, d)
assert [s["Name"] for s in series(tok["kid1"], ids["kid1"])] == ["Spike Kids"], series(tok["kid1"], ids["kid1"])
want_views = ["Movies", "Shows"] + (["Playlists"] if owns_plugin_playlist(tok["kid1"], ids["kid1"]) else [])
assert sorted(v["Name"] for v in views("kid1")) == sorted(want_views), [v["Name"] for v in views("kid1")]
assert [x for x in shares() if x["State"] != "Closed"] == [], shares()
owners = tag_owners(admin, {ids[n]: tok[n] for n in tok})  # ruling B2: playlist owners' tags remain, exactly
assert N["kid1"] not in owners or owns_plugin_playlist(tok["kid1"], ids["kid1"]), owners
for n in ("kid1", *OTHERS):
    assert plugin_tags(blocked(ids[n])) == expected_swm(owners, ids[n]), (n, blocked(ids[n]), expected_swm(owners, ids[n]))
left = run_remote(f"docker exec jf-share-spike sh -c \"ls -A '{SHARE_ROOT}/{N['kid1']}/shows' 2>/dev/null || true\"\n", "swm-t9-left.sh")
assert left.strip() == "", ("the series copy is still on disk", left)

print("PASS: test_share_series")
sys.exit(0)
