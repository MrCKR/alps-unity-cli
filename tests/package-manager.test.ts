import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, rename, rm, stat, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";
import test from "node:test";
import { ensurePackageBridge, checkPackageBridge, deployPackageSource, preparePackageBridgeInstall, restoreDeployedPackage, searchRegistry } from "../src/package-manager.js";

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
  assert.equal(result.packageVersion, "0.1.0");
  assert.equal(result.installMode, "copy");
  assert.equal(result.projectHash.length, 64);
  assert.equal(result.sourceHash.length, 64);
  assert.equal(result.installTime, new Date(result.installTime).toISOString());

  // Target directory should exist and contain the package
  const bridgePath = result.bridgePath;
  const packageJson = JSON.parse(await readFile(join(bridgePath, "package.json"), "utf8")) as { name: string; version: string };
  assert.equal(packageJson.name, "com.unity_cli");
  assert.equal(packageJson.version, "0.1.0");

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
  const metadata = JSON.parse(await readFile(result.installMetadataPath, "utf8")) as { packageName: string; installMode: string; sourceHash: string };
  assert.equal(metadata.packageName, "com.unity_cli");
  assert.equal(metadata.installMode, "copy");
  assert.equal(metadata.sourceHash, result.sourceHash);

  // No file: dependency in manifest
  const manifest = JSON.parse(await readFile(join(project, "Packages", "manifest.json"), "utf8")) as { dependencies: Record<string, string> };
  assert.equal(manifest.dependencies["com.unity_cli"], undefined);
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
  const stale = `${first.bridgePath}.previous-123-456`;
  await mkdir(stale, { recursive: true });
  await writeFile(join(stale, "package.json"), JSON.stringify({ name: "com.unity_cli", version: "0.1.0" }), "utf8");

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
  assert.equal(second.packageVersion, "0.1.0");
  assert.notEqual(second.installTime, first.installTime);

  // Verify the metadata was corrected
  const updatedMetadata = JSON.parse(await readFile(metadataPath, "utf8")) as { packageVersion: string };
  assert.equal(updatedMetadata.packageVersion, "0.1.0");
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

test("checkPackageBridge succeeds for valid copy-mode install", async () => {
  const project = await makeUnityProject();

  await ensurePackageBridge(project);
  const checked = await checkPackageBridge(project);

  assert.equal(checked.dependencyName, "com.unity_cli");
  assert.equal(checked.packageVersion, "0.1.0");
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
  await writeFile(join(packages, "package.json"), JSON.stringify({ name: "com.unity_cli", version: "0.1.0" }), "utf8");

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

test("deployPackageSource rejects the internal deployment backup directory as package name", async () => {
  const project = await makeUnityProject();
  const source = await mkdtemp(join(tmpdir(), "unity-pkg-source-backup-name-"));
  await writeFile(join(source, "package.json"), JSON.stringify({ name: ".unity_cli_deploy_backups", version: "1.0.0" }), "utf8");

  await assert.rejects(
    () => deployPackageSource(project, source),
    { code: "PACKAGE_NAME_INVALID" }
  );
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
