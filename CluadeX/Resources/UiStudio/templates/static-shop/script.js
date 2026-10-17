const products = [
  { id: 1, name: 'Linen throw', cat: 'home', price: 48, emoji: '🧣', bg: '#fde68a' },
  { id: 2, name: 'Ceramic mug', cat: 'kitchen', price: 18, emoji: '☕', bg: '#fecaca' },
  { id: 3, name: 'Camp lantern', cat: 'outdoor', price: 36, emoji: '🏮', bg: '#fed7aa' },
  { id: 4, name: 'Oak cutting board', cat: 'kitchen', price: 42, emoji: '🪵', bg: '#e7e5e4' },
  { id: 5, name: 'Wool slippers', cat: 'home', price: 54, emoji: '🧦', bg: '#ddd6fe' },
  { id: 6, name: 'Steel bottle', cat: 'outdoor', price: 24, emoji: '🧴', bg: '#bae6fd' },
  { id: 7, name: 'Plant pot', cat: 'home', price: 29, emoji: '🪴', bg: '#bbf7d0' },
  { id: 8, name: 'Pour-over set', cat: 'kitchen', price: 64, emoji: '🫖', bg: '#fef3c7' },
];

const money = (n) => '$' + n.toFixed(2);
const grid = document.getElementById('grid');
const sort = document.getElementById('sort');
const resultCount = document.getElementById('resultCount');
let category = 'all';

// ── catalogue ─────────────────────────────────────────────────────────────
function renderGrid() {
  let list = products.filter((p) => category === 'all' || p.cat === category);
  if (sort.value === 'price-asc') list = [...list].sort((a, b) => a.price - b.price);
  if (sort.value === 'price-desc') list = [...list].sort((a, b) => b.price - a.price);
  if (sort.value === 'name') list = [...list].sort((a, b) => a.name.localeCompare(b.name));
  grid.innerHTML = list.map((p) => `
    <article class="card">
      <div class="card-img" style="background:${p.bg}">${p.emoji}</div>
      <div class="card-body"><h3 class="card-name">${p.name}</h3><p class="card-cat">${p.cat}</p></div>
      <div class="card-foot"><span class="price">${money(p.price)}</span><button class="add" data-id="${p.id}">Add</button></div>
    </article>`).join('');
  resultCount.textContent = `(${list.length})`;
}

document.getElementById('cats').addEventListener('click', (e) => {
  const btn = e.target.closest('.cat');
  if (!btn) return;
  category = btn.dataset.cat;
  document.querySelectorAll('.cat').forEach((c) => c.classList.toggle('active', c === btn));
  renderGrid();
});
sort.addEventListener('change', renderGrid);

// ── cart (kept in localStorage) ───────────────────────────────────────────
let cart = {};
try { cart = JSON.parse(localStorage.getItem('cart') || '{}') || {}; } catch { cart = {}; }
const save = () => localStorage.setItem('cart', JSON.stringify(cart));

function renderCart() {
  const lines = document.getElementById('lines');
  const entries = Object.entries(cart).filter(([id, q]) => q > 0 && products.some((p) => p.id === +id));
  const count = entries.reduce((s, [, q]) => s + q, 0);
  const subtotal = entries.reduce((s, [id, q]) => s + products.find((p) => p.id === +id).price * q, 0);
  const shipping = subtotal === 0 || subtotal >= 50 ? 0 : 6;
  document.getElementById('cartCount').textContent = count;
  document.getElementById('subtotal').textContent = money(subtotal);
  document.getElementById('shipping').textContent = subtotal === 0 ? '—' : shipping === 0 ? 'Free' : money(shipping);
  document.getElementById('total').textContent = money(subtotal + shipping);
  document.getElementById('checkout').disabled = count === 0;
  lines.innerHTML = entries.length === 0
    ? '<li class="empty">Your cart is empty.</li>'
    : entries.map(([id, q]) => {
        const p = products.find((x) => x.id === +id);
        return `<li class="line">
          <span class="line-img">${p.emoji}</span>
          <div><b>${p.name}</b><div class="qty"><button data-dec="${id}">−</button>${q}<button data-inc="${id}">+</button></div></div>
          <b>${money(p.price * q)}</b>
        </li>`;
      }).join('');
}

grid.addEventListener('click', (e) => {
  const btn = e.target.closest('.add');
  if (!btn) return;
  cart[btn.dataset.id] = (cart[btn.dataset.id] || 0) + 1;
  save(); renderCart();
  btn.classList.add('added'); btn.textContent = 'Added ✓';
  setTimeout(() => { btn.classList.remove('added'); btn.textContent = 'Add'; }, 900);
});
document.getElementById('lines').addEventListener('click', (e) => {
  const inc = e.target.dataset.inc, dec = e.target.dataset.dec;
  if (inc) cart[inc]++;
  if (dec) cart[dec] = Math.max(0, cart[dec] - 1);
  if (inc || dec) { save(); renderCart(); }
});

const drawer = document.getElementById('drawer');
const scrim = document.getElementById('scrim');
const openCart = (open) => {
  drawer.classList.toggle('open', open);
  scrim.classList.toggle('show', open);
  drawer.setAttribute('aria-hidden', String(!open));
};
document.getElementById('cartBtn').addEventListener('click', () => openCart(true));
document.getElementById('closeCart').addEventListener('click', () => openCart(false));
scrim.addEventListener('click', () => openCart(false));
document.getElementById('checkout').addEventListener('click', () => {
  alert('Connect your payment provider here.');
});

document.getElementById('year').textContent = new Date().getFullYear();
renderGrid();
renderCart();
