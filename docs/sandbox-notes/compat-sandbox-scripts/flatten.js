// Materialize top-level symlink entries in the deploy node_modules into real
// dirs (dereferenced), so the dsh moduleFallback BFS (which resolves via
// resolve.paths WITHOUT realpath normalization) sees a flat npm-like layout.
const fs = require('node:fs');
const path = require('node:path');

const NM = 'E:/dsh-compat-sandbox/runtime-new/node_modules';

function materialize(dir) {
  let entries;
  try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
  for (const e of entries) {
    const full = path.join(dir, e.name);
    if (e.name.startsWith('.')) { // .pnpm handled separately
      continue;
    }
    if (e.isDirectory() && !e.isSymbolicLink()) { // real scope dir -> recurse
      materialize(full);
      continue;
    }
    if (!e.isSymbolicLink()) continue;
    // symlink (package or scope): dereference into a real dir
    let real;
    try { real = fs.realpathSync(full); } catch { console.error('broken link, removing:', full); fs.rmSync(full); continue; }
    const st = fs.lstatSync(full);
    if (!st.isSymbolicLink()) continue;
    fs.rmSync(full, { recursive: true, force: true });
    fs.cpSync(real, full, {
      recursive: true, dereference: true, force: true,
      filter: (s) => path.basename(s) !== 'node_modules',
    });
    console.log('materialized:', path.relative(NM, full));
  }
}
materialize(NM);
console.log('top-level materialization done');
