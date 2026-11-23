import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseTokens, fmtTokens, percent, budgetTone, splitAllowed, buildAllowed, usageByDay, aiMarkup, providerChip } from '../../src/JulOS.Frontend/wwwroot/ai.js';

const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

test('Budgets werden mit k und M gelesen, leer heisst ohne Grenze, Unsinn wird abgelehnt', () => {
  assert.equal(parseTokens('200k'), 200000);
  assert.equal(parseTokens(' 1,5 M '), 1500000);
  assert.equal(parseTokens('1500'), 1500);
  assert.equal(parseTokens(''), null);
  assert.equal(parseTokens('   '), null);
  for (const bad of ['abc', '-5', '0', '12x', '1k2', '1e6']) assert.equal(parseTokens(bad), undefined, bad);
});

test('Tokens werden kurz gezeigt und Budgets in Prozent gerechnet (80 % warnt, 100 % ist voll)', () => {
  assert.equal(fmtTokens(950, 'en'), '950');
  assert.equal(fmtTokens(1234, 'en'), '1.2k');
  assert.equal(fmtTokens(200000, 'en'), '200k');
  assert.equal(fmtTokens(2000000, 'en'), '2M');
  assert.equal(percent(50, 100), 50);
  assert.equal(percent(150, 100), 100);
  assert.equal(percent(5, null), null);
  assert.equal(budgetTone(79, 100), 'ok');
  assert.equal(budgetTone(80, 100), 'warn');
  assert.equal(budgetTone(100, 100), 'full');
  assert.equal(budgetTone(1e9, null), 'ok');
});

test('erlaubte Anbieter und Modelle: genannte Modelle gehen vor, sonst die angehakten Anbieter', () => {
  assert.deepEqual(buildAllowed(['openai', 'codex', 'openai'], ''), ['openai', 'codex']);
  assert.deepEqual(buildAllowed(['openai'], 'openai/gpt-4.1, codex/x ; openai/gpt-4.1'), ['openai/gpt-4.1', 'codex/x']);
  assert.deepEqual(splitAllowed(['openai', 'codex/x']), { providers: ['openai'], models: ['codex/x'] });
});

test('der Verbrauch wird je Tag und App zusammengezaehlt', () => {
  const rows = [
    { day: '2026-11-23', appId: 'a', app: 'Notizen', requests: 2, failed: 1, aborted: 0, tokensIn: 10, tokensOut: 20 },
    { day: '2026-11-23', appId: 'a', app: 'Notizen', requests: 1, failed: 0, aborted: 1, tokensIn: 5, tokensOut: 5 },
    { day: '2026-11-22', appId: 'b', app: 'Kochen', requests: 4, failed: 0, aborted: 0, tokensIn: 1, tokensOut: 1 },
  ];
  assert.deepEqual(usageByDay(rows), [
    { day: '2026-11-23', app: 'Notizen', requests: 3, failed: 2, tokensIn: 15, tokensOut: 25 },
    { day: '2026-11-22', app: 'Kochen', requests: 4, failed: 0, tokensIn: 1, tokensOut: 1 },
  ]);
});

test('die Anzeige maskiert Namen und zeigt keinen Schluessel der Anbieter, ein Schluessel einer App nur direkt nach dem Anlegen', () => {
  globalThis.location = { origin: 'https://julos.example' };
  const state = {
    enabled: true, defaultModel: null, canStoreKeys: true,
    providers: [{ id: 'openai', enabled: true, keySet: true, status: 'ready', detail: null }],
    apps: [{ id: 'x', name: '<b>Notizen</b>', keyHint: 'abcd', active: true, allowed: ['openai'], dailyTokens: 100, monthlyTokens: null, ratePerMinute: 30, allowFiles: false, createdAt: '2026-11-23T10:00:00Z', lastUsedAt: null, budget: { day: { used: 85, limit: 100 }, month: { used: 85, limit: null } } }],
  };
  const ui = { keys: {}, msg: {}, models: [], usage: [], editing: null, form: null, shown: null, error: '' };
  const plain = aiMarkup(state, ui, esc);
  assert.ok(!plain.includes('<b>Notizen</b>') && plain.includes('&lt;b&gt;Notizen&lt;/b&gt;'));
  assert.ok(plain.includes('ai-warn'));                       // 85 von 100: Hinweis
  assert.ok(!plain.includes('jos_ai_'));
  const once = aiMarkup(state, { ...ui, shown: { id: 'x', key: 'jos_ai_NEUER' } }, esc);
  assert.ok(once.includes('jos_ai_NEUER') && once.includes('https://julos.example/api/ai/v1'));
  assert.equal(providerChip({ enabled: false, status: 'ready' }).key, 'ai.status.off');
  assert.equal(providerChip({ enabled: true, status: 'ready' }).tone, 'ok');
});
