import assert from "node:assert/strict";
import { copyFile, mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { readAssemblyIdentity } from "../src/assembly-identity.js";
import { inspectPackageAssemblyConflicts, preparePackageBridgeInstall } from "../src/package-manager.js";

const bundledHarmony = join(process.cwd(), "unitypkg", "Editor", "Plugins", "0Harmony.dll");

/** 用临时契约覆盖预算阈值，使规模兜底维度可在小夹具上直接验证。 */
async function writePolicyContract(root: string, patch: (policy: Record<string, number>) => void): Promise<string> {
  const contract = JSON.parse(await readFile(join(process.cwd(), "contracts", "package-assembly-closure.json"), "utf8"));
  patch(contract.conflictPolicy);
  const path = join(root, "assembly-closure.json");
  await writeFile(path, JSON.stringify(contract, null, 2), "utf8");
  return path;
}

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

test("project assembly scan counts only DLL and protected-name files as candidates", async () => {
  const root = await mkdtemp(join(tmpdir(), "unitycli-conflict-candidates-"));
  try {
    await mkdir(join(root, "Assets", "Art"), { recursive: true });
    for (const name of ["texture.png", "texture.png.meta", "Program.cs", "notes.txt"]) {
      await writeFile(join(root, "Assets", "Art", name), "not an assembly", "utf8");
    }
    await writeFile(join(root, "Assets", "Art", "ThirdParty.dll"), "not a portable executable", "utf8");
    const inspection = await inspectPackageAssemblyConflicts(root);
    assert.equal(inspection.status, "clear");
    assert.equal(inspection.assemblyCandidates, 1);
    assert.ok(inspection.scannedEntries >= 5, `every entry must still be traversed, received ${inspection.scannedEntries}`);
    const assets = inspection.roots.find((entry) => entry.source === "Assets");
    assert.ok(assets, `expected an Assets root scan, received ${JSON.stringify(inspection.roots)}`);
    assert.equal(assets!.assemblyCandidates, 1);
    assert.ok(assets!.entries >= 5);
    assert.equal(assets!.directories, 2);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("unrelated project files never consume an assembly budget", async () => {
  const root = await mkdtemp(join(tmpdir(), "unitycli-conflict-bulk-"));
  try {
    const assetDirectory = join(root, "Assets", "Bulk");
    await mkdir(assetDirectory, { recursive: true });
    for (let batch = 0; batch < 20; batch += 1) {
      await Promise.all(Array.from({ length: 100 }, (_unused, offset) => writeFile(join(assetDirectory, `cache-${batch * 100 + offset}.meta`), "", "utf8")));
    }
    const inspection = await inspectPackageAssemblyConflicts(root);
    assert.equal(inspection.status, "clear");
    assert.equal(inspection.assemblyCandidates, 0);
    assert.ok(inspection.scannedEntries >= 2000, `expected all bulk files to be traversed, received ${inspection.scannedEntries}`);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("assembly conflict reports whether a project copy is byte-identical to the packaged dependency", async () => {
  const root = await mkdtemp(join(tmpdir(), "unitycli-conflict-identical-"));
  try {
    await mkdir(join(root, "Assets", "ThirdParty"), { recursive: true });
    await copyFile(bundledHarmony, join(root, "Assets", "ThirdParty", "0Harmony.dll"));
    const identical = await inspectPackageAssemblyConflicts(root);
    assert.equal(identical.status, "conflict");
    assert.equal(identical.conflicts[0].identicalToPackageDependency, true);

    await rm(join(root, "Assets", "ThirdParty", "0Harmony.dll"));
    await writeFile(join(root, "Assets", "ThirdParty", "0Harmony.dll"), "another 0Harmony build", "utf8");
    const different = await inspectPackageAssemblyConflicts(root);
    assert.equal(different.status, "conflict");
    assert.equal(different.conflicts[0].assemblyFileName, "0Harmony.dll");
    assert.equal(different.conflicts[0].identicalToPackageDependency, undefined);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("assembly conflict guidance tells the caller how to resolve it instead of only blocking", async () => {
  const root = await mkdtemp(join(tmpdir(), "unitycli-conflict-guidance-"));
  try {
    await mkdir(join(root, "Assets", "ThirdParty"), { recursive: true });
    await copyFile(bundledHarmony, join(root, "Assets", "ThirdParty", "0Harmony.dll"));
    await assert.rejects(() => preparePackageBridgeInstall(root), (error: unknown) => {
      const typed = error as { code?: string; nextActions?: string[] };
      const actions = (typed.nextActions ?? []).join("\n");
      assert.equal(typed.code, "PACKAGE_ASSEMBLY_CONFLICT");
      assert.match(actions, /nothing was installed/i);
      assert.match(actions, /identicalToPackageDependency/);
      assert.match(actions, /never delete, move, or rename project files yourself/i);
      assert.match(actions, /cannot be installed for this project/i);
      return true;
    });
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("scale backstops fail closed with a budget error that is not a conflict report", async () => {
  const root = await mkdtemp(join(tmpdir(), "unitycli-conflict-budget-"));
  try {
    const assetDirectory = join(root, "Assets", "ThirdParty");
    await mkdir(assetDirectory, { recursive: true });
    for (let batch = 0; batch < 41; batch += 1) {
      await Promise.all(Array.from({ length: 100 }, (_unused, offset) => writeFile(join(assetDirectory, `library-${batch * 100 + offset}.dll`), "not a portable executable", "utf8")));
    }
    await assert.rejects(() => inspectPackageAssemblyConflicts(root), (error: unknown) => {
      const typed = error as {
        code?: string;
        nextActions?: string[];
        details?: { budget?: string; limits?: Record<string, number>; observed?: Record<string, number>; largestDirectories?: unknown[]; roots?: unknown[] };
      };
      assert.equal(typed.code, "PACKAGE_ASSEMBLY_SCAN_BUDGET_EXHAUSTED");
      assert.equal(typed.details?.budget, "identityReads");
      assert.equal(typed.details?.limits?.maxIdentityFiles, 4096);
      assert.equal(typed.details?.observed?.scannedIdentityFiles, 4097);
      assert.ok((typed.details?.roots ?? []).length >= 1);
      assert.ok((typed.details?.largestDirectories ?? []).length >= 1);
      const actions = typed.nextActions ?? [];
      assert.ok(actions.length > 0);
      assert.ok(!actions.some((action) => /remove or update the conflicting package/i.test(action)), `budget guidance must not tell the caller to remove a conflicting package: ${JSON.stringify(actions)}`);
      return true;
    });
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("assembly scan stops at the conflict enumeration limit and keeps the exact paths", async () => {
  const root = await mkdtemp(join(tmpdir(), "unitycli-conflict-limit-"));
  try {
    for (let index = 0; index < 205; index += 1) {
      const directory = join(root, "Assets", `Copy${String(index).padStart(3, "0")}`);
      await mkdir(directory, { recursive: true });
      await writeFile(join(directory, "0Harmony.dll"), "same file name, different bytes", "utf8");
    }
    await assert.rejects(() => inspectPackageAssemblyConflicts(root), (error: unknown) => {
      const typed = error as { code?: string; details?: { conflicts?: unknown[] }; nextActions?: string[] };
      assert.equal(typed.code, "PACKAGE_ASSEMBLY_CONFLICT_LIMIT");
      assert.equal(typed.details?.conflicts?.length, 200);
      assert.ok((typed.nextActions ?? []).length > 0);
      return true;
    });
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("every scan scale dimension fails closed and an invalid policy stays a source integrity error", async () => {
  const root = await mkdtemp(join(tmpdir(), "unitycli-budget-dimensions-"));
  try {
    await mkdir(join(root, "Assets", "Nested"), { recursive: true });
    for (let index = 0; index < 6; index += 1) await writeFile(join(root, "Assets", "Nested", `file-${index}.png`), "", "utf8");
    for (const name of ["first.dll", "second.dll"]) await writeFile(join(root, "Assets", "Nested", name), "not a portable executable", "utf8");

    const entriesContract = await writePolicyContract(root, (policy) => { policy.maxScannedEntries = 3; });
    await assert.rejects(() => inspectPackageAssemblyConflicts(root, entriesContract), (error: unknown) => {
      const typed = error as { code?: string; details?: { budget?: string; observed?: Record<string, number>; limits?: Record<string, number> } };
      assert.equal(typed.code, "PACKAGE_ASSEMBLY_SCAN_BUDGET_EXHAUSTED");
      assert.equal(typed.details?.budget, "entries");
      assert.ok((typed.details?.observed?.scannedEntries ?? 0) > (typed.details?.limits?.maxScannedEntries ?? 0));
      return true;
    });

    const directoriesContract = await writePolicyContract(root, (policy) => { policy.maxDirectories = 1; });
    await assert.rejects(() => inspectPackageAssemblyConflicts(root, directoriesContract), (error: unknown) => {
      const typed = error as { code?: string; details?: { budget?: string } };
      assert.equal(typed.code, "PACKAGE_ASSEMBLY_SCAN_BUDGET_EXHAUSTED");
      assert.equal(typed.details?.budget, "directories");
      return true;
    });

    const identityContract = await writePolicyContract(root, (policy) => { policy.maxIdentityFiles = 1; });
    await assert.rejects(() => inspectPackageAssemblyConflicts(root, identityContract), (error: unknown) => {
      const typed = error as { code?: string; details?: { budget?: string } };
      assert.equal(typed.code, "PACKAGE_ASSEMBLY_SCAN_BUDGET_EXHAUSTED");
      assert.equal(typed.details?.budget, "identityReads");
      return true;
    });

    const invalidContract = await writePolicyContract(root, (policy) => { policy.maxConflicts = 0; });
    await assert.rejects(() => inspectPackageAssemblyConflicts(root, invalidContract), { code: "PACKAGE_SOURCE_INTEGRITY_FAILED" });
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
