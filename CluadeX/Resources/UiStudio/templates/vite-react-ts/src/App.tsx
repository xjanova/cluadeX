import Header from './components/Header'
import Hero from './components/Hero'
import FeatureCard from './components/FeatureCard'
import TaskList from './components/TaskList'

const features = [
  { icon: '⚡', title: 'Instant feedback', text: 'Vite reloads your changes in milliseconds.' },
  { icon: '🧩', title: 'Components', text: 'Small, typed pieces that are easy to reuse.' },
  { icon: '🎯', title: 'Click to source', text: 'Pick any element in UI Studio and land on its line.' },
]

export default function App() {
  return (
    <>
      <Header />
      <main className="container">
        <Hero />
        <section className="features" id="features">
          {features.map((f) => (
            <FeatureCard key={f.title} icon={f.icon} title={f.title} text={f.text} />
          ))}
        </section>
        <TaskList />
      </main>
      <footer className="footer">Built with Vite + React · {{PROJECT_TITLE}}</footer>
    </>
  )
}
