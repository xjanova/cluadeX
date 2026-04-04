using System.Globalization;

namespace CluadeX.Services;

/// <summary>
/// Localization service supporting Thai and English languages.
/// Uses dictionary-based translations with fallback to English.
/// </summary>
public class LocalizationService
{
    private string _currentLanguage = "en";
    public string CurrentLanguage => _currentLanguage;
    public event Action? LanguageChanged;

    // ─── Translation Dictionaries ────────────────────────────────

    private static readonly Dictionary<string, Dictionary<string, string>> Translations = new()
    {
        // ═══════ NAVIGATION ═══════
        ["nav.chat"] = new() { ["en"] = "Chat", ["th"] = "แชท" },
        ["nav.models"] = new() { ["en"] = "Models", ["th"] = "โมเดล" },
        ["nav.settings"] = new() { ["en"] = "Settings", ["th"] = "ตั้งค่า" },
        ["nav.plugins"] = new() { ["en"] = "Plugins", ["th"] = "ปลั๊กอิน" },
        ["nav.permissions"] = new() { ["en"] = "Permissions", ["th"] = "สิทธิ์" },
        ["nav.tasks"] = new() { ["en"] = "Tasks", ["th"] = "งาน" },
        ["nav.features"] = new() { ["en"] = "Features", ["th"] = "ฟีเจอร์" },
        ["nav.mcpServers"] = new() { ["en"] = "MCP Servers", ["th"] = "เซิร์ฟเวอร์ MCP" },
        ["mcp.title"] = new() { ["en"] = "MCP Servers", ["th"] = "เซิร์ฟเวอร์ MCP" },
        ["mcp.subtitle"] = new() { ["en"] = "Manage Model Context Protocol servers — add tools and integrations for the AI agent", ["th"] = "จัดการเซิร์ฟเวอร์ MCP — เพิ่มเครื่องมือและการเชื่อมต่อสำหรับ AI agent" },

        // ═══════ SIDEBAR ═══════
        ["sidebar.newChat"] = new() { ["en"] = "New Chat", ["th"] = "แชทใหม่" },
        ["sidebar.openFolder"] = new() { ["en"] = "Open Folder", ["th"] = "เปิดโฟลเดอร์" },
        ["sidebar.chatHistory"] = new() { ["en"] = "CHAT HISTORY", ["th"] = "ประวัติแชท" },
        ["sidebar.noConversations"] = new() { ["en"] = "No conversations yet.\nClick 'New Chat' to start!", ["th"] = "ยังไม่มีการสนทนา\nคลิก 'แชทใหม่' เพื่อเริ่ม!" },
        ["sidebar.noResults"] = new() { ["en"] = "No results found.", ["th"] = "ไม่พบผลลัพธ์" },
        ["sidebar.deleteSession"] = new() { ["en"] = "Delete session", ["th"] = "ลบเซสชัน" },
        ["sidebar.clearSearch"] = new() { ["en"] = "Clear search", ["th"] = "ล้างการค้นหา" },

        // ═══════ TITLEBAR ═══════
        ["title.subtitle"] = new() { ["en"] = " — AI Coding Assistant", ["th"] = " — ผู้ช่วยเขียนโค้ด AI" },

        // ═══════ SETTINGS PAGE ═══════
        ["settings.title"] = new() { ["en"] = "Settings", ["th"] = "ตั้งค่า" },
        ["settings.subtitle"] = new() { ["en"] = "Configure CluadeX preferences and paths", ["th"] = "ตั้งค่าการใช้งาน CluadeX" },
        ["settings.provider"] = new() { ["en"] = "AI Provider", ["th"] = "ผู้ให้บริการ AI" },
        ["settings.activeProvider"] = new() { ["en"] = "Active Provider", ["th"] = "ผู้ให้บริการที่ใช้งาน" },
        ["settings.language"] = new() { ["en"] = "Language / ภาษา", ["th"] = "ภาษา / Language" },
        ["settings.languageDesc"] = new() { ["en"] = "Switch between English and Thai", ["th"] = "สลับระหว่างภาษาอังกฤษและภาษาไทย" },
        ["settings.inference"] = new() { ["en"] = "Inference Settings", ["th"] = "ตั้งค่าการประมวลผล" },
        ["settings.temperature"] = new() { ["en"] = "Temperature", ["th"] = "อุณหภูมิ (Temperature)" },
        ["settings.maxTokens"] = new() { ["en"] = "Max Tokens", ["th"] = "จำนวนโทเค็นสูงสุด" },
        ["settings.contextSize"] = new() { ["en"] = "Context Size", ["th"] = "ขนาด Context" },
        ["settings.agent"] = new() { ["en"] = "Agent Settings", ["th"] = "ตั้งค่า Agent" },
        ["settings.autoExecute"] = new() { ["en"] = "Auto Execute Code", ["th"] = "รันโค้ดอัตโนมัติ" },
        ["settings.directories"] = new() { ["en"] = "Directories", ["th"] = "ไดเรกทอรี" },
        ["settings.saved"] = new() { ["en"] = "Settings saved!", ["th"] = "บันทึกแล้ว!" },
        ["settings.featureToggles"] = new() { ["en"] = "Feature Toggles", ["th"] = "เปิด/ปิดฟีเจอร์" },
        ["settings.featureTogglesDesc"] = new() { ["en"] = "Enable or disable optional features", ["th"] = "เปิดหรือปิดฟีเจอร์เสริม" },

        // ═══════ FEATURES PAGE ═══════
        ["features.title"] = new() { ["en"] = "Features & Capabilities", ["th"] = "ฟีเจอร์และความสามารถ" },
        ["features.subtitle"] = new() { ["en"] = "Everything CluadeX can do for you", ["th"] = "ทุกสิ่งที่ CluadeX ทำได้" },
        ["features.core"] = new() { ["en"] = "Core Features", ["th"] = "ฟีเจอร์หลัก" },
        ["features.coreDesc"] = new() { ["en"] = "Built-in features available to everyone", ["th"] = "ฟีเจอร์พื้นฐานที่ทุกคนใช้ได้" },
        ["features.advanced"] = new() { ["en"] = "Advanced Features", ["th"] = "ฟีเจอร์ขั้นสูง" },
        ["features.advancedDesc"] = new() { ["en"] = "Requires API key from cloud providers", ["th"] = "ต้องใส่ API Key จากผู้ให้บริการ Cloud" },
        ["features.fun"] = new() { ["en"] = "Fun & Extras", ["th"] = "สนุกสนานและเพิ่มเติม" },
        ["features.funDesc"] = new() { ["en"] = "Optional fun features and customization", ["th"] = "ฟีเจอร์สนุกสนานและการปรับแต่งเพิ่มเติม" },
        ["features.tools"] = new() { ["en"] = "Agent Tools", ["th"] = "เครื่องมือ Agent" },
        ["features.toolsDesc"] = new() { ["en"] = "AI-powered tools for coding assistance", ["th"] = "เครื่องมือ AI สำหรับช่วยเขียนโค้ด" },
        ["features.security"] = new() { ["en"] = "Security & Privacy", ["th"] = "ความปลอดภัยและความเป็นส่วนตัว" },
        ["features.securityDesc"] = new() { ["en"] = "Protection and data safety features", ["th"] = "ฟีเจอร์ปกป้องข้อมูลและความปลอดภัย" },
        ["features.enabled"] = new() { ["en"] = "Enabled", ["th"] = "เปิดใช้งาน" },
        ["features.disabled"] = new() { ["en"] = "Disabled", ["th"] = "ปิดใช้งาน" },
        ["features.requiresKey"] = new() { ["en"] = "Requires API Key", ["th"] = "ต้องมี API Key" },
        ["features.free"] = new() { ["en"] = "Free", ["th"] = "ฟรี" },
        ["features.configureKey"] = new() { ["en"] = "Configure Key in Settings", ["th"] = "ตั้งค่า Key ในหน้าตั้งค่า" },

        // ═══════ FEATURE ITEMS ═══════
        // Core
        ["feature.localInference"] = new() { ["en"] = "Local GGUF Inference", ["th"] = "ประมวลผล GGUF บนเครื่อง" },
        ["feature.localInference.desc"] = new() { ["en"] = "Run AI models locally on your GPU/CPU with LLamaSharp. No internet required, fully private.", ["th"] = "รันโมเดล AI บนเครื่องของคุณด้วย GPU/CPU ผ่าน LLamaSharp ไม่ต้องใช้อินเทอร์เน็ต ข้อมูลเป็นส่วนตัว 100%" },
        ["feature.multiProvider"] = new() { ["en"] = "Multi-Provider AI", ["th"] = "รองรับหลายผู้ให้บริการ AI" },
        ["feature.multiProvider.desc"] = new() { ["en"] = "Switch between Local, OpenAI, Anthropic, Google Gemini, and Ollama providers.", ["th"] = "สลับระหว่าง Local, OpenAI, Anthropic, Google Gemini และ Ollama ได้ทันที" },
        ["feature.chatPersistence"] = new() { ["en"] = "Chat Persistence (SQLite)", ["th"] = "บันทึกแชทถาวร (SQLite)" },
        ["feature.chatPersistence.desc"] = new() { ["en"] = "All conversations saved locally with full-text search (FTS5). Never lose your chat history.", ["th"] = "บันทึกการสนทนาทั้งหมดในเครื่องพร้อมค้นหาข้อความเต็ม (FTS5) ไม่สูญหาย" },
        ["feature.markdown"] = new() { ["en"] = "Markdown & Syntax Highlighting", ["th"] = "Markdown และไฮไลท์โค้ด" },
        ["feature.markdown.desc"] = new() { ["en"] = "Rich text rendering with code blocks, tables, and syntax-highlighted code snippets.", ["th"] = "แสดงผลข้อความสวยงามพร้อมบล็อกโค้ด ตาราง และไฮไลท์โค้ดสีสัน" },
        ["feature.gpuDetection"] = new() { ["en"] = "GPU Auto-Detection", ["th"] = "ตรวจจับ GPU อัตโนมัติ" },
        ["feature.gpuDetection.desc"] = new() { ["en"] = "Automatically detects your NVIDIA GPU and VRAM for optimal model loading.", ["th"] = "ตรวจจับ GPU NVIDIA และ VRAM อัตโนมัติเพื่อโหลดโมเดลได้เหมาะสม" },
        ["feature.darkTheme"] = new() { ["en"] = "Catppuccin Mocha Theme", ["th"] = "ธีม Catppuccin Mocha" },
        ["feature.darkTheme.desc"] = new() { ["en"] = "Beautiful dark theme with metallic gradients, drop shadows, and rounded corners.", ["th"] = "ธีมมืดสวยงามพร้อมกราเดียนท์เมทัลลิก เงาตกกระทบ และขอบมน" },
        ["feature.huggingface"] = new() { ["en"] = "HuggingFace Model Hub", ["th"] = "ดาวน์โหลดจาก HuggingFace" },
        ["feature.huggingface.desc"] = new() { ["en"] = "Browse, search, and download GGUF models directly from HuggingFace Hub.", ["th"] = "ค้นหาและดาวน์โหลดโมเดล GGUF จาก HuggingFace Hub ได้โดยตรง" },
        ["feature.i18n"] = new() { ["en"] = "Thai/English Localization", ["th"] = "รองรับภาษาไทย/อังกฤษ" },
        ["feature.i18n.desc"] = new() { ["en"] = "Full Thai and English language support. Switch anytime from Settings.", ["th"] = "รองรับภาษาไทยและอังกฤษเต็มรูปแบบ สลับได้ทุกเมื่อจากหน้าตั้งค่า" },

        // Advanced (requires API key)
        ["feature.openai"] = new() { ["en"] = "OpenAI (GPT-4, o1, o3)", ["th"] = "OpenAI (GPT-4, o1, o3)" },
        ["feature.openai.desc"] = new() { ["en"] = "Use OpenAI's GPT-4o, o1, o3 models for coding. Requires OpenAI API key.", ["th"] = "ใช้โมเดล GPT-4o, o1, o3 ของ OpenAI สำหรับเขียนโค้ด ต้องมี API Key ของ OpenAI" },
        ["feature.anthropic"] = new() { ["en"] = "Anthropic (Claude 4)", ["th"] = "Anthropic (Claude 4)" },
        ["feature.anthropic.desc"] = new() { ["en"] = "Use Anthropic's Claude Sonnet/Opus for advanced coding assistance. Requires Anthropic API key.", ["th"] = "ใช้ Claude Sonnet/Opus ของ Anthropic สำหรับช่วยเขียนโค้ดขั้นสูง ต้องมี API Key ของ Anthropic" },
        ["feature.gemini"] = new() { ["en"] = "Google Gemini", ["th"] = "Google Gemini" },
        ["feature.gemini.desc"] = new() { ["en"] = "Use Google's Gemini Pro/Ultra models. Requires Google AI API key.", ["th"] = "ใช้โมเดล Gemini Pro/Ultra ของ Google ต้องมี Google AI API Key" },
        ["feature.ollama"] = new() { ["en"] = "Ollama Integration", ["th"] = "เชื่อมต่อ Ollama" },
        ["feature.ollama.desc"] = new() { ["en"] = "Connect to local Ollama server for running models. Free — requires Ollama installed.", ["th"] = "เชื่อมต่อ Ollama เพื่อรันโมเดลบนเครื่อง ฟรี — ต้องติดตั้ง Ollama ก่อน" },

        // Agent Tools
        ["feature.codeExecution"] = new() { ["en"] = "Code Execution", ["th"] = "รันโค้ด" },
        ["feature.codeExecution.desc"] = new() { ["en"] = "Execute code snippets in multiple languages with sandboxed environment.", ["th"] = "รันโค้ดหลายภาษาในสภาพแวดล้อมที่ปลอดภัย (Sandbox)" },
        ["feature.fileSystem"] = new() { ["en"] = "File System Tools", ["th"] = "จัดการไฟล์" },
        ["feature.fileSystem.desc"] = new() { ["en"] = "Read, write, search, and manage project files with permission controls.", ["th"] = "อ่าน เขียน ค้นหา และจัดการไฟล์โปรเจกต์พร้อมระบบควบคุมสิทธิ์" },
        ["feature.git"] = new() { ["en"] = "Git Integration", ["th"] = "จัดการ Git" },
        ["feature.git.desc"] = new() { ["en"] = "Full Git operations: commit, branch, merge, diff, stash, tag — all from chat.", ["th"] = "ใช้ Git ครบ: commit, branch, merge, diff, stash, tag — ทั้งหมดจากแชท" },
        ["feature.github"] = new() { ["en"] = "GitHub Integration", ["th"] = "เชื่อมต่อ GitHub" },
        ["feature.github.desc"] = new() { ["en"] = "Create PRs, issues, search repos, clone projects via GitHub CLI.", ["th"] = "สร้าง PR, Issues, ค้นหา repo, clone โปรเจกต์ผ่าน GitHub CLI" },
        ["feature.webFetch"] = new() { ["en"] = "Web Fetch", ["th"] = "ดึงข้อมูลเว็บ" },
        ["feature.webFetch.desc"] = new() { ["en"] = "Fetch web pages and APIs, convert HTML to text for analysis.", ["th"] = "ดึงหน้าเว็บและ API แปลง HTML เป็นข้อความเพื่อวิเคราะห์" },
        ["feature.contextMemory"] = new() { ["en"] = "Context Memory", ["th"] = "หน่วยความจำบริบท" },
        ["feature.contextMemory.desc"] = new() { ["en"] = "Smart context management to keep relevant code and files in memory.", ["th"] = "จัดการบริบทอัจฉริยะ เก็บโค้ดและไฟล์ที่เกี่ยวข้องไว้ในหน่วยความจำ" },
        ["feature.smartEditing"] = new() { ["en"] = "Smart Code Editing", ["th"] = "แก้โค้ดอัจฉริยะ" },
        ["feature.smartEditing.desc"] = new() { ["en"] = "AI-powered code editing with auto-fix, refactoring, and intelligent suggestions.", ["th"] = "แก้โค้ดด้วย AI พร้อม auto-fix, refactoring และคำแนะนำอัจฉริยะ" },

        // Security
        ["feature.dpapi"] = new() { ["en"] = "DPAPI Key Encryption", ["th"] = "เข้ารหัส Key ด้วย DPAPI" },
        ["feature.dpapi.desc"] = new() { ["en"] = "All API keys encrypted at rest using Windows Data Protection API. Only your Windows account can decrypt.", ["th"] = "API Key ทั้งหมดเข้ารหัสด้วย Windows DPAPI มีเฉพาะบัญชี Windows ของคุณเท่านั้นที่ถอดรหัสได้" },
        ["feature.permissions"] = new() { ["en"] = "Permission System", ["th"] = "ระบบสิทธิ์" },
        ["feature.permissions.desc"] = new() { ["en"] = "Allow/Deny/Ask rules for file, command, and network access. First-match-wins.", ["th"] = "กฎ อนุญาต/ปฏิเสธ/ถาม สำหรับการเข้าถึงไฟล์ คำสั่ง และเครือข่าย" },
        ["feature.commandBlock"] = new() { ["en"] = "Dangerous Command Blocking", ["th"] = "บล็อกคำสั่งอันตราย" },
        ["feature.commandBlock.desc"] = new() { ["en"] = "Blocks 30+ dangerous shell commands (rm -rf, format, del, etc.) automatically.", ["th"] = "บล็อกคำสั่งอันตราย 30+ รายการ (rm -rf, format, del ฯลฯ) อัตโนมัติ" },
        ["feature.pathSafety"] = new() { ["en"] = "Path Traversal Protection", ["th"] = "ป้องกัน Path Traversal" },
        ["feature.pathSafety.desc"] = new() { ["en"] = "Prevents access outside project directory. Rejects absolute paths and ../ traversal.", ["th"] = "ป้องกันการเข้าถึงนอกไดเรกทอรีโปรเจกต์ ปฏิเสธ absolute path และ ../" },
        ["feature.noTelemetry"] = new() { ["en"] = "Zero Telemetry", ["th"] = "ไม่มี Telemetry" },
        ["feature.noTelemetry.desc"] = new() { ["en"] = "CluadeX sends ZERO analytics or tracking data. Your code stays on your machine.", ["th"] = "CluadeX ไม่ส่งข้อมูลใดๆ กลับไปที่ใด โค้ดของคุณอยู่ในเครื่องคุณเท่านั้น" },

        // Fun
        ["feature.buddy"] = new() { ["en"] = "Buddy Companion", ["th"] = "สัตว์เลี้ยง Buddy" },
        ["feature.buddy.desc"] = new() { ["en"] = "A cute AI companion pet generated from your user ID. 18 species, 5 rarity levels, hats, shiny variants! Pet it for hearts!", ["th"] = "สัตว์เลี้ยง AI น่ารักที่สร้างจาก User ID ของคุณ มี 18 สายพันธุ์, 5 ระดับความหายาก, หมวก, แบบเรืองแสง! ลูบเพื่อให้หัวใจ!" },
        ["feature.plugins"] = new() { ["en"] = "Plugin System", ["th"] = "ระบบปลั๊กอิน" },
        ["feature.plugins.desc"] = new() { ["en"] = "Extend CluadeX with custom plugins. Browse, install, enable/disable from Plugin Manager.", ["th"] = "ขยายความสามารถด้วยปลั๊กอิน เรียกดู ติดตั้ง เปิด/ปิด จาก Plugin Manager" },
        ["feature.taskManager"] = new() { ["en"] = "Task Manager", ["th"] = "จัดการงาน" },
        ["feature.taskManager.desc"] = new() { ["en"] = "Run background shell commands with status tracking, output capture, and stop/kill.", ["th"] = "รันคำสั่ง Shell เบื้องหลังพร้อมติดตามสถานะ บันทึกผลลัพธ์ และหยุด/ฆ่ากระบวนการ" },

        // ═══════ PLUGIN MANAGER ═══════
        ["plugins.title"] = new() { ["en"] = "Plugin Manager", ["th"] = "จัดการปลั๊กอิน" },
        ["plugins.subtitle"] = new() { ["en"] = "Browse catalog, install, and manage plugins", ["th"] = "เรียกดูแค็ตตาล็อก ติดตั้ง และจัดการปลั๊กอิน" },
        ["plugins.installed"] = new() { ["en"] = "Installed", ["th"] = "ติดตั้งแล้ว" },
        ["plugins.catalog"] = new() { ["en"] = "Catalog", ["th"] = "แค็ตตาล็อก" },
        ["plugins.install"] = new() { ["en"] = "Install", ["th"] = "ติดตั้ง" },
        ["plugins.uninstall"] = new() { ["en"] = "Uninstall", ["th"] = "ถอนการติดตั้ง" },
        ["plugins.enable"] = new() { ["en"] = "Enable", ["th"] = "เปิดใช้" },
        ["plugins.disable"] = new() { ["en"] = "Disable", ["th"] = "ปิดใช้" },
        ["plugins.search"] = new() { ["en"] = "Search plugins...", ["th"] = "ค้นหาปลั๊กอิน..." },
        ["plugins.noPlugins"] = new() { ["en"] = "No plugins installed yet.\nBrowse the Catalog tab to get started!", ["th"] = "ยังไม่มีปลั๊กอินที่ติดตั้ง\nไปที่แท็บแค็ตตาล็อกเพื่อเริ่มต้น!" },
        ["plugins.selectPlugin"] = new() { ["en"] = "Select a plugin to view details", ["th"] = "เลือกปลั๊กอินเพื่อดูรายละเอียด" },
        ["plugins.hookEvents"] = new() { ["en"] = "Hook Events", ["th"] = "เหตุการณ์ Hook" },
        ["plugins.whatItDoes"] = new() { ["en"] = "What it does", ["th"] = "ทำอะไรได้บ้าง" },
        ["plugins.author"] = new() { ["en"] = "Author", ["th"] = "ผู้สร้าง" },
        ["plugins.category"] = new() { ["en"] = "Category", ["th"] = "หมวดหมู่" },
        ["plugins.version"] = new() { ["en"] = "Version", ["th"] = "เวอร์ชัน" },
        ["plugins.status"] = new() { ["en"] = "Status", ["th"] = "สถานะ" },
        ["plugins.installedBadge"] = new() { ["en"] = "INSTALLED", ["th"] = "ติดตั้งแล้ว" },
        ["plugins.availableBadge"] = new() { ["en"] = "AVAILABLE", ["th"] = "พร้อมติดตั้ง" },

        // ═══════ CHAT VIEW ═══════
        ["chat.placeholder"] = new() { ["en"] = "Type a message...", ["th"] = "พิมพ์ข้อความ..." },
        ["chat.send"] = new() { ["en"] = "Send", ["th"] = "ส่ง" },
        ["chat.stop"] = new() { ["en"] = "Stop", ["th"] = "หยุด" },
        ["chat.thinking"] = new() { ["en"] = "Thinking...", ["th"] = "กำลังคิด..." },
        ["chat.you"] = new() { ["en"] = "You", ["th"] = "คุณ" },

        // ═══════ MODEL MANAGER ═══════
        ["models.title"] = new() { ["en"] = "Model Manager", ["th"] = "จัดการโมเดล" },
        ["models.subtitle"] = new() { ["en"] = "Download and manage GGUF models", ["th"] = "ดาวน์โหลดและจัดการโมเดล GGUF" },
        ["models.localModels"] = new() { ["en"] = "Local Models", ["th"] = "โมเดลในเครื่อง" },
        ["models.download"] = new() { ["en"] = "Download from HuggingFace", ["th"] = "ดาวน์โหลดจาก HuggingFace" },

        // ═══════ BUDDY ═══════
        ["buddy.pet"] = new() { ["en"] = "Pet me!", ["th"] = "ลูบฉันสิ!" },
        ["buddy.stats"] = new() { ["en"] = "Stats", ["th"] = "สถิติ" },
        ["buddy.rarity"] = new() { ["en"] = "Rarity", ["th"] = "ความหายาก" },
        ["buddy.species"] = new() { ["en"] = "Species", ["th"] = "สายพันธุ์" },
        ["buddy.hatched"] = new() { ["en"] = "Hatched", ["th"] = "ฟักเมื่อ" },
        ["buddy.shiny"] = new() { ["en"] = "SHINY!", ["th"] = "เรืองแสง!" },
        ["buddy.debugging"] = new() { ["en"] = "Debugging", ["th"] = "ดีบัก" },
        ["buddy.patience"] = new() { ["en"] = "Patience", ["th"] = "อดทน" },
        ["buddy.chaos"] = new() { ["en"] = "Chaos", ["th"] = "วุ่นวาย" },
        ["buddy.wisdom"] = new() { ["en"] = "Wisdom", ["th"] = "ปัญญา" },
        ["buddy.snark"] = new() { ["en"] = "Snark", ["th"] = "เสียดสี" },

        // ═══════ COMMON ═══════
        ["common.save"] = new() { ["en"] = "Save", ["th"] = "บันทึก" },
        ["common.cancel"] = new() { ["en"] = "Cancel", ["th"] = "ยกเลิก" },
        ["common.delete"] = new() { ["en"] = "Delete", ["th"] = "ลบ" },
        ["common.confirm"] = new() { ["en"] = "Confirm", ["th"] = "ยืนยัน" },
        ["common.close"] = new() { ["en"] = "Close", ["th"] = "ปิด" },
        ["common.search"] = new() { ["en"] = "Search", ["th"] = "ค้นหา" },
        ["common.loading"] = new() { ["en"] = "Loading...", ["th"] = "กำลังโหลด..." },
        ["common.error"] = new() { ["en"] = "Error", ["th"] = "ข้อผิดพลาด" },
        ["common.success"] = new() { ["en"] = "Success", ["th"] = "สำเร็จ" },
        ["common.on"] = new() { ["en"] = "ON", ["th"] = "เปิด" },
        ["common.off"] = new() { ["en"] = "OFF", ["th"] = "ปิด" },

        // ═══════ LOADING OVERLAY ═══════
        ["loading.title"] = new() { ["en"] = "Loading Model", ["th"] = "กำลังโหลดโมเดล" },
        ["loading.hint"] = new() { ["en"] = "Please wait while the model loads into memory...", ["th"] = "กรุณารอสักครู่ขณะโหลดโมเดลเข้าหน่วยความจำ..." },

        // ═══════ RARITY NAMES ═══════
        ["rarity.common"] = new() { ["en"] = "Common", ["th"] = "ธรรมดา" },
        ["rarity.uncommon"] = new() { ["en"] = "Uncommon", ["th"] = "ไม่ธรรมดา" },
        ["rarity.rare"] = new() { ["en"] = "Rare", ["th"] = "หายาก" },
        ["rarity.epic"] = new() { ["en"] = "Epic", ["th"] = "มหากาพย์" },
        ["rarity.legendary"] = new() { ["en"] = "Legendary", ["th"] = "ตำนาน" },

        // ═══════ SPECIES NAMES ═══════
        ["species.duck"] = new() { ["en"] = "Duck", ["th"] = "เป็ด" },
        ["species.goose"] = new() { ["en"] = "Goose", ["th"] = "ห่าน" },
        ["species.blob"] = new() { ["en"] = "Blob", ["th"] = "บล็อบ" },
        ["species.cat"] = new() { ["en"] = "Cat", ["th"] = "แมว" },
        ["species.dragon"] = new() { ["en"] = "Dragon", ["th"] = "มังกร" },
        ["species.octopus"] = new() { ["en"] = "Octopus", ["th"] = "ปลาหมึก" },
        ["species.owl"] = new() { ["en"] = "Owl", ["th"] = "นกฮูก" },
        ["species.penguin"] = new() { ["en"] = "Penguin", ["th"] = "เพนกวิน" },
        ["species.turtle"] = new() { ["en"] = "Turtle", ["th"] = "เต่า" },
        ["species.snail"] = new() { ["en"] = "Snail", ["th"] = "หอยทาก" },
        ["species.ghost"] = new() { ["en"] = "Ghost", ["th"] = "ผี" },
        ["species.axolotl"] = new() { ["en"] = "Axolotl", ["th"] = "แอกโซลอเติล" },
        ["species.capybara"] = new() { ["en"] = "Capybara", ["th"] = "คาปิบารา" },
        ["species.cactus"] = new() { ["en"] = "Cactus", ["th"] = "กระบองเพชร" },
        ["species.robot"] = new() { ["en"] = "Robot", ["th"] = "หุ่นยนต์" },
        ["species.rabbit"] = new() { ["en"] = "Rabbit", ["th"] = "กระต่าย" },
        ["species.mushroom"] = new() { ["en"] = "Mushroom", ["th"] = "เห็ด" },
        ["species.chonk"] = new() { ["en"] = "Chonk", ["th"] = "อ้วนกลม" },
    };

    public void SetLanguage(string lang)
    {
        if (lang != "en" && lang != "th") lang = "en";
        _currentLanguage = lang;
        LanguageChanged?.Invoke();
    }

    /// <summary>Get translation for a key. Falls back to English, then to the key itself.</summary>
    public string T(string key)
    {
        if (Translations.TryGetValue(key, out var dict))
        {
            if (dict.TryGetValue(_currentLanguage, out var val))
                return val;
            if (dict.TryGetValue("en", out var fallback))
                return fallback;
        }
        return key;
    }

    /// <summary>Get translation with format args.</summary>
    public string T(string key, params object[] args)
    {
        return string.Format(T(key), args);
    }

    public string[] AvailableLanguages => ["en", "th"];
    public string[] LanguageDisplayNames => ["English", "ไทย"];
}
