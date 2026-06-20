/**
 * Stale wording tests for documentation and skill files.
 *
 * Ensures docs/skill/examples reflect actual CLI commands and do not
 * reference non-existent commands or outdated terminology.
 *
 * ALP-505 P9-T2: Chinese docs & CLI Skill rewrite
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

// These commands do NOT exist in the current CLI implementation.
// They may appear in deprecation/removal context but must not be taught as available.
const NONEXISTENT_COMMANDS = [
  "tools list",              // removed; use "tools group" + "tools group list <group>"
  "tools groups",            // removed; use "tools group" (singular)
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

// Must use snake_case, not camelCase
const STALE_CAMEL_CASE = [
  "nextActions",            // must be next_actions
  "retryAfterMs",           // must be retry_after_ms
  "readOnlyhint",           // must be readOnlyHint (PascalCase for descriptor field)
];

// Must use correct error code names
const STALE_ERROR_CODES = [
  "E_PLAYMODE_NOT_READY",   // must be E_RUNTIME_NOT_READY
];

// ── Tests ──────────────────────────────────────────────────

test("docs do not teach non-existent commands as available", () => {
  const files = allDocFiles();
  const violations: string[] = [];

  for (const file of files) {
    let content: string;
    try {
      content = readDocFile(file);
    } catch {
      continue; // skip missing files
    }

    for (const cmd of NONEXISTENT_COMMANDS) {
      // Allow mentions in "规划中/planned/规划中（P3）/规划中（P5）" context
      // and in explicit deprecation/removal context (废弃/移除/deprecated/removed)
      // But NOT in code blocks without a planned or deprecation marker nearby
      const lines = content.split("\n");
      for (let i = 0; i < lines.length; i++) {
        const line = lines[i];
        if (!line.includes(cmd)) continue;

        // Skip if line or nearby context mentions "规划中/planned/规划/P3/P5"
        // or deprecation markers (废弃/移除/deprecated/removed/替代路径/已移除)
        const contextWindow = lines.slice(Math.max(0, i - 3), i + 4).join(" ");
        if (
          contextWindow.includes("规划中") ||
          contextWindow.includes("planned") ||
          contextWindow.includes("规划") ||
          contextWindow.includes("P3") ||
          contextWindow.includes("P5") ||
          contextWindow.includes("待实现") ||
          contextWindow.includes("实现后生效") ||
          contextWindow.includes("废弃") ||
          contextWindow.includes("移除") ||
          contextWindow.includes("removed") ||
          contextWindow.includes("deprecated") ||
          contextWindow.includes("替代路径") ||
          contextWindow.includes("已移除") ||
          contextWindow.includes("已废弃") ||
          contextWindow.includes("不得") ||
          contextWindow.includes("不要") ||
          contextWindow.includes("不要做") ||
          contextWindow.includes("未实现") ||
          contextWindow.includes("尚未实现")
        ) {
          continue; // allowed: it's clearly marked as planned or deprecated
        }

        violations.push(`${file}:${i + 1}: "${cmd}" appears without planned or deprecation marker`);
      }
    }
  }

  assert.equal(
    violations.length,
    0,
    `Non-existent commands found without planned markers:\n${violations.join("\n")}`
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
  const skillPath = fileURLToPath(new URL("../skills/unity_cli/SKILL.md", import.meta.url));
  const skill = readFileSync(skillPath, "utf8");

  // --input-json is not a CLI parameter; the CLI uses --input
  const inputJsonMatches = skill.match(/--input-json/g);
  const inputJsonAllowed = (skill.match(/Do not.*--input-json|不要.*--input-json|不得.*--input-json|用 `--input-json`/g) ?? []).length;
  assert.ok(
    (inputJsonMatches?.length ?? 0) - inputJsonAllowed <= 0,
    `skill must not teach --input-json (found ${(inputJsonMatches?.length ?? 0)} occurrences beyond the warning)`
  );

  // JSON field next_actions, not nextActions
  // Allow in "使用 ... |" context (warning table)
  const nextActionsMatches = skill.match(/nextActions/g);
  const nextActionsAllowed = (skill.match(/使用 `nextActions`/g) ?? []).length;
  assert.ok(
    (nextActionsMatches?.length ?? 0) - nextActionsAllowed <= 0,
    `skill must use next_actions (snake_case), not nextActions (camelCase) outside warning context`
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

test("docs use snake_case for error response fields", () => {
  const files = allDocFiles();
  const violations: string[] = [];

  for (const file of files) {
    let content: string;
    try {
      content = readDocFile(file);
    } catch {
      continue;
    }

    // Check for camelCase field names that should be snake_case
    // Allow in "不是/不要/Do not" context (warning against them)
    // nextActions → next_actions (in error response context)
    if (content.includes("nextActions")) {
      // Count occurrences that are NOT in a "warning/不是" context
      const lines = content.split("\n");
      let staleCount = 0;
      for (const line of lines) {
        if (!line.includes("nextActions")) continue;
        const trimmed = line.trim().toLowerCase();
        if (trimmed.includes("不是") || trimmed.includes("不要") || trimmed.includes("do not") || trimmed.includes("instead of") || (trimmed.includes("使用 ") && trimmed.includes(" | "))) continue;
        staleCount++;
      }
      if (staleCount > 0) {
        violations.push(`${file}: uses "nextActions" instead of "next_actions" (outside warning context)`);
      }
    }
    if (content.includes("retryAfterMs")) {
      const lines = content.split("\n");
      let staleCount = 0;
      for (const line of lines) {
        if (!line.includes("retryAfterMs")) continue;
        const trimmed = line.trim().toLowerCase();
        if (trimmed.includes("不是") || trimmed.includes("不要") || trimmed.includes("do not") || trimmed.includes("instead of") || (trimmed.includes("使用 ") && trimmed.includes(" | "))) continue;
        staleCount++;
      }
      if (staleCount > 0) {
        violations.push(`${file}: uses "retryAfterMs" instead of "retry_after_ms" (outside warning context)`);
      }
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

test("docs use actual command names (tools group not tools groups)", () => {
  const files = allDocFiles();
  const violations: string[] = [];

  for (const file of files) {
    let content: string;
    try {
      content = readDocFile(file);
    } catch {
      continue;
    }

    // "tools groups" (plural) is a removed legacy alias — the actual command is "tools group" (singular)
    // "tools list" is a removed command — use "tools group" + "tools group list <group>"
    // Allow in "规划中/planned/deprecated/废弃/移除" context
    const lines = content.split("\n");
    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];
      // Check for removed commands used as current guidance
      if (/tools groups(?![\w.-])/i.test(line)) {
        const contextWindow = lines.slice(Math.max(0, i - 3), i + 4).join(" ");
        if (
          contextWindow.includes("规划中") ||
          contextWindow.includes("planned") ||
          contextWindow.includes("P4") ||
          contextWindow.includes("P9-T1") ||
          contextWindow.includes("方案") ||
          contextWindow.includes("计划") ||
          contextWindow.includes("废弃") ||
          contextWindow.includes("移除") ||
          contextWindow.includes("removed") ||
          contextWindow.includes("deprecated") ||
          contextWindow.includes("已移除") ||
          contextWindow.includes("替代路径")
        ) {
          continue;
        }
        violations.push(`${file}:${i + 1}: uses "tools groups" (removed) — actual command is "tools group" (singular)`);
      }
    }
  }

  assert.equal(
    violations.length,
    0,
    `Removed commands found without deprecation markers:\n${violations.join("\n")}`
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
