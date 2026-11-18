# Messfenster: ein Quadrat (rot/gruen), das bei Klick oder Taste umschaltet. Aufruf: probe.py X Y
import sys, tkinter as tk
X0 = int(sys.argv[1]) if len(sys.argv) > 1 else 700
Y0 = int(sys.argv[2]) if len(sys.argv) > 2 else 20
root = tk.Tk(); root.overrideredirect(True); root.geometry(f'140x160+{X0}+{Y0}')
c = tk.Canvas(root, width=140, height=160, highlightthickness=0, bg='#202020'); c.pack()
probe = c.create_rectangle(0, 0, 120, 120, fill='#ff0000', outline='')
state = {'on': False}
def toggle(_e=None):
    state['on'] = not state['on']
    c.itemconfig(probe, fill='#00ff00' if state['on'] else '#ff0000')
root.bind('<Button-1>', toggle); root.bind('<Key>', toggle); root.focus_force()
root.mainloop()
