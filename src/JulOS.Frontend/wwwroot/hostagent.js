/* =====================================================================
 * Der Abschnitt "Agent" der Host-Seite (docs/HOST-AGENT.md): Zustand des Agenten, aktualisieren, entfernen und die EINE Einrichtung fuer jede Maschine: Haken, was der Agent duerfen soll
 * (aus denen wird --allow a,b und ggf. --docker-group), dann "per SSH installieren" oder "Befehl kopieren". Reine Funktionen ohne DOM (Markup und Entscheidungen); die Bedienung steht in hostpage.js.
 * ===================================================================== */
import { t, tk } from './i18n.js';
import { agentSummary } from './hosts.js';
import { agentDetail, installCommand } from './hostinfo.js';
import { icon, card, callout, noteP, button } from './hostui.js';

/** Die Haken der Einrichtung: Wort fuer --allow, Text. Reihenfolge wie auf dem Bildschirm; nie ein Standard (alles beginnt aus). */
export const ALLOW_CHOICES = [
  { word: 'updates', label: tk('app.allowUpdates') }, { word: 'logs', label: tk('app.allowLogs') }, { word: 'services', label: tk('app.allowServices') },
  { word: 'docker', label: tk('app.allowDocker') }, { word: 'reboot', label: tk('app.allowReboot') },
];

/** Der Zustand der Einrichtung auf der Seite: gewaehlte Woerter, Docker-Gruppe, fertiger Befehl und Hinweis */
export const newSetup = () => ({ allow: [], dockerGroup: false, command: '', note: '' });

/** Der Befehl zu den Haken (nur mit einem frischen Code, den der Server gibt) */
export const setupCommand = (origin, code, s) => installCommand(origin, code, { allow: s.allow, dockerGroup: s.dockerGroup });

/** Der Satz zum Aktualisieren: warum es nicht geht (alt: Server kann es nicht, offline) oder '' */
export function updateHint(a) {
  if (!a || !a.updateAvailable) return '';
  return !a.canUpdate ? t('shell.agentCannotUpdate') : !a.online ? t('shell.agentOfflineUpdate') : '';
}

const check = (word, label, checked, esc) => `<label class="hu-check"><input type="checkbox" data-allow="${esc(word)}"${checked ? ' checked' : ''}><span class="hu-cb" aria-hidden="true"></span><span>${esc(t(label))}</span></label>`;

/**
 * Markup des Abschnitts. ctx: { host, esc, setup (newSetup), terminal (Terminal-Verbindung des Hosts oder null) }.
 * Die Tasten tragen data-Attribute (data-hag-*), die hostpage.js bedient; alles vom Server geht durch esc.
 */
export function agentPanel(ctx) {
  const { host, esc, setup, terminal } = ctx;
  const a = host.agent;
  const state = a ? [agentSummary(host), agentDetail(host)].filter(Boolean).join(' · ') : t('hosts.noAgent');
  const due = !!(a && a.updateAvailable);
  const hint = updateHint(a);
  const upd = !a ? '' : (due ? callout(t('hosts.updateAvailable') + (a.latest ? ` (${a.latest})` : ''), esc, 'info') + (hint ? noteP(hint, esc) : '') + `<div class="hu-acts">${button(t('app.updateNow'), esc, { tone: 'filled', icon: 'refresh', disabled: !(a.online && a.canUpdate), data: { 'hag-update': '1' } })}</div>` : '')
    + `<label class="hu-switch"><input type="checkbox" data-hag-auto="1"${a.autoUpdate ? ' checked' : ''}><span class="hu-sw" aria-hidden="true"></span><span>${esc(t('app.autoUpdate'))}</span></label>`;
  const status = card(t('app.hostAgent'), `<p class="hu-note">${esc(state)}</p>${upd}${a ? `<div class="hu-acts">${button(t('app.agentRemove'), esc, { tone: 'danger', sm: true, data: { 'hag-remove': '1' } })}</div>` : ''}`, esc, { icon: 'terminal' });
  const ticks = ALLOW_CHOICES.map((c) => check(c.word, c.label, setup.allow.includes(c.word), esc));
  // Die Docker-Gruppe ist eine eigene, ernste Entscheidung: die Zeile erscheint erst, wenn Docker angehakt ist
  const group = `<label class="hu-check" data-hag-grouprow${setup.allow.includes('docker') ? '' : ' hidden'}><input type="checkbox" data-hag-group="1"${setup.dockerGroup ? ' checked' : ''}><span class="hu-cb" aria-hidden="true"></span><span>${esc(t('app.allowDockerGroup'))}</span></label>`;
  const cmd = setup.command ? `<div class="hu-cmd">${noteP(setup.note, esc)}<pre class="hu-pre" id="hostAgentCmd">${esc(setup.command)}</pre></div>` : '';
  const setupCard = card(a ? t('hostview.setupAgain') : t('hostview.setupTitle'), noteP(t('app.hostAgentHint'), esc)
    + `<div class="hu-checks"><h5 class="hu-sub">${esc(t('app.allowTitle'))}</h5>${noteP(t('app.allowHint'), esc)}${ticks.slice(0, 4).join('')}${group}${ticks[4]}</div>`
    + noteP(terminal ? t('shell.sshTarget', { name: terminal.name }) : t('shell.sshNoTerminal'), esc)
    + `<div class="hu-acts">${terminal ? button(t('app.hostSshInstall'), esc, { tone: 'filled', icon: 'terminal', data: { 'hag-ssh': '1' } }) : ''}${button(t('app.hostSshCopy'), esc, { tone: terminal ? 'tonal' : 'filled', icon: 'download', data: { 'hag-copy': '1' } })}</div>${cmd}`, esc, { icon: 'download' });
  return `<div class="hu-cols">${status}${setupCard}</div>`;
}
