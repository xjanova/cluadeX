# CluadeX — AI Coding Assistant

<p align="center">
  <img src="CluadeX/Assets/logo.png" width="120" alt="CluadeX Logo"/>
</p>

<p align="center">
  <strong>A powerful, privacy-first AI coding assistant for Windows</strong><br/>
  Built with WPF .NET 8 | Catppuccin Mocha theme | Multi-provider AI | Zero telemetry
</p>

<p align="center">
  <a href="https://github.com/xjanova/CluadeX/releases/latest"><img src="https://img.shields.io/github/v/release/xjanova/CluadeX?style=flat-square" alt="Release"/></a>
  <img src="https://img.shields.io/badge/.NET-8.0-blue?style=flat-square" alt=".NET 8"/>
  <img src="https://img.shields.io/badge/platform-Windows-blue?style=flat-square" alt="Windows"/>
  <img src="https://img.shields.io/badge/telemetry-zero-green?style=flat-square" alt="Zero Telemetry"/>
  <img src="https://img.shields.io/badge/license-MIT-green?style=flat-square" alt="MIT"/>
</p>

---

## Features / ฟีเจอร์ทั้งหมด

CluadeX is a full-featured AI coding assistant that runs on your desktop with support for both local and cloud AI models. Every feature is listed below.

CluadeX คือผู้ช่วยเขียนโค้ด AI เต็มรูปแบบที่ทำงานบนเดสก์ท็อปของคุณ รองรับทั้งโมเดล AI บนเครื่องและ Cloud ฟีเจอร์ทั้งหมดอยู่ด้านล่าง

---

### Core Features / ฟีเจอร์หลัก

| Feature | Description | คำอธิบาย |
|---------|-------------|----------|
| **Local GGUF Inference** | Run AI models locally on your GPU/CPU. Transparent dual backend — LLamaSharp (in-proc, CUDA 12) for classic models, bundled `llama-server.exe` auto-launched for newer architectures (Gemma 3/3N/4, Llama 4, Qwen 3, DeepSeek V3/R1, Phi 4). | รันโมเดล AI บนเครื่อง ใช้ backend คู่: LLamaSharp สำหรับโมเดลคลาสสิก, llama-server.exe สำหรับ Gemma 4 / Llama 4 / Qwen 3 ฯลฯ |
| **Multi-Provider AI** | Switch between Local, OpenAI, Anthropic, Google Gemini, and Ollama providers instantly. | สลับระหว่าง Local, OpenAI, Anthropic, Google Gemini และ Ollama ได้ทันที |
| **Chat Persistence (SQLite + FTS5)** | All conversations saved locally with full-text search. Sessions are tagged with project path and filtered per-folder in the sidebar. Never lose your chat history. | บันทึกการสนทนาทั้งหมด ค้นหาข้อความเต็ม และแยก session ตามโปรเจคอัตโนมัติ |
| **Markdown & Syntax Highlighting** | Rich text rendering with code blocks, tables, and syntax-highlighted code snippets. | แสดงผลข้อความสวยงามพร้อมบล็อกโค้ด ตาราง และไฮไลท์โค้ดสีสัน |
| **Live GPU Monitoring** | Real-time sparkline of GPU utilization + color-coded temperature chip in the sidebar status bar (polls `nvidia-smi` every 2s). | กราฟ GPU utilization + chip อุณหภูมิแบบ real-time ในแถบสถานะ |
| **GPU Auto-Detection** | Automatically detects your NVIDIA GPU and VRAM for optimal model loading. Multi-GPU supported via automatic `--tensor-split`. | ตรวจจับ GPU และ VRAM อัตโนมัติ รองรับหลายการ์ดจอผ่าน tensor-split |
| **Catppuccin Mocha Theme** | Beautiful dark theme with metallic gradients, drop shadows, glowing menu, and rounded corners. | ธีมมืดสวยงามพร้อมเมนูเรืองแสง กราเดียนท์เมทัลลิก เงาตกกระทบ ขอบมน |
| **HuggingFace Model Hub** | Browse, search, and download GGUF models directly from HuggingFace Hub. Search is filtered to GGUF text-generation LLMs only (no embeddings/image/audio). Every result card has a "Read more" link to the HF model page. | ค้นหาและดาวน์โหลด GGUF กรองเฉพาะ LLM text-generation ทุกผลมีลิงก์ Read more ไปดู model card |
| **Rich Model Catalog** | Curated catalog with fit indicator on every card: 🟢 Fast (fits GPU) · 🟦 Good · 🟡 Partial (CPU offload) · 🟠 Slow · 🔴 Too large. 17 Gemma variants (4, 3N, 3, 3 QAT, 2, CodeGemma) plus Qwen 2.5, DeepSeek, Llama 3, Phi 3.5, StarCoder 2, CodeLlama. List/Grid view toggle. | แคตตาล็อกโมเดลพร้อมตัวบอกความเหมาะสมกับ VRAM ของคุณ มี 17 Gemma + รุ่นอื่นอีก toggle ระหว่าง list/grid |
| **Thai/English Localization** | Hot-swappable Thai/English with `{services:Loc key}` XAML markup extension — all bound labels refresh live on language change. 200+ translations covering Settings, Features, navigation, buddy, common UI. | สลับภาษาแบบ hot-reload ผ่าน MarkupExtension 200+ คำแปล |
| **Feature Toggles** | Enable or disable optional features from the Features page. | เปิด/ปิดฟีเจอร์เสริมได้จากหน้า Features |
| **Activation Key System** | Advanced features gated behind activation key. Free tier includes local inference, chat, Ollama, buddy, and more. | ฟีเจอร์ขั้นสูงต้องใส่ activation key ฟรีเทียร์มีครบเรื่องพื้นฐาน |
| **Portable Mode** | Place a `portable` or `portable.txt` file next to the exe to store all data locally. | วางไฟล์ `portable` ข้างไฟล์ exe เพื่อเก็บข้อมูลทั้งหมดในโฟลเดอร์เดียวกัน |

---

### Sidebar & UX / แถบข้าง

| Feature | Description | คำอธิบาย |
|---------|-------------|----------|
| **Collapsible Menu** | System menu (Chat/Models/Settings/Plugins/…) collapses to a gradient "MENU" strip so chat history dominates. Default collapsed; state persisted. | เมนูระบบพับได้ — default พับเพื่อเน้นประวัติแชท |
| **Per-Project Session Filter** | Sidebar shows sessions from the current project folder only. Toggle switch exposes "all projects" view. Legacy sessions (no project tag) always visible. | sidebar แสดง session เฉพาะโปรเจคที่เปิด มี toggle ให้ดูทุกโปรเจค |
| **Project Header** | Current project name + icon shown prominently above chat history. Tooltip shows full path. | หัวโปรเจคแสดงชื่อโฟลเดอร์ปัจจุบัน |
| **Sticky Buddy** | Buddy widget pinned above status bar — stays visible even when chat history scrolls. | buddy อยู่ติดด้านล่างไม่หล่นหายเมื่อเลื่อนประวัติ |
| **Claude Code-style Chat UX** | Inline tool-step indicators with verbs + elapsed time, thinking indicator, live token counter, non-blocking streaming (BeginInvoke + token batching). | UX สไตล์ Claude Code: บอกเครื่องมือที่ใช้, เวลา, token, stream ไม่บล็อก |
| **Rich 3D Menu Styling** | Selected nav items get gradient fill, drop shadow, colored icon bubble, and accent strip. Hover state preview. | เมนูมีมิติ: กรอบสี, เงา, bubble icon |

---

### Advanced Model Loading / การโหลดโมเดลขั้นสูง

| Feature | Description | คำอธิบาย |
|---------|-------------|----------|
| **Multi-GPU Tensor Split** | If 2+ NVIDIA GPUs are detected, llama-server is launched with `--tensor-split <vram1,vram2,...>` so the model is split proportionally by VRAM. | หลายการ์ดจอ → แบ่งโหลดตามอัตราส่วน VRAM อัตโนมัติ |
| **CPU Offload for VRAM Overflow** | Models larger than VRAM run with partial CPU offload (`-ngl N`). Fit indicator suggests optimal `-ngl` per model. | โมเดลใหญ่กว่า VRAM ใช้ CPU ช่วย พร้อมคำแนะนำค่า `-ngl` |
| **All CPU Cores by Default** | When ThreadCount = 0, llama-server is launched with `-t Environment.ProcessorCount` (all logical cores) for maximum throughput. | ใช้ทุก CPU core อัตโนมัติ |
| **Live Load Progress** | Parses `llama-server` stderr for structured loading steps — shows percent layers offloaded to GPU, VRAM upload size, context allocation phase in the UI. | บอก % โหลดเข้า VRAM ระหว่างโหลดโมเดลแบบ real-time |
| **Architecture Detection** | GGUF header parsed before loading. Known-incompatible archs (Gemma 3/3N/4, Llama 4, Qwen 3, DeepSeek V3/R1, Phi 4) automatically routed to `llama-server.exe`. | ตรวจ GGUF arch ก่อนโหลด เพื่อเลือก backend ที่ถูกต้อง |
| **Auto-Load Last Model** | On startup, the last loaded model is restored through the architecture-aware router — works for Gemma 4 etc., not just LLamaSharp-compatible models. | โหลดโมเดลล่าสุดอัตโนมัติ รองรับทุก architecture |
| **Gemma Reasoning Fix** | llama-server launched with `--reasoning-format none` so models that emit thinking tokens (Gemma 4, DeepSeek R1, Qwen 3) don't return empty `content` fields. | fix Gemma 4 / R1 / Qwen 3 ที่เคยออกเป็นข้อความว่างเปล่า |
| **Microcompact** | Before re-sending the conversation to the API, old tool results are shrunk (base64 images stripped, ISO-8601 timestamps collapsed, large bodies truncated). Keeps recent turns verbatim. | บีบอัดผลเครื่องมือเก่าก่อนส่งไป API ประหยัด context |

---

### AI Providers / ผู้ให้บริการ AI

| Provider | Models | Requirements |
|----------|--------|-------------|
| **Local GGUF** | Any GGUF model via transparent dual backend (LLamaSharp in-proc for classic archs, bundled `llama-server.exe` for Gemma 3/3N/4, Llama 4, Qwen 3, DeepSeek V3/R1, Phi 4). Multi-GPU tensor-split supported. | GPU with VRAM (CUDA 12) or CPU |
| **OpenAI** | GPT-4o, GPT-4o-mini, o1, o1-pro, o3, o3-mini, o4-mini | OpenAI API Key |
| **Anthropic** | Claude Opus 4, Claude Sonnet 4, Claude 3.5 Sonnet, Claude Haiku 3.5 | Anthropic API Key |
| **Google Gemini** | Gemini 2.5 Pro/Flash, Gemini 2.0 Flash, Gemma 4 (31B, 26B MoE, E4B, E2B), Gemma 3 | Google AI API Key |
| **Ollama** | Any model supported by Ollama | Ollama installed locally |

> API keys are encrypted at rest using Windows DPAPI. Only your Windows account can decrypt them.
>
> API Key ทั้งหมดเข้ารหัสด้วย Windows DPAPI มีเฉพาะบัญชี Windows ของคุณเท่านั้นที่ถอดรหัสได้

---

### Agent System / ระบบ Agent อัจฉริยะ

CluadeX features a Claude Code-level agentic system with multi-step reasoning, parallel tool execution, native tool_use API, and self-correction.

CluadeX มีระบบ Agent ระดับ Claude Code ที่คิดหลายขั้นตอน รันเครื่องมือแบบ parallel, ใช้ native tool_use API และแก้ไขตัวเอง

| Capability | Description | คำอธิบาย |
|------------|-------------|----------|
| **Multi-Step Reasoning** | Agent loop up to 15 iterations: generate → use tools → read results → decide next action. | วนลูป Agent สูงสุด 15 รอบ: สร้าง → ใช้เครื่องมือ → อ่านผล → ตัดสินใจขั้นต่อไป |
| **Parallel Tool Execution** | Read-only tools run concurrently via `Task.WhenAll`. Write tools run sequentially. | เครื่องมืออ่านอย่างเดียวรันพร้อมกัน เครื่องมือเขียนรันทีละตัว |
| **Native tool_use API** | Dual-mode: native structured tool_use for Anthropic, legacy [ACTION:] text for others. | สองโหมด: native tool_use สำหรับ Anthropic, [ACTION:] สำหรับ provider อื่น |
| **Extended Thinking** | Claude's extended thinking with configurable budget_tokens for complex reasoning. | Extended thinking ของ Claude พร้อม budget_tokens ปรับได้ |
| **Prompt Caching** | Anthropic prompt caching via cache_control ephemeral to reduce API costs. | Prompt caching ลดค่าใช้จ่าย API |
| **Self-Correction** | If a tool fails, the AI reads the error and tries a different approach (2-3 attempts). | ถ้าเครื่องมือล้มเหลว AI อ่าน error และลองวิธีอื่น (2-3 วิธี) |
| **Max Output Recovery** | Detects truncated responses and auto-continues (up to 3 retries). | ตรวจจับคำตอบที่ถูกตัด และขอต่ออัตโนมัติ (สูงสุด 3 ครั้ง) |
| **Reactive Compaction** | Auto-compacts history on 413 errors (context too long) and retries. | บีบอัดประวัติอัตโนมัติเมื่อ context ยาวเกิน แล้วลองใหม่ |
| **Tool Result Budgets** | Per-tool 50K chars, aggregate 200K chars truncation to prevent overflow. | จำกัดผลลัพธ์ต่อเครื่องมือ 50K, รวม 200K ป้องกัน overflow |
| **Smart Context** | Auto-loads referenced files, injects git status/branch at session start. | โหลดไฟล์อ้างอิงอัตโนมัติ, ใส่ git status/branch ตอนเริ่ม session |
| **Cost Tracking** | Real-time USD cost display per session with per-model pricing. | แสดงค่าใช้จ่าย USD แบบ real-time ต่อ session |
| **Bilingual Prompts** | 10-section system prompt dynamically generated in Thai or English. | System prompt 10 ส่วน สร้างแบบ dynamic ตามภาษา |
| **Feature-Gated Tools** | Tools respect feature toggles — disabled features are hidden from AI. | เครื่องมือที่ปิดจะถูกซ่อนจาก AI |

---

### Agent Tools / เครื่องมือ Agent (48 เครื่องมือ)

#### File Tools (10)

| Tool | Description | คำอธิบาย |
|------|-------------|----------|
| `read_file` | Read file contents | อ่านเนื้อหาไฟล์ |
| `write_file` | Create or overwrite a file | สร้างหรือเขียนทับไฟล์ |
| `edit_file` | Find and replace text in a file | ค้นหาและแทนที่ข้อความในไฟล์ |
| `list_files` | List files and directories | แสดงรายการไฟล์และไดเรกทอรี |
| `search_files` | Find files by glob pattern | ค้นหาไฟล์ตามรูปแบบ |
| `search_content` | Search text in file contents | ค้นหาข้อความในเนื้อหาไฟล์ |
| `glob` | Pattern-based file matching (e.g., `**/*.cs`) | ค้นหาไฟล์ด้วย glob pattern |
| `grep` | Regex content search with context lines | ค้นหาเนื้อหาด้วย regex พร้อมบรรทัดบริบท |
| `run_command` | Execute a shell command | รันคำสั่ง shell |
| `create_directory` | Create a new directory | สร้างไดเรกทอรีใหม่ |

#### Git Tools (14)

| Tool | Description | คำอธิบาย |
|------|-------------|----------|
| `git_status` | Show working tree status | แสดงสถานะ working tree |
| `git_add` | Stage files for commit | Stage ไฟล์สำหรับ commit |
| `git_commit` | Commit staged changes | Commit การเปลี่ยนแปลง |
| `git_push` | Push to remote | Push ไปยัง remote |
| `git_pull` | Pull from remote | Pull จาก remote |
| `git_branch` | List or create branches | แสดง/สร้าง branch |
| `git_checkout` | Switch branches | สลับ branch |
| `git_diff` | Show changes | แสดงการเปลี่ยนแปลง |
| `git_log` | Show commit history | แสดงประวัติ commit |
| `git_clone` | Clone a repository | Clone repository |
| `git_init` | Initialize a new repo | สร้าง repo ใหม่ |
| `git_stash` | Stash/unstash changes | Stash การเปลี่ยนแปลง |
| `git_worktree_create` | Create an isolated git worktree | สร้าง worktree แยก |
| `git_worktree_remove` | Remove a git worktree | ลบ worktree |

#### GitHub Tools (5)

| Tool | Description | คำอธิบาย |
|------|-------------|----------|
| `gh_pr_create` | Create a Pull Request | สร้าง Pull Request |
| `gh_pr_list` | List Pull Requests | แสดงรายการ PR |
| `gh_issue_create` | Create an Issue | สร้าง Issue |
| `gh_issue_list` | List Issues | แสดงรายการ Issues |
| `gh_repo_view` | View repository info | ดูข้อมูล repository |

#### Web Tools (2)

| Tool | Description | คำอธิบาย |
|------|-------------|----------|
| `web_fetch` | Fetch a URL and extract content | ดึงเนื้อหาจาก URL |
| `web_search` | Search the web via DuckDuckGo | ค้นหาเว็บผ่าน DuckDuckGo |

#### Advanced Tools (13)

| Tool | Description | คำอธิบาย |
|------|-------------|----------|
| `repl` | Persistent Python/Node.js REPL sessions | REPL session ถาวร (Python/Node.js) |
| `powershell` | Execute PowerShell commands | รันคำสั่ง PowerShell |
| `notebook_edit` | Edit Jupyter .ipynb notebook cells | แก้ไขเซลล์ Jupyter notebook |
| `ask_user` | Ask user a question via dialog | ถามผู้ใช้ผ่าน dialog |
| `config` | View CluadeX settings | ดูการตั้งค่า CluadeX |
| `skill_invoke` | Invoke a skill by name (e.g., commit, review-pr) | เรียกใช้ skill ตามชื่อ (เช่น commit, review-pr) |
| `memory_save` | Save persistent memory across sessions | บันทึก memory ถาวรข้าม session |
| `memory_list` | List all saved memories | แสดงรายการ memory ทั้งหมด |
| `memory_delete` | Delete a saved memory | ลบ memory |
| `todo_write` | Track progress with a todo list | ติดตามความคืบหน้าด้วย todo list |
| `plan_mode` | Create an execution plan before acting | สร้างแผนก่อนลงมือทำ |
| `agent_spawn` | Spawn a sub-agent for complex tasks | สร้าง sub-agent สำหรับงานซับซ้อน |
| `task_create/list/stop/output` | Background task management (4 tools) | จัดการ task เบื้องหลัง (4 เครื่องมือ) |

---

### Skills System / ระบบ Skill (Claude Code-style)

CluadeX supports slash-command skills — reusable prompt templates invoked via `/command`.

CluadeX รองรับ slash-command skills — template prompt ที่ใช้ซ้ำได้ผ่าน `/command`

| Feature | Description | คำอธิบาย |
|---------|-------------|----------|
| **Built-in Skills** | `/commit` (git commit), `/review-pr` (PR review), `/simplify` (code quality) | Skill พื้นฐาน 3 ตัว |
| **Custom Skills** | Create `.md` files with YAML frontmatter in `~/.cluadex/skills/` or `{project}/.cluadex/skills/` | สร้าง skill เองด้วยไฟล์ markdown + YAML frontmatter |
| **Tool Restrictions** | Skills can limit which tools the AI is allowed to use. | Skill กำหนดได้ว่า AI ใช้เครื่องมือไหนได้บ้าง |
| **Project Override** | Project skills override user skills, user skills override built-in. | Skill ของโปรเจกต์มีสิทธิ์สูงกว่า Skill ของผู้ใช้ |

---

### Memory System / ระบบ Memory ถาวร

Persistent file-based memory that survives across sessions — like Claude Code's memdir.

Memory แบบไฟล์ที่ข้ามเซสชั่นได้ — เหมือนระบบ memdir ของ Claude Code

| Feature | Description | คำอธิบาย |
|---------|-------------|----------|
| **4 Memory Types** | `user` (preferences), `feedback` (corrections), `project` (goals), `reference` (links) | 4 ประเภท: ผู้ใช้, feedback, โปรเจกต์, อ้างอิง |
| **Dual Scope** | Global (`~/.cluadex/memory/`) and project (`.cluadex/memory/`) | สองขอบเขต: ทั้งหมด และ เฉพาะโปรเจกต์ |
| **MEMORY.md Index** | Auto-maintained index file (200-line cap). Injected into system prompt. | ไฟล์ index อัตโนมัติ ถูกใส่เข้า system prompt |
| **Agent Tools** | `memory_save`, `memory_list`, `memory_delete` — AI manages memory itself. | AI จัดการ memory ได้เอง |
| **Session Memory Extraction** | Optional background pass on session end: AI scans the transcript, extracts durable facts (preferences, references, project constraints) and saves them as memory files. Opt-in via Settings. | เมื่อจบเซสชัน สกัด fact สำคัญมาเก็บเป็น memory อัตโนมัติ (opt-in) |

---

### Hooks System / ระบบ Hooks

Run shell commands before/after tool execution — like Claude Code's hooks.

รันคำสั่ง shell ก่อน/หลังใช้เครื่องมือ — เหมือนระบบ hooks ของ Claude Code

| Feature | Description | คำอธิบาย |
|---------|-------------|----------|
| **PreToolUse** | Run before a tool executes. Can block execution on failure. | รันก่อนใช้เครื่องมือ สามารถบล็อกได้ถ้าล้มเหลว |
| **PostToolUse** | Run after a tool executes. Best-effort (doesn't block). | รันหลังใช้เครื่องมือ (ไม่บล็อก) |
| **Wildcard Matchers** | Match by tool name pattern (e.g., `run_command`, `*` for all). | จับคู่ตามชื่อเครื่องมือ |
| **Variable Substitution** | `{tool}`, `{path}`, `{command}` replaced with actual values. | `{tool}`, `{path}`, `{command}` แทนที่ด้วยค่าจริง |
| **Config Files** | `.cluadex/hooks.json` (project) and `~/.cluadex/hooks.json` (global) | ไฟล์ config ระดับโปรเจกต์ และ global |

---

### Code Review / รีวิวโค้ด

| Feature | Description | คำอธิบาย |
|---------|-------------|----------|
| **One-Click Review** | Review button in status bar — analyzes all code changes in the session. | ปุ่มรีวิวในแถบสถานะ วิเคราะห์โค้ดทั้งหมดในเซสชัน |
| **5-Point Check** | Bugs, security issues, improvements, missing docs, good practices. | ตรวจ: บัก, ความปลอดภัย, การปรับปรุง, เอกสาร, แนวปฏิบัติที่ดี |
| **Star Rating** | Overall quality rating ⭐ 1-5 stars. | ให้คะแนนคุณภาพ ⭐ 1-5 ดาว |
| **Bilingual** | Reviews in Thai or English based on language setting. | รีวิวเป็นภาษาไทยหรืออังกฤษตามการตั้งค่า |

---

### Context & Token Tracking / ติดตาม Context และ Token

| Feature | Description | คำอธิบาย |
|---------|-------------|----------|
| **LED Context Bar** | 5-segment LED bar (Green → Teal → Yellow → Peach → Red) showing context usage at a glance. | แถบ LED 5 ช่อง แสดงการใช้ context อย่างรวดเร็ว |
| **Level Labels** | FRESH (0-25%) → OK (25-50%) → WARM (50-75%) → HOT (75-90%) → FULL (90-100%) | ป้ายระดับ: สด → ปกติ → อุ่น → ร้อน → เต็ม |
| **Per-Turn Stats** | Shows tokens, generation time, and tokens/second after each AI response. | แสดง token, เวลา, และความเร็วหลังแต่ละคำตอบ |
| **Session Warning** | At 75%: suggest new session. At 90%: urgent warning — AI quality degrades. | ที่ 75%: แนะนำเริ่มใหม่ ที่ 90%: เตือนด่วน คุณภาพ AI ลดลง |
| **Auto-Compaction** | History automatically compacted when context gets full, preserving important decisions. | บีบอัดประวัติอัตโนมัติเมื่อ context เต็ม เก็บการตัดสินใจสำคัญ |

---

### Plugin System / ระบบปลั๊กอิน

CluadeX includes a plugin system with a curated catalog of 20 ready-to-install plugins.

CluadeX มีระบบปลั๊กอินพร้อมแค็ตตาล็อก 20 ปลั๊กอินพร้อมติดตั้ง

#### Plugin Manager Features

| Feature | Description | คำอธิบาย |
|---------|-------------|----------|
| **Two Tabs** | "Installed" tab for managing installed plugins, "Catalog" tab for browsing available plugins. | แท็บ "ติดตั้งแล้ว" และ "แค็ตตาล็อก" |
| **One-Click Install** | Install plugins from catalog with a single click. Generates manifest.json automatically. | ติดตั้งปลั๊กอินจากแค็ตตาล็อกด้วยคลิกเดียว |
| **Search & Filter** | Search plugins by name, description, category, or tags. | ค้นหาปลั๊กอินตามชื่อ คำอธิบาย หมวดหมู่ หรือแท็ก |
| **Enable/Disable** | Toggle plugins on/off without uninstalling. | เปิด/ปิดปลั๊กอินโดยไม่ต้องถอนการติดตั้ง |
| **Custom Plugins** | Install custom plugins from any folder with `manifest.json`. | ติดตั้งปลั๊กอินเองจากโฟลเดอร์ที่มี manifest.json |

#### Curated Plugin Catalog (20 plugins)

| Category | Plugins |
|----------|---------|
| **Code Quality** | Auto Lint & Format, Auto Test Runner, Type Check Guard |
| **Security** | Secret Scanner, Dependency Auditor, Path Traversal Guard |
| **Git** | Smart Auto-Commit, Branch Protector, PR Template Generator |
| **Productivity** | Session Logger, TODO Tracker, Context Compactor |
| **Documentation** | Auto Documentation, Changelog Writer |
| **Integration** | Desktop Notifications, API Cost Tracker |
| **Safety** | Backup Before Edit, File Size Guard |
| **AI** | Prompt Enhancer, AI Code Reviewer |

Each plugin uses the Claude Code hook system format (PreToolUse, PostToolUse, SessionStart, SessionEnd, etc.).

---

### MCP (Model Context Protocol) / โปรโตคอล MCP

CluadeX supports MCP 2.0 for connecting external tool servers — bringing unlimited tool extensibility.

CluadeX รองรับ MCP 2.0 สำหรับเชื่อมต่อ tool server ภายนอก ขยายเครื่องมือได้ไม่จำกัด

| Feature | Description | คำอธิบาย |
|---------|-------------|----------|
| **MCP Server Manager** | Manage MCP servers with JSON-RPC 2.0 lifecycle control. | จัดการ MCP server ด้วย JSON-RPC 2.0 |
| **Stdio Transport** | Communicate with MCP servers via stdin/stdout. | สื่อสารกับ MCP server ผ่าน stdin/stdout |
| **Dynamic Tool Discovery** | Auto-discovers tools from connected MCP servers and registers them as agent tools. | ค้นพบเครื่องมือจาก MCP server อัตโนมัติและลงทะเบียนเป็น agent tool |
| **Qualified Tool Names** | MCP tools use `server::tool` naming to avoid conflicts. | MCP tool ใช้ชื่อแบบ `server::tool` ป้องกันชื่อซ้ำ |
| **MCP Servers UI** | Dedicated management page to add, configure, and monitor MCP servers. | หน้า UI เฉพาะสำหรับเพิ่ม ตั้งค่า และติดตาม MCP server |

---

### Security & Privacy / ความปลอดภัยและความเป็นส่วนตัว

| Feature | Description | คำอธิบาย |
|---------|-------------|----------|
| **DPAPI Key Encryption** | All API keys encrypted at rest using Windows Data Protection API. Only your Windows account can decrypt. | API Key ทั้งหมดเข้ารหัสด้วย Windows DPAPI |
| **Permission System** | Allow/Deny/Ask rules with tool-scoped patterns (e.g., `run_command(npm:*)`). `**` glob support. | กฎ อนุญาต/ปฏิเสธ/ถาม พร้อม wildcard pattern ระดับเครื่องมือ |
| **Dangerous Command Blocking** | Blocks 23+ dangerous shell command patterns (rm -rf, format, del, shutdown, reg delete, shell injection, etc.) automatically. | บล็อกรูปแบบคำสั่งอันตราย 23+ รายการอัตโนมัติ |
| **Path Traversal Protection** | Prevents access outside project directory. Rejects absolute paths and `../` traversal. | ป้องกันการเข้าถึงนอกไดเรกทอรีโปรเจกต์ |
| **Shell Injection Prevention** | Git commit messages use temp files, branch names are validated with regex, shell arguments are sanitized. | ป้องกัน Shell Injection ผ่านการตรวจสอบอินพุตอย่างเข้มงวด |
| **API Key Redaction** | API keys are automatically redacted from error messages shown to users. | API Key ถูกซ่อนจากข้อความ error อัตโนมัติ |
| **Per-Request Auth Headers** | Authentication headers are set per-request (not on shared HttpClient) to prevent leakage. | ตั้งค่า Auth Header แยกต่อ request เพื่อป้องกันการรั่วไหล |
| **Feature-Gated Execution** | Disabled tools are blocked at both prompt and execution level — AI can't use tools you've turned off. | เครื่องมือที่ปิดถูกบล็อกทั้งที่ prompt และ execution — AI ใช้ไม่ได้ |
| **Zero Telemetry** | CluadeX sends ZERO analytics or tracking data. Your code stays on your machine. | CluadeX ไม่ส่งข้อมูลใดๆ กลับไปที่ใด โค้ดอยู่ในเครื่องคุณเท่านั้น |

---

### Fun & Extras / สนุกสนานและเพิ่มเติม

| Feature | Description | คำอธิบาย |
|---------|-------------|----------|
| **Buddy Companion** | A cute AI companion pet generated from your user ID. 18 species, 5 rarity levels (Common/Uncommon/Rare/Epic/Legendary), 8 hat types, 1% shiny chance! Click to pet for hearts! Stats: Debugging, Patience, Chaos, Wisdom, Snark. | สัตว์เลี้ยง AI น่ารักที่สร้างจาก User ID 18 สายพันธุ์, 5 ระดับความหายาก, 8 หมวก, 1% เรืองแสง! |
| **Plugin System** | Extend CluadeX with 20 curated plugins or custom ones. Browse catalog, one-click install, enable/disable. | ขยายด้วย 20 ปลั๊กอินคัดสรรหรือปลั๊กอินเอง |
| **Task Manager** | Run background shell commands with status tracking, output capture, and stop/kill. | รันคำสั่ง Shell เบื้องหลังพร้อมติดตามสถานะ |

### Buddy Species / สายพันธุ์ Buddy ทั้งหมด (18 species)

| Emoji | Species | ชื่อไทย | | Emoji | Species | ชื่อไทย |
|-------|---------|---------|---|-------|---------|---------|
| 🦆 | Duck | เป็ด | | 🦉 | Owl | นกฮูก |
| 🪨 | Goose | ห่าน | | 🐧 | Penguin | เพนกวิน |
| 💧 | Blob | บล็อบ | | 🐢 | Turtle | เต่า |
| 🐱 | Cat | แมว | | 🐌 | Snail | หอยทาก |
| 🐉 | Dragon | มังกร | | 👻 | Ghost | ผี |
| 🐙 | Octopus | ปลาหมึก | | 🦎 | Axolotl | แอกโซลอเติล |
| 🦫 | Capybara | คาปิบารา | | 🤖 | Robot | หุ่นยนต์ |
| 🌵 | Cactus | กระบองเพชร | | 🐰 | Rabbit | กระต่าย |
| 🍄 | Mushroom | เห็ด | | 🐾 | Chonk | อ้วนกลม |

---

## Installation / การติดตั้ง

### Option 1: Download Release (Recommended)

1. Go to [Releases](https://github.com/xjanova/CluadeX/releases/latest)
2. Download `CluadeX-v*.zip`
3. Extract and run `CluadeX.exe`

### Option 2: Build from Source

```bash
git clone https://github.com/xjanova/CluadeX.git
cd CluadeX
dotnet build CluadeX/CluadeX.csproj -c Release
dotnet run --project CluadeX/CluadeX.csproj
```

### Requirements

- **OS**: Windows 10/11 (x64)
- **Runtime**: .NET 8.0 (bundled in release)
- **GPU** (optional): NVIDIA GPU with CUDA 12 support for local model acceleration
- **Disk**: 500MB+ for the app, varies for AI models (2-30GB per model)

---

## Configuration / การตั้งค่า

### API Keys

To use cloud AI providers, add your API keys in **Settings > AI Provider**:

| Provider | Where to get key |
|----------|-----------------|
| OpenAI | https://platform.openai.com/api-keys |
| Anthropic | https://console.anthropic.com/settings/keys |
| Google Gemini | https://aistudio.google.com/apikey |

All keys are encrypted using Windows DPAPI and stored in `settings.json`.

### Language / ภาษา

Switch between English and Thai in **Settings > Language**. The AI will respond in your chosen language automatically.

สลับภาษาได้ที่ **ตั้งค่า > ภาษา** AI จะตอบตามภาษาที่เลือกอัตโนมัติ

### Feature Toggles

Enable or disable optional features from the **Features** page in the sidebar. Toggleable features:

- Buddy Companion, Plugin System, Task Manager
- Git / GitHub Integration
- Web Fetch, Context Memory, Smart Editing
- Markdown Rendering
- Permission System, Dangerous Command Blocking

### Activation Key

Advanced features (cloud providers, Git/GitHub, web fetch, etc.) require an activation key. Enter in the **Features** page. Free tier includes: local inference, Ollama, chat persistence, buddy companion, GPU detection, and more.

ฟีเจอร์ขั้นสูง (cloud providers, Git/GitHub ฯลฯ) ต้องใส่ activation key ใส่ได้ที่หน้า **ฟีเจอร์** ฟรีเทียร์รวม: local inference, Ollama, แชท, buddy, GPU detection และอื่นๆ

---

## Data Storage / การจัดเก็บข้อมูล

| Location | Content |
|----------|---------|
| `%LocalAppData%\CluadeX\` | Default data root (installed mode) |
| `{exe_dir}\Data\` | Data root (portable mode) |
| `settings.json` | Configuration with encrypted API keys |
| `codex.db` | SQLite database with chat history and FTS5 index |
| `buddy.json` | Buddy companion soul (name, personality, hatch date) |
| `Models\` | Downloaded GGUF model files |
| `Sessions\` | Chat session storage |
| `Logs\` | Application logs (Serilog) |
| `plugins\` | Plugin directory with `manifest.json` per plugin |
| `plugins\plugins_config.json` | Enabled/disabled plugin configuration |
| `permissions.json` | Permission rules (with tool-scoped patterns) |
| `memory\` | Persistent memory files (MEMORY.md + per-memory .md) |
| `~/.cluadex/skills/` | User-global skill files |
| `~/.cluadex/memory/` | User-global memory files |
| `~/.cluadex/hooks.json` | User-global hook definitions |
| `.cluadex/hooks.json` | Project-specific hook definitions |
| `.cluadex/skills/` | Project-specific skill files |

---

## Architecture / สถาปัตยกรรม

```
CluadeX/
├── Assets/               # Logo, icons
├── Converters/           # WPF value converters (LED bar, context colors, etc.)
├── Models/               # Data models (AppSettings, BuddyModels, ChatMessage,
│                         #   PluginInfo, CatalogPlugin, PermissionRule, etc.)
├── Resources/            # XAML themes, styles (Catppuccin Mocha)
├── Services/             # Business logic (31 services)
│   ├── Providers/        # AI providers (Local, OpenAI, Anthropic, Gemini, Ollama, LlamaServer)
│   │   ├── IAiProvider          # Interface + native tool_use models (ToolSchema, NativeMessage, etc.)
│   │   ├── AnthropicProvider    # Native tool_use, extended thinking, prompt caching, cost recording
│   │   └── ...                  # OpenAI, Gemini, Ollama, LocalGguf, LlamaServer
│   ├── Mcp/              # Model Context Protocol (MCP 2.0)
│   │   ├── McpServerManager     # Server lifecycle, JSON-RPC 2.0
│   │   ├── McpStdioTransport    # Stdio transport
│   │   └── McpToolRegistry      # Tool discovery + qualified names
│   ├── AgentToolService     # 48 tools, native schemas, hooks integration
│   ├── CodeAgentService     # Dual-mode agentic loop (native + legacy), 10-section prompt
│   ├── CostTrackingService  # Per-model USD cost tracking (input/output/cache tokens)
│   ├── MemoryService        # 4-type persistent memory system (MEMORY.md index)
│   ├── HookService          # PreToolUse/PostToolUse shell hooks
│   ├── SkillService         # Skill discovery, YAML frontmatter, built-in skills
│   ├── PermissionService    # Wildcard + tool-scoped permission patterns
│   ├── ContextMemoryService # Token counting, AI-powered auto-compaction
│   ├── FileSystemService    # Safe file ops with path traversal protection
│   ├── GitService           # Git operations with input validation
│   ├── GitHubService        # GitHub CLI integration (gh)
│   ├── PluginService        # Plugin catalog (20 plugins), management
│   ├── SettingsService      # Settings with DPAPI encryption
│   ├── SmartEditingService  # Code validation, bracket checking
│   ├── TaskManagerService   # Background task execution
│   ├── WebFetchService      # HTTP + DuckDuckGo search
│   └── ...                  # Buddy, Localization, Activation, GPU, HuggingFace,
│                            #   AutoUpdate, BugReport, LspClient, XmanLicense, etc.
├── ViewModels/           # MVVM ViewModels
│   ├── ChatViewModel        # Chat, token tracking, LED context, review command
│   ├── FeaturesViewModel    # 27 features, 5 categories, activation key
│   ├── MainViewModel        # Navigation, buddy, localization
│   ├── PluginManagerViewModel # Installed + Catalog tabs, search/filter
│   ├── McpServersViewModel   # MCP server management
│   └── ...                  # ModelManager, Settings, Permissions, Tasks
├── Views/                # WPF UserControls (8 pages)
│   ├── ChatView             # Chat with LED context bar, per-turn stats, review button
│   ├── FeaturesView         # Feature catalog with toggle switches
│   ├── PluginManagerView    # Two-tab plugin manager with card catalog
│   ├── McpServersView       # MCP server configuration and monitoring
│   └── ...                  # ModelManager, Settings, Permissions, Tasks
├── App.xaml              # Application resources, global styles
├── App.xaml.cs           # DI container (31 services, 9 ViewModels)
├── MainWindow.xaml       # Sidebar, buddy widget, navigation, 8 pages
└── MainWindow.xaml.cs    # Window chrome, buddy pet handler
```

---

## Tech Stack

| Technology | Purpose |
|-----------|---------|
| WPF (.NET 8) | Desktop UI framework |
| LLamaSharp 0.26.0 | Local GGUF model inference (CUDA 12 + CPU) |
| Microsoft.Data.Sqlite 9.0.0 | Chat persistence with FTS5 full-text search |
| Markdig 0.37.0 | Markdown parsing and rendering |
| Serilog | Structured logging to disk |
| Microsoft.Extensions.FileSystemGlobbing | Glob pattern file matching |
| System.Security.Cryptography.ProtectedData | DPAPI encryption for API keys |
| System.Management | WMI-based GPU/hardware detection |

---

## Privacy / ความเป็นส่วนตัว

**CluadeX collects ZERO telemetry, analytics, or tracking data.**

- No crash reporting · No usage statistics · No device fingerprinting
- No phone-home functionality · All data stays on your machine
- API calls go only to the provider you configure

**CluadeX ไม่ส่งข้อมูลใดๆ กลับไปที่ใดทั้งสิ้น**

- ไม่มีการรายงาน crash · ไม่มีสถิติการใช้งาน · ไม่มีการระบุตัวอุปกรณ์
- ข้อมูลทั้งหมดอยู่ในเครื่องของคุณ · API calls ไปเฉพาะผู้ให้บริการที่คุณเลือก

---

## License

MIT License - see [LICENSE](LICENSE) file.

---

<p align="center">
  Made with ❤️ by <a href="https://xman4289.com">Xman Studio</a>
</p>
