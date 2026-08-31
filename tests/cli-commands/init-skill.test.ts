import assert from "node:assert/strict";
import { access, mkdir, mkdtemp, readFile, readdir, rm, symlink, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import test from "node:test";
import { execute } from "../../src/cli.js";
import { resolveInitSkillTarget } from "../../src/cli-commands/init-skill.js";
import { findSkillSourcePath } from "../../src/cli-commands/skill-installer.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

async function entries(path: string): Promise<string[]> { try { return (await readdir(path)).sort(); } catch { return []; } }

test("Skill preset target model maps four agent/scope destinations", async () => {
  const root = await mkdtemp(join(tmpdir(), "unity-skill-targets-"));
  try {
    const workspace = join(root, "workspace");
    const userHome = join(root, "home");
    await mkdir(workspace);
    await mkdir(userHome);
    assert.deepEqual(resolveInitSkillTarget(".agents", "workspace", workspace, userHome), { basePath: resolve(workspace), targetPath: join(resolve(workspace), ".agents", "skills", "unitycli") });
    assert.deepEqual(resolveInitSkillTarget("claude", "workspace", workspace, userHome), { basePath: resolve(workspace), targetPath: join(resolve(workspace), ".claude", "skills", "unitycli") });
    assert.deepEqual(resolveInitSkillTarget(".agents", "user", undefined, userHome), { basePath: resolve(userHome), targetPath: join(resolve(userHome), ".agents", "skills", "unitycli") });
    assert.deepEqual(resolveInitSkillTarget("claude", "user", undefined, userHome), { basePath: resolve(userHome), targetPath: join(resolve(userHome), ".claude", "skills", "unitycli") });
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Skill checks support a Unity project parent workspace and zero-Unity user scope without writes", async () => {
  const root = await mkdtemp(join(tmpdir(), "unity-skill-scope-check-"));
  try {
    const workspace = join(root, "workspace");
    const unityProject = join(workspace, "client", "UnityProject");
    const userHome = join(root, "home");
    await mkdir(unityProject, { recursive: true });
    await mkdir(userHome);
    const runtime = { dataDir: join(root, "data"), userHome } as never;
    const workspaceCheck = await execute(["init", "skill", "--agent", ".agents", "--scope", "workspace", "--workspace", workspace, "--check"], runtime);
    assert.equal(workspaceCheck.ok, true, JSON.stringify(workspaceCheck));
    if (!workspaceCheck.ok) throw new Error("Expected workspace Skill check.");
    assert.equal((workspaceCheck.data as any).skill.targetPath, join(resolve(workspace), ".agents", "skills", "unitycli"));
    assert.equal((workspaceCheck.data as any).skill.status, "missing");
    assert.deepEqual(await entries(workspace), ["client"]);

    const userCheck = await execute(["init", "skill", "--agent", "claude", "--scope", "user", "--check"], runtime);
    assert.equal(userCheck.ok, true, JSON.stringify(userCheck));
    if (!userCheck.ok) throw new Error("Expected user Skill check.");
    assert.equal((userCheck.data as any).skill.targetPath, join(resolve(userHome), ".claude", "skills", "unitycli"));
    assert.equal((userCheck.data as any).skill.status, "missing");
    assert.deepEqual(await entries(userHome), []);
    assert.deepEqual(await entries(root), ["home", "workspace"]);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("preset Skill installation covers four targets, idempotence, checks, and bounded transaction cleanup", async () => {
  const root = await mkdtemp(join(tmpdir(), "unity-skill-install-matrix-"));
  try {
    const workspace = join(root, "workspace");
    const userHome = join(root, "home");
    const dataDir = join(root, "data");
    await mkdir(workspace);
    await mkdir(userHome);
    const runtime = { dataDir, userHome } as never;
    const cases = [
      { agent: ".agents", scope: "workspace", args: ["--workspace", workspace], target: join(workspace, ".agents", "skills", "unitycli") },
      { agent: "claude", scope: "workspace", args: ["--workspace", workspace], target: join(workspace, ".claude", "skills", "unitycli") },
      { agent: ".agents", scope: "user", args: [], target: join(userHome, ".agents", "skills", "unitycli") },
      { agent: "claude", scope: "user", args: [], target: join(userHome, ".claude", "skills", "unitycli") },
    ];
    for (const item of cases) {
      const argv = ["init", "skill", "--agent", item.agent, "--scope", item.scope, ...item.args];
      const installed = await execute(argv, runtime);
      assert.equal(installed.ok, true, JSON.stringify(installed));
      if (!installed.ok) throw new Error("Expected Skill installation.");
      assert.equal((installed.data as any).changes.length, 1);
      assert.equal((installed.data as any).changes[0].status, "installed");
      assert.equal((installed.data as any).skill.status, "installed");
      await access(join(item.target, "SKILL.md"));

      const checkHashBefore = (installed.data as any).skill.hash;
      const checked = await execute([...argv, "--check"], runtime);
      assert.equal(checked.ok, true, JSON.stringify(checked));
      if (!checked.ok) throw new Error("Expected Skill check.");
      assert.equal((checked.data as any).skill.status, "installed");
      assert.equal((checked.data as any).skill.hash, checkHashBefore);
      assert.deepEqual((checked.data as any).nextActions, []);

      const skipped = await execute(argv, runtime);
      assert.equal(skipped.ok, true, JSON.stringify(skipped));
      if (!skipped.ok) throw new Error("Expected idempotent Skill install.");
      assert.equal((skipped.data as any).changes[0].status, "skipped");
    }
    await assert.rejects(() => access(join(workspace, ".agents", "skills", ".unitycli-install")), { code: "ENOENT" });
    await assert.rejects(() => access(join(workspace, ".claude", "skills", ".unitycli-install")), { code: "ENOENT" });
    await assert.rejects(() => access(join(dataDir, ".temp", "skill-install", "agents")), { code: "ENOENT" });
    await assert.rejects(() => access(join(dataDir, ".temp", "skill-install", "claude")), { code: "ENOENT" });
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("explicit Skill target installs and updates a Pi workspace with exact readback", async () => {
  const root = await mkdtemp(join(tmpdir(), "unity-skill-explicit-"));
  try {
    const target = join(root, ".pi", "skills", "unitycli");
    const runtime = { dataDir: join(root, "data"), userHome: join(root, "home") } as never;
    const argv = ["init", "skill", "--target", target];
    const installed = await execute(argv, runtime);
    assert.equal(installed.ok, true, JSON.stringify(installed));
    if (!installed.ok) throw new Error("Expected explicit Skill installation.");
    assert.equal((installed.data as any).target, resolve(target));
    assert.equal((installed.data as any).changes[0].status, "installed");
    assert.equal((installed.data as any).skill.status, "installed");
    await access(join(target, "SKILL.md"));

    const checked = await execute([...argv, "--check"], runtime);
    assert.equal(checked.ok, true, JSON.stringify(checked));
    assert.equal((checked.data as any).skill.status, "installed");
    assert.deepEqual((checked.data as any).nextActions, []);

    await writeFile(join(target, "references", "custom-tools.md"), "# drifted\n", "utf8");
    assert.equal(((await execute([...argv, "--check"], runtime)) as any).data.skill.status, "drifted");
    const updated = await execute(argv, runtime);
    assert.equal(updated.ok, true, JSON.stringify(updated));
    assert.equal((updated as any).data.changes[0].status, "updated");
    assert.equal(((await execute([...argv, "--check"], runtime)) as any).data.skill.status, "installed");
    await assert.rejects(() => access(join(root, ".pi", "skills", ".unitycli-install")), { code: "ENOENT" });
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("explicit Skill target preserves a non-owned directory and rejects bundled source overlap", async () => {
  const root = await mkdtemp(join(tmpdir(), "unity-skill-ownership-"));
  try {
    const target = join(root, ".pi", "skills", "unitycli");
    await mkdir(target, { recursive: true });
    const sentinel = join(target, "unrelated.txt");
    await writeFile(sentinel, "keep\n", "utf8");
    const runtime = { dataDir: join(root, "data"), userHome: join(root, "home") } as never;
    const unowned = await execute(["init", "skill", "--target", target], runtime);
    assert.equal(unowned.ok, false, JSON.stringify(unowned));
    assert.equal(unowned.error.code, "E_SKILL_TARGET_OWNERSHIP_UNPROVEN");
    assert.equal(await readFile(sentinel, "utf8"), "keep\n");
    assert.deepEqual(await entries(target), ["unrelated.txt"]);

    const sourceTarget = dirname(await findSkillSourcePath());
    const sourceBefore = await readFile(join(sourceTarget, "SKILL.md"), "utf8");
    const overlap = await execute(["init", "skill", "--target", sourceTarget], runtime);
    assert.equal(overlap.ok, false, JSON.stringify(overlap));
    assert.equal(overlap.error.code, "E_SKILL_TARGET_OVERLAPS_SOURCE");
    assert.equal(await readFile(join(sourceTarget, "SKILL.md"), "utf8"), sourceBefore);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Skill effect repairs drift and damage while preserving legal FeedbackMode", async () => {
  const root = await mkdtemp(join(tmpdir(), "unity-skill-repair-"));
  try {
    const workspace = join(root, "workspace");
    await mkdir(workspace);
    const runtime = { dataDir: join(root, "data"), userHome: join(root, "home") } as never;
    const genericArgs = ["init", "skill", "--agent", ".agents", "--scope", "workspace", "--workspace", workspace];
    const claudeArgs = ["init", "skill", "--agent", "claude", "--scope", "workspace", "--workspace", workspace];
    assert.equal((await execute(genericArgs, runtime)).ok, true);
    assert.equal((await execute(claudeArgs, runtime)).ok, true);

    const genericRoot = join(workspace, ".agents", "skills", "unitycli");
    const genericSkill = join(genericRoot, "SKILL.md");
    await writeFile(genericSkill, (await readFile(genericSkill, "utf8")).replace("FeedbackMode: off", "FeedbackMode: on"), "utf8");
    await writeFile(join(genericRoot, "references", "custom-tools.md"), "# drifted\n", "utf8");
    const drifted = await execute([...genericArgs, "--check"], runtime);
    assert.equal(drifted.ok, true);
    if (!drifted.ok) throw new Error("Expected drifted check.");
    assert.equal((drifted.data as any).skill.status, "drifted");
    const updated = await execute(genericArgs, runtime);
    assert.equal(updated.ok, true, JSON.stringify(updated));
    if (!updated.ok) throw new Error("Expected drift repair.");
    assert.equal((updated.data as any).changes[0].status, "updated");
    assert.match(await readFile(genericSkill, "utf8"), /^FeedbackMode: on$/m);
    assert.equal((await execute([...genericArgs, "--check"], runtime) as any).data.skill.status, "installed");

    const claudeSkill = join(workspace, ".claude", "skills", "unitycli", "SKILL.md");
    await writeFile(claudeSkill, "# damaged\n\nFeedbackMode: invalid\n", "utf8");
    const damaged = await execute([...claudeArgs, "--check"], runtime);
    assert.equal(damaged.ok, true);
    if (!damaged.ok) throw new Error("Expected damaged check.");
    assert.equal((damaged.data as any).skill.status, "damaged");
    const repaired = await execute(claudeArgs, runtime);
    assert.equal(repaired.ok, true, JSON.stringify(repaired));
    if (!repaired.ok) throw new Error("Expected damage repair.");
    assert.equal((repaired.data as any).changes[0].status, "updated");
    assert.equal((repaired.data as any).skill.status, "installed");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Skill target validation rejects removed agents, illegal combinations, and linked roots before writes", async () => {
  const root = await mkdtemp(join(tmpdir(), "unity-skill-target-errors-"));
  try {
    const workspace = join(root, "workspace");
    const userHome = join(root, "home");
    const external = join(root, "external");
    await mkdir(workspace);
    await mkdir(userHome);
    await mkdir(external);
    const runtime = { dataDir: join(root, "data"), userHome } as never;
    const cases: Array<{ argv: string[]; code: string }> = [
      { argv: ["init", "skill", "--agent", "pi", "--scope", "workspace", "--workspace", workspace], code: "UNKNOWN_AGENT_KIND" },
      { argv: ["init", "skill", "--agent", "unknown", "--scope", "user"], code: "UNKNOWN_AGENT_KIND" },
      { argv: ["init", "skill", "--agent", ".agents", "--scope", "invalid"], code: "E_INIT_SCOPE_INVALID" },
      { argv: ["init", "skill", "--agent", ".agents", "--scope", "workspace"], code: "E_INIT_WORKSPACE_REQUIRED" },
      { argv: ["init", "skill", "--agent", ".agents", "--scope", "user", "--workspace", workspace], code: "E_INIT_WORKSPACE_FORBIDDEN" },
      { argv: ["init", "skill", "--agent", ".agents", "--scope", "workspace", "--workspace", join(root, "missing")], code: "E_SKILL_ROOT_INVALID" },
      { argv: ["init", "skill", "--target", join(workspace, ".pi", "skills")], code: "E_INIT_SKILL_TARGET_INVALID" },
      { argv: ["init", "skill", "--target", join(workspace, ".unitycli-install", "unitycli")], code: "E_INIT_SKILL_TARGET_INVALID" },
      { argv: ["init", "skill", "--target", join(workspace, ".pi", "skills", "unitycli"), "--agent", ".agents", "--scope", "workspace", "--workspace", workspace], code: "E_INIT_SKILL_TARGET_CONFLICT" },
    ];
    for (const item of cases) {
      const result = await execute(item.argv, runtime);
      assert.equal(result.ok, false, item.argv.join(" "));
      assert.equal(result.error.code, item.code, item.argv.join(" "));
    }

    const linkedWorkspace = join(root, "linked-workspace");
    await symlink(external, linkedWorkspace, "junction");
    const linkedRoot = await execute(["init", "skill", "--agent", ".agents", "--scope", "workspace", "--workspace", linkedWorkspace], runtime);
    assert.equal(linkedRoot.ok, false);
    assert.equal(linkedRoot.error.code, "E_SKILL_ROOT_INVALID");

    await mkdir(join(workspace, ".agents"));
    const linkedSkills = join(workspace, ".agents", "skills");
    await symlink(external, linkedSkills, "junction");
    const linkedTarget = await execute(["init", "skill", "--agent", ".agents", "--scope", "workspace", "--workspace", workspace], runtime);
    assert.equal(linkedTarget.ok, false);
    assert.equal(linkedTarget.error.code, "E_SKILL_TARGET_UNSAFE");
    const explicitLinkedTarget = await execute(["init", "skill", "--target", join(linkedSkills, "unitycli")], runtime);
    assert.equal(explicitLinkedTarget.ok, false);
    assert.equal(explicitLinkedTarget.error.code, "E_SKILL_TARGET_UNSAFE");
    assert.deepEqual(await entries(external), []);
    assert.deepEqual(await entries(userHome), []);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
