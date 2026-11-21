#!/usr/bin/env bash
# Builds the vendored editor (CodeMirror 6 and js-yaml) into src/JulOS.Frontend/wwwroot/vendor/codemirror/<version>/.
# npm and esbuild are used here only, in a throwaway folder; nothing of this ships in the image and the app has no build step.
# The versions are pinned in tools/vendor-codemirror/package.json and package-lock.json (`npm ci` checks every tarball against its lock hash).
# Run it again after changing a version, then copy the printed lines into wwwroot/vendor/README.md and licenses.txt.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
src="$here/vendor-codemirror"
version="$(node -e "console.log(require('$src/package.json').dependencies['@codemirror/view'])")"
out="$here/../src/JulOS.Frontend/wwwroot/vendor/codemirror/$version"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
cp "$src/package.json" "$src/package-lock.json" "$src/entry.js" "$src/yaml-entry.js" "$work/"
cd "$work"
npm ci --ignore-scripts --no-audit --no-fund --loglevel=error
bundle() { npx --no-install esbuild "$1" --bundle --minify --format=esm --target=es2022 --legal-comments=none --outfile="$2"; }
rm -rf "$out"; mkdir -p "$out"
bundle entry.js "$out/codemirror.mjs"
bundle yaml-entry.js "$out/js-yaml.mjs"
# The licence texts of everything that went in (all MIT): one file, each package once
{
  echo "Licence texts of the packages bundled in codemirror.mjs and js-yaml.mjs (all MIT). Built by tools/vendor-codemirror.sh."
  for d in node_modules/@codemirror/* node_modules/@lezer/* node_modules/js-yaml node_modules/crelt node_modules/style-mod node_modules/w3c-keyname node_modules/@marijn/find-cluster-break; do
    [ -d "$d" ] || continue
    f="$(ls "$d"/LICENSE* 2>/dev/null | head -1 || true)"
    [ -n "$f" ] || continue
    echo; echo "=== $(node -e "const p=require('./$d/package.json');console.log(p.name+' '+p.version)") ==="; cat "$f"
  done
} > "$out/LICENSE.txt"
echo "built into $out"
( cd "$out" && sha256sum codemirror.mjs js-yaml.mjs LICENSE.txt && wc -c codemirror.mjs js-yaml.mjs && gzip -9 -c codemirror.mjs | wc -c )
