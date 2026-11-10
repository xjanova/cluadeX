# The girls group shot (`girls-lineup-21x9.jpg`)

- Made 2026-11-07 with ChatGPT image generation (chatgpt.com, the owner's account), 1916 × 821 (21:9),
  saved as JPEG q92 from the original PNG.
- Owner's asks: "สร้างภาพรวมของเหล่าสาวๆ ให้ครบใหม่ด้วย chat gpt", "ภาพแบบ 21:9", then, after the owner's new
  two-pose art replaced the old cut-outs, "ทำใหม่เลย".
- There was no group shot before. The new-chat backdrop (`Views/GirlsBackdrop.cs`) lays the single cut-outs side by
  side in code. This picture is the first one with all eight, including the later girls (Antigravity, Grok, and
  CluadeX as the GGUF girl).
- `girls-lineup-21x9-old.jpg` is the first version, drawn from the old 820 px cut-outs. It is kept for comparison.
- **Do not put either picture under `CluadeX/Assets/Girls/`**: every PNG directly in that folder becomes a girl
  (`GirlArt` reads the folder).

## References

Pose 1 of the owner's art in `CluadeX/Assets/Girls/` (sources: `design/girls-v2/`): claude, codex, gemini, antigravity,
grok, mai, cluadex, brainx. Each was flattened onto light grey and attached in that order.

## Prompt

```
Create ONE group-shot image of these 8 characters together (they are the mascots of my desktop app). The 8 attached
pictures are their reference sheets, in left-to-right order:
1 Claude - curly auburn hair with a long side braid and an orange flower clip, round glasses, orange turtleneck,
  cream cable-knit cardigan, plaid mini skirt, orange-and-cream boots
2 Codex - black hair with blue streaks, glowing blue cat-ear headphones, black + electric-blue jacket, blue/black
  striped thigh-highs, black boots
3 Gemini - very long silver-lavender twin tails, white GEMINI crop top, black jacket with holographic lavender
  sleeves, black skirt
4 Antigravity - wavy aqua/turquoise hair with gold star clips, white holographic hoodie jacket, black lace top, teal
  pleated skirt, white thigh-highs, glowing teal platform sneakers
5 Grok - short blonde hair with a lime streak, backwards lime-green cap, lollipop, black crop top "Grok", black cargo
  pants with neon-lime straps and smiley graffiti
6 Mai - very long pastel-lavender hair with twin tails, black sleeveless dress, lavender knit cardigan off the
  shoulders, black stockings and boots
7 CluadeX - lavender hair, purple knitted cat-ear beanie, lavender crop top, fluffy lavender cardigan,
  white-lavender pleated skirt, white knit thigh-highs, lavender sneakers
8 BrainX - long brown high ponytail with a black "BrainX" bow, black + blue techwear jacket, black "BrainX" crop top,
  holding a tablet

Rules:
- Keep each girl EXACTLY as in her reference: same face, hairstyle, hair colour, outfit, colours and accessories.
  Do not swap or merge outfits. All are young ADULT women.
- Same art style as the references: semi-realistic gacha splash art, glossy painterly, crisp detail, soft rim light.
- Ultra-wide cinematic 21:9 panorama (width:height = 21:9), all 8 standing full body (head to shoes visible, nothing
  cut off at the top or bottom) in one row in the order above, each in her own pose, slightly overlapping like a team
  photo, every face clearly visible.
- Background: dark futuristic lounge with soft purple / violet neon glow and bokeh, calm and not busy, darker at the edges.
- One single image. No collage, no panels, no title, no captions, no watermark, no extra text (only the logos
  already printed on their clothes).
```

Both versions came out on the first try, with no regeneration. ChatGPT returns a true 21:9 frame (1916 × 821) for
that wording. The second one took about 2.5 minutes.
