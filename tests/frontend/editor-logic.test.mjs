// Reine Logik des Editors: Zeilenenden, Sprache nach Dateiname, JSON- und YAML-Pruefung.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { detectEol, withEol, langFor, checkJson, checkYaml, lineCol, checkKind } from '../../src/JulOS.Frontend/wwwroot/editor-logic.js';

const www = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'src', 'JulOS.Frontend', 'wwwroot');

test('Zeilenenden: lf, crlf, gemischt, und zurueckschreiben', () => {
  assert.equal(detectEol('a\nb\n'), 'lf');
  assert.equal(detectEol('a'), 'lf');
  assert.equal(detectEol('a\r\nb\r\n'), 'crlf');
  assert.equal(detectEol('a\r\nb\n'), 'mixed');
  assert.equal(withEol('a\nb\n', 'crlf'), 'a\r\nb\r\n');
  assert.equal(withEol('a\nb\n', 'lf'), 'a\nb\n');
  assert.equal(withEol('a\nb\n', 'mixed'), 'a\nb\n');           // gemischt wird einheitlich LF
});

test('Sprache nach Endung, Name, Ordner und Shebang', () => {
  const cases = [['docker-compose.yml', 'yaml'], ['a.YAML', 'yaml'], ['package.json', 'json'], ['tsconfig.jsonc', 'jsonc'], ['README.md', 'markdown'], ['run.sh', 'shell'], ['.bashrc', 'shell'],
    ['app.ini', 'properties'], ['.env', 'properties'], ['.env.production', 'properties'], ['x.service', 'properties'], ['pyproject.toml', 'toml'], ['a.xml', 'xml'], ['a.py', 'python'],
    ['a.mjs', 'javascript'], ['syslog.log', 'log'], ['Dockerfile', 'dockerfile'], ['Dockerfile.dev', 'dockerfile'], ['app.dockerfile', 'dockerfile'], ['nginx.conf', 'nginx'],
    ['notes.txt', 'text'], ['Makefile', 'text'], ['.hidden', 'text']];
  for (const [name, lang] of cases) assert.equal(langFor(name), lang, name);
  assert.equal(langFor('site.conf', '/etc/nginx/sites-available/site.conf'), 'nginx');
  assert.equal(langFor('site.conf', '/etc/ssh/site.conf'), 'properties');
  assert.equal(langFor('backup', '/x/backup', '#!/bin/bash'), 'shell');
  assert.equal(langFor('tool', '/x/tool', '#!/usr/bin/env python3'), 'python');
  assert.equal(langFor('tool', '/x/tool', '#!/usr/bin/env node'), 'javascript');
  assert.equal(langFor('tool', '/x/tool', 'hello'), 'text');
  assert.equal(checkKind('json'), 'json'); assert.equal(checkKind('yaml'), 'yaml'); assert.equal(checkKind('jsonc'), ''); assert.equal(checkKind('shell'), '');
});

test('JSON: gueltige Texte sind in Ordnung, leer ebenso', () => {
  for (const ok of ['', '  \n', '{}', '[]', '0', '-1.5e+3', '"x\\u00e9\\n"', 'true', 'null', '{"a":[1,2,{"b":null}],"c":"d"}', '\n {\n "a" : 1 \n}\n']) assert.equal(checkJson(ok), null, JSON.stringify(ok));
});

test('JSON: Fehler mit Code und Stelle', () => {
  const at = (text) => { const r = checkJson(text); return r && { code: r.code, ...lineCol(text, r.pos) }; };
  assert.deepEqual(at('{"a":1,}'), { code: 'trailing', line: 0, col: 7 });
  assert.deepEqual(at('[1,2,]'), { code: 'trailing', line: 0, col: 5 });
  assert.deepEqual(at('{\n  "a": 1\n  "b": 2\n}'), { code: 'comma', line: 2, col: 2 });
  assert.deepEqual(at("{'a': 1}"), { code: 'key', line: 0, col: 1 });
  assert.deepEqual(at('{"a" 1}'), { code: 'colon', line: 0, col: 5 });
  assert.deepEqual(at('{"a": 1'), { code: 'end', line: 0, col: 7 });
  assert.deepEqual(at('{"a": tru}'), { code: 'value', line: 0, col: 6 });
  assert.deepEqual(at('{"a": 01}'), { code: 'comma', line: 0, col: 7 });
  assert.deepEqual(at('{"a": "x\ny"}'), { code: 'string', line: 0, col: 6 });
  assert.deepEqual(at('{"a": "\\x"}'), { code: 'escape', line: 0, col: 7 });
  assert.deepEqual(at('{"a": 1} x'), { code: 'data', line: 0, col: 9 });
  assert.deepEqual(at('// c\n{}'), { code: 'value', line: 0, col: 0 });
  assert.equal(checkJson('['.repeat(500)).code, 'deep');
  // Gleiches Urteil wie JSON.parse bei einer Reihe von Texten
  for (const s of ['{"a":}', '[1 2]', '{"a":1,"a":2}', '1.', '.5', '-', '"\t"', '[,1]', '{,}', 'nul', '[1,]', '{"a":[}']) assert.equal(checkJson(s) === null, (() => { try { JSON.parse(s); return true; } catch { return false; } })(), s);
});

// Der Parser ist der vendored js-yaml (derselbe Code, den der Browser laedt)
const { loadAll } = await import(join(www, 'vendor', 'codemirror', '6.43.15', 'js-yaml.mjs'));
test('YAML: in Ordnung, mehrere Dokumente, Anker, eigene Tags', () => {
  for (const ok of ['', 'a: 1\nb:\n  - x\n  - y\n', 'a: 1\n---\nb: 2\n', 'a: &x 1\nb: *x\n', 'k: !Ref Thing\n', 'k: !vault |\n  abc\n']) assert.equal(checkYaml(ok, loadAll), null, JSON.stringify(ok));
});
test('YAML: Fehler mit Zeile und Spalte (0-basiert)', () => {
  const e1 = checkYaml('a: 1\nb: [1, 2\nc: x\n', loadAll);
  assert.equal(e1.line, 2); assert.match(e1.reason, /\S/);
  const e2 = checkYaml('a: 1\n\tb: 2\n', loadAll);
  assert.equal(e2.line, 1); assert.match(e2.reason, /tab/i);
  const e3 = checkYaml('a: 1\na: 2\n', loadAll);
  assert.equal(e3.line, 1); assert.match(e3.reason, /duplicat/i);
  assert.equal(checkYaml('x', () => { throw new Error('kaputt'); }).reason, 'kaputt');
});
