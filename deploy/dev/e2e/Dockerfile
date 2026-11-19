# Runner der Browser-Tests: Playwright mit Chromium (das Image bringt den Browser mit), Testdateien kommen per Volume (deploy/dev/e2e/compose.override.yaml).
# Die Version des Images und die in package.json muessen gleich sein.
FROM mcr.microsoft.com/playwright:v1.65.1-noble
WORKDIR /opt/e2e
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
ENTRYPOINT ["/opt/e2e/node_modules/.bin/playwright", "test"]
