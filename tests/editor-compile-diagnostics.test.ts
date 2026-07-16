import assert from "node:assert/strict";
import { mkdtemp, readFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { runCompileDiagnostics } from "../src/cli-commands/editor.js";
import type { Runtime } from "../src/cli-commands/types.js";
import type { BridgeRequest, BridgeResponse } from "../src/bridge/types.js";

test("script import example declares a domain-reload wait budget", async () => {
  const descriptorSource = await readFile(new URL("../src/tools/editor/compile-diagnostics.js", import.meta.url), "utf8");
  const orchestrationSource = await readFile(new URL("../src/cli-commands/editor.js", import.meta.url), "utf8");
  assert.match(descriptorSource, /Import one changed script[\s\S]*--wait-timeout-ms", "180000"[\s\S]*Player\.cs/);
  assert.match(descriptorSource, /playModeSupport: "editor-only"/);
  assert.match(descriptorSource, /unavailable in PlayMode because its default AssetDatabase refresh/);
  assert.match(orchestrationSource, /MAX_COMPILE_WAIT_TIMEOUT_MS = 180_000/);
});

/** 构造可审计的 Bridge mock，验证编排层不会污染 typed Context input。 */
test("compile diagnostics keeps timeout in orchestration and retries stale target", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-compile-diagnostics-"));
  const payloads: BridgeRequest[] = [];
  let stateAttempts = 0;
  const runtime = {
    dataDir,
    async bridgeExecute(payload: BridgeRequest): Promise<BridgeResponse> {
      payloads.push(payload);
      if (payload.command === "context.read") {
        stateAttempts += 1;
        if (stateAttempts === 1) {
          return {
            ok: false,
            requestId: payload.requestId,
            error: {
              code: "E_TARGET_STALE",
              message: "target reloaded",
              transient: false,
              retryAfterMs: 0,
              nextActions: ["Resolve the instance again."]
            }
          };
        }
        return { ok: true, requestId: payload.requestId, data: { timedOut: false, state: { busy: { isBusy: false } } } };
      }
      if (payload.command === "project-tool.run" && (payload.input as { name?: string }).name === "asset.refresh") {
        return { ok: true, requestId: payload.requestId, data: { result: { refreshed: true } } };
      }
      if (payload.command === "project-tool.run" && (payload.input as { name?: string }).name === "console.get") {
        return { ok: true, requestId: payload.requestId, data: { result: { items: [], total: 0, pageInfo: { total: 0 } } } };
      }
      throw new Error(`Unexpected command: ${payload.command}`);
    }
  } as unknown as Runtime;

  const result = await runCompileDiagnostics({
    refresh: true,
    waitReady: true,
    waitTimeoutMs: 1_000,
    pollIntervalMs: 0,
    count: 20
  }, runtime, "req_compile_diagnostics");

  assert.equal((result.attempts as { wait: number }).wait, 1);
  assert.equal(stateAttempts, 3);
  const statePayloads = payloads.filter((item) => item.command === "context.read");
  assert.deepEqual(statePayloads.map((payload) => payload.input), [
    { name: "editor.state", input: {} },
    { name: "editor.state", input: {} },
    { name: "editor.state", input: { waitReady: true } }
  ]);
  for (const payload of statePayloads) assert.equal(payload.execution?.timeoutMs, undefined);
});

test("compile diagnostics rejects PlayMode before asset refresh", async () => {
  const commands: string[] = [];
  const runtime = {
    dataDir: await mkdtemp(join(tmpdir(), "unity-cli-compile-playmode-")),
    async bridgeExecute(payload: BridgeRequest): Promise<BridgeResponse> {
      commands.push(payload.command + ":" + ((payload.input as { name?: string }).name ?? ""));
      return {
        ok: true,
        requestId: payload.requestId,
        data: { command: "context.read", result: { state: { playMode: { isPlaying: true, isPlayingOrWillChangePlaymode: true } } } }
      };
    }
  } as unknown as Runtime;

  await assert.rejects(
    runCompileDiagnostics({ refresh: true }, runtime, "req_compile_playmode"),
    (error: unknown) => error instanceof Error && (error as { code?: string }).code === "E_TOOL_PLAYMODE_UNAVAILABLE"
  );
  assert.deepEqual(commands, ["context.read:editor.state"]);
});
