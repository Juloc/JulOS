import { t, tk } from './i18n.js';
import { noteP, callout, card, listBlock, button, icon } from './hostui.js';

/* =====================================================================
 * Der Login eines Hosts (docs/HOSTS.md, "Login"): ein Benutzer und ein Passwort fuer Terminal, Dateien und Desktop-Verbindungen dieses Hosts. Reine Funktionen ohne DOM
 * (Markup und Entscheidungen), gemeinsam fuer Handy und Desktop; die Bedienung (Speichern, Entfernen) steht in hostpage.js. Das Passwortfeld ist nie vorbelegt,
 * der Server nennt nie ein Passwort, nur ob eins gespeichert ist. Alles vom Server geht durch esc.
 * ===================================================================== */

/** Der Benutzer, mit dem sich die Verbindung anmeldet: der des Host-Logins oder ihr eigener (aeltere Antworten ohne die Felder: der gespeicherte) */
export const loginUser = (c) => (c && (c.loginUser || c.username)) || '';
/** Liegt fuer die Verbindung ein Passwort bereit (eigenes oder das des Host-Logins), sodass nichts gefragt werden muss */
export const loginSaved = (c) => !!c && (c.loginPassword !== undefined ? !!c.loginPassword : !!c.hasPassword);
/** Gilt der Login des Hosts fuer diese Verbindung */
export const usesHostLogin = (c) => !!c && c.loginSource === 'host';

/** Der Host-Login im Verbindungsdialog: den Haken "Eigenen Benutzer verwenden" gibt es nur, wenn der Host einen Login hat */
export const hostHasLogin = (host) => !!(host && host.loginUser);

/** Ein Satz zum Zustand des Logins */
export function loginState(login) {
  if (!login || !login.user) return t('hostlogin.stateNone');
  return t(login.hasPassword ? STATE_SAVED : STATE_USER, { user: login.user });
}

const STATE_SAVED = tk('hostlogin.stateSaved'), STATE_USER = tk('hostlogin.stateUser'), PW_KEEP = tk('hostlogin.passwordKeep'), PW_NEW = tk('hostlogin.passwordPh');
const KIND = { terminal: tk('hostlogin.kindTerminal'), desktop: tk('hostlogin.kindDesktop'), vnc: tk('hostlogin.kindVnc') };

/** Was eine Verbindung beim Verbinden benutzt, als Marke { text, tone } */
export function connectionBadge(row) {
  if (row.source === 'host') return { text: t('hostlogin.usesHost'), tone: 'ok' };
  if (row.addressNew) return { text: t('hostlogin.addressNew'), tone: 'warn' };
  return { text: row.username ? t('hostlogin.own', { user: row.username }) : t('hostlogin.ownLogin'), tone: '' };
}

/**
 * Markup des Abschnitts Login. ctx: { host, esc, login: { resp, error, loading } } (resp: Antwort von GET /api/hosts/{id}/login).
 * Die Bedienung traegt data-hl-* (hostpage.js): save, remove, suggest. Das Passwortfeld bleibt leer (kein value, autocomplete new-password).
 */
export function loginPanel(ctx) {
  const { esc } = ctx, st = ctx.login || {}, r = st.resp;
  if (!r) return noteP(st.error || t('hostinfo.stLoading'), esc, !!st.error);
  const field = (id, label, input) => `<label class="hu-field" for="${id}"><span>${esc(label)}</span>${input}</label>`;
  const user = field('hostLoginUser', t('hostlogin.user'), `<input id="hostLoginUser" type="text" maxlength="64" autocomplete="off" autocapitalize="none" autocorrect="off" spellcheck="false" value="${esc(r.user || '')}">`);
  const pw = field('hostLoginPw', t('hostlogin.password'), `<input id="hostLoginPw" type="password" maxlength="256" autocomplete="new-password" autocapitalize="none" autocorrect="off" spellcheck="false"${r.canStore ? '' : ' disabled'} placeholder="${esc(t(r.hasPassword ? PW_KEEP : PW_NEW))}">`);
  const never = `<label class="hu-check"><input type="checkbox" id="hostLoginNoPw" data-hl-nopw="1"${r.canStore ? '' : ' disabled'}><span class="hu-cb" aria-hidden="true"></span><span>${esc(t('hostlogin.noPassword'))}</span></label>`;
  const suggest = !r.user && r.suggestion ? `<div class="hu-acts">${callout(t('hostlogin.suggest', { user: r.suggestion }), esc, 'info')}${button(t('hostlogin.useSuggestion'), esc, { tone: 'tonal', sm: true, data: { 'hl-suggest': r.suggestion } })}</div>` : '';
  const acts = `<div class="hu-acts">${button(t('hostlogin.save'), esc, { tone: 'filled', data: { 'hl-save': '1' } })}${r.user ? button(t('hostlogin.remove'), esc, { tone: 'danger', sm: true, data: { 'hl-remove': '1' } }) : ''}</div>`;
  const form = `<div class="hu-form" id="hostLoginForm">${user}${pw}${never}${acts}</div>`;
  const rows = (r.connections || []).map((c) => ({ name: c.name, detail: `${t(KIND[c.kind] || KIND.terminal)} · ${c.address}`, badge: connectionBadge(c) }));
  const list = rows.length ? listBlock(rows, esc) : noteP(t('hostlogin.noConnections'), esc);
  const newAddress = (r.connections || []).some((c) => c.addressNew);
  const status = card(t('hostlogin.title'), `${noteP(t('hostlogin.sentence'), esc)}<p class="hu-note" id="hostLoginState">${esc(loginState(r))}</p>${suggest}${form}`
    + `${r.canStore ? '' : noteP(t('hostlogin.noKey'), esc, true)}${noteP(t('hostlogin.rootHint'), esc)}`, esc, { icon: 'lock', id: 'login' });
  const conns = card(t('hostlogin.connections'), `${list}${newAddress ? noteP(t('hostlogin.addressNote'), esc, true) : noteP(t('hostlogin.addressInfo'), esc)}`, esc, { icon: 'terminal' });
  return `<div class="hu-cols">${status}${conns}</div>`;
}

/** Die Anfrage zum Speichern aus den Feldern: leeres Passwort = das gespeicherte bleibt; der Haken "nicht speichern" entfernt es */
export function loginRequest(fields) {
  const body = { user: String(fields.user || '').trim() };
  if (fields.password) body.password = fields.password;
  else if (fields.noPassword) body.forgetPassword = true;
  return body;
}

/** Eine Meldung nach dem Speichern: gespeichert, und wie viele Verbindungen ihren eigenen Benutzer behalten haben */
export const savedText = (resp) => [t('hostlogin.saved'), resp && resp.pinned ? t('hostlogin.pinned', { n: resp.pinned }) : ''].filter(Boolean).join(' ');

export { icon };
