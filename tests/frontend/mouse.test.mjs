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

test('a wheel turn is one event in Windows units (120 a notch), vertical up is positive, horizontal right is positive', () => {
  const el = fakeElement(), seen = [];
  const mouse = new MouseInput(el);
  mouse.onmousedown = mouse.onmouseup = (s) => seen.push(s);
  assert.equal(el.fire('wheel', { clientX: 100, clientY: 50, deltaY: -100, deltaMode: 0 }), true);   // Chrome: 100 pixels a notch, up
  el.fire('wheel', { clientX: 100, clientY: 50, deltaY: 100, deltaMode: 0 });
  el.fire('wheel', { clientX: 100, clientY: 50, deltaX: 100, deltaMode: 0 });
  el.fire('wheel', { clientX: 100, clientY: 50, deltaY: 0 });                                      // no movement: nothing to send
  assert.deepEqual(seen.map((s) => [s.wheel, s.hwheel]), [[120, 0], [0, 0], [-120, 0], [0, 0], [0, 120], [0, 0]]);
});

test('line and page wheels are scaled, and small steps add up instead of getting lost', () => {
  const el = fakeElement(), seen = [];
  const mouse = new MouseInput(el);
  mouse.onmousedown = (s) => seen.push(s);
  el.fire('wheel', { clientX: 100, clientY: 50, deltaY: -3, deltaMode: 1 });                       // Firefox: 3 lines a notch
  el.fire('wheel', { clientX: 100, clientY: 50, deltaY: -1, deltaMode: 2 });                       // one page
  for (let i = 0; i < 5; i++) el.fire('wheel', { clientX: 100, clientY: 50, deltaY: -1, deltaMode: 0 });   // 1.2 units each
  assert.deepEqual(seen.map((s) => s.wheel), [120, 360, 1, 1, 1, 1, 2]);                           // 1.2, 2.4, 3.6, 4.8, 6.0 -> 1,1,1,1,2
});

test('the thumb buttons are tracked as back and forward', () => {
  const el = fakeElement(), seen = [];
  const mouse = new MouseInput(el);
  mouse.onmousedown = mouse.onmouseup = (s) => seen.push(s);
  el.fire('mousedown', { clientX: 100, clientY: 50, button: 3 });
  el.fire('mousedown', { clientX: 100, clientY: 50, button: 4 });
  el.fire('mouseup', { clientX: 100, clientY: 50, button: 3 });
  assert.deepEqual(seen.map((s) => [s.back, s.forward]), [[true, false], [true, true], [false, true]]);
});

test('the context menu of the browser is suppressed', () => {
  const el = fakeElement(); new MouseInput(el);
  assert.equal(el.fire('contextmenu'), true);
});
