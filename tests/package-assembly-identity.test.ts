import assert from "node:assert/strict";
import { copyFile, mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { readAssemblyIdentity } from "../src/assembly-identity.js";
import { inspectPackageAssemblyConflicts } from "../src/package-manager.js";

const bundledHarmony = join(process.cwd(), "unitypkg", "Editor", "Plugins", "0Harmony.dll");

test("reads the exact identity of the bundled Harmony dependency", async () => {
  const identity = await readAssemblyIdentity(bundledHarmony);
  assert.ok(identity);
  assert.equal(identity!.name, "0Harmony");
  assert.equal(identity!.version, "2.4.2.0");
  assert.equal(identity!.culture, "");
  assert.equal(identity!.publicKeyToken, null);
});

test("returns null for files that are not managed assemblies", async () => {
  const root = await mkdtemp(join(tmpdir(), "unitycli-identity-"));
  try {
    const textPath = join(root, "not-a-dll.dll");
    await writeFile(textPath, "this is not a portable executable", "utf8");
    assert.equal(await readAssemblyIdentity(textPath), null);
    assert.equal(await readAssemblyIdentity(join(root, "missing.dll")), null);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("project assembly scan covers Assets and detects a renamed protected identity", async () => {
  const root = await mkdtemp(join(tmpdir(), "unitycli-conflict-"));
  try {
    await mkdir(join(root, "Assets", "Plugins"), { recursive: true });
    await copyFile(bundledHarmony, join(root, "Assets", "Plugins", "RenamedDependency.dll"));
    const inspection = await inspectPackageAssemblyConflicts(root);
    assert.equal(inspection.status, "conflict");
    const conflict = inspection.conflicts.find((entry) => entry.relativePath === "Assets/Plugins/RenamedDependency.dll");
    assert.ok(conflict, `expected an Assets conflict, received ${JSON.stringify(inspection.conflicts)}`);
    assert.equal(conflict!.source, "Assets");
    assert.equal(conflict!.provider, "Assets/Plugins");
    assert.match(conflict!.assemblyIdentity ?? "", /^0Harmony, Version=2\.4\.2\.0/);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("project assembly scan detects a protected file name under Assets", async () => {
  const root = await mkdtemp(join(tmpdir(), "unitycli-conflict-name-"));
  try {
    await mkdir(join(root, "Assets"), { recursive: true });
    await copyFile(bundledHarmony, join(root, "Assets", "0Harmony.dll"));
    const inspection = await inspectPackageAssemblyConflicts(root);
    assert.equal(inspection.status, "conflict");
    assert.equal(inspection.conflicts[0].relativePath, "Assets/0Harmony.dll");
    assert.equal(inspection.conflicts[0].source, "Assets");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("project assembly scan ignores an Assets copy of the bridge package itself", async () => {
  const root = await mkdtemp(join(tmpdir(), "unitycli-conflict-self-"));
  try {
    await mkdir(join(root, "Assets", "com.alpsckr.unitycli", "Editor", "Plugins"), { recursive: true });
    await copyFile(bundledHarmony, join(root, "Assets", "com.alpsckr.unitycli", "Editor", "Plugins", "0Harmony.dll"));
    const inspection = await inspectPackageAssemblyConflicts(root);
    assert.equal(inspection.status, "clear", JSON.stringify(inspection.conflicts));
    assert.equal(inspection.conflicts.length, 0);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("project assembly scan reports clear when no protected assembly exists", async () => {
  const root = await mkdtemp(join(tmpdir(), "unitycli-conflict-clear-"));
  try {
    await mkdir(join(root, "Assets", "Scripts"), { recursive: true });
    await writeFile(join(root, "Assets", "Scripts", "Program.cs"), "// nothing to scan", "utf8");
    const inspection = await inspectPackageAssemblyConflicts(root);
    assert.equal(inspection.status, "clear");
    assert.equal(inspection.scannedDirectories >= 1, true);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
