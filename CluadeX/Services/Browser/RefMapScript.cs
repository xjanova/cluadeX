namespace CluadeX.Services.Browser;

/// <summary>
/// The helper the browser tools use to read the page and find elements. It is injected with
/// Page.addScriptToEvaluateOnNewDocument into an ISOLATED world named <see cref="WorldName"/>: it shares the DOM
/// with the page but not its JavaScript globals, so page scripts can neither see nor tamper with it (and it cannot
/// read the page's own variables — browser_eval runs in the main world for that).
///
/// ref_N ids are stable until the next navigation (a new document gets a new world). Kept as a C# string rather
/// than an embedded resource so the project file does not change; it is plain ES2020.
/// </summary>
public static class RefMapScript
{
    public const string WorldName = "cluadex";

    public const string Source = """
(() => {
  if (globalThis.__cx) return;
  const refs = new Map(), ids = new WeakMap();
  let next = 1;
  const refOf = el => { let id = ids.get(el); if (!id) { id = 'ref_' + (next++); ids.set(el, id); refs.set(id, new WeakRef(el)); } return id; };
  const byRef = id => { const w = refs.get(String(id || '')); const el = w && w.deref(); return el && el.isConnected ? el : null; };
  const clip = (s, n) => { s = (s || '').replace(/\s+/g, ' ').trim(); return s.length > n ? s.slice(0, n) + '…' : s; };
  const INTERACTIVE = new Set(['a','button','input','select','textarea','summary','details','label','option']);
  const ROLE_BY_TAG = { a: 'link', button: 'button', select: 'combobox', textarea: 'textbox', img: 'img', canvas: 'canvas',
    h1: 'heading', h2: 'heading', h3: 'heading', h4: 'heading', h5: 'heading', h6: 'heading', nav: 'navigation',
    main: 'main', header: 'banner', footer: 'contentinfo', form: 'form', table: 'table', ul: 'list', ol: 'list',
    li: 'listitem', dialog: 'dialog', video: 'video', audio: 'audio', iframe: 'iframe', summary: 'button',
    label: 'label', p: 'paragraph', option: 'option', svg: 'img' };
  const hidden = el => {
    if (!(el instanceof Element)) return false;
    if (el.hidden || el.getAttribute('aria-hidden') === 'true') return true;
    const cs = getComputedStyle(el);
    return cs.display === 'none' || cs.visibility === 'hidden' || cs.visibility === 'collapse' || (cs.opacity === '0' && el.tagName !== 'CANVAS');
  };
  const roleOf = el => {
    const r = el.getAttribute && el.getAttribute('role'); if (r) return r.split(' ')[0];
    const tag = el.tagName.toLowerCase();
    if (tag === 'input') {
      const t = (el.getAttribute('type') || 'text').toLowerCase();
      return ({ checkbox: 'checkbox', radio: 'radio', button: 'button', submit: 'button', reset: 'button', image: 'button',
        range: 'slider', number: 'spinbutton', search: 'searchbox', file: 'button', hidden: 'none' })[t] || 'textbox';
    }
    if (tag === 'a' && !el.hasAttribute('href')) return 'generic';
    return ROLE_BY_TAG[tag] || (el.isContentEditable ? 'textbox' : 'generic');
  };
  const isInteractive = el => {
    const tag = el.tagName.toLowerCase();
    if (INTERACTIVE.has(tag)) return !(tag === 'a' && !el.hasAttribute('href')) && !(tag === 'input' && el.type === 'hidden');
    if (el.isContentEditable) return true;
    const r = el.getAttribute('role');
    if (r && /^(button|link|checkbox|radio|tab|menuitem|switch|slider|textbox|combobox|option|searchbox|spinbutton)$/.test(r)) return true;
    if (el.hasAttribute('onclick') || (el.hasAttribute('tabindex') && el.getAttribute('tabindex') !== '-1')) return true;
    return tag === 'canvas';
  };
  const nameOf = el => {
    const al = el.getAttribute('aria-label'); if (al) return clip(al, 80);
    const lb = el.getAttribute('aria-labelledby');
    if (lb) { const t = lb.split(/\s+/).map(i => document.getElementById(i)).filter(Boolean).map(e => e.innerText).join(' '); if (t.trim()) return clip(t, 80); }
    const tag = el.tagName.toLowerCase();
    if (tag === 'img' || (tag === 'input' && el.type === 'image')) return clip(el.getAttribute('alt') || el.getAttribute('title') || '', 80);
    if (tag === 'input' || tag === 'textarea' || tag === 'select') {
      if (el.id) { const l = document.querySelector('label[for="' + CSS.escape(el.id) + '"]'); if (l && l.innerText.trim()) return clip(l.innerText, 80); }
      const pl = el.closest('label'); if (pl && pl.innerText.trim()) return clip(pl.innerText, 80);
      if (['button','submit','reset'].includes(el.type)) return clip(el.value, 80);
      return clip(el.getAttribute('placeholder') || el.getAttribute('title') || el.getAttribute('name') || el.id || '', 80);
    }
    if (tag === 'canvas') return el.id ? '#' + el.id : '';
    return clip(el.innerText || el.textContent || el.getAttribute('title') || '', 80);
  };
  const box = el => { const r = el.getBoundingClientRect(); return { x: Math.round(r.left), y: Math.round(r.top), w: Math.round(r.width), h: Math.round(r.height) }; };
  const offscreen = r => r.y + r.h < 0 || r.x + r.w < 0 || r.y > innerHeight || r.x > innerWidth;
  const valueOf = el => {
    const tag = el.tagName.toLowerCase();
    if (tag === 'input') { const t = (el.type || '').toLowerCase(); if (t === 'password') return el.value ? '••••' : ''; if (t === 'checkbox' || t === 'radio') return null; return el.value; }
    if (tag === 'textarea' || tag === 'select') return el.value;
    return null;
  };
  const stateOf = el => {
    const s = [];
    if (el.disabled) s.push('disabled');
    if (el.checked) s.push('checked');
    if (el.getAttribute('aria-expanded') === 'true') s.push('expanded');
    if (el.getAttribute('aria-selected') === 'true') s.push('selected');
    if (el.required) s.push('required');
    if (document.activeElement === el) s.push('focused');
    return s;
  };
  const line = (el, depth) => {
    const role = roleOf(el), id = refOf(el), r = box(el);
    let s = '  '.repeat(depth) + role;
    const n = nameOf(el); if (n) s += ' "' + n.replace(/"/g, "'") + '"';
    if (role === 'heading') s += ' level=' + el.tagName.slice(1);
    if (role === 'link') { const h = el.getAttribute('href') || ''; if (h && !h.startsWith('javascript:')) s += ' -> ' + clip(h, 100); }
    const v = valueOf(el); if (v !== null && v !== undefined) s += ' value="' + clip(String(v), 60).replace(/"/g, "'") + '"';
    if (role === 'canvas') s += ' ' + r.w + 'x' + r.h + ' css px (pixels: use browser_screenshot or browser_eval)';
    const st = stateOf(el); if (st.length) s += ' (' + st.join(', ') + ')';
    if (offscreen(r)) s += ' (offscreen)';
    return s + ' [' + id + ']';
  };
  const MEANINGFUL = new Set(['heading','link','button','textbox','checkbox','radio','combobox','slider','spinbutton','searchbox',
    'img','canvas','dialog','listitem','option','tab','menuitem','switch','video','audio','iframe','label','paragraph','navigation','main','form','table']);
  function walk(root, max, onlyInteractive) {
    const out = []; let used = 0, truncated = false;
    const push = s => { if (used + s.length + 1 > max) { truncated = true; return false; } out.push(s); used += s.length + 1; return true; };
    const visit = (node, depth) => {
      if (truncated) return;
      if (node.nodeType === Node.TEXT_NODE) {
        if (onlyInteractive) return;
        const t = clip(node.textContent, 200);
        if (t && node.parentElement && !MEANINGFUL.has(roleOf(node.parentElement)) && !isInteractive(node.parentElement)) push('  '.repeat(depth) + 'text "' + t.replace(/"/g, "'") + '"');
        return;
      }
      if (node.nodeType !== Node.ELEMENT_NODE) return;
      const el = node, tag = el.tagName.toLowerCase();
      if (tag === 'script' || tag === 'style' || tag === 'noscript' || tag === 'template' || tag === 'head') return;
      if (hidden(el)) return;
      const role = roleOf(el), inter = isInteractive(el);
      let childDepth = depth;
      if (onlyInteractive ? inter : (inter || MEANINGFUL.has(role))) { if (!push(line(el, depth))) return; childDepth = depth + 1; }
      if (role === 'canvas' || role === 'img') return;
      if (!onlyInteractive && (role === 'heading' || role === 'link' || role === 'button' || role === 'paragraph' || role === 'label' || role === 'listitem' || role === 'option')) return;
      for (const c of (el.shadowRoot ? [...el.shadowRoot.childNodes, ...el.childNodes] : el.childNodes)) visit(c, childDepth);
    };
    visit(root, 0);
    return { text: out.join('\n'), truncated };
  }
  function pageText(root, max) {
    const main = root || document.querySelector('main, article, [role=main]') || document.body || document.documentElement;
    let t = (main && main.innerText) || '';
    t = t.replace(/[ \t]+\n/g, '\n').replace(/\n{3,}/g, '\n\n').trim();
    const truncated = t.length > max;
    return { text: truncated ? t.slice(0, max) : t, truncated, total: t.length };
  }
  const api = {
    info: () => ({ url: location.href, title: document.title, w: innerWidth, h: innerHeight, dpr: devicePixelRatio,
      sx: Math.round(scrollX), sy: Math.round(scrollY), sw: document.documentElement.scrollWidth, sh: document.documentElement.scrollHeight,
      ready: document.readyState, focus: document.hasFocus() }),
    read(mode, ref, max) {
      max = Math.max(200, Math.min(max || 20000, 200000));
      const root = ref ? byRef(ref) : null;
      if (ref && !root) return { error: 'stale_ref' };
      if (mode === 'text') return pageText(root, max);
      return walk(root || document.body || document.documentElement, max, mode === 'interactive');
    },
    find(q, limit) {
      q = String(q || '').toLowerCase().trim(); if (!q) return [];
      const out = [];
      for (const el of document.querySelectorAll('body *')) {
        if (out.length >= (limit || 20)) break;
        if (hidden(el)) continue;
        const role = roleOf(el);
        if (role === 'generic' && !isInteractive(el)) {
          // plain containers only when they hold the text themselves (not a parent of a match)
          const own = [...el.childNodes].filter(n => n.nodeType === 3).map(n => n.textContent).join(' ').toLowerCase();
          if (!own.includes(q)) continue;
        }
        const hay = [role, nameOf(el), el.getAttribute('placeholder'), el.getAttribute('aria-label'), el.id, el.getAttribute('name'),
          (el.value && typeof el.value === 'string') ? el.value : ''].join(' ').toLowerCase();
        if (!hay.includes(q)) continue;
        out.push({ ref: refOf(el), role, name: nameOf(el), box: box(el), tag: el.tagName.toLowerCase() });
      }
      return out;
    },
    point(ref) {
      const el = byRef(ref); if (!el) return null;
      el.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' });
      const r = el.getBoundingClientRect();
      return { x: r.left + r.width / 2, y: r.top + r.height / 2, w: r.width, h: r.height, left: r.left, top: r.top,
        role: roleOf(el), name: nameOf(el), tag: el.tagName.toLowerCase(), field: api.fieldOf(el) };
    },
    rect(ref) {
      const el = byRef(ref); if (!el) return null;
      const r = el.getBoundingClientRect();
      return { x: r.left + scrollX, y: r.top + scrollY, w: r.width, h: r.height };
    },
    reveal(ref) {
      const el = byRef(ref); if (!el) return null;
      const r0 = el.getBoundingClientRect();
      if (r0.top < 0 || r0.left < 0 || r0.bottom > innerHeight || r0.right > innerWidth)
        el.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'instant' });
      return api.rect(ref);
    },
    fieldOf(el) {
      if (!el || !el.tagName) return null;
      return { tag: el.tagName.toLowerCase(), type: (el.getAttribute('type') || '').toLowerCase(),
        autocomplete: (el.getAttribute('autocomplete') || '').toLowerCase(), name: el.getAttribute('name') || el.id || '',
        editable: el.isContentEditable || ['input','textarea','select'].includes(el.tagName.toLowerCase()) };
    },
    focused() {
      let el = document.activeElement;
      while (el && el.shadowRoot && el.shadowRoot.activeElement) el = el.shadowRoot.activeElement;
      if (!el || el === document.body) return null;
      return Object.assign({ ref: refOf(el), role: roleOf(el), nameText: nameOf(el) }, api.fieldOf(el));
    },
    focus(ref) { const el = byRef(ref); if (!el) return false; el.scrollIntoView({ block: 'center', behavior: 'instant' }); el.focus(); return document.activeElement === el; },
    change() { const el = document.activeElement; if (!el || el === document.body) return false; el.dispatchEvent(new Event('change', { bubbles: true })); return true; },
    valueOfFocus() { const el = document.activeElement; if (!el || el === document.body) return null; const v = valueOf(el); return v === null ? (el.isContentEditable ? el.innerText : null) : v; },
    selectAll(ref) {
      const el = ref ? byRef(ref) : document.activeElement; if (!el) return false;
      if (typeof el.select === 'function') { el.select(); return true; }
      if (el.isContentEditable) { const r = document.createRange(); r.selectNodeContents(el); const s = getSelection(); s.removeAllRanges(); s.addRange(r); return true; }
      return false;
    },
    canvases() {
      return [...document.querySelectorAll('canvas')].filter(c => !hidden(c)).map(c => {
        const r = c.getBoundingClientRect();
        return { ref: refOf(c), id: c.id || '', x: r.left, y: r.top, w: r.width, h: r.height, pw: c.width, ph: c.height };
      });
    },
    overflowX: () => document.documentElement.scrollWidth > innerWidth + 1,
    frames(ms) {
      return new Promise(res => {
        let n = 0; const t0 = performance.now();
        requestAnimationFrame(function f() { n++; if (performance.now() - t0 < ms) requestAnimationFrame(f); else res(n); });
        setTimeout(() => res(n), ms + 400);
      });
    },
  };
  Object.defineProperty(globalThis, '__cx', { value: Object.freeze(api), configurable: false, writable: false });
})();
""";
}
