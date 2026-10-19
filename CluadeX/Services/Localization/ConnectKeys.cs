namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the Setup page's "Connections" group (GitHub, Stripe, Canva) and the one-click
/// BrainX install. Every key needs both "en" and "th". Guides are plain steps a non-programmer can follow.</summary>
internal static class ConnectKeys
{
    private static Dictionary<string, string> E(string en, string th) => new() { ["en"] = en, ["th"] = th };

    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        ["setup.group.connect"] = E("Connections — let the AI work with your accounts", "การเชื่อมต่อ — ให้ AI ทำงานกับบัญชีของคุณ"),

        // ── rows ──
        ["setup.item.conn-github"] = E("GitHub", "GitHub"),
        ["setup.why.conn-github"] = E(
            "The AI reads and manages your repositories, issues, pull requests and Actions. You sign in once in the browser — no token to copy.",
            "AI อ่านและจัดการ repo, issue, pull request และ Actions ของคุณได้ ล็อกอินผ่านเบราว์เซอร์ครั้งเดียว ไม่ต้องก๊อปปี้ token"),
        ["setup.item.conn-stripe"] = E("Stripe", "Stripe"),
        ["setup.why.conn-stripe"] = E(
            "The AI looks up and manages customers, products, prices, payment links and invoices. Needs one key from your Stripe dashboard.",
            "AI ดูและจัดการลูกค้า สินค้า ราคา ลิงก์ชำระเงิน และใบแจ้งหนี้ได้ ใช้คีย์หนึ่งตัวจากหน้า Stripe Dashboard"),
        ["setup.item.conn-canva"] = E("Canva", "Canva"),
        ["setup.why.conn-canva"] = E(
            "The AI finds, creates and exports your Canva designs. You sign in to Canva in the browser the first time it is used.",
            "AI ค้นหา สร้าง และส่งออกงานออกแบบใน Canva ได้ ครั้งแรกที่ใช้จะให้ล็อกอิน Canva ในเบราว์เซอร์"),

        ["connect.state.connected"] = E("Connected", "เชื่อมแล้ว"),
        ["connect.place.brainx"] = E("Through BrainX — shared by every agent on this PC", "ผ่าน BrainX — ทุกเอเจนต์ในเครื่องนี้ใช้ร่วมกัน"),
        ["connect.place.cluadex"] = E("In CluadeX (install BrainX to share it with every agent)", "อยู่ใน CluadeX (ติดตั้ง BrainX เพื่อให้ทุกเอเจนต์ใช้ร่วมกัน)"),
        ["connect.place.manualBrainx"] = E("Already set up by hand in BrainX — that one is used", "ตั้งค่าเองไว้แล้วใน BrainX — ใช้ตัวนั้น"),
        ["connect.place.manualCluadex"] = E("Already set up by hand in CluadeX's MCP list — that one is used", "ตั้งค่าเองไว้แล้วในรายการ MCP ของ CluadeX — ใช้ตัวนั้น"),
        ["connect.place.notLive"] = E(" · tools appear after BrainX restarts", " · เครื่องมือจะขึ้นหลัง BrainX เริ่มใหม่"),
        ["connect.place.willBrainx"] = E("Will be added to BrainX (shared by every agent)", "จะเพิ่มเข้า BrainX (ทุกเอเจนต์ใช้ร่วมกัน)"),
        ["connect.place.willCluadex"] = E("Will be added to CluadeX (no BrainX on this PC)", "จะเพิ่มเข้า CluadeX (เครื่องนี้ยังไม่มี BrainX)"),

        ["setup.act.connect"] = E("Connect", "เชื่อมต่อ"),
        ["setup.act.guide"] = E("How it works", "วิธีทำ"),
        ["setup.act.disconnect"] = E("Disconnect", "ยกเลิกการเชื่อมต่อ"),
        ["setup.act.installBrainX"] = E("Install BrainX", "ติดตั้ง BrainX"),
        ["setup.act.downloadPage"] = E("Download page", "หน้าดาวน์โหลด"),

        // ── guides (the "How it works" button, and the confirm before connecting) ──
        ["connect.guide.github"] = E(
            "1. Press Connect.\n2. If the GitHub CLI isn't installed, CluadeX installs it (a console window, via winget).\n3. A console window opens for the sign-in: press Enter, your browser opens GitHub — sign in and type the 8-character code shown in the console.\n4. CluadeX downloads GitHub's official MCP server (checked against GitHub's own checksum) and connects it.\n\nThen just ask, e.g. \"list my open pull requests\" or \"open an issue in my-repo about the login bug\".\nThe AI acts with your GitHub account, so it asks before it changes anything.",
            "1. กด เชื่อมต่อ\n2. ถ้ายังไม่มี GitHub CLI โปรแกรมจะติดตั้งให้ (หน้าต่างคอนโซล ผ่าน winget)\n3. จะมีหน้าต่างคอนโซลขึ้นมาให้ล็อกอิน: กด Enter เบราว์เซอร์จะเปิดหน้า GitHub — ล็อกอิน แล้วพิมพ์รหัส 8 ตัวที่เห็นในคอนโซล\n4. CluadeX โหลด MCP server ทางการของ GitHub (ตรวจ checksum ของ GitHub เองก่อน) แล้วเชื่อมให้\n\nจากนั้นสั่งได้เลย เช่น \"ดู pull request ที่ยังเปิดอยู่ของฉัน\" หรือ \"เปิด issue ใน my-repo เรื่องบั๊กล็อกอิน\"\nAI ใช้บัญชี GitHub ของคุณ จึงจะถามก่อนเปลี่ยนแปลงอะไรเสมอ"),
        ["connect.guide.stripe"] = E(
            "1. Open the Stripe dashboard ▸ Developers ▸ API keys (the button opens it).\n2. Best: Create restricted key, tick only what the AI may touch (e.g. Customers, Products, Prices: Write; the rest None). Start in Test mode (rk_test_…).\n3. Copy the key, press Connect, paste it. It is stored encrypted on this PC only — never in a file that syncs.\n4. If Node.js isn't installed, CluadeX installs it first.\n\nThen ask, e.g. \"create a product 'Pro plan' at 299 THB/month and give me a payment link\".\nA live secret key (sk_live_…) can move real money — use a restricted key.",
            "1. เปิด Stripe Dashboard ▸ Developers ▸ API keys (กดปุ่มเพื่อเปิดหน้านั้น)\n2. แนะนำ: กด Create restricted key แล้วติ๊กเฉพาะสิ่งที่ยอมให้ AI แตะ (เช่น Customers, Products, Prices: Write ส่วนอื่น None) เริ่มจาก Test mode ก่อน (rk_test_…)\n3. ก๊อปปี้คีย์ กด เชื่อมต่อ แล้ววาง คีย์ถูกเข้ารหัสเก็บในเครื่องนี้เท่านั้น ไม่ลงไฟล์ที่ sync ขึ้นคลาวด์\n4. ถ้ายังไม่มี Node.js โปรแกรมจะติดตั้งให้ก่อน\n\nจากนั้นสั่งได้เลย เช่น \"สร้างสินค้า Pro plan ราคา 299 บาท/เดือน แล้วขอลิงก์ชำระเงิน\"\nคีย์ live แบบเต็ม (sk_live_…) โอนเงินจริงได้ — ใช้ restricted key ดีกว่า"),
        ["connect.guide.canva"] = E(
            "1. Press Connect (CluadeX installs Node.js first if it's missing or too old).\n2. The first time the AI uses Canva, your browser opens Canva's sign-in — sign in and press Allow.\n3. That's all: no key to copy. Canva remembers the permission.\n\nThen ask, e.g. \"find my 'Grand opening' poster in Canva and export it as PNG\".",
            "1. กด เชื่อมต่อ (ถ้ายังไม่มี Node.js หรือรุ่นเก่าไป โปรแกรมจะติดตั้งให้ก่อน)\n2. ครั้งแรกที่ AI ใช้ Canva เบราว์เซอร์จะเปิดหน้าล็อกอิน Canva — ล็อกอินแล้วกด Allow\n3. แค่นั้น ไม่ต้องก๊อปปี้คีย์ Canva จะจำสิทธิ์ไว้ให้\n\nจากนั้นสั่งได้เลย เช่น \"หาโปสเตอร์ 'เปิดร้าน' ใน Canva แล้ว export เป็น PNG\""),
        ["connect.guide.title"] = E("Connect {0}", "เชื่อมต่อ {0}"),
        ["connect.guide.ok"] = E("Got it", "เข้าใจแล้ว"),
        ["connect.guide.open"] = E("Open the page", "เปิดหน้าเว็บ"),
        ["connect.confirm.go"] = E("Connect", "เชื่อมต่อ"),
        ["connect.confirm.cancel"] = E("Cancel", "ยกเลิก"),

        // ── the key prompt ──
        ["connect.key.title"] = E("{0} key", "คีย์ {0}"),
        ["connect.key.prompt"] = E("Paste the key (a restricted key is safest). It is stored encrypted on this PC only.", "วางคีย์ (restricted key ปลอดภัยที่สุด) คีย์จะถูกเข้ารหัสเก็บในเครื่องนี้เท่านั้น"),
        ["connect.key.save"] = E("Save and connect", "บันทึกและเชื่อมต่อ"),
        ["connect.key.bad"] = E("That doesn't look like a {0} key (it should start with {1}). Nothing was saved.", "คีย์นี้ไม่ใช่รูปแบบของ {0} (ต้องขึ้นต้นด้วย {1}) ยังไม่ได้บันทึกอะไร"),
        ["connect.key.liveTitle"] = E("A live secret key", "คีย์ live แบบเต็มสิทธิ์"),
        ["connect.key.liveBody"] = E(
            "This is a live secret key (sk_live_): with it the AI could refund, charge or delete real data. A restricted key (rk_live_ / rk_test_) limited to what you need is much safer. Use this key anyway?",
            "นี่คือคีย์ live แบบเต็มสิทธิ์ (sk_live_) AI จะคืนเงิน ตัดเงิน หรือลบข้อมูลจริงได้ restricted key (rk_live_ / rk_test_) ที่จำกัดเฉพาะสิ่งที่ต้องใช้ปลอดภัยกว่ามาก จะใช้คีย์นี้ต่อไหม?"),
        ["connect.key.useAnyway"] = E("Use it anyway", "ใช้ต่อ"),

        // ── progress / results ──
        ["connect.note.ghInstall"] = E("Installing the GitHub CLI — watch the console window…", "กำลังติดตั้ง GitHub CLI — ดูในหน้าต่างคอนโซล…"),
        ["connect.note.ghSignIn"] = E("Sign in to GitHub in the console window / browser…", "ล็อกอิน GitHub ในหน้าต่างคอนโซล/เบราว์เซอร์…"),
        ["connect.note.download"] = E("Downloading GitHub's MCP server…", "กำลังโหลด MCP server ของ GitHub…"),
        ["connect.note.nodeInstall"] = E("Installing Node.js — watch the console window…", "กำลังติดตั้ง Node.js — ดูในหน้าต่างคอนโซล…"),
        ["connect.note.registering"] = E("Connecting…", "กำลังเชื่อมต่อ…"),
        ["connect.note.brainx"] = E("Connected through BrainX — CluadeX, Claude and Gemini all have it now.", "เชื่อมผ่าน BrainX แล้ว — CluadeX, Claude และ Gemini ใช้ได้หมด"),
        ["connect.note.cluadex"] = E("Connected in CluadeX.", "เชื่อมใน CluadeX แล้ว"),
        ["connect.note.canvaFirst"] = E(" The first time the AI uses Canva, sign in in the browser that opens.", " ครั้งแรกที่ AI ใช้ Canva ให้ล็อกอินในเบราว์เซอร์ที่เปิดขึ้นมา"),
        ["connect.note.disconnected"] = E("Disconnected — the key was removed from this PC.", "ยกเลิกการเชื่อมต่อแล้ว — ลบคีย์ออกจากเครื่องนี้แล้ว"),
        ["connect.note.stillNoGh"] = E("The GitHub CLI isn't there yet — press Connect again after the install finishes.", "ยังไม่เจอ GitHub CLI — รอให้ติดตั้งเสร็จแล้วกด เชื่อมต่อ อีกครั้ง"),
        ["connect.note.stillNoSignIn"] = E("Not signed in to GitHub yet — press Connect again when the sign-in is done.", "ยังไม่ได้ล็อกอิน GitHub — ล็อกอินเสร็จแล้วกด เชื่อมต่อ อีกครั้ง"),
        ["connect.note.stillNoNode"] = E("Node.js isn't there yet (Canva needs 22.16 or later) — press Connect again after the install.", "ยังไม่เจอ Node.js (Canva ต้องใช้รุ่น 22.16 ขึ้นไป) — ติดตั้งเสร็จแล้วกด เชื่อมต่อ อีกครั้ง"),
        ["connect.disconnect.title"] = E("Disconnect {0}?", "ยกเลิกการเชื่อมต่อ {0}?"),
        ["connect.disconnect.body"] = E(
            "The AI will no longer reach {0}. CluadeX removes its own entry (in BrainX and in CluadeX) and the saved key. Entries you made by hand are not touched.",
            "AI จะเข้าถึง {0} ไม่ได้อีก CluadeX จะลบรายการของตัวเอง (ทั้งใน BrainX และ CluadeX) และคีย์ที่บันทึกไว้ รายการที่คุณตั้งเองจะไม่ถูกแตะ"),

        ["connect.err.noServer"] = E("GitHub's MCP server isn't downloaded yet.", "ยังไม่ได้โหลด MCP server ของ GitHub"),
        ["connect.err.signIn"] = E("Not signed in to GitHub.", "ยังไม่ได้ล็อกอิน GitHub"),
        ["connect.err.noNode"] = E("Node.js is needed.", "ต้องมี Node.js"),
        ["connect.err.noKey"] = E("No key saved.", "ยังไม่มีคีย์"),
        ["connect.err.unknown"] = E("Unknown connection.", "ไม่รู้จักการเชื่อมต่อนี้"),
        ["connect.err.noMcp"] = E("CluadeX's MCP manager isn't ready — try again in a moment.", "ตัวจัดการ MCP ของ CluadeX ยังไม่พร้อม — ลองใหม่อีกสักครู่"),
        ["connect.err.mcpUnreadable"] = E("CluadeX's MCP list (mcp_servers.json) could not be read or saved, so nothing was changed. Check it on the MCP page.", "อ่านหรือบันทึกรายการ MCP ของ CluadeX (mcp_servers.json) ไม่ได้ จึงยังไม่ได้เปลี่ยนอะไร — ตรวจที่หน้า MCP"),
        ["connect.err.noAsset"] = E("GitHub's release has no Windows server file — try again later.", "release ของ GitHub ไม่มีไฟล์สำหรับ Windows — ลองใหม่ภายหลัง"),
        ["connect.err.checksum"] = E("The downloaded server didn't match GitHub's checksum — it was not installed.", "ไฟล์ที่โหลดมาไม่ตรงกับ checksum ของ GitHub — จึงไม่ได้ติดตั้ง"),
        ["connect.err.download"] = E("The download failed — check the internet connection and try again.", "โหลดไม่สำเร็จ — ตรวจอินเทอร์เน็ตแล้วลองใหม่"),

        // ── one-click BrainX ──
        ["brainx.confirm.title"] = E("Install BrainX?", "ติดตั้ง BrainX?"),
        ["brainx.confirm.body"] = E(
            "CluadeX downloads the BrainX installer from xman4289.com, checks it (right site, a real installer, its digital signature), then runs it. Follow the installer's window; when it finishes, BrainX starts and CluadeX connects to it — and your connections move into BrainX so every agent can use them.",
            "CluadeX จะโหลดตัวติดตั้ง BrainX จาก xman4289.com ตรวจก่อน (มาจากเว็บที่ถูกต้อง เป็นตัวติดตั้งจริง และลายเซ็นดิจิทัล) แล้วเปิดให้ ทำตามหน้าต่างตัวติดตั้งไปจนเสร็จ BrainX จะเปิดเองแล้ว CluadeX เชื่อมให้ — การเชื่อมต่อบริการต่างๆ จะย้ายเข้า BrainX เพื่อให้ทุกเอเจนต์ใช้ร่วมกัน"),
        ["brainx.confirm.go"] = E("Download and install", "โหลดและติดตั้ง"),
        ["brainx.note.downloading"] = E("Downloading BrainX… {0}", "กำลังโหลด BrainX… {0}"),
        ["brainx.note.checking"] = E("Checking the installer…", "กำลังตรวจตัวติดตั้ง…"),
        ["brainx.note.running"] = E("The BrainX installer is open — follow its window…", "เปิดตัวติดตั้ง BrainX แล้ว — ทำตามหน้าต่างของมัน…"),
        ["brainx.note.waiting"] = E("Waiting for BrainX to start…", "รอ BrainX เปิดขึ้นมา…"),
        ["brainx.note.comingSoon"] = E("BrainX isn't open for download yet (Coming Soon).", "BrainX ยังไม่เปิดให้ดาวน์โหลด (Coming Soon)"),
        ["brainx.note.done"] =E("BrainX is installed and connected.", "ติดตั้งและเชื่อมต่อ BrainX แล้ว"),
        ["brainx.note.notYet"] = E("The installer closed, but BrainX isn't running yet — open BrainX once, then press Check again.", "ตัวติดตั้งปิดไปแล้ว แต่ BrainX ยังไม่ทำงาน — เปิด BrainX หนึ่งครั้งแล้วกด ตรวจอีกครั้ง"),
        ["brainx.unsigned.title"] = E("The installer isn't digitally signed", "ตัวติดตั้งไม่มีลายเซ็นดิจิทัล"),
        ["brainx.unsigned.body"] = E(
            "It came from xman4289.com and is a real Windows installer, but it carries no digital signature, so Windows can't vouch for who made it. Its SHA-256 is:\n{0}\nRun it?",
            "ไฟล์มาจาก xman4289.com และเป็นตัวติดตั้ง Windows จริง แต่ไม่มีลายเซ็นดิจิทัล Windows จึงยืนยันไม่ได้ว่าใครเป็นผู้สร้าง SHA-256 ของไฟล์คือ:\n{0}\nจะเปิดติดตั้งไหม?"),
        ["brainx.unsigned.run"] = E("Run it", "เปิดติดตั้ง"),
        ["brainx.err.host"] = E("The download pointed away from xman4289.com, so it was stopped. The download page opens in your browser instead.", "ลิงก์ดาวน์โหลดพาออกนอก xman4289.com จึงหยุดไว้ — เปิดหน้าดาวน์โหลดในเบราว์เซอร์แทน"),
        ["brainx.err.notFile"] = E("xman4289.com answered with a web page instead of the installer (the free download may be closed right now). The download page opens in your browser.", "xman4289.com ส่งหน้าเว็บกลับมาแทนตัวติดตั้ง (ช่องดาวน์โหลดฟรีอาจปิดอยู่ตอนนี้) — เปิดหน้าดาวน์โหลดในเบราว์เซอร์แทน"),
        ["brainx.err.size"] = E("The downloaded file isn't the size of an installer — it was deleted.", "ไฟล์ที่โหลดมาขนาดไม่ใช่ตัวติดตั้ง — ลบทิ้งแล้ว"),
        ["brainx.err.http"] = E("xman4289.com didn't give the installer (server error) — try again later.", "xman4289.com ไม่ส่งตัวติดตั้งมา (เซิร์ฟเวอร์ขัดข้อง) — ลองใหม่ภายหลัง"),
        ["brainx.err.signature"] = E("The installer's digital signature is broken — it may have been tampered with, so it was deleted and not run.", "ลายเซ็นดิจิทัลของตัวติดตั้งเสียหาย อาจถูกแก้ไข จึงลบทิ้งและไม่เปิด"),
        ["brainx.err.run"] = E("The installer could not be started.", "เปิดตัวติดตั้งไม่ได้"),
    };
}
