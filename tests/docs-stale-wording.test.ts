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
import { fileURLToPath } from "node:url";
import { join, extname } from "node:path";

const __dirname = fileURLToPath(new URL(".", import.meta.url));

// ── Helpers ────────────────────────────────────────────────

function readDocFile(relPath: string): string {
  return readFileSync(join(__dirname, "..", relPath), "utf8");
}

function listMarkdownFiles(dir: string): string[] {
  const absDir = join(__dirname, "..", dir);
  const results: string[] = [];
  try {
    for (const entry of readdirSync(absDir)) {
      const full = join(absDir, entry);
      if (statSync(full).isFile() && extname(entry) === ".md") {
        results.push(`${dir}/${entry}`);
      }
    }
  } catch {
    // directory may not exist
  }
  return results;
}

function allDocFiles(): string[] {
  const files: string[] = [];
  files.push("README.md");
  files.push("skills/unity_cli/SKILL.md");
  files.push(...listMarkdownFiles("docs"));
  files.push(...listMarkdownFiles("examples"));
  files.push(...listMarkdownFiles("extensions/probuilder/docs"));
  files.push(...listMarkdownFiles("extensions/cinemachine/docs"));
  files.push(...listMarkdownFiles("extensions/urp/docs"));
  files.push(...listMarkdownFiles("extensions/hdrp/docs"));
  return files;
}

// ── Stale command names that must NOT appear as current/available ──

// Old ProBuilder coarse tool names replaced by fine-grained extension tools.
// probuilder.shape → probuilder.create-shape
// probuilder.create → probuilder.create-shape / probuilder.create-poly-shape
// probuilder.edit → probuilder.extrude-faces / probuilder.delete-faces / etc.
// probuilder.csg → probuilder.delete-faces / probuilder.combine-meshes / probuilder.merge-objects
//
// Each entry is [staleName, regexPattern] to avoid false positives:
// - "probuilder.create" must not match "probuilder.create-shape"
// - "probuilder.edit" must not match "probuilder.editor"
const STALE_PROBUILDER_PATTERNS: [string, RegExp][] = [
  // Dot-format: probuilder.shape, probuilder.create, probuilder.edit, probuilder.csg
  ["probuilder.shape", /probuilder\.shape(?![\w-])/g],
  ["probuilder.create", /probuilder\.create(?![\w-])/g],
  ["probuilder.edit", /probuilder\.edit(?![\w-])/g],
  ["probuilder.csg", /probuilder\.csg(?![\w-])/g],
  // Space-format: "probuilder shape", "probuilder create", "probuilder edit", "probuilder csg"
  // These are CLI shortcut forms that no longer exist (replaced by fine-grained extension tools).
  // Must not match valid forms like "probuilder create-shape" or "probuilder extrude-faces".
  ["probuilder shape ", /probuilder\s+shape(?![\w-])/g],
  ["probuilder create ", /probuilder\s+create(?![\w-])/g],
  ["probuilder edit ", /probuilder\s+edit(?![\w-])/g],
  ["probuilder csg ", /probuilder\s+csg(?![\w-])/g],
];

// ── Current commands that MUST appear in docs ──

const REQUIRED_COMMANDS = [
  "tools group",            // actual command to list groups
  "tools group list",       // actual command to list tools in a group
  "tools describe",         // actual command for full descriptor
  "tools run",              // actual command to execute
  "tools search",           // actual command for keyword search
  "--include-custom",       // flag to discover project tools
  "context list",           // actual command to list contexts
  "context describe",       // actual command for context descriptor
  "context read",           // actual command to read context
  "extensions list",        // actual command to list extensions
  "extensions install",     // actual command to install extension
  "extensions remove",      // actual command to remove extension
  "extensions doctor",      // actual command for extension diagnostics
];

// ── Field naming conventions ──

// Stage 7: JSON fields are now camelCase (nextActions, retryAfterMs).
// Legacy snake-style field names are assembled to keep full-repo literal scans clean.
const LEGACY_NEXT_ACTIONS_FIELD = "next" + "_actions";
const LEGACY_RETRY_AFTER_MS_FIELD = "retry" + "_after_ms";
const STALE_SNAKE_CASE = [
  LEGACY_NEXT_ACTIONS_FIELD,   // must be nextActions
  LEGACY_RETRY_AFTER_MS_FIELD, // must be retryAfterMs
];

// Must use correct error code names
const STALE_ERROR_CODES = [
  "E_PLAYMODE_NOT_READY",   // must be E_RUNTIME_NOT_READY
];

// ── Tests ──────────────────────────────────────────────────

test("docs do not use stale ProBuilder coarse tool names", () => {
  const files = allDocFiles();
  const violations: string[] = [];

  for (const file of files) {
    let content: string;
    try {
      content = readDocFile(file);
    } catch {
      continue;
    }

    for (const [name, pattern] of STALE_PROBUILDER_PATTERNS) {
      const lines = content.split("\n");
      for (let i = 0; i < lines.length; i++) {
        const line = lines[i];
        // Use a fresh regex (global flag is stateful); reset lastIndex
        pattern.lastIndex = 0;
        if (!pattern.test(line)) continue;

        // Allow in explicit removed or historical context
        const contextWindow = lines.slice(Math.max(0, i - 3), i + 4).join(" ");
        if (
          contextWindow.includes("废弃") ||
          contextWindow.includes("移除") ||
          contextWindow.includes("removed") ||
          contextWindow.includes("已移除") ||
          contextWindow.includes("已废弃") ||
          contextWindow.includes("历史") ||
          contextWindow.includes("旧名") ||
          contextWindow.includes("替代") ||
          contextWindow.includes("不得") ||
          contextWindow.includes("不要") ||
          contextWindow.includes("旧") ||
          contextWindow.includes("coarse") ||
          contextWindow.includes("规划中") ||
          contextWindow.includes("planned")
        ) {
          continue;
        }

        violations.push(`${file}:${i + 1}: "${name}" appears without deprecation or historical marker`);
      }
    }
  }

  assert.equal(
    violations.length,
    0,
    `Stale ProBuilder names found without markers:\n${violations.join("\n")}`
  );
});

test("docs contain required current command references", () => {
  // Check that README and SKILL.md contain all required commands
  const requiredFiles = ["README.md", "skills/unity_cli/SKILL.md"];
  const violations: string[] = [];

  for (const file of requiredFiles) {
    let content: string;
    try {
      content = readDocFile(file);
    } catch {
      continue;
    }

    for (const cmd of REQUIRED_COMMANDS) {
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

test("skill does not use stale CLI parameter/field/error names", () => {
  const skillPath = fileURLToPath(new URL("../../skills/unity_cli/SKILL.md", import.meta.url));
  const skill = readFileSync(skillPath, "utf8");

  // --input-json is not a CLI parameter; the CLI uses --input
  const inputJsonMatches = skill.match(/--input-json/g);
  const inputJsonAllowed = (skill.match(/Do not.*--input-json|不要.*--input-json|不得.*--input-json|用 `--input-json`/g) ?? []).length;
  assert.ok(
    (inputJsonMatches?.length ?? 0) - inputJsonAllowed <= 0,
    `skill must not teach --input-json (found ${(inputJsonMatches?.length ?? 0)} occurrences beyond the warning)`
  );

  // Stage 7: JSON fields are now camelCase. Skill must not use legacy snake-style names.
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

  // E_RUNTIME_NOT_READY, not E_PLAYMODE_NOT_READY
  // Allow in warning table
  const playmodeMatches = skill.match(/E_PLAYMODE_NOT_READY/g);
  const playmodeAllowed = (skill.match(/使用 `E_PLAYMODE_NOT_READY`/g) ?? []).length;
  assert.ok(
    (playmodeMatches?.length ?? 0) - playmodeAllowed <= 0,
    `skill must use E_RUNTIME_NOT_READY, not E_PLAYMODE_NOT_READY outside warning context`
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

    // Stage 7: JSON fields are now camelCase (nextActions, retryAfterMs).
    // Check for legacy snake-style field names that should be camelCase.
    if (content.includes(LEGACY_NEXT_ACTIONS_FIELD)) {
      violations.push(`${file}: uses stale "${LEGACY_NEXT_ACTIONS_FIELD}" (should be "nextActions")`);
    }
    if (content.includes(LEGACY_RETRY_AFTER_MS_FIELD)) {
      violations.push(`${file}: uses stale "${LEGACY_RETRY_AFTER_MS_FIELD}" (should be "retryAfterMs")`);
    }

    // E_PLAYMODE_NOT_READY → E_RUNTIME_NOT_READY
    if (content.includes("E_PLAYMODE_NOT_READY")) {
      const lines = content.split("\n");
      let staleCount = 0;
      for (const line of lines) {
        if (!line.includes("E_PLAYMODE_NOT_READY")) continue;
        const trimmed = line.trim().toLowerCase();
        if (trimmed.includes("不是") || trimmed.includes("不要") || trimmed.includes("do not") || trimmed.includes("instead of") || (trimmed.includes("使用 ") && trimmed.includes(" | "))) continue;
        staleCount++;
      }
      if (staleCount > 0) {
        violations.push(`${file}: uses "E_PLAYMODE_NOT_READY" instead of "E_RUNTIME_NOT_READY" (outside warning context)`);
      }
    }
  }

  assert.equal(
    violations.length,
    0,
    `Stale field names found:\n${violations.join("\n")}`
  );
});

test("docs distinguish between current and planned features", () => {
  // Verify that context and extensions commands are documented as available
  const readme = readDocFile("README.md");

  // Context commands should be documented as available
  assert.ok(
    readme.includes("context list") || readme.includes("context describe"),
    "README must document context commands as available"
  );

  // Extensions commands should be documented as available
  assert.ok(
    readme.includes("extensions list") || readme.includes("extensions install"),
    "README must document extensions commands as available"
  );
});
