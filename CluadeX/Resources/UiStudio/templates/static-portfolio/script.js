// Project filters
const chips = document.querySelectorAll('#filters .chip');
chips.forEach((chip) => chip.addEventListener('click', () => {
  chips.forEach((c) => c.classList.toggle('active', c === chip));
  const f = chip.dataset.filter;
  document.querySelectorAll('.project').forEach((p) => p.classList.toggle('hidden', f !== 'all' && p.dataset.cat !== f));
}));

// Contact form validation (no backend — wire it to your API or a form service)
const form = document.getElementById('contactForm');
const status = document.getElementById('formStatus');
form.addEventListener('submit', (e) => {
  e.preventDefault();
  let ok = true;
  for (const field of form.querySelectorAll('input, textarea')) {
    const valid = field.checkValidity() && field.value.trim() !== '';
    field.classList.toggle('invalid', !valid);
    ok = ok && valid;
  }
  status.className = 'form-status ' + (ok ? 'ok' : 'err');
  status.textContent = ok ? 'Thanks! Your message is on its way.' : 'Please fill in every field with a valid email.';
  if (ok) form.reset();
});

document.getElementById('year').textContent = new Date().getFullYear();
