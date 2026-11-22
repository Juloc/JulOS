# Appearance

Settings, section **Appearance** (desktop: Start > Settings, phone: Profile). The choice is saved **per user on the server** (setting `appearance`, next to `theme`, `lang` and `keybar`, see
[FOLDERS.md](FOLDERS.md)), with `localStorage` `pd.appearance` as the local fallback, and it takes effect **at once** without a reload (CSS variables set on `html`).
The brightness (light, dark, system) stays in [THEME.md](THEME.md).

| Area | Desktop | Phone | Notes |
| --- | --- | --- | --- |
| Brightness | yes | yes | `theme.scheme`, as before |
| Accent colour | 8 fixed colours and a custom one | the colour of the phone theme (as before) | see below |
| Background | the painted mountain scene, six gradients, two patterns | no | no own picture, see below |
| Dock | mode (always, automatic, collapsible) and size (small, medium, large), **at the bottom only** | no | |
| Icons | size (small, medium, large) and style (outline, filled) | size and style | override per connection: [ICONS.md](ICONS.md) |

Each row with a changed value has **Reset** (per area), and "Reset appearance" resets everything (brightness, colours, background, dock, icons, own icons).

## Stored value (`appearance`)

Only differences from the default are stored; a missing field means default, so resetting an area removes its field and an all-default value deletes the key.

```json
{ "accent": "#2f6fe0", "bg": "ocean", "dock": { "mode": "always", "size": "l" },
  "icons": { "size": "s", "style": "filled", "over": { "<connection id>": { "k": "database", "c": "red" } } } }
```

The server (`SettingsRules.WriteAppearance`, test `The_appearance_setting_keeps_only_known_fields_and_limits_sizes`) drops unknown fields, checks the colour (`#rrggbb`), the fixed lists (dock mode, sizes, style),
the form of the names (`bg`, icon, colour: short ids; an unknown name falls back to the default in the browser) and the number of own icons (200). The browser normalises everything again
(`normalizeAppearance`), so a value from an old device or another version cannot break the page.

## Accent colour and contrast

The accent is one colour; `accentVars(hex, dark)` (`appearance.js`) computes from it **all** accent-related variables of the desktop (`--dk-accent*`, button gradient, hover and selection tints, the Material
names for dialogs). Hue stays, lightness and saturation are fitted so that, in the light and in the dark scheme, text reaches **4.5:1**, icons and lines **3:1**, and the button text 4.5:1 on both
ends of its gradient. This is checked in two places: `tests/frontend/appearance.test.mjs` (the fitting itself, for all fixed colours and edge cases such as pure yellow, white, black) and
`tests/frontend/theme-css.test.mjs` (the real variables of `desktop.css` with the accent put in, over the worst areas of the painted scene and of every background).
A very light or very dark custom colour therefore comes out as a readable shade of the same hue.

## Backgrounds

The painted scene is the default. The others are gradients and two patterns (dots, grid) of three colours each for light and dark (`WALLPAPERS`); the pattern is white in light and black in
dark, so it never makes the ground worse for text. **An own picture is not offered** on purpose: it would have to travel inside the settings value (there is no file endpoint, and this change
touches the backend only for the setting rule), every settings call would carry it, and the text on the menu bar and dock could not be guaranteed readable over any picture. It can come with a
real, small file endpoint later (IDEAS.md).

## Dock

Mode as before (`always`, `auto`, `toggle`; "automatic" is the thin pills that open at the bottom edge, [MULTI-SCREEN.md](MULTI-SCREEN.md)) and now also **size**, which scales the dock icons, the launcher and
the space that maximised windows keep free. An old per-device choice (`localStorage` `pd.dockMode`) moves into the setting once after sign-in. **A dock at the left or right is not built:** the bottom edge is
wired into window sizes, snapping, the taskbar of a remote desktop and the edge hover; moving it is a bigger change (BACKLOG).

## Files

`appearance.js` (pure: normalise, reset, accent, wallpapers, variables, `applyAppearance`), `icons.js` and `icons.css` (family, mapping, filled style, size), `icon-picker.js` (the row in the connection
dialog), state and sync in `app.js` (`getAppearance`, `setAppearance`, `resetAppearance`, event `julos:appearance`), rows in `settings.js` and their drawing in `desktop.js` and `mobile.js`.
