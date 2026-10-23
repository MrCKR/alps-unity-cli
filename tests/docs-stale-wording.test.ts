/**
 * Stale wording tests for documentation and skill files.
 *
 * Ensures docs/skill/examples reflect actual CLI commands and do not
 * reference non-existent commands or outdated terminology.
 *
 * Chinese docs & CLI Skill rewrite
 */

import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, extname } from "node:path";

const projectRoot = process.cwd();

// ── Helpers ────────────────────────────────────────────────

function readDocFile(relPath: string): string {
  return readFileSync(join(projectRoot, relPath), "utf8");
}

const PUBLIC_TEXT_EXTENSIONS = new Set([".md", ".json", ".jsonl", ".yaml", ".yml"]);

/** 递归列出公开说明与示例，确保嵌套 reference/eval 不逃逸治理。 */
function listPublicTextFiles(dir: string): string[] {
  const root = join(projectRoot, dir);
  const results: string[] = [];

  function visit(absDir: string, relDir: string): void {
    try {
      for (const entry of readdirSync(absDir).sort()) {
        const full = join(absDir, entry);
        const relativePath = `${relDir}/${entry}`.replaceAll("\\", "/");
        if (statSync(full).isDirectory()) visit(full, relativePath);
        else if (PUBLIC_TEXT_EXTENSIONS.has(extname(entry))) results.push(relativePath);
      }
    } catch {
      // 可选公开目录不存在时没有待扫描文件。
    }
  }

  visit(root, dir);
  return results;
}

function allDocFiles(): string[] {
  return [...new Set([
    "README.md",
    "AGENTS.md",
    ...listPublicTextFiles("docs"),
    ...listPublicTextFiles("examples"),
    ...listPublicTextFiles("skills/unitycli"),
    ...listPublicTextFiles("skill-factory"),
    ...listPublicTextFiles("extensions/probuilder/docs"),
    ...listPublicTextFiles("extensions/cinemachine/docs"),
    ...listPublicTextFiles("extensions/urp/docs"),
    ...listPublicTextFiles("extensions/hdrp/docs"),
    ".pi/skills/unity-cli-capability/SKILL.md",
    ".pi/skills/unity-cli-extension/SKILL.md",
    ".pi/skills/unity-cli-validation/SKILL.md",
    ".pi/agents/unity-cli-gate.md",
  ])].sort();
}

// ── Current commands that MUST appear in docs ──

const RUNTIME_COMMANDS = [
  "tools group",            // actual command to list groups
  "tools group list",       // actual command to list tools in a group
  "tools describe",         // actual command for full descriptor
  "tools run",              // actual command to execute
  "tools search",           // actual command for keyword search
  "command",                // same Tool catalog, schema argument invocation
];

const EXTENSION_COMMANDS = [
  "extensions list",        // actual command to list extensions
  "extensions install",     // actual command to install extension
  "extensions remove",      // actual command to remove extension
  "extensions doctor",      // actual command for extension diagnostics
];

// ── Field naming conventions ──

// JSON response fields use camelCase (nextActions, retryAfterMs).
// Non-camelCase field names are assembled to keep full-repo literal scans clean.
const LEGACY_NEXT_ACTIONS_FIELD = "next" + "_actions";
const LEGACY_RETRY_AFTER_MS_FIELD = "retry" + "_after_ms";
const STALE_SNAKE_CASE = [
  LEGACY_NEXT_ACTIONS_FIELD,   // must be nextActions
  LEGACY_RETRY_AFTER_MS_FIELD, // must be retryAfterMs
];

// Must use correct error code names
const STALE_ERROR_CODES = [
  "E_PLAYMODE_NOT_READY",   // must be E_RUNTIME_UNAVAILABLE
  "E_RUNTIME_NOT_READY",     // must be E_RUNTIME_UNAVAILABLE
];

// ── Tests ──────────────────────────────────────────────────

test("docs contain required current command references", () => {
  // 主 Skill 聚焦运行时用法，扩展管理命令由安装引用文件承载。
  const requiredFiles = [
    { file: "README.md", commands: [...RUNTIME_COMMANDS, ...EXTENSION_COMMANDS] },
    { file: "skills/unitycli/SKILL.md", commands: RUNTIME_COMMANDS },
    { file: "skills/unitycli/references/installation.md", commands: EXTENSION_COMMANDS },
  ];
  const violations: string[] = [];

  for (const { file, commands } of requiredFiles) {
    const content = readDocFile(file);
    for (const cmd of commands) {
      if (!content.includes(cmd)) {
        violations.push(`${file}: missing required command "${cmd}"`);
      }
    }
  }

  assert.equal(
    violations.length,
    0,
    `Required commands missing from docs:\n${violations.join("\n")}`
  );
});

test("README is an operational npm page for one-line AI setup and manual verification", () => {
  const readme = readDocFile("README.md");
  assert.match(readme, /npm install -g @alpsckr\/unitycli/);
  assert.match(readme, /执行 unitycli init help，然后照做。/);
  for (const behavior of ["unique current Unity project", "exact Skill directory", "normally close", "independently authorized effects"]) assert.match(readme, new RegExp(behavior, "i"));
  for (const command of ["unitycli init help", "unitycli init discover", "unitycli init package --project", "--close-editor", "unitycli init skill --target", "unitycli init skill --agent .agents --scope workspace", "unitycli init skill --agent claude --scope user", "--check", "unitycli doctor", "unitycli instances list", "npm uninstall -g @alpsckr/unitycli"]) assert.match(readme, new RegExp(command.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  assert.match(readme, /Node\.js 20/);
  assert.match(readme, /Unity 2022\.3/);
  assert.match(readme, /First Unity Call/);
  assert.doesNotMatch(readme, /Project Baseline|Bridge command、handler、journal|completion contract都是内部实现|--pkg-only|--skill-only/);
  assert.match(readDocFile("docs/architecture.md"), /Registry v3|Health v3|Tool Catalog|Installation Control Plane/);
  assert.match(readDocFile("docs/development.md"), /build:test|architecture:validate|Dev Unity Isolation|npm pack --dry-run/);
});

test("public search guidance uses the current pipe-token syntax", () => {
  const oldSearchPlaceholder = "tools search <" + "keyword>";
  const violations: string[] = [];
  for (const file of allDocFiles()) {
    const content = readDocFile(file);
    if (content.includes(oldSearchPlaceholder)) violations.push(`${file}: uses the old single-keyword placeholder`);
  }
  assert.deepEqual(violations, []);

  for (const file of ["README.md", "docs/tool-discovery.md", "docs/error-codes.md", "docs/tool-execution.md", "skills/unitycli/SKILL.md"]) {
    assert.match(readDocFile(file), /tools search '?<k1\|k2\|\.\.\.>'?/, `${file} must publish the current search syntax`);
  }
  for (const file of ["README.md", "docs/tool-discovery.md", "docs/tool-execution.md", "skills/unitycli/SKILL.md", "skills/unitycli/references/operation-protocol.md"]) {
    const content = readDocFile(file);
    assert.match(content, /(?:PowerShell|powershell).{0,120}(?:single quote|单引号|引号)/is, `${file} must explain PowerShell quoting`);
    assert.match(content, /tools search '[^']*\|[^']*'/, `${file} must show one quoted pipe-token search`);
  }
});

test("public Tool guidance keeps the command host budget outside the CLI deadline", () => {
  for (const file of ["README.md", "docs/tool-execution.md", "skills/unitycli/SKILL.md", "skills/unitycli/references/operation-protocol.md"]) {
    const content = readDocFile(file);
    assert.match(content, /--timeout-ms.{0,80}(?:5000|5秒)/s, `${file} must reserve command-host time beyond the CLI timeout`);
    assert.match(content, /(?:等待|wait).{0,24}(?:CLI|unitycli|进程|process).{0,12}(?:退出|exit)/is, `${file} must require observing CLI process exit`);
    assert.match(content, /(?:不(?:会|能)|不能|does not).{0,30}(?:停止|中止|强停|stop).{0,30}(?:Unity|effect|C#)/is, `${file} must not claim CLI timeout cancels Unity work`);
  }
});

test("public instance guidance explains default filtering and explicit full expansion", () => {
  const readme = readDocFile("README.md");
  const targetDocs = readDocFile("docs/target-and-instance.md");
  const skill = readDocFile("skills/unitycli/SKILL.md");
  for (const [file, content] of [["README.md", readme], ["docs/target-and-instance.md", targetDocs], ["skills/unitycli/SKILL.md", skill]] as const) {
    assert.match(content, /instances list --all/, `${file} must publish explicit full expansion`);
    assert.match(content, /(?:默认|default).{0,24}offline/i, `${file} must explain the default offline filter`);
  }
  assert.match(targetDocs, /hiddenOfflineCount/);
  assert.match(targetDocs, /不(?:会)?恢复内部 session、快照、进程 claim 或诊断字段/);
  for (const [file, content] of [["README.md", readme], ["docs/target-and-instance.md", targetDocs], ["skills/unitycli/SKILL.md", skill]] as const) {
    assert.match(content, /invalidRecordCount/, `${file} must expose isolated invalid record count`);
    assert.match(content, /(?:旧格式|损坏|不可读).{0,60}(?:不(?:会)?阻断|继续返回|逐文件|隔离)/s, `${file} must explain per-record isolation`);
    assert.match(content, /instances prune --instance/, `${file} must publish writer-owned historical cleanup`);
    assert.match(content, /removed.{0,30}(?:deferred|remaining)|deferred.{0,30}removed/s, `${file} must explain real prune counters`);
    assert.match(content, /(?:不得|不要|不能).{0,30}(?:读取|删除).{0,20}(?:内部|记录|文件)/s, `${file} must reject manual internal-state mutation`);
    assert.match(content, /(?:首次|启动|编译|重载).{0,60}(?:等待|ready)/is, `${file} must explain bounded readiness waiting`);
  }
  assert.doesNotMatch(`${readme}\n${targetDocs}\n${skill}`, /Only the C# registry writer may remove|固定返回\s*removed.?0|只报告.{0,20}removed.?0/i);
});

test("skill does not use stale CLI parameter/field/error names", () => {
  const skillPath = join(projectRoot, "skills", "unitycli", "SKILL.md");
  const skill = readFileSync(skillPath, "utf8");

  // --input-json is not a CLI parameter; the CLI uses --input
  const inputJsonMatches = skill.match(/--input-json/g);
  const inputJsonAllowed = (skill.match(/Do not.*--input-json|不要.*--input-json|不得.*--input-json|用 `--input-json`/g) ?? []).length;
  assert.ok(
    (inputJsonMatches?.length ?? 0) - inputJsonAllowed <= 0,
    `skill must not teach --input-json (found ${(inputJsonMatches?.length ?? 0)} occurrences beyond the warning)`
  );

  // Skill response fields must use camelCase.
  const nextActionsStale = skill.match(new RegExp(LEGACY_NEXT_ACTIONS_FIELD, "g"));
  assert.ok(
    (nextActionsStale?.length ?? 0) === 0,
    `skill must use nextActions (camelCase), not ${LEGACY_NEXT_ACTIONS_FIELD}`
  );
  const retryAfterMsStale = skill.match(new RegExp(LEGACY_RETRY_AFTER_MS_FIELD, "g"));
  assert.ok(
    (retryAfterMsStale?.length ?? 0) === 0,
    `skill must use retryAfterMs (camelCase), not ${LEGACY_RETRY_AFTER_MS_FIELD}`
  );

  // E_RUNTIME_UNAVAILABLE, not E_PLAYMODE_NOT_READY or E_RUNTIME_NOT_READY
  const stalePlayModeCode = /E_(?:PLAYMODE_NOT_READY|RUNTIME_NOT_READY|PLAYMODE_UNAVAILABLE)/g;
  const playmodeMatches = skill.match(stalePlayModeCode);
  const playmodeAllowed = (skill.match(/使用 `E_(?:PLAYMODE_NOT_READY|RUNTIME_NOT_READY|PLAYMODE_UNAVAILABLE)`/g) ?? []).length;
  assert.ok(
    (playmodeMatches?.length ?? 0) - playmodeAllowed <= 0,
    `skill must use E_RUNTIME_UNAVAILABLE, not a playmode-specific availability code outside warning context`
  );
});

test("docs use camelCase for error response fields", () => {
  const files = allDocFiles();
  const violations: string[] = [];

  for (const file of files) {
    let content: string;
    try {
      content = readDocFile(file);
    } catch {
      continue;
    }

    // Error response fields must use camelCase (nextActions, retryAfterMs).
    if (content.includes(LEGACY_NEXT_ACTIONS_FIELD)) {
      violations.push(`${file}: uses stale "${LEGACY_NEXT_ACTIONS_FIELD}" (should be "nextActions")`);
    }
    if (content.includes(LEGACY_RETRY_AFTER_MS_FIELD)) {
      violations.push(`${file}: uses stale "${LEGACY_RETRY_AFTER_MS_FIELD}" (should be "retryAfterMs")`);
    }

    // E_PLAYMODE_NOT_READY / E_RUNTIME_NOT_READY / E_PLAYMODE_UNAVAILABLE → E_RUNTIME_UNAVAILABLE
    const stalePlayModeCode = /E_(?:PLAYMODE_NOT_READY|RUNTIME_NOT_READY|PLAYMODE_UNAVAILABLE)/;
    if (stalePlayModeCode.test(content)) {
      const lines = content.split("\n");
      let staleCount = 0;
      for (const line of lines) {
        if (!stalePlayModeCode.test(line)) continue;
        const trimmed = line.trim().toLowerCase();
        if (trimmed.includes("不是") || trimmed.includes("不要") || trimmed.includes("do not") || trimmed.includes("instead of") || (trimmed.includes("使用 ") && trimmed.includes(" | "))) continue;
        staleCount++;
      }
      if (staleCount > 0) {
        violations.push(`${file}: uses a retired playmode availability code instead of "E_RUNTIME_UNAVAILABLE" (outside warning context)`);
      }
    }
  }

  assert.equal(
    violations.length,
    0,
    `Stale field names found:\n${violations.join("\n")}`
  );
});

test("public docs do not expose private Bridge HTTP routes", () => {
  const violations: string[] = [];
  for (const file of allDocFiles()) {
    let content: string;
    try {
      content = readDocFile(file);
    } catch {
      continue;
    }
    if (/\bPOST\s+\/bridge\b|\/bridge\/execute\b/i.test(content)) violations.push(file);
  }
  assert.deepEqual(violations, []);
});

test("docs expose only the current Tool、command和extension surfaces", () => {
  const readme = readDocFile("README.md");
  const architecture = readDocFile("docs/architecture.md");
  assert.doesNotMatch(readme, /\bcontext (?:list|search|describe|read)\b/);
  assert.match(architecture, /Public top-level commands are frozen to:[\s\S]*tools[\s\S]*command[\s\S]*init/);

  const staleContextDocs: string[] = [];
  const staleCliDocs: string[] = [];
  const staleCountDocs: string[] = [];
  const oldCapabilityRoots = "scene|asset|camera|go|prefab|package|build|test|editor|profiler|context";
  const oldCliPattern = new RegExp(`(?:unitycli|dist/cli\\.js)\\s+(?:${oldCapabilityRoots})\\b`, "i");
  const staleCountPattern = /\b(?:43|44|361|404|408|409|413|935|945)\b.{0,24}\b(?:TS-local|C# Tool|completion|command|tests?)\b/i;

  for (const file of allDocFiles()) {
    const content = readDocFile(file).replaceAll("UnityCliProjectToolContext", "");
    if (/\bContext\b|\bcontext\s+(?:list|search|describe|read)\b|IUnityCliContext|ProjectContextRegistry|defineContext|registeredContext/.test(content)) {
      staleContextDocs.push(file);
    }
    if (oldCliPattern.test(content)) staleCliDocs.push(file);
    if (staleCountPattern.test(content)) staleCountDocs.push(file);
  }

  assert.deepEqual(staleContextDocs, [], "public text must not expose the deleted Context surface");
  assert.deepEqual(staleCliDocs, [], "public text must not invoke Unity capability domains as top-level commands");
  assert.deepEqual(staleCountDocs, [], "public text must not freeze obsolete generated inventory counts");

  for (const file of ["docs/architecture.md", "AGENTS.md", "skills/unitycli/SKILL.md", "docs/availability-differences.md"]) {
    const content = readDocFile(file);
    for (const boundary of ["auth", "Unity Hub", "Cloud", "License", "proxy"]) {
      assert.match(content, new RegExp(boundary, "i"), `${file} must state the external control-plane boundary for ${boundary}`);
    }
  }

  // Extensions commands should be documented as available
  assert.ok(
    readme.includes("extensions list") || readme.includes("extensions install"),
    "README must document extensions commands as available"
  );
});
