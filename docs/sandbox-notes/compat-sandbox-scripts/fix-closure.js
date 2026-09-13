// Closure filler + validator for the pnpm-deploy'd dsh runtime. Memory-lean:
// realpath-deduped tree walk, memoized resolution, recheck-only fixpoint.
const fs = require('node:fs');
const path = require('node:path');

const DEPLOY = 'E:/dsh-compat-sandbox/runtime-new';
const WORKSPACE = 'E:/dsh-compat-sandbox/src/deepseek-harness';
const DEPLOY_NM = path.join(DEPLOY, 'node_modules');

function readM(d) {
  try { return JSON.parse(fs.readFileSync(path.join(d, 'package.json'), 'utf8')); }
  catch { return null; }
}

/** Every package dir (realpath-deduped) under root. */
function collectPackageDirs(root) {
  const seen = new Set();
  const out = [];
  (function walk(dir, depth) {
    if (depth > 14) return;
    let real;
    try { real = fs.realpathSync(dir); } catch { return; }
    if (seen.has(real)) return;
    seen.add(real);
    if (fs.existsSync(path.join(dir, 'package.json'))) out.push(dir);
    let entries;
    try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
    for (const e of entries) {
      if (!e.isDirectory() && !e.isSymbolicLink()) continue;
      if (e.name === 'bin' || e.name === 'dist' || e.name === 'lib') continue;
      walk(path.join(dir, e.name), depth + 1);
    }
  })(root, 0);
  return out;
}

const resolveMemo = new Map();
/** Node resolution walk starting AT the package dir. */
function resolveFrom(fromDir, name) {
  const key = fromDir + '|' + name;
  if (resolveMemo.has(key)) return resolveMemo.get(key);
  let dir = fromDir, result = null;
  while (true) {
    const candidate = path.join(dir, 'node_modules', name);
    if (fs.existsSync(path.join(candidate, 'package.json'))) { result = candidate; break; }
    const parent = path.dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  resolveMemo.set(key, result);
  return result;
}

/** Required (deps + non-optional peers) names of a manifest. */
function requiredNames(m) {
  const req = new Set(Object.keys(m.dependencies ?? {}));
  for (const [name] of Object.entries(m.peerDependencies ?? {})) {
    if (!m.peerDependenciesMeta?.[name]?.optional) req.add(name);
  }
  return req;
}

/** Unresolved [dependent, name] over given package dirs (default: all). */
function collectMissing(dirs) {
  const missing = [];
  for (const dir of dirs) {
    const m = readM(dir);
    if (!m) continue;
    for (const name of requiredNames(m)) {
      if (name.startsWith('@types/')) continue;
      if (resolveFrom(dir, name) === null) missing.push([dir, name]);
    }
  }
  return missing;
}

// Workspace source index: name -> first real-content dir (root links, then .pnpm store).
const wsIndex = new Map();
function buildWsIndex() {
  const consider = (nmDir) => {
    let entries;
    try { entries = fs.readdirSync(nmDir, { withFileTypes: true }); } catch { return; }
    for (const e of entries) {
      if (e.name.startsWith('.')) { if (e.name === '.pnpm') continue; continue; }
      const full = path.join(nmDir, e.name);
      if (e.name.startsWith('@')) {
        let sub;
        try { sub = fs.readdirSync(full); } catch { continue; }
        for (const s of sub) {
          const pkg = path.join(full, s);
          const key = e.name + '/' + s;
          if (!wsIndex.has(key) && fs.existsSync(path.join(pkg, 'package.json'))) wsIndex.set(key, pkg);
        }
      } else if (!wsIndex.has(e.name) && fs.existsSync(path.join(full, 'package.json'))) {
        wsIndex.set(e.name, full);
      }
    }
  };
  consider(path.join(WORKSPACE, 'node_modules'));
  // workspace package source dirs (built in place; cover peers with no pnpm instance)
  for (const tree of ['packages', 'apps', 'vendor']) {
    (function scanSource(dir, depth) {
      if (depth > 5) return;
      if (fs.existsSync(path.join(dir, 'package.json'))) {
        let m;
        try { m = JSON.parse(fs.readFileSync(path.join(dir, 'package.json'), 'utf8')); } catch { return; }
        if (m.name && !wsIndex.has(m.name)) wsIndex.set(m.name, dir);
      }
      let entries;
      try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
      for (const e of entries) {
        if (!e.isDirectory() || e.name.startsWith('.') || e.name === 'node_modules') continue;
        scanSource(path.join(dir, e.name), depth + 1);
      }
    })(path.join(WORKSPACE, tree), 0);
  }
  // .pnpm store: index every hash instance's node_modules/<name> (first hit wins)
  const pnpmDir = path.join(WORKSPACE, 'node_modules', '.pnpm');
  let hashes = [];
  try { hashes = fs.readdirSync(pnpmDir); } catch { }
  for (const h of hashes) consider(path.join(pnpmDir, h, 'node_modules'));
}

function copyIntoDeploy(name, src) {
  const dest = path.join(DEPLOY_NM, name);
  fs.rmSync(dest, { recursive: true, force: true });
  fs.mkdirSync(path.dirname(dest), { recursive: true });
  // Copy the package CONTENT only: never follow its node_modules links (the
  // fixpoint validator copies any genuinely required dep explicitly).
  fs.cpSync(src, dest, {
    recursive: true, dereference: true, force: true,
    filter: (s) => path.basename(s) !== 'node_modules',
  });
  return fs.existsSync(path.join(dest, 'package.json'));
}

console.log('walking deploy tree...');
const allDirs = collectPackageDirs(DEPLOY);
console.log('package dirs found:', allDirs.length);
buildWsIndex();
console.log('workspace source index:', wsIndex.size, 'packages');

const copied = new Set();
let pass = 0;
while (pass++ < 12) {
  // FULL-tree validation each round: previous rounds' copies introduce new
  // package dirs whose own deps must resolve too (deploy root must be a
  // complete flat closure, because moduleFallback links point here).
  resolveMemo.clear();
  const allDirs = collectPackageDirs(DEPLOY);
  const missing = collectMissing(allDirs);
  if (missing.length === 0) {
    console.log(`PASS ${pass}: closure complete over ${allDirs.length} package dirs ` +
      '(all required deps + non-optional peers resolve).');
    break;
  }
  const unique = [...new Set(missing.map(([, n]) => n))];
  console.log(`PASS ${pass}: ${missing.length} unresolved refs, ${unique.length} unique packages (dirs=${allDirs.length}).`);
  let anyCopied = false;
  for (const name of unique) {
    const src = wsIndex.get(name);
    if (src && copyIntoDeploy(name, src)) { copied.add(name); anyCopied = true; }
    else console.error(`  FILL-FAIL: ${name} (no workspace source)`);
  }
  if (!anyCopied) {
    console.error('Remaining unresolved (first 15):');
    for (const [dep, name] of missing.slice(0, 15)) console.error(`  ${name}  <- ${dep}`);
    process.exit(1);
  }
}
if (copied.size) console.log(`copied ${copied.size} packages into deploy root node_modules`);
