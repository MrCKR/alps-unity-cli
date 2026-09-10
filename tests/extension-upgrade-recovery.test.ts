/**
 * 扩展升级自愈回归测试：覆盖 init 包发布后的投影重铺与编译产物标记校验。
 */

import assert from "node:assert/strict";
import test from "node:test";
import { mkdir, mkdtemp, readdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { URP_MANIFEST, VFX_GRAPH_MANIFEST } from "../src/extensions/index.js";
import {
  extensionInstallManifestPath,
  extensionInstallRoot,
  installExtensionBundle,
  inspectCompiledExtensionMarkers,
  readExtensionInstallManifest,
  refreshInstalledExtensionProjections,
} from "../src/extensions/installer.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

/** 创建最小 Unity project root 并安装 vfx-graph 扩展投影。 */
async function createProjectWithVfxGraph(): Promise<string> {
  const projectDir = await mkdtemp(join(tmpdir(), "unity-cli-ext-recovery-"));
  await mkdir(join(projectDir, "Packages"), { recursive: true });
  await writeFile(join(projectDir, "Packages", "manifest.json"), JSON.stringify({ dependencies: {} }));
  await writeFile(join(projectDir, "Packages", "packages-lock.json"), JSON.stringify({ dependencies: {} }));
  await installExtensionBundle(projectDir, VFX_GRAPH_MANIFEST);
  return projectDir;
}

/** 把安装 manifest 中指定扩展的 sourceHash 改写为旧值，模拟旧版本 CLI 留下的过期安装记录。 */
async function tamperInstallManifestSourceHash(projectDir: string, extensionId: string, sourceHash: string): Promise<void> {
  const manifest = await readExtensionInstallManifest(projectDir);
  const entry = manifest.extensions.find((item) => item.extensionId === extensionId);
  assert.ok(entry, `install manifest must contain ${extensionId}`);
  entry.sourceHash = sourceHash;
  await writeFile(extensionInstallManifestPath(projectDir), JSON.stringify(manifest));
}

test("init upgrade refresh reinstalls stale extension projections and restores the manifest hash", async () => {
  const projectDir = await createProjectWithVfxGraph();
  try {
    const entry = (await readExtensionInstallManifest(projectDir)).extensions.find((item) => item.extensionId === "vfx-graph")!;
    const projectedFile = join(extensionInstallRoot(projectDir, "vfx-graph"), entry.files[0]!.path);
    const originalProjection = await readFile(projectedFile, "utf8");

    // 模拟旧版本安装：manifest 记录过期 hash，投影文件内容也停留在旧 bundle 内容。
    await tamperInstallManifestSourceHash(projectDir, "vfx-graph", "0".repeat(64));
    await writeFile(projectedFile, `${originalProjection}\n// stale projection from an older CLI version\n`);

    const result = await refreshInstalledExtensionProjections(projectDir);
    assert.deepEqual(result.repaired.map((item) => item.extensionId), ["vfx-graph"]);
    assert.deepEqual(result.upToDate, []);
    assert.deepEqual(result.failures, []);

    // 投影文件重铺为当前 bundle 内容，manifest hash 恢复为当前 source hash。
    const sourceFile = join(process.cwd(), "extensions", "vfx-graph", ...entry.files[0]!.path.split("/"));
    assert.equal(await readFile(projectedFile, "utf8"), await readFile(sourceFile, "utf8"));
    const refreshedEntry = (await readExtensionInstallManifest(projectDir)).extensions.find((item) => item.extensionId === "vfx-graph")!;
    assert.equal(refreshedEntry.sourceHash, entry.sourceHash);
  } finally {
    await rm(projectDir, { recursive: true, force: true });
  }
});

test("init upgrade refresh reports up-to-date projections without rewriting them", async () => {
  const projectDir = await createProjectWithVfxGraph();
  try {
    const entry = (await readExtensionInstallManifest(projectDir)).extensions.find((item) => item.extensionId === "vfx-graph")!;
    const projectedFile = join(extensionInstallRoot(projectDir, "vfx-graph"), entry.files[0]!.path);
    const before = await readFile(projectedFile, "utf8");

    const result = await refreshInstalledExtensionProjections(projectDir);
    assert.deepEqual(result.repaired, []);
    assert.deepEqual(result.upToDate, ["vfx-graph"]);
    assert.deepEqual(result.failures, []);
    assert.equal(await readFile(projectedFile, "utf8"), before);
  } finally {
    await rm(projectDir, { recursive: true, force: true });
  }
});

test("init upgrade refresh keeps unknown installed extensions as explicit failures", async () => {
  const projectDir = await mkdtemp(join(tmpdir(), "unity-cli-ext-recovery-"));
  try {
    await mkdir(join(projectDir, "Packages", "com.alpsckr.unitycli"), { recursive: true });
    await writeFile(extensionInstallManifestPath(projectDir), JSON.stringify({
      version: 1,
      extensions: [{
        extensionId: "future-extension",
        version: "9.9.9",
        installedAt: new Date().toISOString(),
        sourceHash: "0".repeat(64),
        files: [],
        requiresUnityPackages: [],
        copyRoot: "extension-directory",
        layout: "directory",
        installMode: "copy-if-package-present",
      }],
    }));

    const result = await refreshInstalledExtensionProjections(projectDir);
    assert.deepEqual(result.repaired, []);
    assert.equal(result.failures.length, 1);
    assert.equal(result.failures[0]!.extensionId, "future-extension");
    assert.equal(result.failures[0]!.code, "E_EXTENSION_UNKNOWN_INSTALLED");
    assert.match(result.failures[0]!.nextActions[0] ?? "", /extensions remove future-extension/);
  } finally {
    await rm(projectDir, { recursive: true, force: true });
  }
});

test("init upgrade refresh aborts per extension once the immutable deadline elapsed", async () => {
  const projectDir = await createProjectWithVfxGraph();
  try {
    const entry = (await readExtensionInstallManifest(projectDir)).extensions.find((item) => item.extensionId === "vfx-graph")!;
    const projectedFile = join(extensionInstallRoot(projectDir, "vfx-graph"), entry.files[0]!.path);
    await tamperInstallManifestSourceHash(projectDir, "vfx-graph", "0".repeat(64));

    const result = await refreshInstalledExtensionProjections(projectDir, Date.now() - 1);
    assert.deepEqual(result.repaired, []);
    assert.equal(result.failures.length, 1);
    assert.equal(result.failures[0]!.code, "E_EXTENSION_REPAIR_DEADLINE");
    // 投影保持原状：deadline 用尽时不得发生半刷新。
    assert.ok((await readFile(projectedFile, "utf8")).length > 0);
    const untouched = (await readExtensionInstallManifest(projectDir)).extensions.find((item) => item.extensionId === "vfx-graph")!;
    assert.equal(untouched.sourceHash, "0".repeat(64));
  } finally {
    await rm(projectDir, { recursive: true, force: true });
  }
});

test("compiled marker inspection detects stale ScriptAssemblies output without the exact marker", async () => {
  const projectDir = await createProjectWithVfxGraph();
  try {
    // 投影根即 Unity 包根：asmdef 位于 Extensions/vfx-graph/ 顶层。
    const installRoot = extensionInstallRoot(projectDir, "vfx-graph");
    const asmdefFileName = (await readdir(installRoot)).find((name) => name.endsWith(".asmdef"))!;
    const asmdefName = (JSON.parse(await readFile(join(installRoot, asmdefFileName), "utf8")) as { name: string }).name;

    // 旧编译产物：不含 UnityCliExtensionAssembly 精确标记。
    const assembliesDir = join(projectDir, "Library", "ScriptAssemblies");
    await mkdir(assembliesDir, { recursive: true });
    await writeFile(join(assembliesDir, `${asmdefName}.dll`), Buffer.from("stale assembly bytes without the marker", "utf8"));

    let checks = await inspectCompiledExtensionMarkers(projectDir, ["vfx-graph"]);
    assert.equal(checks.length, 1);
    assert.equal(checks[0]!.compiledAssemblyFound, true);
    assert.equal(checks[0]!.markerPresent, false);
    assert.equal(checks[0]!.assemblyName, asmdefName);

    // 重编译后的产物包含精确标记。
    await writeFile(join(assembliesDir, `${asmdefName}.dll`), Buffer.concat([
      Buffer.from([0, 1, 2, 0]),
      Buffer.from("UnityCliExtensionAssembly", "utf8"),
      Buffer.from([9, 8, 7]),
    ]));
    checks = await inspectCompiledExtensionMarkers(projectDir, ["vfx-graph"]);
    assert.equal(checks[0]!.markerPresent, true);

    // 编译产物缺失时显式报告 found=false，而不是静默 pass。
    await rm(join(assembliesDir, `${asmdefName}.dll`));
    checks = await inspectCompiledExtensionMarkers(projectDir, ["vfx-graph"]);
    assert.equal(checks[0]!.compiledAssemblyFound, false);
  } finally {
    await rm(projectDir, { recursive: true, force: true });
  }
});

test("compiled marker inspection covers the urp projection installed from the same CLI bundle", async () => {
  const projectDir = await mkdtemp(join(tmpdir(), "unity-cli-ext-recovery-"));
  try {
    await mkdir(join(projectDir, "Packages"), { recursive: true });
    await writeFile(join(projectDir, "Packages", "manifest.json"), JSON.stringify({ dependencies: {} }));
    // urp 扩展要求 URP 包已解析；installExtensionBundle 以 packages-lock.json 为准。
    await writeFile(join(projectDir, "Packages", "packages-lock.json"), JSON.stringify({
      dependencies: { "com.unity.render-pipelines.universal": { version: "14.0.11", depth: 0, source: "registry" } },
    }));
    await installExtensionBundle(projectDir, URP_MANIFEST);

    const checks = await inspectCompiledExtensionMarkers(projectDir, ["urp"]);
    assert.equal(checks.length, 1);
    assert.equal(checks[0]!.compiledAssemblyFound, false);
    assert.match(checks[0]!.assemblyName ?? "", /Urp/i);
  } finally {
    await rm(projectDir, { recursive: true, force: true });
  }
});
