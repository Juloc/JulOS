#!/usr/bin/env python3
"""Erzeugt die App-Icons (PNG) aus der Logo-Form von JulOS: weisses, rundes Dreieck auf dem Verlauf lila -> blau.

Nur die Standardbibliothek (zlib, struct). Die SVG-Dateien (icon.svg, icon-maskable.svg) beschreiben dieselbe Form;
dieses Skript rastert sie fuer die Groessen, die Browser, Android und iOS als PNG verlangen. Aufruf aus dem Repo-Stamm:
    python3 tools/make-icons.py
"""
import math
import os
import struct
import zlib

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "JulOS.Frontend", "wwwroot")
A, B = (0xA0, 0x6C, 0xF0), (0x3F, 0x8C, 0xF5)     # Verlauf wie --dk-logo-a / --dk-logo-b der Desktop-Oberflaeche
TRI = ((128, 88), (408, 256), (128, 424))       # das Logo-Dreieck in einem 512er Raster
ROUND = 28.8                                    # halbe Strichstaerke: rundet die Ecken
SS = 4                                          # Unterabtastung je Richtung (Kantenglaettung)


def seg_dist(px, py, a, b):
    ax, ay, bx, by = a[0], a[1], b[0], b[1]
    dx, dy = bx - ax, by - ay
    t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)))
    return math.hypot(px - (ax + t * dx), py - (ay + t * dy))


def in_triangle(px, py, tri):
    s = [(tri[(i + 1) % 3][0] - tri[i][0]) * (py - tri[i][1]) - (tri[(i + 1) % 3][1] - tri[i][1]) * (px - tri[i][0]) for i in range(3)]
    return all(v >= 0 for v in s) or all(v <= 0 for v in s)


def in_rounded_rect(px, py, rx):
    if rx <= 0:
        return True
    cx = min(max(px, rx), 512 - rx)
    cy = min(max(py, rx), 512 - rx)
    return math.hypot(px - cx, py - cy) <= rx


def glyph_coverage(px, py, scale, shift):
    """Liegt (px, py) im runden Dreieck? Das Dreieck wird um die Mitte skaliert und um shift nach links gerueckt (optische Mitte)."""
    x = (px - 256) / scale + 256 - shift
    y = (py - 256) / scale + 256
    if in_triangle(x, y, TRI):
        return True
    return min(seg_dist(x, y, TRI[i], TRI[(i + 1) % 3]) for i in range(3)) <= ROUND


def render(size, rx, scale, shift=-12.0):
    rows = []
    step = 512 / size
    for j in range(size):
        row = bytearray([0])
        for i in range(size):
            r = g = b = a = 0
            for sj in range(SS):
                for si in range(SS):
                    px = (i + (si + .5) / SS) * step
                    py = (j + (sj + .5) / SS) * step
                    if not in_rounded_rect(px, py, rx):
                        continue
                    t = (px + py) / 1024
                    col = tuple(A[k] + (B[k] - A[k]) * t for k in range(3))
                    if glyph_coverage(px, py, scale, shift):
                        col = (255, 255, 255)
                    r += col[0]; g += col[1]; b += col[2]; a += 255
            n = SS * SS
            if a:
                cnt = a // 255
                row += bytes((round(r / cnt), round(g / cnt), round(b / cnt), round(a / n)))
            else:
                row += bytes((0, 0, 0, 0))
        rows.append(bytes(row))
    return rows


def png(size, rows):
    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)
    raw = b"".join(rows)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


# (Dateiname, Groesse, Eckenradius im 512er Raster, Skalierung des Zeichens)
# "maskable": randlos, das System schneidet selbst zu; das Zeichen bleibt im inneren Kreis (80 %).
# apple-touch: randlos und deckend, iOS rundet selbst.
JOBS = [
    ("icon-512.png", 512, 112, 1.0), ("icon-192.png", 192, 112, 1.0), ("icon-32.png", 32, 112, 1.0),
    ("icon-512-maskable.png", 512, 0, .72), ("icon-192-maskable.png", 192, 0, .72), ("icon-180.png", 180, 0, .84),
]

if __name__ == "__main__":
    for name, size, rx, scale in JOBS:
        data = png(size, render(size, rx, scale))
        with open(os.path.join(OUT, name), "wb") as f:
            f.write(data)
        print(f"{name}: {size}x{size}, {len(data)} Bytes")
