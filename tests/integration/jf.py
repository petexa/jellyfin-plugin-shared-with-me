import json, urllib.request, urllib.parse, time, os
B = os.environ.get("SWM_JF_URL", "http://localhost:8097").rstrip("/")  # the disposable test server
STATE = os.path.join(os.path.dirname(__file__), "state.json")
def hdr(tok=None):
    a = 'MediaBrowser Client="spike", Device="spike", DeviceId="spike-1", Version="1"'
    if tok: a += f', Token="{tok}"'
    return {"Authorization": a, "Content-Type": "application/json"}
def call(method, path, tok=None, body=None, raw=False, **q):
    url = B + path + ("?" + urllib.parse.urlencode(q, doseq=True) if q else "")
    data = json.dumps(body).encode() if body is not None else (b"" if method == "POST" else None)
    req = urllib.request.Request(url, data=data, method=method, headers=hdr(tok))
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            b = r.read()
            if raw: return r.status, len(b)
            if not b: return r.status, None
            try:
                return r.status, json.loads(b)
            except ValueError:  # an HTML or JS page
                return r.status, b.decode(errors="replace")
    except urllib.error.HTTPError as e:
        b = e.read()
        try:
            return e.code, json.loads(b)
        except ValueError:
            return e.code, b[:300].decode(errors="replace")
def login(name, pw):
    s, d = call("POST", "/Users/AuthenticateByName", body={"Username": name, "Pw": pw})
    assert s == 200, (s, d)
    return d["AccessToken"], d["User"]["Id"]
def load(): return json.load(open(STATE)) if os.path.exists(STATE) else {}
def save(d): json.dump(d, open(STATE, "w"), indent=1)
def scan(tok):
    s, d = call("POST", "/Library/Refresh", tok)
    assert s in (200, 204), ("library refresh", s, d)
    time.sleep(3)
    for _ in range(60):
        s, t = call("GET", "/ScheduledTasks", tok)
        assert s == 200, ("scheduled tasks", s, t)
        if not any(x["State"] != "Idle" and "Scan" in x["Name"] for x in t): return
        time.sleep(2)
    raise TimeoutError("library scan still running after 120 s")
def movies(tok, uid):
    s, d = call("GET", "/Items", tok, userId=uid, recursive="true", includeItemTypes="Movie",
                fields="Path,OfficialRating,CustomRating,ParentId")
    return d["Items"] if s == 200 else (s, d)
def series(tok, uid):
    s, d = call("GET", "/Items", tok, userId=uid, recursive="true", includeItemTypes="Series",
                fields="Path,OfficialRating,CustomRating,ParentId,Tags")
    return d["Items"] if s == 200 else (s, d)
def show(label, items):
    print(label)
    if isinstance(items, tuple): print("   ", items); return
    for i in items: print(f"    {i['Name']:16} off={i.get('OfficialRating')} cust={i.get('CustomRating')} {i['Id'][:8]} {i.get('Path')}")
    if not items: print("    (none)")
def set_tags(tok, uid, item_id, tags):
    """Replace an item's own tags through the normal item-update API (admin token)."""
    s, dto = call("GET", f"/Items/{item_id}", tok, userId=uid)
    assert s == 200, ("get item for tags", item_id, s, dto)
    dto["Tags"] = list(tags)
    s, d = call("POST", f"/Items/{item_id}", tok, body=dto)
    assert s in (200, 204), ("set tags", item_id, s, d)
def run_remote(script_text, name="swm-remote.sh"):
    """Run a bash script on the machine that holds the test server's folders (/opt/jf-share-spike/...).

    SWM_RUN is the command that runs a script read from its standard input on that machine. The default, `bash -s`,
    is this machine; for a remote Docker host use for example SWM_RUN='ssh me@docker-host bash -s'.
    The script is copied to a file there, run from it with stdin closed, and the file removed afterwards, each over
    its own connection: some tests depend on the pause that gives between the script's last change and whatever the
    test does next (the server's directory cache must not still hold the old listing).
    """
    import shlex, subprocess, uuid
    cmd = shlex.split(os.environ.get("SWM_RUN", "bash -s"))
    path = f"/tmp/swm-{uuid.uuid4().hex[:12]}-{name}"

    def run(text, check=True):
        r = subprocess.run(cmd, input=text, capture_output=True, text=True)
        if check and r.returncode != 0:
            raise RuntimeError(f"remote script {name} failed ({r.returncode}): {r.stdout}{r.stderr}")
        return r

    try:
        run(f"cat > '{path}' <<'SWM_SCRIPT_END'\n#!/usr/bin/env bash\nset -euo pipefail\n{script_text}"
            + ("" if script_text.endswith("\n") else "\n") + "SWM_SCRIPT_END\n")
        return run(f"bash '{path}' </dev/null\n").stdout
    finally:
        run(f"rm -f '{path}'\n", check=False)
# Default share root inside the spike container (<data path>/shared-with-me) and its host path.
SHARE_ROOT = "/config/data/shared-with-me"
SHARE_ROOT_HOST = "/opt/jf-share-spike/config/data/shared-with-me"
# A real library whose path only shares a prefix with the share root (must NOT count as a share library).
SIBLING = "/config/data/shared-with-me-other"
SIBLING_HOST = "/opt/jf-share-spike/config/data/shared-with-me-other"
STORE_HOST = "/opt/jf-share-spike/config/plugins/configurations/SharedWithMe"
def under(p, root):
    p = p.rstrip("/"); root = root.rstrip("/")
    return p == root or p.startswith(root + "/")
def assert_settled(admin_tok, user_ids, label, quiet=6):
    """The plugin has stopped writing: over `quiet` seconds its policy-write and playlist-write counters
    (GET /SharedWithMe/Diagnostics) do not move and no user's BlockedTags/EnabledFolders change."""
    def diag():
        s, d = call("GET", "/SharedWithMe/Diagnostics", admin_tok)
        assert s == 200, (label, "diagnostics", s, d)
        return d
    def snap():
        out = {}
        for uid in user_ids:
            s, u = call("GET", f"/Users/{uid}", admin_tok)
            assert s == 200, (label, s, u)
            p = u["Policy"]
            out[uid] = (p["EnableAllFolders"], sorted(p.get("BlockedTags") or []), sorted(p.get("EnabledFolders") or []))
        return out
    time.sleep(2)  # let the echo of the last write run (isolation debounce is 50 ms)
    d0, p0 = diag(), snap()
    time.sleep(quiet)
    d1, p1 = diag(), snap()
    assert d1["PolicyWrites"] == d0["PolicyWrites"], (label, "the plugin kept writing policies", d0, d1)
    assert d1["PlaylistWrites"] == d0["PlaylistWrites"], (label, "the plugin kept writing playlists", d0, d1)
    assert p0 == p1, (label, "policies kept changing", p0, p1)
    return d1

# ---- the one tag rule (Task 11, ruling B2) ----
import re as _re
PLAYLIST_FOLDER = _re.compile(r"/swm-[0-9a-f]{32}$")  # a plugin "Share with" playlist's folder (random name)
PLUGIN_TAG = _re.compile(r"^swm-[0-9a-fA-F]{32}$")


def is_plugin_tag(t):
    return bool(PLUGIN_TAG.match(t))


def owns_plugin_playlist(token, uid):
    s, d = call("GET", "/Items", token, userId=uid, includeItemTypes="Playlist", recursive="true", fields="Path")
    assert s == 200, ("playlists", uid, s, d)
    return any(PLAYLIST_FOLDER.search(p.get("Path") or "") for p in d["Items"])


def tag_owners(admin_tok, tokens):
    """Users every other user must block the swm- tag of: a share library on the server (its folder names them) or a
    plugin playlist (tokens: {user id: token} of every user on the server)."""
    users = {uid.replace("-", "").lower() for uid in tokens}
    owners = {uid.replace("-", "").lower() for uid, t in tokens.items() if owns_plugin_playlist(t, uid)}
    s, folders = call("GET", "/Library/VirtualFolders", admin_tok)
    assert s == 200, (s, folders)
    for vf in folders:
        for loc in vf.get("Locations") or []:
            m = _re.match(_re.escape(SHARE_ROOT) + r"/([0-9a-f]{32})/(movies|shows)/?$", loc)
            if m and m.group(1) in users:  # a share library is a recipient's; an orphan share-layout folder is nobody's
                owners.add(m.group(1))
    return owners


def expected_swm(owners, uid):
    """The exact swm- tags user uid must block under the one rule: every other tag owner's, never its own."""
    me = uid.replace("-", "").lower()
    return sorted("swm-" + o for o in owners if o != me)


def plugin_tags(tags):
    return sorted(t.lower() for t in (tags or []) if is_plugin_tag(t))
