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
| **Local GGUF Inference** | Run AI models locally on your GPU/CPU with LLamaSharp. No internet required, fully private. | รันโมเดล AI บนเครื่องด้วย GPU/CPU ไม่ต้องใช้อินเทอร์เน็ต ข้อมูลเป็นส่วนตัว 100% |
| **Multi-Provider AI** | Switch between Local, OpenAI, Anthropic, Google Gemini, and Ollama providers instantly. | สลับระหว่าง Local, OpenAI, Anthropic, Google Gemini และ Ollama ได้ทันที |
| **Chat Persistence (SQLite + FTS5)** | All conversations saved locally with full-text search. Never lose your chat history. | บันทึกการสนทนาทั้งหมดในเครื่องพร้อมค้นหาข้อความเต็ม ไม่สูญหาย |
| **Markdown & Syntax Highlighting** | Rich text rendering with code blocks, tables, and syntax-highlighted code snippets. | แสดงผลข้อความสวยงามพร้อมบล็อกโค้ด ตาราง และไฮไลท์โค้ดสีสัน |
| **GPU Auto-Detection** | Automatically detects your NVIDIA GPU and VRAM for optimal model loading. | ตรวจจับ GPU NVIDIA และ VRAM อัตโนมัติเพื่อโหลดโมเดลได้เหมาะสม |
| **Catppuccin Mocha Theme** | Beautiful dark theme with metallic gradients, drop shadows, and rounded corners. | ธีมมืดสวยงามพร้อมกราเดียนท์เมทัลลิก เงาตกกระทบ และขอบมน |
| **HuggingFace Model Hub** | Browse, search, and download GGUF models directly from HuggingFace Hub. | ค้นหาและดาวน์โหลดโมเดล GGUF จาก HuggingFace Hub ได้โดยตรง |
| **Thai/English Localization** | Full Thai and English language support. Switch anytime from Settings. | รองรับภาษาไทยและอังกฤษเต็มรูปแบบ สลับได้ทุกเมื่อจากหน้าตั้งค่า |
| **Feature Toggles** | Enable or disable optional features from the Features page. | เปิด/ปิดฟีเจอร์เสริมได้จากหน้า Features |
| **Portable Mode** | Place a `portable` or `portable.txt` file next to the exe to store all data locally. | วางไฟล์ `portable` ข้างไฟล์ exe เพื่อเก็บข้อมูลทั้งหมดในโฟลเดอร์เดียวกัน |

---

### AI Providers / ผู้ให้บริการ AI

| Provider | Requirements | คำอธิบาย |
|----------|-------------|----------|
| **Local GGUF** | GPU with VRAM (CUDA 12) or CPU | รันโมเดล GGUF บนเครื่อง ไม่ต้องใช้อินเทอร์เน็ต ฟรี |
| **OpenAI** | OpenAI API Key | ใช้ GPT-4o, o1, o3 ต้องมี API Key ของ OpenAI |
| **Anthropic** | Anthropic API Key | ใช้ Claude Sonnet/Opus ต้องมี API Key ของ Anthropic |
| **Google Gemini** | Google AI API Key | ใช้ Gemini Pro/Ultra ต้องมี Google AI API Key |
| **Ollama** | Ollama installed locally | เชื่อมต่อ Ollama server บนเครื่อง ฟรี ต้องติดตั้ง Ollama |

> API keys are encrypted at rest using Windows DPAPI. Only your Windows account can decrypt them.
>
> API Key ทั้งหมดเข้ารหัสด้วย Windows DPAPI มีเฉพาะบัญชี Windows ของคุณเท่านั้นที่ถอดรหัสได้

---

### Agent Tools / เครื่องมือ Agent

CluadeX includes a powerful agent system with these tools:

| Tool | Description | คำอธิบาย |
|------|-------------|----------|
| **Code Execution** | Execute code snippets in multiple languages with sandboxed environment. | รันโค้ดหลายภาษาในสภาพแวดล้อมที่ปลอดภัย (Sandbox) |
| **File System** | Read, write, search, and manage project files with permission controls. | อ่าน เขียน ค้นหา และจัดการไฟล์โปรเจกต์พร้อมระบบควบคุมสิทธิ์ |
| **Git Integration** | Full Git operations: commit, branch, merge, diff, stash, tag — all from chat. Validates branch names and sanitizes commit messages. | ใช้ Git ครบ: commit, branch, merge, diff, stash, tag ทั้งหมดจากแชท |
| **GitHub Integration** | Create PRs, issues, search repos, clone projects via GitHub CLI. | สร้าง PR, Issues, ค้นหา repo, clone โปรเจกต์ผ่าน GitHub CLI |
| **Web Fetch** | Fetch web pages and APIs, convert HTML to text for analysis. | ดึงหน้าเว็บและ API แปลง HTML เป็นข้อความเพื่อวิเคราะห์ |
| **Context Memory** | Smart context management to keep relevant code and files in memory. | จัดการบริบทอัจฉริยะ เก็บโค้ดและไฟล์ที่เกี่ยวข้องไว้ในหน่วยความจำ |
| **Smart Code Editing** | AI-powered code editing with auto-fix, refactoring, and intelligent suggestions. | แก้โค้ดด้วย AI พร้อม auto-fix, refactoring และคำแนะนำอัจฉริยะ |

---

### Security & Privacy / ความปลอดภัยและความเป็นส่วนตัว

| Feature | Description | คำอธิบาย |
|---------|-------------|----------|
| **DPAPI Key Encryption** | All API keys encrypted at rest using Windows Data Protection API. Only your Windows account can decrypt. | API Key ทั้งหมดเข้ารหัสด้วย Windows DPAPI มีเฉพาะบัญชี Windows ของคุณเท่านั้นที่ถอดรหัสได้ |
| **Permission System** | Allow/Deny/Ask rules for file, command, and network access. First-match-wins. | กฎ อนุญาต/ปฏิเสธ/ถาม สำหรับการเข้าถึงไฟล์ คำสั่ง และเครือข่าย |
| **Dangerous Command Blocking** | Blocks 30+ dangerous shell commands (rm -rf, format, del, shutdown, reg delete, etc.) automatically. | บล็อกคำสั่งอันตราย 30+ รายการอัตโนมัติ |
| **Path Traversal Protection** | Prevents access outside project directory. Rejects absolute paths and `../` traversal. | ป้องกันการเข้าถึงนอกไดเรกทอรีโปรเจกต์ |
| **Shell Injection Prevention** | Git commit messages use temp files, branch names are validated with regex, shell arguments are sanitized. | ป้องกัน Shell Injection ผ่านการตรวจสอบอินพุตอย่างเข้มงวด |
| **API Key Redaction** | API keys are automatically redacted from error messages shown to users. | API Key ถูกซ่อนจากข้อความ error อัตโนมัติ |
| **Per-Request Auth Headers** | Authentication headers are set per-request (not on shared HttpClient) to prevent leakage. | ตั้งค่า Auth Header แยกต่อ request เพื่อป้องกันการรั่วไหล |
| **Zero Telemetry** | CluadeX sends ZERO analytics or tracking data. Your code stays on your machine. | CluadeX ไม่ส่งข้อมูลใดๆ กลับไปที่ใด โค้ดของคุณอยู่ในเครื่องคุณเท่านั้น |

---

### Fun & Extras / สนุกสนานและเพิ่มเติม

| Feature | Description | คำอธิบาย |
|---------|-------------|----------|
| **Buddy Companion** | A cute AI companion pet generated from your user ID. 18 species, 5 rarity levels (Common/Uncommon/Rare/Epic/Legendary), 8 hat types, 1% shiny chance! Click to pet for hearts! Stats: Debugging, Patience, Chaos, Wisdom, Snark. | สัตว์เลี้ยง AI น่ารักที่สร้างจาก User ID ของคุณ มี 18 สายพันธุ์, 5 ระดับความหายาก, 8 แบบหมวก, โอกาส 1% เรืองแสง! คลิกเพื่อลูบ! |
| **Plugin System** | Extend CluadeX with custom plugins. Browse, install, enable/disable from Plugin Manager. Scans `{DataRoot}/plugins/` for `manifest.json`. | ขยายความสามารถด้วยปลั๊กอิน เรียกดู ติดตั้ง เปิด/ปิด จาก Plugin Manager |
| **Task Manager** | Run background shell commands with status tracking, output capture, and stop/kill. | รันคำสั่ง Shell เบื้องหลังพร้อมติดตามสถานะ บันทึกผลลัพธ์ และหยุด/ฆ่ากระบวนการ |

---

### Buddy Species / สายพันธุ์ Buddy ทั้งหมด

| Emoji | Species | ชื่อไทย |
|-------|---------|---------|
| 🦆 | Duck | เป็ด |
| 🪨 | Goose | ห่าน |
| 💧 | Blob | บล็อบ |
| 🐱 | Cat | แมว |
| 🐉 | Dragon | มังกร |
| 🐙 | Octopus | ปลาหมึก |
| 🦉 | Owl | นกฮูก |
| 🐧 | Penguin | เพนกวิน |
| 🐢 | Turtle | เต่า |
| 🐌 | Snail | หอยทาก |
| 👻 | Ghost | ผี |
| 🦎 | Axolotl | แอกโซลอเติล |
| 🦫 | Capybara | คาปิบารา |
| 🌵 | Cactus | กระบองเพชร |
| 🤖 | Robot | หุ่นยนต์ |
| 🐰 | Rabbit | กระต่าย |
| 🍄 | Mushroom | เห็ด |
| 🐾 | Chonk | อ้วนกลม |

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

Switch between English and Thai in **Settings > Language**.

สลับภาษาได้ที่ **ตั้งค่า > ภาษา**

### Feature Toggles

Enable or disable optional features from the **Features** page in the sidebar. Toggleable features include:

- Buddy Companion
- Plugin System
- Task Manager
- Git / GitHub Integration
- Web Fetch
- Context Memory
- Smart Editing
- Markdown Rendering
- Permission System
- Dangerous Command Blocking

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
| `plugins\` | Plugin directory (scans for `manifest.json`) |
| `permissions.json` | Permission rules |

---

## Architecture / สถาปัตยกรรม

```
CluadeX/
├── Assets/               # Logo, icons
├── Converters/           # WPF value converters (frozen brushes for perf)
├── Models/               # Data models (AppSettings, BuddyModels, ChatMessage, etc.)
├── Resources/            # XAML themes, styles (Catppuccin Mocha)
├── Services/             # Business logic
│   ├── Providers/        # AI providers (Local, OpenAI, Anthropic, Gemini, Ollama)
│   ├── AgentToolService  # Agent tool orchestration with permission checks
│   ├── BuddyService      # Deterministic companion generation (FNV-1a + Mulberry32 PRNG)
│   ├── CodeExecutionService # Sandboxed code execution
│   ├── FileSystemService    # Safe file operations with path traversal protection
│   ├── GitService           # Git operations with input validation
│   ├── GitHubService        # GitHub CLI integration with shell arg sanitization
│   ├── LocalizationService  # Thai/English i18n (150+ translations)
│   ├── PermissionService    # Allow/Deny/Ask permission rules
│   ├── PluginService        # Plugin discovery and management
│   ├── SettingsService      # Settings with DPAPI encryption
│   ├── TaskManagerService   # Background task execution
│   └── WebFetchService      # HTTP fetching
├── ViewModels/           # MVVM ViewModels
│   ├── ChatViewModel     # Chat logic, session management, search
│   ├── FeaturesViewModel  # Feature catalog with toggles
│   ├── MainViewModel      # Navigation, buddy, localization
│   └── ...               # ModelManager, Settings, Plugins, Permissions, Tasks
├── Views/                # WPF UserControls
│   ├── ChatView           # Chat interface
│   ├── FeaturesView       # Feature catalog page
│   └── ...               # ModelManager, Settings, Plugins, Permissions, Tasks
├── App.xaml              # Application resources, global styles
├── App.xaml.cs           # DI container configuration
├── MainWindow.xaml       # Main window with sidebar, buddy widget, navigation
└── MainWindow.xaml.cs    # Window chrome, buddy pet handler
```

---

## Tech Stack

| Technology | Purpose |
|-----------|---------|
| WPF (.NET 8) | Desktop UI framework |
| LLamaSharp 0.25.0 | Local GGUF model inference (CUDA 12 + CPU) |
| Microsoft.Data.Sqlite | Chat persistence with FTS5 full-text search |
| Markdig | Markdown parsing and rendering |
| Serilog | Structured logging |
| System.Security.Cryptography.ProtectedData | DPAPI encryption for API keys |
| System.Management | WMI-based GPU/hardware detection |

---

## Privacy / ความเป็นส่วนตัว

**CluadeX collects ZERO telemetry, analytics, or tracking data.**

- No crash reporting
- No usage statistics
- No device fingerprinting
- No phone-home functionality
- All data stays on your machine
- API calls go only to the provider you configure

**CluadeX ไม่ส่งข้อมูลใดๆ กลับไปที่ใดทั้งสิ้น**

- ไม่มีการรายงาน crash
- ไม่มีสถิติการใช้งาน
- ไม่มีการระบุตัวอุปกรณ์
- ข้อมูลทั้งหมดอยู่ในเครื่องของคุณ
- API calls ไปเฉพาะผู้ให้บริการที่คุณเลือก

---

## License

MIT License - see [LICENSE](LICENSE) file.

---

<p align="center">
  Made with ❤️ by <a href="https://xman4289.com">Xman Studio</a>
</p>
