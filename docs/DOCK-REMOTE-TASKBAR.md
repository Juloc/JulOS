# Dock with the taskbar of the remote desktop (plan, not built)

Status: **planned**. Wish of the owner, 2026-11-21. Part of the dock work in [BACKLOG.md](BACKLOG.md) (thin pills) and of the window manager in [MULTI-SCREEN.md](MULTI-SCREEN.md). Nothing is built yet.

## What the owner wants

While connected to a Linux desktop over RDP, the **taskbar of that desktop is built into the JulOS dock**: the JulOS dock stays **narrow**, and next to its pills a **clearly recognisable area** shows the taskbar of the VM. Nothing on the host may be broken or changed for this.

## Idea: the taskbar is a slice of the same picture

The remote picture is one canvas ([stream.js](../src/JulOS.Frontend/wwwroot/stream.js), `display`). The taskbar is just the bottom (or top, left, right) strip of that picture, `P` pixels high. JulOS draws the picture in two places:

1. **Work area:** the picture without the strip, in the window as today.
2. **Dock:** the strip, cut out of the same picture with `drawImage` (source rectangle), placed in the dock, **framed and labelled** (colour of the connection, name tag "ubuntu-dev · RDP").

The remote desktop is asked for `width x (window height + P)` (the existing `sendSize`), so it sees an ordinary screen and its panel stays where it is: no setting changed, no process touched, no package installed. A click in the strip is sent to the VM with the y coordinate shifted back to where the strip really is, so the start menu, tray, window buttons, right click and tooltips work as on a real screen. Menus that open upward appear in the work area above, which is part of the same picture.

It is a view, not an emulation: if the VM changes its panel (theme, size), the strip shows it at once.

## Where `P` and the side come from

Three sources, in this order; the first that works wins and the choice is stored **per connection**:

1. **By hand, once (works everywhere, also Windows and Wayland):** "Mark the taskbar" in the connection menu; the owner drags a rectangle over the panel on the live picture; JulOS stores side and size.
2. **Agent (Linux X11, nice to have):** the desktop agent already runs on the VM (see [HOST-AGENT.md](HOST-AGENT.md)); it reads the work area and struts of the panel (`_NET_WORKAREA`, `_NET_WM_STRUT_PARTIAL`) and reports side and size, also when they change. No change on the host, only reading.
3. **Off:** no strip (panel hidden or auto-hiding): the dock shows the thin pills only, and a normal remote window fills the area.

Optionally later: the agent also reports the **list of open windows** (title, icon, active) so JulOS can draw its own pills for them and cut the panel out completely. That is a second step and a free choice, never required.

## What the dock looks like

See the mockup in the conversation (three states):

- **No remote window focused:** only thin pills (5 px), as in the backlog item.
- **Remote window focused:** a dock of about the height of the VM taskbar. On the left the JulOS part (logo, thin pills), then a separator, then the **framed strip** with a coloured border and a small tag with the connection name. The frame colour is the colour of the connection, the same on its window title bar, so it is always clear which panel belongs to which VM.
- **Mouse at the lower edge:** the dock unfolds (names under the pills, the focused connection named), the strip stays.
- **Two remote windows:** each focused window shows its own strip; switching focus switches the strip. Never two strips at once.
- **Panel on top or at the side:** the strip follows the real side: the dock is thin on the bottom and the strip appears at the top edge of the window, or as a narrow bar on the side. Default is the bottom.

## Important points

- **Non-destructive:** no write on the host in any step. Hiding or auto-hiding the real panel is **not** done by JulOS (a setting for it may come later as an explicit, reversible action with the confirmation flow of [HOST-AGENT.md](HOST-AGENT.md)).
- **Seamless:** one picture, one decoder, the same encoded video; the strip is drawn from the already decoded frame. No second stream, no extra load on the VM, only a second `drawImage` per frame in the browser.
- **Size change:** window resize, full screen and DPI scale change `width x (height + P)` in one step; the slice is recomputed in the page. Fractional scale: the strip is cut on whole source pixels, scaled like the picture.
- **Input:** pointer events in the dock strip are mapped to the VM's coordinates (pure function, tested). Keyboard focus stays with the remote window; mouse wheel and drag work as in the work area. A drag that starts in the strip stays captured until the button is released.
- **Touch (phone):** the phone page has no desktop dock and keeps its own layout; the strip is a desktop feature.
- **Several screens:** when [MULTI-SCREEN.md](MULTI-SCREEN.md) lands, the strip belongs to the browser window that shows the lower edge of that VM's picture; the slice maths is the same as the viewport maths of step 3 there.
- **Auto-hiding or missing panel:** nothing to cut; the dock shows only pills; the connection menu offers "Mark the taskbar" if the owner changes the VM.
- **Windows RDP hosts:** the same, with the taskbar strip (about 48 px at 100 %); works through "Mark the taskbar" or, with a future Windows agent, automatically.
- **Accessibility and clarity:** the frame has a visible name and a contrast border (not only a colour); the dock has an accessible name per area.
- **Settings:** per connection: use the taskbar in the dock yes or no (default yes once a strip is known), side and size, frame colour. Stored with the connection (and later per device, see MULTI-SCREEN).

## Steps (each releasable)

1. **Slice in the page:** "Mark the taskbar" by hand, strip in the dock, input mapping, resize to `height + P`, per connection. M. Pure functions (slice and coordinate mapping) get a small test; the rest is checked by running it against a real RDP VM.
2. **Agent detection** of side and size (X11), updates on change. S to M, together with an agent version bump.
3. **Window list from the agent** as JulOS pills, panel cut out completely (optional). M to L, only if wanted.

## Open questions

- Should the strip show in the dock **always** while a remote window is focused, or only when the pointer is near the lower edge (like an auto-hide)? Default proposal: always.
- With two VMs open side by side: the strip of the focused one only (proposal), or one strip per visible window?
- Is a second step with JulOS-drawn pills for remote windows wanted, or is the real panel enough?
