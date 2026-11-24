#!/usr/bin/env python3
"""Nur fuer das Wegwerf-Ziel der Test-Instanz: ein kleines Fenster mit einem Eingabefeld (Titel "clip-entry", Inhalt aus dem ersten Argument), damit die
Browser-Tests Markieren (Umschalt+Pfeile), Strg+C und Strg+V gegen ein echtes Anwendungsfenster der VM pruefen koennen."""
import sys
import gi
gi.require_version("Gtk", "3.0")
from gi.repository import Gtk

win = Gtk.Window(title="clip-entry")
win.set_default_size(420, 60)
entry = Gtk.Entry()
entry.set_text(sys.argv[1] if len(sys.argv) > 1 else "")
# der Inhalt steht fuer die Browser-Tests in einer Datei (clip-probe: GET /entry-text), damit sie pruefen koennen, WAS beim Einfuegen angekommen ist
entry.connect("changed", lambda e: open("/tmp/clip-entry.txt", "w", encoding="utf-8").write(e.get_text()))
win.add(entry)
win.connect("destroy", Gtk.main_quit)
win.show_all()
entry.grab_focus()
entry.set_position(-1)
Gtk.main()
