#!/usr/bin/env python3
"""JulOS: Zwischenablage-Helfer fuer Fotos und Dateien.

Laeuft in der grafischen VM-Sitzung (DISPLAY :10). Beobachtet ~/julos-uploads:
- kommt dort ein Bild an (vom Server hochgeladen), wird es in die Zwischenablage gelegt, sodass Strg+V in Claude & Co. es einfuegt.
  Quittiert mit <datei>.ok bzw. <datei>.err. Alte Dateien werden nach 30 Minuten geloescht.
- files/<id>/<name>: eine Datei fuer die Zwischenablage. Sie wird als Dateiliste abgelegt (Strg+V im Dateimanager kopiert sie), die
  Datei bleibt im Ordner liegen (7 Tage). Quittiert mit .ok bzw. .err im Ordner <id>.
- pull/<id>.req: der Server will das Bild aus der Zwischenablage der VM. Antwort <id>.png (nur Bilder, nie Dateien oder Pfade),
  <id>.none oder <id>.err.
"""
import ctypes, glob, os, shutil, sys, time
import gi
gi.require_version("Gtk", "3.0"); gi.require_version("Gdk", "3.0"); gi.require_version("GdkPixbuf", "2.0")
from gi.repository import Gtk, Gdk, GdkPixbuf, GLib

UP = os.path.expanduser("~/julos-uploads")
os.makedirs(UP, exist_ok=True)
clip = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
keep = [None]          # Bild im Speicher halten, solange wir Besitzer der Zwischenablage sind
SKIP = (".tmp", ".ok", ".err")
FILES = os.path.join(UP, "files")
PULL = os.path.join(UP, "pull")
os.makedirs(FILES, exist_ok=True)
os.makedirs(PULL, exist_ok=True)
FILE_KEEP = 7 * 24 * 3600
PULL_MAX = 14 * 1024 * 1024
files_keep = [None]    # die abgelegte Dateiliste, solange wir Besitzer der Zwischenablage sind


# PyGObject kennt gtk_clipboard_set_with_data nicht (Rueckruf mit Zerstoerer): direkt aus libgtk aufrufen
_gtk = ctypes.CDLL("libgtk-3.so.0")
_gdk = ctypes.CDLL("libgdk-3.so.0")


class _TargetEntry(ctypes.Structure):
    _fields_ = [("target", ctypes.c_char_p), ("flags", ctypes.c_uint), ("info", ctypes.c_uint)]


_GetFunc = ctypes.CFUNCTYPE(None, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint, ctypes.c_void_p)
_ClearFunc = ctypes.CFUNCTYPE(None, ctypes.c_void_p, ctypes.c_void_p)
_gdk.gdk_atom_intern.restype = ctypes.c_void_p
_gdk.gdk_atom_intern.argtypes = [ctypes.c_char_p, ctypes.c_int]
_gtk.gtk_clipboard_set_with_data.restype = ctypes.c_int
_gtk.gtk_clipboard_set_with_data.argtypes = [ctypes.c_void_p, ctypes.POINTER(_TargetEntry), ctypes.c_uint, _GetFunc, _ClearFunc, ctypes.c_void_p]
_gtk.gtk_selection_data_set.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int, ctypes.c_char_p, ctypes.c_int]
_COPIED = b"x-special/gnome-copied-files"


def set_file_clipboard(path):
    uri = GLib.filename_to_uri(path, None).encode()
    targets = (_TargetEntry * 2)(_TargetEntry(_COPIED, 0, 1), _TargetEntry(b"text/uri-list", 0, 2))

    def get(_cb, sel, info, _data):
        body = b"copy\n" + uri if info == 1 else uri + b"\r\n"
        _gtk.gtk_selection_data_set(sel, _gdk.gdk_atom_intern(_COPIED if info == 1 else b"text/uri-list", 0), 8, body, len(body))

    def clear(_cb, _data):
        pass

    # die Rueckrufe muessen am Leben bleiben, solange wir Besitzer sind (der naechste Aufruf ersetzt sie erst, nachdem GTK die alten freigegeben hat)
    get_cb, clear_cb = _GetFunc(get), _ClearFunc(clear)
    if not _gtk.gtk_clipboard_set_with_data(ctypes.c_void_p(hash(clip)), targets, 2, get_cb, clear_cb, None):
        raise RuntimeError("Zwischenablage nicht beschreibbar")
    files_keep[0] = (get_cb, clear_cb, targets)


def scan_files():
    now = time.time()
    for d in sorted(glob.glob(os.path.join(FILES, "*"))):
        if not os.path.isdir(d) or d.endswith(".tmp"):
            continue
        if now - os.path.getmtime(d) > FILE_KEEP:
            shutil.rmtree(d, ignore_errors=True)
            continue
        if os.path.exists(os.path.join(d, ".ok")) or os.path.exists(os.path.join(d, ".err")) or d in handled:
            continue
        handled.add(d)
        try:
            names = [n for n in os.listdir(d) if not n.startswith(".")]
            if len(names) != 1:
                raise RuntimeError("erwartet genau eine Datei")
            path = os.path.realpath(os.path.join(d, names[0]))
            if not path.startswith(os.path.realpath(d) + os.sep) or not os.path.isfile(path):
                raise RuntimeError("ungueltiger Pfad")
            set_file_clipboard(path)
            open(os.path.join(d, ".ok"), "w").close()
            print(time.strftime("%H:%M:%S"), "Datei in der Zwischenablage:", names[0], flush=True)
        except Exception as e:  # noqa: BLE001
            with open(os.path.join(d, ".err"), "w") as fh:
                fh.write(str(e)[:120])
            print(time.strftime("%H:%M:%S"), "Fehler:", e, flush=True)


def scan_pull():
    now = time.time()
    for r in sorted(glob.glob(os.path.join(PULL, "*"))):
        if now - os.path.getmtime(r) > 300:
            try: os.unlink(r)
            except OSError: pass
            continue
        if not r.endswith(".req") or r in handled:
            continue
        handled.add(r)
        base = r[:-4]
        try:
            pb = clip.wait_for_image()
            if pb is None:
                open(base + ".none", "w").close()
            else:
                tmp = base + ".png.tmp"
                pb.savev(tmp, "png", [], [])
                if os.path.getsize(tmp) > PULL_MAX:
                    os.unlink(tmp)
                    raise RuntimeError("Bild zu gross")
                os.rename(tmp, base + ".png")
            print(time.strftime("%H:%M:%S"), "Bild aus der Zwischenablage geholt:", "ja" if pb else "keins", flush=True)
        except Exception as e:  # noqa: BLE001
            with open(base + ".err", "w") as fh:
                fh.write(str(e)[:120])
            print(time.strftime("%H:%M:%S"), "Fehler:", e, flush=True)


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
    scan_files()
    scan_pull()
    return True


handled = set()
GLib.timeout_add(300, scan)
print("julos-clip laeuft, ueberwacht", UP, flush=True)
Gtk.main()
