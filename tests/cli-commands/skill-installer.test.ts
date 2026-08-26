import assert from "node:assert/strict";
import { access, mkdir, mkdtemp, readFile, rename, rm, symlink, writeFile } from "node:fs/promises";
import { join, dirname } from "node:path";
import { tmpdir } from "node:os";
import test from "node:test";
import { handleInit } from "../../src/cli-commands/package.js";
import type { Runtime } from "../../src/cli-commands/types.js";
import {
  AgentSkillInstallerRegistry,
  AGENT_KINDS,
  findSkillSourcePath,
  parseAgentList,
  type AgentKind,
  type SkillInstallResult,
  type SkillCheckResult,
  setAfterSkillPublishForTest,
  setBeforeSkillPublishForTest,
  setBeforeSkillPreviousCleanupForTest,
  setBeforeSkillRollbackForTest,
  SkillInstallerError
} from "../../src/cli-commands/skill-installer.js";

// ─── Helpers ─────────────────────────────────────────────────────────────────

function skillText(content: string, mode: "off" | "on" = "off"): string {
  return `${content}\n\nFeedbackMode: ${mode}\n`;
}

async function makeSkillSource(content: string): Promise<string> {
  const dir = await mkdtemp(join(tmpdir(), "skill-source-"));
  const skillDir = join(dir, "skills", "unitycli");
  await mkdir(join(skillDir, "references"), { recursive: true });
  await mkdir(join(skillDir, "scripts"), { recursive: true });
  await writeFile(join(skillDir, "SKILL.md"), skillText(content), "utf8");
  await writeFile(join(skillDir, "references", "guide.md"), "# Runtime guide\n", "utf8");
  await writeFile(join(skillDir, "scripts", "record.ps1"), "# Runtime script\n", "utf8");
  return join(skillDir, "SKILL.md");
}

async function makeProject(): Promise<string> {
  const project = await mkdtemp(join(tmpdir(), "unity-skill-project-"));
  const packages = join(project, "Packages");
  await mkdir(packages, { recursive: true });
  await writeFile(join(packages, "manifest.json"), JSON.stringify({ dependencies: {} }, null, 2), "utf8");
  return project;
}

async function makeUnityProject(): Promise<string> {
  const project = await makeProject();
  await mkdir(join(project, "Assets"), { recursive: true });
  await mkdir(join(project, "ProjectSettings"), { recursive: true });
  await mkdir(join(project, ".pi"), { recursive: true });
  await writeFile(join(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.61f1\n", "utf8");
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
  assert.deepEqual(parseAgentList("pi,claude,.agents"), ["pi", "claude", ".agents"]);
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

test("parseAgentList rejects unsupported .agent directory name", () => {
  assert.throws(() => parseAgentList(".agent"), (error: unknown) => {
    assert.ok(error instanceof SkillInstallerError);
    assert.equal(error.code, "UNKNOWN_AGENT_KIND");
    assert.match(error.message, /\.agents/);
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

test("AGENT_KINDS contains pi, claude, .agents", () => {
  assert.deepEqual([...AGENT_KINDS], ["pi", "claude", ".agents"]);
});

// ─── resolveTargetPath ───────────────────────────────────────────────────────

test("resolveTargetPath maps pi to .pi/skills/unitycli/SKILL.md", () => {
  const reg = registry();
  const installer = reg.get("pi")!;
  assert.equal(installer.resolveTargetPath("/project"), join("/project", ".pi", "skills", "unitycli", "SKILL.md"));
});

test("resolveTargetPath maps claude to .claude/skills/unitycli/SKILL.md", () => {
  const reg = registry();
  const installer = reg.get("claude")!;
  assert.equal(installer.resolveTargetPath("/project"), join("/project", ".claude", "skills", "unitycli", "SKILL.md"));
});

test("resolveTargetPath maps .agents to .agents/skills/unitycli/SKILL.md", () => {
  const reg = registry();
  const installer = reg.get(".agents")!;
  assert.equal(installer.resolveTargetPath("/project"), join("/project", ".agents", "skills", "unitycli", "SKILL.md"));
});

// ─── Install: single agent ───────────────────────────────────────────────────

test("install for pi creates .pi/skills/unitycli/SKILL.md", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# UnityCLI Skill\nHello world");
  const project = await makeProject();

  const results = await reg.installForAgents("pi", sourcePath, project);
  assert.equal(results.length, 1);
  assert.equal(results[0].agent, "pi");
  assert.equal(results[0].status, "installed");

  const targetPath = join(project, ".pi", "skills", "unitycli", "SKILL.md");
  const content = await readFile(targetPath, "utf8");
  assert.equal(content, skillText("# UnityCLI Skill\nHello world"));
  assert.equal(await readFile(join(dirname(targetPath), "references", "guide.md"), "utf8"), "# Runtime guide\n");
  assert.equal(await readFile(join(dirname(targetPath), "scripts", "record.ps1"), "utf8"), "# Runtime script\n");

  await rm(dirname(sourcePath), { recursive: true });
  await rm(project, { recursive: true });
});

test("install for claude creates .claude/skills/unitycli/SKILL.md", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# UnityCLI Skill\nClaude test");
  const project = await makeProject();

  const results = await reg.installForAgents("claude", sourcePath, project);
  assert.equal(results.length, 1);
  assert.equal(results[0].agent, "claude");
  assert.equal(results[0].status, "installed");

  const targetPath = join(project, ".claude", "skills", "unitycli", "SKILL.md");
  const content = await readFile(targetPath, "utf8");
  assert.equal(content, skillText("# UnityCLI Skill\nClaude test"));

  await rm(dirname(sourcePath), { recursive: true });
  await rm(project, { recursive: true });
});

test("install for .agents creates .agents/skills/unitycli/SKILL.md", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# UnityCLI Skill\nAgent test");
  const project = await makeProject();

  const results = await reg.installForAgents(".agents", sourcePath, project);
  assert.equal(results.length, 1);
  assert.equal(results[0].agent, ".agents");
  assert.equal(results[0].status, "installed");

  const targetPath = join(project, ".agents", "skills", "unitycli", "SKILL.md");
  const content = await readFile(targetPath, "utf8");
  assert.equal(content, skillText("# UnityCLI Skill\nAgent test"));

  await rm(dirname(sourcePath), { recursive: true });
  await rm(project, { recursive: true });
});

// ─── Install: multi-agent ────────────────────────────────────────────────────

test("install for pi,claude installs two agent skills", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# UnityCLI Skill\nMulti-agent test");
  const project = await makeProject();

  const results = await reg.installForAgents("pi,claude", sourcePath, project);
  assert.equal(results.length, 2);
  assert.equal(results[0].agent, "pi");
  assert.equal(results[0].status, "installed");
  assert.equal(results[1].agent, "claude");
  assert.equal(results[1].status, "installed");

  // Verify both files exist
  const piContent = await readFile(join(project, ".pi", "skills", "unitycli", "SKILL.md"), "utf8");
  const claudeContent = await readFile(join(project, ".claude", "skills", "unitycli", "SKILL.md"), "utf8");
  assert.equal(piContent, skillText("# UnityCLI Skill\nMulti-agent test"));
  assert.equal(claudeContent, skillText("# UnityCLI Skill\nMulti-agent test"));

  await rm(dirname(sourcePath), { recursive: true });
  await rm(project, { recursive: true });
});

// ─── Install: hash skip ──────────────────────────────────────────────────────

test("install skips when target hash matches source", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# UnityCLI Skill\nSame content");
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
  const skillDir = join(sourceDir, "skills", "unitycli");
  await mkdir(skillDir, { recursive: true });
  const sourcePath = join(skillDir, "SKILL.md");
  const project = await makeProject();

  // Install with initial content
  await writeFile(sourcePath, skillText("# Version 1\nInitial content"), "utf8");
  const first = await reg.installForAgents("pi", sourcePath, project);
  assert.equal(first[0].status, "installed");
  const firstHash = first[0].hash!;

  // Modify source content
  await writeFile(sourcePath, skillText("# Version 2\nUpdated content"), "utf8");
  const second = await reg.installForAgents("pi", sourcePath, project);
  assert.equal(second[0].status, "updated");
  assert.equal(second[0].previousHash, firstHash);
  assert.ok(second[0].newHash !== firstHash);

  // Verify the file was actually overwritten
  const content = await readFile(join(project, ".pi", "skills", "unitycli", "SKILL.md"), "utf8");
  assert.equal(content, skillText("# Version 2\nUpdated content"));

  await rm(sourceDir, { recursive: true });
  await rm(project, { recursive: true });
});

// ─── Install: directory auto-creation ────────────────────────────────────────

test("install creates target directory when it does not exist", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# UnityCLI Skill\nDir test");
  const project = await makeProject();

  // Project has no .pi directory at all
  const results = await reg.installForAgents("pi", sourcePath, project);
  assert.equal(results[0].status, "installed");

  // Verify nested directory was created
  const content = await readFile(join(project, ".pi", "skills", "unitycli", "SKILL.md"), "utf8");
  assert.equal(content, skillText("# UnityCLI Skill\nDir test"));

  await rm(dirname(sourcePath), { recursive: true });
  await rm(project, { recursive: true });
});

// ─── Check mode ──────────────────────────────────────────────────────────────

test("check reports missing when skill is not installed", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# UnityCLI Skill\nCheck test");
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
  const sourcePath = await makeSkillSource("# UnityCLI Skill\nCheck installed");
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
  const skillDir = join(sourceDir, "skills", "unitycli");
  await mkdir(skillDir, { recursive: true });
  const sourcePath = join(skillDir, "SKILL.md");
  const project = await makeProject();

  // Install with initial content
  await writeFile(sourcePath, skillText("# Version 1\nOld"), "utf8");
  await reg.installForAgents("pi", sourcePath, project);

  // Modify source
  await writeFile(sourcePath, skillText("# Version 2\nNew"), "utf8");
  const results = await reg.checkForAgents("pi", sourcePath, project);
  assert.equal(results[0].status, "outdated");
  assert.ok(results[0].hash !== results[0].sourceHash);

  await rm(sourceDir, { recursive: true });
  await rm(project, { recursive: true });
});

// ─── Check with --agent ──────────────────────────────────────────────────────

test("check for multiple agents reports per-agent status", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# UnityCLI Skill\nMulti check");
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

test("check covers changed, missing, and extra runtime bundle files", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# UnityCLI Skill\nBundle check");
  const project = await makeProject();
  const targetRoot = join(project, ".pi", "skills", "unitycli");
  await reg.installForAgents("pi", sourcePath, project);

  await writeFile(join(targetRoot, "references", "guide.md"), "changed\n", "utf8");
  assert.equal((await reg.checkForAgents("pi", sourcePath, project))[0].status, "outdated");
  await reg.installForAgents("pi", sourcePath, project);

  await rm(join(targetRoot, "scripts", "record.ps1"));
  assert.equal((await reg.checkForAgents("pi", sourcePath, project))[0].status, "outdated");
  await reg.installForAgents("pi", sourcePath, project);

  await writeFile(join(targetRoot, "references", "extra.md"), "extra\n", "utf8");
  assert.equal((await reg.checkForAgents("pi", sourcePath, project))[0].status, "outdated");
  await reg.installForAgents("pi", sourcePath, project);
  await assert.rejects(access(join(targetRoot, "references", "extra.md")));

  await rm(dirname(dirname(sourcePath)), { recursive: true });
  await rm(project, { recursive: true });
});

test("bundle update removes source-deleted managed files", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# UnityCLI Skill\nBundle cleanup");
  const sourceRoot = dirname(sourcePath);
  const project = await makeProject();
  const targetRoot = join(project, ".pi", "skills", "unitycli");
  await reg.installForAgents("pi", sourcePath, project);

  await rm(join(sourceRoot, "references", "guide.md"));
  await writeFile(join(sourceRoot, "references", "current.md"), "current\n", "utf8");
  const result = await reg.installForAgents("pi", sourcePath, project);
  assert.equal(result[0].status, "updated");
  await assert.rejects(access(join(targetRoot, "references", "guide.md")));
  assert.equal(await readFile(join(targetRoot, "references", "current.md"), "utf8"), "current\n");

  await rm(dirname(dirname(sourcePath)), { recursive: true });
  await rm(project, { recursive: true });
});

test("FeedbackMode is ignored by bundle check and preserved across updates", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# UnityCLI Skill\nFeedback mode");
  const project = await makeProject();
  const targetRoot = join(project, ".pi", "skills", "unitycli");
  const targetSkill = join(targetRoot, "SKILL.md");
  await reg.installForAgents("pi", sourcePath, project);

  await writeFile(targetSkill, (await readFile(targetSkill, "utf8")).replace("FeedbackMode: off", "FeedbackMode: on"), "utf8");
  assert.equal((await reg.checkForAgents("pi", sourcePath, project))[0].status, "installed");

  await writeFile(join(dirname(sourcePath), "references", "guide.md"), "# Updated guide\n", "utf8");
  assert.equal((await reg.installForAgents("pi", sourcePath, project))[0].status, "updated");
  assert.match(await readFile(targetSkill, "utf8"), /^FeedbackMode: on$/m);
  assert.equal((await reg.checkForAgents("pi", sourcePath, project))[0].status, "installed");

  await rm(dirname(dirname(sourcePath)), { recursive: true });
  await rm(project, { recursive: true });
});

test("invalid or duplicate FeedbackMode declarations fail closed", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# UnityCLI Skill\nInvalid feedback");
  const project = await makeProject();
  await reg.installForAgents("pi", sourcePath, project);
  const targetSkill = join(project, ".pi", "skills", "unitycli", "SKILL.md");

  await writeFile(targetSkill, (await readFile(targetSkill, "utf8")).replace("FeedbackMode: off", "FeedbackMode: maybe"), "utf8");
  await assert.rejects(() => reg.checkForAgents("pi", sourcePath, project), (error: unknown) => {
    assert.ok(error instanceof SkillInstallerError);
    assert.equal(error.code, "E_SKILL_FEEDBACK_MODE_INVALID");
    return true;
  });

  await writeFile(targetSkill, `${skillText("# UnityCLI Skill\nDuplicate")}FeedbackMode: on\n`, "utf8");
  await assert.rejects(() => reg.installForAgents("pi", sourcePath, project), (error: unknown) => {
    assert.ok(error instanceof SkillInstallerError);
    assert.equal(error.code, "E_SKILL_FEEDBACK_MODE_INVALID");
    return true;
  });

  await rm(dirname(dirname(sourcePath)), { recursive: true });
  await rm(project, { recursive: true });
});

test("publish failure restores the prior complete bundle and clears fixed workspace", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# UnityCLI Skill\nRestore prior");
  const project = await makeProject();
  const targetRoot = join(project, ".pi", "skills", "unitycli");
  await reg.installForAgents("pi", sourcePath, project);
  const previousSkill = await readFile(join(targetRoot, "SKILL.md"), "utf8");
  await writeFile(join(dirname(sourcePath), "references", "guide.md"), "# New source\n", "utf8");

  setBeforeSkillPublishForTest(async () => { throw new Error("forced publish failure"); });
  try {
    await assert.rejects(() => reg.installForAgents("pi", sourcePath, project), (error: unknown) => {
      assert.ok(error instanceof SkillInstallerError);
      assert.equal(error.code, "E_SKILL_INSTALL_IO");
      return true;
    });
  } finally {
    setBeforeSkillPublishForTest(undefined);
  }
  assert.equal(await readFile(join(targetRoot, "SKILL.md"), "utf8"), previousSkill);
  assert.equal(await readFile(join(targetRoot, "references", "guide.md"), "utf8"), "# Runtime guide\n");
  assert.equal((await reg.checkForAgents("pi", sourcePath, project))[0].status, "outdated");
  const workspace = join(project, "Library", "UnityCli", "skill-install", "pi");
  await assert.rejects(access(join(workspace, "staging")));
  await assert.rejects(access(join(workspace, "previous")));

  await rm(dirname(dirname(sourcePath)), { recursive: true });
  await rm(project, { recursive: true });
});

test("readback failure restores the prior complete bundle", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# UnityCLI Skill\nReadback rollback");
  const project = await makeProject();
  const targetRoot = join(project, ".pi", "skills", "unitycli");
  await reg.installForAgents("pi", sourcePath, project);
  const oldSkill = await readFile(join(targetRoot, "SKILL.md"), "utf8");
  await writeFile(join(dirname(sourcePath), "references", "guide.md"), "# Updated source\n", "utf8");
  setAfterSkillPublishForTest(async () => {
    await writeFile(join(targetRoot, "SKILL.md"), skillText("# Invalid readback").replace("FeedbackMode: off", "FeedbackMode: invalid"), "utf8");
  });
  try {
    await assert.rejects(() => reg.installForAgents("pi", sourcePath, project), (error: unknown) => {
      assert.ok(error instanceof SkillInstallerError);
      assert.equal(error.code, "E_SKILL_FEEDBACK_MODE_INVALID");
      return true;
    });
  } finally {
    setAfterSkillPublishForTest(undefined);
  }
  assert.equal(await readFile(join(targetRoot, "SKILL.md"), "utf8"), oldSkill);
  assert.equal(await readFile(join(targetRoot, "references", "guide.md"), "utf8"), "# Runtime guide\n");

  await rm(dirname(dirname(sourcePath)), { recursive: true });
  await rm(project, { recursive: true });
});

test("rollback failure retains previous and the next install recovers it", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# UnityCLI Skill\nRetained rollback");
  const project = await makeProject();
  const targetRoot = join(project, ".pi", "skills", "unitycli");
  const previousRoot = join(project, "Library", "UnityCli", "skill-install", "pi", "previous");
  await reg.installForAgents("pi", sourcePath, project);
  await writeFile(join(dirname(sourcePath), "references", "guide.md"), "# Updated source\n", "utf8");
  setAfterSkillPublishForTest(async () => {
    await writeFile(join(targetRoot, "SKILL.md"), "# Invalid\n\nFeedbackMode: invalid\n", "utf8");
  });
  setBeforeSkillRollbackForTest(async () => { throw new Error("forced rollback failure"); });
  try {
    await assert.rejects(() => reg.installForAgents("pi", sourcePath, project), (error: unknown) => {
      assert.ok(error instanceof SkillInstallerError);
      assert.equal(error.code, "E_SKILL_INSTALL_ROLLBACK_FAILED");
      return true;
    });
  } finally {
    setAfterSkillPublishForTest(undefined);
    setBeforeSkillRollbackForTest(undefined);
  }
  assert.equal(await readFile(join(targetRoot, "SKILL.md"), "utf8"), "# Invalid\n\nFeedbackMode: invalid\n");
  assert.equal(await readFile(join(previousRoot, "references", "guide.md"), "utf8"), "# Runtime guide\n");

  assert.equal((await reg.installForAgents("pi", sourcePath, project))[0].status, "updated");
  assert.equal((await reg.checkForAgents("pi", sourcePath, project))[0].status, "installed");
  await assert.rejects(access(previousRoot));

  await rm(dirname(dirname(sourcePath)), { recursive: true });
  await rm(project, { recursive: true });
});

test("restart keeps a committed target when previous cleanup was interrupted", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# UnityCLI Skill\nCommitted target");
  const project = await makeProject();
  const targetRoot = join(project, ".pi", "skills", "unitycli");
  const previousRoot = join(project, "Library", "UnityCli", "skill-install", "pi", "previous");
  await reg.installForAgents("pi", sourcePath, project);
  await writeFile(join(dirname(sourcePath), "references", "guide.md"), "# Committed update\n", "utf8");

  setBeforeSkillPreviousCleanupForTest(async () => { throw new Error("simulated process stop before previous cleanup"); });
  try {
    await assert.rejects(() => reg.installForAgents("pi", sourcePath, project), (error: unknown) => {
      assert.ok(error instanceof SkillInstallerError);
      assert.equal(error.code, "E_SKILL_INSTALL_IO");
      return true;
    });
  } finally {
    setBeforeSkillPreviousCleanupForTest(undefined);
  }
  assert.equal(await readFile(join(targetRoot, "references", "guide.md"), "utf8"), "# Committed update\n");
  await access(join(previousRoot, "committed-target.json"));

  assert.equal((await reg.installForAgents("pi", sourcePath, project))[0].status, "skipped");
  assert.equal(await readFile(join(targetRoot, "references", "guide.md"), "utf8"), "# Committed update\n");
  await assert.rejects(access(previousRoot));

  await rm(dirname(dirname(sourcePath)), { recursive: true });
  await rm(project, { recursive: true });
});

test("concurrent installs serialize the publish section", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# UnityCLI Skill\nConcurrent publish");
  const project = await makeProject();
  await reg.installForAgents("pi", sourcePath, project);
  await writeFile(join(dirname(sourcePath), "references", "guide.md"), "# Concurrent update\n", "utf8");
  let active = 0;
  let maximumActive = 0;
  setBeforeSkillPublishForTest(async () => {
    active += 1;
    maximumActive = Math.max(maximumActive, active);
    await new Promise((resolveDelay) => setTimeout(resolveDelay, 75));
    active -= 1;
  });
  let results;
  try {
    results = await Promise.all([
      reg.installForAgents("pi", sourcePath, project),
      reg.installForAgents("pi", sourcePath, project),
    ]);
  } finally {
    setBeforeSkillPublishForTest(undefined);
  }
  assert.equal(maximumActive, 1);
  assert.deepEqual(results.map((result) => result[0].status).sort(), ["skipped", "updated"]);
  assert.equal((await reg.checkForAgents("pi", sourcePath, project))[0].status, "installed");

  await rm(dirname(dirname(sourcePath)), { recursive: true });
  await rm(project, { recursive: true });
});

test("update keeps the published bundle readable and serializes checks", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# UnityCLI Skill\nReadable update");
  const project = await makeProject();
  const targetRoot = join(project, ".pi", "skills", "unitycli");
  await reg.installForAgents("pi", sourcePath, project);
  await writeFile(join(dirname(sourcePath), "references", "guide.md"), "# Updated guide\n", "utf8");

  let enterPublish!: () => void;
  let releasePublish!: () => void;
  const publishEntered = new Promise<void>((resolveEntered) => { enterPublish = resolveEntered; });
  const publishReleased = new Promise<void>((resolveReleased) => { releasePublish = resolveReleased; });
  setBeforeSkillPublishForTest(async () => {
    enterPublish();
    await publishReleased;
  });
  const updatePromise = reg.installForAgents("pi", sourcePath, project);
  let checkCompleted = false;
  let checkPromise: Promise<SkillCheckResult[]> | undefined;
  try {
    await publishEntered;
    assert.match(await readFile(join(targetRoot, "SKILL.md"), "utf8"), /Readable update/);
    assert.equal(await readFile(join(targetRoot, "references", "guide.md"), "utf8"), "# Runtime guide\n");
    checkPromise = reg.checkForAgents("pi", sourcePath, project).finally(() => { checkCompleted = true; });
    await new Promise((resolveDelay) => setTimeout(resolveDelay, 50));
    assert.equal(checkCompleted, false);
  } finally {
    releasePublish();
    setBeforeSkillPublishForTest(undefined);
  }

  assert.equal((await updatePromise)[0].status, "updated");
  assert.equal((await checkPromise!)[0].status, "installed");
  assert.equal(await readFile(join(targetRoot, "references", "guide.md"), "utf8"), "# Updated guide\n");

  await rm(dirname(dirname(sourcePath)), { recursive: true });
  await rm(project, { recursive: true });
});

test("restart recovery restores previous and clears staging before install", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# UnityCLI Skill\nRestart recovery");
  const project = await makeProject();
  const targetRoot = join(project, ".pi", "skills", "unitycli");
  const workspace = join(project, "Library", "UnityCli", "skill-install", "pi");
  await reg.installForAgents("pi", sourcePath, project);
  await mkdir(workspace, { recursive: true });
  await rename(targetRoot, join(workspace, "previous"));
  await mkdir(join(workspace, "staging"), { recursive: true });
  await writeFile(join(workspace, "staging", "partial.txt"), "partial\n", "utf8");

  assert.equal((await reg.installForAgents("pi", sourcePath, project))[0].status, "skipped");
  assert.equal((await reg.checkForAgents("pi", sourcePath, project))[0].status, "installed");
  await assert.rejects(access(join(workspace, "staging")));
  await assert.rejects(access(join(workspace, "previous")));

  await rm(dirname(dirname(sourcePath)), { recursive: true });
  await rm(project, { recursive: true });
});

test("restart discards an uncommitted partial staging while preserving target", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# UnityCLI Skill\nPartial staging recovery");
  const project = await makeProject();
  const targetRoot = join(project, ".pi", "skills", "unitycli");
  const stagingRoot = join(project, "Library", "UnityCli", "skill-install", "pi", "staging");
  await reg.installForAgents("pi", sourcePath, project);
  const priorSkill = await readFile(join(targetRoot, "SKILL.md"), "utf8");
  await mkdir(stagingRoot, { recursive: true });
  await writeFile(join(stagingRoot, "partial.txt"), "partial\n", "utf8");

  assert.equal((await reg.installForAgents("pi", sourcePath, project))[0].status, "skipped");
  assert.equal(await readFile(join(targetRoot, "SKILL.md"), "utf8"), priorSkill);
  await assert.rejects(access(stagingRoot));

  await rm(dirname(dirname(sourcePath)), { recursive: true });
  await rm(project, { recursive: true });
});

test("bundle roots and SKILL.md reject reparse links and invalid UTF-8", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# UnityCLI Skill\nStrict bytes");
  const sourceRoot = dirname(sourcePath);
  const project = await makeProject();
  const external = await mkdtemp(join(tmpdir(), "skill-external-"));
  await writeFile(join(external, "guide.md"), "# Runtime guide\n", "utf8");
  await rm(join(sourceRoot, "references"), { recursive: true });
  await symlink(external, join(sourceRoot, "references"), "junction");
  await assert.rejects(() => reg.checkForAgents("pi", sourcePath, project), (error: unknown) => {
    assert.ok(error instanceof SkillInstallerError);
    assert.equal(error.code, "E_SKILL_BUNDLE_INVALID");
    return true;
  });

  await rm(join(sourceRoot, "references"), { recursive: true });
  await mkdir(join(sourceRoot, "references"));
  await writeFile(join(sourceRoot, "references", "guide.md"), "# Runtime guide\n", "utf8");
  await writeFile(sourcePath, Buffer.concat([Buffer.from("# Invalid\n\nFeedbackMode: off\n", "utf8"), Buffer.from([0x80])]));
  await assert.rejects(() => reg.installForAgents("pi", sourcePath, project), (error: unknown) => {
    assert.ok(error instanceof SkillInstallerError);
    assert.equal(error.code, "E_SKILL_BUNDLE_INVALID");
    return true;
  });

  await rm(dirname(dirname(sourcePath)), { recursive: true });
  await rm(project, { recursive: true });
  await rm(external, { recursive: true });
});

test("expired deadline performs no install effect", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# UnityCLI Skill\nExpired deadline");
  const project = await makeProject();
  await assert.rejects(() => reg.installForAgents("pi", sourcePath, project, Date.now() - 1), (error: unknown) => {
    assert.ok(error instanceof SkillInstallerError);
    assert.equal(error.code, "E_EXECUTION_TIMEOUT");
    return true;
  });
  await assert.rejects(access(join(project, ".pi", "skills", "unitycli")));
  await assert.rejects(access(join(project, "Library", "UnityCli", "skill-install")));

  await rm(dirname(dirname(sourcePath)), { recursive: true });
  await rm(project, { recursive: true });
});

test("check honors a supplied expired deadline", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# UnityCLI Skill\nExpired check");
  const project = await makeProject();
  await reg.installForAgents("pi", sourcePath, project);

  await assert.rejects(
    () => reg.checkForAgents("pi", sourcePath, project, Date.now() - 1),
    (error: unknown) => {
      assert.ok(error instanceof SkillInstallerError);
      assert.equal(error.code, "E_EXECUTION_TIMEOUT");
      return true;
    },
  );

  await rm(dirname(dirname(sourcePath)), { recursive: true });
  await rm(project, { recursive: true });
});

test("deadline expiry before target publication preserves the prior bundle", async () => {
  const reg = registry();
  const sourcePath = await makeSkillSource("# UnityCLI Skill\nDeadline rollback");
  const project = await makeProject();
  const targetRoot = join(project, ".pi", "skills", "unitycli");
  const workspace = join(project, "Library", "UnityCli", "skill-install", "pi");
  await reg.installForAgents("pi", sourcePath, project);
  const priorSkill = await readFile(join(targetRoot, "SKILL.md"), "utf8");
  await writeFile(join(dirname(sourcePath), "references", "guide.md"), "# Deadline update\n", "utf8");
  const deadlineMs = Date.now() + 400;
  setBeforeSkillPublishForTest(async () => {
    while (Date.now() <= deadlineMs + 20) await new Promise((resolveDelay) => setTimeout(resolveDelay, 10));
  });
  try {
    await assert.rejects(() => reg.installForAgents("pi", sourcePath, project, deadlineMs), (error: unknown) => {
      assert.ok(error instanceof SkillInstallerError);
      assert.equal(error.code, "E_EXECUTION_TIMEOUT");
      return true;
    });
  } finally {
    setBeforeSkillPublishForTest(undefined);
  }
  assert.equal(await readFile(join(targetRoot, "SKILL.md"), "utf8"), priorSkill);
  assert.equal(await readFile(join(targetRoot, "references", "guide.md"), "utf8"), "# Runtime guide\n");
  await assert.rejects(access(join(workspace, "previous")));

  await rm(dirname(dirname(sourcePath)), { recursive: true });
  await rm(project, { recursive: true });
});

test("init canonicalizes junction aliases before acquiring the cross-process lock", async () => {
  const project = await makeUnityProject();
  const aliasParent = await mkdtemp(join(tmpdir(), "skill-alias-parent-"));
  const alias = join(aliasParent, "UnityProjectAlias");
  await symlink(project, alias, "junction");
  let active = 0;
  let maximumActive = 0;
  setBeforeSkillPublishForTest(async () => {
    active += 1;
    maximumActive = Math.max(maximumActive, active);
    await new Promise((resolveDelay) => setTimeout(resolveDelay, 75));
    active -= 1;
  });
  let results;
  try {
    results = await Promise.all([
      handleInit({ project, agent: "pi", "skill-only": true, "timeout-ms": "30000" }, {} as Runtime, "req_canonical_real"),
      handleInit({ project: alias, agent: "pi", "skill-only": true, "timeout-ms": "30000" }, {} as Runtime, "req_canonical_alias"),
    ]);
  } finally {
    setBeforeSkillPublishForTest(undefined);
  }
  assert.equal(results[0].ok, true);
  assert.equal(results[1].ok, true);
  assert.equal(maximumActive, 1);
  const installedReference = await readFile(join(project, ".pi", "skills", "unitycli", "references", "custom-tools.md"), "utf8");
  assert.ok(installedReference.length > 0);

  await rm(alias, { recursive: true });
  await rm(aliasParent, { recursive: true });
  await rm(project, { recursive: true });
});

// ─── Unknown agent ───────────────────────────────────────────────────────────

test("init check through a junction alias serializes with canonical install", async () => {
  const project = await makeUnityProject();
  const aliasParent = await mkdtemp(join(tmpdir(), "skill-check-alias-parent-"));
  const alias = join(aliasParent, "UnityProjectAlias");
  await symlink(project, alias, "junction");
  await handleInit({ project, agent: "pi", "skill-only": true, "timeout-ms": "30000" }, {} as Runtime, "req_check_alias_seed");
  const targetReference = join(project, ".pi", "skills", "unitycli", "references", "custom-tools.md");
  await writeFile(targetReference, "# Drifted\n", "utf8");

  let enterPublish!: () => void;
  let releasePublish!: () => void;
  const publishEntered = new Promise<void>((resolveEntered) => { enterPublish = resolveEntered; });
  const publishReleased = new Promise<void>((resolveReleased) => { releasePublish = resolveReleased; });
  setBeforeSkillPublishForTest(async () => {
    enterPublish();
    await publishReleased;
  });
  const installPromise = handleInit({ project, agent: "pi", "skill-only": true, "timeout-ms": "30000" }, {} as Runtime, "req_check_alias_install");
  let checkCompleted = false;
  let checkPromise: Promise<Awaited<ReturnType<typeof handleInit>>> | undefined;
  try {
    await publishEntered;
    checkPromise = handleInit({ project: alias, agent: "pi", "skill-only": true, check: true, "timeout-ms": "30000" }, {} as Runtime, "req_check_alias_read")
      .finally(() => { checkCompleted = true; });
    await new Promise((resolveDelay) => setTimeout(resolveDelay, 50));
    assert.equal(checkCompleted, false);
  } finally {
    releasePublish();
    setBeforeSkillPublishForTest(undefined);
  }

  const installResult = await installPromise;
  const checkResult = await checkPromise!;
  assert.equal(installResult.ok, true);
  assert.equal(checkResult.ok, true);
  if (!checkResult.ok) throw new Error("Expected successful canonical alias check.");
  const skills = (checkResult.data as { skills: SkillCheckResult[] }).skills;
  assert.equal(skills[0].status, "installed");

  const autoDetected = await handleInit({ project: alias, "skill-only": true, check: true, "timeout-ms": "30000" }, {} as Runtime, "req_check_alias_auto");
  assert.equal(autoDetected.ok, true);
  if (!autoDetected.ok) throw new Error("Expected successful canonical alias auto-detection.");
  assert.equal((autoDetected.data as { skills: SkillCheckResult[] }).skills[0].agent, "pi");

  await rm(alias, { recursive: true });
  await rm(aliasParent, { recursive: true });
  await rm(project, { recursive: true });
});

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

test("findSkillSourcePath resolves skills/unitycli/SKILL.md from package root", async () => {
  // findSkillSourcePath should find the actual skill file from the package
  const sourcePath = await findSkillSourcePath();
  assert.ok(sourcePath.replaceAll("\\", "/").endsWith("/skills/unitycli/SKILL.md"));

  // Verify the file is readable
  const content = await readFile(sourcePath, "utf8");
  assert.ok(content.length > 0);
});
