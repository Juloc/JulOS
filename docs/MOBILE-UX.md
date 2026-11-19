# Phone interface: what was hard to understand, what was done

Status 2026-11-19: the audit and the plan were turned into six small steps (one commit each, see the end). The phone shell (`?phone`, 375 to 430 px) and, where the same parts apply,
the desktop window share one set of building blocks, one structure for settings and plainer texts. What was **not** checked on a real phone is listed at the end.

The building blocks are shown on a living page: `docs/ui-kit.html` (it loads the app's own stylesheets, light and dark, seed colour switch). Open it locally:

```bash
python3 -m http.server 8123 --bind 127.0.0.1
# then http://127.0.0.1:8123/docs/ui-kit.html in the repo root
```

Screenshots before and after each step are in [mobile-ux-screens/](mobile-ux-screens/) (412 x 915 phone, 1280 x 800 desktop, light and dark; file names start with the step number, `0-before-*` is the state before).

## What was seen (the audit)

- **Everywhere:** no common rhythm (cards, tiles, chips and buttons in many shapes, sizes and spacings), icon-only buttons without a label, jargon ("stream", "host key", "agent"), several entry points
  for the same action, error texts that say what is wrong but not what to do next.
- **Home:** a server card with CPU, RAM and storage took a third of the screen; favourite tiles were small with cut names; a key symbol on each tile was not explained.
- **Services page (the connections):** three rows of filter chips, two tiles with the same name could not be told apart, a mixed grid of "quick actions", a floating button that duplicated "New connection" and covered the last row;
  the name clashed with the Services module (widgets).
- **Monitoring:** an empty placeholder in one of five navigation slots. **Profile:** one long flat list with a raw backup error text and jargon values.
- **Terminal (program sessions):** about fourteen tiny key buttons, a second row of icon-only buttons, copy and paste without labels, no hint how to sign in to Claude or Codex.

## What was done (the six steps)

1. **Component sheet** (`docs/ui-kit.html`, `m3.css`). Spacing steps (4, 8, 12, 16, 24), one card and tile radius, page header, section with one line of help, rows (label, explanation, **one** control),
   tiles, buttons (filled, tonal, text), chips, switch row, empty state, message (info, ok, warning, error, with "Details"), sheet, navigation bar, main row, danger area. Colours only from the theme variables.
   Contrast was checked by a script (visible texts against the composed background, light and dark): no text under 4.5:1 except disabled controls. Buttons, chips, icon buttons, segments, avatar and keys are 44 px high.
2. **Settings as groups** (`settings.js`; phone: Profil, desktop: Einstellungen). Konto, Darstellung, Verbindungen, Terminal und Programme, Zwischenablage und Ton, Backup, Über JulOS und Diagnose, and **Abmelden** alone at the end.
   Both surfaces render the same description (order, titles, help, rows), `tests/frontend/settings.test.mjs` pins it. The backup shows a short sentence with the next step ("Der Backup-Ordner gehört nicht dem Server-Benutzer. Prüfe die Rechte …"),
   the raw server text only behind "Details". Build, image transfer and network tools moved to the about group. The switch "show pages without data" is gone.
3. **Verbindungen page** (was Dienste; the module of service widgets keeps the name Dienste). One search row with a **Filter** button; the sheet has kind, folder and host; set filters show as chips that remove themselves.
   Tiles in two columns show icon, name (two lines), status dot and host; equal names get the host, kind or address appended (`conn-names.js`). One main button "Neue Verbindung" (page header, or inside the empty state); no floating button,
   no second quick-action grid. Monitoring is not in the navigation; "Server" is called **Hosts** and only shows with load data or hosts. The key symbol is text ("Passwort gespeichert") in the tile menu.
4. **Start page.** Greeting, the big **Weiter** row (last used connection with host and time; without a connection the main action "Neue Verbindung"), open sessions, favourites in two columns, **Programme** (Claude Code and Codex, one tap),
   activities and a one-line server row that leads to Hosts.
5. **Terminal.** Row 1: at most six keys with short labels plus **Mehr**; the sheet **Mehr Tasten** lists all keys with their names and the tools as labelled buttons. Row 2: input, **Einfügen**, new line, **Senden**. For Claude and Codex a card explains the sign-in once;
   **Adresse öffnen** opens the last https address in the terminal (only on tap). The generic key bar no longer repeats Esc/Ctrl/Tab in terminals and every icon key has a name. Details: [CLI-SESSIONS.md](CLI-SESSIONS.md).
6. **Words.** One sentence with the next step is added to known server errors (`NEXT_STEP` in `i18n-errors.js`, German and English); "Host-Schlüssel" is "Erkennungsschlüssel des Rechners", "Quick Connect" is "Schnell verbinden", errors during a session say "Neu verbinden".
   Every icon button has an `aria-label` and tooltip (checked on all phone pages and desktop views). All texts go through the i18n module.

## Decisions and open items

- **Zwischenablage und Ton** has no real settings (the checks live in the session dialogs), so the group has two explanatory rows without a control instead of invented switches.
- **Left alone on purpose** (another change works there): the host card and host dialog, "Agent" wording and the updates display, and [HOSTS.md](HOSTS.md) which still says "Server" for the phone page. "Agent" is still not explained for readers.
- Server texts stay German and unchanged (the test compares them with the source); the next step is added in the browser. "Host-Schlüssel nicht vertraut." therefore still shows the old word in German.
- Desktop windows other than Einstellungen and the connections list (Dateien, System, module windows) were only checked, not redesigned. The desktop has no component sheet of its own: its parts (`dk-card`, `dk-row`, `dk-btn`, `dk-srow`) follow the same structure with the `--dk-*` variables.
- Not done: Hosts page redesign (see INTEGRATIONS.md, M6), icons that hint at "rights" beyond the key, a font-size setting for the whole interface.

## Not verified (no real device)

Everything was checked in the browser pane at 412 x 915 and 1280 x 800 (Chromium, emulated touch size, light and dark), plus script checks for contrast, tap size and names. **Not verified:** real Android Chrome (keyboard behaviour of the input bar,
long press for the tile menu, safe areas, the animation of sheets), real Windows Brave, screen reader output, one-hand reach. See [PHONE-TEST.md](PHONE-TEST.md) and the list below.

### To look at on the phone

- Start: is "Weiter" the first thing you want to tap? Does a long press on a tile open the menu (and does it say "Passwort gespeichert" for stored passwords)?
- Verbindungen: search field and Filter side by side; the filter sheet; two tiles with the same name; the main button at the top right.
- Profil: the groups, the colour row (7 circles on one line), the backup note under "Backup", "Abmelden" at the end.
- Terminal with Claude or Codex: the card at the first start, "Adresse öffnen" (opens the sign-in page), "Einfügen" then "Senden" for the code; "Mehr" and the sheet above the keyboard; keys reachable with the thumb.
- Dark mode and large system font size on every page.
