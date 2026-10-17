// Mobile menu
const toggle = document.getElementById('navToggle');
const links = document.getElementById('navLinks');
toggle.addEventListener('click', () => links.classList.toggle('open'));
links.addEventListener('click', (e) => { if (e.target.tagName === 'A') links.classList.remove('open'); });

// Monthly / yearly pricing
document.querySelectorAll('.billing-btn').forEach((btn) => {
  btn.addEventListener('click', () => {
    document.querySelectorAll('.billing-btn').forEach((b) => b.classList.toggle('active', b === btn));
    const period = btn.dataset.period;
    document.querySelectorAll('.amount').forEach((el) => {
      el.textContent = '$' + el.dataset[period];
    });
  });
});

document.getElementById('year').textContent = new Date().getFullYear();
