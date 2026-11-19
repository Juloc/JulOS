import gi, sys
gi.require_version('Gtk','3.0')
from gi.repository import Gtk, Pango
w = Gtk.Window(title='editor'); w.move(int(sys.argv[2]) if len(sys.argv)>2 else 20, 20); w.resize(480, 280)
tv = Gtk.TextView(); tv.set_editable(False); tv.set_cursor_visible(False)
tv.override_font(Pango.FontDescription(sys.argv[1] if len(sys.argv)>1 else 'Monospace 11'))
b = tv.get_buffer()
tags = {k: b.create_tag(k, foreground=v) for k, v in dict(kw='#0000ff', st='#008000', cm='#808080', rd='#ff0000', mg='#c000c0', bk='#000000', org='#e07000').items()}
lines = [[('kw','def '),('bk','render'),('bk','(frame, '),('org','size'),('bk','='),('mg','1024'),('bk','):')],
 [('cm','    # chroma of colored text is the hard part')],
 [('bk','    '),('kw','for '),('bk','row '),('kw','in '),('bk','range(frame.height):')],
 [('bk','        text = '),('st','"Hello, wörld! 0123456789"')],
 [('bk','        '),('rd','raise '),('bk','ValueError(text)')],
 [('bk','    '),('kw','return '),('mg','None'),('bk','  '),('cm','# fi fl ffi il1I O0o')]]
for l in lines:
    for t, s in l: b.insert_with_tags(b.get_end_iter(), s, tags[t])
    b.insert(b.get_end_iter(), '\n')
w.add(tv); w.connect('destroy', Gtk.main_quit); w.show_all(); Gtk.main()
