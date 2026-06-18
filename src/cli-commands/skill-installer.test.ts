import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { join, dirname } from "node:path";
import { tmpdir } from "node:os";
import test from "node:test";
import {
  AgentSkillInstallerRegistry,
  AGENT_KINDS,
  findSkillSourcePath,
  parseAgentList,
  type AgentKind,
  type SkillInstallResult,
  type SkillCheckResult,
  SkillInstallerError
} from "./skill-installer.js";

// ─── Helpers ─────────────────────────────────────────────────────────────────

async function makeSkillSource(content: string): Promise<string> {
  const dir = await mkdtemp(join(tmpdir(), "skill-source-"));
  const skillDir = join(dir, "skills", "unity_cli");
  await mkdir(skillDir, { recursive: true });
  await writeFile(join(skillDir, "SKILL.md"), content, "utf8");
  return join(skillDir, "SKILL.md");
}

async function makeProject(): Promise<string> {
  const project = await mkdtemp(join(tmpdir(), "unity-skill-project-"));
  const packages = join(project, "Packages");
  await mkdir(packages, { recursive: true });
  await writeFile(join(packages, "manifest.json"), JSON.stringify({ dependencies: {} }, null, 2), "utf8");
  return project;
}

function registry(): AgentSkillInstallerRegistry {
  return new AgentSkillInstallerRegistry();
}

// ─── parseAgentList ──────────────────────────────────────────────────────────

test("parseAgentList accepts single agent string", () => {
  assert.deepEqual(parseAgentList("pi"), ["pi"]);
});

test("parseAgentList accepts comma-separated agents", () => {
  assert.deepEqual(parseAgentList("pi,claude"), ["pi", "claude"]);
});

test("parseAgentList accepts all three agents", () => {
  assert.deepEqual(parseAgentList("pi,claude,.agent"), ["pi", "claude", ".agent"]);
});

test("parseAgentList accepts AgentKind[] array", () => {
  assert.deepEqual(parseAgentList(["pi", "claude"]), ["pi", "claude"]);
});

test("parseAgentList trims whitespace", () => {
  assert.deepEqual(parseAgentList("  pi ,  claude  "), ["pi", "claude"]);
});

test("parseAgentList deduplicates", () => {
  assert.deepEqual(parseAgentList("pi,pi"), ["pi"]);
});

test("parseAgentList throws for unknown agent kind", () => {
  assert.throws(() => parseAgentList("unknown"), (error: unknown) => {
    assert.ok(error instanceof SkillInstallerError);
    assert.equal(error.code, "UNKNOWN_AGENT_KIND");
    return true;
  });
});

test("parseAgentList throws for empty list", () => {
  assert.throws(() => parseAgentList(""), (error: unknown) => {
    assert.ok(error instanceof SkillInstallerError);
    assert.equal(error.code, "AGENT_KIND_REQUIRED");
    return true;
  });
});

// ─── AGENT_KINDS ─────────────────────────────────────────────────────────────

test("AGENT_KINDS contains pi, claude, .agent", () => {
  assert.deepEqual([...AGENT_KINDS], ["pi", "claude", ".agent"]);
});

// ─── resolveTargetPath ───────────────────────────────────────────────────────

test("resolveTargetPath maps pi to .pi/skills/unity_cli/SKILL.md", () => {
  const reg = registry();
  const installer = reg.get("pi")!;
  assert.equal(installer.resolveTargetPath("/project"), "/project/.pi/skills/unity_cli/SKILL.md");
});

test("resolveTargetPath maps claude to .claude/skills/unity_cli/SKILL.md", () => {
  const reg = registry();
  const installer = reg.get("claude")!;
  assert.equal(installer.resolveTargetPath("/project"), "/project/.claude/skills/unity_cli/SKILL.md");
});

test("resolveTargetPath maps .agent to .agent/skills/unity_cli/SKILL.md", () => {
  const reg = registry();
  const installer = reg.get(".agent")!;
  assert.equal(installer.resolveTargetPath("/project"), "/project/.agent/skills/unity_cli/SKILL.md");
});

// ─── Install: single agent ───────────────────────────────────────────────────

test("install for pi creates .pi/skills/unity_cli/SKILL.md", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# Unity CLI Skill\nHello world");
  const project = await makeProject();

  const results = await reg.installForAgents("pi", sourcePath, project);
  assert.equal(results.length, 1);
  assert.equal(results[0].agent, "pi");
  assert.equal(results[0].status, "installed");

  const targetPath = join(project, ".pi", "skills", "unity_cli", "SKILL.md");
  const content = await readFile(targetPath, "utf8");
  assert.equal(content, "# Unity CLI Skill\nHello world");

  await rm(dirname(sourcePath), { recursive: true });
  await rm(project, { recursive: true });
});

test("install for claude creates .claude/skills/unity_cli/SKILL.md", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# Unity CLI Skill\nClaude test");
  const project = await makeProject();

  const results = await reg.installForAgents("claude", sourcePath, project);
  assert.equal(results.length, 1);
  assert.equal(results[0].agent, "claude");
  assert.equal(results[0].status, "installed");

  const targetPath = join(project, ".claude", "skills", "unity_cli", "SKILL.md");
  const content = await readFile(targetPath, "utf8");
  assert.equal(content, "# Unity CLI Skill\nClaude test");

  await rm(dirname(sourcePath), { recursive: true });
  await rm(project, { recursive: true });
});

test("install for .agent creates .agent/skills/unity_cli/SKILL.md", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# Unity CLI Skill\nAgent test");
  const project = await makeProject();

  const results = await reg.installForAgents(".agent", sourcePath, project);
  assert.equal(results.length, 1);
  assert.equal(results[0].agent, ".agent");
  assert.equal(results[0].status, "installed");

  const targetPath = join(project, ".agent", "skills", "unity_cli", "SKILL.md");
  const content = await readFile(targetPath, "utf8");
  assert.equal(content, "# Unity CLI Skill\nAgent test");

  await rm(dirname(sourcePath), { recursive: true });
  await rm(project, { recursive: true });
});

// ─── Install: multi-agent ────────────────────────────────────────────────────

test("install for pi,claude installs two agent skills", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# Unity CLI Skill\nMulti-agent test");
  const project = await makeProject();

  const results = await reg.installForAgents("pi,claude", sourcePath, project);
  assert.equal(results.length, 2);
  assert.equal(results[0].agent, "pi");
  assert.equal(results[0].status, "installed");
  assert.equal(results[1].agent, "claude");
  assert.equal(results[1].status, "installed");

  // Verify both files exist
  const piContent = await readFile(join(project, ".pi", "skills", "unity_cli", "SKILL.md"), "utf8");
  const claudeContent = await readFile(join(project, ".claude", "skills", "unity_cli", "SKILL.md"), "utf8");
  assert.equal(piContent, "# Unity CLI Skill\nMulti-agent test");
  assert.equal(claudeContent, "# Unity CLI Skill\nMulti-agent test");

  await rm(dirname(sourcePath), { recursive: true });
  await rm(project, { recursive: true });
});

// ─── Install: hash skip ──────────────────────────────────────────────────────

test("install skips when target hash matches source", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# Unity CLI Skill\nSame content");
  const project = await makeProject();

  // First install
  const first = await reg.installForAgents("pi", sourcePath, project);
  assert.equal(first[0].status, "installed");

  // Second install — same content
  const second = await reg.installForAgents("pi", sourcePath, project);
  assert.equal(second[0].status, "skipped");
  assert.equal(second[0].hash, first[0].hash);

  await rm(dirname(sourcePath), { recursive: true });
  await rm(project, { recursive: true });
});

// ─── Install: hash overwrite with change description ─────────────────────────

test("install overwrites when source hash differs and reports change", async () => {
  const reg = registry();
  const sourceDir = await mkdtemp(join(tmpdir(), "skill-source-"));
  const skillDir = join(sourceDir, "skills", "unity_cli");
  await mkdir(skillDir, { recursive: true });
  const sourcePath = join(skillDir, "SKILL.md");
  const project = await makeProject();

  // Install with initial content
  await writeFile(sourcePath, "# Version 1\nInitial content", "utf8");
  const first = await reg.installForAgents("pi", sourcePath, project);
  assert.equal(first[0].status, "installed");
  const firstHash = first[0].hash!;

  // Modify source content
  await writeFile(sourcePath, "# Version 2\nUpdated content", "utf8");
  const second = await reg.installForAgents("pi", sourcePath, project);
  assert.equal(second[0].status, "updated");
  assert.equal(second[0].previousHash, firstHash);
  assert.ok(second[0].newHash !== firstHash);

  // Verify the file was actually overwritten
  const content = await readFile(join(project, ".pi", "skills", "unity_cli", "SKILL.md"), "utf8");
  assert.equal(content, "# Version 2\nUpdated content");

  await rm(sourceDir, { recursive: true });
  await rm(project, { recursive: true });
});

// ─── Install: directory auto-creation ────────────────────────────────────────

test("install creates target directory when it does not exist", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# Unity CLI Skill\nDir test");
  const project = await makeProject();

  // Project has no .pi directory at all
  const results = await reg.installForAgents("pi", sourcePath, project);
  assert.equal(results[0].status, "installed");

  // Verify nested directory was created
  const content = await readFile(join(project, ".pi", "skills", "unity_cli", "SKILL.md"), "utf8");
  assert.equal(content, "# Unity CLI Skill\nDir test");

  await rm(dirname(sourcePath), { recursive: true });
  await rm(project, { recursive: true });
});

// ─── Check mode ──────────────────────────────────────────────────────────────

test("check reports missing when skill is not installed", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# Unity CLI Skill\nCheck test");
  const project = await makeProject();

  const results = await reg.checkForAgents("pi", sourcePath, project);
  assert.equal(results.length, 1);
  assert.equal(results[0].agent, "pi");
  assert.equal(results[0].status, "missing");
  assert.ok(results[0].sourceHash);

  await rm(dirname(sourcePath), { recursive: true });
  await rm(project, { recursive: true });
});

test("check reports installed when skill matches source", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# Unity CLI Skill\nCheck installed");
  const project = await makeProject();

  // Install first
  await reg.installForAgents("pi", sourcePath, project);

  const results = await reg.checkForAgents("pi", sourcePath, project);
  assert.equal(results[0].status, "installed");
  assert.equal(results[0].hash, results[0].sourceHash);

  await rm(dirname(sourcePath), { recursive: true });
  await rm(project, { recursive: true });
});

test("check reports outdated when skill differs from source", async () => {
  const reg = registry();
  const sourceDir = await mkdtemp(join(tmpdir(), "skill-source-"));
  const skillDir = join(sourceDir, "skills", "unity_cli");
  await mkdir(skillDir, { recursive: true });
  const sourcePath = join(skillDir, "SKILL.md");
  const project = await makeProject();

  // Install with initial content
  await writeFile(sourcePath, "# Version 1\nOld", "utf8");
  await reg.installForAgents("pi", sourcePath, project);

  // Modify source
  await writeFile(sourcePath, "# Version 2\nNew", "utf8");
  const results = await reg.checkForAgents("pi", sourcePath, project);
  assert.equal(results[0].status, "outdated");
  assert.ok(results[0].hash !== results[0].sourceHash);

  await rm(sourceDir, { recursive: true });
  await rm(project, { recursive: true });
});

// ─── Check with --agent ──────────────────────────────────────────────────────

test("check for multiple agents reports per-agent status", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# Unity CLI Skill\nMulti check");
  const project = await makeProject();

  // Install only pi
  await reg.installForAgents("pi", sourcePath, project);

  const results = await reg.checkForAgents("pi,claude", sourcePath, project);
  assert.equal(results.length, 2);
  assert.equal(results[0].agent, "pi");
  assert.equal(results[0].status, "installed");
  assert.equal(results[1].agent, "claude");
  assert.equal(results[1].status, "missing");

  await rm(dirname(sourcePath), { recursive: true });
  await rm(project, { recursive: true });
});

// ─── Unknown agent ───────────────────────────────────────────────────────────

test("installForAgents throws for unknown agent kind", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# test");
  const project = await makeProject();

  await assert.rejects(
    () => reg.installForAgents("unknown", sourcePath, project),
    (error: unknown) => {
      assert.ok(error instanceof SkillInstallerError);
      assert.equal(error.code, "UNKNOWN_AGENT_KIND");
      return true;
    }
  );

  await rm(dirname(sourcePath), { recursive: true });
  await rm(project, { recursive: true });
});

// ─── findSkillSourcePath ─────────────────────────────────────────────────────

test("findSkillSourcePath resolves skills/unity_cli/SKILL.md from package root", async () => {
  // findSkillSourcePath should find the actual skill file from the package
  const sourcePath = await findSkillSourcePath();
  assert.ok(sourcePath.endsWith("skills/unity_cli/SKILL.md") || sourcePath.includes("skills"));

  // Verify the file is readable
  const content = await readFile(sourcePath, "utf8");
  assert.ok(content.length > 0);
});
