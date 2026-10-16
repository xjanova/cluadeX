namespace CluadeX.Services;

/// <summary>The phone page served by <see cref="WatchWebServer"/>: one self-contained HTML file (no CDN — it must work
/// on a home network without internet), reading /api with the same key every 10 s. Every value from the projects is
/// put in with textContent, never as HTML.</summary>
public static class WatchWebPage
{
    public const string Html = """
<!doctype html>
<html lang="th">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="referrer" content="no-referrer">
<title>CluadeX · ติดตาม</title>
<style>
  :root { --bg:#140f24; --card:#1f1836; --line:#3a2f5c; --text:#f1ecff; --muted:#a99cc9; --faint:#7a6d9c;
          --accent:#b89cff; --ok:#5ee0a0; --bad:#ff6b8a; --warn:#ffc46b; }
  @media (prefers-color-scheme: light) {
    :root { --bg:#f6f2ff; --card:#ffffff; --line:#e1d8f7; --text:#24183f; --muted:#6d5f92; --faint:#9a8fbb;
            --accent:#7a52e0; --ok:#1f9d64; --bad:#d63358; --warn:#b97a10; }
  }
  * { box-sizing: border-box; }
  body { margin:0; background:var(--bg); color:var(--text); font:15px/1.45 "Segoe UI", "Noto Sans Thai", system-ui, sans-serif; }
  header { position:sticky; top:0; z-index:2; padding:14px 16px 10px; background:linear-gradient(var(--bg) 70%, transparent); }
  h1 { margin:0; font-size:20px; letter-spacing:.2px; }
  .sub { color:var(--muted); font-size:12.5px; margin-top:2px; }
  .summary { display:flex; gap:8px; margin-top:10px; flex-wrap:wrap; }
  .pill { border:1px solid var(--line); background:var(--card); border-radius:999px; padding:4px 11px; font-size:12.5px; }
  .pill.ok { color:var(--ok); } .pill.bad { color:var(--bad); }
  main { padding:4px 16px 32px; display:grid; gap:12px; max-width:760px; margin:0 auto; }
  .card { background:var(--card); border:1px solid var(--line); border-radius:16px; padding:14px 14px 12px; }
  .card.bad { border-color:var(--bad); box-shadow:0 0 0 1px color-mix(in srgb, var(--bad) 30%, transparent); }
  .top { display:flex; align-items:center; gap:9px; }
  .dot { width:10px; height:10px; border-radius:50%; background:var(--faint); flex:none; }
  .dot.ok { background:var(--ok); } .dot.bad { background:var(--bad); }
  .name { font-weight:650; font-size:16px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  .path { color:var(--faint); font:11.5px/1.3 ui-monospace, Consolas, monospace; margin:3px 0 9px 19px; word-break:break-all; }
  .chips { display:flex; flex-wrap:wrap; gap:6px; margin-left:19px; }
  .chip { font-size:12px; border-radius:9px; padding:2px 9px; background:color-mix(in srgb, var(--text) 7%, transparent); color:var(--muted); }
  .chip.ok { color:var(--ok); } .chip.bad { color:var(--bad); } .chip.run { color:var(--accent); }
  details { margin:10px 0 0 19px; }
  summary { cursor:pointer; color:var(--muted); font-size:12.5px; }
  pre { margin:8px 0 0; padding:10px; border-radius:10px; background:color-mix(in srgb, var(--text) 6%, transparent);
        font:11.5px/1.4 ui-monospace, Consolas, monospace; white-space:pre-wrap; word-break:break-word; max-height:280px; overflow:auto; }
  pre.err { color:var(--bad); }
  .empty, .error { color:var(--muted); text-align:center; padding:40px 10px; }
  .error { color:var(--bad); }
  footer { color:var(--faint); text-align:center; font-size:11.5px; padding-bottom:24px; }
</style>
</head>
<body>
<header>
  <h1>ติดตามแอป</h1>
  <div class="sub" id="sub">กำลังโหลด…</div>
  <div class="summary" id="summary"></div>
</header>
<main id="list"></main>
<footer>อ่านอย่างเดียว · อัปเดตทุก 10 วินาที · สั่งงานได้จากหน้าติดตามใน CluadeX</footer>
<script>
"use strict";
const key = new URLSearchParams(location.search).get("k") || "";
const $ = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };
const fmt = iso => { if (!iso) return ""; const d = new Date(iso); return d.toLocaleString("th-TH-u-ca-gregory", { day:"numeric", month:"short", hour:"2-digit", minute:"2-digit" }); };
const TEST = { never:["ยังไม่ได้ทดสอบ",""], running:["กำลังทดสอบ…","run"], passed:["✓ เทสต์ผ่าน","ok"], failed:["✗ เทสต์ไม่ผ่าน","bad"] };
const RUN = { running:["● แอปทำงานอยู่","ok"], crashed:["✗ แอปล่ม","bad"] };
const WEB = { up:["● หน้าเว็บใช้ได้","ok"], down:["✗ หน้าเว็บไม่ตอบ","bad"] };

function card(a) {
  const c = $("section", "card" + (a.problem ? " bad" : ""));
  const top = $("div", "top");
  top.append($("span", "dot " + (a.problem ? "bad" : a.test.state === "passed" ? "ok" : "")), $("span", "name", a.name));
  c.append(top, $("div", "path", a.project));
  const chips = $("div", "chips");
  const t = TEST[a.test.state] || TEST.never;
  chips.append($("span", "chip " + t[1], t[0] + (a.test.summary ? " · " + a.test.summary : "")));
  if (RUN[a.run.state]) chips.append($("span", "chip " + RUN[a.run.state][1], RUN[a.run.state][0]));
  if (WEB[a.web.state]) chips.append($("span", "chip " + WEB[a.web.state][1], WEB[a.web.state][0] + (a.web.detail ? " · " + a.web.detail : "")));
  if (a.test.when) chips.append($("span", "chip", fmt(a.test.when)));
  c.append(chips);
  if (a.test.tail) { const d = $("details"); d.append($("summary", null, "ผลเทสต์ที่ไม่ผ่าน"), $("pre", null, a.test.tail)); c.append(d); }
  if (a.run.errors) { const d = $("details"); d.open = true; d.append($("summary", null, "ข้อความ error ของแอป"), $("pre", "err", a.run.errors)); c.append(d); }
  if (a.web.url) c.append($("div", "path", a.web.url));
  return c;
}

async function load() {
  try {
    const r = await fetch("/api?k=" + encodeURIComponent(key), { cache: "no-store" });
    if (!r.ok) throw new Error(r.status === 403 ? "รหัสในลิงก์ไม่ถูกต้อง — สแกน QR ใหม่จาก CluadeX" : "HTTP " + r.status);
    const data = await r.json();
    document.getElementById("sub").textContent = data.machine + " · อัปเดต " + fmt(data.at);
    const bad = data.apps.filter(a => a.problem).length;
    const s = document.getElementById("summary"); s.replaceChildren();
    s.append($("span", "pill", data.apps.length + " แอป"),
             $("span", "pill " + (bad ? "bad" : "ok"), bad ? "มีปัญหา " + bad : "ปกติทั้งหมด"));
    const list = document.getElementById("list"); list.replaceChildren();
    if (!data.apps.length) list.append($("div", "empty", "ยังไม่ได้ติดตามแอปไหน — เพิ่มได้ที่หน้าติดตามใน CluadeX"));
    data.apps.slice().sort((x, y) => (y.problem - x.problem)).forEach(a => list.append(card(a)));
  } catch (e) {
    document.getElementById("sub").textContent = "เชื่อมต่อ CluadeX ไม่ได้";
    const list = document.getElementById("list"); list.replaceChildren($("div", "error", String(e.message || e)));
  }
}
load();
setInterval(load, 10000);
</script>
</body>
</html>
""";
}
