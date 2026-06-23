const { spawnSync } = require('node:child_process');

const result = spawnSync(process.execPath, ['scripts/gen-command-contract.mjs'], {
  stdio: 'inherit',
  shell: false
});

process.exit(result.status ?? 1);
