import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, readdir, rm, symlink, writeFile } from "node:fs/promises";
import { createHash } from "node:crypto";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";
import { discoverInitEnvironment } from "../../src/cli-commands/init-discovery.js";
import { agentSkillInstaller, findSkillSourcePath } from "../../src/cli-commands/skill-installer.js";

async function makeUnityProject(path: string, version = "2022.3.61f1"): Promise<void> {
  await mkdir(join(path, "Assets"), { recursive: true });
  await mkdir(join(path, "Packages"), { recursive: true });
  await mkdir(join(path, "ProjectSettings"), { recursive: true });
  await writeFile(join(path, "Packages", "manifest.json"), "{\"dependencies\":{}}\n", "utf8");
  await writeFile(join(path, "ProjectSettings", "ProjectVersion.txt"), `m_EditorVersion: ${version}\n`, "utf8");
}

function projects(data: Record<string, unknown>): Array<{ path: string; unityVersion: string; relation: string; depth: number }> {
  return data.unityProjects as Array<{ path: string; unityVersion: string; relation: string; depth: number }>;
}

async function snapshot(root: string, relative = ""): Promise<Array<{ path: string; sha256: string }>> {
  const result: Array<{ path: string; sha256: string }> = [];
  for (const entry of await readdir(join(root, relative), { withFileTypes: true })) {
    const child = relative ? join(relative, entry.name) : entry.name;
    if (entry.isDirectory()) result.push(...await snapshot(root, child));
    else if (entry.isFile()) result.push({ path: child.replaceAll("\\", "/"), sha256: createHash("sha256").update(await readFile(join(root, child))).digest("hex") });
  }
  return result.sort((left, right) => left.path.localeCompare(right.path));
}

test("discover finds CWD, ancestor, and bounded descendant Unity projects with exact versions", async () => {
  const root = await mkdtemp(join(tmpdir(), "unity-discover-projects-"));
  try {
    await mkdir(join(root, ".git"));
    const workspace = join(root, "workspace");
    const direct = join(workspace, "GameA");
    const nested = join(workspace, "group", "GameB");
    const ancestor = join(root, "AncestorGame");
    await mkdir(workspace, { recursive: true });
    await Promise.all([makeUnityProject(direct, "2022.3.61f1"), makeUnityProject(nested, "2023.2.20f1"), makeUnityProject(ancestor, "6000.0.40f1")]);

    const descendantData = await discoverInitEnvironment(workspace, root);
    assert.equal(descendantData.cwd, resolve(workspace));
    assert.equal(descendantData.repositoryRoot, resolve(root));
    assert.deepEqual(projects(descendantData).map((item) => ({ path: item.path, version: item.unityVersion, relation: item.relation, depth: item.depth })), [
      { path: resolve(direct), version: "2022.3.61f1", relation: "descendant", depth: 1 },
      { path: resolve(nested), version: "2023.2.20f1", relation: "descendant", depth: 2 },
    ]);

    const cwdData = await discoverInitEnvironment(direct, root);
    assert.ok(projects(cwdData).some((item) => item.path === resolve(direct) && item.relation === "cwd" && item.depth === 0));
    const child = join(ancestor, "Tools", "Sub");
    await mkdir(child, { recursive: true });
    const ancestorData = await discoverInitEnvironment(child, root);
    assert.ok(projects(ancestorData).some((item) => item.path === resolve(ancestor) && item.relation === "ancestor" && item.depth === 2));
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("discover rejects partial markers and skips ignored, linked, and over-depth projects", async () => {
  const root = await mkdtemp(join(tmpdir(), "unity-discover-boundaries-"));
  try {
    const workspace = join(root, "workspace");
    await mkdir(workspace, { recursive: true });
    await mkdir(join(workspace, "AssetsOnly", "Assets"), { recursive: true });
    await mkdir(join(workspace, "BadManifest", "Assets"), { recursive: true });
    await mkdir(join(workspace, "BadManifest", "Packages"), { recursive: true });
    await mkdir(join(workspace, "BadManifest", "ProjectSettings"), { recursive: true });
    await writeFile(join(workspace, "BadManifest", "Packages", "manifest.json"), "not-json", "utf8");
    await writeFile(join(workspace, "BadManifest", "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.61f1\n", "utf8");
    for (const ignored of [".git", "node_modules", "Library", "Temp", "Logs", "Obj"]) await makeUnityProject(join(workspace, ignored, "IgnoredGame"));
    await makeUnityProject(join(workspace, "a", "b", "c", "d", "TooDeep"));
    const external = join(root, "ExternalGame");
    await makeUnityProject(external);
    await symlink(external, join(workspace, "LinkedGame"), "junction");

    const data = await discoverInitEnvironment(workspace, root);
    assert.deepEqual(projects(data), []);
    assert.equal((data.scan as { truncated: boolean }).truncated, false);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("discover reports Skill copies, hashes, per-host shadowing, decisions, and zero writes", async () => {
  const root = await mkdtemp(join(tmpdir(), "unity-discover-skills-"));
  try {
    const workspace = join(root, "workspace");
    const userHome = join(root, "home");
    await mkdir(join(workspace, ".git"), { recursive: true });
    await mkdir(userHome);
    const sourcePath = await findSkillSourcePath();
    await agentSkillInstaller.installForAgents([".agents"], sourcePath, workspace);
    await agentSkillInstaller.installForAgents([".agents"], sourcePath, userHome);
    await agentSkillInstaller.installForAgents(["claude"], sourcePath, workspace);
    await agentSkillInstaller.installForAgents(["claude"], sourcePath, userHome);
    await writeFile(join(userHome, ".agents", "skills", "unitycli", "references", "custom-tools.md"), "# drifted\n", "utf8");
    await writeFile(join(workspace, ".claude", "skills", "unitycli", "SKILL.md"), "# damaged\n\nFeedbackMode: invalid\n", "utf8");
    const before = await snapshot(root);

    const data = await discoverInitEnvironment(workspace, userHome);
    const copies = data.skillCopies as Array<{ scope: string; agent: string; status: string; targetPath: string; sourceHash: string; hash?: string }>;
    assert.equal(copies.length, 4);
    const by = (scope: string, agent: string) => copies.find((copy) => copy.scope === scope && copy.agent === agent)!;
    assert.equal(by("workspace", ".agents").status, "installed");
    assert.match(by("workspace", ".agents").hash!, /^[a-f0-9]{64}$/);
    assert.equal(by("user", ".agents").status, "drifted");
    assert.match(by("user", ".agents").sourceHash, /^[a-f0-9]{64}$/);
    assert.equal(by("workspace", "claude").status, "damaged");
    assert.equal(by("user", "claude").status, "installed");
    assert.equal(by("workspace", ".agents").targetPath, join(resolve(workspace), ".agents", "skills", "unitycli"));
    assert.equal(by("user", "claude").targetPath, join(resolve(userHome), ".claude", "skills", "unitycli"));

    const risks = data.shadowingRisks as Array<{ agent: string; precedence: string[]; copies: unknown[] }>;
    assert.equal(risks.length, 2);
    const genericRisk = risks.find((risk) => risk.agent === ".agents")!;
    const claudeRisk = risks.find((risk) => risk.agent === "claude")!;
    assert.deepEqual(genericRisk.precedence, ["workspace", "user"]);
    assert.equal(genericRisk.copies.length, 2);
    assert.deepEqual(claudeRisk.precedence, ["user", "workspace"]);
    assert.equal(claudeRisk.copies.length, 2);
    assert.deepEqual(projects(data), []);
    assert.deepEqual((data.skillWorkspaces as Array<{ path: string }>).map((item) => item.path), [resolve(workspace)]);
    assert.deepEqual((data.requiredDecisions as Array<{ id: string }>).map((item) => item.id), ["bridgeProjects", "skillAgent", "skillScope", "skillWorkspace"]);
    assert.ok((data.nextActions as string[]).every((item) => /user|help/i.test(item)));
    assert.deepEqual(await snapshot(root), before);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("discover returns stable zero/multi results and caps a large directory queue", async () => {
  const root = await mkdtemp(join(tmpdir(), "unity-discover-limit-"));
  try {
    const empty = join(root, "empty");
    await mkdir(empty);
    const zero = await discoverInitEnvironment(empty, root);
    assert.deepEqual(projects(zero), []);
    assert.ok((zero.skillCopies as Array<{ status: string }>).every((copy) => copy.status === "missing"));
    assert.deepEqual(zero.shadowingRisks, []);

    const large = join(root, "large");
    await mkdir(large);
    await Promise.all(Array.from({ length: 2005 }, (_, index) => mkdir(join(large, `dir-${String(index).padStart(4, "0")}`))));
    const bounded = await discoverInitEnvironment(large, root);
    assert.deepEqual(projects(bounded), []);
    assert.equal((bounded.scan as { truncated: boolean }).truncated, true);
    assert.ok((bounded.scan as { scannedDirectories: number }).scannedDirectories <= 2000);
    assert.deepEqual(bounded.limits, { maxDescendantDepth: 4, maxScannedDirectories: 2000, maxUnityProjects: 100, maxWorkspaceCandidates: 20 });
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
