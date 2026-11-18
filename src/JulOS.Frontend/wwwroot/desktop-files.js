/* =====================================================================
 * Dateien-Fenster (nur lesen): zeigt den einen Ordner, den der Server freigibt (JULOS_FILES_ROOT, Standard der Upload-Ordner).
 * Ordner oeffnen, nach Namen suchen, Datei herunterladen. Es gibt hier nichts zum Aendern, Loeschen oder Hochladen.
 * Wird von desktop.js erst beim ersten Oeffnen geladen. deps: { el, btn, ic, api, notify }
 * ===================================================================== */

const fmtNum = (n, d = 1) => n.toLocaleString('de-DE', { minimumFractionDigits: d, maximumFractionDigits: d });
export const fileSize = (b) => (b < 1024 ? fmtNum(b, 0) + ' B' : b < 1024 ** 2 ? fmtNum(b / 1024, b < 10240 ? 1 : 0) + ' KiB' : b < 1024 ** 3 ? fmtNum(b / 1024 ** 2) + ' MiB' : fmtNum(b / 1024 ** 3) + ' GiB');
const fmtDate = (iso) => new Date(iso).toLocaleDateString('de-DE', { day: '2-digit', month: '2-digit', year: 'numeric' });
const q = (path) => encodeURIComponent(path);
export const downloadUrl = (path) => '/api/files/download?path=' + q(path);

/** Baut das Fenster-Innere in `body` und gibt { go(path) } zurueck */
export function mount(body, { el, btn, ic, api }) {
  const st = { path: '', back: [], fwd: [], sel: null, query: '', seq: 0 };
  body.innerHTML = '';
  const root = el('div', 'dk-fs');
  const side = el('nav', 'dk-fside'); side.setAttribute('aria-label', 'Orte');
  const home = btn('dk-nav sel', ic('folder') + '<span>Meine Dateien</span>'); home.dataset.act = 'root';
  const note = el('p', 'dk-fnote'); note.textContent = 'Der gemeinsame Ordner dieses Servers. Hier kannst du nur ansehen, suchen und herunterladen.';
  side.append(home, note);
  const main = el('div', 'dk-fmain');
  const tool = el('div', 'dk-ftool');
  const bBack = btn('dk-nv', ic('left'), 'Zurück'), bFwd = btn('dk-nv', ic('right'), 'Vorwärts'), bUp = btn('dk-nv', ic('up'), 'Ordner nach oben');
  const crumbs = el('div', 'dk-crumbs'); crumbs.setAttribute('aria-label', 'Pfad');
  const find = el('label', 'dk-fsearch', ic('search') + '<input type="text" autocomplete="off" spellcheck="false" aria-label="Nach Namen suchen">');
  const input = find.querySelector('input');
  tool.append(bBack, bFwd, bUp, crumbs, find);
  const view = el('div', 'dk-fview'); view.setAttribute('aria-live', 'polite');
  const stat = el('div', 'dk-fstat');
  main.append(tool, view, stat);
  root.append(side, main); body.appendChild(root);

  const base = (p) => p.split('/').pop();
  const parent = (p) => p.split('/').slice(0, -1).join('/');
  const status = (text, extra) => { stat.replaceChildren(); const s = el('span'); s.textContent = text; stat.appendChild(s); if (extra) stat.appendChild(extra); };

  function renderCrumbs() {
    crumbs.replaceChildren();
    const parts = st.path ? st.path.split('/') : [];
    const add = (label, path, last) => {
      const b = btn('dk-crumb' + (last ? ' cur' : ''), ''); b.textContent = label; b.addEventListener('click', () => go(path));
      if (crumbs.childNodes.length) crumbs.appendChild(el('span', 'dk-csep', ic('right')));
      crumbs.appendChild(b);
    };
    add('Dateien', '', !parts.length);
    parts.forEach((s, i) => add(s, parts.slice(0, i + 1).join('/'), i === parts.length - 1));
    crumbs.scrollLeft = crumbs.scrollWidth;
    input.placeholder = 'In „' + (parts.length ? parts[parts.length - 1] : 'Dateien') + '“ suchen …';
    bBack.disabled = !st.back.length; bFwd.disabled = !st.fwd.length; bUp.disabled = !st.path;
  }

  function fileIcon(e, big) {
    const t = el('span', 'dk-fic' + (e.isDirectory ? ' dir' : '') + (big ? ' big' : ''), ic(e.isDirectory ? 'folder' : 'file'));
    return t;
  }
  function item(e, withPath) {
    const b = btn('dk-fi' + (e.isDirectory ? ' dir' : ''), '');
    b.dataset.path = e.path; b.dataset.dir = e.isDirectory ? '1' : '';
    const name = el('span', 'dk-fn'); name.textContent = e.name; name.title = e.name;
    const meta = el('span', 'dk-fm');
    meta.textContent = e.isDirectory ? (e.items === 1 ? '1 Eintrag' : (e.items >= 999 ? '999+' : e.items) + ' Einträge') : fileSize(e.size);
    b.append(fileIcon(e, !withPath), name, meta);
    if (withPath) { const w = el('span', 'dk-fp'); w.textContent = parent(e.path) || 'Dateien'; w.title = e.path; b.insertBefore(w, meta); }
    return b;
  }

  function showError(msg) { view.replaceChildren(); const n = el('div', 'dk-fempty'); n.textContent = msg; view.appendChild(n); status(''); }

  function select(path, e) {
    st.sel = path;
    for (const b of view.querySelectorAll('.dk-fi')) b.classList.toggle('sel', b.dataset.path === path);
    if (!e) return;
    const dl = btn('dk-btn pri', ic('download') + 'Herunterladen'); dl.addEventListener('click', () => download(path));
    status(`${e.name} · ${fileSize(e.size)} · geändert am ${fmtDate(e.modified)}`, dl);
  }
  function download(path) {
    const a = document.createElement('a'); a.href = downloadUrl(path); a.download = ''; a.rel = 'noopener'; document.body.appendChild(a); a.click(); a.remove();
  }

  let entries = new Map();
  const wire = (grid) => {
    grid.addEventListener('click', (ev) => {
      const b = ev.target.closest('.dk-fi'); if (!b) return;
      if (b.dataset.dir) return go(b.dataset.path);
      select(b.dataset.path, entries.get(b.dataset.path));
    });
    grid.addEventListener('dblclick', (ev) => { const b = ev.target.closest('.dk-fi'); if (b && !b.dataset.dir) download(b.dataset.path); });
  };

  async function load(path) {
    const my = ++st.seq;
    view.classList.add('busy');
    try {
      const l = await api('/api/files/list?path=' + q(path));
      if (my !== st.seq) return false;
      st.path = l.path; st.sel = null; st.query = ''; input.value = '';
      entries = new Map(l.entries.map((e) => [e.path, e]));
      renderCrumbs();
      view.replaceChildren();
      if (!l.entries.length) { const n = el('div', 'dk-fempty'); n.textContent = 'Dieser Ordner ist leer.'; view.appendChild(n); }
      else { const g = el('div', 'dk-fgrid'); for (const e of l.entries) g.appendChild(item(e, false)); wire(g); view.appendChild(g); }
      const dirs = l.entries.filter((e) => e.isDirectory).length, n = l.entries.length;
      status(`${n} ${n === 1 ? 'Element' : 'Elemente'}${dirs ? `, davon ${dirs} ${dirs === 1 ? 'Ordner' : 'Ordner'}` : ''}${l.truncated ? ' (nur die ersten ' + n + ' gezeigt)' : ''}`);
      return true;
    } catch (e) {
      if (my !== st.seq) return false;
      showError(e.message || 'Nicht geladen.'); return false;
    } finally { if (my === st.seq) view.classList.remove('busy'); }
  }

  async function go(path, record = true) {
    const before = st.path;
    if (await load(path) && record && before !== st.path) { st.back.push(before); st.fwd = []; renderCrumbs(); }
  }

  let timer = null;
  async function search() {
    const text = input.value.trim();
    if (!text) { if (st.query) await load(st.path); return; }
    const my = ++st.seq; st.query = text;
    view.classList.add('busy');
    try {
      const r = await api('/api/files/search?path=' + q(st.path) + '&q=' + q(text));
      if (my !== st.seq) return;
      entries = new Map(r.results.map((e) => [e.path, e]));
      view.replaceChildren();
      if (!r.results.length) { const n = el('div', 'dk-fempty'); n.textContent = `Nichts mit „${text}“ gefunden.`; view.appendChild(n); status(''); return; }
      const g = el('div', 'dk-flist'); for (const e of r.results) g.appendChild(item(e, true)); wire(g); view.appendChild(g);
      status(`${r.results.length} ${r.results.length === 1 ? 'Treffer' : 'Treffer'}${r.truncated ? ' (die Suche wurde abgebrochen, es gibt vermutlich mehr)' : ''}`);
    } catch (e) { if (my === st.seq) showError(e.message || 'Suche fehlgeschlagen.'); }
    finally { if (my === st.seq) view.classList.remove('busy'); }
  }
  input.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(search, 300); });
  input.addEventListener('keydown', (e) => { if (e.key === 'Enter') { clearTimeout(timer); search(); } else if (e.key === 'Escape' && input.value) { input.value = ''; e.stopPropagation(); search(); } });

  bBack.addEventListener('click', async () => { if (!st.back.length) return; const to = st.back[st.back.length - 1], was = st.path; if (await load(to)) { st.back.pop(); st.fwd.push(was); renderCrumbs(); } });
  bFwd.addEventListener('click', async () => { if (!st.fwd.length) return; const to = st.fwd[st.fwd.length - 1], was = st.path; if (await load(to)) { st.fwd.pop(); st.back.push(was); renderCrumbs(); } });
  bUp.addEventListener('click', () => go(parent(st.path)));
  home.addEventListener('click', () => go(''));
  renderCrumbs();
  go('');
  return { go: (p) => go(p) };
}
