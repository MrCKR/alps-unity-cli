import { readdir, readFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDir = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(scriptDir, "../../../..");
const issuesRoot = resolve(repositoryRoot, ".issues");
const states = ["in-progress", "completed"];
const requiredSections = [
  "Problem",
  "Direct Evidence",
  "Root Cause",
  "Fix Method",
  "Target Package Status",
  "Product Source Status",
  "Validation",
  "Files",
  "Blockers",
  "Next Action",
];
const targetStatuses = new Set(["not-modified", "modified-awaiting-validation", "partially-verified", "verified", "failed", "blocked", "not-applicable"]);
const productStatuses = new Set(["not-synced", "modified-awaiting-validation", "verified", "failed", "blocked"]);
const failures = [];
const records = [];
const seenIds = new Set();
const seenNumbers = new Set();

for (const state of states) {
  const directory = resolve(issuesRoot, state);
  let entries;
  try {
    entries = await readdir(directory, { withFileTypes: true });
  } catch (error) {
    failures.push(`Missing issue state directory: ${directory}`);
    continue;
  }

  for (const entry of entries) {
    if (!entry.isFile() || entry.name === ".gitkeep") continue;
    if (!/^\d{3}-[a-z0-9]+(?:-[a-z0-9]+)*\.md$/.test(entry.name)) {
      failures.push(`${state}/${entry.name}: filename must be <three-digits>-<english-slug>.md`);
      continue;
    }

    const number = entry.name.slice(0, 3);
    const path = resolve(directory, entry.name);
    const text = await readFile(path, "utf8");
    const issueId = field(text, "IssueId");
    const status = field(text, "Status");
    const targetStatus = field(text, "TargetPackageStatus");
    const productStatus = field(text, "ProductSourceStatus");

    if (seenNumbers.has(number)) failures.push(`${state}/${entry.name}: duplicate issue number ${number}`);
    seenNumbers.add(number);
    if (issueId !== `ISSUE-${number}`) failures.push(`${state}/${entry.name}: IssueId must be ISSUE-${number}`);
    if (seenIds.has(issueId)) failures.push(`${state}/${entry.name}: duplicate IssueId ${issueId}`);
    seenIds.add(issueId);

    const expectedStatus = state === "completed" ? "completed" : "in-progress";
    if (status !== expectedStatus) failures.push(`${state}/${entry.name}: Status must be ${expectedStatus}`);
    if (!targetStatuses.has(targetStatus)) failures.push(`${state}/${entry.name}: invalid TargetPackageStatus ${targetStatus || "<missing>"}`);
    if (!productStatuses.has(productStatus)) failures.push(`${state}/${entry.name}: invalid ProductSourceStatus ${productStatus || "<missing>"}`);
    for (const section of requiredSections) {
      if (!text.includes(`\n## ${section}\n`)) failures.push(`${state}/${entry.name}: missing section ## ${section}`);
    }

    if (state === "completed") {
      if (targetStatus !== "verified" && targetStatus !== "not-applicable") failures.push(`${state}/${entry.name}: completed issue requires TargetPackageStatus: verified or not-applicable`);
      if (productStatus !== "verified") failures.push(`${state}/${entry.name}: completed issue requires ProductSourceStatus: verified`);
      if (!section(text, "Blockers").includes("- none")) failures.push(`${state}/${entry.name}: completed issue requires '- none' in Blockers`);
      if (!section(text, "Next Action").trim().startsWith("none")) failures.push(`${state}/${entry.name}: completed issue requires Next Action to be none`);
    }

    records.push({ issueId, state, file: `${state}/${entry.name}`, targetStatus, productStatus });
  }
}

records.sort((left, right) => left.issueId.localeCompare(right.issueId));
const result = { ok: failures.length === 0, issueCount: records.length, records, failures };
process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
if (failures.length > 0) process.exitCode = 1;

// 读取顶层单值字段，拒绝把章节内自然语言误当状态。
function field(text, name) {
  const match = text.match(new RegExp(`^${name}:\\s*(.+?)\\s*$`, "m"));
  return match?.[1]?.trim() ?? "";
}

// 提取章节正文，供完成门槛做确定性检查。
function section(text, name) {
  const marker = `\n## ${name}\n`;
  const start = text.indexOf(marker);
  if (start < 0) return "";
  const bodyStart = start + marker.length;
  const next = text.indexOf("\n## ", bodyStart);
  return text.slice(bodyStart, next < 0 ? text.length : next);
}
