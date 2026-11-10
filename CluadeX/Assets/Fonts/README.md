# Bundled fonts

The app's own UI font, picked in Settings › General › Text size and font ("Noto Sans — comes with CluadeX").
Licence: SIL Open Font License 1.1, full text in `OFL.txt` (it ships next to the app as `Licenses\NotoSans-OFL.txt`).

| File | Source | What was done |
|---|---|---|
| `NotoSansCX-Regular.ttf`, `-Medium`, `-SemiBold`, `-Bold` | Noto Sans Thai v2.002, the variable font (axes wght 100–900, wdth 62.5–100) from notofonts/thai | Static instances at wght 400 / 500 / 600 / 700, wdth 100 (`fontTools.varLib.instancer`), then the Thai block taken out (`fontTools.subset`, 339 code points kept: Basic Latin, Latin-1 and punctuation) and the family renamed **Noto Sans CX** (name IDs 1/2/4/6/16/17). Renamed because it is a modified version, and a distinct name keeps it from clashing with a real Noto Sans Thai installed on the PC. |
| `NotoSans-Regular.ttf` | Noto Sans v2.008 from notofonts/latin-greek-cyrillic | Unchanged. Greek, Cyrillic, Vietnamese and the rest of Latin. |

## Why Thai is not drawn by a bundled font

WPF's own text shaping places Thai vowels and tone marks wrongly in modern OpenType Thai fonts: Noto Sans Thai (variable
and static) and IBM Plex Sans Thai both put the marks of "ปั๊ม", "น้ำ", "ก่" off their letters, at every size (rendered
side by side 2026-11-08). Clearing the fonts' mark-filtering flags did not change it. Leelawadee UI and Tahoma stack them
right. So the bundled family is

    ./#Noto Sans CX, Leelawadee UI, ./#Noto Sans, Segoe UI      (Services/TypographyService.cs, BundledFamily)

Latin comes from Noto Sans CX in four real weights; every Thai character is missing from it, so WPF takes Thai from
Leelawadee UI, which comes with every Windows 10/11 and is not shipped with CluadeX. Scripts neither Noto font has
(Chinese, Japanese, Korean, emoji …) fall back to the system's fonts as before. `tests/themetest --hero` checks the
weights, that Thai falls through, and saves `thai-sizes-bundled.png` for the eye.

Claude's own app fonts are commercial and licensed to Anthropic; they cannot be shipped with CluadeX.
