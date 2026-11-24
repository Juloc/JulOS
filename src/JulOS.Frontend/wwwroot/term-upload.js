/* =====================================================================
 * Terminal: Dateien und Bilder auf den Host legen (docs/CLIPBOARD.md, "Terminal"). Es gibt keinen eigenen Weg: dasselbe wie "Hochladen" in der Dateien-App
 * (files-core.js, files-ui.js): der Pool der SFTP-Sitzungen, die strenge Pruefung des Host-Schluessels (mit denselben Abfragen), der Schalter "Dateien schreiben",
 * Zweischritt-Bestaetigung (prepare, Dialog, Token), Grenzen, Namens- und Groessenpruefung auf dem Server, Fortschritt und Abbruch.
 * Der Ordner kommt vom Terminal (OSC 7) oder ist leer (Home des Benutzers); der Server prueft ihn selbst (absolut, kein "..", muss ein Ordner sein).
 * Rueckgabe von uploadToShell: die Pfade (absolut, wie der Server sie meldet) oder null, wenn nichts oder nicht alles hochgeladen wurde.
 * ===================================================================== */
import { t, tp } from './i18n.js';
import * as C from './files-core.js';
import { safeName } from './files-explorer.js';
import { h, modal, noteEl, confirmDialog, progressDialog } from './files-ui.js';
import { passwordDialog, hostKeyDialog } from './files-app.js';
import { planNames } from './term-paste.js';

const REOPEN = ['password_required', 'password_unreadable', 'host_key_new', 'host_key_changed', 'connection_lost'];
const MAX_FILES = 100;     // wie die Dateien-App (ein Auftrag)

/** Das Schreiben ist aus: der Grund und der Weg zum Schalter (wie in der Dateien-App). */
async function explainWriteOff(kit) {
  const d = modal({ title: t('files.writeOffTitle'), kit, content: [h('p', { text: t('files.writeOffText') })], actions: [{ id: 'x', label: t('common.close'), kind: 'ghost' }, { id: 'go', label: t('files.writeOffOpen'), kind: 'primary' }] });
  if ((await d.promise) === 'go') window.dispatchEvent(new CustomEvent('julos:open-security', { detail: { section: 'filesWrite' } }));
}

/**
 * @param api     die Abfrage mit Freigabe durch den zweiten Faktor (shell.js)
 * @param conn    die gespeicherte SSH-Verbindung des Terminals
 * @param dir     Ordner laut OSC 7 oder '' (Home)
 * @param files   File-Objekte (Datei aus dem Dateidialog, vom Ablegen oder ein Bild aus der Zwischenablage mit fromClipboard)
 * @param say     kurze Meldung
 */
export async function uploadToShell({ api, conn, dir, files, kit, say }) {
  if (!api) return say(t('clip2.noApi')), null;
  if (!conn || conn.quick || !conn.id) return say(t('clip2.noStored')), null;
  const fapi = C.makeFilesApi(api);
  const place = { id: conn.id, kind: 'ssh', name: conn.name, host: conn.name, label: conn.name, address: conn.target || conn.host || '' };
  let info;
  try { info = await fapi.info(); } catch (e) { say(C.errorMessage(e)); return null; }
  if (!info.write) { await explainWriteOff(kit); return null; }
  const tooBig = files.filter((f) => info.maxBytes && f.size > info.maxBytes);
  if (tooBig.length) { say(t('files.err.too_large')); return null; }
  if (files.length > MAX_FILES) { say(t('files.err.too_many_items')); return null; }

  const plan = planNames(files);
  const named = plan.map(({ file, name }) => new File([file], name, { type: file.type }));
  const ask = { password: (p, wrong, host) => passwordDialog(kit, p, wrong, host), hostKey: (p, data, changed) => hostKeyDialog(kit, p, data, changed) };
  const ensureOpen = (p) => C.openPlace(fapi, p, ask);
  // Fehlt die Sitzung (Leerlauf, noch nie geoeffnet) oder ist der Schluessel neu: einmal oeffnen, mit den Abfragen der Dateien-App, und noch einmal
  const guarded = async (fn) => { try { return await fn(); } catch (e) { if (REOPEN.includes(e.code)) { await ensureOpen(place); return fn(); } throw e; } };

  let prep;
  try { prep = await guarded(() => fapi.prepare('upload', place, { dir, names: named.map((f) => f.name) })); }
  catch (e) { if (!C.isCancelled(e)) say(C.errorMessage(e)); return null; }
  const r = await confirmDialog(kit, C.uploadModel(prep, named), { expiresIn: prep.expiresIn });
  if (!r) return null;

  const overwrite = new Set(prep.conflicts);
  let aborted = false, current = null;
  const prog = progressDialog(kit, t('files.upload.title'), { onCancel: () => { aborted = true; if (current) current.abort(); } });
  const done = [], failed = [];
  for (let i = 0; i < named.length && !aborted; i++) {
    const f = named[i];
    prog.update({ percent: 0, text: `${safeName(f.name)} (${i + 1}/${named.length})` });
    const progress = (loaded, total) => prog.update({ percent: total ? Math.round((loaded / total) * 100) : null, text: `${safeName(f.name)} (${i + 1}/${named.length}) · ${C.fileSize(loaded)} / ${C.fileSize(total)}` });
    try {
      await guarded(() => (current = fapi.upload(place, prep.dir, f, { overwrite: overwrite.has(f.name), token: prep.token, answer: r.answer, onProgress: progress })).promise);
      done.push(C.joinPath(prep.dir, f.name, place));
    } catch (e) { if (C.isCancelled(e) && aborted) break; failed.push(`${safeName(f.name)}: ${C.errorMessage(e)}`); }
  }
  const ok = !failed.length && !aborted;
  // Ein Fehlschlag oder Abbruch schreibt nichts auf die Kommandozeile; der Dialog sagt, was oben ist
  await prog.finish({ ok, text: aborted ? t('files.upload.cancelled', { n: done.length }) : ok ? t('files.upload.done', { n: tp('files.nFiles', done.length) }) : t('files.upload.partial', { ok: done.length, failed: failed.length }), details: failed });
  return ok ? done : null;
}
