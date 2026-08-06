import assert from "node:assert/strict";
import { access, mkdtemp, readFile, readdir, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import test from "node:test";
import { AgentSkillInstallerRegistry } from "../src/cli-commands/skill-installer.js";

const skillRoot = resolve("skills", "unity_cli");

/** 递归列出相对文件路径，统一使用正斜杠。 */
async function listFiles(root: string, directory = ""): Promise<string[]> {
  const absolute = join(root, directory);
  const files: string[] = [];
  for (const entry of await readdir(absolute, { withFileTypes: true })) {
    const relative = directory ? join(directory, entry.name) : entry.name;
    if (entry.isDirectory()) files.push(...await listFiles(root, relative));
    else if (entry.isFile()) files.push(relative.split("\\").join("/"));
  }
  return files.sort();
}

test("governed manifest declares ownership, runtime projection, contracts, and missing evidence", async () => {
  const manifest = JSON.parse(await readFile(join(skillRoot, "manifest.json"), "utf8")) as Record<string, any>;
  const packageJson = JSON.parse(await readFile(resolve("package.json"), "utf8")) as { version: string };
  assert.equal(manifest.name, "unity_cli");
  assert.equal(manifest.version, packageJson.version);
  assert.ok(manifest.owner);
  assert.equal(manifest.review_cadence, "per-release");
  assert.equal(manifest.maturity_tier, "governed");
  assert.equal(manifest.lifecycle_stage, "governed");
  assert.deepEqual(manifest.runtime_projection.include, ["SKILL.md", "references/**", "scripts/**"]);
  assert.ok(manifest.runtime_projection.exclude.includes("manifest.json"));
  assert.ok(manifest.input_files["file-backed fixture"].length >= 3);
  assert.ok(manifest.output_contract.feedback_summary_condition);
  assert.ok(manifest.rollback_boundary.skill_install);
  assert.equal(manifest.permissions.network, "deny");
  assert.ok(Object.values(manifest.evidence_status).filter((value) => value === "missing evidence").length >= 4);
});

test("agent interface is broad-triggered, PowerShell-local, and network-denied", async () => {
  const text = await readFile(join(skillRoot, "agents", "interface.yaml"), "utf8");
  assert.match(text, /mode: "automatic"/);
  assert.match(text, /shell: "powershell"/);
  assert.match(text, /source_tier: "local"/);
  assert.match(text, /remote_inline_execution: "forbid"/);
  assert.match(text, /network: "deny"/);
  for (const target of ["openai", "claude", "generic"]) assert.match(text, new RegExp(`- "${target}"`));
});

test("file-backed output evals and governed reports are internally reachable", async () => {
  const casesPath = join(skillRoot, "evals", "output", "cases.jsonl");
  const cases = (await readFile(casesPath, "utf8")).split(/\r?\n/).filter(Boolean).map((line) => JSON.parse(line) as Record<string, any>);
  assert.equal(cases.length, 5);
  const fileBacked = cases.filter((item) => Array.isArray(item.input_files) && item.input_files.length > 0);
  assert.ok(fileBacked.length >= 2);
  for (const item of fileBacked) {
    for (const input of item.input_files as string[]) await access(join(dirname(casesPath), input));
  }
  const trust = await readFile(join(skillRoot, "reports", "trust-report.md"), "utf8");
  const scorecard = await readFile(join(skillRoot, "reports", "output_quality_scorecard.md"), "utf8");
  assert.match(trust, /trust report/i);
  assert.match(trust, /rollback boundary/i);
  assert.match(trust, /missing evidence/i);
  assert.match(scorecard, /output contract/i);
  assert.match(scorecard, /file-backed fixture/i);
  assert.match(scorecard, /missing evidence/i);
});

test("product installer projects only the six runtime files", async () => {
  const project = await mkdtemp(join(tmpdir(), "unity-cli-governed-install-"));
  const sourcePath = join(skillRoot, "SKILL.md");
  try {
    const registry = new AgentSkillInstallerRegistry();
    await registry.installForAgents("pi", sourcePath, project);
    const target = join(project, ".pi", "skills", "unity_cli");
    assert.deepEqual(await listFiles(target), [
      "SKILL.md",
      "references/custom-contexts.md",
      "references/custom-tools.md",
      "references/feedback.md",
      "references/operation-protocol.md",
      "scripts/record-feedback.ps1",
    ]);
    await assert.rejects(access(join(target, "manifest.json")));
    await assert.rejects(access(join(target, "reports")));
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});
