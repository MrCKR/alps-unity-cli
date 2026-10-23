import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import type { RootAdmissionLease, RootAdmissionRequest } from "../src/bridge/index.js";
import { dispatchTool } from "../src/tools/dispatcher.js";
import { makeLiveTool } from "./helpers/live-tool-fixtures.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

function lease(request: RootAdmissionRequest): RootAdmissionLease {
  return {
    requestId: request.requestId,
    command: request.command,
    rootFingerprint: "a".repeat(64),
    completionContractIdentity: request.completionContractIdentity,
    target: request.target,
    instanceId: `unity-${"b".repeat(64)}`,
    canonicalProjectPath: process.cwd(),
    sessionId: `session-${"c".repeat(64)}`,
    processId: process.pid,
    processStartedAt: "injected-process",
    listenerId: `listener-${"e".repeat(32)}`,
    listenerGeneration: 1,
    token: "d".repeat(64),
    consumed: false,
    grantExpiresAtMs: request.admissionAbsoluteDeadlineMs + 2_000,
    executionAbsoluteDeadlineMs: request.admissionAbsoluteDeadlineMs + request.timeoutMs,
    injected: true,
  };
}

test("ordinary Tool input parsing passes one finite absolute admission deadline", async () => {
  const root = await mkdtemp(join(tmpdir(), "unity-cli-input-deadline-"));
  const inputPath = join(root, "input.json");
  await writeFile(inputPath, JSON.stringify({ path: "Assets/Fixtures/ordinary.asset" }), "utf8");
  let admissionRequest: RootAdmissionRequest | undefined;
  let bridgeCalls = 0;
  const supervisor = {
    acquireRootAdmission: async (request: RootAdmissionRequest): Promise<RootAdmissionLease> => {
      admissionRequest = request;
      return lease(request);
    },
    completeRootAdmission: async (): Promise<void> => undefined,
  };
  try {
    const startedAtMs = Date.now();
    const result = await dispatchTool("asset.refresh", undefined, {
      "input-file": inputPath,
      "dry-run": true,
      "timeout-ms": "250",
    }, {
      bridgeExecute: async () => { bridgeCalls += 1; throw new Error("ordinary input test must not send a Bridge request"); },
      createSessionSupervisor: () => supervisor as never,
    }, "req_ordinary_input_deadline");

    assert.equal(result.ok, true, JSON.stringify(result));
    assert.ok(admissionRequest);
    assert.equal(admissionRequest!.timeoutMs, 250);
    assert.equal(Number.isFinite(admissionRequest!.admissionAbsoluteDeadlineMs), true);
    assert.ok(admissionRequest!.admissionAbsoluteDeadlineMs >= startedAtMs + 200);
    assert.equal(bridgeCalls, 0);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("resolved live descriptor owns the default input and admission deadline", async () => {
  const root = await mkdtemp(join(tmpdir(), "unity-cli-resolved-input-deadline-"));
  const inputPath = join(root, "input.json");
  await writeFile(inputPath, "{}", "utf8");
  const tool = makeLiveTool({
    name: "fixture.resolved-timeout",
    summary: "Read a resolved fixture.",
    description: "Read a resolved fixture through one live descriptor.",
    group: "fixture",
    timeoutMs: 750,
    completionContract: {
      executionClass: "atomic",
      journalPolicy: "none",
      mutation: false,
      completionPredicate: "typed-result-schema-v1",
      proofSource: "typed-handler-result",
      reloadPolicy: "fail-before-effect",
      timeoutError: "E_EXECUTION_TIMEOUT",
      outcomeUnknownPolicy: "not-applicable-read-only",
      completionDriverId: "direct-typed-result-v1",
    },
  });
  let admissionRequest: RootAdmissionRequest | undefined;
  const supervisor = {
    acquireRootAdmission: async (request: RootAdmissionRequest): Promise<RootAdmissionLease> => {
      admissionRequest = request;
      return lease(request);
    },
    completeRootAdmission: async (): Promise<void> => undefined,
  };
  try {
    const result = await dispatchTool(tool.name, undefined, { "input-file": inputPath }, {
      bridgeExecute: async (payload) => ({ ok: true, requestId: payload.requestId, data: { result: {} } }),
      createSessionSupervisor: () => supervisor as never,
    }, "req_resolved_input_deadline", {}, tool);
    assert.equal(result.ok, true, JSON.stringify(result));
    assert.ok(admissionRequest);
    assert.equal(admissionRequest!.timeoutMs, 750);
    assert.equal(Number.isFinite(admissionRequest!.admissionAbsoluteDeadlineMs), true);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("dispatcher keeps the non-code input deadline before live descriptor resolution", async () => {
  const source = await readFile("src/tools/dispatcher.ts", "utf8");
  assert.match(source, /const localMetadata = findToolByName\(name\);[\s\S]*const timeoutMetadata = resolvedTool \?\? localMetadata;[\s\S]*const defaultTimeoutMs = name === "debug\.code-execute" \? 60_000 : 30_000;[\s\S]*beginToolInvocation\(flags, requestId, \{ timeoutMs: timeoutMetadata\?\.timeoutMs \?\? defaultTimeoutMs \}, dispatchStartedAtMs\);[\s\S]*const admissionAbsoluteDeadlineMs = started\.context\.admissionAbsoluteDeadlineMs;[\s\S]*parseTypedInput\(flags, requestId, name === "debug\.code-execute" \? 1024 \* 1024 : undefined, admissionAbsoluteDeadlineMs\)/);
  assert.match(source, /resolveCatalogToolForInvocation\(name, catalogFlags, runtime, requestId, started\.context\)/);
});

test("ordinary Tool open stdin returns one timeout envelope before dispatch", async (context) => {
  const cliPath = fileURLToPath(new URL("../src/cli.js", import.meta.url));
  if (!existsSync(cliPath)) return context.skip("compiled CLI is generated by build:test");

  const child = spawn(process.execPath, [cliPath, "tools", "run", "scene.hierarchy", "--stdin", "--timeout-ms", "100"], {
    cwd: process.cwd(),
    env: { ...process.env, UNITY_CLI_ALLOW_NON_WINDOWS: "1" },
    stdio: ["pipe", "pipe", "pipe"],
  });
  child.stdin.write('{"pageSize":1}');
  let stdout = "";
  child.stdout.setEncoding("utf8");
  child.stdout.on("data", (chunk) => { stdout += chunk; });
  const exitCode = await new Promise<number | null>((resolvePromise, rejectPromise) => {
    const timer = setTimeout(() => { child.kill(); rejectPromise(new Error("ordinary Tool stdin did not honor timeout")); }, 2_000);
    child.once("error", rejectPromise);
    child.once("exit", (code) => { clearTimeout(timer); resolvePromise(code); });
  });
  child.stdin.destroy();

  assert.equal(exitCode, 1);
  const lines = stdout.trim().split(/\r?\n/);
  assert.equal(lines.length, 1);
  const result = JSON.parse(lines[0]!) as { ok: boolean; error?: { code?: string } };
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_EXECUTION_TIMEOUT");
});
