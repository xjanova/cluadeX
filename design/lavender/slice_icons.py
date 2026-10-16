"""Cut a ChatGPT 3x2 icon sheet (transparent PNG) into separate icons: each cell trimmed to its visible pixels,
centred on a square transparent canvas, saved at 256 px into CluadeX/Assets/Icons/<name>.png.

    python -P slice_icons.py <sheet.png> name1,name2,...,name6
"""
import sys
from PIL import Image

sheet, names = sys.argv[1], sys.argv[2].split(',')
OUT = r"E:\Code\ClaudeClient\CluadeX\Assets\Icons"
import os; os.makedirs(OUT, exist_ok=True)
im = Image.open(sheet).convert('RGBA')
W, H = im.size
cw, ch = W // 3, H // 2
for i, name in enumerate(names):
    cx, cy = (i % 3) * cw, (i // 3) * ch
    cell = im.crop((cx, cy, cx + cw, cy + ch))
    # trim on alpha > 24 (faint glow beyond that is dropped so icons do not read as tiny)
    a = cell.getchannel('A').point(lambda v: 255 if v > 24 else 0)
    box = a.getbbox() or (0, 0, cw, ch)
    cell = cell.crop(box)
    side = max(cell.size)
    canvas = Image.new('RGBA', (side, side), (0, 0, 0, 0))
    canvas.paste(cell, ((side - cell.width) // 2, (side - cell.height) // 2), cell)
    canvas = canvas.resize((256, 256), Image.LANCZOS)
    canvas.save(os.path.join(OUT, name + '.png'))
    print(name, box, '→ 256')
