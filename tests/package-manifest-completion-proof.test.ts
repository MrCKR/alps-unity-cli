import assert from "node:assert/strict";
import test from "node:test";
import { generatedToolCompletionContract } from "../src/tools/.generated-completion-registry.js";
import { validateLocalCompletionProof } from "../src/tools/dispatcher.js";
import { StructuredToolError } from "../src/tools/errors.js";

const hash = "a".repeat(64);
const resolve = {
  status: "resolved",
  manifestHash: hash,
  lockHash: "b".repeat(64),
  packageSnapshotHash: "c".repeat(64),
  packageCount: 1,
  packages: [{ name: "com.acme.sample", version: "1.2.3", source: "registry" }],
};

/** 使用生成 completion contract 验证 TS-local Package terminal。 */
function validate(name: string, result: unknown): void {
  const completion = generatedToolCompletionContract(name);
  assert.ok(completion, name);
  validateLocalCompletionProof(name, completion, result);
}

function invalid(name: string, result: unknown): Promise<void> {
  return assert.rejects(
    async () => validate(name, result),
    (error: unknown) => error instanceof StructuredToolError && error.code === "E_TOOL_COMPLETION_PROOF_INVALID",
  );
}

test("package.install requires independent manifest, dependency, files, and PackageInfo proof", async () => {
  const terminal = {
    action: "install",
    manifestPath: "Packages/manifest.json",
    manifestHash: hash,
    files: { proven: true, manifestHash: hash },
    dependency: { present: true, name: "com.acme.sample", version: "1.2.3" },
    resolve,
  };
  validate("package.install", terminal);
  await invalid("package.install", { ...terminal, files: { proven: false, manifestHash: hash } });
  await invalid("package.install", { ...terminal, dependency: { present: false, name: "com.acme.sample", version: "1.2.3" } });
  await invalid("package.install", { ...terminal, resolve: { status: "requested" } });
});

test("package.remove requires absent dependency readback and terminal PackageInfo proof", async () => {
  const terminal = {
    action: "remove",
    manifestPath: "Packages/manifest.json",
    manifestHash: hash,
    files: { proven: true, manifestHash: hash },
    dependency: { present: false, name: "com.acme.sample" },
    resolve,
  };
  validate("package.remove", terminal);
  await invalid("package.remove", { ...terminal, dependency: { present: true, name: "com.acme.sample" } });
  await invalid("package.remove", { ...terminal, resolve: { ...resolve, status: "failed" } });
});

test("package.deploy and restore require filesystem proof plus terminal resolve", async () => {
  validate("package.deploy", {
    status: "deployed",
    targetPath: "Packages/com.alpsckr.unitycli",
    sourceHash: hash,
    copiedFileCount: 2,
    files: { proven: true, sourceHash: hash },
    resolve,
  });
  validate("package.restore", {
    status: "restored",
    targetPath: "Packages/com.alpsckr.unitycli",
    copiedFileCount: 0,
    files: { proven: true, restoredFromBackup: false, removedDeployedPackage: true },
    resolve,
  });
  await invalid("package.deploy", {
    status: "deployed",
    targetPath: "Packages/com.alpsckr.unitycli",
    sourceHash: hash,
    copiedFileCount: 2,
    files: { proven: true, sourceHash: hash },
    resolve: { status: "accepted" },
  });
});

test("package.resolve accepts only a fresh terminal PackageInfo snapshot", async () => {
  validate("package.resolve", resolve);
  await invalid("package.resolve", { ...resolve, packageSnapshotHash: undefined });
  await invalid("package.resolve", { ...resolve, packages: [{ name: "com.acme.sample", version: "1.2.3" }] });
});

test("intermediate and nested failed states never become a successful Package terminal", async () => {
  await invalid("package.resolve", { ...resolve, accepted: true });
  await invalid("package.resolve", { ...resolve, child: { status: "failed" } });
});
