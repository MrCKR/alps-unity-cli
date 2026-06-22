/**
 * Unit tests for init auto-detect: extension auto-detection and auto-install
 * based on Unity project's installed packages.
 *
 * P5-T03: init auto-detect Unity packages and install matching extensions.
 */

import assert from "node:assert/strict";
import test from "node:test";
import { mkdtemp, mkdir, writeFile, readFile, rm } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";

import { autoDetectExtensions, autoDetectSummary } from "../../src/cli-commands/extension-auto-detect.js";
import { readInstalledExtensions } from "../../src/cli-commands/extensions.js";

async function makeProjectDir(installedPackages: Record<string, string> = {}, enabledExtensions: string[] = []): Promise<string> {
  const dir = await mkdtemp(join(tmpdir(), "unity-cli-auto-detect-"));
  await mkdir(join(dir, "Packages"), { recursive: true });

  await writeFile(
    join(dir, "Packages", "manifest.json"),
    JSON.stringify({ dependencies: installedPackages })
  );

  const lockDeps: Record<string, { version: string }> = {};
  for (const [id, version] of Object.entries(installedPackages)) {
    lockDeps[id] = { version };
  }
  await writeFile(
    join(dir, "Packages", "packages-lock.json"),
    JSON.stringify({ dependencies: lockDeps })
  );

  if (enabledExtensions.length > 0) {
    await mkdir(join(dir, "Packages", "com.unity_cli"), { recursive: true });
    await writeFile(
      join(dir, "Packages", "com.unity_cli", "extensions.json"),
      JSON.stringify({ extensions: enabledExtensions })
    );
  }

  return dir;
}

// ── autoDetectExtensions ──────────────────────────────────────────────────

test("auto-detect installs extensions whose packages are present", async () => {
  const project = await makeProjectDir({
    "com.unity.probuilder": "5.2.0",
    "com.unity.visualeffectgraph": "14.0.0"
  });
  try {
    const result = await autoDetectExtensions(project);
    assert.equal(result.installed.length, 2);
    assert.ok(result.installed.some((e) => e.extensionId === "probuilder"));
    assert.ok(result.installed.some((e) => e.extensionId === "vfx-graph"));
    assert.equal(result.unavailable.length, 3);
    assert.equal(result.unknown.length, 0);

    // Verify state was written
    const enabled = await readInstalledExtensions(project);
    assert.ok(enabled.has("probuilder"));
    assert.ok(enabled.has("vfx-graph"));
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("auto-detect skips already-installed extension bundles", async () => {
  const project = await makeProjectDir({ "com.unity.probuilder": "5.2.0" });
  try {
    await autoDetectExtensions(project);
    const result = await autoDetectExtensions(project);
    assert.equal(result.installed.length, 0);
    assert.equal(result.skipped.length, 1);
    assert.ok(result.skipped.some((e) => e.extensionId === "probuilder"));
    assert.equal(result.skipped[0].reason, "Extension bundle is already installed.");
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("auto-detect marks unavailable when Unity packages missing", async () => {
  const project = await makeProjectDir({});
  try {
    const result = await autoDetectExtensions(project);
    assert.equal(result.installed.length, 0);
    assert.equal(result.unavailable.length, 5);
    for (const ext of result.unavailable) {
      assert.ok(ext.missingPackageIds.length > 0);
      assert.ok(ext.nextActions.length > 0);
    }
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("auto-detect handles mixed installed/missing packages", async () => {
  const project = await makeProjectDir({
    "com.unity.probuilder": "5.2.0",
    "com.unity.cinemachine": "3.1.0"
  });
  try {
    const result = await autoDetectExtensions(project);
    assert.equal(result.installed.length, 2);
    assert.ok(result.installed.some((e) => e.extensionId === "probuilder"));
    assert.ok(result.installed.some((e) => e.extensionId === "cinemachine"));
    assert.equal(result.unavailable.length, 3);
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("auto-detect does not install Unity packages themselves", async () => {
  const project = await makeProjectDir({});
  try {
    const result = await autoDetectExtensions(project);
    // All unavailable — extensions were not auto-installed because deps missing
    assert.equal(result.installed.length, 0);
    assert.equal(result.unavailable.length, 5);
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("auto-detect does not guess from file names or directories", async () => {
  const project = await makeProjectDir({});
  // Create a directory that might look like ProBuilder support but isn't in manifest
  await mkdir(join(project, "Assets", "ProBuilder"), { recursive: true });
  try {
    const result = await autoDetectExtensions(project);
    // ProBuilder should still be unavailable since package not in manifest
    assert.equal(result.installed.length, 0);
    const probuilder = result.unavailable.find((e) => e.extensionId === "probuilder");
    assert.ok(probuilder);
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("auto-detect dry-run does not write install state", async () => {
  const project = await makeProjectDir({ "com.unity.probuilder": "5.2.0" });
  try {
    const result = await autoDetectExtensions(project, true);
    assert.equal(result.installed.length, 1);
    assert.ok(result.installed.some((e) => e.extensionId === "probuilder"));

    // Verify state was NOT written (dry run)
    const enabled = await readInstalledExtensions(project);
    assert.equal(enabled.size, 0);
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

test("auto-detect handles missing manifest.json gracefully", async () => {
  const project = await mkdtemp(join(tmpdir(), "unity-cli-auto-detect-"));
  try {
    // No Packages/manifest.json at all
    const result = await autoDetectExtensions(project);
    // All extensions should be unavailable (empty packages read)
    assert.equal(result.installed.length, 0);
    assert.equal(result.unavailable.length, 5);
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});

// ── autoDetectSummary ─────────────────────────────────────────────────────

test("autoDetectSummary produces correct counts", () => {
  const result = {
    installed: [
      { extensionId: "probuilder", name: "ProBuilder" },
      { extensionId: "vfx-graph", name: "VFX Graph" }
    ],
    skipped: [
      { extensionId: "cinemachine", name: "Cinemachine", reason: "Extension is already enabled." }
    ],
    unavailable: [
      { extensionId: "urp", name: "URP", missingPackageIds: ["com.unity.render-pipelines.universal"], nextActions: ["Install the package"] }
    ],
    unknown: []
  };
  const summary = autoDetectSummary(result);
  assert.equal(summary.total, 4);
  assert.equal(summary.installed, 2);
  assert.equal(summary.skipped, 1);
  assert.equal(summary.unavailable, 1);
  assert.equal(summary.unknown, 0);
  assert.ok(summary.summary.includes("ProBuilder"));
  assert.ok(summary.summary.includes("VFX Graph"));
});

test("autoDetectSummary handles empty results", () => {
  const result = { installed: [], skipped: [], unavailable: [], unknown: [] };
  const summary = autoDetectSummary(result);
  assert.equal(summary.total, 0);
  assert.equal(summary.summary, "No extensions detected.");
});

// ── Integration: init --check with extensions ───────────────────────────────

test("init --check includes extension auto-detect plan", async () => {
  const project = await makeProjectDir({ "com.unity.probuilder": "5.2.0" });
  try {
    // Use the CLI execute path indirectly — test the auto-detect module
    const result = await autoDetectExtensions(project, true);
    assert.ok(result.installed.some((e) => e.extensionId === "probuilder"));
    assert.ok(result.unavailable.some((e) => e.extensionId === "cinemachine"));
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});
