// Does Microsoft Edge take the CluadeX Bridge extension as it is? Real Edge on a NEW temp profile (deleted at the end),
// the extension folder given on the command line, CDP over a loopback port. It proves the half of the Edge connection
// that is Edge's: the folder loads, the id is the fixed one (so the native host's allowed_origins fits), every API
// background.js uses is there, and a window of its own in a labelled tab group takes the debugger and a screenshot.
// It does not touch the registry, the named pipe or the user's own Edge / Chrome, so it is safe beside a running
// CluadeX. The other half (the native host under HKCU\Software\Microsoft\Edge\NativeMessagingHosts and the relay)
// is the same code as Chrome's: chrometest S1b2 checks both keys, and its C / T / P cases the relay.
// Usage: node edge-probe.mjs "<msedge.exe>" "<repo>\CluadeX\Resources\ChromeExtension"      (exit code 1 = a check failed)
import { spawn } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const [edge, extension] = process.argv.slice(2);
const FIXED = 'dcgjpajonelpldhmhealmiipjgkliglm';
const port = 9300 + Math.floor(Math.random() * 500);
const profile = mkdtempSync(join(tmpdir(), 'cx-edgeprobe-'));
const proc = spawn(edge, [
  `--user-data-dir=${profile}`, '--no-first-run', '--no-default-browser-check',
  `--remote-debugging-port=${port}`, '--remote-allow-origins=*',
  `--load-extension=${extension}`, '--disable-features=DisableLoadExtensionCommandLineSwitch',
  'about:blank',
], { stdio: 'ignore' });
const sleep = ms => new Promise(r => setTimeout(r, ms));
let failed = 0;
const say = (ok, label, v) => console.log((ok ? 'OK   ' : (failed++, 'FAIL ')) + label + (v === undefined ? '' : ' → ' + (typeof v === 'string' ? v : JSON.stringify(v))));

let ws, nextId = 1;
const waiting = new Map();
const send = (method, params = {}, sessionId, ms = 20000) => new Promise((resolve, reject) => {
  const id = nextId++;
  const t = setTimeout(() => { waiting.delete(id); reject(new Error(method + ': no answer in ' + ms + ' ms')); }, ms);
  waiting.set(id, { resolve: v => { clearTimeout(t); resolve(v); }, reject: e => { clearTimeout(t); reject(e); } });
  ws.send(JSON.stringify({ id, method, params, sessionId }));
});

try {
  let version = null;
  for (let i = 0; i < 40 && !version; i++) {
    try { version = await (await fetch(`http://127.0.0.1:${port}/json/version`)).json(); } catch { await sleep(500); }
  }
  if (!version) throw new Error('Edge did not open its debugging port');
  say(true, 'Edge started on a temp profile', version.Browser + ' · ' + version['User-Agent']);
  ws = new WebSocket(version.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = () => reject(new Error('websocket refused')); });
  ws.onmessage = e => {
    const m = JSON.parse(e.data);
    const w = m.id && waiting.get(m.id);
    if (!w) return;
    waiting.delete(m.id);
    if (m.error) w.reject(new Error(JSON.stringify(m.error))); else w.resolve(m.result);
  };

  let worker = null, all = [];
  for (let i = 0; i < 20 && !worker; i++) {
    all = (await send('Target.getTargets')).targetInfos;
    worker = all.find(t => t.type === 'service_worker' && t.url.includes(FIXED));
    if (!worker) await sleep(500);
  }
  console.log('     extension targets: ' + all.filter(t => t.url.startsWith('chrome-extension://')).map(t => t.type + ' ' + t.url).join(' | '));
  if (!worker) throw new Error('no extension service worker — Edge did not load the folder (targets: ' + all.map(t => t.type + ' ' + t.url).join(' | ').slice(0, 400) + ')');
  const id = worker.url.split('/')[2];
  say(id === FIXED, 'Edge loaded the extension folder; its id is the fixed one (the same as in Chrome)', id + ' · ' + worker.url);

  const evalIn = async expression => {
    const { sessionId } = await send('Target.attachToTarget', { targetId: worker.targetId, flatten: true });
    try {
      const r = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true }, sessionId, 40000);
      if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? r.exceptionDetails.text);
      return r.result.value ?? null;
    } finally { await send('Target.detachFromTarget', { sessionId }).catch(() => {}); }
  };
  const step = async (label, expression, good = () => true) => {
    try { const v = await evalIn(expression); say(good(v), label, v); return v; }
    catch (e) { say(false, label, e.message); return null; }
  };
  // A service worker that has only just started has no `chrome` yet: wait for it before the first question.
  for (let i = 0; i < 20; i++) {
    if (await evalIn('typeof chrome !== "undefined" && !!chrome.runtime').catch(() => false)) break;
    await sleep(300);
  }
  await step('manifest version and name', 'chrome.runtime.getManifest().version + " · " + chrome.runtime.getManifest().name');
  await step('it is Edge (the user agent carries "Edg/")', 'navigator.userAgent', v => / Edg\//.test(v));
  await step('every API the extension uses is there',
    `['debugger.attach','debugger.sendCommand','debugger.onDetach','tabGroups.update','tabs.group','declarativeNetRequest.updateSessionRules','windows.create','alarms.create','storage.session','storage.local','action.setBadgeText','runtime.connectNative'].filter(p => { const v = p.split('.').reduce((o, k) => o && o[k], chrome); return v === undefined || v === null; }).join(' ') || 'none missing'`,
    v => v === 'none missing');
  await step('a window of its own, in a labelled tab group, the debugger attached, CDP answered, a screenshot taken',
    `chrome.windows.create({url: 'about:blank', focused: false}).then(async w => {
       const tabId = w.tabs[0].id;
       const g = await chrome.tabs.group({tabIds: [tabId], createProperties: {windowId: w.id}});
       const group = await chrome.tabGroups.update(g, {title: 'CluadeX agent', color: 'purple'});
       await chrome.debugger.attach({tabId}, '1.3');
       const frame = await chrome.debugger.sendCommand({tabId}, 'Page.getFrameTree');
       const shot = await chrome.debugger.sendCommand({tabId}, 'Page.captureScreenshot', {format: 'png'}).then(s => s.data.length, e => 'refused: ' + e.message);
       await chrome.debugger.detach({tabId});
       await chrome.windows.remove(w.id);
       return {group: group.title + '/' + group.color, frameTree: !!frame.frameTree, screenshotChars: shot};
     })`, v => v && v.frameTree && typeof v.screenshotChars === 'number');
  await step('the session rule that lifts a page\'s CSP for the agent is accepted',
    `chrome.declarativeNetRequest.getSessionRules().then(r => r.length + ' rule(s)')`);
  await step('with no host registered yet, connectNative fails quietly',
    `new Promise(done => { const p = chrome.runtime.connectNative('com.xmanstudio.cluadex_probe_none'); p.onDisconnect.addListener(() => done(chrome.runtime.lastError?.message || 'disconnected')); })`);
} catch (e) {
  say(false, 'stopped', e.message);
} finally {
  try { if (ws && ws.readyState === 1) await send('Browser.close', {}, undefined, 4000).catch(() => {}); } catch {}
  await sleep(3000);
  try { proc.kill(); } catch {}
  await sleep(1500);
  try { rmSync(profile, { recursive: true, force: true, maxRetries: 10, retryDelay: 300 }); console.log('temp profile removed'); }
  catch (e) { console.log('PROFILE LEFT ' + profile + ' ' + e.message); }
  process.exit(failed ? 1 : 0);
}
