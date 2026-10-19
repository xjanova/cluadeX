namespace CluadeX.Services.Voice;

/// <summary>
/// The page the mic window shows (served by <see cref="ChromeDictationService"/>). It runs the browser's speech
/// recognition — continuous, with live guesses — and sends what it hears to CluadeX one update at a time (a later
/// guess can never overtake an earlier final). It restarts itself when the browser ends a listening stretch (silence,
/// the ~1 minute cap), and closes its own window when CluadeX ends the session or goes away.
///
/// With <c>speak=1</c> it also reads replies aloud through the browser's own speech synthesis (free; a Thai voice
/// when there is one), and stops listening while it talks so the mic does not type the reply into the box.
/// With <c>mode=live</c> it is a GPT-Live call instead: microphone and speaker over WebRTC, the "oai-events" data
/// channel relayed to and from CluadeX. The page never sees the API key — it hands its SDP offer to CluadeX, which
/// creates the session — and it talks to nothing but its own origin and the WebRTC peer.
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
#text .who{color:var(--muted);font-size:12px;margin-right:6px}
#text .ai{color:#b9a8ff}
#mic.live{background:var(--accent);color:#fff}
#mic.muted{background:var(--panel);color:var(--rec)}
#meter{font-variant-numeric:tabular-nums}
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
    <button class="b" id="hush" hidden></button>
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
      micTip: 'เปิด/พักไมค์ (Space)', sendTip: 'ส่งข้อความในช่องพิมพ์ให้ AI (Enter)', closeTip: 'ปิดไมค์ (Esc)',
      speaking: 'กำลังอ่านคำตอบให้ฟัง…', speakingHint: 'ไมค์พักระหว่างอ่าน แล้วฟังต่อเอง', hush: 'หยุดอ่าน',
      tapToHear: 'เบราว์เซอร์ขอให้กดก่อนเล่นเสียง', tapToHearHint: 'กดปุ่ม "อ่านอีกครั้ง" หนึ่งครั้ง', replay: 'อ่านอีกครั้ง',
      liveConnecting: 'กำลังต่อ GPT-Live…', liveOn: 'คุยได้เลย — GPT-Live กำลังฟัง',
      liveHint: 'งานที่ต้องใช้โค้ด/ไฟล์ส่งให้ CluadeX ทำ — การขออนุญาตอยู่ในหน้าต่าง CluadeX',
      liveMuted: 'ปิดเสียงไมค์อยู่', liveMutedHint: 'กดปุ่มไมค์ (Space) เพื่อพูดต่อ — เวลายังนับอยู่',
      liveFailed: 'เริ่ม GPT-Live ไม่ได้', liveClosing: 'กำลังวางสาย…', liveEnded: 'จบการคุยแล้ว — ปิดหน้าต่างนี้ได้',
      liveNote: 'GPT-Live $0.05/นาที คิดตามวินาที (+ ค่าโมเดลที่ทำงาน)', liveLimit: 'จำกัด', min: 'นาที',
      liveMicTip: 'ปิด/เปิดเสียงไมค์ (Space)', liveCloseTip: 'วางสาย (Esc)', you: 'คุณ', ai: 'AI', hangUp: 'วางสาย'
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
      micTip: 'Mic on / pause (Space)', sendTip: 'Send the input box to the AI (Enter)', closeTip: 'Close the mic (Esc)',
      speaking: 'Reading the reply aloud…', speakingHint: 'The mic pauses while it reads, then listens again', hush: 'Stop reading',
      tapToHear: 'The browser wants a click before it plays sound', tapToHearHint: 'Press "Read again" once', replay: 'Read again',
      liveConnecting: 'Connecting to GPT-Live…', liveOn: 'Talk — GPT-Live is listening',
      liveHint: 'Work on code and files goes to CluadeX — approvals appear in the CluadeX window',
      liveMuted: 'Mic muted', liveMutedHint: 'Press the mic (Space) to talk again — the clock is still running',
      liveFailed: 'GPT-Live could not start', liveClosing: 'Hanging up…', liveEnded: 'Call ended — you can close this window',
      liveNote: 'GPT-Live $0.05/min, billed per second (+ the model doing the work)', liveLimit: 'limit', min: 'min',
      liveMicTip: 'Mute / unmute (Space)', liveCloseTip: 'Hang up (Esc)', you: 'You', ai: 'AI', hangUp: 'Hang up'
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

  const SPEAK = q.get('speak') === '1', LIVE = q.get('mode') === 'live';
  const CAP = Math.max(0, +(q.get('cap') || 0));
  const SR = window.SpeechRecognition || window.webkitSpeechRecognition;
  // busy: from start() until onend — one recognizer at a time. want: the user wants to be heard.
  // talking: a reply is being read aloud — recognition waits, or it would type the reply into the box.
  let rec = null, busy = false, want = true, closing = false, lastErr = null, talking = false;
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
    if (closing || !want || busy || talking || LIVE) return;
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
    if (LIVE) return endLive(false);
    if (closing) return;
    closing = true; want = false;
    try { rec && rec.abort(); } catch (e) {}
    try { window.speechSynthesis && speechSynthesis.cancel(); } catch (e) {}
    micBtn.classList.remove('on');
    show(M.closed, '');
    window.close();
  }

  // ── Reading replies aloud: the browser's own speech synthesis, free ──
  const hushBtn = $('hush');
  let talkGen = 0, lastSay = null, lastHush = null, pendingSay = null;
  function pickVoice(l) {
    const vs = (window.speechSynthesis && speechSynthesis.getVoices()) || [];
    const base = (l || '').slice(0, 2).toLowerCase();
    const same = vs.filter(v => (v.lang || '').toLowerCase().replace('_', '-').startsWith(base));
    return same.find(v => /natural|online|google/i.test(v.name)) || same[0] || null;
  }
  // Chrome stops a long utterance after ~15 s: read it sentence by sentence.
  function pieces(text) {
    const out = [];
    for (const s of text.split(/(?<=[.!?。])\s+|\n+/)) {
      let rest = s.trim();
      while (rest.length > 180) { const cut = rest.lastIndexOf(' ', 180); const at = cut > 60 ? cut : 180; out.push(rest.slice(0, at)); rest = rest.slice(at).trim(); }
      if (rest) out.push(rest);
    }
    return out;
  }
  function sayAloud(text, l) {
    if (!text) return;
    if (!window.speechSynthesis) { post('/event', { type: 'error', detail: 'no-tts' }); return; }
    const gen = ++talkGen;
    try { speechSynthesis.cancel(); } catch (e) {}
    talking = true; pendingSay = { text, l };
    if (busy) { try { rec.abort(); } catch (e) {} }   // onend does not restart while talking
    show(M.speaking, M.speakingHint); hushBtn.textContent = M.hush; hushBtn.hidden = false;
    post('/event', { type: 'speaking', detail: l });
    const parts = pieces(text), voice = pickVoice(l);
    parts.forEach((p, i) => {
      const u = new SpeechSynthesisUtterance(p);
      u.lang = l; if (voice) u.voice = voice;
      u.onend = () => { if (gen === talkGen && i === parts.length - 1) doneTalking(gen); };
      u.onerror = e => {
        if (gen !== talkGen) return;
        if (e.error === 'not-allowed') { show(M.tapToHear, M.tapToHearHint, true); hushBtn.textContent = M.replay; talking = false; resumeRec(); return; }
        if (e.error !== 'interrupted' && e.error !== 'canceled') doneTalking(gen);
      };
      speechSynthesis.speak(u);
    });
  }
  function doneTalking(gen) {
    if (gen !== talkGen || !talking) return;
    talking = false; pendingSay = null; hushBtn.hidden = true;
    post('/event', { type: 'spoke', detail: '' });
    resumeRec();
  }
  function resumeRec() {
    if (closing) return;
    if (want && !busy) startRec();
    else if (!want) show(M.paused, M.hintOff);
  }
  function hushNow() {
    talkGen++;
    try { speechSynthesis.cancel(); } catch (e) {}
    if (talking) { talking = false; pendingSay = null; post('/event', { type: 'spoke', detail: '' }); }
    hushBtn.hidden = true; resumeRec();
  }
  hushBtn.onclick = () => {
    if (hushBtn.textContent === M.replay && pendingSay) sayAloud(pendingSay.text, pendingSay.l);
    else hushNow();
  };

  // ── Live voice: GPT-Live over WebRTC; the key stays in CluadeX ──
  let pc = null, dc = null, stream = null, liveUp = false, closedSeen = false, muted = false, liveStart = 0, outSent = 0;
  const outQ = [];
  const FORWARD = new Set(['session.started', 'session.input_transcript.delta', 'session.output_transcript.delta',
    'session.delegation.created', 'session.usage.updated', 'session.closed', 'session.commentary.appended',
    'session.thinking.appended', 'session.instructions.appended', 'session.input_audio.muted', 'session.input_audio.unmuted', 'error']);
  const lines = [];
  function liveLine(who, delta) {
    const last = lines[lines.length - 1];
    if (last && last.who === who) last.text += delta; else lines.push({ who, text: delta });
    while (lines.length > 12) lines.shift();
    textEl.textContent = '';
    for (const l of lines) {
      const d = document.createElement('div'); if (l.who === 'ai') d.className = 'ai';
      const w = document.createElement('span'); w.className = 'who'; w.textContent = l.who === 'ai' ? M.ai : M.you;
      d.appendChild(w); d.appendChild(document.createTextNode(l.text)); textEl.appendChild(d);
    }
    textEl.scrollTop = textEl.scrollHeight;
  }
  // Events go to CluadeX one at a time, in order (a delegation must follow the words that led to it).
  let evBusy = false; const evQ = [];
  function forward(ev) {
    const slim = { type: ev.type };
    for (const k of ['event_id', 'delta', 'start_ms', 'end_ms', 'delegation', 'usage', 'reason', 'error', 'client_event_id', 'offset_ms'])
      if (ev[k] !== undefined) slim[k] = ev[k];
    evQ.push(slim); pumpEv();
  }
  async function pumpEv() {
    if (evBusy) return; evBusy = true;
    while (evQ.length) await post('/live/event', { ev: evQ.shift() });
    evBusy = false;
  }
  function dcSend(obj) { try { if (dc && dc.readyState === 'open') { dc.send(typeof obj === 'string' ? obj : JSON.stringify(obj)); return true; } } catch (e) {} return false; }
  function flushOut() { while (outQ.length && dc && dc.readyState === 'open') { const m = outQ[0]; if (!dcSend(m.m)) break; outQ.shift(); outSent = m.n; } }
  function onLive(ev) {
    if (!ev || typeof ev.type !== 'string') return;
    if (FORWARD.has(ev.type)) forward(ev);
    switch (ev.type) {
      case 'session.started': liveUp = true; micBtn.classList.add('live'); show(M.liveOn, M.liveHint); break;
      case 'session.input_transcript.delta': liveLine('you', ev.delta || ''); break;
      case 'session.output_transcript.delta': liveLine('ai', ev.delta || ''); break;
      case 'session.input_audio.muted': setMuted(true); break;
      case 'session.input_audio.unmuted': setMuted(false); break;
      case 'session.closed': closedSeen = true; break;
      case 'error': if (ev.error && ev.error.message) hintEl.textContent = String(ev.error.message).slice(0, 200); break;
    }
  }
  function setMuted(m) {
    muted = m; micBtn.classList.toggle('muted', m); micBtn.classList.toggle('live', !m);
    if (stream) stream.getAudioTracks().forEach(t => t.enabled = !m);
    if (!closing) m ? show(M.liveMuted, M.liveMutedHint) : show(M.liveOn, M.liveHint);
  }
  function toggleMute() {
    if (!liveUp || closing) return;
    const m = !muted; setMuted(m);
    dcSend({ type: m ? 'session.input_audio.mute' : 'session.input_audio.unmute', event_id: 'pg_' + Date.now() });
  }
  function meter() {
    if (!liveStart || closing) return;
    const sec = Math.floor((Date.now() - liveStart) / 1000), billed = Math.max(15, sec);
    const usd = (billed / 60 * 0.05).toFixed(3);
    $('note').innerHTML = '';
    const m = document.createElement('span'); m.id = 'meter';
    m.textContent = Math.floor(sec / 60) + ':' + String(sec % 60).padStart(2, '0') + ' · $' + usd + (CAP ? ' · ' + M.liveLimit + ' ' + CAP + ' ' + M.min : '');
    $('note').appendChild(m);
  }
  async function iceDone(timeoutMs) {
    if (pc.iceGatheringState === 'complete') return;
    await new Promise(res => {
      const t = setTimeout(res, timeoutMs);
      pc.addEventListener('icegatheringstatechange', () => { if (pc.iceGatheringState === 'complete') { clearTimeout(t); res(); } });
    });
  }
  function liveFail(why, detail) {
    show(M.liveFailed, detail || '', true);
    post('/event', { type: 'error', detail: why });
    cleanupLive();
  }
  function cleanupLive() {
    try { stream && stream.getTracks().forEach(t => t.stop()); } catch (e) {}
    try { pc && pc.close(); } catch (e) {}
    micBtn.classList.remove('live', 'muted');
  }
  async function runLive() {
    micBtn.title = M.liveMicTip; $('close').textContent = M.hangUp; $('close').title = M.liveCloseTip;
    $('send').hidden = true; langSel.hidden = true; $('note').textContent = M.liveNote; textEl.dataset.empty = '';
    show(M.liveConnecting, '');
    try { stream = await navigator.mediaDevices.getUserMedia({ audio: true }); }
    catch (e) {
      const nf = e && (e.name === 'NotFoundError' || e.name === 'OverconstrainedError');
      return liveFail(nf ? 'audio-capture' : 'not-allowed', nf ? M.noMicHint : M.deniedHint);
    }
    if (closing) return cleanupLive();
    try { await connectLive(); }
    catch (e) { if (!closing) liveFail('live-setup', String((e && e.message) || e).slice(0, 200)); }
  }
  async function connectLive() {
    pc = new RTCPeerConnection();
    const audio = document.createElement('audio'); audio.autoplay = true; document.body.appendChild(audio);
    pc.ontrack = e => { audio.srcObject = e.streams[0]; };
    stream.getTracks().forEach(t => pc.addTrack(t, stream));
    dc = pc.createDataChannel('oai-events');
    dc.onmessage = e => { let ev = null; try { ev = JSON.parse(e.data); } catch (x) {} onLive(ev); };
    dc.onopen = flushOut;
    pc.onconnectionstatechange = () => { if (pc.connectionState === 'failed' && !closing) endLive(true); };
    await pc.setLocalDescription(await pc.createOffer());
    await iceDone(3000);
    let r = null, j = {};
    try {
      r = await fetch('/live/offer', { method: 'POST', headers: { 'Content-Type': 'application/json' },
                                       body: JSON.stringify({ t: T, s: S, sdp: pc.localDescription.sdp }) });
      try { j = await r.json(); } catch (e) {}
    } catch (e) {}
    if (!r || !r.ok || !j.sdp) return liveFail('live', (j && j.error) ? String(j.error).slice(0, 300) : (r ? 'HTTP ' + r.status : ''));
    liveStart = Date.now();   // created: billing has started
    try { await pc.setRemoteDescription({ type: 'answer', sdp: j.sdp }); }
    catch (e) { return liveFail('live-answer', String((e && e.message) || e).slice(0, 200)); }
    meter(); setInterval(meter, 1000);
  }
  // Hang up: session.close, then wait for session.closed (it carries the final usage) before dropping the call.
  async function endLive(sayBye) {
    if (closing) return;
    closing = true;
    show(M.liveClosing, '');
    if (sayBye) post('/bye', {});
    if (dc && dc.readyState === 'open' && !closedSeen) {
      dcSend({ type: 'session.close', event_id: 'pg_close' });
      const until = Date.now() + 15000;
      while (!closedSeen && Date.now() < until && dc.readyState === 'open') await new Promise(res => setTimeout(res, 200));
      while (evQ.length || evBusy) await new Promise(res => setTimeout(res, 50));
    }
    cleanupLive();
    show(M.liveEnded, '');
    if (!sayBye) post('/bye', {});
    window.close();
  }

  micBtn.onclick = () => LIVE ? toggleMute() : toggle();
  $('send').onclick = () => post('/send', {});
  $('close').onclick = () => { if (LIVE) endLive(false); else { post('/bye', {}); shut(); } };
  langSel.onchange = () => {
    lang = langSel.value;
    try { localStorage.setItem('lang', lang); } catch (e) {}
    if (busy) { try { rec.abort(); } catch (e) {} }   // onend starts again in the new language
    else startRec();
  };
  document.addEventListener('keydown', e => {
    const onControl = e.target && (e.target.tagName === 'SELECT' || e.target.tagName === 'BUTTON');
    if (e.key === 'Escape') { e.preventDefault(); if (LIVE) endLive(false); else { post('/bye', {}); shut(); } }
    else if (e.key === 'Enter' && !onControl && !LIVE) { e.preventDefault(); post('/send', {}); }
    else if (e.code === 'Space' && !onControl) { e.preventDefault(); LIVE ? toggleMute() : toggle(); }
  });
  window.addEventListener('pagehide', () => {
    if (LIVE && !closedSeen) dcSend({ type: 'session.close', event_id: 'pg_hide' });
    if (closing && !LIVE) return;
    try { navigator.sendBeacon('/bye', JSON.stringify({ t: T, s: S })); } catch (e) {}
  });

  // CluadeX's side: a long poll. It answers at once when the session ends, the box was sent, a reply is to be read
  // aloud or a live message is waiting for the data channel — else after ~15 s.
  async function watch() {
    let since = -1, fails = 0, sentSeen = null;
    while (!closing) {
      try {
        const r = await fetch('/state?t=' + encodeURIComponent(T) + '&s=' + S + '&since=' + since + '&out=' + outSent, { cache: 'no-store' });
        if (r.status === 403) return shut();
        const st = await r.json();
        fails = 0;
        if (!st.active) return shut();
        if (sentSeen !== null && st.sent !== sentSeen) { finals = ''; interim = ''; if (!LIVE) render(); }
        sentSeen = st.sent; since = st.v;
        if (st.say) {
          if (lastHush !== null && st.hush !== lastHush && talking) hushNow();
          lastHush = st.hush;
          // CluadeX empties the text when a session starts or is hushed, so text here is a reply of this window's.
          if (st.say.id !== lastSay) { lastSay = st.say.id; if (st.say.text && SPEAK && !LIVE) sayAloud(st.say.text, st.say.lang || lang); }
        }
        if (st.out) for (const m of st.out) if (m.n > outSent && !outQ.some(x => x.n === m.n)) outQ.push(m);
        flushOut();
      } catch (e) {
        if (++fails >= 3) return shut();
        await new Promise(res => setTimeout(res, 1000));
      }
    }
  }

  if (window.speechSynthesis) try { speechSynthesis.getVoices(); } catch (e) {}   // starts loading the voice list
  if (LIVE) { textEl.dataset.empty = ''; watch(); runLive(); }
  else { render(); watch(); startRec(); }
})();
</script>
</body>
</html>
""";
}
