"""Generate Resources/Themes/Lavender.xaml from Midnight.xaml: the same keys (themetest requires it), every colour
re-mapped onto the CluadeX lavender art (deep violet night room, lavender glass, pink glow, white, gold accents).
Status / diff / syntax / chart colours keep their hue so meaning survives (green ok, red error, amber warning).

    python -P make_theme.py   (run from anywhere; paths are absolute)
"""
import colorsys, re

SRC = r"E:\Code\ClaudeClient\CluadeX\Resources\Themes\Midnight.xaml"
DST = r"E:\Code\ClaudeClient\CluadeX\Resources\Themes\Lavender.xaml"

KEEP_KEYS = ("Status.", "Diff.", "Syntax.", "Chart.", "Role.", "Success", "Warning", "Danger", "Error", "Added", "Removed")
GOLD_KEYS = ("License.Pro", "Gold", "Star")

def remap(hex_str, key):
    h = hex_str.lstrip('#')
    a = ''
    if len(h) == 8: a, h = h[:2], h[2:]
    elif len(h) == 3: h = ''.join(c * 2 for c in h)
    elif len(h) != 6: return hex_str
    r, g, b = (int(h[i:i + 2], 16) / 255 for i in (0, 2, 4))
    hh, l, s = colorsys.rgb_to_hls(r, g, b)
    deg = hh * 360
    if any(k in key for k in KEEP_KEYS):
        return hex_str
    if any(k in key for k in GOLD_KEYS) and 30 <= deg <= 60:
        return hex_str
    if s < 0.12:
        # neutrals: dark = deep violet night, light = lavender white
        if l < 0.5:
            nh, ns, nl = 265, 0.42, l * 1.05 + 0.01
        else:
            nh, ns, nl = 270, 0.55, max(l, 0.82) if l > 0.7 else l
    else:
        # chroma: everything gathers in violet → lavender → pink
        if 150 <= deg <= 260:      # cyan / blue (Midnight's neon) → violet / lavender
            nh = 262 + (deg - 150) * 0.18
        elif 260 < deg <= 320:     # already purple / magenta → keep near
            nh = deg
        elif deg > 320 or deg < 20:  # pink / red accents → soft pink
            nh = 318
        else:                       # green / yellow / orange accents → lilac (gold handled above)
            nh = 285
        ns = min(1.0, max(s, 0.55))
        nl = l
    rr, gg, bb = colorsys.hls_to_rgb(nh / 360, min(max(nl, 0), 1), min(max(ns, 0), 1))
    return '#' + a + ''.join(f"{round(v * 255):02X}" for v in (rr, gg, bb))

text = open(SRC, encoding='utf-8').read()
out, key = [], ""
for line in text.splitlines(keepends=True):
    m = re.search(r'x:Key="([^"]+)"', line)
    if m: key = m.group(1)
    line = re.sub(r'#[0-9A-Fa-f]{3,8}\b', lambda mm: remap(mm.group(0), key), line)
    out.append(line)
res = ''.join(out)
res = res.replace('<sys:String x:Key="Theme.Id">Midnight</sys:String>', '<sys:String x:Key="Theme.Id">Lavender</sys:String>')
res = res.replace('THEME: Midnight — the CluadeX look as it was before themes existed (neon on navy). DEFAULT.',
                  'THEME: Lavender — the CluadeX mascot\'s night room: deep violet, lavender glass, pink glow, gold.\n'
                  '        GENERATED from Midnight.xaml by design/lavender/make_theme.py (same keys); hand-tune below.')
open(DST, 'w', encoding='utf-8').write(res)
print("written", DST, len(res))
