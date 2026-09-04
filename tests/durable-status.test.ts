import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import type { BridgeRequest, BridgeResponse } from "../src/bridge/types.js";
import { findDefineTool, type ToolContext } from "../src/tools/define.js";
import { StructuredToolError } from "../src/tools/errors.js";
import "../src/tools/.generated-registry.js";
import "../src/tools/.generated-schemas.js";

const cases = [
  ["asset.refresh.status", "internal.asset.refresh.public-status"],
  ["build.player.status", "internal.build.player.public-status"],
  ["editor.play-mode.status", "internal.editor.play-mode.public-status"],
  ["package.install.status", "internal.package.install.public-status"],
  ["package.remove.status", "internal.package.remove.public-status"],
  ["package.update.status", "internal.package.update.public-status"],
  ["test.run.status", "internal.test.run.public-status"],
] as const;

function context(bridgeExecute: ToolContext["bridgeExecute"]): ToolContext {
  return {
    requestId: "status-read-01", projectPath: "C:/Project", artifactDir: "C:/Temp", execution: { timeoutMs: 5_000 },
    absoluteDeadlineMs: Date.now() + 5_000, flags: { instance: "instance-01" }, bridgeExecute,
  };
}

for (const [toolName, bridgeCommand] of cases) {
  test(`${toolName} sends only the public root requestId`, async () => {
    const tool = findDefineTool(toolName);
    assert.ok(tool);
    let posted: BridgeRequest | undefined;
    const result = await tool.execute({ requestId: "root-01" }, context(async (payload: BridgeRequest): Promise<BridgeResponse> => {
      posted = payload;
      return { ok: true, requestId: payload.requestId, data: { requestId: "root-01", state: "running", phase: "effect_started" } };
    }));
    assert.deepEqual(result, { requestId: "root-01", state: "running", phase: "effect_started" });
    assert.equal(posted?.command, bridgeCommand);
    assert.deepEqual(posted?.input, { requestId: "root-01" });
    assert.equal(JSON.stringify(posted).includes("digest"), false);
  });
}

test("durable status returns the exact terminal envelope object", async () => {
  const tool = findDefineTool("test.run.status");
  assert.ok(tool);
  const terminalEnvelope = { ok: false, requestId: "root-terminal", error: { code: "E_TESTS_FAILED", message: "failed", nextActions: [] } };
  const result = await tool.execute({ requestId: "root-terminal" }, context(async (payload: BridgeRequest): Promise<BridgeResponse> => ({
    ok: true, requestId: payload.requestId, data: { requestId: "root-terminal", state: "completed", terminalEnvelope },
  })));
  assert.deepEqual(result, { requestId: "root-terminal", state: "completed", terminalEnvelope });
});

test("durable status fails closed on malformed state and preserves bridge errors", async () => {
  const tool = findDefineTool("asset.refresh.status");
  assert.ok(tool);
  await assert.rejects(
    () => tool.execute({ requestId: "root-invalid" }, context(async (payload: BridgeRequest): Promise<BridgeResponse> => ({ ok: true, requestId: payload.requestId, data: { state: "completed" } }))),
    (error: unknown) => error instanceof StructuredToolError && error.code === "E_BRIDGE_PROTOCOL",
  );
  await assert.rejects(
    () => tool.execute({ requestId: "root-missing" }, context(async (payload: BridgeRequest): Promise<BridgeResponse> => ({ ok: false, requestId: payload.requestId, error: { code: "E_REQUEST_NOT_FOUND", message: "missing", nextActions: ["Check requestId."] } }))),
    (error: unknown) => error instanceof StructuredToolError && error.code === "E_REQUEST_NOT_FOUND",
  );
});

test("durable status Bridge path is read-only, bounded, and digest-internal", async () => {
  const [active, coordinator, operation, queuePolicy] = await Promise.all([
    readFile("unitypkg/Editor/Infrastructure/UnityCliActiveOperation.cs", "utf8"),
    readFile("unitypkg/Editor/Infrastructure/UnityCliDurableStatusCoordinator.cs", "utf8"),
    readFile("unitypkg/Editor/Infrastructure/UnityCliOperationCoordinator.cs", "utf8"),
    readFile("unitypkg/Shared/UnityCliBridgeQueuePolicy.cs", "utf8"),
  ]);
  assert.match(active, /MaxTerminalCacheBytes = 8 \* 1024 \* 1024/);
  assert.match(active, /TryGetTerminalByRequestId\(string scope, string requestId, out string requestDigest, out string statusDomain, out object envelope\)/);
  assert.match(coordinator, /requestId is required and must be a safe root request identity/);
  assert.doesNotMatch(coordinator, /public sealed class Args[\s\S]*Digest/);
  assert.match(operation, /UnityCliDurableStatusCoordinator\.IsCommand\(request\.Command\)/);
  for (const [, command] of cases) assert.match(queuePolicy, new RegExp(command.replaceAll(".", "\\.")));
});
