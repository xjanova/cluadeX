import { useState } from 'react'

export default function Navbar({ onSignup }) {
  const [menu, setMenu] = useState(false)

  return (
    <header className="sticky top-0 z-10 border-b border-slate-200 bg-white/80 backdrop-blur">
      <div className="mx-auto flex h-16 max-w-6xl items-center gap-8 px-6">
        <a href="/" className="text-lg font-extrabold">
          <span className="text-sky-500">●</span> {{PROJECT_TITLE}}
        </a>
        <nav className="hidden gap-6 text-sm font-medium text-slate-600 md:flex">
          <a href="#pricing" className="hover:text-slate-900">Pricing</a>
          <a href="#" className="hover:text-slate-900">Customers</a>
          <a href="#" className="hover:text-slate-900">Docs</a>
        </nav>
        <div className="ml-auto hidden items-center gap-3 md:flex">
          <a href="#" className="text-sm font-semibold text-slate-600">Sign in</a>
          <button onClick={onSignup} className="rounded-lg bg-sky-500 px-4 py-2 text-sm font-semibold text-white hover:bg-sky-600">
            Sign up
          </button>
        </div>
        <button className="ml-auto text-2xl md:hidden" onClick={() => setMenu((m) => !m)} aria-label="Menu">☰</button>
      </div>
      {menu && (
        <nav className="flex flex-col gap-3 border-t border-slate-200 px-6 py-4 text-sm font-medium md:hidden">
          <a href="#pricing">Pricing</a>
          <a href="#">Customers</a>
          <a href="#">Docs</a>
          <button onClick={onSignup} className="rounded-lg bg-sky-500 px-4 py-2 font-semibold text-white">Sign up</button>
        </nav>
      )}
    </header>
  )
}
