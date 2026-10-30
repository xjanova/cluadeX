// CluadeX Bridge: gives the CluadeX app on this PC a window of its own in this Chrome.
// The app reaches this worker through a native-messaging host (CluadeX.exe itself, over a pipe only this Windows user
// can open). There is no server and no account in between. The agent drives only the window opened here for it; the
// user's own tabs are never attached to.

const HOST = 'com.xmanstudio.cluadex_chrome';
const PAUSED_TEXT = 'Paused from the CluadeX icon in Chrome.';

// Exactly what CluadeX's browser tools send. This is the user's real profile: reading cookies and clearing a site's
// data are left out on purpose, so a bug or a hijacked host still cannot reach them.
const ALLOWED_METHODS = new Set([
  'Emulation.clearDeviceMetricsOverride', 'Emulation.setDeviceMetricsOverride', 'Emulation.setEmitTouchEventsForMouse',
  'Emulation.setFocusEmulationEnabled', 'Emulation.setTouchEmulationEnabled', 'Emulation.setUserAgentOverride',
  'Fetch.continueRequest', 'Fetch.enable', 'Fetch.fulfillRequest',
  'Input.dispatchKeyEvent', 'Input.dispatchMouseEvent', 'Input.dispatchTouchEvent', 'Input.insertText',
  'Log.enable', 'Network.enable', 'Network.setCacheDisabled',
  'Page.addScriptToEvaluateOnNewDocument', 'Page.captureScreenshot', 'Page.createIsolatedWorld', 'Page.enable',
  'Page.getFrameTree', 'Page.getLayoutMetrics', 'Page.handleJavaScriptDialog', 'Page.navigate',
  'Page.removeScriptToEvaluateOnNewDocument', 'Page.setLifecycleEventsEnabled',
  'Runtime.addBinding', 'Runtime.callFunctionOn', 'Runtime.enable', 'Runtime.evaluate', 'Runtime.releaseObjectGroup',
  'Runtime.runIfWaitingForDebugger', 'Runtime.terminateExecution',
  'Target.setAutoAttach',
]);

// A popup's tab is not under the debugger, so closing it from tabs.onCreated can lose the race to its first request
// (measured on Chrome 156: 3 of 8 reached the site). So the browser itself refuses them: a sandbox without allow-popups,
// on the agent's tab only. A page cannot script its way out of a header.
const NO_POPUPS_RULE = 1;
const NO_POPUPS = 'sandbox allow-downloads allow-forms allow-modals allow-orientation-lock allow-pointer-lock '
  + 'allow-presentation allow-same-origin allow-scripts allow-storage-access-by-user-activation allow-top-navigation';

let port = null;
let linked = false;          // the app answered, not only its host
let connectedAt = 0;
let agent = null;            // { tabId, windowId } of the window the app opened
let paused = false;
let forwarded = new Set();   // the CDP events the app asked for

const ready = chrome.storage.local.get('paused').then(v => { paused = !!v.paused; });

function send(message) {
  try { port?.postMessage(message); } catch { /* the port just closed: onDisconnect handles it */ }
}

function refreshBadge() {
  chrome.action.setBadgeText({ text: paused ? 'II' : linked ? 'ON' : '' });
  chrome.action.setBadgeBackgroundColor({ color: paused ? '#8a8a8a' : '#1f9d55' });
  chrome.action.setTitle({
    title: paused ? 'CluadeX is paused. Click to let it work again.'
      : linked ? 'CluadeX is connected. Click to pause it.'
        : 'CluadeX Bridge: waiting for the CluadeX app',
  });
}

async function connect() {
  await ready;
  if (port) return;
  connectedAt = Date.now();
  let opened;
  try { opened = chrome.runtime.connectNative(HOST); } catch { return; }
  port = opened;
  opened.onMessage.addListener(onMessage);
  opened.onDisconnect.addListener(() => {
    void chrome.runtime.lastError;
    if (port !== opened) return;
    const worked = linked || Date.now() - connectedAt > 3000;
    port = null;
    linked = false;
    detachAgent();
    refreshBadge();
    // A host that dies at once is not registered (the feature is off in CluadeX): the alarm tries again, gently.
    if (worked) setTimeout(connect, 1000);
  });
  send({ ev: 'hello', version: chrome.runtime.getManifest().version, userAgent: navigator.userAgent, paused });
}

function refusePopups(tabId) {
  return chrome.declarativeNetRequest.updateSessionRules({
    removeRuleIds: [NO_POPUPS_RULE],
    addRules: tabId == null ? [] : [{
      id: NO_POPUPS_RULE,
      action: { type: 'modifyHeaders', responseHeaders: [{ header: 'Content-Security-Policy', operation: 'append', value: NO_POPUPS }] },
      condition: { tabIds: [tabId], resourceTypes: ['main_frame', 'sub_frame'] },
    }],
  });
}

async function detachAgent() {
  const left = agent;
  agent = null;
  forwarded = new Set();
  await Promise.all([chrome.storage.session.remove('agent'), refusePopups(null)]);
  if (left) {
    try { await chrome.debugger.detach({ tabId: left.tabId }); } catch { /* already gone */ }
  }
  return left;
}

// A minimized window or a tab behind another never answers captureScreenshot (measured on Chrome 156).
async function keepDrawable() {
  const tab = await chrome.tabs.get(agent.tabId);   // the user may have dragged it to another window
  const win = await chrome.windows.get(tab.windowId);
  if (win.state === 'minimized') await chrome.windows.update(tab.windowId, { state: 'normal' });
  if (!tab.active) await chrome.tabs.update(agent.tabId, { active: true });
}

const COMMANDS = {
  welcome() {
    linked = true;
    refreshBadge();
  },

  // A normal window, never focused: the user keeps typing where they were.
  async open({ width, height }) {
    if (paused) throw new Error(PAUSED_TEXT);
    await COMMANDS.close();
    const win = await chrome.windows.create({ url: 'about:blank', type: 'normal', focused: false, width, height });
    const tabId = win.tabs[0].id;
    try {
      await chrome.debugger.attach({ tabId }, '1.3');
    } catch (e) {
      await chrome.windows.remove(win.id).catch(() => {});
      throw e;
    }
    agent = { tabId, windowId: win.id };
    await Promise.all([chrome.storage.session.set({ agent }), refusePopups(tabId)]);
    return agent;
  },

  async close() {
    const left = await detachAgent();
    if (left) await chrome.tabs.remove(left.tabId).catch(() => {});
  },

  subscribe({ events }) {
    forwarded = new Set(events);
  },

  async cdp({ method, params, sessionId }) {
    if (paused) throw new Error(PAUSED_TEXT);
    if (!agent) throw new Error('CluadeX has no window open.');
    if (!ALLOWED_METHODS.has(method)) throw new Error(`${method} is not allowed on your real Chrome profile.`);
    if (method === 'Page.captureScreenshot') await keepDrawable();
    const target = sessionId ? { tabId: agent.tabId, sessionId } : { tabId: agent.tabId };
    return await chrome.debugger.sendCommand(target, method, params);
  },

  reload() {
    chrome.runtime.reload();
  },
};

async function onMessage(message) {
  const { id, cmd } = message;
  try {
    const run = COMMANDS[cmd];
    if (!run) throw new Error(`Unknown command: ${cmd}`);
    const result = await run(message);
    if (id != null) send({ id, ok: true, result: result ?? {} });
  } catch (e) {
    if (id != null) send({ id, ok: false, error: String(e?.message ?? e) });
  }
}

async function setPaused(value) {
  paused = value;
  await chrome.storage.local.set({ paused });
  send({ ev: 'state', paused });
  refreshBadge();
}

chrome.action.onClicked.addListener(async () => {
  const pausing = !paused;
  await setPaused(pausing);
  // The window stays open: what the agent was showing is the user's to look at. Only the control ends.
  if (pausing && agent) {
    await detachAgent();
    send({ ev: 'detached', reason: 'paused' });
  }
});

chrome.debugger.onEvent.addListener((source, method, params) => {
  if (agent && source.tabId === agent.tabId && forwarded.has(method)) send({ ev: 'cdp', sessionId: source.sessionId, method, params });
});

chrome.debugger.onDetach.addListener(async (source, reason) => {
  if (!agent || source.tabId !== agent.tabId) return;
  agent = null;
  await Promise.all([chrome.storage.session.remove('agent'), refusePopups(null)]);
  // The Cancel button on Chrome's "started debugging" bar means stop: it holds until the user clicks the icon.
  if (reason === 'canceled_by_user') await setPaused(true);
  send({ ev: 'detached', reason });
});

// The second line behind the sandbox, for a document that came with no response to add it to.
chrome.tabs.onCreated.addListener(tab => {
  if (!agent || tab.openerTabId !== agent.tabId) return;
  send({ ev: 'popup', url: tab.pendingUrl || tab.url || '' });
  chrome.tabs.remove(tab.id).catch(() => {});
});

chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);
chrome.alarms.onAlarm.addListener(connect);
chrome.alarms.create('reconnect', { periodInMinutes: 1 });

// A worker restarted by Chrome forgets its window, but Chrome keeps the debugger attached to it.
chrome.storage.session.get('agent').then(({ agent: stale }) => {
  if (!stale) return;
  chrome.debugger.detach({ tabId: stale.tabId }).catch(() => {}).finally(() => chrome.storage.session.remove('agent'));
  refusePopups(null);
});

refreshBadge();
connect();
