# Linux programs in the JulOS dock (build plan)

Status: **plan for the build** (2026-11-27). Written by the JulOS chief chat for a build session; every decision below is made by the owner (interactive, 2026-11-27) or by the chief. If something here does not fit the code, ask the chief before choosing. Context: the owner's desktop mockup ("JulOS – Complete Desktop Shell", not in the repo: it holds private names) and its comparison with today's JulOS, summarised in [IDEAS.md](IDEAS.md) "Desktop shell after the owner's mockup".

## Goal in one sentence

The programs of the remote Linux desktop that has the focus appear in the JulOS dock like JulOS's own apps: running ones grouped by program, favourites pinned and started with one click, a title list on hover, and the VM's own panel hides by itself while JulOS shows them.

## Decisions (owner, 2026-11-27)

1. **Running and pinned.** Open windows grouped by program, plus pinned favourites that start the program in the VM (`/v1/apps/launch` exists).
2. **Hover shows a title list,** not pictures: the agent never returns window contents ([AGENT_API.md](AGENT_API.md), rule 2), and that stays.
3. **Only the focused machine.** The dock follows the session window with the focus; a small host label says which machine. When a JulOS window (Home, Files …) has the focus, the dock keeps the programs of the last focused desktop session, dimmed.
4. **The VM panel hides automatically** (existing `apps` mode) while the dock shows the programs; it comes back by itself after 90 s without requests, as today.
5. **Look:** the family style from `base` (owner's choice); this build does not restyle anything. Write the new dock part so it can move to `base` later ([BASE.md](BASE.md) rule 3: built in JulOS first): no JulOS globals inside the component, data and actions passed in.
6. **Phone:** not in this build. The phone keeps its look and gets the same grouping and pins in a later step, using the same logic file.

## What exists and must be reused

- `app.js` `RemoteSession`: `W` (window state), `pollWindows` (every 2 s while the window bar is on), `activateWindow`, the window menu (minimise, close), the apps list and launch (`/apps`, `/apps/launch`), the mode switch (`/mode`).
- `desktop.js` builds the dock (JulOS apps, `desktop-pills.js` for collapsed pills); `desktop-taskbar.js` cuts the VM's own taskbar from the picture ([DOCK-REMOTE-TASKBAR.md](DOCK-REMOTE-TASKBAR.md)): keep it; it is the fallback for machines without an agent.
- Settings per user: `GET/PUT /api/settings/{key}` with `SettingsRules` (`src/JulOS.Backend/Logic/SettingsRules.cs`) validating every key.

## Agent (0.9.1 → 0.10.0)

`/v1/apps` gets one new field per app: `wmclass`, from `Gio.DesktopAppInfo.get_startup_wm_class()` (empty string if none). A new field is allowed under rule 3 (no `/v2`). Old agents: the browser falls back to matching the lower-cased app id without `.desktop` against the window `class` (and the first word of the name). Bump `AGENT_VERSION`; no server minimum change (the field is optional).

## Browser

### New pure logic: `remote-apps.js`

No DOM, unit-tested (`tests/frontend/remote-apps.test.mjs`):
- `groupWindows(windows, apps, pins)` → ordered entries `{key, appId?, name, icon, windows:[{id,title,active,minimized}], pinned, running, active}`. Key = app id when known, else `class:<class>`. Order: pinned in the user's order first, then running unpinned in order of first appearance (stable between polls: a window that closes does not reshuffle the rest).
- `matchApp(window, apps)`: `wmclass` equal (case-insensitive) → id without `.desktop` equal to class → first word of name equal → none.
- `clickAction(entry)` → `launch` (not running), `activate` (one window, not active), `minimize` (one window, active), `list` (several windows).
- `validPins(value)`: an object `{ <connectionId>: [appId, …] }`, at most 20 connections, at most 24 ids each, id ≤ 200 characters, no control characters. The same rules on the server.

### Dock part: `desktop-remote-apps.js`

- Mounted in the dock after JulOS's own apps, behind a thin separator, with the host label (connection name, truncated) before the first entry.
- Entry: the agent's icon (`data:image/png;base64,` only after checking the string is base64 of at most 16 KB; otherwise the generic app icon), a dot per open window (max 3 dots), an active marker. Name as tooltip and `aria-label` ("Chromium, 2 Fenster").
- Click: as `clickAction`. Middle click or the menu entry *Neues Fenster*: launch again.
- Hover (desktop, 300 ms) or long press (touch) or the context key: a small popover with the window titles (click activates, × closes with the existing close call, no confirmation, as everywhere), *Anheften* / *Lösen*, *Neues Fenster*, *Nach links* / *Nach rechts* (for pinned entries; also keyboard reachable).
- Drag to reorder pinned entries (pointer events, desktop only); the order saves after the drop.
- Polling: use the session's existing `pollWindows`; do not start a second timer. While the dock shows a session's programs, that session polls even if its own window bar is hidden.
- Mode: when the dock starts showing a session's programs and the agent is there, `POST /mode {apps}`; when the setting is turned off or the session closes, `POST /mode {desktop}` (best effort). Keep the existing 90 s fallback.

### Setting

Appearance, Dock: *Linux-Programme im Dock* (on by default for desktop sessions with an agent). Off = today's behaviour exactly. Pins in a new settings key `dock.pins` (per user, server-side, follows to every device), validated in `SettingsRules` like `validPins`.

## Tests

- Frontend (pure): grouping, ordering stability, matching with and without `wmclass`, click action, pin validation and the icon check (non-base64 and oversize refused).
- Backend (security-relevant): `dock.pins` refused when too many connections or ids, too long, or with control characters; accepted at the limits.
- Agent: if the agent has tests, one for `wmclass` in `list_apps` with a fake `AppInfo`; else note it as not tested.
- No test for plain display.

## Docs in the same commits

`docs/DOCK.md` status → built, README line under the window bar, `docs/AGENT_API.md` (`wmclass`), `docs/APPEARANCE.md` (the switch), CHANGELOG under *Unveröffentlicht*, i18n de and en, service worker version +1 and the new files in its list.

## Done means

- Tests green (frontend suite, backend suite once through `devq`).
- Run by hand in the dev stack (test desktop with the agent): Chromium and the terminal appear in the dock while their windows are open; pin Chromium, close it, start it from the dock; two terminal windows show two dots and a title list; reorder two pins and reload: order kept; the VM panel hides and comes back after turning the switch off. Report it with screenshots.
- Not verified, say so: the real dev desktop of the owner, the phone (not part of this build), the browser e2e suite unless run.
