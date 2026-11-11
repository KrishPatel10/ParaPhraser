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
      document.getElementById('end-version').textContent = `Version ${release.tag_name.replace(/^v/, '')} · Free and open source`;
    }
  })
  .catch(() => {});

// The hero window is a small working demo of the app.
const presets = {
  clear: {
    instruction: 'Rewrite this clearly and naturally.',
    text: 'Could you send me the report by Friday? I need it for the client meeting.',
    changes: '<del>hey can u</del> <ins>Could you</ins> send me the report by <del>friday, i</del> <ins>Friday? I</ins> need it for the client meeting<ins>.</ins>'
  },
  polite: {
    instruction: 'Make it warm and polite.',
    text: 'Hi! Would you mind sending me the report by Friday? I need it for the client meeting. Thank you!',
    changes: '<del>hey can u send</del> <ins>Hi! Would you mind sending</ins> me the report by <del>friday, i</del> <ins>Friday? I</ins> need it for the client meeting<ins>. Thank you!</ins>'
  },
  short: {
    instruction: 'Make it as short as possible.',
    text: 'Please send the report by Friday for the client meeting.',
    changes: '<del>hey can u</del> <ins>Please</ins> send <del>me</del> the report by <del>friday, i need it for</del> <ins>Friday for</ins> the client meeting<ins>.</ins>'
  }
};

const hero = {
  window: document.querySelector('.hero-picture .win'),
  original: document.getElementById('hero-original'),
  originalPaper: document.querySelector('.paper.original'),
  source: document.getElementById('hero-source'),
  instruction: document.getElementById('hero-instruction'),
  suggestion: document.getElementById('hero-suggestion'),
  status: document.getElementById('hero-status'),
  changes: document.getElementById('hero-changes'),
  copy: document.getElementById('hero-copy'),
  replace: document.getElementById('hero-replace'),
  presetButtons: [...document.querySelectorAll('[data-preset]')]
};
const originalText = hero.original.textContent;
let current = presets.clear;
let run = 0;

// A changing label reads better through a short blur than a hard swap.
function swapText(element, text) {
  element.textContent = text;
  if (!calm.matches) {
    element.animate([{ filter: 'blur(2px)', opacity: 0.4 }, { filter: 'none', opacity: 1 }], { duration: 200, easing: 'ease-out' });
  }
}

function setReplaced(replaced) {
  hero.originalPaper.classList.toggle('replaced', replaced);
  swapText(hero.original, replaced ? current.text : originalText);
  swapText(hero.source, replaced ? 'Replaced in Teams' : 'Original · Teams');
  swapText(hero.replace, replaced ? 'Undo' : 'Replace');
}

// Words arrive one at a time, the way the app streams a suggestion.
async function generate() {
  const id = ++run;
  if (hero.originalPaper.classList.contains('replaced')) {
    setReplaced(false);
  }

  hero.changes.setAttribute('aria-pressed', 'false');
  hero.changes.textContent = 'Show changes';
  [hero.changes, hero.copy, hero.replace].forEach(button => { button.disabled = true; });
  hero.status.textContent = '';
  hero.suggestion.setAttribute('aria-busy', 'true');
  hero.window.classList.add('streaming');

  const words = current.text.split(' ');
  hero.suggestion.textContent = '';
  for (let index = 0; index < words.length; index++) {
    if (!calm.matches) {
      await new Promise(resolve => setTimeout(resolve, 45));
    }

    if (id !== run) {
      return;
    }

    hero.suggestion.textContent = words.slice(0, index + 1).join(' ');
  }

  hero.window.classList.remove('streaming');
  hero.suggestion.setAttribute('aria-busy', 'false');
  hero.status.textContent = '· 1.2 s';
  [hero.changes, hero.copy, hero.replace].forEach(button => { button.disabled = false; });
}

hero.presetButtons.forEach(button => button.addEventListener('click', () => {
  if (button.getAttribute('aria-pressed') === 'true') {
    return;
  }

  press(hero.presetButtons, button);
  current = presets[button.dataset.preset];
  swapText(hero.instruction, current.instruction);
  generate();
}));

document.getElementById('hero-generate').addEventListener('click', generate);

hero.changes.addEventListener('click', () => {
  const showing = hero.changes.getAttribute('aria-pressed') !== 'true';
  hero.changes.setAttribute('aria-pressed', String(showing));
  hero.changes.textContent = showing ? 'Hide changes' : 'Show changes';
  if (showing) {
    hero.suggestion.innerHTML = current.changes;
  } else {
    hero.suggestion.textContent = current.text;
  }
});

let copiedTimer;
hero.copy.addEventListener('click', () => {
  navigator.clipboard?.writeText(current.text).catch(() => {});
  swapText(hero.copy, 'Copied');
  clearTimeout(copiedTimer);
  copiedTimer = setTimeout(() => swapText(hero.copy, 'Copy'), 1400);
});

hero.replace.addEventListener('click', () => setReplaced(!hero.originalPaper.classList.contains('replaced')));

// Play the first suggestion once the window has slid in.
setTimeout(generate, calm.matches ? 0 : 1300);
