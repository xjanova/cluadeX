// Sidebar filter: hides pages that don't match and groups left empty
const filter = document.getElementById('filter');
filter.addEventListener('input', () => {
  const q = filter.value.trim().toLowerCase();
  let group = null, groupHasMatch = false;
  const finish = () => { if (group) group.classList.toggle('hidden', !groupHasMatch); };
  for (const el of document.getElementById('tree').children) {
    if (el.classList.contains('group')) { finish(); group = el; groupHasMatch = false; continue; }
    const match = !q || el.textContent.toLowerCase().includes(q);
    el.classList.toggle('hidden', !match);
    groupHasMatch = groupHasMatch || match;
  }
  finish();
});

// Copy buttons on code blocks
document.querySelectorAll('.copy').forEach((btn) => btn.addEventListener('click', async () => {
  const code = btn.parentElement.querySelector('code').innerText;
  try { await navigator.clipboard.writeText(code); btn.textContent = 'Copied!'; }
  catch { btn.textContent = 'Press Ctrl+C'; }
  setTimeout(() => (btn.textContent = 'Copy'), 1400);
}));

// "On this page" follows the scroll position
const links = [...document.querySelectorAll('#toc a')];
const sections = links.map((a) => document.querySelector(a.getAttribute('href'))).filter(Boolean);
const observer = new IntersectionObserver((entries) => {
  for (const e of entries) {
    if (!e.isIntersecting) continue;
    links.forEach((a) => a.classList.toggle('active', a.getAttribute('href') === '#' + e.target.id));
  }
}, { rootMargin: '-70px 0px -70% 0px' });
sections.forEach((s) => observer.observe(s));

document.getElementById('updated').textContent = new Date().toLocaleDateString();
