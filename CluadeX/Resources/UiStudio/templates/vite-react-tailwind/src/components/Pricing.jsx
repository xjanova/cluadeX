const plans = [
  { name: 'Free', price: 0, perks: ['3 projects', 'Basic search', 'Community support'], featured: false },
  { name: 'Team', price: 12, perks: ['Unlimited projects', 'Advanced search', 'Priority support', 'Guest access'], featured: true },
  { name: 'Enterprise', price: 39, perks: ['SSO & SCIM', 'Audit log', 'Dedicated manager'], featured: false },
]

export default function Pricing({ onChoose }) {
  return (
    <section id="pricing" className="mx-auto max-w-6xl px-6 py-24">
      <h2 className="text-center text-4xl font-extrabold tracking-tight">Simple pricing</h2>
      <p className="mt-3 text-center text-slate-600">Start free. Upgrade when your team grows.</p>
      <div className="mt-12 grid gap-6 md:grid-cols-3">
        {plans.map((p) => (
          <div
            key={p.name}
            className={
              p.featured
                ? 'relative flex flex-col rounded-3xl bg-slate-900 p-8 text-white shadow-2xl'
                : 'flex flex-col rounded-3xl border border-slate-200 bg-white p-8'
            }
          >
            {p.featured && (
              <span className="absolute -top-3 left-8 rounded-full bg-sky-500 px-3 py-1 text-xs font-bold text-white">Popular</span>
            )}
            <h3 className="text-lg font-semibold">{p.name}</h3>
            <p className="mt-4">
              <span className="text-5xl font-extrabold">${p.price}</span>
              <span className="opacity-60"> /user/mo</span>
            </p>
            <ul className="mt-6 flex-1 space-y-3 text-sm">
              {p.perks.map((perk) => (
                <li key={perk}>✓ {perk}</li>
              ))}
            </ul>
            <button
              onClick={onChoose}
              className={
                p.featured
                  ? 'mt-8 rounded-xl bg-sky-500 py-3 font-semibold hover:bg-sky-400'
                  : 'mt-8 rounded-xl border border-slate-300 py-3 font-semibold hover:border-slate-400'
              }
            >
              Choose {p.name}
            </button>
          </div>
        ))}
      </div>
    </section>
  )
}
