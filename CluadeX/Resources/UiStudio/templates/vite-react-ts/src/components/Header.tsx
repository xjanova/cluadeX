import { useEffect, useState } from 'react'

export default function Header() {
  const [dark, setDark] = useState(() => localStorage.getItem('theme') === 'dark')

  useEffect(() => {
    document.documentElement.dataset.theme = dark ? 'dark' : 'light'
    localStorage.setItem('theme', dark ? 'dark' : 'light')
  }, [dark])

  return (
    <header className="header">
      <div className="container header-inner">
        <a className="brand" href="/">
          <span className="brand-mark">◆</span>
          {{PROJECT_TITLE}}
        </a>
        <nav className="nav">
          <a href="#features">Features</a>
          <a href="#tasks">Tasks</a>
        </nav>
        <button className="theme-btn" onClick={() => setDark((d) => !d)} aria-label="Toggle theme">
          {dark ? '☀' : '☾'}
        </button>
      </div>
    </header>
  )
}
