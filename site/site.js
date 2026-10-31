const modes = {
  grammar: { word: 'Fix', label: 'Fixed', html: 'Hey, can you send me the report by Friday? I need it for the client meeting.' },
  rewrite: { word: 'Rewrite', label: 'Suggestion', html: 'Could you send me the report by Friday? I need it for the client meeting.' },
  shorten: { word: 'Shorten', label: 'Shorter', html: 'Please send the report by Friday for the client meeting.' },
  format: { word: 'Format', label: 'Same words, formatted', html: 'hey can u send me the report by <strong>friday</strong>, i need it for the <strong>client meeting</strong>' }
};

const notes = {
  offline: "You'll also need Ollama. It's free, and Settings links to it.",
  online: "You'll also need an API key from Anthropic, OpenAI, Google, or OpenRouter."
};

function press(buttons, active) {
  buttons.forEach(button => button.setAttribute('aria-pressed', String(button === active)));
}

const trySection = document.querySelector('.try');
const modeButtons = [...trySection.querySelectorAll('button')];
modeButtons.forEach(button => button.addEventListener('click', () => {
  const mode = modes[button.dataset.mode];
  press(modeButtons, button);
  trySection.dataset.mode = button.dataset.mode;
  document.getElementById('try-word').textContent = mode.word;
  document.getElementById('try-label').textContent = mode.label;
  document.getElementById('try-output').innerHTML = mode.html;
}));

const howButtons = [...document.querySelectorAll('[data-how]')];
howButtons.forEach(button => button.addEventListener('click', () => {
  press(howButtons, button);
  document.getElementById('how-note').textContent = notes[button.dataset.how];
}));

const reveals = document.querySelectorAll('.reveal');
if ('IntersectionObserver' in window) {
  const observer = new IntersectionObserver(entries => {
    for (const entry of entries) {
      if (entry.isIntersecting) {
        entry.target.classList.add('in');
        observer.unobserve(entry.target);
      }
    }
  }, { threshold: 0.12 });

  reveals.forEach(item => {
    const siblings = [...item.parentElement.children];
    item.style.setProperty('--delay', `${siblings.indexOf(item) * 70}ms`);
    observer.observe(item);
  });
} else {
  reveals.forEach(item => item.classList.add('in'));
}
