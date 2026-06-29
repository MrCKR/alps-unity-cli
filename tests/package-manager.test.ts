import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, rm, stat, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";
import test from "node:test";
import { ensurePackageBridge, checkPackageBridge, deployPackageSource, restoreDeployedPackage } from "../src/package-manager.js";

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

test("ensurePackageBridge removes stale file: dependency from manifest", async () => {
  const project = await makeUnityProject();

  // Simulate an old file: dependency from a previous install mode
  const manifestPath = join(project, "Packages", "manifest.json");
  const manifest = JSON.parse(await readFile(manifestPath, "utf8")) as { dependencies: Record<string, string> };
  manifest.dependencies["com.unity_cli"] = "file:../../some/path";
  await writeFile(manifestPath, JSON.stringify(manifest, null, 2), "utf8");

  const result = await ensurePackageBridge(project);

  // The file: dependency should have been removed
  const updatedManifest = JSON.parse(await readFile(manifestPath, "utf8")) as { dependencies: Record<string, string> };
  assert.equal(updatedManifest.dependencies["com.unity_cli"], undefined);
  assert.equal(result.installMode, "copy");
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
