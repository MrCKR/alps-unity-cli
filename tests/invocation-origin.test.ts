import assert from "node:assert/strict";
import { mkdtemp } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";
import test from "node:test";
import { execute } from "../src/cli.js";
import { JobRunner } from "../src/job-runner.js";
import { JobStore } from "../src/job-store.js";
import type { BridgeRequest } from "../src/bridge/index.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

async function makeRuntime() {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-origin-"));
  const store = new JobStore(dataDir);
  return { dataDir, store, runner: new JobRunner(store, dataDir) };
}

test("arbitrary execution rejects ordinary direct delivery before Bridge dispatch", async () => {
  const runtime = await makeRuntime();
  let bridgeCalls = 0;
  const bridgeExecute = async (payload: BridgeRequest) => {
    bridgeCalls += 1;
    return { ok: true as const, requestId: payload.requestId, data: { completed: true } };
  };

  const reflection = await execute([
    "tools", "run", "reflection.method.call", "--yes", "--input",
    JSON.stringify({ typeName: "UnityEngine.Mathf", methodName: "Abs", args: [-1], parameterTypes: ["System.Int32"] })
  ], { ...runtime, bridgeExecute });
  const code = await execute(["tools", "run", "code.exec", "--yes", "--input", JSON.stringify({ code: "return 1;" })], { ...runtime, bridgeExecute });
  const crossfade = await execute(["tools", "run", "animator.crossfade", "--yes", "--input", JSON.stringify({ target: "Player", state: "Run" })], { ...runtime, bridgeExecute });

  assert.ok(!reflection.ok);
  assert.equal(reflection.error.code, "E_DIRECT_BUSINESS_PROOF_UNAVAILABLE");
  assert.ok(!code.ok);
  assert.equal(code.error.code, "E_DIRECT_BUSINESS_PROOF_UNAVAILABLE");
  assert.ok(!crossfade.ok);
  assert.equal(crossfade.error.code, "E_DIRECT_BUSINESS_PROOF_UNAVAILABLE");
  assert.equal(bridgeCalls, 0);
});

test("frozen batch origin preserves the existing reflection step route", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const inline = JSON.stringify({
    steps: [{
      id: "reflection",
      command: ["tools", "run", "reflection.method.call"],
      args: ["--yes", "--input", JSON.stringify({ typeName: "UnityEngine.Mathf", methodName: "Abs", args: [-1], parameterTypes: ["System.Int32"] })]
    }]
  });

  const result = await execute(["batch", "run", "--input", inline, "--yes"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true as const, requestId: payload.requestId, data: { command: "project-tool.run", result: { data: 1 } } };
    }
  });

  assert.equal(result.ok, true);
  assert.equal(requests.length, 1);
  assert.equal(requests[0].command, "project-tool.run");
  assert.equal((requests[0].input as { name?: string }).name, "reflection.method.call");
});
