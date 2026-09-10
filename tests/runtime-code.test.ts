import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import type { BridgeRequest } from "../src/bridge/index.js";
import { execute } from "../src/cli.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

const probe = {
  readTool: "scene.gameobject",
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

test("code.execute rejects malformed completionProbe before Bridge dispatch", async () => {
  for (const input of [
    request({ completionProbe: { ...probe, readTool: "scene.unknown" } }),
    request({ completionProbe: { ...probe, assertions: [{ ...probe.assertions[0], operator: "unknown" }] } }),
  ]) {
    let calls = 0;
    const result = await execute(["tools", "run", "code.execute", "--input", JSON.stringify(input)], {
      ...offline,
      bridgeExecute: async () => { calls += 1; throw new Error("must not dispatch"); },
    } as never);
    assert.ok(!result.ok, JSON.stringify(result));
    assert.equal(result.error?.code, "E_SCHEMA_INVALID");
    assert.equal(calls, 0);
  }
});

test("code.execute dry-run hashes source and never calls Bridge", async () => {
  let calls = 0;
  const result = await execute(["tools", "run", "code.execute", "--input", JSON.stringify(request()), "--dry-run"], {
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

test("code.execute rejects oversized typed input file before JSON materialization", async () => {
  const root = await mkdtemp(join(tmpdir(), "code-exec-input-"));
  const inputPath = join(root, "input.json");
  await writeFile(inputPath, `{"code":"${"x".repeat(1024 * 1024)}"}`, "utf8");
  let calls = 0;
  const result = await execute(["tools", "run", "code.execute", "--input-file", inputPath], {
    ...offline,
    bridgeExecute: async () => { calls += 1; throw new Error("must not dispatch"); },
  } as never);
  assert.ok(!result.ok);
  assert.equal(result.error?.code, "E_CODE_INPUT_TOO_LARGE");
  assert.equal(calls, 0);
});

test("code.execute typed stdin stops waiting at the immutable deadline", async () => {
  const child = spawn(process.execPath, ["dist/cli.js", "tools", "run", "code.execute", "--stdin", "--timeout-ms", "100"], {
    cwd: process.cwd(),
    env: { ...process.env, UNITY_CLI_ALLOW_NON_WINDOWS: "1" },
    stdio: ["pipe", "pipe", "pipe"],
  });
  child.stdin.write('{"code":"return null;"');
  let stdout = "";
  child.stdout.setEncoding("utf8");
  child.stdout.on("data", (chunk) => { stdout += chunk; });
  const exitCode = await new Promise<number | null>((resolvePromise, rejectPromise) => {
    const timer = setTimeout(() => { child.kill(); rejectPromise(new Error("code.execute stdin did not honor timeout")); }, 2_000);
    child.once("error", rejectPromise);
    child.once("exit", (code) => { clearTimeout(timer); resolvePromise(code); });
  });
  child.stdin.destroy();
  assert.equal(exitCode, 1);
  const result = JSON.parse(stdout) as { ok: boolean; error?: { code?: string } };
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_EXECUTION_TIMEOUT");
});

test("code.execute rejects oversized source locally without Bridge dispatch", async () => {
  let calls = 0;
  const result = await execute(["tools", "run", "code.execute", "--input", JSON.stringify({ code: "x".repeat(768 * 1024 + 1) })], {
    ...offline,
    bridgeExecute: async () => { calls += 1; throw new Error("must not dispatch"); },
  } as never);
  assert.ok(!result.ok);
  assert.equal(result.error?.code, "E_CODE_INPUT_TOO_LARGE");
  assert.equal(calls, 0);
});

test("code.execute caps extra reference count before Bridge dispatch", async () => {
  let calls = 0;
  const result = await execute(["tools", "run", "code.execute", "--input", JSON.stringify({ code: "return null;", refs: Array.from({ length: 65 }, () => ".") })], {
    ...offline,
    bridgeExecute: async () => { calls += 1; throw new Error("must not dispatch"); },
  } as never);
  assert.ok(!result.ok);
  assert.equal(result.error?.code, "E_REFERENCE_RESOLUTION_FAILED");
  assert.equal(calls, 0);
});

test("code.execute Bridge request retains the dispatcher absolute deadline", async () => {
  const startedAt = Date.now();
  let captured: BridgeRequest | undefined;
  const result = await execute(["tools", "run", "code.execute", "--timeout-ms", "5000", "--input", JSON.stringify({ code: "return null;" })], {
    ...offline,
    bridgeExecute: async (payload: BridgeRequest) => {
      captured = payload;
      return { ok: true as const, requestId: payload.requestId, data: { compile: { diagnostics: [] }, runtime: { returnValue: null, stdout: "", stderr: "", stdoutTruncated: false, stderrTruncated: false }, completion: null } };
    },
  } as never);
  assert.ok(result.ok, JSON.stringify(result));
  assert.ok(captured);
  const deadline = Date.parse(captured!.execution.absoluteDeadlineUtc);
  assert.ok(deadline >= startedAt + 4_000 && deadline <= startedAt + 5_250, `${startedAt} ${deadline}`);
});

test("code.execute direct success without completionProbe returns runtime data", async () => {
  const requests: BridgeRequest[] = [];
  const result = await execute(["tools", "run", "code.execute", "--input", JSON.stringify({ code: "return 1;" })], {
    ...offline,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return {
        ok: true as const,
        requestId: payload.requestId,
        data: {
          compile: { diagnostics: [] },
          runtime: { returnValue: 1, stdout: "", stderr: "", stdoutTruncated: false, stderrTruncated: false },
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

test("code.execute direct success carries exact probe and returns fresh assertion readback", async () => {
  const requests: BridgeRequest[] = [];
  const result = await execute(["tools", "run", "code.execute", "--input", JSON.stringify(request())], {
    ...offline,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return {
        ok: true as const,
        requestId: payload.requestId,
        data: {
          compile: { diagnostics: [] },
          runtime: { returnValue: null, stdout: "", stderr: "", stdoutTruncated: false, stderrTruncated: false },
          completion: { assertions: [{ path: "/gameObject/transform/position/x", value: 1 }] },
        },
      };
    },
  } as never);

  assert.ok(result.ok, JSON.stringify(result));
  assert.equal(requests.length, 1);
  assert.equal(requests[0].command, "code.execute");
  assert.deepEqual(requests[0].input?.completionProbe, probe);
  assert.equal(Object.hasOwn(requests[0].execution, "securityProof"), false);
  assert.deepEqual((result.data as { completion: unknown }).completion, {
    assertions: [{ path: "/gameObject/transform/position/x", value: 1 }],
  });
});

test("code.execute preserves execution timeout and scrubs internal error details", async () => {
  const result = await execute(["tools", "run", "code.execute", "--input", JSON.stringify(request())], {
    ...offline,
    bridgeExecute: async (payload: BridgeRequest) => ({
      ok: false as const,
      requestId: payload.requestId,
      error: {
        code: "E_EXECUTION_TIMEOUT",
        message: "Effect began without a proven terminal.",
        nextActions: ["Run the declared read Tool before submitting another request."],
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

test("code.execute preserves nested runtime cause fields and stable error semantics", async () => {
  const causes = [
    { depth: 0, exceptionType: "System.Reflection.TargetInvocationException", message: "Invocation failed.", stackTrace: "outer" },
    { depth: 1, exceptionType: "NUnit.Framework.AssertionException", message: "Expected 2 but was 1.", stackTrace: "inner" },
  ];
  const result = await execute(["tools", "run", "code.execute", "--input", JSON.stringify({ code: "throw new Exception();" })], {
    ...offline,
    bridgeExecute: async (payload: BridgeRequest) => ({
      ok: false as const,
      requestId: payload.requestId,
      error: {
        code: "E_CODE_RUNTIME_EXCEPTION",
        message: "Expected 2 but was 1.",
        nextActions: ["Inspect the bounded runtime exception cause chain and affected business state."],
        transient: false,
        retryAfterMs: 0,
        details: {
          diagnostics: [],
          exceptionType: "NUnit.Framework.AssertionException",
          message: "Expected 2 but was 1.",
          stackTrace: "inner",
          causes,
          causeChainTruncated: false,
          stdout: "",
          stderr: "",
        },
      },
    }),
  } as never);

  assert.ok(!result.ok);
  assert.equal(result.error.code, "E_CODE_RUNTIME_EXCEPTION");
  assert.equal(result.error.message, "Expected 2 but was 1.");
  assert.equal(result.error.transient, false);
  assert.equal(result.error.retryAfterMs, 0);
  assert.deepEqual((result.error.details as { causes: unknown }).causes, causes);
  assert.equal((result.error.details as { causeChainTruncated: boolean }).causeChainTruncated, false);
  assert.deepEqual(result.error.nextActions, ["Inspect the bounded runtime exception cause chain and affected business state."]);
});
