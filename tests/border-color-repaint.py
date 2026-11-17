"""
A border colour changed after the first paint, and a grey border on a canvas that converts colours itself.

Serves tests/border-color-repaint/index.html as an override of the self-test app, opens it on the phone, waits for
the page's script to change its borders, takes a screenshot and reads each box's borders back out of the pixels.
Reports PASS or FAIL per check and exits 1 when one failed. Needs a running Debug build of Sideload in a loaded
save:

    pwsh -File Workspace/tools/session/session.ps1 -Build _wt-Sideload-<topic> -NoApp
    python -I tests/border-color-repaint.py --slot mptest

The repaint routes, one red box each turned green: `style.borderColor`, `style.setProperty('border-color')`,
`classList.add` of a class that sets `border-color`, the `border` shorthand written inline, and `style.borderColor`
on a square box over a transparent fill, which the painter draws as four strips instead of the ring.

The grey boxes are Clipwise's tile: a 160 outline, 90 when selected. Their brightness on screen is held against
those numbers, because a ring the canvas never linearised shows lighter than declared (160 arrives as about 207,
90 as about 160) and reads exactly like a selection that kept the old colour.

Every check runs twice: on the phone canvas as the game sets it up, which hands vertex colours through, and with
`sideloadgamma canvas convert`, which makes it convert them itself like every overlay and surface canvas a mod
creates. An overlay is not in the screenshot, so the phone stands in for it.

The override folder is removed and the canvas restored at the end, because a leftover override outranks every
later build of the page.
"""
import argparse
import importlib.util
import os
import shutil
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "Workspace", "tools", "bridge"))

import s1  # noqa: E402
from PIL import Image  # noqa: E402

# The pixel readers live in the per-side colour check; one copy of them, not two.
_spec = importlib.util.spec_from_file_location("border_side_colors", os.path.join(HERE, "border-side-colors.py"))
bsc = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(bsc)

APP = "sideload-selftest"
PAGE = os.path.join(HERE, "border-color-repaint")

# Fill colour of each box, as the page declares it, and what was written to turn its border green.
FILLS = {
    "static": ((0, 0, 128), "class at build time (control)"),
    "inline": ((0, 0, 255), "style.borderColor"),
    "setprop": ((255, 128, 255), "style.setProperty"),
    "klass": ((0, 255, 255), "classList.add"),
    "shorthand": ((255, 0, 255), "style.border"),
    "strips": ((128, 0, 255), "style.borderColor, four strips"),
}

GREEN = {"top": "green", "right": "green", "bottom": "green", "left": "green"}

# Clipwise's tile in its own greys: fill colour, the outline brightness it declares, and how it got there.
GREYS = {
    "greyoff": ((255, 0, 128), 160, "#a0a0a0 outline, unselected"),
    "greyon": ((0, 128, 255), 90, "#5a5a5a outline, selected at build time"),
    "greyscript": ((128, 255, 0), 90, "#5a5a5a outline, selected by classList.add"),
}

# Antialiasing at the strip's edge pulls the mean a little toward the fill and the background. The unconverted
# ring lands 45 to 70 above the declared value, well outside this.
GREY_TOLERANCE = 20

MODES = ("keep", "convert")


def outline_brightness(img, box, depth=3):
    """Mean brightness of the grey pixels in a thin strip just above and below the fill, over the middle 60 %."""
    x0, y0, x1, y1 = box
    px = img.load()
    values = []
    for y in list(range(y0 - depth, y0)) + list(range(y1 + 1, y1 + 1 + depth)):
        for x in range(int(x0 + (x1 - x0) * 0.2), int(x0 + (x1 - x0) * 0.8)):
            r, g, b = px[x, y]
            # Grey, and not the page's black behind the rounded corners and the antialiased edge.
            if max(r, g, b) - min(r, g, b) <= 24 and max(r, g, b) >= 40:
                values.append((r + g + b) / 3)
    return sum(values) / len(values) if values else None


def set_canvas(game, how):
    answer = game.run(f"sideloadgamma canvas {how}", until="sideloadgamma canvas", max_wait_ms=5000)
    lines = [str(l) for l in (answer.get("result", {}) or {}).get("lines", []) if "sideloadgamma" in str(l)]
    for line in lines:
        print(f"[test]   {line}")
    return any("vertexGamma=" in l for l in lines)


def check_screen(game, mode):
    reloaded = game.call("sideload_reload", {"appId": APP})
    print(f"[test] [{mode}] reload: {reloaded.get('ok')}")

    # The page writes its colours 0.3 s after it starts. Two seconds cover the reload, that timer and the
    # repaint with frames to spare.
    time.sleep(2.0)
    path = game.shot()
    print(f"[test] [{mode}] screenshot {path}")
    img = Image.open(path).convert("RGB")

    boxes = {}
    for name, (colour, route) in FILLS.items():
        boxes[name] = bsc.find_fill(img, colour)
        bsc.check(f"[{mode}] {name}: fill found on screen", boxes[name] is not None, f"no pixels near {colour}")
        if boxes[name]:
            print(f"[test]   {name} fill at {boxes[name]}")
            # Rounded boxes are read over the straight middle of each side, clear of the corner arcs.
            bsc.expect(img, boxes[name], f"[{mode}] {name} ({route})", GREEN)

    for name, (colour, declared, route) in GREYS.items():
        boxes[name] = bsc.find_fill(img, colour)
        bsc.check(f"[{mode}] {name}: fill found on screen", boxes[name] is not None, f"no pixels near {colour}")
        if not boxes[name]:
            continue
        seen = outline_brightness(img, boxes[name])
        print(f"[test]   {name} ({route}) fill at {boxes[name]}, outline brightness {seen}")
        bsc.check(f"[{mode}] {name}: outline shows {declared}",
                  seen is not None and abs(seen - declared) <= GREY_TOLERANCE,
                  "no grey pixels next to the fill" if seen is None else f"{seen:.0f}")

    found = [b for b in boxes.values() if b]
    if found:
        pad = 40
        area = (min(b[0] for b in found) - pad, min(b[1] for b in found) - pad,
                max(b[2] for b in found) + pad, max(b[3] for b in found) + pad)
        crop = os.path.join(os.path.dirname(path), f"border-color-repaint-{mode}.png")
        img.crop(area).save(crop)
        print(f"[test] [{mode}] crop {crop}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--slot", required=True)
    parser.add_argument("--keep", action="store_true", help="leave the override folder in the slot")
    parser.add_argument("--mode", choices=MODES, help="run one canvas mode only")
    args = parser.parse_args()

    game = s1.Game(args.slot)
    override = os.path.join(game.root, "Mods", APP)
    os.makedirs(override, exist_ok=True)
    shutil.copy(os.path.join(PAGE, "index.html"), os.path.join(override, "index.html"))
    print(f"[test] page copied to {override}")

    try:
        opened = game.call("sideload_open_app", {"appId": APP, "open": True}, timeout_ms=8000)
        bsc.check("self-test app opens", bool(opened.get("ok")), str(opened))

        for mode in ([args.mode] if args.mode else MODES):
            bsc.check(f"[{mode}] canvas switched", set_canvas(game, mode), "no 'vertexGamma=' line from sideloadgamma")
            check_screen(game, mode)
    finally:
        try:
            set_canvas(game, "auto")
        except Exception as e:
            print(f"[test] WARNING canvas not restored: {e}")
        if not args.keep:
            shutil.rmtree(override, ignore_errors=True)
            print(f"[test] override removed: {not os.path.exists(override)}")

    if bsc.failed:
        print(f"{bsc.failed} check(s) failed.")
        return 1
    print("all checks passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
