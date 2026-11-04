"""
Per-side border colours, checked on screen.

Serves tests/border-side-colors/index.html as an override of the self-test app, opens it on the phone, takes a
screenshot and reads each box's four borders back out of the pixels. Reports PASS or FAIL per check and exits 1
when one failed. Needs a running Debug build of Sideload in a loaded save:

    pwsh -File Workspace/tools/session/session.ps1 -Build _wt-Sideload-<topic> -NoApp
    python -I tests/border-side-colors.py --slot mptest

The override folder is removed again at the end, because a leftover one outranks every later build of the page.
"""
import argparse
import os
import shutil
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "Workspace", "tools", "bridge"))

import s1  # noqa: E402
from PIL import Image  # noqa: E402

APP = "sideload-selftest"
PAGE = os.path.join(HERE, "border-side-colors")

# Fill colour of each box, as the page declares it.
FILLS = {
    "case1": (0, 0, 255),
    "case2": (0, 0, 128),
    "thick": (255, 255, 0),
    "round": (0, 255, 255),
    "script": (128, 0, 255),
    "pair": (255, 0, 255),
}

failed = 0


def check(name, ok, detail=""):
    global failed
    print(("PASS  " if ok else "FAIL  ") + name + ("" if ok else " - " + detail))
    if not ok:
        failed += 1


def kind(px):
    """'red', 'green' or None. Dominance rather than exact values, so the phone's own colour handling cannot
    turn a pass into a fail."""
    r, g, b = px[:3]
    if r > g + 50 and r > b + 50:
        return "red"
    if g > r + 50 and g > b + 50:
        return "green"
    return None


def find_fill(img, colour, tolerance=45):
    """The bounding box of the densest block of pixels in this colour, or None."""
    w, h = img.size
    px = img.load()
    rows, cols = {}, {}
    for y in range(h):
        for x in range(w):
            p = px[x, y]
            if all(abs(p[i] - colour[i]) <= tolerance for i in range(3)):
                rows[y] = rows.get(y, 0) + 1
                cols[x] = cols.get(x, 0) + 1
    if not rows:
        return None
    # Stray pixels of a similar colour elsewhere on screen are dropped by asking for real density.
    row_peak, col_peak = max(rows.values()), max(cols.values())
    ys = [y for y, n in rows.items() if n >= row_peak * 0.5]
    xs = [x for x, n in cols.items() if n >= col_peak * 0.5]
    return min(xs), min(ys), max(xs), max(ys)


def band(img, box, side, depth=10, span=(0.2, 0.8)):
    """Counts of red and green pixels in a strip just outside one side of the fill, over the middle of that side."""
    x0, y0, x1, y1 = box
    px = img.load()
    counts = {"red": 0, "green": 0, None: 0}
    if side in ("top", "bottom"):
        xa, xb = int(x0 + (x1 - x0) * span[0]), int(x0 + (x1 - x0) * span[1])
        ys = range(y0 - depth, y0) if side == "top" else range(y1 + 1, y1 + 1 + depth)
        for y in ys:
            for x in range(xa, xb):
                counts[kind(px[x, y])] += 1
    else:
        ya, yb = int(y0 + (y1 - y0) * span[0]), int(y0 + (y1 - y0) * span[1])
        xs = range(x0 - depth, x0) if side == "left" else range(x1 + 1, x1 + 1 + depth)
        for x in xs:
            for y in range(ya, yb):
                counts[kind(px[x, y])] += 1
    return counts


def expect(img, box, name, sides, spans=None):
    """sides: side -> 'red', 'green' or None (no border). spans: side -> the part of that side to read, when it
    is not the default middle 60 %."""
    for side, want in sides.items():
        c = band(img, box, side, span=(spans or {}).get(side, (0.2, 0.8)))
        if want is None:
            ok = c["red"] == 0 and c["green"] == 0
        else:
            other = "green" if want == "red" else "red"
            ok = c[want] > 0 and c[other] == 0
        check(f"{name}: {side} is {want or 'empty'}", ok, f"red {c['red']}, green {c['green']}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--slot", required=True)
    parser.add_argument("--keep", action="store_true", help="leave the override folder in the slot")
    args = parser.parse_args()

    game = s1.Game(args.slot)
    override = os.path.join(game.root, "Mods", APP)
    os.makedirs(override, exist_ok=True)
    shutil.copy(os.path.join(PAGE, "index.html"), os.path.join(override, "index.html"))
    print(f"[test] page copied to {override}")

    try:
        opened = game.call("sideload_open_app", {"appId": APP, "open": True}, timeout_ms=8000)
        check("self-test app opens", bool(opened.get("ok")), str(opened))
        reloaded = game.call("sideload_reload", {"appId": APP})
        print(f"[test] reload: {reloaded.get('ok')}")

        # The page recolours #script itself 0.3 s after it starts, through a 0.2 s transition. Two seconds
        # cover the reload, that timer and the tween with frames to spare. The page's mount is proven by the
        # fills being found below, so this needs no sideload_eval, which older bridge builds lack.
        time.sleep(2.0)
        path = game.shot()
        print(f"[test] screenshot {path}")
        img = Image.open(path).convert("RGB")

        boxes = {}
        for name, colour in FILLS.items():
            boxes[name] = find_fill(img, colour)
            check(f"{name}: fill found on screen", boxes[name] is not None, f"no pixels near {colour}")
            if boxes[name]:
                print(f"[test]   {name} fill at {boxes[name]}")

        if boxes["case1"]:
            expect(img, boxes["case1"], "case1 (.a.b border-top-color)",
                   {"top": "green", "right": None, "bottom": None, "left": None})
        if boxes["case2"]:
            expect(img, boxes["case2"], "case2 (.c.d border-top shorthand)",
                   {"top": "green", "right": None, "bottom": None, "left": None})
        if boxes["thick"]:
            expect(img, boxes["thick"], "thick (6px, green top)",
                   {"top": "green", "right": "red", "bottom": "red", "left": "red"})
        if boxes["round"]:
            # Equal widths on a rounded box keep the ring in the shared colour, and the odd side is laid over it
            # between the corner arcs only. On a 34 px box with a 12 px radius that straight part is the middle
            # fifth of the side; the arcs above and below it stay red by design.
            expect(img, boxes["round"], "round (radius 12, green left)",
                   {"top": "red", "right": "red", "bottom": "red", "left": "green"},
                   spans={"left": (0.4, 0.6)})
        if boxes["script"]:
            expect(img, boxes["script"], "script (style.borderTopColor)",
                   {"top": "green", "right": "red", "bottom": "red", "left": "red"})
        if boxes["pair"]:
            expect(img, boxes["pair"], "pair (border-color: red green)",
                   {"top": "red", "right": "green", "bottom": "red", "left": "green"})

        crop = os.path.join(os.path.dirname(path), "border-side-colors-crop.png")
        found = [b for b in boxes.values() if b]
        if found:
            pad = 40
            area = (min(b[0] for b in found) - pad, min(b[1] for b in found) - pad,
                    max(b[2] for b in found) + pad, max(b[3] for b in found) + pad)
            img.crop(area).save(crop)
            print(f"[test] crop {crop}")
    finally:
        if not args.keep:
            shutil.rmtree(override, ignore_errors=True)
            print(f"[test] override removed: {not os.path.exists(override)}")

    if failed:
        print(f"{failed} check(s) failed.")
        return 1
    print("all checks passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
