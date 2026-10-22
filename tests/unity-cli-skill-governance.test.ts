import assert from "node:assert/strict";
import { access, lstat, mkdir, mkdtemp, readFile, readdir, realpath, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import test from "node:test";
import { AgentSkillInstallerRegistry } from "../src/cli-commands/skill-installer.js";

const skillRoot = resolve("skills", "unitycli");
const factoryRoot = resolve("skill-factory", "unitycli");

/** 递归列出相对文件路径，统一使用正斜杠。 */
async function listFiles(root: string, directory = ""): Promise<string[]> {
  const absolute = join(root, directory);
  const files: string[] = [];
  for (const entry of await readdir(absolute, { withFileTypes: true })) {
    const relative = directory ? join(directory, entry.name) : entry.name;
    assert.equal(entry.isSymbolicLink(), false, `Refuse linked fixture entry: ${relative}`);
    if (entry.isDirectory()) files.push(...await listFiles(root, relative));
    else if (entry.isFile()) files.push(relative.split("\\").join("/"));
  }
  return files.sort();
}

test("governed manifest declares ownership, runtime projection, contracts, and missing evidence", async () => {
  const manifest = JSON.parse(await readFile(join(factoryRoot, "manifest.json"), "utf8")) as Record<string, any>;
  const packageJson = JSON.parse(await readFile(resolve("package.json"), "utf8")) as { version: string };
  const skill = await readFile(join(skillRoot, "SKILL.md"), "utf8");
  assert.match(skill, /^---\r?\nname: unitycli\r?$/m);
  assert.equal(manifest.name, "unitycli");
  assert.equal(manifest.version, packageJson.version);
  assert.ok(manifest.owner);
  assert.equal(manifest.review_cadence, "per-release");
  assert.equal(manifest.maturity_tier, "governed");
  // 信任报告声明 per-release 节奏，因此必须提到当前发布版本，否则会被静默跳过刷新。
  const trustReport = await readFile(join(factoryRoot, "reports", "trust-report.md"), "utf8");
  const escapedVersion = packageJson.version.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  assert.match(trustReport, new RegExp(`for release ${escapedVersion}\\b`), "trust report must be reviewed for the current release");
  assert.equal(manifest.lifecycle_stage, "governed");
  assert.deepEqual(manifest.runtime_projection.include, ["SKILL.md", "references/**", "scripts/**"]);
  assert.equal(manifest.factory_root, "skill-factory/unitycli");
  // 安装投影以外的治理资产必须位于 factory root；Skill 目录只保留运行时入口与投影。
  // experience/ 是安装后的本地反馈状态，不属于版本控制内容。
  const runtimeTopLevel = new Set((await listFiles(skillRoot))
    .filter((file) => !file.startsWith("experience/"))
    .map((file) => (file.includes("/") ? file.split("/")[0] : file)));
  assert.deepEqual([...runtimeTopLevel].sort(), ["SKILL.md", "references", "scripts"]);
  assert.ok(manifest.input_files["file-backed fixture"].length >= 3);
  assert.ok(manifest.output_contract.feedback_summary_condition);
  assert.ok(manifest.rollback_boundary.skill_install);
  assert.equal(manifest.permissions.network, "deny");
  assert.ok(Object.values(manifest.evidence_status).filter((value) => value === "missing evidence").length >= 4);
});

test("public runtime Skill routes operations without embedding the setup workflow", async () => {
  const skill = await readFile(join(skillRoot, "SKILL.md"), "utf8");
  const operation = await readFile(join(skillRoot, "references", "operation-protocol.md"), "utf8");
  const installation = await readFile(join(skillRoot, "references", "installation.md"), "utf8");

  assert.match(skill, /运行时导航入口，不是固定工具手册/);
  assert.match(skill, /\]\(references\/installation\.md\)/);
  for (const setupDetail of ["npm install", "init discover", "init package --close-editor", "init skill --target", "extensions list", "extensions install", "extensions remove", "extensions doctor"]) {
    const pattern = new RegExp(setupDetail.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"));
    assert.doesNotMatch(skill, pattern);
    assert.doesNotMatch(operation, pattern);
  }

  assert.match(skill, /目标未知、需要审计当前连接或错误要求检查实例时/);
  assert.match(skill, /当前会话首次发现或调用目标项目的 Unity 能力前，必须先执行 `tools group`/);
  assert.match(skill, /有界的临时 Unity 内存或 Editor API 逻辑可以考虑 `debug\.code\.execute`/);
  assert.match(skill, /稳定发现、typed schema、结构化结果、明确副作用、重复使用或团队共享/);
  assert.match(skill, /references\/custom-tools\.md/);

  for (const required of ["npm install -g @alpsckr/unitycli@latest", "unitycli init help", "init discover", "init package --project <UnityProject>", "--close-editor", "init skill --target <SkillDirectory>", "init skill --agent <.agents|claude>", "--scope <workspace|user>", "--workspace <path>", "--check"]) {
    assert.match(installation, new RegExp(required.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  }
  assert.doesNotMatch(installation, /pkg-only|skill-only|init --check|init --project/);
});

test("agent interface is broad-triggered, PowerShell-local, and network-denied", async () => {
  const text = await readFile(join(factoryRoot, "agents", "interface.yaml"), "utf8");
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
  const casesPath = join(factoryRoot, "evals", "output", "cases.jsonl");
  const cases = (await readFile(casesPath, "utf8")).split(/\r?\n/).filter(Boolean).map((line) => JSON.parse(line) as Record<string, any>);
  assert.equal(cases.length, 7);
  const fileBacked = cases.filter((item) => Array.isArray(item.input_files) && item.input_files.length > 0);
  assert.ok(fileBacked.length >= 2);
  for (const item of fileBacked) {
    for (const input of item.input_files as string[]) await access(join(dirname(casesPath), input));
  }
  const trust = await readFile(join(factoryRoot, "reports", "trust-report.md"), "utf8");
  const scorecard = await readFile(join(factoryRoot, "reports", "output_quality_scorecard.md"), "utf8");
  assert.match(trust, /trust report/i);
  assert.match(trust, /rollback boundary/i);
  assert.match(trust, /missing evidence/i);
  assert.match(scorecard, /output contract/i);
  assert.match(scorecard, /file-backed fixture/i);
  assert.match(scorecard, /missing evidence/i);
});

test("product installer projects byte-identical six-file runtime bundles for both hosts", async () => {
  // 夹具只在固定开发临时根内创建；拒绝根目录经过链接重定向。
  const temporaryRoot = resolve(process.env.LOCALAPPDATA ?? tmpdir(), "UnityCli-Dev", ".temp", "skill-projection-tests");
  await mkdir(temporaryRoot, { recursive: true });
  const allowedRoot = await realpath(temporaryRoot);
  assert.equal(allowedRoot.toLowerCase(), temporaryRoot.toLowerCase());
  const project = await mkdtemp(join(allowedRoot, "bundle-"));
  const sourcePath = join(skillRoot, "SKILL.md");
  const expected = [
    "SKILL.md",
    "references/custom-tools.md",
    "references/debugging.md",
    "references/feedback.md",
    "references/installation.md",
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
      // 新引用文件既要随安装投影，也要通过产品的只读完整性检查。
      const [checked] = await registry.checkForAgents(agent, sourcePath, project);
      assert.equal(checked.status, "installed");
      await assert.rejects(access(join(target, "manifest.json")));
      await assert.rejects(access(join(target, "reports")));
    }
  } finally {
    // 删除前验证真实目标严格位于任务临时根内，并拒绝子树中的链接。
    assert.equal((await lstat(project)).isSymbolicLink(), false);
    const canonicalProject = await realpath(project);
    assert.equal(dirname(canonicalProject), await realpath(allowedRoot));
    await listFiles(canonicalProject);
    await rm(canonicalProject, { recursive: true, force: true });
  }
});
