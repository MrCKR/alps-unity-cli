import { createHash } from "node:crypto";
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { basename, dirname, isAbsolute, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const PLAN_STATUSES = new Set(["draft", "active", "paused", "blocked", "completed", "superseded"]);
const DIRECTION_STATUSES = new Set(["pending", "active", "completed", "blocked", "decision-required", "drift"]);
const FILE_STATUSES = new Set(["planned", "touched", "verified", "deleted", "excluded", "decision-required"]);
const EVIDENCE_STATUSES = new Set(["CURRENT", "STALE", "DIAGNOSTIC", "FAILED", "BLOCKED", "MISSING"]);
const FAILURE_CLASSES = new Set(["none", "PRODUCT_DEFECT", "HARNESS_DEFECT", "ENVIRONMENT_BLOCKED", "SPEC_GAP", "TEST_OVERREACH"]);
const DRIFT_RESULTS = new Set(["PASS", "DECISION_REQUIRED", "DRIFT", "BLOCKED"]);
const REQUIRED_FILES = ["plan.md", "context.md", "files.md", "decisions.md", "execution-log.md"];

// 从 Markdown 的固定键值行读取计划状态，避免依赖模糊标题推断。
function readField(source, field) {
  const match = source.match(new RegExp(`^(?:-\\s+)?${field}:\\s*(.+?)\\s*$`, "m"));
  return match ? normalizeValue(match[1]) : null;
}

// 读取文本；缺失文件由调用方统一报告，避免抛出不可执行的堆栈。
function readText(path) {
  return existsSync(path) ? readFileSync(path, "utf8") : "";
}

// 去除 Markdown code span，保证状态值与路径按实际内容比较。
function normalizeValue(value) {
  return value.trim().replace(/^`(.+)`$/, "$1");
}

// 读取指定二级标题下的 Markdown table，并返回 header 与数据行。
function readTable(source, heading) {
  const start = source.indexOf(heading);
  if (start < 0) return { header: [], rows: [] };
  const next = source.indexOf("\n## ", start + heading.length);
  const section = source.slice(start, next < 0 ? source.length : next);
  const rows = section.split(/\r?\n/)
    .filter((line) => line.startsWith("|") && line.endsWith("|"))
    .map((line) => line.slice(1, -1).split("|").map((cell) => cell.trim()))
    .filter((cells) => !cells.every((cell) => /^:?-+:?$/.test(cell)));
  return { header: rows[0] ?? [], rows: rows.slice(1) };
}

// 从单元格中提取 requirement ID；范围写法不允许，必须逐项列出。
function readRequirementIds(value) {
  return [...value.matchAll(/\b(?:PG|R)\d+\b/g)].map((match) => match[0]);
}

// 检查文件、方向和 evidence 引用的 requirement 均由 plan.md 定义。
function validateRequirementCell(value, owner, requirementIds, errors) {
  const ids = readRequirementIds(value);
  if (ids.length === 0) {
    errors.push(`${owner} does not reference a requirement ID`);
    return [];
  }
  for (const id of ids) {
    if (!requirementIds.has(id)) errors.push(`${owner} references undefined requirement: ${id}`);
  }
  return ids;
}

// 递归枚举 manifest 目录中的 raw 文件，保证声明集合与实际集合一致。
function listManifestFiles(root) {
  const files = [];
  for (const entry of readdirSync(root, { withFileTypes: true })) {
    const path = join(root, entry.name);
    if (entry.isDirectory()) files.push(...listManifestFiles(path));
    else if (entry.isFile()) files.push(path);
  }
  return files;
}

// CURRENT manifest 必须把同目录全部 raw evidence 的 hash 与 size 一并闭合。
function validateCurrentManifest(manifestPath, evidenceRoot, rowNumber, errors) {
  let manifest;
  try {
    manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
  } catch {
    errors.push(`CURRENT manifest is not valid JSON in row ${rowNumber}`);
    return;
  }
  if (!Array.isArray(manifest.files) || manifest.files.length === 0) {
    errors.push(`CURRENT manifest requires a non-empty files array in row ${rowNumber}`);
    return;
  }
  const manifestRoot = dirname(manifestPath);
  const declaredPaths = new Set();
  for (const [index, item] of manifest.files.entries()) {
    const childPath = typeof item?.path === "string" ? resolve(manifestRoot, item.path) : null;
    const childRelative = childPath ? relative(evidenceRoot, childPath) : "..";
    const manifestRelative = childPath ? relative(manifestRoot, childPath).replaceAll("\\", "/") : "..";
    if (!childPath || isAbsolute(childRelative) || /^\.\.(?:[\\/]|$)/.test(childRelative) || isAbsolute(manifestRelative) || /^\.\.(?:[\\/]|$)/.test(manifestRelative)) {
      errors.push(`CURRENT manifest child escapes its evidence directory in row ${rowNumber}, item ${index + 1}`);
      continue;
    }
    if (childPath === manifestPath) {
      errors.push(`CURRENT manifest cannot list itself in row ${rowNumber}, item ${index + 1}`);
      continue;
    }
    if (declaredPaths.has(manifestRelative)) {
      errors.push(`CURRENT manifest has duplicate child in row ${rowNumber}, item ${index + 1}`);
      continue;
    }
    declaredPaths.add(manifestRelative);
    if (!existsSync(childPath) || !statSync(childPath).isFile()) {
      errors.push(`CURRENT manifest child is missing in row ${rowNumber}, item ${index + 1}`);
      continue;
    }
    const actualHash = createHash("sha256").update(readFileSync(childPath)).digest("hex");
    const actualSize = statSync(childPath).size;
    if (item.sha256 !== actualHash) errors.push(`CURRENT manifest child SHA-256 mismatch in row ${rowNumber}, item ${index + 1}`);
    if (item.sizeBytes !== actualSize) errors.push(`CURRENT manifest child SizeBytes mismatch in row ${rowNumber}, item ${index + 1}`);
  }
  const actualPaths = listManifestFiles(manifestRoot)
    .filter((path) => path !== manifestPath)
    .map((path) => relative(manifestRoot, path).replaceAll("\\", "/"));
  for (const path of actualPaths) {
    if (!declaredPaths.has(path)) errors.push(`CURRENT manifest omits raw evidence in row ${rowNumber}: ${path}`);
  }
}

// 验证一个文件夹式计划包的结构、状态和跨文件治理不变量。
export function validatePlanPackage(planRoot) {
  const root = resolve(planRoot);
  const errors = [];
  const warnings = [];
  if (!existsSync(root) || !statSync(root).isDirectory()) {
    return { ok: false, planRoot: root, errors: [`Plan root is not a directory: ${root}`], warnings };
  }

  for (const file of REQUIRED_FILES) {
    if (!existsSync(join(root, file))) errors.push(`Missing required file: ${file}`);
  }
  const directionsRoot = join(root, "directions");
  const evidenceRoot = join(root, "evidence");
  if (!existsSync(directionsRoot) || !statSync(directionsRoot).isDirectory()) errors.push("Missing required directory: directions");
  if (!existsSync(evidenceRoot) || !statSync(evidenceRoot).isDirectory()) errors.push("Missing required directory: evidence");
  if (!existsSync(join(evidenceRoot, "index.md"))) errors.push("Missing required file: evidence/index.md");
  for (const directory of ["static", "live", "gates"]) {
    const path = join(evidenceRoot, directory);
    if (!existsSync(path) || !statSync(path).isDirectory()) errors.push(`Missing required directory: evidence/${directory}`);
  }

  const planSource = readText(join(root, "plan.md"));
  const planStatus = readField(planSource, "Status");
  const currentDirection = readField(planSource, "CurrentDirection");
  if (!planStatus || !PLAN_STATUSES.has(planStatus)) errors.push(`Invalid plan Status: ${planStatus ?? "missing"}`);
  if (!currentDirection) errors.push("Missing plan CurrentDirection");

  const requirementTable = readTable(planSource, "## 零、要求映射");
  const requirementIdIndex = requirementTable.header.indexOf("ID");
  const requirementEvidenceIndex = requirementTable.header.indexOf("完成证据");
  if (requirementIdIndex < 0 || requirementEvidenceIndex < 0 || requirementTable.rows.length === 0) {
    errors.push("plan.md requires a non-empty ## 零、要求映射 table with ID and 完成证据 columns");
  }
  const requirementIds = new Set();
  for (const row of requirementTable.rows) {
    const id = normalizeValue(row[requirementIdIndex] ?? "");
    const completionEvidence = normalizeValue(row[requirementEvidenceIndex] ?? "");
    if (!/^(?:PG|R)\d+$/.test(id)) errors.push(`Invalid requirement ID: ${id || "missing"}`);
    else if (requirementIds.has(id)) errors.push(`Duplicate requirement ID: ${id}`);
    else requirementIds.add(id);
    if (!completionEvidence) errors.push(`Requirement ${id || "missing"} has no completion evidence mapping`);
  }

  const directionFiles = existsSync(directionsRoot) && statSync(directionsRoot).isDirectory()
    ? readdirSync(directionsRoot).filter((file) => file.endsWith(".md")).sort()
    : [];
  if (directionFiles.length === 0) errors.push("At least one direction file is required");

  const directions = directionFiles.map((file) => {
    const source = readText(join(directionsRoot, file));
    const status = readField(source, "Status");
    const sourceRequirements = readField(source, "SourceRequirements") ?? "";
    const failureClass = readField(source, "Class");
    const exitResult = readField(source, "ExitResult");
    if (!status || !DIRECTION_STATUSES.has(status)) errors.push(`Invalid direction Status in ${file}: ${status ?? "missing"}`);
    validateRequirementCell(sourceRequirements, `Direction ${file}`, requirementIds, errors);
    if (!failureClass || !FAILURE_CLASSES.has(failureClass)) errors.push(`Invalid failure Class in ${file}: ${failureClass ?? "missing"}`);
    if (!exitResult || !DRIFT_RESULTS.has(exitResult)) errors.push(`Invalid ExitResult in ${file}: ${exitResult ?? "missing"}`);
    for (const token of ["ForbiddenDrift:", "## Outcomes", "## Exit Audit", "## Handoff"]) {
      if (!source.includes(token)) errors.push(`Direction ${file} is missing: ${token}`);
    }
    const outcomeCount = [...source.matchAll(/^- \[[ xX]\] O\d+\s+.+$/gm)].length;
    if (outcomeCount < 3 || outcomeCount > 6) errors.push(`Direction ${file} requires 3-6 outcomes; found ${outcomeCount}`);
    const doneEvidenceCount = [...source.matchAll(/^\s+- DoneEvidence:\s*.+$/gm)].length;
    const todoMappingCount = [...source.matchAll(/^\s+- Todos:\s*.+$/gm)].length;
    if (doneEvidenceCount !== outcomeCount) errors.push(`Direction ${file} requires one DoneEvidence per outcome; found ${doneEvidenceCount}/${outcomeCount}`);
    if (todoMappingCount !== outcomeCount) errors.push(`Direction ${file} requires one Todos mapping per outcome; found ${todoMappingCount}/${outcomeCount}`);
    return { file, status, outcomeCount, sourceRequirements, failureClass, exitResult };
  });

  const activeDirections = directions.filter((direction) => direction.status === "active");
  if (planStatus === "active") {
    if (activeDirections.length !== 1) errors.push(`Active plan requires exactly one active direction; found ${activeDirections.length}`);
    const expected = activeDirections.length === 1 ? `directions/${activeDirections[0].file}` : null;
    if (expected && currentDirection !== expected) errors.push(`CurrentDirection must be ${expected}; found ${currentDirection}`);
  } else if (activeDirections.length !== 0) {
    errors.push(`Plan status ${planStatus ?? "missing"} cannot have an active direction`);
  }
  if (planStatus !== "active" && currentDirection && currentDirection !== "none") {
    errors.push(`Plan status ${planStatus ?? "missing"} requires CurrentDirection: none`);
  }

  const contextSource = readText(join(root, "context.md"));
  for (const token of ["CapturedAt:", "PlanStatus:", "CurrentDirection:", "NextSafeAction:", "## Repository", "## Evidence State", "## Safety State"]) {
    if (!contextSource.includes(token)) errors.push(`context.md is missing: ${token}`);
  }
  const contextPlanStatus = readField(contextSource, "PlanStatus");
  const contextCurrentDirection = readField(contextSource, "CurrentDirection");
  if (contextPlanStatus !== planStatus) errors.push(`context.md PlanStatus must match plan.md: ${contextPlanStatus ?? "missing"}/${planStatus ?? "missing"}`);
  if (contextCurrentDirection !== currentDirection) errors.push(`context.md CurrentDirection must match plan.md: ${contextCurrentDirection ?? "missing"}/${currentDirection ?? "missing"}`);

  const filesSource = readText(join(root, "files.md"));
  for (const token of ["## 文件清单", "## 禁止触碰", "## 删除 successor mapping"]) {
    if (!filesSource.includes(token)) errors.push(`files.md is missing: ${token}`);
  }
  const fileTable = readTable(filesSource, "## 文件清单");
  const fileRequirementIndex = fileTable.header.indexOf("Requirement");
  const fileStatusIndex = fileTable.header.indexOf("Status");
  if (fileRequirementIndex < 0 || fileStatusIndex < 0 || fileTable.rows.length === 0) errors.push("files.md requires Requirement and Status columns with at least one row");
  const fileRequirementCoverage = new Set();
  for (const [index, row] of fileTable.rows.entries()) {
    for (const id of validateRequirementCell(row[fileRequirementIndex] ?? "", `files.md row ${index + 1}`, requirementIds, errors)) fileRequirementCoverage.add(id);
    const status = normalizeValue(row[fileStatusIndex] ?? "");
    if (!FILE_STATUSES.has(status)) errors.push(`Invalid files.md Status in row ${index + 1}: ${status || "missing"}`);
  }

  const decisionsSource = readText(join(root, "decisions.md"));
  if (!decisionsSource.includes("# Owner Decisions")) errors.push("decisions.md is missing its canonical title");

  const logSource = readText(join(root, "execution-log.md"));
  if (!/^##\s+.+\|\s*.+\|\s*.+$/m.test(logSource)) errors.push("execution-log.md requires at least one engineering event");
  for (const match of logSource.matchAll(/^- FailureClass:\s*(.+?)\s*$/gm)) {
    const value = normalizeValue(match[1]);
    if (!FAILURE_CLASSES.has(value)) errors.push(`Invalid execution-log FailureClass: ${value}`);
  }
  for (const match of logSource.matchAll(/^- DriftResult:\s*(.+?)\s*$/gm)) {
    const value = normalizeValue(match[1]);
    if (!DRIFT_RESULTS.has(value)) errors.push(`Invalid execution-log DriftResult: ${value}`);
  }

  const evidenceSource = readText(join(evidenceRoot, "index.md"));
  if (!evidenceSource.includes("## 证据索引")) errors.push("evidence/index.md is missing: ## 证据索引");
  const evidenceTable = readTable(evidenceSource, "## 证据索引");
  const evidencePathIndex = evidenceTable.header.indexOf("Evidence");
  const evidenceSourceStampIndex = evidenceTable.header.indexOf("Source Stamp");
  const evidenceHashIndex = evidenceTable.header.indexOf("SHA-256");
  const evidenceSizeIndex = evidenceTable.header.indexOf("SizeBytes");
  const evidenceStatusIndex = evidenceTable.header.indexOf("Status");
  const evidenceRequirementIndex = evidenceTable.header.indexOf("Requirements");
  if ([evidencePathIndex, evidenceSourceStampIndex, evidenceHashIndex, evidenceSizeIndex, evidenceStatusIndex, evidenceRequirementIndex].some((index) => index < 0)) {
    errors.push("evidence/index.md requires Evidence, Source Stamp, SHA-256, SizeBytes, Status and Requirements columns");
  }
  const evidenceRequirementCoverage = new Set();
  for (const [index, row] of evidenceTable.rows.entries()) {
    const rowNumber = index + 1;
    const status = normalizeValue(row[evidenceStatusIndex] ?? "");
    if (!EVIDENCE_STATUSES.has(status)) errors.push(`Invalid evidence Status in row ${rowNumber}: ${status || "missing"}`);
    for (const id of validateRequirementCell(row[evidenceRequirementIndex] ?? "", `evidence row ${rowNumber}`, requirementIds, errors)) evidenceRequirementCoverage.add(id);
    const relativePath = normalizeValue(row[evidencePathIndex] ?? "");
    const evidencePath = relativePath ? resolve(evidenceRoot, relativePath) : null;
    if (status !== "MISSING" && evidencePath && !existsSync(evidencePath)) {
      errors.push(`Evidence path does not exist in row ${rowNumber}: ${relativePath}`);
    }
    if (status === "CURRENT") {
      const sourceStamp = normalizeValue(row[evidenceSourceStampIndex] ?? "");
      const hash = normalizeValue(row[evidenceHashIndex] ?? "");
      const size = normalizeValue(row[evidenceSizeIndex] ?? "");
      const evidenceRelative = evidencePath ? relative(evidenceRoot, evidencePath) : "..";
      if (!/^[a-z][a-z0-9-]*-sha256:[0-9a-f]{64}$/.test(sourceStamp)) errors.push(`CURRENT evidence requires an algorithm-qualified Source Stamp in row ${rowNumber}`);
      if (!/^sha256:[0-9a-f]{64}$/.test(hash)) errors.push(`CURRENT evidence requires SHA-256 in row ${rowNumber}`);
      if (!/^[1-9]\d*$/.test(size)) errors.push(`CURRENT evidence requires positive SizeBytes in row ${rowNumber}`);
      if (!evidencePath || isAbsolute(evidenceRelative) || /^\.\.(?:[\\/]|$)/.test(evidenceRelative)) {
        errors.push(`CURRENT evidence must stay inside the plan evidence root in row ${rowNumber}`);
      } else if (existsSync(evidencePath)) {
        const stat = statSync(evidencePath);
        if (!stat.isFile()) errors.push(`CURRENT evidence must be a file in row ${rowNumber}`);
        else {
          const actualHash = `sha256:${createHash("sha256").update(readFileSync(evidencePath)).digest("hex")}`;
          if (hash !== actualHash) errors.push(`CURRENT evidence SHA-256 mismatch in row ${rowNumber}`);
          if (size !== String(stat.size)) errors.push(`CURRENT evidence SizeBytes mismatch in row ${rowNumber}`);
          if (basename(evidencePath) === "manifest.json") validateCurrentManifest(evidencePath, evidenceRoot, rowNumber, errors);
        }
      }
    }
  }

  for (const id of requirementIds) {
    if (!fileRequirementCoverage.has(id)) errors.push(`Requirement ${id} has no files.md coverage`);
    if (!evidenceRequirementCoverage.has(id)) errors.push(`Requirement ${id} has no evidence/index.md coverage`);
  }

  if (/^\d{4}[-_]?\d{2}[-_]?\d{2}/.test(basename(root))) warnings.push("Plan folder starts with a date; prefer a stable short name");
  return {
    ok: errors.length === 0,
    planRoot: root,
    planStatus,
    currentDirection,
    requirementIds: [...requirementIds],
    directions,
    activeDirections: activeDirections.map((item) => item.file),
    errors,
    warnings,
  };
}

// CLI 入口只打印稳定 JSON，供 Agent、测试和 CI 直接判断。
function main(argv) {
  if (argv.length !== 1) {
    console.error("Usage: node validate-plan-package.mjs <plan-root>");
    process.exitCode = 2;
    return;
  }
  const result = validatePlanPackage(argv[0]);
  console.log(JSON.stringify(result, null, 2));
  if (!result.ok) process.exitCode = 1;
}

if (process.argv[1] && resolve(process.argv[1]) === resolve(fileURLToPath(import.meta.url))) main(process.argv.slice(2));
