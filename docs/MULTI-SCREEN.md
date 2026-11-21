# One desktop over several browser windows (plan, not built)

Status: **planned**. Decisions of the owner on 2026-11-21 (interview). Nothing is built yet; the order of work is at the end.

## What the owner wants

- **One big desktop over several browser windows**, like extended monitors: one browser window shows the left part of the same surface, a second one the right part. Windows and programs move freely between them. There is no "send to screen 2" step: a window can **lie across the border** and then **each browser window shows its half, seamlessly**, also for a running remote screen (RDP, VNC).
- **Moving and sizing like Windows**: drag a window (also over the edge of a browser window), snap to edges and corners, and keep working with the **keyboard** (move, size, half, quarter, maximise, restore). The Windows key and the arrow keys are the model.
- **The mouse buttons 4 and 5** (browser back and forward) go to JulOS and not to the real browser: they walk back and forward inside the focused JulOS window (for example the Files explorer), never away from the page.
- **Memory per device**: which screens exist, which connections are open and where, position and size of every window. The next time the same device opens JulOS, everything is where it was. **Continuing on the phone** works per connection.
- The dock folds into **thin pills** (about 5 px high, one per running program) and unfolds when hovered (see the backlog). Icons of the programs come automatically from the programs; the surface (theme, accent, wallpaper, dock) is customisable.

## What this needs (design)

1. **Device and workspace on the server.** Every browser gets a device id (kept locally and registered with the account, see "Devices and sessions"). The server stores one **workspace** per user and device: the list of screens (browser windows with their size), and the list of windows (kind: app or connection, connection id, geometry in desktop coordinates, stacking order, state). A change is sent over one WebSocket and written to the database; all windows of the device (and the same account on another device, if the owner wants to continue there) follow.
2. **One coordinate system for all browser windows.** A browser window knows its place on the real monitors (`window.screenX`, `screenY`, `outerWidth`, `innerWidth`, the browser frame offset) and reports it; the desktop is the union of these rectangles in screen coordinates. So the arrangement comes **from where the owner really put the browser windows**, no setup. Moving a browser window moves its part of the desktop. Browsers on one device share state without delay through `BroadcastChannel`, the server is the memory and the bridge to other devices.
3. **Dragging across the border.** While the button is held, pointer capture keeps delivering the pointer in global screen coordinates (`screenX`, `screenY`) even outside the page; every window computes where the drag is in its own area and draws the window part that lies inside. The window manager of JulOS (not the browser) owns all of this, including snapping.
4. **Keyboard.** The Windows key is reserved by the operating system; in full screen the Keyboard Lock API can take it (the code already does that for the remote windows). Without full screen, an alternative modifier (for example Ctrl+Alt+arrows) moves and snaps the active window. Snap zones: left and right half, quarters, maximise, restore, also across screens. The key bindings are settings, not constants.
5. **Mouse buttons 4 and 5.** The page handles `mousedown`/`mouseup` for the buttons 3 and 4, prevents the browser default and sends back and forward to the focused JulOS window; no `history` entries are added for window navigation.
6. **Streams across the border (the hard part).** Today a connection has one viewer per user; a second browser takes it over (status 777). New: **several viewers of one session**. The server sends the same encoded video to every viewer of the connection, each viewer gets a keyframe when it joins, and each browser window draws the whole picture offset so that only its part is visible. Input comes from the window under the pointer; clipboard and sound belong to the focused viewer. Each viewer has its own back-pressure, so a slow window cannot slow the others. Measure the extra cost (encoding is shared, only the sending multiplies).
7. **Continue on the phone.** The phone is not part of the spanned desktop. "Continue here" opens the same connection on the phone as a viewer or takes it over (the owner's choice per connection); the window state is read from the workspace.

## Order of work (each step releasable on its own)

1. **Window manager in one browser window:** keyboard move, size and snap, mouse buttons 4 and 5, thin dock pills, workspace per device saved on the server and restored (screens, open connections, geometry).
2. **Several browser windows, apps without a stream first** (Files explorer, Hosts, Settings): shared workspace, global coordinates from the real window positions, drag across the border, one window shown in two browsers.
3. **Several viewers per connection** (engine and relay change) so that a remote screen can lie across the border; shared pacing; input and clipboard owner.
4. **Continue on the phone** and across devices.

Effort: step 1 M to L, step 2 L, step 3 L to XL (engine), step 4 M. Tests: pure functions for the coordinate mapping, snapping and the visible part of a window; the multi-viewer relay with the existing fake targets; everything else is checked by running it (several real browser windows).

## Open questions

- What should happen when a browser window is closed with windows lying in it (they stay on the remaining screens, moved inside the visible area)?
- How much lag between the two halves of a stream is acceptable (a few frames)?
- Should a second device see the same open windows at once or only on request ("continue here")?
