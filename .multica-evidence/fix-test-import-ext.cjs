const fs = require('node:fs');
const path = require('node:path');

function walk(dir) {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) return walk(full);
    return entry.isFile() && entry.name.endsWith('.ts') ? [full] : [];
  });
}

for (const file of walk(path.join(process.cwd(), 'tests'))) {
  let text = fs.readFileSync(file, 'utf8');
  text = text.replace(/(from\s+["'])(\.\.?\/[^"']+?)(["'])/g, (match, prefix, specifier, suffix) => {
    if (specifier.endsWith('.js') || specifier.endsWith('.json') || specifier.endsWith('.node')) return match;
    return `${prefix}${specifier}.js${suffix}`;
  });
  text = text.replace(/(import\(\s*["'])(\.\.?\/[^"']+?)(["']\s*\))/g, (match, prefix, specifier, suffix) => {
    if (specifier.endsWith('.js') || specifier.endsWith('.json') || specifier.endsWith('.node')) return match;
    return `${prefix}${specifier}.js${suffix}`;
  });
  fs.writeFileSync(file, text);
}
