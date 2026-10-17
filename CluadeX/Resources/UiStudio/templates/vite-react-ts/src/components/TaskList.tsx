import { useEffect, useState, type FormEvent } from 'react'

type Task = { id: number; text: string; done: boolean }

function load(): Task[] {
  try {
    const raw = JSON.parse(localStorage.getItem('tasks') ?? '[]')
    return Array.isArray(raw) ? raw : []
  } catch {
    return []
  }
}

export default function TaskList() {
  const [tasks, setTasks] = useState<Task[]>(load)
  const [text, setText] = useState('')

  useEffect(() => localStorage.setItem('tasks', JSON.stringify(tasks)), [tasks])

  function add(e: FormEvent) {
    e.preventDefault()
    const value = text.trim()
    if (!value) return
    setTasks((t) => [...t, { id: Date.now(), text: value, done: false }])
    setText('')
  }

  const toggle = (id: number) => setTasks((t) => t.map((x) => (x.id === id ? { ...x, done: !x.done } : x)))
  const remove = (id: number) => setTasks((t) => t.filter((x) => x.id !== id))
  const left = tasks.filter((t) => !t.done).length

  return (
    <section className="panel" id="tasks">
      <div className="panel-head">
        <h2 className="panel-title">Tasks</h2>
        <span className="muted">{left} left</span>
      </div>
      <form className="task-form" onSubmit={add}>
        <input className="input" value={text} onChange={(e) => setText(e.target.value)} placeholder="What needs doing?" />
        <button className="btn btn-primary" type="submit">Add</button>
      </form>
      {tasks.length === 0 ? (
        <p className="empty">Nothing yet — add your first task.</p>
      ) : (
        <ul className="tasks">
          {tasks.map((t) => (
            <li key={t.id} className={t.done ? 'task done' : 'task'}>
              <label className="task-label">
                <input type="checkbox" checked={t.done} onChange={() => toggle(t.id)} />
                <span>{t.text}</span>
              </label>
              <button className="icon-btn" onClick={() => remove(t.id)} aria-label="Delete task">✕</button>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}
