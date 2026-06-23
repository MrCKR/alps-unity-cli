const fs = require('fs');
const path = require('path');
const dir = 'src/tools/metadata/groups';
const baseline = ['tools ', 'context ', 'extensions ', 'init'];
let changed = 0;
const touched = [];
for (const f of fs.readdirSync(dir)) {
  if (!f.endsWith('.ts')) continue;
  const fp = path.join(dir, f);
  let lines = fs.readFileSync(fp, 'utf8').split('\n');
  let lastName = null;
  let fileChanged = false;
  for (let i = 0; i < lines.length; i++) {
    const nm = lines[i].match(/^\s*name:\s*"([^"]+)"/);
    if (nm) lastName = nm[1];
    const cm = lines[i].match(/^(\s*cliCommand:\s*")([^"]*)(",?\s*)$/);
    if (cm && lastName) {
      const old = cm[2];
      if (baseline.some(b => old.startsWith(b))) continue;
      const newVal = 'tools run ' + lastName;
      if (old !== newVal) {
        lines[i] = cm[1] + newVal + cm[3];
        changed++;
        fileChanged = true;
      }
    }
  }
  if (fileChanged) {
    fs.writeFileSync(fp, lines.join('\n'));
    touched.push(f);
  }
}
console.log('cliCommand rewritten:', changed);
console.log('files touched:', touched.join(', '));
