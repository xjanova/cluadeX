// Shared by index.html, register.html and forgot.html.
// No backend: replace the success branch with a call to your auth API.

// Show / hide password
document.querySelectorAll('.pw-toggle').forEach((btn) => {
  btn.addEventListener('click', () => {
    const input = btn.previousElementSibling;
    const show = input.type === 'password';
    input.type = show ? 'text' : 'password';
    btn.textContent = show ? 'Hide' : 'Show';
  });
});

// Password strength meter
document.querySelectorAll('[data-strength]').forEach((input) => {
  const field = input.closest('.field');
  const meter = field.querySelector('.meter');
  const label = field.querySelector('[data-strength-label]');
  const words = ['Use 8+ characters with a mix of letters, numbers & symbols.', 'Weak', 'Fair', 'Good', 'Strong'];
  input.addEventListener('input', () => {
    const v = input.value;
    let score = 0;
    if (v.length >= 8) score++;
    if (/[A-Z]/.test(v) && /[a-z]/.test(v)) score++;
    if (/\d/.test(v)) score++;
    if (/[^A-Za-z0-9]/.test(v) || v.length >= 14) score++;
    if (!v) score = 0;
    meter.dataset.level = score;
    label.textContent = words[score];
  });
});

// Validation
document.querySelectorAll('form[data-validate]').forEach((form) => {
  const msg = form.querySelector('.form-msg');
  const check = (input) => {
    const ok = input.checkValidity() && (input.type === 'checkbox' || input.value.trim() !== '' || !input.required);
    (input.closest('.field') || input.closest('.check')).classList.toggle('invalid', !ok);
    return ok;
  };
  form.querySelectorAll('input').forEach((i) => i.addEventListener('blur', () => { if (i.value) check(i); }));
  form.addEventListener('submit', (e) => {
    e.preventDefault();
    const inputs = [...form.querySelectorAll('input[required]')];
    const ok = inputs.map(check).every(Boolean);
    msg.className = 'form-msg' + (ok ? ' ok' : '');
    msg.textContent = ok ? (form.dataset.success || 'Looks good! Connect your auth API to finish.') : '';
    if (!ok) inputs.find((i) => !i.checkValidity())?.focus();
  });
});
