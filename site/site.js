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
const calm = matchMedia('(prefers-reduced-motion: reduce)');

function showMode(button) {
  if (button.getAttribute('aria-pressed') === 'true') {
    return;
  }

  const mode = modes[button.dataset.mode];
  const output = document.getElementById('try-output');
  press(modeButtons, button);
  trySection.dataset.mode = button.dataset.mode;
  document.getElementById('try-word').textContent = mode.word;
  document.getElementById('try-label').textContent = mode.label;
  output.innerHTML = mode.html;
  if (!calm.matches) {
    output.animate([{ opacity: 0, transform: 'translateY(6px)' }, { opacity: 1, transform: 'none' }], { duration: 220, easing: 'ease-out' });
  }
}

// The section is pinned for several screens, and each equal slice of that
// scroll shows the next action.
function scrollRange() {
  const top = trySection.getBoundingClientRect().top + scrollY;
  return { top, length: Math.max(1, trySection.offsetHeight - innerHeight) };
}

const steps = [...trySection.querySelectorAll('.try-steps span')];
const clamp = value => Math.min(Math.max(value, 0), 1);

function followScroll() {
  const { top, length } = scrollRange();
  const progress = clamp((scrollY - top) / length);
  const count = modeButtons.length;
  showMode(modeButtons[Math.min(Math.floor(progress * count), count - 1)]);
  steps.forEach((step, index) => step.style.setProperty('--fill', clamp(progress * count - index)));
}

addEventListener('scroll', followScroll, { passive: true });
followScroll();

// A click moves to that action's slice, so scrolling on carries on from there.
modeButtons.forEach((button, index) => button.addEventListener('click', () => {
  showMode(button);
  const { top, length } = scrollRange();
  scrollTo({ top: top + length * (index + 0.5) / modeButtons.length });
}));

const saved = {
  email: { label: 'Client email', html: 'Hello, could you please send me the report by Friday? I need it for the client meeting. Thank you.' },
  update: { label: 'Team update', html: "Quick ask: could someone send me the report by Friday? It's for the client meeting." },
  bullets: { label: 'Bullet points', html: '• Report needed by Friday<br>• For the client meeting' }
};

const savedButtons = [...document.querySelectorAll('[data-saved]')];
savedButtons.forEach(button => button.addEventListener('click', () => {
  const choice = saved[button.dataset.saved];
  press(savedButtons, button);
  document.getElementById('saved-label').textContent = choice.label;
  document.getElementById('saved-output').innerHTML = choice.html;
}));

const howButtons = [...document.querySelectorAll('[data-how]')];
howButtons.forEach(button => button.addEventListener('click', () => {
  press(howButtons, button);
  document.getElementById('how-note').textContent = notes[button.dataset.how];
}));

// Topic links open their question, and show which one is open.
const questions = [...document.querySelectorAll('.drawer details')];
const topicLinks = [...document.querySelectorAll('.faq-topics a')];

function markOpenTopic() {
  topicLinks.forEach(link => link.setAttribute('aria-current', String(document.querySelector(link.hash).open)));
}

questions.forEach(question => question.addEventListener('toggle', markOpenTopic));
topicLinks.forEach(link => link.addEventListener('click', () => {
  document.querySelector(link.hash).open = true;
}));
markOpenTopic();

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

// The installer's name includes its version, so ask GitHub for the newest one.
// Without this, the buttons still reach the latest release page.
fetch('https://api.github.com/repos/KrishPatel10/ParaPhraser/releases/latest')
  .then(response => response.ok ? response.json() : null)
  .then(release => {
    const setup = release?.assets.find(asset => /-Setup-.*\.exe$/.test(asset.name));
    if (setup) {
      document.querySelectorAll('[data-download]').forEach(link => { link.href = setup.browser_download_url; });
    }
  })
  .catch(() => {});
