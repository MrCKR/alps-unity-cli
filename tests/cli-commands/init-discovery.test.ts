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

function projects(data: Record<string, unknown>): Array<{ path: string; unityVersion: string; relation: string; depth: number; bridge: { status: string }; editor: { status: string; processCount?: number } }> {
  return data.unityProjects as Array<{ path: string; unityVersion: string; relation: string; depth: number; bridge: { status: string }; editor: { status: string; processCount?: number } }>;
}

const stoppedRuntime = { findUnityProjectProcesses: async () => [] };

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

    const descendantData = await discoverInitEnvironment(workspace, root, stoppedRuntime);
    assert.equal(descendantData.cwd, resolve(workspace));
    assert.equal(descendantData.repositoryRoot, resolve(root));
    assert.deepEqual(projects(descendantData).map((item) => ({ path: item.path, version: item.unityVersion, relation: item.relation, depth: item.depth })), [
      { path: resolve(direct), version: "2022.3.61f1", relation: "descendant", depth: 1 },
      { path: resolve(nested), version: "2023.2.20f1", relation: "descendant", depth: 2 },
    ]);

    const cwdData = await discoverInitEnvironment(direct, root, stoppedRuntime);
    assert.deepEqual(projects(cwdData).map((item) => ({ path: item.path, relation: item.relation, depth: item.depth, bridge: item.bridge.status, editor: item.editor.status })), [
      { path: resolve(direct), relation: "cwd", depth: 0, bridge: "missing", editor: "stopped" },
    ]);
    assert.equal(cwdData.scan, undefined);
    const child = join(ancestor, "Tools", "Sub");
    await mkdir(child, { recursive: true });
    const ancestorData = await discoverInitEnvironment(child, root, stoppedRuntime);
    assert.ok(projects(ancestorData).some((item) => item.path === resolve(ancestor) && item.relation === "ancestor" && item.depth === 2));
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("discover reports one close-Editor decision for the exact running current project", async () => {
  const root = await mkdtemp(join(tmpdir(), "unity-discover-running-"));
  try {
    await makeUnityProject(root);
    const data = await discoverInitEnvironment(root, root, {
      findUnityProjectProcesses: async (_instanceId, projectPath) => [{ processId: 42, processStartedAt: "2026-08-31T00:00:00.000Z", executablePath: "C:\\Unity\\Unity.exe", argv: ["Unity.exe", "-projectPath", projectPath], projectPath }],
      inspectPackageBridge: async (projectPath) => ({ status: "drifted", projectPath, bridgePath: join(projectPath, "Packages", "com.alpsckr.unitycli"), sourceHash: "a".repeat(64), installedVersion: "0.4.0", availableVersion: "0.4.1", reasonCode: "PACKAGE_INSTALL_DRIFTED" }),
    });
    const project = projects(data)[0];
    assert.equal(project.editor.status, "running");
    assert.equal(project.editor.processCount, 1);
    assert.equal(project.bridge.status, "drifted");
    assert.deepEqual((data.requiredDecisions as Array<{ id: string; projectPath: string }>), [{
      id: "closeEditor",
      question: "May UnityCLI normally close the exact target Unity project before Bridge installation?",
      projectPath: resolve(root),
      multiple: false,
    }]);
    assert.equal(data.scan, undefined);
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

    const data = await discoverInitEnvironment(workspace, root, stoppedRuntime);
    assert.deepEqual(projects(data), []);
    assert.equal((data.scan as { truncated: boolean }).truncated, false);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("discover returns one recommended workspace Skill target without enumerating every host and scope", async () => {
  const root = await mkdtemp(join(tmpdir(), "unity-discover-skills-"));
  try {
    const workspace = join(root, "workspace");
    const userHome = join(root, "home");
    await mkdir(join(workspace, ".git"), { recursive: true });
    await mkdir(userHome);
    const sourcePath = await findSkillSourcePath();
    await agentSkillInstaller.installForAgents([".agents"], sourcePath, workspace);
    const before = await snapshot(root);

    const data = await discoverInitEnvironment(workspace, userHome, stoppedRuntime);
    const skill = data.skill as { workspace: string; recommendedTarget: string; explicitTargetSupported: boolean; status: string; hash?: string };
    assert.equal(skill.workspace, resolve(workspace));
    assert.equal(skill.recommendedTarget, join(resolve(workspace), ".agents", "skills", "unitycli"));
    assert.equal(skill.explicitTargetSupported, true);
    assert.equal(skill.status, "installed");
    assert.match(skill.hash!, /^[a-f0-9]{64}$/);
    assert.deepEqual(projects(data), []);
    assert.deepEqual(data.requiredDecisions, []);
    assert.equal("skillCopies" in data, false);
    assert.equal("shadowingRisks" in data, false);
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
    const zero = await discoverInitEnvironment(empty, root, stoppedRuntime);
    assert.deepEqual(projects(zero), []);
    assert.equal((zero.skill as { status: string }).status, "missing");
    assert.deepEqual(zero.requiredDecisions, []);

    const large = join(root, "large");
    await mkdir(large);
    await Promise.all(Array.from({ length: 2005 }, (_, index) => mkdir(join(large, `dir-${String(index).padStart(4, "0")}`))));
    const bounded = await discoverInitEnvironment(large, root, stoppedRuntime);
    assert.deepEqual(projects(bounded), []);
    assert.equal((bounded.scan as { truncated: boolean }).truncated, true);
    assert.ok((bounded.scan as { scannedDirectories: number }).scannedDirectories <= 2000);
    assert.deepEqual(bounded.scan, { scannedDirectories: 2000, truncated: true, maxDescendantDepth: 4, maxScannedDirectories: 2000, maxUnityProjects: 100 });
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
