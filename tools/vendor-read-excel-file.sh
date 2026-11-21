#!/usr/bin/env bash
# Builds the vendored Excel reader (read-excel-file, MIT) into src/JulOS.Frontend/wwwroot/vendor/read-excel-file/.
# npm and esbuild are used here only, in a throwaway folder; nothing of this ships in the image and the app has no build step.
# Versions are pinned in tools/vendor-read-excel-file/package.json and package-lock.json (`npm ci` checks every tarball against its lock hash).
# Why a build at all (the package's own browser bundle starts blob: workers, which the page CSP forbids): see tools/vendor-read-excel-file/entry.js.
# Run it again after changing a version, then copy the printed hashes into wwwroot/vendor/README.md and the names into licenses.txt.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
src="$here/vendor-read-excel-file"
out="$here/../src/JulOS.Frontend/wwwroot/vendor/read-excel-file"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
cp "$src/package.json" "$src/package-lock.json" "$src/entry.js" "$work/"
cd "$work"
npm ci --ignore-scripts --no-audit --no-fund --loglevel=error
rm -rf "$out"; mkdir -p "$out"
npx --no-install esbuild entry.js --bundle --minify --format=esm --target=es2022 --legal-comments=none --outfile="$out/read-excel-file.mjs"
# The licence texts of everything that went in (all MIT): one file, each package once
{
  echo "Licence texts of the packages bundled in read-excel-file.mjs (all MIT). Built by tools/vendor-read-excel-file.sh."
  for d in node_modules/read-excel-file node_modules/saxen node_modules/fflate; do
    echo; echo "=== $(node -e "const p=require('./$d/package.json');console.log(p.name+' '+p.version)") ==="; cat "$d"/LICENSE
  done
} > "$out/LICENSE.txt"
echo "built into $out"
( cd "$out" && sha256sum read-excel-file.mjs LICENSE.txt && wc -c read-excel-file.mjs && gzip -9 -c read-excel-file.mjs | wc -c )
