/* =====================================================================
 * Dock eingeklappt: duenne Pillen (docs/MULTI-SCREEN.md, docs/BACKLOG.md "Desktop polish and screens"). Eine Pille je offenem Fenster, 5 px hoch;
 * Maus am Pillenstreifen oder Tastaturfokus klappt sie zu Karten mit dem Namen auf (CSS, desktop.css). Jede Pille ist eine Taste mit dem Namen des Fensters.
 * desktop.js liefert die Liste (update) und die Aktionen; hier steht nur das Element. Das Aussehen (Farben je Zustand) steckt im CSS:
 * .act = aktives Fenster, .min = minimiert, .rem = Verbindung (Fernbild oder Terminal). Aktiv ist auch breiter, nicht nur eine andere Farbe.
 * ===================================================================== */

/**
 * @param {{label: string, onClick: (id: string) => void, onMenu: (id: string, anchor: HTMLElement) => void}} o
 * Eintrag von update: { id, title, icon (Kachel-HTML aus dem Code), remote, active, minimized, hint (zusaetzlicher Text fuer Screenreader, zum Beispiel "minimiert") }
 */
export function createPills({ label, onClick, onMenu }) {
  const nav = document.createElement('nav');
  nav.className = 'dk-pills'; nav.setAttribute('aria-label', label);
  const buttons = new Map();

  function make(it) {
    const b = document.createElement('button');
    b.type = 'button'; b.className = 'dk-pill'; b.dataset.pill = it.id;
    b.innerHTML = '<span class="dk-pc"><span class="dk-pi"></span><span class="dk-pn"></span></span>';
    if (it.icon) b.querySelector('.dk-pi').innerHTML = it.icon;       // Kachel aus dem Code (desktop.js), kein Text von aussen
    b.addEventListener('click', () => onClick(it.id));
    b.addEventListener('contextmenu', (e) => { e.preventDefault(); e.stopPropagation(); onMenu(it.id, b); });
    buttons.set(it.id, b);
    return b;
  }

  /** Zeichnet die Liste neu, ohne Tasten zu ersetzen (ein Tastaturfokus auf einer Pille bleibt beim Wechsel des aktiven Fensters stehen) */
  function update(items) {
    const ids = new Set(items.map((i) => i.id));
    for (const [id, b] of buttons) if (!ids.has(id)) { b.remove(); buttons.delete(id); }
    items.forEach((it, i) => {
      const b = buttons.get(it.id) || make(it);
      if (nav.children[i] !== b) nav.insertBefore(b, nav.children[i] || null);
      b.classList.toggle('act', !!it.active); b.classList.toggle('min', !!it.minimized); b.classList.toggle('rem', !!it.remote);
      if (it.active) b.setAttribute('aria-current', 'true'); else b.removeAttribute('aria-current');
      const name = b.querySelector('.dk-pn');
      if (name.textContent !== it.title) name.textContent = it.title;
      const text = it.hint ? `${it.title} (${it.hint})` : it.title;
      b.setAttribute('aria-label', text); b.title = text;
    });
  }
  return { el: nav, update };
}
