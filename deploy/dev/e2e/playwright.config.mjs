import { defineConfig } from '@playwright/test';

// Wenige, schnelle Ablaeufe in fester Reihenfolge (einer baut auf dem anderen auf: Konto, Verbindungen). Bilder und Traces nur bei Fehlern.
export default defineConfig({
  testDir: './tests',
  outputDir: '/out/results',
  workers: 1,
  fullyParallel: false,
  retries: 1,                         // ein Wiederholungsversuch (die Ersteinrichtung ist davon ausgenommen, siehe Test)
  timeout: 45_000,
  globalTimeout: 480_000,             // harte Obergrenze: ein haengender Lauf soll nicht ewig blockieren
  expect: { timeout: 20_000 },
  reporter: [['list']],
  use: {
    baseURL: 'http://127.0.0.1:8080',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'off',
    actionTimeout: 15_000,
    locale: 'de-DE',
  },
});
