import { useEffect, useState } from 'react'

export default function Modal({ open, onClose }) {
  const [email, setEmail] = useState('')
  const [sent, setSent] = useState(false)

  useEffect(() => {
    if (!open) return
    const onKey = (e) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open, onClose])

  useEffect(() => {
    if (!open) { setSent(false); setEmail('') }
  }, [open])

  if (!open) return null

  return (
    <div className="fixed inset-0 z-20 grid place-items-center bg-slate-900/50 p-4" onClick={onClose}>
      <div className="w-full max-w-md rounded-3xl bg-white p-8 shadow-2xl" onClick={(e) => e.stopPropagation()} role="dialog" aria-modal="true">
        {sent ? (
          <div className="text-center">
            <p className="text-4xl">🎉</p>
            <h2 className="mt-4 text-2xl font-bold">Check your inbox</h2>
            <p className="mt-2 text-slate-600">We sent a sign-up link to {email}.</p>
            <button onClick={onClose} className="mt-6 w-full rounded-xl bg-slate-900 py-3 font-semibold text-white">Done</button>
          </div>
        ) : (
          <form onSubmit={(e) => { e.preventDefault(); if (email.includes('@')) setSent(true) }}>
            <h2 className="text-2xl font-bold">Create your account</h2>
            <p className="mt-2 text-slate-600">Free for 14 days, no card required.</p>
            <input
              type="email"
              required
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              placeholder="you@company.com"
              className="mt-6 w-full rounded-xl border border-slate-300 px-4 py-3 outline-none focus:border-sky-500 focus:ring-4 focus:ring-sky-100"
            />
            <button type="submit" className="mt-4 w-full rounded-xl bg-sky-500 py-3 font-semibold text-white hover:bg-sky-600">
              Continue
            </button>
            <button type="button" onClick={onClose} className="mt-2 w-full py-2 text-sm text-slate-500">Cancel</button>
          </form>
        )}
      </div>
    </div>
  )
}
