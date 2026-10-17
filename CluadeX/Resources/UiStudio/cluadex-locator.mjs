// cluadex-locator — a Vite plugin from CluadeX UI Studio.
//
// In `vite dev` only (apply: 'serve'), it writes data-cx-src="path:line:col" on every HTML element of your
// JSX/TSX, Vue templates, Svelte components and index.html, so clicking an element in CluadeX's UI Studio finds
// the exact line that wrote it. Production builds are untouched: no attribute, no path, no cost.
//
// It only reads your code with the parser your framework already ships (@babel/parser, vue/compiler-sfc,
// svelte/compiler) and inserts attributes; it never changes line numbers. Safe to delete at any time — remove
// this file and the cxLocator() line from vite.config.
import path from 'node:path'

const ATTR = 'data-cx-src'
// .ts/.mts are left alone: with the JSX grammar on, a type assertion like <string>x would read as an element.
const JSX_RE = /\.(jsx|tsx|js|mjs)$/
const SKIP_TAGS = new Set(['html', 'head', 'meta', 'link', 'script', 'style', 'title', 'base', 'template', 'slot'])

export default function cxLocator(options = {}) {
  let root = process.cwd()
  const parsers = {}

  // The parsers come from the project's own node_modules (this file sits in the project root, so a bare import
  // resolves there). Dynamic import works for CommonJS and ESM-only packages alike; a missing one disables
  // that file type quietly.
  const load = async (name) => {
    if (!(name in parsers)) {
      parsers[name] = import(name).then((m) => (m && m.default && !m.parse ? m.default : m)).catch(() => null)
    }
    return parsers[name]
  }

  const rel = (id) => path.relative(root, id).split(path.sep).join('/')

  // 1-based line/col of a 0-based offset
  const lineCol = (code, offset) => {
    let line = 1, start = 0
    for (let i = 0; i < offset; i++) if (code.charCodeAt(i) === 10) { line++; start = i + 1 }
    return [line, offset - start + 1]
  }

  // Insert ` data-cx-src="..."` right after each tag name (offsets from the end, so earlier ones stay valid).
  const apply = (code, inserts) => {
    inserts.sort((a, b) => b.at - a.at)
    let out = code
    for (const { at, text } of inserts) out = out.slice(0, at) + text + out.slice(at)
    return out
  }

  const attr = (file, code, start) => {
    const [l, c] = lineCol(code, start)
    return ` ${ATTR}="${file}:${l}:${c}"`
  }

  async function jsx(code, id) {
    if (!code.includes('<')) return null
    const babel = await load('@babel/parser')
    if (!babel) return null
    let ast
    try {
      ast = babel.parse(code, {
        sourceType: 'module', errorRecovery: true,
        plugins: ['jsx', ...(id.endsWith('.tsx') ? ['typescript'] : []), 'decorators-legacy', 'importAttributes'],
      })
    } catch { return null }
    const file = rel(id)
    const inserts = []
    const visit = (node) => {
      if (!node || typeof node.type !== 'string') return
      if (node.type === 'JSXOpeningElement' && node.name.type === 'JSXIdentifier' && /^[a-z]/.test(node.name.name)
          && !SKIP_TAGS.has(node.name.name)
          && !node.attributes.some(a => a.type === 'JSXAttribute' && a.name && a.name.name === ATTR)) {
        inserts.push({ at: node.name.end, text: attr(file, code, node.start) })
      }
      for (const key in node) {
        if (key === 'loc' || key === 'start' || key === 'end' || key === 'extra') continue
        const v = node[key]
        if (Array.isArray(v)) { for (const c of v) if (c && typeof c.type === 'string') visit(c) }
        else if (v && typeof v.type === 'string') visit(v)
      }
    }
    visit(ast.program)
    return inserts.length ? apply(code, inserts) : null
  }

  async function vue(code, id) {
    const sfc = (await load('vue/compiler-sfc')) || (await load('@vue/compiler-sfc'))
    if (!sfc) return null
    let descriptor
    try { descriptor = sfc.parse(code, { filename: id }).descriptor } catch { return null }
    const tpl = descriptor && descriptor.template
    if (!tpl || !tpl.ast || (tpl.lang && tpl.lang !== 'html')) return null
    const file = rel(id)
    const inserts = []
    const visit = (node) => {
      if (node.type === 1 && node.tagType === 0 && !SKIP_TAGS.has(node.tag)) {
        let start = node.loc.start.offset
        // template AST offsets are file-wide in Vue >= 3.4; older versions count from the template block
        if (code.slice(start, start + node.tag.length + 1) !== '<' + node.tag) start += tpl.loc.start.offset
        if (code.slice(start, start + node.tag.length + 1) === '<' + node.tag
            && !(node.props || []).some(p => p.name === ATTR)) {
          inserts.push({ at: start + 1 + node.tag.length, text: attr(file, code, start) })
        }
      }
      for (const c of node.children || []) if (c && typeof c === 'object') visit(c)
      if (node.branches) for (const b of node.branches) visit(b)
    }
    for (const c of tpl.ast.children || []) visit(c)
    return inserts.length ? apply(code, inserts) : null
  }

  async function svelte(code, id) {
    const compiler = await load('svelte/compiler')
    if (!compiler) return null
    let ast
    try { ast = compiler.parse(code) } catch { return null }
    const file = rel(id)
    const inserts = []
    const visit = (node) => {
      if (!node || typeof node !== 'object') return
      if ((node.type === 'Element' || node.type === 'RegularElement') && typeof node.name === 'string'
          && /^[a-z]/.test(node.name) && !node.name.includes(':') && !SKIP_TAGS.has(node.name)
          && code.startsWith('<' + node.name, node.start)) {
        inserts.push({ at: node.start + 1 + node.name.length, text: attr(file, code, node.start) })
      }
      for (const key of ['children', 'nodes', 'else', 'then', 'catch', 'pending', 'body', 'fragment', 'consequent', 'alternate']) {
        const v = node[key]
        if (Array.isArray(v)) v.forEach(visit)
        else if (v && typeof v === 'object') visit(v)
      }
    }
    visit(ast.html || ast.fragment)
    return inserts.length ? apply(code, inserts) : null
  }

  function html(code, file) {
    const inserts = []
    const re = /<!--[\s\S]*?-->|<(script|style|textarea)\b[\s\S]*?<\/\1\s*>|<([a-zA-Z][\w-]*)/g
    let m
    while ((m = re.exec(code))) {
      const name = m[2]
      if (!name || SKIP_TAGS.has(name.toLowerCase())) continue
      inserts.push({ at: m.index + 1 + name.length, text: attr(file, code, m.index) })
    }
    return inserts.length ? apply(code, inserts) : code
  }

  return {
    name: 'cluadex-locator',
    apply: 'serve',
    enforce: 'pre',
    configResolved(config) {
      root = config.root || process.cwd()
    },
    async transform(code, id) {
      if (id.includes('?') || id.includes('/node_modules/') || id.includes('\\node_modules\\') || id.startsWith('\0')) return null
      if (options.exclude && options.exclude.test(id)) return null
      let out = null
      try {
        if (id.endsWith('.vue')) out = await vue(code, id)
        else if (id.endsWith('.svelte')) out = await svelte(code, id)
        else if (JSX_RE.test(id)) out = await jsx(code, id)
      } catch { out = null }
      return out == null ? null : { code: out, map: null }
    },
    transformIndexHtml: {
      order: 'pre',
      handler(code, ctx) {
        try {
          // "/" arrives as the folder, not the file: name the page that is really served
          let file = ctx.filename ? rel(ctx.filename) : ''
          if (!/\.html?$/i.test(file)) file = path.posix.join(file, 'index.html')
          return html(code, file)
        } catch { return code }
      },
    },
  }
}
