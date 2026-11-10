# Provider logos (Billing & cost page)

Each provider tab shows its company's own mark, bundled as an app resource (never fetched at runtime). The PNGs are what
the app draws; `source/` keeps the vector files they were rendered from. Nothing was redrawn: the Simple Icons files
were rendered as published, in the brand colour Simple Icons lists for that brand, at 256×256 by headless Microsoft
Edge; the Google and llama.cpp files are the vendors' own. Every logo sits on a white tile in the app, so its colours
stay as published in both light and dark themes.

All marks are trademarks of their owners and are used only to label that provider's own usage (nominative use). Their
presence does not imply endorsement.

| File | Used for | Mark | Source (fetched 2026-11-02) | Licence / terms |
|---|---|---|---|---|
| `anthropic.png` | Anthropic API | Anthropic "A" mark, `#191919` | Simple Icons 16.36.0, `icons/anthropic.svg` (`source/anthropic.svg`); Simple Icons lists anthropic.com as its source | Simple Icons: CC0 1.0. The mark: Anthropic's trademark |
| `claude-code.png` | Claude Dev (Claude Code CLI) | Claude Code mark, `#D97757` | Simple Icons 16.36.0, `icons/claudecode.svg` (`source/claude-code.svg`); listed source code.claude.com | Simple Icons: CC0 1.0. The mark: Anthropic's trademark |
| `gemini.png` | Gemini API, Gemini Dev (Gemini CLI) | Gemini sparkle (current four-colour mark), 512×512 | Google's own file: the icon gemini.google.com serves, `https://www.gstatic.com/lamda/images/gemini_sparkle_4g_512_lt_f94943af3be039176192d.png`, unmodified | Google trademark; Google brand terms (about.google/brand-resource-center) |
| `antigravity.png` | Antigravity Dev | Google Antigravity mark, 276×276 | Google's own file: `https://antigravity.google/assets/image/antigravity-logo.png` (the site's icon), unmodified. `source/antigravity-favicon.svg` is the same mark as the site's SVG favicon | Google trademark; Google brand terms |
| `ollama.png` | Ollama | Ollama llama, `#000000` | Simple Icons 16.36.0, `icons/ollama.svg` (`source/ollama.svg`); listed source github.com/ollama/ollama issue 2152 | Simple Icons: CC0 1.0. The mark: Ollama's |
| `llamacpp.png` | llama-server, Local GGUF (LLamaSharp runs llama.cpp) | llama.cpp icon | ggml-org/llama.cpp, `media/llama1-icon.svg` (`source/llamacpp-llama1-icon.svg`), master, 2026-11-02 | Repository licence MIT (ggml-org) |

## NVIDIA (DGX Spark)

| File | Used for | Mark | Source (fetched 2026-11-08) | Terms |
|---|---|---|---|---|
| `nvidia.png` | Every DGX Spark entry: the title-bar chip, the DGX Spark window, the command palette row | NVIDIA logo, horizontal format: eye mark `#76B900` + wordmark `#000000`, 632×120 | NVIDIA's own file from its Logo and Brand Usage page (nvidia.com/en-us/about-nvidia/legal-info/logo-brand-usage/): `https://www.nvidia.com/content/dam/en-zz/Solutions/about-nvidia/logo-and-brand/nvidia-logo-horz.svg` (`source/nvidia-logo-horz.svg`, unmodified). Rendered by headless Microsoft Edge with the viewBox cut to the artwork's own bounds (2069.12 × 392.92) — shapes, proportions and colours untouched | NVIDIA trademark; NVIDIA logo and brand usage guidelines |

How the app follows NVIDIA's rules for it: eye mark and wordmark are never separated; it sits on a white tile (green eye +
black wordmark is NVIDIA's approved combination on white) in every theme; it is never drawn smaller than NVIDIA's digital
minimum for the horizontal format, 15 px tall; no effects, recolouring or stretching. It names the hardware CluadeX
connects to; it does not claim an NVIDIA partnership or endorsement.

## Not bundled — shown as a neutral text badge

* **OpenAI** (OpenAI API, OpenAI Dev / Codex CLI). OpenAI's Blossom is not in Simple Icons any more, and the official
  files come from the "Download logos" button on openai.com/brand, which means agreeing to OpenAI's Marks usage terms.
  That was not done on the owner's behalf. To add it: download the Blossom from openai.com/brand (black on white,
  unaltered, with its prescribed clear space), save it as `openai.png` here (the project's `Assets\Logos\*.png` resource
  glob picks it up), and set the logo of OpenAI / OpenAIDev in `Services/Billing/ProviderBrand.cs`.
* **BrainX** (the cowork room as a model): no published mark to take.
