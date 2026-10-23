import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, rename, rm, stat, writeFile } from "node:fs/promises";
import { createHash } from "node:crypto";
import { join } from "node:path";
import { tmpdir } from "node:os";
import test from "node:test";
import { ensurePackageBridge, checkPackageBridge, deployPackageSource, hashPackageTree, inspectPackageAssemblyConflicts, inspectPackageBridge, inspectPackageDeploymentFiles, PackageManagerError, preparePackageBridgeInstall, removePackageBridge, restoreDeployedPackage, verifyPackageSourceIntegrity } from "../src/package-manager.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

async function makeUnityProject(): Promise<string> {
  const project = await mkdtemp(join(tmpdir(), "unity-pkg-test-"));
  const packages = join(project, "Packages");
  await mkdir(packages, { recursive: true });
  await writeFile(join(packages, "manifest.json"), JSON.stringify({
    dependencies: {}
  }, null, 2), "utf8");
  return project;
}

test("ensurePackageBridge copies unitypkg contents to Packages/com.alpsckr.unitycli", async () => {
  const project = await makeUnityProject();
  const result = await ensurePackageBridge(project);

  assert.equal(result.dependencyName, "com.alpsckr.unitycli");
  assert.equal(result.packageVersion, "0.10.2");
  assert.equal(result.installMode, "copy");
  assert.equal(result.projectHash.length, 64);
  assert.equal(result.sourceHash.length, 64);
  assert.equal(result.installTime, new Date(result.installTime).toISOString());

  // Target directory should exist and contain the package
  const bridgePath = result.bridgePath;
  const packageJson = JSON.parse(await readFile(join(bridgePath, "package.json"), "utf8")) as { name: string; version: string };
  assert.equal(packageJson.name, "com.alpsckr.unitycli");
  assert.equal(packageJson.version, "0.10.2");

  // Editor files should be copied
  const bridgeSource = await readFile(join(bridgePath, "Editor", "UnityCliBridge.cs"), "utf8");
  assert.match(bridgeSource, /class UnityCliBridge/);

  // File manifest should exist and list managed files
  const fileManifest = JSON.parse(await readFile(result.fileManifestPath, "utf8")) as { version: number; files: string[] };
  assert.equal(fileManifest.version, 1);
  assert.ok(fileManifest.files.length > 0);
  assert.ok(fileManifest.files.some((f) => f === "package.json"));
  assert.ok(fileManifest.files.some((f) => f.includes("Editor/UnityCliBridge.cs")));

  // Install metadata should exist
  const metadata = JSON.parse(await readFile(result.installMetadataPath, "utf8")) as { packageName: string; installMode: string; sourceHash: string; managedFilesSha256: string };
  assert.equal(metadata.packageName, "com.alpsckr.unitycli");
  assert.equal(metadata.installMode, "copy");
  assert.equal(metadata.sourceHash, result.sourceHash);
  assert.match(metadata.managedFilesSha256, /^[a-f0-9]{64}$/);

  // No file: dependency in manifest
  const manifest = JSON.parse(await readFile(join(project, "Packages", "manifest.json"), "utf8")) as { dependencies: Record<string, string> };
  assert.equal(manifest.dependencies["com.alpsckr.unitycli"], undefined);
});

test("canonical package source manifest fixes every file hash and fails closed on damage", async () => {
  const root = await mkdtemp(join(tmpdir(), "unity-canonical-source-"));
  const source = join(root, "unitypkg");
  const contractPath = join(root, "package-files.json");
  const sentinelPath = join(root, "target-sentinel.txt");
  try {
    await mkdir(source);
    const packageBytes = Buffer.from(`${JSON.stringify({ name: "com.alpsckr.unitycli", version: "0.10.2", unity: "2022.3" }, null, 2)}\n`, "utf8");
    await writeFile(join(source, "package.json"), packageBytes);
    await writeFile(sentinelPath, "unchanged\n", "utf8");
    const sourceHash = await hashPackageTree(source);
    const contract = {
      schemaVersion: 1,
      packageName: "com.alpsckr.unitycli",
      packageVersion: "0.10.2",
      sourceHash,
      files: [{ path: "package.json", size: packageBytes.length, sha256: createHash("sha256").update(packageBytes).digest("hex") }],
    };
    await writeFile(contractPath, `${JSON.stringify(contract, null, 2)}\n`, "utf8");

    const verified = await verifyPackageSourceIntegrity(source, contractPath);
    assert.equal(verified.sourceHash, sourceHash);
    assert.deepEqual(verified.files, ["package.json"]);

    await writeFile(join(source, "package.json"), "{}\n", "utf8");
    await assert.rejects(() => verifyPackageSourceIntegrity(source, contractPath), { code: "PACKAGE_SOURCE_INTEGRITY_FAILED" });
    assert.equal(await readFile(sentinelPath, "utf8"), "unchanged\n");

    await writeFile(join(source, "package.json"), packageBytes);
    await rm(contractPath);
    await assert.rejects(() => verifyPackageSourceIntegrity(source, contractPath), { code: "PACKAGE_SOURCE_INTEGRITY_FAILED" });
    assert.equal(await readFile(sentinelPath, "utf8"), "unchanged\n");

    await writeFile(contractPath, `${JSON.stringify({ ...contract, sourceHash: "0".repeat(64) }, null, 2)}\n`, "utf8");
    await assert.rejects(() => verifyPackageSourceIntegrity(source, contractPath), { code: "PACKAGE_SOURCE_INTEGRITY_FAILED" });
    assert.equal(await readFile(sentinelPath, "utf8"), "unchanged\n");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Bridge inspection distinguishes missing, damaged, drifted, and repaired installs", async () => {
  const project = await makeUnityProject();
  try {
    const missing = await inspectPackageBridge(project);
    assert.equal(missing.status, "missing");
    assert.equal(missing.availableVersion, "0.10.2");
    assert.equal(missing.installedVersion, undefined);
    assert.equal(missing.reasonCode, "PACKAGE_NOT_INSTALLED");
    await assert.rejects(() => stat(missing.bridgePath), { code: "ENOENT" });

    const installed = await ensurePackageBridge(project);
    const installedHashBeforeCheck = await hashPackageTree(installed.bridgePath);
    assert.equal((await inspectPackageBridge(project)).status, "installed");
    assert.equal(await hashPackageTree(installed.bridgePath), installedHashBeforeCheck);
    const managedFile = join(installed.bridgePath, "Editor", "UnityCliBridge.cs");
    const canonicalBytes = await readFile(join("unitypkg", "Editor", "UnityCliBridge.cs"));

    await rm(managedFile);
    const missingFile = await inspectPackageBridge(project);
    assert.equal(missingFile.status, "damaged");
    assert.equal(missingFile.reasonCode, "PACKAGE_INSTALL_DAMAGED");
    await ensurePackageBridge(project);
    assert.deepEqual(await readFile(managedFile), canonicalBytes);
    assert.equal((await inspectPackageBridge(project)).status, "installed");

    await writeFile(managedFile, "// damaged\n", "utf8");
    assert.equal((await inspectPackageBridge(project)).status, "damaged");
    await ensurePackageBridge(project);
    assert.deepEqual(await readFile(managedFile), canonicalBytes);

    const metadata = JSON.parse(await readFile(installed.installMetadataPath, "utf8")) as { sourceHash: string };
    metadata.sourceHash = "0".repeat(64);
    await writeFile(installed.installMetadataPath, `${JSON.stringify(metadata, null, 2)}\n`, "utf8");
    const drifted = await inspectPackageBridge(project);
    assert.equal(drifted.status, "drifted");
    assert.equal(drifted.installedVersion, "0.10.2");
    assert.equal(drifted.availableVersion, "0.10.2");
    assert.equal(drifted.reasonCode, "PACKAGE_INSTALL_DRIFTED");
    await ensurePackageBridge(project);
    assert.equal((await inspectPackageBridge(project)).status, "installed");
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("assembly conflict inspection blocks any other provider of the bundled assembly", async () => {
  const project = await makeUnityProject();
  try {
    const otherDll = join(project, "Packages", "com.example.conflict", "Plugins", "0Harmony.dll");
    const pipelineDll = join(project, "Packages", "com.unity.pipeline", "Runtime", "Plugins", "CodeAnalysis", "Microsoft.CodeAnalysis.dll");
    await mkdir(join(otherDll, ".."), { recursive: true });
    await mkdir(join(pipelineDll, ".."), { recursive: true });
    await writeFile(otherDll, "conflict", "utf8");
    await writeFile(pipelineDll, "pipeline roslyn is not protected by UnityCLI", "utf8");

    const conflict = await inspectPackageAssemblyConflicts(project);
    assert.equal(conflict.status, "conflict");
    assert.equal(conflict.conflicts.length, 1);
    assert.equal(conflict.conflicts[0].provider, "com.example.conflict");
    assert.equal(conflict.conflicts[0].assemblyFileName, "0Harmony.dll");
    assert.match(conflict.conflicts[0].sha256, /^[a-f0-9]{64}$/);
    await assert.rejects(() => preparePackageBridgeInstall(project), (error: unknown) => {
      const typed = error as { code?: string; details?: { conflicts?: Array<{ relativePath?: string }> }; nextActions?: string[] };
      assert.equal(typed.code, "PACKAGE_ASSEMBLY_CONFLICT");
      assert.match(String(typed.details?.conflicts?.[0]?.relativePath), /com\.example\.conflict/);
      assert.ok((typed.nextActions ?? []).length > 0);
      return true;
    });
    await assert.rejects(() => stat(join(project, "Packages", "com.alpsckr.unitycli")), { code: "ENOENT" });
    assert.equal((await inspectPackageBridge(project)).status, "conflict");

    await rm(join(project, "Packages", "com.example.conflict"), { recursive: true, force: true });
    const cacheDll = join(project, "Library", "PackageCache", "com.example.cached@1.0.0", "Runtime", "0Harmony.dll");
    await mkdir(join(cacheDll, ".."), { recursive: true });
    await writeFile(cacheDll, "cached conflict", "utf8");
    const cacheConflict = await inspectPackageAssemblyConflicts(project);
    assert.equal(cacheConflict.conflicts[0].provider, "com.example.cached");
    assert.equal(cacheConflict.conflicts[0].source, "PackageCache");
    await rm(join(project, "Library", "PackageCache", "com.example.cached@1.0.0"), { recursive: true, force: true });

    // Newtonsoft 不再受保护：项目自带副本、官方包副本与第三方副本都不阻止 Bridge 安装。
    for (const copy of [
      join(project, "Assets", "ThirdLib", "Newtonsoft.Json", "Newtonsoft.Json.dll"),
      join(project, "Packages", "com.unity.nuget.newtonsoft-json", "Runtime", "Newtonsoft.Json.dll"),
      join(project, "Packages", "com.example.vendor", "Plugins", "Newtonsoft.Json.dll")
    ]) {
      await mkdir(join(copy, ".."), { recursive: true });
      await writeFile(copy, "project supplied json.net", "utf8");
    }
    const clear = await inspectPackageAssemblyConflicts(project);
    assert.equal(clear.status, "clear", JSON.stringify(clear.conflicts));
    assert.equal(clear.conflicts.length, 0);
    const installed = await ensurePackageBridge(project);
    assert.equal((await checkPackageBridge(project)).sourceHash, installed.sourceHash);
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("ensurePackageBridge refreshes orphaned legacy metadata when the package directory is absent", async () => {
  const project = await makeUnityProject();
  const metadataPath = join(project, "Packages", "unitycli.install.json");
  await writeFile(metadataPath, `${JSON.stringify({
    packageName: "com.alpsckr.unitycli",
    packageVersion: "0.1.0",
    sourceHash: "0".repeat(64),
    projectHash: "legacy",
    installMode: "copy",
    installTime: "2026-07-02T07:50:45.979Z",
    cliCompatibilityRange: ">=0.1.0 <0.2.0"
  }, null, 2)}\n`, "utf8");

  const installed = await ensurePackageBridge(project);
  const metadata = JSON.parse(await readFile(metadataPath, "utf8")) as Record<string, unknown>;
  assert.equal(installed.packageVersion, "0.10.2");
  assert.equal(metadata.packageVersion, "0.10.2");
  assert.equal(metadata.cliCompatibilityRange, ">=0.10.0 <0.11.0");
  assert.match(String(metadata.managedFilesSha256), /^[a-f0-9]{64}$/);
});

test("ensurePackageBridge rejects invalid metadata when the package directory exists", async () => {
  const project = await makeUnityProject();
  const packagePath = join(project, "Packages", "com.alpsckr.unitycli");
  await mkdir(packagePath, { recursive: true });
  await writeFile(join(packagePath, "package.json"), JSON.stringify({ name: "com.alpsckr.unitycli", version: "0.1.0" }), "utf8");
  await writeFile(join(project, "Packages", "unitycli.install.json"), "{}\n", "utf8");
  await assert.rejects(() => ensurePackageBridge(project), { code: "INIT_CHECK_FAILED" });
});

test("ensurePackageBridge rejects a source-hash-stale init plan before target writes", async () => {
  const project = await makeUnityProject();
  const plan = await preparePackageBridgeInstall(project);
  plan.sourceHash = "0".repeat(64);
  await assert.rejects(() => ensurePackageBridge(project, plan), { code: "INIT_PLAN_STALE" });
  await assert.rejects(() => stat(plan.bridgePath), { code: "ENOENT" });
});

test("ensurePackageBridge skips copy when version and hash match existing install", async () => {
  const project = await makeUnityProject();

  const first = await ensurePackageBridge(project);
  const firstInstallTime = first.installTime;

  // Record mtime of a file to verify it wasn't overwritten
  const bridgeCsPath = join(first.bridgePath, "Editor", "UnityCliBridge.cs");
  const firstMtime = (await stat(bridgeCsPath)).mtimeMs;

  // Wait a tiny bit to ensure mtime would differ if overwritten
  await new Promise((resolve) => setTimeout(resolve, 10));

  const second = await ensurePackageBridge(project);
  assert.equal(second.installTime, firstInstallTime);

  // File should not have been re-copied
  const secondMtime = (await stat(bridgeCsPath)).mtimeMs;
  assert.equal(secondMtime, firstMtime);
});

test("ensurePackageBridge removes a proven stale publish sibling before idempotent success", async () => {
  const project = await makeUnityProject();
  const first = await ensurePackageBridge(project);
  const stale = `${first.bridgePath}.previous-00000000-0000-4000-8000-000000000001`;
  await mkdir(stale, { recursive: true });
  await writeFile(join(stale, "package.json"), JSON.stringify({ name: "com.alpsckr.unitycli", version: "0.2.2" }), "utf8");

  const second = await ensurePackageBridge(project);
  assert.equal(second.installTime, first.installTime);
  await assert.rejects(() => stat(stale), { code: "ENOENT" });
});

test("ensurePackageBridge overwrites when version or hash changes", async () => {
  const project = await makeUnityProject();

  const first = await ensurePackageBridge(project);
  const metadataPath = first.installMetadataPath;

  // Corrupt the version in metadata to simulate a mismatch
  const metadata = JSON.parse(await readFile(metadataPath, "utf8")) as { packageVersion: string };
  metadata.packageVersion = "0.0.1";
  await writeFile(metadataPath, JSON.stringify(metadata, null, 2), "utf8");

  // Second install should detect the mismatch and re-copy
  const second = await ensurePackageBridge(project);
  assert.equal(second.packageVersion, "0.10.2");
  assert.notEqual(second.installTime, first.installTime);

  // Verify the metadata was corrected
  const updatedMetadata = JSON.parse(await readFile(metadataPath, "utf8")) as { packageVersion: string };
  assert.equal(updatedMetadata.packageVersion, "0.10.2");
});

test("ensurePackageBridge retries transient Windows rename failures before publishing", async () => {
  const project = await makeUnityProject();
  const first = await ensurePackageBridge(project);
  const managedFile = join(first.bridgePath, "Editor", "UnityCliBridge.cs");
  await writeFile(managedFile, "// force fresh projection", "utf8");
  const plan = await preparePackageBridgeInstall(project);
  let publishAttempts = 0;

  const result = await ensurePackageBridge(project, plan, {
    absoluteDeadlineMs: Date.now() + 5_000,
    publishRetryDelayMs: 1,
    renameDirectory: async (source, target) => {
      if (target === plan.bridgePath && String(source).includes(".staging-") && publishAttempts++ < 2) {
        throw Object.assign(new Error("transient publish lock"), { code: "EPERM" });
      }
      await rename(source, target);
    },
  });

  assert.equal(publishAttempts, 3);
  assert.equal((await checkPackageBridge(project)).sourceHash, result.sourceHash);
});

test("ensurePackageBridge publish window honors the caller immutable deadline instead of a hidden 5s clamp", async () => {
  const project = await makeUnityProject();
  const first = await ensurePackageBridge(project);
  const managedFile = join(first.bridgePath, "Editor", "UnityCliBridge.cs");
  await writeFile(managedFile, "// force fresh projection", "utf8");
  const plan = await preparePackageBridgeInstall(project);
  let publishAttempts = 0;

  // 24 次 ~250ms 的 transient 失败 ≈ 6s：超过旧 5s 隐性 clamp，但远在 caller 60s deadline 之内。
  const result = await ensurePackageBridge(project, plan, {
    absoluteDeadlineMs: Date.now() + 60_000,
    publishRetryDelayMs: 250,
    renameDirectory: async (source, target) => {
      if (target === plan.bridgePath && String(source).includes(".staging-") && publishAttempts++ < 24) {
        throw Object.assign(new Error("defender scan lock"), { code: "EPERM" });
      }
      await rename(source, target);
    },
  });

  assert.equal(publishAttempts, 25);
  assert.equal((await checkPackageBridge(project)).sourceHash, result.sourceHash);
});

test("ensurePackageBridge restores the previous target when staged publish exhausts its deadline", async () => {
  const project = await makeUnityProject();
  const first = await ensurePackageBridge(project);
  const managedFile = join(first.bridgePath, "Editor", "UnityCliBridge.cs");
  const previousContent = "// previous target must survive";
  await writeFile(managedFile, previousContent, "utf8");
  const plan = await preparePackageBridgeInstall(project);

  await assert.rejects(() => ensurePackageBridge(project, plan, {
    absoluteDeadlineMs: Date.now() + 30,
    publishRetryDelayMs: 5,
    renameDirectory: async (source, target) => {
      if (target === plan.bridgePath && String(source).includes(".staging-")) {
        throw Object.assign(new Error("persistent publish lock"), { code: "EPERM" });
      }
      await rename(source, target);
    },
  }), (error: unknown) => {
    assert.ok(error instanceof PackageManagerError);
    assert.equal(error.code, "PACKAGE_DIRECTORY_LOCKED");
    assert.equal(error.transient, false);
    assert.match(error.nextActions[0], /locked package directory/);
    assert.match(error.nextActions[1], /--close-editor/);
    return true;
  });

  assert.equal(await readFile(managedFile, "utf8"), previousContent);
});

test("ensurePackageBridge preserves user-added files in target directory", async () => {
  const project = await makeUnityProject();

  const first = await ensurePackageBridge(project);

  // Add a user file in the target directory
  const userFilePath = join(first.bridgePath, "Editor", "MyCustomTool.cs");
  await writeFile(userFilePath, "// user code", "utf8");

  // Corrupt metadata to force re-install
  const metadata = JSON.parse(await readFile(first.installMetadataPath, "utf8")) as { sourceHash: string };
  metadata.sourceHash = "0000000000000000000000000000000000000000000000000000000000000000";
  await writeFile(first.installMetadataPath, JSON.stringify(metadata, null, 2), "utf8");

  // Re-install
  const second = await ensurePackageBridge(project);

  // User file should still exist (file manifest mechanism: we only remove tracked managed files)
  const userContent = await readFile(userFilePath, "utf8");
  assert.equal(userContent, "// user code");

  // Managed files should be refreshed
  const bridgeSource = await readFile(join(second.bridgePath, "Editor", "UnityCliBridge.cs"), "utf8");
  assert.match(bridgeSource, /class UnityCliBridge/);
});

test("ensurePackageBridge rejects managed-file traversal without touching an external sentinel", async () => {
  const project = await makeUnityProject();
  const first = await ensurePackageBridge(project);
  const settingsDir = join(project, "ProjectSettings");
  const sentinel = join(settingsDir, "ProjectSettings.asset");
  await mkdir(settingsDir, { recursive: true });
  await writeFile(sentinel, "sentinel", "utf8");
  await writeFile(first.fileManifestPath, `${JSON.stringify({ version: 1, files: ["../../ProjectSettings/ProjectSettings.asset"] })}\n`, "utf8");

  await assert.rejects(() => ensurePackageBridge(project), (error: unknown) =>
    typeof error === "object" && error !== null && (error as { code?: string }).code === "PACKAGE_FILE_MANIFEST_INVALID");
  assert.equal(await readFile(sentinel, "utf8"), "sentinel");
});

test("checkPackageBridge rejects a subset managed-file manifest", async () => {
  const project = await makeUnityProject();
  const first = await ensurePackageBridge(project);
  await writeFile(first.fileManifestPath, `${JSON.stringify({ version: 1, files: ["package.json"] })}\n`, "utf8");
  await writeFile(join(first.bridgePath, "Editor", "UnityCliBridge.cs"), "// replaced", "utf8");

  await assert.rejects(() => checkPackageBridge(project), (error: unknown) =>
    typeof error === "object" && error !== null && (error as { code?: string }).code === "PACKAGE_FILE_MANIFEST_INVALID");
  await assert.rejects(() => ensurePackageBridge(project), (error: unknown) =>
    typeof error === "object" && error !== null && (error as { code?: string }).code === "PACKAGE_FILE_MANIFEST_INVALID");
  assert.equal(await readFile(join(first.bridgePath, "Editor", "UnityCliBridge.cs"), "utf8"), "// replaced");
});

test("ensurePackageBridge rejects empty, duplicate, drive, and UNC managed paths", async () => {
  for (const files of [[], ["package.json", "package.json"], ["Editor/A.cs", "Editor/a.cs"], ["Editor\\A.cs"], ["C:\\Windows\\system.ini"], ["\\\\server\\share\\outside.txt"]]) {
    const project = await makeUnityProject();
    const first = await ensurePackageBridge(project);
    await writeFile(first.fileManifestPath, `${JSON.stringify({ version: 1, files })}\n`, "utf8");
    await assert.rejects(() => ensurePackageBridge(project), (error: unknown) =>
      typeof error === "object" && error !== null && (error as { code?: string }).code === "PACKAGE_FILE_MANIFEST_INVALID", JSON.stringify(files));
  }
});

test("removePackageBridge deletes only verified managed files and preserves user files", async () => {
  const project = await makeUnityProject();
  const installed = await ensurePackageBridge(project);
  const userFile = join(installed.bridgePath, "Editor", "MyCustomTool.cs");
  await writeFile(userFile, "// user code", "utf8");

  await removePackageBridge(project);
  assert.equal(await readFile(userFile, "utf8"), "// user code");
  await assert.rejects(() => stat(join(installed.bridgePath, "package.json")), { code: "ENOENT" });
  await assert.rejects(() => stat(installed.fileManifestPath), { code: "ENOENT" });
  await assert.rejects(() => stat(installed.installMetadataPath), { code: "ENOENT" });
});

test("removePackageBridge rejects an unverified manifest before deleting files", async () => {
  const project = await makeUnityProject();
  const installed = await ensurePackageBridge(project);
  const managedSource = join(installed.bridgePath, "Editor", "UnityCliBridge.cs");
  await writeFile(installed.fileManifestPath, `${JSON.stringify({ version: 1, files: ["../../ProjectSettings/ProjectSettings.asset"] })}\n`, "utf8");

  await assert.rejects(() => removePackageBridge(project), (error: unknown) =>
    typeof error === "object" && error !== null && (error as { code?: string }).code === "PACKAGE_FILE_MANIFEST_INVALID");
  assert.match(await readFile(managedSource, "utf8"), /class UnityCliBridge/);
});

test("removePackageBridge rejects mismatched ownership metadata before deleting managed files", async () => {
  for (const [field, value] of [["packageVersion", "99.0.0"], ["sourceHash", "0".repeat(64)], ["projectHash", "foreign-project"], ["cliCompatibilityRange", ">=99.0.0"]] as const) {
    const project = await makeUnityProject();
    const installed = await ensurePackageBridge(project);
    const metadata = JSON.parse(await readFile(installed.installMetadataPath, "utf8")) as Record<string, unknown>;
    metadata[field] = value;
    await writeFile(installed.installMetadataPath, `${JSON.stringify(metadata, null, 2)}\n`, "utf8");

    await assert.rejects(() => removePackageBridge(project), (error: unknown) =>
      typeof error === "object" && error !== null && (error as { code?: string }).code === "BRIDGE_PACKAGE_REMOVE_UNVERIFIED");
    assert.match(await readFile(join(installed.bridgePath, "Editor", "UnityCliBridge.cs"), "utf8"), /class UnityCliBridge/);
  }
});

test("ensurePackageBridge republishes tampered identity and compatibility metadata", async () => {
  for (const [field, value] of [["packageName", "com.example.foreign"], ["cliCompatibilityRange", ">=99.0.0"]] as const) {
    const project = await makeUnityProject();
    const installed = await ensurePackageBridge(project);
    const metadata = JSON.parse(await readFile(installed.installMetadataPath, "utf8")) as Record<string, unknown>;
    metadata[field] = value;
    await writeFile(installed.installMetadataPath, `${JSON.stringify(metadata, null, 2)}\n`, "utf8");

    await ensurePackageBridge(project);
    const observed = JSON.parse(await readFile(installed.installMetadataPath, "utf8")) as Record<string, unknown>;
    assert.equal(observed.packageName, "com.alpsckr.unitycli");
    assert.equal(observed.cliCompatibilityRange, ">=0.10.0 <0.11.0");
  }
});

test("checkPackageBridge succeeds for valid copy-mode install", async () => {
  const project = await makeUnityProject();

  await ensurePackageBridge(project);
  const checked = await checkPackageBridge(project);

  assert.equal(checked.dependencyName, "com.alpsckr.unitycli");
  assert.equal(checked.packageVersion, "0.10.2");
  assert.equal(checked.installMode, "copy");
  assert.equal(checked.projectHash.length, 64);
  assert.equal(checked.sourceHash.length, 64);
});

test("checkPackageBridge fails when package not installed", async () => {
  const project = await makeUnityProject();
  await assert.rejects(
    () => checkPackageBridge(project),
    { code: "INIT_CHECK_FAILED" }
  );
});

test("checkPackageBridge fails when install metadata is missing", async () => {
  const project = await makeUnityProject();

  // Copy the package without going through ensurePackageBridge
  const packages = join(project, "Packages", "com.alpsckr.unitycli");
  await mkdir(join(packages, "Editor"), { recursive: true });
  await writeFile(join(packages, "package.json"), JSON.stringify({ name: "com.alpsckr.unitycli", version: "0.2.2" }), "utf8");

  await assert.rejects(
    () => checkPackageBridge(project),
    { code: "INIT_CHECK_FAILED" }
  );
});

test("checkPackageBridge fails when source hash has changed", async () => {
  const project = await makeUnityProject();

  const result = await ensurePackageBridge(project);

  // Corrupt source hash in metadata
  const metadata = JSON.parse(await readFile(result.installMetadataPath, "utf8")) as { sourceHash: string };
  metadata.sourceHash = "0000000000000000000000000000000000000000000000000000000000000000";
  await writeFile(result.installMetadataPath, JSON.stringify(metadata, null, 2), "utf8");

  await assert.rejects(
    () => checkPackageBridge(project),
    { code: "INIT_CHECK_FAILED" }
  );
});

test("checkPackageBridge fails when CLI compatibility range changes", async () => {
  const project = await makeUnityProject();

  const result = await ensurePackageBridge(project);

  // Corrupt compatibility range in metadata
  const metadata = JSON.parse(await readFile(result.installMetadataPath, "utf8")) as { cliCompatibilityRange: string };
  metadata.cliCompatibilityRange = ">=99.0.0";
  await writeFile(result.installMetadataPath, JSON.stringify(metadata, null, 2), "utf8");

  await assert.rejects(
    () => checkPackageBridge(project),
    { code: "INIT_CHECK_FAILED" }
  );
});

test("deployPackageSource rejects source under target project Packages", async () => {
  const project = await makeUnityProject();
  const source = join(project, "Packages", "com.acme.local");
  await mkdir(source, { recursive: true });
  await writeFile(join(source, "package.json"), JSON.stringify({ name: "com.acme.local", version: "1.0.0" }), "utf8");

  await assert.rejects(
    () => deployPackageSource(project, source),
    { code: "PACKAGE_DEPLOY_SOURCE_INSIDE_PROJECT" }
  );
});

test("deployPackageSource rejects an existing target with an unreadable package manifest", async () => {
  const project = await makeUnityProject();
  const packageName = "com.example.invalid-target";
  const target = join(project, "Packages", packageName);
  await mkdir(target, { recursive: true });
  await writeFile(join(target, "package.json"), "not-json", "utf8");
  const source = await mkdtemp(join(tmpdir(), "unity-valid-source-"));
  await writeFile(join(source, "package.json"), `${JSON.stringify({ name: packageName, version: "2.0.0" })}\n`, "utf8");

  await assert.rejects(() => deployPackageSource(project, source));
  assert.equal(await readFile(join(target, "package.json"), "utf8"), "not-json");
});

test("deployPackageSource rejects the internal deployment backup directory as package name", async () => {
  const project = await makeUnityProject();
  const source = await mkdtemp(join(tmpdir(), "unity-pkg-source-backup-name-"));
  await writeFile(join(source, "package.json"), JSON.stringify({ name: ".unitycli_deploy_backups", version: "1.0.0" }), "utf8");

  await assert.rejects(
    () => deployPackageSource(project, source),
    { code: "PACKAGE_NAME_INVALID" }
  );
});

test("deploy and restore retain the prior package until metadata commit", async () => {
  const project = await makeUnityProject();
  const packageName = "com.example.transaction";
  const target = join(project, "Packages", packageName);
  await mkdir(target, { recursive: true });
  await writeFile(join(target, "package.json"), `${JSON.stringify({ name: packageName, version: "1.0.0" })}\n`, "utf8");
  await writeFile(join(target, "old.txt"), "old", "utf8");
  const source = await mkdtemp(join(tmpdir(), "unity-deploy-source-"));
  await writeFile(join(source, "package.json"), `${JSON.stringify({ name: packageName, version: "2.0.0" })}\n`, "utf8");
  await writeFile(join(source, "new.txt"), "new", "utf8");

  const deployed = await deployPackageSource(project, source);
  assert.equal(await readFile(join(target, "new.txt"), "utf8"), "new");
  assert.ok(deployed.backupPath);
  assert.equal(await readFile(join(deployed.backupPath!, "old.txt"), "utf8"), "old");
  await assert.rejects(() => stat(join(project, "Packages", ".unitycli_deploy_backups", "package-deployment-transaction.json")), { code: "ENOENT" });
  await writeFile(join(source, "package.json"), `${JSON.stringify({ name: packageName, version: "3.0.0" })}\n`, "utf8");
  const repeated = await deployPackageSource(project, source);
  assert.equal(repeated.backupPath, deployed.backupPath);
  assert.equal(repeated.backupPackageVersion, "1.0.0");

  await restoreDeployedPackage(project, packageName);
  assert.equal(await readFile(join(target, "old.txt"), "utf8"), "old");
  await assert.rejects(() => stat(join(target, "new.txt")), { code: "ENOENT" });
  await assert.rejects(() => stat(deployed.backupPath!), { code: "ENOENT" });
});

test("repeated deploy without an original target does not invent a backup version", async () => {
  const project = await makeUnityProject();
  const packageName = "com.example.no-backup";
  const source = await mkdtemp(join(tmpdir(), "unity-no-backup-"));
  await writeFile(join(source, "package.json"), `${JSON.stringify({ name: packageName, version: "1.0.0" })}\n`, "utf8");
  const first = await deployPackageSource(project, source);
  await writeFile(join(source, "package.json"), `${JSON.stringify({ name: packageName, version: "2.0.0" })}\n`, "utf8");
  const repeated = await deployPackageSource(project, source);
  assert.equal(first.backupPath, undefined);
  assert.equal(repeated.backupPath, undefined);
  assert.equal(repeated.backupPackageVersion, undefined);
});

test("deploy rejects target drift against the durable pre-state before marker creation", async () => {
  const project = await makeUnityProject();
  const packageName = "com.example.drift";
  const target = join(project, "Packages", packageName);
  await mkdir(target, { recursive: true });
  await writeFile(join(target, "package.json"), `${JSON.stringify({ name: packageName, version: "1.0.0" })}\n`, "utf8");
  const source = await mkdtemp(join(tmpdir(), "unity-drift-source-"));
  await writeFile(join(source, "package.json"), `${JSON.stringify({ name: packageName, version: "2.0.0" })}\n`, "utf8");
  const prepared = await deployPackageSource(project, source, { dryRun: true });
  const observed = await inspectPackageDeploymentFiles(project, packageName, prepared.targetPath, prepared.backupPath);
  await writeFile(join(target, "concurrent.txt"), "concurrent", "utf8");

  await assert.rejects(() => deployPackageSource(project, source, {
    plannedBackupPath: prepared.backupPath,
    expectedPreState: {
      sourcePath: prepared.sourcePath, sourceHash: prepared.sourceHash,
      targetExists: observed.targetExists, targetHash: observed.targetHash,
      recordIdentity: observed.recordIdentity, backupPath: prepared.backupPath,
      backupExists: observed.backupExists, backupHash: observed.backupHash,
    }
  }), { code: "PACKAGE_DEPLOY_PRESTATE_CHANGED" });
  assert.equal(await readFile(join(target, "concurrent.txt"), "utf8"), "concurrent");
  await assert.rejects(() => stat(join(project, "Packages", ".unitycli_deploy_backups", "package-deployment-transaction.json")), { code: "ENOENT" });
});

test("deployment recovery rejects a transaction root path without deleting backups", async () => {
  const project = await makeUnityProject();
  const source = await mkdtemp(join(tmpdir(), "unity-marker-source-"));
  await writeFile(join(source, "package.json"), `${JSON.stringify({ name: "com.example.marker", version: "1.0.0" })}\n`, "utf8");
  const backupRoot = join(project, "Packages", ".unitycli_deploy_backups");
  await mkdir(backupRoot, { recursive: true });
  const sentinel = join(backupRoot, "sentinel.txt");
  await writeFile(sentinel, "keep", "utf8");
  await writeFile(join(backupRoot, "package-deployment-transaction.json"), `${JSON.stringify({
    version: 1, operation: "deploy", phase: "commit_proven", packageName: "com.example.marker",
    targetPath: join(project, "Packages", "com.example.marker"), stagingPath: join(backupRoot, "transactions"),
    rollbackPath: join(backupRoot, "transactions", "00000000-0000-4000-8000-000000000001", "rollback"),
    targetExisted: false, backupCreated: false, deleteBackupOnCommit: false
  })}\n`, "utf8");

  await assert.rejects(() => deployPackageSource(project, source), { code: "PACKAGE_DEPLOYMENT_TRANSACTION_INVALID" });
  assert.equal(await readFile(sentinel, "utf8"), "keep");
});

test("deployment recovery rejects an unsafe package target without deleting project settings", async () => {
  const project = await makeUnityProject();
  const source = await mkdtemp(join(tmpdir(), "unity-unsafe-marker-"));
  await writeFile(join(source, "package.json"), `${JSON.stringify({ name: "com.example.safe", version: "1.0.0" })}\n`, "utf8");
  const settings = join(project, "ProjectSettings");
  await mkdir(settings, { recursive: true });
  const sentinel = join(settings, "ProjectSettings.asset");
  await writeFile(sentinel, "keep", "utf8");
  const backupRoot = join(project, "Packages", ".unitycli_deploy_backups");
  const transactionRoot = join(backupRoot, "transactions", "00000000-0000-4000-8000-000000000001");
  await mkdir(backupRoot, { recursive: true });
  await writeFile(join(backupRoot, "package-deployment-transaction.json"), `${JSON.stringify({
    version: 1, operation: "deploy", phase: "prepared", packageName: "../ProjectSettings", targetPath: settings,
    stagingPath: join(transactionRoot, "staging"), rollbackPath: join(transactionRoot, "rollback"),
    targetExisted: false, backupCreated: false, deleteBackupOnCommit: false
  })}\n`, "utf8");

  await assert.rejects(() => deployPackageSource(project, source), { code: "PACKAGE_DEPLOYMENT_TRANSACTION_INVALID" });
  assert.equal(await readFile(sentinel, "utf8"), "keep");
});

test("deploy rejects a non-canonical planned backup before transaction cleanup", async () => {
  const project = await makeUnityProject();
  const packageName = "com.example.plan";
  const target = join(project, "Packages", packageName);
  await mkdir(target, { recursive: true });
  await writeFile(join(target, "package.json"), `${JSON.stringify({ name: packageName, version: "1.0.0" })}\n`, "utf8");
  const source = await mkdtemp(join(tmpdir(), "unity-plan-source-"));
  await writeFile(join(source, "package.json"), `${JSON.stringify({ name: packageName, version: "2.0.0" })}\n`, "utf8");
  const backupRoot = join(project, "Packages", ".unitycli_deploy_backups");

  await assert.rejects(() => deployPackageSource(project, source, { plannedBackupPath: backupRoot }), { code: "PACKAGE_DEPLOY_PLAN_INVALID" });
  await stat(join(target, "package.json"));
});

test("restore rejects backup drift against the durable pre-state before marker creation", async () => {
  const project = await makeUnityProject();
  const packageName = "com.example.restore-drift";
  const target = join(project, "Packages", packageName);
  await mkdir(target, { recursive: true });
  await writeFile(join(target, "package.json"), `${JSON.stringify({ name: packageName, version: "1.0.0" })}\n`, "utf8");
  const source = await mkdtemp(join(tmpdir(), "unity-restore-drift-"));
  await writeFile(join(source, "package.json"), `${JSON.stringify({ name: packageName, version: "2.0.0" })}\n`, "utf8");
  const deployed = await deployPackageSource(project, source);
  const prepared = await restoreDeployedPackage(project, packageName, { dryRun: true });
  const observed = await inspectPackageDeploymentFiles(project, packageName, prepared.targetPath, prepared.backupPath);
  await writeFile(join(deployed.backupPath!, "drift.txt"), "drift", "utf8");

  await assert.rejects(() => restoreDeployedPackage(project, packageName, { expectedPreState: {
    targetPath: prepared.targetPath, targetExists: observed.targetExists, targetHash: observed.targetHash,
    recordIdentity: observed.recordIdentity, backupPath: prepared.backupPath,
    backupExists: observed.backupExists, backupHash: observed.backupHash,
  } }), { code: "PACKAGE_RESTORE_PRESTATE_CHANGED" });
  assert.equal(JSON.parse(await readFile(join(target, "package.json"), "utf8")).version, "2.0.0");
  await assert.rejects(() => stat(join(project, "Packages", ".unitycli_deploy_backups", "package-deployment-transaction.json")), { code: "ENOENT" });
});

test("restore rejects a declared backup that is missing", async () => {
  const project = await makeUnityProject();
  const packageName = "com.example.missing-backup";
  const target = join(project, "Packages", packageName);
  await mkdir(target, { recursive: true });
  await writeFile(join(target, "package.json"), `${JSON.stringify({ name: packageName, version: "1.0.0" })}\n`, "utf8");
  const source = await mkdtemp(join(tmpdir(), "unity-missing-backup-"));
  await writeFile(join(source, "package.json"), `${JSON.stringify({ name: packageName, version: "2.0.0" })}\n`, "utf8");
  const deployed = await deployPackageSource(project, source);
  await rm(deployed.backupPath!, { recursive: true, force: true });

  await assert.rejects(() => restoreDeployedPackage(project, packageName), { code: "PACKAGE_DEPLOYMENT_BACKUP_MISSING" });
  assert.equal(JSON.parse(await readFile(join(target, "package.json"), "utf8")).version, "2.0.0");
});

test("restore rejects a backup owned by another deployment record", async () => {
  const project = await makeUnityProject();
  const deploy = async (name: string) => {
    const target = join(project, "Packages", name);
    await mkdir(target, { recursive: true });
    await writeFile(join(target, "package.json"), `${JSON.stringify({ name, version: "1.0.0" })}\n`, "utf8");
    const source = await mkdtemp(join(tmpdir(), "unity-cross-backup-"));
    await writeFile(join(source, "package.json"), `${JSON.stringify({ name, version: "2.0.0" })}\n`, "utf8");
    return deployPackageSource(project, source);
  };
  const first = await deploy("com.example.first");
  const second = await deploy("com.example.second");
  const metadata = JSON.parse(await readFile(first.metadataPath, "utf8")) as { deployments: Record<string, { backupPath?: string }> };
  metadata.deployments["com.example.first"].backupPath = second.backupPath;
  await writeFile(first.metadataPath, `${JSON.stringify(metadata, null, 2)}\n`, "utf8");

  await assert.rejects(() => restoreDeployedPackage(project, "com.example.first"), { code: "PACKAGE_DEPLOYMENT_METADATA_UNSAFE" });
  assert.ok(second.backupPath);
  await stat(second.backupPath!);
});

test("restoreDeployedPackage rejects unsafe deployment metadata paths", async () => {
  const project = await makeUnityProject();
  const source = await mkdtemp(join(tmpdir(), "unity-pkg-source-"));
  await writeFile(join(source, "package.json"), JSON.stringify({ name: "com.acme.local", version: "1.0.0" }), "utf8");

  const deployed = await deployPackageSource(project, source);
  const metadata = JSON.parse(await readFile(deployed.metadataPath, "utf8")) as { deployments: Record<string, { targetPath: string }> };
  metadata.deployments["com.acme.local"].targetPath = join(tmpdir(), "outside-com.acme.local");
  await writeFile(deployed.metadataPath, `${JSON.stringify(metadata, null, 2)}\n`, "utf8");

  await assert.rejects(
    () => restoreDeployedPackage(project, "com.acme.local"),
    { code: "PACKAGE_DEPLOYMENT_METADATA_UNSAFE" }
  );
});

test("package tree hash follows the cross-language depth-first ordinal fixture", async () => {
  const root = await mkdtemp(join(tmpdir(), "unity-tree-hash-"));
  await mkdir(join(root, "Editor"), { recursive: true });
  await mkdir(join(root, "Runtime", "Bin"), { recursive: true });
  await writeFile(join(root, "Editor", "Tool.cs"), "tool\n", "utf8");
  await writeFile(join(root, "Editor.meta"), "meta\n", "utf8");
  await writeFile(join(root, "Runtime", "Bin", "data.bin"), Buffer.from([0, 1, 255]));
  await writeFile(join(root, "Runtime.meta"), "runtime-meta\n", "utf8");
  assert.equal(await hashPackageTree(root), "a111c735599238f31af02cbe903f3939a0c300472c473d1c7f9b314371748df5");
});

test("file manifest tracks only files that were copied, not directories", async () => {
  const project = await makeUnityProject();
  const result = await ensurePackageBridge(project);

  const fileManifest = JSON.parse(await readFile(result.fileManifestPath, "utf8")) as { version: number; files: string[] };

  // All entries should be file paths, not directory paths (no trailing /)
  for (const file of fileManifest.files) {
    assert.ok(!file.endsWith("/"), `File manifest entry should not be a directory: ${file}`);
    assert.ok(file.length > 0, `File manifest entry should not be empty`);
  }

  // Should include key files
  assert.ok(fileManifest.files.includes("package.json"), "package.json should be in file manifest");
});
