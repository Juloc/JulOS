import { FOLDER_ICON_KEYS, FOLDER_ICON_LABEL, folderIconSvg } from './folders.js';
import { t } from './i18n.js';
import { serverMsg } from './i18n-errors.js';

/* =====================================================================
 * Dialog "Ordner" (docs/FOLDERS.md): anlegen, umbenennen, Symbol waehlen, Reihenfolge, loeschen (die Verbindungen bleiben, ohne Ordner).
 * Gemeinsam fuer Handy und Desktop; das Markup steht in index.html (#folderDlg).
 * ===================================================================== */
const $ = (s) => document.querySelector(s);

/**
 * @param {{state: {folders: object[], connections: object[]}, api: Function, esc: Function, changed: () => Promise<void>}} o
 * changed: nach jeder Aenderung (die Seite laedt Verbindungen und Ordner neu und zeichnet die Listen)
 * @returns {() => void} oeffnet den Dialog
 */
export function initFolderDialog({ state, api, esc, changed }) {
  let editing = null, icon = 'folder';
  const msg = (text, ok = false) => { const m = $('#folderMsg'); m.textContent = text || ''; m.classList.toggle('ok', ok && !!text); };

  function drawIcons() {
    $('#folderIcons').innerHTML = FOLDER_ICON_KEYS.map((k) => `<button type="button" class="ghost" role="radio" aria-checked="${k === icon}" data-icon="${k}" aria-label="${FOLDER_ICON_LABEL[k]}" title="${FOLDER_ICON_LABEL[k]}">${folderIconSvg(k)}</button>`).join('');
  }
  function drawList() {
    const n = (f) => state.connections.filter((c) => c.folderId === f.id).length;
    $('#folderList').innerHTML = state.folders.map((f, i) => `<div class="frow" data-id="${esc(f.id)}"><span class="fi">${folderIconSvg(f.icon)}</span><span class="fn">${esc(f.name)} <small>(${n(f)})</small></span>
      <button type="button" class="ghost" data-a="up" aria-label="${t('folderDialog.moveUp')}" ${i === 0 ? 'disabled' : ''}>▲</button><button type="button" class="ghost" data-a="down" aria-label="${t('folderDialog.moveDown')}" ${i === state.folders.length - 1 ? 'disabled' : ''}>▼</button>
      <button type="button" class="ghost" data-a="edit" aria-label="${t('common.edit')}">✎</button><button type="button" class="ghost" data-a="del" aria-label="${t('common.delete')}">✕</button></div>`).join('');
  }
  function reset() {
    editing = null; icon = 'folder';
    $('#folderName').value = ''; $('#folderSave').textContent = t('folderDialog.create'); $('#folderNew').hidden = true;
    drawIcons();
  }
  const refresh = async () => { await changed(); drawList(); };

  $('#folderIcons').addEventListener('click', (e) => { const b = e.target.closest('[data-icon]'); if (b) { icon = b.dataset.icon; drawIcons(); } });
  $('#folderNew').addEventListener('click', () => { reset(); msg(''); $('#folderName').focus(); });
  $('#folderForm').addEventListener('submit', async (e) => {
    e.preventDefault();
    const name = $('#folderName').value;
    try {
      await api(editing ? `/api/folders/${editing.id}` : '/api/folders', { method: editing ? 'PUT' : 'POST', body: { name, icon } });
      msg(editing ? t('folderDialog.saved') : t('folderDialog.created'), true);
      reset(); await refresh();
    } catch (err) { msg(serverMsg(err.message)); }
  });
  $('#folderList').addEventListener('click', async (e) => {
    const b = e.target.closest('[data-a]'); if (!b) return;
    const f = state.folders.find((x) => x.id === b.closest('.frow').dataset.id); if (!f) return;
    const i = state.folders.indexOf(f);
    try {
      if (b.dataset.a === 'edit') { editing = f; icon = f.icon; $('#folderName').value = f.name; $('#folderSave').textContent = t('common.save'); $('#folderNew').hidden = false; drawIcons(); $('#folderName').focus(); msg(''); }
      else if (b.dataset.a === 'up' || b.dataset.a === 'down') {
        const ids = state.folders.map((x) => x.id); const j = i + (b.dataset.a === 'up' ? -1 : 1);
        if (j < 0 || j >= ids.length) return;
        [ids[i], ids[j]] = [ids[j], ids[i]];
        await api('/api/folders/order', { method: 'PUT', body: { ids } }); msg(''); await refresh();
      } else if (b.dataset.a === 'del') {
        // Loeschen braucht ein zweites Tippen; die Verbindungen des Ordners bleiben, ohne Ordner
        if (b.dataset.sure !== '1') { b.dataset.sure = '1'; b.textContent = t('folderDialog.confirmDelete'); setTimeout(() => { b.dataset.sure = ''; b.textContent = '✕'; }, 3000); return; }
        await api(`/api/folders/${f.id}`, { method: 'DELETE' });
        if (editing && editing.id === f.id) reset();
        msg(t('folderDialog.deleted'), true); await refresh();
      }
    } catch (err) { msg(serverMsg(err.message)); }
  });

  return () => { reset(); msg(''); drawList(); $('#folderDlg').showModal(); };
}
