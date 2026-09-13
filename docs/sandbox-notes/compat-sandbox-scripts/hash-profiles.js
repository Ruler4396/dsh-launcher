// Zero-pollution snapshot of the user profiles dir (mirrors the launcher's
// CaptureUserProfilesHash: every file recursively, .dsh-safe excluded).
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

const root = 'E:/dsh-compat-sandbox/home/profiles';
const outFile = process.argv[2];
const out = {};
(function walk(dir) {
  let es;
  try { es = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
  for (const e of es) {
    const full = path.join(dir, e.name);
    const rel = path.relative(root, full).split(path.sep).join('/');
    if (rel.includes('/.dsh-safe/') || rel.startsWith('.dsh-safe/')) continue;
    if (e.isDirectory()) walk(full);
    else if (e.isFile()) {
      try {
        out[rel] = crypto.createHash('sha256').update(fs.readFileSync(full)).digest('hex');
      } catch { /* locked file skipped, same as launcher */ }
    }
  }
})(root);
fs.writeFileSync(outFile, JSON.stringify(out, null, 1));
console.log('hashed files:', Object.keys(out).length, '->', outFile);
