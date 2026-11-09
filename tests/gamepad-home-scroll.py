"""
Controller scrolling on a phone home screen with more icons than fit.

Drives the home screen with a virtual gamepad (`sideloadpad`, Debug builds only) and reads where the selected
icon ends up. First with Sideload's repair switched off, which must show the reported bug (the selection leaves
the visible grid and its frame draws outside the phone screen), then with it on, where the grid must follow the
selection. A screenshot is taken in each state. Needs a running Debug build of Sideload with S1API and enough
phone apps that the home screen scrolls, in a loaded save:

    pwsh -File Workspace/tools/session/session.ps1 -Build _wt-Sideload-<topic> -Mods S1API,Reflash
    python -I tests/gamepad-home-scroll.py --slot mptest

Reports PASS or FAIL per check and exits 1 when one failed.
"""
import argparse
import os
import re
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "Workspace", "tools", "bridge"))

import s1  # noqa: E402

failed = 0


def check(ok, what):
    global failed
    print(("PASS " if ok else "FAIL ") + what)
    if not ok:
        failed += 1


def lines_of(answer):
    result = answer.get("result") or {}
    return [l if isinstance(l, str) else l.get("text", str(l)) for l in result.get("lines") or []]


def press(game, verb, count):
    """Press and return the dump line about the selected icon (the press coroutine dumps when it is done)."""
    answer = game.run("sideloadpad %s %d" % (verb, count), until="sideloadpad dump: selected", max_wait_ms=20000)
    found = [l for l in lines_of(answer) if "sideloadpad dump:" in l]
    for l in found:
        print("    " + l.split("sideloadpad dump:", 1)[1].strip())
    selected = [l for l in found if "selected icon" in l]
    return selected[-1] if selected else ""


def shot(game, out, name):
    path = game.shot()
    if path and os.path.exists(path):
        target = os.path.join(out, name)
        shutil.copyfile(path, target)
        print("    screenshot " + target)
        return target
    print("    screenshot missing")
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--slot", required=True)
    ap.add_argument("--presses", type=int, default=8, help="d-pad downs, enough to pass the visible rows")
    ap.add_argument("--out", default=os.path.join(HERE, "_out"))
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)

    game = s1.Game(a.slot)
    game.run("sideloadpad open", max_wait_ms=3000)

    first = press(game, "up", 1)
    check("selected icon" in first, "a home icon is selected after a controller press")
    icons = 0
    for l in lines_of(game.run("sideloadpad dump", until="sideloadpad dump: icons", max_wait_ms=5000)):
        m = re.search(r"icons (\d+),.*scroll (\S+)", l)
        if m:
            icons = int(m.group(1))
            check(m.group(2) != "none", "the home grid sits in a scroll view (%s, %d icons)" % (m.group(2), icons))

    # Before: the repair off reproduces the report.
    game.run("sideloadpad fix off", max_wait_ms=3000)
    press(game, "up", a.presses)
    before = press(game, "down", a.presses)
    check("OUTSIDE the frame" in before and "draws outside the phone screen" in before,
          "without the repair the selection leaves the grid and its frame draws outside (bug reproduced)")
    shot(game, a.out, "gamepad-home-before.png")

    # After: the repair on keeps the selection in view and its frame clipped.
    game.run("sideloadpad fix on", max_wait_ms=3000)
    press(game, "up", a.presses)
    after = press(game, "down", a.presses)
    check("inside the frame" in after, "with the repair the grid scrolls to the selected icon")
    check("maskable=True" in after, "with the repair the selected icon's frame is clipped by the grid mask")
    m = re.search(r"normPos ([0-9.]+)", " ".join(lines_of(game.run("sideloadpad dump", until="sideloadpad dump: icons", max_wait_ms=5000))))
    check(m is not None and float(m.group(1)) < 0.5, "the grid scrolled down (normPos %s)" % (m.group(1) if m else "?"))
    shot(game, a.out, "gamepad-home-after.png")

    back = press(game, "up", a.presses)
    check("inside the frame" in back, "scrolling back up keeps the selection in view")

    game.run("sideloadpad off", max_wait_ms=3000)
    print("%d check(s) failed" % failed if failed else "all checks passed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
