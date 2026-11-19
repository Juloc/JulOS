# Hell, Dunkel und System

Die Desktop-Oberfläche und die Handy-Shell haben je ein helles und ein dunkles Farbschema. Die Wahl heißt **Hell / Dunkel / System**
(System folgt `prefers-color-scheme` des Geräts und wechselt live mit ihm) und ist für beide Oberflächen dieselbe.

## Wo man wählt

- Desktop: Start → Einstellungen → Darstellung (drei Knöpfe).
- Handy: Profil → Darstellung → Helligkeit.
- Die Wahl gilt sofort. Das Sitzungsbild (Canvas/Video), seine Statusleiste und das Pop-out-Fenster (immer dunkel) bleiben, wie sie sind.

## Speicherung

- Server: die Einstellung `theme` je Benutzer (`/api/settings`, [FOLDERS.md](FOLDERS.md)). Sie hat die Felder `seed` (Grundfarbe, nur Handy) und
  `scheme` mit `light`, `dark` oder `auto` (= System). Der Server prüft beides (`SettingsRules.cs`): unbekannte Werte werden abgelehnt, unbekannte
  Felder fallen weg. Es gibt keinen zweiten Schlüssel; die Desktop-Wahl ist `theme.scheme`.
- Lokal: `localStorage` `pd.scheme` (und `pd.seed`) als Rückfall. Ist der Server nicht erreichbar, gilt die lokale Kopie; ein ungültiger lokaler
  Wert wird zu `auto`. Der Abgleich beim Anmelden und beim Zurückkehren zur Seite steht in `prefs.js`: ein Wert vom Server geht vor,
  offene lokale Änderungen gehen zuerst hinaus.
- Die reine Logik (Werte, Beschriftung, „ist es dunkel?“) steht in `scheme.js` (mit Test `tests/frontend/scheme.test.mjs`).

## Wie es angewendet wird

`applyTheme()` in `app.js` bestimmt „dunkel“ aus Wahl und System und setzt:

- `html.style.colorScheme` (`light` oder `dark`): Formularelemente, Scrollleisten, `light-dark()` der Handy-Farben (`m3.css`) und das Terminal richten sich danach;
- `html[data-scheme="light|dark"]`: daran hängt der dunkle Farbsatz der Desktop-Oberfläche (`desktop.css`);
- das Ereignis `julos:scheme` auf `document` (die Desktop-Einstellungen ziehen ihre Knöpfe nach, auch wenn die Wahl von einem anderen Gerät kommt);
- die Farben `--md-*` der Handy-Oberfläche (Material You, aus der Grundfarbe berechnet).

## Aufbau von desktop.css

- Alle Farben stehen als Variablen im Block `html.desktop` (hell). Der Block `html.desktop[data-scheme="dark"]` ändert nur die Werte, die im Dunkeln anders
  sind; die Regeln darunter benutzen nur `var(--dk-…)`. Eine neue Farbe gehört in beide Blöcke, nie als Wert in eine Regel.
- `--dk-t1` … `--dk-hint`: Textstufen; `--dk-card*`, `--dk-glass*`, `--dk-bar`, `--dk-dock`: Flächen (halbtransparent über dem Hintergrundbild);
  `--dk-hover*`, `--dk-sel`: Zustände; `--dk-ok-*`, `--dk-bad-*`, `--dk-st-*`: Status; `--dk-ctl-edge`: Rand von Feldern.
- `--dk-surface` … `--dk-error-container` sind die Material-Namen, die `style.css` (Dialoge, Menüs der Fernsteuerung) erwartet. Der Block
  `#desktop, html.desktop dialog` setzt damit `--md-*` neu; das überschreibt, was `applyTheme()` für das Handy auf `html` setzt (Dialoge liegen außerhalb von `#desktop`).
- `--wp-*`: Farben des Hintergrundbilds (`scene()` in `desktop.js`, als `style="fill:var(--wp-…)"`), `--dk-logo-*`: Logo. Symbole zeichnen mit `currentColor`.
- Bewusst fest: das schwarze Sitzungsfenster, die Statusleiste über dem Bild, die Glanzkante der Kacheln und die Farbverläufe der Programm-Kacheln
  (`g: [...]` in `desktop.js`, weißes Symbol, je Ende mindestens 3:1).

## Kontrast (WCAG AA)

- Text mindestens 4,5:1, Ränder von Feldern und Zustandspunkte mindestens 3:1, in beiden Schemata.
- `tests/frontend/theme-css.test.mjs` rechnet das aus den Variablen nach: jede Textstufe gegen Fenster, Karte, Menü, Menüleiste, Dock und Dialog,
  über den ungünstigsten großen Flächen des Hintergrundbilds (hell: dunkelster Himmel, Berge, See; dunkel: hellste Flächen). Dazu: beide Sätze vollständig, jede benutzte
  Variable definiert, keine festen Farben in den Regeln und in den SVG-Texten von `desktop.js`.
- Stichprobe im Browser: ein Skript geht die sichtbaren Texte einer Ansicht durch und rechnet Vordergrund gegen die zusammengesetzte Fläche
  (Hintergrundbild ohne Fenster als Bild darunter). Stand beim Einbau: keine Stelle unter der Schwelle (Start, Apps, Dienste, Hosts, System, Einstellungen, Dateien, Menüs, Launcher, Dialoge, Terminal-Fenster).
