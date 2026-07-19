import assert from "node:assert/strict";
import { mkdtemp, mkdir, readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";
import { tmpdir } from "node:os";
import {
  addScopedRegistry,
  clearRegistry,
  installPackage,
  removePackage,
  removeScopedRegistry,
  setRegistry
} from "../src/package-manager.js";
import { validateLocalCompletionProof } from "../src/tools/dispatcher.js";
import { generatedToolCompletionContract } from "../src/tools/.generated-completion-registry.js";

async function makeProject(): Promise<string> {
  const projectPath = await mkdtemp(join(tmpdir(), "unity-cli-package-proof-"));
  await mkdir(join(projectPath, "Packages"));
  await writeFile(join(projectPath, "Packages", "manifest.json"), JSON.stringify({
    dependencies: { "com.example.base": "1.0.0" },
    registry: "https://old.registry.test",
    scopedRegistries: [{ name: "Old", url: "https://old.registry.test", scopes: ["com.old"] }]
  }, null, 2));
  return projectPath;
}

async function manifest(projectPath: string): Promise<Record<string, unknown>> {
  return JSON.parse(await readFile(join(projectPath, "Packages", "manifest.json"), "utf8")) as Record<string, unknown>;
}

function completion(toolName: string) {
  const contract = generatedToolCompletionContract(toolName);
  assert.ok(contract, `missing completion contract for ${toolName}`);
  return contract;
}

test("package manifest mutations return verified disk readback", async () => {
  const projectPath = await makeProject();

  const set = await setRegistry(projectPath, "https://new.registry.test");
  assert.equal(set.registry, (await manifest(projectPath)).registry);
  validateLocalCompletionProof("package.registry.set", completion("package.registry.set"), { ...set, projectPath }, false);

  const clear = await clearRegistry(projectPath);
  assert.equal(clear.registryCleared, true);
  assert.equal(Object.hasOwn(await manifest(projectPath), "registry"), false);
  validateLocalCompletionProof("package.registry.clear", completion("package.registry.clear"), { ...clear, projectPath }, false);

  const added = await addScopedRegistry(projectPath, { name: "Acme", url: "https://registry.acme.test", scopes: ["com.acme", "com.acme"] });
  const persistedRegistries = (await manifest(projectPath)).scopedRegistries as Array<{ name: string; url: string; scopes: string[] }>;
  assert.deepEqual(added.registry, persistedRegistries.find((entry) => entry.name === "Acme"));
  validateLocalCompletionProof("package.scoped-registry.add", completion("package.scoped-registry.add"), { ...added, projectPath }, false);

  const removedRegistry = await removeScopedRegistry(projectPath, "Acme", { force: true });
  assert.equal(removedRegistry.registryRemoved, true);
  assert.equal(((await manifest(projectPath)).scopedRegistries as Array<{ name: string }>).some((entry) => entry.name === "Acme"), false);
  validateLocalCompletionProof("package.scoped-registry.remove", completion("package.scoped-registry.remove"), { action: "remove", ...removedRegistry, projectPath }, false);

  const installed = await installPackage(projectPath, { name: "com.acme.tool", version: "2.0.0" });
  assert.equal(installed.installedVersion, ((await manifest(projectPath)).dependencies as Record<string, string>)["com.acme.tool"]);
  validateLocalCompletionProof("package.install", completion("package.install"), { action: "install", package: "com.acme.tool", ...installed, projectPath }, false);

  const removedPackage = await removePackage(projectPath, "com.acme.tool", { force: true });
  assert.equal(removedPackage.dependencyRemoved, true);
  assert.equal(Object.hasOwn((await manifest(projectPath)).dependencies as Record<string, string>, "com.acme.tool"), false);
  validateLocalCompletionProof("package.remove", completion("package.remove"), { action: "remove", package: "com.acme.tool", ...removedPackage, projectPath }, false);
});

test("package completion proof rejects missing readback and nested failed terminals", () => {
  assert.throws(() => validateLocalCompletionProof("package.install", completion("package.install"), {
    action: "install", manifestPath: "D:/project/Packages/manifest.json", installedVersion: "2.0.0"
  }, false), /manifest installation readback/);

  assert.throws(() => validateLocalCompletionProof("package.remove", completion("package.remove"), {
    action: "remove", manifestPath: "D:/project/Packages/manifest.json", dependencyRemoved: true,
    liveRefresh: { status: "timeout" }
  }, false), /returned failed terminal status timeout/);

  assert.throws(() => validateLocalCompletionProof("package.registry.clear", completion("package.registry.clear"), {
    manifestPath: "D:/project/Packages/manifest.json", registryCleared: false
  }, false), /cleared manifest registry readback/);
});
