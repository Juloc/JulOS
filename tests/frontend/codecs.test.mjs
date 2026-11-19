// Codecs des Streams (codecs.js): Codec-Text aus dem SPS, Aushandlung. Dieselben Bytes wie CodecStringTests.cs auf dem Server.
import test from 'node:test';
import assert from 'node:assert/strict';
import { codecStringOf, codecMask, probeCodecs, decoderConfig, PROBES, CODEC_H264, CODEC_HEVC } from '../../src/JulOS.Frontend/wwwroot/codecs.js';

const nal = (...b) => Uint8Array.of(0, 0, 0, 1, ...b);
const join = (...parts) => Uint8Array.from(parts.flatMap((p) => [...p]));

test('h264: profile, constraints and level of the SPS', () => {
  const stream = join(nal(0x67, 0x64, 0x00, 0x28, 0xac, 0xd9, 0x40), nal(0x68, 0xee, 0x3c, 0x80), nal(0x65, 0x88));
  assert.equal(codecStringOf(stream, false), 'avc1.640028');
  assert.equal(codecStringOf(nal(0x67, 0x42, 0xc0, 0x1f, 0x00), false), 'avc1.42c01f');
  assert.equal(codecStringOf(Uint8Array.of(0, 0, 1, 0x09, 0x10, 0, 0, 1, 0x67, 0x4d, 0x40, 0x1e, 0x00), false), 'avc1.4d401e');   // three-byte start code, leading unit
});

test('h264: no SPS gives the default, and h265 reads a different unit type', () => {
  assert.equal(codecStringOf(nal(0x65, 0x88, 0x84), false), 'avc1.42e01f');
  assert.equal(codecStringOf(Uint8Array.of(), true), 'hvc1.1.6.L120.90');
});

test('h265: profile, compatibility, tier, level and constraints of the SPS', () => {
  // Main, level 4.1 (123), progressive and frame-only constraint bits
  const sps = Uint8Array.of(0, 0, 0, 1, 0x42, 0x01, 0x01, 0x01, 0x60, 0x00, 0x00, 0x00, 0x90, 0x00, 0x00, 0x00, 0x00, 0x00, 0x7b, 0xa0);
  assert.equal(codecStringOf(sps, true), 'hvc1.1.6.L123.90');
  // high tier, Main 10 (compatibility bit 2 alone, reversed: 4), level 5.1, no constraint bits
  const high = Uint8Array.of(0, 0, 0, 1, 0x42, 0x01, 0x01, 0x22, 0x20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x99, 0xa0);
  assert.equal(codecStringOf(high, true), 'hvc1.2.4.H153');
});

test('h265: emulation prevention bytes inside the profile part are skipped (the real stream has them)', () => {
  // an SPS as the server's HevcLayout writes it for 1920 x 1080 at level 4.0: the zero runs of the profile part carry 00 00 03
  const sps = Uint8Array.of(0, 0, 0, 1, 0x42, 0x01, 0x01, 0x01, 0x60, 0x00, 0x00, 0x03, 0x00, 0x90, 0x00, 0x00, 0x03, 0x00, 0x00, 0x03, 0x00, 0x78, 0xa0, 0x03, 0xc0, 0x80);
  assert.equal(codecStringOf(sps, true), 'hvc1.1.6.L120.90');
});

test('the codec mask always has H.264 and adds H.265 only when the browser can', () => {
  assert.equal(codecMask(null), CODEC_H264);
  assert.equal(codecMask({ h264: true, hevc: false }), 1);
  assert.equal(codecMask({ h264: true, hevc: true }), 3);
  assert.equal(codecMask({ h264: false, hevc: true }), 3);      // the server assumes H.264 anyway
});

test('probing asks for avc1 and hvc1 with hardware preferred for H.265 and survives a decoder that throws', async () => {
  const asked = [];
  const decoder = { isConfigSupported: async (c) => { asked.push(c); return { supported: c.codec.startsWith('avc1') }; } };
  assert.deepEqual(await probeCodecs(decoder), { h264: true, hevc: false });
  assert.equal(asked.length, 2);
  assert.match(asked[0].codec, /^avc1\./);
  assert.match(asked[1].codec, /^hvc1\./);
  assert.equal(asked[1].hardwareAcceleration, 'prefer-hardware');
  assert.equal(PROBES.hevc.hevc.format, 'annexb');
  assert.deepEqual(await probeCodecs({ isConfigSupported: async () => { throw new Error('nein'); } }), { h264: false, hevc: false });
  assert.deepEqual(await probeCodecs(undefined), { h264: false, hevc: false });
  assert.deepEqual(await probeCodecs({ isConfigSupported: async () => ({ supported: true }) }), { h264: true, hevc: true });
});

test('the decoder is configured from the codec text: annexb for both, hardware preferred for H.265', () => {
  assert.deepEqual(decoderConfig('avc1.640028'), { codec: 'avc1.640028', avc: { format: 'annexb' }, optimizeForLatency: true });
  const c = decoderConfig('hvc1.1.6.L123.90');
  assert.equal(c.hevc.format, 'annexb');
  assert.equal(c.hardwareAcceleration, 'prefer-hardware');
  assert.equal(c.avc, undefined);
});
