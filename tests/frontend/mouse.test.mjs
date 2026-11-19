// Maus am Desktop-Browser (mouse.js): aus Mausereignissen werden Zustandsobjekte fuer den Stream-Client.
import test from 'node:test';
import assert from 'node:assert/strict';
import { MouseInput, mouseState } from '../../src/JulOS.Frontend/wwwroot/mouse.js';

function fakeElement() {
  const handlers = {};
  return {
    addEventListener: (type, fn) => { handlers[type] = fn; },
    getBoundingClientRect: () => ({ left: 100, top: 50 }),
    fire: (type, props = {}) => { let prevented = false; handlers[type]({ preventDefault: () => { prevented = true; }, ...props }); return prevented; },
  };
}

test('positions are relative to the element and buttons are tracked', () => {
  const el = fakeElement(), seen = [];
  const mouse = new MouseInput(el);
  mouse.onmousedown = mouse.onmouseup = mouse.onmousemove = (s) => seen.push(s);
  el.fire('mousemove', { clientX: 130, clientY: 90 });
  el.fire('mousedown', { clientX: 130, clientY: 90, button: 0 });
  el.fire('mousedown', { clientX: 130, clientY: 90, button: 2 });
  el.fire('mouseup', { clientX: 140, clientY: 95, button: 0 });
  assert.deepEqual(seen[0], mouseState(30, 40));
  assert.deepEqual(seen[1], mouseState(30, 40, true));
  assert.deepEqual(seen[2], mouseState(30, 40, true, false, true));
  assert.deepEqual(seen[3], mouseState(40, 45, false, false, true));
});

test('a wheel step is one press and one release of up or down', () => {
  const el = fakeElement(), seen = [];
  const mouse = new MouseInput(el);
  mouse.onmousedown = mouse.onmouseup = (s) => seen.push(s);
  assert.equal(el.fire('wheel', { clientX: 100, clientY: 50, deltaY: -100 }), true);
  el.fire('wheel', { clientX: 100, clientY: 50, deltaY: 100 });
  el.fire('wheel', { clientX: 100, clientY: 50, deltaY: 0 });             // no movement: nothing to send
  assert.deepEqual(seen.map((s) => [s.up, s.down]), [[true, false], [false, false], [false, true], [false, false]]);
});

test('the context menu of the browser is suppressed', () => {
  const el = fakeElement(); new MouseInput(el);
  assert.equal(el.fire('contextmenu'), true);
});
