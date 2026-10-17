// ── data ──────────────────────────────────────────────────────────────────
const names = ['Somchai P.', 'Anna Kim', 'Maria Lopez', 'Ken Watanabe', 'Priya Shah', 'Lucas Martin', 'Nok S.', 'Omar Ali'];
const statuses = ['paid', 'pending', 'shipped', 'refunded'];
const orders = Array.from({ length: 24 }, (_, i) => ({
  id: 1043 - i,
  customer: names[i % names.length],
  date: new Date(Date.now() - i * 86400000 * 1.3),
  status: statuses[(i * 7) % 4 === 3 && i % 5 !== 0 ? 0 : (i * 7) % 4],
  total: Math.round((40 + ((i * 37) % 260) + (i % 3) * 19.5) * 100) / 100,
}));

// ── orders table: search + sort ───────────────────────────────────────────
const tbody = document.querySelector('#orders tbody');
const search = document.getElementById('search');
const rowCount = document.getElementById('rowCount');
let sortKey = 'id', sortDir = -1;

function render() {
  const q = search.value.trim().toLowerCase();
  const rows = orders
    .filter((o) => !q || `#${o.id} ${o.customer} ${o.status}`.toLowerCase().includes(q))
    .sort((a, b) => (a[sortKey] > b[sortKey] ? 1 : a[sortKey] < b[sortKey] ? -1 : 0) * sortDir);
  tbody.innerHTML = rows.map((o) => `
    <tr>
      <td><b>#${o.id}</b></td>
      <td>${o.customer}</td>
      <td>${o.date.toLocaleDateString()}</td>
      <td><span class="status ${o.status}">${o.status}</span></td>
      <td class="num">$${o.total.toFixed(2)}</td>
    </tr>`).join('');
  rowCount.textContent = `${rows.length} of ${orders.length}`;
  document.querySelectorAll('#orders th').forEach((th) => {
    th.classList.toggle('sorted-asc', th.dataset.key === sortKey && sortDir === 1);
    th.classList.toggle('sorted-desc', th.dataset.key === sortKey && sortDir === -1);
  });
}

document.querySelectorAll('#orders th').forEach((th) => th.addEventListener('click', () => {
  if (sortKey === th.dataset.key) sortDir = -sortDir; else { sortKey = th.dataset.key; sortDir = 1; }
  render();
}));
search.addEventListener('input', render);
render();

// ── revenue chart (plain SVG, no library) ─────────────────────────────────
const svg = document.getElementById('chart');
function series(n, seed) {
  return Array.from({ length: n }, (_, i) => 40 + 30 * Math.sin((i + seed) / 3) + i * 2 + ((i * seed) % 7) * 3);
}
function drawChart(days) {
  const n = days <= 7 ? 7 : days <= 30 ? 15 : 30;
  const a = series(n, 3), b = series(n, 7).map((v) => v * 0.8);
  const W = 600, H = 240, pad = 10;
  const max = Math.max(...a, ...b) * 1.1;
  const pt = (v, i) => [pad + (i * (W - pad * 2)) / (n - 1), H - pad - (v / max) * (H - pad * 2)];
  const line = (s) => s.map((v, i) => pt(v, i).join(',')).join(' ');
  const area = `M${pt(a[0], 0)} L${a.map((v, i) => pt(v, i).join(',')).join(' L')} L${W - pad},${H - pad} L${pad},${H - pad} Z`;
  const grid = [0.25, 0.5, 0.75].map((f) => `<line x1="0" x2="${W}" y1="${H * f}" y2="${H * f}" stroke="var(--line)" stroke-dasharray="4 4"/>`).join('');
  svg.innerHTML = `
    <defs><linearGradient id="g" x1="0" x2="0" y1="0" y2="1">
      <stop offset="0" stop-color="#4f46e5" stop-opacity=".25"/><stop offset="1" stop-color="#4f46e5" stop-opacity="0"/>
    </linearGradient></defs>
    ${grid}
    <polyline points="${line(b)}" fill="none" stroke="var(--line)" stroke-width="2.5"/>
    <path d="${area}" fill="url(#g)"/>
    <polyline points="${line(a)}" fill="none" stroke="#4f46e5" stroke-width="3" stroke-linejoin="round"/>`;
}
const range = document.getElementById('range');
range.addEventListener('change', () => drawChart(+range.value));
drawChart(+range.value);

// ── theme + mobile menu ───────────────────────────────────────────────────
const root = document.documentElement;
const saved = localStorage.getItem('theme');
if (saved) root.dataset.theme = saved;
document.getElementById('themeBtn').addEventListener('click', () => {
  root.dataset.theme = root.dataset.theme === 'dark' ? 'light' : 'dark';
  localStorage.setItem('theme', root.dataset.theme);
});
document.getElementById('menuBtn').addEventListener('click', () => document.getElementById('sidebar').classList.toggle('open'));
