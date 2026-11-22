/* =====================================================================
 * Dateien-Fenster (Desktop): die Dateien-App (files-app.js) in einem Fenster. Orte: der gemeinsame Ordner des Servers (nur lesen, JULOS_FILES_ROOT) und jeder Host mit SSH-Zugang (SFTP,
 * docs/FILES.md). Wird von desktop.js erst beim ersten Oeffnen geladen. deps: { api, getState, notify }
 * ===================================================================== */
import { mountFiles } from './files-app.js';
import { LOCAL_ID } from './files-core.js';

export { fileSize } from './files-core.js';

/** Baut das Fenster-Innere in `body` und gibt { go(path), refreshPlaces() } zurueck */
export function mount(body, { api, getState, notify }) {
  const app = mountFiles(body, { api, kit: false, getState, notify });
  return {
    /** Oeffnet den gemeinsamen Ordner (zum Beispiel aus dem Menue "Dateien") im angegebenen Pfad */
    go: (path) => app.enter(LOCAL_ID, path),
    refreshPlaces: () => app.refreshPlaces(),
    /** Zurueck (dir < 0) oder vor im Verlauf der Ordner, wie Alt+Links und Alt+Rechts und die Maustasten 4 und 5 */
    navigate: (dir) => (dir < 0 ? app.back() : app.forward()),
    destroy: () => app.destroy(),
  };
}
