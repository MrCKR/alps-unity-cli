const fs = require('node:fs');
const path = require('node:path');

const root = process.cwd();
const dist = path.join(root, 'dist');

function copyDir(name) {
  const source = path.join(root, name);
  const target = path.join(dist, name);
  if (fs.existsSync(source)) {
    fs.rmSync(target, { recursive: true, force: true });
    fs.cpSync(source, target, { recursive: true });
  }
}

function copyFile(name) {
  const source = path.join(root, name);
  const target = path.join(dist, name);
  if (fs.existsSync(source)) {
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.copyFileSync(source, target);
  }
}

for (const dir of ['cli', 'unitypkg', 'extensions', 'schemas', 'skills', 'docs', 'contracts']) {
  copyDir(dir);
}

for (const file of ['README.md', 'package.json']) {
  copyFile(file);
}
