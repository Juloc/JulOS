# Phone interface: what is hard to understand, and the plan

Status: a first audit of the phone shell (375 to 430 px wide, `?phone`) on 2026-11-19 after the user said it is "not understandable and hard to use". It lists what was
seen on each page, a proposal, and open questions. Nothing is built yet; the order of work is in [INTEGRATIONS.md](INTEGRATIONS.md) next to the other milestones.

## What was seen

**Home**
- A "Server" card with CPU, RAM and storage (storage shows a dash) repeats on the Server page; it takes a third of the screen before the user reaches a connection.
- Favourite tiles are small, the names are cut or wrapped ("Claude auf DEV-VM"), a key symbol on each tile is not explained (it means "password stored").

**Services page (shows the connections)**
- Three rows of filter chips (kind with a count that can be 0, folder, host) sit above the tiles; on a phone they are more than the tiles themselves.
- Two tiles with nearly the same name are indistinguishable ("Codex auf DEV-VM", "Codex auf dev-desktop"); the host is not shown on the tile.
- "Quick actions" mixes unlike things in one grid: a primary "New connection", "Last: ...", "Open SSH", "Claude Code", "Codex", "Quick Connect", "Folders".
- The floating "Add" button duplicates "New connection" and covers the last row of tiles.
- Naming clash: the page is called "Dienste" ("your desktops and terminals") while the new Services module (widgets) will also be called Dienste.

**Server page**: the first card repeats the header; storage shows a dash; the host card lists every connection as wrapping chips, including connections that belong to another machine (a data mistake the page makes visible but does not explain).

**Monitoring page**: an empty placeholder ("coming soon") occupies one of five main navigation slots.

**Profile page**: one long flat list; the backup line shows a raw technical error text; "Image transfer: Stream (H.264 on the CPU)" and "Build: <hash>" are jargon for most readers; there is a setting to hide pages without data instead of hiding them by default.

**Terminal (program sessions)**: a row of about fourteen tiny key buttons, an input bar, and below a second row of icon-only buttons (keyboard, full screen, more); copy and paste icons have no label; the first-use path (what to do when `claude` asks to log in) is not shown.

**Everywhere**: icon-only buttons without a label, jargon ("stream", "host key"), several entry points for the same action, error texts that say what is wrong but not what to do next.

## Proposal (small steps, each testable on a phone)

1. **Navigation:** four tabs, Start, Connections, Hosts, Profile. Hide Monitoring until it has data. Rename the current "Dienste" page to "Connections"; the widget module keeps the name "Services".
2. **Start:** one primary action ("New connection"), a large "Continue" row (last used, with the host name), favourites in two columns with a two-line label and the host as a second line; the key symbol becomes a text hint in the tile menu.
3. **Connections:** one search field and one "Filter" sheet (kind, folder, host) instead of three chip rows; the list grouped by host; a tile shows icon, name, host line and a status dot; duplicate names get the host appended automatically.
4. **Terminal:** one compact key row with short text labels and a "more keys" sheet; a clear first-use hint for program sessions (login steps, where to paste the code); labelled copy and paste.
5. **Hosts:** one card per machine with status, agent version, what is installed (Claude, Codex, tmux) and what needs attention (updates, backup); the actions of that machine on the card (see INTEGRATIONS.md, M6).
6. **Messages:** every error shows a short sentence and a next step ("Backup folder is not writable: ask the server admin to run ..."); technical details behind a "Details" link.
7. **Empty states** explain the next step in one line with a button; no placeholder pages in the main navigation.
8. **Test on a real phone** (see [PHONE-TEST.md](PHONE-TEST.md)); screenshots before and after for each step.

## The user's answer (2026-11-19)

Main complaint: the interface as a whole feels random (no consistent look and rhythm between pages), and the settings are not clearly structured or well formatted.
Devices: Android Chrome and a Windows PC with Brave (so the desktop layout counts as much as the phone shell).

Consequences for the plan, to be placed before step 2 above:

- **A. One design pass instead of page-by-page fixes:** a short component sheet (headings, section cards, list rows, tiles, buttons, chips, switches, empty states, messages) with fixed
  spacing, type sizes, radii and colours taken from the theme variables (the dark and light sets of `THEME.md`), then every page uses only those parts. Review by screenshots of every
  page before and after, on Android Chrome size and on the desktop layout.
- **B. Settings rebuilt as a structured page:** groups with a title and one line of help ("Account", "Appearance", "Connections", "Terminal and programs", "Clipboard and sound",
  "Backup and security", "About"); every row has a label, a short explanation and one control (switch, choice, button); technical values (build, encoder, host keys) move to
  an "About and diagnostics" group; destructive actions are last and separated; the same structure in the desktop settings window.
- **C. Hide what has no content:** pages without data (Monitoring) are not shown until they have something to show.

## Open questions

Which pages should be done first after the design pass (Home, Connections, Settings, Terminal)? A real Android screenshot of the worst page would help to calibrate.
