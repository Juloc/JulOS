/* =====================================================================
 * Codecs des Bildschirm-Streams (H.264 und H.265): was der Browser dekodieren kann, und der Codec-Text aus den Parametersaetzen im Strom.
 * Reine Funktionen, ohne Browser testbar (tests/frontend/codecs.test.mjs, dieselben Bytes wie CodecStringTests auf dem Server).
 * ===================================================================== */

export const CODEC_H264 = 1, CODEC_HEVC = 2;      // Bits der Codec-Menge (ScreenProtocol: Adresse "codecs=" und Nachricht 30)

// Mit diesen Einstellungen prueft der Browser, ob er dekodieren kann. H.265 nur, wenn der Browser es mit Hardware kann: eine Software-Dekodierung
// kaeme bei dieser Bildrate nicht mit. Die Stufe 4.0 steht fuer "ein uebliches Desktop-Bild"; was der Strom dann wirklich hat, steht in seinem SPS.
export const PROBES = {
  h264: { codec: 'avc1.42e01f', avc: { format: 'annexb' }, optimizeForLatency: true },
  hevc: { codec: 'hvc1.1.6.L120.90', hevc: { format: 'annexb' }, hardwareAcceleration: 'prefer-hardware', optimizeForLatency: true },
};

/** Die Codec-Menge als Zahl fuer den Server: { h264, hevc } -> 1, 2 oder 3 (nie 0: H.264 gilt immer als Untergrenze, so wie der Server es annimmt). */
export function codecMask(support) {
  return (support && support.hevc ? CODEC_HEVC : 0) | CODEC_H264;
}

/** Was VideoDecoder.isConfigSupported fuer die beiden Codecs sagt. `decoder` ist die Klasse (zum Testen austauschbar). Wirft nie. */
export async function probeCodecs(decoder) {
  const result = { h264: false, hevc: false };
  if (!decoder || typeof decoder.isConfigSupported !== 'function') return result;
  for (const key of ['h264', 'hevc']) {
    try { result[key] = !!(await decoder.isConfigSupported(PROBES[key])).supported; } catch { result[key] = false; }
  }
  return result;
}

// Die Einheiten (NAL) eines Annex-B-Stroms in den ersten Bytes: [{ type-Byte-Position, Daten-Position }]
function* startCodes(bytes, limit = 256) {
  const end = Math.min(bytes.length - 4, limit);
  for (let i = 0; i < end; i++) {
    if (bytes[i] === 0 && bytes[i + 1] === 0 && bytes[i + 2] === 1) yield i + 3;
  }
}

const hex = (b) => b.toString(16).padStart(2, '0');

/**
 * Codec-Text aus dem SPS im Schluesselbild: "avc1.640028" fuer H.264, "hvc1.1.6.L123.90" fuer H.265.
 * Ohne lesbaren SPS ein Standardwert (der Dekodierer meldet dann selbst, wenn der Strom nicht passt).
 */
export function codecStringOf(bytes, hevc) {
  for (const at of startCodes(bytes)) {
    if (!hevc) {
      if ((bytes[at] & 0x1f) === 7 && at + 3 < bytes.length) return 'avc1.' + hex(bytes[at + 1]) + hex(bytes[at + 2]) + hex(bytes[at + 3]);
    } else if (((bytes[at] >> 1) & 0x3f) === 33) {
      const text = hevcString(bytes, at + 2);
      if (text) return text;
    }
  }
  return hevc ? 'hvc1.1.6.L120.90' : 'avc1.42e01f';
}

// SPS von H.265: sps_video_parameter_set_id(4) max_sub_layers(3) nesting(1), dann profile_tier_level: Profilraum(2) Stufe(1) Profil(5),
// 32 Kompatibilitaetsbits, 6 Bytes Einschraenkungen, Level. Dreizehn Bytes nach dem NAL-Kopf, ohne die Schutzbytes (00 00 03).
function hevcString(bytes, from) {
  const r = [];
  let zeros = 0;
  for (let i = from; i < bytes.length && r.length < 13; i++) {
    const b = bytes[i];
    if (zeros >= 2 && b === 3) { zeros = 0; continue; }
    r.push(b);
    zeros = b === 0 ? zeros + 1 : 0;
  }
  if (r.length < 13) return null;
  const space = r[1] >> 6, tier = (r[1] >> 5) & 1, profile = r[1] & 0x1f;
  const compat = ((r[2] << 24) | (r[3] << 16) | (r[4] << 8) | r[5]) >>> 0;
  let reversed = 0;
  for (let bit = 0; bit < 32; bit++) reversed = (reversed | (((compat >>> bit) & 1) << (31 - bit))) >>> 0;
  let last = 5;
  while (last >= 0 && r[6 + last] === 0) last--;
  let text = 'hvc1.' + (space ? String.fromCharCode(64 + space) : '') + profile + '.' + reversed.toString(16).toUpperCase() + '.' + (tier ? 'H' : 'L') + r[12];
  for (let i = 0; i <= last; i++) text += '.' + r[6 + i].toString(16).toUpperCase();
  return text;
}

/** Die Konfiguration fuer VideoDecoder.configure() zu einem Codec-Text. */
export function decoderConfig(codec) {
  return codec.startsWith('hvc1') || codec.startsWith('hev1')
    ? { codec, hevc: { format: 'annexb' }, hardwareAcceleration: 'prefer-hardware', optimizeForLatency: true }
    : { codec, avc: { format: 'annexb' }, optimizeForLatency: true };
}
