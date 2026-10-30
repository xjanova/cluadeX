// Real Chrome on a throwaway profile with the CluadeX Bridge loaded over the debugging pipe, for chrometest.
// Never the user's profile: --user-data-dir is a new temp folder, which is the only thing this script ever deletes.
// Usage: node launch-chrome.mjs <chrome.exe> <extension folder> <host-resolver-rules>
// Reads {id, expression} lines on stdin and evaluates them in the extension's service worker. Stdin closing ends it.
import { spawn } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { createInterface } from 'node:readline';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const [chrome, extension, hostRules] = process.argv.slice(2);
const profile = mkdtempSync(join(tmpdir(), 'cx-chrometest-'));
const say = message => process.stdout.write(JSON.stringify(message) + '\n');

const proc = spawn(chrome, [
  `--user-data-dir=${profile}`, '--no-first-run', '--no-default-browser-check',
  '--remote-debugging-pipe', '--enable-unsafe-extension-debugging',
  `--host-resolver-rules=${hostRules}`, 'about:blank',
], { stdio: ['ignore', 'ignore', 'ignore', 'pipe', 'pipe'] });

const toChrome = proc.stdio[3], fromChrome = proc.stdio[4];
let buffer = Buffer.alloc(0), nextId = 1, quitting = false;
const waiting = new Map();

fromChrome.on('data', chunk => {
  buffer = Buffer.concat([buffer, chunk]);
  let end;
  while ((end = buffer.indexOf(0)) >= 0) {
    const m = JSON.parse(buffer.subarray(0, end).toString('utf8'));
    buffer = buffer.subarray(end + 1);
    const w = m.id && waiting.get(m.id);
    if (!w) continue;
    waiting.delete(m.id);
    if (m.error) w.reject(new Error(JSON.stringify(m.error))); else w.resolve(m.result);
  }
});
// The pipe breaking while Chrome exits is expected: the 'exit' handler below ends this script.
fromChrome.on('error', () => {});
toChrome.on('error', () => {});

const send = (method, params = {}, sessionId) => new Promise((resolve, reject) => {
  const id = nextId++;
  waiting.set(id, { resolve, reject });
  toChrome.write(JSON.stringify({ id, method, params, sessionId }) + '\0');
});

let extensionId = '';

async function evaluate(expression) {
  const { targetInfos } = await send('Target.getTargets');
  const worker = targetInfos.find(t => t.type === 'service_worker' && t.url.includes(extensionId));
  if (!worker) throw new Error('the extension has no running service worker');
  const { sessionId } = await send('Target.attachToTarget', { targetId: worker.targetId, flatten: true });
  try {
    const r = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true }, sessionId);
    if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? r.exceptionDetails.text);
    return r.result.value ?? null;
  } finally {
    await send('Target.detachFromTarget', { sessionId }).catch(() => {});
  }
}

// As for a real user, who had to turn it on to load the extension. Without it Chrome disables an unpacked extension
// the moment it reloads (disable reason unsupportedDeveloperExtension, measured on 156).
async function developerMode() {
  const { targetId } = await send('Target.createTarget', { url: 'chrome://extensions' });
  const { sessionId } = await send('Target.attachToTarget', { targetId, flatten: true });
  await new Promise(resolve => setTimeout(resolve, 800));
  await send('Runtime.evaluate', {
    expression: 'new Promise(done => chrome.developerPrivate.updateProfileConfiguration({inDeveloperMode: true}, done))',
    awaitPromise: true,
  }, sessionId);
  await send('Target.closeTarget', { targetId });
}

function removeProfile() {
  try { rmSync(profile, { recursive: true, force: true, maxRetries: 10, retryDelay: 300 }); }
  catch (e) { say({ profileLeft: profile, reason: String(e) }); }
}

proc.on('exit', () => {
  removeProfile();
  say({ exited: true });
  process.exit(0);
});

function quit() {
  if (quitting) return;
  quitting = true;
  send('Browser.close').catch(() => {});
  setTimeout(() => { try { proc.kill(); } catch { /* already gone */ } }, 4000);
}

createInterface({ input: process.stdin }).on('line', async line => {
  const { id, expression } = JSON.parse(line);
  try { say({ id, ok: true, value: await evaluate(expression) }); }
  catch (e) { say({ id, ok: false, error: String(e?.message ?? e) }); }
}).on('close', quit);

try {
  await developerMode();
  extensionId = (await send('Extensions.loadUnpacked', { path: extension })).id;
  say({ ready: true, extensionId });
} catch (e) {
  say({ failed: String(e?.message ?? e) });
  quit();
}
