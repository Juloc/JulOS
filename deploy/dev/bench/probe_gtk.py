import gi, sys, math, random, os
gi.require_version('Gtk','3.0'); gi.require_version('GdkPixbuf','2.0')
from gi.repository import Gtk, Gdk, GLib, GdkPixbuf
# Messfenster: Quadrat rot/gruen, schaltet bei Klick; plus ein bewegtes Bild (Last) wie ein Video
css = Gtk.CssProvider(); css.load_from_data(b'.r{background:#ff0000} .g{background:#00ff00} .d{background:#202020}')
Gtk.StyleContext.add_provider_for_screen(Gdk.Screen.get_default(), css, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)
w = Gtk.Window(type=Gtk.WindowType.POPUP); w.move(620, 90); w.resize(140, 160)
box = Gtk.EventBox(); box.get_style_context().add_class('d'); box.set_size_request(140, 160)
sq = Gtk.EventBox(); sq.set_size_request(120, 120); sq.get_style_context().add_class('r')
fix = Gtk.Fixed(); fix.put(sq, 0, 0); box.add(fix); w.add(box)
state = {'on': False}
def press(_w, _e):
    state['on'] = not state['on']; ctx = sq.get_style_context()
    ctx.remove_class('r' if state['on'] else 'g'); ctx.add_class('g' if state['on'] else 'r'); return True
box.add_events(Gdk.EventMask.BUTTON_PRESS_MASK); sq.add_events(Gdk.EventMask.BUTTON_PRESS_MASK)
box.connect('button-press-event', press); sq.connect('button-press-event', press); w.show_all()
NOISE = len(sys.argv) > 2 and sys.argv[2] == 'noise'
if len(sys.argv) > 1 and sys.argv[1] == 'video':
    v = Gtk.Window(type=Gtk.WindowType.POPUP); v.move(200, 330)
    W, H = (320, 180) if os.environ.get('TICK') else (480, 270)
    row = bytes((int(127+127*math.sin(x*0.05)), int(127+127*math.sin(x*0.031+2)), int(127+127*math.sin(x*0.017+4))) [i] for x in range(W*3) for i in range(3))
    img = Gtk.Image(); v.add(img); v.show_all(); t = {'n': 0}
    def tick():
        t['n'] += 7; off = (t['n'] % W) * 3
        data = os.urandom(W * H * 3) if NOISE else b''.join(row[off + (y % 7) * 3: off + (y % 7) * 3 + W * 3] for y in range(H))
        img.set_from_pixbuf(GdkPixbuf.Pixbuf.new_from_bytes(GLib.Bytes.new(data), GdkPixbuf.Colorspace.RGB, False, 8, W, H, W * 3)); return True
    GLib.timeout_add(int(os.environ.get("TICK","33")), tick)
Gtk.main()
