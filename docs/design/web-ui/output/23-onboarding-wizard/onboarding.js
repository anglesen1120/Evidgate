const list = document.getElementById('onb-nav');
if (!list) throw new Error('Missing onboarding navigator');
const buttons = Array.from(list.querySelectorAll('button[data-onb-step]'));
const heading = document.getElementById('onb-step-title');
const back = document.getElementById('onb-back');
const next = document.getElementById('onb-next');
const toggle = document.getElementById('onb-step-toggle');
let expanded = false;
const setup = new URLSearchParams(window.location.search).get('setup') ?? 'project';
const confirmed = { empty: 0, project: 1, connected: 2, run: 3, evidence: 4, enabled: 5 };
const completed = Object.hasOwn(confirmed, setup) ? confirmed[setup] : confirmed.project;
const required = Math.min(completed + 1, 5);
let viewed = required;

function setCopy(element, en, vi) {
  if (!element) return;
  element.setAttribute('data-en', en);
  element.setAttribute('data-vi', vi);
  element.textContent = document.documentElement.lang === 'en' ? en : vi;
}

function renderSummary() {
  const viewedLabel = buttons[viewed - 1].querySelector('[data-onb-label]');
  const nextLabel = buttons[required - 1].querySelector('[data-onb-label]');
  setCopy(document.getElementById('onb-viewed-summary'),
    `Viewing ${viewed} of 5: ${viewedLabel.dataset.en}`,
    `Đang xem bước ${viewed}/5: ${viewedLabel.dataset.vi}`);
  setCopy(document.getElementById('onb-next-summary'),
    completed === 5 ? 'All 5 steps complete.' : `Next required ${required} of 5: ${nextLabel.dataset.en}.`,
    completed === 5 ? 'Đã hoàn thành tất cả 5 bước.' : `Bước cần làm ${required}/5: ${nextLabel.dataset.vi}.`);
  document.getElementById('onb-project-context').hidden = completed === 0;
}

function setExpanded(value) {
  expanded = value;
  toggle.setAttribute('aria-expanded', String(value));
  list.dataset.expanded = String(value);
  document.getElementById('onb-locked-reason').dataset.expanded = String(value);
  setCopy(document.getElementById('onb-toggle-label'),
    value ? 'Hide steps' : 'Show all steps',
    value ? 'Thu gọn các bước' : 'Xem tất cả các bước');
}

function renderNavigation() {
  for (const button of buttons) {
    const step = Number(button.dataset.onbStep);
    const status = step <= completed ? 'complete' : step === required ? 'required' : 'upcoming';
    const blocked = step > required;
    button.setAttribute('aria-disabled', String(blocked));
    if (blocked) button.setAttribute('aria-describedby', 'onb-locked-reason');
    else button.removeAttribute('aria-describedby');
    if (step === viewed) button.setAttribute('aria-current', 'step');
    else button.removeAttribute('aria-current');
    button.querySelector('[data-onb-viewing]').hidden = step !== viewed;
    for (const icon of button.querySelectorAll('[data-onb-icon]')) {
      icon.hidden = icon.dataset.onbIcon !== status;
    }
    const statuses = {
      complete: ['Complete', 'Đã hoàn thành'],
      required: ['Next required', 'Bước cần làm'],
      upcoming: ['Upcoming', 'Sắp tới'],
    };
    setCopy(button.querySelector('[data-onb-status]'), ...statuses[status]);
  }
}

function renderProgress() {
  const count = document.getElementById('onb-count');
  const progress = document.getElementById('onb-progress');
  setCopy(count, `${completed} of 5 steps complete`, `Đã hoàn thành ${completed}/5 bước`);
  progress.setAttribute('aria-valuenow', String(completed));
  progress.setAttribute('aria-valuetext', count.textContent);
  document.getElementById('onb-progress-fill').style.setProperty('--pct', `${completed / 5 * 100}%`);
  document.getElementById('onb-locked-reason').hidden = required === 5;
  document.getElementById('onb-binding-count').textContent = completed >= 2 ? '1' : '0';
  setCopy(document.getElementById('onb-health'),
    completed >= 2 ? 'Connected' : 'Awaiting CI', completed >= 2 ? 'Đã kết nối' : 'Chờ CI');
}

function renderBody() {
  const selected = buttons.find((button) => Number(button.dataset.onbStep) === viewed);
  const label = selected.querySelector('[data-onb-label]');
  setCopy(heading, label.getAttribute('data-en'), label.getAttribute('data-vi'));
  for (const panel of document.querySelectorAll('[data-onb-panel]')) {
    panel.hidden = Number(panel.dataset.onbPanel) !== viewed;
  }
  document.querySelector('[data-onb-project-ready]').hidden = completed === 0;
  document.querySelector('[data-onb-project-empty]').hidden = completed > 0;
  document.querySelector('[data-onb-ledger-ready]').hidden = completed === 0;
  document.querySelector('[data-onb-ledger-empty]').hidden = completed > 0;
  document.querySelector('[data-onb-enabled]').hidden = completed !== 5;
  document.querySelector('[data-onb-preview]').hidden = completed === 5;
  back.disabled = viewed === 1;
  next.disabled = viewed === 5 || viewed > completed;
  const reason = document.getElementById('onb-next-reason');
  reason.hidden = !next.disabled;
  setCopy(reason,
    viewed === 5 ? 'There is no next setup step.' : 'Complete this step before continuing.',
    viewed === 5 ? 'Không còn bước thiết lập tiếp theo.' : 'Hoàn tất bước này trước khi tiếp tục.');
  if (next.disabled) next.setAttribute('aria-describedby', reason.id);
  else next.removeAttribute('aria-describedby');
}

function selectStep(step) {
  if (step < 1 || step > required) return;
  viewed = step;
  renderNavigation();
  renderBody();
  renderSummary();
  setExpanded(false);
  heading.focus();
}

function moveFocus(event) {
  if (event.altKey || event.ctrlKey || event.metaKey) return;
  if (event.key === 'Escape' && expanded && window.matchMedia('(max-width: 767px)').matches) {
    event.preventDefault();
    setExpanded(false);
    toggle.focus();
    return;
  }
  const index = buttons.indexOf(event.target);
  if (index < 0) return;
  const offsets = { ArrowRight: 1, ArrowDown: 1, ArrowLeft: -1, ArrowUp: -1 };
  let target;
  if (event.key === 'Home') target = 0;
  else if (event.key === 'End') target = buttons.length - 1;
  else if (Object.hasOwn(offsets, event.key)) target = (index + offsets[event.key] + buttons.length) % buttons.length;
  else return;
  event.preventDefault();
  buttons[target].focus();
}

for (const button of buttons) {
  button.addEventListener('click', () => selectStep(Number(button.dataset.onbStep)));
}
list.addEventListener('keydown', moveFocus);
toggle.addEventListener('click', () => setExpanded(!expanded));
toggle.addEventListener('keydown', moveFocus);
window.matchMedia('(max-width: 767px)').addEventListener('change', (event) => {
  if (event.matches && !expanded && list.contains(document.activeElement)) toggle.focus();
});
back.addEventListener('click', () => selectStep(viewed - 1));
next.addEventListener('click', () => { if (viewed <= completed) selectStep(viewed + 1); });
document.addEventListener('eg:locale', () => {
  renderNavigation();
  renderProgress();
  renderBody();
  renderSummary();
  setExpanded(expanded);
});
renderNavigation();
renderProgress();
renderBody();
renderSummary();
setExpanded(false);
