/* =====================================================================
 * Einstellungen der Touch-Bedienung als Blatt: erreichbar unter Einstellungen -> Sitzung & Touch und im Menue der Sitzung. Die Einstellungen gehoeren app.js
 * (lokal je Geraet gemerkt, als Einstellung "touch" auf den Server gespiegelt); hier steht nur die Oberflaeche. Mit einer Probierflaeche, auf der man Halten,
 * Doppeltipp und halten und Zwei-Finger-Tipp ausprobiert, ohne dass etwas an eine Sitzung geht. Beschreibung: docs/TOUCH.md.
 * ===================================================================== */
import { t, applyDom } from './i18n.js';
import { showDialog } from './desktop-window.js';
import { TouchGestures, normalizeTouch, TOUCH_DEFAULTS, HOLD_MIN, HOLD_MAX } from './touch-gestures.js';
import { TouchUi, bindSurface } from './touch-ui.js';

let provider = null, dlg = null, tryCleanup = null;

/** app.js meldet, woher die Einstellungen kommen und wohin Aenderungen gehen: { get: () => cfg, set: (patch) => void }. */
export function bindTouchSettings(p) { provider = p; }

const seg = (name, options, value) => `<div class="tt-seg" role="radiogroup" data-name="${name}">${options.map(([v, label]) => `<button type="button" role="radio" aria-checked="${v === value}" data-v="${v}">${label}</button>`).join('')}</div>`;
const check = (name, label, on) => `<label class="tt-check"><input type="checkbox" data-name="${name}"${on ? ' checked' : ''}><span>${label}</span></label>`;

function render() {
  const c = provider.get(), tablet = c.mode === 'tablet', pad = c.mode === 'pad';
  dlg.innerHTML = `
    <h2>${t('touch.title')}</h2>
    <p class="tt-hint">${t('touch.intro')}</p>
    <div class="tt-try" id="ttTry"><span id="ttTryText">${c.mode === 'tablet' ? t('tab.tryHint') : t('touch.tryHint')}</span></div>
    <div class="tt-field"><span>${t('touch.mode')}</span>${seg('mode', [['tablet', t('tab.modeTablet')], ['direct', t('tab.modeMouse')], ['pad', t('touch.modePad')]], c.mode)}</div>
    ${tablet ? `<p class="tt-hint">${t('tab.rules')}</p>
    <div class="tt-field"><span>${t('tab.pinch')}</span>${seg('pinch', [['off', t('tab.pinchOff')], ['view', t('tab.pinchView')], ['size', t('tab.pinchSize')]], c.pinch)}</div>` : ''}
    <div class="tt-field"><span>${t('touch.right')}</span>${seg('rightClick', [['hold', t('touch.rightHold')], ['two', t('touch.rightTwo')], ['both', t('touch.rightBoth')]], c.rightClick)}</div>
    <div class="tt-field"><span>${t('touch.hold')}</span><div class="tt-range"><input type="range" data-name="holdMs" min="${HOLD_MIN}" max="${HOLD_MAX}" step="50" value="${c.holdMs}"><output>${t('touch.ms', { n: c.holdMs })}</output></div></div>
    <div class="tt-field"><span>${t('touch.scrollDir')}</span>${seg('scrollDir', [['natural', t('touch.dirNatural')], ['classic', t('touch.dirClassic')]], c.scrollDir)}</div>
    <div class="tt-field"><span>${t('touch.scrollSpeed')}</span>${seg('scrollSpeed', [['slow', t('touch.speedSlow')], ['normal', t('touch.speedNormal')], ['fast', t('touch.speedFast')]], c.scrollSpeed)}</div>
    ${pad ? `<div class="tt-field"><span>${t('touch.padSpeed')}</span><div class="tt-range"><input type="range" data-name="padSpeed" min="0.6" max="3" step="0.1" value="${c.padSpeed}"><output>${t('touch.times', { n: c.padSpeed.toFixed(1) })}</output></div></div>
    ${check('tapDrag', t('touch.tapDrag'), c.tapDrag)}` : ''}
    ${c.mode === 'direct' ? check('fingerDrag', t('touch.fingerDrag'), c.fingerDrag) : ''}
    ${check('vibrate', t('touch.vibrate'), c.vibrate)}
    ${check('clipChip', t('touch.clipChip'), c.clipChip)}
    <div class="row">
      <button type="button" class="ghost" data-act="reset">${t('touch.reset')}</button>
      <button type="button" class="ghost close" data-act="close">${t('common.close')}</button>
    </div>`;
  wire();
}

function wire() {
  const change = (patch) => { provider.set(patch); refreshTry(); };
  for (const group of dlg.querySelectorAll('.tt-seg')) {
    group.addEventListener('click', (e) => {
      const b = e.target.closest('[data-v]'); if (!b) return;
      for (const x of group.querySelectorAll('[data-v]')) x.setAttribute('aria-checked', String(x === b));
      change({ [group.dataset.name]: b.dataset.v });
      if (group.dataset.name === 'mode') { render(); applyDom(dlg); }       // je Modus andere Felder
    });
  }
  for (const input of dlg.querySelectorAll('input[type="checkbox"]')) input.addEventListener('change', () => change({ [input.dataset.name]: input.checked }));
  for (const input of dlg.querySelectorAll('input[type="range"]')) {
    const out = input.parentElement.querySelector('output');
    input.addEventListener('input', () => {
      const v = Number(input.value);
      out.textContent = input.dataset.name === 'holdMs' ? t('touch.ms', { n: v }) : t('touch.times', { n: v.toFixed(1) });
      change({ [input.dataset.name]: v });
    });
  }
  dlg.querySelector('[data-act="reset"]').addEventListener('click', () => { provider.set({ ...TOUCH_DEFAULTS }); render(); });
  dlg.querySelector('[data-act="close"]').addEventListener('click', () => dlg.close());
  startTry();
}

/* ---------- Probierflaeche ---------- */
let tryGestures = null, tryUi = null, tryBind = null;
function refreshTry() { if (tryGestures) tryGestures.setConfig(provider.get()); }
function startTry() {
  stopTry();
  const box = dlg.querySelector('#ttTry'), text = dlg.querySelector('#ttTryText');
  if (!box) return;
  tryGestures = new TouchGestures(provider.get());
  tryUi = new TouchUi(box, () => provider.get().vibrate);
  const say = (s) => { text.innerHTML = s; };
  tryBind = bindSurface(box, tryGestures, (acts) => {
    for (const a of acts) {
      tryUi.show(a);
      if (a.type === 'ring' && a.phase === 'ready') say(`<b>${t('tab.tryReady')}</b>`);
      else if (a.type === 'ring' && a.phase === 'armed') say(`<b>${t('tab.tryArmed')}</b>`);
      else if (a.type === 'click') say(a.button === 'right' ? `<b>${t('touch.tryRight')}</b>` : a.how === 'double' ? `<b>${t('tab.tryDouble')}</b>` : a.how === 'long' ? `<b>${t('tab.tryLong')}</b>` : `<b>${t('touch.tryLeft')}</b>`);
      else if (a.type === 'zoom') say(`<b>${t('tab.tryZoom', { p: Math.round(a.ratio * 100) })}</b>`);
      else if (a.type === 'pan') say(`<b>${t('tab.tryPan')}</b>`);
      else if (a.type === 'press') say(`<b>${t('touch.tryDrag')}</b>`);
      else if (a.type === 'release') say(t('touch.tryReleased'));
      else if (a.type === 'scroll') say(`<b>${t('touch.tryScroll')}</b>`);
      else if (a.type === 'pinch') say(`<b>${t('touch.tryPinch', { p: Math.round(a.ratio * 100) })}</b>`);
      else if (a.type === 'zoomReset') say(`<b>${t('touch.tryZoomReset')}</b>`);
    }
  });
}
function stopTry() {
  if (tryBind) { tryBind.dispose(); tryBind = null; }
  if (tryUi) { tryUi.dispose(); tryUi = null; }
  tryGestures = null;
}

export function openTouchSettings() {
  if (!provider) return;
  if (!dlg) {
    dlg = document.createElement('dialog'); dlg.id = 'touchDlg'; dlg.className = 'tt-sheet';
    dlg.addEventListener('close', stopTry);
    document.body.appendChild(dlg);
  }
  render();
  applyDom(dlg);
  showDialog(dlg, { title: t('touch.title') });
}
export { normalizeTouch };
