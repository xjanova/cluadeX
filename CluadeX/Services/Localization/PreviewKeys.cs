namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the built-in browser (Dev Preview pane, browser_* tools) (team T3 owns this file). Every key needs both "en" and "th".</summary>
internal static class PreviewKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        // ═══════ Engine ═══════
        ["browser.engine.missing"] = new()
        {
            ["en"] = "The built-in browser needs the Microsoft Edge WebView2 Runtime, which is not installed on this PC. Install it with: {0} — then try again.",
            ["th"] = "เบราว์เซอร์ในตัวต้องใช้ Microsoft Edge WebView2 Runtime ซึ่งยังไม่ได้ติดตั้งในเครื่องนี้ ติดตั้งด้วยคำสั่ง: {0} — แล้วลองใหม่",
        },
        ["browser.engine.cdpUnavailable"] = new()
        {
            ["en"] = "The \"cdp\" browser engine is not available in this version. Set the browser engine back to \"auto\" to use WebView2.",
            ["th"] = "เอนจินเบราว์เซอร์แบบ \"cdp\" ยังไม่มีในเวอร์ชันนี้ ตั้งเอนจินเบราว์เซอร์กลับเป็น \"auto\" เพื่อใช้ WebView2",
        },
        ["browser.engine.startFailed"] = new()
        {
            ["en"] = "The built-in browser could not start. Details are in the Debug Log.",
            ["th"] = "เปิดเบราว์เซอร์ในตัวไม่สำเร็จ ดูรายละเอียดใน Debug Log",
        },
        ["browser.chrome.notConnected"] = new()
        {
            ["en"] = "Your Chrome is not connected. Open Chrome. If the CluadeX Bridge extension is not in it yet, add it from Settings ▸ Agent ▸ Your Chrome.",
            ["th"] = "Chrome ของคุณยังไม่ได้เชื่อมต่อ เปิด Chrome ก่อน ถ้ายังไม่ได้ลง extension CluadeX Bridge ให้เพิ่มจาก การตั้งค่า ▸ เอเจนต์ ▸ Chrome ของคุณ",
        },
        ["browser.chrome.paused"] = new()
        {
            ["en"] = "CluadeX is paused in Chrome. Ask the user to click the CluadeX icon in Chrome's toolbar when it may work there again.",
            ["th"] = "CluadeX ถูกหยุดไว้ใน Chrome ให้ขอผู้ใช้กดไอคอน CluadeX บนแถบเครื่องมือของ Chrome เมื่ออนุญาตให้ทำงานต่อ",
        },
        ["browser.chrome.openFailed"] = new()
        {
            ["en"] = "Chrome could not open a window for CluadeX: {0}",
            ["th"] = "Chrome เปิดหน้าต่างให้ CluadeX ไม่สำเร็จ: {0}",
        },
        ["browser.open.realChrome"] = new()
        {
            ["en"] = "This is the user's real Chrome, in a window of its own, with their logins. Do only what they asked here.",
            ["th"] = "นี่คือ Chrome จริงของผู้ใช้ เปิดในหน้าต่างแยก และใช้การล็อกอินของผู้ใช้ ทำเฉพาะสิ่งที่ผู้ใช้สั่งเท่านั้น",
        },
        // ═══════ Addresses ═══════
        ["browser.url.empty"] = new() { ["en"] = "No address given — pass url.", ["th"] = "ไม่ได้ระบุที่อยู่ — ใส่ url" },
        ["browser.url.invalid"] = new() { ["en"] = "\"{0}\" is not a valid address.", ["th"] = "\"{0}\" ไม่ใช่ที่อยู่ที่ถูกต้อง" },
        ["browser.url.fileOutside"] = new()
        {
            ["en"] = "\"{0}\" is outside the open project — the built-in browser opens only files inside the project (or serve them with bg_serve).",
            ["th"] = "\"{0}\" อยู่นอกโปรเจกต์ที่เปิดอยู่ — เบราว์เซอร์ในตัวเปิดได้เฉพาะไฟล์ในโปรเจกต์ (หรือเสิร์ฟด้วย bg_serve)",
        },
        ["browser.url.scheme"] = new()
        {
            ["en"] = "\"{0}:\" addresses are not opened by the built-in browser — use http://localhost:PORT or a project file.",
            ["th"] = "เบราว์เซอร์ในตัวไม่เปิดที่อยู่แบบ \"{0}:\" — ใช้ http://localhost:PORT หรือไฟล์ในโปรเจกต์",
        },
        ["browser.url.externalDenied"] = new()
        {
            ["en"] = "Opening sites outside this PC is switched off (external pages: deny): {0}",
            ["th"] = "ปิดการเปิดเว็บไซต์ภายนอกเครื่องนี้ไว้ (หน้าเว็บภายนอก: ปฏิเสธ): {0}",
        },
        ["browser.bg.unknown"] = new()
        {
            ["en"] = "Unknown background process \"{0}\". Start a server with bg_serve or bg_start, and see the running ones with bg_list.",
            ["th"] = "ไม่รู้จักโปรเซสเบื้องหลัง \"{0}\" เริ่มเซิร์ฟเวอร์ด้วย bg_serve หรือ bg_start และดูที่กำลังทำงานด้วย bg_list",
        },
        ["browser.bg.noUrl"] = new()
        {
            ["en"] = "Background process \"{0}\" has no address to open: {1}",
            ["th"] = "โปรเซสเบื้องหลัง \"{0}\" ไม่มีที่อยู่ให้เปิด: {1}",
        },
        ["browser.open.failed"] = new() { ["en"] = "Could not open {0}: {1}", ["th"] = "เปิด {0} ไม่สำเร็จ: {1}" },
        ["browser.request.blocked"] = new()
        {
            ["en"] = "CluadeX blocked a {0} — it was never sent (the page got a 403). If the page needs that site, call browser_open again with allow=\"{1}\" (the user is asked once), or the user allows it under Features ▸ Dev Preview ▸ Sites outside this PC.",
            ["th"] = "CluadeX บล็อก {0} — คำขอนี้ไม่ได้ถูกส่งออกไป (หน้าเว็บได้รับ 403) หากหน้าเว็บต้องใช้เว็บนั้น ให้เรียก browser_open อีกครั้งพร้อม allow=\"{1}\" (ระบบจะถามผู้ใช้หนึ่งครั้ง) หรือผู้ใช้อนุญาตได้ที่ ฟีเจอร์ ▸ Dev Preview ▸ เว็บไซต์นอกเครื่องนี้",
        },
        ["browser.request.fileOutside"] = new()
        {
            ["en"] = "CluadeX blocked {0} — the file is outside the open project, so the page may not load it. Copy it into the project, or serve its folder with bg_serve.",
            ["th"] = "CluadeX บล็อก {0} — ไฟล์นี้อยู่นอกโปรเจกต์ที่เปิดอยู่ หน้าเว็บจึงโหลดไม่ได้ ให้คัดลอกไฟล์เข้ามาในโปรเจกต์ หรือเสิร์ฟโฟลเดอร์นั้นด้วย bg_serve",
        },
        ["browser.perm.allowSites"] = new()
        {
            ["en"] = "The page may contact these sites: {0}",
            ["th"] = "หน้าเว็บจะติดต่อเว็บไซต์เหล่านี้ได้: {0}",
        },
        ["browser.perm.read"] = new()
        {
            ["en"] = "Read this page outside your PC (text, screenshot, console) in this chat: {0}",
            ["th"] = "อ่านหน้าเว็บนอกเครื่องนี้ (ข้อความ ภาพหน้าจอ คอนโซล) ในแชทนี้: {0}",
        },
        ["browser.open.badAllow"] = new()
        {
            ["en"] = "allow must list http(s) or ws(s) sites, e.g. allow=\"https://api.example.com\" — not: {0}",
            ["th"] = "allow ต้องเป็นรายชื่อเว็บไซต์ http(s) หรือ ws(s) เช่น allow=\"https://api.example.com\" — ไม่ใช่: {0}",
        },
        ["browser.open.windowClosed"] = new()
        {
            ["en"] = "(The user closed the preview window in this chat, so it was not opened again — the page runs off-screen; the PREVIEW button in the code workbench shows it.)",
            ["th"] = "(ผู้ใช้ปิดหน้าต่างพรีวิวในแชทนี้แล้ว จึงไม่เปิดขึ้นมาอีก — หน้าเว็บยังทำงานอยู่นอกจอ ปุ่ม พรีวิว ในหน้าแก้ไขโค้ดจะแสดงหน้านี้)",
        },
        ["browser.open.serviceWorker"] = new()
        {
            ["en"] = "A service worker controls this page: it answers the page's requests before the network (a Mock Service Worker, or a PWA cache that may serve files older than your edit). If the page shows stale files or unexpected answers, call browser_open again with fresh=true — it removes the site's service workers and cache.",
            ["th"] = "หน้านี้มี service worker ควบคุมอยู่ ซึ่งจะตอบคำขอของหน้าเว็บก่อนถึงเครือข่าย (เช่น Mock Service Worker หรือแคชของ PWA ที่อาจส่งไฟล์เก่ากว่าที่คุณแก้ไว้) หากหน้าเว็บแสดงไฟล์เก่าหรือคำตอบที่ไม่คาดคิด ให้เรียก browser_open อีกครั้งพร้อม fresh=true เพื่อลบ service worker และแคชของเว็บนั้น",
        },
        ["browser.open.blocked"] = new() { ["en"] = "Opening {0} was blocked: {1}", ["th"] = "การเปิด {0} ถูกบล็อก: {1}" },
        // ═══════ Tool errors ═══════
        ["browser.err.noPage"] = new() { ["en"] = "No page is open — call browser_open first.", ["th"] = "ยังไม่มีหน้าเว็บที่เปิดอยู่ — เรียก browser_open ก่อน" },
        ["browser.err.crashed"] = new() { ["en"] = "The page crashed. Open it again with browser_open.", ["th"] = "หน้าเว็บแครช เปิดใหม่ด้วย browser_open" },
        ["browser.err.timeout"] = new()
        {
            ["en"] = "The page did not answer in time ({0}). It may be stuck in a long script — try browser_open to reload it.",
            ["th"] = "หน้าเว็บไม่ตอบสนองภายในเวลาที่กำหนด ({0}) อาจติดอยู่ในสคริปต์ที่ทำงานนาน — ลอง browser_open เพื่อโหลดใหม่",
        },
        ["browser.err.engine"] = new() { ["en"] = "The browser engine refused the command: {0}", ["th"] = "เอนจินเบราว์เซอร์ปฏิเสธคำสั่ง: {0}" },
        ["browser.err.staleRef"] = new()
        {
            ["en"] = "\"{0}\" no longer points at an element (the page changed). Get fresh refs with browser_find or browser_read mode=interactive.",
            ["th"] = "\"{0}\" ไม่ชี้ไปที่องค์ประกอบใดแล้ว (หน้าเว็บเปลี่ยนไป) ขอ ref ใหม่ด้วย browser_find หรือ browser_read mode=interactive",
        },
        ["browser.err.noTarget"] = new() { ["en"] = "Give a ref, or both x and y.", ["th"] = "ระบุ ref หรือทั้ง x และ y" },
        ["browser.err.badRegion"] = new()
        {
            ["en"] = "region must be \"x0,y0,x1,y1\" in the previous screenshot's pixels, with x1 > x0 and y1 > y0.",
            ["th"] = "region ต้องเป็น \"x0,y0,x1,y1\" ตามพิกเซลของภาพหน้าจอก่อนหน้า โดย x1 > x0 และ y1 > y0",
        },
        ["browser.err.noShot"] = new()
        {
            ["en"] = "Take a browser_screenshot first — region is measured in its pixels.",
            ["th"] = "ถ่าย browser_screenshot ก่อน — region วัดตามพิกเซลของภาพนั้น",
        },
        ["browser.err.badAction"] = new()
        {
            ["en"] = "Unknown action \"{0}\". Use move, down, up, press, drag or hover.",
            ["th"] = "ไม่รู้จัก action \"{0}\" ใช้ move, down, up, press, drag หรือ hover",
        },
        ["browser.err.badKey"] = new()
        {
            ["en"] = "Unknown key \"{0}\". Examples: Enter, Space, ArrowLeft, a, ctrl+a, Shift+Tab, F5.",
            ["th"] = "ไม่รู้จักปุ่ม \"{0}\" ตัวอย่าง: Enter, Space, ArrowLeft, a, ctrl+a, Shift+Tab, F5",
        },
        ["browser.err.noText"] = new() { ["en"] = "Nothing to type — text is empty.", ["th"] = "ไม่มีข้อความให้พิมพ์ — text ว่างอยู่" },
        ["browser.err.noQuery"] = new() { ["en"] = "Nothing to look for — query is empty.", ["th"] = "ไม่มีคำให้ค้นหา — query ว่างอยู่" },
        ["browser.err.badExpression"] = new() { ["en"] = "No JavaScript given — pass expression.", ["th"] = "ไม่ได้ระบุ JavaScript — ใส่ expression" },
        ["browser.err.badPreset"] = new()
        {
            ["en"] = "Unknown preset \"{0}\". Use mobile, tablet, desktop, or width and height.",
            ["th"] = "ไม่รู้จัก preset \"{0}\" ใช้ mobile, tablet, desktop หรือ width และ height",
        },
        ["browser.err.pageMoved"] = new()
        {
            ["en"] = "Nothing was done: the page moved to {0} while this call waited (it was asked about {1}). This chat has not approved {0} — call the tool again and the user is asked about it.",
            ["th"] = "ยังไม่ได้ทำอะไร: หน้าเว็บย้ายไปที่ {0} ระหว่างที่คำสั่งนี้รออยู่ (สิทธิ์ที่ขอไว้เป็นของ {1}) แชทนี้ยังไม่ได้อนุญาต {0} — เรียกเครื่องมืออีกครั้ง แล้วระบบจะถามผู้ใช้ก่อน",
        },
        ["browser.err.unexpected"] = new()
        {
            ["en"] = "The browser tool failed unexpectedly. Details are in the Debug Log.",
            ["th"] = "เครื่องมือเบราว์เซอร์ทำงานผิดพลาดโดยไม่คาดคิด ดูรายละเอียดใน Debug Log",
        },
        ["browser.type.sensitive"] = new()
        {
            ["en"] = "Refused: that is a password / payment card / one-time-code field on {0}. The agent never types credentials on sites outside this PC — ask the user to fill it in.",
            ["th"] = "ปฏิเสธ: ช่องนั้นเป็นช่องรหัสผ่าน / บัตรชำระเงิน / รหัสครั้งเดียว บน {0} เอเจนต์จะไม่พิมพ์ข้อมูลลับบนเว็บนอกเครื่องนี้ — ให้ผู้ใช้กรอกเอง",
        },
        ["browser.type.noField"] = new()
        {
            ["en"] = "No editable element has focus — pass ref (from browser_find) or click the field first.",
            ["th"] = "ไม่มีช่องที่แก้ไขได้ถูกโฟกัสอยู่ — ใส่ ref (จาก browser_find) หรือคลิกช่องนั้นก่อน",
        },
        ["browser.closed"] = new()
        {
            ["en"] = "Closed the built-in browser. Background servers keep running; browser_open starts a fresh page.",
            ["th"] = "ปิดเบราว์เซอร์ในตัวแล้ว เซิร์ฟเวอร์เบื้องหลังยังทำงานอยู่ — browser_open จะเริ่มหน้าใหม่",
        },
        // ═══════ Preview pane ═══════
        ["preview.title"] = new() { ["en"] = "PREVIEW", ["th"] = "พรีวิว" },
        ["preview.toggleTip"] = new()
        {
            ["en"] = "Show or hide the built-in browser the agent drives (Dev Preview)",
            ["th"] = "แสดงหรือซ่อนเบราว์เซอร์ในตัวที่เอเจนต์ควบคุม (Dev Preview)",
        },
        ["preview.addressTip"] = new()
        {
            ["en"] = "Address — Enter opens it (localhost and project files open freely)",
            ["th"] = "ที่อยู่ — กด Enter เพื่อเปิด (localhost และไฟล์ในโปรเจกต์เปิดได้เลย)",
        },
        ["preview.go"] = new() { ["en"] = "Go", ["th"] = "ไป" },
        ["preview.reload"] = new() { ["en"] = "Reload", ["th"] = "โหลดใหม่" },
        ["preview.popout"] = new() { ["en"] = "Open in its own window", ["th"] = "เปิดในหน้าต่างแยก" },
        ["preview.local"] = new() { ["en"] = "LOCAL", ["th"] = "ในเครื่อง" },
        ["preview.projectFile"] = new() { ["en"] = "PROJECT FILE", ["th"] = "ไฟล์โปรเจกต์" },
        ["preview.external"] = new() { ["en"] = "EXTERNAL", ["th"] = "ภายนอก" },
        ["preview.desktop"] = new() { ["en"] = "Desktop", ["th"] = "เดสก์ท็อป" },
        ["preview.mobile"] = new() { ["en"] = "Mobile", ["th"] = "มือถือ" },
        ["preview.tablet"] = new() { ["en"] = "Tablet", ["th"] = "แท็บเล็ต" },
        ["preview.custom"] = new() { ["en"] = "Custom", ["th"] = "กำหนดเอง" },
        ["preview.viewportTip"] = new()
        {
            ["en"] = "Viewport — click to switch Desktop / Mobile (375×812, touch)",
            ["th"] = "ขนาดจอ — คลิกเพื่อสลับ เดสก์ท็อป / มือถือ (375×812 สัมผัส)",
        },
        ["preview.driving"] = new() { ["en"] = "Agent is driving", ["th"] = "เอเจนต์กำลังควบคุม" },
        ["preview.drivingTip"] = new()
        {
            ["en"] = "The agent is using the page right now — you can still click and play",
            ["th"] = "เอเจนต์กำลังใช้หน้านี้อยู่ — คุณยังคลิกและเล่นได้ตามปกติ",
        },
        ["preview.console"] = new() { ["en"] = "Console", ["th"] = "คอนโซล" },
        ["preview.consoleEmpty"] = new() { ["en"] = "No console messages.", ["th"] = "ยังไม่มีข้อความในคอนโซล" },
        ["preview.consoleCounts"] = new() { ["en"] = "{0} errors · {1} warnings", ["th"] = "ข้อผิดพลาด {0} · คำเตือน {1}" },
        ["preview.empty.title"] = new() { ["en"] = "No page open", ["th"] = "ยังไม่มีหน้าเว็บที่เปิดอยู่" },
        ["preview.empty.hint"] = new()
        {
            ["en"] = "The agent opens pages with browser_open — or type a localhost address above and press Enter.",
            ["th"] = "เอเจนต์จะเปิดหน้าเว็บด้วย browser_open — หรือพิมพ์ที่อยู่ localhost ด้านบนแล้วกด Enter",
        },
        ["preview.engineMissing"] = new()
        {
            ["en"] = "The WebView2 Runtime is not installed. Run this command, then reopen the pane:",
            ["th"] = "ยังไม่ได้ติดตั้ง WebView2 Runtime รันคำสั่งนี้ แล้วเปิดแผงนี้ใหม่:",
        },
        ["preview.copyCommand"] = new() { ["en"] = "Copy command", ["th"] = "คัดลอกคำสั่ง" },
        ["preview.copied"] = new() { ["en"] = "Copied", ["th"] = "คัดลอกแล้ว" },
        ["preview.elsewhere"] = new()
        {
            ["en"] = "The page is shown in the other preview window.",
            ["th"] = "หน้าเว็บกำลังแสดงอยู่ในหน้าต่างพรีวิวอีกอัน",
        },
        ["preview.bringHere"] = new() { ["en"] = "Show it here", ["th"] = "แสดงที่นี่" },
        ["preview.locked"] = new()
        {
            ["en"] = "Dev Preview is not available:",
            ["th"] = "ยังใช้ Dev Preview ไม่ได้:",
        },
        ["preview.windowTitle"] = new() { ["en"] = "CluadeX Preview", ["th"] = "CluadeX พรีวิว" },
        ["preview.starting"] = new() { ["en"] = "Starting the browser…", ["th"] = "กำลังเปิดเบราว์เซอร์…" },
        ["preview.openFailed"] = new() { ["en"] = "Could not open: {0}", ["th"] = "เปิดไม่สำเร็จ: {0}" },
        ["preview.crashed"] = new()
        {
            ["en"] = "The page crashed — press Reload to start it again.",
            ["th"] = "หน้าเว็บแครช — กด โหลดใหม่ เพื่อเริ่มอีกครั้ง",
        },
        ["preview.closePage"] = new()
        {
            ["en"] = "Close the page and stop the browser (frees its memory and GPU)",
            ["th"] = "ปิดหน้าเว็บและหยุดเบราว์เซอร์ (คืนหน่วยความจำและ GPU)",
        },
        ["preview.checkAgain"] = new() { ["en"] = "Check again", ["th"] = "ตรวจอีกครั้ง" },
        ["preview.covered"] = new()
        {
            ["en"] = "The page is hidden while this dialog is open — it keeps running.",
            ["th"] = "ซ่อนหน้าเว็บไว้ขณะที่หน้าต่างนี้เปิดอยู่ — หน้าเว็บยังทำงานต่อ",
        },
        ["preview.err.engine"] = new()
        {
            ["en"] = "The browser could not do that — try Reload. (Details are in the Debug Log.)",
            ["th"] = "เบราว์เซอร์ทำรายการนั้นไม่ได้ — ลอง โหลดใหม่ (รายละเอียดอยู่ใน Debug Log)",
        },
        ["preview.err.closed"] = new() { ["en"] = "The page was closed.", ["th"] = "หน้าเว็บถูกปิดแล้ว" },
        ["preview.err.timeout"] = new()
        {
            ["en"] = "The page did not answer in time — it may be busy in a long script. Try Reload.",
            ["th"] = "หน้าเว็บไม่ตอบสนองภายในเวลาที่กำหนด — อาจกำลังรันสคริปต์นาน ลอง โหลดใหม่",
        },
        ["preview.externalDenied"] = new()
        {
            ["en"] = "Sites outside this PC are blocked (Features ▸ Dev Preview): {0}",
            ["th"] = "บล็อกเว็บไซต์นอกเครื่องนี้ไว้ (ฟีเจอร์ ▸ Dev Preview): {0}",
        },
        ["preview.popupBlocked"] = new()
        {
            ["en"] = "Pop-up not opened: {0} — type it in the address bar to open it here.",
            ["th"] = "ไม่ได้เปิดป๊อปอัป: {0} — พิมพ์ที่อยู่นี้ในช่องที่อยู่เพื่อเปิดที่นี่",
        },
        // ═══════ Features page row ═══════
        ["devPreview.engineFound"] = new() { ["en"] = "Engine: {0}", ["th"] = "เอนจิน: {0}" },
        ["devPreview.engineMissing"] = new()
        {
            ["en"] = "WebView2 Runtime not installed — install with: {0}",
            ["th"] = "ยังไม่ได้ติดตั้ง WebView2 Runtime — ติดตั้งด้วย: {0}",
        },
        ["devPreview.externalLabel"] = new() { ["en"] = "Sites outside this PC:", ["th"] = "เว็บไซต์นอกเครื่องนี้:" },
        ["devPreview.externalTip"] = new()
        {
            ["en"] = "What the built-in browser does with sites that are not on this PC: ask before the agent opens one (and before a page calls one), open them without asking, or block them all",
            ["th"] = "เบราว์เซอร์ในตัวจะทำอย่างไรกับเว็บไซต์ที่ไม่ได้อยู่ในเครื่องนี้: ถามก่อนเอเจนต์เปิด (และก่อนหน้าเว็บเรียกใช้) เปิดได้โดยไม่ต้องถาม หรือบล็อกทั้งหมด",
        },
        ["devPreview.external.ask"] = new() { ["en"] = "Ask", ["th"] = "ถามก่อน" },
        ["devPreview.external.allow"] = new() { ["en"] = "Allow", ["th"] = "อนุญาต" },
        ["devPreview.external.deny"] = new() { ["en"] = "Block", ["th"] = "บล็อก" },
    };
}
