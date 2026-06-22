const fs = require('node:fs');
const path = require('node:path');

const root = process.cwd();
const srcRoot = path.join(root, 'src');
const testsRoot = path.join(root, 'tests');

function walk(dir) {
  const entries = fs.readdirSync(dir, { withFileTypes: true });
  return entries.flatMap((entry) => {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) return walk(full);
    return entry.isFile() && entry.name.endsWith('.test.ts') ? [full] : [];
  });
}

function posixRelative(fromDir, toPath) {
  let rel = path.relative(fromDir, toPath).replaceAll(path.sep, '/');
  if (!rel.startsWith('.')) rel = './' + rel;
  return rel;
}

function stripJs(filePath) {
  return filePath.endsWith('.js') ? filePath.slice(0, -3) : filePath;
}

function resolveOriginalImport(originalDir, specifier) {
  const absolute = path.resolve(originalDir, specifier);
  return absolute.endsWith('.js') ? absolute : `${absolute}.js`;
}

function runtimeOutputDirFor(destRel) {
  return path.join(root, 'dist', 'tests', path.dirname(destRel));
}

function runtimePathForOriginalUrl(originalDir, literal) {
  const absolute = path.resolve(originalDir, literal);
  const srcRel = path.relative(srcRoot, absolute);
  if (!srcRel.startsWith('..') && !path.isAbsolute(srcRel)) {
    return path.join(root, 'dist', 'src', srcRel);
  }
  return absolute;
}

const files = walk(srcRoot);
for (const sourceFile of files) {
  const rel = path.relative(srcRoot, sourceFile);
  const destFile = path.join(testsRoot, rel);
  const originalDir = path.dirname(sourceFile);
  const destDir = path.dirname(destFile);
  const runtimeDestDir = runtimeOutputDirFor(rel);

  let text = fs.readFileSync(sourceFile, 'utf8');

  text = text.replace(/(from\s+["'])(\.\.?\/[^"']+)(["'])/g, (match, prefix, specifier, suffix) => {
    const originalTarget = resolveOriginalImport(originalDir, specifier);
    const srcRel = path.relative(srcRoot, originalTarget);
    if (srcRel.startsWith('..') || path.isAbsolute(srcRel)) return match;
    const newSpecifier = stripJs(posixRelative(destDir, path.join(srcRoot, srcRel)));
    return `${prefix}${newSpecifier}${suffix}`;
  });

  text = text.replace(/(import\(\s*["'])(\.\.?\/[^"']+)(["']\s*\))/g, (match, prefix, specifier, suffix) => {
    const originalTarget = resolveOriginalImport(originalDir, specifier);
    const srcRel = path.relative(srcRoot, originalTarget);
    if (srcRel.startsWith('..') || path.isAbsolute(srcRel)) return match;
    const newSpecifier = stripJs(posixRelative(destDir, path.join(srcRoot, srcRel)));
    return `${prefix}${newSpecifier}${suffix}`;
  });

  text = text.replace(/(new URL\(\s*["'])(\.\.?\/[^"']+|[^"'/:]+\.js)(["']\s*,\s*import\.meta\.url\s*\))/g, (match, prefix, literal, suffix) => {
    const runtimeTarget = runtimePathForOriginalUrl(originalDir, literal);
    const newLiteral = posixRelative(runtimeDestDir, runtimeTarget);
    return `${prefix}${newLiteral}${suffix}`;
  });

  fs.mkdirSync(path.dirname(destFile), { recursive: true });
  fs.writeFileSync(destFile, text);
  fs.rmSync(sourceFile);
}

console.log(`migrated ${files.length} test files`);
