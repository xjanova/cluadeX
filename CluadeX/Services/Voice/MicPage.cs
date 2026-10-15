namespace CluadeX.Services.Voice;

/// <summary>
/// The page the mic window shows (served by <see cref="ChromeDictationService"/>). It runs the browser's speech
/// recognition — continuous, with live guesses — and sends what it hears to CluadeX one update at a time (a later
/// guess can never overtake an earlier final). It restarts itself when the browser ends a listening stretch (silence,
/// the ~1 minute cap), and closes its own window when CluadeX ends the session or goes away.
/// </summary>
internal static class MicPage
{
    public const string Html = """
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<title>CluadeX Mic</title>
<style>
:root{color-scheme:dark;--bg:#0e1016;--panel:#161a24;--line:#262c3b;--text:#e7e9f0;--muted:#8a92a6;--accent:#7c5cff;--rec:#ff4d5e}
*{box-sizing:border-box}
html,body{margin:0;height:100%;background:var(--bg);color:var(--text);font:14px "Segoe UI","Leelawadee UI",Tahoma,sans-serif;overflow:hidden}
.wrap{display:flex;flex-direction:column;height:100%;padding:14px 16px 12px;gap:10px}
.top{display:flex;align-items:center;gap:14px}
#mic{width:60px;height:60px;border-radius:50%;border:0;background:var(--panel);color:var(--text);cursor:pointer;flex:none;
     box-shadow:0 0 0 1px var(--line);display:grid;place-items:center;transition:background .15s}
#mic svg{width:26px;height:26px}
#mic.on{background:var(--rec);color:#fff;animation:pulse 1.4s infinite}
@keyframes pulse{0%{box-shadow:0 0 0 0 rgba(255,77,94,.55)}70%{box-shadow:0 0 0 14px rgba(255,77,94,0)}100%{box-shadow:0 0 0 0 rgba(255,77,94,0)}}
.status{flex:1;min-width:0}
#state{font-weight:600;line-height:1.35}
.err #state{color:var(--rec)}
#hint{color:var(--muted);font-size:12px;margin-top:3px}
select{background:var(--panel);color:var(--text);border:1px solid var(--line);border-radius:6px;padding:4px 6px;font:inherit;font-size:12px}
#text{flex:1;overflow:auto;background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:10px 12px;
      line-height:1.55;white-space:pre-wrap;word-break:break-word;font-size:15px}
#text .i{color:var(--muted)}
#text:empty::before{content:attr(data-empty);color:var(--muted);font-size:13px}
.row{display:flex;gap:8px;align-items:center}
.note{color:var(--muted);font-size:11px;flex:1;min-width:0}
button.b{border:1px solid var(--line);background:var(--panel);color:var(--text);border-radius:8px;padding:7px 16px;cursor:pointer;font:inherit}
button.p{background:var(--accent);border-color:var(--accent);color:#fff}
button.b:hover{filter:brightness(1.15)}
</style>
</head>
<body>
<div class="wrap" id="wrap">
  <div class="top">
    <button id="mic" aria-label="mic">
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
        <rect x="9" y="2" width="6" height="12" rx="3"/><path d="M5 10a7 7 0 0 0 14 0"/><path d="M12 17v4"/><path d="M8 21h8"/>
      </svg>
    </button>
    <div class="status"><div id="state"></div><div id="hint"></div></div>
    <select id="lang"><option value="th-TH">ไทย</option><option value="en-US">English</option></select>
  </div>
  <div id="text"></div>
  <div class="row">
    <div class="note" id="note"></div>
    <button class="b" id="close"></button>
    <button class="b p" id="send"></button>
  </div>
</div>
<script>
(() => {
  const q = new URLSearchParams(location.search);
  const T = q.get('t') || '', S = +(q.get('s') || 0), UI = q.get('ui') === 'th' ? 'th' : 'en';
  const ENG = q.get('eng') === 'microsoft' ? 'Microsoft' : 'Google';
  const M = {
    th: {
      starting: 'กำลังเปิดไมค์…', listen: 'กำลังฟัง… พูดได้เลย', paused: 'พักไมค์อยู่',
      hintOn: 'คำพูดขึ้นในช่องพิมพ์ของ CluadeX — ยังไม่ส่งจนกว่าจะกดส่ง', hintOff: 'กดปุ่มไมค์ (หรือ Space) เพื่อฟังต่อ',
      empty: 'คำพูดของคุณจะขึ้นที่นี่', send: 'ส่ง', close: 'ปิด',
      note: 'เสียงถูกส่งไปที่ ' + ENG + ' เพื่อแปลงเป็นข้อความ',
      denied: 'ยังไม่ได้อนุญาตไมค์', deniedHint: 'กด Allow ที่ด้านบนหน้าต่างนี้ แล้วกดปุ่มไมค์อีกครั้ง',
      noMic: 'ไม่พบไมโครโฟน', noMicHint: 'เสียบไมค์ หรือเปิดสิทธิ์ไมค์ใน Windows › Privacy › Microphone',
      network: 'ต่อบริการแปลงเสียงไม่ได้', networkHint: 'ต้องใช้อินเทอร์เน็ต — กำลังลองใหม่…',
      unsupported: 'เบราว์เซอร์นี้ไม่มีระบบแปลงเสียง', unsupportedHint: 'ติดตั้ง Google Chrome แล้วลองใหม่',
      langBad: 'ภาษานี้ใช้ไม่ได้ในเบราว์เซอร์นี้', closed: 'ปิดไมค์แล้ว — ปิดหน้าต่างนี้ได้เลย',
      micTip: 'เปิด/พักไมค์ (Space)', sendTip: 'ส่งข้อความในช่องพิมพ์ให้ AI (Enter)', closeTip: 'ปิดไมค์ (Esc)'
    },
    en: {
      starting: 'Opening the mic…', listen: 'Listening… go ahead', paused: 'Mic paused',
      hintOn: 'Your words go into the CluadeX input box — nothing is sent until you press Send', hintOff: 'Press the mic (or Space) to listen again',
      empty: 'What you say appears here', send: 'Send', close: 'Close',
      note: 'Audio is sent to ' + ENG + ' to turn it into text',
      denied: 'The microphone is not allowed', deniedHint: 'Press Allow at the top of this window, then the mic button',
      noMic: 'No microphone found', noMicHint: 'Plug one in, or allow it in Windows › Privacy › Microphone',
      network: 'Cannot reach the speech service', networkHint: 'It needs the internet — retrying…',
      unsupported: 'This browser has no speech recognition', unsupportedHint: 'Install Google Chrome and try again',
      langBad: 'This browser cannot recognise that language', closed: 'Mic off — you can close this window',
      micTip: 'Mic on / pause (Space)', sendTip: 'Send the input box to the AI (Enter)', closeTip: 'Close the mic (Esc)'
    }
  }[UI];

  const $ = id => document.getElementById(id);
  const wrap = $('wrap'), micBtn = $('mic'), stateEl = $('state'), hintEl = $('hint'), textEl = $('text'), langSel = $('lang');
  $('send').textContent = M.send; $('send').title = M.sendTip;
  $('close').textContent = M.close; $('close').title = M.closeTip;
  micBtn.title = M.micTip; $('note').textContent = M.note; textEl.dataset.empty = M.empty;

  let lang = q.get('lang') || 'th-TH';
  try { lang = localStorage.getItem('lang') || lang; } catch (e) {}
  if (![...langSel.options].some(o => o.value === lang)) lang = 'th-TH';
  langSel.value = lang;

  const SR = window.SpeechRecognition || window.webkitSpeechRecognition;
  // busy: from start() until onend — one recognizer at a time. want: the user wants to be heard.
  let rec = null, busy = false, want = true, closing = false, lastErr = null;
  let finals = '', interim = '';

  function show(state, hint, isErr) {
    stateEl.textContent = state; hintEl.textContent = hint || '';
    wrap.classList.toggle('err', !!isErr);
  }
  function render() {
    textEl.textContent = finals;
    if (interim) {
      const s = document.createElement('span');
      s.className = 'i';
      s.textContent = (finals && !/\s$/.test(finals) ? ' ' : '') + interim;
      textEl.appendChild(s);
    }
    textEl.scrollTop = textEl.scrollHeight;
  }

  function post(path, body) {
    return fetch(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, keepalive: true,
                         body: JSON.stringify(Object.assign({ t: T, s: S }, body)) })
      .then(r => { if (r.status === 403 || r.status === 410) shut(); return r; })
      .catch(() => {});
  }

  // One /text update in flight at a time; updates made meanwhile are merged, so finals are never lost or reordered.
  let inflight = false, pend = null;
  function pushText(fin, intr) {
    if (pend) { if (fin) pend.final = pend.final ? pend.final + ' ' + fin : fin; pend.interim = intr; }
    else pend = { final: fin, interim: intr };
    pump();
  }
  async function pump() {
    if (inflight || !pend) return;
    const body = pend; pend = null; inflight = true;
    await post('/text', body);
    inflight = false; pump();
  }

  function startRec() {
    if (closing || !want || busy) return;
    if (!SR) { want = false; show(M.unsupported, M.unsupportedHint, true); post('/event', { type: 'error', detail: 'unsupported' }); return; }
    rec = new SR();
    rec.lang = lang; rec.continuous = true; rec.interimResults = true; rec.maxAlternatives = 1;
    rec.onstart = () => {
      lastErr = null; micBtn.classList.add('on'); show(M.listen, M.hintOn);
      post('/event', { type: 'listening', detail: lang });
    };
    rec.onresult = e => {
      let fin = '', intr = '';
      for (let i = e.resultIndex; i < e.results.length; i++) {
        const r = e.results[i];
        if (r.isFinal) fin += r[0].transcript; else intr += r[0].transcript;
      }
      fin = fin.trim(); intr = intr.trim();
      if (fin) finals += (finals && !/\s$/.test(finals) ? ' ' : '') + fin;
      interim = intr;
      render();
      pushText(fin, intr);
    };
    rec.onerror = e => {
      lastErr = e.error;
      if (e.error === 'no-speech' || e.error === 'aborted') return;
      if (e.error === 'not-allowed' || e.error === 'service-not-allowed') { want = false; show(M.denied, M.deniedHint, true); }
      else if (e.error === 'audio-capture') { want = false; show(M.noMic, M.noMicHint, true); }
      else if (e.error === 'network') show(M.network, M.networkHint, true);
      else if (e.error === 'language-not-supported') { want = false; show(M.langBad, '', true); }
      post('/event', { type: 'error', detail: e.error });
    };
    rec.onend = () => {
      busy = false;
      if (interim) { interim = ''; render(); pushText('', ''); }
      if (want && !closing) { setTimeout(startRec, lastErr === 'network' ? 2000 : 120); return; }
      micBtn.classList.remove('on');
      if (!wrap.classList.contains('err')) show(M.paused, M.hintOff);
    };
    try { busy = true; rec.start(); show(M.starting, ''); }
    catch (err) { busy = false; setTimeout(startRec, 500); }
  }

  function toggle() {
    if (closing) return;
    if (want) {
      want = false;
      if (busy) { try { rec.stop(); } catch (e) {} } else { micBtn.classList.remove('on'); show(M.paused, M.hintOff); }
      post('/event', { type: 'paused', detail: lang });
    }
    else { want = true; wrap.classList.remove('err'); startRec(); }   // still stopping: onend starts it again
  }

  function shut() {
    if (closing) return;
    closing = true; want = false;
    try { rec && rec.abort(); } catch (e) {}
    micBtn.classList.remove('on');
    show(M.closed, '');
    window.close();
  }

  micBtn.onclick = toggle;
  $('send').onclick = () => post('/send', {});
  $('close').onclick = () => { post('/bye', {}); shut(); };
  langSel.onchange = () => {
    lang = langSel.value;
    try { localStorage.setItem('lang', lang); } catch (e) {}
    if (busy) { try { rec.abort(); } catch (e) {} }   // onend starts again in the new language
    else startRec();
  };
  document.addEventListener('keydown', e => {
    const onControl = e.target && (e.target.tagName === 'SELECT' || e.target.tagName === 'BUTTON');
    if (e.key === 'Escape') { e.preventDefault(); post('/bye', {}); shut(); }
    else if (e.key === 'Enter' && !onControl) { e.preventDefault(); post('/send', {}); }
    else if (e.code === 'Space' && !onControl) { e.preventDefault(); toggle(); }
  });
  window.addEventListener('pagehide', () => {
    if (closing) return;
    try { navigator.sendBeacon('/bye', JSON.stringify({ t: T, s: S })); } catch (e) {}
  });

  // CluadeX's side: a long poll. It answers at once when the session ends or the box was sent, else after ~15 s.
  async function watch() {
    let since = -1, fails = 0, sentSeen = null;
    while (!closing) {
      try {
        const r = await fetch('/state?t=' + encodeURIComponent(T) + '&s=' + S + '&since=' + since, { cache: 'no-store' });
        if (r.status === 403) return shut();
        const st = await r.json();
        fails = 0;
        if (!st.active) return shut();
        if (sentSeen !== null && st.sent !== sentSeen) { finals = ''; interim = ''; render(); }
        sentSeen = st.sent; since = st.v;
      } catch (e) {
        if (++fails >= 3) return shut();
        await new Promise(res => setTimeout(res, 1000));
      }
    }
  }

  render();
  watch();
  startRec();
})();
</script>
</body>
</html>
""";
}
