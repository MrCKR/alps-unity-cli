/**
 * Unit tests for the extensions CLI command surface (list/install/remove/doctor).
 *
 * Tests extensions list/install/remove/doctor commands.
 */

import assert from "node:assert/strict";
import test from "node:test";
import { mkdtemp, mkdir, writeFile, readFile, rm, stat } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";

import { handleExtensionsCommand } from "../../src/cli-commands/extensions.js";
import { readInstalledExtensions, writeInstalledExtensions } from "../../src/cli-commands/extensions.js";

const requestId = "test_req_ext_cmd_001";

// ── Test runtime ────────────────────────────────────────────────────────────

const emptyRuntime = {
  dataDir: process.cwd(),
  store: {} as never,
  runner: {} as never
};

async function makeProjectDir(installedPackages: Record<string, string> = {}, enabledExtensions: string[] = []): Promise<string> {
  const dir = await mkdtemp(join(tmpdir(), "unity-cli-ext-cmd-"));
  await mkdir(join(dir, "Packages"), { recursive: true });

  // Write manifest.json with installed packages
  await writeFile(
    join(dir, "Packages", "manifest.json"),
    JSON.stringify({ dependencies: installedPackages })
  );

  // Write packages-lock.json mirroring manifest
  const lockDeps: Record<string, { version: string }> = {};
  for (const [id, version] of Object.entries(installedPackages)) {
    lockDeps[id] = { version };
  }
  await writeFile(
    join(dir, "Packages", "packages-lock.json"),
    JSON.stringify({ dependencies: lockDeps })
  );

  // Write extensions install state if any
  if (enabledExtensions.length > 0) {
    await mkdir(join(dir, "Packages", "com.unity_cli"), { recursive: true });
    await writeFile(
      join(dir, "Packages", "com.unity_cli", "extensions.json"),
      JSON.stringify({ extensions: enabledExtensions })
    );
  }

  return dir;
}

// ── extensions list ────────────────────────────────────────────────────────

test("extensions list returns all five extensions with availability", async () => {
  const project = await makeProjectDir({ "com.unity.probuilder": "5.2.0" });
  try {
    const result = await handleExtensionsCommand("list", [], { project }, emptyRuntime, requestId);
    assert.equal(result.ok, true);
    const data = result.data as { count: number; extensions: Array<{ id: string; availability: string }> };
    assert.equal(data.count, 5);
    const probuilder = data.extensions.find((e) => e.id === "probuilder")!;
    assert.equal(probuilder.availability, "available");
    const vfx = data.extensions.find((e) => e.id === "vfx-graph")!;
    assert.equal(vfx.availability, "unavailable");
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("extensions list marks enabled extensions", async () => {
  const project = await makeProjectDir(
    { "com.unity.probuilder": "5.2.0" },
    ["probuilder"]
  );
  try {
    const result = await handleExtensionsCommand("list", [], { project }, emptyRuntime, requestId);
    const data = result.data as { extensions: Array<{ id: string; enabled: boolean }> };
    const probuilder = data.extensions.find((e) => e.id === "probuilder")!;
    assert.equal(probuilder.enabled, true);
    const vfx = data.extensions.find((e) => e.id === "vfx-graph")!;
    assert.equal(vfx.enabled, false);
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("extensions list includes unity package ids", async () => {
  const project = await makeProjectDir({});
  try {
    const result = await handleExtensionsCommand("list", [], { project }, emptyRuntime, requestId);
    const data = result.data as { extensions: Array<{ id: string; unityPackages: Array<{ packageId: string }> }> };
    const probuilder = data.extensions.find((e) => e.id === "probuilder")!;
    assert.equal(probuilder.unityPackages[0].packageId, "com.unity.probuilder");
    const hdrp = data.extensions.find((e) => e.id === "hdrp")!;
    assert.equal(hdrp.unityPackages[0].packageId, "com.unity.render-pipelines.high-definition");
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("extensions list includes nextActions for unavailable extensions", async () => {
  const project = await makeProjectDir({});
  try {
    const result = await handleExtensionsCommand("list", [], { project }, emptyRuntime, requestId);
    const data = result.data as { extensions: Array<{ id: string; availability: string; nextActions: string[] }> };
    const probuilder = data.extensions.find((e) => e.id === "probuilder")!;
    assert.equal(probuilder.availability, "unavailable");
    assert.ok(probuilder.nextActions.length > 0);
    assert.ok(probuilder.nextActions.some((a) => a.includes("Unity Package Manager")));
    assert.ok(probuilder.nextActions.some((a) => a.includes("unity_cli extensions install probuilder")));
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

// ── extensions install ─────────────────────────────────────────────────────

test("extensions install enables an extension and writes state", async () => {
  const project = await makeProjectDir({ "com.unity.probuilder": "5.2.0" });
  try {
    const result = await handleExtensionsCommand("install", ["probuilder"], { project }, emptyRuntime, requestId);
    assert.equal(result.ok, true);
    const data = result.data as { status: string; extensionId: string };
    assert.equal(data.status, "installed");
    assert.equal(data.extensionId, "probuilder");

    // Verify state was written
    const enabled = await readInstalledExtensions(project);
    assert.ok(enabled.has("probuilder"));
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("extensions install on already-installed returns already_installed", async () => {
  const project = await makeProjectDir({ "com.unity.probuilder": "5.2.0" });
  try {
    await handleExtensionsCommand("install", ["probuilder"], { project }, emptyRuntime, requestId);
    const result = await handleExtensionsCommand("install", ["probuilder"], { project }, emptyRuntime, requestId);
    assert.equal(result.ok, true);
    const data = result.data as { status: string };
    assert.equal(data.status, "already_installed");
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});


test("extensions install creates a Unity extension directory package", async () => {
  const project = await makeProjectDir({ "com.unity.probuilder": "5.2.0" });
  try {
    const result = await handleExtensionsCommand("install", ["probuilder"], { project }, emptyRuntime, requestId);
    assert.equal(result.ok, true);
    const data = result.data as { targetRoot: string; fileCount: number };
    assert.ok(data.targetRoot.endsWith(join("Packages", "com.unity_cli", "Extensions", "probuilder")));
    assert.ok(data.fileCount > 0);

    const extensionRoot = join(project, "Packages", "com.unity_cli", "Extensions", "probuilder");
    const installedManifest = JSON.parse(await readFile(join(extensionRoot, "extension.json"), "utf8")) as { id: string; copyRoot: string; unityInstall: { layout: string } };
    assert.equal(installedManifest.id, "probuilder");
    assert.equal(installedManifest.copyRoot, "extension-directory");
    assert.equal(installedManifest.unityInstall.layout, "directory");

    const commandSource = await readFile(join(extensionRoot, "Editor", "Commands", "ProBuilderCommands.cs"), "utf8");
    assert.match(commandSource, /RegisterProBuilderCommands/);
    await stat(join(extensionRoot, "docs", "probuilder.md"));
    await assert.rejects(() => stat(join(extensionRoot, "bridge")), { code: "ENOENT" });
    await assert.rejects(() => stat(join(extensionRoot, "tools")), { code: "ENOENT" });

    const installManifest = JSON.parse(await readFile(join(project, "Packages", "com.unity_cli", "unity_cli.extensions.manifest.json"), "utf8")) as {
      extensions: Array<{ extensionId: string; copyRoot: string; layout?: string; files: Array<{ path: string }> }>;
    };
    const entry = installManifest.extensions.find((item) => item.extensionId === "probuilder")!;
    assert.equal(entry.copyRoot, "extension-directory");
    assert.equal(entry.layout, "directory");
    assert.ok(entry.files.some((file) => file.path === "extension.json"));
    assert.ok(entry.files.some((file) => file.path === "Editor/Commands/ProBuilderCommands.cs"));
    assert.ok(entry.files.some((file) => file.path === "docs/probuilder.md"));
    assert.ok(!entry.files.some((file) => file.path.startsWith("unitypkg/")));
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});
test("extensions install unknown extension fails with UNKNOWN_EXTENSION", async () => {
  const project = await makeProjectDir({});
  try {
    const result = await handleExtensionsCommand("install", ["nonexistent"], { project }, emptyRuntime, requestId);
    assert.equal(result.ok, false);
    assert.equal(result.error?.code, "UNKNOWN_EXTENSION");
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("extensions install without name fails with EXTENSION_NAME_REQUIRED", async () => {
  const project = await makeProjectDir({});
  try {
    const result = await handleExtensionsCommand("install", [], { project }, emptyRuntime, requestId);
    assert.equal(result.ok, false);
    assert.equal(result.error?.code, "EXTENSION_NAME_REQUIRED");
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("extensions install without project fails with PROJECT_PATH_REQUIRED", async () => {
  const result = await handleExtensionsCommand("install", ["probuilder"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "PROJECT_PATH_REQUIRED");
});

test("extensions install with missing Unity dep fails without copying bundle", async () => {
  const project = await makeProjectDir({});
  try {
    const result = await handleExtensionsCommand("install", ["cinemachine"], { project }, emptyRuntime, requestId);
    assert.equal(result.ok, false);
    assert.equal(result.error?.code, "E_EXTENSION_DEPENDENCY_MISSING");
    assert.ok((result.error?.next_actions ?? []).some((a) => a.includes("Unity Package Manager")));
    assert.ok((result.error?.next_actions ?? []).some((a) => a.includes("extensions install cinemachine")));
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

// ── extensions remove ──────────────────────────────────────────────────────

test("extensions remove disables an installed extension", async () => {
  const project = await makeProjectDir({ "com.unity.probuilder": "5.2.0" });
  try {
    await handleExtensionsCommand("install", ["probuilder"], { project }, emptyRuntime, requestId);
    const result = await handleExtensionsCommand("remove", ["probuilder"], { project }, emptyRuntime, requestId);
    assert.equal(result.ok, true);
    const data = result.data as { status: string; extensionId: string };
    assert.equal(data.status, "removed");

    // Verify state was removed
    const enabled = await readInstalledExtensions(project);
    assert.ok(!enabled.has("probuilder"));
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("extensions remove on non-installed fails with EXTENSION_NOT_INSTALLED", async () => {
  const project = await makeProjectDir({});
  try {
    const result = await handleExtensionsCommand("remove", ["probuilder"], { project }, emptyRuntime, requestId);
    assert.equal(result.ok, false);
    assert.equal(result.error?.code, "EXTENSION_NOT_INSTALLED");
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("extensions remove without name fails with EXTENSION_NAME_REQUIRED", async () => {
  const project = await makeProjectDir({});
  try {
    const result = await handleExtensionsCommand("remove", [], { project }, emptyRuntime, requestId);
    assert.equal(result.ok, false);
    assert.equal(result.error?.code, "EXTENSION_NAME_REQUIRED");
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("extensions remove without project fails with PROJECT_PATH_REQUIRED", async () => {
  const result = await handleExtensionsCommand("remove", ["probuilder"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "PROJECT_PATH_REQUIRED");
});

// ── extensions doctor ───────────────────────────────────────────────────────

test("extensions doctor reports healthy when all consistent", async () => {
  const project = await makeProjectDir({ "com.unity.probuilder": "5.2.0" });
  try {
    await handleExtensionsCommand("install", ["probuilder"], { project }, emptyRuntime, requestId);
    const result = await handleExtensionsCommand("doctor", [], { project }, emptyRuntime, requestId);
    assert.equal(result.ok, true);
    const data = result.data as { healthy: boolean; issues: string[] };
    // ProBuilder installed & enabled = healthy for that extension.
    // Other extensions have deps not installed but are not enabled, so no issue.
    assert.equal(data.healthy, true);
    assert.equal(data.issues.length, 0);
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("extensions doctor flags enabled extension with missing Unity deps", async () => {
  const project = await makeProjectDir({}, ["probuilder"]);
  try {
    const result = await handleExtensionsCommand("doctor", [], { project }, emptyRuntime, requestId);
    assert.equal(result.ok, true);
    const data = result.data as { healthy: boolean; issues: string[]; checks: Array<{ id: string; issues: string[] }> };
    assert.equal(data.healthy, false);
    assert.ok(data.issues.length > 0);
    const probuilderCheck = data.checks.find((c) => c.id === "probuilder")!;
    assert.ok(probuilderCheck.issues.length > 0);
    assert.ok(probuilderCheck.issues.some((i) => i.includes("missing")));
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("extensions doctor flags available extension not enabled", async () => {
  const project = await makeProjectDir({ "com.unity.probuilder": "5.2.0" });
  try {
    const result = await handleExtensionsCommand("doctor", [], { project }, emptyRuntime, requestId);
    assert.equal(result.ok, true);
    const data = result.data as { healthy: boolean; checks: Array<{ id: string; issues: string[] }> };
    assert.equal(data.healthy, false);
    const probuilderCheck = data.checks.find((c) => c.id === "probuilder")!;
    assert.ok(probuilderCheck.issues.some((i) => i.includes("directory is not installed")));
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("extensions doctor without project fails with PROJECT_PATH_REQUIRED", async () => {
  const result = await handleExtensionsCommand("doctor", [], {}, emptyRuntime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "PROJECT_PATH_REQUIRED");
});

test("extensions doctor detects unknown enabled extensions", async () => {
  const project = await makeProjectDir({}, ["nonexistent-extension"]);
  try {
    const result = await handleExtensionsCommand("doctor", [], { project }, emptyRuntime, requestId);
    assert.equal(result.ok, true);
    const data = result.data as { healthy: boolean; checks: Array<{ id: string; issues: string[] }> };
    assert.equal(data.healthy, false);
    const unknownCheck = data.checks.find((c) => c.id === "nonexistent-extension")!;
    assert.ok(unknownCheck);
    assert.ok(unknownCheck.issues.some((i) => i.includes("not registered")));
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

// ── Unknown command ─────────────────────────────────────────────────────────

test("extensions unknown command fails with UNKNOWN_EXTENSIONS_COMMAND", async () => {
  const result = await handleExtensionsCommand("frobnicate", [], {}, emptyRuntime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "UNKNOWN_EXTENSIONS_COMMAND");
});

test("extensions with no command defaults to list", async () => {
  const project = await makeProjectDir({});
  try {
    const result = await handleExtensionsCommand(undefined, [], { project }, emptyRuntime, requestId);
    assert.equal(result.ok, true);
    const data = result.data as { command: string };
    assert.equal(data.command, "extensions.list");
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

// ── Install state store ────────────────────────────────────────────────────

test("readInstalledExtensions returns empty Set when no state file", async () => {
  const project = await mkdtemp(join(tmpdir(), "unity-cli-ext-store-"));
  try {
    const enabled = await readInstalledExtensions(project);
    assert.equal(enabled.size, 0);
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("writeInstalledExtensions then readInstalledExtensions round-trips", async () => {
  const project = await mkdtemp(join(tmpdir(), "unity-cli-ext-store-"));
  try {
    await writeInstalledExtensions(project, new Set(["probuilder", "vfx-graph"]));
    const enabled = await readInstalledExtensions(project);
    assert.ok(enabled.has("probuilder"));
    assert.ok(enabled.has("vfx-graph"));
    assert.equal(enabled.size, 2);
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("writeInstalledExtensions creates nested directories", async () => {
  const project = await mkdtemp(join(tmpdir(), "unity-cli-ext-store-"));
  try {
    // State path is <project>/Packages/com.unity_cli/extensions.json — both dirs absent
    await writeInstalledExtensions(project, new Set(["probuilder"]));
    const stateContent = await readFile(
      join(project, "Packages", "com.unity_cli", "extensions.json"),
      "utf8"
    );
    const parsed = JSON.parse(stateContent) as { extensions: string[] };
    assert.deepEqual(parsed.extensions, ["probuilder"]);
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("readInstalledExtensions handles malformed state file as empty", async () => {
  const project = await mkdtemp(join(tmpdir(), "unity-cli-ext-store-"));
  try {
    await mkdir(join(project, "Packages", "com.unity_cli"), { recursive: true });
    await writeFile(
      join(project, "Packages", "com.unity_cli", "extensions.json"),
      "not valid json"
    );
    const enabled = await readInstalledExtensions(project);
    assert.equal(enabled.size, 0);
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});
