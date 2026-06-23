// 批量迁移测试 argv:旧顶层 ["group","cmd",...] → 新入口
// 策略:匹配 execute(["<g>","<c>" 前缀,按规则重写前两个元素,保留后续 argv。
const fs = require('fs');
const path = require('path');

const files = [
  'tests/bridge-commands.test.ts',
  'tests/cli.test.ts',
  'tests/batch.test.ts',
  'tests/runtime-code.test.ts',
  'tests/instances-prune.test.ts',
  'tests/docs-reflect.test.ts',
  'tests/tools-integration.test.ts',
  'tests/project-context-discovery.test.ts',
  'tests/project-tools.test.ts',
  'tests/security.test.ts'
];

const uiSlashMap = {
  'canvas/create': 'canvas.create',
  'text/create': 'text.create',
  'button/create': 'button.create',
  'image/create': 'image.create',
  'document/info': 'document.info',
  'document/create': 'document.create',
  'attach-document': 'attach-document',
  'create-panel-settings': 'create-panel-settings',
  'get-visual-tree': 'get-visual-tree'
};

function migrateLine(line) {
  // 匹配 execute(["g", "c" — 捕获 g 和 c(带引号和逗号)。
  // 之后保留 "... 之后的全部内容(含闭合 ] 和参数)。
  const m = line.match(/execute\(\["([^"]*)",\s*"([^"]*)"/);
  if (!m) {
    // 单元素 execute(["g"] 形态
    const m1 = line.match(/execute\(\["([^"]*)"\]/);
    if (!m1) return line;
    const g = m1[1];
    if (g === 'init' || g === 'tools' || g === 'context' || g === 'extensions') return line;
    if (g === 'capabilities') return line.replace(/execute\(\["capabilities"\]/, 'execute(["tools", "run", "diagnose.capabilities"]');
    if (g === 'doctor') return line.replace(/execute\(\["doctor"\]/, 'execute(["tools", "run", "diagnose.doctor"]');
    if (g === 'docs') return line.replace(/execute\(\["docs"\]/, 'execute(["tools", "run", "docs.list"]');
    if (g === 'package') return line.replace(/execute\(\["package"\]/, 'execute(["tools", "run", "package.list"]');
    return line;
  }
  const [, g, c] = m;
  // 基线顶层保留
  if (g === 'init' || g === 'tools' || g === 'context' || g === 'extensions') return line;

  // 找到前缀结束位置(第二个引号的闭合引号后),保留之后内容
  const cQuoteStart = line.indexOf(`"${c}"`, m.index + m[1].indexOf(c));
  const prefixEnd = cQuoteStart + c.length + 2; // 含两侧引号
  const after = line.slice(prefixEnd); // 形如 , "--flag", ...], runtime)

  function rebuild(newPrefix) {
    return line.slice(0, m.index) + newPrefix + after;
  }

  // instances
  if (g === 'instances') {
    if (c === 'list') return rebuild('execute(["context", "read", "instances.list"');
    if (c === 'prune') return rebuild('execute(["tools", "run", "instances.prune"');
  }
  // target
  if (g === 'target') {
    if (c === 'get') return rebuild('execute(["context", "read", "target.current"');
    if (c === 'set') return rebuild('execute(["tools", "run", "target.set"');
    if (c === 'clear') return rebuild('execute(["tools", "run", "target.clear"');
    if (c === 'alias') {
      // target alias set/unset/list <args> — 需读 after 第一个字符串
      const am = after.match(/^,\s*"([^"]*)"/);
      const sub = am ? am[1] : '';
      const aliasSub = sub === 'set' ? 'alias-set' : sub === 'unset' ? 'alias-unset' : 'alias-list';
      // 去掉 after 里的 sub 字符串
      let newAfter = after;
      if (am) newAfter = after.slice(am[0].length);
      return line.slice(0, m.index) + 'execute(["tools", "run", "target.' + aliasSub + '"' + newAfter;
    }
  }
  // capabilities/doctor
  if (g === 'capabilities') return rebuild('execute(["tools", "run", "diagnose.capabilities"');
  if (g === 'doctor') return rebuild('execute(["tools", "run", "diagnose.doctor"');
  // docs
  if (g === 'docs') return rebuild('execute(["tools", "run", "docs.' + c + '"');
  // reflect
  if (g === 'reflect') return rebuild('execute(["tools", "run", "reflect.' + c + '"');
  // package
  if (g === 'package') return rebuild('execute(["tools", "run", "package.' + c + '"');
  // batch
  if (g === 'batch') return rebuild('execute(["tools", "run", "batch.' + c + '"');
  // job
  if (g === 'job') return rebuild('execute(["tools", "run", "job.' + c + '"');
  // code
  if (g === 'code') return rebuild('execute(["tools", "run", "code.' + c + '"');
  // ui slash
  if (g === 'ui') {
    const mapped = uiSlashMap[c] ?? c.replace('/', '-');
    return rebuild('execute(["tools", "run", "ui.' + mapped + '"');
  }
  // 通用:["g","c",...] → ["tools","run","g.c",...]
  return rebuild('execute(["tools", "run", "' + g + '.' + c + '"');
}

let total = 0;
for (const f of files) {
  const fp = path.resolve(f);
  if (!fs.existsSync(fp)) continue;
  const src = fs.readFileSync(fp, 'utf8');
  const lines = src.split('\n');
  let changed = 0;
  for (let i = 0; i < lines.length; i++) {
    if (!lines[i].includes('execute([')) continue;
    const nl = migrateLine(lines[i]);
    if (nl !== lines[i]) { lines[i] = nl; changed++; }
  }
  if (changed > 0) {
    fs.writeFileSync(fp, lines.join('\n'));
    console.log(`${f}: ${changed} lines`);
    total += changed;
  }
}
console.log('total:', total);
