/* =====================================================================
 * Dienste-Widgets (Modul "Dienste", docs/MODULES.md): Kacheln mit Zustand (gruen, rot, grau), Antwortzeit und Kennzahlen, dazu der Dialog zum Anlegen
 * und Aendern. Gemeinsam fuer den Desktop (Fenster "Widgets") und das Handy (Dienste-Seite); die Oberflaeche zeichnet sich selbst, der Server fragt nur
 * die gespeicherten Adressen ab. Der API-Schluessel wird nur eingegeben, nie angezeigt: der Server liefert nur "gespeichert: ja oder nein".
 * ===================================================================== */
import { t, tp, tk } from './i18n.js';
import { showDialog, focusField, askWindow } from './desktop-window.js';
import { svcIcon, ICON_KEYS, ICON_LABEL, TEMPLATES, templateById, fromTemplate, originOf, stateClass, stateText, errorText, msText, fmtMetric } from './modules.js';

const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const POLL_MS = 30000;
const FORMATS = [['plain', tk('widgets.fmtPlain')], ['bytes', tk('widgets.fmtBytes')], ['rate', tk('widgets.fmtRate')], ['percent', tk('widgets.fmtPercent')]];

/** Eine Kachel als HTML. st = Zustand vom Server oder undefined (noch nicht geprueft). */
export function tileHtml(svc, st, { canEdit } = {}) {
  const cls = stateClass(st);
  const metrics = st && st.metrics && st.metrics.length ? `<dl class="wd-metrics">${st.metrics.map((m) => `<div><dt>${esc(m.label)}</dt><dd>${esc(fmtMetric(m))}</dd></div>`).join('')}</dl>` : '';
  const err = errorText(st);
  const sub = st ? [stateText(st), msText(st)].filter(Boolean).join(' · ') : t('widgets.checking');
  const body = `<span class="wd-head"><span class="wd-ic">${svcIcon(svc.icon)}</span><span class="wd-name">${esc(svc.name)}</span><span class="wd-dot ${cls}" title="${esc(stateText(st))}"></span></span>
    <span class="wd-sub ${cls}">${esc(sub)}</span>${err ? `<span class="wd-err">${esc(err)}</span>` : ''}${metrics}`;
  const open = svc.openUrl ? `<a class="wd-body" href="${esc(svc.openUrl)}" target="_blank" rel="noopener noreferrer" aria-label="${esc(t('widgets.openService', { name: svc.name }))}">${body}</a>` : `<div class="wd-body">${body}</div>`;
  const edit = canEdit ? `<button type="button" class="wd-edit" data-edit="${esc(svc.id)}" aria-label="${esc(t('widgets.editService', { name: svc.name }))}" title="${esc(t('common.edit'))}">${editIcon}</button>` : '';
  return `<div class="wd-tile ${cls}" data-id="${esc(svc.id)}">${open}${edit}</div>`;
}
const editIcon = '<svg class="wd-i" viewBox="0 0 24 24" aria-hidden="true"><path d="M12 20h9M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z"/></svg>';

/**
 * Zeichnet die Kacheln in box und haelt sie aktuell (alle 30 s, solange box im Dokument steht).
 * deps: { api, canEdit, mobile, wireSheet? }. Gibt { reload, destroy } zurueck.
 */
export function mountWidgets(box, deps) {
  const { api } = deps;
  let canEdit = false, list = [], statuses = new Map(), timer = null, seq = 0, keyStore = false, max = 30;
  box.replaceChildren(); box.classList.add('wd');
  const bar = document.createElement('div'); bar.className = 'wd-bar';
  const note = document.createElement('span'); note.className = 'wd-note'; note.textContent = t('common.loading');
  const reload = document.createElement('button'); reload.type = 'button'; reload.className = 'wd-btn'; reload.textContent = t('common.reload');
  const add = document.createElement('button'); add.type = 'button'; add.className = 'wd-btn primary'; add.textContent = t('widgets.add'); add.hidden = true;
  bar.append(note, reload, add);
  const grid = document.createElement('div'); grid.className = 'wd-grid'; grid.setAttribute('aria-live', 'polite');
  box.append(bar, grid);

  function draw() {
    if (!list.length) {
      grid.innerHTML = `<p class="wd-empty">${esc(canEdit ? t('widgets.emptyEdit') : t('widgets.empty'))}</p>`;
      note.textContent = '';
      return;
    }
    grid.innerHTML = list.map((s) => tileHtml(s, statuses.get(s.id), { canEdit })).join('');
    const down = list.filter((s) => (statuses.get(s.id) || {}).state === 'down').length;
    note.textContent = tp('widgets.count', list.length) + (down ? ' · ' + tp('widgets.downCount', down) : '');
  }
  async function loadStatus() {
    const my = ++seq;
    try {
      const r = await api('/api/modules/services/status');
      if (my !== seq) return;
      statuses = new Map((r.statuses || []).map((s) => [s.id, s]));
      draw();
    } catch (e) { if (my === seq) note.textContent = e.message || t('widgets.notLoaded'); }
  }
  async function load() {
    try {
      const r = await api('/api/modules/services');
      list = r.services || []; canEdit = !!r.canEdit; keyStore = !!r.keyStore; max = r.max || 30;
      add.hidden = !canEdit; add.disabled = list.length >= max;
      draw();
      await loadStatus();
    } catch (e) { grid.innerHTML = `<p class="wd-empty">${esc(e.message || t('widgets.notLoaded'))}</p>`; note.textContent = ''; }
  }
  const tick = () => { if (!box.isConnected) return destroy(); if (!document.hidden) loadStatus(); };
  function destroy() { clearInterval(timer); timer = null; }
  const form = (svc) => openServiceForm({ ...deps, keyStore, svc, changed: load });
  reload.addEventListener('click', load);
  add.addEventListener('click', () => form(null));
  grid.addEventListener('click', (e) => { const b = e.target.closest('[data-edit]'); if (b) form(list.find((s) => s.id === b.dataset.edit)); });
  timer = setInterval(tick, POLL_MS);
  load();
  return { reload: load, destroy };
}

/** Dialog zum Anlegen (svc = null) oder Aendern. Ohne Schalter "control" am Server gibt es ihn nicht (die Knoepfe fehlen dann). */
export function openServiceForm({ api, mobile, wireSheet, keyStore, svc, changed }) {
  const editing = !!svc;
  const dlg = document.createElement('dialog');
  dlg.className = 'wd-dlg' + (mobile ? ' m3sheet' : '');
  const tplOptions = [`<option value="">${esc(t('widgets.tplNone'))}</option>`, ...TEMPLATES.map((x) => `<option value="${x.id}">${esc(t(x.name))}</option>`)].join('');
  const iconOptions = ICON_KEYS.map((k) => `<option value="${k}">${esc(t(ICON_LABEL[k]))}</option>`).join('');
  const fmtOptions = FORMATS.map(([k, l]) => `<option value="${k}">${esc(t(l))}</option>`).join('');
  dlg.innerHTML = `<form class="wd-form" autocomplete="off" novalidate>
    <h2>${esc(editing ? t('widgets.editTitle') : t('widgets.addTitle'))}</h2>
    ${editing ? '' : `<label>${esc(t('widgets.template'))}<select name="template">${tplOptions}</select></label>`}
    <label>${esc(t('widgets.name'))}<input name="name" maxlength="60" required></label>
    <label>${esc(t('widgets.icon'))}<select name="icon">${iconOptions}</select></label>
    <label>${esc(t('widgets.openUrl'))}<input name="openUrl" type="url" inputmode="url" placeholder="http://192.0.2.10:8080/"></label>
    <label>${esc(t('widgets.healthUrl'))}<input name="healthUrl" type="url" inputmode="url"></label>
    <label>${esc(t('widgets.metricsUrl'))}<input name="metricsUrl" type="url" inputmode="url"></label>
    <label>${esc(t('widgets.interval'))}<input name="intervalSeconds" type="number" min="10" max="3600" value="60"></label>
    <fieldset class="wd-key"><legend>${esc(t('widgets.keyTitle'))}</legend>
      <label>${esc(t('widgets.headerName'))}<input name="headerName" maxlength="64" placeholder="Authorization"></label>
      <label>${esc(t('widgets.apiKey'))}<input name="apiKey" type="password" autocomplete="new-password" ${keyStore ? '' : 'disabled'} placeholder="${esc(svc && svc.hasKey ? t('widgets.keyKept') : '')}"></label>
      ${svc && svc.hasKey ? `<label class="wd-check"><input name="removeKey" type="checkbox"> ${esc(t('widgets.keyRemove'))}</label>` : ''}
      <p class="wd-hint">${esc(keyStore ? t('widgets.keyHint') : t('widgets.keyOff'))}</p>
    </fieldset>
    <fieldset class="wd-fields"><legend>${esc(t('widgets.metricsTitle'))}</legend><div class="wd-rows"></div>
      <button type="button" class="wd-btn" data-a="row">${esc(t('widgets.addMetric'))}</button><p class="wd-hint">${esc(t('widgets.metricsHint'))}</p></fieldset>
    <p class="wd-msg" role="alert"></p>
    <div class="wd-actions">${editing ? `<button type="button" class="wd-btn danger" data-a="del">${esc(t('common.delete'))}</button>` : ''}<span class="wd-sp"></span>
      <button type="button" class="wd-btn" data-a="cancel">${esc(t('common.cancel'))}</button><button type="submit" class="wd-btn primary">${esc(t('common.save'))}</button></div></form>`;
  document.body.appendChild(dlg);
  if (mobile && wireSheet) wireSheet(dlg);
  const f = dlg.querySelector('form'), rows = dlg.querySelector('.wd-rows'), msg = dlg.querySelector('.wd-msg');
  const val = (n) => f.elements[n];
  let touched = { healthUrl: editing, metricsUrl: editing };

  function addRow(m = { path: '', label: '', unit: '', format: 'plain' }) {
    const r = document.createElement('div'); r.className = 'wd-row';
    r.innerHTML = `<input data-k="path" maxlength="120" placeholder="${esc(t('widgets.fieldPath'))}" value="${esc(m.path)}" aria-label="${esc(t('widgets.fieldPath'))}">
      <input data-k="label" maxlength="40" placeholder="${esc(t('widgets.fieldLabel'))}" value="${esc(m.label)}" aria-label="${esc(t('widgets.fieldLabel'))}">
      <input data-k="unit" maxlength="12" placeholder="${esc(t('widgets.fieldUnit'))}" value="${esc(m.unit)}" aria-label="${esc(t('widgets.fieldUnit'))}">
      <select data-k="format" aria-label="${esc(t('widgets.fieldFormat'))}">${fmtOptions}</select>
      <button type="button" class="wd-x" data-a="rm" aria-label="${esc(t('common.delete'))}">×</button>`;
    r.querySelector('select').value = m.format || 'plain';
    rows.appendChild(r);
  }
  const metricsOf = () => [...rows.children].map((r) => Object.fromEntries([...r.querySelectorAll('[data-k]')].map((x) => [x.dataset.k, x.value.trim()]))).filter((m) => m.path || m.label);

  function fill(v) {
    for (const k of ['name', 'icon', 'openUrl', 'healthUrl', 'metricsUrl', 'intervalSeconds', 'headerName']) if (v[k] !== undefined && val(k)) val(k).value = v[k];
    rows.replaceChildren(); for (const m of v.metrics || []) addRow(m);
  }
  if (svc) fill(svc); else { val('icon').value = 'globe'; }

  // Vorlage und Adresse: aus der Adresse des Dienstes entstehen Gesundheits- und Kennzahlen-Adresse, solange sie nicht von Hand geaendert wurden
  function applyTemplate() {
    const tpl = templateById(val('template')?.value);
    if (!tpl) return;
    const base = originOf(val('openUrl').value);
    const d = fromTemplate(tpl, base);
    if (!val('name').value) val('name').value = tpl.id.startsWith('jularr') ? 'Jularr' : tpl.id === 'caddyui' ? 'Caddy UI' : '';
    val('icon').value = d.icon; val('headerName').value = d.headerName; val('intervalSeconds').value = d.intervalSeconds;
    if (!touched.healthUrl) val('healthUrl').value = d.healthUrl;
    if (!touched.metricsUrl) val('metricsUrl').value = d.metricsUrl;
    rows.replaceChildren(); for (const m of d.metrics) addRow(m);
  }
  f.elements.template?.addEventListener('change', applyTemplate);
  val('openUrl').addEventListener('input', () => { if (val('template')?.value) applyTemplate(); });
  val('healthUrl').addEventListener('input', () => { touched.healthUrl = true; });
  val('metricsUrl').addEventListener('input', () => { touched.metricsUrl = true; });

  const close = () => { dlg.close(); dlg.remove(); };
  dlg.addEventListener('close', () => dlg.remove());
  dlg.addEventListener('click', async (e) => {
    const b = e.target.closest('[data-a]'); if (!b) return;
    if (b.dataset.a === 'cancel') return close();
    if (b.dataset.a === 'row') return addRow();
    if (b.dataset.a === 'rm') return b.closest('.wd-row').remove();
    if (b.dataset.a === 'del') {
      if (!(await askWindow({ title: t('common.delete') + ' · ' + svc.name, text: t('widgets.confirmDelete', { name: svc.name }), yes: t('common.delete'), no: t('common.cancel'), danger: true }))) return;
      try { await api('/api/modules/services/' + svc.id, { method: 'DELETE' }); close(); await changed(); } catch (err) { msg.textContent = err.message; }
    }
  });
  f.addEventListener('submit', async (e) => {
    e.preventDefault();
    const body = {
      name: val('name').value, icon: val('icon').value, openUrl: val('openUrl').value, healthUrl: val('healthUrl').value, metricsUrl: val('metricsUrl').value,
      intervalSeconds: Number(val('intervalSeconds').value) || 60, headerName: val('headerName').value, metrics: metricsOf(), template: val('template')?.value || (svc ? svc.template : ''),
    };
    const key = val('apiKey').value;
    if (key) body.apiKey = key; else if (val('removeKey') && val('removeKey').checked) body.apiKey = '';
    msg.textContent = '';
    try {
      const saved = await api(editing ? '/api/modules/services/' + svc.id : '/api/modules/services', { method: editing ? 'PUT' : 'POST', body });
      val('apiKey').value = '';
      close();
      api('/api/modules/services/' + saved.id + '/check', { method: 'POST', body: {} }).catch(() => {}).finally(changed);
    } catch (err) { msg.textContent = err.message; }
  });
  showDialog(dlg, { system: true, width: 520, resizable: true, disposable: true });
  focusField(dlg, val('name'));
}
