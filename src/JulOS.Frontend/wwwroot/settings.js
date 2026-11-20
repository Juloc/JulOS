/* =====================================================================
 * Aufbau der Einstellungen: Handy (Profil) und Desktop (Einstellungsfenster) zeigen dieselben Gruppen in derselben Reihenfolge.
 * Eine Gruppe hat einen Titel und eine Zeile Hilfe; jede Zeile hat eine Beschriftung, eine kurze Erklaerung und genau ein Bedienelement
 * (Taste, Auswahl, Wert, Status) oder, bei "note", keins. Gefaehrliches steht am Ende in einer eigenen Gruppe.
 * Hier steht nur die Beschreibung und die reine Aufbereitung der Werte (Backup als Satz mit naechstem Schritt, Angaben des Servers); gezeichnet
 * wird in mobile.js und desktop.js. Kein DOM, damit es sich mit node pruefen laesst (tests/frontend/settings.test.mjs).
 * ===================================================================== */
import { t, tk, fmtDate, fmtNumber } from './i18n.js';

/** ctl: button | choice | swatches | value | status | link | note. only: 'phone' | 'desktop' (sonst beide). */
export const GROUPS = [
  { id: 'account', title: tk('set.account'), help: tk('set.accountHelp'), rows: [
    { id: 'user', ctl: 'value', label: tk('set.user'), help: tk('set.userHelp') },
    { id: 'password', ctl: 'button', label: tk('set.password'), help: tk('set.passwordHelp'), action: tk('set.change') },
    { id: 'devices', ctl: 'button', label: tk('set.devices'), help: tk('set.devicesHelp'), action: tk('set.show') },
  ] },
  { id: 'security', title: tk('set.security'), help: tk('set.securityHelp'), rows: [
    { id: 'security', ctl: 'button', label: tk('set.securityPage'), help: tk('set.securityPageHelp'), action: tk('set.open') },
  ] },
  { id: 'appearance', title: tk('set.appearance'), help: tk('set.appearanceHelp'), rows: [
    { id: 'scheme', ctl: 'choice', label: tk('set.scheme'), help: tk('set.schemeHelp') },
    { id: 'lang', ctl: 'choice', label: tk('set.lang'), help: tk('set.langHelp') },
    { id: 'color', ctl: 'swatches', only: 'phone', label: tk('set.color'), help: tk('set.colorHelp') },
    { id: 'dock', ctl: 'choice', only: 'desktop', label: tk('set.dock'), help: tk('set.dockHelp') },
  ] },
  { id: 'connections', title: tk('set.connections'), help: tk('set.connectionsHelp'), rows: [
    { id: 'folders', ctl: 'button', label: tk('set.folders'), help: tk('set.foldersHelp'), action: tk('set.manage') },
    { id: 'remote', ctl: 'note', only: 'desktop', label: tk('desktop.remoteControl'), help: tk('desktop.remoteControlText') },
  ] },
  { id: 'programs', title: tk('set.programs'), help: tk('set.programsHelp'), rows: [
    { id: 'claude', ctl: 'button', label: tk('set.claude'), help: tk('set.claudeHelp'), action: tk('set.start') },
    { id: 'codex', ctl: 'button', label: tk('set.codex'), help: tk('set.codexHelp'), action: tk('set.start') },
  ] },
  { id: 'alerts', title: tk('set.alertsGroup'), help: tk('set.alertsGroupHelp'), rows: [
    { id: 'alerts', ctl: 'button', label: tk('set.alerts'), help: tk('set.alertsHelp'), action: tk('set.manage') },
  ] },
  { id: 'sync', title: tk('set.sync'), help: tk('set.syncHelp'), rows: [
    { id: 'clipboard', ctl: 'note', label: tk('set.clipboard'), help: tk('set.clipboardHelp') },
    { id: 'sound', ctl: 'note', label: tk('set.sound'), help: tk('set.soundHelp') },
  ] },
  { id: 'backup', title: tk('set.backup'), help: tk('set.backupHelp'), rows: [
    { id: 'backup', ctl: 'status', label: tk('set.lastBackup'), help: tk('set.lastBackupHelp') },
    { id: 'backupCopy', ctl: 'status', label: tk('set.backupCopy'), help: tk('set.backupCopyHelp'), optional: true },
  ] },
  { id: 'about', title: tk('set.about'), help: tk('set.aboutHelp'), rows: [
    { id: 'version', ctl: 'value', label: tk('set.version'), help: tk('set.versionHelp') },
    { id: 'build', ctl: 'value', label: tk('set.build'), help: tk('set.buildHelp'), optional: true },
    { id: 'transfer', ctl: 'value', label: tk('set.transfer'), help: tk('set.transferHelp') },
    { id: 'nettools', ctl: 'button', label: tk('set.nettools'), help: tk('set.nettoolsHelp'), action: tk('set.open') },
    { id: 'licenses', ctl: 'link', label: tk('set.licenses'), help: tk('set.licensesHelp'), action: tk('set.open') },
    { id: 'source', ctl: 'link', label: tk('set.source'), help: tk('set.sourceHelp'), action: tk('set.open') },
  ] },
  { id: 'danger', danger: true, title: tk('set.signOutTitle'), help: tk('set.signOutHelp'), rows: [
    { id: 'signout', ctl: 'button', danger: true, label: tk('set.signOutHere'), help: tk('set.signOutHereHelp'), action: tk('set.signOut') },
  ] },
];

/** Die Zeilen einer Gruppe fuer eine Oberflaeche ('phone' oder 'desktop'). */
export const rowsFor = (group, platform) => group.rows.filter((r) => !r.only || r.only === platform);

const sizeText = (bytes) => (bytes < 1048576 ? t('set.kb', { n: Math.max(1, Math.round(bytes / 1024)) }) : t('set.mb', { n: fmtNumber(bytes / 1048576, 1) }));

/** Das Backup als Satz mit naechstem Schritt: { tone: ok|info|warn|error, chip, line, next, details }. Rohtext des Servers nur in details. */
export function backupInfo(b) {
  if (!b) return { tone: 'info', chip: t('set.unknown'), line: t('set.backupUnknown'), next: '', details: '' };
  const message = b.message || '';
  switch (b.state) {
    case 'ok': return { tone: 'ok', chip: t('set.ok'), line: `${fmtDate(b.lastAt)}${b.bytes ? ', ' + sizeText(b.bytes) : ''}`, next: '', details: '' };
    case 'off': return { tone: 'info', chip: t('set.off'), line: t('set.backupOff'), next: t('set.backupOffNext'), details: '' };
    case 'pending': return { tone: 'info', chip: t('set.planned'), line: t('set.backupPending', { time: b.time || '' }), next: '', details: '' };
    case 'failed': return { tone: 'error', chip: t('set.error'), line: t('set.backupFailed'), next: /schreiben|writ/i.test(message) ? t('set.backupFailedRights') : t('set.backupFailedGeneric'), details: message };
    default: return { tone: 'warn', chip: t('set.notSetUp'), line: t('set.backupNoDir'), next: t('set.backupNoDirNext'), details: message };
  }
}

/** Das zweite Ziel des Backups (nur wenn es eingerichtet ist), sonst null. */
export function copyInfo(b) {
  const c = b && b.copy;
  if (!c || c.state === 'off') return null;
  const at = c.lastAt ? fmtDate(c.lastAt) : '';
  if (c.state === 'ok') return { tone: 'ok', chip: t('set.ok'), line: t('set.copyOk', { at }), next: '', details: '' };
  if (c.state === 'failed') return { tone: 'error', chip: t('set.error'), line: t('set.copyFailed') + (at ? ' ' + t('set.copyLastOk', { at }) : ''), next: t('set.copyFailedNext'), details: c.message || '' };
  return { tone: 'info', chip: t('set.planned'), line: t('set.copyNone'), next: '', details: '' };
}

/** Die technischen Werte aus /api/about als fertige Texte. */
export function aboutValues(a) {
  const built = a && a.built ? fmtDate(a.built) : '';
  return {
    version: a ? `JulOS ${a.version}` : '',
    build: a ? [a.commit, built].filter(Boolean).join(' · ') : '',
    transfer: a && a.engines ? (a.engines.stream ? t('set.transferVideo', { encoder: a.engines.encoder || 'H.264' }) : t('set.transferNone')) : '',
    backup: backupInfo(a && a.backup),
    copy: copyInfo(a && a.backup),
    repository: a ? a.repository : '',
  };
}
