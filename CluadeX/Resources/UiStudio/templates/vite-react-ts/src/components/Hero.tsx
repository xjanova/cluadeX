import { useState } from 'react'

export default function Hero() {
  const [count, setCount] = useState(0)

  return (
    <section className="hero">
      <span className="pill">React 19 · TypeScript · Vite</span>
      <h1 className="hero-title">Design it live, ship it fast.</h1>
      <p className="hero-lead">
        Edit any element in CluadeX UI Studio — the change lands in the exact component that renders it.
      </p>
      <div className="hero-actions">
        <button className="btn btn-primary" onClick={() => setCount((c) => c + 1)}>
          Clicked {count} {count === 1 ? 'time' : 'times'}
        </button>
        <a className="btn btn-ghost" href="https://react.dev" target="_blank" rel="noreferrer">
          React docs
        </a>
      </div>
    </section>
  )
}
