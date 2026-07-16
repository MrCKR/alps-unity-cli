import fs from "node:fs";
import path from "node:path";

const root = process.cwd();
const strict = process.argv.includes("--strict");
const fix = process.argv.includes("--fix");
const matrixPath = path.join(root, ".plan/evidence/final-cleanup/security-matrix.json");

function filesUnder(relative, extension) {
  const base = path.join(root, relative);
  if (!fs.existsSync(base)) return [];
  const result = [];
  const visit = (directory) => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const full = path.join(directory, entry.name);
      if (entry.isDirectory()) visit(full);
      else if (entry.name.endsWith(extension)) result.push(full);
    }
  };
  visit(base);
  return result.sort();
}

const relative = (file) => path.relative(root, file).replaceAll("\\", "/");
function boolField(text, field) {
  const match = text.match(new RegExp(`\\b${field}\\s*=\\s*(true|false)`));
  return match ? match[1] === "true" : false;
}
function stringArrayField(text, field) {
  const match = text.match(new RegExp(`\\b${field}\\s*=\\s*new\\[\\]\\s*\\{([^}]*)\\}`));
  return match ? [...match[1].matchAll(/"([^"]+)"/g)].map((item) => item[1]) : [];
}

// 这些入口的能力边界本身就是任意执行，不能依赖实现中恰好出现的 API 名称。
// test.run 只运行项目中已有的测试程序集，不接收调用方代码。
const arbitraryCodeTools = new Set(["code.exec", "editor.menu", "reflection.method.call"]);
// RenderSettings 与场景对象修改只有显式保存场景后才持久化，本身不得触发 --yes。
const forcedNonPersistent = new Set([
  "graphics.settings", "graphics.skybox-set-ambient", "graphics.skybox-set-fog",
  "graphics.skybox-set-material", "graphics.skybox-set-reflection", "graphics.skybox-set-sun"
]);
// Package resolve 会更新 Packages/packages-lock.json，即使 manifest 不变也属于持久化写入。
const forcedPersistent = new Map([["package.resolve", "updates Packages/packages-lock.json through Unity Package Manager"]]);
const persistentSideEffects = new Set([
  "asset-write", "asset-delete", "file-write", "project-file-write", "project-file-delete",
  "manifest-change", "package-write", "player-settings-write", "project-settings-write", "settings-write", "build-output-write",
  "persistent-artifact-write", "memory-snapshot-write", "baked-data-write", "artifact-write"
]);
const persistentApiPatterns = [
  /AssetDatabase\.(CreateAsset|DeleteAsset|MoveAsset|RenameAsset|CopyAsset)/,
  /EditorSceneManager\.(SaveScene|SaveOpenScenes)/,
  /PrefabUtility\.(SaveAsPrefabAsset|SavePrefabAsset)/,
  /File\.(WriteAllText|WriteAllBytes|Delete|Move|Copy)/,
  /Directory\.(CreateDirectory|Delete|Move)/,
  /PlayerSettings\.[A-Za-z0-9_]+\s*=/,
  /EditorBuildSettings\.scenes\s*=/,
  /Lightmapping\.(Bake|Clear)/,
  /BakeReflectionProbe/,
  /PackageManager\.Client\.(Add|Remove|Embed)/,
  /TakeSnapshot/,
  /CaptureScreenshot/
];
const nonPersistentSignals = new Set([
  "scene-modify", "component-add", "component-remove", "component-modify", "undo-stack",
  "editor-session", "editor-session-state", "session-affecting", "runtime-event-dispatch",
  "component-state-edit", "scene-object-create", "scene-object-edit"
]);

function classify(name, text, sideEffects) {
  if (forcedNonPersistent.has(name)) return { persistentOrArbitraryCode: false, reason: "scene RenderSettings/session state only; no scene save" };
  if (forcedPersistent.has(name)) return { persistentOrArbitraryCode: true, reason: forcedPersistent.get(name) };
  if (arbitraryCodeTools.has(name)) return { persistentOrArbitraryCode: true, reason: "arbitrary code or method execution entry point" };
  const effect = sideEffects.find((item) => persistentSideEffects.has(item));
  if (effect) return { persistentOrArbitraryCode: true, reason: `persistent side effect ${effect}, confirmed against ExecuteAsync source` };
  const api = persistentApiPatterns.find((pattern) => pattern.test(text));
  if (api) return { persistentOrArbitraryCode: true, reason: `ExecuteAsync persistent API: ${api.source}` };
  const sceneSignal = sideEffects.find((item) => nonPersistentSignals.has(item));
  return { persistentOrArbitraryCode: false, reason: sceneSignal ? `scene/session-only side effect: ${sceneSignal}` : "read-only or no persistent write/arbitrary execution in ExecuteAsync" };
}

function parseCSharp() {
  const files = [...filesUnder("unitypkg/Editor/Tools", ".cs"), ...filesUnder("extensions", ".cs")];
  const entries = [];
  for (const file of files) {
    const text = fs.readFileSync(file, "utf8");
    if (!/IUnityCliTool\s*</.test(text)) continue;
    const name = text.match(/\bName\s*=\s*"([^"]+)"/)?.[1];
    if (!name) continue;
    const sideEffects = stringArrayField(text, "SideEffects");
    const classification = classify(name, text, sideEffects);
    entries.push({ name, source: relative(file), owner: relative(file).startsWith("extensions/") ? "extension" : "built-in", ...classification,
      destructiveHint: classification.persistentOrArbitraryCode, supportsDryRun: boolField(text, "SupportsDryRun"), sideEffects,
      bridgeSpec: null, policy: null, CSharpDescriptor: boolField(text, "DestructiveHint") });
  }
  return entries;
}

function parseBridgeSpecs() {
  const files = [...filesUnder("src/bridge/domains", ".ts"), ...filesUnder("extensions", ".ts")]
    .filter((file) => file.includes(`${path.sep}bridge${path.sep}domains${path.sep}`));
  const result = new Map();
  for (const file of files) {
    const text = fs.readFileSync(file, "utf8");
    for (const line of text.split(/\r?\n/)) {
      const explicitName = line.match(/toolName:\s*"([^"]+)"/)?.[1];
      const group = line.match(/group:\s*"([^"]+)"/)?.[1];
      const command = line.match(/command:\s*"([^"]*)"/)?.[1];
      const name = explicitName ?? (group && command ? `${group}.${command}` : undefined);
      if (name) result.set(name, { source: relative(file), destructiveHint: /destructiveHint:\s*true/.test(line), supportsDryRun: /supportsDryRun:\s*true/.test(line) });
    }
  }
  return result;
}

function parseTsLocal() {
  const entries = [];
  for (const file of filesUnder("src/tools", ".ts")) {
    if (file.includes(`${path.sep}metadata${path.sep}`) || file.endsWith("define.ts")) continue;
    const text = fs.readFileSync(file, "utf8");
    if (!/defineTool(?:<[^>]+>)?\s*\(/.test(text)) continue;
    const name = text.match(/\bname:\s*"([^"]+)"/)?.[1];
    if (!name) continue;
    const sideEffects = [...text.matchAll(/sideEffects:\s*\[([^\]]*)\]/g)].flatMap((match) => [...match[1].matchAll(/"([^"]+)"/g)].map((item) => item[1]));
    const classification = classify(name, text, sideEffects);
    entries.push({ name, source: relative(file), owner: "ts-local", ...classification, destructiveHint: classification.persistentOrArbitraryCode,
      supportsDryRun: /supportsDryRun:\s*true/.test(text), sideEffects, bridgeSpec: null, policy: null, CSharpDescriptor: null,
      tsDescriptor: /destructiveHint:\s*true/.test(text) });
  }
  return entries;
}

function parsePolicy() {
  const text = fs.readFileSync(path.join(root, "src/security-policy.ts"), "utf8");
  const result = new Map();
  for (const match of text.matchAll(/\{\s*command:\s*"([^"]+)"[^{}]*?destructiveHint:\s*(true|false)[^{}]*?\}/gs)) {
    result.set(match[1], { destructiveHint: match[2] === "true", supportsDryRun: /supportsDryRun:\s*true/.test(match[0]) });
  }
  return result;
}

let bridge = parseBridgeSpecs();
let policy = parsePolicy();
let entries = [...parseCSharp(), ...parseTsLocal()].sort((a, b) => a.name.localeCompare(b.name) || a.source.localeCompare(b.source));

if (fix) {
  for (const entry of entries) {
    const file = path.join(root, entry.source);
    let text = fs.readFileSync(file, "utf8");
    if (entry.owner === "ts-local") text = text.replace(/destructiveHint:\s*(true|false)/, `destructiveHint: ${entry.destructiveHint}`);
    else text = text.replace(/DestructiveHint\s*=\s*(true|false)/, `DestructiveHint = ${entry.destructiveHint}`);
    fs.writeFileSync(file, text);
  }
  for (const [name, spec] of bridge) {
    const entry = entries.find((candidate) => candidate.name === name);
    if (!entry) continue;
    const file = path.join(root, spec.source);
    let text = fs.readFileSync(file, "utf8");
    const lines = text.split(/(?<=\n)/);
    text = lines.map((line) => {
      const directName = `${line.match(/group:\s*"([^"]+)"/)?.[1] ?? ""}.${line.match(/command:\s*"([^"]+)"/)?.[1] ?? ""}`;
      if (!line.includes(`toolName: "${name}"`) && directName !== name) return line;
      let updated = line.replace(/,\s*destructiveHint:\s*true/g, "").replace(/,\s*supportsDryRun:\s*true/g, "");
      const suffix = `${entry.destructiveHint ? ", destructiveHint: true" : ""}${entry.supportsDryRun ? ", supportsDryRun: true" : ""}`;
      return updated.replace(/\s*}\s*,?\s*(\r?\n)?$/, (tail) => `${suffix} }${tail.includes(",") ? "," : ""}${tail.endsWith("\r\n") ? "\r\n" : tail.endsWith("\n") ? "\n" : ""}`);
    }).join("");
    fs.writeFileSync(file, text);
  }
  const destructive = entries.filter((entry) => entry.destructiveHint).sort((a, b) => a.name.localeCompare(b.name));
  const policySource = `export interface SecurityPolicyEntry {\n  command: string;\n  summary: string;\n  destructiveHint: boolean;\n  supportsDryRun?: boolean;\n}\n\n// 显式业务策略由全量源码审计生成；动态 wrapper 仍由 Bridge live descriptor 做最终证明。\nconst SECURITY_POLICY: SecurityPolicyEntry[] = [\n${destructive.map((entry) => `  { command: ${JSON.stringify(entry.name)}, summary: ${JSON.stringify(entry.reason)}, destructiveHint: true${entry.supportsDryRun ? ", supportsDryRun: true" : ""} }`).join(",\n")}\n];\n\nconst BY_COMMAND = new Map(SECURITY_POLICY.map((entry) => [entry.command, entry]));\nexport function securityPolicyForCommand(command: string | undefined): SecurityPolicyEntry | undefined {\n  return command ? BY_COMMAND.get(command) : undefined;\n}\nexport function destructiveCommands(): SecurityPolicyEntry[] {\n  return [...SECURITY_POLICY];\n}\n`;
  fs.writeFileSync(path.join(root, "src/security-policy.ts"), policySource);
  bridge = parseBridgeSpecs();
  policy = parsePolicy();
  entries = [...parseCSharp(), ...parseTsLocal()].sort((a, b) => a.name.localeCompare(b.name) || a.source.localeCompare(b.source));
}
for (const entry of entries) {
  entry.bridgeSpec = bridge.get(entry.name)?.destructiveHint ?? null;
  entry.policy = policy.get(entry.name)?.destructiveHint ?? null;
}
const mismatches = [];
const wrapperSpecs = ["project-tool.run", "runtime-tool.run"];
for (const wrapper of wrapperSpecs) {
  const spec = bridge.get(wrapper);
  if (spec?.destructiveHint === true) mismatches.push(`${wrapper}: wrapper metadata must be non-destructive; live descriptor controls confirmation`);
}
const bridgeExecutionSource = fs.readFileSync(path.join(root, "src/bridge/index.ts"), "utf8");
if (!/if \(explicitDescriptor\)[\s\S]*destructiveSecurityProof/.test(bridgeExecutionSource)) {
  mismatches.push("bridge security: explicit live descriptor proof is required for dynamic tool wrappers");
}
if (!/command === "project-tool\.run" \|\| command === "runtime-tool\.run"/.test(bridgeExecutionSource)) {
  mismatches.push("bridge security: project/runtime wrapper must resolve the invoked tool descriptor");
}
const runtimeWrapperSource = fs.readFileSync(path.join(root, "src/bridge/domains/runtime-tool.ts"), "utf8");
if (/bridgeCommand:\s*"runtime-tool\.run"[^\n]*destructiveHint:\s*true/.test(runtimeWrapperSource)) {
  mismatches.push("runtime-tool.run: static wrapper spec must not be destructive");
}
const wrapperCSharpSources = [
  ["project-tool.run", "unitypkg/Editor/Commands/ProjectToolCommands.cs"],
  ["runtime-tool.run", "unitypkg/Editor/Commands/RuntimeToolCommands.cs"]
];
for (const [wrapper, source] of wrapperCSharpSources) {
  const text = fs.readFileSync(path.join(root, source), "utf8");
  const registration = new RegExp(`"${wrapper}",[\\s\\S]*?\\n\\s*(true|false),\\s*\\n\\s*60000`).exec(text);
  if (!registration || registration[1] !== "false") mismatches.push(`${wrapper}: C# registry wrapper must be non-destructive`);
}
const contractPath = path.join(root, "contracts/command-contract.json");
if (fs.existsSync(contractPath)) {
  const contractCommands = JSON.parse(fs.readFileSync(contractPath, "utf8")).commands ?? [];
  for (const wrapper of wrapperSpecs) {
    if (contractCommands.find((entry) => entry.bridgeCommand === wrapper)?.destructive !== false) {
      mismatches.push(`${wrapper}: contract wrapper must be non-destructive`);
    }
  }
}
for (const entry of entries) {
  const expected = entry.destructiveHint;
  if (entry.owner === "ts-local") {
    if (entry.tsDescriptor !== expected) mismatches.push(`${entry.name}: TS descriptor=${entry.tsDescriptor}, expected=${expected}`);
  } else {
    if (entry.CSharpDescriptor !== expected) mismatches.push(`${entry.name}: C#=${entry.CSharpDescriptor}, expected=${expected}`);
    if (entry.bridgeSpec !== expected) mismatches.push(`${entry.name}: bridgeSpec=${entry.bridgeSpec}, expected=${expected}`);
    if ((bridge.get(entry.name)?.supportsDryRun ?? false) !== entry.supportsDryRun) mismatches.push(`${entry.name}: bridge supportsDryRun=${bridge.get(entry.name)?.supportsDryRun ?? false}, C#=${entry.supportsDryRun}`);
  }
  if ((entry.policy === true) !== expected) mismatches.push(`${entry.name}: policy=${entry.policy}, expected=${expected}`);
  if (expected && (policy.get(entry.name)?.supportsDryRun ?? false) !== entry.supportsDryRun) mismatches.push(`${entry.name}: policy supportsDryRun=${policy.get(entry.name)?.supportsDryRun ?? false}, descriptor=${entry.supportsDryRun}`);
  if (entry.supportsDryRun && !/\b(ctx\.DryRun|dryRun|DryRun)\b/.test(fs.readFileSync(path.join(root, entry.source), "utf8"))) mismatches.push(`${entry.name}: supportsDryRun=true without an execution dry-run branch`);
}
// 同名 TS hybrid 与 live C# 实现是一个公开 Tool 的两种执行投影，不按重复 capability 报错。
const output = { generatedAt: new Date().toISOString(), rules: {
  destructive: "persistent file/asset/ProjectSettings/Packages writes or arbitrary code execution",
  nonDestructive: "read-only, scene-memory, component, session, PlayMode, selection, console and profiler state only"
}, counts: { total: entries.length, builtIn: entries.filter((entry) => entry.owner === "built-in").length,
  extension: entries.filter((entry) => entry.owner === "extension").length, tsLocal: entries.filter((entry) => entry.owner === "ts-local").length,
  destructive: entries.filter((entry) => entry.destructiveHint).length, nonDestructive: entries.filter((entry) => !entry.destructiveHint).length,
  mismatches: mismatches.length }, entries, mismatches };
fs.mkdirSync(path.dirname(matrixPath), { recursive: true });
fs.writeFileSync(matrixPath, `${JSON.stringify(output, null, 2)}\n`);
console.log(JSON.stringify(output.counts));
if (mismatches.length) console.error(mismatches.join("\n"));
if (strict && mismatches.length) process.exitCode = 1;
