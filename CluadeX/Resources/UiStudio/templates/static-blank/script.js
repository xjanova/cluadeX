const button = document.getElementById('hello');
const output = document.getElementById('output');

button.addEventListener('click', () => {
  output.textContent = `Hello from {{PROJECT_TITLE}} — ${new Date().toLocaleTimeString()}`;
});
