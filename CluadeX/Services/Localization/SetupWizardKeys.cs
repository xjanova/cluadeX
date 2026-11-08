namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the first-run setup wizard, the Xman Studio account sign-in, the connection doctor and
/// the redesigned update screens (installer contract 2.x; Thai copy from the approved design boards). Every key needs both
/// "en" and "th".</summary>
internal static class SetupWizardKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        // ── error codes → sentences (contract 2.5) ──
        ["acct.err.invalidCredentials"] = new() { ["en"] = "The e-mail or password is wrong.", ["th"] = "อีเมลหรือรหัสผ่านไม่ถูกต้อง" },
        ["acct.err.accountDisabled"] = new() { ["en"] = "This account is suspended — contact the team.", ["th"] = "บัญชีนี้ถูกระงับ ติดต่อทีมงาน" },
        ["acct.err.rateLimit"] = new() { ["en"] = "Too many tries — wait a minute.", ["th"] = "ลองบ่อยเกินไป รอสัก 1 นาที" },
        ["acct.err.expiredToken"] = new() { ["en"] = "The code has expired — get a new one.", ["th"] = "โค้ดหมดอายุ ขอโค้ดใหม่" },
        ["acct.err.accessDenied"] = new() { ["en"] = "The link was cancelled on the phone.", ["th"] = "ยกเลิกการเชื่อมจากมือถือแล้ว" },
        ["acct.err.otherDevice"] = new() { ["en"] = "This license is in use on another PC.", ["th"] = "ไลเซ่นนี้ใช้อยู่บนเครื่องอื่น" },
        ["acct.err.invalidLicense"] = new() { ["en"] = "The key is not valid.", ["th"] = "คีย์ไม่ถูกต้อง" },
        ["acct.err.licenseExpired"] = new() { ["en"] = "The license has expired.", ["th"] = "ไลเซ่นหมดอายุแล้ว" },
        ["acct.err.licenseRevoked"] = new() { ["en"] = "The license was revoked.", ["th"] = "ไลเซ่นถูกยกเลิก" },
        ["acct.err.trialUsed"] = new() { ["en"] = "This PC has already used its trial.", ["th"] = "เครื่องนี้ใช้สิทธิ์ทดลองไปแล้ว" },
        ["acct.err.deviceBlocked"] = new() { ["en"] = "This PC is blocked — contact the team.", ["th"] = "เครื่องนี้ถูกระงับ ติดต่อทีมงาน" },
        ["acct.err.freeNotAvailable"] = new() { ["en"] = "The free license is not open for this program yet.", ["th"] = "ไลเซ่นฟรียังไม่เปิดสำหรับโปรแกรมนี้" },
        ["acct.err.productNotFound"] = new() { ["en"] = "The server does not know this program.", ["th"] = "เซิร์ฟเวอร์ไม่รู้จักโปรแกรมนี้" },
        ["acct.err.validation"] = new() { ["en"] = "Some details were missing — check and try again.", ["th"] = "ข้อมูลไม่ครบ ตรวจแล้วลองอีกครั้ง" },
        ["acct.err.emptyLogin"] = new() { ["en"] = "Enter your Xman Studio e-mail or ID.", ["th"] = "กรอกอีเมลหรือไอดี Xman Studio" },
        ["acct.err.emptyPassword"] = new() { ["en"] = "Enter your password.", ["th"] = "กรอกรหัสผ่าน" },
        ["acct.err.emptyKey"] = new() { ["en"] = "Enter your license key.", ["th"] = "กรอกคีย์ไลเซ่น" },
        ["acct.err.notDeployed"] = new() { ["en"] = "The server does not support signing in from the app yet.", ["th"] = "เซิร์ฟเวอร์ยังไม่รองรับการเข้าสู่ระบบจากแอป" },
        ["acct.err.offline"] = new() { ["en"] = "Could not reach xman4289.com.", ["th"] = "เชื่อมต่อ xman4289.com ไม่ได้" },
        ["acct.err.serverError"] = new() { ["en"] = "The server has a temporary problem — try again shortly.", ["th"] = "เซิร์ฟเวอร์มีปัญหาชั่วคราว ลองใหม่อีกสักครู่" },
        ["acct.err.notJson"] = new() { ["en"] = "A web page answered instead of the server — a Wi-Fi that needs a login first, or a block.", ["th"] = "มีหน้าเว็บอื่นตอบแทนเซิร์ฟเวอร์ อาจติด Wi-Fi ที่ต้องล็อกอินก่อน หรือถูกบล็อก" },
        ["acct.err.badResponse"] = new() { ["en"] = "The server's answer could not be read.", ["th"] = "อ่านคำตอบของเซิร์ฟเวอร์ไม่ได้" },
        ["acct.err.generic"] = new() { ["en"] = "Something went wrong.", ["th"] = "เกิดข้อผิดพลาด" },
        ["acct.err.unknown"] = new() { ["en"] = "The server answered {0}.", ["th"] = "เซิร์ฟเวอร์ตอบว่า {0}" },

        // ── connection doctor (contract 2.4) ──
        ["acct.doc.title"] = new() { ["en"] = "Connection check", ["th"] = "ตรวจการเชื่อมต่อ" },
        ["acct.doc.run"] = new() { ["en"] = "Check the connection", ["th"] = "ตรวจการเชื่อมต่อ" },
        ["acct.doc.running"] = new() { ["en"] = "Checking the connection…", ["th"] = "กำลังตรวจการเชื่อมต่อ…" },
        ["acct.doc.copy"] = new() { ["en"] = "Copy report", ["th"] = "คัดลอกรายงาน" },
        ["acct.doc.copied"] = new() { ["en"] = "Report copied", ["th"] = "คัดลอกรายงานแล้ว" },
        ["acct.doc.send"] = new() { ["en"] = "Send to the team", ["th"] = "ส่งให้ทีมงาน" },
        ["acct.doc.send.ok"] = new() { ["en"] = "Sent — quote {0} when you talk to the team.", ["th"] = "ส่งแล้ว แจ้งรหัส {0} กับทีมงานได้เลย" },
        ["acct.doc.send.fail"] = new() { ["en"] = "Could not send it — copy the report and send it by LINE or e-mail instead.", ["th"] = "ส่งไม่สำเร็จ คัดลอกรายงานแล้วส่งทาง LINE หรืออีเมลแทน" },
        ["acct.doc.saved"] = new() { ["en"] = "Saved to {0}", ["th"] = "บันทึกไว้ที่ {0}" },
        ["acct.doc.allGood"] = new() { ["en"] = "Everything answered — the connection is fine.", ["th"] = "ทุกอย่างตอบปกติ การเชื่อมต่อใช้ได้" },
        ["acct.doc.cause"] = new() { ["en"] = "Cause", ["th"] = "สาเหตุ" },
        ["acct.doc.close"] = new() { ["en"] = "Close", ["th"] = "ปิด" },
        ["acct.doc.skipped"] = new() { ["en"] = "skipped", ["th"] = "ข้าม" },
        ["acct.doc.step.network"] = new() { ["en"] = "Internet", ["th"] = "อินเทอร์เน็ต" },
        ["acct.doc.step.proxy"] = new() { ["en"] = "Proxy", ["th"] = "พร็อกซี" },
        ["acct.doc.step.dns"] = new() { ["en"] = "Find xman4289.com (DNS)", ["th"] = "หาที่อยู่ xman4289.com (DNS)" },
        ["acct.doc.step.tcp"] = new() { ["en"] = "Port 443", ["th"] = "พอร์ต 443" },
        ["acct.doc.step.tls"] = new() { ["en"] = "Secure connection (TLS)", ["th"] = "การเชื่อมต่อแบบปลอดภัย (TLS)" },
        ["acct.doc.step.api"] = new() { ["en"] = "Xman Studio server", ["th"] = "เซิร์ฟเวอร์ Xman Studio" },
        ["acct.doc.step.clock"] = new() { ["en"] = "PC clock", ["th"] = "นาฬิกาเครื่อง" },
        ["acct.doc.step.updates"] = new() { ["en"] = "Update check", ["th"] = "ตรวจหารุ่นใหม่" },
        ["acct.doc.network.fail"] = new() { ["en"] = "This PC is not connected to the internet.", ["th"] = "เครื่องนี้ไม่ได้ต่ออินเทอร์เน็ต" },
        ["acct.doc.proxy.used"] = new() { ["en"] = "Using the proxy {0}", ["th"] = "ใช้พร็อกซี {0}" },
        ["acct.doc.viaProxy"] = new() { ["en"] = "through the proxy — checked by the server step", ["th"] = "ผ่านพร็อกซี ตรวจรวมในขั้นเซิร์ฟเวอร์" },
        ["acct.doc.dns.fail"] = new() { ["en"] = "Could not find {0} (DNS) — the network may block it.", ["th"] = "หาที่อยู่ {0} ไม่เจอ (DNS) เน็ตอาจถูกบล็อก" },
        ["acct.doc.tcp.fail"] = new() { ["en"] = "Could not open port {0} — a firewall or a company network may block it.", ["th"] = "ต่อพอร์ต {0} ไม่ได้ ไฟร์วอลล์หรือเครือข่ายบริษัทอาจบล็อก" },
        ["acct.doc.tls.fail"] = new() { ["en"] = "The certificate does not match — a program intercepts the connection (antivirus/proxy), or the PC clock is wrong.", ["th"] = "ใบรับรองไม่ตรง มีโปรแกรมดักการเชื่อมต่อ (แอนตี้ไวรัส/พร็อกซี) หรือนาฬิกาเครื่องเพี้ยน" },
        ["acct.doc.tls.interceptor"] = new() { ["en"] = "The certificate comes from {0}, which intercepts secure connections — turn off its HTTPS scanning or add xman4289.com as an exception.", ["th"] = "ใบรับรองมาจาก {0} ซึ่งดักการเชื่อมต่อ ปิดการสแกน HTTPS หรือเพิ่ม xman4289.com เป็นข้อยกเว้น" },
        ["acct.doc.api.notDeployed"] = new() { ["en"] = "The server does not support signing in from the app yet.", ["th"] = "เซิร์ฟเวอร์ยังไม่รองรับการเข้าสู่ระบบจากแอป" },
        ["acct.doc.api.serverError"] = new() { ["en"] = "The server has a temporary problem.", ["th"] = "เซิร์ฟเวอร์มีปัญหาชั่วคราว" },
        ["acct.doc.api.rateLimit"] = new() { ["en"] = "Asked too often — wait a minute.", ["th"] = "ขอถี่เกินไป รอสัก 1 นาที" },
        ["acct.doc.api.notJson"] = new() { ["en"] = "A web page answered instead of the API — a Wi-Fi that needs a login first, or Cloudflare blocked it.", ["th"] = "ได้หน้าเว็บแทนข้อมูล ติด Wi-Fi ที่ต้องล็อกอินก่อน หรือถูก Cloudflare กัน" },
        ["acct.doc.api.offline"] = new() { ["en"] = "The server did not answer.", ["th"] = "เซิร์ฟเวอร์ไม่ตอบ" },
        ["acct.doc.clock.off"] = new() { ["en"] = "The PC clock is {0} minutes off — turn on automatic time in Windows.", ["th"] = "นาฬิกาเครื่องคลาด {0} นาที เปิดตั้งเวลาอัตโนมัติใน Windows" },
        ["acct.doc.updates.fail"] = new() { ["en"] = "Could not check for a new version.", ["th"] = "ตรวจหารุ่นใหม่ไม่ได้" },
    };
}
