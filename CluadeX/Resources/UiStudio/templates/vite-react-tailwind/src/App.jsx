import { useState } from 'react'
import Navbar from './components/Navbar.jsx'
import Pricing from './components/Pricing.jsx'
import Modal from './components/Modal.jsx'

const stats = [
  { value: '12k+', label: 'Teams' },
  { value: '99.9%', label: 'Uptime' },
  { value: '4.9/5', label: 'Rating' },
  { value: '24/7', label: 'Support' },
]

export default function App() {
  const [open, setOpen] = useState(false)

  return (
    <div className="min-h-screen">
      <Navbar onSignup={() => setOpen(true)} />

      <section className="mx-auto max-w-5xl px-6 pb-16 pt-24 text-center">
        <span className="rounded-full bg-sky-100 px-3 py-1 text-sm font-semibold text-sky-700">Now in public beta</span>
        <h1 className="mt-6 text-5xl font-extrabold tracking-tight sm:text-6xl">
          Your team's work, <span className="bg-gradient-to-r from-sky-500 to-violet-600 bg-clip-text text-transparent">beautifully organised</span>
        </h1>
        <p className="mx-auto mt-6 max-w-2xl text-lg text-slate-600">
          {{PROJECT_TITLE}} brings projects, docs and chat together so everyone knows what to do next.
        </p>
        <div className="mt-10 flex flex-wrap justify-center gap-3">
          <button onClick={() => setOpen(true)} className="rounded-xl bg-slate-900 px-6 py-3 font-semibold text-white shadow-lg shadow-slate-900/20 hover:bg-slate-800">
            Get started free
          </button>
          <a href="#pricing" className="rounded-xl border border-slate-300 bg-white px-6 py-3 font-semibold hover:border-slate-400">
            See pricing
          </a>
        </div>
      </section>

      <section className="mx-auto grid max-w-5xl grid-cols-2 gap-4 px-6 sm:grid-cols-4">
        {stats.map((s) => (
          <div key={s.label} className="rounded-2xl border border-slate-200 bg-white p-6 text-center">
            <p className="text-3xl font-extrabold">{s.value}</p>
            <p className="mt-1 text-sm text-slate-500">{s.label}</p>
          </div>
        ))}
      </section>

      <Pricing onChoose={() => setOpen(true)} />

      <footer className="border-t border-slate-200 py-10 text-center text-sm text-slate-500">
        © {new Date().getFullYear()} {{PROJECT_TITLE}}
      </footer>

      <Modal open={open} onClose={() => setOpen(false)} />
    </div>
  )
}
