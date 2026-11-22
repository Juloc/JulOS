# One desktop over several browser windows (plan; workspace server built)

Status: **step 1 partly built** (not released): the interface part of the window manager in one browser window (keyboard, snapping with the mouse, mouse buttons 4 and 5, thin dock pills; see "Step 1: what is built" below) and the server side of the workspace (see "Workspace on the server"). **Not built yet:** the connection of the interface to the workspace (loading and saving it from the page) and steps 2 to 4. Decisions of the owner on 2026-11-21 (interview); the order of work is at the end.

## What the owner wants

- **One big desktop over several browser windows**, like extended monitors: one browser window shows the left part of the same surface, a second one the right part. Windows and programs move freely between them. There is no "send to screen 2" step: a window can **lie across the border** and then **each browser window shows its half, seamlessly**, also for a running remote screen (RDP, VNC).
- **Moving and sizing like Windows**: drag a window (also over the edge of a browser window), snap to edges and corners, and keep working with the **keyboard** (move, size, half, quarter, maximise, restore). The Windows key and the arrow keys are the model.
- **The mouse buttons 4 and 5** (browser back and forward) go to JulOS and not to the real browser: they walk back and forward inside the focused JulOS window (for example the Files explorer), never away from the page.
- **Memory per device**: which screens exist, which connections are open and where, position and size of every window. The next time the same device opens JulOS, everything is where it was. **Continuing on the phone** works per connection.
- The dock folds into **thin pills** (about 5 px high, one per running program) and unfolds when hovered (see the backlog). Icons of the programs come automatically from the programs; the surface (theme, accent, wallpaper, dock) is customisable.

## What this needs (design)

1. **Device and workspace on the server.** Every browser makes up a device id (random, kept locally) and uses it for its workspace; the server stores one **workspace** per user and device: the list of screens (browser windows with their size), and the list of windows (kind: app or connection, connection id, geometry in desktop coordinates, stacking order, state). The browser saves the whole document after a change (debounced) and reads it when it opens. Browser windows of one device share state through `BroadcastChannel` and use the version check below against overwriting each other; the server is the memory and the list of devices for "continue on the phone". Built, see the next section. (A WebSocket for live updates to other devices is not part of it; polling the read endpoint is enough until that is wanted.)
2. **One coordinate system for all browser windows.** A browser window knows its place on the real monitors (`window.screenX`, `screenY`, `outerWidth`, `innerWidth`, the browser frame offset) and reports it; the desktop is the union of these rectangles in screen coordinates. So the arrangement comes **from where the owner really put the browser windows**, no setup. Moving a browser window moves its part of the desktop. Browsers on one device share state without delay through `BroadcastChannel`, the server is the memory and the bridge to other devices.
3. **Dragging across the border.** While the button is held, pointer capture keeps delivering the pointer in global screen coordinates (`screenX`, `screenY`) even outside the page; every window computes where the drag is in its own area and draws the window part that lies inside. The window manager of JulOS (not the browser) owns all of this, including snapping.
4. **Keyboard.** The Windows key is reserved by the operating system; in full screen the Keyboard Lock API can take it (the code already does that for the remote windows). Without full screen, an alternative modifier (for example Ctrl+Alt+arrows) moves and snaps the active window. Snap zones: left and right half, quarters, maximise, restore, also across screens. The key bindings are settings, not constants.
5. **Mouse buttons 4 and 5.** The page handles `mousedown`/`mouseup` for the buttons 3 and 4, prevents the browser default and sends back and forward to the focused JulOS window; no `history` entries are added for window navigation.
6. **Streams across the border (the hard part).** Today a connection has one viewer per user; a second browser takes it over (status 777). New: **several viewers of one session**. The server sends the same encoded video to every viewer of the connection, each viewer gets a keyframe when it joins, and each browser window draws the whole picture offset so that only its part is visible. Input comes from the window under the pointer; clipboard and sound belong to the focused viewer. Each viewer has its own back-pressure, so a slow window cannot slow the others. Measure the extra cost (encoding is shared, only the sending multiplies).
7. **Continue on the phone.** The phone is not part of the spanned desktop. "Continue here" opens the same connection on the phone as a viewer or takes it over (the owner's choice per connection); the window state is read from the workspace.

## Workspace on the server (built, no interface yet)

**Storage:** an own table `workspaces (user_id, device_id, name, doc jsonb, version, created_at, updated_at)` (migration `023_workspaces.sql`, primary key user and device), not the per-user settings store. The settings store has a fixed list of keys that `GET /api/settings` sends to every client, no version column and no list of devices; a key per device would bend all three. The table needs nothing else, and it is in the backup like every table (`DbDump` takes all of them; a test restores a workspace) and goes away with its user (`on delete cascade`).

**The device id** comes from the browser (`[A-Za-z0-9_-]{8,64}`, random, kept locally). It is no secret and opens nothing: every request needs a session, and every query has the user in its condition, so a foreign user with the same id gets 404 and an own empty row, never the other's data. At most **16 devices per user**; a new one above that replaces the one unused longest (browsers that clear their data would otherwise only leave dead rows).

| Route | What |
| --- | --- |
| `GET /api/workspaces` | `{ devices: [{ deviceId, name, version, updatedAt, windows, connections: [id] }] }`, most recently saved first. `connections` are the open connections of that device (only the user's own that still exist): what "continue here" picks from. |
| `GET /api/workspaces/{deviceId}` | `{ deviceId, name, version, updatedAt, doc }`, `404 not_found` if nothing is saved. Windows of deleted connections are left out. |
| `PUT /api/workspaces/{deviceId}` | body `{ version, name?, doc }`; answer `{ version, updatedAt, dropped }`. `version` is the version the browser based its document on, `0` for "I have none". `name` (40 characters) is the display name of the device, kept when omitted. |
| `DELETE /api/workspaces/{deviceId}` | forget the device: `204`, also when there was nothing |

**Errors** (body `{ error, code }`): `400 invalid_device` (bad id), `400 invalid_request` (version or doc missing, version below 0), `400 invalid_doc` (the message says which field), `401` without session, `403` without the `X-JulOS` header or from another origin, `409 workspace_conflict`, `413` above 64 KiB, `429 rate_limited` (writes 120 per minute and user, reads 240).

**Version check (no overwriting by a second browser window):** saving is one SQL statement that only writes when the stored version equals `version`, and then counts up; of several writers with the same version exactly one wins. A stale writer gets `409 workspace_conflict` with `{ version, updatedAt, doc }` of the current state (`version: 0` and no `doc` when the device was forgotten in the meantime). It is last-write-wins with a hint: the client may adopt the returned document, or repeat its own save with the returned `version` to overwrite. A save with `version: 0` while the device already exists gets the same 409.

**Document** (checked and rewritten like `SettingsRules`: unknown fields are dropped, wrong types or values outside a list are `invalid_doc`, numbers are rounded and limited to their range, ids and names are patterns; no credentials, no targets, only connection ids):

```json
{ "screens": [{ "id": "s1", "x": 0, "y": 0, "w": 1920, "h": 1080 }],
  "windows": [{ "id": "w1", "kind": "app", "app": "files", "x": 10, "y": 20, "w": 800, "h": 600, "z": 3, "state": "normal" },
              { "id": "w2", "kind": "connection", "connectionId": "<uuid>", "x": 0, "y": 0, "w": 1920, "h": 1080, "z": 4, "state": "snapped", "snap": "left",
                "restore": { "x": 100, "y": 100, "w": 800, "h": 600 } }],
  "focus": "w2",
  "dock": { "collapsed": false, "side": "bottom", "order": ["w1", "w2"] } }
```

- Everything is in desktop coordinates (the union of the screens). `x`, `y` are limited to ±100000, `w`, `h` to 1 to 100000, `z` to 0 to 100000; all whole numbers on disk.
- `id` of screens and windows: `[A-Za-z0-9_:.-]{1,64}`, unique in the document. `kind` is `app` (needs `app`, `[a-z0-9_-]{1,30}`) or `connection` (needs `connectionId`, a UUID). `state` is `normal`, `min`, `max` or `snapped`; `snap` is a free token naming the zone (`[a-z0-9_-]{1,30}`) and is only stored. `restore` (optional) is the rectangle to go back to from `max` or `snapped`.
- Limits: 32 windows, 8 screens, 64 entries in `dock.order`, 64 KiB per document (the 64 KiB request limit comes first). `focus` is kept only if a window with that id exists. All `dock` fields are optional; the dock fields are the ones of the pills (side, folded, order), more can be added in `WorkspaceRules` when the dock needs them.
- **Connections must belong to the user.** On saving, a window whose `connectionId` is not one of the user's connections is dropped (counted in `dropped`) and never stored; on reading, windows of connections deleted since are left out as well.

Not covered, on purpose: windows of other apps' own state (the Files view has its own setting `files`), a live push to other browsers or devices, removal of devices unused for months (the limit of 16 keeps the table small).

## Step 1: what is built (interface part)

- **Pure logic** in `desktop-snap.js` (zones for halves, quarters and maximise, the zone under the pointer, the Windows-like arrow steps, size and move limits, the window under a snapped window that is dragged, next window) and `desktop-keys.js` (the key bindings as data, matching, which keys may fire in a text field); both tested in `tests/frontend/desktop-snap.test.mjs`. `desktop-wm.js` wires them to the windows of `desktop.js`, `desktop-pills.js` is the pill row.
- **Keyboard (default `Ctrl+Alt` as the replacement for the Windows key):** `Ctrl+Alt+Left/Right` half, `Up` maximise (from a half first the quarter above), `Down` restore (from a half first the quarter below; on a free window: minimise); `Ctrl+Alt+Shift+Arrows` size (32 px), `Ctrl+Alt+M` move mode (arrows 24 px, Shift four times as much, `Enter` or `Esc` ends), `Ctrl+Alt+PageDown/PageUp` next and previous window (minimised ones come back). The bindings are `DEFAULT_BINDINGS`; a changed set can be stored in `localStorage` `pd.dk.keys` (`{ "snap.left": { "code": "KeyH", "mods": ["alt"] } }`, `null` switches one off, invalid or doubled keys are ignored); a settings dialog is not built.
- **Who gets the keys:** a remote window with the keyboard gets all keys, in full screen (Keyboard Lock) every key. The one exception is the window switch (`next`, `prev`, marked `global`): without it the keyboard would be trapped in the remote window. In a text field a binding fires only if it is not a character key (Ctrl+Alt is AltGr on German layouts) and has two of Ctrl, Alt, Meta. A remote window can be snapped with the mouse (drag, double click, right click on its pill).
- **Snapping with the mouse:** a preview while dragging (left or right edge: half, corner or the edge near it: quarter, top edge: maximise, bottom edge only corners); the snap happens on release. A snapped or maximised window leaves its zone after 4 px of dragging and gets its old size back with the grip at the same relative place. Double click on the title bar maximises or restores. Snapped windows stay in their zone when the browser window changes size.
- **Mouse buttons 4 and 5:** `mousedown`, `auxclick` and `mouseup` of the buttons 3 and 4 are prevented on the desktop (no `history` entries are made), `mouseup` calls `onNavigate(-1 | 1)` of the focused window (option of `createWindow` or `w.onNavigate`; Files: folder history, Start: the view history). A window without the hook does nothing. Over a session window the buttons are left to the session (`mouse.js` sends them to the target as before).
- **Dock pills:** see [BACKLOG.md](BACKLOG.md). The framed taskbar strip of an RDP desktop ([DOCK-REMOTE-TASKBAR.md](DOCK-REMOTE-TASKBAR.md), `desktop-taskbar.js`) sits at the screen edge; the dock and the pills move up by its height.
- **Checked:** in the browser pane at 1500x900 and 1000x700 (light and dark), every shortcut once, drag to edge and corner, double click, the buttons 4 and 5 as script events, pills, a real RDP window (test target), phone 375 unchanged. **Not checked:** a real mouse with side buttons, Windows Brave, a Mac keyboard, the animations (the pane was hidden while checking), a real browser resize.

## Order of work (each step releasable on its own)

1. **Window manager in one browser window:** keyboard move, size and snap, mouse buttons 4 and 5, thin dock pills, workspace per device saved on the server and restored (screens, open connections, geometry). Server side of the workspace is built; the browser side (save, restore, device list) follows the window manager.
2. **Several browser windows, apps without a stream first** (Files explorer, Hosts, Settings): shared workspace, global coordinates from the real window positions, drag across the border, one window shown in two browsers.
3. **Several viewers per connection** (engine and relay change) so that a remote screen can lie across the border; shared pacing; input and clipboard owner.
4. **Continue on the phone** and across devices.

Effort: step 1 M to L, step 2 L, step 3 L to XL (engine), step 4 M. Tests: pure functions for the coordinate mapping, snapping and the visible part of a window; the multi-viewer relay with the existing fake targets; everything else is checked by running it (several real browser windows).

## Open questions

- What should happen when a browser window is closed with windows lying in it (they stay on the remaining screens, moved inside the visible area)?
- How much lag between the two halves of a stream is acceptable (a few frames)?
- Should a second device see the same open windows at once or only on request ("continue here")?
