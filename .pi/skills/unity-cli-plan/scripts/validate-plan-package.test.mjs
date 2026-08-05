import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { validatePlanPackage } from "./validate-plan-package.mjs";

const SOURCE_STAMP = `test-tree-sha256:${"a".repeat(64)}`;

// 创建最小合法 direction，测试只替换与断言相关的字段。
function createDirection(status = "active", requirement = "R1") {
  return `# Scope\n\nStatus: ${status}\nDirectionId: DIR-001\nSourceRequirements: ${requirement}\nForbiddenDrift: public contract\n\n## Outcomes\n- [ ] O1 scoped\n  - DoneEvidence: evidence/static/o1.json\n  - Todos: #1\n- [ ] O2 bounded\n  - DoneEvidence: evidence/static/o2.json\n  - Todos: #2\n- [ ] O3 verified\n  - DoneEvidence: evidence/static/o3.json\n  - Todos: #3\n\n## Failure Classification\n\n- CurrentFailure: none\n- Class: none\n- Evidence: none\n\n## Exit Audit\n\nExitResult: BLOCKED\n\n## Handoff\n`;
}

// 创建最小合法计划包，覆盖结构、要求映射和跨文件状态一致性。
function createPlanPackage(planStatus = "active", directionStatus = "active") {
  const root = mkdtempSync(join(tmpdir(), "unity-cli-plan-"));
  mkdirSync(join(root, "directions"), { recursive: true });
  for (const directory of ["static", "live", "gates"]) mkdirSync(join(root, "evidence", directory), { recursive: true });
  const current = planStatus === "active" ? "directions/001-scope.md" : "none";
  writeFileSync(join(root, "plan.md"), `# Plan\n\nStatus: ${planStatus}\nCurrentDirection: ${current}\n\n## 零、要求映射\n\n| ID | 来源 | 要求 | 完成证据 |\n|---|---|---|---|\n| R1 | user | scoped | pending |\n`, "utf8");
  writeFileSync(join(root, "context.md"), `# Current Context\n\nCapturedAt: 2026-01-01T00:00:00Z\nPlanStatus: ${planStatus}\nCurrentDirection: ${current}\nNextSafeAction: inspect\n\n## Repository\n## Evidence State\n## Safety State\n`, "utf8");
  writeFileSync(join(root, "files.md"), "# File Manifest\n\n## 文件清单\n\n| Path / Glob | Category | Requirement | Allowed Change | Sync Owner | Ownership | Status |\n|---|---|---|---|---|---|---|\n| src/example.ts | 产品文件 | R1 | scoped | test | task | planned |\n\n## 禁止触碰\n\n## 删除 successor mapping\n", "utf8");
  writeFileSync(join(root, "decisions.md"), "# Owner Decisions\n", "utf8");
  writeFileSync(join(root, "execution-log.md"), "# Execution Log\n\n## 2026-01-01T00:00:00Z | plan | plan-created\n\n- FailureClass: none\n- DriftResult: PASS\n", "utf8");
  writeFileSync(join(root, "directions", "001-scope.md"), createDirection(directionStatus), "utf8");
  writeFileSync(join(root, "evidence", "index.md"), "# Evidence Index\n\n## 证据索引\n\n| Evidence | Kind | Source Stamp | SHA-256 | SizeBytes | Status | Requirements | Notes |\n|---|---|---|---|---:|---|---|---|\n| static/r1.json | static | none | none | 0 | MISSING | R1 | pending |\n", "utf8");
  return root;
}

test("accepts an active package with consistent cross-file governance", () => {
  const root = createPlanPackage();
  try { assert.equal(validatePlanPackage(root).ok, true); }
  finally { rmSync(root, { recursive: true, force: true }); }
});

test("rejects missing required package files", () => {
  const root = createPlanPackage();
  try {
    rmSync(join(root, "files.md"));
    const result = validatePlanPackage(root);
    assert.equal(result.ok, false);
    assert.ok(result.errors.includes("Missing required file: files.md"));
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("rejects two active directions", () => {
  const root = createPlanPackage();
  try {
    writeFileSync(join(root, "directions", "002-extra.md"), createDirection("active"), "utf8");
    const result = validatePlanPackage(root);
    assert.equal(result.ok, false);
    assert.ok(result.errors.some((error) => error.includes("exactly one active direction")));
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("rejects an active direction under a paused plan", () => {
  const root = createPlanPackage("paused", "active");
  try {
    const result = validatePlanPackage(root);
    assert.equal(result.ok, false);
    assert.ok(result.errors.some((error) => error.includes("cannot have an active direction")));
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("rejects missing canonical evidence directories", () => {
  const root = createPlanPackage();
  try {
    rmSync(join(root, "evidence", "live"), { recursive: true });
    const result = validatePlanPackage(root);
    assert.equal(result.ok, false);
    assert.ok(result.errors.includes("Missing required directory: evidence/live"));
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("rejects a direction outside the 3-6 outcome budget", () => {
  const root = createPlanPackage();
  try {
    const source = createDirection().replace(/- \[ \] O3 verified[\s\S]*?- Todos: #3\n/, "");
    writeFileSync(join(root, "directions", "001-scope.md"), source, "utf8");
    const result = validatePlanPackage(root);
    assert.equal(result.ok, false);
    assert.ok(result.errors.some((error) => error.includes("requires 3-6 outcomes")));
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("rejects context state that disagrees with plan.md", () => {
  const root = createPlanPackage();
  try {
    const path = join(root, "context.md");
    writeFileSync(path, readText(path).replace("PlanStatus: active", "PlanStatus: paused"), "utf8");
    const result = validatePlanPackage(root);
    assert.equal(result.ok, false);
    assert.ok(result.errors.some((error) => error.includes("PlanStatus must match")));
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("rejects undefined direction requirements", () => {
  const root = createPlanPackage();
  try {
    writeFileSync(join(root, "directions", "001-scope.md"), createDirection("active", "R2"), "utf8");
    const result = validatePlanPackage(root);
    assert.equal(result.ok, false);
    assert.ok(result.errors.includes("Direction 001-scope.md references undefined requirement: R2"));
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("rejects invalid file ownership status", () => {
  const root = createPlanPackage();
  try {
    const path = join(root, "files.md");
    writeFileSync(path, readText(path).replace("| planned |", "| unknown |"), "utf8");
    const result = validatePlanPackage(root);
    assert.equal(result.ok, false);
    assert.ok(result.errors.some((error) => error.includes("Invalid files.md Status")));
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("rejects invalid evidence status", () => {
  const root = createPlanPackage();
  try {
    const path = join(root, "evidence", "index.md");
    writeFileSync(path, `${readText(path)}| missing.json | static | none | none | 0 | UNKNOWN | R1 | test |\n`, "utf8");
    const result = validatePlanPackage(root);
    assert.equal(result.ok, false);
    assert.ok(result.errors.some((error) => error.includes("Invalid evidence Status")));
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("rejects CURRENT evidence without hash and size", () => {
  const root = createPlanPackage();
  try {
    const path = join(root, "evidence", "index.md");
    writeFileSync(path, `${readText(path)}| index.md | static | stamp | none | 0 | CURRENT | R1 | test |\n`, "utf8");
    const result = validatePlanPackage(root);
    assert.equal(result.ok, false);
    assert.ok(result.errors.some((error) => error.includes("CURRENT evidence requires SHA-256")));
    assert.ok(result.errors.some((error) => error.includes("CURRENT evidence requires positive SizeBytes")));
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("rejects CURRENT evidence whose recorded hash and size do not match", () => {
  const root = createPlanPackage();
  try {
    const path = join(root, "evidence", "index.md");
    const zeroHash = `sha256:${"0".repeat(64)}`;
    writeFileSync(path, `${readText(path)}| index.md | static | ${SOURCE_STAMP} | ${zeroHash} | 1 | CURRENT | R1 | test |\n`, "utf8");
    const result = validatePlanPackage(root);
    assert.equal(result.ok, false);
    assert.ok(result.errors.some((error) => error.includes("SHA-256 mismatch")));
    assert.ok(result.errors.some((error) => error.includes("SizeBytes mismatch")));
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("rejects CURRENT evidence without an algorithm-qualified source stamp", () => {
  const root = createPlanPackage();
  try {
    const artifact = join(root, "evidence", "static", "current.json");
    writeFileSync(artifact, "{}", "utf8");
    const path = join(root, "evidence", "index.md");
    writeFileSync(path, `${readText(path)}| static/current.json | static | none | sha256:${hashFile(artifact)} | 2 | CURRENT | R1 | test |\n`, "utf8");
    const result = validatePlanPackage(root);
    assert.equal(result.ok, false);
    assert.ok(result.errors.some((error) => error.includes("algorithm-qualified Source Stamp")));
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("rejects CURRENT evidence outside the plan evidence root", () => {
  const root = createPlanPackage();
  try {
    const artifact = join(root, "outside.json");
    writeFileSync(artifact, "{}", "utf8");
    const path = join(root, "evidence", "index.md");
    writeFileSync(path, `${readText(path)}| ../outside.json | static | ${SOURCE_STAMP} | sha256:${hashFile(artifact)} | 2 | CURRENT | R1 | test |\n`, "utf8");
    const result = validatePlanPackage(root);
    assert.equal(result.ok, false);
    assert.ok(result.errors.some((error) => error.includes("must stay inside")));
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("rejects a CURRENT manifest with a tampered raw response", () => {
  const root = createPlanPackage();
  try {
    const raw = join(root, "evidence", "static", "raw.md");
    const manifest = join(root, "evidence", "static", "manifest.json");
    writeFileSync(raw, "raw", "utf8");
    writeFileSync(manifest, JSON.stringify({ files: [{ path: "raw.md", sizeBytes: 3, sha256: "0".repeat(64) }] }), "utf8");
    const path = join(root, "evidence", "index.md");
    writeFileSync(path, `${readText(path)}| static/manifest.json | manifest | ${SOURCE_STAMP} | sha256:${hashFile(manifest)} | ${readFileSync(manifest).length} | CURRENT | R1 | test |\n`, "utf8");
    const result = validatePlanPackage(root);
    assert.equal(result.ok, false);
    assert.ok(result.errors.some((error) => error.includes("manifest child SHA-256 mismatch")));
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("rejects a CURRENT manifest that omits a raw response", () => {
  const root = createPlanPackage();
  try {
    const raw = join(root, "evidence", "static", "raw.md");
    const omitted = join(root, "evidence", "static", "omitted.md");
    const manifest = join(root, "evidence", "static", "manifest.json");
    writeFileSync(raw, "raw", "utf8");
    writeFileSync(omitted, "omitted", "utf8");
    writeFileSync(manifest, JSON.stringify({ files: [{ path: "raw.md", sizeBytes: 3, sha256: hashFile(raw) }] }), "utf8");
    const path = join(root, "evidence", "index.md");
    writeFileSync(path, `${readText(path)}| static/manifest.json | manifest | ${SOURCE_STAMP} | sha256:${hashFile(manifest)} | ${readFileSync(manifest).length} | CURRENT | R1 | test |\n`, "utf8");
    const result = validatePlanPackage(root);
    assert.equal(result.ok, false);
    assert.ok(result.errors.some((error) => error.includes("manifest omits raw evidence")));
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test("rejects a requirement without files and evidence coverage", () => {
  const root = createPlanPackage();
  try {
    const path = join(root, "plan.md");
    writeFileSync(path, readText(path).replace("| R1 | user | scoped | pending |\n", "| R1 | user | scoped | pending |\n| R2 | user | second | pending |\n"), "utf8");
    const result = validatePlanPackage(root);
    assert.equal(result.ok, false);
    assert.ok(result.errors.includes("Requirement R2 has no files.md coverage"));
    assert.ok(result.errors.includes("Requirement R2 has no evidence/index.md coverage"));
  } finally { rmSync(root, { recursive: true, force: true }); }
});

// 计算临时 evidence 文件的 SHA-256。
function hashFile(path) {
  return createHash("sha256").update(readFileSync(path)).digest("hex");
}

// 测试 helper 只读取临时 UTF-8 文件。
function readText(path) {
  return readFileSync(path, "utf8");
}
