import assert from "node:assert/strict";
import test from "node:test";
import { execute } from "./cli.js";
import { JobRunner } from "./job-runner.js";
import { JobStore } from "./job-store.js";
import { bridgeCommandRegistry, type BridgeRequest } from "./bridge/index.js";
import { mkdtemp } from "node:fs/promises";
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
  const goDelete = bridgeCommandRegistry.lookup("go", "delete");
  const sceneSave = bridgeCommandRegistry.lookup("scene", "save");

  assert.equal(assetDelete?.destructiveHint, true);
  assert.equal(assetDelete?.supportsDryRun, true);
  assert.equal(goDelete?.destructiveHint, true);
  assert.equal(goDelete?.supportsDryRun, true);
  assert.equal(sceneSave?.destructiveHint, true);
  assert.equal(sceneSave?.supportsDryRun, true);
});

test("destructive dry-run forwards envelope preview without --yes", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];

  const result = await execute(["asset", "delete", "Assets/DeleteMe.asset", "--dry-run"], {
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
  assert.equal(requests[0].security?.destructiveFingerprint, "alp332-destructive-safety-v1");
});

test("code exec dry-run returns local preview and does not call bridge", async () => {
  const runtime = await makeRuntime();
  let called = false;

  const result = await execute(["code", "exec", "--code", "Debug.Log(1);", "--dry-run"], {
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
