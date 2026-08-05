import assert from "node:assert/strict";
import test from "node:test";
import type { BridgeRequest } from "../src/bridge/index.js";
import { execute } from "../src/cli.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

const probe = {
  context: "scene.gameobject",
  input: { target: "ProbeTarget", includeComponents: false },
  assertions: [{
    path: "/gameObject/transform/position/x",
    operator: "approx",
    before: 0,
    after: 1,
    tolerance: 0.0001,
  }],
};

function request(overrides: Record<string, unknown> = {}) {
  return { code: "return null;", completionProbe: probe, ...overrides };
}

const offline = { dataDir: "Z:/missing-unity-cli-data" };

test("code.exec rejects malformed completionProbe before Bridge dispatch", async () => {
  for (const input of [
    request({ completionProbe: { ...probe, context: "scene.unknown" } }),
    request({ completionProbe: { ...probe, assertions: [{ ...probe.assertions[0], operator: "unknown" }] } }),
  ]) {
    let calls = 0;
    const result = await execute(["tools", "run", "code.exec", "--input", JSON.stringify(input)], {
      ...offline,
      bridgeExecute: async () => { calls += 1; throw new Error("must not dispatch"); },
    } as never);
    assert.ok(!result.ok, JSON.stringify(result));
    assert.equal(result.error?.code, "E_SCHEMA_INVALID");
    assert.equal(calls, 0);
  }
});

test("code.exec dry-run hashes source and never calls Bridge", async () => {
  let calls = 0;
  const result = await execute(["tools", "run", "code.exec", "--input", JSON.stringify(request()), "--dry-run"], {
    ...offline,
    bridgeExecute: async () => { calls += 1; throw new Error("must not dispatch"); },
  } as never);

  assert.ok(result.ok, JSON.stringify(result));
  assert.equal(calls, 0);
  const data = result.data as { dryRun: boolean; preview: { sourceSha256: string }; plannedEffects: unknown[] };
  assert.equal(data.dryRun, true);
  assert.match(data.preview.sourceSha256, /^[a-f0-9]{64}$/);
  assert.equal(data.plannedEffects.length, 1);
});

test("code.exec direct success without completionProbe returns runtime data", async () => {
  const requests: BridgeRequest[] = [];
  const result = await execute(["tools", "run", "code.exec", "--input", JSON.stringify({ code: "return 1;" })], {
    ...offline,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return {
        ok: true as const,
        requestId: payload.requestId,
        data: {
          compile: { diagnostics: [] },
          runtime: { returnValue: 1, stdout: "", stderr: "" },
          completion: null,
        },
      };
    },
  } as never);

  assert.ok(result.ok, JSON.stringify(result));
  assert.equal(requests.length, 1);
  assert.equal(Object.prototype.hasOwnProperty.call(requests[0].input ?? {}, "completionProbe"), false);
  assert.equal((result.data as { completion: unknown }).completion, null);
});

test("code.exec direct success carries exact probe and returns fresh assertion readback", async () => {
  const requests: BridgeRequest[] = [];
  const result = await execute(["tools", "run", "code.exec", "--input", JSON.stringify(request())], {
    ...offline,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return {
        ok: true as const,
        requestId: payload.requestId,
        data: {
          compile: { diagnostics: [] },
          runtime: { returnValue: null, stdout: "", stderr: "" },
          completion: { assertions: [{ path: "/gameObject/transform/position/x", value: 1 }] },
        },
      };
    },
  } as never);

  assert.ok(result.ok, JSON.stringify(result));
  assert.equal(requests.length, 1);
  assert.equal(requests[0].command, "code.exec");
  assert.deepEqual(requests[0].input?.completionProbe, probe);
  assert.equal(Object.hasOwn(requests[0].execution, "securityProof"), false);
  assert.deepEqual((result.data as { completion: unknown }).completion, {
    assertions: [{ path: "/gameObject/transform/position/x", value: 1 }],
  });
});

test("code.exec preserves execution timeout and scrubs internal error details", async () => {
  const result = await execute(["tools", "run", "code.exec", "--input", JSON.stringify(request())], {
    ...offline,
    bridgeExecute: async (payload: BridgeRequest) => ({
      ok: false as const,
      requestId: payload.requestId,
      error: {
        code: "E_EXECUTION_TIMEOUT",
        message: "Effect began without a proven terminal.",
        nextActions: ["Inspect the target through a Context before submitting another request."],
        transient: false,
        retryAfterMs: 0,
        details: {
          endpoint: "http://127.0.0.1:54321",
          listenerGeneration: 8,
          blockers: [{
            type: "dialog",
            dialogId: "dialog_runtime",
            title: "Third Party Dialog",
            message: "Third Party Dialog",
            buttons: ["OK"],
            requiresUserAction: true,
            source: "windows-ui",
            confidence: "exact",
          }],
        },
      },
    }),
  } as never);

  assert.ok(!result.ok);
  assert.equal(result.error?.code, "E_EXECUTION_TIMEOUT");
  assert.doesNotMatch(JSON.stringify(result), /54321|endpoint|listenerGeneration/);
  assert.match(JSON.stringify(result), /Third Party Dialog/);
});
