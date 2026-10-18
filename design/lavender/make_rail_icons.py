"""Draw CluadeX's menu icons: one glossy pastel tile per item with a bold white symbol, made to be read at the size the
left rail shows them (34 px). Every theme shows them (owner 2026-10-18: "ไอค่อนใหม่ ใช้ในทุกธีมไปเลย").
The ChatGPT art before this (butterflies, moons, sparkles around a small object) turned to mush at that size, and two
items had no art at all ("มันเล็ก มันดูไม่ค่อยรู้เรื่อง และทำให้ครบ ทุกไอค่อนด้านซ้ายสุด").

THE STYLE FOR ANY MENU THAT NEEDS AN ICON (owner 2026-10-18: "ถ้ามีเมนูต้องใช้ไอค่อนใหม่ให้ยึดแบบนี้"): add a symbol
function and a row to ICONS below and run this script — no generated art, no bare font glyph. Rules that keep them
readable: filled shapes or strokes of 2.4+ units, one symbol per tile that says what the page does, a hue not already
taken by a neighbour.

Drawn at 1024 px on a 32-unit grid (1 unit = 1 px on screen), scaled down to 256 px:
CluadeX/Assets/Icons/<tag>.png, the name the rail button's Tag gives (Resources/Theme.xaml, ActivityBarButton).

    python -P make_rail_icons.py [out_dir]
"""
import math
import os
import sys

from PIL import Image, ImageDraw, ImageFilter

S = 1024                 # working size
U = S / 32               # one unit = one screen pixel at 32 px
OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "CluadeX", "Assets", "Icons")
WHITE = (255, 255, 255, 255)


def hexc(h, a=255):
    h = h.lstrip("#")
    return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16), a)


def p(x, y):
    return (x * U, y * U)


def box(x0, y0, x1, y1):
    return (x0 * U, y0 * U, x1 * U, y1 * U)


def stroke(d, pts, w, fill=WHITE):
    """A polyline with round caps and joins (PIL's own joints leave notches at this width)."""
    pts = [p(*q) for q in pts]
    d.line(pts, fill=fill, width=int(w * U), joint="curve")
    r = w * U / 2
    for x, y in pts:
        d.ellipse((x - r, y - r, x + r, y + r), fill=fill)


def tile(top, bottom):
    """The rounded tile: a vertical gradient, a soft gloss on its upper half, a thin light rim."""
    grad = Image.new("RGBA", (S, S))
    gd = ImageDraw.Draw(grad)
    t, b = hexc(top), hexc(bottom)
    for y in range(S):
        k = y / (S - 1)
        gd.line([(0, y), (S, y)], fill=tuple(int(t[i] + (b[i] - t[i]) * k) for i in range(4)))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle(box(1.5, 1.5, 30.5, 30.5), radius=8 * U, fill=255)
    out = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    out.paste(grad, (0, 0), mask)
    gloss = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(gloss).ellipse(box(-4, -14, 36, 14), fill=(255, 255, 255, 46))
    gm = Image.new("L", (S, S), 0)
    gm.paste(mask, (0, 0))
    out.alpha_composite(Image.composite(gloss, Image.new("RGBA", (S, S), (0, 0, 0, 0)), gm))
    rim = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(rim).rounded_rectangle(box(1.9, 1.9, 30.1, 30.1), radius=7.6 * U, outline=(255, 255, 255, 80), width=int(0.6 * U))
    out.alpha_composite(rim)
    return out


def finish(base, glyph, shade):
    """The symbol over the tile with a soft shadow in the tile's deep colour, so white stays crisp on pale tiles."""
    a = glyph.getchannel("A")
    sh = Image.new("RGBA", (S, S), hexc(shade, 0))
    sh.putalpha(a.point(lambda v: v * 150 // 255))
    sh = sh.filter(ImageFilter.GaussianBlur(0.9 * U))
    base.alpha_composite(sh, (0, int(0.7 * U)))
    base.alpha_composite(glyph)
    return base


# ─────────────────────────────── the symbols ───────────────────────────────

def g_chat(d, c):
    # A speech bubble with three dots.
    d.rounded_rectangle(box(6.5, 7.5, 25.5, 21.5), radius=5.5 * U, fill=WHITE)
    d.polygon([p(9.5, 20), p(8, 26), p(15.5, 20.5)], fill=WHITE)
    for x in (11.5, 16, 20.5):
        d.ellipse(box(x - 1.7, 12.8, x + 1.7, 16.2), fill=c)


def g_code(d, c):
    # </>
    stroke(d, [(11.5, 9.5), (6.5, 16), (11.5, 22.5)], 2.8)
    stroke(d, [(20.5, 9.5), (25.5, 16), (20.5, 22.5)], 2.8)
    stroke(d, [(18, 8.5), (14, 23.5)], 2.6)


def g_models(d, c):
    # A chip with an AI sparkle.
    for i in (12.5, 16, 19.5):
        for a0, b0, a1, b1 in ((i - 0.9, 6.5, i + 0.9, 10), (i - 0.9, 22, i + 0.9, 25.5),
                               (6.5, i - 0.9, 10, i + 0.9), (22, i - 0.9, 25.5, i + 0.9)):
            d.rounded_rectangle(box(a0, b0, a1, b1), radius=0.6 * U, fill=WHITE)
    d.rounded_rectangle(box(9, 9, 23, 23), radius=3 * U, fill=WHITE)
    cx, cy, r, w = 16, 16, 5.2, 1.5
    d.polygon([p(cx, cy - r), p(cx + w, cy - w), p(cx + r, cy), p(cx + w, cy + w),
               p(cx, cy + r), p(cx - w, cy + w), p(cx - r, cy), p(cx - w, cy - w)], fill=c)


def g_timemachine(d, c):
    # A clock with the arrow of history going back round it.
    cx, cy, r, w = 16.5, 16.5, 8.6, 2.5
    a0, a1 = 205, 150           # PIL degrees, clockwise from 3 o'clock: the gap sits at the left
    d.arc(box(cx - r, cy - r, cx + r, cy + r), start=a0, end=a1 + 360, fill=WHITE, width=int(w * U))
    e = math.radians(a1)
    ex, ey = cx + r * math.cos(e), cy + r * math.sin(e)
    d.ellipse(box(ex - w / 2, ey - w / 2, ex + w / 2, ey + w / 2), fill=WHITE)
    s = math.radians(a0)
    sx, sy = cx + r * math.cos(s), cy + r * math.sin(s)
    # The arrowhead at the start, pointing back (counter-clockwise).
    tx, ty = math.sin(s), -math.cos(s)          # counter-clockwise tangent in screen coordinates
    nx, ny = math.cos(s), math.sin(s)
    L, H = 4.6, 3.6
    d.polygon([p(sx + tx * L * 0.75, sy + ty * L * 0.75), p(sx - tx * L * 0.25 + nx * H, sy - ty * L * 0.25 + ny * H),
               p(sx - tx * L * 0.25 - nx * H, sy - ty * L * 0.25 - ny * H)], fill=WHITE)
    stroke(d, [(cx, cy - 4.6), (cx, cy), (cx + 3.4, cy + 2.2)], 2.3)


def g_mcp(d, c):
    # A plug: what connects the agent to a server.
    d.rounded_rectangle(box(11.4, 5.5, 14.2, 12), radius=1.2 * U, fill=WHITE)
    d.rounded_rectangle(box(17.8, 5.5, 20.6, 12), radius=1.2 * U, fill=WHITE)
    d.rounded_rectangle(box(8.5, 10.5, 23.5, 19.5), radius=3.2 * U, fill=WHITE)
    d.polygon([p(11.5, 18), p(20.5, 18), p(18.2, 23), p(13.8, 23)], fill=WHITE)
    stroke(d, [(16, 22), (16, 27)], 2.6)


def g_tasks(d, c):
    # A clipboard with a big tick.
    d.rounded_rectangle(box(7.5, 7, 24.5, 27), radius=3.2 * U, fill=WHITE)
    d.rounded_rectangle(box(12, 4.8, 20, 9.6), radius=1.8 * U, fill=c, outline=WHITE, width=int(1.3 * U))
    stroke(d, [(11.4, 17.6), (14.8, 21), (21, 13.6)], 2.7, fill=c)


def g_watch(d, c):
    # A heartbeat: your apps' health — tests, crashes, pages.
    stroke(d, [(4.8, 17), (10, 17), (12.6, 10.2), (16.6, 23.6), (19.4, 13.6), (21.2, 17), (27.2, 17)], 2.6)


def g_uistudio(d, c):
    # A painter's palette: designing the UI.
    pal = Image.new("L", (S, S), 0)
    pd = ImageDraw.Draw(pal)
    pd.ellipse(box(5.5, 7, 26.5, 25.5), fill=255)
    pd.ellipse(box(15.6, 17.8, 21.6, 23.8), fill=0)     # the thumb hole
    pd.ellipse(box(19.5, 19.5, 28, 28), fill=0)         # the bite at the lower right
    d.bitmap((0, 0), pal, fill=WHITE)
    for (x, y), col in zip(((10.5, 13), (14.6, 10.6), (19.4, 10.8), (10.8, 18.6)),
                           ("#FF5FA2", "#5B8CFF", "#36C98A", "#FFB21F")):
        d.ellipse(box(x - 1.95, y - 1.95, x + 1.95, y + 1.95), fill=hexc(col))


def g_nodegraph(d, c):
    # Three nodes wired together: a flow of steps.
    def curve(x0, y0, x1, y1, n=40):
        pts = []
        for i in range(n + 1):
            t = i / n
            mx = (x0 + x1) / 2
            bx = (1 - t) ** 3 * x0 + 3 * (1 - t) ** 2 * t * mx + 3 * (1 - t) * t ** 2 * mx + t ** 3 * x1
            by = (1 - t) ** 3 * y0 + 3 * (1 - t) ** 2 * t * y0 + 3 * (1 - t) * t ** 2 * y1 + t ** 3 * y1
            pts.append((bx, by))
        return pts
    stroke(d, curve(12, 16, 20, 10), 2.0)
    stroke(d, curve(12, 16, 20, 22), 2.0)
    d.rounded_rectangle(box(5, 12.2, 12.6, 19.8), radius=2.2 * U, fill=WHITE)
    d.rounded_rectangle(box(19.4, 6.2, 27, 13.8), radius=2.2 * U, fill=WHITE)
    d.rounded_rectangle(box(19.4, 18.2, 27, 25.8), radius=2.2 * U, fill=WHITE)


def g_settings(d, c):
    # A gear.
    cx, cy, ro, ri, teeth = 16, 16, 10.6, 7.8, 8
    pts = []
    for i in range(teeth):
        a = 2 * math.pi * i / teeth
        half_top, half_root = math.pi / teeth * 0.42, math.pi / teeth * 0.62
        for ang, rad in ((a - half_root, ri), (a - half_top, ro), (a + half_top, ro), (a + half_root, ri)):
            pts.append(p(cx + rad * math.cos(ang), cy + rad * math.sin(ang)))
    d.polygon(pts, fill=WHITE)
    d.ellipse(box(cx - ri - 0.3, cy - ri - 0.3, cx + ri + 0.3, cy + ri + 0.3), fill=WHITE)
    d.ellipse(box(cx - 3.4, cy - 3.4, cx + 3.4, cy + 3.4), fill=c)


# tag: (symbol, tile top, tile bottom, cut-out colour, shadow colour)
ICONS = {
    "chat":        (g_chat,        "#C7A2FF", "#8848F0", "#9A5CF6", "#4A1E8F"),
    "code":        (g_code,        "#A9BCFF", "#5A63E8", "#6670EE", "#26307F"),
    "models":      (g_models,      "#FFA3DA", "#C9449E", "#D456A9", "#6E1C55"),
    "timemachine": (g_timemachine, "#FFC896", "#E07A62", "#E78A6C", "#7A3426"),
    "mcp":         (g_mcp,         "#8EEAD9", "#24A39C", "#2FB0A8", "#0E5754"),
    "tasks":       (g_tasks,       "#A9EFAE", "#38B276", "#41BA7E", "#145C38"),
    "watch":       (g_watch,       "#FFA0AE", "#DE4A6C", "#E35A78", "#78192F"),
    "uistudio":    (g_uistudio,    "#FFDF8E", "#E9A335", "#EDAA40", "#7A4E0C"),
    "nodegraph":   (g_nodegraph,   "#93D6FF", "#3787E0", "#4394E6", "#123F78"),
    "settings":    (g_settings,    "#D9CDEB", "#8875A8", "#9583B4", "#3A2C55"),
}


def main():
    os.makedirs(OUT, exist_ok=True)
    for tag, (fn, top, bottom, cut, shade) in ICONS.items():
        base = tile(top, bottom)
        glyph = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        fn(ImageDraw.Draw(glyph), hexc(cut))
        img = finish(base, glyph, shade).resize((256, 256), Image.LANCZOS)
        img.save(os.path.join(OUT, tag + ".png"), optimize=True)
        print(tag)


if __name__ == "__main__":
    main()
