import assert from "node:assert/strict";
import test from "node:test";
import { execute } from "../src/cli.js";
import { JobRunner } from "../src/job-runner.js";
import { JobStore } from "../src/job-store.js";
import { bridgeCommandRegistry, type BridgeRequest } from "../src/bridge/index.js";
import { mkdtemp, readFile } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

async function makeRuntime() {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-security-"));
  const store = new JobStore(dataDir);
  return { dataDir, store, runner: new JobRunner(store, dataDir) };
}

test("bridge registry marks destructive commands from security policy", () => {
  const assetDelete = bridgeCommandRegistry.lookup("asset", "delete");
  const sceneSave = bridgeCommandRegistry.lookup("scene", "save");
  const graphicsBakeStart = bridgeCommandRegistry.lookup("graphics", "bake-start");
  const graphicsBakeClear = bridgeCommandRegistry.lookup("graphics", "bake-clear");
  const graphicsBakeReflectionProbe = bridgeCommandRegistry.lookup("graphics", "bake-reflection-probe");

  assert.equal(assetDelete?.destructiveHint, true);
  assert.equal(assetDelete?.supportsDryRun, true);
  assert.equal(sceneSave?.destructiveHint, true);
  assert.equal(sceneSave?.supportsDryRun, true);
  assert.equal(graphicsBakeStart?.destructiveHint, true);
  assert.equal(graphicsBakeStart?.supportsDryRun, undefined);
  assert.equal(graphicsBakeClear?.destructiveHint, true);
  assert.equal(graphicsBakeClear?.supportsDryRun, undefined);
  assert.equal(graphicsBakeReflectionProbe?.destructiveHint, true);
  assert.equal(graphicsBakeReflectionProbe?.supportsDryRun, undefined);
});

test("security policy covers all destructive commands including CLI-only", async () => {
  const { destructiveCommands } = await import("../src/security-policy.js");
  const policyCommands = destructiveCommands().map((e: { command: string }) => e.command);
  const expected = [
    "asset.delete", "animation.clip.create", "animation.clip.modify", "scriptable-object.create", "scriptable-object.modify", "scene.save", "code.exec",
    "probuilder.delete-faces", "probuilder.detach-faces", "probuilder.combine-meshes",
    "probuilder.merge-objects", "probuilder.flip-normals",
    "cinemachine.extension", "urp.feature", "editor.menu", "package.install", "package.remove",
    "batch.run", "instances.prune", "jobs.cancel", "controller.remove-layer", "graphics.bake-start", "graphics.bake-clear", "graphics.bake-reflection-probe",
    "package.embed", "ui.create-panel-settings", "ui.document.update", "ui.document.delete", "ui.update-panel-settings", "ui.link-stylesheet", "ui.render-preview",
    "texture.modify", "texture.delete", "texture.generate", "shader.delete", "screenshot.surround",
    "object.modify", "reflection.method.call"
  ];
  assert.deepEqual(policyCommands.sort(), expected.sort());
  assert.equal(destructiveCommands().length, 38);
});

test("Unity bridge destructive proof list matches persistent-risk policy", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/UnityCliBridge.cs", import.meta.url), "utf8");
  const match = source.match(/private static bool RequiresDestructiveSecurityProof[\s\S]*?\n        }/);
  assert.ok(match, "RequiresDestructiveSecurityProof should exist");
  const body = match[0];
  for (const command of ["asset.delete", "animation.clip.create", "animation.clip.modify", "scene.save", "code.exec", "editor.menu", "reflection.method.call", "screenshot.surround"]) {
    assert.match(body, new RegExp(`command == "${command.replace(/\./g, "\\.")}"`), `${command} should require bridge security proof`);
  }
  for (const command of ["go.delete", "component.remove", "go.modify", "component.modify"]) {
    assert.doesNotMatch(body, new RegExp(`command == "${command.replace(/\./g, "\\.")}"`), `${command} only mutates scene/session state and must not require bridge security proof`);
  }
});

test("destructive dry-run forwards envelope preview without --yes", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];

  const result = await execute(["tools", "run", "asset.delete", "Assets/DeleteMe.asset", "--dry-run"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { dryRun: payload.dryRun, plannedEffects: [{ type: "asset.delete", path: payload.args.path }] } };
    }
  });

  assert.equal(result.ok, true);
  assert.equal(requests[0].dryRun, true);
  assert.equal(requests[0].args.dryRun, true);
  assert.equal(requests[0].security?.confirmed, false);
  assert.equal(requests[0].security?.destructiveFingerprint, "destructive-safety-v1");
});

test("code exec dry-run returns local preview and does not call bridge", async () => {
  const runtime = await makeRuntime();
  let called = false;

  const result = await execute(["tools", "run", "code.exec", "--code", "Debug.Log(1);", "--dry-run"], {
    ...runtime,
    bridgeExecute: async () => {
      called = true;
      return { ok: true, data: {} };
    }
  });

  assert.equal(result.ok, true);
  assert.equal(called, false);
  assert.equal((result.data as { dryRun: boolean }).dryRun, true);
  assert.equal((result.data as { preview: { sourceSha256: string } }).preview.sourceSha256.length, 64);
});
