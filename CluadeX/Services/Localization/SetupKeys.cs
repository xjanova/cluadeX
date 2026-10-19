namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the setup page ("ตัวช่วยติดตั้ง", SetupViewModel). Every key needs both "en" and "th".</summary>
internal static class SetupKeys
{
    private static Dictionary<string, string> E(string en, string th) => new() { ["en"] = en, ["th"] = th };

    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        ["nav.setupTip"] = E("Setup — what CluadeX can use on this PC, and one button for each missing piece", "ตัวช่วยติดตั้ง — ดูว่าเครื่องนี้มีอะไรพร้อมแล้ว และกดปุ่มเดียวเพื่อเติมส่วนที่ยังขาด"),
        ["setup.title"] = E("Setup", "ตัวช่วยติดตั้ง"),
        ["setup.subtitle"] = E(
            "Everything CluadeX can use on this PC — what is ready, what is missing, and one button that gets it. Installs run in a console window you watch, after you see the exact command; keys are entered by you in Settings.",
            "ทุกอย่างที่ CluadeX ใช้ได้บนเครื่องนี้ — อะไรพร้อมแล้ว อะไรยังขาด และปุ่มที่จัดการให้ การติดตั้งจะรันในหน้าต่างคอนโซลให้คุณเห็น หลังจากแสดงคำสั่งให้ดูก่อน ส่วนคีย์คุณเป็นคนใส่เองในหน้า Settings"),
        ["setup.recheck"] = E("Check again", "ตรวจอีกครั้ง"),
        ["setup.checking"] = E("Checking this PC…", "กำลังตรวจเครื่องนี้…"),
        ["setup.checkedAt"] = E("Checked at {0}", "ตรวจล่าสุด {0}"),
        ["setup.checkFailed"] = E("The check could not finish — details are in the Debug Log.", "ตรวจไม่สำเร็จ — รายละเอียดอยู่ใน Debug Log"),
        ["setup.summary"] = E("{0} of {1} ready", "พร้อม {0} จาก {1}"),
        ["setup.required"] = E("needed", "จำเป็น"),
        ["setup.banner.noModel"] = E(
            "Nothing can answer the chat yet. Pick one: enter an API key (Anthropic, OpenAI or Gemini), sign in to Claude Code or the Gemini CLI, or install Ollama and download a model. Cloud models also need the license activated.",
            "ตอนนี้ยังไม่มีโมเดลไหนตอบแชทได้ เลือกสักทาง: ใส่ API key (Anthropic, OpenAI หรือ Gemini), ล็อกอิน Claude Code หรือ Gemini CLI, หรือติดตั้ง Ollama แล้วโหลดโมเดล — โมเดลบนคลาวด์ต้องเปิดใช้งานไลเซนส์ด้วย"),
        ["setup.banner.insidePackage"] = E(
            "CluadeX was started from inside another app's package (e.g. the Claude desktop app), so an installer started from here would land in that app's private folder and vanish. Close CluadeX and open it from the Start menu, then install from here.",
            "CluadeX ถูกเปิดจากในแพ็กเกจของแอปอื่น (เช่น Claude desktop) ถ้าติดตั้งจากตรงนี้ ไฟล์จะไปอยู่ในโฟลเดอร์ส่วนตัวของแอปนั้นแล้วหายไป — ปิด CluadeX แล้วเปิดใหม่จากเมนู Start ก่อน แล้วค่อยติดตั้งจากหน้านี้"),

        ["setup.state.Ready"] = E("Ready", "พร้อม"),
        ["setup.state.Missing"] = E("Not installed", "ยังไม่ได้ติดตั้ง"),
        ["setup.state.NeedsKey"] = E("No key yet", "ยังไม่มีคีย์"),
        ["setup.state.NeedsSignIn"] = E("Not signed in", "ยังไม่ได้ล็อกอิน"),
        ["setup.state.NotRunning"] = E("Installed, not running", "ติดตั้งแล้ว แต่ยังไม่ได้เปิด"),
        ["setup.state.NoModel"] = E("Running, no model yet", "เปิดอยู่ แต่ยังไม่มีโมเดล"),

        ["setup.group.chat"] = E("AI that answers the chat", "AI ที่ตอบแชท"),
        ["setup.group.local"] = E("AI on this PC (free, offline)", "AI ในเครื่อง (ฟรี ไม่ต้องใช้เน็ต)"),
        ["setup.group.dev"] = E("Coding tools", "เครื่องมือเขียนโค้ด"),
        ["setup.group.brain"] = E("BrainX brain", "สมอง BrainX"),

        ["setup.item.license"] = E("CluadeX license", "ไลเซนส์ CluadeX"),
        ["setup.why.license"] = E("Cloud models (Anthropic, OpenAI, Gemini, Claude Dev, Gemini Dev) need it. Models on this PC do not.", "โมเดลบนคลาวด์ (Anthropic, OpenAI, Gemini, Claude Dev, Gemini Dev) ต้องใช้ ส่วนโมเดลในเครื่องไม่ต้อง"),
        ["setup.item.anthropic"] = E("Anthropic API key (Claude)", "API key ของ Anthropic (Claude)"),
        ["setup.why.anthropic"] = E("Claude through the API — the full agent with every CluadeX tool, billed per token.", "ใช้ Claude ผ่าน API — เอเจนต์เต็มรูปแบบพร้อมเครื่องมือทุกตัวของ CluadeX คิดเงินตามจำนวนโทเค็น"),
        ["setup.item.openai"] = E("OpenAI API key", "API key ของ OpenAI"),
        ["setup.why.openai"] = E("GPT models through the API, billed per token.", "ใช้โมเดล GPT ผ่าน API คิดเงินตามจำนวนโทเค็น"),
        ["setup.item.gemini"] = E("Gemini API key", "API key ของ Gemini"),
        ["setup.why.gemini"] = E("Gemini models through the API (AI Studio has a free tier).", "ใช้โมเดล Gemini ผ่าน API (AI Studio มีโควตาฟรี)"),
        ["setup.item.claudecode"] = E("Claude Code CLI (Claude Dev)", "Claude Code CLI (Claude Dev)"),
        ["setup.why.claudecode"] = E("Claude on your Claude subscription — no API key. Install it, then sign in once.", "ใช้ Claude ด้วยแพ็กเกจ Claude ของคุณ ไม่ต้องใช้ API key — ติดตั้งแล้วล็อกอินครั้งเดียว"),
        ["setup.item.geminicli"] = E("Gemini CLI (Gemini Dev)", "Gemini CLI (Gemini Dev)"),
        ["setup.why.geminicli"] = E("Gemini on your Google account — no API key. Install it, then sign in with Google once.", "ใช้ Gemini ด้วยบัญชี Google ของคุณ ไม่ต้องใช้ API key — ติดตั้งแล้วล็อกอินด้วย Google ครั้งเดียว"),
        ["setup.item.ollama"] = E("Ollama", "Ollama"),
        ["setup.why.ollama"] = E("Runs open models on this PC for free. Needs a model downloaded once.", "รันโมเดลโอเพนซอร์สบนเครื่องนี้ฟรี ต้องโหลดโมเดลหนึ่งครั้ง"),
        ["setup.item.llamaserver"] = E("llama.cpp server (llama-server)", "llama.cpp server (llama-server)"),
        ["setup.why.llamaserver"] = E("Runs the GGUF files from the Models page on the GPU, with native tool calls.", "รันไฟล์ GGUF จากหน้า Models บน GPU พร้อมการเรียกเครื่องมือแบบ native"),
        ["setup.item.git"] = E("Git", "Git"),
        ["setup.why.git"] = E("Source control, diffs, checkpoints and commits — most coding work needs it.", "ระบบจัดการเวอร์ชัน, diff, checkpoint และ commit — งานเขียนโค้ดส่วนใหญ่ต้องใช้"),
        ["setup.item.node"] = E("Node.js", "Node.js"),
        ["setup.why.node"] = E("Needed by the Gemini CLI, UI Studio previews and JavaScript projects.", "จำเป็นสำหรับ Gemini CLI, พรีวิวใน UI Studio และโปรเจกต์ JavaScript"),
        ["setup.item.dotnet"] = E(".NET SDK (8 or newer)", ".NET SDK (8 ขึ้นไป)"),
        ["setup.why.dotnet"] = E("Builds and runs C# / .NET projects.", "ใช้ build และรันโปรเจกต์ C# / .NET"),
        ["setup.item.python"] = E("Python + debugpy", "Python + debugpy"),
        ["setup.why.python"] = E("Python projects, and the Debug panel's Python debugger.", "ใช้กับโปรเจกต์ Python และตัวดีบัก Python ในแผง Debug"),
        ["setup.item.webview2"] = E("WebView2 Runtime", "WebView2 Runtime"),
        ["setup.why.webview2"] = E("The built-in browser and page previews.", "เบราว์เซอร์ในตัวและการพรีวิวหน้าเว็บ"),
        ["setup.item.brainx"] = E("BrainX", "BrainX"),
        ["setup.why.brainx"] = E("Your brain: memory across chats, lessons recalled before each task, and the cowork room with the other agents.", "สมองของคุณ: ความจำข้ามแชท ดึงบทเรียนเก่ามาก่อนเริ่มงาน และห้อง cowork กับเอเจนต์ตัวอื่น"),

        ["setup.detail.needsNode"] = E("Node.js is installed with it (npm comes with Node).", "จะติดตั้ง Node.js ให้ด้วย (npm มากับ Node)"),
        ["setup.detail.noDebugpy"] = E("debugpy is not installed", "ยังไม่มี debugpy"),
        ["setup.detail.ollamaModels"] = E("{0} model(s)", "{0} โมเดล"),

        ["setup.act.license"] = E("Activate…", "เปิดใช้งาน…"),
        ["setup.act.settings"] = E("Settings", "ตั้งค่า"),
        ["setup.act.getKey"] = E("Get a key", "ไปเอาคีย์"),
        ["setup.act.enterKey"] = E("Enter the key", "ใส่คีย์"),
        ["setup.act.install"] = E("Install", "ติดตั้ง"),
        ["setup.act.installDebugpy"] = E("Install debugpy", "ติดตั้ง debugpy"),
        ["setup.act.signin"] = E("Sign in", "ล็อกอิน"),
        ["setup.act.start"] = E("Start", "เปิด"),
        ["setup.act.pullModel"] = E("Download llama3.2 3B", "โหลด llama3.2 3B"),
        ["setup.act.browseModels"] = E("Other models", "โมเดลอื่น"),
        ["setup.act.mcpPage"] = E("Connect", "เชื่อมต่อ"),
        ["setup.act.download"] = E("Download BrainX", "ดาวน์โหลด BrainX"),

        ["setup.confirm.title"] = E("Install {0}?", "ติดตั้ง {0}?"),
        ["setup.confirm.body"] = E("CluadeX will open a console window and run (download ≈ {1}):", "CluadeX จะเปิดหน้าต่างคอนโซลแล้วรันคำสั่งนี้ (ดาวน์โหลดประมาณ {1}):"),
        ["setup.confirm.winget"] = E(
            "winget installs the package from Microsoft's community repository and accepts the package's own license for you — Windows may ask for administrator approval. CluadeX checks again when the window closes.",
            "winget จะติดตั้งแพ็กเกจจากคลังของ Microsoft และยอมรับเงื่อนไขการใช้งานของแพ็กเกจนั้นแทนคุณ — Windows อาจถามสิทธิ์ผู้ดูแลระบบ CluadeX จะตรวจใหม่เองเมื่อหน้าต่างปิด"),
        ["setup.confirm.console"] = E("CluadeX checks again when the window closes.", "CluadeX จะตรวจใหม่เองเมื่อหน้าต่างปิด"),
        ["setup.confirm.install"] = E("Install", "ติดตั้ง"),
        ["setup.confirm.run"] = E("Download", "ดาวน์โหลด"),
        ["setup.confirm.cancel"] = E("Cancel", "ยกเลิก"),

        ["setup.note.installing"] = E("Installing — watch the console window…", "กำลังติดตั้ง — ดูความคืบหน้าในหน้าต่างคอนโซล…"),
        ["setup.note.installed"] = E("Installed.", "ติดตั้งเสร็จแล้ว"),
        ["setup.note.installedNotFound"] = E("The installer finished, but CluadeX does not see it yet — restart CluadeX if it stays like this.", "ตัวติดตั้งทำงานจบแล้ว แต่ CluadeX ยังมองไม่เห็น — ถ้ายังเป็นแบบนี้ให้ปิดแล้วเปิด CluadeX ใหม่"),
        ["setup.note.installFailed"] = E("The install stopped (exit code {0}) — the console window shows why.", "การติดตั้งหยุดกลางคัน (exit code {0}) — ดูสาเหตุในหน้าต่างคอนโซล"),
        ["setup.note.signingIn"] = E("Finish the sign-in in the window / browser that opened — CluadeX notices by itself…", "ล็อกอินให้เสร็จในหน้าต่างหรือเบราว์เซอร์ที่เปิดขึ้นมา — CluadeX จะรู้เองเมื่อเสร็จ…"),
        ["setup.note.signInFailed"] = E("Couldn't open the sign-in. Open a terminal and run the CLI (claude / gemini) once.", "เปิดหน้าล็อกอินไม่ได้ — เปิดเทอร์มินัลแล้วรัน claude หรือ gemini หนึ่งครั้ง"),
        ["setup.note.starting"] = E("Starting…", "กำลังเปิด…"),
        ["setup.note.startFailed"] = E("Couldn't start it — open it from the Start menu.", "เปิดไม่ได้ — ลองเปิดจากเมนู Start"),
        ["setup.note.readyNow"] = E("Ready now.", "พร้อมใช้แล้ว"),
        ["setup.note.notYet"] = E("Not done yet — press Check again when it is.", "ยังไม่เสร็จ — เสร็จแล้วกด ตรวจอีกครั้ง"),
        ["setup.note.failed"] = E("That didn't work — details are in the Debug Log.", "ทำไม่สำเร็จ — รายละเอียดอยู่ใน Debug Log"),
    };
}
