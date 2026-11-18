#!/usr/bin/env python3
"""JulOS: Zwischenablage-Helfer fuer Fotos.

Laeuft in der grafischen VM-Sitzung (DISPLAY :10). Beobachtet ~/julos-uploads: kommt dort ein Bild an
(vom Server hochgeladen), wird es in die Zwischenablage gelegt, sodass Strg+V in Claude & Co. es einfuegt.
Quittiert mit <datei>.ok bzw. <datei>.err. Alte Dateien werden nach 30 Minuten geloescht.
"""
import glob, os, sys, time
import gi
gi.require_version("Gtk", "3.0"); gi.require_version("Gdk", "3.0"); gi.require_version("GdkPixbuf", "2.0")
from gi.repository import Gtk, Gdk, GdkPixbuf, GLib

UP = os.path.expanduser("~/julos-uploads")
os.makedirs(UP, exist_ok=True)
clip = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
keep = [None]          # Bild im Speicher halten, solange wir Besitzer der Zwischenablage sind
SKIP = (".tmp", ".ok", ".err")


def scan():
    now = time.time()
    for f in sorted(glob.glob(os.path.join(UP, "*"))):
        if os.path.isdir(f) or f.endswith(SKIP):
            continue
        if now - os.path.getmtime(f) > 1800:
            for g in (f, f + ".ok", f + ".err"):
                try: os.unlink(g)
                except OSError: pass
            continue
        if os.path.exists(f + ".ok") or os.path.exists(f + ".err") or f in handled:
            continue
        handled.add(f)
        try:
            pb = GdkPixbuf.Pixbuf.new_from_file(f)
            clip.set_image(pb)
            keep[0] = pb
            open(f + ".ok", "w").close()
            print(time.strftime("%H:%M:%S"), "Zwischenablage gesetzt:", os.path.basename(f), pb.get_width(), "x", pb.get_height(), flush=True)
        except Exception as e:  # noqa: BLE001
            with open(f + ".err", "w") as fh:
                fh.write(str(e)[:120])
            print(time.strftime("%H:%M:%S"), "Fehler:", e, flush=True)
    return True


handled = set()
GLib.timeout_add(300, scan)
print("julos-clip laeuft, ueberwacht", UP, flush=True)
Gtk.main()
