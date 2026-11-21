"""Task 1: the plugin is installed and Active on the spike server."""
import sys
from jf import call, login

tok, _ = login("admin", "spike-admin")
status, plugins = call("GET", "/Plugins", tok)
assert status == 200, (status, plugins)
mine = [p for p in plugins if p["Name"] == "Shared With Me"]
assert mine, "plugin 'Shared With Me' not listed; have: %s" % [p["Name"] for p in plugins]
assert mine[0]["Status"] == "Active", mine[0]
print("PASS: Shared With Me is Active, version", mine[0]["Version"])
sys.exit(0)
