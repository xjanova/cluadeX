type Props = {
  icon: string
  title: string
  text: string
}

export default function FeatureCard({ icon, title, text }: Props) {
  return (
    <article className="card">
      <div className="card-icon">{icon}</div>
      <h3 className="card-title">{title}</h3>
      <p className="card-text">{text}</p>
    </article>
  )
}
