/* CluadeX UI Studio overlay — injected into every page the Studio shows (AddScriptToExecuteOnDocumentCreated).
 * Draws the hover/selection boxes in a closed shadow root (page CSS can't touch them, they never touch the page),
 * reports the picked element with its exact source location, and applies live previews the host asks for.
 * Talks to CluadeX only through window.chrome.webview — in any other browser it does nothing. */
(function () {
  'use strict';
  if (window.top !== window || window.__cx) return;           // top frame only, once
  const host = window.chrome && window.chrome.webview;
  if (!host) return;

  const ATTR = 'data-cx-src';
  const MAX_NODES = 4000, MAX_DEPTH = 200;
  const STYLE_KEYS = ['display', 'position', 'width', 'height', 'min-width', 'max-width', 'min-height',
    'padding-top', 'padding-right', 'padding-bottom', 'padding-left',
    'margin-top', 'margin-right', 'margin-bottom', 'margin-left',
    'gap', 'flex-direction', 'justify-content', 'align-items', 'flex-wrap', 'grid-template-columns',
    'color', 'background-color', 'background-image', 'font-family', 'font-size', 'font-weight', 'line-height',
    'letter-spacing', 'text-align', 'text-transform', 'border-radius', 'border-top-width', 'border-top-style',
    'border-top-color', 'opacity', 'box-shadow', 'overflow', 'z-index', 'cursor'];

  let mode = 'select';          // 'select' | 'interact'
  let hovered = null, selected = null, editing = null;
  let nextId = 1;
  const ids = new WeakMap(), byId = new Map();
  const preview = new Map();    // element -> what it was before the live preview (for revert)
  const snap = (el) => {
    let s = preview.get(el);
    if (!s) {
      s = { style: el.getAttribute('style'), cls: el.getAttribute('class'), text: null, attrs: {} };
      if (onlyText(el)) s.text = el.textContent;
      preview.set(el, s);
    }
    return s;
  };

  const post = (msg) => { try { host.postMessage(msg); } catch (_) {} };
  const idOf = (el) => { let n = ids.get(el); if (!n) { n = nextId++; ids.set(el, n); } byId.set(n, new WeakRef(el)); return n; };
  const elById = (n) => { const r = byId.get(n); const el = r && r.deref(); return el && el.isConnected ? el : null; };

  // ── overlay layer ──────────────────────────────────────────────────────────
  let shell, root, hoverBox, hoverTag, selBox, selTag, marginBox, paddingBox, handles = {};
  function mount() {
    if (shell && shell.isConnected) return;
    shell = document.createElement('cx-studio-overlay');
    shell.style.cssText = 'all:initial;position:fixed;inset:0;pointer-events:none;z-index:2147483647;';
    root = shell.attachShadow({ mode: 'closed' });
    root.innerHTML = `<style>
      .box{position:fixed;pointer-events:none;box-sizing:border-box;display:none}
      .hover{outline:1.5px dashed #7c5cff;background:rgba(124,92,255,.06)}
      .sel{outline:2px solid #22d3ee}
      .margin{background:rgba(251,146,60,.18)}
      .padding{background:rgba(74,222,128,.18)}
      .tag{position:fixed;display:none;font:600 11px/1.6 ui-monospace,Consolas,monospace;color:#fff;
           padding:0 6px;border-radius:4px;white-space:nowrap;pointer-events:none;max-width:60vw;overflow:hidden;text-overflow:ellipsis}
      .tag.h{background:#7c5cff}.tag.s{background:#0891b2}
      .handle{position:fixed;width:9px;height:9px;background:#22d3ee;border:1.5px solid #fff;border-radius:2px;
              display:none;pointer-events:auto;box-shadow:0 0 0 1px rgba(0,0,0,.25)}
      .handle.e{cursor:ew-resize}.handle.s{cursor:ns-resize}.handle.se{cursor:nwse-resize}
    </style>
    <div class="box margin"></div><div class="box padding"></div>
    <div class="box hover"></div><div class="box sel"></div>
    <div class="tag h"></div><div class="tag s"></div>
    <div class="handle e"></div><div class="handle s"></div><div class="handle se"></div>`;
    [marginBox, paddingBox, hoverBox, selBox] = root.querySelectorAll('.box');
    [hoverTag, selTag] = root.querySelectorAll('.tag');
    root.querySelectorAll('.handle').forEach(h => {
      const dir = h.classList[1];
      handles[dir] = h;
      h.addEventListener('pointerdown', (e) => startResize(e, dir));
    });
    (document.documentElement || document).appendChild(shell);
  }

  function label(el) {
    let s = el.tagName.toLowerCase();
    if (el.id) s += '#' + el.id;
    const cls = typeof el.className === 'string' ? el.className.trim().split(/\s+/).filter(Boolean).slice(0, 2) : [];
    if (cls.length) s += '.' + cls.join('.');
    return s;
  }

  function place(box, r) {
    box.style.display = 'block';
    box.style.left = r.left + 'px'; box.style.top = r.top + 'px';
    box.style.width = Math.max(0, r.width) + 'px'; box.style.height = Math.max(0, r.height) + 'px';
  }
  function placeTag(tag, r, text) {
    tag.textContent = text;
    tag.style.display = 'block';
    const top = r.top > 20 ? r.top - 19 : r.bottom + 2;
    tag.style.left = Math.max(0, r.left) + 'px'; tag.style.top = top + 'px';
  }

  function drawHover() {
    if (!root) return;
    if (!hovered || hovered === selected || mode !== 'select') { hoverBox.style.display = 'none'; hoverTag.style.display = 'none'; return; }
    const r = hovered.getBoundingClientRect();
    place(hoverBox, r);
    placeTag(hoverTag, r, label(hovered) + '  ' + Math.round(r.width) + '×' + Math.round(r.height));
  }

  function drawSelection() {
    if (!root) return;
    const hide = () => { [selBox, marginBox, paddingBox, selTag].forEach(b => b.style.display = 'none'); Object.values(handles).forEach(h => h.style.display = 'none'); };
    if (!selected || !selected.isConnected) { hide(); return; }
    const r = selected.getBoundingClientRect();
    const cs = getComputedStyle(selected);
    const px = (v) => parseFloat(v) || 0;
    place(selBox, r);
    place(marginBox, { left: r.left - px(cs.marginLeft), top: r.top - px(cs.marginTop),
      width: r.width + px(cs.marginLeft) + px(cs.marginRight), height: r.height + px(cs.marginTop) + px(cs.marginBottom) });
    place(paddingBox, { left: r.left + px(cs.borderLeftWidth), top: r.top + px(cs.borderTopWidth),
      width: r.width - px(cs.borderLeftWidth) - px(cs.borderRightWidth), height: r.height - px(cs.borderTopWidth) - px(cs.borderBottomWidth) });
    placeTag(selTag, r, label(selected) + '  ' + Math.round(r.width) + '×' + Math.round(r.height));
    const show = mode === 'select' && !editing;
    const put = (h, x, y) => { h.style.display = show ? 'block' : 'none'; h.style.left = (x - 5) + 'px'; h.style.top = (y - 5) + 'px'; };
    put(handles.e, r.right, r.top + r.height / 2);
    put(handles.s, r.left + r.width / 2, r.bottom);
    put(handles.se, r.right, r.bottom);
  }

  let rafPending = false;
  function redraw() {
    if (rafPending) return;
    rafPending = true;
    requestAnimationFrame(() => { rafPending = false; drawHover(); drawSelection(); });
  }

  // ── source location ────────────────────────────────────────────────────────
  function reactSource(el) {
    // React 16–18 dev builds keep where each host element was written (React 19 dropped _debugSource).
    for (const k in el) {
      if (k.startsWith('__reactFiber$') || k.startsWith('__reactInternalInstance$')) {
        const s = el[k] && el[k]._debugSource;
        if (s && s.fileName) return s.fileName + ':' + s.lineNumber + ':' + (s.columnNumber || 1);
        return null;
      }
    }
    return null;
  }
  function svelteSource(el) {
    const m = el.__svelte_meta && el.__svelte_meta.loc;
    return m && m.file ? m.file + ':' + m.line + ':' + (m.column + 1) : null;
  }
  function ownSource(el) {
    if (!el || el.nodeType !== 1) return null;
    const a = el.getAttribute(ATTR);
    if (a) return { src: a, kind: 'locator' };
    const r = reactSource(el) || svelteSource(el);
    return r ? { src: r, kind: 'react' } : null;
  }
  function ancestorSource(el) {
    for (let p = el.parentElement; p; p = p.parentElement) { const s = ownSource(p); if (s) return s.src; }
    return null;
  }

  function cssPath(el) {
    const parts = [];
    for (let e = el; e && e.nodeType === 1 && e !== document.documentElement; e = e.parentElement) {
      if (e.id && /^[A-Za-z][\w-]*$/.test(e.id)) { parts.unshift('#' + e.id); break; }
      let s = e.tagName.toLowerCase();
      const p = e.parentElement;
      if (p) {
        const same = Array.from(p.children).filter(c => c.tagName === e.tagName);
        if (same.length > 1) s += ':nth-of-type(' + (same.indexOf(e) + 1) + ')';
      }
      parts.unshift(s);
    }
    return parts.join(' > ');
  }

  function onlyText(el) {
    if (!el.childNodes.length) return !['IMG', 'INPUT', 'BR', 'HR', 'svg', 'SVG', 'VIDEO', 'IFRAME', 'CANVAS', 'SELECT', 'TEXTAREA'].includes(el.tagName);
    for (const n of el.childNodes) if (n.nodeType === 1) return false;
    return true;
  }

  function describe(el) {
    const own = ownSource(el);
    const cs = getComputedStyle(el);
    const styles = {};
    for (const k of STYLE_KEYS) styles[k] = cs.getPropertyValue(k);
    const attrs = {};
    for (const a of ['href', 'src', 'alt', 'title', 'placeholder', 'type', 'name', 'value', 'target', 'rel', 'aria-label', 'role'])
      if (el.hasAttribute(a)) attrs[a] = el.getAttribute(a);
    const sib = (e) => (e ? (ownSource(e) || {}).src || null : null);
    const r = el.getBoundingClientRect();
    let html = el.outerHTML || '';
    html = html.replace(/\sdata-cx-src="[^"]*"/g, '');
    if (html.length > 1500) html = html.slice(0, 1500) + '…';
    return {
      nodeId: idOf(el), tag: el.tagName.toLowerCase(), id: el.id || '',
      classes: typeof el.className === 'string' ? el.className.trim() : (el.getAttribute('class') || ''),
      text: (el.innerText || el.textContent || '').trim().slice(0, 2000), onlyText: onlyText(el),
      src: own ? own.src : null, srcKind: own ? own.kind : (ancestorSource(el) ? 'ancestor' : 'none'),
      ancestorSrc: own ? null : ancestorSource(el),
      parentSrc: sib(el.parentElement), prevSrc: sib(el.previousElementSibling), nextSrc: sib(el.nextElementSibling),
      selector: cssPath(el), width: r.width, height: r.height, styles,
      inlineStyle: el.getAttribute('style') || '', attrs, html, childCount: el.children.length,
    };
  }

  // ── selection ──────────────────────────────────────────────────────────────
  function isOurs(el) { return el === shell || (el && el.tagName === 'CX-STUDIO-OVERLAY'); }
  function pickable(el) {
    while (el && (el.nodeType !== 1 || isOurs(el))) el = el.parentElement;
    if (!el || el === document.documentElement) return document.body;
    return el;
  }

  function select(el, quiet) {
    if (editing) finishEdit(true);
    selected = el;
    if (el) { lastSelSrc = el.getAttribute(ATTR); lastSelPath = cssPath(el); }
    redraw();
    if (!el) { post({ type: 'deselect' }); return; }
    if (!quiet) post({ type: 'select', el: describe(el) });
  }

  // capture phase: the page never sees clicks while selecting
  const swallow = (e) => { if (mode === 'select' && !editing && !inHandle(e)) { e.preventDefault(); e.stopImmediatePropagation(); } };
  // The overlay host has pointer-events:none, so the only thing of ours a pointer can hit is a resize handle
  // (closed shadow root: the document sees the host as the target).
  const inHandle = (e) => e.target === shell;

  document.addEventListener('pointermove', (e) => {
    if (mode !== 'select' || resizing) return;
    const el = pickable(e.target);
    if (el !== hovered) { hovered = el; redraw(); }
  }, true);
  document.addEventListener('pointerleave', () => { hovered = null; redraw(); }, true);
  ['pointerdown', 'mousedown', 'pointerup', 'mouseup', 'auxclick', 'contextmenu', 'submit'].forEach(t => document.addEventListener(t, swallow, true));
  document.addEventListener('click', (e) => {
    if (mode !== 'select' || editing) { if (editing && !editing.contains(e.target)) finishEdit(true); return; }
    if (inHandle(e)) return;
    e.preventDefault(); e.stopImmediatePropagation();
    select(pickable(e.target));
  }, true);
  document.addEventListener('dblclick', (e) => {
    if (mode !== 'select' || editing) return;
    e.preventDefault(); e.stopImmediatePropagation();
    const el = pickable(e.target);
    if (onlyText(el) && el !== document.body) beginEdit(el);
  }, true);

  // ── inline text edit ───────────────────────────────────────────────────────
  let editOriginal = '';
  function beginEdit(el) {
    select(el);
    editing = el;
    editOriginal = el.textContent;
    el.setAttribute('contenteditable', 'plaintext-only');
    el.focus();
    const range = document.createRange(); range.selectNodeContents(el);
    const s = getSelection(); s.removeAllRanges(); s.addRange(range);
    redraw();
  }
  function finishEdit(commit) {
    const el = editing;
    if (!el) return;
    editing = null;
    el.removeAttribute('contenteditable');
    const value = el.textContent;
    if (!commit) el.textContent = editOriginal;
    else if (value !== editOriginal) post({ type: 'text', value, el: describe(el) });
    redraw();
  }

  // ── resize by dragging ─────────────────────────────────────────────────────
  let resizing = null;
  function startResize(e, dir) {
    if (!selected) return;
    e.preventDefault(); e.stopPropagation();
    const r = selected.getBoundingClientRect();
    snap(selected);
    resizing = { dir, x: e.clientX, y: e.clientY, w: r.width, h: r.height, el: selected };
    e.target.setPointerCapture(e.pointerId);
    e.target.addEventListener('pointermove', onResize);
    e.target.addEventListener('pointerup', endResize, { once: true });
  }
  function onResize(e) {
    const z = resizing; if (!z) return;
    const grid = (v) => e.shiftKey ? Math.round(v) : Math.round(v / 4) * 4;   // 4px grid unless Shift
    if (z.dir !== 's') z.el.style.width = grid(z.w + e.clientX - z.x) + 'px';
    if (z.dir !== 'e') z.el.style.height = grid(z.h + e.clientY - z.y) + 'px';
    redraw();
  }
  function endResize(e) {
    const z = resizing; resizing = null;
    e.target.removeEventListener('pointermove', onResize);
    if (!z) return;
    post({ type: 'resize', width: z.dir !== 's' ? z.el.style.width : null, height: z.dir !== 'e' ? z.el.style.height : null, el: describe(z.el) });
  }

  // ── keys ───────────────────────────────────────────────────────────────────
  document.addEventListener('keydown', (e) => {
    if (editing) {
      if (e.key === 'Escape') { e.preventDefault(); finishEdit(false); }
      else if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); finishEdit(true); }
      return;
    }
    if (mode !== 'select' || !selected) return;
    const k = e.key, ctrl = e.ctrlKey || e.metaKey;
    let cmd = null;
    if (k === 'Escape') { const p = selected.parentElement; if (p && p !== document.documentElement) select(p); e.preventDefault(); return; }
    if (k === 'Delete') cmd = 'delete';
    else if (ctrl && (k === 'd' || k === 'D')) cmd = 'duplicate';
    else if (e.altKey && k === 'ArrowUp') cmd = 'moveUp';
    else if (e.altKey && k === 'ArrowDown') cmd = 'moveDown';
    else if (ctrl && (k === 'z' || k === 'Z')) cmd = 'undo';
    else if (k === 'Enter' && onlyText(selected)) { e.preventDefault(); beginEdit(selected); return; }
    if (cmd) { e.preventDefault(); e.stopImmediatePropagation(); post({ type: 'command', value: cmd }); }
  }, true);

  window.addEventListener('scroll', redraw, true);
  window.addEventListener('resize', redraw);

  // ── tree for the Layers panel / Node Graph ─────────────────────────────────
  function buildTree() {
    let count = 0, truncated = false;
    const walk = (el, depth) => {
      if (count >= MAX_NODES || depth > MAX_DEPTH) { truncated = true; return null; }
      if (isOurs(el) || ['SCRIPT', 'STYLE', 'NOSCRIPT', 'TEMPLATE', 'LINK', 'META'].includes(el.tagName)) return null;
      count++;
      const own = ownSource(el);
      const node = { n: idOf(el), t: el.tagName.toLowerCase() };
      if (el.id) node.i = el.id;
      const cls = typeof el.className === 'string' ? el.className.trim() : '';
      if (cls) node.c = cls.length > 80 ? cls.slice(0, 80) : cls;
      if (own) node.s = own.src;
      if (onlyText(el)) { const t = (el.textContent || '').trim(); if (t) node.x = t.length > 40 ? t.slice(0, 40) + '…' : t; }
      const kids = [];
      for (const c of el.children) { const k = walk(c, depth + 1); if (k) kids.push(k); }
      if (kids.length) node.k = kids;
      return node;
    };
    const tree = document.body ? walk(document.body, 0) : null;
    post({ type: 'tree', tree, truncated });
  }

  let treeTimer = 0;
  const scheduleTree = () => { clearTimeout(treeTimer); treeTimer = setTimeout(() => { buildTree(); if (selected && !selected.isConnected) reselectLost(); redraw(); }, 400); };
  let lastSelSrc = null, lastSelPath = null;
  function reselectLost() {
    const el = (lastSelSrc && document.querySelector('[' + ATTR + '="' + CSS.escape(lastSelSrc) + '"]'))
      || (lastSelPath && document.querySelector(lastSelPath));
    select(el || null);
  }

  // ── API for the host ───────────────────────────────────────────────────────
  window.__cx = {
    setMode(m) { mode = m === 'interact' ? 'interact' : 'select'; if (editing) finishEdit(true); hovered = null; redraw(); },
    selectById(n) { const el = elById(n); if (el) { el.scrollIntoView({ block: 'nearest', inline: 'nearest' }); select(el); } },
    hoverById(n) { hovered = elById(n); redraw(); },
    selectBySrc(src) { const el = src ? document.querySelector('[' + ATTR + '="' + CSS.escape(src) + '"]') : null; if (el) { el.scrollIntoView({ block: 'nearest' }); select(el); } return !!el; },
    selectBySelector(sel) {
      let el = null;
      try { el = sel ? document.querySelector(sel) : null; } catch (_) { el = null; }
      if (el) { el.scrollIntoView({ block: 'nearest' }); select(el); }
      return !!el;
    },
    selectParent() { if (selected && selected.parentElement && selected.parentElement !== document.documentElement) select(selected.parentElement); },
    deselect() { select(null); },
    /** Live preview: set inline styles on the selected element (null/'' removes). Nothing is saved. */
    previewStyle(map) {
      if (!selected) return;
      snap(selected);
      for (const k in map) { const v = map[k]; if (v == null || v === '') selected.style.removeProperty(k); else selected.style.setProperty(k, v); }
      redraw();
    },
    previewText(t) { if (selected && onlyText(selected)) { snap(selected); selected.textContent = t; redraw(); } },
    previewClass(c) { if (selected) { snap(selected); selected.setAttribute('class', c); redraw(); } },
    previewAttr(name, v) {
      if (!selected) return;
      const s = snap(selected);
      if (!(name in s.attrs)) s.attrs[name] = selected.getAttribute(name);
      if (v == null) selected.removeAttribute(name); else selected.setAttribute(name, v);
      redraw();
    },
    /** Put back everything previewed since the last save (style, class, text, attributes). */
    revertPreview() {
      for (const [el, s] of preview) {
        if (s.style == null) el.removeAttribute('style'); else el.setAttribute('style', s.style);
        if (s.cls == null) el.removeAttribute('class'); else el.setAttribute('class', s.cls);
        if (s.text != null && onlyText(el)) el.textContent = s.text;
        for (const a in s.attrs) { if (s.attrs[a] == null) el.removeAttribute(a); else el.setAttribute(a, s.attrs[a]); }
      }
      preview.clear(); redraw();
      if (selected) post({ type: 'select', el: describe(selected) });
    },
    commitPreview() { preview.clear(); },
    refreshSelection() { if (selected && selected.isConnected) post({ type: 'select', el: describe(selected) }); },
    rememberSelection() { lastSelSrc = selected ? selected.getAttribute(ATTR) : null; lastSelPath = selected ? cssPath(selected) : null; },
    restoreSelection() { if (lastSelSrc || lastSelPath) reselectLost(); },
    sendTree: buildTree,
    reloadCss() {
      for (const l of document.querySelectorAll('link[rel="stylesheet"]')) {
        const u = new URL(l.href, location.href); u.searchParams.set('cxv', Date.now()); l.href = u.toString();
      }
    },
    /** The changes the user made live, as CSS — for pages the Studio can't write (a remote site). */
    exportCss() {
      const out = [];
      for (const [el] of preview) { const s = el.getAttribute('style'); if (s) out.push(cssPath(el) + ' { ' + s + ' }'); }
      return out.join('\n');
    },
    /** Which of these declarations the browser rejects (the inspector refuses to save them). */
    invalidCss(map) { return Object.keys(map).filter((k) => map[k] && !CSS.supports(k, map[k])); },
  };

  function start() {
    mount();
    buildTree();
    new MutationObserver((list) => {
      for (const m of list) { if (!isOurs(m.target) && !(m.target === document.documentElement && [...m.addedNodes].every(isOurs))) { scheduleTree(); return; } }
    }).observe(document.documentElement, { childList: true, subtree: true });
    post({ type: 'ready', value: location.href });
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
  else start();
})();
