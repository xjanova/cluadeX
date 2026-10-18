namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the UI Studio's page gallery and simulated app screens. Every key needs "en" and "th".</summary>
internal static class StudioPageKeys
{
    private static Dictionary<string, string> E(string en, string th) => new() { ["en"] = en, ["th"] = th };

    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        // ═══════ project kinds ═══════
        ["studio.kind.server"] = E("{0} · opened where it runs", "{0} · เปิดจากที่เว็บรันอยู่"),
        ["studio.kind.simulated"] = E("{0} · screens simulated from the code", "{0} · จำลองหน้าจอจากโค้ด"),

        // ═══════ gallery: chrome ═══════
        ["studio.pages.button"] = E("Pages", "หน้าทั้งหมด"),
        ["studio.pages.tip"] = E("Every page of the project as a thumbnail — pick one to load it here and edit it", "ทุกหน้าของโปรเจกต์เป็นภาพย่อ — เลือกหน้าที่จะโหลดมาแก้ที่นี่"),
        ["studio.pages.title"] = E("All pages", "หน้าทั้งหมดในโปรเจกต์"),
        ["studio.pages.subtitle.web"] = E("Real screenshots, kept and re-taken when the code changes. Click a page to open it for editing.", "ภาพหน้าจริงที่ถ่ายเก็บไว้ และถ่ายใหม่เมื่อโค้ดเปลี่ยน — คลิกหน้าไหนก็เปิดมาแก้ได้ทันที"),
        ["studio.pages.subtitle.sim"] = E("This app can't run in the Studio's browser, so each screen is drawn from its code. Click one to open it; click any part of it to jump to its line.", "แอปนี้รันในเบราว์เซอร์ของ Studio ไม่ได้ จึงวาดแต่ละหน้าจอจากโค้ด — คลิกเพื่อเปิด แล้วคลิกส่วนไหนก็กระโดดไปบรรทัดนั้น"),
        ["studio.pages.subtitle.server"] = E("Pages from routes/web.php, shot from where the site runs. Click a page to open it.", "หน้าจาก routes/web.php ถ่ายจากที่เว็บรันอยู่ — คลิกหน้าไหนก็เปิดหน้านั้น"),
        ["studio.pages.search"] = E("Search pages, routes, files…", "ค้นหน้า เส้นทาง หรือไฟล์…"),
        ["studio.pages.refreshTip"] = E("Read the project's pages again", "อ่านหน้าของโปรเจกต์ใหม่"),
        ["studio.pages.shootAll"] = E("Shoot all", "ถ่ายภาพทั้งหมด"),
        ["studio.pages.shootAllTip"] = E("Take a fresh thumbnail of every page shown (pages that need a value or might DO something are skipped)", "ถ่ายภาพใหม่ทุกหน้าที่แสดงอยู่ (ข้ามหน้าที่ต้องใส่ค่า หรือหน้าที่เปิดแล้วอาจทำอะไรบางอย่าง)"),
        ["studio.pages.base"] = E("SITE ADDRESS", "ที่อยู่เว็บ"),
        ["studio.pages.baseHint"] = E("Where this site runs: Laragon (http://name.test), php artisan serve (http://localhost:8000) or the live site", "เว็บนี้รันอยู่ที่ไหน: Laragon (http://ชื่อ.test), php artisan serve (http://localhost:8000) หรือเว็บจริง"),
        ["studio.pages.baseApply"] = E("Use", "ใช้ที่อยู่นี้"),
        ["studio.pages.baseSet"] = E("Pages open from {0}", "เปิดหน้าจาก {0}"),
        ["studio.pages.remoteNote"] = E("This site is on the internet: a thumbnail is a real visit, so they are taken only when you ask.", "เว็บนี้อยู่บนอินเทอร์เน็ต — การถ่ายภาพคือการเข้าเว็บจริง จึงถ่ายเมื่อคุณสั่งเท่านั้น"),
        ["studio.pages.addWeb"] = E("ADD A WEB PAGE", "เพิ่มหน้าเว็บ"),
        ["studio.pages.addWebHint"] = E("Any web page — https://…", "หน้าเว็บใดก็ได้ — https://…"),
        ["studio.pages.add"] = E("Add", "เพิ่ม"),
        ["studio.pages.startForShots"] = E("Start the preview to take the thumbnails", "เริ่มรันพรีวิวก่อนเพื่อถ่ายภาพ"),
        ["studio.pages.discovering"] = E("Reading the project's pages…", "กำลังอ่านหน้าของโปรเจกต์…"),
        ["studio.pages.empty"] = E("No pages found in this project.", "ไม่พบหน้าในโปรเจกต์นี้"),
        ["studio.pages.failed"] = E("Could not read the project's pages — see the Debug Log.", "อ่านหน้าของโปรเจกต์ไม่สำเร็จ — ดูที่ Debug Log"),
        ["studio.pages.summary"] = E("{0} pages · {1} with a picture", "{0} หน้า · มีภาพแล้ว {1}"),
        ["studio.pages.summaryFiltered"] = E("{0} of {1} pages · {2} with a picture", "{0} จาก {1} หน้า · มีภาพแล้ว {2}"),

        // ═══════ gallery: sections ═══════
        ["studio.pages.all"] = E("All", "ทั้งหมด"),
        ["studio.pages.routed"] = E("Routes", "มีเส้นทาง"),
        ["studio.pages.otherScreens"] = E("Other screens", "หน้าจออื่น"),
        ["studio.pages.views"] = E("Views", "หน้าจอ (UserControl)"),
        ["studio.pages.windows"] = E("Windows", "หน้าต่าง (Window)"),
        ["studio.pages.withParams"] = E("Need a value", "ต้องใส่ค่า"),
        ["studio.pages.webPages"] = E("Web pages", "หน้าเว็บที่เพิ่ม"),

        // ═══════ gallery: card states ═══════
        ["studio.pages.queued"] = E("waiting to shoot", "รอถ่ายภาพ"),
        ["studio.pages.shooting"] = E("shooting…", "กำลังถ่ายภาพ…"),
        ["studio.pages.shotAt"] = E("shot {0}", "ถ่ายเมื่อ {0}"),
        ["studio.pages.httpError"] = E("HTTP {0} · {1}", "HTTP {0} · {1}"),
        ["studio.pages.failedShot"] = E("couldn't open — is it running?", "เปิดไม่ได้ — เว็บรันอยู่หรือเปล่า?"),
        ["studio.pages.needsParams"] = E("needs {0}", "ต้องใส่ {0}"),
        ["studio.pages.noServer"] = E("start the preview to shoot it", "เริ่มรันพรีวิวก่อนจึงถ่ายได้"),
        ["studio.pages.noBase"] = E("set the site address above", "ตั้งที่อยู่เว็บด้านบนก่อน"),
        ["studio.pages.risky"] = E("not shot on its own — opening it may DO something", "ไม่ถ่ายเอง — เปิดหน้านี้อาจทำให้เกิดการกระทำบางอย่าง"),
        ["studio.pages.manual"] = E("on the internet — click the camera to shoot", "อยู่บนอินเทอร์เน็ต — กดปุ่มกล้องเพื่อถ่าย"),
        ["studio.pages.simulated"] = E("simulated from the code", "จำลองจากโค้ด"),
        ["studio.pages.noSketch"] = E("couldn't draw this screen", "วาดหน้าจอนี้ไม่ได้"),
        ["studio.pages.login"] = E("login", "ต้องล็อกอิน"),
        ["studio.pages.stale"] = E("old", "ภาพเก่า"),
        ["studio.pages.current"] = E("open now", "เปิดอยู่"),
        ["studio.pages.shootOne"] = E("Take a new thumbnail", "ถ่ายภาพใหม่"),
        ["studio.pages.openCode"] = E("Open its code", "เปิดโค้ดของหน้านี้"),
        ["studio.pages.remove"] = E("Remove from the gallery", "เอาออกจากแกลเลอรี"),

        // ═══════ gallery: actions ═══════
        ["studio.pages.fillParams"] = E("Replace {0} in the address bar with a real value, then press Enter", "ใส่ค่าจริงแทน {0} ในช่องที่อยู่ แล้วกด Enter"),
        ["studio.pages.fillParamsShort"] = E("This page needs a value in its address", "หน้านี้ต้องใส่ค่าในที่อยู่ก่อน"),
        ["studio.pages.nothingToShoot"] = E("Nothing to shoot here", "ไม่มีหน้าที่ถ่ายได้"),
        ["studio.pages.riskyTitle"] = E("Open this page?", "เปิดหน้านี้?"),
        ["studio.pages.riskyBody"] = E("{0}\n\nIts address looks like an action (log out, delete, pay, run…). Opening it — even just for a thumbnail — may do that. Shoot it anyway?", "{0}\n\nที่อยู่นี้ดูเหมือนเป็นการกระทำ (ออกจากระบบ ลบ จ่ายเงิน รันงาน…) การเปิด — แม้แค่ถ่ายภาพ — อาจทำสิ่งนั้นจริง จะถ่ายต่อไหม?"),
        ["studio.pages.shootAnyway"] = E("Shoot anyway", "ถ่ายต่อ"),
        ["studio.pages.remoteTitle"] = E("Visit pages on the internet?", "เข้าหน้าเว็บบนอินเทอร์เน็ต?"),
        ["studio.pages.remoteBody"] = E("To shoot {0} pages the Studio will open each one on {1} (as if you visited it, with your login). Pages that may DO something are skipped.", "เพื่อถ่าย {0} หน้า Studio จะเปิดทีละหน้าบน {1} (เหมือนคุณเข้าเว็บเอง ด้วยการล็อกอินของคุณ) — ข้ามหน้าที่อาจทำให้เกิดการกระทำ"),

        // ═══════ simulated stage ═══════
        ["studio.stage.screens"] = E("Screens", "หน้าจอ"),
        ["studio.stage.hint"] = E("Click a part of the screen to see where it is written · Ctrl+click opens its line", "คลิกส่วนไหนของหน้าจอเพื่อดูว่าเขียนไว้ที่ไหน · Ctrl+คลิก เปิดบรรทัดนั้นทันที"),
        ["studio.stage.approx"] = E("A wireframe drawn from the code — the real app shows real data and its own styling.", "ภาพร่างที่วาดจากโค้ด — แอปจริงจะแสดงข้อมูลจริงและสไตล์ของแอปเอง"),
        ["studio.stage.widget"] = E("WIDGET", "WIDGET"),
        ["studio.stage.noNode"] = E("Click any part of the simulated screen to pick it.", "คลิกส่วนใดของหน้าจอจำลองเพื่อเลือก"),
        ["studio.stage.usedAt"] = E("used by this screen at {0}", "หน้าจอนี้ใช้ที่ {0}"),
        ["studio.stage.openCode"] = E("Open its line", "เปิดบรรทัดนี้"),
        ["studio.stage.openScreen"] = E("Open the screen's file", "เปิดไฟล์ของหน้าจอ"),
        ["studio.stage.aiTitle"] = E("ASK AI TO CHANGE THIS SCREEN", "ให้ AI แก้หน้าจอนี้"),
        ["studio.stage.aiHint"] = E("The AI gets the screen, its file and the part you picked.", "AI จะได้รู้ว่าหน้าจอไหน ไฟล์ไหน และส่วนที่คุณเลือก"),
        ["studio.stage.unknown"] = E("{0} parts drawn as labelled boxes (widgets the sketch can't read)", "มี {0} ส่วนที่วาดเป็นกล่องชื่อ (widget ที่ภาพร่างอ่านไม่ออก)"),
    };
}
