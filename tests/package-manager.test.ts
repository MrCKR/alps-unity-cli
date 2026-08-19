import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, rename, rm, stat, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";
import test from "node:test";
import { ensurePackageBridge, checkPackageBridge, deployPackageSource, hashPackageTree, inspectPackageDeploymentFiles, preparePackageBridgeInstall, removePackageBridge, restoreDeployedPackage, searchRegistry } from "../src/package-manager.js";

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

test("searchRegistry projects only the declared public package fields", async () => {
  const originalFetch = globalThis.fetch;
  globalThis.fetch = (async () => new Response(JSON.stringify({
    objects: [{ package: { name: "com.acme.tool", version: "1.2.3", description: "Tool", author: { name: "Private" }, links: { npm: "https://example.invalid" } } }]
  }), { status: 200, headers: { "content-type": "application/json" } })) as typeof fetch;
  try {
    assert.deepEqual(await searchRegistry("https://registry.example.test", "acme"), [
      { name: "com.acme.tool", version: "1.2.3", description: "Tool" }
    ]);
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("ensurePackageBridge copies unitypkg contents to Packages/com.unity_cli", async () => {
  const project = await makeUnityProject();
  const result = await ensurePackageBridge(project);

  assert.equal(result.dependencyName, "com.unity_cli");
  assert.equal(result.packageVersion, "0.3.1");
  assert.equal(result.installMode, "copy");
  assert.equal(result.projectHash.length, 64);
  assert.equal(result.sourceHash.length, 64);
  assert.equal(result.installTime, new Date(result.installTime).toISOString());

  // Target directory should exist and contain the package
  const bridgePath = result.bridgePath;
  const packageJson = JSON.parse(await readFile(join(bridgePath, "package.json"), "utf8")) as { name: string; version: string };
  assert.equal(packageJson.name, "com.unity_cli");
  assert.equal(packageJson.version, "0.3.1");

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
  assert.equal(metadata.packageName, "com.unity_cli");
  assert.equal(metadata.installMode, "copy");
  assert.equal(metadata.sourceHash, result.sourceHash);
  assert.match(metadata.managedFilesSha256, /^[a-f0-9]{64}$/);

  // No file: dependency in manifest
  const manifest = JSON.parse(await readFile(join(project, "Packages", "manifest.json"), "utf8")) as { dependencies: Record<string, string> };
  assert.equal(manifest.dependencies["com.unity_cli"], undefined);
});

test("ensurePackageBridge refreshes orphaned legacy metadata when the package directory is absent", async () => {
  const project = await makeUnityProject();
  const metadataPath = join(project, "Packages", "unity_cli.install.json");
  await writeFile(metadataPath, `${JSON.stringify({
    packageName: "com.unity_cli",
    packageVersion: "0.1.0",
    sourceHash: "0".repeat(64),
    projectHash: "legacy",
    installMode: "copy",
    installTime: "2026-07-02T07:50:45.979Z",
    cliCompatibilityRange: ">=0.1.0 <0.2.0"
  }, null, 2)}\n`, "utf8");

  const installed = await ensurePackageBridge(project);
  const metadata = JSON.parse(await readFile(metadataPath, "utf8")) as Record<string, unknown>;
  assert.equal(installed.packageVersion, "0.3.1");
  assert.equal(metadata.packageVersion, "0.3.1");
  assert.equal(metadata.cliCompatibilityRange, ">=0.2.0 <0.3.0");
  assert.match(String(metadata.managedFilesSha256), /^[a-f0-9]{64}$/);
});

test("ensurePackageBridge rejects invalid metadata when the package directory exists", async () => {
  const project = await makeUnityProject();
  const packagePath = join(project, "Packages", "com.unity_cli");
  await mkdir(packagePath, { recursive: true });
  await writeFile(join(packagePath, "package.json"), JSON.stringify({ name: "com.unity_cli", version: "0.1.0" }), "utf8");
  await writeFile(join(project, "Packages", "unity_cli.install.json"), "{}\n", "utf8");
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
  await writeFile(join(stale, "package.json"), JSON.stringify({ name: "com.unity_cli", version: "0.2.2" }), "utf8");

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
  assert.equal(second.packageVersion, "0.3.1");
  assert.notEqual(second.installTime, first.installTime);

  // Verify the metadata was corrected
  const updatedMetadata = JSON.parse(await readFile(metadataPath, "utf8")) as { packageVersion: string };
  assert.equal(updatedMetadata.packageVersion, "0.3.1");
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
  }), { code: "EPERM" });

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
    assert.equal(observed.packageName, "com.unity_cli");
    assert.equal(observed.cliCompatibilityRange, ">=0.2.0 <0.3.0");
  }
});

test("checkPackageBridge succeeds for valid copy-mode install", async () => {
  const project = await makeUnityProject();

  await ensurePackageBridge(project);
  const checked = await checkPackageBridge(project);

  assert.equal(checked.dependencyName, "com.unity_cli");
  assert.equal(checked.packageVersion, "0.3.1");
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
  const packages = join(project, "Packages", "com.unity_cli");
  await mkdir(join(packages, "Editor"), { recursive: true });
  await writeFile(join(packages, "package.json"), JSON.stringify({ name: "com.unity_cli", version: "0.2.2" }), "utf8");

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
  await writeFile(join(source, "package.json"), JSON.stringify({ name: ".unity_cli_deploy_backups", version: "1.0.0" }), "utf8");

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
  await assert.rejects(() => stat(join(project, "Packages", ".unity_cli_deploy_backups", "package-deployment-transaction.json")), { code: "ENOENT" });
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
  await assert.rejects(() => stat(join(project, "Packages", ".unity_cli_deploy_backups", "package-deployment-transaction.json")), { code: "ENOENT" });
});

test("deployment recovery rejects a transaction root path without deleting backups", async () => {
  const project = await makeUnityProject();
  const source = await mkdtemp(join(tmpdir(), "unity-marker-source-"));
  await writeFile(join(source, "package.json"), `${JSON.stringify({ name: "com.example.marker", version: "1.0.0" })}\n`, "utf8");
  const backupRoot = join(project, "Packages", ".unity_cli_deploy_backups");
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
  const backupRoot = join(project, "Packages", ".unity_cli_deploy_backups");
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
  const backupRoot = join(project, "Packages", ".unity_cli_deploy_backups");

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
  await assert.rejects(() => stat(join(project, "Packages", ".unity_cli_deploy_backups", "package-deployment-transaction.json")), { code: "ENOENT" });
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
