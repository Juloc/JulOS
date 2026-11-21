// Entry of the vendored Excel reader (built by tools/vendor-read-excel-file.sh into wwwroot/vendor/read-excel-file/read-excel-file.mjs).
//
// Why not the package's own browser bundle: it unpacks big sheets with fflate's asynchronous `unzip`, which starts a Web Worker from a `blob:` URL (also `read-excel-file/universal` does),
// and the page's CSP (`worker-src 'self'`) forbids that, so every spreadsheet with a sheet over ~0.5 MB of XML would fail. Here the same library code runs with the synchronous
// `unzipSync` on the main thread instead; the viewer limits the unpacked size before it gets here (viewer-office.js, `zipRefusal`).
// The paths into the package's `modules/` folder (what its own exports are built from) are pinned by the lock file.
import { unzipSync } from 'fflate';
import parseXml from './node_modules/read-excel-file/modules/xml/parseXml.js';
import parseSpreadsheetContents from './node_modules/read-excel-file/modules/xlsx/parseSpreadsheetContents.js';
import filterZipArchiveEntry from './node_modules/read-excel-file/modules/export/filterZipArchiveEntry.js';
import validateLeadingBytes from './node_modules/read-excel-file/modules/xlsx/file/validateLeadingBytes.js';
import InvalidInputError from './node_modules/read-excel-file/modules/xlsx/file/InvalidInputError.js';

/** Reads an `.xlsx` file (Uint8Array) into [{ sheet, data }]; formulas come as their cached result. Throws InvalidInputError (`.code` XLS_FILE_NOT_SUPPORTED, FILE_NOT_SUPPORTED, INVALID_ZIP) or InvalidSpreadsheetError. */
export async function readXlsx(bytes) {
  validateLeadingBytes(bytes);
  let contents;
  try { contents = unzipSync(bytes, { filter: (file) => filterZipArchiveEntry({ path: file.name }) }); }
  catch (e) { throw new InvalidInputError('INVALID_ZIP', e); }
  return parseSpreadsheetContents(undefined, parseXml, contents, {});
}
