import { readFileSync, readdirSync, existsSync } from "node:fs";
import { join, relative, sep } from "node:path";
import { classifyAll, FACT_SEMANTICS, retiredCodes } from "./lib/error-code-taxonomy.mjs";
import { SEMANTIC_OVERRIDES } from "./lib/error-code-renames.mjs";

const root = process.argv[2] ?? process.cwd();
const registryPath = join(root, "contracts", "error-codes.json");
const failures = [];
const metrics = {};

const registry = JSON.parse(readFileSync(registryPath, "utf8"));
const registered = new Map(registry.codes.map((entry) => [entry.code, entry]));
const { entries, dynamicSites } = classifyAll(root);
const emitted = new Map(entries.map((entry) => [entry.code, entry]));
const emittedFiles = () => [...new Set(entries.flatMap((entry) => entry.files.map((file) => join(root, file))))];
metrics.registeredCodes = registered.size;
metrics.emittedCodes = emitted.size;

// R1 双向一致：源码发出的码必须已登记，登记的码必须仍被发出。
const unregistered = [...emitted.keys()].filter((code) => !registered.has(code)).sort();
const stale = [...registered.keys()].filter((code) => !emitted.has(code)).sort();
if (unregistered.length > 0) failures.push({ rule: "R1-code-not-registered", count: unregistered.length, samples: unregistered.slice(0, 20) });
if (stale.length > 0) failures.push({ rule: "R1-registry-entry-not-emitted", count: stale.length, samples: stale.slice(0, 20) });

// R2 文档覆盖：每个登记的码必须出现在公开文档里。
const docFiles = readdirSync(join(root, "docs")).filter((name) => name.endsWith(".md")).map((name) => join(root, "docs", name))
  .concat([join(root, "README.md")]);
const documented = new Set();
for (const path of docFiles) {
  if (!existsSync(path)) continue;
  for (const match of readFileSync(path, "utf8").matchAll(/E_[A-Z0-9_]+/g)) documented.add(match[0]);
}
const undocumented = [...registered.keys()].filter((code) => !documented.has(code)).sort();
metrics.documentedCodes = registered.size - undocumented.length;
if (undocumented.length > 0) failures.push({ rule: "R2-code-not-documented", count: undocumented.length, samples: undocumented.slice(0, 20) });

// R4 改名目标唯一且不与其他现存码冲突。
const targets = new Map();
for (const entry of registry.codes) {
  if (!entry.renameTo) continue;
  if (targets.has(entry.renameTo)) failures.push({ rule: "R4-duplicate-rename-target", count: 1, samples: [entry.renameTo, targets.get(entry.renameTo)] });
  if (registered.has(entry.renameTo)) failures.push({ rule: "R4-rename-target-exists", count: 1, samples: [entry.code, entry.renameTo] });
  targets.set(entry.renameTo, entry.code);
}
metrics.renameCandidates = targets.size;

// R5 事实与语义类必须一致，登记表不得自证新的语义。
for (const entry of registry.codes) {
  if (!entry.fact) continue;
  const base = FACT_SEMANTICS[entry.fact];
  if (!base) { failures.push({ rule: "R5-unknown-fact", count: 1, samples: [entry.code, entry.fact] }); continue; }
  const override = SEMANTIC_OVERRIDES[entry.code];
  if (override && !String(override.reason ?? "").trim())
    failures.push({ rule: "R5-override-without-reason", count: 1, samples: [entry.code] });
  const effectStarted = override?.effectStarted ?? base[0];
  const transient = override?.transient ?? base[1];
  if (entry.effectStarted !== effectStarted || entry.transient !== transient)
    failures.push({ rule: "R5-semantics-mismatch", count: 1, samples: [entry.code] });
  if (override && !entry.semanticsOverrideReason)
    failures.push({ rule: "R5-override-not-recorded", count: 1, samples: [entry.code] });
}
metrics.semanticsOverrides = Object.keys(SEMANTIC_OVERRIDES).length;

// R6 码必须能被静态枚举：禁止用运行时参数拼接。
if (dynamicSites.length > 0) failures.push({ rule: "R6-runtime-concatenated-code", count: dynamicSites.length, samples: [...new Set(dynamicSites.map((site) => `${site.file}${site.suffix}`))].slice(0, 20) });

// R7 产品源码不得包含占位或测试专用码。
const placeholders = [...emitted.keys()].filter((code) => ["E_X", "E_TEST_APPEND", "E_EDITOR_RELOADING", "E_BUILD_EFFECT_TEST_FAULT"].includes(code));
if (placeholders.length > 0) failures.push({ rule: "R7-placeholder-code-in-product", count: placeholders.length, samples: placeholders });

metrics.unclassifiedCodes = registry.codes.filter((entry) => !entry.fact).length;
metrics.factNotInVocabulary = registry.codes.filter((entry) => !entry.fact).slice(0, 106).length;

// R8 退役码不得回流：旧名一旦改过就不能再出现在源码或文档里。
const retired = retiredCodes();
const retiredHits = [];
for (const [oldCode, newCode] of Object.entries(retired)) {
  if (oldCode === newCode) continue;
  const pattern = new RegExp(`(?<![A-Z0-9_])${oldCode}(?![A-Z0-9_])`);
  for (const path of [...emittedFiles(), ...docFiles]) {
    if (!existsSync(path)) continue;
    if (pattern.test(readFileSync(path, "utf8"))) retiredHits.push(`${relative(root, path).split(sep).join("/")}: ${oldCode} -> ${newCode}`);
  }
}
if (retiredHits.length > 0) failures.push({ rule: "R8-retired-code-reappeared", count: retiredHits.length, samples: retiredHits.slice(0, 20) });

const report = { ok: failures.length === 0, metrics, failures };
console.log(JSON.stringify(report, null, 2));
if (failures.length > 0) process.exitCode = 1;
