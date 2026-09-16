import assert from "node:assert/strict";
import { access, mkdtemp, readFile, readdir, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import test from "node:test";
import { AgentSkillInstallerRegistry } from "../src/cli-commands/skill-installer.js";

const skillRoot = resolve("skills", "unitycli");

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
  const skill = await readFile(join(skillRoot, "SKILL.md"), "utf8");
  assert.match(skill, /^---\r?\nname: unitycli\r?$/m);
  assert.equal(manifest.name, "unitycli");
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

test("public runtime Skill routes operations without embedding the setup workflow", async () => {
  const skill = await readFile(join(skillRoot, "SKILL.md"), "utf8");
  const operation = await readFile(join(skillRoot, "references", "operation-protocol.md"), "utf8");

  assert.match(skill, /运行时导航入口，不是固定工具手册/);
  assert.doesNotMatch(skill, /^## 安装协议$/m);
  for (const setupDetail of ["init discover", "init package --close-editor", "init skill --target"]) {
    assert.doesNotMatch(skill, new RegExp(setupDetail.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  }

  assert.match(skill, /目标未知、需要审计当前连接或错误要求检查实例时/);
  assert.match(skill, /不了解能力领域时可先看 group，已有业务关键词时可直接 search/);
  assert.match(skill, /有界的临时 Unity 内存或 Editor API 逻辑可以考虑 `code\.execute`/);
  assert.match(skill, /稳定发现、typed schema、结构化结果、明确副作用、重复使用或团队共享/);
  assert.match(skill, /references\/custom-tools\.md/);

  for (const required of ["unitycli init help", "init discover", "init package --project <UnityProject>", "--close-editor", "init skill --target <SkillDirectory>", "init skill --agent <.agents|claude>", "--scope <workspace|user>", "--workspace <path>", "--check"]) {
    assert.match(operation, new RegExp(required.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  }
  assert.doesNotMatch(operation, /pkg-only|skill-only|init --check|init --project/);
});

test("agent interface is broad-triggered, PowerShell-local, and network-denied", async () => {
  const text = await readFile(join(skillRoot, "agents", "interface.yaml"), "utf8");
  assert.match(text, /display_name: "UnityCLI"/);
  assert.match(text, /Use \$unitycli for every Unity task/);
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

test("product installer projects byte-identical five-file runtime bundles for both hosts", async () => {
  const project = await mkdtemp(join(tmpdir(), "unity-cli-governed-install-"));
  const sourcePath = join(skillRoot, "SKILL.md");
  const expected = [
    "SKILL.md",
    "references/custom-tools.md",
    "references/feedback.md",
    "references/operation-protocol.md",
    "scripts/record-feedback.ps1",
  ];
  try {
    const registry = new AgentSkillInstallerRegistry();
    for (const agent of [".agents", "claude"] as const) {
      await registry.installForAgents(agent, sourcePath, project);
      const target = join(project, agent === ".agents" ? ".agents" : ".claude", "skills", "unitycli");
      assert.deepEqual(await listFiles(target), expected);
      for (const file of expected) assert.deepEqual(await readFile(join(target, file)), await readFile(join(skillRoot, file)), `${agent}:${file}`);
      await assert.rejects(access(join(target, "manifest.json")));
      await assert.rejects(access(join(target, "reports")));
    }
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});
