import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { deployPackageSource } from "../src/package-manager.js";
import { runPackageFileMutationWorker } from "../src/tools/package/file-mutation-worker.js";

/** 构造最小 Unity manifest，验证生产 Worker 在退出后才向父进程交付结果。 */
async function projectFixture(): Promise<{ root: string; project: string; manifest: string }> {
  const root = await mkdtemp(join(tmpdir(), "unity-cli-package-worker-"));
  const project = join(root, "Project");
  const packages = join(project, "Packages");
  const manifest = join(packages, "manifest.json");
  await mkdir(packages, { recursive: true });
  await writeFile(manifest, `${JSON.stringify({ dependencies: {} }, null, 2)}\n`, "utf8");
  return { root, project, manifest };
}

test("Package file mutation Worker exits before returning a committed manifest readback", async () => {
  const fixture = await projectFixture();
  try {
    const result = await runPackageFileMutationWorker(
      "install",
      fixture.project,
      { name: "com.acme.worker", version: "1.2.3" },
      Date.now() + 5_000,
      new AbortController().signal,
    );
    assert.equal(result.installedVersion, "1.2.3");
    const manifest = JSON.parse(await readFile(fixture.manifest, "utf8")) as { dependencies: Record<string, string> };
    assert.equal(manifest.dependencies["com.acme.worker"], "1.2.3");
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});

test("Package deploy Worker consumes the frozen backup path for an existing target", async () => {
  const fixture = await projectFixture();
  try {
    const packageName = "com.acme.worker-deploy";
    const source = join(fixture.root, "Source");
    const target = join(fixture.project, "Packages", packageName);
    await mkdir(source, { recursive: true });
    await mkdir(target, { recursive: true });
    await writeFile(join(source, "package.json"), `${JSON.stringify({ name: packageName, version: "2.0.0" })}\n`, "utf8");
    await writeFile(join(source, "new.txt"), "new\n", "utf8");
    await writeFile(join(target, "package.json"), `${JSON.stringify({ name: packageName, version: "1.0.0" })}\n`, "utf8");
    await writeFile(join(target, "old.txt"), "old\n", "utf8");

    const plan = await deployPackageSource(fixture.project, source, { dryRun: true });
    assert.ok(plan.backupPath);
    const result = await runPackageFileMutationWorker(
      "deploy",
      fixture.project,
      plan as unknown as Record<string, unknown>,
      Date.now() + 5_000,
      new AbortController().signal,
    );

    assert.equal(result.backupPath, plan.backupPath);
    assert.equal(await readFile(join(plan.backupPath, "old.txt"), "utf8"), "old\n");
    const backupEntries = await readdir(join(fixture.project, "Packages", ".unity_cli_deploy_backups"), { withFileTypes: true });
    assert.equal(backupEntries.filter((entry) => entry.isDirectory()).length, 1);
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});

test("Repeated deploy without an original target does not invent a backup path", async () => {
  const fixture = await projectFixture();
  try {
    const packageName = "com.acme.worker-repeat";
    const source = join(fixture.root, "RepeatSource");
    await mkdir(source, { recursive: true });
    await writeFile(join(source, "package.json"), `${JSON.stringify({ name: packageName, version: "1.0.0" })}\n`, "utf8");
    await deployPackageSource(fixture.project, source);

    const plan = await deployPackageSource(fixture.project, source, { dryRun: true });
    assert.equal(plan.repeatedDeployment, true);
    assert.equal(plan.backupCreated, false);
    assert.equal(plan.backupPath, undefined);
    const result = await runPackageFileMutationWorker(
      "deploy",
      fixture.project,
      plan as unknown as Record<string, unknown>,
      Date.now() + 5_000,
      new AbortController().signal,
    );
    assert.equal(result.backupPath, undefined);
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});

test("Package file mutation Worker rejects an exhausted reserved deadline without writing", async () => {
  const fixture = await projectFixture();
  try {
    await assert.rejects(
      () => runPackageFileMutationWorker(
        "install",
        fixture.project,
        { name: "com.acme.expired", version: "1.0.0" },
        Date.now() - 1,
        new AbortController().signal,
      ),
      (error: unknown) => (error as { code?: string }).code === "E_EXECUTION_TIMEOUT",
    );
    const manifest = JSON.parse(await readFile(fixture.manifest, "utf8")) as { dependencies: Record<string, string> };
    assert.equal(manifest.dependencies["com.acme.expired"], undefined);
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});
