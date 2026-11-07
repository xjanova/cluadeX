# The girls group shot (`girls-lineup-21x9.jpg`)

- Made 2026-11-07 with ChatGPT image generation (chatgpt.com, the owner's account), 1916 × 821 (21:9),
  saved as JPEG q92 from the original PNG.
- Owner's ask: "สร้างภาพรวมของเหล่าสาวๆ ให้ครบใหม่ด้วย chat gpt", then "ภาพแบบ 21:9".
- There was no group shot before; the new-chat backdrop (`Views/GirlsBackdrop.cs`) lays the single cut-outs side by
  side in code. This picture is the first one with all eight, including the later girls (Antigravity, Grok, and
  CluadeX as the GGUF girl).
- **Do not put it under `CluadeX/Assets/Girls/`**: every PNG there becomes a girl (`GirlArt` reads the folder).

## References

The eight cut-outs from `CluadeX/Assets/Girls/` (claude, codex, gemini, antigravity, grok, mai, cluadex, brainx),
each flattened onto light grey, attached in that order.

## Prompt

```
Create ONE group-shot image of these 8 characters together (they are the mascots of my desktop app). The 8 attached
pictures are the reference sheets, in left-to-right order:
1 Claude - auburn/ginger braid, round glasses, orange knit top, cream cardigan, coffee mug, orange boots
2 Codex - black hair with blue streaks, blue headphones, black + electric-blue outfit, blue/black striped stockings
3 Gemini - long silver-lavender twin tails, white GEMINI crop top, holographic jacket, black skirt
4 Antigravity - wavy turquoise/aqua hair, white holographic jacket, teal pleated skirt, white thigh-highs, glowing platform sneakers
5 Grok - short blonde hair, lime-green cap, black crop top "Grok", black cargo pants with neon-green graffiti
6 Mai - very long pastel-purple twin-tail hair, purple knit cardigan, black outfit
7 CluadeX - purple hair, purple knitted cat-ear beanie, fluffy lavender cardigan, white skirt, white thigh-highs, lavender sneakers
8 BrainX - brown high ponytail with bow, black + blue techwear jacket, "BrainX" crop top, holding a tablet

Rules:
- Keep each girl EXACTLY as in her reference: same face, hairstyle, hair colour, outfit, colours and accessories.
  Do not swap or merge outfits. All are young ADULT women.
- Same art style as the references: semi-realistic gacha splash art, glossy painterly, soft rim light.
- Ultra-wide cinematic 21:9 panorama (width:height = 21:9), all 8 standing full body (head to shoes visible) in one
  row in the order above, each in her own pose, slightly overlapping like a team photo, every face clearly visible.
- Background: dark futuristic lounge with soft purple / violet neon glow and bokeh, calm and not busy, darker at the edges.
- One single image. No collage, no panels, no title, no captions, no watermark, no extra text (only the logos
  already printed on their clothes).
```

First try, no regeneration. ChatGPT returned a true 21:9 frame (1916 × 821) for that wording.
